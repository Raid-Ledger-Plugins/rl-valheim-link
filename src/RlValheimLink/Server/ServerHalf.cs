using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using Newtonsoft.Json.Linq;
using RlValheimLink.Client;
using RlValheimLink.Contract;
using RlValheimLink.Core;
using RlValheimLink.Net;

namespace RlValheimLink.Server
{
    /// <summary>Adapts the plugin logger to Core.</summary>
    internal sealed class LogAdapter : ILinkLog
    {
        public void Debug(string message) => Log.Debug(message);
        public void Info(string message) => Log.Info(message);
        public void Warn(string message) => Log.Warn(message);
        public void Error(string message) => Log.Error(message);
    }

    /// <summary>
    /// The dedicated-server half (design §4–§6): receive a transfer → inflate under the kind's cap →
    /// enrich with what the server can prove → validate → spool (latest wins) → upload; render the
    /// base map once per world; ask one client for the items export per (game version, mod set) and, from
    /// 0.4.0, one client advertising `pieceIcons` for the piece/station icon atlas of the recipes export.
    /// On a dedicated server it also builds two kinds itself (docs/design-chests.md): the chest scan
    /// (`containers`, on a fixed grid) and the `recipes` export (once per game version + server mod set).
    /// </summary>
    internal static class ServerHalf
    {
        private static bool _active;
        private static Spool _spool;
        private static RaidLedgerClient _client;
        private static Uploader _uploader;
        private static BaseMapRenderer _baseMap;
        private static DateTime _nextPeerRefresh = DateTime.MinValue, _nextStatus = DateTime.MinValue, _baseMapRetryAt = DateTime.MinValue;
        private static string _itemsLeasePeer; private static DateTime _itemsLeaseUntil = DateTime.MinValue;
        private static string _worldUid, _gameVersion, _worldName;
        /// <summary>The loaded world is not in Server.WorldAllowlist: hellos are declined, nothing is rendered, spooled or uploaded.</summary>
        private static bool _worldIdle;
        private static bool _dedicated, _waitingLogged;

        // Chest scan (`containers`, docs/design-chests.md §1.7): first slot 2 min after start, then every Chests.IntervalMinutes.
        private static readonly TimeSpan ChestFirstDelay = TimeSpan.FromMinutes(2);
        private static ScanSchedule _chestSchedule;
        private static ChestScanner _chests;
        private static int _chestScans, _unreadableStreak;
        private static DateTime _unreadableWarnAt = DateTime.MinValue;
        private static string _chestStatus = "off";

        // Recipes export (docs/design-chests.md §2): ~90 s after start, once per (game version, server mod set).
        private static RecipesExport _recipes;
        private static DateTime _recipesAt = DateTime.MaxValue;
        private static string _serverModSet;
        /// <summary>Send the recipes export again although its marker is set: a new piece-icon atlas arrived.</summary>
        private static bool _recipesForce;
        // Piece/station icons for the recipes export, rendered by an admin client (0.4.0): the stored map and the request lease.
        private static PieceIconMap _pieceIcons;
        private static string _iconsLeasePeer; private static DateTime _iconsLeaseUntil = DateTime.MinValue;
        private static string _recipesStatus = "off";

        public static string SpoolDirectory =>
            string.IsNullOrEmpty(PluginConfig.SpoolDirectory.Value) ? Path.Combine(BepInEx.Paths.BepInExRootPath, "RLValheimLink", "spool") : PluginConfig.SpoolDirectory.Value;

