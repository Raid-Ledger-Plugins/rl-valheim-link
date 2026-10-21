using System;
using System.Collections.Generic;
using RlValheimLink.Contract;

namespace RlValheimLink.Net
{
    /// <summary>Transfer kinds. PieceIcons (0.4.0) is the admin client's piece/station icon map for the recipes export; older servers drop it (no cap).</summary>
    public enum PayloadKind : byte { Hello = 0, Character = 1, Map = 2, Items = 3, Blob = 4, PieceIcons = 5 }

    /// <summary>
    /// Chunked transfers over the vanilla routed RPC (docs/design.md §3). Client → server carries
    /// gzip'd message JSON or a raw PNG in ≤ 32 KiB chunks; server → client carries small control
    /// messages. The server never echoes payload bytes back; it only ever sends RLVL_Control.
    /// </summary>
    internal static class LinkChannel
    {
        public const string RpcChunk = "RLVL_Chunk";
        public const string RpcControl = "RLVL_Control";
        public const int Protocol = 1;
        public const int ChunkSize = 32 * 1024;
        /// <summary>Cap of a PieceIcons payload, gzip on the wire and inflated JSON alike (≈ 40 B per icon; 8 192 icons ≈ 330 KB).</summary>
        public const int PieceIconsMaxBytes = 1024 * 1024;
        private const int ChunksPerTick = 8;
        private const int MaxSendQueueBytes = 256 * 1024;
        private const float IncomingTimeoutSeconds = 60f;
        private const int MaxOpenPerSender = 4;
        private const int MaxTransfersPerMinutePerSender = 8;

        /// <summary>Largest payload (compressed bytes on the wire) accepted per kind, server side.</summary>
        public static int MaxPayloadBytes(PayloadKind kind)
        {
            switch (kind)
            {
                case PayloadKind.Hello: return 4096;
                case PayloadKind.Character: return Caps.CharacterMaxBytes;
                case PayloadKind.Map: return Caps.MapMaxBytes;
                case PayloadKind.Items: return Caps.ItemsMaxBytes;
                case PayloadKind.Blob: return Caps.BinaryMaxBytes;
                case PayloadKind.PieceIcons: return PieceIconsMaxBytes;
                default: return 0;
            }
        }

        // ---------- outgoing (client, or host loop-back) ----------

        private sealed class Outgoing
        {
            public int Id; public PayloadKind Kind; public byte[] Payload; public int NextChunk; public int TotalChunks; public string Label;
        }

        private static readonly Queue<Outgoing> SendQueue = new Queue<Outgoing>();
        private static Outgoing _current;
        private static int _nextId = 1;
        private static bool _registered, _isServer;

        /// <summary>Fired on the server for every complete transfer: (sender peer uid, kind, payload bytes).</summary>
        public static event Action<long, PayloadKind, byte[]> PayloadReceived;
        /// <summary>Fired on the client for RLVL_Control: (op, arg).</summary>
        public static event Action<string, string> ControlReceived;

        public static void Register(bool isServer)
        {
            _isServer = isServer;
            var rpc = ZRoutedRpc.instance;
            if (rpc == null) { Log.Error("ZRoutedRpc missing; cannot register RPCs"); return; }
            rpc.Register<ZPackage>(RpcChunk, OnChunk);
            rpc.Register<ZPackage>(RpcControl, OnControl);
            _registered = true;
        }

        public static void Reset()
        {
            SendQueue.Clear(); _current = null; Incoming.Clear(); RateWindow.Clear(); _registered = false;
        }

        public static int QueuedTransfers => SendQueue.Count + (_current == null ? 0 : 1);

        /// <summary>Queue a payload for the server. Returns the transfer id (for log lines).</summary>
        public static int Send(PayloadKind kind, byte[] payload, string label)
        {
            if (!_registered) { Log.Warn($"not registered; dropping {label}"); return -1; }
            var o = new Outgoing { Id = _nextId++, Kind = kind, Payload = payload, TotalChunks = Math.Max(1, (payload.Length + ChunkSize - 1) / ChunkSize), Label = label };
            SendQueue.Enqueue(o);
            Log.Info($"queued {label}: transfer #{o.Id} {kind} {payload.Length} bytes in {o.TotalChunks} chunk(s)");
            return o.Id;
        }

        public static void SendControl(long targetPeer, string op, string arg)
        {
            var pkg = new ZPackage();
            pkg.Write(Protocol); pkg.Write(op); pkg.Write(arg ?? "");
            ZRoutedRpc.instance?.InvokeRoutedRPC(targetPeer, RpcControl, pkg);
        }

        public static void Tick() => Pump(ChunksPerTick, false);

