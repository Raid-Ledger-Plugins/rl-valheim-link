using RlValheimLink.Core;
using Xunit;

namespace RlValheimLink.Contract.Tests;

public class SpoolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rlvl-spool-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Fact]
    public void Latest_wins_replaces_pending_and_survives_a_restart()
    {
        var now = DateTime.UtcNow;
        var s = new Spool(_dir);
        Assert.Equal(PutOutcome.Accepted, s.Put("character", "character|a|1", "2026-10-18T12:00:00Z", new byte[] { 1 }, Array.Empty<string>(), now));
        Assert.Equal(PutOutcome.DroppedOlder, s.Put("character", "character|a|1", "2026-10-18T11:59:00Z", new byte[] { 2 }, Array.Empty<string>(), now));
        Assert.Equal(PutOutcome.DroppedOlder, s.Put("character", "character|a|1", "2026-10-18T12:00:00Z", new byte[] { 2 }, Array.Empty<string>(), now));
        Assert.Equal(PutOutcome.ReplacedPending, s.Put("character", "character|a|1", "2026-10-18T12:01:00Z", new byte[] { 3 }, Array.Empty<string>(), now.AddSeconds(5)));
        Assert.Single(s.Pending());
        Assert.Equal(new byte[] { 3 }, s.Read(s.Pending()[0]));

        var again = new Spool(_dir);                      // restart
        Assert.Single(again.Pending());
        Assert.Equal("2026-10-18T12:01:00Z", again.LastAccepted("character|a|1"));
        again.MarkUploaded(again.Pending()[0]);
        Assert.Empty(again.Pending());
        Assert.Equal("2026-10-18T12:01:00Z", again.LastUploaded("character|a|1"));
        Assert.Empty(Directory.GetFiles(Path.Combine(_dir, "pending")));
    }

    [Fact]
    public void A_changed_upload_target_forgets_uploaded_markers_but_keeps_dedupe_and_pending()
    {
        var s = new Spool(_dir);
        Assert.False(s.ResetIfTargetChanged("t1")); // first target: nothing to forget
        var sha = new string('c', 64);
        s.PutBlob(sha, new byte[] { 1 }, 1, 1, DateTime.UtcNow);
        s.MarkBlobUploaded(sha);                                     // file deleted after upload
        s.MarkItemsExport("1.0.20", "h", "2026-10-19T20:00:00Z");
        s.Put("character", "character|a|1", "2026-10-19T20:00:00Z", new byte[] { 1 }, Array.Empty<string>(), DateTime.UtcNow);
        s.MarkUploaded(s.Pending()[0]);
        Assert.False(s.ResetIfTargetChanged("t1"));                 // same target: no-op
        Assert.True(s.ResetIfTargetChanged("t2"));                  // rebuilt instance / new token
        Assert.False(s.HasBlob(sha));                                // re-queued by the next render
        Assert.False(s.HasItemsExport("1.0.20", "h"));               // re-requested at the next hello
        Assert.Null(s.LastUploaded("character|a|1"));
        Assert.Equal("2026-10-19T20:00:00Z", s.LastAccepted("character|a|1")); // dedupe watermark stays
        Assert.False(new Spool(_dir).ResetIfTargetChanged("t2"));   // persisted
    }

    [Fact]
    public void Blobs_are_content_addressed_and_items_exports_are_remembered()
    {
        var s = new Spool(_dir);
        var sha = new string('b', 64);
        s.PutBlob(sha, new byte[] { 9, 9 }, 2, 1, DateTime.UtcNow);
        Assert.True(s.HasBlob(sha)); Assert.False(s.IsBlobUploaded(sha));
        s.MarkBlobUploaded(sha);
        Assert.True(s.IsBlobUploaded(sha)); Assert.False(File.Exists(s.BlobPath(sha)));
        Assert.False(s.HasItemsExport("0.221.4", "h"));
        s.MarkItemsExport("0.221.4", "h", "2026-10-18T12:00:00Z");
        Assert.True(new Spool(_dir).HasItemsExport("0.221.4", "h"));
    }
}
