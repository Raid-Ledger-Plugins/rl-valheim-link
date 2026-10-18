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
        private static MethodInfo _isEquipmentSlot, _isQuickSlot;
        private static Type _vector2i;
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
                    var asm = eaqs.Instance.GetType().Assembly;
                    foreach (var t in asm.GetTypes())
                    {
                        var eq = t.GetMethod("IsEquipmentSlot", BindingFlags.Public | BindingFlags.Static);
                        var qs = t.GetMethod("IsQuickSlot", BindingFlags.Public | BindingFlags.Static);
                        if (eq != null && eq.GetParameters().Length == 1 && _isEquipmentSlot == null) { _isEquipmentSlot = eq; _vector2i = eq.GetParameters()[0].ParameterType; }
                        if (qs != null && qs.GetParameters().Length == 1 && _isQuickSlot == null) _isQuickSlot = qs;
                    }
                    Log.Info($"EquipmentAndQuickSlots detected: IsEquipmentSlot={(_isEquipmentSlot != null)} IsQuickSlot={(_isQuickSlot != null)}");
                }
            }
            catch (Exception e) { Log.Warn("inventory mod probe failed: " + e.Message); }
        }

        /// <summary>Purpose of row <paramref name="y"/> (≥ 4) in a grid of <paramref name="width"/> columns.</summary>
        public static string PurposeOf(int y, int width)
        {
            Probe();
            try
            {
                if (_vector2i != null)
                {
                    for (int x = 0; x < width; x++)
                    {
                        object pos = Activator.CreateInstance(_vector2i, x, y);
                        if (_isEquipmentSlot != null && (bool)_isEquipmentSlot.Invoke(null, new[] { pos })) return RowPurpose.Equipment;
                        if (_isQuickSlot != null && (bool)_isQuickSlot.Invoke(null, new[] { pos })) return RowPurpose.Quickslot;
                    }
                }
            }
            catch (Exception e) { Log.Debug("row purpose probe failed: " + e.Message); }
            if (_azuEpi) return RowPurpose.Extended;
            return RowPurpose.Other;
        }
    }
}
