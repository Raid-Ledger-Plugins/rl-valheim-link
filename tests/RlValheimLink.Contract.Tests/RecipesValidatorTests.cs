using Newtonsoft.Json.Linq;
using Xunit;

namespace RlValheimLink.Contract.Tests;

/// <summary>`recipes` rules the contract's fixtures do not exercise (CONTRACT.md §5b, Zod valheim-link-recipes.schema.ts).</summary>
public class RecipesValidatorTests
{
    private static JObject Vanilla() => (JObject)MessageJson.Parse(File.ReadAllText(Repo.Contract(Path.Combine("fixtures", "recipes-vanilla.json"))));

    private static JObject Piece(int i, int resources) => new JObject
    {
        ["prefab"] = "piece_" + i, ["name"] = "Piece " + i, ["category"] = "Misc", ["tool"] = "Hammer",
        ["station"] = null, ["icon"] = null,
        ["resources"] = new JArray(Enumerable.Range(0, resources).Select(j => new JObject { ["prefab"] = "Res" + j, ["amount"] = 1, ["recover"] = true })),
    };

    [Fact]
    public void Resources_and_inputs_summed_over_the_message_are_capped()
    {
        var msg = Vanilla();
        int already = ((JArray)msg["recipes"]!).Sum(r => ((JArray)r["resources"]!).Count) + ((JArray)msg["processes"]!).Sum(p => ((JArray)p["inputs"]!).Count);
        int budget = Caps.RecipeEntriesMax - already;
        var pieces = new JArray(Enumerable.Range(0, budget / Caps.RequirementsMax).Select(i => Piece(i, Caps.RequirementsMax)));
        if (budget % Caps.RequirementsMax > 0) pieces.Add(Piece(pieces.Count, budget % Caps.RequirementsMax));
        msg["pieces"] = pieces;
        Assert.Empty(Validator.Validate(msg));

        pieces.Add(Piece(pieces.Count, 1));
        Assert.Contains(Validator.Validate(msg), i => i.Path == "" && i.Code == "too_big");
    }

    [Fact]
    public void Optional_1_0_20_fields_may_be_absent_but_never_null()
    {
        var msg = Vanilla();
        var station = (JObject)((JArray)msg["stations"]!)[0];
        var resource = (JObject)((JArray)((JArray)msg["recipes"]!)[0]["resources"]!)[0];
        station["upgrader"] = true; resource["upgraderResource"] = false;
        Assert.Empty(Validator.Validate(msg));

        station["upgrader"] = null; resource["upgraderResource"] = null;
        var issues = Validator.Validate(msg);
        Assert.Contains(issues, i => i.Path == "stations.0.upgrader" && i.Code == "invalid_type");
        Assert.Contains(issues, i => i.Path == "recipes.0.resources.0.upgraderResource" && i.Code == "invalid_type");
    }

    [Fact]
    public void Icons_are_atlas_only()
    {
        var msg = Vanilla();
        ((JArray)msg["pieces"]!)[0]["icon"] = new JObject { ["kind"] = "file", ["file"] = "piece_chest.png" };
        var issues = Validator.Validate(msg);
        Assert.Contains(issues, i => i.Path == "pieces.0.icon.kind" && i.Code == "invalid_value");
        Assert.Contains(issues, i => i.Path == "pieces.0.icon" && i.Code == "unrecognized_keys");
    }

    [Fact]
    public void Repair_station_must_resolve_and_null_stations_are_allowed()
    {
        var msg = Vanilla();
        var recipe = (JObject)((JArray)msg["recipes"]!)[0];
        recipe["repairStation"] = "blackforge";
        Assert.Contains(Validator.Validate(msg), i => i.Path == "recipes.0.repairStation" && i.Code == "custom");

        recipe["station"] = null; recipe["repairStation"] = null;
        Assert.Empty(Validator.Validate(msg));
        Assert.Equal(12_582_912, Caps.BodyCapFor("recipes"));
    }
}
