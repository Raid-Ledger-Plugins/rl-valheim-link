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
            /// <summary>"gameVersion|modSetHash" → capturedAt of the accepted items export.</summary>
            public Dictionary<string, string> ItemsExports = new Dictionary<string, string>();
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
            Save();
        }

        /// <summary>Drop a pending message the server will never accept (400 etc.). The dedupe watermark stays.</summary>
        public void Drop(PendingMessage m)
        {
            if (_index.Pending.TryGetValue(m.Key, out var cur) && cur.CapturedAt == m.CapturedAt)
            {
                _index.Pending.Remove(m.Key);
                try { File.Delete(m.File); } catch (IOException) { }
                Save();
            }
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

        // ---------- items export bookkeeping ----------

        public bool HasItemsExport(string gameVersion, string modSetHash) => _index.ItemsExports.ContainsKey(gameVersion + "|" + modSetHash);

        public void MarkItemsExport(string gameVersion, string modSetHash, string capturedAt)
        {
            _index.ItemsExports[gameVersion + "|" + modSetHash] = capturedAt;
            Save();
        }

        /// <summary>
        /// When the Raid Ledger target changes (instance rebuilt, server id or token changed) nothing
        /// uploaded so far exists there any more: forget the uploaded markers so blobs and the items
        /// export are re-sent, and drop blob entries whose files were deleted after upload so a
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
