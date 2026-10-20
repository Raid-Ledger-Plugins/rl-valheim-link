using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using HarmonyLib;
using RlValheimLink.Client;
using UnityEngine;

namespace RlValheimLink.Server
{
    /// <summary>
    /// Chest-inventory spike (docs/design-chests.md §7), server only, on by default (Chests.Dump; the
    /// operator wants no flags to set). Two minutes after the world loads it finds every container ZDO, decodes each with the
    /// game's own Inventory.Load and logs `RLVL: chestdump[r&lt;run&gt;.c&lt;check&gt;]` lines for the seven
    /// checks; every later run (Chests.DumpRuns, Chests.DumpRerunMinutes apart) also reports what changed.
    /// It uploads nothing and adds no message kind. ZDO string values are never logged (only their length
    /// and whether they are base64): item prefab names, counts, ids and positions only.
    /// Time-sliced: one discovery call per prefab per tick, then at most StacksPerTick decoded stacks per tick.
    /// </summary>
    internal static class ChestDump
    {
        private const int FirstDelaySeconds = 120, StacksPerTick = 200, TallRows = 64, MaxProbes = 6, MaxListed = 10, MaxCallsPerPrefab = 100000;
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        private static IEnumerator<object> _run;
        private static int _runNumber, _runsTotal;
        private static DateTime _nextRunAt = DateTime.MaxValue;
        private static Dictionary<string, Snapshot> _previous;
        private static long[] _previousMaterials;
        private static DateTime _previousAt;
        private static string _chosenLoader;
        private static Dictionary<int, string> _names = new Dictionary<int, string>();
        private static int _itemsHash, _itemDataHash;
        private static int _itemDropAwakes;

        private sealed class PrefabInfo
        {
            public string Name; public int Hash, Width, Height; public Container.PrivacySetting Privacy; public bool Buildable, Vehicle;
            public readonly List<ZDO> Found = new List<ZDO>();
            public readonly List<ZDOID> Ids = new List<ZDOID>();
            public readonly HashSet<ZDOID> Seen = new HashSet<ZDOID>();
            public int Index, Calls; public bool Done;
            public int Raw, Duplicates, Gone, Creator0, Counted, Empty, Unreadable, Stacks, Dropped, HeaderMismatch, RowsBeyondPrefab, MaxRow = -1;
            public double DiscoveryMs, DecodeMs;
        }

        private sealed class Decoded
        {
            public string Source;                // string:items | bytes:items | bytes:itemData | string:itemData; null = no data
            public int HeaderVersion = int.MinValue, HeaderCount = -1;
            public List<ItemDrop.ItemData> Items;
            public string Error;
            public double Ms;
            public int MaxRow = -1;
            public bool HeaderPlausible => HeaderVersion >= 100 && HeaderVersion <= 200 && HeaderCount >= 0;
        }

        private sealed class Loader
        {
            public string Name;
            public Func<int, int, Inventory> Make;
            public Action<Inventory, ZPackage> Load;
        }

        private sealed class Snapshot
        {
            public string Prefab; public Vector3 Pos; public long Creator; public bool Private; public ZDOID Id; public bool Unreadable;
            public readonly SortedDictionary<string, int> Stacks = new SortedDictionary<string, int>(StringComparer.Ordinal);
        }

        public static void Start()
        {
            _run = null; _runNumber = 0; _previous = null; _previousMaterials = null; _chosenLoader = null;
            if (!PluginConfig.ChestsDumpOnStart.Value) { _nextRunAt = DateTime.MaxValue; return; }
            _runsTotal = PluginConfig.ChestsDumpRuns.Value;
            _nextRunAt = DateTime.UtcNow.AddSeconds(FirstDelaySeconds);
            Log.Info($"chestdump: ON (Chests.Dump, default on) — {_runsTotal} run(s), the first in {FirstDelaySeconds} s, then {PluginConfig.ChestsDumpRerunMinutes.Value} min apart; logs only, uploads nothing; materials {string.Join(",", Materials())}");
        }

        public static void Stop() { _run = null; _nextRunAt = DateTime.MaxValue; }

        public static void Tick(DateTime now)
        {
            if (_run != null)
            {
                bool more;
                try { more = _run.MoveNext(); }
                catch (Exception e) { Log.Error($"chestdump[r{_runNumber}] aborted", e); more = false; }
                if (more) return;
                _run = null;
                if (_runNumber < _runsTotal)
                {
                    _nextRunAt = now.AddMinutes(PluginConfig.ChestsDumpRerunMinutes.Value);
                    Log.Info($"chestdump: run {_runNumber + 1} of {_runsTotal} in {PluginConfig.ChestsDumpRerunMinutes.Value} min — move some items in a chest you know before then (freshness check)");
                }
                else
                {
                    _nextRunAt = DateTime.MaxValue;
                    Log.Info($"chestdump: finished ({_runsTotal} run(s)); it runs again after the next start unless [Chests] Dump = false");
                }
                return;
            }
            if (now < _nextRunAt || ZDOMan.instance == null || ZNetScene.instance == null || ObjectDB.instance == null) return;
            _runNumber++;
            _run = Run(_runNumber).GetEnumerator();
        }

        // ---------- one run ----------

