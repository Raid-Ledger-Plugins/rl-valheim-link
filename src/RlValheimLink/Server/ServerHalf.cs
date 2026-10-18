using RlValheimLink.Net;

namespace RlValheimLink.Server
{
    /// <summary>
    /// The dedicated-server half. PENDING the operator's answers to docs/design.md §9: this stub
    /// only logs its configuration state and what arrives, and uploads nothing. The receiver
    /// (enrichment, validation, dedupe, spool) and the uploader (state machine, HTTP, backoff) land
    /// in the server slice once the open questions are settled.
    /// </summary>
    internal static class ServerHalf
    {
        private static bool _active;

        public static void Start()
        {
            _active = true;
            LinkChannel.PayloadReceived -= OnPayload;
            LinkChannel.PayloadReceived += OnPayload;
            bool tokenSet = !string.IsNullOrEmpty(PluginConfig.ServerToken.Value);
            Log.Info($"server half active: serverId='{PluginConfig.ServerId.Value}' baseUrl='{PluginConfig.BaseUrl.Value}' token={(tokenSet ? "set" : "MISSING")} (uploads: NOT BUILT YET — pending design §9)");
            if (string.IsNullOrEmpty(PluginConfig.ServerId.Value) || !tokenSet || string.IsNullOrEmpty(PluginConfig.BaseUrl.Value))
                Log.Warn("RaidLedger.BaseUrl, ServerToken and ServerId must all be set in BepInEx/config/RaidLedger.ValheimLink.cfg before anything can be uploaded");
        }

        public static void Stop()
        {
            _active = false;
            LinkChannel.PayloadReceived -= OnPayload;
        }

        public static void Tick() { }

        private static void OnPayload(long sender, PayloadKind kind, byte[] payload)
        {
            if (!_active) return;
            Log.Info($"server stub: {kind} payload of {payload.Length} bytes from peer {sender} received and DROPPED (server half pending operator OK)");
        }
    }
}
