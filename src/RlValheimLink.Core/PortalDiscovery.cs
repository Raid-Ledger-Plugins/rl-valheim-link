using System;
using System.Collections.Generic;

namespace RlValheimLink.Core
{
    /// <summary>
    /// The portal scan's decisions that need no game (ROK-1945): which network prefabs to scan, whether a
    /// scan may produce a snapshot at all, and when the first scan runs. Pure; unit-tested.
    /// </summary>
    public static class PortalDiscovery
    {
        /// <summary>
        /// The portal scan starts this long after the chest scan's first slot (same interval after that),
        /// so the two prefab discoveries and ZDO walks never run on the same tick.
        /// </summary>
        public static readonly TimeSpan OffsetFromChestScan = TimeSpan.FromSeconds(60);

        public static TimeSpan FirstDelay(TimeSpan chestFirstDelay) => chestFirstDelay + OffsetFromChestScan;

        /// <summary>
        /// A `portals` message is a full snapshot: an empty one clears every portal on Raid Ledger. That is
        /// only right when at least one portal prefab was searched and the world has none. Zero prefabs
        /// means discovery failed (vanilla always has portal_wood), so nothing may be sent.
        /// </summary>
        public static bool CanSnapshot(int portalPrefabCount) => portalPrefabCount > 0;

        /// <summary>
        /// Portal prefabs to scan, sorted: every network prefab carrying the portal component plus the
        /// configured extras that exist as network prefabs. <paramref name="unknownExtras"/> gets the extras
        /// that matched no network prefab (a typo, or a mod that is not installed), sorted.
        /// </summary>
        public static List<string> Select(IEnumerable<KeyValuePair<string, bool>> networkPrefabs, ICollection<string> extra, out List<string> unknownExtras)
        {
            var names = new SortedSet<string>(StringComparer.Ordinal);
            var present = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in networkPrefabs)
            {
                if (p.Key == null) continue;
                present.Add(p.Key);
                if (p.Value || extra.Contains(p.Key)) names.Add(p.Key);
            }
            var unknown = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var e in extra) if (e != null && !present.Contains(e)) unknown.Add(e);
            unknownExtras = new List<string>(unknown);
            return new List<string>(names);
        }
    }
}