        private static IEnumerable<object> Run(int run)
        {
            string r = "r" + run;
            var total = Stopwatch.StartNew();
            int ticks = 0;
            List<PrefabInfo> prefabs = null;
            Safe(r, "start", () => Header(r, run));
            Safe(r + ".c5", "prefab scan", () => prefabs = ContainerPrefabs(r, logIt: run == 1));
            if (prefabs == null || prefabs.Count == 0) { Log.Warn($"chestdump[{r}] no prefab with a Container in ZNetScene; nothing to do"); yield break; }
            if (run == 1)
            {
                Safe(r + ".c5", "station scan", () => Stations(r));
                Safe(r + ".c7", "1.0.20 fields", () => NewFields(r));
            }
            yield return null; ticks++;

            // c3 discovery: every container prefab, one GetAllZDOsWithPrefabIterative call per prefab per tick.
            // The ids are taken right after each call (ZDOs are pooled; a ZDO object can be reused later).
            int discoveryTicks = 0; double discoveryMaxTick = 0;
            var discovery = Stopwatch.StartNew();
            while (prefabs.Any(p => !p.Done))
            {
                if (ZDOMan.instance == null) { Log.Warn($"chestdump[{r}] ZDOMan went away; stopping"); yield break; }
                var tick = Stopwatch.StartNew();
                foreach (var p in prefabs)
                {
                    if (p.Done) continue;
                    var call = Stopwatch.StartNew();
                    if (!Safe(r + ".c3", "discovery " + p.Name, () => p.Done = ZDOMan.instance.GetAllZDOsWithPrefabIterative(p.Name, p.Found, ref p.Index))) p.Done = true;
                    p.DiscoveryMs += call.Elapsed.TotalMilliseconds;
                    foreach (var z in p.Found)
                    {
                        if (z == null) continue;
                        p.Raw++;
                        if (p.Seen.Add(z.m_uid)) p.Ids.Add(z.m_uid); else p.Duplicates++;
                    }
                    p.Found.Clear();
                    if (++p.Calls >= MaxCallsPerPrefab && !p.Done) { p.Done = true; Log.Warn($"chestdump[{r}.c3] discovery {p.Name}: gave up after {p.Calls} calls"); }
                }
                discoveryMaxTick = Math.Max(discoveryMaxTick, tick.Elapsed.TotalMilliseconds);
                discoveryTicks++;
                yield return null; ticks++;
            }
            Log.Info($"chestdump[{r}.c3] discovery: {prefabs.Count} container prefabs, {prefabs.Sum(p => p.Raw)} returned, {prefabs.Sum(p => p.Ids.Count)} unique, {prefabs.Sum(p => p.Duplicates)} duplicates removed, {Ms(discovery.Elapsed.TotalMilliseconds)} ms over {discoveryTicks} ticks (busiest tick {Ms(discoveryMaxTick)} ms, {prefabs.Max(p => p.Calls)} calls for the largest prefab); zdosResident={GameAccess.ResidentZdoCount()} playersConnected={PeersConnected()}");

            // c1 + c2 probes (first run): every key on a few known chests, every decode variant on them.
            if (run == 1)
            {
                Safe(r + ".c2", "member visibility", () => Visibility(r));
                Safe(r + ".c2", "probes", () => Probes(r, prefabs));
                yield return null; ticks++;
            }
            var loader = Loaders().FirstOrDefault(l => l.Name == _chosenLoader) ?? Loaders()[1];

            // c3 counts: re-resolve every id, skip creator 0, decode with the chosen loader.
            var materials = Materials();
            var matTotals = new long[materials.Length]; var matShared = new long[materials.Length]; var matChests = new int[materials.Length];
            var itemTotals = new Dictionary<string, long>(StringComparer.Ordinal);
            var sources = new SortedDictionary<string, int>(StringComparer.Ordinal);
            var snapshots = new Dictionary<string, Snapshot>(StringComparer.Ordinal);
            int stacksThisTick = 0, decodeTicks = 0, collisions = 0; double decodeMaxTick = 0;
            var decodeAll = Stopwatch.StartNew(); var decodeTick = Stopwatch.StartNew();
            foreach (var p in prefabs)
            {
                foreach (var id in p.Ids)
                {
                    if (ZDOMan.instance == null) { Log.Warn($"chestdump[{r}] ZDOMan went away; stopping"); yield break; }
                    var zdo = ZDOMan.instance.GetZDO(id);
                    if (zdo == null || zdo.GetPrefab() != p.Hash) { p.Gone++; continue; }
                    long creator = zdo.GetLong(ZDOVars.s_creator, 0L);
                    if (creator == 0L) { p.Creator0++; continue; }
                    p.Counted++;
                    Decoded d = null;
                    Safe(r + ".c3", "decode " + p.Name, () => d = Decode(zdo, p, loader));
                    var snap = new Snapshot { Prefab = p.Name, Pos = zdo.GetPosition(), Creator = creator, Private = p.Privacy == Container.PrivacySetting.Private, Id = id };
                    string source = d?.Source ?? "none";
                    sources[source] = (sources.TryGetValue(source, out int sc) ? sc : 0) + 1;
                    if (d == null || d.Error != null) { p.Unreadable++; snap.Unreadable = true; }
                    else if (d.Source == null) p.Empty++;
                    else
                    {
                        p.DecodeMs += d.Ms;
                        p.Stacks += d.Items.Count;
                        stacksThisTick += d.Items.Count;
                        if (d.HeaderPlausible) { if (d.HeaderCount >= d.Items.Count) p.Dropped += d.HeaderCount - d.Items.Count; else p.HeaderMismatch++; }
                        else p.HeaderMismatch++;
                        p.MaxRow = Math.Max(p.MaxRow, d.MaxRow);
                        if (d.MaxRow >= p.Height) p.RowsBeyondPrefab++;
                        var inChest = new bool[materials.Length];
                        foreach (var it in d.Items)
                        {
                            string name = ItemName(it);
                            string key = name + " q" + it.m_quality.ToString(Inv);
                            snap.Stacks[key] = (snap.Stacks.TryGetValue(key, out int s) ? s : 0) + it.m_stack;
                            itemTotals[name] = (itemTotals.TryGetValue(name, out long t) ? t : 0L) + it.m_stack;
                            for (int m = 0; m < materials.Length; m++)
                            {
                                if (!string.Equals(name, materials[m], StringComparison.OrdinalIgnoreCase)) continue;
                                matTotals[m] += it.m_stack; inChest[m] = true;
                                if (!snap.Private) matShared[m] += it.m_stack;
                            }
                        }
                        for (int m = 0; m < materials.Length; m++) if (inChest[m]) matChests[m]++;
                    }
                    string skey = SnapshotKey(snap);
                    if (snapshots.ContainsKey(skey)) collisions++; else snapshots[skey] = snap;
                    if (stacksThisTick >= StacksPerTick)
                    {
                        decodeMaxTick = Math.Max(decodeMaxTick, decodeTick.Elapsed.TotalMilliseconds);
                        stacksThisTick = 0; decodeTicks++;
                        yield return null; ticks++;
                        decodeTick.Restart();
                    }
                }
            }
            decodeMaxTick = Math.Max(decodeMaxTick, decodeTick.Elapsed.TotalMilliseconds); decodeTicks++;
            Safe(r + ".c3", "counts", () => Counts(r, prefabs, loader, sources, decodeAll.Elapsed.TotalMilliseconds, decodeTicks, decodeMaxTick, collisions));
            Safe(r + ".c3", "materials", () => MaterialLines(r, materials, matTotals, matShared, matChests, itemTotals));
            Safe(r + ".c4", "creator", () => Creators(r, snapshots));
            if (_previous != null) Safe(r + ".c6", "freshness", () => Freshness(r, snapshots, materials, matTotals));
            _previous = snapshots; _previousMaterials = matTotals; _previousAt = DateTime.UtcNow;
            Log.Info($"chestdump[{r}] done in {total.Elapsed.TotalSeconds.ToString("0.0", Inv)} s over {ticks} ticks");
        }

        // ---------- start of a run ----------