        /// <summary>Largest transfer worth pushing through a closing socket.</summary>
        private const int MaxLogoutTransferBytes = 256 * 1024;

        /// <summary>
        /// Logout/quit: the game saves the profile and tears the connection down in the same frame, so
        /// anything left for a later Update is lost with the socket. Send the small, latest snapshots
        /// now — character first, then map — ignoring the per-frame budget and back-pressure, and
        /// DROP anything big (an items export or a blob): a multi-MB atlas ahead of the character in the
        /// queue starved both and the socket closed with nothing delivered. The server re-requests the
        /// items export on the next join.
        /// </summary>
        public static void FlushForLogout()
        {
            if (QueuedTransfers == 0) return;
            var all = new List<Outgoing>();
            if (_current != null) all.Add(_current);
            all.AddRange(SendQueue);
            _current = null; SendQueue.Clear();

            int Priority(PayloadKind k) => k == PayloadKind.Character ? 0 : k == PayloadKind.Map ? 1 : k == PayloadKind.Hello ? 2 : 3;
            all.Sort((a, b) => Priority(a.Kind).CompareTo(Priority(b.Kind)));
            int sent = 0, dropped = 0;
            foreach (var o in all)
            {
                if (o.Payload.Length > MaxLogoutTransferBytes || Priority(o.Kind) >= 3)
                {
                    dropped++;
                    Log.Info($"logout: {o.Label} ({o.Payload.Length} bytes) not sent — too large for a closing connection; the server asks again next session");
                    continue;
                }
                SendQueue.Enqueue(o);
                sent++;
            }
            Pump(int.MaxValue, true);
            try { ZNet.instance?.GetServerPeer()?.m_socket?.Flush(); } catch (Exception e) { Log.Debug("socket flush: " + e.Message); }
            Log.Info($"logout: flushed {sent} transfer(s) synchronously, dropped {dropped}");
        }

        private static void Pump(int maxChunks, bool ignoreBackPressure)
        {
            if (!_registered) return;
            PruneIncoming();
            if (_current == null && SendQueue.Count == 0) return;
            var rpc = ZRoutedRpc.instance;
            var znet = ZNet.instance;
            if (rpc == null || znet == null) return;
            long target = GameAccess.ServerRoutedId();
            if (target == 0L) return; // not connected yet

            for (int n = 0; n < maxChunks; n++)
            {
                if (_current == null)
                {
                    if (SendQueue.Count == 0) return;
                    _current = SendQueue.Dequeue();
                }
                // Back off while the socket to the server is busy (gameplay traffic first).
                var serverPeer = znet.GetServerPeer();
                if (!ignoreBackPressure && serverPeer != null && serverPeer.m_socket != null && serverPeer.m_socket.GetSendQueueSize() > MaxSendQueueBytes) return;

                int offset = _current.NextChunk * ChunkSize;
                int len = Math.Min(ChunkSize, _current.Payload.Length - offset);
                var bytes = new byte[Math.Max(0, len)];
                if (len > 0) Buffer.BlockCopy(_current.Payload, offset, bytes, 0, len);

                var pkg = new ZPackage();
                pkg.Write(Protocol);
                pkg.Write(_current.Id);
                pkg.Write((byte)_current.Kind);
                pkg.Write(_current.TotalChunks);
                pkg.Write(_current.NextChunk);
                pkg.Write(_current.Payload.Length);
                pkg.Write(bytes);
                rpc.InvokeRoutedRPC(target, RpcChunk, pkg);

                _current.NextChunk++;
                if (_current.TotalChunks >= 40 && _current.NextChunk < _current.TotalChunks && (_current.NextChunk * 4) % _current.TotalChunks < 4)
                    Log.Info($"sending {_current.Label}: {_current.NextChunk * 100 / _current.TotalChunks}% ({_current.NextChunk}/{_current.TotalChunks} chunks)");
                if (_current.NextChunk >= _current.TotalChunks)
                {
                    Log.Info($"sent {_current.Label}: transfer #{_current.Id} complete ({_current.TotalChunks} chunk(s), {_current.Payload.Length} bytes)");
                    _current = null;
                }
            }
        }

        // ---------- incoming (server) ----------

        private sealed class IncomingTransfer
        {
            public PayloadKind Kind; public int TotalChunks; public int Received; public byte[] Buffer; public float LastAt;
        }

        private static readonly Dictionary<long, Dictionary<int, IncomingTransfer>> Incoming = new Dictionary<long, Dictionary<int, IncomingTransfer>>();
        private static readonly Dictionary<long, List<float>> RateWindow = new Dictionary<long, List<float>>();

        private static float Now => UnityEngine.Time.realtimeSinceStartup;