        public static void Start()
        {
            _active = true;
            LinkChannel.PayloadReceived -= OnPayload;
            LinkChannel.PayloadReceived += OnPayload;
            PeerIdentity.Reset();
            _worldUid = GameInfo.WorldUid();
            _gameVersion = GameInfo.GameVersion();
            _worldName = GameInfo.WorldName();
            _worldIdle = !WorldAllowlist.Allows(PluginConfig.WorldAllowlist.Value, _worldName);
            _dedicated = ZNet.instance != null && ZNet.instance.IsDedicated();
            _chestSchedule = null; _chests = null; _chestScans = 0; _unreadableStreak = 0; _chestStatus = "off";
            _recipes = null; _recipesAt = DateTime.MaxValue; _serverModSet = null; _recipesStatus = "off"; _waitingLogged = false;
            _recipesForce = false; _pieceIcons = null; _iconsLeasePeer = null; _iconsLeaseUntil = DateTime.MinValue;
            if (_worldIdle)
            {
                Log.Info($"world '{_worldName}' not in Server.WorldAllowlist; link idle");
                return;
            }

            bool tokenSet = !string.IsNullOrEmpty(PluginConfig.ServerToken.Value);
            bool configured = tokenSet && !string.IsNullOrEmpty(PluginConfig.ServerId.Value) && !string.IsNullOrEmpty(PluginConfig.BaseUrl.Value);
            try
            {
                _spool = new Spool(SpoolDirectory);
                _client = new RaidLedgerClient(new ClientSettings
                {
                    BaseUrl = PluginConfig.BaseUrl.Value,
                    MessagesPath = PluginConfig.MessagesPath.Value,
                    BlobsPath = PluginConfig.BlobsPath.Value,
                    Gzip = PluginConfig.UploadGzip.Value,
                    UserAgent = "RLValheimLink/" + Plugin.Version,
                }, () => PluginConfig.ServerToken.Value);
                _uploader = new Uploader(_spool, _client, new UploaderSettings
                {
                    MinInterval = TimeSpan.FromSeconds(PluginConfig.UploadIntervalSeconds.Value),
                    MaxMessagesPerMinute = PluginConfig.UploadMaxMessagesPerMinute.Value,
                    MaxBlobsPerMinute = PluginConfig.UploadMaxBlobsPerMinute.Value,
                    MaxBlobBytes = PluginConfig.UploadMaxBlobBytes.Value,
                }, new LogAdapter());
                // Instance URL + server id + token (hashed; never logged) identify the upload target.
                string fingerprint = Contract.Png.Sha256Hex(MessageJson.Utf8NoBom.GetBytes(
                    PluginConfig.BaseUrl.Value.TrimEnd('/') + "|" + PluginConfig.ServerId.Value + "|" + Contract.Png.Sha256Hex(MessageJson.Utf8NoBom.GetBytes(PluginConfig.ServerToken.Value ?? "")))).Substring(0, 16);
                if (_spool.ResetIfTargetChanged(fingerprint))
                    Log.Info("upload target changed (instance, server id or token): forgot what was uploaded before — the base map and the items export will be sent again");
                _baseMap = PluginConfig.BaseMapEnabled.Value
                    ? new BaseMapRenderer(Path.Combine(SpoolDirectory, "basemap"), PluginConfig.BaseMapTextureSize.Value, PluginConfig.BaseMapPixelSizeMetres.Value, PluginConfig.BaseMapRowsPerTick.Value)
                    : null;
                _baseMapRetryAt = DateTime.UtcNow.AddSeconds(15);
            }
            catch (Exception e) { Log.Error("server half failed to start", e); _active = false; return; }

            Log.Info($"server half active: serverId='{PluginConfig.ServerId.Value}' baseUrl='{PluginConfig.BaseUrl.Value}' token={(tokenSet ? "set" : "MISSING")} world={_worldUid} game={_gameVersion} spool={SpoolDirectory} ({_spool.Summary()})");
            if (!configured) Log.Warn("RaidLedger.BaseUrl, ServerToken and ServerId must all be set in BepInEx/config/RaidLedger.ValheimLink.cfg; messages are spooled but NOT uploaded until then");
            StartServerKinds();
        }

        public static void Stop()
        {
            _active = false; _worldIdle = false;
            _chests?.Cancel(); _chests = null; _chestSchedule = null; _recipes = null;
            LinkChannel.PayloadReceived -= OnPayload;
            _client?.Dispose(); _client = null; _uploader = null; _spool = null; _baseMap = null;
        }

