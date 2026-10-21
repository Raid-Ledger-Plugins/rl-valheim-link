using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using RlValheimLink.Contract;
using RlValheimLink.Core;

namespace RlValheimLink.Server
{
    /// <summary>
    /// The chest scan (docs/design-chests.md §1), game glue only: find every player-built container in the
    /// world's ZDOs and decode each with the game's own temporary inventory, as the chest dump spike proved
    /// on 1.0.20 (§0.1). The result is a list of <see cref="ScannedContainer"/>; rounding, aggregation, the
    /// caps and the message are <see cref="ContainersBuilder"/>'s. Time-sliced: at most one
    /// GetAllZDOsWithPrefabIterative call per tick while discovering, then about StacksPerTick decoded
    /// stacks per tick. Item values and positions are never logged.
    /// </summary>
    internal sealed class ChestScanner
    {
        private const int TallRows = 64, MaxCallsPerPrefab = 100_000;

        public sealed class PrefabInfo
        {
            public string Name; public int Hash, Width, Height; public string Privacy; public bool Vehicle;
        }

        public sealed class Result
        {
            public DateTime ScheduledAt;
            public List<ScannedContainer> Containers = new List<ScannedContainer>();
            public int Duplicates, Gone, Creator0, Dropped, Ticks;
            public double Ms, BusiestTickMs;
        }

        private readonly List<PrefabInfo> _prefabs;
        private readonly int _stacksPerTick;
        private IEnumerator<object> _run;
        private Result _result;
        private bool _finished;
        private int _itemDataHash;

        private ChestScanner(List<PrefabInfo> prefabs, int stacksPerTick)
        {
            _prefabs = prefabs; _stacksPerTick = Math.Max(1, stacksPerTick);
        }

        public IReadOnlyList<PrefabInfo> Prefabs => _prefabs;
        public bool Running => _run != null;

        /// <summary>
        /// Container prefabs to scan: those with a Container (on the root or a child, so modded chests and
        /// vehicles count) that a build tool can place, minus <paramref name="exclude"/>, plus
        /// <paramref name="extra"/>; vehicles (carts, ships) only when <paramref name="includeVehicles"/>.
        /// Null when the game is not ready yet.
        /// </summary>
        public static ChestScanner Create(int stacksPerTick, bool includeVehicles, ICollection<string> exclude, ICollection<string> extra)
        {
            if (ZNetScene.instance == null || ObjectDB.instance == null || ZDOMan.instance == null) return null;
            var buildable = new HashSet<string>(StringComparer.Ordinal);
            foreach (var go in ObjectDB.instance.m_items)
            {
                var drop = go != null ? go.GetComponent<ItemDrop>() : null;
                var table = drop != null && drop.m_itemData != null && drop.m_itemData.m_shared != null ? drop.m_itemData.m_shared.m_buildPieces : null;
                if (table == null || table.m_pieces == null) continue;
                foreach (var piece in table.m_pieces) if (piece != null) buildable.Add(piece.name);
            }
            var list = new List<PrefabInfo>();
            foreach (var go in ZNetScene.instance.m_prefabs)
            {
                if (go == null) continue;
                bool listed = extra.Contains(go.name);
                if (exclude.Contains(go.name) || (!buildable.Contains(go.name) && !listed)) continue;
                var containers = go.GetComponentsInChildren<Container>(true);
                if (containers.Length == 0) continue;
                var c = containers[0];
                bool vehicle = go.GetComponent<Ship>() != null || go.GetComponent<Vagon>() != null
                    || (GameAccess.TryRead(c, "m_rootObjectOverride", out var root) && root is UnityEngine.Object o && o != null);
                if (vehicle && !includeVehicles) continue;
                list.Add(new PrefabInfo
                {
                    Name = go.name, Hash = go.name.GetStableHashCode(), Width = c.m_width, Height = c.m_height, Vehicle = vehicle,
                    Privacy = c.m_privacy == Container.PrivacySetting.Private ? ContainerPrivacy.Private
                        : c.m_privacy == Container.PrivacySetting.Group ? ContainerPrivacy.Group : ContainerPrivacy.Public,
                });
            }
            list.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
            return new ChestScanner(list, stacksPerTick);
        }

