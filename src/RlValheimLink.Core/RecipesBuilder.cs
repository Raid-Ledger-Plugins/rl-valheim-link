using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using RlValheimLink.Contract;

namespace RlValheimLink.Core
{
    /// <summary>What a normalisation did, for the export's log line and its WARNs.</summary>
    public sealed class RecipesReport
    {
        public int Stations, Recipes, Pieces, Processors, Processes, Entries, CustomFormula;
        public int RenamedIds, MergedResources, DanglingStations, SplitProcesses;
        public int DroppedRecipes, DroppedPieces, DroppedProcessors, DroppedProcesses, DroppedStations, DroppedResources, DroppedInputs;
        public bool Dropped => DroppedRecipes + DroppedPieces + DroppedProcessors + DroppedProcesses + DroppedStations + DroppedResources + DroppedInputs > 0;
    }

    /// <summary>
    /// Normalises a raw `recipes` message read from the game (Server/RecipesExport) into one the contract
    /// accepts (CONTRACT.md §5b, docs/design-chests.md §2): ids, prefab and station references cleaned,
    /// duplicate requirements of one recipe summed, an obliterator conversion with more than 8 alternative
    /// inputs split into several "any one of" processes, everything sorted and the array and entry caps
    /// applied by dropping (never by sending an invalid message). Pure; unit-tested.
    /// </summary>
    public static class RecipesBuilder
    {
        public static RecipesMessage Normalise(RecipesMessage m, out RecipesReport report)
        {
            report = new RecipesReport();
            var r = report;
            m.Locale = Names.Identifier(m.Locale, 32) is string loc && loc.Length >= 2 && loc.IndexOfAny("0123456789".ToCharArray()) < 0 ? loc : "English";

            // ---- stations ----
            var stations = new List<CraftingStationDef>();
            var stationSet = new HashSet<string>(StringComparer.Ordinal);
            foreach (var s in m.Stations ?? new List<CraftingStationDef>())
            {
                string p = s == null ? null : Names.Prefab(s.Prefab);
                if (p == null || !stationSet.Add(p)) { r.DroppedStations++; continue; }
                s.Prefab = p;
                s.Name = Name(s.Name, p);
                s.MaxLevel = Names.Clamp(s.MaxLevel, 1, Caps.StationLevelMax);
                stations.Add(s);
            }
            stations.Sort((a, b) => string.CompareOrdinal(a.Prefab, b.Prefab));
            Cap(stations, Caps.StationsMax, ref r.DroppedStations);
            stationSet = new HashSet<string>(stations.Select(s => s.Prefab), StringComparer.Ordinal);

            // ---- recipes ----
            var recipes = new List<RecipeDef>();
            var ids = new HashSet<string>(StringComparer.Ordinal);
            var pending = new List<RecipeDef>();
            foreach (var x in m.Recipes ?? new List<RecipeDef>())
            {
                if (x == null) continue;
                x.Result = Names.Prefab(x.Result);
                if (x.Result == null) { r.DroppedRecipes++; continue; }
                // Valid, unused asset names keep their id; the rest get <result>.<n> after this pass.
                if (Names.IsPrefab(x.Id) && ids.Add(x.Id)) recipes.Add(x);
                else pending.Add(x);
            }
            foreach (var x in pending)
            {
                string stem = x.Result;
                string id;
                int n = 1;
                do
                {
                    string suffix = "." + n.ToString(CultureInfo.InvariantCulture);
                    id = (stem.Length + suffix.Length > 96 ? stem.Substring(0, 96 - suffix.Length) : stem) + suffix;
                    n++;
                } while (!ids.Add(id));
                x.Id = id;
                r.RenamedIds++;
                recipes.Add(x);
            }
            foreach (var x in recipes)
            {
                x.Amount = Names.Clamp(x.Amount, 1, 10_000);
                x.Station = StationRef(x.Station, stationSet, r);
                x.RepairStation = StationRef(x.RepairStation, stationSet, r);
                x.MinStationLevel = Names.Clamp(x.MinStationLevel, 0, Caps.StationLevelMax);
                x.QualityResultAmountMultiplier = Names.Clamp(x.QualityResultAmountMultiplier, 0, 100);
                if (x.Formula != RecipeFormula.Custom) x.Formula = RecipeFormula.Vanilla;
                x.Resources = RecipeResources(x.Resources, r);
            }
            recipes.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
            Cap(recipes, Caps.RecipesMax, ref r.DroppedRecipes);

            // ---- pieces ----
            var pieces = new List<PieceDef>();
            var pieceSet = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in m.Pieces ?? new List<PieceDef>())
            {
                string prefab = p == null ? null : Names.Prefab(p.Prefab);
                string tool = p == null ? null : Names.Prefab(p.Tool);
                if (prefab == null || tool == null || !pieceSet.Add(prefab)) { r.DroppedPieces++; continue; }
                p.Prefab = prefab; p.Tool = tool;
                p.Name = Name(p.Name, prefab);
                p.Category = Names.Identifier(p.Category, 48) ?? "Misc";
                p.Station = StationRef(p.Station, stationSet, r);
                p.Resources = PieceResources(p.Resources, r);
                pieces.Add(p);
            }
            pieces.Sort((a, b) => string.CompareOrdinal(a.Prefab, b.Prefab));
            Cap(pieces, Caps.PiecesMax, ref r.DroppedPieces);

