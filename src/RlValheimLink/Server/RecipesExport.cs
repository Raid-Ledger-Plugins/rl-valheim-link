using System;
using System.Collections.Generic;
using System.Diagnostics;
using RlValheimLink.Client;
using RlValheimLink.Contract;
using RlValheimLink.Core;
using UnityEngine;

namespace RlValheimLink.Server
{
    /// <summary>
    /// The `recipes` export (docs/design-chests.md §2), read on the dedicated server from ObjectDB.m_recipes,
    /// the build tools' piece tables and the stations' prefabs in ZNetScene. Game glue only: ids, references,
    /// sorting and caps are <see cref="RecipesBuilder"/>'s. A dedicated server has no GPU path for sprites
    /// (docs/design.md §2), so the export itself carries no icons; ServerHalf applies the piece-icon atlas an
    /// admin client rendered (<see cref="Core.PieceIconMap"/>), when it has one. Item names and icons come
    /// from the `items` catalogue. One phase per tick.
    /// </summary>
    internal sealed class RecipesExport
    {
        public sealed class Result
        {
            public RecipesMessage Message;
            public int Ticks;
            public double Ms, BusiestTickMs;
        }

        private IEnumerator<object> _run;
        private Result _result;
        private bool _finished;

        public bool Running => _run != null;

        public void Begin(DateTime capturedAt, string gameVersion)
        {
            _result = new Result();
            _finished = false;
            _run = Run(_result, capturedAt, gameVersion).GetEnumerator();
        }

        /// <summary>One phase of the running export; returns the raw message once, when finished.</summary>
        public Result Tick()
        {
            if (_run == null) return null;
            bool more;
            try { more = _run.MoveNext(); }
            catch { _run = null; _result = null; throw; }
            if (more) return null;
            _run = null;
            var r = _result; _result = null;
            return _finished ? r : null;
        }

