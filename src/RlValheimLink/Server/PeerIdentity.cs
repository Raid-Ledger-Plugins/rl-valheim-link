using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace RlValheimLink.Server
{
    /// <summary>What the server can prove about a connected peer, cached so a message that lands just after a logout can still be attributed (design §4).</summary>
    internal sealed class PeerFacts
    {
        public long Uid;
        public string SteamId;        // null when the socket identity is not a SteamID64
        public string HostName;       // raw, for logs
        public long? PlayerId;        // from the character ZDO; null until spawned
        public bool IsAdmin;
        public DateTime SeenUtc;
        public string ModSetHash;     // from hello
        public string GameVersion;    // from hello
    }

    internal static class PeerIdentity
    {
        private static readonly Regex SteamDigits = new Regex(@"^(?:Steam_)?(\d{17})$");
        private static readonly Dictionary<long, PeerFacts> Cache = new Dictionary<long, PeerFacts>();
        private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(10);

        public static void Reset() => Cache.Clear();

        /// <summary>Refresh every connected peer; called every few seconds from the server tick.</summary>
        public static void RefreshAll()
        {
            var znet = ZNet.instance;
            if (znet == null) return;
            var now = DateTime.UtcNow;
            try
            {
                foreach (var peer in znet.GetPeers())
                {
                    if (peer == null || !peer.IsReady()) continue;
                    var f = Get(peer.m_uid) ?? new PeerFacts { Uid = peer.m_uid };
                    string host = null;
                    try { host = peer.m_socket?.GetHostName(); } catch { }
                    f.HostName = host ?? "";
                    var m = SteamDigits.Match(f.HostName);
                    f.SteamId = m.Success && string.CompareOrdinal(m.Groups[1].Value, Contract.Caps.SteamId64Min) >= 0 ? m.Groups[1].Value : null;
                    try { f.IsAdmin = f.SteamId != null && znet.IsAdmin(f.SteamId); } catch { f.IsAdmin = false; }
                    try
                    {
                        if (!peer.m_characterID.IsNone())
                        {
                            var zdo = ZDOMan.instance?.GetZDO(peer.m_characterID);
                            long pid = zdo != null ? zdo.GetLong(ZDOVars.s_playerID, 0L) : 0L;
                            if (pid != 0L) f.PlayerId = pid;
                        }
                    }
                    catch { }
                    f.SeenUtc = now;
                    Cache[peer.m_uid] = f;
                }
            }
            catch (Exception e) { Log.Debug("peer refresh failed: " + e.Message); }
            var stale = new List<long>();
            foreach (var kv in Cache) if (now - kv.Value.SeenUtc > Ttl) stale.Add(kv.Key);
            foreach (var uid in stale) Cache.Remove(uid);
        }

        public static PeerFacts Get(long uid) => Cache.TryGetValue(uid, out var f) ? f : null;

        /// <summary>The local host's own facts when a non-dedicated server receives its own loop-back messages.</summary>
        public static PeerFacts Self(long uid)
        {
            var f = Get(uid) ?? new PeerFacts { Uid = uid };
            f.SteamId = Client.GameInfo.LocalSteamId();
            if (f.SteamId == Client.GameInfo.PlaceholderSteamId) f.SteamId = null;
            f.HostName = "self";
            f.IsAdmin = true;
            var p = Player.m_localPlayer;
            if (p != null) f.PlayerId = p.GetPlayerID();
            f.SeenUtc = DateTime.UtcNow;
            Cache[uid] = f;
            return f;
        }

        public static void RememberHello(long uid, string gameVersion, string modSetHash)
        {
            var f = Get(uid) ?? new PeerFacts { Uid = uid, SeenUtc = DateTime.UtcNow };
            f.GameVersion = gameVersion; f.ModSetHash = modSetHash;
            Cache[uid] = f;
        }
    }
}
