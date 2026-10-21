using System.Buffers.Binary;
using OneImage.Tests.Png;

namespace OneImage.Tests.Bmp;

public class BmpVariantTests
{
    private static byte[] Rows(int w, int h, int bytesPerRowUnpadded, bool topDown, Func<int, int, byte[]> rowBytes)
    {
        int stride = (bytesPerRowUnpadded + 3) / 4 * 4;
        byte[] data = new byte[stride * h];
        for (int y = 0; y < h; y++)
        {
            int fileRow = topDown ? y : h - 1 - y;
            rowBytes(y, w).CopyTo(data, fileRow * stride);
        }

        return data;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TwentyFourBitBothDirectionsWithPadding(bool topDown)
    {
        foreach (int w in new[] { 1, 2, 3, 5, 8 })
        {
            const int H = 4;
            byte[] expected = new byte[w * H * 3];
            byte[] px = Rows(w, H, w * 3, topDown, (y, ww) =>
            {
                byte[] row = new byte[ww * 3];
                for (int x = 0; x < ww; x++)
                {
                    byte r = (byte)((x * 40) + y), g = (byte)((y * 60) + x), b = (byte)(x ^ y);
                    row[x * 3] = b;
                    row[(x * 3) + 1] = g;
                    row[(x * 3) + 2] = r;
                    expected[((y * ww) + x) * 3] = r;
                    expected[(((y * ww) + x) * 3) + 1] = g;
                    expected[(((y * ww) + x) * 3) + 2] = b;
                }

                return row;
            });
            byte[] file = BmpTestUtil.File(BmpTestUtil.InfoHeader(40, w, topDown ? -H : H, 24, 0), [], px);
            Decoded d = BmpTestUtil.DecodeBmp(file);
            Assert.Equal(PixelFormat.Rgb8, d.Format);
            Assert.Equal(expected, d.Data);
        }
    }

    [Fact]
    public void SixteenBitFormats()
    {
        const int W = 9, H = 3;
        var rnd = new Random(11);
        ushort[] raw = Enumerable.Range(0, W * H).Select(_ => (ushort)rnd.Next(65536)).ToArray();
        byte[] pixels = Rows(W, H, W * 2, false, (y, w) =>
        {
            byte[] r = new byte[w * 2];
            for (int x = 0; x < w; x++)
            {
                BinaryPrimitives.WriteUInt16LittleEndian(r.AsSpan(x * 2), raw[(y * w) + x]);
            }

            return r;
        });

        // BI_RGB 16 bits is 5-5-5.
        Decoded d555 = BmpTestUtil.DecodeBmp(BmpTestUtil.File(BmpTestUtil.InfoHeader(40, W, H, 16, 0), [], pixels));
        // 5-6-5 with BI_BITFIELDS and masks after the 40-byte header.
        byte[] masks565 = new byte[12];
        BinaryPrimitives.WriteUInt32LittleEndian(masks565, 0xF800);
        BinaryPrimitives.WriteUInt32LittleEndian(masks565.AsSpan(4), 0x07E0);
        BinaryPrimitives.WriteUInt32LittleEndian(masks565.AsSpan(8), 0x001F);
        Decoded d565 = BmpTestUtil.DecodeBmp(BmpTestUtil.File(BmpTestUtil.InfoHeader(40, W, H, 16, 3), masks565, pixels));
        // 4-4-4-4 with alpha in a V4 header.
        byte[] v4 = BmpTestUtil.InfoHeader(108, W, H, 16, 3);
        BmpTestUtil.SetMasks(v4, 0x0F00, 0x00F0, 0x000F, 0xF000);
        Decoded d4444 = BmpTestUtil.DecodeBmp(BmpTestUtil.File(v4, [], pixels));

        Assert.Equal(PixelFormat.Rgb8, d555.Format);
        Assert.Equal(PixelFormat.Rgb8, d565.Format);
        Assert.Equal(PixelFormat.Rgba8, d4444.Format);
        for (int y = 0; y < H; y++)
        {
            for (int x = 0; x < W; x++)
            {
                uint v = raw[(y * W) + x];
                int i = (y * W) + x;
                Assert.Equal(new[] { BmpTestUtil.Scale((v >> 10) & 31, 5), BmpTestUtil.Scale((v >> 5) & 31, 5), BmpTestUtil.Scale(v & 31, 5) }, d555.Data.AsSpan(i * 3, 3).ToArray().Select(b => (int)b));
                Assert.Equal(new[] { BmpTestUtil.Scale((v >> 11) & 31, 5), BmpTestUtil.Scale((v >> 5) & 63, 6), BmpTestUtil.Scale(v & 31, 5) }, d565.Data.AsSpan(i * 3, 3).ToArray().Select(b => (int)b));
                Assert.Equal(new[] { BmpTestUtil.Scale((v >> 8) & 15, 4), BmpTestUtil.Scale((v >> 4) & 15, 4), BmpTestUtil.Scale(v & 15, 4), BmpTestUtil.Scale((v >> 12) & 15, 4) }, d4444.Data.AsSpan(i * 4, 4).ToArray().Select(b => (int)b));
            }
        }
    }

    [Fact]
    public void ThirtyTwoBitRgbIsOpaqueAndBitfieldsWithAlphaIsStraight()
    {
        const int W = 5, H = 2;
        var rnd = new Random(2);
        uint[] raw = Enumerable.Range(0, W * H).Select(_ => (uint)rnd.NextInt64(0, 1L << 32)).ToArray();
        byte[] pixels = Rows(W, H, W * 4, false, (y, w) =>
        {
            byte[] r = new byte[w * 4];
            for (int x = 0; x < w; x++)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(r.AsSpan(x * 4), raw[(y * w) + x]);
            }

            return r;
        });

        Decoded plain = BmpTestUtil.DecodeBmp(BmpTestUtil.File(BmpTestUtil.InfoHeader(40, W, H, 32, 0), [], pixels));
        Assert.Equal(PixelFormat.Rgb8, plain.Format);
        Assert.Equal((byte)(raw[0] >> 16), plain.Data[0]);
        Assert.Equal((byte)raw[0], plain.Data[2]);

        // 2-10-10-10 ARGB with a V5 header.
        byte[] v5 = BmpTestUtil.InfoHeader(124, W, H, 32, 3);
        BmpTestUtil.SetMasks(v5, 0x3FF00000, 0x000FFC00, 0x000003FF, 0xC0000000);
        Decoded wide = BmpTestUtil.DecodeBmp(BmpTestUtil.File(v5, [], pixels));
        Assert.Equal(PixelFormat.Rgba8, wide.Format);
        for (int i = 0; i < W * H; i++)
        {
            uint v = raw[i];
            Assert.Equal(
                new[] { BmpTestUtil.Scale((v >> 20) & 1023, 10), BmpTestUtil.Scale((v >> 10) & 1023, 10), BmpTestUtil.Scale(v & 1023, 10), BmpTestUtil.Scale(v >> 30, 2) },
                wide.Data.AsSpan(i * 4, 4).ToArray().Select(b => (int)b));
        }

        // Non-contiguous masks are rejected.
        byte[] bad = BmpTestUtil.InfoHeader(108, W, H, 32, 3);
        BmpTestUtil.SetMasks(bad, 0x00FF00FF, 0x0000FF00, 0x000000FF, 0);
        Assert.Throws<ImageFormatException>(() => BmpTestUtil.DecodeBmp(BmpTestUtil.File(bad, [], pixels)));
    }

