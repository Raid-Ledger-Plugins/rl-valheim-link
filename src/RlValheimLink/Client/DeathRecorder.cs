using System;
using System.Collections.Generic;
using RlValheimLink.Contract;
using RlValheimLink.Core;

namespace RlValheimLink.Client
{
    /// <summary>
    /// ROK-1931: records each death of the local player into the character's custom data (saved with the profile,
    /// so it survives restarts and covers every world; the server keeps only its own world's entries) and reads
    /// the profile's lifetime Deaths stat. Pure list logic lives in <see cref="DeathHistory"/>.
    /// </summary>
    internal static class DeathRecorder
    {
        /// <summary>Called from the Player.OnDeath postfix, only for the local player. Never throws into the game.</summary>
        public static void OnLocalDeath(Player p)
        {
            try
            {
                if (p == null || PluginConfig.OptOut.Value) return;
                var data = p.m_customData;
                if (data == null) { Log.Warn("death not recorded: the player has no custom data"); return; }
                data.TryGetValue(DeathHistory.CustomDataKey, out string stored);
                var list = DeathHistory.Parse(stored);
                var pos = p.transform.position;
                if (DeathHistory.Record(list, GameInfo.WorldUid(), pos.x, pos.z, DateTime.UtcNow) == null)
                { Log.Warn("death not recorded: no usable world uid"); return; }
                data[DeathHistory.CustomDataKey] = DeathHistory.Serialize(list);
                Log.Info($"death recorded ({list.Count} in this character's history, cap {Caps.DeathsMax})");
            }
            catch (Exception e) { Log.Error("death record failed", e); }
        }

        /// <summary>The stored history as a character message carries it (null = omit the field).</summary>
        public static List<DeathRecord> ForMessage(Player p, string capturedAt)
        {
            var data = p.m_customData;
            if (data == null || !data.TryGetValue(DeathHistory.CustomDataKey, out string stored)) return null;
            return DeathHistory.ForMessage(DeathHistory.Parse(stored), capturedAt);
        }

        /// <summary>The profile's lifetime `Deaths` stat; null (field omitted, spec Q3) when this game build has none.</summary>
        public static int? DeathCount()
        {
            var profile = Game.instance != null ? Game.instance.GetPlayerProfile() : null;
            if (profile == null) return null;
            float? stat = GameAccess.DeathsStat(profile);
            if (!stat.HasValue || float.IsNaN(stat.Value) || float.IsInfinity(stat.Value)) return null;
            float v = stat.Value;
            return GameInfo.Clamp((int)Math.Min(Math.Round((double)v), Caps.DeathCountMax), 0, Caps.DeathCountMax);
        }
    }
}