        private IEnumerable<object> Run(Result r, DateTime capturedAt, string gameVersion)
        {
            var total = Stopwatch.StartNew();
            var tick = Stopwatch.StartNew();
            var msg = new RecipesMessage
            {
                CapturedAt = MessageJson.Timestamp(capturedAt),
                GameVersion = gameVersion,
                ModVersion = Plugin.Version,
                Locale = Locale(),
                Atlas = null,
            };
            var stations = new Dictionary<string, CraftingStationDef>(StringComparer.Ordinal);

            // Phase 1: crafting stations and their extensions, from every prefab in ZNetScene.
            var extensions = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            var stackable = new HashSet<string>(StringComparer.Ordinal);
            foreach (var go in ZNetScene.instance.m_prefabs)
            {
                if (go == null) continue;
                var cs = go.GetComponent<CraftingStation>();
                if (cs != null) AddStation(stations, cs);
                foreach (var ext in go.GetComponentsInChildren<StationExtension>(true))
                {
                    if (ext == null || ext.m_craftingStation == null) continue;
                    string target = RecipeSources.StationPrefab(ext.m_craftingStation);
                    if (target == null) continue;
                    if (!extensions.TryGetValue(target, out var set)) extensions[target] = set = new HashSet<string>(StringComparer.Ordinal);
                    set.Add(go.name);
                    if (ext.m_stack) stackable.Add(target);
                }
            }
            r.BusiestTickMs = Math.Max(r.BusiestTickMs, tick.Elapsed.TotalMilliseconds); r.Ticks++;
            yield return null; tick.Restart();

            // Phase 2: recipes.
            foreach (var recipe in ObjectDB.instance.m_recipes)
            {
                if (recipe == null || recipe.m_item == null) continue;
                var def = new RecipeDef
                {
                    Id = recipe.name,
                    Result = recipe.m_item.gameObject.name,
                    Amount = recipe.m_amount,
                    Enabled = recipe.m_enabled,
                    Station = recipe.m_craftingStation != null ? AddStation(stations, recipe.m_craftingStation) : null,
                    RepairStation = recipe.m_repairStation != null ? AddStation(stations, recipe.m_repairStation) : null,
                    MinStationLevel = recipe.m_minStationLevel,
                    UpgradeOnly = ReadBool(recipe, "m_noCraftOnlyUpgrade") ?? false,
                    AnyOneOf = recipe.m_requireOnlyOneIngredient,
                    QualityResultAmountMultiplier = recipe.m_qualityResultAmountMultiplier,
                    Formula = MatchesFormula(recipe) ? RecipeFormula.Vanilla : RecipeFormula.Custom,
                };
                if (recipe.m_resources != null)
                    foreach (var q in recipe.m_resources)
                    {
                        if (q == null || q.m_resItem == null) continue;
                        def.Resources.Add(new RecipeResource
                        {
                            Prefab = q.m_resItem.gameObject.name, Amount = q.m_amount, AmountPerLevel = q.m_amountPerLevel,
                            ExtraAmountOnlyOneIngredient = q.m_extraAmountOnlyOneIngredient, Recover = q.m_recover,
                            UpgraderResource = ReadBool(q, "m_upgraderResource"),
                        });
                    }
                msg.Recipes.Add(def);
            }
            r.BusiestTickMs = Math.Max(r.BusiestTickMs, tick.Elapsed.TotalMilliseconds); r.Ticks++;
            yield return null; tick.Restart();

            // Phase 3: build pieces, from every build tool's piece table (the first tool that lists a piece owns it).
            foreach (var bp in RecipeSources.BuildPieces())
            {
                var piece = bp.Piece;
                var def = new PieceDef
                {
                    Prefab = bp.Prefab.name, Name = Localize(piece.m_name), Category = piece.m_category.ToString(), Tool = bp.Tool,
                    Station = piece.m_craftingStation != null ? AddStation(stations, piece.m_craftingStation) : null, Icon = null,
                };
                if (piece.m_resources != null)
                    foreach (var q in piece.m_resources)
                        if (q != null && q.m_resItem != null)
                            def.Resources.Add(new PieceResource { Prefab = q.m_resItem.gameObject.name, Amount = q.m_amount, Recover = q.m_recover });
                msg.Pieces.Add(def);
            }
            r.BusiestTickMs = Math.Max(r.BusiestTickMs, tick.Elapsed.TotalMilliseconds); r.Ticks++;
            yield return null; tick.Restart();

            // Phase 4: processing stations (smelter, kiln, windmill, spinning wheel, refinery, fermenter, cooking, obliterator).
            var processorSeen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var go in ZNetScene.instance.m_prefabs)
            {
                if (go == null) continue;
                foreach (var c in go.GetComponentsInChildren<Smelter>(true)) Smelter(msg, processorSeen, go.name, c);
                foreach (var c in go.GetComponentsInChildren<Fermenter>(true)) Fermenter(msg, processorSeen, go.name, c);
                foreach (var c in go.GetComponentsInChildren<CookingStation>(true)) Cooking(msg, processorSeen, go.name, c);
                foreach (var c in go.GetComponentsInChildren<Incinerator>(true)) Incinerator(msg, processorSeen, go.name, c);
            }

            foreach (var kv in stations)
            {
                int ext = extensions.TryGetValue(kv.Key, out var set) ? set.Count : 0;
                kv.Value.MaxLevel = Math.Min(Caps.StationLevelMax, 1 + ext);
                kv.Value.StackableExtension = stackable.Contains(kv.Key);
            }
            msg.Stations.AddRange(stations.Values);
            r.BusiestTickMs = Math.Max(r.BusiestTickMs, tick.Elapsed.TotalMilliseconds); r.Ticks++;
            r.Ms = total.Elapsed.TotalMilliseconds;
            r.Message = msg;
            _finished = true;
        }

        // ---------- stations ----------

