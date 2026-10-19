namespace RlValheimLink.Contract.Tests;

/// <summary>
/// Synthetic captures shaped like what the mod reads from the game, with NO real data:
/// SteamID64 76561198000000042, invented names, no seed. They feed fixtures/ (the mod's own
/// output, regenerated with RLVL_WRITE_FIXTURES=1) and the mock receiver tests.
/// </summary>
public static class SyntheticCaptures
{
    public const string ServerId = "srv_fixture_rlvl";
    public const string CapturedAt = "2026-10-18T12:00:00Z";
    public const string GameVersion = "0.221.4";
    public const string ModVersion = "0.1.0";

    static ItemStack Stack(string prefab, int stack = 1, int quality = 1, int variant = 0, double? dur = null, double? max = null) =>
        new() { Prefab = prefab, Stack = stack, Quality = quality, Variant = variant, Durability = dur, MaxDurability = max };

    public static CharacterMessage CharacterVanilla()
    {
        var sword = Stack("SwordBronze", quality: 2, dur: 142.25, max: 200);
        var helmet = Stack("HelmetLeather", dur: 100, max: 100);
        var m = new CharacterMessage
        {
            ServerId = ServerId, CapturedAt = CapturedAt, GameVersion = GameVersion, ModVersion = ModVersion,
            Character = new CharacterIdentity { SteamId = "76561198000000042", PlayerId = "4200000000000042", Name = "Fixturesson", WorldUid = "9876543210987", WorldName = "Fixtureheim" },
            Equipment =
            {
                new EquippedItem { Slot = EquipSlot.RightHand, Item = sword },
                new EquippedItem { Slot = EquipSlot.Head, Item = helmet },
            },
            Inventory = new Inventory
            {
                Grid = new InventoryGrid { Width = 8, Height = 4 },
                Items =
                {
                    new InventorySlot { X = 0, Y = 0, Equipped = true, Item = sword },
                    new InventorySlot { X = 1, Y = 0, Equipped = true, Item = helmet },
                    new InventorySlot { X = 2, Y = 0, Equipped = false, Item = Stack("Wood", 50) },
                    new InventorySlot { X = 3, Y = 0, Equipped = false, Item = Stack("Resin", 20) },
                    new InventorySlot { X = 7, Y = 3, Equipped = false, Item = Stack("CookedMeat", 6) },
                },
            },
            Skills =
            {
                new Skill { Id = "Swords", Level = 12.5, Accumulator = 3.25 },
                new Skill { Id = "Run", Level = 20, Accumulator = 0 },
                new Skill { Id = "Jump", Level = 8, Accumulator = 1.5 },
            },
            Trophies = { "TrophyBoar", "TrophyDeer" },
            KnownItems = { "CookedMeat", "Resin", "Stone", "Wood" },
            KnownRecipes = { "HelmetLeather", "SwordBronze" },
        };
        return m;
    }

    public static CharacterMessage CharacterEaqs()
    {
        var m = CharacterVanilla();
        m.Character.PlayerId = "4200000000000043";
        m.Character.Name = "Fixturedottir";
        m.Inventory.Grid.Height = 6;
        m.Inventory.Grid.ExtraRows.Add(new GridExtraRow { Y = 4, Purpose = RowPurpose.Equipment });
        m.Inventory.Grid.ExtraRows.Add(new GridExtraRow { Y = 5, Purpose = RowPurpose.Quickslot });
        // EAQS keeps equipped items in its own row; vanilla rows hold the rest.
        m.Inventory.Items.RemoveAll(s => s.Equipped);
        m.Inventory.Items.Insert(0, new InventorySlot { X = 0, Y = 4, Equipped = true, Item = m.Equipment[0].Item });
        m.Inventory.Items.Insert(1, new InventorySlot { X = 1, Y = 4, Equipped = true, Item = m.Equipment[1].Item });
        m.Inventory.Items.Add(new InventorySlot { X = 0, Y = 5, Equipped = false, Item = Stack("MeadHealthMinor", 3) });
        return m;
    }

    /// <summary>A deterministic explored disc plus a few scattered cells, in the game's py = 0 = south layout.</summary>
    public static bool[] ExploredCells(int size, int radius, int seed)
    {
        var cells = new bool[size * size];
        int c = size / 2;
        for (int py = 0; py < size; py++)
            for (int px = 0; px < size; px++)
            {
                int dx = px - c, dy = py - c;
                if (dx * dx + dy * dy <= radius * radius) cells[py * size + px] = true;
            }
        var rng = new Random(seed);
        for (int i = 0; i < 500; i++) cells[rng.Next(cells.Length)] = true;
        return cells;
    }

