using System;
using System.Collections.Generic;
using System.Globalization;
using RlValheimLink.Contract;

namespace RlValheimLink.Core
{
    /// <summary>One stack as the game decoded it from a container (before aggregation).</summary>
    public sealed class ScannedStack
    {
        public string Prefab;
        public int Quality;
        public int Stack;
    }

    /// <summary>One player-built container as the server read it from its ZDO (Server/ChestScanner).</summary>
    public sealed class ScannedContainer
    {
        public string Prefab;
        public double X, Y, Z;
        /// <summary>ZDO <c>creator</c>: the placing character's playerId. 0 = not player-built (skipped).</summary>
        public long Creator;
        public string Privacy = ContainerPrivacy.Public;
        public bool Vehicle;
        /// <summary>The prefab's Container size.</summary>
        public int Width, Height;
        /// <summary>Highest decoded grid row, -1 when nothing was decoded.</summary>
        public int MaxRow = -1;
        /// <summary>The inventory could not be decoded: sent with no items (CONTRACT.md §5a).</summary>
        public bool Unreadable;
        public List<ScannedStack> Stacks = new List<ScannedStack>();
    }

    public sealed class ContainersHeader
    {
        public string ServerId, GameVersion, ModVersion, WorldUid;
        public DateTime CapturedAtUtc;
    }

    /// <summary>What a build did, for the per-scan log line and its WARNs.</summary>
    public sealed class ContainersReport
    {
        public int Scanned, Creator0, Containers, Vehicles, Private, Unreadable, Stacks, Entries, Merged;
        public int SlotsClamped, ContainersOverEntries, EntriesOmitted, InvalidStacks, OmittedShared, OmittedPrivate;
        public bool CapsApplied => SlotsClamped > 0 || ContainersOverEntries > 0 || OmittedShared + OmittedPrivate > 0;
    }

    /// <summary>
    /// Turns raw scanned containers into one `containers` message (CONTRACT.md §5a, docs/design-chests.md
    /// §1.5 and §1.8): positions rounded to 0.1 m, stacks aggregated per (prefab, quality), containers of
    /// one creator at one rounded position merged, sorted by prefab, x, z, y, creator, then the
    /// contract's caps applied by dropping (never by sending an invalid message). Pure; unit-tested.
    /// </summary>
    public static class ContainersBuilder
    {
        public static ContainersMessage Build(ContainersHeader header, IEnumerable<ScannedContainer> scanned, out ContainersReport report)
        {
            report = new ContainersReport();
            var byKey = new Dictionary<string, Acc>(StringComparer.Ordinal);
            var order = new List<Acc>();
            foreach (var c in scanned)
            {
                if (c == null) continue;
                report.Scanned++;
                if (c.Creator == 0L) { report.Creator0++; continue; }
                string prefab = Names.Prefab(c.Prefab);
                if (prefab == null) { report.InvalidStacks += c.Stacks?.Count ?? 0; continue; }
                double x = Round(c.X), y = Round(c.Y), z = Round(c.Z);
                string creator = c.Creator.ToString(CultureInfo.InvariantCulture);
                string key = prefab + "|" + Coord(x) + "|" + Coord(y) + "|" + Coord(z) + "|" + creator;
                if (!byKey.TryGetValue(key, out var acc))
                {
                    acc = new Acc { Prefab = prefab, X = x, Y = y, Z = z, Creator = c.Creator, CreatorText = creator, Privacy = Privacy(c.Privacy), AllUnreadable = true };
                    byKey[key] = acc; order.Add(acc);
                }
                else report.Merged++;
                acc.Parts++;
                acc.Vehicle |= c.Vehicle;
                if (Privacy(c.Privacy) == ContainerPrivacy.Private) acc.Privacy = ContainerPrivacy.Private; // never widen a personal chest
                acc.Slots += SlotsOf(c);
                if (c.Unreadable) continue;
                acc.AllUnreadable = false;
                foreach (var s in c.Stacks ?? new List<ScannedStack>())
                {
                    string sp = s == null ? null : Names.Prefab(s.Prefab);
                    if (sp == null || s.Stack <= 0) { report.InvalidStacks++; continue; }
                    acc.Used++;
                    int q = Names.Clamp(s.Quality, 1, 10);
                    string sk = sp + "@" + q.ToString(CultureInfo.InvariantCulture);
                    acc.Sums.TryGetValue(sk, out var cur);
                    acc.Sums[sk] = (cur.prefab ?? sp, q, cur.stack + s.Stack);
                }
            }

            var list = new List<ContainerSnapshot>(order.Count);
            var vehicleFlags = new List<bool>(order.Count);
            order.Sort(CompareAcc);
            foreach (var a in order)
            {
                var snap = new ContainerSnapshot
                {
                    Prefab = a.Prefab, X = a.X, Y = a.Y, Z = a.Z, CreatorPlayerId = a.CreatorText,
                    Privacy = a.Privacy, Vehicle = a.Vehicle, Unreadable = a.AllUnreadable,
                };
                long slots = a.Slots;
                if (slots > Caps.ContainerSlotsMax) { slots = Caps.ContainerSlotsMax; report.SlotsClamped++; }
                snap.Slots = (int)Math.Max(1, slots);
                if (!snap.Unreadable)
                {
                    var stacks = new List<ContainerStack>(a.Sums.Count);
                    foreach (var kv in a.Sums.Values)
                        stacks.Add(new ContainerStack { Prefab = kv.prefab, Quality = kv.quality, Stack = Names.Clamp(kv.stack, 1, 1_000_000_000) });
                    if (stacks.Count > Caps.ContainerItemsMax)
                    {
                        // Keep the largest stacks; the count dropped travels on this container (§1.8).
                        stacks.Sort((p, q) => q.Stack != p.Stack ? q.Stack.CompareTo(p.Stack) : CompareStack(p, q));
                        snap.OmittedEntries = stacks.Count - Caps.ContainerItemsMax;
                        report.ContainersOverEntries++;
                        report.EntriesOmitted += snap.OmittedEntries;
                        stacks.RemoveRange(Caps.ContainerItemsMax, stacks.Count - Caps.ContainerItemsMax);
                    }
                    stacks.Sort(CompareStack);
                    snap.Items = stacks;
                    snap.UsedSlots = Math.Min(a.Used, snap.Slots);
                }
                list.Add(snap);
                vehicleFlags.Add(a.Vehicle);
            }

            // Message caps (§1.8): drop vehicles first, then containers from the end of the sort order.
            int entries = 0;
            foreach (var s in list) entries += s.Items.Count;
            while (list.Count > Caps.ContainersMax || entries > Caps.ContainerEntriesMax)
            {
                int drop = vehicleFlags.LastIndexOf(true);
                if (drop < 0) drop = list.Count - 1;
                var gone = list[drop];
                entries -= gone.Items.Count;
                if (gone.Privacy == ContainerPrivacy.Private) report.OmittedPrivate++; else report.OmittedShared++;
                list.RemoveAt(drop); vehicleFlags.RemoveAt(drop);
            }

            var msg = new ContainersMessage
            {
                ServerId = header.ServerId,
                CapturedAt = MessageJson.Timestamp(header.CapturedAtUtc),
                GameVersion = header.GameVersion,
                ModVersion = header.ModVersion,
                WorldUid = header.WorldUid,
                Containers = list,
                OmittedShared = Math.Min(report.OmittedShared, 1_000_000),
                OmittedPrivate = Math.Min(report.OmittedPrivate, 1_000_000),
            };
            foreach (var s in list)
            {
                report.Containers++;
                if (s.Vehicle) report.Vehicles++;
                if (s.Privacy == ContainerPrivacy.Private) report.Private++;
                if (s.Unreadable) report.Unreadable++;
                report.Stacks += s.UsedSlots;
                report.Entries += s.Items.Count;
            }
            return msg;
        }

