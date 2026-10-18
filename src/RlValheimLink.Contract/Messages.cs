using System.Collections.Generic;
using Newtonsoft.Json;

// Message model for contract/v1 (CONTRACT.md §2–§5). Property order is the
// emit order. Every class maps 1:1 to a strict Zod object; there are no extra
// properties, because an unknown key rejects the whole message upstream.
namespace RlValheimLink.Contract
{
    public abstract class LinkMessage
    {
        [JsonProperty("schemaVersion", Order = 0)] public int SchemaVersion { get; set; } = Caps.SchemaVersion;
        [JsonProperty("serverId", Order = 1)] public string ServerId { get; set; }
        [JsonProperty("capturedAt", Order = 2)] public string CapturedAt { get; set; }
        [JsonProperty("gameVersion", Order = 3)] public string GameVersion { get; set; }
        [JsonProperty("modVersion", Order = 4)] public string ModVersion { get; set; }
        [JsonProperty("kind", Order = 5)] public abstract string Kind { get; }
    }

    // ---------- character ----------

    public sealed class CharacterMessage : LinkMessage
    {
        public override string Kind => "character";
        [JsonProperty("character", Order = 10)] public CharacterIdentity Character { get; set; } = new CharacterIdentity();
        [JsonProperty("equipment", Order = 11)] public List<EquippedItem> Equipment { get; set; } = new List<EquippedItem>();
        [JsonProperty("inventory", Order = 12)] public Inventory Inventory { get; set; } = new Inventory();
        [JsonProperty("skills", Order = 13)] public List<Skill> Skills { get; set; } = new List<Skill>();
        [JsonProperty("trophies", Order = 14)] public List<string> Trophies { get; set; } = new List<string>();
        [JsonProperty("knownItems", Order = 15)] public List<string> KnownItems { get; set; } = new List<string>();
        [JsonProperty("knownRecipes", Order = 16)] public List<string> KnownRecipes { get; set; } = new List<string>();
    }

    public sealed class CharacterIdentity
    {
        [JsonProperty("steamId", Order = 0)] public string SteamId { get; set; }
        [JsonProperty("playerId", Order = 1)] public string PlayerId { get; set; }
        [JsonProperty("name", Order = 2)] public string Name { get; set; }
        [JsonProperty("worldUid", Order = 3)] public string WorldUid { get; set; }
        [JsonProperty("worldName", Order = 4)] public string WorldName { get; set; }
    }

    public sealed class ItemStack
    {
        [JsonProperty("prefab", Order = 0)] public string Prefab { get; set; }
        [JsonProperty("stack", Order = 1)] public int Stack { get; set; }
        [JsonProperty("quality", Order = 2)] public int Quality { get; set; }
        [JsonProperty("variant", Order = 3)] public int Variant { get; set; }
        [JsonProperty("durability", Order = 4)] public double? Durability { get; set; }
        [JsonProperty("maxDurability", Order = 5)] public double? MaxDurability { get; set; }
    }

    public static class EquipSlot
    {
        public const string Head = "head", Chest = "chest", Legs = "legs", Shoulder = "shoulder",
            Utility = "utility", Trinket = "trinket", RightHand = "rightHand", LeftHand = "leftHand", Ammo = "ammo";
        public static readonly string[] All = { Head, Chest, Legs, Shoulder, Utility, Trinket, RightHand, LeftHand, Ammo };
    }

    public sealed class EquippedItem
    {
        [JsonProperty("slot", Order = 0)] public string Slot { get; set; }
        [JsonProperty("item", Order = 1)] public ItemStack Item { get; set; }
    }

    public static class RowPurpose
    {
        public const string Equipment = "equipment", Quickslot = "quickslot", Extended = "extended", Other = "other";
        public static readonly string[] All = { Equipment, Quickslot, Extended, Other };
    }

    public sealed class GridExtraRow
    {
        [JsonProperty("y", Order = 0)] public int Y { get; set; }
        [JsonProperty("purpose", Order = 1)] public string Purpose { get; set; }
    }

    public sealed class InventoryGrid
    {
        [JsonProperty("width", Order = 0)] public int Width { get; set; }
        [JsonProperty("height", Order = 1)] public int Height { get; set; }
        [JsonProperty("extraRows", Order = 2)] public List<GridExtraRow> ExtraRows { get; set; } = new List<GridExtraRow>();
    }

    public sealed class InventorySlot
    {
        [JsonProperty("x", Order = 0)] public int X { get; set; }
        [JsonProperty("y", Order = 1)] public int Y { get; set; }
        [JsonProperty("equipped", Order = 2)] public bool Equipped { get; set; }
        [JsonProperty("item", Order = 3)] public ItemStack Item { get; set; }
    }

    public sealed class Inventory
    {
        [JsonProperty("grid", Order = 0)] public InventoryGrid Grid { get; set; } = new InventoryGrid();
        [JsonProperty("items", Order = 1)] public List<InventorySlot> Items { get; set; } = new List<InventorySlot>();
    }

