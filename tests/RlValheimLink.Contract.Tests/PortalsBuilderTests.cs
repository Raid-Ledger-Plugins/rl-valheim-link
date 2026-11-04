using RlValheimLink.Core;
using Xunit;

namespace RlValheimLink.Contract.Tests;

/// <summary>PortalsBuilder (CONTRACT.md §5c): ZDO reads → one valid `portals` snapshot.</summary>
public class PortalsBuilderTests
{
    static PortalsMessage Build(IEnumerable<ScannedPortal> portals, out PortalsReport rep) =>
        PortalsBuilder.Build(ServerScans.ContainersHeader(), portals, out rep);

    [Fact]
    public void Scan_rounds_sorts_dedupes_and_keeps_modded_prefabs()
    {
        var msg = Build(ServerScans.Portals(), out var rep);
        Assert.Equal("portals", msg.Kind);
        Assert.Equal(ServerScans.WorldUid, msg.WorldUid);
        Assert.Equal("2026-10-21T03:30:00Z", msg.CapturedAt);
        Assert.Equal(
            new[] { "portal_blackmarble", "portal_obsidian", "portal_stone", "portal_wood", "portal_wood", "portal_wood" },
            msg.Portals.Select(p => p.Prefab).ToArray());
        var home = msg.Portals.First(p => p.Prefab == "portal_wood");
        Assert.Equal((512.0, 33.2, -140.7, "home"), (home.X, home.Y!.Value, home.Z, home.Tag));
        Assert.Equal(1, rep.Duplicates);
        Assert.Equal(7, rep.Scanned);
        Assert.Equal(6, rep.Portals);
        Assert.Equal(5, rep.Tagged);
        Assert.Empty(Validator.Validate(MessageJson.ToToken(msg)));
    }

    [Fact]
    public void Long_modded_tag_is_clipped_to_64_without_splitting_a_surrogate_pair()
    {
        var msg = Build(ServerScans.Portals(), out var rep);
        Assert.Equal(new string('g', Caps.PortalTagMaxChars), msg.Portals.Single(p => p.Prefab == "portal_obsidian").Tag);
        Assert.Equal(1, rep.TagsClipped);

        var emoji = new string('a', Caps.PortalTagMaxChars - 1) + "\U0001F525"; // the pair would straddle char 64
        var m2 = Build(new[] { new ScannedPortal { Prefab = "portal_wood", Tag = emoji } }, out _);
        Assert.Equal(Caps.PortalTagMaxChars - 1, m2.Portals[0].Tag.Length);
        Assert.Empty(Validator.Validate(MessageJson.ToToken(m2)));
    }

    [Fact]
    public void Empty_world_is_a_valid_empty_snapshot()
    {
        var msg = Build(new List<ScannedPortal>(), out var rep);
        Assert.Empty(msg.Portals);
        Assert.Equal(0, msg.Omitted);
        Assert.Equal(0, rep.Scanned);
        Assert.Empty(Validator.Validate(MessageJson.ToToken(msg)));
    }

    [Fact]
    public void Null_tag_and_unusable_prefab_are_handled()
    {
        var msg = Build(new[]
        {
            new ScannedPortal { Prefab = "portal_wood(Clone)", X = 1, Y = 2, Z = 3, Tag = null },
            new ScannedPortal { Prefab = "", X = 4, Y = 5, Z = 6, Tag = "x" },
            null!,
        }, out var rep);
        Assert.Single(msg.Portals);
        Assert.Equal(("portal_wood", ""), (msg.Portals[0].Prefab, msg.Portals[0].Tag));
        Assert.Equal(1, rep.Invalid);
        Assert.Empty(Validator.Validate(MessageJson.ToToken(msg)));
    }

    [Fact]
    public void More_than_4096_portals_are_capped_and_counted_in_omitted()
    {
        var many = Enumerable.Range(0, Caps.PortalsMax + 3).Select(i => new ScannedPortal { Prefab = "portal_wood", X = i, Y = 30, Z = 0, Tag = "" });
        var msg = Build(many, out var rep);
        Assert.Equal(Caps.PortalsMax, msg.Portals.Count);
        Assert.Equal(3, msg.Omitted);
        Assert.Equal(3, rep.Omitted);
        Assert.Equal(Caps.PortalsMax - 1, msg.Portals[^1].X);
        Assert.Empty(Validator.Validate(MessageJson.ToToken(msg)));
    }

    [Fact]
    public void Seal_keys_portals_by_world_and_validates()
    {
        var r = Enricher.Seal(ServerScans.PortalsScan(), "13");
        Assert.True(r.Ok, r.Reason);
        Assert.Equal(("portals", "portals|" + ServerScans.WorldUid), (r.Kind, r.Key));
    }
}
