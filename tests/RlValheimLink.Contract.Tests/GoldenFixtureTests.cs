using Newtonsoft.Json.Linq;
using Xunit;

namespace RlValheimLink.Contract.Tests;

/// <summary>contract/v1/fixtures: every golden message parses clean; every invalid one is rejected for the reason EXPECTED.json names.</summary>
public class GoldenFixtureTests
{
    public static IEnumerable<object[]> Golden() =>
        Directory.GetFiles(Repo.Contract("fixtures"), "*.json").OrderBy(f => f).Select(f => new object[] { Path.GetFileName(f) });

    public static IEnumerable<object[]> Invalid() =>
        Directory.GetFiles(Repo.Contract(Path.Combine("fixtures", "invalid")), "*.json")
            .Where(f => Path.GetFileName(f) != "EXPECTED.json").OrderBy(f => f).Select(f => new object[] { Path.GetFileNameWithoutExtension(f) });

    [Theory, MemberData(nameof(Golden))]
    public void Golden_fixture_validates(string file)
    {
        var issues = Validator.Validate(File.ReadAllText(Repo.Contract(Path.Combine("fixtures", file))));
        Assert.True(issues.Count == 0, string.Join("\n", issues));
    }

    [Theory, MemberData(nameof(Golden))]
    public void Golden_map_bitmaps_inflate_to_exact_size(string file)
    {
        var msg = (JObject)MessageJson.Parse(File.ReadAllText(Repo.Contract(Path.Combine("fixtures", file))));
        if ((string)msg["kind"] != "map") return;
        foreach (var key in new[] { "explored", "sharedExplored" })
        {
            if (msg[key] is not JObject bmp) continue;
            int w = (int)bmp["width"], h = (int)bmp["height"];
            var raw = Bitset.Decode((string)bmp["data"], w, h);
            Assert.Equal(Caps.BitmapBytes(w, h), raw.Length);
        }
    }

    [Theory, MemberData(nameof(Invalid))]
    public void Invalid_fixture_is_rejected_for_the_expected_reason(string name)
    {
        var expected = (JObject)MessageJson.Parse(File.ReadAllText(Repo.Contract(Path.Combine("fixtures", "invalid", "EXPECTED.json"))));
        var want = (JObject)expected[name]!;
        string wantPath = (string)want["path"], wantCode = (string)want["code"];

        var issues = Validator.Validate(File.ReadAllText(Repo.Contract(Path.Combine("fixtures", "invalid", name + ".json"))));
        Assert.NotEmpty(issues);
        Assert.True(issues.Any(i => i.Path == wantPath && i.Code == wantCode),
            $"expected {{ path: '{wantPath}', code: '{wantCode}' }}, got:\n{string.Join("\n", issues)}");
    }

    [Fact]
    public void Every_invalid_fixture_has_an_expectation_and_vice_versa()
    {
        var expected = (JObject)MessageJson.Parse(File.ReadAllText(Repo.Contract(Path.Combine("fixtures", "invalid", "EXPECTED.json"))));
        var names = Invalid().Select(o => (string)o[0]).ToHashSet();
        Assert.Equal(names.OrderBy(n => n), expected.Properties().Select(p => p.Name).OrderBy(n => n));
    }
}