        public static void Tick()
        {
            if (!_active || _worldIdle) return;
            var now = DateTime.UtcNow;
            try
            {
                if (now >= _nextPeerRefresh) { PeerIdentity.RefreshAll(); _nextPeerRefresh = now.AddSeconds(5); }
                if (_baseMap != null)
                {
                    if (!_baseMap.Done && !_baseMap.Busy && now >= _baseMapRetryAt)
                    {
                        if (!_baseMap.Begin(_worldUid, _gameVersion)) _baseMapRetryAt = now.AddSeconds(30);
                        if (_baseMap.Done) SpoolBaseMap();
                    }
                    else if (_baseMap.Tick()) SpoolBaseMap();
                }
                if (Configured()) _uploader.Tick(now);
                TickServerKinds(now);
                if (now >= _nextStatus)
                {
                    _nextStatus = now.AddMinutes(5);
                    if (_spool.PendingCount > 0 || _uploader.State != UploaderState.Idle)
                        Log.Info($"status: uploader={_uploader.State} {_spool.Summary()} uploaded={_uploader.Uploaded} failed={_uploader.Failed}");
                }
            }
            catch (Exception e) { Log.Error("server tick failed", e); }
        }

        private static bool Configured() =>
            !string.IsNullOrEmpty(PluginConfig.ServerToken.Value) && !string.IsNullOrEmpty(PluginConfig.ServerId.Value) && !string.IsNullOrEmpty(PluginConfig.BaseUrl.Value);

        private static void SpoolBaseMap()
        {
            if (_baseMap?.Ref == null || _spool.HasBlob(_baseMap.Ref.Sha256)) return;
            _spool.PutBlob(_baseMap.Ref.Sha256, _baseMap.Png, _baseMap.Ref.Width, _baseMap.Ref.Height, DateTime.UtcNow);
            Log.Info($"base map queued for upload ({_baseMap.Png.Length} bytes)");
        }

        public static string Status() => !_active ? "server half inactive"
            : _worldIdle ? $"link idle: world '{_worldName}' not in Server.WorldAllowlist"
            : $"uploader={_uploader?.State} {_spool?.Summary()} uploaded={_uploader?.Uploaded} failed={_uploader?.Failed} baseMap={(_baseMap == null ? "off" : _baseMap.Done ? "ready" : _baseMap.Busy ? "rendering" : "pending")} chests={_chestStatus} recipes={_recipesStatus}";

        // ---------- kinds the server builds itself: containers, recipes ----------

        private static void StartServerKinds()
        {
            if (!_dedicated)
            {
                // A player's single-player or self-hosted world (IsServer too) gets no scan hitches.
                Log.Info("chest scan and recipes export: dedicated servers only; off on this self-hosted world");
                return;
            }
            var now = DateTime.UtcNow;
            if (PluginConfig.ChestsEnabled.Value)
            {
                _chestSchedule = new ScanSchedule(now, ChestFirstDelay, TimeSpan.FromMinutes(PluginConfig.ChestsIntervalMinutes.Value));
                _chestStatus = "scheduled";
                Log.Info($"chest scan on: every {PluginConfig.ChestsIntervalMinutes.Value} min, the first in {ChestFirstDelay.TotalSeconds:0} s; vehicles {(PluginConfig.ChestsIncludeVehicles.Value ? "included" : "excluded")}; excluded prefabs: {Csv(PluginConfig.ChestsExcludePrefabs.Value)}");
            }
            else Log.Info("chest scan off (Chests.Enabled = false)");
            if (PluginConfig.RecipesEnabled.Value)
            {
                _recipesAt = now.AddSeconds(90); _recipesStatus = "scheduled";
                _pieceIcons = _spool.LoadPieceIcons();
                if (_pieceIcons != null) Log.Info($"piece icon atlas on file: {_pieceIcons.Icons.Count} icons for game {_pieceIcons.GameVersion}, atlas {_pieceIcons.Sha12}…{(_spool.HasBlob(_pieceIcons.Atlas.Sha256) ? "" : " (blob gone: the next admin client is asked again)")}");
            }
            else Log.Info("recipes export off (Recipes.Enabled = false)");
        }

