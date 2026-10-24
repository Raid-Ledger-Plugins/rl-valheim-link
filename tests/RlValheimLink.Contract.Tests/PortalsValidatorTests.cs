using Newtonsoft.Json.Linq;
using Xunit;

namespace RlValheimLink.Contract.Tests;

/// <summary>`portals` rules the contract's fixtures do not exercise (CONTRACT.md §5c, Zod valheim-link-portals.schema.ts).</summary>
public class PortalsValidatorTests
{
    private static JObject Modded() => (JObject)MessageJson.Parse(File.ReadAllText(Repo.Contract(Path.Combine("fixtures", "portals-modded.json"))));

    [Fact]
    public void Tag_may_be_empty_and_up_to_64_chars()
    {
        var msg = Modded();
        var p = (JObject)((JArray)msg["portals"]!)[0];
        p["tag"] = "";
        Assert.Empty(Validator.Validate(msg));
        p["tag"] = new string('x', Caps.PortalTagMaxChars);
        Assert.Empty(Validator.Validate(msg));
        p["tag"] = new string('x', Caps.PortalTagMaxChars + 1);
        Assert.Contains(Validator.Validate(msg), i => i.Path == "portals.0.tag" && i.Code == "too_big");
    }

    [Fact]
    public void Null_height_is_part_of_the_position_and_minus_zero_is_zero()
    {
        var msg = Modded();
        var list = (JArray)msg["portals"]!;
        var twin = (JObject)list[2].DeepClone();   // y: null
        list.Add(twin);
        Assert.Contains(Validator.Validate(msg), i => i.Path == "portals.3" && i.Code == "custom");

        twin["y"] = 0;                              // same prefab/x/z, different height: allowed
        Assert.Empty(Validator.Validate(msg));
        var zero = (JObject)twin.DeepClone();
        zero["y"] = -0.0;
        list.Add(zero);
        Assert.Contains(Validator.Validate(msg), i => i.Path == "portals.4" && i.Code == "custom");
    }

    [Fact]
    public void Missing_height_is_rejected_and_the_caps_are_the_contracts()
    {
        var msg = Modded();
        ((JObject)((JArray)msg["portals"]!)[0]).Remove("y");
        Assert.Contains(Validator.Validate(msg), i => i.Path == "portals.0.y" && i.Code == "invalid_type");
        Assert.Equal(4_194_304, Caps.BodyCapFor("portals"));
        Assert.Equal(4096, Caps.PortalsMax);
    }
}
