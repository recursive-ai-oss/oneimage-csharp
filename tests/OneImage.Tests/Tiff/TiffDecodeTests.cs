using System.Buffers.Binary;

namespace OneImage.Tests.Tiff;

public sealed class TiffDecodeTests
{
    private static int Scale(long v, long max, long outMax) => (int)(((v * outMax) + (max / 2)) / max);

    /// <summary>The pixels a decoder must deliver for the builder's samples, derived straight from the documented rules.</summary>
    private static byte[] Expected(TiffBuilder b, out PixelFormat format)
    {
        bool isFloat = b.FloatSamples;
        int baseCh = b.Photometric switch { 5 => 4, 2 => 3, _ => 1 };
        bool palette = b.Photometric == 3;
        bool alpha = !palette && b.Photometric != 5 && b.Spp > baseCh;
        bool premult = alpha && b.ExtraSamples is [1, ..];
        long max = (1L << b.Bits) - 1;
        bool wide = b.Bits > 8;
        int channels = palette ? 3 : baseCh + (alpha ? 1 : 0);
        ColorModel model = b.Photometric switch { 5 => ColorModel.Cmyk, 0 or 1 => ColorModel.Gray, _ => ColorModel.Rgb };
        SampleType st = isFloat ? SampleType.F32 : wide ? SampleType.U16 : SampleType.U8;
        AlphaMode am = !alpha ? AlphaMode.None : (premult && !wide && model == ColorModel.Rgb) || (premult && isFloat) ? AlphaMode.Premultiplied : AlphaMode.Straight;
        format = new PixelFormat(model, st, am);
        int sb = format.BytesPerSample;
        byte[] o = new byte[b.Width * b.Height * channels * sb];
        int p = 0;
        for (int y = 0; y < b.Height; y++)
        {
            for (int x = 0; x < b.Width; x++)
            {
                if (palette)
                {
                    int idx = (int)b.Samples[y, x, 0];
                    int n = b.ColorMap!.Length / 3;
                    bool small = b.ColorMap.All(v => v < 256);
                    for (int c = 0; c < 3; c++)
                    {
                        int v = b.ColorMap[(c * n) + idx];
                        o[p++] = (byte)(small ? v : v >> 8);
                    }

                    continue;
                }

                for (int c = 0; c < channels; c++)
                {
                    uint v = b.Samples[y, x, c];
                    if (isFloat)
                    {
                        BinaryPrimitives.WriteUInt32LittleEndian(o.AsSpan(p), v);
                        p += 4;
                        continue;
                    }

                    bool isAlphaCh = alpha && c == channels - 1;
                    long vv = v;
                    if (b.Photometric == 0 && model == ColorModel.Gray && !isAlphaCh)
                    {
                        vv = max - v;
                    }

                    if (wide)
                    {
                        BinaryPrimitives.WriteUInt16LittleEndian(o.AsSpan(p), (ushort)Scale(vv, max, 65535));
                        p += 2;
                    }
                    else
                    {
                        o[p++] = (byte)Scale(vv, max, 255);
                    }
                }
            }
        }

        if (premult && format.Alpha == AlphaMode.Straight)
        {
            // Associated alpha without a premultiplied format is converted to straight on read.
            int ch = channels;
            for (int i = 0; i < o.Length; i += ch * sb)
            {
                if (wide)
                {
                    int a = BinaryPrimitives.ReadUInt16LittleEndian(o.AsSpan(i + ((ch - 1) * 2)));
                    for (int c = 0; c < ch - 1; c++)
                    {
                        int v = BinaryPrimitives.ReadUInt16LittleEndian(o.AsSpan(i + (c * 2)));
                        BinaryPrimitives.WriteUInt16LittleEndian(o.AsSpan(i + (c * 2)), a == 0 ? (ushort)0 : (ushort)Math.Min(65535, ((v * 65535L) + (a / 2)) / a));
                    }
                }
                else
                {
                    int a = o[i + ch - 1];
                    for (int c = 0; c < ch - 1; c++)
                    {
                        o[i + c] = a == 0 ? (byte)0 : (byte)Math.Min(255, ((o[i + c] * 255) + (a / 2)) / a);
                    }
                }
            }
        }

        return o;
    }