            // ---- processes, then the processors that have any ----
            var processorByPrefab = new Dictionary<string, ProcessorDef>(StringComparer.Ordinal);
            foreach (var p in m.Processors ?? new List<ProcessorDef>())
            {
                string prefab = p == null ? null : Names.Prefab(p.Prefab);
                if (prefab == null || processorByPrefab.ContainsKey(prefab) || !IsProcessorType(p.Type)) { r.DroppedProcessors++; continue; }
                p.Prefab = prefab;
                p.Fuel = Names.Prefab(p.Fuel);
                p.FuelPerProduct = p.FuelPerProduct.HasValue ? Names.Clamp(p.FuelPerProduct.Value, 0, 1_000) : (int?)null;
                p.FuelSecondsPerUnit = p.FuelSecondsPerUnit.HasValue && !double.IsNaN(p.FuelSecondsPerUnit.Value) ? Names.Clamp(p.FuelSecondsPerUnit.Value, 0.001, 86_400) : (double?)null;
                p.MaxInput = p.MaxInput.HasValue && p.MaxInput.Value >= 1 ? Math.Min(p.MaxInput.Value, 10_000) : (int?)null;
                p.BurntResult = Names.Prefab(p.BurntResult);
                if (p.DefaultResult != null)
                {
                    p.DefaultResult.Prefab = Names.Prefab(p.DefaultResult.Prefab);
                    p.DefaultResult = p.DefaultResult.Prefab == null ? null : p.DefaultResult;
                    if (p.DefaultResult != null) p.DefaultResult.PerInputs = Names.Clamp(p.DefaultResult.PerInputs, 1, 10_000);
                }
                processorByPrefab[prefab] = p;
            }

            var processes = new List<ProcessDef>();
            var processKeys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var x in m.Processes ?? new List<ProcessDef>())
            {
                string processor = x == null ? null : Names.Prefab(x.Processor);
                string output = x?.Output == null ? null : Names.Prefab(x.Output.Prefab);
                if (processor == null || output == null || !processorByPrefab.ContainsKey(processor)) { r.DroppedProcesses++; continue; }
                var inputs = Inputs(x.Inputs);
                if (inputs.Count == 0) { r.DroppedProcesses++; continue; }
                x.Processor = processor;
                x.Output = new PrefabAmount { Prefab = output, Amount = Names.Clamp(x.Output.Amount, 1, 10_000) };
                x.Seconds = x.Seconds.HasValue && !double.IsNaN(x.Seconds.Value) ? Names.Clamp(x.Seconds.Value, 0, 604_800) : (double?)null;
                x.Priority = Names.Clamp(x.Priority, -10_000, 10_000);
                var parts = new List<List<PrefabAmount>>();
                if (inputs.Count <= Caps.ProcessInputsMax) parts.Add(inputs);
                else if (x.AnyOneOf)
                {
                    // "Any one of" N inputs is the same as N/8 processes of "any one of" 8 each.
                    for (int i = 0; i < inputs.Count; i += Caps.ProcessInputsMax)
                        parts.Add(inputs.GetRange(i, Math.Min(Caps.ProcessInputsMax, inputs.Count - i)));
                    r.SplitProcesses++;
                }
                else { r.DroppedProcesses++; continue; } // a full set of more than 8 inputs has no v1 shape
                foreach (var part in parts)
                {
                    var p = new ProcessDef { Processor = processor, Inputs = part, AnyOneOf = x.AnyOneOf, Output = x.Output, Seconds = x.Seconds, Priority = x.Priority };
                    if (processKeys.Add(ProcessKey(p))) processes.Add(p);
                }
            }
            processes.Sort(CompareProcess);
            Cap(processes, Caps.ProcessesMax, ref r.DroppedProcesses);
            var used = new HashSet<string>(processes.Select(p => p.Processor), StringComparer.Ordinal);
            var processors = processorByPrefab.Values.Where(p => used.Contains(p.Prefab)).ToList();
            r.DroppedProcessors += processorByPrefab.Count - processors.Count;
            processors.Sort((a, b) => string.CompareOrdinal(a.Prefab, b.Prefab));
            if (processors.Count > Caps.ProcessorsMax)
            {
                Cap(processors, Caps.ProcessorsMax, ref r.DroppedProcessors);
                var kept = new HashSet<string>(processors.Select(p => p.Prefab), StringComparer.Ordinal);
                r.DroppedProcesses += processes.RemoveAll(p => !kept.Contains(p.Processor));
            }