        private static void TickServerKinds(DateTime now)
        {
            if (_chestSchedule == null && _recipesAt == DateTime.MaxValue && _recipes == null) return;
            if (!Configured())
            {
                // Nothing would reach Raid Ledger: spare the server the scans until the settings are there.
                if (!_waitingLogged) { _waitingLogged = true; Log.Info("chest scan and recipes export wait for the [RaidLedger] settings"); }
                return;
            }
            TickChests(now);
            TickRecipes(now);
        }

        private static void TickChests(DateTime now)
        {
            if (_chestSchedule == null) return;
            if (_chests != null && _chests.Running)
            {
                ChestScanner.Result result = null;
                try { result = _chests.Tick(); }
                catch (Exception e) { Log.Error("chest scan aborted; the next scan starts on schedule", e); _chestStatus = "aborted"; }
                if (result != null) FinishChestScan(result);
            }
            if (!_chestSchedule.TryStart(now, _chests != null && _chests.Running, out DateTime slot, out int skipped)) return;
            if (skipped > 0) Log.Info($"chest scan: {skipped} slot(s) skipped (a scan was still running or the server stalled)");
            if (_chests == null)
            {
                _chests = ChestScanner.Create(PluginConfig.ChestsStacksPerTick.Value, PluginConfig.ChestsIncludeVehicles.Value,
                    CsvSet(PluginConfig.ChestsExcludePrefabs.Value), CsvSet(PluginConfig.ChestsExtraPrefabs.Value));
                if (_chests == null) { Log.Warn("chest scan: the world is not loaded yet; trying at the next slot"); return; }
                var prefabs = _chests.Prefabs;
                int vehicles = 0; foreach (var p in prefabs) if (p.Vehicle) vehicles++;
                Log.Info($"chest scan: {prefabs.Count} container prefabs ({vehicles} vehicles): {ChestScanner.Describe(prefabs)}");
            }
            _chests.Begin(slot);
            _chestStatus = "scanning";
        }

        private static void FinishChestScan(ChestScanner.Result res)
        {
            _chestScans++;
            var msg = ContainersBuilder.Build(new ContainersHeader
            {
                ServerId = PluginConfig.ServerId.Value, GameVersion = _gameVersion, ModVersion = Plugin.Version, WorldUid = _worldUid, CapturedAtUtc = res.ScheduledAt,
            }, res.Containers, out var rep);
            var r = Enricher.Seal(msg, PluginConfig.ServerId.Value);
            if (!r.Ok) { Log.Error($"chest scan {_chestScans}: containers message NOT sent, it fails the contract: {Truncate(r.Reason, 400)}"); _chestStatus = "invalid"; return; }
            var outcome = _spool.Put(r.Kind, r.Key, r.CapturedAt, r.Bytes, r.BlobRefs, DateTime.UtcNow);
            _chestStatus = $"last {r.CapturedAt} ({rep.Containers} containers)";
            Log.Info($"chest scan {_chestScans}: {rep.Containers} containers ({rep.Vehicles} vehicles, {rep.Private} personal), {rep.Stacks} stacks in {rep.Entries} entries, " +
                $"{res.Dropped} dropped, {res.Duplicates} duplicates removed, {res.Gone} gone mid-scan, {res.Creator0} not player-built, {rep.Merged} merged, {rep.Unreadable} unreadable, " +
                $"{rep.OmittedShared + rep.OmittedPrivate} omitted; {ChestScanner.Ms(res.Ms)} ms over {res.Ticks} ticks (busiest {ChestScanner.Ms(res.BusiestTickMs)} ms); " +
                $"{r.Key} capturedAt={r.CapturedAt} {r.Bytes.Length} bytes → {outcome}");
            if (rep.CapsApplied)
                Log.Warn($"chest scan {_chestScans}: contract caps applied: {rep.SlotsClamped} container(s) clamped to {Caps.ContainerSlotsMax} slots, {rep.ContainersOverEntries} container(s) over {Caps.ContainerItemsMax} entries ({rep.EntriesOmitted} entries omitted), {rep.OmittedShared + rep.OmittedPrivate} container(s) dropped by the message caps");
            if (rep.InvalidStacks > 0) Log.Debug($"chest scan {_chestScans}: {rep.InvalidStacks} stack(s) without a prefab skipped");
            _unreadableStreak = rep.Unreadable > 0 ? _unreadableStreak + 1 : 0;
            var now = DateTime.UtcNow;
            if (_unreadableStreak >= 3 && now >= _unreadableWarnAt)
            {
                _unreadableWarnAt = now.AddHours(1);
                Log.Warn($"chest scan: {rep.Unreadable} container(s) could not be decoded in {_unreadableStreak} scans in a row; they are sent as unreadable (no items). A mod that changes the chest format?");
            }
        }

