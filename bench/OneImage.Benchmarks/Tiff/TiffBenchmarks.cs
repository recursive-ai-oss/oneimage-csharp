using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using BenchmarkDotNet.Attributes;
using ImageMagick;
using SixLabors.ImageSharp.Processing;
using OneImage.Codecs.Tiff;
using OneImage.Compression;
using OneImage.Kernels;

namespace OneImage.Benchmarks.Tiff;

/// <summary>Deterministic photo-like pixels and the big test files the TIFF benchmarks read.</summary>
internal static class TiffBenchData
{
    public static string Dir { get; } = Path.Combine(Environment.GetEnvironmentVariable("ONEIMAGE_BENCH_DIR") ?? Path.GetTempPath(), "oneimage-tiff-bench");

    public static byte[] Rgb(int w, int h, int seed)
    {
        var rnd = new Random(seed);
        byte[] d = new byte[w * h * 3];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int i = ((y * w) + x) * 3;
                int v = (x / 3) + (y / 2);
                d[i] = (byte)(v + rnd.Next(6));
                d[i + 1] = (byte)((v / 2) + (x / 9) + rnd.Next(6));
                d[i + 2] = (byte)(255 - (v / 3) + rnd.Next(6));
            }
        }

        return d;
    }

    /// <summary>Ten 2000x1500 RGB LZW pages written by libtiff.</summary>
    public static string MultiPage()
    {
        Directory.CreateDirectory(Dir);
        string path = Path.Combine(Dir, "pages10.tif");
        if (!File.Exists(path))
        {
            using var coll = new MagickImageCollection();
            for (int i = 0; i < 10; i++)
            {
                coll.Add(new MagickImage(Rgb(2000, 1500, i), new PixelReadSettings(2000, 1500, StorageType.Char, PixelMapping.RGB)));
            }

            foreach (IMagickImage<byte> im in coll)
            {
                im.Settings.Compression = CompressionMethod.LZW;
            }

            coll.Write(path, MagickFormat.Tiff);
        }

        return path;
    }

    /// <summary>A tiled RGB LZW (horizontal predictor) TIFF of the given size, streamed to disk one tile at a time.</summary>
    public static string Tiled(int size)
    {
        Directory.CreateDirectory(Dir);
        string path = Path.Combine(Dir, $"tiled{size}.tif");
        if (File.Exists(path))
        {
            return path;
        }

        const int tile = 256;
        int across = (size + tile - 1) / tile;
        long[] offsets = new long[across * across];
        long[] counts = new long[offsets.Length];
        string tmp = path + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write))
        {
            fs.Write([(byte)'I', (byte)'I', 43, 0, 8, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]);
            byte[] raw = new byte[tile * tile * 3];
            for (int ty = 0; ty < across; ty++)
            {
                for (int tx = 0; tx < across; tx++)
                {
                    for (int y = 0; y < tile; y++)
                    {
                        for (int x = 0; x < tile; x++)
                        {
                            int gx = (tx * tile) + x;
                            int gy = (ty * tile) + y;
                            int i = ((y * tile) + x) * 3;
                            int v = (gx / 3) + (gy / 2);
                            int n = (int)(((uint)((gx * 73856093) ^ (gy * 19349663)) >> 13) % 6);
                            raw[i] = (byte)(v + n);
                            raw[i + 1] = (byte)((v / 2) + (gx / 9) + n);
                            raw[i + 2] = (byte)(255 - (v / 3) + n);
                        }
                    }

                    TiffKernels.ApplyHorizontal(raw, tile * 3, 3, 8);
                    byte[] c = LzwEncoder.Compress(raw, LzwVariant.Tiff);
                    if ((fs.Position & 1) != 0)
                    {
                        fs.WriteByte(0);
                    }

                    offsets[(ty * across) + tx] = fs.Position;
                    counts[(ty * across) + tx] = c.Length;
                    fs.Write(c);
                }
            }

            if ((fs.Position & 1) != 0)
            {
                fs.WriteByte(0);
            }

            long ifdPos = fs.Position;
            byte[] Arr(long[] v)
            {
                byte[] d = new byte[v.Length * 8];
                for (int i = 0; i < v.Length; i++)
                {
                    BinaryPrimitives.WriteInt64LittleEndian(d.AsSpan(i * 8), v[i]);
                }

                return d;
            }

            static TiffTreeEntry S(int tag, params int[] v)
            {
                byte[] d = new byte[v.Length * 2];
                for (int i = 0; i < v.Length; i++)
                {
                    BinaryPrimitives.WriteUInt16LittleEndian(d.AsSpan(i * 2), (ushort)v[i]);
                }

                return new TiffTreeEntry(tag, 3, v.Length, d);
            }

            static TiffTreeEntry L(int tag, int v)
            {
                byte[] d = new byte[4];
                BinaryPrimitives.WriteInt32LittleEndian(d, v);
                return new TiffTreeEntry(tag, 4, 1, d);
            }

            var entries = new List<TiffTreeEntry>
            {
                L(256, size), L(257, size), S(258, 8, 8, 8), S(259, 5), S(262, 2), S(277, 3), S(284, 1), S(317, 2),
                L(322, tile), L(323, tile),
                new(324, 16, offsets.Length, Arr(offsets)),
                new(325, 16, counts.Length, Arr(counts)),
            };
            var b = new TiffByteBuilder();
            TiffMetadataIo.WriteIfd(b, ifdPos, entries, bigTiff: true, bigEndian: false, 0);
            fs.Write(b.Written);
            fs.Position = 8;
            byte[] first = new byte[8];
            BinaryPrimitives.WriteInt64LittleEndian(first, ifdPos);
            fs.Write(first);
        }

        File.Move(tmp, path);
        return path;
    }
}

