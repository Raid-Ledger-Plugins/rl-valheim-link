using Newtonsoft.Json.Linq;
using RlValheimLink.Core;
using Xunit;

namespace RlValheimLink.Contract.Tests;

/// <summary>The admin client's piece/station icon map (internal RPC, 0.4.0) and how the server applies it to the recipes export.</summary>
public class PieceIconMapTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rlvl-icons-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    static PieceIconMap Map() => ServerScans.IconMap(FixtureOutputTests.IconAtlasBytes());

    static JObject Wire() => JObject.Parse(Map().ToJson());

    static PieceIconMap Parse(JObject o, out string error)
    {
        PieceIconMap.TryParse(o.ToString(), out var m, out error);
        return m;
    }

    [Fact]
    public void Round_trips_through_the_wire_form()
    {
        var map = Map();
        map.ServerModSet = "abc";
        Assert.True(PieceIconMap.TryParse(map.ToJson(), out var back, out var error), error);
        Assert.Equal(map.GameVersion, back.GameVersion);
        Assert.Equal(map.Atlas.Sha256, back.Atlas.Sha256);
        Assert.Equal((256, 192), (back.Atlas.Width, back.Atlas.Height));
        Assert.Equal(ServerScans.IconPrefabs.Length, back.Icons.Count);
        Assert.Equal(new[] { 64, 0, 64, 64 }, back.Icons["blackforge"]);
        Assert.Equal("abc", back.ServerModSet);
        Assert.DoesNotContain("serverModSet", Map().ToJson());   // the client never sends it
    }

    [Fact]
    public void Bad_icons_are_dropped_one_by_one()
    {
        var o = Wire();
        var icons = (JObject)o["icons"]!;
        icons["outside"] = new JArray(200, 160, 64, 64);           // 264 > 256
        icons["too big"] = new JArray(0, 0, 64, 64);               // not a prefab id
        icons["huge"] = new JArray(0, 0, 513, 1);                  // w > 512
        icons["short"] = new JArray(0, 0, 64);
        icons["bigint"] = new JArray(new JValue(System.Numerics.BigInteger.Parse("99999999999999999999")), 0, 1, 1);
        var map = Parse(o, out var error);
        Assert.NotNull(map);
        Assert.Null(error);
        Assert.Equal(5, map.Rejected);
        Assert.Equal(ServerScans.IconPrefabs.Length, map.Icons.Count);
    }

    [Theory]
    [InlineData("gameVersion", "1.0.20 beta", "gameVersion")]
    [InlineData("atlas.sha256", "ABC", "sha256")]
    [InlineData("atlas.contentType", "image/jpeg", "contentType")]
    [InlineData("atlas.width", "0", "size")]
    public void A_bad_header_refuses_the_map(string path, string value, string expect)
    {
        var o = Wire();
        var parts = path.Split('.');
        JToken target = o;
        for (int i = 0; i < parts.Length - 1; i++) target = target[parts[i]]!;
        target[parts[^1]] = int.TryParse(value, out int n) ? new JValue(n) : new JValue(value);
        Assert.Null(Parse(o, out var error));
        Assert.Contains(expect, error);
    }

    [Fact]
    public void A_map_without_usable_icons_or_json_is_refused()
    {
        var o = Wire();
        o["icons"] = new JObject { ["outside"] = new JArray(1000, 0, 64, 64) };
        Assert.Null(Parse(o, out var error));
        Assert.Contains("no usable icon", error);
        Assert.False(PieceIconMap.TryParse("not json", out _, out error));
        Assert.False(PieceIconMap.TryParse(new byte[] { 0xff, 0xfe }, out _, out _));
    }

    [Fact]
    public void Apply_puts_icons_on_matching_stations_and_pieces_and_references_the_atlas()
    {
        var msg = ServerScans.RecipesServer();
        Assert.Null(msg.Atlas);
        int applied = PieceIconMap.Apply(msg, Map(), out int candidates);
        Assert.Equal(msg.Stations.Count + msg.Pieces.Count, candidates);
        // 5 stations with an icon (all but piece_stonecutter) + 7 pieces.
        Assert.Equal(12, applied);
        Assert.NotNull(msg.Atlas);
        Assert.Null(msg.Stations.Single(s => s.Prefab == "piece_stonecutter").Icon);
        var forge = msg.Pieces.Single(p => p.Prefab == "forge").Icon;
        Assert.Equal((192, 0, 64, 64), (forge.X, forge.Y, forge.W, forge.H));
        Assert.Empty(Validator.Validate(msg));
        Assert.Contains(msg.Atlas.Sha256, Enricher.Seal(msg, "13").BlobRefs);
    }

    [Fact]
    public void Apply_with_no_matching_prefab_leaves_the_message_icon_less()
    {
        var map = Map();
        map.Icons.Clear();
        map.Icons["piece_unknown"] = new[] { 0, 0, 64, 64 };
        var msg = ServerScans.RecipesServer();
        Assert.Equal(0, PieceIconMap.Apply(msg, map, out _));
        Assert.Null(msg.Atlas);
        Assert.Empty(Validator.Validate(msg));
        Assert.Equal(0, PieceIconMap.Apply(msg, null, out _));
    }

    [Fact]
    public void Spool_keeps_the_last_map_across_a_restart()
    {
        var s = new Spool(_dir);
        Assert.Null(s.LoadPieceIcons());
        var map = Map();
        map.ServerModSet = "h1";
        s.SavePieceIcons(map);
        map.ServerModSet = "h2";
        s.SavePieceIcons(map);
        var back = new Spool(_dir).LoadPieceIcons();
        Assert.Equal("h2", back.ServerModSet);
        Assert.Equal(map.Icons.Count, back.Icons.Count);
        File.WriteAllText(Path.Combine(_dir, "piece-icons.json"), "{ broken");
        Assert.Null(new Spool(_dir).LoadPieceIcons());
    }
}