        private static void TickRecipes(DateTime now)
        {
            if (_recipes != null && _recipes.Running)
            {
                RecipesExport.Result result = null;
                try { result = _recipes.Tick(); }
                catch (Exception e) { Log.Error("recipes export aborted; it is tried again after the next start", e); _recipes = null; _recipesStatus = "aborted"; return; }
                if (result != null) { FinishRecipes(result); _recipes = null; }
                return;
            }
            if (now < _recipesAt) return;
            if (ObjectDB.instance == null || ZNetScene.instance == null || ObjectDB.instance.m_recipes == null) { _recipesAt = now.AddSeconds(30); return; }
            _recipesAt = DateTime.MaxValue;
            bool force = _recipesForce;
            _recipesForce = false;
            if (!force && _spool.HasRecipesExport(_gameVersion, ServerModSet()))
            {
                _recipesStatus = "sent";
                Log.Info($"recipes export: already sent for game {_gameVersion} and this server's mod set ({_serverModSet.Substring(0, 12)}…)");
                return;
            }
            _recipes = new RecipesExport();
            _recipes.Begin(now, _gameVersion);
            _recipesStatus = "exporting";
        }

        private static void FinishRecipes(RecipesExport.Result res)
        {
            var msg = RecipesBuilder.Normalise(res.Message, out var rep);
            string icons;
            var map = _pieceIcons;
            if (map == null || map.GameVersion != _gameVersion) icons = "none (no admin client with the piece atlas yet)";
            else if (!_spool.HasBlob(map.Atlas.Sha256)) icons = "none (the atlas blob is gone; the next admin client is asked again)";
            else
            {
                int applied = PieceIconMap.Apply(msg, map, out int candidates);
                icons = $"{applied} of {candidates} (atlas {map.Sha12}…)";
            }
            var r = Enricher.Seal(msg, PluginConfig.ServerId.Value);
            if (!r.Ok) { Log.Error($"recipes export NOT sent, it fails the contract: {Truncate(r.Reason, 400)}"); _recipesStatus = "invalid"; return; }
            var outcome = _spool.Put(r.Kind, r.Key, r.CapturedAt, r.Bytes, r.BlobRefs, DateTime.UtcNow);
            if (outcome != PutOutcome.DroppedOlder) _spool.MarkRecipesExport(_gameVersion, ServerModSet(), r.CapturedAt);
            _recipesStatus = "sent";
            Log.Info($"recipes export: {rep.Recipes} recipes, {rep.Pieces} pieces, {rep.Stations} stations, {rep.Processors} processors, {rep.Processes} processes, {rep.Entries} entries, " +
                $"custom formula: {rep.CustomFormula}, ids renamed: {rep.RenamedIds}, icons: {icons}; {ChestScanner.Ms(res.Ms)} ms over {res.Ticks} ticks (busiest {ChestScanner.Ms(res.BusiestTickMs)} ms); " +
                $"{r.Key} capturedAt={r.CapturedAt} {r.Bytes.Length} bytes → {outcome}");
            if (rep.Dropped)
                Log.Warn($"recipes export: dropped to fit the contract: {rep.DroppedRecipes} recipes, {rep.DroppedPieces} pieces, {rep.DroppedStations} stations, {rep.DroppedProcessors} processors (no usable conversion), {rep.DroppedProcesses} processes, {rep.DroppedResources} requirements");
            if (rep.DanglingStations > 0 || rep.SplitProcesses > 0 || rep.MergedResources > 0)
                Log.Debug($"recipes export: {rep.DanglingStations} unknown station reference(s) cleared, {rep.SplitProcesses} obliterator conversion(s) split into groups of {Caps.ProcessInputsMax}, {rep.MergedResources} duplicate requirement(s) summed");
        }

