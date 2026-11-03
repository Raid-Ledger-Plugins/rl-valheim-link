using Newtonsoft.Json.Linq;
using Xunit;

namespace RlValheimLink.Contract.Tests;

/// <summary>ROK-1931 `character.deaths[]` + `deathCount`: the pre-send validator matches Zod's superRefine (CONTRACT.md §3, §6).</summary>
public class DeathsValidatorTests
{
    private static JObject Fixture() => (JObject)MessageJson.Parse(File.ReadAllText(Repo.Contract(Path.Combine("fixtures", "character-deaths.json"))));
    private static JArray Deaths(JObject msg) => (JArray)msg["deaths"]!;

    [Fact]
    public void A_body_without_deaths_or_deathCount_is_valid()
    {
        var msg = Fixture();
        msg.Remove("deaths"); msg.Remove("deathCount");
        Assert.Empty(Validator.Validate(msg));
    }

    [Fact]
    public void An_empty_deaths_list_is_valid()
    {
        var msg = Fixture();
        msg["deaths"] = new JArray();
        Assert.Empty(Validator.Validate(msg));
    }

    [Fact]
    public void At_equal_to_capturedAt_in_another_spelling_is_valid()
    {
        // Date.parse compares instants: "20:15:00.000Z" is not after "20:15:00Z".
        var msg = Fixture();
        Deaths(msg)[0]["at"] = "2026-10-17T20:15:00.000Z";
        Assert.Empty(Validator.Validate(msg));
    }

    [Fact]
    public void One_millisecond_after_capturedAt_is_rejected_on_the_at_path()
    {
        var msg = Fixture();
        Deaths(msg)[1]["at"] = "2026-10-17T20:15:00.001Z";
        Assert.Contains(Validator.Validate(msg), i => i.Path == "deaths.1.at" && i.Code == "custom");
    }

    [Fact]
    public void Uniqueness_is_by_exact_string_like_zod()
    {
        var msg = Fixture();
        Deaths(msg)[1]["at"] = "2026-10-17T20:05:12.0Z"; // same instant as deaths.0, different text → not a duplicate
        Assert.Empty(Validator.Validate(msg));
        Deaths(msg)[2]["at"] = "2026-10-17T20:05:12Z";
        var issues = Validator.Validate(msg);
        Assert.Contains(issues, i => i.Path == "deaths.2" && i.Code == "custom");
        Assert.DoesNotContain(issues, i => i.Path == "deaths.0");
    }

    [Fact]
    public void More_than_200_deaths_is_too_big()
    {
        var msg = Fixture();
        var list = new JArray();
        var t = new System.DateTime(2026, 1, 1, 0, 0, 0, System.DateTimeKind.Utc);
        for (int i = 0; i <= Caps.DeathsMax; i++)
            list.Add(new JObject { ["worldUid"] = "1", ["x"] = 0, ["z"] = 0, ["at"] = MessageJson.Timestamp(t.AddMinutes(i)) });
        msg["deaths"] = list;
        Assert.Contains(Validator.Validate(msg), i => i.Path == "deaths" && i.Code == "too_big");
        list.RemoveAt(0);
        Assert.Empty(Validator.Validate(msg));
    }

    [Theory]
    [InlineData(1_000_001, "too_big")]
    [InlineData(2.5, "invalid_type")]
    public void DeathCount_bounds(double value, string code)
    {
        var msg = Fixture();
        msg["deathCount"] = value;
        Assert.Contains(Validator.Validate(msg), i => i.Path == "deathCount" && i.Code == code);
    }

    [Fact]
    public void A_bad_worldUid_and_a_non_iso_at_are_rejected()
    {
        var msg = Fixture();
        Deaths(msg)[0]["worldUid"] = "01";
        Deaths(msg)[1]["at"] = "2026-10-16 18:40:00Z";
        var issues = Validator.Validate(msg);
        Assert.Contains(issues, i => i.Path == "deaths.0.worldUid" && i.Code == "invalid_format");
        Assert.Contains(issues, i => i.Path == "deaths.1.at" && i.Code == "invalid_format");
    }

    [Fact]
    public void The_model_omits_both_fields_when_null_and_writes_them_when_set()
    {
        var m = new CharacterMessage();
        var o = MessageJson.ToToken(m);
        Assert.Null(o["deaths"]); Assert.Null(o["deathCount"]);
        m.Deaths = new List<DeathRecord> { new DeathRecord { WorldUid = "7", X = 1.5, Z = -2, At = "2026-10-17T20:05:12Z" } };
        m.DeathCount = 3;
        o = MessageJson.ToToken(m);
        Assert.Equal("{\"worldUid\":\"7\",\"x\":1.5,\"z\":-2.0,\"at\":\"2026-10-17T20:05:12Z\"}", o["deaths"]![0]!.ToString(Newtonsoft.Json.Formatting.None));
        Assert.Equal(3, (int)o["deathCount"]!);
    }
}
