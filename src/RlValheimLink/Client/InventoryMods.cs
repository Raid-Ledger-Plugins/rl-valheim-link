using System;
using System.Reflection;
using BepInEx.Bootstrap;
using RlValheimLink.Contract;

namespace RlValheimLink.Client
{
    /// <summary>
    /// Names the purpose of inventory rows past the vanilla four by asking the inventory mod that
    /// added them (docs/design.md §2). Reflection only; any failure means "other". UNVERIFIED method
    /// names for EquipmentAndQuickSlots — the operator checklist confirms them in game.
    /// </summary>
    internal static class InventoryMods
    {
        private const string EaqsGuid = "randyknapp.mods.equipmentandquickslots";
        private const string AzuEpiGuid = "Azumatt.AzuExtendedPlayerInventory";

        private static bool _probed;
        /// <summary>EquipmentAndQuickSlots 3.x: `public static bool API.IsSlotCell(int x, int y, out string slotId)`.</summary>
        private static MethodInfo _isSlotCell;
        private static bool _azuEpi;

        private static void Probe()
        {
            if (_probed) return;
            _probed = true;
            try
            {
                _azuEpi = Chainloader.PluginInfos.ContainsKey(AzuEpiGuid);
                if (Chainloader.PluginInfos.TryGetValue(EaqsGuid, out var eaqs) && eaqs.Instance != null)
                {
                    var api = eaqs.Instance.GetType().Assembly.GetType("EquipmentAndQuickSlots.API", false);
                    _isSlotCell = api?.GetMethod("IsSlotCell", BindingFlags.Public | BindingFlags.Static, null,
                        new[] { typeof(int), typeof(int), typeof(string).MakeByRefType() }, null);
                    Log.Info($"EquipmentAndQuickSlots {eaqs.Metadata.Version} detected: API.IsSlotCell={(_isSlotCell != null)}");
                }
            }
            catch (Exception e) { Log.Warn("inventory mod probe failed: " + e.Message); }
        }

        /// <summary>
        /// Purpose of row <paramref name="y"/> (≥ 4) in a grid of <paramref name="width"/> columns: the
        /// first EAQS slot cell in the row decides (a slot id containing "quick" is a quick slot, any other
        /// EAQS slot is an equipment slot); AzuExtendedPlayerInventory rows are "extended"; else "other".
        /// </summary>
        public static string PurposeOf(int y, int width)
        {
            Probe();
            try
            {
                if (_isSlotCell != null)
                {
                    for (int x = 0; x < width; x++)
                    {
                        var args = new object[] { x, y, null };
                        if ((bool)_isSlotCell.Invoke(null, args))
                        {
                            string id = args[2] as string ?? "";
                            return id.IndexOf("quick", StringComparison.OrdinalIgnoreCase) >= 0 ? RowPurpose.Quickslot : RowPurpose.Equipment;
                        }
                    }
                }
            }
            catch (Exception e) { Log.Debug("row purpose probe failed: " + e.Message); }
            if (_azuEpi) return RowPurpose.Extended;
            return RowPurpose.Other;
        }
    }
}
