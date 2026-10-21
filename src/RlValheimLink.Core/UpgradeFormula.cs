using System;
using System.Globalization;

namespace RlValheimLink.Core
{
    /// <summary>
    /// The documented per-quality cost of a recipe requirement (CONTRACT.md §5b, Raid Ledger ROK-1894), used
    /// by the recipes export to decide `formula`: a recipe whose live values differ is a mod's doing (`custom`).
    /// 0.221.x (0.221.4 IL): amount at q1, then (q − 1) × amountPerLevel. 1.0.x (measured on 1.0.20, all
    /// 3 982 values of the 272 recipes the old rule missed): an upgrade item (m_upgraderResource) costs its
    /// amount at every quality step; any other requirement costs amountPerLevel × 2^(q − 2) from q2 (×1, ×2, ×4).
    /// Station and station level did not change. Pure; unit-tested.
    /// </summary>
    public static class UpgradeFormula
    {
        /// <summary>True for a game whose version string's major part is ≥ 1 (Valheim 1.0.x); 0.221.x and unparseable versions keep the 0.221 rule.</summary>
        public static bool IsOneDotX(string gameVersion)
        {
            if (string.IsNullOrEmpty(gameVersion)) return false;
            int dot = gameVersion.IndexOf('.');
            string major = dot < 0 ? gameVersion : gameVersion.Substring(0, dot);
            return int.TryParse(major, NumberStyles.None, CultureInfo.InvariantCulture, out int m) && m >= 1;
        }

        /// <summary>The documented amount of one requirement at quality <paramref name="q"/> (1-based).</summary>
        public static long Amount(string gameVersion, int amount, int amountPerLevel, bool upgraderResource, int q)
        {
            if (q <= 1) return amount;
            if (!IsOneDotX(gameVersion)) return (long)(q - 1) * amountPerLevel;
            if (upgraderResource) return amount;
            return (long)amountPerLevel << Math.Min(q - 2, 30);
        }

        /// <summary>Required station level at quality <paramref name="q"/>: max(1, minStationLevel) + (q − 1), unchanged in 1.0.x.</summary>
        public static int StationLevel(int minStationLevel, int q) => Math.Max(1, minStationLevel) + (q - 1);
    }
}