        private static void OnChunk(long sender, ZPackage pkg)
        {
            if (!_isServer) return; // clients never accept payloads
            try
            {
                int protocol = pkg.ReadInt();
                if (protocol != Protocol) { Log.Warn($"peer {sender}: protocol {protocol} != {Protocol}; dropped"); return; }
                int id = pkg.ReadInt();
                var kind = (PayloadKind)pkg.ReadByte();
                int total = pkg.ReadInt();
                int index = pkg.ReadInt();
                int totalBytes = pkg.ReadInt();
                byte[] bytes = pkg.ReadByteArray();

                int cap = MaxPayloadBytes(kind);
                if (cap == 0 || totalBytes < 0 || totalBytes > cap || total <= 0 || total > (cap + ChunkSize - 1) / ChunkSize + 1 || index < 0 || index >= total || bytes.Length > ChunkSize)
                {
                    Log.Warn($"peer {sender}: transfer #{id} {kind} rejected (totalBytes={totalBytes}, chunks={total}, index={index}, chunk={bytes.Length})");
                    return;
                }

                if (!Incoming.TryGetValue(sender, out var open)) Incoming[sender] = open = new Dictionary<int, IncomingTransfer>();
                if (!open.TryGetValue(id, out var t))
                {
                    if (index != 0) return; // late chunk of a transfer we dropped
                    if (open.Count >= MaxOpenPerSender) { Log.Warn($"peer {sender}: too many open transfers; #{id} dropped"); return; }
                    if (!AllowTransfer(sender)) { Log.Warn($"peer {sender}: transfer rate limit; #{id} dropped"); return; }
                    open[id] = t = new IncomingTransfer { Kind = kind, TotalChunks = total, Buffer = new byte[totalBytes], LastAt = Now };
                }
                if (t.Kind != kind || t.TotalChunks != total || index != t.Received) { open.Remove(id); Log.Warn($"peer {sender}: transfer #{id} out of order; dropped"); return; }
                int offset = index * ChunkSize;
                if (offset + bytes.Length > t.Buffer.Length) { open.Remove(id); Log.Warn($"peer {sender}: transfer #{id} overflow; dropped"); return; }
                Buffer.BlockCopy(bytes, 0, t.Buffer, offset, bytes.Length);
                t.Received++; t.LastAt = Now;
                if (t.Received < t.TotalChunks) return;

                open.Remove(id);
                int expectedLast = t.Buffer.Length - (t.TotalChunks - 1) * ChunkSize;
                if (bytes.Length != expectedLast) { Log.Warn($"peer {sender}: transfer #{id} length mismatch; dropped"); return; }
                Log.Info($"received transfer #{id} {kind} from peer {sender}: {t.Buffer.Length} bytes");
                PayloadReceived?.Invoke(sender, kind, t.Buffer);
            }
            catch (Exception e) { Log.Error($"peer {sender}: bad chunk", e); }
        }

        private static bool AllowTransfer(long sender)
        {
            if (!RateWindow.TryGetValue(sender, out var list)) RateWindow[sender] = list = new List<float>();
            float now = Now;
            list.RemoveAll(ts => now - ts > 60f);
            if (list.Count >= MaxTransfersPerMinutePerSender) return false;
            list.Add(now);
            return true;
        }

        private static void PruneIncoming()
        {
            if (Incoming.Count == 0) return;
            float now = Now;
            foreach (var kv in Incoming)
            {
                List<int> stale = null;
                foreach (var t in kv.Value) if (now - t.Value.LastAt > IncomingTimeoutSeconds) (stale ?? (stale = new List<int>())).Add(t.Key);
                if (stale != null) foreach (var id in stale) { kv.Value.Remove(id); Log.Warn($"peer {kv.Key}: transfer #{id} timed out"); }
            }
        }

        private static void OnControl(long sender, ZPackage pkg)
        {
            try
            {
                // Only the server may steer a client.
                if (_isServer || sender != GameAccess.ServerRoutedId()) return;
                int protocol = pkg.ReadInt();
                if (protocol != Protocol) return;
                string op = pkg.ReadString();
                string arg = pkg.ReadString();
                ControlReceived?.Invoke(op, arg);
            }
            catch (Exception e) { Log.Error("bad control message", e); }
        }

        // ---------- helpers ----------

        public static byte[] Gzip(byte[] raw)
        {
            using (var ms = new System.IO.MemoryStream())
            {
                using (var gz = new System.IO.Compression.GZipStream(ms, System.IO.Compression.CompressionLevel.Optimal, true))
                    gz.Write(raw, 0, raw.Length);
                return ms.ToArray();
            }
        }
    }
}
