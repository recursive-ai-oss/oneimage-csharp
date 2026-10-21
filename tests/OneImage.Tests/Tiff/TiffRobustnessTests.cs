using System.Buffers.Binary;
using OneImage.Codecs.Tiff;

namespace OneImage.Tests.Tiff;

public sealed class TiffRobustnessTests
{
    // ---- random access ---------------------------------------------------------------------------------------------

    /// <summary>Reads a region the way the core does: one 128x128 request at a time.</summary>
    private static byte[] ReadRegionInTiles(byte[] file, ImageInfo info, ImageRect region)
    {
        FrameInfo f = info.Frames[0];
        int bpp = f.Format.BytesPerPixel;
        byte[] result = new byte[region.Width * region.Height * bpp];
        for (int ty = region.Y; ty < region.Bottom; ty += 128)
        {
            for (int tx = region.X; tx < region.Right; tx += 128)
            {
                var r = new ImageRect(tx, ty, Math.Min(128, region.Right - tx), Math.Min(128, region.Bottom - ty));
                byte[] t = TiffTestUtil.Decode(file, region: r, info: info);
                for (int y = 0; y < r.Height; y++)
                {
                    t.AsSpan(y * r.Width * bpp, r.Width * bpp).CopyTo(result.AsSpan((((r.Y - region.Y) + y) * region.Width + (r.X - region.X)) * bpp));
                }
            }
        }

        return result;
    }

    [Fact]
    public void RegionReadDecodesOnlyTheIntersectingTiles()
    {
        var b = new TiffBuilder { Width = 2048, Height = 2048, Bits = 8, Spp = 3, Photometric = 2, TileW = 256, TileH = 256, Compression = 5 };
        b.MakeSamples(1);
        byte[] file = b.Build();
        ImageInfo info = TiffTestUtil.Info(file);
        var state = (TiffImageState)info.DecoderState!;
        Assert.Equal(DecoderCapabilities.RandomAccess, TiffFormat.Instance.Decoder!.Capabilities);

        var region = new ImageRect(700, 900, 512, 512);
        byte[] got = ReadRegionInTiles(file, info, region);

        // Columns 700..1211 touch tiles 2..4, rows 900..1411 touch tiles 3..5: nine tiles, each decompressed once.
        Assert.Equal(9, Interlocked.Read(ref state.BlocksDecoded));
        byte[] whole = TiffTestUtil.Decode(file, region: region);
        Assert.Equal(whole, got);
    }

    [Fact]
    public void RegionReadOfStrippedFileDecodesOnlyIntersectingStrips()
    {
        var b = new TiffBuilder { Width = 300, Height = 1000, Bits = 8, Spp = 1, RowsPerStrip = 10, Compression = 8 };
        b.MakeSamples(2);
        byte[] file = b.Build();
        ImageInfo info = TiffTestUtil.Info(file);
        var state = (TiffImageState)info.DecoderState!;
        byte[] got = ReadRegionInTiles(file, info, new ImageRect(20, 500, 128, 128));
        Assert.Equal(13, Interlocked.Read(ref state.BlocksDecoded)); // rows 500..627 = strips 50..62
        Assert.Equal(128 * 128, got.Length);
    }

    [Fact]
    public void ParallelRegionReadsAgreeAndDecodeEachBlockOnce()
    {
        var b = new TiffBuilder { Width = 1024, Height = 1024, Bits = 8, Spp = 4, Photometric = 2, ExtraSamples = [2], TileW = 128, TileH = 128, Compression = 5, Predictor = 2 };
        b.MakeSamples(3);
        byte[] file = b.Build();
        byte[] want = TiffTestUtil.Decode(file);
        for (int iteration = 0; iteration < 5; iteration++)
        {
            ImageInfo info = TiffTestUtil.Info(file);
            var state = (TiffImageState)info.DecoderState!;
            Parallel.For(0, 64, new ParallelOptions { MaxDegreeOfParallelism = 8 }, i =>
            {
                var r = new ImageRect((i % 8) * 128, (i / 8) * 128, 128, 128);
                byte[] t = TiffTestUtil.Decode(file, region: r, info: info);
                for (int y = 0; y < 128; y++)
                {
                    Assert.True(t.AsSpan(y * 512, 512).SequenceEqual(want.AsSpan((((r.Y + y) * 1024) + r.X) * 4, 512)));
                }
            });
            Assert.Equal(64, Interlocked.Read(ref state.BlocksDecoded));
        }
    }