        /// <summary>The server's own mod-set hash (BepInEx plugin GUIDs + versions), the key of the recipes and piece-icon markers.</summary>
        private static string ServerModSet() => _serverModSet ?? (_serverModSet = ClientHalf.ModSetHash());

        /// <summary>A usable piece-icon map: this game version, this server's mod set, and its atlas still known to the spool.</summary>
        private static bool HavePieceIcons() =>
            _pieceIcons != null && _pieceIcons.GameVersion == _gameVersion && _pieceIcons.ServerModSet == ServerModSet() && _spool.HasBlob(_pieceIcons.Atlas.Sha256);

        private static void MaybeRequestPieceIcons(long sender, PeerFacts facts, string gameVersion, JArray features)
        {
            if (!_dedicated || !PluginConfig.RecipesEnabled.Value || features == null) return;
            bool can = false;
            foreach (var f in features) if (f is JValue v && v.Type == JTokenType.String && (string)v == "pieceIcons") can = true;
            if (!can || gameVersion != _gameVersion || !MayExport(facts) || HavePieceIcons()) return;
            var now = DateTime.UtcNow;
            if (_iconsLeasePeer != null && now < _iconsLeaseUntil) return;
            _iconsLeasePeer = sender.ToString(); _iconsLeaseUntil = now.AddMinutes(10);
            Log.Info($"peer {sender}: asking for the piece icon atlas (game {gameVersion})");
            LinkChannel.SendControl(sender, "needPieceIcons", $"game={gameVersion}");
        }

        /// <summary>Server.ItemsExportBy governs every client-rendered export: the items catalogue and the piece icon atlas.</summary>
        private static bool MayExport(PeerFacts facts)
        {
            string who = PluginConfig.ItemsExportBy.Value;
            return !(who == "nobody" || (who == "admins" && !facts.IsAdmin));
        }

        private static void OnPieceIcons(long sender, PeerFacts facts, byte[] gz)
        {
            if (!_dedicated || !PluginConfig.RecipesEnabled.Value) { Log.Debug($"peer {sender}: piece icons dropped (no recipes export here)"); return; }
            if (!MayExport(facts)) { Log.Warn($"peer {sender}: piece icons dropped, Server.ItemsExportBy does not allow this client"); return; }
            byte[] json = Inflate(gz, LinkChannel.PieceIconsMaxBytes);
            if (json == null) { Log.Warn($"peer {sender}: piece icons gzip invalid or over {LinkChannel.PieceIconsMaxBytes} bytes; dropped"); return; }
            if (!PieceIconMap.TryParse(json, out var map, out string error))
            {
                Log.Warn($"peer {sender}: piece icons rejected: {error}");
                LinkChannel.SendControl(sender, "reject", "pieceIcons: " + Truncate(error, 120));
                return;
            }
            if (map.GameVersion != _gameVersion) { Log.Warn($"peer {sender}: piece icons for game {map.GameVersion} != server {_gameVersion}; dropped"); return; }
            if (!_spool.HasBlob(map.Atlas.Sha256))
            {
                Log.Warn($"peer {sender}: piece icons reference atlas {map.Sha12}… which never arrived; dropped");
                LinkChannel.SendControl(sender, "reject", "pieceIcons: atlas blob missing");
                return;
            }
            map.ServerModSet = ServerModSet();
            _spool.SavePieceIcons(map);
            _pieceIcons = map;
            _iconsLeasePeer = null;
            Log.Info($"peer {sender}: piece icon atlas stored: {map.Icons.Count} icons ({map.Rejected} rejected), atlas {map.Sha12}… {map.Atlas.Width}x{map.Atlas.Height}; the recipes export is sent again with icons");
            LinkChannel.SendControl(sender, "ack", $"pieceIcons {map.Icons.Count}");
            // A running export picks the new map up when it finishes; otherwise send it again now.
            if (_recipes == null || !_recipes.Running) { _recipesForce = true; _recipesAt = DateTime.UtcNow; }
        }