    [Fact]
    public void PalettedFormatsAndCoreHeader()
    {
        (byte, byte, byte)[] pal = Enumerable.Range(0, 16).Select(i => ((byte)(i * 16), (byte)(255 - (i * 9)), (byte)(i * 3))).ToArray();
        const int W = 11, H = 3;
        foreach (int bpp in new[] { 1, 4, 8 })
        {
            int colors = Math.Min(1 << bpp, 16);
            byte[] idx = new byte[W * H];
            for (int i = 0; i < idx.Length; i++)
            {
                idx[i] = (byte)((i * 7 + 1) % colors);
            }

            byte[] pixels = Rows(W, H, ((W * bpp) + 7) / 8, false, (y, w) =>
            {
                byte[] r = new byte[((w * bpp) + 7) / 8];
                for (int x = 0; x < w; x++)
                {
                    int bit = x * bpp;
                    r[bit >> 3] |= (byte)(idx[(y * w) + x] << (8 - bpp - (bit & 7)));
                }

                return r;
            });
            byte[] table = BmpTestUtil.PaletteBgrx(pal.Take(colors).ToArray());
            // clrUsed smaller than 2^bpp: the table is only that long.
            byte[] file = BmpTestUtil.File(BmpTestUtil.InfoHeader(40, W, H, bpp, 0, colors), table, pixels);
            Decoded d = BmpTestUtil.DecodeBmp(file);
            for (int i = 0; i < idx.Length; i++)
            {
                (byte r, byte g, byte b) = pal[idx[i]];
                Assert.Equal(new[] { r, g, b }, d.Data.AsSpan(i * 3, 3).ToArray());
            }

            // OS/2 core header: 12 bytes, 3-byte palette entries.
            byte[] core = new byte[12];
            BinaryPrimitives.WriteUInt32LittleEndian(core, 12);
            BinaryPrimitives.WriteUInt16LittleEndian(core.AsSpan(4), W);
            BinaryPrimitives.WriteUInt16LittleEndian(core.AsSpan(6), H);
            BinaryPrimitives.WriteUInt16LittleEndian(core.AsSpan(8), 1);
            BinaryPrimitives.WriteUInt16LittleEndian(core.AsSpan(10), (ushort)bpp);
            byte[] table3 = Enumerable.Range(0, 1 << bpp).SelectMany(i => i < colors ? new[] { pal[i].Item3, pal[i].Item2, pal[i].Item1 } : new byte[3]).ToArray();
            Decoded dc = BmpTestUtil.DecodeBmp(BmpTestUtil.File(core, table3, pixels));
            Assert.Equal(d.Data, dc.Data);
        }
    }

