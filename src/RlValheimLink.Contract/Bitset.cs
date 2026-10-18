using System;
using System.IO;
using System.IO.Compression;

namespace RlValheimLink.Contract
{
    /// <summary>
    /// The explored-mask encoding of CONTRACT.md §4: packed bitset (bit i = py·width + px,
    /// byte i >> 3, bit i &amp; 7, LSB first), ONE gzip member with a plain header, padded base64.
    /// </summary>
    public static class Bitset
    {
        /// <summary>Pack a row-major bool array (index py·width + px, py = 0 south) into ⌈w·h/8⌉ bytes.</summary>
        public static byte[] Pack(bool[] cells, int width, int height)
        {
            if (cells == null) throw new ArgumentNullException(nameof(cells));
            long bits = (long)width * height;
            if (cells.Length != bits) throw new ArgumentException($"expected {bits} cells, got {cells.Length}");
            var bytes = new byte[Caps.BitmapBytes(width, height)];
            for (int i = 0; i < cells.Length; i++)
                if (cells[i]) bytes[i >> 3] |= (byte)(1 << (i & 7));
            return bytes;
        }

        public static bool[] Unpack(byte[] bytes, int width, int height)
        {
            long bits = (long)width * height;
            if (bytes.Length != Caps.BitmapBytes(width, height)) throw new ArgumentException("wrong byte count for the mask");
            var cells = new bool[bits];
            for (int i = 0; i < cells.Length; i++)
                cells[i] = (bytes[i >> 3] & (1 << (i & 7))) != 0;
            return cells;
        }

        /// <summary>One gzip member, FLG = 0 (what GZipStream writes), then standard padded base64.</summary>
        public static string GzipBase64(byte[] raw)
        {
            using (var ms = new MemoryStream())
            {
                using (var gz = new GZipStream(ms, CompressionLevel.Optimal, leaveOpen: true))
                    gz.Write(raw, 0, raw.Length);
                return Convert.ToBase64String(ms.ToArray());
            }
        }

        public static ExploredBitmap Encode(bool[] cells, int width, int height)
        {
            return new ExploredBitmap { Width = width, Height = height, Data = GzipBase64(Pack(cells, width, height)) };
        }

        /// <summary>
        /// The server rule of CONTRACT.md §6: bounded inflate, exact length, exactly one gzip member.
        /// Throws <see cref="InvalidDataException"/> on anything else.
        /// </summary>
        public static byte[] Decode(string base64, int width, int height)
        {
            byte[] gz;
            try { gz = Convert.FromBase64String(base64); }
            catch (FormatException e) { throw new InvalidDataException("bad base64", e); }
            int expected = Caps.BitmapBytes(width, height);
            if (gz.Length < 18) throw new InvalidDataException("gzip too short");
            if (gz[0] != 0x1f || gz[1] != 0x8b || gz[2] != 8) throw new InvalidDataException("not a gzip member");
            if (gz[3] != 0) throw new InvalidDataException("gzip FLG must be 0");
            if (gz.Length > Caps.BitmapMaxGzipBytes(width, height)) throw new InvalidDataException("compressed bitmap over the size-tied cap");

            // Single member: raw-inflate the bytes between the 10-byte header and the 8-byte trailer
            // and require the inflater to consume all of them and produce exactly `expected` bytes.
            var output = new byte[expected];
            int produced;
            using (var body = new MemoryStream(gz, 10, gz.Length - 18, writable: false))
            using (var inflate = new DeflateStream(body, CompressionMode.Decompress))
            {
                produced = ReadFully(inflate, output);
                if (produced != expected) throw new InvalidDataException($"inflated {produced} bytes, expected {expected}");
                var one = new byte[1];
                if (inflate.Read(one, 0, 1) != 0) throw new InvalidDataException("inflated output over the limit");
                if (body.Position != body.Length) throw new InvalidDataException("trailing bytes after the deflate stream (second gzip member?)");
            }
            uint crc = BitConverter.ToUInt32(gz, gz.Length - 8);
            uint isize = BitConverter.ToUInt32(gz, gz.Length - 4);
            if (isize != (uint)expected) throw new InvalidDataException("gzip ISIZE trailer mismatch");
            if (crc != Crc32.Compute(output)) throw new InvalidDataException("gzip CRC mismatch");
            return output;
        }

        private static int ReadFully(Stream s, byte[] buf)
        {
            int total = 0;
            while (total < buf.Length)
            {
                int n = s.Read(buf, total, buf.Length - total);
                if (n <= 0) break;
                total += n;
            }
            return total;
        }
    }

    public static class Crc32
    {
        private static readonly uint[] Table = BuildTable();

        private static uint[] BuildTable()
        {
            var t = new uint[256];
            for (uint n = 0; n < 256; n++)
            {
                uint c = n;
                for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                t[n] = c;
            }
            return t;
        }

        public static uint Compute(byte[] data) => Update(0xFFFFFFFFu, data, 0, data.Length) ^ 0xFFFFFFFFu;

        public static uint Update(uint crc, byte[] data, int offset, int count)
        {
            for (int i = offset; i < offset + count; i++) crc = Table[(crc ^ data[i]) & 0xFF] ^ (crc >> 8);
            return crc;
        }
    }
}