        private static void Header(string r, int run)
        {
            _names = GameAccess.ZdoVarNames();
            _itemsHash = ZDOVars.s_items;
            int? itemDataVar = GameAccess.ZdoVar("s_itemData"), creatorIndexVar = GameAccess.ZdoVar("s_creatorIndex");
            _itemDataHash = itemDataVar ?? "itemData".GetStableHashCode();
            Log.Info($"chestdump[{r}] start (run {run} of {_runsTotal}): game={GameInfo.GameVersion()} dedicated={ZNet.instance != null && ZNet.instance.IsDedicated()} world={GameInfo.WorldUid()} zdosResident={GameAccess.ResidentZdoCount()} playersConnected={PeersConnected()} zdoVarNames={_names.Count}");
            if (run > 1) return;
            Log.Info($"chestdump[{r}.c1] hashes: {Compare("items", ZDOVars.s_items)}; {Compare("itemData", itemDataVar)}; {Compare("creator", ZDOVars.s_creator)}; {Compare("creatorIndex", creatorIndexVar)}");
        }

        private static string Compare(string literal, int? zdoVar)
        {
            int h = literal.GetStableHashCode();
            string v = zdoVar.HasValue ? zdoVar.Value.ToString(Inv) : "missing";
            string verdict = !zdoVar.HasValue ? "no such ZDOVars field" : zdoVar.Value == h ? "same" : "DIFFERENT";
            return $"\"{literal}\"={h} ZDOVars.s_{literal}={v} ({verdict})";
        }

        // ---------- c5: prefabs ----------

        private static List<PrefabInfo> ContainerPrefabs(string r, bool logIt)
        {
            var buildable = BuildablePieces();
            var list = new List<PrefabInfo>();
            foreach (var go in ZNetScene.instance.m_prefabs)
            {
                if (go == null) continue;
                var containers = go.GetComponentsInChildren<Container>(true);
                if (containers.Length == 0) continue;
                var c = containers[0];
                bool vehicle = go.GetComponent<Ship>() != null || go.GetComponent<Vagon>() != null || (GameAccess.TryRead(c, "m_rootObjectOverride", out var root) && root is UnityEngine.Object o && o != null);
                var p = new PrefabInfo
                {
                    Name = go.name, Hash = go.name.GetStableHashCode(), Width = c.m_width, Height = c.m_height, Privacy = c.m_privacy,
                    Buildable = buildable.Contains(go.name), Vehicle = vehicle,
                };
                list.Add(p);
                if (logIt)
                    Log.Info($"chestdump[{r}.c5] Container {go.name}: size={c.m_width}x{c.m_height} privacy={c.m_privacy} buildable={p.Buildable} piece={go.GetComponent<Piece>() != null} vehicle={vehicle} on={(c.gameObject == go ? "root" : c.gameObject.name)} containers={containers.Length} {Fields(c, "m_checkGuardStone", "m_autoDestroyEmpty", "m_rootObjectOverride", "m_wagon")}");
            }
            if (logIt)
            {
                int nonNet = ZNetScene.instance.m_nonNetViewPrefabs.Count(go => go != null && go.GetComponentsInChildren<Container>(true).Length > 0);
                Log.Info($"chestdump[{r}.c5] containers: {list.Count} prefabs ({list.Count(p => p.Buildable)} buildable, {list.Count(p => p.Vehicle)} vehicles, {list.Count(p => p.Privacy == Container.PrivacySetting.Private)} private); {buildable.Count} buildable pieces from tools; {nonNet} non-netview prefabs with a Container (never ZDOs)");
            }
            return list;
        }

        private static HashSet<string> BuildablePieces()
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            foreach (var go in ObjectDB.instance.m_items)
            {
                var drop = go != null ? go.GetComponent<ItemDrop>() : null;
                var table = drop != null && drop.m_itemData != null && drop.m_itemData.m_shared != null ? drop.m_itemData.m_shared.m_buildPieces : null;
                if (table == null || table.m_pieces == null) continue;
                foreach (var piece in table.m_pieces) if (piece != null) set.Add(piece.name);
            }
            return set;
        }

        private static IEnumerable<(string prefab, Component comp)> StationComponents()
        {
            foreach (var go in ZNetScene.instance.m_prefabs.Concat(ZNetScene.instance.m_nonNetViewPrefabs))
            {
                if (go == null) continue;
                foreach (var c in go.GetComponentsInChildren<Smelter>(true)) yield return (go.name, c);
                foreach (var c in go.GetComponentsInChildren<Fermenter>(true)) yield return (go.name, c);
                foreach (var c in go.GetComponentsInChildren<CookingStation>(true)) yield return (go.name, c);
                foreach (var c in go.GetComponentsInChildren<Incinerator>(true)) yield return (go.name, c);
                foreach (var c in go.GetComponentsInChildren<CraftingStation>(true)) yield return (go.name, c);
            }
        }

        private static void Stations(string r)
        {
            var counts = new SortedDictionary<string, int>(StringComparer.Ordinal);
            foreach (var (prefab, c) in StationComponents())
            {
                string type = c.GetType().Name;
                counts[type] = (counts.TryGetValue(type, out int n) ? n : 0) + 1;
                string at = c.gameObject.name == prefab ? "" : $" on={c.gameObject.name}";
                switch (c)
                {
                    case Smelter _:
                        Log.Info($"chestdump[{r}.c5] Smelter {prefab}{at}: {Fields(c, "m_name", "m_fuelItem", "m_maxFuel", "m_fuelPerProduct", "m_secPerProduct", "m_maxOre", "m_requiresRoof", "m_windmill", "m_spawnStack")} conversions={List(c, "m_conversion", e => $"{F(e, "m_from")}→{F(e, "m_to")}")}");
                        break;
                    case Fermenter _:
                        Log.Info($"chestdump[{r}.c5] Fermenter {prefab}{at}: {Fields(c, "m_name", "m_fermentationDuration", "m_tapDelay")} conversions={List(c, "m_conversion", e => $"{F(e, "m_from")}→{F(e, "m_to")}×{F(e, "m_producedItems")}")}");
                        break;
                    case CookingStation _:
                        Log.Info($"chestdump[{r}.c5] CookingStation {prefab}{at}: {Fields(c, "m_name", "m_useFuel", "m_fuelItem", "m_maxFuel", "m_secPerFuel", "m_overCookedItem", "m_slots", "m_requireFire")} conversions={List(c, "m_conversion", e => $"{F(e, "m_from")}→{F(e, "m_to")} {F(e, "m_cookTime")}s")}");
                        break;
                    case Incinerator _:
                        Log.Info($"chestdump[{r}.c5] Incinerator {prefab}{at}: {Fields(c, "m_defaultResult", "m_defaultCost", "m_container")} conversions={List(c, "m_conversions", e => $"p{F(e, "m_priority")} {List(e, "m_requirements", q => $"{F(q, "m_resItem")}×{F(q, "m_amount")}", "+")}→{F(e, "m_result")}×{F(e, "m_resultAmount")}{(F(e, "m_requireOnlyOneIngredient") == "True" ? " (any one)" : "")}")}");
                        break;
                    case CraftingStation _:
                        Log.Info($"chestdump[{r}.c5] CraftingStation {prefab}{at}: {Fields(c, "m_name", "m_craftRequireRoof", "m_craftRequireFire", "m_showBasicRecipies", "m_rangeBuild")}");
                        break;
                }
            }
            Log.Info($"chestdump[{r}.c5] stations: {string.Join(", ", counts.Select(kv => kv.Key + "=" + kv.Value))}");
        }

