using RlValheimLink.Core;
using Xunit;

namespace RlValheimLink.Contract.Tests;

/// <summary>
/// Core/UpgradeFormula against values the 1.0.20 production server's own Requirement.GetAmount(q) returned
/// (diagnostic boot 2026-10-21, ROK-1894) and the 0.221.4 IL rule.
/// </summary>
public class UpgradeFormulaTests
{
    // (amount, amountPerLevel, upgraderResource) → live GetAmount(q) for q = 1..4 on 1.0.20.
    public static IEnumerable<object[]> Live1020() => new[]
    {
        new object[] { "Bronze (Recipe_ArmorBronzeChest)", 5, 3, false, new long[] { 5, 3, 6, 12 } },
        new object[] { "DeerHide (Recipe_ArmorBronzeChest)", 2, 0, false, new long[] { 2, 0, 0, 0 } },
        new object[] { "Upgrader1Armor (Recipe_ArmorBronzeChest)", 1, 0, true, new long[] { 1, 1, 1, 1 } },
        new object[] { "BjornHide (Recipe_ArmorBerserkerUndeadLegs)", 10, 5, false, new long[] { 10, 5, 10, 20 } },
        new object[] { "Blueberries (Recipe_ArmorBerserkerChest)", 4, 1, false, new long[] { 4, 1, 2, 4 } },
        new object[] { "Gold (Recipe_HelmetGold, upgrade-only)", 20, 15, false, new long[] { 20, 15, 30, 60 } },
    };

    [Theory, MemberData(nameof(Live1020))]
    public void One_dot_x_matches_what_the_game_returned(string what, int amount, int perLevel, bool upgrader, long[] live)
    {
        for (int q = 1; q <= live.Length; q++)
            Assert.True(live[q - 1] == UpgradeFormula.Amount("1.0.20", amount, perLevel, upgrader, q), $"{what} q{q}");
    }

    [Fact]
    public void Zero_dot_221_keeps_the_old_rule_and_upgrade_items_are_free_after_q1()
    {
        Assert.Equal(new long[] { 5, 3, 6, 9 }, Enumerable.Range(1, 4).Select(q => UpgradeFormula.Amount("0.221.4", 5, 3, false, q)));
        Assert.Equal(new long[] { 1, 0, 0, 0 }, Enumerable.Range(1, 4).Select(q => UpgradeFormula.Amount("0.221.4", 1, 0, true, q)));
    }

    [Fact]
    public void Up_to_quality_3_both_rules_agree()
    {
        for (int q = 1; q <= 3; q++)
            Assert.Equal(UpgradeFormula.Amount("0.221.4", 7, 4, false, q), UpgradeFormula.Amount("1.0.20", 7, 4, false, q));
    }

    [Theory]
    [InlineData("1.0.20", true)]
    [InlineData("1.0.0", true)]
    [InlineData("2.0", true)]
    [InlineData("0.221.4", false)]
    [InlineData("0.220.5", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("x.1", false)]
    public void One_dot_x_is_major_1_or_later(string version, bool expected) => Assert.Equal(expected, UpgradeFormula.IsOneDotX(version));

    [Fact]
    public void Station_level_is_unchanged_and_big_qualities_do_not_overflow()
    {
        Assert.Equal(new[] { 2, 3, 4, 5 }, Enumerable.Range(1, 4).Select(q => UpgradeFormula.StationLevel(2, q)));
        Assert.Equal(1, UpgradeFormula.StationLevel(0, 1));
        Assert.Equal(10_000L << 8, UpgradeFormula.Amount("1.0.20", 1, 10_000, false, 10));
    }
}