        public void Begin(DateTime scheduledAt)
        {
            _result = new Result { ScheduledAt = scheduledAt };
            _finished = false;
            _itemDataHash = GameAccess.ZdoVar("s_itemData") ?? "itemData".GetStableHashCode();
            _run = Run(_result).GetEnumerator();
        }

        public void Cancel() { _run = null; _result = null; }

        /// <summary>One tick of the running scan; returns the result once, when the scan has finished.</summary>
        public Result Tick()
        {
            if (_run == null) return null;
            bool more;
            // A scan that throws is over: the running flag must never stick, or no later scan would start.
            try { more = _run.MoveNext(); }
            catch { _run = null; _result = null; throw; }
            if (more) return null;
            _run = null;
            var r = _result; _result = null;
            return _finished ? r : null;
        }

        private IEnumerable<object> Run(Result r)
        {
            var total = Stopwatch.StartNew();

            // Discovery: one iterator call per tick in total (production: 1.3 ms busiest tick, §0.1).
            // The iterator can return a ZDO twice and ZDOs are pooled, so ids are taken and deduplicated at once.
            var ids = new List<(PrefabInfo info, ZDOID id)>();
            var seen = new HashSet<ZDOID>();
            var found = new List<ZDO>();
            foreach (var p in _prefabs)
            {
                int index = 0, calls = 0;
                bool done = false;
                while (!done)
                {
                    if (ZDOMan.instance == null) yield break;
                    var tick = Stopwatch.StartNew();
                    done = ZDOMan.instance.GetAllZDOsWithPrefabIterative(p.Name, found, ref index);
                    foreach (var z in found)
                    {
                        if (z == null) continue;
                        if (seen.Add(z.m_uid)) ids.Add((p, z.m_uid)); else r.Duplicates++;
                    }
                    found.Clear();
                    if (++calls >= MaxCallsPerPrefab && !done) { Log.Warn($"chest scan: discovery of {p.Name} gave up after {calls} calls"); done = true; }
                    r.BusiestTickMs = Math.Max(r.BusiestTickMs, tick.Elapsed.TotalMilliseconds);
                    r.Ticks++;
                    yield return null;
                }
            }

            // Decode: re-resolve each id (the ZDO may be gone or reused), skip creator 0, decode, budget by stacks.
            var tickWatch = Stopwatch.StartNew();
            int budget = 0;
            foreach (var (p, id) in ids)
            {
                if (ZDOMan.instance == null) yield break;
                var zdo = ZDOMan.instance.GetZDO(id);
                if (zdo == null || zdo.GetPrefab() != p.Hash) { r.Gone++; continue; }
                long creator = zdo.GetLong(ZDOVars.s_creator, 0L);
                if (creator == 0L) { r.Creator0++; continue; }
                var pos = zdo.GetPosition();
                var sc = new ScannedContainer
                {
                    Prefab = p.Name, X = pos.x, Y = pos.y, Z = pos.z, Creator = creator, Privacy = p.Privacy, Vehicle = p.Vehicle,
                    Width = p.Width, Height = p.Height,
                };
                int stacks = Decode(zdo, p, sc, out int dropped);
                r.Dropped += dropped;
                r.Containers.Add(sc);
                budget += Math.Max(1, stacks);
                if (budget >= _stacksPerTick)
                {
                    r.BusiestTickMs = Math.Max(r.BusiestTickMs, tickWatch.Elapsed.TotalMilliseconds);
                    r.Ticks++; budget = 0;
                    yield return null;
                    tickWatch.Restart();
                }
            }
            r.BusiestTickMs = Math.Max(r.BusiestTickMs, tickWatch.Elapsed.TotalMilliseconds);
            r.Ticks++;
            r.Ms = total.Elapsed.TotalMilliseconds;
            _finished = true;
        }

