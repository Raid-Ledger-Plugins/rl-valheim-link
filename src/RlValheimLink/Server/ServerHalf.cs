using System;
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
    /// base map once per world; ask one client for the items export per (game version, mod set).
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
        private static string _worldUid, _gameVersion;

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
                _baseMap = PluginConfig.BaseMapEnabled.Value
                    ? new BaseMapRenderer(Path.Combine(SpoolDirectory, "basemap"), PluginConfig.BaseMapTextureSize.Value, PluginConfig.BaseMapPixelSizeMetres.Value, PluginConfig.BaseMapRowsPerTick.Value)
                    : null;
                _baseMapRetryAt = DateTime.UtcNow.AddSeconds(15);
            }
            catch (Exception e) { Log.Error("server half failed to start", e); _active = false; return; }

            Log.Info($"server half active: serverId='{PluginConfig.ServerId.Value}' baseUrl='{PluginConfig.BaseUrl.Value}' token={(tokenSet ? "set" : "MISSING")} world={_worldUid} game={_gameVersion} spool={SpoolDirectory} ({_spool.Summary()})");
            if (!configured) Log.Warn("RaidLedger.BaseUrl, ServerToken and ServerId must all be set in BepInEx/config/RaidLedger.ValheimLink.cfg; messages are spooled but NOT uploaded until then");
        }

        public static void Stop()
        {
            _active = false;
            LinkChannel.PayloadReceived -= OnPayload;
            _client?.Dispose(); _client = null; _uploader = null; _spool = null; _baseMap = null;
        }

        public static void Tick()
        {
            if (!_active) return;
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
            : $"uploader={_uploader?.State} {_spool?.Summary()} uploaded={_uploader?.Uploaded} failed={_uploader?.Failed} baseMap={(_baseMap == null ? "off" : _baseMap.Done ? "ready" : _baseMap.Busy ? "rendering" : "pending")}";

        // ---------- receive ----------

        private static void OnPayload(long sender, PayloadKind kind, byte[] payload)
        {
            if (!_active) return;
            try
            {
                var facts = sender == ZRoutedRpc.instance?.m_id ? PeerIdentity.Self(sender) : PeerIdentity.Get(sender);
                if (facts == null) { Log.Warn($"peer {sender}: {kind} dropped, peer unknown (never seen connected)"); return; }

                switch (kind)
                {
                    case PayloadKind.Hello: OnHello(sender, facts, payload); return;
                    case PayloadKind.Blob: OnBlob(sender, facts, payload); return;
                    case PayloadKind.Character:
                    case PayloadKind.Map:
                    case PayloadKind.Items: OnMessage(sender, facts, kind, payload); return;
                }
            }
            catch (Exception e) { Log.Error($"peer {sender}: {kind} handling failed", e); }
        }

        private static void OnHello(long sender, PeerFacts facts, byte[] payload)
        {
            var hello = JObject.Parse(MessageJson.Utf8NoBom.GetString(payload));
            string gameVersion = (string)hello["gameVersion"], modSetHash = (string)hello["modSetHash"], modVersion = (string)hello["modVersion"];
            PeerIdentity.RememberHello(sender, gameVersion, modSetHash);
            Log.Info($"peer {sender}: hello mod={modVersion} game={gameVersion} modSet={modSetHash?.Substring(0, Math.Min(12, modSetHash.Length))}… steam={(facts.SteamId ?? "none")} admin={facts.IsAdmin}");
            if (modVersion != Plugin.Version) { LinkChannel.SendControl(sender, "reject", $"modVersion {modVersion} != server {Plugin.Version}"); return; }
            MaybeRequestItems(sender, facts, gameVersion, modSetHash);
        }

        private static void MaybeRequestItems(long sender, PeerFacts facts, string gameVersion, string modSetHash)
        {
            if (string.IsNullOrEmpty(gameVersion) || string.IsNullOrEmpty(modSetHash)) return;
            if (_spool.HasItemsExport(gameVersion, modSetHash)) return;
            string who = PluginConfig.ItemsExportBy.Value;
            if (who == "nobody" || (who == "admins" && !facts.IsAdmin)) return;
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
                ServerModVersion = Plugin.Version,
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