        // ---------- c7: fields new in 1.0.20 ----------

        private static void NewFields(string r)
        {
            var stations = StationComponents().ToList();
            Log.Info($"chestdump[{r}.c7] Smelter.m_noSourceConversion ({GameAccess.MemberVisibility(typeof(Smelter), "m_noSourceConversion")}): {string.Join(" ", stations.Where(s => s.comp is Smelter).Select(s => s.prefab + "=" + F(s.comp, "m_noSourceConversion")))}");
            foreach (var (prefab, c) in stations.Where(s => s.comp is CookingStation))
                Log.Info($"chestdump[{r}.c7] CookingStation {prefab}: {Fields(c, "m_canOvercookItems", "m_canGiveBonusYield", "m_useFueldWhileEmpty", "m_spawnFullDurability", "m_recordCrafter", "m_cheapFireCheck")}");
            foreach (var (prefab, c) in stations.Where(s => s.comp is CraftingStation))
                Log.Info($"chestdump[{r}.c7] CraftingStation {prefab}: {Fields(c, "m_upgrader", "m_canRepair", "m_hasCraftTab")}");

            var recipes = ObjectDB.instance.m_recipes.Where(x => x != null).ToList();
            var noCraft = recipes.Where(x => F(x, "m_noCraftOnlyUpgrade") == "True").Select(x => x.name).ToList();
            Log.Info($"chestdump[{r}.c7] Recipe.m_noCraftOnlyUpgrade ({GameAccess.MemberVisibility(typeof(Recipe), "m_noCraftOnlyUpgrade")}): true on {noCraft.Count} of {recipes.Count} recipes{Sample(noCraft)}");
            var upgrader = new List<string>();
            foreach (var x in recipes)
                if (x.m_resources != null)
                    foreach (var q in x.m_resources)
                        if (q != null && F(q, "m_upgraderResource") == "True") upgrader.Add($"{x.name}:{(q.m_resItem != null ? q.m_resItem.name : "null")}");
            Log.Info($"chestdump[{r}.c7] Piece.Requirement.m_upgraderResource ({GameAccess.MemberVisibility(typeof(Piece.Requirement), "m_upgraderResource")}): true on {upgrader.Count} requirements{Sample(upgrader)}");
            var stationUse = recipes.Where(x => x.m_craftingStation != null).GroupBy(x => x.m_craftingStation.name).Select(g => $"{g.Key}={g.Count()}");
            Log.Info($"chestdump[{r}.c7] recipes per crafting station: {string.Join(" ", stationUse)}; no station={recipes.Count(x => x.m_craftingStation == null)}");
        }

        // ---------- c2: member visibility in the real 1.0.20 assembly ----------

        private static void Visibility(string r)
        {
            void Line(Type t, params string[] members) =>
                Log.Info($"chestdump[{r}.c2] visibility {t.Name}: {string.Join("  ", members.Select(m => m + "=" + GameAccess.MemberVisibility(t, m)))}");
            Line(typeof(Inventory), ".ctor", "Load", "LoadOld", "SetHeight", "AddTempItem", "GetAllItems", "m_temoraryInventory");
            Line(typeof(ZDOMan), "GetAllZDOsWithPrefabIterative", "GetZDO", "m_objectsBySector", "m_objectsByID", "ConvertContainers", "ConvertInventories");
            Line(typeof(ZDOExtraData), "GetData", "GetStrings", "GetByteArrays", "GetAllZDOIDsWithHash");
            Line(typeof(ZDO), "GetString", "GetByteArray", "GetLong", "GetPrefab", "GetPosition");
            Line(typeof(ZNetScene), "GetPrefab", "FindInstance", "m_prefabs");
            Line(typeof(Container), "m_privacy", "m_width", "m_height", "UpdateRows", "CheckAccess", "GetInventory", "Load", "Save");
            Line(typeof(Piece), "SetCreator", "GetCreator", "GetCreatorPlatformUserIdIndex", "m_creatorPlatformUserIDIndex");
            Line(typeof(ItemDrop.ItemData), "m_cheated", "m_dropPrefab", "m_worldLevel");
            Line(typeof(ZDOVars), "s_items", "s_itemData", "s_creator", "s_creatorIndex");
        }

        // ---------- c1 + c2: probes ----------

        private static List<Loader> Loaders() => new List<Loader>
        {
            new Loader { Name = "Inventory(w,h).Load(pkg)", Make = (w, h) => new Inventory("rlvl", null, w, h), Load = (inv, pkg) => inv.Load(pkg) },
            new Loader { Name = $"Inventory(w,{TallRows}).Load(pkg)", Make = (w, h) => new Inventory("rlvl", null, w, Math.Max(h, TallRows)), Load = (inv, pkg) => inv.Load(pkg) },
            new Loader { Name = "Inventory(true).Load(pkg,true)", Make = (w, h) => GameAccess.NewTemporaryInventory(), Load = (inv, pkg) => GameAccess.LoadInventory(inv, pkg, true) },
            new Loader { Name = "Inventory(true).Load(pkg)", Make = (w, h) => GameAccess.NewTemporaryInventory(), Load = (inv, pkg) => inv.Load(pkg) },
            new Loader { Name = $"Inventory(w,{TallRows}).Load(pkg,true)", Make = (w, h) => new Inventory("rlvl", null, w, Math.Max(h, TallRows)), Load = (inv, pkg) => GameAccess.LoadInventory(inv, pkg, true) },
            new Loader { Name = $"Inventory(w,{TallRows}).Load(pkg,false)", Make = (w, h) => new Inventory("rlvl", null, w, Math.Max(h, TallRows)), Load = (inv, pkg) => GameAccess.LoadInventory(inv, pkg, false) },
        };

