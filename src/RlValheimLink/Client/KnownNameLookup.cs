using System;
using System.Collections.Generic;
using RlValheimLink.Core;

namespace RlValheimLink.Client
{
    /// <summary>
    /// Name token → prefab ids for the character's known lists (<see cref="KnownNameMapper"/>), built from the
    /// live ObjectDB so modded items, recipes and pieces work. Built once and rebuilt only when ObjectDB is a
    /// new instance or its item/recipe counts changed (a mod registering late). Every prefab also maps to
    /// itself, so an entry that already is a prefab id (a mod adding one) is kept. Main thread only.
    /// </summary>
    internal static class KnownNameLookup
    {
        public sealed class Tables
        {
            /// <summary>m_knownRecipes: recipe result tokens and piece tokens.</summary>
            public readonly Dictionary<string, List<string>> Recipes = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            /// <summary>m_knownMaterial: item tokens.</summary>
            public readonly Dictionary<string, List<string>> Items = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        }

        private static Tables _tables;
        private static ObjectDB _db;
        private static int _items = -1, _recipes = -1;

        public static Tables Current()
        {
            var db = ObjectDB.instance;
            if (db == null) return new Tables();
            int items = db.m_items != null ? db.m_items.Count : 0, recipes = db.m_recipes != null ? db.m_recipes.Count : 0;
            if (_tables != null && ReferenceEquals(db, _db) && items == _items && recipes == _recipes) return _tables;
            _tables = Build(db); _db = db; _items = items; _recipes = recipes;
            return _tables;
        }

        private static Tables Build(ObjectDB db)
        {
            var t = new Tables();
            if (db.m_items != null)
                foreach (var go in db.m_items)
                {
                    var shared = go != null ? go.GetComponent<ItemDrop>()?.m_itemData?.m_shared : null;
                    if (shared == null) continue;
                    KnownNameMapper.Add(t.Items, shared.m_name, go.name);
                    KnownNameMapper.Add(t.Items, go.name, go.name);
                }
            if (db.m_recipes != null)
                foreach (var recipe in db.m_recipes)
                {
                    var item = recipe != null ? recipe.m_item : null;
                    var shared = item != null ? item.m_itemData?.m_shared : null;
                    if (shared == null) continue;
                    // Player.AddKnownRecipe stores the result item's m_shared.m_name; several recipes can share it.
                    KnownNameMapper.Add(t.Recipes, shared.m_name, item.gameObject.name);
                    KnownNameMapper.Add(t.Recipes, item.gameObject.name, item.gameObject.name);
                }
            // Player.AddKnownPiece stores Piece.m_name in the same set (hammer, hoe, cultivator, modded tools).
            foreach (var p in RecipeSources.BuildPieces())
            {
                KnownNameMapper.Add(t.Recipes, p.Piece.m_name, p.Prefab.name);
                KnownNameMapper.Add(t.Recipes, p.Prefab.name, p.Prefab.name);
            }
            return t;
        }
    }
}
