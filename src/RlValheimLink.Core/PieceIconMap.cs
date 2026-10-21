using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RlValheimLink.Contract;

namespace RlValheimLink.Core
{
    /// <summary>
    /// The piece and station icon atlas an admin client renders for the `recipes` export (a dedicated server
    /// cannot render sprites). It travels over the internal RPC (PayloadKind.PieceIcons, not the Raid Ledger
    /// contract) as <c>{ gameVersion, atlas: &lt;binary ref&gt;, icons: { prefab: [x, y, w, h] } }</c>; the
    /// atlas PNG itself arrives first as a blob. The server keeps the last map in the spool and applies it to
    /// every recipes export of the same game version (<see cref="Apply"/>), so icons follow the contract's
    /// atlas rules (CONTRACT.md §5b, §6): inside the atlas, w and h 1–512.
    /// </summary>
    public sealed class PieceIconMap
    {
        public const int MaxIcons = 8192;
        private static readonly Regex GameVersionPattern = new Regex(@"^\d{1,4}(\.\d{1,5}){1,3}\z", RegexOptions.CultureInvariant);
        private static readonly Regex Sha256 = new Regex(@"^[0-9a-f]{64}\z", RegexOptions.CultureInvariant);

        public string GameVersion;
        public BinaryRef Atlas;
        /// <summary>prefab id → (x, y, w, h) in atlas pixels, top-left origin.</summary>
        public SortedDictionary<string, int[]> Icons = new SortedDictionary<string, int[]>(StringComparer.Ordinal);
        /// <summary>The server's own mod-set hash when it stored the map (a changed mod set asks for a new atlas); null on the wire.</summary>
        public string ServerModSet;
        /// <summary>Entries <see cref="TryParse"/> left out (bad prefab id or rect).</summary>
        public int Rejected;

        public string Sha12 => Atlas?.Sha256 == null ? "?" : Atlas.Sha256.Substring(0, Math.Min(12, Atlas.Sha256.Length));

        /// <summary>
        /// Parses and checks a map (from a client's payload or the spool file). The header (game version,
        /// atlas ref) must be valid or the whole map is refused; single icons with a bad id or a rect outside
        /// the atlas are dropped and counted in <see cref="Rejected"/>.
        /// </summary>
        public static bool TryParse(string json, out PieceIconMap map, out string error)
        {
            map = null; error = null;
            JObject o;
            try { o = MessageJson.Parse(json) as JObject; }
            catch (Exception e) { error = "not JSON: " + e.Message; return false; }
            if (o == null) { error = "not a JSON object"; return false; }

            var m = new PieceIconMap
            {
                GameVersion = Str(o["gameVersion"]),
                ServerModSet = Str(o["serverModSet"]),
            };
            if (m.GameVersion == null || !GameVersionPattern.IsMatch(m.GameVersion)) { error = "gameVersion missing or malformed"; return false; }
            if (!(o["atlas"] is JObject a)) { error = "atlas missing"; return false; }
            m.Atlas = new BinaryRef
            {
                Sha256 = Str(a["sha256"]),
                ByteLength = Int(a["byteLength"]) ?? 0,
                ContentType = Str(a["contentType"]),
                Width = Int(a["width"]) ?? 0,
                Height = Int(a["height"]) ?? 0,
            };
            if (m.Atlas.Sha256 == null || !Sha256.IsMatch(m.Atlas.Sha256)) { error = "atlas.sha256 is not 64 lowercase hex"; return false; }
            if (m.Atlas.ContentType != "image/png") { error = "atlas.contentType is not image/png"; return false; }
            if (m.Atlas.ByteLength < 1 || m.Atlas.ByteLength > Caps.BinaryMaxBytes) { error = "atlas.byteLength out of range"; return false; }
            if (m.Atlas.Width < 1 || m.Atlas.Width > Caps.BinaryEdgeMax || m.Atlas.Height < 1 || m.Atlas.Height > Caps.BinaryEdgeMax) { error = "atlas size out of range"; return false; }
            if (!(o["icons"] is JObject icons)) { error = "icons missing"; return false; }
            if (icons.Count > MaxIcons) { error = $"more than {MaxIcons} icons"; return false; }

            foreach (var prop in icons.Properties())
            {
                var rect = prop.Value as JArray;
                int?[] v = rect != null && rect.Count == 4 ? new[] { Int(rect[0]), Int(rect[1]), Int(rect[2]), Int(rect[3]) } : null;
                bool ok = Names.IsPrefab(prop.Name) && v != null && v[0].HasValue && v[1].HasValue && v[2].HasValue && v[3].HasValue
                    && v[0] >= 0 && v[0] <= 16_383 && v[1] >= 0 && v[1] <= 16_383 && v[2] >= 1 && v[2] <= 512 && v[3] >= 1 && v[3] <= 512
                    && v[0] + v[2] <= m.Atlas.Width && v[1] + v[3] <= m.Atlas.Height;
                if (!ok) { m.Rejected++; continue; }
                m.Icons[prop.Name] = new[] { v[0].Value, v[1].Value, v[2].Value, v[3].Value };
            }
            if (m.Icons.Count == 0) { error = "no usable icon"; return false; }
            map = m;
            return true;
        }

