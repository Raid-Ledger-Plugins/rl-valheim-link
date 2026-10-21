using System;
using System.Text.RegularExpressions;

namespace RlValheimLink.Core
{
    /// <summary>Sanitisers that keep raw game strings inside the contract's patterns (CONTRACT.md §2).</summary>
    public static class Names
    {
        private static readonly Regex PrefabBad = new Regex("[^A-Za-z0-9_.-]", RegexOptions.CultureInvariant);
        private static readonly Regex IdBad = new Regex("[^A-Za-z0-9_]", RegexOptions.CultureInvariant);
        private static readonly Regex PrefabOk = new Regex(@"^[A-Za-z0-9_.-]{1,96}\z", RegexOptions.CultureInvariant);

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

        /// <summary>True when <paramref name="s"/> already is a valid prefab id (no rewriting needed).</summary>
        public static bool IsPrefab(string s) => s != null && PrefabOk.IsMatch(s);

        public static string Identifier(string raw, int max)
        {
            if (string.IsNullOrEmpty(raw)) return null;
            string s = IdBad.Replace(raw, "_");
            if (s.Length > max) s = s.Substring(0, max);
            return s.Length == 0 ? null : s;
        }

        /// <summary>Clip free text to a max length without splitting a surrogate pair. Null becomes empty.</summary>
        public static string Clip(string s, int max)
        {
            if (s == null) return "";
            if (s.Length <= max) return s;
            int cut = max;
            if (char.IsHighSurrogate(s[cut - 1])) cut--;
            return s.Substring(0, cut);
        }

        public static int Clamp(long v, int min, int max) => v < min ? min : v > max ? max : (int)v;

        public static double Clamp(double v, double min, double max) => double.IsNaN(v) ? min : v < min ? min : v > max ? max : v;
    }
}