        private static void Probes(string r, List<PrefabInfo> prefabs)
        {
            var probes = PickProbes(prefabs);
            Log.Info($"chestdump[{r}.c1] probes: {probes.Count} ({string.Join(", ", probes.Select(p => p.info.Name))}); one per prefab, the first player-built one with inventory data");
            var votes = new Dictionary<string, int>(StringComparer.Ordinal);
            var harmony = new Harmony(Plugin.Guid + ".chestdump");
            var awake = GameAccess.ItemDropAwake();
            bool counting = false;
            try
            {
                // Counting ItemDrop.Awake calls shows whether a decode instantiates item objects, whatever the
                // game does to destroy them; without the patch the probes still run (object-count delta only).
                try
                {
                    if (awake != null)
                    {
                        harmony.Patch(awake, postfix: new HarmonyMethod(typeof(ChestDump).GetMethod(nameof(CountItemDropAwake), BindingFlags.NonPublic | BindingFlags.Static)));
                        counting = true;
                    }
                    else Log.Info($"chestdump[{r}.c2] ItemDrop.Awake not found; instantiation is judged by the object count only");
                }
                catch (Exception e) { Log.Warn($"chestdump[{r}.c2] could not count ItemDrop.Awake ({Short(e)}); instantiation is judged by the object count only"); }
                int n = 0;
                foreach (var (info, zdo) in probes)
                {
                    string tag = $"{r}.c1.p{++n}";
                    Safe(tag, "key list", () => ListKeys(tag, zdo, info.Name));
                    string ctag = $"{r}.c2.p{n}";
                    Safe(ctag, "decode variants", () =>
                    {
                        string vote = ProbeDecode(ctag, zdo, info, counting);
                        if (vote != null) votes[vote] = (votes.TryGetValue(vote, out int v) ? v : 0) + 1;
                    });
                }
            }
            finally { harmony.UnpatchSelf(); }
            var best = votes.OrderByDescending(kv => kv.Value).FirstOrDefault();
            _chosenLoader = best.Key ?? Loaders()[1].Name;
            Log.Info($"chestdump[{r}.c2] decision: counting with {_chosenLoader} ({(best.Key == null ? "default: no probe decoded anything" : $"{best.Value} of {votes.Values.Sum()} probes")}; votes {string.Join(", ", votes.Select(kv => kv.Key + "=" + kv.Value))})");
        }

        private static void CountItemDropAwake() => _itemDropAwakes++;

        /// <summary>One probe per container prefab (buildable chests first, then vehicles, then the rest): the first id with a creator and inventory data, else the first with a creator.</summary>
        private static List<(PrefabInfo info, ZDO zdo)> PickProbes(List<PrefabInfo> prefabs)
        {
            var order = prefabs.Where(p => p.Ids.Count > 0)
                .OrderByDescending(p => p.Buildable && !p.Vehicle).ThenByDescending(p => p.Privacy == Container.PrivacySetting.Private).ThenByDescending(p => p.Buildable).ThenByDescending(p => p.Ids.Count);
            var probes = new List<(PrefabInfo, ZDO)>();
            foreach (var p in order)
            {
                ZDO withData = null, withCreator = null;
                foreach (var id in p.Ids)
                {
                    var z = ZDOMan.instance.GetZDO(id);
                    if (z == null || z.GetPrefab() != p.Hash || z.GetLong(ZDOVars.s_creator, 0L) == 0L) continue;
                    if (withCreator == null) withCreator = z;
                    if (ReadData(z, out _) != null) { withData = z; break; }
                }
                var pick = withData ?? withCreator;
                if (pick != null) probes.Add((p, pick));
                if (probes.Count >= MaxProbes) break;
            }
            return probes;
        }

        private static void ListKeys(string tag, ZDO zdo, string prefab)
        {
            var k = GameAccess.ZdoKeys(zdo.m_uid);
            Log.Info($"chestdump[{tag}] keys of {prefab} @{Pos(zdo.GetPosition())} zdoid={zdo.m_uid} owner={zdo.GetOwner()} dataRevision={zdo.DataRevision} persistent={zdo.Persistent} via {k.Via}: floats={k.Floats.Count} vec3={k.Vec3s.Count} quats={k.Quats.Count} ints={k.Ints.Count} longs={k.Longs.Count} strings={k.Strings.Count} byteArrays={k.ByteArrays.Count}");
            // Values are never logged; the first bytes only for the inventory keys or data that starts like an
            // inventory package (an int32 version 100–200), so an unknown key holding player text stays unprinted.
            foreach (var s in k.Strings)
            {
                byte[] decoded = Base64(s.Value);
                Log.Info($"chestdump[{tag}]   string {KeyName(s.Key)}{Mark(s.Key)} len={s.Value?.Length ?? 0} base64={(decoded != null ? $"yes ({decoded.Length} bytes{Head(s.Key, decoded)})" : "no")}");
            }
            foreach (var b in k.ByteArrays) Log.Info($"chestdump[{tag}]   bytes {KeyName(b.Key)}{Mark(b.Key)} len={b.Value?.Length ?? 0}{Head(b.Key, b.Value)}");
            foreach (var i in k.Ints) Log.Info($"chestdump[{tag}]   int {KeyName(i.Key)}{Mark(i.Key)} = {i.Value}");
            foreach (var l in k.Longs) Log.Info($"chestdump[{tag}]   long {KeyName(l.Key)}{Mark(l.Key)} = {l.Value}");
            if (k.Floats.Count + k.Vec3s.Count + k.Quats.Count > 0)
                Log.Info($"chestdump[{tag}]   floats: {string.Join(" ", k.Floats.Select(f => KeyName(f.Key)))}; vec3: {string.Join(" ", k.Vec3s.Select(f => KeyName(f.Key)))}; quats: {string.Join(" ", k.Quats.Select(f => KeyName(f.Key)))}");
        }

