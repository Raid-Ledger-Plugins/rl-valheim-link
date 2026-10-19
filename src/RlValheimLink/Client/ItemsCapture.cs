using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using RlValheimLink.Contract;
using RlValheimLink.Net;
using UnityEngine;

namespace RlValheimLink.Client
{
    /// <summary>
    /// The `items` export (docs/design.md §2): every ObjectDB item with its localised name, category
    /// and an icon copied into one atlas PNG. Runs as a coroutine on the client (icons need a GPU path)
    /// and encodes the PNG on a worker thread.
    /// </summary>
    internal static class ItemsCapture
    {
        private const int PerRow = 32;
        private static Coroutine _running;

        public static bool IsRunning => _running != null;

        public static void Start(string reason)
        {
            if (_running != null) { Log.Warn("items export already running"); return; }
            if (ObjectDB.instance == null || ObjectDB.instance.m_items == null) { Log.Warn("ObjectDB not ready; items export skipped"); return; }
            _running = Plugin.Instance.StartCoroutine(Run(PluginConfig.IconSize.Value, reason));
        }

        public static void Cancel()
        {
            if (_running != null && Plugin.Instance != null) Plugin.Instance.StopCoroutine(_running);
            _running = null;
        }

        private sealed class Entry { public ItemDefinition Def; public Sprite Sprite; }

        private static IEnumerator Run(int iconSize, string reason)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var entries = new List<Entry>();
            string locale = "English";
            try { locale = GameInfo.Identifier(Localization.instance?.GetSelectedLanguage(), 32) ?? "English"; } catch { }
            if (locale.Length < 2) locale = "English";

            var seen = new HashSet<string>();
            foreach (var go in ObjectDB.instance.m_items)
            {
                if (go == null) continue;
                var drop = go.GetComponent<ItemDrop>();
                var sd = drop?.m_itemData?.m_shared;
                if (sd == null) continue;
                string prefab = GameInfo.Prefab(go.name);
                if (prefab == null || !seen.Add(prefab)) continue;
                string name = null;
                try { name = GameInfo.StripRichText(Localization.instance?.Localize(sd.m_name)); } catch { }
                if (string.IsNullOrEmpty(name) || name.StartsWith("[", StringComparison.Ordinal)) name = prefab;
                entries.Add(new Entry
                {
                    Def = new ItemDefinition
                    {
                        Prefab = prefab,
                        Name = GameInfo.Clip(name, 96),
                        Category = GameInfo.Identifier(sd.m_itemType.ToString(), 48) ?? "Misc",
                        MaxStack = GameInfo.Clamp(sd.m_maxStackSize, 1, 100_000),
                        MaxQuality = GameInfo.Clamp(sd.m_maxQuality, 1, 10),
                        Variants = GameInfo.Clamp(sd.m_variants, 0, 64),
                        Icon = null,
                    },
                    Sprite = sd.m_icons != null && sd.m_icons.Length > 0 ? sd.m_icons[0] : null,
                });
                if (entries.Count >= Caps.ItemExportMax) { Log.Warn($"items: ObjectDB has more than {Caps.ItemExportMax} items; the rest are dropped"); break; }
            }
            entries.Sort((a, b) => string.CompareOrdinal(a.Def.Prefab, b.Def.Prefab));
            Log.Info($"items export: {entries.Count} item(s) from ObjectDB, locale {locale}, reason {reason}");
            if (entries.Count == 0) { _running = null; yield break; }

            byte[] png = null;
            BinaryRef atlasRef = null;
            int size = iconSize;
            while (size >= 16)
            {
                int withIcons = 0;
                var rgba = AtlasBuilder.Build(entries, size, PerRow, out int width, out int height, out withIcons, e => e.Sprite, (e, icon) => e.Def.Icon = icon);
                yield return null;
                if (withIcons == 0) { png = null; break; }
                byte[] encoded = null; Exception err = null;
                var t = new Thread(() => { try { encoded = Png.EncodeRgba(rgba, width, height); } catch (Exception e) { err = e; } }) { IsBackground = true, Name = "RLVL-atlas-png" };
                t.Start();
                while (t.IsAlive) yield return null;
                if (err != null) { Log.Error("atlas PNG encode failed", err); break; }
                int maxBlob = Math.Min(Caps.BinaryMaxBytes, PluginConfig.UploadMaxBlobBytes.Value);
                if (encoded.Length <= maxBlob) { png = encoded; atlasRef = Png.MakeRef(png); break; }
                Log.Warn($"atlas at {size} px is {encoded.Length} bytes (> Upload.MaxBlobBytes {maxBlob}); retrying smaller");
                size = size >= 64 ? 48 : size >= 48 ? 32 : size >= 32 ? 16 : 0;
            }
            if (png == null) foreach (var e in entries) e.Def.Icon = null;

            var msg = new ItemsMessage
            {
                ServerId = ClientHalf.PendingServerId,
                CapturedAt = MessageJson.Timestamp(DateTime.UtcNow),
                GameVersion = GameInfo.GameVersion(),
                ModVersion = Plugin.Version,
                Locale = locale,
                Atlas = atlasRef,
            };
            foreach (var e in entries) msg.Items.Add(e.Def);