    public static MapMessage MapPersonal(byte[] baseMapPng = null)
    {
        const int size = 2048;
        var m = new MapMessage
        {
            ServerId = ServerId, CapturedAt = CapturedAt, GameVersion = GameVersion, ModVersion = ModVersion,
            Character = new MapCharacterRef { SteamId = "76561198000000042", PlayerId = "4200000000000042", WorldUid = "9876543210987" },
            Grid = new MapGrid { TextureSize = size, PixelSizeMetres = 12 },
            Explored = Bitset.Encode(ExploredCells(size, 180, 1), size, size),
            SharedExplored = null,
            Pins =
            {
                new MapPin { Type = "Icon0", Label = "Base", X = 120.5, Z = -42.25, Checked = false, Shared = false },
                new MapPin { Type = "Death", Label = "", X = 900, Z = 1450, Checked = true, Shared = false },
                new MapPin { Type = "Bed", Label = "", X = 118, Z = -40, Checked = false, Shared = false },
            },
            BaseMap = baseMapPng == null ? null : Png.MakeRef(baseMapPng),
        };
        return m;
    }

    public static MapMessage MapSharedLayer()
    {
        var m = MapPersonal();
        const int size = 2048;
        m.SharedExplored = Bitset.Encode(ExploredCells(size, 420, 2), size, size);
        m.Pins.Add(new MapPin { Type = "Icon3", Label = "Shared portal", X = 1200, Z = -800, Checked = false, Shared = true });
        return m;
    }

    public static byte[] AtlasPng(int icons, int icon, int perRow)
    {
        int rows = (icons + perRow - 1) / perRow;
        int w = perRow * icon, h = rows * icon;
        var rgba = new byte[w * h * 4];
        for (int i = 0; i < icons; i++)
        {
            int ox = (i % perRow) * icon, oy = (i / perRow) * icon;
            for (int y = 0; y < icon; y++)
                for (int x = 0; x < icon; x++)
                {
                    int o = ((oy + y) * w + ox + x) * 4;
                    rgba[o] = (byte)(i * 37); rgba[o + 1] = (byte)(x * 4); rgba[o + 2] = (byte)(y * 4); rgba[o + 3] = 255;
                }
        }
        return Png.EncodeRgba(rgba, w, h);
    }

    public const int AtlasIcon = 64, AtlasPerRow = 4, AtlasCount = 7;
    public static byte[] AtlasPng() => AtlasPng(AtlasCount, AtlasIcon, AtlasPerRow);

    /// <summary>Takes the atlas PNG bytes (the committed fixtures/items-atlas.png in tests) so the ref is stable across zlib builds.</summary>
    public static ItemsMessage ItemsAtlas(byte[] atlasPng)
    {
        const int icon = AtlasIcon, perRow = AtlasPerRow;
        var defs = new[]
        {
            ("SwordBronze", "Bronze sword", "OneHandedWeapon", 1, 4, 0),
            ("HelmetLeather", "Leather helmet", "Helmet", 1, 4, 0),
            ("Wood", "Wood", "Material", 50, 1, 0),
            ("Resin", "Resin", "Material", 50, 1, 0),
            ("CookedMeat", "Cooked meat", "Consumable", 20, 1, 0),
            ("CapeDeerHide", "Deer hide cape", "Shoulder", 1, 4, 0),
            ("TrophyBoar", "Boar trophy", "Trophy", 20, 1, 0),
        };
        var m = new ItemsMessage
        {
            ServerId = ServerId, CapturedAt = CapturedAt, GameVersion = GameVersion, ModVersion = ModVersion,
            Locale = "English", Atlas = Png.MakeRef(atlasPng),
        };
        for (int i = 0; i < defs.Length; i++)
        {
            var d = defs[i];
            m.Items.Add(new ItemDefinition
            {
                Prefab = d.Item1, Name = d.Item2, Category = d.Item3, MaxStack = d.Item4, MaxQuality = d.Item5, Variants = d.Item6,
                Icon = ItemIcon.Atlas((i % perRow) * icon, (i / perRow) * icon, icon, icon),
            });
        }
        m.Items.Add(new ItemDefinition { Prefab = "Fixture_NoIcon", Name = "Icon-less fixture item", Category = "Misc", MaxStack = 1, MaxQuality = 1, Variants = 0, Icon = null });
        return m;
    }

    /// <summary>File-backed icons, one with the additive `blob` ref (contract 2026-10-18) and one without.</summary>
    public static ItemsMessage ItemsFileIcons(byte[] iconPng)
    {
        var m = new ItemsMessage
        {
            ServerId = ServerId, CapturedAt = CapturedAt, GameVersion = GameVersion, ModVersion = ModVersion,
            Locale = "English", Atlas = null,
        };
        m.Items.Add(new ItemDefinition { Prefab = "SwordBronze", Name = "Bronze sword", Category = "OneHandedWeapon", MaxStack = 1, MaxQuality = 4, Variants = 0, Icon = ItemIcon.FromFile("SwordBronze.png", Png.MakeRef(iconPng)) });
        m.Items.Add(new ItemDefinition { Prefab = "Wood", Name = "Wood", Category = "Material", MaxStack = 50, MaxQuality = 1, Variants = 0, Icon = ItemIcon.FromFile("Wood.png") });
        return m;
    }
}
