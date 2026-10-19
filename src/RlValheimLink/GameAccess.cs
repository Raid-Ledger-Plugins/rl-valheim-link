using System;
using System.Collections;
using System.Collections.Generic;
using HarmonyLib;

namespace RlValheimLink
{
    /// <summary>
    /// Every non-public game member the mod reads, reached through Harmony's reflection-backed
    /// accessors. The build compiles against publicized reference assemblies, but the game's runtime
    /// (Unity 6 Mono) enforces visibility: a direct call to a private member throws
    /// MethodAccessException / FieldAccessException at JIT time (seen on the first client run, 0.1.2).
    /// Nothing outside this class may touch a non-public game member; CI scans the built DLL for that.
    /// </summary>
    internal static class GameAccess
    {
        // Humanoid equipment slots (protected)
        public static readonly AccessTools.FieldRef<Humanoid, ItemDrop.ItemData> HelmetItem = AccessTools.FieldRefAccess<Humanoid, ItemDrop.ItemData>("m_helmetItem");
        public static readonly AccessTools.FieldRef<Humanoid, ItemDrop.ItemData> ChestItem = AccessTools.FieldRefAccess<Humanoid, ItemDrop.ItemData>("m_chestItem");
        public static readonly AccessTools.FieldRef<Humanoid, ItemDrop.ItemData> LegItem = AccessTools.FieldRefAccess<Humanoid, ItemDrop.ItemData>("m_legItem");
        public static readonly AccessTools.FieldRef<Humanoid, ItemDrop.ItemData> ShoulderItem = AccessTools.FieldRefAccess<Humanoid, ItemDrop.ItemData>("m_shoulderItem");
        public static readonly AccessTools.FieldRef<Humanoid, ItemDrop.ItemData> UtilityItem = AccessTools.FieldRefAccess<Humanoid, ItemDrop.ItemData>("m_utilityItem");
        public static readonly AccessTools.FieldRef<Humanoid, ItemDrop.ItemData> TrinketItem = AccessTools.FieldRefAccess<Humanoid, ItemDrop.ItemData>("m_trinketItem");
        public static readonly AccessTools.FieldRef<Humanoid, ItemDrop.ItemData> RightItem = AccessTools.FieldRefAccess<Humanoid, ItemDrop.ItemData>("m_rightItem");
        public static readonly AccessTools.FieldRef<Humanoid, ItemDrop.ItemData> LeftItem = AccessTools.FieldRefAccess<Humanoid, ItemDrop.ItemData>("m_leftItem");
        public static readonly AccessTools.FieldRef<Humanoid, ItemDrop.ItemData> AmmoItem = AccessTools.FieldRefAccess<Humanoid, ItemDrop.ItemData>("m_ammoItem");

        // Player knowledge (private)
        public static readonly AccessTools.FieldRef<Player, HashSet<string>> Trophies = AccessTools.FieldRefAccess<Player, HashSet<string>>("m_trophies");
        public static readonly AccessTools.FieldRef<Player, HashSet<string>> KnownMaterial = AccessTools.FieldRefAccess<Player, HashSet<string>>("m_knownMaterial");
        public static readonly AccessTools.FieldRef<Player, HashSet<string>> KnownRecipes = AccessTools.FieldRefAccess<Player, HashSet<string>>("m_knownRecipes");

        // Skills (private)
        public static readonly AccessTools.FieldRef<Skills, Dictionary<Skills.SkillType, Skills.Skill>> SkillData = AccessTools.FieldRefAccess<Skills, Dictionary<Skills.SkillType, Skills.Skill>>("m_skillData");

        // Minimap (private). m_explored / m_exploredOthers are BitArray in 1.0.x.
        public static readonly AccessTools.FieldRef<Minimap, BitArray> Explored = AccessTools.FieldRefAccess<Minimap, BitArray>("m_explored");
        public static readonly AccessTools.FieldRef<Minimap, BitArray> ExploredOthers = AccessTools.FieldRefAccess<Minimap, BitArray>("m_exploredOthers");
        public static readonly AccessTools.FieldRef<Minimap, List<Minimap.PinData>> Pins = AccessTools.FieldRefAccess<Minimap, List<Minimap.PinData>>("m_pins");

        /// <summary>
        /// The routed-RPC id of this game instance (ZRoutedRpc.m_id is private; ZNet sets it from the
        /// ZDO session id, which is public).
        /// </summary>
        public static long OwnRoutedId() => ZDOMan.GetSessionID();

        /// <summary>
        /// Where a client's routed RPCs go: the server peer's uid, or our own id when we are the server
        /// (a non-dedicated host loops back). Replaces the private ZRoutedRpc.GetServerPeerID().
        /// </summary>
        public static long ServerRoutedId()
        {
            var znet = ZNet.instance;
            if (znet == null) return 0L;
            if (znet.IsServer()) return OwnRoutedId();
            var peer = znet.GetServerPeer();
            return peer != null ? peer.m_uid : 0L;
        }

        /// <summary>Touch every accessor so a missing or retyped field surfaces at startup as one log line, not mid-capture.</summary>
        public static string SelfCheck()
        {
            try
            {
                _ = HelmetItem; _ = ChestItem; _ = LegItem; _ = ShoulderItem; _ = UtilityItem; _ = TrinketItem; _ = RightItem; _ = LeftItem; _ = AmmoItem;
                _ = Trophies; _ = KnownMaterial; _ = KnownRecipes; _ = SkillData; _ = Explored; _ = ExploredOthers; _ = Pins;
                return null;
            }
            catch (Exception e) { return e.GetBaseException().Message; }
        }
    }
}