            if (png != null) LinkChannel.Send(PayloadKind.Blob, png, $"atlas {atlasRef.Width}x{atlasRef.Height} sha256={atlasRef.Sha256.Substring(0, 12)}…");
            ClientHalf.Dispatch(msg, PayloadKind.Items, $"items (count={msg.Items.Count} atlas={(png == null ? "none" : png.Length + " bytes")}) exported in {sw.ElapsedMilliseconds} ms");
            _running = null;
        }
    }

    /// <summary>Copies sprites into a top-down RGBA atlas via a RenderTexture (GPU required: never runs on the dedicated server).</summary>
    internal static class AtlasBuilder
    {
        public static byte[] Build<T>(List<T> entries, int icon, int perRow, out int width, out int height, out int withIcons,
            Func<T, Sprite> sprite, Action<T, ItemIcon> assign)
        {
            int n = entries.Count;
            int rows = Math.Max(1, (n + perRow - 1) / perRow);
            width = perRow * icon; height = rows * icon;
            var rgba = new byte[width * height * 4];
            withIcons = 0;

            // Group by source texture so each texture is blitted once.
            var byTexture = new Dictionary<Texture2D, List<int>>();
            for (int i = 0; i < n; i++)
            {
                var s = sprite(entries[i]);
                if (s == null || s.texture == null) continue;
                if (!byTexture.TryGetValue(s.texture, out var list)) byTexture[s.texture] = list = new List<int>();
                list.Add(i);
            }

            var prev = RenderTexture.active;
            foreach (var kv in byTexture)
            {
                var tex = kv.Key;
                RenderTexture rt = null;
                try
                {
                    rt = RenderTexture.GetTemporary(tex.width, tex.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
                    Graphics.Blit(tex, rt);
                    RenderTexture.active = rt;
                    foreach (int i in kv.Value)
                    {
                        var s = sprite(entries[i]);
                        var r = s.textureRect;
                        int rw = Mathf.Max(1, Mathf.RoundToInt(r.width)), rh = Mathf.Max(1, Mathf.RoundToInt(r.height));
                        var crop = new Texture2D(rw, rh, TextureFormat.RGBA32, false);
                        crop.ReadPixels(new Rect(r.x, r.y, rw, rh), 0, 0, false);
                        crop.Apply(false);
                        Color32[] px = crop.GetPixels32();
                        UnityEngine.Object.Destroy(crop);

                        int ox = (i % perRow) * icon, oy = (i / perRow) * icon;
                        Resample(px, rw, rh, rgba, width, ox, oy, icon);
                        assign(entries[i], ItemIcon.Atlas(ox, oy, icon, icon));
                        withIcons++;
                    }
                }
                catch (Exception e) { Log.Warn($"atlas: texture {tex.name} skipped: {e.Message}"); }
                finally
                {
                    RenderTexture.active = prev;
                    if (rt != null) RenderTexture.ReleaseTemporary(rt);
                }
            }
            RenderTexture.active = prev;
            return rgba;
        }

        /// <summary>Bilinear resample of a bottom-up Unity pixel block into a top-down RGBA atlas cell.</summary>
        private static void Resample(Color32[] src, int sw, int sh, byte[] dst, int dstWidth, int ox, int oy, int icon)
        {
            for (int y = 0; y < icon; y++)
            {
                float fy = (y + 0.5f) * sh / icon - 0.5f;             // top-down target row → bottom-up source row
                float sy = sh - 1 - fy;
                int y0 = Mathf.Clamp(Mathf.FloorToInt(sy), 0, sh - 1), y1 = Mathf.Clamp(y0 + 1, 0, sh - 1);
                float ty = Mathf.Clamp01(sy - y0);
                for (int x = 0; x < icon; x++)
                {
                    float sx = (x + 0.5f) * sw / icon - 0.5f;
                    int x0 = Mathf.Clamp(Mathf.FloorToInt(sx), 0, sw - 1), x1 = Mathf.Clamp(x0 + 1, 0, sw - 1);
                    float tx = Mathf.Clamp01(sx - x0);
                    Color32 a = src[y0 * sw + x0], b = src[y0 * sw + x1], c = src[y1 * sw + x0], d = src[y1 * sw + x1];
                    int o = ((oy + y) * dstWidth + ox + x) * 4;
                    dst[o] = Lerp(a.r, b.r, c.r, d.r, tx, ty);
                    dst[o + 1] = Lerp(a.g, b.g, c.g, d.g, tx, ty);
                    dst[o + 2] = Lerp(a.b, b.b, c.b, d.b, tx, ty);
                    dst[o + 3] = Lerp(a.a, b.a, c.a, d.a, tx, ty);
                }
            }
        }

        private static byte Lerp(byte a, byte b, byte c, byte d, float tx, float ty)
        {
            float top = a + (b - a) * tx, bottom = c + (d - c) * tx;
            return (byte)Mathf.Clamp(Mathf.RoundToInt(top + (bottom - top) * ty), 0, 255);
        }
    }
}
