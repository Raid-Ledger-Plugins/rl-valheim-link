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
        public static ConfigEntry<bool> TrustClientSteamId, RequireExactModVersion;
        public static ConfigEntry<string> ItemsExportBy, WorldAllowlist;
        // [BaseMap]
        public static ConfigEntry<bool> BaseMapEnabled;
        public static ConfigEntry<int> BaseMapRowsPerTick, BaseMapTextureSize;
        public static ConfigEntry<float> BaseMapPixelSizeMetres;
        // [Client]
        public static ConfigEntry<bool> OptOut, SendCharacter, SendMap;
        public static ConfigEntry<int> MinSecondsBetweenCharacter, MinSecondsBetweenMap;
        // [Items]
        public static ConfigEntry<int> IconSize;
        // [Chests] — the chest scan (`containers`, docs/design-chests.md §1) and the spike's diagnostic dump (§7)
        public static ConfigEntry<bool> ChestsEnabled, ChestsIncludeVehicles, ChestsDiagnosticDump;
        public static ConfigEntry<int> ChestsIntervalMinutes, ChestsStacksPerTick;
        public static ConfigEntry<string> ChestsExcludePrefabs, ChestsExtraPrefabs;
        public static ConfigEntry<int> ChestsDumpRuns, ChestsDumpRerunMinutes;
        public static ConfigEntry<string> ChestsDumpMaterials;
        // [Recipes] — the `recipes` export (docs/design-chests.md §2)
        public static ConfigEntry<bool> RecipesEnabled;

        public static void Bind(ConfigFile cfg)
        {
            BaseUrl = cfg.Bind("RaidLedger", "BaseUrl", "", "Server only. Your Raid Ledger instance's base URL (scheme + host, no trailing slash). Empty = uploads off.");
            ServerToken = cfg.Bind("RaidLedger", "ServerToken", "", "Server only. Per-server bearer token issued by Raid Ledger admin for THIS game server. Never logged.");
            ServerId = cfg.Bind("RaidLedger", "ServerId", "", "Server only. Raid Ledger's id for this game server, issued with the token; goes into every message.");
            MessagesPath = cfg.Bind("RaidLedger", "MessagesPath", "/api/plugins/valheim/link/messages/{kind}", "Server only. POST route for messages; {kind} is replaced by character | map | items | containers | recipes (ROK-1833). Change only to point at a mock on another path.");
            BlobsPath = cfg.Bind("RaidLedger", "BlobsPath", "/api/plugins/valheim/link/blobs/{sha256}", "Server only. PUT route for PNG blobs; {sha256} is replaced (ROK-1833).");

            UploadIntervalSeconds = cfg.Bind("Upload", "IntervalSeconds", 5, new ConfigDescription("Server only. Minimum seconds between HTTP requests to Raid Ledger.", new AcceptableValueRange<int>(1, 600)));
            UploadMaxMessagesPerMinute = cfg.Bind("Upload", "MaxMessagesPerMinute", 30, new ConfigDescription("Server only. Self-imposed message POSTs per minute; Raid Ledger allows 60 per server before 429.", new AcceptableValueRange<int>(1, 60)));
            UploadMaxBlobsPerMinute = cfg.Bind("Upload", "MaxBlobsPerMinute", 10, new ConfigDescription("Server only. Self-imposed blob PUTs per minute; Raid Ledger allows 20 per server before 429.", new AcceptableValueRange<int>(1, 20)));
            UploadMaxBlobBytes = cfg.Bind("Upload", "MaxBlobBytes", 10 * 1024 * 1024, new ConfigDescription("Largest PNG the mod will upload (bytes). 10 MiB while Raid Ledger's nginx caps bodies there (ROK-1850); the contract allows 16 MiB. Also bounds the client's atlas.", new AcceptableValueRange<int>(1024 * 1024, 16 * 1024 * 1024)));
            UploadGzip = cfg.Bind("Upload", "Gzip", true, "Server only. Send message bodies with Content-Encoding: gzip.");
            SpoolDirectory = cfg.Bind("Upload", "SpoolDirectory", "", "Server only. Pending uploads + blob cache. Empty = <BepInEx>/RLValheimLink/spool.");

            TrustClientSteamId = cfg.Bind("Server", "TrustClientSteamId", false, "Server only. Accept the client's steamId claim when the peer's socket identity is not a SteamID64 (crossplay/PlayFab). Off = such peers are skipped.");
            RequireExactModVersion = cfg.Bind("Server", "RequireExactModVersion", false, "Server only. Reject clients whose mod version differs from the server's. Off (default): any client speaking the same RPC protocol is accepted and its version is logged, so server-side updates never force a modpack update.");
            ItemsExportBy = cfg.Bind("Server", "ItemsExportBy", "admins", new ConfigDescription("Server only. Which clients may produce the items export when asked: admins | anyone | nobody.", new AcceptableValueList<string>("admins", "anyone", "nobody")));
            WorldAllowlist = cfg.Bind("Server", "WorldAllowlist", "", "Server only. Comma-separated world names (case-insensitive) this server links to Raid Ledger; empty = every world. When the loaded world is not listed the link stays idle: no base map, clients are told the world is not linked, nothing is spooled or uploaded. Set it to your production world when the same server is sometimes booted into a test world.");

            BaseMapEnabled = cfg.Bind("BaseMap", "Enabled", true, "Server only. Render the world's base map PNG once per (world, game version).");
            BaseMapRowsPerTick = cfg.Bind("BaseMap", "RowsPerTick", 4, new ConfigDescription("Server only. Rows rendered per Update tick (time-slicing; 2048 rows at 4/tick ≈ 10 s of ticks).", new AcceptableValueRange<int>(1, 256)));
            BaseMapTextureSize = cfg.Bind("BaseMap", "TextureSize", 2048, new ConfigDescription("Server only. Edge of the rendered base map in pixels; must equal the clients' minimap textureSize (vanilla 2048) or the map is sent without a base map.", new AcceptableValueRange<int>(64, 4096)));
            BaseMapPixelSizeMetres = cfg.Bind("BaseMap", "PixelSizeMetres", 12f, new ConfigDescription("Server only. World metres per base-map pixel; must equal the clients' minimap pixel size (vanilla 12).", new AcceptableValueRange<float>(0.5f, 256f)));

            OptOut = cfg.Bind("Client", "OptOut", false, "Client only. PER-PLAYER OPT-OUT: when true this game client sends nothing to the server (no character, map or items data).");
            SendCharacter = cfg.Bind("Client", "SendCharacter", true, "Client only. Send the character summary on save/logout/quit.");
            SendMap = cfg.Bind("Client", "SendMap", true, "Client only. Send explored map + pins on save/logout/quit when they changed.");
            MinSecondsBetweenCharacter = cfg.Bind("Client", "MinSecondsBetweenCharacter", 30, new ConfigDescription("Client only. Throttle for character sends.", new AcceptableValueRange<int>(5, 3600)));
            MinSecondsBetweenMap = cfg.Bind("Client", "MinSecondsBetweenMap", 300, new ConfigDescription("Client only. Throttle for map sends.", new AcceptableValueRange<int>(30, 86400)));

            IconSize = cfg.Bind("Items", "IconSize", 64, new ConfigDescription("Client only. Icon edge in pixels for the items atlas (reduced automatically if the PNG would exceed 16 MiB).", new AcceptableValueRange<int>(16, 128)));

            ChestsEnabled = cfg.Bind("Chests", "Enabled", true, "Dedicated server only. Scan every player-built container (chests, carts, ships) and send its contents to Raid Ledger as a `containers` snapshot: 2 minutes after start, then every IntervalMinutes. On by default; set false to switch it off. Needs the [RaidLedger] settings and a world in [Server] WorldAllowlist.");
            ChestsIntervalMinutes = cfg.Bind("Chests", "IntervalMinutes", 10, new ConfigDescription("Dedicated server only. Minutes between chest scans, on a fixed grid from the first scan. Every scan is sent, even when nothing changed, so the site's \"scanned N min ago\" is honest.", new AcceptableValueRange<int>(5, 120)));
            ChestsStacksPerTick = cfg.Bind("Chests", "StacksPerTick", 200, new ConfigDescription("Dedicated server only. Item stacks decoded per server tick while a scan runs (time-slicing; 0.004 ms per stack measured on a production world).", new AcceptableValueRange<int>(20, 5000)));
            ChestsIncludeVehicles = cfg.Bind("Chests", "IncludeVehicles", true, "Dedicated server only. Also scan carts and ships (flagged as vehicles; the site can hide them).");
            ChestsExcludePrefabs = cfg.Bind("Chests", "ExcludePrefabs", "incinerator", "Dedicated server only. Comma-separated container prefabs never scanned. Default: the obliterator, whose contents are about to be destroyed.");
            ChestsExtraPrefabs = cfg.Bind("Chests", "ExtraPrefabs", "", "Dedicated server only. Comma-separated container prefabs to scan although no build tool lists them (a modded chest placed some other way). Player-built only: a container without a creator is never sent.");
            ChestsDiagnosticDump = cfg.Bind("Chests", "DiagnosticDump", false, "Dedicated server only. The 0.2.x chest-inventory spike's diagnostic (docs/design-chests.md §7): 2 minutes after every start, log what the server can read from every container (lines start 'RLVL: chestdump'). Logs only. Off by default now that the chest scan ships; replaces Dump, which is ignored.");
            ChestsDumpRuns = cfg.Bind("Chests", "DumpRuns", 2, new ConfigDescription("Dedicated server only. How many dumps to run after each start when DiagnosticDump is on; every dump after the first also reports what changed since the previous one.", new AcceptableValueRange<int>(1, 6)));
            ChestsDumpRerunMinutes = cfg.Bind("Chests", "DumpRerunMinutes", 10, new ConfigDescription("Dedicated server only. Minutes between dumps (from the end of one to the start of the next).", new AcceptableValueRange<int>(1, 120)));
            ChestsDumpMaterials = cfg.Bind("Chests", "DumpMaterials", "Wood,Stone,Resin", "Dedicated server only. Comma-separated item prefab names (up to 5) whose totals over all player-built containers each dump prints, to compare with what you see in the chests.");

            RecipesEnabled = cfg.Bind("Recipes", "Enabled", true, "Dedicated server only. Send the server's crafting recipes, build pieces, crafting stations and processing steps (smelter, kiln, fermenter, cooking, obliterator…) to Raid Ledger as a `recipes` snapshot, once per game version and server mod set, about 90 s after start. On by default; set false to switch it off.");
        }
    }
}
