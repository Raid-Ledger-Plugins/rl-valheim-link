using RlValheimLink.Core;

namespace RlValheimLink.Contract.Tests;

/// <summary>
/// Raw server-side reads (what Server/ChestScanner and Server/RecipesExport hand to Core), shaped after the
/// production chest dump of 2026-10-20 (mod 0.2.1, game 1.0.20: prefab names and sizes, stack mixes,
/// station values and conversions) and ANONYMISED: creators are the fixture playerIds
/// 4200000000000042/43/44 (no real player ids, names or Steam ids), positions are invented, the world uid
/// is the fixture one. They feed fixtures/containers-scan.json and fixtures/recipes-server.json through
/// the real builders, so those files are the mod's own output.
/// </summary>
public static class ServerScans
{
    public const string WorldUid = "9876543210987";
    public const string GameVersion = "1.0.20";
    public static readonly DateTime ScanSlot = new DateTime(2026, 10, 21, 3, 30, 0, DateTimeKind.Utc);

    static ScannedContainer Chest(string prefab, double x, double y, double z, long creator, int w, int h, params (string prefab, int q, int n)[] stacks)
    {
        var c = new ScannedContainer { Prefab = prefab, X = x, Y = y, Z = z, Creator = creator, Width = w, Height = h };
        int i = 0;
        foreach (var s in stacks) { c.Stacks.Add(new ScannedStack { Prefab = s.prefab, Quality = s.q, Stack = s.n }); c.MaxRow = Math.Max(c.MaxRow, i++ / w); }
        return c;
    }

    public static List<ScannedContainer> Containers() => new()
    {
        // A base of wood chests (the dump: 107 piece_chest_wood, 5x2, mostly sorted materials).
        Chest("piece_chest_wood", 512.34, 33.2, -140.61, 4200000000000042, 5, 2, ("Stone", 1, 50), ("CopperOre", 1, 18), ("Stone", 1, 14)),
        Chest("piece_chest_wood", 503.17, 34.0, -150.58, 4200000000000042, 5, 2, ("Copper", 1, 146)),
        Chest("piece_chest_wood", 503.66, 34.0, -150.2, 4200000000000042, 5, 2, ("Tin", 1, 254)),
        Chest("piece_chest_wood", 2256.2, 31.2, 792.9, 4200000000000043, 5, 2, ("DeerHide", 1, 33), ("Feathers", 1, 10), ("LeatherScraps", 1, 50), ("LeatherScraps", 1, 32), ("RoundLog", 1, 14), ("Stone", 1, 26), ("Wood", 1, 50), ("Wood", 1, 15)),
        Chest("piece_chest_wood", 2257.3, 31.2, 795.1, 4200000000000043, 5, 2, ("AxeBronze", 2, 1), ("IronNails", 1, 10), ("MeadPoisonResist", 1, 11), ("PickaxeAntler", 1, 1), ("PickaxeAntler", 1, 1), ("Tin", 1, 3)),
        Chest("piece_chest_wood", 2249.7, 30.0, 784.1, 4200000000000043, 5, 2, ("Coal", 1, 50), ("Coal", 1, 45), ("IronScrap", 1, 163)),
        // No inventory data under items/itemData: an empty chest (the dump: 9 of 111).
        Chest("piece_chest_wood", 2251.0, 30.0, 786.5, 4200000000000043, 5, 2),
        // Two overlapping pieces of one builder at one rounded position: merged into one container.
        Chest("piece_chest_wood", 2252.04, 30.0, 788.01, 4200000000000043, 5, 2, ("Resin", 1, 50)),
        Chest("piece_chest_wood", 2251.96, 30.0, 787.99, 4200000000000043, 5, 2, ("Resin", 1, 20), ("GreydwarfEye", 1, 50)),
        // A world-generated chest of a buildable prefab (creator 0): never sent.
        Chest("piece_chest_wood", -1800.5, 40.0, 3120.0, 0, 5, 2, ("Amber", 1, 2)),
        // Ships (the dump: 4 Karve, 1 with items), flagged as vehicles.
        WithVehicle(Chest("Karve", 640.2, 29.6, -122.4, 4200000000000044, 2, 2, ("BronzeNails", 1, 80), ("Wood", 1, 1), ("Coal", 1, 3))),
        WithVehicle(Chest("Karve", 655.9, 29.5, -130.1, 4200000000000044, 2, 2)),
        // A personal chest (piece_chest_private, 3x2, Private): owner + admins only at read time.
        WithPrivacy(Chest("piece_chest_private", 505.0, 34.0, -145.0, 4200000000000044, 3, 2, ("Coins", 1, 250), ("Ruby", 1, 3)), ContainerPrivacy.Private),
        // A chest the game could not decode: sent, flagged, with no items.
        Unreadable(Chest("piece_chest_blackmetal", 510.0, 34.0, -146.5, 4200000000000042, 8, 4)),
    };

