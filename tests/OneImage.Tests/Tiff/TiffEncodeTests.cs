using System.Runtime.InteropServices;
using ImageMagick;

namespace OneImage.Tests.Tiff;

/// <summary>Round trips and cross-checks against libtiff (through Magick.NET) and encoder behavior.</summary>
internal readonly record struct Cmyk8Px(byte C, byte M, byte Y, byte K) : IPixel<Cmyk8Px>
{
    public static PixelFormat Format => PixelFormat.Cmyk8;
}

internal readonly record struct Cmyk16Px(ushort C, ushort M, ushort Y, ushort K) : IPixel<Cmyk16Px>
{
    public static PixelFormat Format => PixelFormat.Cmyk16;
}

public sealed class TiffEncodeTests
{
    private static ImageConfiguration Config { get; } = new ImageConfiguration().Add(TiffFormat.Instance);

    private static LoadOptions Load { get; } = new() { Configuration = Config };

    /// <summary>A source image in any supported format: a synthetic TIFF decoded by the library.</summary>
    private static Image Source(PixelFormat fmt, int w, int h, int seed)
    {
        var b = new TiffBuilder { Width = w, Height = h, Compression = 1, RowsPerStrip = 7 };
        bool isF = fmt.Sample == SampleType.F32;
        b.Bits = fmt.Sample switch { SampleType.U8 => 8, SampleType.U16 => 16, _ => 32 };
        b.Spp = fmt.ChannelCount;
        b.FloatSamples = isF;
        b.Photometric = fmt.Model switch { ColorModel.Gray => 1, ColorModel.Rgb => 2, _ => 5 };
        if (fmt.HasAlpha)
        {
            b.ExtraSamples = [2];
        }

        if (isF)
        {
            var rnd = new Random(seed);
            b.Samples = new uint[h, w, b.Spp];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    for (int c = 0; c < b.Spp; c++)
                    {
                        b.Samples[y, x, c] = BitConverter.SingleToUInt32Bits((float)((rnd.NextDouble() * 3) - 1) + (x / 9f));
                    }
                }
            }
        }
        else
        {
            b.MakeSamples(seed, smooth: false);
        }

        return Image.Load(b.Build(), Load);
    }

    private static readonly PixelFormat[] AllFormats =
    [
        PixelFormat.Gray8, PixelFormat.GrayAlpha8, PixelFormat.Rgb8, PixelFormat.Rgba8, PixelFormat.Gray16, PixelFormat.Rgb16, PixelFormat.Rgba16,
        PixelFormat.GrayF32, PixelFormat.RgbF32, PixelFormat.RgbaF32, PixelFormat.Cmyk8, PixelFormat.Cmyk16,
    ];

    private static byte[] Pixels(Image img, int frame = 0)
    {
        ImageFrame f = img.Frames[frame];
        byte[] data = new byte[f.Width * f.Height * f.Format.BytesPerPixel];
        // Typed access for each layout we use; the raw bytes are what matters.
        switch (f.Format.Model, f.Format.Sample, f.Format.HasAlpha)
        {
            case (ColorModel.Gray, SampleType.U8, false): MemoryMarshal.AsBytes(f.ToArray<Gray8>().AsSpan()).CopyTo(data); break;
            case (ColorModel.Gray, SampleType.U8, true): MemoryMarshal.AsBytes(f.ToArray<GrayAlpha8>().AsSpan()).CopyTo(data); break;
            case (ColorModel.Gray, SampleType.U16, _): MemoryMarshal.AsBytes(f.ToArray<Gray16>().AsSpan()).CopyTo(data); break;
            case (ColorModel.Gray, SampleType.F32, _): MemoryMarshal.AsBytes(f.ToArray<GrayFloat>().AsSpan()).CopyTo(data); break;
            case (ColorModel.Rgb, SampleType.U8, false): MemoryMarshal.AsBytes(f.ToArray<Rgb24>().AsSpan()).CopyTo(data); break;
            case (ColorModel.Rgb, SampleType.U8, true): MemoryMarshal.AsBytes(f.ToArray<Rgba32>().AsSpan()).CopyTo(data); break;
            case (ColorModel.Rgb, SampleType.U16, false): MemoryMarshal.AsBytes(f.ToArray<Rgb48>().AsSpan()).CopyTo(data); break;
            case (ColorModel.Rgb, SampleType.U16, true): MemoryMarshal.AsBytes(f.ToArray<Rgba64>().AsSpan()).CopyTo(data); break;
            case (ColorModel.Rgb, SampleType.F32, false): MemoryMarshal.AsBytes(f.ToArray<RgbFloat>().AsSpan()).CopyTo(data); break;
            case (ColorModel.Rgb, SampleType.F32, true): MemoryMarshal.AsBytes(f.ToArray<RgbaFloat>().AsSpan()).CopyTo(data); break;
            case (ColorModel.Cmyk, SampleType.U8, _): MemoryMarshal.AsBytes(f.ToArray<Cmyk8Px>().AsSpan()).CopyTo(data); break;
            default: MemoryMarshal.AsBytes(f.ToArray<Cmyk16Px>().AsSpan()).CopyTo(data); break;
        }

        return data;
    }

    private static byte[] Save(Image img, TiffEncoderOptions? o = null)
    {
        using var ms = new MemoryStream();
        img.Save(ms, TiffFormat.Instance, new SaveOptions { Configuration = Config, EncoderOptions = o });
        return ms.ToArray();
    }

    [Fact]
    public void EveryFormatAndOptionRoundTripsBitExact()
    {
        var failures = new List<string>();
        int cases = 0;
        foreach (PixelFormat fmt in AllFormats)
        {
            Image src = Source(fmt, 61, 43, fmt.GetHashCode());
            byte[] want = Pixels(src);
            foreach (TiffCompression comp in Enum.GetValues<TiffCompression>())
            {
                foreach (bool pred in new[] { false, true })
                {
                    foreach (bool big in new[] { false, true })
                    {
                        foreach (int rps in new[] { 0, 5 })
                        {
                            cases++;
                            string label = $"{fmt} {comp} pred={pred} big={big} rps={rps}";
                            try
                            {
                                byte[] file = Save(src, new TiffEncoderOptions { Compression = comp, UsePredictor = pred, BigTiff = big, RowsPerStrip = rps });
                                Image back = Image.Load(file, Load);
                                if (back.Format != fmt || !Pixels(back).AsSpan().SequenceEqual(want))
                                {
                                    failures.Add(label + ": mismatch");
                                }
                            }
                            catch (Exception ex)
                            {
                                failures.Add($"{label}: {ex.GetType().Name} {ex.Message}");
                            }
                        }
                    }
                }
            }
        }

        Assert.True(failures.Count == 0, $"{failures.Count}/{cases}\n" + string.Join("\n", failures.Take(30)));
    }

    /// <summary>
    /// With ONEIMAGE_TIFF_DUMP set to a directory, writes every format/option combination there with the expected pixels, for
    /// tools/verify-tiff-with-libtiff.py (an independent libtiff read of 16-bit and float files, which Magick.NET Q8 cannot give).
    /// </summary>
    [Fact]
    public void DumpFilesForLibtiffCheck()
    {
        string? dir = Environment.GetEnvironmentVariable("ONEIMAGE_TIFF_DUMP");
        if (string.IsNullOrEmpty(dir))
        {
            return;
        }

        Directory.CreateDirectory(dir);
        foreach (PixelFormat fmt in AllFormats)
        {
            Image src = Source(fmt, 61, 43, fmt.GetHashCode());
            byte[] want = Pixels(src);
            foreach (TiffCompression comp in Enum.GetValues<TiffCompression>())
            {
                foreach (bool pred in new[] { false, true })
                {
                    foreach (bool big in new[] { false, true })
                    {
                        string name = $"{fmt.Model}{fmt.Sample}{fmt.Alpha}_{comp}_{(pred ? "pred" : "nopred")}_{(big ? "big" : "classic")}";
                        File.WriteAllBytes(Path.Combine(dir, name + ".tif"), Save(src, new TiffEncoderOptions { Compression = comp, UsePredictor = pred, BigTiff = big, RowsPerStrip = 5 }));
                        File.WriteAllBytes(Path.Combine(dir, name + ".raw"), want);
                    }
                }
            }
        }
    }

    [Fact]
    public void OutputIsByteIdenticalAcrossRuns()
    {
        Image src = Source(PixelFormat.Rgba8, 120, 90, 4);
        foreach (TiffCompression comp in Enum.GetValues<TiffCompression>())
        {
            var o = new TiffEncoderOptions { Compression = comp };
            Assert.Equal(Save(src, o), Save(src, o));
        }
    }

    [Fact]
    public void MultiPageWithDifferentSizesAndFormats()
    {
        Image a = Source(PixelFormat.Rgb8, 30, 20, 1);
        Image b = Source(PixelFormat.Gray16, 11, 40, 2);
        Image c = Source(PixelFormat.RgbaF32, 64, 9, 3);
        var img = new Image([a.Frames[0].Node, b.Frames[0].Node, c.Frames[0].Node], [default, default, default], 0, ImageMetadata.Empty, null);
        foreach (bool big in new[] { false, true })
        {
            byte[] file = Save(img, new TiffEncoderOptions { BigTiff = big });
            Image back = Image.Load(file, Load);
            Assert.Equal(3, back.FrameCount);
            Assert.Equal((30, 20), (back.Frames[0].Width, back.Frames[0].Height));
            Assert.Equal((11, 40), (back.Frames[1].Width, back.Frames[1].Height));
            Assert.Equal((64, 9), (back.Frames[2].Width, back.Frames[2].Height));
            Assert.Equal(PixelFormat.Gray16, back.Frames[1].Format);
            for (int i = 0; i < 3; i++)
            {
                Assert.Equal(Pixels(img, i), Pixels(back, i));
            }

            // libtiff sees the same three pages
            using var coll = new MagickImageCollection(file);
            Assert.Equal(3, coll.Count);
            Assert.Equal(11u, coll[1].Width);
            Assert.Equal(40u, coll[1].Height);
        }
    }

    [Fact]
    public void MetadataBlobsRoundTrip()
    {
        byte[] icc = Enumerable.Range(0, 3000).Select(i => (byte)(i * 7)).ToArray();
        byte[] xmp = System.Text.Encoding.UTF8.GetBytes("<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf/></x:xmpmeta>");
        byte[] iptc = Enumerable.Range(0, 64).Select(i => (byte)(0x1C + i)).ToArray();
        byte[] iptcOdd = iptc[..61];
        foreach (bool bigEndianExif in new[] { false, true })
        {
            byte[] exif = BuildExif(bigEndianExif);
            foreach (byte[] ip in new[] { iptc, iptcOdd })
            {
                foreach (bool big in new[] { false, true })
                {
                    Image src = Source(PixelFormat.Rgb8, 20, 30, 5);
                    var meta = new ImageMetadata { IccProfile = icc, Xmp = xmp, Iptc = ip, Exif = exif, Orientation = 6 };
                    var img = new Image([src.Frames[0].Node], [default], 0, meta, null);
                    byte[] file = Save(img, new TiffEncoderOptions { BigTiff = big });
                    // Orientation is a stored tag; the core applies it on load unless asked not to.
                    Image oriented = Image.Load(file, Load);
                    Assert.Equal((30, 20, 1), (oriented.Width, oriented.Height, oriented.Metadata.Orientation));
                    Image back = Image.Load(file, new LoadOptions { Configuration = Config, AutoOrient = false });
                    Assert.Equal(icc, back.Metadata.IccProfile.ToArray());
                    Assert.Equal(xmp, back.Metadata.Xmp.ToArray());
                    Assert.Equal(ip.Length % 4 == 0 ? ip : ip, back.Metadata.Iptc.ToArray()[..ip.Length]);
                    Assert.Equal(6, back.Metadata.Orientation);

                    // The EXIF blob is normalized once; after that it is stable and holds the same entries.
                    byte[] normalized = back.Metadata.Exif.ToArray();
                    Assert.NotEmpty(normalized);
                    var expected = OneImage.Codecs.Tiff.TiffMetadataIo.ParseBlob(exif, ImageLimits.Default)!;
                    var actual = OneImage.Codecs.Tiff.TiffMetadataIo.ParseBlob(normalized, ImageLimits.Default)!;
                    // The core may add the pixel-size and orientation tags on save; everything that was in the blob must still be there.
                    AssertContains(expected, actual);
                    Image again = Image.Load(Save(back, new TiffEncoderOptions { BigTiff = big }), Load);
                    Assert.Equal(normalized, again.Metadata.Exif.ToArray());

                    // libtiff reads the ICC profile and the EXIF-bearing file
                    using var m = new MagickImage(file);
                    Assert.Equal(icc, m.GetProfile("icc")!.ToByteArray());
                }
            }
        }
    }

    private static void AssertContains(List<OneImage.Codecs.Tiff.TiffTreeEntry> expected, List<OneImage.Codecs.Tiff.TiffTreeEntry> actual)
    {
        foreach (var e in expected)
        {
            var a = actual.SingleOrDefault(x => x.Tag == e.Tag);
            Assert.NotNull(a);
            Assert.Equal((e.Type, e.Count), (a.Type, a.Count));
            if (e.Sub is not null)
            {
                AssertContains(e.Sub, a.Sub!);
            }
            else
            {
                Assert.Equal(e.Value, a.Value);
            }
        }
    }

    private static string Describe(List<OneImage.Codecs.Tiff.TiffTreeEntry> tree)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var e in tree.OrderBy(e => e.Tag))
        {
            sb.Append($"{e.Tag}/{e.Type}/{e.Count}/{(e.Sub is null ? Convert.ToHexString(e.Value) : "{" + Describe(e.Sub) + "}")};");
        }

        return sb.ToString();
    }

    private static byte[] BuildExif(bool be)
    {
        using var ms = new MemoryStream();
        var make = new OneImage.Codecs.Tiff.TiffTreeEntry(271, 2, 6, "Canon\0"u8.ToArray());
        var soft = new OneImage.Codecs.Tiff.TiffTreeEntry(305, 2, 13, "OneImage test\0"u8.ToArray()[..13]);
        byte[] rat = new byte[8];
        OneImage.Codecs.Tiff.TiffMetadataIo.PutU32(rat, 1, be);
        OneImage.Codecs.Tiff.TiffMetadataIo.PutU32(rat.AsSpan(4), 250, be);
        byte[] iso = new byte[2];
        OneImage.Codecs.Tiff.TiffMetadataIo.PutU16(iso, 400, be);
        var exifIfd = new List<OneImage.Codecs.Tiff.TiffTreeEntry>
        {
            new(33434, 5, 1, rat),
            new(34855, 3, 1, iso),
            new(37510, 7, 20, Enumerable.Range(0, 20).Select(i => (byte)(i + 65)).ToArray()),
        };
        var ifd0 = new List<OneImage.Codecs.Tiff.TiffTreeEntry> { make, soft, new(34665, 4, 1, []) { Sub = exifIfd } };
        var b = new OneImage.Codecs.Tiff.TiffByteBuilder();
        b.AppendZeros(8);
        Span<byte> h = b.At(0, 8);
        h[0] = h[1] = be ? (byte)'M' : (byte)'I';
        OneImage.Codecs.Tiff.TiffMetadataIo.PutU16(h[2..], 42, be);
        OneImage.Codecs.Tiff.TiffMetadataIo.PutU32(h[4..], 8, be);
        OneImage.Codecs.Tiff.TiffMetadataIo.WriteIfd(b, 0, ifd0, false, be, 0);
        _ = ms;
        return b.Written.ToArray();
    }

    // ---- libtiff as the reader of our files ------------------------------------------------------------------------

    [Fact]
    public void LibtiffReadsOurFiles()
    {
        foreach ((PixelFormat fmt, string map) in new[] { (PixelFormat.Rgb8, "RGB"), (PixelFormat.Rgba8, "RGBA"), (PixelFormat.Gray8, "I") })
        {
            Image src = Source(fmt, 77, 49, 11);
            byte[] want = Pixels(src);
            foreach (TiffCompression comp in Enum.GetValues<TiffCompression>())
            {
                foreach (bool pred in new[] { false, true })
                {
                    foreach (bool big in new[] { false, true })
                    {
                        byte[] file = Save(src, new TiffEncoderOptions { Compression = comp, UsePredictor = pred, BigTiff = big, RowsPerStrip = 6 });
                        using var m = new MagickImage(file);
                        byte[] got = m.GetPixels().ToByteArray(map)!;
                        Assert.True(got.AsSpan().SequenceEqual(want), $"{fmt} {comp} pred={pred} big={big}");
                    }
                }
            }
        }
    }

    // ---- libtiff as the writer of files we read ---------------------------------------------------------------------

    private static byte[] RandomRgb(int w, int h, int channels, int seed)
    {
        var rnd = new Random(seed);
        byte[] d = new byte[w * h * channels];
        for (int i = 0; i < d.Length; i++)
        {
            d[i] = (byte)(((i / channels % w) * 3) + rnd.Next(5) + (i % channels * 40));
        }

        return d;
    }

    [Fact]
    public void ReadsFilesWrittenByLibtiffWithTilesBigTiffAndBothByteOrders()
    {
        var failures = new List<string>();
        int n = 0;
        foreach (bool tiled in new[] { false, true })
        {
            foreach (bool big in new[] { false, true })
            {
                foreach (string endian in new[] { "lsb", "msb" })
                {
                    foreach (CompressionMethod comp in new[] { CompressionMethod.NoCompression, CompressionMethod.LZW, CompressionMethod.Zip, CompressionMethod.RLE })
                    {
                        foreach ((int ch, string map) in new[] { (3, "RGB"), (4, "RGBA"), (1, "I") })
                        {
                            const int w = 197;
                            const int h = 131;
                            byte[] raw = RandomRgb(w, h, ch, ch);
                            using var img = new MagickImage(raw, new PixelReadSettings(w, h, StorageType.Char, map));
                            img.Format = big ? MagickFormat.Tiff64 : MagickFormat.Tiff;
                            img.Settings.Compression = comp;
                            img.Settings.SetDefine("tiff:endian", endian);
                            if (ch == 4)
                            {
                                img.Settings.SetDefine("tiff:alpha", "unassociated");
                            }

                            if (tiled)
                            {
                                img.Settings.SetDefine("tiff:tile-geometry", "64x48");
                            }

                            using var ms = new MemoryStream();
                            img.Write(ms);
                            byte[] file = ms.ToArray();
                            n++;
                            string label = $"tiled={tiled} big={big} {endian} {comp} ch={ch}";
                            try
                            {
                                using var lib = new MagickImage(file);
                                byte[] want = lib.GetPixels().ToByteArray(map)!;
                                ImageInfo info = TiffTestUtil.Info(file);
                                byte[] got = TiffTestUtil.Decode(file, info: info);
                                if (!got.AsSpan().SequenceEqual(want))
                                {
                                    failures.Add(label + ": differs from libtiff");
                                }

                                if (tiled && !(file[0] == (endian == "lsb" ? 'I' : 'M')))
                                {
                                    failures.Add(label + ": byte order");
                                }
                            }
                            catch (Exception ex)
                            {
                                failures.Add($"{label}: {ex.GetType().Name} {ex.Message}");
                            }
                        }
                    }
                }
            }
        }

        Assert.True(failures.Count == 0, $"{failures.Count}/{n}\n" + string.Join("\n", failures.Take(20)));
    }

    [Fact]
    public void ReadsMultiPageFileFromLibtiff()
    {
        using var coll = new MagickImageCollection();
        for (int i = 0; i < 4; i++)
        {
            int w = 40 + (i * 13);
            int h = 30 + (i * 7);
            coll.Add(new MagickImage(RandomRgb(w, h, 3, i), new PixelReadSettings((uint)w, (uint)h, StorageType.Char, PixelMapping.RGB)));
        }

        using var ms = new MemoryStream();
        coll.Write(ms, MagickFormat.Tiff);
        byte[] file = ms.ToArray();
        ImageInfo info = TiffTestUtil.Info(file);
        Assert.Equal(4, info.Frames.Count);
        using var back = new MagickImageCollection(file);
        for (int i = 0; i < 4; i++)
        {
            Assert.Equal((int)back[i].Width, info.Frames[i].Width);
            Assert.Equal((int)back[i].Height, info.Frames[i].Height);
            Assert.Equal(back[i].GetPixels().ToByteArray(PixelMapping.RGB), TiffTestUtil.Decode(file, i, info: info));
        }
    }
}
