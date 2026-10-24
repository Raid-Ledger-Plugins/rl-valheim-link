using System;
using System.Collections.Generic;

namespace RlValheimLink.Core
{
    /// <summary>What one mapping did, for the capture's log line.</summary>
    public sealed class KnownNameResult
    {
        /// <summary>Prefab ids, ordinal-sorted, unique, capped.</summary>
        public List<string> Prefabs = new List<string>();
        /// <summary>Entries with no prefab: dropped, never sent.</summary>
        public int Unmatched;
        /// <summary>Up to <see cref="KnownNameMapper.MaxExamples"/> unmatched entries, for the WARN.</summary>
        public List<string> UnmatchedExamples = new List<string>();
        /// <summary>Prefab ids cut by the cap.</summary>
        public int OverCap;
    }

    /// <summary>
    /// The player's known lists hold the game's NAME TOKENS, not prefab ids (0.221.4 IL; 1.0.20 keeps the
    /// signatures): `Player.AddKnownRecipe` stores the result item's `m_shared.m_name` ("$item_sword_bronze"),
    /// `AddKnownPiece` stores `Piece.m_name` ("$piece_workbench"), both in m_knownRecipes, and `AddKnownItem`
    /// stores `m_shared.m_name` in m_knownMaterial. The contract wants prefab ids (CONTRACT.md §3), so the
    /// capture maps every entry through a lookup built from the live ObjectDB (modded items included).
    /// One token can name several prefabs (variants sharing a name), so every match is sent. An entry with no
    /// match is dropped. Then ordinal sort, dedupe and the cap, as before. Pure; unit-tested.
    /// </summary>
    public static class KnownNameMapper
    {
        public const int MaxExamples = 5;

        public static KnownNameResult Map(IEnumerable<string> raw, IReadOnlyDictionary<string, List<string>> lookup, int cap)
        {
            var r = new KnownNameResult();
            var set = new SortedSet<string>(StringComparer.Ordinal);
            if (raw != null)
                foreach (var entry in raw)
                {
                    if (string.IsNullOrEmpty(entry)) continue;
                    bool any = false;
                    if (lookup != null && lookup.TryGetValue(entry, out var prefabs) && prefabs != null)
                        foreach (var p in prefabs)
                        {
                            string id = Names.Prefab(p);
                            if (id != null && Names.IsPrefab(id)) { set.Add(id); any = true; }
                        }
                    if (any) continue;
                    r.Unmatched++;
                    if (r.UnmatchedExamples.Count < MaxExamples) r.UnmatchedExamples.Add(entry);
                }
            r.Prefabs = new List<string>(set);
            if (r.Prefabs.Count > cap)
            {
                r.OverCap = r.Prefabs.Count - cap;
                r.Prefabs.RemoveRange(cap, r.OverCap);
            }
            return r;
        }

        /// <summary>Adds <paramref name="key"/> → <paramref name="prefab"/> to a lookup (one key, several prefabs, each once).</summary>
        public static void Add(Dictionary<string, List<string>> lookup, string key, string prefab)
        {
            if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(prefab)) return;
            if (!lookup.TryGetValue(key, out var list)) lookup[key] = list = new List<string>(1);
            if (!list.Contains(prefab)) list.Add(prefab);
        }
    }
}
