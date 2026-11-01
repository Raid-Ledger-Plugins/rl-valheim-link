using System.Net;
using System.Text;
using RlValheimLink.Core;
using Xunit;

namespace RlValheimLink.Contract.Tests;

/// <summary>
/// ROK-2059: an items export counts as done for (game version, mod set) only once Raid Ledger accepts THAT
/// upload. A permanent rejection lets the next eligible join ask again; a transient failure keeps it queued.
/// </summary>
public class ItemsExportLifecycleTests : IDisposable
{
    private const string Game = "1.0.21", ModSet = "abc123", At = "2026-11-01T10:00:00Z";
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
        var client = new RaidLedgerClient(new ClientSettings { BaseUrl = "http://rl.invalid" }, () => "t", handler);
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
    public async Task Permanent_rejection_forgets_the_export_so_the_next_eligible_join_asks_again(HttpStatusCode status, string code)
    {
        var spool = Spooled();
        var (_, log) = await UploadOnce(spool, status, code);
        Assert.Equal(0, spool.PendingCount);
        Assert.False(spool.HasItemsExport(Game, ModSet), $"after a {(int)status} the export must not count as done:\n{log}");
        Assert.False(new Spool(_dir).HasItemsExport(Game, ModSet));
        Assert.True(log.Any("items|1.0.21 was rejected for good"), log.ToString());
        Assert.DoesNotContain(log.Lines, l => l.Contains("Bearer"));
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
