using Newtonsoft.Json.Linq;
using RlValheimLink.Core;
using Xunit;

namespace RlValheimLink.Contract.Tests;

public class EnricherTests
{
    static JObject Client(LinkMessage m) { m.ServerId = "pending"; return MessageJson.ToToken(m); }
    static EnrichContext Ctx() => new() { ServerId = "12", SocketSteamId = "76561198000000042", ZdoPlayerId = 4200000000000042, ServerModVersion = SyntheticCaptures.ModVersion, NowUtc = DateTime.Parse("2026-10-18T12:00:10Z").ToUniversalTime() };

    [Fact]
    public void Sets_serverId_and_identity_from_the_socket_and_keeps_a_sane_capturedAt()
    {
        var msg = SyntheticCaptures.CharacterVanilla();
        msg.Character.SteamId = "76561198000000099"; // a lying client
        var r = Enricher.Enrich(Client(msg), Ctx());
        Assert.True(r.Ok, r.Reason);
        Assert.Equal("character|76561198000000042|4200000000000042", r.Key);
        var o = (JObject)MessageJson.Parse(r.Bytes);
        Assert.Equal("12", (string)o["serverId"]);
        Assert.Equal("76561198000000042", (string)o["character"]["steamId"]);
        Assert.Equal(SyntheticCaptures.CapturedAt, (string)o["capturedAt"]);
        Assert.Contains(r.Notes, n => n.Contains("claim differed"));
        Assert.Empty(Validator.Validate(o));
    }

    [Fact]
    public void Rejects_playerId_mismatch_modVersion_mismatch_and_non_steam_peers()
    {
        var ctx = Ctx(); ctx.ZdoPlayerId = 1;
        Assert.False(Enricher.Enrich(Client(SyntheticCaptures.CharacterVanilla()), ctx).Ok);

        ctx = Ctx(); ctx.ServerModVersion = "9.9.9";
        Assert.Contains("modVersion", Enricher.Enrich(Client(SyntheticCaptures.CharacterVanilla()), ctx).Reason);

        ctx = Ctx(); ctx.SocketSteamId = null;
        Assert.Contains("not a SteamID64", Enricher.Enrich(Client(SyntheticCaptures.CharacterVanilla()), ctx).Reason);

        ctx = Ctx(); ctx.SocketSteamId = null; ctx.TrustClientSteamId = true;
        var trusted = Enricher.Enrich(Client(SyntheticCaptures.CharacterVanilla()), ctx);
        Assert.True(trusted.Ok, trusted.Reason);
        Assert.Equal("76561198000000042", trusted.SteamId);

        var placeholder = SyntheticCaptures.CharacterVanilla(); placeholder.Character.SteamId = Enricher.PlaceholderSteamId;
        Assert.False(Enricher.Enrich(Client(placeholder), ctx).Ok);
    }

    [Fact]
    public void Clamps_a_skewed_clock_and_attaches_the_base_map_when_the_grid_matches()
    {
        var ctx = Ctx(); ctx.NowUtc = DateTime.Parse("2026-10-18T13:00:00Z").ToUniversalTime();
        ctx.BaseMap = new BinaryRef { Sha256 = new string('a', 64), ByteLength = 1000, Width = 2048, Height = 2048 };
        var r = Enricher.Enrich(Client(SyntheticCaptures.MapPersonal()), ctx);
        Assert.True(r.Ok, r.Reason);
        Assert.Equal("2026-10-18T13:00:00Z", r.CapturedAt);
        Assert.Equal("map|76561198000000042|4200000000000042|9876543210987", r.Key);
        Assert.Equal(new[] { new string('a', 64) }, r.BlobRefs);
        var o = (JObject)MessageJson.Parse(r.Bytes);
        Assert.Equal(2048, (int)o["baseMap"]["width"]);
        Assert.Empty(Validator.Validate(o));

        ctx.BaseMap.Width = ctx.BaseMap.Height = 1024;
        r = Enricher.Enrich(Client(SyntheticCaptures.MapPersonal()), ctx);
        Assert.True(r.Ok); Assert.Empty(r.BlobRefs);
    }

    [Fact]
    public void Items_key_and_refs_include_atlas_and_file_icon_blobs()
    {
        var png = File.ReadAllBytes(Repo.Fixture("items-atlas.png"));
        var r = Enricher.Enrich(Client(SyntheticCaptures.ItemsAtlas(png)), Ctx());
        Assert.True(r.Ok, r.Reason);
        Assert.Equal("items|" + SyntheticCaptures.GameVersion, r.Key);
        Assert.Equal(new[] { Png.Sha256Hex(png) }, r.BlobRefs);
        var f = Enricher.Enrich(Client(SyntheticCaptures.ItemsFileIcons(png)), Ctx());
        Assert.Equal(new[] { Png.Sha256Hex(png) }, f.BlobRefs);
    }

    [Fact]
    public void A_message_that_fails_the_contract_after_enrichment_is_rejected()
    {
        var o = Client(SyntheticCaptures.CharacterVanilla());
        o["equipment"]![0]!["slot"] = "tail";
        var r = Enricher.Enrich(o, Ctx());
        Assert.False(r.Ok); Assert.Contains("equipment.0.slot", r.Reason);
    }
}