    [Fact]
    public void LoadedImageCropMatchesFullDecode()
    {
        var b = new TiffBuilder { Width = 700, Height = 500, Bits = 16, Spp = 3, Photometric = 2, TileW = 128, TileH = 64, Compression = 8, Predictor = 2 };
        b.MakeSamples(4, smooth: false);
        byte[] file = b.Build();
        var cfg = new ImageConfiguration().Add(TiffFormat.Instance);
        Image img = Image.Load(file, new LoadOptions { Configuration = cfg });
        Rgb48[] all = img.ToArray<Rgb48>();
        Rgb48[] part = img.Crop(333, 77, 200, 150).ToArray<Rgb48>();
        for (int y = 0; y < 150; y++)
        {
            for (int x = 0; x < 200; x++)
            {
                Assert.Equal(all[((77 + y) * 700) + 333 + x], part[(y * 200) + x]);
            }
        }
    }

    // ---- the pieces ---------------------------------------------------------------------------------------------------

    [Fact]
    public void CcittCodeTablesArePrefixFreeAndComplete()
    {
        foreach (bool black in new[] { false, true })
        {
            List<string> codes = TiffCcitt.AllCodes(black).ToList();
            Assert.Equal(64 + 27 + 13, codes.Count);
            Assert.Equal(codes.Count, codes.Distinct().Count());
            foreach (string a in codes)
            {
                foreach (string c in codes)
                {
                    Assert.False(a != c && c.StartsWith(a, StringComparison.Ordinal), $"{a} is a prefix of {c}");
                }
            }
        }
    }

    [Fact]
    public void JpegSeamMergesSharedTablesIntoTheStrip()
    {
        byte[] tables = [0xFF, 0xD8, 0xFF, 0xDB, 0, 4, 1, 2, 0xFF, 0xD9];
        byte[] strip = [0xFF, 0xD8, 0xFF, 0xC0, 0, 3, 9, 0xFF, 0xDA, 1, 2, 3, 0xFF, 0xD9];
        Assert.Equal(new byte[] { 0xFF, 0xD8, 0xFF, 0xDB, 0, 4, 1, 2, 0xFF, 0xC0, 0, 3, 9, 0xFF, 0xDA, 1, 2, 3, 0xFF, 0xD9 }, TiffJpegSeam.Merge(tables, strip));

        // Without a decoder wired in, JPEG-compressed files fail with a typed error; with a hook they reach it.
        var b = new TiffBuilder { Bits = 8, Spp = 1, Compression = 7, BlockOverride = _ => strip };
        b.MakeSamples(1);
        byte[] file = b.Build();
        Assert.Throws<ImageFormatException>(() => TiffTestUtil.Decode(file));
        int calls = 0;
        TiffJpegSeam.DecodeJpeg = (jpeg, w, h, comps, ycc, dest) =>
        {
            calls++;
            dest.Fill(77);
        };
        try
        {
            Assert.All(TiffTestUtil.Decode(file), v => Assert.Equal(77, v));
            Assert.True(calls > 0);
        }
        finally
        {
            TiffJpegSeam.DecodeJpeg = null;
        }
    }

