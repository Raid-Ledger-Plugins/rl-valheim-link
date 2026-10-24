namespace RlValheimLink.Contract
{
    /// <summary>
    /// Constants mirrored from contract/v1/CONTRACT.md §2–§7. The contract owns these;
    /// if they change upstream the drift check fails and this file is updated to match.
    /// </summary>
    public static class Caps
    {
        public const int SchemaVersion = 1;

        public const int CharacterMaxBytes = 2_097_152;
        public const int MapMaxBytes = 8_388_608;
        public const int ItemsMaxBytes = 12_582_912;
        public const int ContainersMaxBytes = 10_485_760;
        public const int RecipesMaxBytes = 12_582_912;
        public const int PortalsMaxBytes = 4_194_304;
        public const int BinaryMaxBytes = 16_777_216;

        public const int BitmapDataMaxChars = 3_000_000;
        public const int BitmapDataMinChars = 28;
        public const int BitmapEdgeMin = 64;
        public const int BitmapEdgeMax = 4096;

        public const int EquipmentMax = 32;
        public const int InventoryItemsMax = 512;
        public const int GridEdgeMax = 32;
        public const int VanillaRows = 4;
        public const int SkillsMax = 128;
        public const int TrophiesMax = 1024;
        public const int KnownMax = 8192;
        public const int PinsMax = 2000;
        public const int ItemExportMax = 10_000;
        public const int ItemExportMin = 1;
        public const int ContainersMax = 4096;
        public const int ContainerItemsMax = 256;
        public const int ContainerSlotsMax = 4096;
        public const int ContainerEntriesMax = 50_000;
        public const int RecipesMax = 4096;
        public const int PiecesMax = 4096;
        public const int StationsMax = 256;
        public const int ProcessorsMax = 256;
        public const int ProcessesMax = 2048;
        public const int RequirementsMax = 16;
        public const int ProcessInputsMax = 8;
        public const int RecipeEntriesMax = 12_000;
        public const int StationLevelMax = 20;
        public const int PortalsMax = 4096;
        public const int PortalTagMaxChars = 64;

        public const int TimestampMaxChars = 24;
        public const int WorldCoordMax = 32_768;
        public const int BinaryEdgeMax = 16_384;

        public const string SteamId64Min = "76561197960265728";
        public const string Int64Min = "-9223372036854775808";
        public const string Int64Max = "9223372036854775807";

        /// <summary>Raw bitset bytes for a width × height mask: ⌈w·h/8⌉.</summary>
        public static int BitmapBytes(int width, int height)
        {
            long bits = (long)width * height;
            return (int)((bits + 7) / 8);
        }

        /// <summary>Largest gzip member accepted for a mask (CONTRACT.md §4).</summary>
        public static int BitmapMaxGzipBytes(int width, int height)
        {
            int raw = BitmapBytes(width, height);
            return raw + (raw + 7) / 8 + (raw + 63) / 64 + 64;
        }

        /// <summary>Largest base64 length for a mask's data field.</summary>
        public static int BitmapMaxDataChars(int width, int height)
        {
            int gz = BitmapMaxGzipBytes(width, height);
            return 4 * ((gz + 2) / 3);
        }

        public static int BodyCapFor(string kind)
        {
            switch (kind)
            {
                case "character": return CharacterMaxBytes;
                case "map": return MapMaxBytes;
                case "items": return ItemsMaxBytes;
                case "containers": return ContainersMaxBytes;
                case "recipes": return RecipesMaxBytes;
                case "portals": return PortalsMaxBytes;
                default: return 0;
            }
        }
    }
}
