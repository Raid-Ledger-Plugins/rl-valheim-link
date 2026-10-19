using System;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace RlValheimLink.Core
{
    /// <summary>One request's outcome, reduced to what the state machine needs. Never carries the token.</summary>
    public sealed class UploadResult
    {
        public int StatusCode;              // 0 = transport failure (no response)
        public string Code;                 // Raid Ledger's error code, or "accepted" / "stale" / "stored" / "exists"
        public string Message;              // short, for logs; truncated
        public int RetryAfterSeconds;       // from the header, 0 when absent
        public string Issues;               // schema issues as "path:code, …" when present
        public bool TransportError => StatusCode == 0;
        public bool Success => StatusCode >= 200 && StatusCode < 300;
        public override string ToString() => StatusCode == 0 ? $"transport error: {Message}" : $"{StatusCode} {Code}{(string.IsNullOrEmpty(Message) ? "" : " " + Message)}{(string.IsNullOrEmpty(Issues) ? "" : " [" + Issues + "]")}";
    }

    public sealed class ClientSettings
    {
        public string BaseUrl;
        public string MessagesPath = "/api/plugins/valheim/link/messages/{kind}";
        public string BlobsPath = "/api/plugins/valheim/link/blobs/{sha256}";
        public bool Gzip = true;
        public TimeSpan MessageTimeout = TimeSpan.FromSeconds(30);
        public TimeSpan BlobTimeout = TimeSpan.FromSeconds(120);
        public string UserAgent = "RLValheimLink";
    }

    /// <summary>
    /// The Raid Ledger ingest client (design §6, routes verified against ROK-1833): POST one message per
    /// request (gzip body optional), PUT one PNG per request (identity only). The token is read per
    /// request through a delegate so a rotated token in the config is picked up without a restart.
    /// </summary>
    public sealed class RaidLedgerClient : IDisposable
    {
        private readonly HttpClient _http;
        private readonly ClientSettings _s;
        private readonly Func<string> _token;

        public RaidLedgerClient(ClientSettings settings, Func<string> token, HttpMessageHandler handler = null)
        {
            _s = settings;
            _token = token;
            try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; } catch (NotSupportedException) { }
            _http = handler == null ? new HttpClient() : new HttpClient(handler);
            _http.Timeout = Timeout.InfiniteTimeSpan; // per-request CTS below
            _http.DefaultRequestHeaders.UserAgent.ParseAdd(_s.UserAgent);
        }

        public string MessageUrl(string kind) => _s.BaseUrl.TrimEnd('/') + _s.MessagesPath.Replace("{kind}", kind);
        public string BlobUrl(string sha256) => _s.BaseUrl.TrimEnd('/') + _s.BlobsPath.Replace("{sha256}", sha256);

        public Task<UploadResult> PostMessageAsync(string kind, byte[] json, CancellationToken ct = default)
        {
            var req = new HttpRequestMessage(HttpMethod.Post, MessageUrl(kind));
            byte[] body = json;
            if (_s.Gzip)
            {
                using (var ms = new MemoryStream())
                {
                    using (var gz = new GZipStream(ms, CompressionLevel.Optimal, true)) gz.Write(json, 0, json.Length);
                    body = ms.ToArray();
                }
            }
            var content = new ByteArrayContent(body);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
            if (_s.Gzip) content.Headers.ContentEncoding.Add("gzip");
            req.Content = content;
            return SendAsync(req, _s.MessageTimeout, ct);
        }

        public Task<UploadResult> PutBlobAsync(string sha256, byte[] png, CancellationToken ct = default)
        {
            var req = new HttpRequestMessage(HttpMethod.Put, BlobUrl(sha256));
            var content = new ByteArrayContent(png);
            content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            req.Content = content;
            return SendAsync(req, _s.BlobTimeout, ct);
        }

        private async Task<UploadResult> SendAsync(HttpRequestMessage req, TimeSpan timeout, CancellationToken ct)
        {
            string token = _token() ?? "";
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                cts.CancelAfter(timeout);
                try
                {
                    using (var res = await _http.SendAsync(req, HttpCompletionOption.ResponseContentRead, cts.Token).ConfigureAwait(false))
                    {
                        var r = new UploadResult { StatusCode = (int)res.StatusCode };
                        if (res.Headers.RetryAfter != null && res.Headers.RetryAfter.Delta.HasValue) r.RetryAfterSeconds = (int)Math.Ceiling(res.Headers.RetryAfter.Delta.Value.TotalSeconds);
                        string text = res.Content == null ? "" : await res.Content.ReadAsStringAsync().ConfigureAwait(false);
                        ParseBody(text, r);
                        return r;
                    }
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    return new UploadResult { StatusCode = 0, Code = "timeout", Message = $"no response within {timeout.TotalSeconds:0}s" };
                }
                catch (Exception e)
                {
                    return new UploadResult { StatusCode = 0, Code = "network", Message = Truncate(Innermost(e).Message, 200) };
                }
            }
        }

        private static void ParseBody(string text, UploadResult r)
        {
            if (string.IsNullOrWhiteSpace(text)) { r.Code = r.Success ? "ok" : "no_body"; return; }
            try
            {
                var o = JObject.Parse(text);
                r.Code = (string)(o["status"] as JValue) ?? (string)(o["error"] as JValue) ?? (r.Success ? "ok" : "unknown");
                r.Message = Truncate((string)(o["message"] as JValue), 200);
                if (o["issues"] is JArray issues)
                {
                    var parts = new System.Collections.Generic.List<string>();
                    foreach (var i in issues) if (i is JObject io) parts.Add($"{(string)io["path"]}:{(string)io["code"]}");
                    if (parts.Count > 0) r.Issues = string.Join(", ", parts.ToArray());
                }
                if (o["missingBlobs"] is JArray mb && mb.Count > 0) r.Message = (r.Message ?? "") + $" missingBlobs={mb.Count}";
            }
            catch (Exception) { r.Code = r.Success ? "ok" : "unparseable"; r.Message = Truncate(text, 120); }
        }

        private static Exception Innermost(Exception e) { while (e.InnerException != null) e = e.InnerException; return e; }
        private static string Truncate(string s, int max) => s == null ? null : s.Length <= max ? s : s.Substring(0, max) + "…";

        public void Dispose() => _http.Dispose();
    }
}