public abstract class TiffBenchBase
{
    protected static long Sum(ReadOnlySpan<byte> data)
    {
        long s = 0;
        for (int i = 0; i < data.Length; i += 4099)
        {
            s += data[i];
        }

        return s;
    }
}

/// <summary>Decode all pages of a 10-page 2000x1500 RGB LZW TIFF into memory.</summary>
[MemoryDiagnoser]
[ShortRunJob]
public class TiffMultiPageDecode : TiffBenchBase
{
    private string _path = null!;
    private Rgb24[] _page = [];

    [GlobalSetup]
    public void Setup()
    {
        _path = TiffBenchData.MultiPage();
        _page = new Rgb24[2000 * 1500];
    }

    [BenchmarkCategory("Gate", "tiff-multipage-decode")]
    [Benchmark(Baseline = true, Description = "ImageSharp")]
    public long ImageSharp()
    {
        using var img = SixLabors.ImageSharp.Image.Load<SixLabors.ImageSharp.PixelFormats.Rgb24>(_path);
        long s = 0;
        for (int i = 0; i < img.Frames.Count; i++)
        {
            s += img.Frames[i].DangerousTryGetSinglePixelMemory(out var mem) ? mem.Span[^1].R : 0;
        }

        return s + img.Frames.Count;
    }

    // Each page is decoded into one buffer the caller keeps (ImageSharp reuses pooled memory across loads; allocating a fresh
    // 9 MB array per page would charge us for something that row does not pay). The ToArray row shows the allocating form.
    [BenchmarkCategory("Gate", "tiff-multipage-decode")]
    [Benchmark(Description = "oneimage, pages into a reused buffer")]
    public long OneImage() => OneImageWithDegree(0);

    [BenchmarkCategory("tiff-multipage-decode-alloc")]
    [Benchmark(Description = "oneimage, ToArray per page (allocates)")]
    public long OneImageToArray()
    {
        Image img = Image.Load(_path);
        long s = 0;
        for (int i = 0; i < img.FrameCount; i++)
        {
            Rgb24[] px = img.Frames[i].ToArray<Rgb24>();
            s += px[^1].R;
        }

        return s + img.FrameCount;
    }

    private long OneImageWithDegree(int degree)
    {
        Image img = Image.Load(_path);
        long s = 0;
        for (int i = 0; i < img.FrameCount; i++)
        {
            ImageFrame f = img.Frames[i];
            f.CopyPixelsTo<Rgb24>(_page, new ImageRect(0, 0, f.Width, f.Height), degree);
            s += _page[^1].R;
        }

        return s + img.FrameCount;
    }

    // Not the default configuration: a per-call degree above the default of min(4, cores). Shown beside the gate row, never used as it.
    [BenchmarkCategory("Gate", "tiff-multipage-decode")]
    [Benchmark(Description = "oneimage, NOT DEFAULT: per-call maxDegree = all cores")]
    public long OneImageDegreeAllCores() => OneImageWithDegree(Environment.ProcessorCount);

    [BenchmarkCategory("Gate", "tiff-multipage-decode")]
    [Benchmark(Description = "oneimage, NOT DEFAULT: per-call maxDegree = 8")]
    public long OneImageDegree8() => OneImageWithDegree(8);

    [BenchmarkCategory("Gate", "tiff-multipage-decode")]
    [Benchmark(Description = "Magick.NET")]
    public long Magick()
    {
        using var coll = new MagickImageCollection(_path);
        long s = 0;
        foreach (IMagickImage<byte> im in coll)
        {
            using var pix = im.GetPixelsUnsafe();
            s += pix.GetArea(0, 0, 1, 1)![0];
        }

        return s + coll.Count;
    }

    [BenchmarkCategory("Gate", "tiff-multipage-decode")]
    [Benchmark(Description = "NetVips")]
    public long NetVips()
    {
        long s = 0;
        for (int i = 0; i < 10; i++)
        {
            using var im = global::NetVips.Image.Tiffload(_path, page: i);
            byte[] mem = im.WriteToMemory<byte>();
            s += mem[^1];
        }

        return s;
    }
}

/// <summary>Decode one 512x512 region of a large tiled TIFF.</summary>
[MemoryDiagnoser]
[ShortRunJob]
public class TiffRegionDecode : TiffBenchBase
{
    private string _path = null!;

    [Params(8000, 20000)]
    public int Size { get; set; }

