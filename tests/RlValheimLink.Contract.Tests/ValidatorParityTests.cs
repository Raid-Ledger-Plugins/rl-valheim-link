using Newtonsoft.Json.Linq;
using Xunit;

namespace RlValheimLink.Contract.Tests;

/// <summary>Where .NET and Zod 4 differ by default: regex anchors, regex-only lengths, enum/literal codes.</summary>
public class ValidatorParityTests
{
    private static JObject Fixture(string name) => (JObject)MessageJson.Parse(File.ReadAllText(Repo.Contract(Path.Combine("fixtures", name))));

    [Fact]
    public void A_trailing_newline_never_passes_a_pattern()
    {
        // .NET "$" also matches before a final "\n"; JavaScript's does not, so Raid Ledger would reject these.
        var msg = Fixture("recipes-vanilla.json");
        ((JArray)msg["recipes"]!)[0]["id"] = "Recipe_SwordIron\n";
        msg["serverId"] = "srv_fixture_01\n";
        var issues = Validator.Validate(msg);
        Assert.Contains(issues, i => i.Path == "recipes.0.id" && i.Code == "invalid_format");
        Assert.Contains(issues, i => i.Path == "serverId" && i.Code == "invalid_format");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(97)]
    public void Regex_only_strings_report_invalid_format_for_length(int length)
    {
        var msg = Fixture("containers-basic.json");
        ((JArray)msg["containers"]!)[0]["prefab"] = new string('a', length);
        Assert.Contains(Validator.Validate(msg), i => i.Path == "containers.0.prefab" && i.Code == "invalid_format");
    }

    [Fact]
    public void Enums_and_literals_report_invalid_value_even_when_missing()
    {
        var msg = Fixture("recipes-vanilla.json");
        ((JObject)((JArray)msg["recipes"]!)[0]).Remove("formula");
        ((JArray)msg["processors"]!)[0]["type"] = 3;
        ((JArray)msg["pieces"]!)[0]["icon"] = new JObject { ["x"] = 0, ["y"] = 0, ["w"] = 1, ["h"] = 1 };
        var issues = Validator.Validate(msg);
        Assert.Contains(issues, i => i.Path == "recipes.0.formula" && i.Code == "invalid_value");
        Assert.Contains(issues, i => i.Path == "processors.0.type" && i.Code == "invalid_value");
        Assert.Contains(issues, i => i.Path == "pieces.0.icon.kind" && i.Code == "invalid_value");
    }
}
