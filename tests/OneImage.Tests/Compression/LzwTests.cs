using System.Buffers;
using System.Buffers.Binary;
using OneImage.Compression;
using ImageMagick;

namespace OneImage.Tests.Compression;

public class LzwTests
{
    private static byte[] Decode(byte[] comp, LzwVariant v, int min, int inChunk = int.MaxValue, int outChunk = 1 << 16, long max = long.MaxValue, double ratio = 0)
    {
        var dec = new LzwDecoder(v, min, max, ratio);
        var result = new MemoryStream();
        byte[] buf = new byte[outChunk];
        int pos = 0;
        while (true)
        {
            int n = Math.Min(inChunk, comp.Length - pos);
            InflateStatus st = dec.Decode(comp.AsSpan(pos, n), buf, out int c, out int w);
            result.Write(buf, 0, w);
            pos += c;
            if (st == InflateStatus.Done)
            {
                return result.ToArray();
            }

            if (st == InflateStatus.NeedInput && pos >= comp.Length)
            {
                throw new ImageFormatException("truncated");
            }
        }
    }

    private static byte[] Symbols(int n, int bits, int seed, bool smooth)
    {
        var rnd = new Random(seed);
        var b = new byte[n];
        int mask = (1 << bits) - 1;
        int v = 0;
        for (int i = 0; i < n; i++)
        {
            if (smooth)
            {
                v += rnd.Next(5) == 0 ? rnd.Next(-1, 2) : 0;
                b[i] = (byte)(v & mask);
            }
            else
            {
                b[i] = (byte)(rnd.Next() & mask);
            }
        }

        return b;
    }

    [Fact]
    public void GifSpecificationSampleMatchesPublishedBytes()
    {
        // The 10x10 sample image used in many GIF format walkthroughs (minimum code size 2).
        byte[] indices =
        {
            1, 1, 1, 1, 1, 2, 2, 2, 2, 2,
            1, 1, 1, 1, 1, 2, 2, 2, 2, 2,
            1, 1, 1, 1, 1, 2, 2, 2, 2, 2,
            1, 1, 1, 0, 0, 0, 0, 2, 2, 2,
            1, 1, 1, 0, 0, 0, 0, 2, 2, 2,
            2, 2, 2, 0, 0, 0, 0, 1, 1, 1,
            2, 2, 2, 0, 0, 0, 0, 1, 1, 1,
            2, 2, 2, 2, 2, 1, 1, 1, 1, 1,
            2, 2, 2, 2, 2, 1, 1, 1, 1, 1,
            2, 2, 2, 2, 2, 1, 1, 1, 1, 1,
        };
        byte[] published = { 0x8C, 0x2D, 0x99, 0x87, 0x2A, 0x1C, 0xDC, 0x33, 0xA0, 0x02, 0x75, 0xEC, 0x95, 0xFA, 0xA8, 0xDE, 0x60, 0x8C, 0x04, 0x91, 0x4C, 0x01 };
        Assert.Equal(indices, Decode(published, LzwVariant.Gif, 2));
        Assert.Equal(indices, Decode(published, LzwVariant.Gif, 2, 1, 1));
        Assert.Equal(published, LzwEncoder.Compress(indices, LzwVariant.Gif, 2));
    }