    [Fact]
    public void Rle8WithRunsAbsoluteDeltaAndEndOfLine()
    {
        const int W = 8, H = 4;
        (byte, byte, byte)[] pal = Enumerable.Range(0, 256).Select(i => ((byte)i, (byte)(i * 3), (byte)(255 - i))).ToArray();
        // File order is bottom-up: the first stored row is the bottom row of the image.
        byte[] rle =
        [
            4, 10, 4, 11, 0, 0,                 // row 0: 4x10, 4x11, end of line
            0, 3, 20, 21, 22, 0, 0, 0,          // row 1: absolute run of 3 (+pad byte), end of line
            0, 2, 2, 1,                         // delta: 2 right, 1 down from (0,2): lands on (2,3), row 2 stays index 0
            3, 30, 0, 1,                        // row 3: x=2..4 are 30, end of bitmap
        ];
        byte[] file = BmpTestUtil.File(BmpTestUtil.InfoHeader(40, W, H, 8, 1), BmpTestUtil.PaletteBgrx(pal), rle);
        int[][] idx =
        [
            [10, 10, 10, 10, 11, 11, 11, 11],
            [20, 21, 22, 0, 0, 0, 0, 0],
            [0, 0, 0, 0, 0, 0, 0, 0],
            [0, 0, 30, 30, 30, 0, 0, 0],
        ];
        Decoded d = BmpTestUtil.DecodeBmp(file);
        for (int y = 0; y < H; y++)
        {
            // idx rows are listed in file order; image row y is file row H-1-y.
            for (int x = 0; x < W; x++)
            {
                (byte r, byte g, byte b) = pal[idx[H - 1 - y][x]];
                Assert.Equal(new[] { r, g, b }, d.Data.AsSpan(((y * W) + x) * 3, 3).ToArray());
            }
        }
    }

    [Fact]
    public void Rle4WithNibblesAndAbsoluteRuns()
    {
        const int W = 7, H = 2;
        (byte, byte, byte)[] pal = Enumerable.Range(0, 16).Select(i => ((byte)(i * 16), (byte)(i * 5), (byte)(200 - i))).ToArray();
        byte[] rle =
        [
            7, 0x3A, 0, 0,                      // row 0: 7 pixels alternating 3,A,3,A,3,A,3
            0, 5, 0x12, 0x34, 0x50, 0, 0, 1,    // row 1: absolute 5 nibbles in 3 bytes (+pad), EOL, end
        ];
        byte[] file = BmpTestUtil.File(BmpTestUtil.InfoHeader(40, W, H, 4, 2), BmpTestUtil.PaletteBgrx(pal), rle);
        int[][] idx = [[3, 10, 3, 10, 3, 10, 3], [1, 2, 3, 4, 5, 0, 0]];
        Decoded d = BmpTestUtil.DecodeBmp(file);
        for (int y = 0; y < H; y++)
        {
            for (int x = 0; x < W; x++)
            {
                (byte r, byte g, byte b) = pal[idx[H - 1 - y][x]];
                Assert.Equal(new[] { r, g, b }, d.Data.AsSpan(((y * W) + x) * 3, 3).ToArray());
            }
        }
    }

    [Fact]
    public void EmbeddedProfileIsReported()
    {
        byte[] icc = Enumerable.Range(0, 200).Select(i => (byte)(i * 3)).ToArray();
        const int W = 2, H = 2;
        byte[] header = BmpTestUtil.InfoHeader(124, W, H, 24, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(56), 0x4D424544);
        byte[] pixels = new byte[8 * H];
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(112), (uint)(124 + pixels.Length));
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(116), (uint)icc.Length);
        byte[] file = BmpTestUtil.File(header, [], pixels, icc);
        ImageInfo info = BmpFormat.Instance.Decoder!.ReadInfo(new MemoryStream(file), ImageLimits.Default);
        Assert.Equal(icc, info.Metadata.IccProfile.ToArray());
    }

    [Fact]
    public void RegionDecodeMatchesCropForBothOrientations()
    {
        foreach (bool top in new[] { false, true })
        {
            const int W = 19, H = 14;
            byte[] px = Rows(W, H, W * 3, top, (y, w) => Enumerable.Range(0, w * 3).Select(i => (byte)((y * 31) + i)).ToArray());
            byte[] file = BmpTestUtil.File(BmpTestUtil.InfoHeader(40, W, top ? -H : H, 24, 0), [], px);
            Decoded full = BmpTestUtil.DecodeBmp(file);
            using var ms = new MemoryStream(file);
            ImageInfo info = BmpFormat.Instance.Decoder!.ReadInfo(ms, ImageLimits.Default);
            var r = new ImageRect(4, 3, 9, 7);
            var sink = new CollectSink(9, 7, PixelFormat.Rgb8);
            BmpFormat.Instance.Decoder.DecodeFrame(ms, info, 0, new DecodeRequest(r), sink, ImageLimits.Default, CancellationToken.None);
            for (int y = 0; y < 7; y++)
            {
                Assert.Equal(full.Data.AsSpan((((3 + y) * W) + 4) * 3, 27).ToArray(), sink.Data.AsSpan(y * 27, 27).ToArray());
            }
        }
    }
}