        private static string AddStation(Dictionary<string, CraftingStationDef> stations, CraftingStation cs)
        {
            string prefab = RecipeSources.StationPrefab(cs);
            if (prefab == null) return null;
            if (stations.ContainsKey(prefab)) return prefab;
            // Recipes can reference a station on a child object; its prefab root's own station describes it best.
            var rootStation = RecipeSources.RootStation(cs);
            stations[prefab] = new CraftingStationDef { Prefab = prefab, Name = Localize(rootStation.m_name), Upgrader = ReadBool(rootStation, "m_upgrader"), Icon = null };
            return prefab;
        }

        // ---------- processors ----------

        private static void Smelter(RecipesMessage msg, HashSet<string> seen, string prefab, Smelter c)
        {
            bool fuel = c.m_maxFuel > 0 && c.m_fuelItem != null;
            var processes = new List<ProcessDef>();
            if (c.m_conversion != null)
                foreach (var x in c.m_conversion)
                    // Siege engines (BatteringRam) and stations without an input (piece_FrostKiln, piece_bathtub) have no conversion to plan.
                    if (x != null && x.m_from != null && x.m_to != null)
                        processes.Add(Process(prefab, x.m_from, 1, x.m_to, 1, c.m_secPerProduct));
            AddProcessor(msg, seen, processes, new ProcessorDef
            {
                Prefab = prefab, Type = ProcessorType.Smelter,
                Fuel = fuel ? c.m_fuelItem.gameObject.name : null, FuelPerProduct = fuel ? c.m_fuelPerProduct : (int?)null,
                WindPowered = c.m_windmill != null, RequiresRoof = c.m_requiresRoof, MaxInput = c.m_maxOre > 0 ? c.m_maxOre : (int?)null,
            });
        }

        private static void Fermenter(RecipesMessage msg, HashSet<string> seen, string prefab, Fermenter c)
        {
            var processes = new List<ProcessDef>();
            if (c.m_conversion != null)
                foreach (var x in c.m_conversion)
                    if (x != null && x.m_from != null && x.m_to != null)
                        processes.Add(Process(prefab, x.m_from, 1, x.m_to, x.m_producedItems, c.m_fermentationDuration));
            // One batch at a time, covered and under a roof (docs/design-chests.md §2.2).
            AddProcessor(msg, seen, processes, new ProcessorDef { Prefab = prefab, Type = ProcessorType.Fermenter, RequiresRoof = true, MaxInput = 1 });
        }

        private static void Cooking(RecipesMessage msg, HashSet<string> seen, string prefab, CookingStation c)
        {
            bool fuel = c.m_useFuel && c.m_fuelItem != null;
            var processes = new List<ProcessDef>();
            if (c.m_conversion != null)
                foreach (var x in c.m_conversion)
                    if (x != null && x.m_from != null && x.m_to != null)
                        processes.Add(Process(prefab, x.m_from, 1, x.m_to, 1, x.m_cookTime));
            bool overcooks = ReadBool(c, "m_canOvercookItems") ?? true; // 1.0.20: false on piece_FrostFoundry
            AddProcessor(msg, seen, processes, new ProcessorDef
            {
                Prefab = prefab, Type = ProcessorType.Cooking,
                Fuel = fuel ? c.m_fuelItem.gameObject.name : null, FuelSecondsPerUnit = fuel ? c.m_secPerFuel : (double?)null,
                MaxInput = c.m_slots != null && c.m_slots.Length > 0 ? c.m_slots.Length : (int?)null,
                BurntResult = overcooks && c.m_overCookedItem != null ? c.m_overCookedItem.gameObject.name : null,
            });
        }

