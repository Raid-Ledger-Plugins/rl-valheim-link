using RlValheimLink.Core;
using Xunit;

namespace RlValheimLink.Contract.Tests;

public class ConfigMigrationTests
{
    [Fact]
    public void Old_default_admins_at_revision_0_becomes_anyone_at_revision_1()
    {
        var r = ConfigMigration.Migrate("admins", 0);
        Assert.Equal("anyone", r.ItemsExportBy);
        Assert.Equal(1, r.Revision);
        Assert.True(r.ItemsExportByChanged);
        Assert.True(r.NeedsSave(0));
    }

    [Theory]
    [InlineData("anyone")]
    [InlineData("nobody")]
    public void Other_values_at_revision_0_are_kept_and_revision_moves_to_1(string value)
    {
        var r = ConfigMigration.Migrate(value, 0);
        Assert.Equal(value, r.ItemsExportBy);
        Assert.Equal(1, r.Revision);
        Assert.False(r.ItemsExportByChanged);
        Assert.True(r.NeedsSave(0));
    }

    [Theory]
    [InlineData("admins", 1)]
    [InlineData("admins", 2)]
    [InlineData("nobody", 1)]
    public void At_revision_1_or_later_nothing_is_touched(string value, int revision)
    {
        var r = ConfigMigration.Migrate(value, revision);
        Assert.Equal(value, r.ItemsExportBy);
        Assert.Equal(revision, r.Revision);
        Assert.False(r.ItemsExportByChanged);
        Assert.False(r.NeedsSave(revision));
    }
}
