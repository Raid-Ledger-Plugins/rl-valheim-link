using System;
using System.Text;
using System.Text.RegularExpressions;

namespace RlValheimLink.Client
{
    /// <summary>Reads of game-wide facts plus the sanitisers that keep raw game strings inside the contract's patterns.</summary>
    internal static class GameInfo
    {
        private static readonly Regex GameVersionPattern = new Regex(@"^\d{1,4}(\.\d{1,5}){1,3}$");
        private static readonly Regex PrefabBad = new Regex("[^A-Za-z0-9_.-]");
        private static readonly Regex IdBad = new Regex("[^A-Za-z0-9_]");

        /// <summary>The individual-account base: a syntactically valid SteamID64 that is nobody's. Used as the client's claim when Steam is unavailable; the server replaces it or rejects it.</summary>
        public const string PlaceholderSteamId = "76561197960265728";

        public static string GameVersion()
        {
            string v = null;
            try { v = global::Version.CurrentVersion.ToString(); } catch { }
            if (v == null || !GameVersionPattern.IsMatch(v))
            {
                try { v = global::Version.GetVersionString(false); } catch { }
            }
            if (v != null && !GameVersionPattern.IsMatch(v))
            {
                var m = Regex.Match(v, @"\d{1,4}(\.\d{1,5}){1,3}");
                v = m.Success ? m.Value : null;
            }
            return v ?? "0.0.0";
        }

        public static string LocalSteamId()
        {
            try
            {
                ulong id = Steamworks.SteamUser.GetSteamID().m_SteamID;
                if (id > 0) return id.ToString();
            }
            catch (Exception) { }
            return PlaceholderSteamId;
        }

        public static string WorldUid() => ZNet.instance != null ? ZNet.instance.GetWorldUID().ToString() : "0";

        public static string WorldName()
        {
            string n = null;
            try { n = ZNet.instance?.GetWorldName(); } catch { }
            return Clip(string.IsNullOrEmpty(n) ? "unknown" : n, 64);
        }

        /// <summary>Prefab name as the contract wants it: strip "(Clone)", replace anything outside [A-Za-z0-9_.-], ≤ 96 chars. Null when nothing is left.</summary>
        public static string Prefab(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return null;
            string s = raw.Trim();
            if (s.EndsWith("(Clone)", StringComparison.Ordinal)) s = s.Substring(0, s.Length - 7);
            s = PrefabBad.Replace(s, "_");
            if (s.Length > 96) s = s.Substring(0, 96);
            return s.Length == 0 ? null : s;
        }

        public static string Identifier(string raw, int max)
        {
            if (string.IsNullOrEmpty(raw)) return null;
            string s = IdBad.Replace(raw, "_");
            if (s.Length > max) s = s.Substring(0, max);
            return s.Length == 0 ? null : s;
        }

        /// <summary>Clip free text to a max length without splitting a surrogate pair. Empty stays empty.</summary>
        public static string Clip(string s, int max)
        {
            if (s == null) return "";
            if (s.Length <= max) return s;
            int cut = max;
            if (char.IsHighSurrogate(s[cut - 1])) cut--;
            return s.Substring(0, cut);
        }

        public static string StripRichText(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            return Regex.Replace(s, "<[^>]{1,32}>", "").Trim();
        }

        public static int Clamp(int v, int min, int max) => v < min ? min : v > max ? max : v;
        public static double Clamp(double v, double min, double max) => double.IsNaN(v) ? min : v < min ? min : v > max ? max : v;

        public static string Sha256Hex(params byte[][] parts)
        {
            using (var sha = System.Security.Cryptography.SHA256.Create())
            {
                foreach (var p in parts) sha.TransformBlock(p, 0, p.Length, null, 0);
                sha.TransformFinalBlock(new byte[0], 0, 0);
                var sb = new StringBuilder(64);
                foreach (var b in sha.Hash) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }
    }
}
