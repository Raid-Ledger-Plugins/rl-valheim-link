using Newtonsoft.Json.Linq;
using RlValheimLink.Core;
using Xunit;

namespace RlValheimLink.Contract.Tests;

/// <summary>ROK-1931: the death history kept in the profile's custom data and what of it a character message carries.</summary>
public class DeathHistoryTests
{
    static readonly DateTime T0 = new(2026, 10, 17, 20, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Record_puts_the_newest_first_rounds_and_clamps_coordinates()
    {
        var list = new List<DeathRecord>();
        DeathHistory.Record(list, "123", 10.123456, -5.5, T0);
        var d = DeathHistory.Record(list, "123", 99999, -99999, T0.AddMinutes(5));
        Assert.Equal(2, list.Count);
        Assert.Same(d, list[0]);
        Assert.Equal("2026-10-17T20:05:00Z", list[0].At);
        Assert.Equal(Caps.WorldCoordMax, list[0].X); Assert.Equal(-Caps.WorldCoordMax, list[0].Z);
        Assert.Equal(10.12, list[1].X);
    }

    [Fact]
    public void Record_keeps_at_unique_by_moving_a_same_second_death_one_second_on()
    {
        var list = new List<DeathRecord>();
        DeathHistory.Record(list, "1", 0, 0, T0);
        DeathHistory.Record(list, "1", 0, 0, T0.AddMilliseconds(400));
        DeathHistory.Record(list, "1", 0, 0, T0.AddMilliseconds(900));
        Assert.Equal(new[] { "2026-10-17T20:00:02Z", "2026-10-17T20:00:01Z", "2026-10-17T20:00:00Z" }, list.Select(x => x.At));
    }

    [Fact]
    public void Record_caps_the_history_at_200_dropping_the_oldest()
    {
        var list = new List<DeathRecord>();
        for (int i = 0; i < Caps.DeathsMax + 5; i++) DeathHistory.Record(list, "1", i, 0, T0.AddMinutes(i));
        Assert.Equal(Caps.DeathsMax, list.Count);
        Assert.Equal(MessageJson.Timestamp(T0.AddMinutes(Caps.DeathsMax + 4)), list[0].At);
        Assert.Equal(MessageJson.Timestamp(T0.AddMinutes(5)), list[^1].At);
    }

    [Fact]
    public void Record_ignores_a_world_uid_that_is_not_an_int64()
    {
        var list = new List<DeathRecord>();
        Assert.Null(DeathHistory.Record(list, null, 0, 0, T0));
        Assert.Null(DeathHistory.Record(list, "abc", 0, 0, T0));
        Assert.Empty(list);
    }

    [Fact]
    public void Serialize_and_parse_round_trip_and_a_corrupt_store_reads_as_empty()
    {
        var list = new List<DeathRecord>();
        DeathHistory.Record(list, "-42", 1.5, 2.25, T0);
        DeathHistory.Record(list, "7", -3, 4, T0.AddHours(1));
        var back = DeathHistory.Parse(DeathHistory.Serialize(list));
        Assert.Equal(DeathHistory.Serialize(list), DeathHistory.Serialize(back));
        Assert.Empty(DeathHistory.Parse(null));
        Assert.Empty(DeathHistory.Parse("not json"));
        Assert.Single(DeathHistory.Parse("[{\"worldUid\":\"1\",\"x\":0,\"z\":0,\"at\":\"2026-10-17T20:00:00Z\"},{\"x\":\"bad\"},42]"));
    }

    [Fact]
    public void ForMessage_holds_back_deaths_after_capturedAt_and_returns_null_when_none_remain()
    {
        var list = new List<DeathRecord>();
        DeathHistory.Record(list, "1", 0, 0, T0);
        DeathHistory.Record(list, "1", 0, 0, T0.AddMinutes(10));
        var sent = DeathHistory.ForMessage(list, MessageJson.Timestamp(T0.AddMinutes(1)));
        Assert.Equal(new[] { "2026-10-17T20:00:00Z" }, sent!.Select(x => x.At));
        Assert.Null(DeathHistory.ForMessage(list, MessageJson.Timestamp(T0.AddMinutes(-1))));
        Assert.Null(DeathHistory.ForMessage(new List<DeathRecord>(), MessageJson.Timestamp(T0)));
    }

    [Fact]
    public void A_message_built_from_the_history_passes_the_validator()
    {
        var list = new List<DeathRecord>();
        for (int i = 0; i < Caps.DeathsMax; i++) DeathHistory.Record(list, i % 2 == 0 ? "1234567890123" : "987654321", i * 3.33, -i, T0.AddMinutes(i));
        var m = SyntheticCaptures.CharacterVanilla();
        m.Deaths = DeathHistory.ForMessage(list, m.CapturedAt);
        m.DeathCount = 250;
        Assert.Equal(Caps.DeathsMax, m.Deaths!.Count);
        Assert.Empty(Validator.Validate(m));
    }

    [Fact]
    public void DropAfter_removes_entries_later_than_a_server_clamped_capturedAt()
    {
        var deaths = (JArray)MessageJson.Parse("[{\"worldUid\":\"1\",\"x\":0,\"z\":0,\"at\":\"2026-10-18T12:30:00Z\"},{\"worldUid\":\"1\",\"x\":0,\"z\":0,\"at\":\"2026-10-18T11:00:00Z\"}]");
        Assert.Equal(1, DeathHistory.DropAfter(deaths, "2026-10-18T12:00:10Z"));
        Assert.Single(deaths);
        Assert.Equal("2026-10-18T11:00:00Z", (string)deaths[0]["at"]!);
    }
}
