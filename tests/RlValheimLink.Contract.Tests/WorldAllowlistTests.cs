using RlValheimLink.Core;
using Xunit;

namespace RlValheimLink.Contract.Tests;

public class WorldAllowlistTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(" , ,")]
    public void Empty_list_allows_every_world(string csv)
    {
        Assert.True(WorldAllowlist.Allows(csv, "Midgard"));
        Assert.True(WorldAllowlist.Allows(csv, ""));
        Assert.True(WorldAllowlist.Allows(csv, null));
        Assert.Empty(WorldAllowlist.Parse(csv));
    }

    [Fact]
    public void Matches_case_insensitively_and_ignores_surrounding_whitespace()
    {
        const string csv = " Midgard , testworld ";
        Assert.Equal(new[] { "Midgard", "testworld" }, WorldAllowlist.Parse(csv));
        Assert.True(WorldAllowlist.Allows(csv, "midgard"));
        Assert.True(WorldAllowlist.Allows(csv, "MIDGARD"));
        Assert.True(WorldAllowlist.Allows(csv, "  TestWorld "));
        Assert.False(WorldAllowlist.Allows(csv, "Midgard2"));
        Assert.False(WorldAllowlist.Allows(csv, "mid"));
        Assert.False(WorldAllowlist.Allows(csv, ""));
        Assert.False(WorldAllowlist.Allows(csv, null));
    }

    [Fact]
    public void A_single_name_links_only_that_world()
    {
        Assert.True(WorldAllowlist.Allows("Midgard", "Midgard"));
        Assert.False(WorldAllowlist.Allows("Midgard", "Asgard"));
    }
}