        /// <summary>The spool key of a world's containers snapshot (latest wins).</summary>
        public static string SpoolKey(string worldUid) => "containers|" + worldUid;

        /// <summary>slots = max(width × height, width × (highest decoded row + 1)): 1.0.20 rows may grow at runtime (§1.3).</summary>
        private static long SlotsOf(ScannedContainer c)
        {
            long w = Math.Max(1, c.Width), h = Math.Max(1, c.Height);
            return Math.Max(w * h, w * (c.MaxRow + 1L));
        }

        private static string Privacy(string p) =>
            p == ContainerPrivacy.Private || p == ContainerPrivacy.Group ? p : ContainerPrivacy.Public;

        /// <summary>World metres rounded to 0.1 m and kept inside the contract's |v| ≤ 32 768; -0 becomes 0.</summary>
        public static double Round(double v)
        {
            if (double.IsNaN(v) || double.IsInfinity(v)) return 0;
            double r = Math.Round(v * 10, MidpointRounding.AwayFromZero) / 10;
            if (r > Caps.WorldCoordMax) r = Caps.WorldCoordMax;
            if (r < -Caps.WorldCoordMax) r = -Caps.WorldCoordMax;
            return r == 0 ? 0 : r;
        }

        private static string Coord(double v) => v.ToString("R", CultureInfo.InvariantCulture);

        private static int CompareStack(ContainerStack a, ContainerStack b)
        {
            int c = string.CompareOrdinal(a.Prefab, b.Prefab);
            return c != 0 ? c : a.Quality.CompareTo(b.Quality);
        }

        private static int CompareAcc(Acc a, Acc b)
        {
            int c = string.CompareOrdinal(a.Prefab, b.Prefab);
            if (c == 0) c = a.X.CompareTo(b.X);
            if (c == 0) c = a.Z.CompareTo(b.Z);
            if (c == 0) c = a.Y.CompareTo(b.Y);
            if (c == 0) c = a.Creator.CompareTo(b.Creator);
            return c;
        }

        private sealed class Acc
        {
            public string Prefab, CreatorText, Privacy;
            public double X, Y, Z;
            public long Creator, Slots;
            public int Parts, Used;
            public bool Vehicle, AllUnreadable;
            public readonly Dictionary<string, (string prefab, int quality, long stack)> Sums = new Dictionary<string, (string, int, long)>(StringComparer.Ordinal);
        }
    }
}
