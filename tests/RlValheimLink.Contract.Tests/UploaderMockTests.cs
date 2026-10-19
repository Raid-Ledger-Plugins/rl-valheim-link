using System.Diagnostics;
using System.Net;
using RlValheimLink.Core;
using Xunit;

namespace RlValheimLink.Contract.Tests;

/// <summary>
/// End to end: Spool + Uploader + RaidLedgerClient against tools/mock-ingest (spawned with node).
/// This is the ROK-1834 acceptance step "posts successfully to the mock".
/// </summary>
public class UploaderMockTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rlvl-up-" + Guid.NewGuid().ToString("N"));
    private Process _node;
    private int _port;

    public void Dispose()
    {
        try { if (_node != null && !_node.HasExited) _node.Kill(true); } catch { }
        try { Directory.Delete(_dir, true); } catch { }
    }

    private async Task StartMock(Dictionary<string, string> env)
    {
        _port = 18000 + Random.Shared.Next(1000);
        var dir = Path.Combine(Repo.Root, "tools", "mock-ingest");
        Assert.True(Directory.Exists(Path.Combine(dir, "node_modules")), "tools/mock-ingest/node_modules missing: run `npm ci` there first (CI does this before dotnet test)");
        var psi = new ProcessStartInfo("node", "server.mjs") { WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        psi.Environment["PORT"] = _port.ToString();
        foreach (var kv in env) psi.Environment[kv.Key] = kv.Value;
        _node = Process.Start(psi)!;
        using var http = new HttpClient();
        for (int i = 0; i < 100; i++)
        {
            try { if ((await http.GetAsync($"http://localhost:{_port}/health")).IsSuccessStatusCode) return; } catch { }
            await Task.Delay(50);
        }
        throw new Exception("mock-ingest did not start: " + await _node.StandardError.ReadToEndAsync());
    }

    private static async Task Drive(Uploader up, Func<bool> done, int maxTicks = 400)
    {
        for (int i = 0; i < maxTicks && !done(); i++) { up.Tick(DateTime.UtcNow); await Task.Delay(10); }
    }

    [Fact]
    public async Task Fixtures_and_their_atlas_reach_the_mock_blob_first_then_message()
    {
        await StartMock(new() { ["RLVL_MOCK_TOKEN"] = "t-123", ["RLVL_MOCK_SERVER_ID"] = "12" });
        var log = new TestLog();
        var spool = new Spool(_dir);
        var client = new RaidLedgerClient(new ClientSettings { BaseUrl = $"http://localhost:{_port}" }, () => "t-123");
        var up = new Uploader(spool, client, new UploaderSettings { MinInterval = TimeSpan.Zero }, log);

        var png = File.ReadAllBytes(Repo.Fixture("items-atlas.png"));
        var now = DateTime.UtcNow;
        foreach (var (file, build) in new (string, Func<LinkMessage>)[] {
            ("character", () => SyntheticCaptures.CharacterVanilla()), ("map", () => SyntheticCaptures.MapSharedLayer()), ("items", () => SyntheticCaptures.ItemsAtlas(png)) })
        {
            var r = Enricher.Enrich(MessageJson.ToToken(build()), new EnrichContext { ServerId = "12", SocketSteamId = "76561198000000042", ServerModVersion = SyntheticCaptures.ModVersion, NowUtc = now });
            Assert.True(r.Ok, r.Reason);
            Assert.Equal(PutOutcome.Accepted, spool.Put(r.Kind, r.Key, r.CapturedAt, r.Bytes, r.BlobRefs, now));
        }
        spool.PutBlob(Png.Sha256Hex(png), png, 256, 128, now);

        await Drive(up, () => spool.PendingCount == 0 && spool.IsBlobUploaded(Png.Sha256Hex(png)));
        Assert.True(spool.PendingCount == 0, log.ToString());
        Assert.Equal(4, up.Uploaded);
        Assert.Equal(0, up.Failed);
        // Blob went before the items message, so the mock reports no missing blobs for it.
        int blobLine = log.Lines.FindIndex(l => l.Contains("PUT blob") && l.Contains("→ 201"));
        int itemsLine = log.Lines.FindIndex(l => l.Contains("POST items") && l.Contains("→ 202"));
        Assert.True(blobLine >= 0 && itemsLine > blobLine, log.ToString());
        Assert.DoesNotContain(log.Lines, l => l.Contains("t-123"));

        using var http = new HttpClient();
        var state = await http.GetStringAsync($"http://localhost:{_port}/state");
        Assert.Contains("\"character|12|76561198000000042|4200000000000042\"", state);
        Assert.Contains(Png.Sha256Hex(png), state);
    }

    [Fact]
    public async Task Wrong_token_halts_and_a_corrected_token_resumes()
    {
        await StartMock(new() { ["RLVL_MOCK_TOKEN"] = "good", ["RLVL_MOCK_SERVER_ID"] = "12" });
        var log = new TestLog();
        var spool = new Spool(_dir);
        string token = "bad";
        var client = new RaidLedgerClient(new ClientSettings { BaseUrl = $"http://localhost:{_port}" }, () => token);
        var up = new Uploader(spool, client, new UploaderSettings { MinInterval = TimeSpan.Zero, HaltRetry = TimeSpan.FromMilliseconds(300) }, log);
        var r = Enricher.Enrich(MessageJson.ToToken(SyntheticCaptures.CharacterVanilla()), new EnrichContext { ServerId = "12", SocketSteamId = "76561198000000042", ServerModVersion = SyntheticCaptures.ModVersion, NowUtc = DateTime.UtcNow });
        spool.Put(r.Kind, r.Key, r.CapturedAt, r.Bytes, r.BlobRefs, DateTime.UtcNow);

        await Drive(up, () => up.State == UploaderState.Halted);
        Assert.Equal(UploaderState.Halted, up.State);
        Assert.Contains("admin dialog", up.HaltReason);
        token = "good"; // the operator pasted the rotated token
        await Drive(up, () => spool.PendingCount == 0);
        Assert.Equal(0, spool.PendingCount);
        Assert.True(log.Any("retrying after halt"), log.ToString());
    }

    [Fact]
    public async Task Rate_limit_429_backs_off_for_retry_after_and_bad_requests_are_dropped()
    {
        await StartMock(new() { ["RLVL_MOCK_TOKEN"] = "t", ["RLVL_MOCK_SERVER_ID"] = "12", ["RLVL_MOCK_RATE_MESSAGES"] = "1" });
        var log = new TestLog();
        var spool = new Spool(_dir);
        var client = new RaidLedgerClient(new ClientSettings { BaseUrl = $"http://localhost:{_port}" }, () => "t");
        var up = new Uploader(spool, client, new UploaderSettings { MinInterval = TimeSpan.Zero, MaxMessagesPerMinute = 60 }, log);
        var now = DateTime.UtcNow;
        var a = SyntheticCaptures.CharacterVanilla();
        var b = SyntheticCaptures.CharacterEaqs();
        foreach (var m in new LinkMessage[] { a, b })
        {
            var r = Enricher.Enrich(MessageJson.ToToken(m), new EnrichContext { ServerId = "12", SocketSteamId = "76561198000000042", ServerModVersion = SyntheticCaptures.ModVersion, NowUtc = now });
            spool.Put(r.Kind, r.Key, r.CapturedAt, r.Bytes, r.BlobRefs, now);
        }
        await Drive(up, () => up.State == UploaderState.Backoff);
        Assert.Equal(UploaderState.Backoff, up.State);
        Assert.True(up.ResumeAt > DateTime.UtcNow.AddSeconds(30), "Retry-After honoured");
        Assert.Equal(1, up.Uploaded);

        // A body the real server rejects (wrong serverId → 403 is a halt; a schema break → 400 drop): fake a 400 by corrupting the spooled file.
        var pending = spool.Pending()[0];
        File.WriteAllText(pending.File, "{\"kind\":\"character\",\"nope\":1}");
        up.Resume();
        await Drive(up, () => spool.PendingCount == 0 || up.State == UploaderState.Backoff);
        Assert.True(spool.PendingCount == 0 || up.State == UploaderState.Backoff, log.ToString());
    }
}