    [Fact]
    public void PackBitsRoundTrips()
    {
        var rnd = new Random(5);
        for (int n = 0; n < 200; n++)
        {
            byte[] row = new byte[rnd.Next(1, 400)];
            for (int i = 0; i < row.Length; i++)
            {
                row[i] = (byte)(rnd.Next(4) == 0 ? rnd.Next(256) : 7);
            }

            byte[] enc = new byte[row.Length + (row.Length / 128) + 2];
            int len = OneImage.Kernels.TiffKernels.PackBitsEncode(row, enc);
            byte[] dec = new byte[row.Length];
            Assert.Equal(row.Length, OneImage.Kernels.TiffKernels.PackBitsDecode(enc.AsSpan(0, len), dec));
            Assert.Equal(row, dec);
        }
    }

    // ---- hostile input -----------------------------------------------------------------------------------------------

    private static void AssertTyped(Action act)
    {
        try
        {
            act();
        }
        catch (ImageException)
        {
        }
        catch (ArgumentException)
        {
        }
    }

    private static TiffBuilder Small() => new() { Width = 40, Height = 30, Bits = 8, Spp = 3, Photometric = 2, Compression = 5, RowsPerStrip = 8 };

    [Fact]
    public void DirectoryLoopIsRejected()
    {
        var b = Small();
        b.MakeSamples(1);
        byte[] file = b.Build();
        // point the chain's "next" at the directory itself
        uint ifd = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(4));
        int count = BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan((int)ifd));
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan((int)ifd + 2 + (count * 12)), ifd);
        Assert.Throws<ImageFormatException>(() => TiffTestUtil.Info(file));
    }

    [Fact]
    public void OffsetsPastTheEndAndHugeCountsAreTypedErrors()
    {
        var b = Small();
        b.MakeSamples(1);
        byte[] file = b.Build();
        uint ifd = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(4));

        byte[] bad = (byte[])file.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(bad.AsSpan(4), (uint)bad.Length + 1000);
        Assert.Throws<ImageFormatException>(() => TiffTestUtil.Info(bad));

        // entry count larger than the file
        bad = (byte[])file.Clone();
        BinaryPrimitives.WriteUInt16LittleEndian(bad.AsSpan((int)ifd), 60000);
        Assert.Throws<ImageFormatException>(() => TiffTestUtil.Info(bad));

        // strip offset past the end: info parses, the strip read fails
        ImageInfo info = TiffTestUtil.Info(file);
        var st = (TiffImageState)info.DecoderState!;
        st.Frames[0].Offsets[1] = file.Length + 5;
        Assert.Throws<ImageFormatException>(() => TiffTestUtil.Decode(file, info: info));

        // giant strip count / byte count tags
        foreach (int tag in new[] { 273, 279 })
        {
            bad = (byte[])file.Clone();
            int e = EntryPos(bad, tag);
            BinaryPrimitives.WriteUInt32LittleEndian(bad.AsSpan(e + 4), 0x7FFFFFF0);
            AssertTypedThrows(() => TiffTestUtil.Info(bad));
        }
    }

    private static void AssertTypedThrows(Action act)
    {
        Exception? ex = Record.Exception(act);
        Assert.True(ex is ImageException, ex?.ToString() ?? "no exception");
    }

    private static int EntryPos(byte[] file, int tag)
    {
        int ifd = (int)BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(4));
        int count = BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(ifd));
        for (int i = 0; i < count; i++)
        {
            int p = ifd + 2 + (i * 12);
            if (BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(p)) == tag)
            {
                return p;
            }
        }

        throw new InvalidOperationException("tag not found");
    }

    [Fact]
    public void AbsurdDimensionsFailBeforeAllocation()
    {
        var b = Small();
        b.MakeSamples(1);
        byte[] file = b.Build();
        foreach ((int tag, uint value) in new[] { (256, 0x7FFFFFFFu), (257, 0x7FFFFFFFu), (256, 70000u), (256, 0u) })
        {
            byte[] bad = (byte[])file.Clone();
            int e = EntryPos(bad, tag);
            BinaryPrimitives.WriteUInt32LittleEndian(bad.AsSpan(e + 8), value);
            AssertTypedThrows(() => TiffTestUtil.Info(bad));
        }

        // 60000 x 60000 passes the dimension limit but not the pixel limit
        byte[] big = (byte[])file.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(big.AsSpan(EntryPos(big, 256) + 8), 60000);
        BinaryPrimitives.WriteUInt32LittleEndian(big.AsSpan(EntryPos(big, 257) + 8), 60000);
        Assert.Throws<ImageLimitException>(() => TiffTestUtil.Info(big));

        // a tiny tile size with a huge image gives too many tiles
        var t = new TiffBuilder { Width = 16, Height = 16, TileW = 16, TileH = 16 };
        t.MakeSamples(1);
        byte[] tiled = t.Build();
        BinaryPrimitives.WriteUInt32LittleEndian(tiled.AsSpan(EntryPos(tiled, 256) + 8), 60000);
        BinaryPrimitives.WriteUInt32LittleEndian(tiled.AsSpan(EntryPos(tiled, 257) + 8), 60000);
        AssertTypedThrows(() => TiffTestUtil.Info(tiled, new ImageLimits { MaxPixelsPerFrame = long.MaxValue }));
    }

    [Fact]
    public void DecompressionBombIsStoppedByTheRatioLimit()
    {
        // 16 MiB of zeros compresses to ~16 KiB with Deflate: ratio ~1000 passes, a stricter limit must refuse.
        var b = new TiffBuilder { Width = 4096, Height = 4096, Bits = 8, Spp = 1, RowsPerStrip = 4096, Compression = 8 };
        b.Samples = new uint[4096, 4096, 1];
        byte[] file = b.Build();
        Assert.True(file.Length < 100_000);
        var strict = new ImageLimits { MaxExpansionRatio = 50 };
        Assert.Throws<ImageLimitException>(() => TiffTestUtil.Decode(file, limits: strict));
        Assert.Equal(4096 * 4096, TiffTestUtil.Decode(file).Length);

        // a stream that would expand past the strip size is cut at the expected size, never beyond
        var lzw = new TiffBuilder { Width = 4096, Height = 4096, Bits = 8, Spp = 1, RowsPerStrip = 4096, Compression = 5, Samples = b.Samples };
        Assert.Throws<ImageLimitException>(() => TiffTestUtil.Decode(lzw.Build(), limits: new ImageLimits { MaxExpansionRatio = 20 }));
    }

    [Fact]
    public void TinyFileClaimingAHugeBlockAllocatesNothing()
    {
        foreach (int comp in new[] { 1, 5, 8, 32773, 4 })
        {
            var b = new TiffBuilder { Width = 16, Height = 16, Bits = comp == 4 ? 1 : 8, Spp = 1, Compression = comp, BlockOverride = _ => [1, 2, 3, 4] };
            b.MakeSamples(1);
            byte[] file = b.Build();
            // one 16000 x 16000 strip would be 256 MB of raw samples
            BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(EntryPos(file, 256) + 8), 16000);
            BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(EntryPos(file, 257) + 8), 16000);
            BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(EntryPos(file, 278) + 8), 16000);
            ImageInfo info = TiffTestUtil.Info(file);
            long before = GC.GetAllocatedBytesForCurrentThread();
            AssertTypedThrows(() => TiffTestUtil.Decode(file, region: new ImageRect(0, 0, 128, 128), info: info));
            Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 8_000_000, $"compression {comp} allocated too much");
        }
    }

    [Fact]
    public void BadCcittDataGivesTypedErrors()
    {
        byte[][] payloads =
        [
            [0x00, 0x00, 0x00, 0x00],
            [0xFF, 0xFF, 0xFF, 0xFF],
            [0x00, 0x10, 0x01, 0x80, 0x00],
            [],
            Enumerable.Range(0, 64).Select(i => (byte)(i * 37)).ToArray(),
        ];
        foreach (int comp in new[] { 2, 3, 4 })
        {
            foreach (byte[] payload in payloads)
            {
                var b = new TiffBuilder { Width = 64, Height = 20, Bits = 1, Spp = 1, Photometric = 0, Compression = comp, BlockOverride = _ => payload.Length == 0 ? [0] : payload };
                b.MakeSamples(1, smooth: false);
                byte[] file = b.Build();
                if (payload.Length > 0 && payload.All(v => v == 0))
                {
                    AssertTypedThrows(() => TiffTestUtil.Decode(file)); // zeros are never a valid code
                }
                else
                {
                    AssertTyped(() => TiffTestUtil.Decode(file)); // other garbage may happen to be valid; it must not crash or hang
                }
            }
        }
    }

    [Fact]
    public void MutatedFilesNeverCrashOrHang()
    {
        var sources = new List<byte[]>();
        foreach (string name in new[] { "rgb8_lzwpred", "gray16_deflatepred", "bilevel_g4", "bilevel_g3_2dfill", "palette8_packbits", "grayf32_lzwfloatpred", "cmyk8_none" })
        {
            sources.Add(File.ReadAllBytes(Path.Combine(TiffTestUtil.DataDir, name + ".tif")));
        }

        var tb = new TiffBuilder { Width = 50, Height = 40, Bits = 12, Spp = 3, Photometric = 2, Planar = true, TileW = 16, TileH = 16, Compression = 8, BigEndian = true };
        tb.MakeSamples(1, smooth: false);
        sources.Add(tb.Build());
        var big = new TiffBuilder { Width = 50, Height = 40, Bits = 8, Spp = 1, BigTiff = true, Compression = 32773 };
        big.MakeSamples(1);
        sources.Add(big.Build());

        var rnd = new Random(20261021);
        int runs = 0;
        // A longer hunt can be run with ONEIMAGE_TIFF_MUTATIONS=60000; the default keeps the suite fast.
        int iterations = int.TryParse(Environment.GetEnvironmentVariable("ONEIMAGE_TIFF_MUTATIONS"), out int env) ? env : 1500;
        foreach (byte[] src in sources)
        {
            for (int iter = 0; iter < iterations; iter++)
            {
                byte[] m = (byte[])src.Clone();
                int edits = rnd.Next(1, 6);
                for (int k = 0; k < edits; k++)
                {
                    // bias towards the header and directory area, where structure lives
                    int pos = rnd.Next(4) == 0 ? rnd.Next(m.Length) : Math.Min(m.Length - 1, rnd.Next(Math.Min(m.Length, 400)) + Math.Max(0, m.Length - 600) * rnd.Next(2));
                    switch (rnd.Next(3))
                    {
                        case 0: m[pos] = (byte)rnd.Next(256); break;
                        case 1: m[pos] ^= (byte)(1 << rnd.Next(8)); break;
                        default: m[pos] = rnd.Next(2) == 0 ? (byte)0xFF : (byte)0; break;
                    }
                }

                if (rnd.Next(10) == 0)
                {
                    Array.Resize(ref m, rnd.Next(1, m.Length));
                }

                runs++;
                try
                {
                    ImageInfo info = TiffTestUtil.Info(m, new ImageLimits { MaxPixelsPerFrame = 1 << 24, MaxTotalPixels = 1 << 25 });
                    for (int f = 0; f < Math.Min(info.Frames.Count, 3); f++)
                    {
                        TiffTestUtil.Decode(m, f, info: info, limits: new ImageLimits { MaxPixelsPerFrame = 1 << 24 });
                    }
                }
                catch (ImageException)
                {
                }
            }
        }

        Assert.True(runs > 10000);
    }

    [Fact]
    public void FrameAndPageLimitsAreEnforced()
    {
        var b = Small();
        b.MakeSamples(1);
        byte[] file = b.Build();
        Assert.Throws<ImageLimitException>(() => TiffTestUtil.Info(file, new ImageLimits { MaxPixelsPerFrame = 100 }));
    }
}
