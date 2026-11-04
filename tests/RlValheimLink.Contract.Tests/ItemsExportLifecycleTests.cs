using System.Net;
using System.Text;
using Newtonsoft.Json.Linq;
using RlValheimLink.Core;
using Xunit;

namespace RlValheimLink.Contract.Tests;

/// <summary>
/// ROK-2059: an items export counts as done for (game version, mod set) only once Raid Ledger accepts THAT
/// upload. A permanent rejection lets the next eligible join ask again; a transient failure keeps it queued.
/// </summary>
public class ItemsExportLifecycleTests : IDisposable
{
    private const string Game = "1.0.21", ModSet = "abc123", At = "2026-11-01T10:00:00Z", Token = "tok-ROK2059";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rlvl-items-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private sealed class Answer : HttpMessageHandler
    {
        private readonly HttpStatusCode _status; private readonly string _code;
        public int Calls;
        public Answer(HttpStatusCode status, string code) { _status = status; _code = code; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(_status) { Content = new StringContent($"{{\"code\":\"{_code}\"}}", Encoding.UTF8, "application/json") });
        }
    }

    /// <summary>Spools one items export the way the server half does (ServerHalf.OnMessage).</summary>
    private Spool Spooled()
    {
        var spool = new Spool(_dir);
        Assert.Equal(PutOutcome.Accepted, spool.Put("items", Spool.ItemsKey(Game), At, Encoding.UTF8.GetBytes("{}"), Array.Empty<string>(), DateTime.UtcNow));
        spool.MarkItemsExportQueued(Game, ModSet, At);
        return spool;
    }

    /// <summary>Lets the uploader send the spooled export once and take Raid Ledger's answer.</summary>
    private static async Task<(Uploader up, TestLog log)> UploadOnce(Spool spool, HttpStatusCode status, string code)
    {
        var handler = new Answer(status, code);
        var log = new TestLog();
        var client = new RaidLedgerClient(new ClientSettings { BaseUrl = "http://rl.invalid" }, () => Token, handler);
        var up = new Uploader(spool, client, new UploaderSettings { MinInterval = TimeSpan.Zero }, log);
        for (int i = 0; i < 400 && (handler.Calls == 0 || up.State == UploaderState.Sending); i++) { up.Tick(DateTime.UtcNow); await Task.Delay(5); }
        Assert.True(handler.Calls == 1 && up.State != UploaderState.Sending, "the uploader never got its answer:\n" + log);
        return (up, log);
    }

    [Fact]
    public async Task Accepted_upload_marks_the_export_done_and_it_is_never_requested_again()
    {
        var spool = Spooled();
        Assert.Equal(ItemsExportStatus.Queued, spool.ItemsExportState(Game, ModSet)); // spooled is not done yet
        var (_, log) = await UploadOnce(spool, HttpStatusCode.Accepted, "accepted");
        Assert.Equal(0, spool.PendingCount);
        Assert.Equal(ItemsExportStatus.Accepted, spool.ItemsExportState(Game, ModSet));
        var restarted = new Spool(_dir);
        Assert.Equal(ItemsExportStatus.Accepted, restarted.ItemsExportState(Game, ModSet));
        Assert.Equal(0, restarted.StaleItemsMarkersCleared);
        Assert.False(log.Any("rejected for good"), log.ToString());
    }

    [Theory]
    [InlineData(HttpStatusCode.UnprocessableEntity, "invalid_message")]
    [InlineData(HttpStatusCode.RequestEntityTooLarge, "body_too_large")]
    [InlineData(HttpStatusCode.BadRequest, "kind_mismatch")]
    public async Task Permanent_rejection_forgets_the_export_and_asks_again_only_after_a_cool_down(HttpStatusCode status, string code)
    {
        var spool = Spooled();
        var before = DateTime.UtcNow;
        var (_, log) = await UploadOnce(spool, status, code);
        Assert.Equal(0, spool.PendingCount);
        Assert.False(spool.HasItemsExport(Game, ModSet), $"after a {(int)status} the export must not count as done:\n{log}");
        var until = spool.ItemsRetryAfter(Game, ModSet);
        Assert.True(until.HasValue, $"a {(int)status} must start a cool-down:\n{log}");
        Assert.InRange(until!.Value - before, TimeSpan.FromMinutes(59), TimeSpan.FromMinutes(61));
        Assert.False(spool.ShouldRequestItems(Game, ModSet, until.Value.AddSeconds(-1)), "asked again inside the cool-down");
        Assert.True(spool.ShouldRequestItems(Game, ModSet, until.Value), "not asked again once the cool-down ended");
        var restarted = new Spool(_dir);
        Assert.False(restarted.ShouldRequestItems(Game, ModSet, until.Value.AddSeconds(-1)), "a restart must not reset the cool-down");
        Assert.Single(log.Lines, l => l.Contains("items|1.0.21 was rejected for good") && l.Contains("cool-down 1 h"));
        Assert.DoesNotContain(log.Lines, l => l.Contains(Token));
    }

    [Fact]
    public async Task Conflict_409_counts_as_accepted()
    {
        var spool = Spooled();
        var (_, log) = await UploadOnce(spool, HttpStatusCode.Conflict, "duplicate");
        Assert.Equal(0, spool.PendingCount);
        Assert.True(ItemsExportStatus.Accepted == spool.ItemsExportState(Game, ModSet), $"409 must mark the export done, was {spool.ItemsExportState(Game, ModSet)}:\n{log}");
    }

    /// <summary>Spools a further items export of <paramref name="modSet"/> under the same game version (latest wins).</summary>
    private static void Respool(Spool spool, string modSet, string at)
    {
        spool.Put("items", Spool.ItemsKey(Game), at, Encoding.UTF8.GetBytes("{}"), Array.Empty<string>(), DateTime.UtcNow);
        spool.MarkItemsExportQueued(Game, modSet, at);
    }

    [Fact]
    public void Two_mod_sets_under_one_game_version_settle_by_capture_time_and_cool_down_independently()
    {
        var spool = Spooled();                                   // mod set A at t1
        var inflightA = spool.Pending()[0];
        Respool(spool, "def456", "2026-11-01T11:00:00Z");        // mod set B at t2 replaces A's pending message
        spool.MarkUploaded(inflightA);                           // the older in-flight upload is acked
        Assert.Equal(ItemsExportStatus.Accepted, spool.ItemsExportState(Game, ModSet));
        Assert.True(ItemsExportStatus.Queued == spool.ItemsExportState(Game, "def456"), $"an older ack settled the newer mod set's export: {spool.ItemsExportState(Game, "def456")}");

        var t0 = new DateTime(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc);
        spool.Drop(spool.Pending()[0], t0);                      // B rejected for good
        Assert.False(spool.ShouldRequestItems(Game, "def456", t0.AddMinutes(30)));
        Assert.Null(spool.ItemsRetryAfter(Game, ModSet));         // A untouched by B's rejection
        Respool(spool, "ghi789", "2026-11-01T12:30:00Z");        // mod set C is spooled meanwhile and accepted
        spool.MarkUploaded(spool.Pending()[0]);
        Assert.True(spool.ItemsRetryAfter(Game, "def456").HasValue, "accepting mod set C reset mod set B's cool-down");
    }

    [Fact]
    public void Cool_down_doubles_per_rejection_caps_at_24_h_and_resets_on_accept()
    {
        var spool = Spooled();
        var t0 = new DateTime(2026, 11, 2, 0, 0, 0, DateTimeKind.Utc);
        var expected = new[] { 1, 2, 4, 8, 16, 24, 24 };
        for (int i = 0; i < expected.Length; i++)
        {
            if (i > 0) Respool(spool, ModSet, $"2026-11-01T1{i}:00:00Z");
            var until = spool.Drop(spool.Pending()[0], t0);
            Assert.True(until == t0.AddHours(expected[i]), $"rejection #{i + 1}: cool-down {(until - t0)?.TotalHours} h, expected {expected[i]} h");
        }
        Respool(spool, ModSet, "2026-11-01T18:00:00Z");
        spool.MarkUploaded(spool.Pending()[0]);
        Assert.Null(spool.ItemsRetryAfter(Game, ModSet));
        Respool(spool, ModSet, "2026-11-01T19:00:00Z");
        Assert.True(spool.Drop(spool.Pending()[0], t0) == t0.AddHours(1), "after an accept the next rejection must start over at 1 h");
    }

    [Fact]
    public void A_target_change_clears_the_cool_down()
    {
        var spool = Spooled();
        spool.ResetIfTargetChanged("t1");
        spool.Drop(spool.Pending()[0], DateTime.UtcNow);
        Assert.True(spool.ResetIfTargetChanged("t2"));
        Assert.Null(spool.ItemsRetryAfter(Game, ModSet));
        Assert.True(new Spool(_dir).ShouldRequestItems(Game, ModSet, DateTime.UtcNow));
    }

    [Fact]
    public void Reconcile_requeues_a_pre_fix_marker_whose_export_is_still_pending()
    {
        Spooled();
        // Rewrite the index as 0.4.2 left it: the marker set as done at spool time, no upload accepted yet.
        string path = Path.Combine(_dir, "index.json");
        var idx = JObject.Parse(File.ReadAllText(path));
        idx["ItemsExports"] = idx["ItemsExportsQueued"];
        idx["ItemsExportsQueued"] = new JObject();
        File.WriteAllText(path, idx.ToString());
        var spool = new Spool(_dir);
        Assert.True(ItemsExportStatus.Queued == spool.ItemsExportState(Game, ModSet), $"a still-pending export must go back to queued, was {spool.ItemsExportState(Game, ModSet)}");
        Assert.Equal(0, spool.StaleItemsMarkersCleared);
        Assert.Equal(1, spool.PendingCount);
    }

    [Fact]
    public void A_queued_marker_whose_pending_file_vanished_is_cleared_at_load()
    {
        var first = Spooled();
        File.Delete(first.Pending()[0].File);
        var spool = new Spool(_dir);
        Assert.Equal(ItemsExportStatus.None, spool.ItemsExportState(Game, ModSet));
        Respool(spool, "def456", "2026-11-01T11:00:00Z");        // another mod set's export for the same game version
        Assert.True(ItemsExportStatus.None == spool.ItemsExportState(Game, ModSet), $"the orphan marker came back as {spool.ItemsExportState(Game, ModSet)} and blocks its mod set");
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable, "unavailable")]
    [InlineData(HttpStatusCode.TooManyRequests, "rate_limited")]
    [InlineData(HttpStatusCode.RequestEntityTooLarge, "quota_exceeded")]
    public async Task Transient_failure_keeps_the_export_queued_and_does_not_ask_again(HttpStatusCode status, string code)
    {
        var spool = Spooled();
        var (up, log) = await UploadOnce(spool, status, code);
        Assert.Equal(UploaderState.Backoff, up.State);
        Assert.Equal(1, spool.PendingCount);
        Assert.Equal(ItemsExportStatus.Queued, spool.ItemsExportState(Game, ModSet));
        Assert.True(new Spool(_dir).HasItemsExport(Game, ModSet), $"after a {(int)status} the queued export must not be requested again:\n{log}");
    }

    [Fact]
    public void A_pre_fix_marker_whose_upload_was_never_accepted_is_forgotten_at_load()
    {
        // 0.4.2 and older set the marker when the export was spooled. Index as such a server left it after a 413.
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "index.json"),
            "{\"LastAccepted\":{\"items|1.0.21\":\"" + At + "\",\"items|1.0.20\":\"2026-10-01T10:00:00Z\"}," +
            "\"LastUploaded\":{\"items|1.0.20\":\"2026-10-01T10:00:00Z\"}," +
            "\"ItemsExports\":{\"1.0.21|abc123\":\"" + At + "\",\"1.0.20|abc123\":\"2026-10-01T10:00:00Z\"}}");
        var spool = new Spool(_dir);
        Assert.False(spool.HasItemsExport(Game, ModSet));                                         // rejected upload: ask again
        Assert.Equal(ItemsExportStatus.Accepted, spool.ItemsExportState("1.0.20", ModSet));       // accepted upload: kept
        Assert.Equal(1, spool.StaleItemsMarkersCleared);
    }
}