            // ---- Σ resources + inputs ≤ VALHEIM_LINK_RECIPE_ENTRIES_MAX: pieces go first, then recipes, then processes ----
            int entries = recipes.Sum(x => x.Resources.Count) + pieces.Sum(x => x.Resources.Count) + processes.Sum(x => x.Inputs.Count);
            while (entries > Caps.RecipeEntriesMax)
            {
                if (pieces.Count > 0) { entries -= pieces[pieces.Count - 1].Resources.Count; pieces.RemoveAt(pieces.Count - 1); r.DroppedPieces++; }
                else if (recipes.Count > 0) { entries -= recipes[recipes.Count - 1].Resources.Count; recipes.RemoveAt(recipes.Count - 1); r.DroppedRecipes++; }
                else { entries -= processes[processes.Count - 1].Inputs.Count; processes.RemoveAt(processes.Count - 1); r.DroppedProcesses++; }
            }

            m.Stations = stations; m.Recipes = recipes; m.Pieces = pieces; m.Processors = processors; m.Processes = processes;
            if (m.Atlas == null)
            {
                // Icons need the atlas (CONTRACT.md §6): without one every icon is null.
                foreach (var s in stations) s.Icon = null;
                foreach (var p in pieces) p.Icon = null;
            }
            r.Stations = stations.Count; r.Recipes = recipes.Count; r.Pieces = pieces.Count; r.Processors = processors.Count; r.Processes = processes.Count;
            r.Entries = entries;
            r.CustomFormula = recipes.Count(x => x.Formula == RecipeFormula.Custom);
            return m;
        }

        /// <summary>The spool key of a game version's recipes snapshot (latest wins), like `items`.</summary>
        public static string SpoolKey(string gameVersion) => "recipes|" + gameVersion;

        private static string Name(string raw, string prefab)
        {
            string n = (raw ?? "").Trim();
            if (n.Length == 0 || n.StartsWith("$", StringComparison.Ordinal) || n.StartsWith("[", StringComparison.Ordinal)) n = prefab;
            return Names.Clip(n, 96);
        }

        private static string StationRef(string raw, HashSet<string> stations, RecipesReport r)
        {
            string p = Names.Prefab(raw);
            if (p == null) return null;
            if (stations.Contains(p)) return p;
            r.DanglingStations++;
            return null;
        }

        private static List<RecipeResource> RecipeResources(List<RecipeResource> raw, RecipesReport r)
        {
            var list = new List<RecipeResource>();
            var byPrefab = new Dictionary<string, RecipeResource>(StringComparer.Ordinal);
            foreach (var x in raw ?? new List<RecipeResource>())
            {
                string p = x == null ? null : Names.Prefab(x.Prefab);
                if (p == null) { r.DroppedResources++; continue; }
                if (byPrefab.TryGetValue(p, out var have))
                {
                    // One item listed twice in a recipe: the game takes both, so the amounts add up.
                    have.Amount = Names.Clamp((long)have.Amount + x.Amount, 0, 10_000);
                    have.AmountPerLevel = Names.Clamp((long)have.AmountPerLevel + x.AmountPerLevel, 0, 10_000);
                    have.ExtraAmountOnlyOneIngredient = Names.Clamp((long)have.ExtraAmountOnlyOneIngredient + x.ExtraAmountOnlyOneIngredient, 0, 10_000);
                    have.Recover |= x.Recover;
                    if (x.UpgraderResource.HasValue) have.UpgraderResource = (have.UpgraderResource ?? false) | x.UpgraderResource.Value;
                    r.MergedResources++;
                    continue;
                }
                x.Prefab = p;
                x.Amount = Names.Clamp(x.Amount, 0, 10_000);
                x.AmountPerLevel = Names.Clamp(x.AmountPerLevel, 0, 10_000);
                x.ExtraAmountOnlyOneIngredient = Names.Clamp(x.ExtraAmountOnlyOneIngredient, 0, 10_000);
                byPrefab[p] = x; list.Add(x);
            }
            if (list.Count > Caps.RequirementsMax) { r.DroppedResources += list.Count - Caps.RequirementsMax; list.RemoveRange(Caps.RequirementsMax, list.Count - Caps.RequirementsMax); }
            return list;
        }

        private static List<PieceResource> PieceResources(List<PieceResource> raw, RecipesReport r)
        {
            var list = new List<PieceResource>();
            var byPrefab = new Dictionary<string, PieceResource>(StringComparer.Ordinal);
            foreach (var x in raw ?? new List<PieceResource>())
            {
                string p = x == null ? null : Names.Prefab(x.Prefab);
                if (p == null) { r.DroppedResources++; continue; }
                if (byPrefab.TryGetValue(p, out var have))
                {
                    have.Amount = Names.Clamp((long)have.Amount + x.Amount, 0, 10_000);
                    have.Recover |= x.Recover;
                    r.MergedResources++;
                    continue;
                }
                x.Prefab = p;
                x.Amount = Names.Clamp(x.Amount, 0, 10_000);
                byPrefab[p] = x; list.Add(x);
            }
            if (list.Count > Caps.RequirementsMax) { r.DroppedResources += list.Count - Caps.RequirementsMax; list.RemoveRange(Caps.RequirementsMax, list.Count - Caps.RequirementsMax); }
            return list;
        }

        private static List<PrefabAmount> Inputs(List<PrefabAmount> raw)
        {
            var list = new List<PrefabAmount>();
            var byPrefab = new Dictionary<string, PrefabAmount>(StringComparer.Ordinal);
            foreach (var x in raw ?? new List<PrefabAmount>())
            {
                string p = x == null ? null : Names.Prefab(x.Prefab);
                if (p == null) continue;
                if (byPrefab.TryGetValue(p, out var have)) { have.Amount = Names.Clamp((long)have.Amount + x.Amount, 1, 10_000); continue; }
                var a = new PrefabAmount { Prefab = p, Amount = Names.Clamp(x.Amount, 1, 10_000) };
                byPrefab[p] = a; list.Add(a);
            }
            return list;
        }

        private static bool IsProcessorType(string t) =>
            t == ProcessorType.Smelter || t == ProcessorType.Fermenter || t == ProcessorType.Cooking || t == ProcessorType.Incinerator;

        private static string ProcessKey(ProcessDef p) =>
            p.Processor + "|" + string.Join("+", p.Inputs.Select(i => i.Prefab + "x" + i.Amount.ToString(CultureInfo.InvariantCulture))) + "|" + p.AnyOneOf + "|" +
            p.Output.Prefab + "x" + p.Output.Amount.ToString(CultureInfo.InvariantCulture) + "|" + p.Priority.ToString(CultureInfo.InvariantCulture);

        private static int CompareProcess(ProcessDef a, ProcessDef b)
        {
            int c = string.CompareOrdinal(a.Processor, b.Processor);
            if (c == 0) c = b.Priority.CompareTo(a.Priority);
            if (c == 0) c = string.CompareOrdinal(a.Inputs[0].Prefab, b.Inputs[0].Prefab);
            if (c == 0) c = string.CompareOrdinal(a.Output.Prefab, b.Output.Prefab);
            if (c == 0) c = a.Inputs.Count.CompareTo(b.Inputs.Count);
            return c;
        }

        private static void Cap<T>(List<T> list, int max, ref int dropped)
        {
            if (list.Count <= max) return;
            dropped += list.Count - max;
            list.RemoveRange(max, list.Count - max);
        }
    }
}