    static ScannedContainer WithVehicle(ScannedContainer c) { c.Vehicle = true; return c; }
    static ScannedContainer WithPrivacy(ScannedContainer c, string p) { c.Privacy = p; return c; }
    static ScannedContainer Unreadable(ScannedContainer c) { c.Unreadable = true; return c; }

    public static ContainersHeader ContainersHeader() => new()
    {
        ServerId = SyntheticCaptures.ServerId, GameVersion = GameVersion, ModVersion = SyntheticCaptures.ModVersion, WorldUid = WorldUid, CapturedAtUtc = ScanSlot,
    };

    public static ContainersMessage ContainersScan() => ContainersBuilder.Build(ContainersHeader(), Containers(), out _);

    // ---------- recipes ----------

    static RecipeResource R(string prefab, int amount, int perLevel = 0, bool recover = true, bool upgrader = false) =>
        new() { Prefab = prefab, Amount = amount, AmountPerLevel = perLevel, ExtraAmountOnlyOneIngredient = 0, Recover = recover, UpgraderResource = upgrader };

    static PieceResource P(string prefab, int amount, bool recover = true) => new() { Prefab = prefab, Amount = amount, Recover = recover };

    static ProcessDef Conv(string processor, string from, string to, double seconds, int toAmount = 1) => new()
    {
        Processor = processor, Inputs = { new PrefabAmount { Prefab = from, Amount = 1 } }, Output = new PrefabAmount { Prefab = to, Amount = toAmount }, Seconds = seconds,
    };

