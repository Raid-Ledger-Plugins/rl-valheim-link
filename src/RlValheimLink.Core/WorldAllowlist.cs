using System;
using System.Collections.Generic;

namespace RlValheimLink.Core
{
    /// <summary>
    /// <c>[Server] WorldAllowlist</c> (docs/design.md §8): the world names this server may link to
    /// Raid Ledger. Comma-separated, trimmed, matched case-insensitively; an empty list allows every
    /// world. Lets one dedicated server be booted into a test world without that world's captures
    /// overwriting the production world's "latest" snapshots (the character key carries no world).
    /// </summary>
    public static class WorldAllowlist
    {
        /// <summary>The configured names, trimmed, empties dropped, in config order.</summary>
        public static List<string> Parse(string csv)
        {
            var names = new List<string>();
            if (string.IsNullOrEmpty(csv)) return names;
            foreach (var raw in csv.Split(','))
            {
                var n = raw.Trim();
                if (n.Length > 0) names.Add(n);
            }
            return names;
        }

        /// <summary>True when <paramref name="csv"/> is empty (every world) or names <paramref name="worldName"/>, ignoring case and surrounding whitespace.</summary>
        public static bool Allows(string csv, string worldName)
        {
            var names = Parse(csv);
            if (names.Count == 0) return true;
            var world = (worldName ?? "").Trim();
            foreach (var n in names)
                if (string.Equals(n, world, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }
    }
}
