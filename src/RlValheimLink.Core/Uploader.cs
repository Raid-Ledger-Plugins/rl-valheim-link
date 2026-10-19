using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace RlValheimLink.Core
{
    public sealed class UploaderSettings
    {
        public TimeSpan MinInterval = TimeSpan.FromSeconds(5);
        public int MaxMessagesPerMinute = 30;
        public int MaxBlobsPerMinute = 10;
        public int MaxBlobBytes = 10 * 1024 * 1024;
        public TimeSpan BackoffBase = TimeSpan.FromSeconds(30);
        public TimeSpan BackoffMax = TimeSpan.FromMinutes(10);
        public TimeSpan HaltRetry = TimeSpan.FromMinutes(10);
        public TimeSpan QuotaRetry = TimeSpan.FromMinutes(10);
    }

    public enum UploaderState { Idle, Sending, Backoff, Halted }

    /// <summary>
    /// The upload state machine of design §6, driven by <see cref="Tick"/> from the game's main
    /// thread: one request in flight at a time, blob before the message that references it, budgets,
    /// backoff on 429/5xx/transport, halt on 401/403 with a periodic retry so a pasted-in token or
    /// corrected ServerId is picked up. All decisions are synchronous; only the HTTP call is async.
    /// </summary>
    public sealed class Uploader
    {
        private readonly Spool _spool;
        private readonly RaidLedgerClient _client;
        private readonly UploaderSettings _s;
        private readonly ILinkLog _log;
        private readonly List<DateTime> _msgWindow = new List<DateTime>(), _blobWindow = new List<DateTime>();

        private Task<UploadResult> _inflight;
        private PendingMessage _inflightMessage;
        private string _inflightBlob;
        private DateTime _inflightStarted;
        private DateTime _lastRequest = DateTime.MinValue;
        private DateTime _resumeAt = DateTime.MinValue;
        private int _failures;
        private UploaderState _state = UploaderState.Idle;
        private string _haltReason;

        public Uploader(Spool spool, RaidLedgerClient client, UploaderSettings settings, ILinkLog log)
        {
            _spool = spool; _client = client; _s = settings; _log = log;
        }

        public UploaderState State => _state;
        public string HaltReason => _haltReason;
        public int Uploaded { get; private set; }
        public int Failed { get; private set; }
        public DateTime ResumeAt => _resumeAt;

        /// <summary>Clears a halt (e.g. after the config changed) so the next tick tries again.</summary>
        public void Resume() { if (_state == UploaderState.Halted || _state == UploaderState.Backoff) { _state = UploaderState.Idle; _resumeAt = DateTime.MinValue; } }

        public void Tick(DateTime nowUtc)
        {
            if (_inflight != null)
            {
                if (!_inflight.IsCompleted) return;
                var task = _inflight; _inflight = null;
                var result = task.IsFaulted ? new UploadResult { StatusCode = 0, Code = "fault", Message = task.Exception?.GetBaseException().Message } : task.Result;
                OnResult(result, nowUtc);
                return;
            }
            if ((_state == UploaderState.Backoff || _state == UploaderState.Halted) && nowUtc < _resumeAt) return;
            if (_state == UploaderState.Halted) _log.Info("retrying after halt: " + _haltReason);
            _state = UploaderState.Idle;
            if (nowUtc - _lastRequest < _s.MinInterval) return;

            Prune(_msgWindow, nowUtc); Prune(_blobWindow, nowUtc);
            foreach (var m in _spool.Pending())
            {
                // Blobs the message references go first (either order is accepted upstream, but this way a message never lands "missing" its atlas).
                foreach (var sha in m.BlobRefs)
                {
                    if (!_spool.HasBlob(sha) || _spool.IsBlobUploaded(sha)) continue;
                    if (_blobWindow.Count >= _s.MaxBlobsPerMinute) break;
                    var blob = _spool.Blob(sha);
                    if (blob.ByteLength > _s.MaxBlobBytes) { _log.Warn($"blob {Short(sha)} is {blob.ByteLength} bytes > Upload.MaxBlobBytes {_s.MaxBlobBytes}; dropped"); _spool.DropBlob(sha); continue; }
                    Start(nowUtc, null, sha, _client.PutBlobAsync(sha, _spool.ReadBlob(sha)));
                    _blobWindow.Add(nowUtc);
                    return;
                }
                if (_msgWindow.Count >= _s.MaxMessagesPerMinute) return;
                byte[] bytes;
                try { bytes = _spool.Read(m); }
                catch (Exception e) { _log.Error($"cannot read spooled {m.Key}: {e.Message}; dropped"); _spool.Drop(m); continue; }
                Start(nowUtc, m, null, _client.PostMessageAsync(m.Kind, bytes));
                _msgWindow.Add(nowUtc);
                return;
            }
        }

        private void Start(DateTime now, PendingMessage m, string sha, Task<UploadResult> task)
        {
            _inflight = task; _inflightMessage = m; _inflightBlob = sha; _inflightStarted = now; _lastRequest = now;
            _state = UploaderState.Sending;
            _log.Debug(m != null ? $"POST {m.Kind} {m.Key} capturedAt={m.CapturedAt}" : $"PUT blob {Short(sha)}");
        }

        private void OnResult(UploadResult r, DateTime now)
        {
            string what = _inflightMessage != null ? $"{_inflightMessage.Kind} {_inflightMessage.Key}" : $"blob {Short(_inflightBlob)}";
            long ms = (long)(now - _inflightStarted).TotalMilliseconds;
            var m = _inflightMessage; string sha = _inflightBlob;
            _inflightMessage = null; _inflightBlob = null;

            if (r.Success)
            {
                _failures = 0; Uploaded++;
                _state = UploaderState.Idle;
                if (m != null) _spool.MarkUploaded(m); else _spool.MarkBlobUploaded(sha);
                _log.Info($"{(m != null ? "POST" : "PUT")} {what} → {r} in {ms} ms");
                return;
            }
            Failed++;
            switch (r.StatusCode)
            {
                case 401:
                    Halt(now, $"401 {r.Code}: the per-server token was rejected (rotated?). Paste the current token from the Raid Ledger admin dialog into RaidLedger.ServerToken.");
                    break;
                case 403:
                    Halt(now, $"403 {r.Code}: RaidLedger.ServerId does not match the token's server. Copy the id shown next to the token in the admin dialog.");
                    break;
                case 429:
                    Backoff(now, TimeSpan.FromSeconds(Math.Max(1, Math.Min(600, r.RetryAfterSeconds > 0 ? r.RetryAfterSeconds : 60))), $"429 rate limited on {what}");
                    break;
                case 413 when r.Code == "quota_exceeded":
                    _state = UploaderState.Backoff; _resumeAt = now + _s.QuotaRetry;
                    _log.Warn($"{what} → {r}: this server's storage quota on Raid Ledger is full; retrying in {_s.QuotaRetry.TotalMinutes:0} min (an admin must raise the quota or prune)");
                    break;
                case 400:
                case 404:
                case 413:
                case 415:
                case 422:
                    _state = UploaderState.Idle;
                    _log.Error($"{what} → {r}: dropped (a mod bug, a contract drift or a route mismatch — check contract-drift and the RaidLedger.*Path settings)");
                    if (m != null) _spool.Drop(m); else _spool.DropBlob(sha);
                    break;
                case 409:
                    _state = UploaderState.Idle; _log.Info($"{what} → {r}: treated as done");
                    if (m != null) _spool.MarkUploaded(m); else _spool.MarkBlobUploaded(sha);
                    break;
                default:
                    _failures++;
                    double seconds = Math.Min(_s.BackoffMax.TotalSeconds, _s.BackoffBase.TotalSeconds * Math.Pow(2, Math.Min(10, _failures - 1)));
                    Backoff(now, TimeSpan.FromSeconds(seconds), $"{what} → {r} (failure #{_failures})");
                    break;
            }
        }

        private void Backoff(DateTime now, TimeSpan delay, string why)
        {
            _state = UploaderState.Backoff; _resumeAt = now + delay;
            _log.Warn($"{why}; backing off {delay.TotalSeconds:0} s");
        }

        private void Halt(DateTime now, string reason)
        {
            _state = UploaderState.Halted; _haltReason = reason; _resumeAt = now + _s.HaltRetry;
            _log.Error($"uploads HALTED: {reason} Retrying in {_s.HaltRetry.TotalMinutes:0} min.");
        }

        private static void Prune(List<DateTime> window, DateTime now) => window.RemoveAll(t => (now - t).TotalSeconds > 60);
        private static string Short(string sha) => string.IsNullOrEmpty(sha) ? "?" : sha.Length > 12 ? sha.Substring(0, 12) + "…" : sha;
    }
}
