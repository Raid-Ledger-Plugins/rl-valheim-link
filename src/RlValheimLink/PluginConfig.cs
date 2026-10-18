using BepInEx.Configuration;

namespace RlValheimLink
{
    /// <summary>BepInEx/config/RaidLedger.ValheimLink.cfg — see docs/design.md §8.</summary>
    internal static class PluginConfig
    {
        // [RaidLedger] — server side
        public static ConfigEntry<string> BaseUrl, ServerToken, ServerId, MessagesPath, BlobsPath;
        // [Upload]
        public static ConfigEntry<int> UploadIntervalSeconds, UploadMaxMessagesPerMinute, UploadMaxBlobsPerMinute, UploadMaxBlobBytes;
        public static ConfigEntry<bool> UploadGzip;
        public static ConfigEntry<string> SpoolDirectory;
        // [Server]
        public static ConfigEntry<bool> TrustClientSteamId;
        public static ConfigEntry<string> ItemsExportBy;
        // [BaseMap]
        public static ConfigEntry<bool> BaseMapEnabled;
        public static ConfigEntry<int> BaseMapRowsPerTick;
        // [Client]
        public static ConfigEntry<bool> OptOut, SendCharacter, SendMap;
        public static ConfigEntry<int> MinSecondsBetweenCharacter, MinSecondsBetweenMap;
        // [Items]
        public static ConfigEntry<int> IconSize;

        public static void Bind(ConfigFile cfg)
        {
            BaseUrl = cfg.Bind("RaidLedger", "BaseUrl", "", "Server only. Raid Ledger base URL, e.g. https://raid.gamernight.net (no trailing slash). Empty = uploads off.");
            ServerToken = cfg.Bind("RaidLedger", "ServerToken", "", "Server only. Per-server bearer token issued by Raid Ledger admin for THIS game server. Never logged.");
            ServerId = cfg.Bind("RaidLedger", "ServerId", "", "Server only. Raid Ledger's id for this game server, issued with the token; goes into every message.");
            MessagesPath = cfg.Bind("RaidLedger", "MessagesPath", "/api/plugins/valheim/link/messages/{kind}", "Server only. POST route for messages; {kind} is replaced by character | map | items (ROK-1833). Change only to point at a mock on another path.");
            BlobsPath = cfg.Bind("RaidLedger", "BlobsPath", "/api/plugins/valheim/link/blobs/{sha256}", "Server only. PUT route for PNG blobs; {sha256} is replaced (ROK-1833).");

            UploadIntervalSeconds = cfg.Bind("Upload", "IntervalSeconds", 5, new ConfigDescription("Server only. Minimum seconds between HTTP requests to Raid Ledger.", new AcceptableValueRange<int>(1, 600)));
            UploadMaxMessagesPerMinute = cfg.Bind("Upload", "MaxMessagesPerMinute", 30, new ConfigDescription("Server only. Self-imposed message POSTs per minute; Raid Ledger allows 60 per server before 429.", new AcceptableValueRange<int>(1, 60)));
            UploadMaxBlobsPerMinute = cfg.Bind("Upload", "MaxBlobsPerMinute", 10, new ConfigDescription("Server only. Self-imposed blob PUTs per minute; Raid Ledger allows 20 per server before 429.", new AcceptableValueRange<int>(1, 20)));
            UploadMaxBlobBytes = cfg.Bind("Upload", "MaxBlobBytes", 10 * 1024 * 1024, new ConfigDescription("Largest PNG the mod will upload (bytes). 10 MiB while Raid Ledger's nginx caps bodies there (ROK-1850); the contract allows 16 MiB. Also bounds the client's atlas.", new AcceptableValueRange<int>(1024 * 1024, 16 * 1024 * 1024)));
            UploadGzip = cfg.Bind("Upload", "Gzip", true, "Server only. Send message bodies with Content-Encoding: gzip.");
            SpoolDirectory = cfg.Bind("Upload", "SpoolDirectory", "", "Server only. Pending uploads + blob cache. Empty = <BepInEx>/RLValheimLink/spool.");

            TrustClientSteamId = cfg.Bind("Server", "TrustClientSteamId", false, "Server only. Accept the client's steamId claim when the peer's socket identity is not a SteamID64 (crossplay/PlayFab). Off = such peers are skipped.");
            ItemsExportBy = cfg.Bind("Server", "ItemsExportBy", "admins", new ConfigDescription("Server only. Which clients may produce the items export when asked: admins | anyone | nobody.", new AcceptableValueList<string>("admins", "anyone", "nobody")));

            BaseMapEnabled = cfg.Bind("BaseMap", "Enabled", true, "Server only. Render the world's base map PNG once per (world, game version).");
            BaseMapRowsPerTick = cfg.Bind("BaseMap", "RowsPerTick", 8, new ConfigDescription("Server only. Rows rendered per Update tick (time-slicing).", new AcceptableValueRange<int>(1, 256)));

            OptOut = cfg.Bind("Client", "OptOut", false, "Client only. PER-PLAYER OPT-OUT: when true this game client sends nothing to the server (no character, map or items data).");
            SendCharacter = cfg.Bind("Client", "SendCharacter", true, "Client only. Send the character summary on save/logout/quit.");
            SendMap = cfg.Bind("Client", "SendMap", true, "Client only. Send explored map + pins on save/logout/quit when they changed.");
            MinSecondsBetweenCharacter = cfg.Bind("Client", "MinSecondsBetweenCharacter", 30, new ConfigDescription("Client only. Throttle for character sends.", new AcceptableValueRange<int>(5, 3600)));
            MinSecondsBetweenMap = cfg.Bind("Client", "MinSecondsBetweenMap", 300, new ConfigDescription("Client only. Throttle for map sends.", new AcceptableValueRange<int>(30, 86400)));

            IconSize = cfg.Bind("Items", "IconSize", 64, new ConfigDescription("Client only. Icon edge in pixels for the items atlas (reduced automatically if the PNG would exceed 16 MiB).", new AcceptableValueRange<int>(16, 128)));
        }
    }
}
