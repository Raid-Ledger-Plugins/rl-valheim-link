using RlValheimLink.Core;
using Xunit;

namespace RlValheimLink.Contract.Tests;

/// <summary>PortalDiscovery (ROK-1945 review MINOR-1, MINOR-2, NIT-1): the scan's game-free decisions.</summary>
public class PortalDiscoveryTests
{
    static KeyValuePair<string, bool> P(string name, bool portal) => new(name, portal);

    [Fact]
    public void Zero_portal_prefabs_never_produce_a_snapshot_that_would_wipe_raid_ledger()
    {
        Assert.False(PortalDiscovery.CanSnapshot(0), "0 portal prefabs = broken discovery: an empty snapshot must not be sent");
        Assert.True(PortalDiscovery.CanSnapshot(1), "≥1 portal prefab searched: an empty world is a valid empty snapshot");
    }

    [Fact]
    public void Portal_scan_first_slot_is_offset_from_the_chest_scan_and_shares_its_interval()
    {
        var start = new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);
        var chestDelay = TimeSpan.FromMinutes(2);
        var interval = TimeSpan.FromMinutes(10);
        var chests = new ScanSchedule(start, chestDelay, interval);
        var portals = new ScanSchedule(start, PortalDiscovery.FirstDelay(chestDelay), interval);
        Assert.Equal(TimeSpan.FromSeconds(60), portals.NextAt - chests.NextAt);
        Assert.Equal(chests.Interval, portals.Interval);
    }

    [Fact]
    public void Select_keeps_component_prefabs_and_known_extras_and_reports_unknown_extras()
    {
        var net = new[] { P("portal_wood", true), P("portal_stone", true), P("piece_chest", false), P("modded_gate", false), P(null!, true) };
        var extra = new HashSet<string> { "modded_gate", "modded_gat3", "zz_missing" };
        var names = PortalDiscovery.Select(net, extra, out var unknown);
        Assert.Equal(new[] { "modded_gate", "portal_stone", "portal_wood" }, names.ToArray());
        Assert.Equal(new[] { "modded_gat3", "zz_missing" }, unknown.ToArray());
    }

    [Fact]
    public void Select_with_no_portal_component_and_no_extras_finds_nothing()
    {
        var names = PortalDiscovery.Select(new[] { P("piece_chest", false) }, new HashSet<string>(), out var unknown);
        Assert.Empty(names);
        Assert.Empty(unknown);
        Assert.False(PortalDiscovery.CanSnapshot(names.Count));
    }
}