        public static bool TryParse(byte[] utf8, out PieceIconMap map, out string error)
        {
            try { return TryParse(MessageJson.Utf8NoBom.GetString(utf8), out map, out error); }
            catch (Exception e) { map = null; error = "not UTF-8: " + e.Message; return false; }
        }

        /// <summary>The wire / spool form (compact JSON). <see cref="ServerModSet"/> is written only when set.</summary>
        public string ToJson()
        {
            var icons = new JObject();
            foreach (var kv in Icons) icons[kv.Key] = new JArray(kv.Value[0], kv.Value[1], kv.Value[2], kv.Value[3]);
            var o = new JObject
            {
                ["gameVersion"] = GameVersion,
                ["atlas"] = JObject.FromObject(Atlas),
                ["icons"] = icons,
            };
            if (ServerModSet != null) o["serverModSet"] = ServerModSet;
            return o.ToString(Formatting.None);
        }

        /// <summary>
        /// Puts the map's icons on a normalised recipes message: every station and piece whose prefab has an
        /// icon gets its atlas rect, and the message references the atlas when at least one icon landed.
        /// Call after <see cref="RecipesBuilder.Normalise"/>. Returns how many icons were applied;
        /// <paramref name="candidates"/> is the number of stations and pieces in the message.
        /// </summary>
        public static int Apply(RecipesMessage msg, PieceIconMap map, out int candidates)
        {
            candidates = msg.Stations.Count + msg.Pieces.Count;
            if (map == null || map.Atlas == null || map.Icons.Count == 0) return 0;
            int applied = 0;
            foreach (var s in msg.Stations) if (Lookup(map, s.Prefab, out var icon)) { s.Icon = icon; applied++; }
            foreach (var p in msg.Pieces) if (Lookup(map, p.Prefab, out var icon)) { p.Icon = icon; applied++; }
            if (applied > 0)
                msg.Atlas = new BinaryRef { Sha256 = map.Atlas.Sha256, ByteLength = map.Atlas.ByteLength, ContentType = "image/png", Width = map.Atlas.Width, Height = map.Atlas.Height };
            return applied;
        }

        private static bool Lookup(PieceIconMap map, string prefab, out AtlasIcon icon)
        {
            icon = null;
            if (prefab == null || !map.Icons.TryGetValue(prefab, out var r)) return false;
            icon = new AtlasIcon { X = r[0], Y = r[1], W = r[2], H = r[3] };
            return true;
        }

        private static string Str(JToken t) => t is JValue v && v.Type == JTokenType.String ? (string)v : null;

        private static int? Int(JToken t) =>
            t is JValue v && v.Value is long l && l >= int.MinValue && l <= int.MaxValue ? (int?)(int)l : null;
    }
}