    /// <summary>What RecipesExport reads, before RecipesBuilder: unsorted, one duplicate id, a duplicate requirement, a processor without conversions, an obliterator conversion of 10 alternatives.</summary>
    public static RecipesMessage RecipesRaw()
    {
        var m = new RecipesMessage
        {
            ServerId = SyntheticCaptures.ServerId, CapturedAt = "2026-10-21T03:28:00Z", GameVersion = GameVersion, ModVersion = SyntheticCaptures.ModVersion,
            Locale = "English", Atlas = null,
            Stations =
            {
                new CraftingStationDef { Prefab = "piece_workbench", Name = "Workbench", MaxLevel = 5, Upgrader = false },
                new CraftingStationDef { Prefab = "forge", Name = "Forge", MaxLevel = 7, Upgrader = false },
                new CraftingStationDef { Prefab = "piece_cauldron", Name = "Cauldron", MaxLevel = 6, Upgrader = false },
                new CraftingStationDef { Prefab = "blackforge", Name = "Black forge", MaxLevel = 3, Upgrader = false },
                new CraftingStationDef { Prefab = "UpgradeStation", Name = "$piece_upgradestation", MaxLevel = 1, Upgrader = true },
                new CraftingStationDef { Prefab = "piece_stonecutter", Name = "Stonecutter", MaxLevel = 1, Upgrader = false },
            },
            Recipes =
            {
                new RecipeDef { Id = "Recipe_SwordBronze", Result = "SwordBronze", Station = "forge", MinStationLevel = 1, Resources = { R("Bronze", 8, 4), R("Wood", 2), R("LeatherScraps", 2, 1) } },
                new RecipeDef { Id = "Recipe_Bronze", Result = "Bronze", Station = "forge", MinStationLevel = 1, Resources = { R("Copper", 2), R("Tin", 1) } },
                new RecipeDef { Id = "Recipe_Bronze5", Result = "Bronze", Amount = 5, Station = "forge", MinStationLevel = 1, Resources = { R("Copper", 10), R("Tin", 5) } },
                new RecipeDef { Id = "Recipe_ArmorBronzeChest", Result = "ArmorBronzeChest", Station = "forge", MinStationLevel = 2, Resources = { R("Bronze", 5, 3), R("DeerHide", 2), R("Upgrader1Armor", 0, 1, recover: false, upgrader: true) } },
                new RecipeDef { Id = "Recipe_HelmetGold", Result = "HelmetGold", Station = "blackforge", MinStationLevel = 1, UpgradeOnly = true, Resources = { R("Gold", 0, 10), R("Upgrader4Armor", 0, 1, recover: false, upgrader: true) } },
                new RecipeDef { Id = "Recipe_MeadBaseHealthMinor", Result = "MeadBaseHealthMinor", Station = "piece_cauldron", RepairStation = "piece_cauldron", MinStationLevel = 1, Resources = { R("Honey", 10), R("Blueberries", 5), R("Raspberry", 10), R("Dandelion", 1) } },
                new RecipeDef { Id = "Recipe_Torch", Result = "Torch", Station = null, Resources = { R("Wood", 1), R("Resin", 2), R("Resin", 0, 1) } },
                // A mod's second recipe under an existing asset name, and a recipe whose live cost a mod patched.
                new RecipeDef { Id = "Recipe_Torch", Result = "Torch", Amount = 2, Station = "piece_workbench", MinStationLevel = 1, Formula = RecipeFormula.Custom, Resources = { R("Wood", 1), R("Resin", 1) } },
                new RecipeDef { Id = "Recipe Fixture (Clone)", Result = "Fixture_Item", Station = "piece_workbench", Resources = { R("Stone", 3) } },
            },
            Pieces =
            {
                new PieceDef { Prefab = "piece_workbench", Name = "Workbench", Category = "Crafting", Tool = "Hammer", Station = null, Resources = { P("Wood", 10) } },
                new PieceDef { Prefab = "piece_chest_wood", Name = "Chest", Category = "Furniture", Tool = "Hammer", Station = "piece_workbench", Resources = { P("Wood", 10) } },
                new PieceDef { Prefab = "piece_chest_private", Name = "Personal chest", Category = "Furniture", Tool = "Hammer", Station = "piece_workbench", Resources = { P("FineWood", 12), P("Iron", 2) } },
                new PieceDef { Prefab = "forge", Name = "Forge", Category = "Crafting", Tool = "Hammer", Station = "piece_workbench", Resources = { P("Stone", 4), P("Coal", 4), P("Wood", 10), P("Copper", 6) } },
                new PieceDef { Prefab = "smelter", Name = "Smelter", Category = "Crafting", Tool = "Hammer", Station = "piece_workbench", Resources = { P("Stone", 50), P("SurtlingCore", 5) } },
                new PieceDef { Prefab = "fermenter", Name = "Fermenter", Category = "Crafting", Tool = "Hammer", Station = "forge", Resources = { P("FineWood", 30), P("Bronze", 5), P("Resin", 10) } },
                new PieceDef { Prefab = "sapling_turnip", Name = "$piece_sapling_turnip", Category = "Misc", Tool = "Cultivator", Station = null, Resources = { P("TurnipSeeds", 1, recover: false) } },
            },
            Processors =
            {
                new ProcessorDef { Prefab = "smelter", Type = ProcessorType.Smelter, Fuel = "Coal", FuelPerProduct = 2, MaxInput = 10 },
                new ProcessorDef { Prefab = "charcoal_kiln", Type = ProcessorType.Smelter, MaxInput = 25 },
                new ProcessorDef { Prefab = "blastfurnace", Type = ProcessorType.Smelter, Fuel = "Coal", FuelPerProduct = 2, MaxInput = 10 },
                new ProcessorDef { Prefab = "windmill", Type = ProcessorType.Smelter, WindPowered = true, MaxInput = 50 },
                new ProcessorDef { Prefab = "piece_spinningwheel", Type = ProcessorType.Smelter, RequiresRoof = true, MaxInput = 40 },
                new ProcessorDef { Prefab = "eitrrefinery", Type = ProcessorType.Smelter, Fuel = "Sap", FuelPerProduct = 1, MaxInput = 20 },
                new ProcessorDef { Prefab = "piece_bathtub", Type = ProcessorType.Smelter, Fuel = "Wood", FuelPerProduct = 1 },
                new ProcessorDef { Prefab = "fermenter", Type = ProcessorType.Fermenter, RequiresRoof = true, MaxInput = 1 },
                new ProcessorDef { Prefab = "piece_cookingstation", Type = ProcessorType.Cooking, MaxInput = 2, BurntResult = "Coal" },
                new ProcessorDef { Prefab = "piece_oven", Type = ProcessorType.Cooking, Fuel = "Wood", FuelSecondsPerUnit = 2000, MaxInput = 4, BurntResult = "Coal" },
                new ProcessorDef { Prefab = "piece_FrostFoundry", Type = ProcessorType.Cooking, Fuel = "FrozenFuel", FuelSecondsPerUnit = 10, MaxInput = 1, BurntResult = null },
                new ProcessorDef { Prefab = "incinerator", Type = ProcessorType.Incinerator, DefaultResult = new ProcessorDefaultResult { Prefab = "Coal", PerInputs = 10 } },
            },
            Processes =
            {
                Conv("smelter", "CopperOre", "Copper", 30), Conv("smelter", "TinOre", "Tin", 30), Conv("smelter", "IronScrap", "Iron", 30), Conv("smelter", "BronzeScrap", "Bronze", 30),
                Conv("charcoal_kiln", "Wood", "Coal", 15), Conv("charcoal_kiln", "FineWood", "Coal", 15), Conv("charcoal_kiln", "RoundLog", "Coal", 15),
                // The blast furnace lists FlametalOreNew → FlametalNew twice in 1.0.20; one process is kept.
                Conv("blastfurnace", "FlametalOreNew", "FlametalNew", 30), Conv("blastfurnace", "BlackMetalScrap", "BlackMetal", 30), Conv("blastfurnace", "FlametalOreNew", "FlametalNew", 30),
                Conv("windmill", "Barley", "BarleyFlour", 10), Conv("windmill", "Oat", "OatFlour", 10),
                Conv("piece_spinningwheel", "Flax", "LinenThread", 30),
                Conv("eitrrefinery", "Softtissue", "Eitr", 40),
                Conv("fermenter", "MeadBaseHealthMinor", "MeadHealthMinor", 2400, 6), Conv("fermenter", "MeadBasePoisonResist", "MeadPoisonResist", 2400, 6),
                Conv("piece_cookingstation", "RawMeat", "CookedMeat", 25), Conv("piece_cookingstation", "NeckTail", "NeckTailGrilled", 25),
                Conv("piece_oven", "BreadDough", "Bread", 50),
                Conv("piece_FrostFoundry", "SwordGoldUncooked", "SwordGold", 50),
                new ProcessDef
                {
                    Processor = "incinerator", AnyOneOf = true, Priority = 0, Seconds = null, Output = new PrefabAmount { Prefab = "Coal", Amount = 1 },
                    Inputs = { new() { Prefab = "Wood", Amount = 5 }, new() { Prefab = "ElderBark", Amount = 5 }, new() { Prefab = "FineWood", Amount = 5 }, new() { Prefab = "RoundLog", Amount = 5 }, new() { Prefab = "YggdrasilWood", Amount = 5 } },
                },
                new ProcessDef
                {
                    Processor = "incinerator", AnyOneOf = true, Priority = 1, Seconds = null, Output = new PrefabAmount { Prefab = "Coins", Amount = 10 },
                    Inputs =
                    {
                        new() { Prefab = "TrophyBlob", Amount = 1 }, new() { Prefab = "TrophyBoar", Amount = 1 }, new() { Prefab = "TrophyBonemass", Amount = 1 }, new() { Prefab = "TrophyDeathsquito", Amount = 1 },
                        new() { Prefab = "TrophyDeer", Amount = 1 }, new() { Prefab = "TrophyDragonQueen", Amount = 1 }, new() { Prefab = "TrophyDraugr", Amount = 1 }, new() { Prefab = "TrophyDraugrElite", Amount = 1 },
                        new() { Prefab = "TrophyDraugrFem", Amount = 1 }, new() { Prefab = "TrophyEikthyr", Amount = 1 },
                    },
                },
            },
        };
        return m;
    }