    private static void Check(TiffBuilder b, List<string> failures, string label)
    {
        byte[] file = b.Build();
        byte[] expected = Expected(b, out PixelFormat fmt);
        try
        {
            ImageInfo info = TiffTestUtil.Info(file);
            if (info.Frames[0].Format != fmt)
            {
                failures.Add($"{label}: format {info.Frames[0].Format} != {fmt}");
                return;
            }

            byte[] got = TiffTestUtil.Decode(file, info: info);
            if (!got.AsSpan().SequenceEqual(expected))
            {
                failures.Add($"{label}: pixels differ");
                return;
            }

            // A sub-rectangle must equal the same rectangle of the full decode.
            int rx = b.Width / 3;
            int ry = b.Height / 4;
            var r = new ImageRect(rx, ry, Math.Max(1, b.Width - rx - 2), Math.Max(1, b.Height - ry - 1));
            byte[] part = TiffTestUtil.Decode(file, region: r);
            int bpp = fmt.BytesPerPixel;
            for (int y = 0; y < r.Height; y++)
            {
                if (!part.AsSpan(y * r.Width * bpp, r.Width * bpp).SequenceEqual(expected.AsSpan((((ry + y) * b.Width) + rx) * bpp, r.Width * bpp)))
                {
                    failures.Add($"{label}: region differs at row {y}");
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            failures.Add($"{label}: {ex.GetType().Name} {ex.Message}");
        }
    }

    [Fact]
    public void GeneratedCorpusMatchesLibtiffBitExact()
    {
        string manifest = Path.Combine(TiffTestUtil.DataDir, "manifest.txt");
        var failures = new List<string>();
        int n = 0;
        foreach (string line in File.ReadAllLines(manifest).Where(l => l.Length > 0))
        {
            string[] p = line.Split('|');
            byte[] file = File.ReadAllBytes(Path.Combine(TiffTestUtil.DataDir, p[0] + ".tif"));
            byte[] expected = File.ReadAllBytes(Path.Combine(TiffTestUtil.DataDir, (p.Length > 4 ? p[4] : p[0]) + ".raw"));
            try
            {
                ImageInfo info = TiffTestUtil.Info(file);
                FrameInfo f = info.Frames[0];
                if (f.Width != int.Parse(p[1]) || f.Height != int.Parse(p[2]) || f.Format != TiffTestUtil.FormatByName(p[3]))
                {
                    failures.Add($"{p[0]}: header {f.Width}x{f.Height} {f.Format}");
                    continue;
                }

                if (!TiffTestUtil.Decode(file, info: info).AsSpan().SequenceEqual(expected))
                {
                    failures.Add($"{p[0]}: pixels differ");
                }

                n++;
            }
            catch (Exception ex)
            {
                failures.Add($"{p[0]}: {ex.GetType().Name} {ex.Message}");
            }
        }

        Assert.True(failures.Count == 0, string.Join("\n", failures));
        Assert.True(n >= 50, "corpus too small: " + n);
    }

    [Fact]
    public void SyntheticLayoutsDecodeExactly()
    {
        var failures = new List<string>();
        int cases = 0;
        int[] comps = [1, 5, 8, 32773];
        foreach (bool be in new[] { false, true })
        {
            foreach (int comp in comps)
            {
                foreach (bool tiled in new[] { false, true })
                {
                    // Gray, both polarities, every bit depth we scale
                    foreach (int bits in new[] { 1, 2, 4, 8, 9, 12, 16 })
                    {
                        foreach (int photo in new[] { 0, 1 })
                        {
                            int pred = bits is 8 or 16 && comp is 5 or 8 ? 2 : 1;
                            var b = new TiffBuilder { Bits = bits, Photometric = photo, BigEndian = be, Compression = comp, Predictor = pred };
                            if (tiled)
                            {
                                b.TileW = 16;
                                b.TileH = 16;
                            }

                            b.MakeSamples(bits + photo, smooth: bits >= 8);
                            Check(b, failures, $"gray bits={bits} photo={photo} be={be} comp={comp} tiled={tiled} pred={pred}");
                            cases++;
                        }
                    }

                    // RGB and RGBA, contiguous and planar
                    foreach (int bits in new[] { 4, 8, 12, 16 })
                    {
                        foreach (int spp in new[] { 3, 4, 5 })
                        {
                            foreach (bool planar in new[] { false, true })
                            {
                                foreach (int extra in spp == 3 ? new[] { 0 } : new[] { 2, 1 })
                                {
                                    int pred = bits is 8 or 16 && comp is 5 or 8 ? 2 : 1;
                                    var b = new TiffBuilder { Bits = bits, Spp = spp, Photometric = 2, Planar = planar, BigEndian = be, Compression = comp, Predictor = pred };
                                    if (spp > 3)
                                    {
                                        b.ExtraSamples = spp == 4 ? [extra] : [extra, 0];
                                    }

                                    if (tiled)
                                    {
                                        b.TileW = 16;
                                        b.TileH = 32;
                                    }

                                    b.MakeSamples(bits * spp, smooth: false);
                                    Check(b, failures, $"rgb bits={bits} spp={spp} planar={planar} extra={extra} be={be} comp={comp} tiled={tiled} pred={pred}");
                                    cases++;
                                }
                            }
                        }
                    }

                    // CMYK
                    foreach (int bits in new[] { 8, 16 })
                    {
                        foreach (bool planar in new[] { false, true })
                        {
                            var b = new TiffBuilder { Bits = bits, Spp = 4, Photometric = 5, Planar = planar, BigEndian = be, Compression = comp, Predictor = comp is 5 or 8 ? 2 : 1 };
                            if (tiled)
                            {
                                b.TileW = 32;
                                b.TileH = 16;
                            }

                            b.MakeSamples(77, smooth: false);
                            Check(b, failures, $"cmyk bits={bits} planar={planar} be={be} comp={comp} tiled={tiled}");
                            cases++;
                        }
                    }

                    // Palette
                    foreach (int bits in new[] { 1, 4, 8 })
                    {
                        int n = 1 << bits;
                        var map = new ushort[n * 3];
                        for (int i = 0; i < map.Length; i++)
                        {
                            map[i] = (ushort)((i * 2477) % 65536);
                        }

                        var b = new TiffBuilder { Bits = bits, Photometric = 3, ColorMap = map, BigEndian = be, Compression = comp };
                        if (tiled)
                        {
                            b.TileW = 16;
                            b.TileH = 16;
                        }

                        b.MakeSamples(5, smooth: false);
                        Check(b, failures, $"palette bits={bits} be={be} comp={comp} tiled={tiled}");
                        cases++;
                    }

                    // 32-bit float, with and without the floating point predictor, gray / RGB / RGBA
                    foreach (int spp in new[] { 1, 3, 4 })
                    {
                        foreach (int pred in new[] { 1, 3 })
                        {
                            var b = new TiffBuilder { Bits = 32, Spp = spp, Photometric = spp == 1 ? 1 : 2, FloatSamples = true, BigEndian = be, Compression = comp, Predictor = comp is 5 or 8 ? pred : 1 };
                            if (spp == 4)
                            {
                                b.ExtraSamples = [2];
                            }

                            if (tiled)
                            {
                                b.TileW = 16;
                                b.TileH = 16;
                            }

                            var rnd = new Random(spp * 10 + pred);
                            b.Samples = new uint[b.Height, b.Width, spp];
                            for (int y = 0; y < b.Height; y++)
                            {
                                for (int x = 0; x < b.Width; x++)
                                {
                                    for (int c = 0; c < spp; c++)
                                    {
                                        b.Samples[y, x, c] = BitConverter.SingleToUInt32Bits((float)((rnd.NextDouble() * 8) - 2) + (x / 7f));
                                    }
                                }
                            }

                            Check(b, failures, $"float spp={spp} pred={pred} be={be} comp={comp} tiled={tiled}");
                            cases++;
                        }
                    }
                }
            }
        }

        Assert.True(failures.Count == 0, $"{failures.Count} of {cases} failed:\n" + string.Join("\n", failures.Take(40)));
    }

    [Fact]
    public void BigTiffAndFillOrderAndOldStyleLzw()
    {
        var failures = new List<string>();
        var b = new TiffBuilder { Bits = 8, Spp = 3, Photometric = 2, BigTiff = true, Compression = 8, Predictor = 2 };
        b.MakeSamples(1);
        Check(b, failures, "bigtiff deflate");
        b = new TiffBuilder { Bits = 16, Spp = 1, BigTiff = true, BigEndian = true, TileW = 16, TileH = 16 };
        b.MakeSamples(2);
        Check(b, failures, "bigtiff be tiled");
        b = new TiffBuilder { Bits = 8, Compression = 5, FillOrder = 2 };
        b.MakeSamples(3);
        Check(b, failures, "fillorder 2 lzw");
        b = new TiffBuilder { Bits = 1, Photometric = 0, Compression = 1, FillOrder = 2 };
        b.MakeSamples(3, smooth: false);
        Check(b, failures, "fillorder 2 raw 1-bit");
        Assert.True(failures.Count == 0, string.Join("\n", failures));

        // Old-style LZW: least significant bit first with a late code-width change, which is the GIF variant with 8-bit symbols.
        var plain = new TiffBuilder { Bits = 8, Spp = 1, Compression = 1, RowsPerStrip = 17 };
        plain.MakeSamples(9);
        byte[] raw = new byte[plain.Width * plain.Height];
        for (int y = 0; y < plain.Height; y++)
        {
            for (int x = 0; x < plain.Width; x++)
            {
                raw[(y * plain.Width) + x] = (byte)plain.Samples[y, x, 0];
            }
        }

        byte[] old = OneImage.Compression.LzwEncoder.Compress(raw, OneImage.Compression.LzwVariant.Gif, 8);
        Assert.Equal(0, old[0]);
        Assert.Equal(1, old[1] & 1);
        var oldB = new TiffBuilder { Bits = 8, Spp = 1, Compression = 5, RowsPerStrip = 17, Samples = plain.Samples, BlockOverride = _ => old };
        byte[] got = TiffTestUtil.Decode(oldB.Build());
        Assert.Equal(raw, got);
    }

    [Fact]
    public void OddSizesAndSingleRowStrips()
    {
        var failures = new List<string>();
        foreach ((int w, int h) in new[] { (1, 1), (1, 9), (7, 1), (8, 8), (15, 3), (17, 17), (31, 5), (33, 129), (130, 131) })
        {
            foreach (int rps in new[] { 1, 3, 1000 })
            {
                var b = new TiffBuilder { Width = w, Height = h, Bits = 8, Spp = 3, Photometric = 2, RowsPerStrip = rps, Compression = 5 };
                b.MakeSamples(w + h);
                Check(b, failures, $"{w}x{h} rps={rps}");
            }
        }

        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    [Fact]
    public void LoadsThroughImageApiAndMatchesDirectDecode()
    {
        string path = Path.Combine(TiffTestUtil.DataDir, "rgba8_lzwpred.tif");
        Image img = Image.Load(path);
        Assert.Equal(53, img.Width);
        Assert.Equal(37, img.Height);
        Rgba32[] px = img.ToArray<Rgba32>();
        byte[] expected = File.ReadAllBytes(Path.Combine(TiffTestUtil.DataDir, "rgba8_lzwpred.raw"));
        Assert.Equal(expected, System.Runtime.InteropServices.MemoryMarshal.AsBytes(px.AsSpan()).ToArray());
        Image cropped = img.Crop(new ImageRect(10, 5, 20, 20));
        Rgba32[] c = cropped.ToArray<Rgba32>();
        Assert.Equal(px[(5 * 53) + 10], c[0]);
        Assert.Equal(px[(24 * 53) + 29], c[(19 * 20) + 19]);
    }
}