    [GlobalSetup]
    public void Setup() => _path = TiffBenchData.Tiled(Size);

    // Libraries without region access load the whole image first; at 20000x20000 (1.2 GB of RGB) that does not fit the
    // memory this host gives a benchmark process, so those rows are skipped there and measured at 8000 only.
    [Benchmark(Baseline = true, Description = "ImageSharp (whole image, then crop)")]
    public long ImageSharp()
    {
        if (Size > 10000)
        {
            return -1;
        }

        using var img = SixLabors.ImageSharp.Image.Load<SixLabors.ImageSharp.PixelFormats.Rgb24>(_path);
        img.Mutate(c => c.Crop(new SixLabors.ImageSharp.Rectangle(3000, 3500, 512, 512)));
        return img.Width;
    }

    [Benchmark(Description = "oneimage")]
    public long OneImage()
    {
        // 20000x20000 is 400 MP, above the default per-frame limit of 268 MP, so the limit is raised for this file.
        var options = new LoadOptions { Limits = new ImageLimits { MaxPixelsPerFrame = 1L << 30, MaxTotalPixels = 1L << 31 } };
        Image img = Image.Load(_path, options);
        Rgb24[] px = img.Crop(3000, 3500, 512, 512).ToArray<Rgb24>();
        return px[^1].R;
    }

    [Benchmark(Description = "Magick.NET (whole image, then crop)")]
    public long Magick()
    {
        if (Size > 10000)
        {
            return -1;
        }

        using var im = new MagickImage(_path);
        im.Crop(new MagickGeometry(3000, 3500, 512, 512));
        return im.Width;
    }

    [Benchmark(Description = "NetVips (tile-addressed)")]
    public long NetVips()
    {
        using var im = global::NetVips.Image.NewFromFile(_path, access: global::NetVips.Enums.Access.Random);
        using var c = im.Crop(3000, 3500, 512, 512);
        return c.WriteToMemory<byte>()[^1];
    }
}

/// <summary>LZW encode of a 2000x1500 RGB image to memory.</summary>
[MemoryDiagnoser]
[ShortRunJob]
public class TiffLzwEncode : TiffBenchBase
{
    private byte[] _rgb = null!;
    private Rgb24[] _px = null!;

    [GlobalSetup]
    public void Setup()
    {
        _rgb = TiffBenchData.Rgb(2000, 1500, 99);
        _px = new Rgb24[2000 * 1500];
        for (int i = 0; i < _px.Length; i++)
        {
            _px[i] = new Rgb24(_rgb[i * 3], _rgb[(i * 3) + 1], _rgb[(i * 3) + 2]);
        }
    }

    [BenchmarkCategory("Gate", "tiff-encode")]
    [Benchmark(Baseline = true, Description = "ImageSharp")]
    public long ImageSharp()
    {
        using var img = SixLabors.ImageSharp.Image.LoadPixelData<SixLabors.ImageSharp.PixelFormats.Rgb24>(_rgb, 2000, 1500);
        using var ms = new MemoryStream();
        img.Save(ms, new SixLabors.ImageSharp.Formats.Tiff.TiffEncoder { Compression = SixLabors.ImageSharp.Formats.Tiff.Constants.TiffCompression.Lzw });
        return ms.Length;
    }

    [BenchmarkCategory("Gate", "tiff-encode")]
    [Benchmark(Description = "oneimage (no predictor)")]
    public long OneImageNoPredictor()
    {
        Image img = Image.FromPixels<Rgb24>(_px, 2000, 1500);
        using var ms = new MemoryStream();
        img.Save(ms, TiffFormat.Instance, new SaveOptions { EncoderOptions = new TiffEncoderOptions { UsePredictor = false } });
        return ms.Length;
    }

    [BenchmarkCategory("Gate", "tiff-encode")]
    [Benchmark(Description = "oneimage (horizontal predictor)")]
    public long OneImagePredictor()
    {
        Image img = Image.FromPixels<Rgb24>(_px, 2000, 1500);
        using var ms = new MemoryStream();
        img.Save(ms, TiffFormat.Instance);
        return ms.Length;
    }

    [BenchmarkCategory("Gate", "tiff-encode")]
    [Benchmark(Description = "Magick.NET")]
    public long Magick()
    {
        using var im = new MagickImage(_rgb, new PixelReadSettings(2000, 1500, StorageType.Char, PixelMapping.RGB));
        im.Settings.Compression = CompressionMethod.LZW;
        using var ms = new MemoryStream();
        im.Write(ms, MagickFormat.Tiff);
        return ms.Length;
    }

    [BenchmarkCategory("Gate", "tiff-encode")]
    [Benchmark(Description = "NetVips")]
    public long NetVips()
    {
        using var im = global::NetVips.Image.NewFromMemory(_rgb, 2000, 1500, 3, global::NetVips.Enums.BandFormat.Uchar);
        using var ms = new MemoryStream();
        im.TiffsaveStream(ms, compression: global::NetVips.Enums.ForeignTiffCompression.Lzw);
        return ms.Length;
    }
}
