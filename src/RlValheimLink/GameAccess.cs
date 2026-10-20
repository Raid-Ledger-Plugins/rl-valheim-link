using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

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

        // ---------- chest dump spike (docs/design-chests.md §7) ----------
        // Members that are new in 1.0.20, or whose real visibility only the 0.221.4 IL shows, are reached
        // by name here and resolved lazily: a wrong guess about one of them is a log line from the dump,
        // never a type-initialiser failure that would take the captures above down with it.

        private const BindingFlags AnyInstance = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        private const BindingFlags AnyStatic = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;

        /// <summary>Every key stored on one ZDO, by type (hash → value).</summary>
        public sealed class ZdoKeySet
        {
            public string Via;
            public List<KeyValuePair<int, float>> Floats = new List<KeyValuePair<int, float>>();
            public List<KeyValuePair<int, Vector3>> Vec3s = new List<KeyValuePair<int, Vector3>>();
            public List<KeyValuePair<int, Quaternion>> Quats = new List<KeyValuePair<int, Quaternion>>();
            public List<KeyValuePair<int, int>> Ints = new List<KeyValuePair<int, int>>();
            public List<KeyValuePair<int, long>> Longs = new List<KeyValuePair<int, long>>();
            public List<KeyValuePair<int, string>> Strings = new List<KeyValuePair<int, string>>();
            public List<KeyValuePair<int, byte[]>> ByteArrays = new List<KeyValuePair<int, byte[]>>();
        }

        /// <summary>
        /// All keys on a ZDO. 1.0.20: ZDOExtraData.GetData(id, out floats, …, out byteArrays, out connection);
        /// 0.221.4: ZDOExtraData.GetFloats(id) … GetByteArrays(id). The game hands out shared temp lists, so
        /// they are copied at once.
        /// </summary>
        public static ZdoKeySet ZdoKeys(ZDOID id)
        {
            var set = new ZdoKeySet();
            var getData = typeof(ZDOExtraData).GetMethods(AnyStatic).FirstOrDefault(m => m.Name == "GetData" && m.GetParameters().Length == 9);
            if (getData != null)
            {
                var args = new object[9];
                args[0] = id;
                Invoke(getData, null, args);
                Copy(args[1], set.Floats); Copy(args[2], set.Vec3s); Copy(args[3], set.Quats); Copy(args[4], set.Ints);
                Copy(args[5], set.Longs); Copy(args[6], set.Strings); Copy(args[7], set.ByteArrays);
                set.Via = "ZDOExtraData.GetData (" + Visibility(getData) + ")";
                return set;
            }
            Copy(CallStatic(typeof(ZDOExtraData), "GetFloats", id), set.Floats);
            Copy(CallStatic(typeof(ZDOExtraData), "GetVec3s", id), set.Vec3s);
            Copy(CallStatic(typeof(ZDOExtraData), "GetQuaternions", id), set.Quats);
            Copy(CallStatic(typeof(ZDOExtraData), "GetInts", id), set.Ints);
            Copy(CallStatic(typeof(ZDOExtraData), "GetLongs", id), set.Longs);
            Copy(CallStatic(typeof(ZDOExtraData), "GetStrings", id), set.Strings);
            Copy(CallStatic(typeof(ZDOExtraData), "GetByteArrays", id), set.ByteArrays);
            set.Via = "ZDOExtraData.Get<Type>s (0.221.x API)";
            return set;
        }

        private static void Copy<T>(object source, List<T> into)
        {
            if (source is IEnumerable<T> e) into.AddRange(e);
        }

        private static object CallStatic(Type t, string name, params object[] args)
        {
            var m = t.GetMethods(AnyStatic).FirstOrDefault(x => x.Name == name && x.GetParameters().Length == args.Length);
            if (m == null) throw new MissingMethodException(t.Name, name);
            return Invoke(m, null, args);
        }

        /// <summary>Reflection call that rethrows the game's own exception rather than TargetInvocationException.</summary>
        private static object Invoke(MethodBase m, object target, object[] args)
        {
            try { return m is ConstructorInfo c ? c.Invoke(args) : m.Invoke(target, args); }
            catch (TargetInvocationException e) when (e.InnerException != null) { throw e.InnerException; }
        }

        /// <summary>Number of ZDOs resident on this server (ZDOMan.m_objectsByID is private), or -1.</summary>
        public static int ResidentZdoCount()
        {
            var f = FindField(typeof(ZDOMan), "m_objectsByID");
            return ZDOMan.instance != null && f?.GetValue(ZDOMan.instance) is ICollection c ? c.Count : -1;
        }

        /// <summary>1.0.20's temporary inventory (`new Inventory(true)`); null when this build has no such constructor.</summary>
        public static Inventory NewTemporaryInventory()
        {
            var ctor = typeof(Inventory).GetConstructor(AnyInstance, null, new[] { typeof(bool) }, null);
            return ctor == null ? null : (Inventory)Invoke(ctor, null, new object[] { true });
        }

        /// <summary>1.0.20's `Inventory.Load(ZPackage, bool)`; throws MissingMethodException when absent.</summary>
        public static void LoadInventory(Inventory inv, ZPackage pkg, bool flag)
        {
            var m = typeof(Inventory).GetMethod("Load", AnyInstance, null, new[] { typeof(ZPackage), typeof(bool) }, null);
            if (m == null) throw new MissingMethodException("Inventory", "Load(ZPackage, Boolean)");
            Invoke(m, inv, new object[] { pkg, flag });
        }

        /// <summary>Every int field of ZDOVars as value → "s_name" (the game's names for ZDO key hashes).</summary>
        public static Dictionary<int, string> ZdoVarNames()
        {
            var names = new Dictionary<int, string>();
            foreach (var f in typeof(ZDOVars).GetFields(AnyStatic))
            {
                if (f.FieldType != typeof(int)) continue;
                int v = (int)f.GetValue(null);
                names[v] = names.TryGetValue(v, out var other) ? other + "|" + f.Name : f.Name;
            }
            return names;
        }

        /// <summary>A ZDOVars hash by field name (e.g. "s_itemData", new in 1.0.20); null when this build has no such field.</summary>
        public static int? ZdoVar(string field)
        {
            var f = typeof(ZDOVars).GetField(field, AnyStatic);
            return f != null && f.FieldType == typeof(int) ? (int?)(int)f.GetValue(null) : null;
        }

        /// <summary>Name(s) of a Version.Item value (106 = PickedUp|Stable, 110 = NewCheats in 1.0.20); null when unknown.</summary>
        public static string ItemVersionName(int value)
        {
            var t = typeof(global::Version).GetNestedType("Item", BindingFlags.Public | BindingFlags.NonPublic);
            if (t == null || !t.IsEnum) return null;
            var names = new List<string>();
            foreach (var n in Enum.GetNames(t)) if (Convert.ToInt32(Enum.Parse(t, n)) == value) names.Add(n);
            return names.Count == 0 ? null : string.Join("|", names);
        }

        /// <summary>Read a field or property by name, walking base types; false when this build has no such member.</summary>
        public static bool TryRead(object obj, string member, out object value)
        {
            value = null;
            if (obj == null) return false;
            var f = FindField(obj.GetType(), member);
            if (f != null) { value = f.GetValue(f.IsStatic ? null : obj); return true; }
            var p = FindProperty(obj.GetType(), member);
            if (p == null) return false;
            value = p.GetValue(obj, null);
            return true;
        }

        /// <summary>
        /// Real visibility in the running game: "public" / "private" / "protected" / "internal" / "missing" for a
        /// field or property; for a method or ".ctor", every overload as "(ParamTypes):visibility".
        /// </summary>
        public static string MemberVisibility(Type t, string member)
        {
            var methods = member == ".ctor"
                ? t.GetConstructors(AnyInstance).Cast<MethodBase>().ToList()
                : t.GetMethods(AnyInstance | BindingFlags.Static).Where(m => m.Name == member).Cast<MethodBase>().ToList();
            if (methods.Count > 0)
                return string.Join(",", methods.Select(m => "(" + string.Join(",", m.GetParameters().Select(p => p.ParameterType.Name)) + "):" + Visibility(m)));
            var f = FindField(t, member);
            if (f != null) return Visibility(f);
            var prop = FindProperty(t, member);
            return prop == null ? "missing" : Visibility(prop.GetGetMethod(true));
        }

        /// <summary>ItemDrop.Awake (private), so the dump can count item GameObjects a decode instantiates; null when absent.</summary>
        public static MethodInfo ItemDropAwake() =>
            typeof(ItemDrop).GetMethod("Awake", AnyInstance | BindingFlags.DeclaredOnly, null, Type.EmptyTypes, null);

        private static string Visibility(MemberInfo m) => m switch
        {
            null => "missing",
            FieldInfo f => f.IsPublic ? "public" : f.IsFamily ? "protected" : f.IsAssembly ? "internal" : "private",
            MethodBase b => b.IsPublic ? "public" : b.IsFamily ? "protected" : b.IsAssembly ? "internal" : "private",
            _ => "?",
        };

        private static FieldInfo FindField(Type t, string name)
        {
            for (; t != null; t = t.BaseType)
            {
                var f = t.GetField(name, AnyInstance | BindingFlags.Static | BindingFlags.DeclaredOnly);
                if (f != null) return f;
            }
            return null;
        }

        private static PropertyInfo FindProperty(Type t, string name)
        {
            for (; t != null; t = t.BaseType)
            {
                var p = t.GetProperty(name, AnyInstance | BindingFlags.DeclaredOnly);
                if (p != null && p.GetIndexParameters().Length == 0 && p.GetGetMethod(true) != null) return p;
            }
            return null;
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
