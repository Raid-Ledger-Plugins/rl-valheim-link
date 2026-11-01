using RlValheimLink.Core;
using Xunit;

namespace RlValheimLink.Contract.Tests;

/// <summary>
/// Core/MapPins (ROK-1881): pin labels reached Raid Ledger with rich-text tags and untrimmed whitespace because
/// the raw <c>m_name</c> was only clipped. The client localises first; these cover what happens after that.
/// </summary>
public class MapPinsTests
{
    [Theory]
    [InlineData("<color=#ff0000>Copper</color>", "Copper")]
    [InlineData("<color=red>Copper</color> vein", "Copper vein")]
    [InlineData("<b>Base</b>", "Base")]
    [InlineData("<i><size=120%>Swamp</size></i> crypt", "Swamp crypt")]
    [InlineData("<sprite=3> Boss", "Boss")]
    [InlineData("<sprite name=\"skull\">Elder", "Elder")]
    [InlineData("<#00ff00>Berries", "Berries")]
    public void Label_StripsRichTextTags(string localized, string expected) =>
        Assert.Equal(expected, MapPins.Label(localized));

    [Theory]
    [InlineData("<3 home", "<3 home")]
    [InlineData("<-- portal", "<-- portal")]
    [InlineData("a < b > c", "a < b > c")]
    public void Label_KeepsPlayerTextThatOnlyLooksLikeATag(string localized, string expected) =>
        Assert.Equal(expected, MapPins.Label(localized));

    [Fact]
    public void Label_KeepsEmoji() => Assert.Equal("Base \U0001F3E0", MapPins.Label("<b>Base \U0001F3E0</b>"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("<b></b>")]
    [InlineData("<color=#fff> </color>")]
    [InlineData("\n\t")]
    public void Label_EmptyAfterStripIsEmptyString(string? localized) => Assert.Equal("", MapPins.Label(localized!));

    [Fact]
    public void Label_TrimsAndFlattensControlCharacters() =>
        Assert.Equal("Iron here", MapPins.Label("  Iron\nhere \t"));

    [Fact]
    public void Label_ClipsAfterStrippingWithoutSplittingASurrogatePair()
    {
        // 63 letters + one astral emoji = 65 UTF-16 units after the tag goes; the cut lands inside the pair.
        string body = new string('a', 63) + "\U0001F600";
        string label = MapPins.Label("<color=red>" + body + "</color>");
        Assert.Equal(new string('a', 63), label);
        Assert.False(char.IsHighSurrogate(label[^1]), "label ends on a lone high surrogate");
    }

    [Fact]
    public void Label_TagsDoNotCountTowardTheCap()
    {
        string body = new string('b', 64);
        Assert.Equal(body, MapPins.Label("<b>" + body + "</b>"));
    }

    [Fact]
    public void Label_NeverExceedsTheContractCap() =>
        Assert.True(MapPins.Label(new string('x', 200)).Length <= MapPins.LabelMax);

    [Theory]
    [InlineData(0L, false)]
    [InlineData(1234567890123L, true)]
    [InlineData(-5L, true)]
    public void IsShared_IsANonZeroOwnerId(long ownerId, bool expected) => Assert.Equal(expected, MapPins.IsShared(ownerId));

    [Theory]
    [InlineData("42", true)]
    [InlineData("-1", true)]
    [InlineData("Icon0", false)]
    [InlineData("Hildir1", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsUnnamedType_OnlyForBareIntegers(string? name, bool expected) => Assert.Equal(expected, MapPins.IsUnnamedType(name!));
}
