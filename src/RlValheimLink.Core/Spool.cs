using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RlValheimLink.Core
{
    public sealed class PendingMessage
    {
        public string Kind;
        public string Key;
        public string CapturedAt;
        public List<string> BlobRefs = new List<string>();
        public string File;        // the JSON payload path
        public DateTime QueuedUtc;
    }

    public sealed class SpoolBlob
    {
        public int ByteLength;
        public int Width;
        public int Height;
        public bool Uploaded;
        public DateTime StoredUtc;
    }

    public enum PutOutcome { Accepted, ReplacedPending, DroppedOlder }

    /// <summary>Where the items export for a (game version, mod set) stands (ROK-2059).</summary>
    public enum ItemsExportStatus { None, Queued, Accepted }

    /// <summary>Permanent rejections of an items export for one (game version, mod set) and when it may be asked for again.</summary>
    public sealed class ItemsRejection
    {
        public int Count;
        public DateTime UntilUtc;
    }

    /// <summary>
    /// Disk-backed queue of pending uploads with latest-wins dedupe (design §5). One file per snapshot
    /// key under pending/, blobs by sha256 under blobs/, bookkeeping in index.json. Survives restarts.
    /// Not thread-safe: the server half calls it from the main thread only.
    /// </summary>
    public sealed class Spool
    {
        private sealed class Index
        {
            public Dictionary<string, string> LastAccepted = new Dictionary<string, string>();
            public Dictionary<string, string> LastUploaded = new Dictionary<string, string>();
            public Dictionary<string, PendingMessage> Pending = new Dictionary<string, PendingMessage>();
            public Dictionary<string, SpoolBlob> Blobs = new Dictionary<string, SpoolBlob>();
            /// <summary>"gameVersion|modSetHash" → capturedAt of the items export Raid Ledger accepted (2xx/409 for that upload).</summary>
            public Dictionary<string, string> ItemsExports = new Dictionary<string, string>();
            /// <summary>"gameVersion|modSetHash" → capturedAt of an items export spooled but not yet accepted (ROK-2059).</summary>
            public Dictionary<string, string> ItemsExportsQueued = new Dictionary<string, string>();
            /// <summary>"gameVersion|modSetHash" → its permanent rejections: no client is asked again before UntilUtc.</summary>
            public Dictionary<string, ItemsRejection> ItemsExportsRejected = new Dictionary<string, ItemsRejection>();
            /// <summary>"gameVersion|modSetHash" (the server's own mod set) → capturedAt of the spooled recipes export.</summary>
            public Dictionary<string, string> RecipesExports = new Dictionary<string, string>();
            /// <summary>Fingerprint of the upload target (instance URL, server id, token) the markers above belong to.</summary>
            public string Target;
        }

        private static readonly Regex Unsafe = new Regex("[^A-Za-z0-9_.-]");
        private readonly string _root, _pendingDir, _blobDir, _indexPath;
        private Index _index = new Index();

        public Spool(string root)
        {
            _root = root;
            _pendingDir = Path.Combine(root, "pending");
            _blobDir = Path.Combine(root, "blobs");
            _indexPath = Path.Combine(root, "index.json");
            Directory.CreateDirectory(_pendingDir);
            Directory.CreateDirectory(_blobDir);
            Load();
        }

        public string Root => _root;
        public int PendingCount => _index.Pending.Count;

        /// <summary>
        /// Items-export markers dropped at load because no upload of theirs was ever accepted: before 0.4.3 the
        /// marker was set when the export was spooled, so a rejected upload left the catalogue missing for good.
        /// </summary>
        public int StaleItemsMarkersCleared { get; private set; }

        private void Load()
        {
            if (!File.Exists(_indexPath)) return;
            try { _index = JsonConvert.DeserializeObject<Index>(File.ReadAllText(_indexPath)) ?? new Index(); }
            catch (Exception) { _index = new Index(); }
            // Drop index entries whose files vanished.
            var gone = new List<string>();
            foreach (var kv in _index.Pending) if (!File.Exists(kv.Value.File)) gone.Add(kv.Key);
            foreach (var k in gone) _index.Pending.Remove(k);
            gone.Clear();
            foreach (var kv in _index.Blobs) if (!kv.Value.Uploaded && !File.Exists(BlobPath(kv.Key))) gone.Add(kv.Key);
            foreach (var k in gone) _index.Blobs.Remove(k);
            if (_index.ItemsExportsQueued == null) _index.ItemsExportsQueued = new Dictionary<string, string>();
            if (_index.ItemsExports == null) _index.ItemsExports = new Dictionary<string, string>();
            if (_index.ItemsExportsRejected == null) _index.ItemsExportsRejected = new Dictionary<string, ItemsRejection>();
            ReconcileItemsMarkers();
        }

        /// <summary>
        /// An accepted marker is only valid when an upload of its spool key at or after its capturedAt was
        /// accepted (<see cref="LastUploaded(string)"/>). Any other is a pre-0.4.3 spool-time marker: back to
        /// queued while its message is still pending, otherwise forgotten so the next eligible client is asked.
        /// </summary>
        private void ReconcileItemsMarkers()
        {
            StaleItemsMarkersCleared = 0;
            foreach (var kv in new List<KeyValuePair<string, string>>(_index.ItemsExports))
            {
                string key = ItemsKeyOfMarker(kv.Key);
                string up = LastUploaded(key);
                if (up != null && Compare(kv.Value, up) <= 0) continue;
                _index.ItemsExports.Remove(kv.Key);
                if (_index.Pending.ContainsKey(key)) _index.ItemsExportsQueued[kv.Key] = kv.Value;
                else StaleItemsMarkersCleared++;
            }
            // A queued marker whose pending message vanished would otherwise read as queued again as soon as
            // another export for the same game version is spooled, and block its own mod set for good.
            int orphans = 0;
            foreach (var kv in new List<KeyValuePair<string, string>>(_index.ItemsExportsQueued))
            {
                if (_index.Pending.ContainsKey(ItemsKeyOfMarker(kv.Key))) continue;
                _index.ItemsExportsQueued.Remove(kv.Key);
                orphans++;
            }
            if (StaleItemsMarkersCleared > 0 || orphans > 0) Save();
        }

        private void Save()
        {
            string tmp = _indexPath + ".tmp";
            File.WriteAllText(tmp, JsonConvert.SerializeObject(_index, Formatting.Indented));
            if (File.Exists(_indexPath)) File.Delete(_indexPath);
            File.Move(tmp, _indexPath);
        }

        private static int Compare(string a, string b) => string.CompareOrdinal(a ?? "", b ?? ""); // ISO-8601 Z timestamps sort lexically

        /// <summary>Latest-wins: an older-or-equal capturedAt for a known key is dropped; a newer one replaces any pending file.</summary>
        public PutOutcome Put(string kind, string key, string capturedAt, byte[] bytes, IEnumerable<string> blobRefs, DateTime nowUtc)
        {
            if (_index.LastAccepted.TryGetValue(key, out var last) && Compare(capturedAt, last) <= 0) return PutOutcome.DroppedOlder;
            bool replaced = _index.Pending.TryGetValue(key, out var old);
            string file = Path.Combine(_pendingDir, Unsafe.Replace(key, "_") + ".json");
            string tmp = file + ".tmp";
            File.WriteAllBytes(tmp, bytes);
            if (File.Exists(file)) File.Delete(file);
            File.Move(tmp, file);
            _index.Pending[key] = new PendingMessage { Kind = kind, Key = key, CapturedAt = capturedAt, BlobRefs = new List<string>(blobRefs), File = file, QueuedUtc = replaced ? old.QueuedUtc : nowUtc };
            _index.LastAccepted[key] = capturedAt;
            Save();
            return replaced ? PutOutcome.ReplacedPending : PutOutcome.Accepted;
        }

        /// <summary>Pending messages, oldest queued first.</summary>
        public List<PendingMessage> Pending()
        {
            var list = new List<PendingMessage>(_index.Pending.Values);
            list.Sort((a, b) => a.QueuedUtc.CompareTo(b.QueuedUtc));
            return list;
        }

        public byte[] Read(PendingMessage m) => File.ReadAllBytes(m.File);

        public void MarkUploaded(PendingMessage m)
        {
            if (_index.Pending.TryGetValue(m.Key, out var cur) && cur.CapturedAt == m.CapturedAt)
            {
                _index.Pending.Remove(m.Key);
                try { File.Delete(m.File); } catch (IOException) { }
            }
            if (!_index.LastUploaded.TryGetValue(m.Key, out var last) || Compare(m.CapturedAt, last) > 0) _index.LastUploaded[m.Key] = m.CapturedAt;
            SettleItemsExports(m, accepted: true, DateTime.UtcNow);
            Save();
        }

        /// <summary>Drop a pending message the server will never accept (400 etc.). The dedupe watermark stays.</summary>
        public void Drop(PendingMessage m) => Drop(m, DateTime.UtcNow);

        /// <summary>
        /// As <see cref="Drop(PendingMessage)"/>. For an items export, returns until when its (game version, mod
        /// set) is not asked for again (<see cref="ItemsRetryBase"/> doubling per rejection up to
        /// <see cref="ItemsRetryMax"/>), or null when no queued export was settled.
        /// </summary>
        public DateTime? Drop(PendingMessage m, DateTime nowUtc)
        {
            if (_index.Pending.TryGetValue(m.Key, out var cur) && cur.CapturedAt == m.CapturedAt)
            {
                _index.Pending.Remove(m.Key);
                try { File.Delete(m.File); } catch (IOException) { }
            }
            var until = SettleItemsExports(m, accepted: false, nowUtc);
            Save();
            return until;
        }

        /// <summary>First wait after a permanent rejection of an items export; doubles per further rejection.</summary>
        public static readonly TimeSpan ItemsRetryBase = TimeSpan.FromHours(1);
        /// <summary>Longest wait between two requests of a deterministically rejected items export.</summary>
        public static readonly TimeSpan ItemsRetryMax = TimeSpan.FromHours(24);

        /// <summary>
        /// Resolves the queued items-export markers this upload answers: its key, captured at or before it (an
        /// older export of another mod set under the same key was replaced by this one, latest wins). Accepted →
        /// the export is done and any rejection record goes; dropped → the marker goes and a cool-down starts,
        /// after which the next eligible client join asks again (ROK-2059).
        /// </summary>
        private DateTime? SettleItemsExports(PendingMessage m, bool accepted, DateTime nowUtc)
        {
            if (m.Kind != "items") return null;
            DateTime? until = null;
            foreach (var kv in new List<KeyValuePair<string, string>>(_index.ItemsExportsQueued))
            {
                if (ItemsKeyOfMarker(kv.Key) != m.Key || Compare(kv.Value, m.CapturedAt) > 0) continue;
                _index.ItemsExportsQueued.Remove(kv.Key);
                if (accepted) { _index.ItemsExports[kv.Key] = kv.Value; _index.ItemsExportsRejected.Remove(kv.Key); continue; }
                var rej = _index.ItemsExportsRejected.TryGetValue(kv.Key, out var r) ? r : new ItemsRejection();
                rej.Count++;
                double hours = Math.Min(ItemsRetryMax.TotalHours, ItemsRetryBase.TotalHours * Math.Pow(2, Math.Min(10, rej.Count - 1)));
                rej.UntilUtc = nowUtc + TimeSpan.FromHours(hours);
                _index.ItemsExportsRejected[kv.Key] = rej;
                if (until == null || rej.UntilUtc > until) until = rej.UntilUtc;
            }
            return until;
        }

        // ---------- blobs ----------

        public string BlobPath(string sha256) => Path.Combine(_blobDir, sha256 + ".png");

        /// <summary>Blobs stored but not yet uploaded, oldest first (a base map arrives before any message references it).</summary>
        public List<string> PendingBlobs()
        {
            var list = new List<KeyValuePair<string, SpoolBlob>>();
            foreach (var kv in _index.Blobs) if (!kv.Value.Uploaded && File.Exists(BlobPath(kv.Key))) list.Add(kv);
            list.Sort((a, b) => a.Value.StoredUtc.CompareTo(b.Value.StoredUtc));
            var shas = new List<string>(list.Count);
            foreach (var kv in list) shas.Add(kv.Key);
            return shas;
        }

        public bool HasBlob(string sha256) => _index.Blobs.TryGetValue(sha256, out var b) && (b.Uploaded || File.Exists(BlobPath(sha256)));
        public bool IsBlobUploaded(string sha256) => _index.Blobs.TryGetValue(sha256, out var b) && b.Uploaded;
        public SpoolBlob Blob(string sha256) => _index.Blobs.TryGetValue(sha256, out var b) ? b : null;

        public void PutBlob(string sha256, byte[] png, int width, int height, DateTime nowUtc)
        {
            if (_index.Blobs.TryGetValue(sha256, out var existing) && existing.Uploaded) return;
            string file = BlobPath(sha256), tmp = file + ".tmp";
            File.WriteAllBytes(tmp, png);
            if (File.Exists(file)) File.Delete(file);
            File.Move(tmp, file);
            _index.Blobs[sha256] = new SpoolBlob { ByteLength = png.Length, Width = width, Height = height, Uploaded = false, StoredUtc = nowUtc };
            Save();
        }

        public byte[] ReadBlob(string sha256) => File.ReadAllBytes(BlobPath(sha256));

        public void MarkBlobUploaded(string sha256)
        {
            if (!_index.Blobs.TryGetValue(sha256, out var b)) return;
            b.Uploaded = true;
            try { File.Delete(BlobPath(sha256)); } catch (IOException) { }
            Save();
        }

        public void DropBlob(string sha256)
        {
            _index.Blobs.Remove(sha256);
            try { File.Delete(BlobPath(sha256)); } catch (IOException) { }
            Save();
        }

        // ---------- items and recipes export bookkeeping ----------

        /// <summary>The spool key of the items export for a game version (latest wins per game version).</summary>
        public static string ItemsKey(string gameVersion) => "items|" + gameVersion;

        private static string ItemsKeyOfMarker(string marker)
        {
            int bar = marker.LastIndexOf('|');
            return ItemsKey(bar < 0 ? marker : marker.Substring(0, bar));
        }

        /// <summary>
        /// Whether a client must NOT be asked for this (game version, mod set): Raid Ledger accepted its export,
        /// or one is spooled and still waiting to upload (transient failures retry it; asking again would only
        /// replace it). False after a permanent rejection, so the next eligible client join asks again.
        /// </summary>
        public bool HasItemsExport(string gameVersion, string modSetHash) => ItemsExportState(gameVersion, modSetHash) != ItemsExportStatus.None;

        public ItemsExportStatus ItemsExportState(string gameVersion, string modSetHash)
        {
            string k = gameVersion + "|" + modSetHash;
            if (_index.ItemsExports.ContainsKey(k)) return ItemsExportStatus.Accepted;
            return _index.ItemsExportsQueued.ContainsKey(k) && _index.Pending.ContainsKey(ItemsKey(gameVersion)) ? ItemsExportStatus.Queued : ItemsExportStatus.None;
        }

        /// <summary>
        /// Whether a joining eligible client should be asked for the items export: none accepted or queued, and
        /// no permanent-rejection cool-down running for this (game version, mod set).
        /// </summary>
        public bool ShouldRequestItems(string gameVersion, string modSetHash, DateTime nowUtc)
        {
            if (HasItemsExport(gameVersion, modSetHash)) return false;
            var until = ItemsRetryAfter(gameVersion, modSetHash);
            return until == null || nowUtc >= until.Value;
        }

        /// <summary>Until when a permanently rejected items export is not asked for again; null when it never was rejected.</summary>
        public DateTime? ItemsRetryAfter(string gameVersion, string modSetHash) =>
            _index.ItemsExportsRejected.TryGetValue(gameVersion + "|" + modSetHash, out var r) ? r.UntilUtc : (DateTime?)null;

        /// <summary>An items export for (game version, mod set) was spooled; it counts as done only once Raid Ledger accepts the upload.</summary>
        public void MarkItemsExportQueued(string gameVersion, string modSetHash, string capturedAt)
        {
            _index.ItemsExportsQueued[gameVersion + "|" + modSetHash] = capturedAt;
            Save();
        }

        public bool HasRecipesExport(string gameVersion, string modSetHash) => _index.RecipesExports.ContainsKey(gameVersion + "|" + modSetHash);

        public void MarkRecipesExport(string gameVersion, string modSetHash, string capturedAt)
        {
            _index.RecipesExports[gameVersion + "|" + modSetHash] = capturedAt;
            Save();
        }

        // ---------- piece-icon atlas map (recipes icons rendered by an admin client) ----------

        private string PieceIconsPath => Path.Combine(_root, "piece-icons.json");

        /// <summary>Keeps the last piece-icon map (<see cref="PieceIconMap.ToJson"/>), replacing the previous one atomically.</summary>
        public void SavePieceIcons(PieceIconMap map)
        {
            string tmp = PieceIconsPath + ".tmp";
            File.WriteAllText(tmp, map.ToJson());
            if (File.Exists(PieceIconsPath)) File.Delete(PieceIconsPath);
            File.Move(tmp, PieceIconsPath);
        }

        /// <summary>The stored piece-icon map, or null when there is none or it no longer parses.</summary>
        public PieceIconMap LoadPieceIcons()
        {
            try { return File.Exists(PieceIconsPath) && PieceIconMap.TryParse(File.ReadAllText(PieceIconsPath), out var m, out _) ? m : null; }
            catch (IOException) { return null; }
        }

        /// <summary>
        /// When the Raid Ledger target changes (instance rebuilt, server id or token changed) nothing
        /// uploaded so far exists there any more: forget the uploaded markers so blobs and the items
        /// and recipes exports are re-sent, and drop blob entries whose files were deleted after upload so a
        /// re-render / re-export re-queues them. Pending messages and the dedupe watermarks stay
        /// (the next capture is newer anyway). Returns true when a reset happened.
        /// </summary>
        public bool ResetIfTargetChanged(string fingerprint)
        {
            if (_index.Target == fingerprint) return false;
            bool first = _index.Target == null;
            _index.Target = fingerprint;
            if (!first)
            {
                _index.LastUploaded.Clear();
                _index.ItemsExports.Clear();
                _index.ItemsExportsRejected.Clear();
                _index.RecipesExports.Clear();
                var gone = new List<string>();
                foreach (var kv in _index.Blobs)
                {
                    if (File.Exists(BlobPath(kv.Key))) kv.Value.Uploaded = false;
                    else gone.Add(kv.Key);
                }
                foreach (var k in gone) _index.Blobs.Remove(k);
            }
            Save();
            return !first;
        }

        public string LastUploaded(string key) => _index.LastUploaded.TryGetValue(key, out var v) ? v : null;
        public string LastAccepted(string key) => _index.LastAccepted.TryGetValue(key, out var v) ? v : null;

        /// <summary>Human summary for status lines.</summary>
        public string Summary()
        {
            int blobsPending = 0; foreach (var b in _index.Blobs.Values) if (!b.Uploaded) blobsPending++;
            return $"pending={_index.Pending.Count} blobsPending={blobsPending} uploadedKeys={_index.LastUploaded.Count}";
        }
    }
}