        /// <summary>Decodes one container into <paramref name="sc"/>; a chest that cannot be decoded is marked unreadable (sent with no items). Returns the stacks decoded.</summary>
        private int Decode(ZDO zdo, PrefabInfo p, ScannedContainer sc, out int dropped)
        {
            dropped = 0;
            byte[] data;
            try { data = ReadData(zdo, out bool badBase64); if (badBase64) { sc.Unreadable = true; return 0; } }
            catch (Exception) { sc.Unreadable = true; return 0; }
            if (data == null) return 0; // no inventory data: an empty chest
            try
            {
                var inv = GameAccess.NewTemporaryInventory();
                if (inv != null) GameAccess.LoadInventory(inv, new ZPackage(data), true);
                else
                {
                    // Before 1.0.20 there is no temporary inventory: a tall scratch inventory keeps rows beyond the prefab's.
                    inv = new Inventory("rlvl", null, Math.Max(1, p.Width), Math.Max(p.Height, TallRows));
                    inv.Load(new ZPackage(data));
                }
                var items = inv.GetAllItems();
                foreach (var it in items)
                {
                    if (it == null) continue;
                    sc.MaxRow = Math.Max(sc.MaxRow, it.m_gridPos.y);
                    sc.Stacks.Add(new ScannedStack { Prefab = it.m_dropPrefab != null ? it.m_dropPrefab.name : null, Quality = it.m_quality, Stack = it.m_stack });
                }
                int version = data.Length >= 4 ? BitConverter.ToInt32(data, 0) : 0, header = NumItems(data);
                if (version >= 100 && version <= 200 && header > items.Count) dropped = header - items.Count; // unknown prefabs (a removed mod) the game skipped
                return items.Count;
            }
            catch (Exception)
            {
                sc.Unreadable = true; sc.Stacks.Clear(); sc.MaxRow = -1;
                return 0;
            }
        }

        /// <summary>The chest's inventory package, in the order the spike settled (§1.2): string "items" as base64 (older worlds), bytes "items" (1.0.20), bytes then string "itemData".</summary>
        private byte[] ReadData(ZDO zdo, out bool badBase64)
        {
            badBase64 = false;
            string s = zdo.GetString(ZDOVars.s_items, "");
            if (!string.IsNullOrEmpty(s)) return Base64(s, ref badBase64);
            byte[] b = zdo.GetByteArray(ZDOVars.s_items, null);
            if (b != null && b.Length > 0) return b;
            b = zdo.GetByteArray(_itemDataHash, null);
            if (b != null && b.Length > 0) return b;
            s = zdo.GetString(_itemDataHash, "");
            if (!string.IsNullOrEmpty(s)) return Base64(s, ref badBase64);
            return null;
        }

        private static byte[] Base64(string s, ref bool bad)
        {
            try { return Convert.FromBase64String(s); }
            catch (FormatException) { bad = true; return null; }
        }

        /// <summary>The stack count after the int32 version, as ZPackage.WriteNumItems writes it (one byte below 128, else two).</summary>
        private static int NumItems(byte[] data) =>
            data.Length >= 5 ? ((data[4] & 0x80) != 0 ? (data.Length >= 6 ? ((data[4] & 0x7F) << 8) | data[5] : -1) : data[4]) : -1;

        public static string Describe(IEnumerable<PrefabInfo> prefabs) =>
            string.Join(",", prefabs.Select(p => p.Name + (p.Vehicle ? "(vehicle)" : "") + (p.Privacy == ContainerPrivacy.Private ? "(personal)" : "")));

        public static string Ms(double ms) => ms.ToString("0.0", CultureInfo.InvariantCulture);
    }
}
