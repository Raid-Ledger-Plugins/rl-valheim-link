using Newtonsoft.Json.Linq;
using Xunit;

namespace RlValheimLink.Contract.Tests;

/// <summary>
/// fixtures/*.json is the mod's own serialised output for synthetic captures. This test proves the
/// committed files are exactly what the writer produces today, and that they pass the validator.
/// Regenerate with: RLVL_WRITE_FIXTURES=1 dotnet test
/// </summary>
public class FixtureOutputTests
{
    public static IEnumerable<object[]> Cases()
    {
        yield return new object[] { "character-vanilla.json", (Func<LinkMessage>)(() => SyntheticCaptures.CharacterVanilla()) };
        yield return new object[] { "character-eaqs.json", (Func<LinkMessage>)(() => SyntheticCaptures.CharacterEaqs()) };
        yield return new object[] { "map-personal.json", (Func<LinkMessage>)(() => SyntheticCaptures.MapPersonal()) };
        yield return new object[] { "map-shared-layer.json", (Func<LinkMessage>)(() => SyntheticCaptures.MapSharedLayer()) };
        yield return new object[] { "items-atlas.json", (Func<LinkMessage>)(() => SyntheticCaptures.ItemsAtlas(AtlasBytes())) };
        yield return new object[] { "items-file-icons.json", (Func<LinkMessage>)(() => SyntheticCaptures.ItemsFileIcons(AtlasBytes())) };
    }

    static bool Writing => Environment.GetEnvironmentVariable("RLVL_WRITE_FIXTURES") == "1";

    /// <summary>The committed atlas PNG; regenerated only in write mode (deflate output may differ between zlib builds).</summary>
    static byte[] AtlasBytes()
    {
        var path = Repo.Fixture("items-atlas.png");
        if (Writing || !File.Exists(path))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, SyntheticCaptures.AtlasPng());
        }
        return File.ReadAllBytes(path);
    }

    [Fact]
    public void Atlas_png_fixture_matches_its_ref()
    {
        var png = AtlasBytes();
        Assert.True(Png.TryReadSize(png, out int w, out int h));
        Assert.Equal((SyntheticCaptures.AtlasPerRow * SyntheticCaptures.AtlasIcon, 2 * SyntheticCaptures.AtlasIcon), (w, h));
        var msg = SyntheticCaptures.ItemsAtlas(png);
        Assert.Equal(Png.Sha256Hex(png), msg.Atlas.Sha256);
        Assert.Equal(png.Length, msg.Atlas.ByteLength);
        Assert.True(png.Length <= Caps.BinaryMaxBytes);
    }

    [Theory, MemberData(nameof(Cases))]
    public void Fixture_matches_writer_output_and_validates(string file, Func<LinkMessage> build)
    {
        var msg = build();
        var issues = Validator.Validate(msg);
        Assert.True(issues.Count == 0, string.Join("\n", issues));

        var bytes = MessageJson.SerializeToBytes(msg);
        Assert.True(bytes.Length <= Caps.BodyCapFor(msg.Kind), $"{file}: {bytes.Length} bytes over the §7 cap");

        var path = Repo.Fixture(file);
        if (Writing)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, bytes);
        }
        Assert.True(File.Exists(path), $"{file} missing: run with RLVL_WRITE_FIXTURES=1");
        var committed = File.ReadAllBytes(path);
        if (committed.AsSpan().SequenceEqual(bytes)) return;
        // A different zlib build may emit a different (still valid) deflate stream for the same bitset:
        // compare map bitmaps by decoded content and everything else byte for byte.
        Assert.True(JToken.DeepEquals(Canonical(committed), Canonical(bytes)),
            $"{file} differs from the writer's output beyond bitmap compression: regenerate with RLVL_WRITE_FIXTURES=1");
    }

    static JToken Canonical(byte[] json)
    {
        var tok = (JObject)MessageJson.Parse(json);
        if ((string)tok["kind"] != "map") return tok;
        foreach (var key in new[] { "explored", "sharedExplored" })
            if (tok[key] is JObject b)
                b["data"] = Png.Sha256Hex(Bitset.Decode((string)b["data"], (int)b["width"], (int)b["height"]));
        return tok;
    }

    [Fact]
    public void File_icon_blob_is_optional_and_omitted_when_absent()
    {
        var msg = SyntheticCaptures.ItemsFileIcons(AtlasBytes());
        var json = MessageJson.Serialize(msg);
        Assert.Contains("\"file\":\"SwordBronze.png\",\"blob\":{\"sha256\":", json);
        Assert.Contains("\"file\":\"Wood.png\"}", json);
        Assert.Empty(Validator.Validate(msg));
        msg.Items[0].Icon.Blob.Sha256 = "nope";
        Assert.Contains(Validator.Validate(msg), i => i.Path == "items.0.icon.blob.sha256" && i.Code == "invalid_format");
    }

    [Fact]
    public void Serializer_writes_nulls_and_keeps_numbers_plain()
    {
        var json = MessageJson.Serialize(SyntheticCaptures.CharacterVanilla());
        Assert.Contains("\"durability\":null", json);
        Assert.Contains("\"durability\":142.25", json);
        Assert.Contains("\"level\":20.0", json);
        Assert.DoesNotContain("\n", json);
        Assert.Contains("\"schemaVersion\":1,\"serverId\":", json);
    }

    [Fact]
    public void Validator_catches_our_own_mistakes_before_send()
    {
        var m = SyntheticCaptures.CharacterVanilla();
        m.Inventory.Items.Add(new InventorySlot { X = 9, Y = 0, Equipped = false, Item = new ItemStack { Prefab = "Stone", Stack = 1, Quality = 1 } });
        Assert.Contains(Validator.Validate(m), i => i.Path == "inventory.items.5" && i.Code == "custom");

        var map = SyntheticCaptures.MapPersonal();
        map.Grid.TextureSize = 1024;
        Assert.Contains(Validator.Validate(map), i => i.Path == "explored" && i.Code == "custom");

        var items = SyntheticCaptures.ItemsAtlas(AtlasBytes());
        items.Atlas = null;
        Assert.Contains(Validator.Validate(items), i => i.Path == "atlas" && i.Code == "custom");

        var nan = SyntheticCaptures.CharacterVanilla();
        nan.Skills[0].Level = double.NaN;
        Assert.NotEmpty(Validator.Validate(nan));
    }
}