    [Fact]
    public void TiffSpecificationExampleCodes()
    {
        // TIFF 6.0 section 13: the string 7 7 7 8 8 7 7 6 6 compresses to codes 256 7 258 8 8 258 6 6 257.
        int[] codes = { 256, 7, 258, 8, 8, 258, 6, 6, 257 };
        var packed = new List<byte>();
        ulong acc = 0;
        int n = 0;
        foreach (int c in codes)
        {
            acc = (acc << 9) | (uint)c;
            n += 9;
            while (n >= 8)
            {
                packed.Add((byte)(acc >> (n - 8)));
                n -= 8;
            }
        }

        if (n > 0)
        {
            packed.Add((byte)(acc << (8 - n)));
        }

        byte[] expected = { 7, 7, 7, 8, 8, 7, 7, 6, 6 };
        Assert.Equal(expected, Decode(packed.ToArray(), LzwVariant.Tiff, 8));
        Assert.Equal(packed.ToArray(), LzwEncoder.Compress(expected, LzwVariant.Tiff));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    public void GifRoundTripsAcrossCodeSizeAndClearBoundaries(int bits)
    {
        foreach (bool smooth in new[] { false, true })
        {
            foreach (int len in new[] { 0, 1, 2, 3, 100, 5000, 70_000, 400_000 })
            {
                byte[] data = Symbols(len, bits, bits + len, smooth);
                byte[] z = LzwEncoder.Compress(data, LzwVariant.Gif, bits);
                Assert.True(data.AsSpan().SequenceEqual(Decode(z, LzwVariant.Gif, bits)), $"bits={bits} len={len} smooth={smooth}");
                if (len <= 5000)
                {
                    Assert.True(data.AsSpan().SequenceEqual(Decode(z, LzwVariant.Gif, bits, 1, 1)), $"1-byte chunks bits={bits} len={len}");
                }
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TiffRoundTripsAcrossCodeSizeAndClearBoundaries(bool smooth)
    {
        foreach (int len in new[] { 0, 1, 2, 3, 100, 5000, 70_000, 500_000 })
        {
            byte[] data = Symbols(len, 8, len, smooth);
            byte[] z = LzwEncoder.Compress(data, LzwVariant.Tiff);
            Assert.True(data.AsSpan().SequenceEqual(Decode(z, LzwVariant.Tiff, 8)), $"len={len} smooth={smooth}");
            if (len <= 5000)
            {
                Assert.True(data.AsSpan().SequenceEqual(Decode(z, LzwVariant.Tiff, 8, 1, 1)), $"1-byte chunks len={len}");
                Assert.True(data.AsSpan().SequenceEqual(Decode(z, LzwVariant.Tiff, 8, 5, 3)), $"5/3 chunks len={len}");
            }
        }
    }

    [Fact]
    public void ManyCodesAndClearsAreExercised()
    {
        // Incompressible symbols fill the table quickly; confirm the stream really contains several clear codes.
        byte[] data = Symbols(200_000, 8, 77, false);
        byte[] z = LzwEncoder.Compress(data, LzwVariant.Gif, 8);
        Assert.True(z.Length > 4096 * 12 / 8 * 3);
        Assert.True(data.AsSpan().SequenceEqual(Decode(z, LzwVariant.Gif, 8)));
    }

    [Fact]
    public void EncoderOutputIsIndependentOfWriteChunking()
    {
        byte[] data = Symbols(50_000, 6, 3, true);
        byte[] whole = LzwEncoder.Compress(data, LzwVariant.Gif, 6);
        var sink = new ArrayBufferWriter<byte>();
        var e = new LzwEncoder(sink, LzwVariant.Gif, 6);
        for (int i = 0; i < data.Length; i += 37)
        {
            e.Write(data.AsSpan(i, Math.Min(37, data.Length - i)));
        }

        e.Finish();
        Assert.Equal(whole, sink.WrittenSpan.ToArray());
    }

    [Fact]
    public void DecoderAcceptsDeferredClear()
    {
        // Hand-made GIF stream with minimum code size 2 that never emits a clear code once the table is full
        // is not producible by our encoder, so build one: random 2-bit symbols encoded with a table that is
        // never reset (dictionary growth stops at 4096).
        byte[] data = Symbols(60_000, 2, 5, false);
        byte[] z = EncodeWithoutClear(data, 2);
        Assert.Equal(data, Decode(z, LzwVariant.Gif, 2));
    }

    // A tiny independent GIF-style LZW encoder that stops adding entries when the table is full instead of clearing.
    private static byte[] EncodeWithoutClear(byte[] data, int min)
    {
        int clear = 1 << min;
        int eoi = clear + 1;
        var dict = new Dictionary<(int, byte), int>();
        int next = eoi + 1;
        int size = min + 1;
        var bytes = new List<byte>();
        ulong acc = 0;
        int n = 0;
        int decNext = next;
        int emitted = 0;

        void Put(int code)
        {
            acc |= (ulong)code << n;
            n += size;
            while (n >= 8)
            {
                bytes.Add((byte)acc);
                acc >>= 8;
                n -= 8;
            }
        }

        void Emit(int code)
        {
            Put(code);
            if (emitted > 0 && decNext < 4096)
            {
                decNext++;
                if (decNext == (1 << size) && size < 12)
                {
                    size++;
                }
            }

            emitted++;
        }

        Put(clear);
        int cur = -1;
        foreach (byte b in data)
        {
            if (cur < 0)
            {
                cur = b;
                continue;
            }

            if (dict.TryGetValue((cur, b), out int v))
            {
                cur = v;
            }
            else
            {
                Emit(cur);
                if (next < 4096)
                {
                    dict[(cur, b)] = next++;
                }

                cur = b;
            }
        }

        Emit(cur);
        Put(eoi);
        if (n > 0)
        {
            bytes.Add((byte)acc);
        }

        return bytes.ToArray();
    }

    [Fact]
    public void ResetReusesDecoderAndEncoderAcrossStreams()
    {
        foreach (LzwVariant v in new[] { LzwVariant.Gif, LzwVariant.Tiff })
        {
            var sink = new ArrayBufferWriter<byte>();
            var enc = new LzwEncoder(sink, v, 8);
            var dec = new LzwDecoder(v, 8);
            byte[] outBuf = new byte[1 << 16];
            for (int round = 0; round < 4; round++)
            {
                int bits = v == LzwVariant.Gif ? 2 + (round * 2) : 8;
                byte[] data = Symbols(20_000 + (round * 7000), bits, round, round % 2 == 0);
                sink.Clear();
                enc.Reset(bits);
                enc.Write(data);
                enc.Finish();
                Assert.Equal(LzwEncoder.Compress(data, v, bits), sink.WrittenSpan.ToArray());
                dec.Reset(bits);
                var result = new MemoryStream();
                byte[] z = sink.WrittenSpan.ToArray();
                int pos = 0;
                while (true)
                {
                    InflateStatus st = dec.Decode(z.AsSpan(pos), outBuf.AsSpan(0, 1000 + (round * 333)), out int c, out int w);
                    result.Write(outBuf, 0, w);
                    pos += c;
                    if (st == InflateStatus.Done)
                    {
                        break;
                    }
                }

                Assert.Equal(data, result.ToArray());
            }
        }
    }

    [Fact]
    public void LongAndOddLengthStringsDecodeExactly()
    {
        // Runs of one symbol build strings of every length up to 4096; mixed run lengths hit all chunk remainders.
        var rnd = new Random(9);
        var data = new List<byte>();
        for (int i = 0; i < 300; i++)
        {
            byte sym = (byte)rnd.Next(4);
            data.AddRange(Enumerable.Repeat(sym, rnd.Next(1, 5000)));
        }

        byte[] arr = data.ToArray();
        foreach (LzwVariant v in new[] { LzwVariant.Gif, LzwVariant.Tiff })
        {
            byte[] z = LzwEncoder.Compress(arr, v, v == LzwVariant.Gif ? 2 : 8);
            foreach (int outChunk in new[] { 1 << 20, 4097, 4096, 1000, 13, 7 })
            {
                Assert.True(arr.AsSpan().SequenceEqual(Decode(z, v, 2, int.MaxValue, outChunk)), $"{v} outChunk={outChunk}");
            }
        }
    }

    // ---- malformed input and guards -----------------------------------------------------------------

    [Fact]
    public void BadCodesThrowFormatException()
    {
        // Gif min 2: first code after clear must be a literal (< 4).
        var w = new List<byte>();
        ulong acc = 0;
        int n = 0;
        void Put(int code, int size)
        {
            acc |= (ulong)code << n;
            n += size;
            while (n >= 8)
            {
                w.Add((byte)acc);
                acc >>= 8;
                n -= 8;
            }
        }

        Put(4, 3);
        Put(7, 3); // table entry that does not exist
        Put(5, 3);
        if (n > 0)
        {
            w.Add((byte)acc);
        }

        Assert.Throws<ImageFormatException>(() => Decode(w.ToArray(), LzwVariant.Gif, 2));

        w.Clear();
        acc = 0;
        n = 0;
        Put(4, 3);
        Put(1, 3);
        Put(7, 3); // next is 6, so 7 is beyond it
        Put(5, 3);
        if (n > 0)
        {
            w.Add((byte)acc);
        }

        Assert.Throws<ImageFormatException>(() => Decode(w.ToArray(), LzwVariant.Gif, 2));
        Assert.Throws<ImageFormatException>(() => Decode(new byte[] { 0x84, 0x20 }, LzwVariant.Gif, 2)); // truncated, no EOI
        Assert.Throws<ArgumentOutOfRangeException>(() => new LzwDecoder(LzwVariant.Gif, 9));
        Assert.Throws<ArgumentOutOfRangeException>(() => new LzwDecoder(LzwVariant.Gif, 1));
    }

    [Fact]
    public void RandomMutationsNeverHangOrCrash()
    {
        var rnd = new Random(2024);
        byte[] gifSeed = LzwEncoder.Compress(Symbols(30_000, 5, 1, true), LzwVariant.Gif, 5);
        byte[] tiffSeed = LzwEncoder.Compress(Symbols(30_000, 8, 2, true), LzwVariant.Tiff);
        int bad = 0;
        int iterations = int.TryParse(Environment.GetEnvironmentVariable("ONEIMAGE_MUTATION_ITERS"), out int env) ? env : 3000;
        for (int i = 0; i < iterations; i++)
        {
            bool tiff = i % 2 == 0;
            byte[] m = (byte[])(tiff ? tiffSeed : gifSeed).Clone();
            for (int f = 0; f < 1 + rnd.Next(4); f++)
            {
                m[rnd.Next(m.Length)] ^= (byte)(1 << rnd.Next(8));
            }

            try
            {
                Decode(m, tiff ? LzwVariant.Tiff : LzwVariant.Gif, tiff ? 8 : 5, rnd.Next(2) == 0 ? int.MaxValue : 1 + rnd.Next(30), 1 + rnd.Next(3000), 5_000_000);
            }
            catch (ImageException)
            {
                bad++;
            }
        }

        Assert.True(bad > 50, $"bad={bad}");
    }

    [Fact]
    public void ExpansionGuardStopsAtCapAndRatio()
    {
        // A run of one symbol compresses at roughly 4096:1 growth per code near the end of the table.
        byte[] zeros = new byte[30_000_000];
        byte[] z = LzwEncoder.Compress(zeros, LzwVariant.Gif, 2);
        Assert.True(z.Length < zeros.Length / 100);

        var dec = new LzwDecoder(LzwVariant.Gif, 2, maxOutputBytes: 1_000_000);
        byte[] buf = new byte[65536];
        long delivered = 0;
        Assert.Throws<ImageLimitException>(() =>
        {
            int pos = 0;
            while (true)
            {
                InflateStatus st = dec.Decode(z.AsSpan(pos), buf, out int c, out int w);
                pos += c;
                delivered += w;
                if (st == InflateStatus.Done)
                {
                    break;
                }
            }
        });
        Assert.True(delivered <= 1_000_000);

        // With a ratio the same stream is refused well before the end.
        var dec2 = new LzwDecoder(LzwVariant.Gif, 2, long.MaxValue, 50);
        long delivered2 = 0;
        Assert.Throws<ImageLimitException>(() =>
        {
            int pos = 0;
            while (true)
            {
                InflateStatus st = dec2.Decode(z.AsSpan(pos), buf, out int c, out int w);
                pos += c;
                delivered2 += w;
                if (st == InflateStatus.Done)
                {
                    break;
                }
            }
        });
        Assert.True(delivered2 < zeros.Length / 4);
    }

    // ---- independent implementation: Magick.NET (ImageMagick's GIF coder and libtiff) -------------------

    private sealed record GifFrame(int Width, int Height, byte[] Palette, int MinCodeSize, byte[] Payload);

    private static GifFrame ParseGif(byte[] g)
    {
        int w = BinaryPrimitives.ReadUInt16LittleEndian(g.AsSpan(6));
        int h = BinaryPrimitives.ReadUInt16LittleEndian(g.AsSpan(8));
        int flags = g[10];
        int pos = 13;
        byte[] gct = Array.Empty<byte>();
        if ((flags & 0x80) != 0)
        {
            int size = 3 * (1 << ((flags & 7) + 1));
            gct = g.AsSpan(pos, size).ToArray();
            pos += size;
        }

        while (true)
        {
            byte b = g[pos++];
            if (b == 0x21)
            {
                pos++;
                while (g[pos] != 0)
                {
                    pos += g[pos] + 1;
                }

                pos++;
            }
            else if (b == 0x2C)
            {
                int fw = BinaryPrimitives.ReadUInt16LittleEndian(g.AsSpan(pos + 4));
                int fh = BinaryPrimitives.ReadUInt16LittleEndian(g.AsSpan(pos + 6));
                int lf = g[pos + 8];
                pos += 9;
                byte[] pal = gct;
                if ((lf & 0x80) != 0)
                {
                    int size = 3 * (1 << ((lf & 7) + 1));
                    pal = g.AsSpan(pos, size).ToArray();
                    pos += size;
                }

                Assert.Equal(0, lf & 0x40); // not interlaced
                int min = g[pos++];
                var payload = new List<byte>();
                while (g[pos] != 0)
                {
                    payload.AddRange(g.AsSpan(pos + 1, g[pos]).ToArray());
                    pos += g[pos] + 1;
                }

                return new GifFrame(fw, fh, pal, min, payload.ToArray());
            }
            else
            {
                throw new InvalidOperationException("unexpected block " + b);
            }
        }
    }

    private static byte[] Rgb(MagickImage img) => img.GetPixels().ToByteArray(PixelMapping.RGB)!;

    [Fact]
    public void DecodesGifPayloadsWrittenByImageMagick()
    {
        foreach ((int colors, int w, int h) in new[] { (4, 64, 40), (16, 97, 83), (200, 300, 200), (256, 513, 257) })
        {
            byte[] rgb = new byte[w * h * 3];
            var rnd = new Random(colors);
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int k = ((((x / 5) + (y / 3)) * 7) + (rnd.Next(10) == 0 ? rnd.Next(colors) : 0)) % colors;
                    int o = ((y * w) + x) * 3;
                    rgb[o] = (byte)((k * 53) & 255);
                    rgb[o + 1] = (byte)((k * 97) & 255);
                    rgb[o + 2] = (byte)(((k * 29) + 11) & 255);
                }
            }

            using var img = new MagickImage(rgb, new PixelReadSettings((uint)w, (uint)h, StorageType.Char, PixelMapping.RGB));
            img.Format = MagickFormat.Gif;
            var ms = new MemoryStream();
            img.Write(ms);
            byte[] file = ms.ToArray();
            GifFrame f = ParseGif(file);
            byte[] idx = Decode(f.Payload, LzwVariant.Gif, f.MinCodeSize);
            Assert.Equal(f.Width * f.Height, idx.Length);
            using var back = new MagickImage(file);
            byte[] expect = Rgb(back);
            for (int i = 0; i < idx.Length; i++)
            {
                Assert.True(expect[i * 3] == f.Palette[idx[i] * 3] && expect[(i * 3) + 1] == f.Palette[(idx[i] * 3) + 1] && expect[(i * 3) + 2] == f.Palette[(idx[i] * 3) + 2], $"pixel {i} colors={colors}");
            }

            // The same indices re-encoded by us decode to identical indices, even with 1-byte chunks.
            byte[] again = LzwEncoder.Compress(idx, LzwVariant.Gif, f.MinCodeSize);
            Assert.Equal(idx, Decode(again, LzwVariant.Gif, f.MinCodeSize, 1, 1));
        }
    }

    [Fact]
    public void ImageMagickDecodesGifPayloadsWrittenByUs()
    {
        foreach ((int bits, int w, int h) in new[] { (2, 50, 70), (4, 97, 83), (8, 300, 200) })
        {
            int colors = 1 << bits;
            byte[] idx = Symbols(w * h, bits, bits, true);
            byte[] pal = new byte[colors * 3];
            for (int i = 0; i < colors; i++)
            {
                pal[i * 3] = (byte)((i * 37) & 255);
                pal[(i * 3) + 1] = (byte)(((i * 91) + 5) & 255);
                pal[(i * 3) + 2] = (byte)(((i * 17) + 200) & 255);
            }

            var ms = new MemoryStream();
            ms.Write("GIF89a"u8);
            byte[] lsd = new byte[7];
            BinaryPrimitives.WriteUInt16LittleEndian(lsd, (ushort)w);
            BinaryPrimitives.WriteUInt16LittleEndian(lsd.AsSpan(2), (ushort)h);
            lsd[4] = (byte)(0x80 | (bits - 1));
            ms.Write(lsd);
            ms.Write(pal);
            ms.WriteByte(0x2C);
            byte[] desc = new byte[9];
            BinaryPrimitives.WriteUInt16LittleEndian(desc.AsSpan(4), (ushort)w);
            BinaryPrimitives.WriteUInt16LittleEndian(desc.AsSpan(6), (ushort)h);
            ms.Write(desc);
            ms.WriteByte((byte)bits);
            byte[] payload = LzwEncoder.Compress(idx, LzwVariant.Gif, bits);
            for (int i = 0; i < payload.Length; i += 255)
            {
                int n = Math.Min(255, payload.Length - i);
                ms.WriteByte((byte)n);
                ms.Write(payload, i, n);
            }

            ms.WriteByte(0);
            ms.WriteByte(0x3B);
            using var back = new MagickImage(ms.ToArray());
            byte[] got = Rgb(back);
            Assert.Equal(w * h * 3, got.Length);
            for (int i = 0; i < idx.Length; i++)
            {
                Assert.True(got[i * 3] == pal[idx[i] * 3] && got[(i * 3) + 1] == pal[(idx[i] * 3) + 1] && got[(i * 3) + 2] == pal[(idx[i] * 3) + 2], $"pixel {i} bits={bits}");
            }
        }
    }

    private static uint ReadTag(byte[] t, int entry, out int count, out int type)
    {
        count = (int)BinaryPrimitives.ReadUInt32LittleEndian(t.AsSpan(entry + 4));
        type = BinaryPrimitives.ReadUInt16LittleEndian(t.AsSpan(entry + 2));
        return type == 3 && count == 1 ? BinaryPrimitives.ReadUInt16LittleEndian(t.AsSpan(entry + 8)) : BinaryPrimitives.ReadUInt32LittleEndian(t.AsSpan(entry + 8));
    }

    [Fact]
    public void DecodesTiffStripsWrittenByLibtiff()
    {
        foreach ((int w, int h) in new[] { (100, 100), (333, 51), (640, 480) })
        {
            var rnd = new Random(w);
            byte[] pixels = new byte[w * h];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    pixels[(y * w) + x] = (byte)(((x * 3) + (y * 2) + (rnd.Next(6) == 0 ? rnd.Next(40) : 0)) & 255);
                }
            }

            using var img = new MagickImage(pixels, new PixelReadSettings((uint)w, (uint)h, StorageType.Char, "I"));
            img.ColorType = ColorType.Grayscale;
            img.Depth = 8;
            img.HasAlpha = false;
            img.Settings.Compression = CompressionMethod.LZW;
            img.Format = MagickFormat.Tiff;
            var ms = new MemoryStream();
            img.Write(ms);
            byte[] t = ms.ToArray();
            Assert.Equal((byte)'I', t[0]);
            int ifd = (int)BinaryPrimitives.ReadUInt32LittleEndian(t.AsSpan(4));
            int entries = BinaryPrimitives.ReadUInt16LittleEndian(t.AsSpan(ifd));
            uint[] offsets = Array.Empty<uint>();
            uint[] counts = Array.Empty<uint>();
            int rowsPerStrip = h;
            int predictor = 1;
            for (int e = 0; e < entries; e++)
            {
                int entry = ifd + 2 + (e * 12);
                int tag = BinaryPrimitives.ReadUInt16LittleEndian(t.AsSpan(entry));
                uint val = ReadTag(t, entry, out int count, out int type);
                if (tag is 273 or 279)
                {
                    var arr = new uint[count];
                    int size = type == 3 ? 2 : 4;
                    int at = count * size <= 4 ? entry + 8 : (int)val;
                    for (int i = 0; i < count; i++)
                    {
                        arr[i] = type == 3 ? BinaryPrimitives.ReadUInt16LittleEndian(t.AsSpan(at + (i * 2))) : BinaryPrimitives.ReadUInt32LittleEndian(t.AsSpan(at + (i * 4)));
                    }

                    if (tag == 273)
                    {
                        offsets = arr;
                    }
                    else
                    {
                        counts = arr;
                    }
                }
                else if (tag == 278)
                {
                    rowsPerStrip = (int)val;
                }
                else if (tag == 259)
                {
                    Assert.Equal(5u, val);
                }
                else if (tag == 277)
                {
                    Assert.True(val == 1u, $"samples per pixel {val}, bits {img.Depth}, alpha {img.HasAlpha}, colorspace {img.ColorSpace}, channels {img.ChannelCount}");
                }
                else if (tag == 317)
                {
                    predictor = (int)val;
                }
            }

            Assert.NotEmpty(offsets);
            int row = 0;
            for (int s = 0; s < offsets.Length; s++)
            {
                int rows = Math.Min(rowsPerStrip, h - row);
                byte[] strip = t.AsSpan((int)offsets[s], (int)counts[s]).ToArray();
                byte[] got = Decode(strip, LzwVariant.Tiff, 8);
                if (predictor == 2)
                {
                    // Undo horizontal differencing (TIFF 6.0 section 14).
                    for (int r = 0; r < rows; r++)
                    {
                        for (int x = 1; x < w; x++)
                        {
                            got[(r * w) + x] += got[(r * w) + x - 1];
                        }
                    }
                }

                Assert.True(pixels.AsSpan(row * w, rows * w).SequenceEqual(got), $"{w}x{h} strip {s}");
                row += rows;
            }

            Assert.Equal(h, row);
        }
    }

