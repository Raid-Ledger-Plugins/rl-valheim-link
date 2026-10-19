using System;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace RlValheimLink.Contract
{
    /// <summary>
    /// Minimal PNG writer (8-bit RGBA, no interlace) and IHDR reader. Pure C#, so the
    /// headless server can encode the base map without a graphics device and the tests
    /// can check what Raid Ledger will read from the IHDR.
    /// </summary>
    public static class Png
    {
        private static readonly byte[] Signature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

        /// <summary>Encode top-down RGBA rows (4 bytes per pixel, row 0 = top).</summary>
        public static byte[] EncodeRgba(byte[] rgba, int width, int height)
        {
            if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width));
            if (rgba.Length != (long)width * height * 4) throw new ArgumentException("rgba length must be width*height*4");
            int stride = width * 4;
            // Filter type 2 (Up) per row: cheap, and helps flat biome fills and icon grids.
            var filtered = new byte[(stride + 1) * height];
            for (int y = 0; y < height; y++)
            {
                int src = y * stride, dst = y * (stride + 1);
                filtered[dst] = 2;
                if (y == 0) Buffer.BlockCopy(rgba, src, filtered, dst + 1, stride);
                else
                    for (int i = 0; i < stride; i++)
                        filtered[dst + 1 + i] = (byte)(rgba[src + i] - rgba[src - stride + i]);
            }
            byte[] zlib = ZlibCompress(filtered);

            using (var ms = new MemoryStream())
            {
                ms.Write(Signature, 0, Signature.Length);
                var ihdr = new byte[13];
                WriteBE(ihdr, 0, (uint)width);
                WriteBE(ihdr, 4, (uint)height);
                ihdr[8] = 8; ihdr[9] = 6; ihdr[10] = 0; ihdr[11] = 0; ihdr[12] = 0;
                WriteChunk(ms, "IHDR", ihdr);
                WriteChunk(ms, "IDAT", zlib);
                WriteChunk(ms, "IEND", new byte[0]);
                return ms.ToArray();
            }
        }

        /// <summary>Width and height from the IHDR, as the server reads them before decoding.</summary>
        public static bool TryReadSize(byte[] png, out int width, out int height)
        {
            width = height = 0;
            if (png == null || png.Length < 33) return false;
            for (int i = 0; i < 8; i++) if (png[i] != Signature[i]) return false;
            if (ReadBE(png, 8) != 13) return false;
            if (png[12] != 'I' || png[13] != 'H' || png[14] != 'D' || png[15] != 'R') return false;
            uint w = ReadBE(png, 16), h = ReadBE(png, 20);
            if (w == 0 || h == 0 || w > int.MaxValue || h > int.MaxValue) return false;
            width = (int)w; height = (int)h;
            return true;
        }

        public static BinaryRef MakeRef(byte[] png)
        {
            if (!TryReadSize(png, out int w, out int h)) throw new InvalidDataException("not a PNG");
            return new BinaryRef { Sha256 = Sha256Hex(png), ByteLength = png.Length, ContentType = "image/png", Width = w, Height = h };
        }

        public static string Sha256Hex(byte[] data)
        {
            using (var sha = SHA256.Create())
            {
                var hash = sha.ComputeHash(data);
                var sb = new StringBuilder(64);
                foreach (var b in hash) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

        private static byte[] ZlibCompress(byte[] data)
        {
            using (var ms = new MemoryStream())
            {
                ms.WriteByte(0x78); ms.WriteByte(0x9C);
                using (var def = new DeflateStream(ms, CompressionLevel.Optimal, leaveOpen: true))
                    def.Write(data, 0, data.Length);
                uint a = 1, b = 0;
                foreach (var d in data) { a = (a + d) % 65521; b = (b + a) % 65521; }
                var adler = new byte[4];
                WriteBE(adler, 0, (b << 16) | a);
                ms.Write(adler, 0, 4);
                return ms.ToArray();
            }
        }

        private static void WriteChunk(Stream s, string type, byte[] data)
        {
            var len = new byte[4]; WriteBE(len, 0, (uint)data.Length); s.Write(len, 0, 4);
            var t = Encoding.ASCII.GetBytes(type);
            s.Write(t, 0, 4);
            s.Write(data, 0, data.Length);
            uint crc = Crc32.Update(0xFFFFFFFFu, t, 0, 4);
            crc = Crc32.Update(crc, data, 0, data.Length) ^ 0xFFFFFFFFu;
            var c = new byte[4]; WriteBE(c, 0, crc); s.Write(c, 0, 4);
        }

        private static void WriteBE(byte[] b, int o, uint v) { b[o] = (byte)(v >> 24); b[o + 1] = (byte)(v >> 16); b[o + 2] = (byte)(v >> 8); b[o + 3] = (byte)v; }
        private static uint ReadBE(byte[] b, int o) => ((uint)b[o] << 24) | ((uint)b[o + 1] << 16) | ((uint)b[o + 2] << 8) | b[o + 3];
    }
}
