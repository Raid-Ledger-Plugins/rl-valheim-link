using System;
using System.Collections.Generic;
using System.Text;
using RlValheimLink.Contract;
using RlValheimLink.Net;

namespace RlValheimLink.Client
{
    /// <summary>
    /// The player-side half (docs/design.md §2): capture on every profile save, validate with the
    /// contract validator, gzip, hand to the channel. Opt-out short-circuits everything.
    /// </summary>
    internal static class ClientHalf
    {
        /// <summary>The client does not know Raid Ledger's server id; the server half replaces this (design §4).</summary>
        public const string PendingServerId = "pending";

        private static bool _active, _helloSent;
        private static DateTime _lastCharacter = DateTime.MinValue, _lastMap = DateTime.MinValue;
        private static string _lastMapHash;

        public static void Start()
        {
            _helloSent = false; _lastMapHash = null;
            _active = !PluginConfig.OptOut.Value;
            LinkChannel.ControlReceived -= OnControl;
            LinkChannel.ControlReceived += OnControl;
            if (!_active) { Log.Info("client half OFF: Client.OptOut = true (nothing is sent from this game)"); return; }
            Log.Info($"client half active (sendCharacter={PluginConfig.SendCharacter.Value} sendMap={PluginConfig.SendMap.Value} iconSize={PluginConfig.IconSize.Value})");
        }

        public static void Stop()
        {
            _active = false;
            LinkChannel.ControlReceived -= OnControl;
            ItemsCapture.Cancel();
            PieceIconsCapture.Cancel();
        }

        public static void Tick()
        {
            if (!_active || _helloSent) return;
            if (Player.m_localPlayer == null || ZNet.instance == null) return;
            SendHello();
        }

        private static void SendHello()
        {
            _helloSent = true;
            var hello = new Newtonsoft.Json.Linq.JObject
            {
                ["protocol"] = LinkChannel.Protocol,
                ["modVersion"] = Plugin.Version,
                ["gameVersion"] = GameInfo.GameVersion(),
                ["modSetHash"] = ModSetHash(),
                // Optional work this client can do on request; older servers ignore the field (0.4.0).
                ["features"] = new Newtonsoft.Json.Linq.JArray(Features),
            };
            LinkChannel.Send(PayloadKind.Hello, MessageJson.Utf8NoBom.GetBytes(hello.ToString(Newtonsoft.Json.Formatting.None)), "hello");
        }

        /// <summary>Hello capability flags. `pieceIcons`: renders the piece/station icon atlas for the recipes export (`needPieceIcons`).</summary>
        public static readonly string[] Features = { "pieceIcons" };

        /// <summary>SHA-256 of the loaded BepInEx plugin GUIDs + versions: the server asks for a new items export when it changes.</summary>
        public static string ModSetHash()
        {
            var ids = new List<string>();
            foreach (var kv in BepInEx.Bootstrap.Chainloader.PluginInfos) ids.Add(kv.Key + "@" + kv.Value.Metadata.Version);
            ids.Sort(StringComparer.Ordinal);
            return GameInfo.Sha256Hex(Encoding.UTF8.GetBytes(string.Join("\n", ids)));
        }

        /// <summary>Harmony postfix of Game.SavePlayerProfile: the periodic save broadcast, logout and quit.</summary>
        public static void OnProfileSaved(bool setLogoutPoint)
        {
            if (!_active) return;
            var p = Player.m_localPlayer;
            if (p == null) return;
            var now = DateTime.UtcNow;
            try
            {
                if (PluginConfig.SendCharacter.Value && (now - _lastCharacter).TotalSeconds >= PluginConfig.MinSecondsBetweenCharacter.Value)
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    var msg = CharacterCapture.Capture(p);
                    if (Dispatch(msg, PayloadKind.Character, $"character (equipment={msg.Equipment.Count} items={msg.Inventory.Items.Count} grid={msg.Inventory.Grid.Width}x{msg.Inventory.Grid.Height} skills={msg.Skills.Count} trophies={msg.Trophies.Count} knownItems={msg.KnownItems.Count} knownRecipes={msg.KnownRecipes.Count}) captured in {sw.ElapsedMilliseconds} ms"))
                        _lastCharacter = now;
                }
            }
            catch (Exception e) { Log.Error("character capture failed", e); }

            try
            {
                if (PluginConfig.SendMap.Value && (now - _lastMap).TotalSeconds >= PluginConfig.MinSecondsBetweenMap.Value)
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    var r = MapCapture.Capture(p);
                    if (r == null) return;
                    if (r.ContentHash == _lastMapHash) { Log.Debug("map unchanged since last send"); return; }
                    var m = r.Message;
                    if (Dispatch(m, PayloadKind.Map, $"map (size={m.Grid.TextureSize} pixel={m.Grid.PixelSizeMetres}m explored={m.Explored.Data.Length}ch shared={(m.SharedExplored == null ? "none" : m.SharedExplored.Data.Length + "ch")} pins={m.Pins.Count}) captured in {sw.ElapsedMilliseconds} ms"))
                    { _lastMap = now; _lastMapHash = r.ContentHash; }
                }
            }
            catch (Exception e) { Log.Error("map capture failed", e); }

            // Logout and quit save with setLogoutPoint = true and close the connection in this same frame:
            // push the small snapshots out now (big transfers are dropped; the server re-asks next session).
            if (setLogoutPoint) { ItemsCapture.Cancel(); PieceIconsCapture.Cancel(); LinkChannel.FlushForLogout(); }
        }

        /// <summary>Validate → serialise → size check → gzip → queue. Returns false (and logs why) when nothing was sent.</summary>
        public static bool Dispatch(LinkMessage msg, PayloadKind kind, string summary)
        {
            var issues = Validator.Validate(msg);
            if (issues.Count > 0)
            {
                Log.Error($"{summary}: NOT sent, {issues.Count} contract issue(s): {string.Join("; ", issues.ConvertAll(i => i.ToString()).ToArray())}");
                return false;
            }
            byte[] json = MessageJson.SerializeToBytes(msg);
            int cap = Caps.BodyCapFor(msg.Kind);
            if (json.Length > cap) { Log.Error($"{summary}: NOT sent, {json.Length} bytes over the §7 cap {cap}"); return false; }
            byte[] gz = LinkChannel.Gzip(json);
            Log.Info($"{summary}; {json.Length} bytes JSON, {gz.Length} bytes gzip");
            return LinkChannel.Send(kind, gz, msg.Kind) > 0;
        }

        private static void OnControl(string op, string arg)
        {
            if (!_active) return;
            switch (op)
            {
                case "needItems":
                    Log.Info("server asked for an items export (" + arg + ")");
                    ItemsCapture.Start(arg);
                    break;
                case "needPieceIcons":
                    Log.Info("server asked for the piece icon atlas (" + arg + ")");
                    PieceIconsCapture.Start(arg);
                    break;
                case "reject":
                    if (arg != null && arg.StartsWith("hello: ", StringComparison.Ordinal))
                    {
                        // The server declined this session as a whole (e.g. the world is not in its Server.WorldAllowlist): capture nothing more.
                        _active = false;
                        ItemsCapture.Cancel();
                        PieceIconsCapture.Cancel();
                        Log.Warn("server declined this client; nothing is sent this session: " + arg.Substring(7));
                        break;
                    }
                    Log.Warn("server rejected a transfer: " + arg);
                    break;
                case "ack":
                    Log.Debug("server ack: " + arg);
                    break;
                default:
                    Log.Debug("unknown control op " + op);
                    break;
            }
        }
    }
}
