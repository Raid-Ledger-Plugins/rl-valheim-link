using System.Collections.Generic;
using Newtonsoft.Json;

// Message model for contract/v1 (CONTRACT.md §2–§5b). Property order is the
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
        /// <summary>Optional on a file icon (additive, contract 2026-10-18): the uploaded PNG's binary ref. Omitted when null; the server then leaves the icon unresolved.</summary>
        [JsonProperty("blob", Order = 6, NullValueHandling = NullValueHandling.Ignore)] public BinaryRef Blob { get; set; }

        public static ItemIcon Atlas(int x, int y, int w, int h) => new ItemIcon { Kind = "atlas", X = x, Y = y, W = w, H = h };
        public static ItemIcon FromFile(string file, BinaryRef blob = null) => new ItemIcon { Kind = "file", File = file, Blob = blob };
    }

    // ---------- containers (CONTRACT.md §5a) ----------

    public sealed class ContainersMessage : LinkMessage
    {
        public override string Kind => "containers";
        [JsonProperty("worldUid", Order = 10)] public string WorldUid { get; set; }
        [JsonProperty("containers", Order = 11)] public List<ContainerSnapshot> Containers { get; set; } = new List<ContainerSnapshot>();
        [JsonProperty("omittedShared", Order = 12)] public int OmittedShared { get; set; }
        [JsonProperty("omittedPrivate", Order = 13)] public int OmittedPrivate { get; set; }
    }

    public static class ContainerPrivacy
    {
        public const string Public = "public", Private = "private", Group = "group";
        public static readonly string[] All = { Public, Private, Group };
    }

    public sealed class ContainerSnapshot
    {
        [JsonProperty("prefab", Order = 0)] public string Prefab { get; set; }
        [JsonProperty("x", Order = 1)] public double X { get; set; }
        [JsonProperty("y", Order = 2)] public double Y { get; set; }
        [JsonProperty("z", Order = 3)] public double Z { get; set; }
        [JsonProperty("creatorPlayerId", Order = 4)] public string CreatorPlayerId { get; set; }
        [JsonProperty("privacy", Order = 5)] public string Privacy { get; set; } = ContainerPrivacy.Public;
        [JsonProperty("vehicle", Order = 6)] public bool Vehicle { get; set; }
        [JsonProperty("slots", Order = 7)] public int Slots { get; set; }
        [JsonProperty("usedSlots", Order = 8)] public int UsedSlots { get; set; }
        [JsonProperty("unreadable", Order = 9)] public bool Unreadable { get; set; }
        [JsonProperty("omittedEntries", Order = 10)] public int OmittedEntries { get; set; }
        [JsonProperty("items", Order = 11)] public List<ContainerStack> Items { get; set; } = new List<ContainerStack>();
    }

    public sealed class ContainerStack
    {
        [JsonProperty("prefab", Order = 0)] public string Prefab { get; set; }
        [JsonProperty("quality", Order = 1)] public int Quality { get; set; }
        [JsonProperty("stack", Order = 2)] public int Stack { get; set; }
    }

    // ---------- recipes (CONTRACT.md §5b) ----------

    public sealed class RecipesMessage : LinkMessage
    {
        public override string Kind => "recipes";
        [JsonProperty("locale", Order = 10)] public string Locale { get; set; }
        [JsonProperty("atlas", Order = 11)] public BinaryRef Atlas { get; set; }
        [JsonProperty("stations", Order = 12)] public List<CraftingStationDef> Stations { get; set; } = new List<CraftingStationDef>();
        [JsonProperty("recipes", Order = 13)] public List<RecipeDef> Recipes { get; set; } = new List<RecipeDef>();
        [JsonProperty("pieces", Order = 14)] public List<PieceDef> Pieces { get; set; } = new List<PieceDef>();
        [JsonProperty("processors", Order = 15)] public List<ProcessorDef> Processors { get; set; } = new List<ProcessorDef>();
        [JsonProperty("processes", Order = 16)] public List<ProcessDef> Processes { get; set; } = new List<ProcessDef>();
    }

    /// <summary>A recipes-kind icon: atlas rect only (CONTRACT.md §5b). Null means no icon.</summary>
    public sealed class AtlasIcon
    {
        [JsonProperty("kind", Order = 0)] public string Kind { get; set; } = "atlas";
        [JsonProperty("x", Order = 1)] public int X { get; set; }
        [JsonProperty("y", Order = 2)] public int Y { get; set; }
        [JsonProperty("w", Order = 3)] public int W { get; set; }
        [JsonProperty("h", Order = 4)] public int H { get; set; }
    }

    public sealed class CraftingStationDef
    {
        [JsonProperty("prefab", Order = 0)] public string Prefab { get; set; }
        [JsonProperty("name", Order = 1)] public string Name { get; set; }
        [JsonProperty("maxLevel", Order = 2)] public int MaxLevel { get; set; } = 1;
        [JsonProperty("stackableExtension", Order = 3)] public bool StackableExtension { get; set; }
        /// <summary>1.0.20 CraftingStation.m_upgrader; optional (omitted when null), never sent as null.</summary>
        [JsonProperty("upgrader", Order = 4, NullValueHandling = NullValueHandling.Ignore)] public bool? Upgrader { get; set; }
        [JsonProperty("icon", Order = 5)] public AtlasIcon Icon { get; set; }
    }

    public static class RecipeFormula
    {
        public const string Vanilla = "vanilla", Custom = "custom";
    }

    public sealed class RecipeDef
    {
        [JsonProperty("id", Order = 0)] public string Id { get; set; }
        [JsonProperty("result", Order = 1)] public string Result { get; set; }
        [JsonProperty("amount", Order = 2)] public int Amount { get; set; } = 1;
        [JsonProperty("enabled", Order = 3)] public bool Enabled { get; set; } = true;
        [JsonProperty("station", Order = 4)] public string Station { get; set; }
        [JsonProperty("repairStation", Order = 5)] public string RepairStation { get; set; }
        [JsonProperty("minStationLevel", Order = 6)] public int MinStationLevel { get; set; }
        [JsonProperty("upgradeOnly", Order = 7)] public bool UpgradeOnly { get; set; }
        [JsonProperty("anyOneOf", Order = 8)] public bool AnyOneOf { get; set; }
        [JsonProperty("qualityResultAmountMultiplier", Order = 9)] public double QualityResultAmountMultiplier { get; set; } = 1;
        [JsonProperty("formula", Order = 10)] public string Formula { get; set; } = RecipeFormula.Vanilla;
        [JsonProperty("resources", Order = 11)] public List<RecipeResource> Resources { get; set; } = new List<RecipeResource>();
    }

    public sealed class RecipeResource
    {
        [JsonProperty("prefab", Order = 0)] public string Prefab { get; set; }
        [JsonProperty("amount", Order = 1)] public int Amount { get; set; }
        [JsonProperty("amountPerLevel", Order = 2)] public int AmountPerLevel { get; set; }
        [JsonProperty("extraAmountOnlyOneIngredient", Order = 3)] public int ExtraAmountOnlyOneIngredient { get; set; }
        [JsonProperty("recover", Order = 4)] public bool Recover { get; set; }
        /// <summary>1.0.20 Piece.Requirement.m_upgraderResource; optional (omitted when null), never sent as null.</summary>
        [JsonProperty("upgraderResource", Order = 5, NullValueHandling = NullValueHandling.Ignore)] public bool? UpgraderResource { get; set; }
    }

    public sealed class PieceDef
    {
        [JsonProperty("prefab", Order = 0)] public string Prefab { get; set; }
        [JsonProperty("name", Order = 1)] public string Name { get; set; }
        [JsonProperty("category", Order = 2)] public string Category { get; set; }
        [JsonProperty("tool", Order = 3)] public string Tool { get; set; }
        [JsonProperty("station", Order = 4)] public string Station { get; set; }
        [JsonProperty("icon", Order = 5)] public AtlasIcon Icon { get; set; }
        [JsonProperty("resources", Order = 6)] public List<PieceResource> Resources { get; set; } = new List<PieceResource>();
    }

    public sealed class PieceResource
    {
        [JsonProperty("prefab", Order = 0)] public string Prefab { get; set; }
        [JsonProperty("amount", Order = 1)] public int Amount { get; set; }
        [JsonProperty("recover", Order = 2)] public bool Recover { get; set; }
    }

    public static class ProcessorType
    {
        public const string Smelter = "smelter", Fermenter = "fermenter", Cooking = "cooking", Incinerator = "incinerator";
    }

    public sealed class ProcessorDef
    {
        [JsonProperty("prefab", Order = 0)] public string Prefab { get; set; }
        [JsonProperty("type", Order = 1)] public string Type { get; set; }
        [JsonProperty("fuel", Order = 2)] public string Fuel { get; set; }
        [JsonProperty("fuelPerProduct", Order = 3)] public int? FuelPerProduct { get; set; }
        [JsonProperty("fuelSecondsPerUnit", Order = 4)] public double? FuelSecondsPerUnit { get; set; }
        [JsonProperty("windPowered", Order = 5)] public bool WindPowered { get; set; }
        [JsonProperty("requiresRoof", Order = 6)] public bool RequiresRoof { get; set; }
        [JsonProperty("maxInput", Order = 7)] public int? MaxInput { get; set; }
        [JsonProperty("burntResult", Order = 8)] public string BurntResult { get; set; }
        [JsonProperty("defaultResult", Order = 9)] public ProcessorDefaultResult DefaultResult { get; set; }
    }

    public sealed class ProcessorDefaultResult
    {
        [JsonProperty("prefab", Order = 0)] public string Prefab { get; set; }
        [JsonProperty("perInputs", Order = 1)] public int PerInputs { get; set; }
    }

    public sealed class ProcessDef
    {
        [JsonProperty("processor", Order = 0)] public string Processor { get; set; }
        [JsonProperty("inputs", Order = 1)] public List<PrefabAmount> Inputs { get; set; } = new List<PrefabAmount>();
        [JsonProperty("anyOneOf", Order = 2)] public bool AnyOneOf { get; set; }
        [JsonProperty("output", Order = 3)] public PrefabAmount Output { get; set; }
        [JsonProperty("seconds", Order = 4)] public double? Seconds { get; set; }
        [JsonProperty("priority", Order = 5)] public int Priority { get; set; }
    }

    public sealed class PrefabAmount
    {
        [JsonProperty("prefab", Order = 0)] public string Prefab { get; set; }
        [JsonProperty("amount", Order = 1)] public int Amount { get; set; }
    }
}
