using System;
using System.Collections.Generic;
using System.Globalization;
using RlValheimLink.Contract;

namespace RlValheimLink.Core
{
    /// <summary>One portal as the server read it from its ZDO (Server/PortalScanner).</summary>
    public sealed class ScannedPortal
    {
        public string Prefab;
        public double X, Y, Z;
        /// <summary>The ZDO's pairing tag (<c>ZDOVars.s_tag</c>) as typed in game; null = none.</summary>
        public string Tag;
    }

    /// <summary>What a build did, for the per-scan log line. Tags are player free text and are never logged.</summary>
    public sealed class PortalsReport
    {
        public int Scanned, Portals, Tagged, Invalid, Duplicates, TagsClipped, Omitted;
    }

    /// <summary>
    /// Turns raw scanned portals into one `portals` message (CONTRACT.md §5c): positions rounded to 0.1 m
    /// like containers, tags clipped to the contract's 64 chars (modded portals are not bound by vanilla's
    /// 10), one portal per (prefab, rounded position), sorted by prefab, x, z, y, tag, then the 4 096 cap
    /// applied by dropping from the end and counting the drop in `omitted`. Pure; unit-tested.
    /// </summary>
    public static class PortalsBuilder
    {
        public static PortalsMessage Build(ContainersHeader header, IEnumerable<ScannedPortal> scanned, out PortalsReport report)
        {
            report = new PortalsReport();
            var list = new List<PortalSnapshot>();
            foreach (var p in scanned ?? new List<ScannedPortal>())
            {
                if (p == null) continue;
                report.Scanned++;
                string prefab = Names.Prefab(p.Prefab);
                if (prefab == null) { report.Invalid++; continue; }
                string tag = Names.Clip(p.Tag, Caps.PortalTagMaxChars);
                if (p.Tag != null && tag.Length < p.Tag.Length) report.TagsClipped++;
                list.Add(new PortalSnapshot
                {
                    Prefab = prefab, X = ContainersBuilder.Round(p.X), Y = ContainersBuilder.Round(p.Y), Z = ContainersBuilder.Round(p.Z), Tag = tag,
                });
            }

            // Sort first so the portal kept for a duplicate (prefab, position) does not depend on scan order.
            list.Sort(Compare);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var unique = new List<PortalSnapshot>(list.Count);
            foreach (var s in list)
            {
                if (seen.Add(s.Prefab + "|" + Coord(s.X) + "|" + Coord(s.Y ?? 0) + "|" + Coord(s.Z))) unique.Add(s);
                else report.Duplicates++;
            }
            if (unique.Count > Caps.PortalsMax)
            {
                report.Omitted = unique.Count - Caps.PortalsMax;
                unique.RemoveRange(Caps.PortalsMax, report.Omitted);
            }

            foreach (var s in unique) if (s.Tag.Length > 0) report.Tagged++;
            report.Portals = unique.Count;
            return new PortalsMessage
            {
                ServerId = header.ServerId,
                CapturedAt = MessageJson.Timestamp(header.CapturedAtUtc),
                GameVersion = header.GameVersion,
                ModVersion = header.ModVersion,
                WorldUid = header.WorldUid,
                Portals = unique,
                Omitted = Math.Min(report.Omitted, 1_000_000),
            };
        }

        /// <summary>The spool key of a world's portals snapshot (latest wins).</summary>
        public static string SpoolKey(string worldUid) => "portals|" + worldUid;

        private static string Coord(double v) => v.ToString("R", CultureInfo.InvariantCulture);

        private static int Compare(PortalSnapshot a, PortalSnapshot b)
        {
            int c = string.CompareOrdinal(a.Prefab, b.Prefab);
            if (c == 0) c = a.X.CompareTo(b.X);
            if (c == 0) c = a.Z.CompareTo(b.Z);
            if (c == 0) c = (a.Y ?? 0).CompareTo(b.Y ?? 0);
            if (c == 0) c = string.CompareOrdinal(a.Tag, b.Tag);
            return c;
        }
    }
}
