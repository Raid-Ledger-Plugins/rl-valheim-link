using Xunit;

namespace RlValheimLink.Contract.Tests;

public class EncoderTests
{
    [Theory]
    [InlineData(64, 64)]
    [InlineData(2048, 2048)]
    [InlineData(100, 37)]
    public void Bitset_round_trips_and_follows_the_contract_layout(int w, int h)
    {
        var rng = new Random(w * 31 + h);
        var cells = new bool[w * h];
        for (int i = 0; i < cells.Length; i++) cells[i] = rng.Next(7) == 0;
        cells[0] = true;                        // pixel (0,0): byte 0 bit 0
        cells[w * 3 + 5] = true;                // pixel (5,3): bit i = 3*w+5

        var packed = Bitset.Pack(cells, w, h);
        Assert.Equal(Caps.BitmapBytes(w, h), packed.Length);
        Assert.Equal(1, packed[0] & 1);
        int i2 = 3 * w + 5;
        Assert.NotEqual(0, packed[i2 >> 3] & (1 << (i2 & 7)));

        var bmp = Bitset.Encode(cells, w, h);
        Assert.StartsWith("H4sI", bmp.Data);
        Assert.Equal(0, bmp.Data.Length % 4);
        Assert.True(bmp.Data.Length <= Caps.BitmapMaxDataChars(w, h));
        Assert.True(bmp.Data.Length >= Caps.BitmapDataMinChars);

        var raw = Bitset.Decode(bmp.Data, w, h);
        Assert.Equal(packed, raw);
        Assert.Equal(cells, Bitset.Unpack(raw, w, h));
    }

    [Fact]
    public void Bitset_decode_rejects_two_members_and_wrong_length()
    {
        var one = Convert.FromBase64String(Bitset.GzipBase64(new byte[Caps.BitmapBytes(64, 64) / 2]));
        var two = one.Concat(one).ToArray();
        Assert.Throws<System.IO.InvalidDataException>(() => Bitset.Decode(Convert.ToBase64String(two), 64, 64));
        Assert.Throws<System.IO.InvalidDataException>(() => Bitset.Decode(Bitset.GzipBase64(new byte[10]), 64, 64));
        Assert.Throws<System.IO.InvalidDataException>(() => Bitset.Decode("H4sIAAAA", 64, 64));
    }

    [Fact]
    public void Gzip_member_has_a_plain_header()
    {
        var gz = Convert.FromBase64String(Bitset.GzipBase64(new byte[512]));
        Assert.Equal(0x1f, gz[0]); Assert.Equal(0x8b, gz[1]); Assert.Equal(8, gz[2]);
        Assert.Equal(0, gz[3]); // FLG = 0: no name, comment, extra or header CRC
    }

    [Fact]
    public void Png_writes_a_readable_IHDR_and_a_matching_ref()
    {
        int w = 96, h = 40;
        var rgba = new byte[w * h * 4];
        for (int i = 0; i < rgba.Length; i += 4) { rgba[i] = (byte)(i % 251); rgba[i + 1] = 80; rgba[i + 2] = 200; rgba[i + 3] = 255; }
        var png = Png.EncodeRgba(rgba, w, h);
        Assert.True(Png.TryReadSize(png, out int rw, out int rh));
        Assert.Equal((w, h), (rw, rh));
        var r = Png.MakeRef(png);
        Assert.Equal(png.Length, r.ByteLength);
        Assert.Equal(64, r.Sha256.Length);
        Assert.Equal("image/png", r.ContentType);
        Assert.Equal(Png.Sha256Hex(png), r.Sha256);
    }

    [Fact]
    public void Timestamp_is_contract_shaped()
    {
        var ts = MessageJson.Timestamp(new DateTime(2026, 10, 17, 20, 15, 0, DateTimeKind.Utc));
        Assert.Equal("2026-10-17T20:15:00Z", ts);
        Assert.True(MessageJson.TryParseTimestamp("2026-10-17T20:15:00.123Z", out var parsed));
        Assert.Equal(DateTimeKind.Utc, parsed.Kind);
    }
}