    public static RecipesMessage RecipesServer() => RecipesBuilder.Normalise(RecipesRaw(), out _);

    // ---------- piece/station icon atlas (0.4.0, rendered by an admin client) ----------

    /// <summary>The prefabs the admin client's atlas covers, in atlas order (piece_stonecutter has no icon, to show a partial atlas).</summary>
    public static readonly string[] IconPrefabs =
        { "UpgradeStation", "blackforge", "fermenter", "forge", "piece_cauldron", "piece_chest_private", "piece_chest_wood", "piece_workbench", "sapling_turnip", "smelter" };

    public const int IconSize = 64, IconsPerRow = 4;

    public static byte[] IconAtlasPng() => SyntheticCaptures.AtlasPng(IconPrefabs.Length, IconSize, IconsPerRow);

    /// <summary>What PieceIconsCapture sends: the atlas ref and a rect per prefab (AtlasBuilder's grid).</summary>
    public static PieceIconMap IconMap(byte[] atlasPng)
    {
        var map = new PieceIconMap { GameVersion = GameVersion, Atlas = Png.MakeRef(atlasPng) };
        for (int i = 0; i < IconPrefabs.Length; i++)
            map.Icons[IconPrefabs[i]] = new[] { (i % IconsPerRow) * IconSize, (i / IconsPerRow) * IconSize, IconSize, IconSize };
        return map;
    }

    /// <summary>The server's recipes export with the stored icon map applied (ServerHalf.FinishRecipes).</summary>
    public static RecipesMessage RecipesServerIcons(byte[] atlasPng)
    {
        var msg = RecipesServer();
        PieceIconMap.Apply(msg, IconMap(atlasPng), out _);
        return msg;
    }
}