        /// <summary>Every loader on one probe; returns the loader this probe votes for (null when nothing decoded).</summary>
        private static string ProbeDecode(string tag, ZDO zdo, PrefabInfo p, bool countingAwakes)
        {
            byte[] data = ReadData(zdo, out string source);
            if (data == null)
            {
                Log.Info($"chestdump[{tag}] no inventory data under items/itemData (source={source ?? "none"}); the key list above shows what is there");
                return null;
            }
            int v = data.Length >= 4 ? BitConverter.ToInt32(data, 0) : int.MinValue;
            int c = data.Length >= 8 ? BitConverter.ToInt32(data, 4) : -1;
            int numItems = data.Length >= 5 ? ((data[4] & 0x80) != 0 && data.Length >= 6 ? ((data[4] & 0x7F) << 8) | data[5] : data[4]) : -1;
            Log.Info($"chestdump[{tag}] {p.Name} source={source} {data.Length} bytes; header int32 version={v} ({GameAccess.ItemVersionName(v) ?? "not a Version.Item value"}) int32 count={c} (numItems reading={numItems}) first16={Hex(data, 16)}");

            var results = new List<(Loader loader, Decoded d, int awakes, int objects)>();
            foreach (var L in Loaders())
            {
                var d = new Decoded { Source = source };
                var pkg = new ZPackage(data);
                Inventory inv = null;
                int awakesBefore = _itemDropAwakes, objectsBefore = CountItemDrops();
                var sw = Stopwatch.StartNew();
                try
                {
                    inv = L.Make(p.Width, p.Height) ?? throw new MissingMethodException("Inventory", ".ctor(Boolean)");
                    L.Load(inv, pkg);
                    d.Items = new List<ItemDrop.ItemData>(inv.GetAllItems());
                    foreach (var it in d.Items) d.MaxRow = Math.Max(d.MaxRow, it.m_gridPos.y);
                }
                catch (Exception e) { d.Error = Short(e); }
                d.Ms = sw.Elapsed.TotalMilliseconds;
                int awakes = _itemDropAwakes - awakesBefore, objects = CountItemDrops() - objectsBefore;
                results.Add((L, d, awakes, objects));
                string size = inv != null ? $"{inv.GetWidth()}x{inv.GetHeight()}" : "-";
                string read = SafeString(() => $"{pkg.GetPos()}/{pkg.Size()}");
                Log.Info(d.Error != null
                    ? $"chestdump[{tag}] loader {L.Name}: threw {d.Error} (read {read} bytes)"
                    : $"chestdump[{tag}] loader {L.Name}: stacks={d.Items.Count} in {Ms(d.Ms)} ms ({Ms(d.Items.Count > 0 ? d.Ms / d.Items.Count : 0)} ms/stack, first call includes JIT) itemDrop.Awake={(countingAwakes ? awakes.ToString(Inv) : "n/a")} itemDropObjects+={objects} inventory={size} maxRow={d.MaxRow} read={read} bytes dropPrefabNull={d.Items.Count(i => i.m_dropPrefab == null)}");
            }

            var ok = results.Where(x => x.d.Error == null).ToList();
            if (ok.Count == 0) return null;
            int most = ok.Max(x => x.d.Items.Count);
            var best = ok.FirstOrDefault(x => x.loader.Name.StartsWith("Inventory(true)", StringComparison.Ordinal) && x.d.Items.Count == most && x.awakes == 0 && x.objects == 0 && x.d.Items.All(i => i.m_dropPrefab != null));
            if (best.loader == null) best = ok.FirstOrDefault(x => x.loader.Name == Loaders()[1].Name && x.d.Items.Count == most);
            if (best.loader == null) best = ok.First(x => x.d.Items.Count == most);
            foreach (var it in best.d.Items.Take(8))
                Log.Info($"chestdump[{tag}]   item {ItemName(it)} x{it.m_stack} q{it.m_quality} v{it.m_variant} at ({it.m_gridPos.x},{it.m_gridPos.y}) worldLevel={it.m_worldLevel} cheated={F(it, "m_cheated")} shared={(it.m_shared != null ? it.m_shared.m_name : "null")}");
            if (best.d.Items.Count > 8) Log.Info($"chestdump[{tag}]   … {best.d.Items.Count - 8} more stacks");
            Log.Info($"chestdump[{tag}] rows: prefab {p.Width}x{p.Height}, decoded maxRow={best.d.MaxRow}{(best.d.MaxRow >= p.Height ? " → BEYOND the prefab's rows" : "")}; header count {c} vs decoded {best.d.Items.Count}; live instance: {LiveInventory(zdo)}; vote {best.loader.Name}");
            return most > 0 ? best.loader.Name : null;
        }

        private static int CountItemDrops()
        {
            try { return Resources.FindObjectsOfTypeAll<ItemDrop>().Length; } catch { return -1; }
        }

        private static string LiveInventory(ZDO zdo)
        {
            var nv = ZNetScene.instance.FindInstance(zdo);
            if (nv == null) return "not loaded";
            var c = nv.GetComponentInChildren<Container>(true);
            var inv = c != null ? c.GetInventory() : null;
            return inv == null ? "loaded, no inventory" : $"{inv.GetWidth()}x{inv.GetHeight()} stacks={inv.GetAllItems().Count}";
        }

        // ---------- decode ----------

        /// <summary>The design's order (design-chests §1.2): string "items" as base64, byte[] "items", byte[] "itemData", string "itemData".</summary>
        private static byte[] ReadData(ZDO zdo, out string source)
        {
            string s = zdo.GetString(ZDOVars.s_items, "");
            if (!string.IsNullOrEmpty(s)) { source = "string:items"; return Base64(s); }
            byte[] b = zdo.GetByteArray(ZDOVars.s_items, null);
            if (b != null && b.Length > 0) { source = "bytes:items"; return b; }
            b = zdo.GetByteArray(_itemDataHash, null);
            if (b != null && b.Length > 0) { source = "bytes:itemData"; return b; }
            s = zdo.GetString(_itemDataHash, "");
            if (!string.IsNullOrEmpty(s)) { source = "string:itemData"; return Base64(s); }
            source = null;
            return null;
        }

        private static Decoded Decode(ZDO zdo, PrefabInfo p, Loader loader)
        {
            var d = new Decoded();
            byte[] data = ReadData(zdo, out d.Source);
            if (data == null) { if (d.Source != null) d.Error = d.Source + " is not base64"; return d; }
            if (data.Length >= 8) { d.HeaderVersion = BitConverter.ToInt32(data, 0); d.HeaderCount = BitConverter.ToInt32(data, 4); }
            var sw = Stopwatch.StartNew();
            try
            {
                var inv = loader.Make(p.Width, p.Height) ?? throw new MissingMethodException("Inventory", ".ctor(Boolean)");
                loader.Load(inv, new ZPackage(data));
                d.Items = new List<ItemDrop.ItemData>(inv.GetAllItems());
                foreach (var it in d.Items) d.MaxRow = Math.Max(d.MaxRow, it.m_gridPos.y);
            }
            catch (Exception e) { d.Error = Short(e); }
            d.Ms = sw.Elapsed.TotalMilliseconds;
            return d;
        }

        // ---------- c3: counts ----------

        private static void Counts(string r, List<PrefabInfo> prefabs, Loader loader, SortedDictionary<string, int> sources, double decodeMs, int decodeTicks, double decodeMaxTick, int collisions)
        {
            foreach (var p in prefabs.Where(x => x.Raw > 0).OrderBy(x => x.Name, StringComparer.Ordinal))
                Log.Info($"chestdump[{r}.c3] {p.Name}: found={p.Raw} unique={p.Ids.Count} duplicates={p.Duplicates} gone={p.Gone} creator0={p.Creator0} counted={p.Counted} empty={p.Empty} unreadable={p.Unreadable} stacks={p.Stacks} dropped={p.Dropped} headerMismatch={p.HeaderMismatch} maxRow={p.MaxRow} rowsBeyondPrefab={p.RowsBeyondPrefab} (prefab {p.Width}x{p.Height} {p.Privacy}{(p.Buildable ? " buildable" : "")}{(p.Vehicle ? " vehicle" : "")}) discovery={Ms(p.DiscoveryMs)} ms over {p.Calls} calls decode={Ms(p.DecodeMs)} ms");
            var none = prefabs.Where(x => x.Raw == 0).Select(x => x.Name).OrderBy(x => x, StringComparer.Ordinal).ToList();
            if (none.Count > 0) Log.Info($"chestdump[{r}.c3] none in this world: {string.Join(", ", none)}");
            int stacks = prefabs.Sum(p => p.Stacks), counted = prefabs.Sum(p => p.Counted);
            double perStack = stacks > 0 ? prefabs.Sum(p => p.DecodeMs) / stacks : 0;
            Log.Info($"chestdump[{r}.c3] totals with {loader.Name}: counted={counted} ({prefabs.Where(p => p.Vehicle).Sum(p => p.Counted)} in vehicles, {prefabs.Where(p => p.Privacy == Container.PrivacySetting.Private).Sum(p => p.Counted)} private) creator0={prefabs.Sum(p => p.Creator0)} duplicates={prefabs.Sum(p => p.Duplicates)} gone={prefabs.Sum(p => p.Gone)} empty={prefabs.Sum(p => p.Empty)} unreadable={prefabs.Sum(p => p.Unreadable)} stacks={stacks} dropped={prefabs.Sum(p => p.Dropped)} headerMismatch={prefabs.Sum(p => p.HeaderMismatch)} rowsBeyondPrefab={prefabs.Sum(p => p.RowsBeyondPrefab)} samePositionAndCreator={collisions}; decode {Ms(decodeMs)} ms wall over {decodeTicks} ticks (busiest tick {Ms(decodeMaxTick)} ms), {Ms(perStack)} ms/stack");
            Log.Info($"chestdump[{r}.c3] data sources: {string.Join(", ", sources.Select(kv => kv.Key + "=" + kv.Value))}");
        }

        private static void MaterialLines(string r, string[] materials, long[] totals, long[] shared, int[] chests, Dictionary<string, long> itemTotals)
        {
            for (int m = 0; m < materials.Length; m++)
            {
                bool known = ObjectDB.instance.GetItemPrefab(materials[m]) != null;
                Log.Info($"chestdump[{r}.c3] material {materials[m]}{(known ? "" : " (NOT an item prefab in ObjectDB — check the spelling)")}: {totals[m]} in {chests[m]} containers (shared {shared[m]}, personal {totals[m] - shared[m]})");
            }
            Log.Info($"chestdump[{r}.c3] top items: {string.Join(", ", itemTotals.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal).Take(10).Select(kv => kv.Key + "=" + kv.Value))} ({itemTotals.Count} distinct)");
        }

        // ---------- c4: creator on personal chests ----------

        private static void Creators(string r, Dictionary<string, Snapshot> snapshots)
        {
            var personal = snapshots.Values.Where(s => s.Private).OrderBy(s => s.Prefab, StringComparer.Ordinal).ThenBy(s => s.Pos.x).ToList();
            int creatorIndexHash = GameAccess.ZdoVar("s_creatorIndex") ?? "creatorIndex".GetStableHashCode();
            var peers = PeerIdentity.Known();
            Log.Info($"chestdump[{r}.c4] personal (privacy Private) containers with a creator: {personal.Count}; distinct creators {personal.Select(s => s.Creator).Distinct().Count()}; peers known {peers.Count}");
            foreach (var s in personal.Take(MaxListed))
            {
                var zdo = ZDOMan.instance.GetZDO(s.Id);
                string ci = "absent", live = "gone";
                if (zdo != null)
                {
                    var k = GameAccess.ZdoKeys(s.Id);
                    var found = new List<string>();
                    found.AddRange(k.Ints.Where(x => x.Key == creatorIndexHash).Select(x => "int:" + x.Value));
                    found.AddRange(k.Longs.Where(x => x.Key == creatorIndexHash).Select(x => "long:" + x.Value));
                    found.AddRange(k.Strings.Where(x => x.Key == creatorIndexHash).Select(x => $"string(len {x.Value?.Length ?? 0})"));
                    found.AddRange(k.ByteArrays.Where(x => x.Key == creatorIndexHash).Select(x => $"bytes(len {x.Value?.Length ?? 0})"));
                    if (found.Count > 0) ci = string.Join(",", found);
                    live = LivePiece(zdo);
                }
                var placer = peers.FirstOrDefault(f => f.PlayerId == s.Creator);
                Log.Info($"chestdump[{r}.c4] {s.Prefab} @{Pos(s.Pos)} creator={s.Creator} creatorIndex={ci} placer={(placer != null ? $"peer {placer.Uid} (playerId matches; steam={placer.SteamId ?? "none"})" : "no known peer has this playerId")} livePiece={live} stacks={s.Stacks.Count}{(s.Unreadable ? " UNREADABLE" : "")}");
            }
            var first = personal.FirstOrDefault(s => ZDOMan.instance.GetZDO(s.Id) != null);
            if (first != null) Safe(r + ".c4", "key list", () => ListKeys(r + ".c4", ZDOMan.instance.GetZDO(first.Id), first.Prefab));
            foreach (var f in peers)
                Log.Info($"chestdump[{r}.c4] peer {f.Uid}: playerId={(f.PlayerId.HasValue ? f.PlayerId.Value.ToString(Inv) : "not spawned")} steam={f.SteamId ?? "none"} admin={f.IsAdmin} personalChestsCreated={personal.Count(s => s.Creator == f.PlayerId)} containersCreated={snapshots.Values.Count(s => s.Creator == f.PlayerId)}");
        }

        private static string LivePiece(ZDO zdo)
        {
            var nv = ZNetScene.instance.FindInstance(zdo);
            if (nv == null) return "not loaded";
            var piece = nv.GetComponent<Piece>();
            return piece == null ? "loaded, no Piece" : Fields(piece, "m_creator", "m_creatorPlatformUserIDIndex");
        }

        // ---------- c6: freshness ----------

        private static void Freshness(string r, Dictionary<string, Snapshot> now, string[] materials, long[] matTotals)
        {
            var changed = new List<(Snapshot before, Snapshot after)>();
            int added = 0, unchanged = 0;
            foreach (var kv in now)
            {
                if (!_previous.TryGetValue(kv.Key, out var before)) { added++; continue; }
                if (before.Stacks.Count == kv.Value.Stacks.Count && before.Stacks.All(e => kv.Value.Stacks.TryGetValue(e.Key, out int n) && n == e.Value)) unchanged++;
                else changed.Add((before, kv.Value));
            }
            int removed = _previous.Keys.Count(k => !now.ContainsKey(k));
            Log.Info($"chestdump[{r}.c6] since the previous run ({(DateTime.UtcNow - _previousAt).TotalMinutes.ToString("0.0", Inv)} min ago): changed={changed.Count} unchanged={unchanged} added={added} removed={removed} (vehicles that moved count as removed + added)");
            foreach (var (before, after) in changed.Take(MaxListed))
            {
                var keys = before.Stacks.Keys.Union(after.Stacks.Keys).OrderBy(k => k, StringComparer.Ordinal);
                var diff = keys.Select(k => (k, b: before.Stacks.TryGetValue(k, out int x) ? x : 0, a: after.Stacks.TryGetValue(k, out int y) ? y : 0)).Where(t => t.a != t.b).Select(t => $"{t.k} {t.b}→{t.a}");
                Log.Info($"chestdump[{r}.c6] changed {after.Prefab} @{Pos(after.Pos)}{(after.Private ? " (personal)" : "")}: {string.Join(", ", diff)}");
            }
            if (_previousMaterials != null && _previousMaterials.Length == matTotals.Length)
                Log.Info($"chestdump[{r}.c6] materials since the previous run: {string.Join(", ", materials.Select((m, i) => $"{m} {_previousMaterials[i]}→{matTotals[i]}"))}");
            int n = 0;
            foreach (var (_, after) in changed.Take(3))
            {
                var zdo = ZDOMan.instance.GetZDO(after.Id);
                if (zdo == null) continue;
                string tag = $"{r}.c6.p{++n}";
                Safe(tag, "key list", () => ListKeys(tag, zdo, after.Prefab));
            }
        }

        // ---------- helpers ----------

        private static string[] Materials() =>
            (PluginConfig.ChestsDumpMaterials.Value ?? "").Split(',').Select(s => s.Trim()).Where(s => s.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Take(5).ToArray();

        private static string SnapshotKey(Snapshot s) =>
            string.Format(Inv, "{0}|{1:0.0}|{2:0.0}|{3:0.0}|{4}", s.Prefab, s.Pos.x, s.Pos.y, s.Pos.z, s.Creator);

        private static string ItemName(ItemDrop.ItemData it) =>
            it.m_dropPrefab != null ? it.m_dropPrefab.name : it.m_shared != null ? "shared:" + it.m_shared.m_name : "?";

        private static int PeersConnected() => ZNet.instance != null ? ZNet.instance.GetPeers().Count : -1;

        private static string KeyName(int hash) => _names.TryGetValue(hash, out var n) ? $"{n} ({hash})" : $"? ({hash})";

        private static string Mark(int hash) => hash == _itemsHash ? " [= \"items\"]" : hash == _itemDataHash ? " [= \"itemData\"]" : "";

        private static string Pos(Vector3 p) => string.Format(Inv, "({0:0.0}, {1:0.0}, {2:0.0})", p.x, p.y, p.z);

        private static string Ms(double ms) => ms.ToString("0.000", Inv);

        private static byte[] Base64(string s)
        {
            if (string.IsNullOrEmpty(s) || s.Length % 4 != 0) return null;
            try { return Convert.FromBase64String(s); } catch (FormatException) { return null; }
        }

        private static string Head(int hash, byte[] data)
        {
            if (data == null) return "";
            int v = data.Length >= 4 ? BitConverter.ToInt32(data, 0) : int.MinValue;
            bool package = v >= 100 && v <= 200;
            if (!package && hash != _itemsHash && hash != _itemDataHash) return ", not package-like";
            return $", first16={Hex(data, 16)}{(package ? $" (int32 version {v}: {GameAccess.ItemVersionName(v) ?? "not a Version.Item value"})" : "")}";
        }

        private static string Hex(byte[] b, int max)
        {
            if (b == null) return "null";
            var sb = new StringBuilder();
            for (int i = 0; i < Math.Min(b.Length, max); i++) { if (i > 0 && i % 4 == 0) sb.Append(' '); sb.Append(b[i].ToString("x2", Inv)); }
            return sb.ToString();
        }

        private static string Sample(List<string> items) => items.Count == 0 ? "" : ": " + string.Join(", ", items.Take(15)) + (items.Count > 15 ? $" … {items.Count - 15} more" : "");

        /// <summary>"m_a=v m_b=v" read by name; "m_x=missing" when this build has no such member.</summary>
        private static string Fields(object obj, params string[] names) => string.Join(" ", names.Select(n => n + "=" + F(obj, n)));

        private static string F(object obj, string name) => GameAccess.TryRead(obj, name, out var v) ? Fmt(v) : "missing";

        private static string List(object obj, string field, Func<object, string> fmt, string sep = ", ")
        {
            if (!GameAccess.TryRead(obj, field, out var v)) return "missing";
            if (!(v is IEnumerable e)) return Fmt(v);
            return "[" + string.Join(sep, e.Cast<object>().Select(x => x == null ? "null" : fmt(x))) + "]";
        }

        private static string Fmt(object v)
        {
            switch (v)
            {
                case null: return "null";
                case UnityEngine.Object o: return o == null ? "null" : o.name;
                case string s: return "\"" + s + "\"";
                case IFormattable f: return f.ToString(null, Inv);
                case ICollection c: return $"[{c.Count}]";
                default: return v.ToString();
            }
        }

        private static string Short(Exception e)
        {
            var b = e is TargetInvocationException t && t.InnerException != null ? t.InnerException : e;
            string m = b.GetType().Name + ": " + b.Message;
            return m.Length <= 200 ? m : m.Substring(0, 200) + "…";
        }

        private static string SafeString(Func<string> f) { try { return f(); } catch { return "?"; } }

        private static bool Safe(string tag, string what, Action a)
        {
            try { a(); return true; }
            catch (Exception e)
            {
                string full = e.ToString();
                Log.Warn($"chestdump[{tag}] {what} failed: {(full.Length <= 1500 ? full : full.Substring(0, 1500) + "…")}");
                return false;
            }
        }
    }
}