    [Fact]
    public void LibtiffDecodesTiffStripsWrittenByUs()
    {
        foreach ((int w, int h) in new[] { (100, 100), (257, 129), (640, 300) })
        {
            byte[] pixels = Symbols(w * h, 8, w, true);
            byte[] strip = LzwEncoder.Compress(pixels, LzwVariant.Tiff);
            var ms = new MemoryStream();
            ms.Write(new byte[] { (byte)'I', (byte)'I', 42, 0, 0, 0, 0, 0 });
            ms.Write(strip);
            if ((ms.Length & 1) != 0)
            {
                ms.WriteByte(0);
            }

            int ifdAt = (int)ms.Length;
            var tags = new (ushort Tag, ushort Type, uint Value)[]
            {
                (256, 4, (uint)w), (257, 4, (uint)h), (258, 3, 8), (259, 3, 5), (262, 3, 1),
                (273, 4, 8), (277, 3, 1), (278, 4, (uint)h), (279, 4, (uint)strip.Length),
            };
            byte[] two = new byte[2];
            BinaryPrimitives.WriteUInt16LittleEndian(two, (ushort)tags.Length);
            ms.Write(two);
            foreach ((ushort tag, ushort type, uint value) in tags)
            {
                byte[] buf = new byte[12];
                BinaryPrimitives.WriteUInt16LittleEndian(buf, tag);
                BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(2), type);
                BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(4), 1);
                BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(8), value);
                ms.Write(buf);
            }

            ms.Write(new byte[4]);
            byte[] file = ms.ToArray();
            BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(4), (uint)ifdAt);
            using var back = new MagickImage(file);
            Assert.Equal((uint)w, back.Width);
            byte[] got = Rgb(back);
            for (int i = 0; i < pixels.Length; i++)
            {
                Assert.True(got[i * 3] == pixels[i], $"pixel {i} {w}x{h}");
            }
        }
    }
}
