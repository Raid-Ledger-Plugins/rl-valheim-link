using RlValheimLink.Core;
using Xunit;

namespace RlValheimLink.Contract.Tests;

/// <summary>The kinds the dedicated server builds itself: ContainersBuilder, RecipesBuilder, ScanSchedule, Enricher.Seal and the recipes marker.</summary>
public class ServerKindsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rlvl-kinds-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    static ScannedContainer Box(string prefab, double x, long creator, int stacks = 0, bool vehicle = false, string privacy = ContainerPrivacy.Public) => new()
    {
        Prefab = prefab, X = x, Y = 30, Z = 0, Creator = creator, Width = 8, Height = 4, Vehicle = vehicle, Privacy = privacy,
        Stacks = Enumerable.Range(0, stacks).Select(j => new ScannedStack { Prefab = "Item" + j.ToString("D3"), Quality = 1, Stack = j + 1 }).ToList(),
    };

    static ContainersMessage Build(IEnumerable<ScannedContainer> scanned, out ContainersReport rep) => ContainersBuilder.Build(ServerScans.ContainersHeader(), scanned, out rep);

    // ---------- containers ----------

    [Fact]
    public void Scan_aggregates_merges_rounds_sorts_and_skips_creator_0()
    {
        var msg = Build(ServerScans.Containers(), out var rep);
        Assert.Empty(Validator.Validate(msg));
        Assert.Equal(14, rep.Scanned);
        Assert.Equal(1, rep.Creator0);
        Assert.Equal(1, rep.Merged);
        Assert.Equal(12, msg.Containers.Count);
        Assert.Equal("2026-10-21T03:30:00Z", msg.CapturedAt);
        Assert.Equal(ServerScans.WorldUid, msg.WorldUid);

        // Sorted by prefab, x, z, y, creator.
        Assert.Equal(new[] { "Karve", "Karve", "piece_chest_blackmetal", "piece_chest_private" }, msg.Containers.Take(4).Select(c => c.Prefab));

        var first = msg.Containers.Single(c => c.X == 512.3);
        Assert.Equal(-140.6, first.Z);
        Assert.Equal(new[] { ("CopperOre", 18), ("Stone", 64) }, first.Items.Select(i => (i.Prefab, i.Stack)));
        Assert.Equal(3, first.UsedSlots);
        Assert.Equal(10, first.Slots);

        var merged = msg.Containers.Single(c => c.X == 2252.0);
        Assert.Equal(20, merged.Slots);
        Assert.Equal(new[] { ("GreydwarfEye", 50), ("Resin", 70) }, merged.Items.Select(i => (i.Prefab, i.Stack)));

        var quality = msg.Containers.Single(c => c.Items.Any(i => i.Prefab == "AxeBronze"));
        Assert.Equal(2, quality.Items.Single(i => i.Prefab == "AxeBronze").Quality);
        Assert.Equal(2, quality.Items.Single(i => i.Prefab == "PickaxeAntler").Stack);

        var bad = msg.Containers.Single(c => c.Unreadable);
        Assert.Empty(bad.Items);
        Assert.Equal(0, bad.UsedSlots);
        Assert.Equal(32, bad.Slots);
        Assert.Equal(ContainerPrivacy.Private, msg.Containers.Single(c => c.Prefab == "piece_chest_private").Privacy);
        Assert.All(msg.Containers.Where(c => c.Prefab == "Karve"), c => Assert.True(c.Vehicle));
        Assert.Equal((2, 1, 1), (rep.Vehicles, rep.Private, rep.Unreadable));
    }

    [Fact]
    public void Rows_beyond_the_prefab_widen_slots_and_used_never_exceeds_slots()
    {
        var c = Box("piece_chest_wood", 1, 7, stacks: 3);
        c.Width = 5; c.Height = 2; c.MaxRow = 3;          // a live chest with more rows than its prefab
        var msg = Build(new[] { c }, out _);
        Assert.Equal(20, msg.Containers[0].Slots);

        var tiny = Box("piece_pot3", 2, 7, stacks: 5);
        tiny.Width = 1; tiny.Height = 1;                    // more stacks decoded than cells (a modded format)
        msg = Build(new[] { tiny }, out _);
        Assert.Equal(1, msg.Containers[0].Slots);
        Assert.Equal(1, msg.Containers[0].UsedSlots);
        Assert.Empty(Validator.Validate(msg));
    }

    [Fact]
    public void Over_256_entries_keeps_the_largest_stacks_and_counts_the_rest_on_the_container()
    {
        var msg = Build(new[] { Box("piece_chest_modded", 1, 7, stacks: 300) }, out var rep);
        var c = msg.Containers[0];
        Assert.Equal(Caps.ContainerItemsMax, c.Items.Count);
        Assert.Equal(44, c.OmittedEntries);
        Assert.Equal(1, rep.ContainersOverEntries);
        Assert.DoesNotContain(c.Items, i => i.Stack <= 44);   // stacks 1..44 were the smallest
        Assert.Equal(c.Items.OrderBy(i => i.Prefab, StringComparer.Ordinal).Select(i => i.Prefab), c.Items.Select(i => i.Prefab));
        Assert.Empty(Validator.Validate(msg));
    }

    [Fact]
    public void Message_caps_drop_vehicles_first_then_from_the_end_and_split_the_count_by_privacy()
    {
        // 200 full containers = 51 200 entries > 50 000: five must go; the two ships first.
        var scanned = Enumerable.Range(0, 198).Select(i => Box("piece_chest", i, 7, stacks: 256, privacy: i >= 196 ? ContainerPrivacy.Private : ContainerPrivacy.Public)).ToList();
        scanned.Add(Box("VikingShip", 5000, 7, stacks: 256, vehicle: true));
        scanned.Add(Box("Karve", 5001, 7, stacks: 256, vehicle: true));
        var msg = Build(scanned, out var rep);
        Assert.Empty(Validator.Validate(msg));
        Assert.DoesNotContain(msg.Containers, c => c.Vehicle);
        Assert.Equal(195, msg.Containers.Count);
        Assert.Equal(2, msg.OmittedPrivate);                   // the personal chests sort last among the rest
        Assert.Equal(3, msg.OmittedShared);                    // two ships + one shared chest
        Assert.True(rep.CapsApplied);
    }

    [Fact]
    public void More_than_4096_containers_are_capped()
    {
        var msg = Build(Enumerable.Range(0, 4100).Select(i => Box("piece_chest_wood", i, 7)), out _);
        Assert.Equal(Caps.ContainersMax, msg.Containers.Count);
        Assert.Equal(4, msg.OmittedShared);
        Assert.Empty(Validator.Validate(msg));
    }

    [Fact]
    public void Different_creators_at_one_position_are_never_merged_and_negative_ids_are_kept()
    {
        var msg = Build(new[] { Box("piece_chest_private", 3, 7, 1, privacy: ContainerPrivacy.Private), Box("piece_chest_private", 3, -30486006L, 1, privacy: ContainerPrivacy.Private) }, out var rep);
        Assert.Equal(2, msg.Containers.Count);
        Assert.Equal(0, rep.Merged);
        Assert.Equal(new[] { "-30486006", "7" }, msg.Containers.Select(c => c.CreatorPlayerId));
        Assert.Empty(Validator.Validate(msg));
    }

    [Fact]
    public void Coordinates_round_to_a_tenth_and_stay_in_range()
    {
        Assert.Equal(-261.4, ContainersBuilder.Round(-261.4499));
        Assert.Equal(0.1, ContainersBuilder.Round(0.05));
        Assert.Equal(0d, ContainersBuilder.Round(-0.04));
        Assert.False(double.IsNegative(ContainersBuilder.Round(-0.04)));
        Assert.Equal(Caps.WorldCoordMax, ContainersBuilder.Round(40000));
        Assert.Equal(0d, ContainersBuilder.Round(double.NaN));
    }

    [Fact]
    public void An_empty_world_is_a_valid_snapshot()
    {
        var msg = Build(Array.Empty<ScannedContainer>(), out _);
        Assert.Empty(msg.Containers);
        Assert.Empty(Validator.Validate(msg));
    }

    // ---------- recipes ----------

    [Fact]
    public void Recipes_are_normalised_into_a_valid_message()
    {
        var msg = RecipesBuilder.Normalise(ServerScans.RecipesRaw(), out var rep);
        Assert.Empty(Validator.Validate(msg));
        Assert.Null(msg.Atlas);
        Assert.All(msg.Stations, s => Assert.Null(s.Icon));
        Assert.All(msg.Pieces, p => Assert.Null(p.Icon));

        // ids: the duplicate asset name and the invalid one get <result>.<n>.
        Assert.Contains(msg.Recipes, r => r.Id == "Recipe_Torch" && r.Amount == 1);
        Assert.Contains(msg.Recipes, r => r.Id == "Torch.1" && r.Amount == 2 && r.Formula == RecipeFormula.Custom);
        Assert.Contains(msg.Recipes, r => r.Id == "Fixture_Item.1");
        Assert.Equal(2, rep.RenamedIds);
        Assert.Equal(msg.Recipes.Select(r => r.Id).OrderBy(x => x, StringComparer.Ordinal), msg.Recipes.Select(r => r.Id));

        // A requirement listed twice is summed.
        var torch = msg.Recipes.Single(r => r.Id == "Recipe_Torch");
        Assert.Equal((2, 1), (torch.Resources.Single(x => x.Prefab == "Resin").Amount, torch.Resources.Single(x => x.Prefab == "Resin").AmountPerLevel));
        Assert.Equal(1, rep.MergedResources);

        // Untranslated tokens fall back to the prefab.
        Assert.Equal("UpgradeStation", msg.Stations.Single(s => s.Prefab == "UpgradeStation").Name);
        Assert.Equal("sapling_turnip", msg.Pieces.Single(p => p.Prefab == "sapling_turnip").Name);

        // Processors without a usable conversion go; the 1.0.20 duplicate conversion is one process.
        Assert.DoesNotContain(msg.Processors, p => p.Prefab == "piece_bathtub");
        Assert.Single(msg.Processes, p => p.Processor == "blastfurnace" && p.Inputs[0].Prefab == "FlametalOreNew");

        // 10 alternative trophies → two "any one of" processes of ≤ 8.
        var trophies = msg.Processes.Where(p => p.Processor == "incinerator" && p.Output.Prefab == "Coins").ToList();
        Assert.Equal(new[] { 8, 2 }, trophies.Select(p => p.Inputs.Count));
        Assert.All(trophies, p => Assert.True(p.AnyOneOf));
        Assert.Equal(1, rep.SplitProcesses);

        // processes sorted by processor, then priority descending.
        var inc = msg.Processes.Where(p => p.Processor == "incinerator").Select(p => p.Priority).ToList();
        Assert.Equal(inc.OrderByDescending(x => x), inc);
        Assert.Equal(msg.Processes.Select(p => p.Processor).OrderBy(x => x, StringComparer.Ordinal), msg.Processes.Select(p => p.Processor));
    }

    [Fact]
    public void Unknown_station_references_are_cleared_instead_of_failing_the_message()
    {
        var raw = ServerScans.RecipesRaw();
        raw.Recipes[0].Station = "piece_modded_anvil";
        raw.Pieces[0].Station = "piece_modded_anvil";
        var msg = RecipesBuilder.Normalise(raw, out var rep);
        Assert.Empty(Validator.Validate(msg));
        Assert.Equal(2, rep.DanglingStations);
    }

    [Fact]
    public void A_full_set_of_more_than_8_inputs_has_no_v1_shape_and_is_dropped()
    {
        var raw = ServerScans.RecipesRaw();
        var p = raw.Processes.Last();
        p.AnyOneOf = false;
        var msg = RecipesBuilder.Normalise(raw, out var rep);
        Assert.DoesNotContain(msg.Processes, x => x.Output.Prefab == "Coins");
        Assert.Equal(1, rep.DroppedProcesses);
        Assert.Empty(Validator.Validate(msg));
    }

    [Fact]
    public void Entries_over_12000_drop_pieces_first()
    {
        var raw = ServerScans.RecipesRaw();
        for (int i = 0; i < 800; i++)
            raw.Pieces.Add(new PieceDef { Prefab = "piece_mod_" + i.ToString("D4"), Name = "Mod piece", Category = "Misc", Tool = "Hammer", Resources = Enumerable.Range(0, 16).Select(j => new PieceResource { Prefab = "Res" + j, Amount = 1, Recover = true }).ToList() });
        var msg = RecipesBuilder.Normalise(raw, out var rep);
        Assert.True(rep.Entries <= Caps.RecipeEntriesMax);
        Assert.True(rep.DroppedPieces > 0);
        Assert.Equal(raw.Recipes.Count, msg.Recipes.Count);
        Assert.Empty(Validator.Validate(msg));
    }

    [Fact]
    public void Ids_are_shortened_to_fit_96_characters()
    {
        var raw = ServerScans.RecipesRaw();
        raw.Recipes.Add(new RecipeDef { Id = "bad id", Result = new string('A', 96), Resources = { new RecipeResource { Prefab = "Wood", Amount = 1 } } });
        var msg = RecipesBuilder.Normalise(raw, out _);
        Assert.Contains(msg.Recipes, r => r.Id == new string('A', 94) + ".1");
        Assert.Empty(Validator.Validate(msg));
    }

    // ---------- seal, schedule, marker ----------

    [Fact]
    public void Seal_sets_the_server_id_and_the_snapshot_key_and_refuses_an_invalid_message()
    {
        var c = Enricher.Seal(ServerScans.ContainersScan(), "13");
        Assert.True(c.Ok, c.Reason);
        Assert.Equal("containers|" + ServerScans.WorldUid, c.Key);
        Assert.Contains("\"serverId\":\"13\"", MessageJson.Utf8NoBom.GetString(c.Bytes));

        var r = Enricher.Seal(ServerScans.RecipesServer(), "13");
        Assert.True(r.Ok, r.Reason);
        Assert.Equal("recipes|1.0.20", r.Key);
        Assert.Empty(r.BlobRefs);

        var broken = ServerScans.ContainersScan();
        broken.Containers[0].CreatorPlayerId = "0";
        var b = Enricher.Seal(broken, "13");
        Assert.False(b.Ok);
        Assert.Contains("creatorPlayerId", b.Reason);
        Assert.Null(b.Bytes);
    }

    [Fact]
    public void Schedule_runs_on_a_fixed_grid_and_skips_busy_or_missed_slots()
    {
        var t0 = new DateTime(2026, 10, 21, 3, 0, 0, DateTimeKind.Utc);
        var s = new ScanSchedule(t0, TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(10));
        Assert.False(s.TryStart(t0.AddMinutes(1), false, out _, out _));
        Assert.True(s.TryStart(t0.AddMinutes(2).AddSeconds(1), false, out var slot, out int skipped));
        Assert.Equal((t0.AddMinutes(2), 0), (slot, skipped));
        Assert.False(s.TryStart(t0.AddMinutes(12), true, out _, out skipped));   // still running: slot skipped
        Assert.Equal(1, skipped);
        Assert.True(s.TryStart(t0.AddMinutes(45), false, out slot, out skipped)); // stalled past 22 and 32: start at 42
        Assert.Equal((t0.AddMinutes(42), 2), (slot, skipped));
        Assert.Equal(t0.AddMinutes(52), s.NextAt);
    }

    [Fact]
    public void Recipes_marker_is_per_game_version_and_mod_set_and_reset_with_the_target()
    {
        var s = new Spool(_dir);
        s.ResetIfTargetChanged("t1");
        Assert.False(s.HasRecipesExport("1.0.20", "h1"));
        s.MarkRecipesExport("1.0.20", "h1", "2026-10-21T03:28:00Z");
        Assert.True(new Spool(_dir).HasRecipesExport("1.0.20", "h1"));
        Assert.False(s.HasRecipesExport("1.0.20", "h2"));
        Assert.False(s.HasRecipesExport("1.0.21", "h1"));
        Assert.True(s.ResetIfTargetChanged("t2"));
        Assert.False(s.HasRecipesExport("1.0.20", "h1"));
    }
}