        private static HashSet<string> CsvSet(string csv)
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            foreach (var part in (csv ?? "").Split(',')) { var t = part.Trim(); if (t.Length > 0) set.Add(t); }
            return set;
        }

        private static string Csv(string csv) { var set = CsvSet(csv); return set.Count == 0 ? "none" : string.Join(",", set); }

        // ---------- receive ----------

        private static void OnPayload(long sender, PayloadKind kind, byte[] payload)
        {
            if (!_active) return;
            if (_worldIdle)
            {
                // The client learns once, on its hello, that this world is not linked and sends nothing else this session (design §3).
                if (kind == PayloadKind.Hello) LinkChannel.SendControl(sender, "reject", $"hello: world '{_worldName}' is not linked to Raid Ledger (Server.WorldAllowlist)");
                Log.Debug($"peer {sender}: {kind} {(kind == PayloadKind.Hello ? "declined" : "dropped")}, link idle");
                return;
            }
            try
            {
                var facts = sender == GameAccess.OwnRoutedId() ? PeerIdentity.Self(sender) : PeerIdentity.Get(sender);
                if (facts == null) { Log.Warn($"peer {sender}: {kind} dropped, peer unknown (never seen connected)"); return; }

                switch (kind)
                {
                    case PayloadKind.Hello: OnHello(sender, facts, payload); return;
                    case PayloadKind.Blob: OnBlob(sender, facts, payload); return;
                    case PayloadKind.Character:
                    case PayloadKind.Map:
                    case PayloadKind.Items: OnMessage(sender, facts, kind, payload); return;
                    case PayloadKind.PieceIcons: OnPieceIcons(sender, facts, payload); return;
                }
            }
            catch (Exception e) { Log.Error($"peer {sender}: {kind} handling failed", e); }
        }

        private static void OnHello(long sender, PeerFacts facts, byte[] payload)
        {
            var hello = JObject.Parse(MessageJson.Utf8NoBom.GetString(payload));
            string gameVersion = (string)hello["gameVersion"], modSetHash = (string)hello["modSetHash"], modVersion = (string)hello["modVersion"];
            int protocol = hello["protocol"] is JValue pv && pv.Type == JTokenType.Integer ? (int)pv : -1;
            PeerIdentity.RememberHello(sender, gameVersion, modSetHash, modVersion);
            Log.Info($"peer {sender}: hello mod={modVersion} protocol={protocol} game={gameVersion} modSet={modSetHash?.Substring(0, Math.Min(12, modSetHash.Length))}… steam={(facts.SteamId ?? "none")} admin={facts.IsAdmin}");
            // What must match is the RPC protocol (the chunk format and this hello); mod versions may differ.
            if (protocol != LinkChannel.Protocol) { LinkChannel.SendControl(sender, "reject", $"protocol {protocol} != server {LinkChannel.Protocol}: update the mod"); return; }
            if (PluginConfig.RequireExactModVersion.Value && modVersion != Plugin.Version) { LinkChannel.SendControl(sender, "reject", $"modVersion {modVersion} != server {Plugin.Version}"); return; }
            if (modVersion != Plugin.Version) Log.Info($"peer {sender}: client mod {modVersion} differs from server {Plugin.Version}; same protocol, accepted");
            MaybeRequestItems(sender, facts, gameVersion, modSetHash);
            MaybeRequestPieceIcons(sender, facts, gameVersion, hello["features"] as JArray);
        }

        private static void MaybeRequestItems(long sender, PeerFacts facts, string gameVersion, string modSetHash)
        {
            if (string.IsNullOrEmpty(gameVersion) || string.IsNullOrEmpty(modSetHash)) return;
            if (_spool.HasItemsExport(gameVersion, modSetHash)) return;
            if (!MayExport(facts)) return;
            var now = DateTime.UtcNow;
            if (_itemsLeasePeer != null && now < _itemsLeaseUntil) return;
            _itemsLeasePeer = sender.ToString(); _itemsLeaseUntil = now.AddMinutes(10);
            Log.Info($"peer {sender}: asking for the items export (game {gameVersion}, mod set {modSetHash.Substring(0, Math.Min(12, modSetHash.Length))}…)");
            LinkChannel.SendControl(sender, "needItems", $"game={gameVersion}");
        }

