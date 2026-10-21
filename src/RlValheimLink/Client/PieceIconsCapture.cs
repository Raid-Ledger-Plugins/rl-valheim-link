using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using RlValheimLink.Contract;
using RlValheimLink.Core;
using RlValheimLink.Net;
using UnityEngine;

namespace RlValheimLink.Client
{
    /// <summary>
    /// The piece and station icon atlas for the server's `recipes` export (0.4.0): a dedicated server cannot
    /// render sprites, so an admin client copies every build piece's and crafting station's icon into one
    /// atlas PNG (the same AtlasBuilder as the items export) and sends it as a blob, followed by the
    /// prefab → rect map (PayloadKind.PieceIcons, internal RPC JSON, see <see cref="PieceIconMap"/>). Ids come
    /// from <see cref="RecipeSources"/>, the same helper the server's export uses. Runs as a coroutine and
    /// encodes the PNG on a worker thread.
    /// </summary>
    internal static class PieceIconsCapture
    {
        private const int PerRow = 32;
        private static Coroutine _running;

        public static bool IsRunning => _running != null;

        public static void Start(string reason)
        {
            if (_running != null) { Log.Warn("piece icon export already running"); return; }
            if (ObjectDB.instance == null || ZNetScene.instance == null) { Log.Warn("ObjectDB not ready; piece icon export skipped"); return; }
            _running = Plugin.Instance.StartCoroutine(Run(PluginConfig.IconSize.Value, reason));
        }

        public static void Cancel()
        {
            if (_running != null && Plugin.Instance != null) Plugin.Instance.StopCoroutine(_running);
            _running = null;
        }

        private sealed class Entry { public string Prefab; public Sprite Sprite; public ItemIcon Icon; }

        private static IEnumerator Run(int iconSize, string reason)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var entries = new List<Entry>();
            try
            {
                foreach (var kv in RecipeSources.Icons())
                {
                    entries.Add(new Entry { Prefab = kv.Key, Sprite = kv.Value });
                    if (entries.Count >= PieceIconMap.MaxIcons) { Log.Warn($"piece icons: more than {PieceIconMap.MaxIcons}; the rest are left out"); break; }
                }
            }
            catch (Exception e) { Log.Error("piece icon export: reading pieces and stations failed", e); _running = null; yield break; }
            Log.Info($"piece icon export: {entries.Count} piece and station icon(s), reason {reason}");
            if (entries.Count == 0) { _running = null; yield break; }

            byte[] png = null;
            BinaryRef atlasRef = null;
            int size = iconSize;
            while (size >= 16)
            {
                var rgba = AtlasBuilder.Build(entries, size, PerRow, out int width, out int height, out int withIcons, e => e.Sprite, (e, icon) => e.Icon = icon);
                yield return null;
                if (withIcons == 0) break;
                byte[] encoded = null; Exception err = null;
                var t = new Thread(() => { try { encoded = Png.EncodeRgba(rgba, width, height); } catch (Exception e) { err = e; } }) { IsBackground = true, Name = "RLVL-piece-atlas-png" };
                t.Start();
                while (t.IsAlive) yield return null;
                if (err != null) { Log.Error("piece atlas PNG encode failed", err); break; }
                int maxBlob = Math.Min(Caps.BinaryMaxBytes, PluginConfig.UploadMaxBlobBytes.Value);
                if (encoded.Length <= maxBlob) { png = encoded; atlasRef = Png.MakeRef(png); break; }
                Log.Warn($"piece atlas at {size} px is {encoded.Length} bytes (> Upload.MaxBlobBytes {maxBlob}); retrying smaller");
                foreach (var e in entries) e.Icon = null;
                size = size >= 64 ? 48 : size >= 48 ? 32 : size >= 32 ? 16 : 0;
            }
            if (png == null) { Log.Warn("piece icon export: no atlas could be built; nothing sent"); _running = null; yield break; }

            var map = new PieceIconMap { GameVersion = GameInfo.GameVersion(), Atlas = atlasRef };
            foreach (var e in entries)
                if (e.Icon != null && e.Icon.X.HasValue) map.Icons[e.Prefab] = new[] { e.Icon.X.Value, e.Icon.Y.Value, e.Icon.W.Value, e.Icon.H.Value };
            byte[] gz = LinkChannel.Gzip(MessageJson.Utf8NoBom.GetBytes(map.ToJson()));
            if (gz.Length > LinkChannel.PieceIconsMaxBytes) { Log.Error($"piece icon map is {gz.Length} bytes gzip (> {LinkChannel.PieceIconsMaxBytes}); nothing sent"); _running = null; yield break; }

            // The blob first: the server only accepts a map whose atlas it already holds.
            LinkChannel.Send(PayloadKind.Blob, png, $"piece atlas {atlasRef.Width}x{atlasRef.Height} sha256={atlasRef.Sha256.Substring(0, 12)}…");
            LinkChannel.Send(PayloadKind.PieceIcons, gz, $"piece icons (count={map.Icons.Count} atlas={png.Length} bytes at {size} px) exported in {sw.ElapsedMilliseconds} ms");
            _running = null;
        }
    }
}
