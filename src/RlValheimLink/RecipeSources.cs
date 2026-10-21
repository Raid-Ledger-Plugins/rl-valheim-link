using System;
using System.Collections.Generic;
using RlValheimLink.Core;
using UnityEngine;

namespace RlValheimLink
{
    /// <summary>
    /// Where the `recipes` export finds build pieces and crafting stations, and the prefab ids it gives
    /// them. Shared by the server's export (Server/RecipesExport) and the admin client's piece-icon atlas
    /// (Client/PieceIconsCapture), so an icon rendered on a client lands on the same id the server sends.
    /// </summary>
    internal static class RecipeSources
    {
        public sealed class BuildPiece
        {
            public string Tool;
            public GameObject Prefab;
            public Piece Piece;
        }

        /// <summary>Every build tool's pieces (ObjectDB items with m_buildPieces); the first tool that lists a piece owns it.</summary>
        public static List<BuildPiece> BuildPieces()
        {
            var list = new List<BuildPiece>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var go in ObjectDB.instance.m_items)
            {
                var drop = go != null ? go.GetComponent<ItemDrop>() : null;
                var table = drop != null && drop.m_itemData != null && drop.m_itemData.m_shared != null ? drop.m_itemData.m_shared.m_buildPieces : null;
                if (table == null || table.m_pieces == null) continue;
                foreach (var pgo in table.m_pieces)
                {
                    var piece = pgo != null ? pgo.GetComponent<Piece>() : null;
                    if (piece == null || !seen.Add(pgo.name)) continue;
                    list.Add(new BuildPiece { Tool = go.name, Prefab = pgo, Piece = piece });
                }
            }
            return list;
        }

        /// <summary>A piece's id: its prefab name, as the contract wants it.</summary>
        public static string PiecePrefab(GameObject pieceGo) => pieceGo == null ? null : Names.Prefab(pieceGo.name);

        /// <summary>A station's id is its prefab: the root of the object the CraftingStation sits on (UpgradeStation carries a second, child station).</summary>
        public static string StationPrefab(CraftingStation cs) => cs == null ? null : Names.Prefab(cs.transform.root.gameObject.name);

        /// <summary>The station that describes a prefab: its root's own CraftingStation, else the one referenced.</summary>
        public static CraftingStation RootStation(CraftingStation cs)
        {
            if (cs == null) return null;
            var root = cs.transform.root.GetComponent<CraftingStation>();
            return root != null ? root : cs;
        }

        /// <summary>Every crafting station the export can list: root stations in ZNetScene plus those recipes and pieces reference, by id.</summary>
        public static Dictionary<string, CraftingStation> Stations(IEnumerable<BuildPiece> pieces)
        {
            var stations = new Dictionary<string, CraftingStation>(StringComparer.Ordinal);
            void Add(CraftingStation cs)
            {
                string id = StationPrefab(cs);
                if (id != null && !stations.ContainsKey(id)) stations[id] = RootStation(cs);
            }
            foreach (var go in ZNetScene.instance.m_prefabs)
            {
                var cs = go != null ? go.GetComponent<CraftingStation>() : null;
                if (cs != null) Add(cs);
            }
            foreach (var recipe in ObjectDB.instance.m_recipes)
            {
                if (recipe == null) continue;
                if (recipe.m_craftingStation != null) Add(recipe.m_craftingStation);
                if (recipe.m_repairStation != null) Add(recipe.m_repairStation);
            }
            foreach (var p in pieces)
                if (p.Piece.m_craftingStation != null) Add(p.Piece.m_craftingStation);
            return stations;
        }

        /// <summary>The icon of every piece and station by id (pieces first; a station that is also a piece keeps the piece's sprite). Null sprites are left out.</summary>
        public static List<KeyValuePair<string, Sprite>> Icons()
        {
            var pieces = BuildPieces();
            var icons = new List<KeyValuePair<string, Sprite>>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in pieces)
            {
                string id = PiecePrefab(p.Prefab);
                if (id != null && p.Piece.m_icon != null && seen.Add(id)) icons.Add(new KeyValuePair<string, Sprite>(id, p.Piece.m_icon));
            }
            foreach (var kv in Stations(pieces))
                if (kv.Value != null && kv.Value.m_icon != null && seen.Add(kv.Key)) icons.Add(new KeyValuePair<string, Sprite>(kv.Key, kv.Value.m_icon));
            icons.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));
            return icons;
        }
    }
}