        private static void OnBlob(long sender, PeerFacts facts, byte[] png)
        {
            if (!Contract.Png.TryReadSize(png, out int w, out int h)) { Log.Warn($"peer {sender}: blob is not a PNG; dropped"); return; }
            if (png.Length > PluginConfig.UploadMaxBlobBytes.Value) { Log.Warn($"peer {sender}: blob {png.Length} bytes > Upload.MaxBlobBytes; dropped"); return; }
            var r = Contract.Png.MakeRef(png);
            if (_spool.HasBlob(r.Sha256)) { Log.Debug($"peer {sender}: blob {r.Sha256.Substring(0, 12)}… already spooled"); return; }
            _spool.PutBlob(r.Sha256, png, w, h, DateTime.UtcNow);
            Log.Info($"peer {sender}: blob {r.Sha256.Substring(0, 12)}… {w}x{h} {png.Length} bytes spooled");
        }

        private static void OnMessage(long sender, PeerFacts facts, PayloadKind kind, byte[] gz)
        {
            string kindName = kind.ToString().ToLowerInvariant();
            int cap = Caps.BodyCapFor(kindName);
            byte[] json = Inflate(gz, cap);
            if (json == null) { Log.Warn($"peer {sender}: {kindName} gzip invalid or over {cap} bytes; dropped"); return; }
            JObject obj;
            try { obj = MessageJson.Parse(json) as JObject; } catch (Exception e) { Log.Warn($"peer {sender}: {kindName} is not JSON ({e.Message}); dropped"); return; }

            var ctx = new EnrichContext
            {
                ServerId = PluginConfig.ServerId.Value,
                SocketSteamId = facts.SteamId,
                TrustClientSteamId = PluginConfig.TrustClientSteamId.Value,
                ZdoPlayerId = facts.PlayerId,
                ServerModVersion = PluginConfig.RequireExactModVersion.Value ? Plugin.Version : null,
                NowUtc = DateTime.UtcNow,
                BaseMap = _baseMap?.Ref,
            };
            var r = Enricher.Enrich(obj, ctx);
            if (!r.Ok)
            {
                Log.Warn($"peer {sender}: {kindName} rejected: {r.Reason}");
                LinkChannel.SendControl(sender, "reject", kindName + ": " + Truncate(r.Reason, 120));
                return;
            }
            foreach (var n in r.Notes) Log.Debug($"peer {sender}: {n}");
            var outcome = _spool.Put(r.Kind, r.Key, r.CapturedAt, r.Bytes, r.BlobRefs, DateTime.UtcNow);
            Log.Info($"peer {sender}: {kindName} {r.Key} capturedAt={r.CapturedAt} {r.Bytes.Length} bytes → {outcome}{(r.BlobRefs.Count > 0 ? $" refs={r.BlobRefs.Count}" : "")}");
            if (outcome == PutOutcome.DroppedOlder) return;
            if (kind == PayloadKind.Items && facts.ModSetHash != null && facts.GameVersion != null)
            {
                _spool.MarkItemsExport(facts.GameVersion, facts.ModSetHash, r.CapturedAt);
                _itemsLeasePeer = null;
            }
            LinkChannel.SendControl(sender, "ack", $"{kindName} {r.CapturedAt}");
        }

        /// <summary>Bounded gunzip: null when the stream is bad or would exceed <paramref name="cap"/>.</summary>
        private static byte[] Inflate(byte[] gz, int cap)
        {
            try
            {
                using (var ms = new MemoryStream(gz))
                using (var z = new GZipStream(ms, CompressionMode.Decompress))
                using (var outp = new MemoryStream())
                {
                    var buf = new byte[64 * 1024];
                    int n;
                    while ((n = z.Read(buf, 0, buf.Length)) > 0)
                    {
                        if (outp.Length + n > cap) return null;
                        outp.Write(buf, 0, n);
                    }
                    return outp.ToArray();
                }
            }
            catch (Exception) { return null; }
        }

        private static string Truncate(string s, int max) => s == null ? "" : s.Length <= max ? s : s.Substring(0, max) + "…";
    }
}