    public sealed class Skill
    {
        [JsonProperty("id", Order = 0)] public string Id { get; set; }
        [JsonProperty("level", Order = 1)] public double Level { get; set; }
        [JsonProperty("accumulator", Order = 2)] public double Accumulator { get; set; }
    }

    // ---------- map ----------

    public sealed class MapMessage : LinkMessage
    {
        public override string Kind => "map";
        [JsonProperty("character", Order = 10)] public MapCharacterRef Character { get; set; } = new MapCharacterRef();
        [JsonProperty("grid", Order = 11)] public MapGrid Grid { get; set; } = new MapGrid();
        [JsonProperty("explored", Order = 12)] public ExploredBitmap Explored { get; set; }
        [JsonProperty("sharedExplored", Order = 13)] public ExploredBitmap SharedExplored { get; set; }
        [JsonProperty("pins", Order = 14)] public List<MapPin> Pins { get; set; } = new List<MapPin>();
        [JsonProperty("baseMap", Order = 15)] public BinaryRef BaseMap { get; set; }
    }

    public sealed class MapCharacterRef
    {
        [JsonProperty("steamId", Order = 0)] public string SteamId { get; set; }
        [JsonProperty("playerId", Order = 1)] public string PlayerId { get; set; }
        [JsonProperty("worldUid", Order = 2)] public string WorldUid { get; set; }
    }

    public sealed class MapGrid
    {
        [JsonProperty("textureSize", Order = 0)] public int TextureSize { get; set; }
        [JsonProperty("pixelSizeMetres", Order = 1)] public double PixelSizeMetres { get; set; }
    }

    public sealed class ExploredBitmap
    {
        public const string EncodingName = "gzip+base64-bitset-lsb0";
        [JsonProperty("encoding", Order = 0)] public string Encoding { get; set; } = EncodingName;
        [JsonProperty("width", Order = 1)] public int Width { get; set; }
        [JsonProperty("height", Order = 2)] public int Height { get; set; }
        [JsonProperty("data", Order = 3)] public string Data { get; set; }
    }

    public sealed class MapPin
    {
        [JsonProperty("type", Order = 0)] public string Type { get; set; }
        [JsonProperty("label", Order = 1)] public string Label { get; set; } = "";
        [JsonProperty("x", Order = 2)] public double X { get; set; }
        [JsonProperty("z", Order = 3)] public double Z { get; set; }
        [JsonProperty("checked", Order = 4)] public bool Checked { get; set; }
        [JsonProperty("shared", Order = 5)] public bool Shared { get; set; }
    }

    public sealed class BinaryRef
    {
        [JsonProperty("sha256", Order = 0)] public string Sha256 { get; set; }
        [JsonProperty("byteLength", Order = 1)] public int ByteLength { get; set; }
        [JsonProperty("contentType", Order = 2)] public string ContentType { get; set; } = "image/png";
        [JsonProperty("width", Order = 3)] public int Width { get; set; }
        [JsonProperty("height", Order = 4)] public int Height { get; set; }
    }

    // ---------- items ----------

    public sealed class ItemsMessage : LinkMessage
    {
        public override string Kind => "items";
        [JsonProperty("locale", Order = 10)] public string Locale { get; set; }
        [JsonProperty("atlas", Order = 11)] public BinaryRef Atlas { get; set; }
        [JsonProperty("items", Order = 12)] public List<ItemDefinition> Items { get; set; } = new List<ItemDefinition>();
    }

    public sealed class ItemDefinition
    {
        [JsonProperty("prefab", Order = 0)] public string Prefab { get; set; }
        [JsonProperty("name", Order = 1)] public string Name { get; set; }
        [JsonProperty("category", Order = 2)] public string Category { get; set; }
        [JsonProperty("maxStack", Order = 3)] public int MaxStack { get; set; }
        [JsonProperty("maxQuality", Order = 4)] public int MaxQuality { get; set; }
        [JsonProperty("variants", Order = 5)] public int Variants { get; set; }
        [JsonProperty("icon", Order = 6)] public ItemIcon Icon { get; set; }
    }

    /// <summary>Either an atlas rect (kind "atlas") or a file name (kind "file"). Null means no icon.</summary>
    public sealed class ItemIcon
    {
        [JsonProperty("kind", Order = 0)] public string Kind { get; set; }
        [JsonProperty("x", Order = 1, NullValueHandling = NullValueHandling.Ignore)] public int? X { get; set; }
        [JsonProperty("y", Order = 2, NullValueHandling = NullValueHandling.Ignore)] public int? Y { get; set; }
        [JsonProperty("w", Order = 3, NullValueHandling = NullValueHandling.Ignore)] public int? W { get; set; }
        [JsonProperty("h", Order = 4, NullValueHandling = NullValueHandling.Ignore)] public int? H { get; set; }
        [JsonProperty("file", Order = 5, NullValueHandling = NullValueHandling.Ignore)] public string File { get; set; }

        public static ItemIcon Atlas(int x, int y, int w, int h) => new ItemIcon { Kind = "atlas", X = x, Y = y, W = w, H = h };
        public static ItemIcon FromFile(string file) => new ItemIcon { Kind = "file", File = file };
    }
}
