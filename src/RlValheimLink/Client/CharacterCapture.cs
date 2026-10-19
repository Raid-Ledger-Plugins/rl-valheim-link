using System;
using System.Collections.Generic;
using RlValheimLink.Contract;
using ContractInventory = RlValheimLink.Contract.Inventory;

namespace RlValheimLink.Client
{
    /// <summary>Builds a `character` message from the local player (docs/design.md §2). Main thread only.</summary>
    internal static class CharacterCapture
    {
        public static CharacterMessage Capture(Player p)
        {
            var m = new CharacterMessage
            {
                ServerId = ClientHalf.PendingServerId,
                CapturedAt = MessageJson.Timestamp(DateTime.UtcNow),
                GameVersion = GameInfo.GameVersion(),
                ModVersion = Plugin.Version,
                Character = new CharacterIdentity
                {
                    SteamId = GameInfo.LocalSteamId(),
                    PlayerId = p.GetPlayerID().ToString(),
                    Name = GameInfo.Clip(string.IsNullOrEmpty(p.GetPlayerName()) ? "unknown" : p.GetPlayerName(), 32),
                    WorldUid = GameInfo.WorldUid(),
                    WorldName = GameInfo.WorldName(),
                },
            };

            Equipment(p, m.Equipment);
            Inventory(p, m.Inventory);
            Skills(p, m.Skills);
            m.Trophies = PrefabList(p.m_trophies, Caps.TrophiesMax, "trophies");
            m.KnownItems = PrefabList(p.m_knownMaterial, Caps.KnownMax, "knownItems");
            m.KnownRecipes = PrefabList(p.m_knownRecipes, Caps.KnownMax, "knownRecipes");
            return m;
        }

        private static void Equipment(Humanoid h, List<EquippedItem> list)
        {
            Add(list, EquipSlot.Head, h.m_helmetItem);
            Add(list, EquipSlot.Chest, h.m_chestItem);
            Add(list, EquipSlot.Legs, h.m_legItem);
            Add(list, EquipSlot.Shoulder, h.m_shoulderItem);
            Add(list, EquipSlot.Utility, h.m_utilityItem);
            Add(list, EquipSlot.Trinket, h.m_trinketItem);
            Add(list, EquipSlot.RightHand, h.m_rightItem);
            Add(list, EquipSlot.LeftHand, h.m_leftItem);
            Add(list, EquipSlot.Ammo, h.m_ammoItem);
        }

        private static void Add(List<EquippedItem> list, string slot, ItemDrop.ItemData d)
        {
            if (d == null) return;
            var s = Stack(d);
            if (s != null) list.Add(new EquippedItem { Slot = slot, Item = s });
        }

        /// <summary>One item stack; null when the prefab cannot be named (then the stack is skipped and logged).</summary>
        public static ItemStack Stack(ItemDrop.ItemData d)
        {
            string prefab = PrefabName(d);
            if (prefab == null) { Log.Warn("item without a prefab name skipped: " + (d.m_shared?.m_name ?? "?")); return null; }
            var s = new ItemStack
            {
                Prefab = prefab,
                Stack = GameInfo.Clamp(d.m_stack, 1, 100_000),
                Quality = GameInfo.Clamp(d.m_quality, 1, 10),
                Variant = GameInfo.Clamp(d.m_variant, 0, 63),
            };
            if (d.m_shared != null && d.m_shared.m_useDurability)
            {
                double max = GameInfo.Clamp(d.GetMaxDurability(), 0, 1_000_000);
                s.Durability = GameInfo.Clamp(d.m_durability, 0, 1_000_000);
                s.MaxDurability = max;
            }
            return s;
        }

        public static string PrefabName(ItemDrop.ItemData d)
        {
            string name = null;
            try
            {
                if (d.m_dropPrefab != null) name = d.m_dropPrefab.name;
                if (name == null && ObjectDB.instance != null && d.m_shared != null)
                {
                    var go = ObjectDB.instance.GetItemPrefab(d.m_shared);
                    if (go != null) name = go.name;
                }
            }
            catch (Exception e) { Log.Debug("prefab lookup failed: " + e.Message); }
            return GameInfo.Prefab(name);
        }

        private static void Inventory(Player p, ContractInventory inv)
        {
            var src = p.GetInventory();
            if (src == null) { inv.Grid.Width = 8; inv.Grid.Height = 4; return; }
            int width = GameInfo.Clamp(src.GetWidth(), 1, Caps.GridEdgeMax);
            int height = GameInfo.Clamp(src.GetHeight(), 1, Caps.GridEdgeMax);
            inv.Grid.Width = width; inv.Grid.Height = height;
            for (int y = Caps.VanillaRows; y < height; y++)
                inv.Grid.ExtraRows.Add(new GridExtraRow { Y = y, Purpose = InventoryMods.PurposeOf(y, width) });

            var seen = new HashSet<int>();
            int skipped = 0;
            foreach (var d in src.GetAllItems())
            {
                if (d == null) continue;
                int x = d.m_gridPos.x, y = d.m_gridPos.y;
                if (x < 0 || y < 0 || x >= width || y >= height || !seen.Add(y * width + x)) { skipped++; continue; }
                var s = Stack(d);
                if (s == null) { skipped++; continue; }
                inv.Items.Add(new InventorySlot { X = x, Y = y, Equipped = d.m_equipped, Item = s });
                if (inv.Items.Count >= Caps.InventoryItemsMax) break;
            }
            inv.Items.Sort((a, b) => a.Y != b.Y ? a.Y.CompareTo(b.Y) : a.X.CompareTo(b.X));
            if (skipped > 0) Log.Warn($"inventory: {skipped} stack(s) skipped (outside grid, duplicate cell or unnamed prefab)");
        }

        private static void Skills(Player p, List<Skill> list)
        {
            var skills = p.GetSkills();
            if (skills == null || skills.m_skillData == null) return;
            var seen = new HashSet<string>();
            foreach (var kv in skills.m_skillData)
            {
                string id = GameInfo.Identifier(kv.Key.ToString(), 64);
                if (id == null || !seen.Add(id)) continue;
                list.Add(new Skill
                {
                    Id = id,
                    Level = GameInfo.Clamp(kv.Value.m_level, 0, 100),
                    Accumulator = GameInfo.Clamp(kv.Value.m_accumulator, 0, 1_000_000),
                });
            }
            list.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
            if (list.Count > Caps.SkillsMax) { Log.Warn($"skills: {list.Count - Caps.SkillsMax} dropped over the cap"); list.RemoveRange(Caps.SkillsMax, list.Count - Caps.SkillsMax); }
        }

        private static List<string> PrefabList(IEnumerable<string> src, int cap, string what)
        {
            var set = new SortedSet<string>(StringComparer.Ordinal);
            if (src != null)
                foreach (var raw in src)
                {
                    var p = GameInfo.Prefab(raw);
                    if (p != null) set.Add(p);
                }
            var list = new List<string>(set);
            if (list.Count > cap) { Log.Warn($"{what}: {list.Count - cap} dropped over the cap of {cap}"); list.RemoveRange(cap, list.Count - cap); }
            return list;
        }
    }
}