        private static void Incinerator(RecipesMessage msg, HashSet<string> seen, string prefab, Incinerator c)
        {
            var processes = new List<ProcessDef>();
            if (c.m_conversions != null)
                foreach (var x in c.m_conversions)
                {
                    if (x == null || x.m_result == null || x.m_requirements == null) continue;
                    var p = new ProcessDef
                    {
                        Processor = prefab, AnyOneOf = x.m_requireOnlyOneIngredient,
                        Output = new PrefabAmount { Prefab = x.m_result.gameObject.name, Amount = x.m_resultAmount }, Seconds = null, Priority = x.m_priority,
                    };
                    foreach (var q in x.m_requirements)
                        if (q != null && q.m_resItem != null) p.Inputs.Add(new PrefabAmount { Prefab = q.m_resItem.gameObject.name, Amount = q.m_amount });
                    processes.Add(p);
                }
            AddProcessor(msg, seen, processes, new ProcessorDef
            {
                Prefab = prefab, Type = ProcessorType.Incinerator,
                DefaultResult = c.m_defaultResult != null ? new ProcessorDefaultResult { Prefab = c.m_defaultResult.gameObject.name, PerInputs = c.m_defaultCost } : null,
            });
        }

        private static ProcessDef Process(string processor, ItemDrop from, int fromAmount, ItemDrop to, int toAmount, double seconds) => new ProcessDef
        {
            Processor = processor,
            Inputs = { new PrefabAmount { Prefab = from.gameObject.name, Amount = fromAmount } },
            Output = new PrefabAmount { Prefab = to.gameObject.name, Amount = toAmount },
            Seconds = seconds, Priority = 0,
        };

        /// <summary>A processor goes in only with at least one usable conversion; the first component of a prefab wins.</summary>
        private static void AddProcessor(RecipesMessage msg, HashSet<string> seen, List<ProcessDef> processes, ProcessorDef def)
        {
            if (processes.Count == 0 || !seen.Add(def.Prefab)) return;
            msg.Processors.Add(def);
            msg.Processes.AddRange(processes);
        }

        // ---------- formula check (CONTRACT.md §5b) ----------

        /// <summary>
        /// True when the live per-quality station level, station and requirement amounts equal the documented
        /// formula for every quality the result item has; false ("custom") when a mod patched them or a call throws.
        /// Recipe.GetAmount is never called: it reads Player.m_localPlayer.
        /// </summary>
        private static bool MatchesFormula(Recipe recipe)
        {
            try
            {
                var shared = recipe.m_item.m_itemData != null ? recipe.m_item.m_itemData.m_shared : null;
                int maxQ = Math.Max(1, Math.Min(10, shared != null ? shared.m_maxQuality : 1));
                for (int q = 1; q <= maxQ; q++)
                {
                    if (recipe.GetRequiredStationLevel(q) != Math.Max(1, recipe.m_minStationLevel) + (q - 1)) return false;
                    var expected = recipe.m_craftingStation != null ? recipe.m_craftingStation : q > 1 ? recipe.m_repairStation : null;
                    if (recipe.GetRequiredStation(q) != expected) return false;
                    if (recipe.m_resources == null) continue;
                    foreach (var res in recipe.m_resources)
                        if (res != null && res.GetAmount(q) != (q <= 1 ? res.m_amount : (q - 1) * res.m_amountPerLevel)) return false;
                }
                return true;
            }
            catch (Exception) { return false; }
        }

        // ---------- helpers ----------

        /// <summary>A bool field that only some game versions have (1.0.20's upgrade fields): null when this build has none.</summary>
        private static bool? ReadBool(object obj, string field) => GameAccess.TryRead(obj, field, out var v) && v is bool b ? b : (bool?)null;

        private static string Locale()
        {
            try { return GameInfo.Identifier(Localization.instance?.GetSelectedLanguage(), 32) ?? "English"; } catch { return "English"; }
        }

        /// <summary>Localised display name; RecipesBuilder falls back to the prefab when the token is untranslated.</summary>
        private static string Localize(string token)
        {
            if (string.IsNullOrEmpty(token)) return null;
            try { return GameInfo.StripRichText(Localization.instance?.Localize(token)) ?? token; } catch { return token; }
        }
    }
}
