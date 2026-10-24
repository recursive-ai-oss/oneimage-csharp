using System.Runtime.InteropServices;
using System.Security.Cryptography;
using OneImage.Tests.Png;
using Xunit;

namespace OneImage.Tests.Region;

/// <summary>
/// Region and band reads of a loaded image must return the same bytes as version 1.7.0, however the image is kept between
/// reads. The reference file holds one hash per (input, streaming mode, destination format, read pattern); set
/// <c>ONEIMAGE_CAPTURE_REGION</c> to a path to write a new reference file instead of comparing.
/// </summary>
[Collection(MemoryMeasurement.Name)]
public sealed class RegionReadIdentityTests : IDisposable
{
    private static readonly Lazy<Dictionary<string, string>> s_reference = new(LoadReference);
    private readonly long _savedThreshold = DecodeSourceNode.StreamingMinBytes;

    public void Dispose() => DecodeSourceNode.StreamingMinBytes = _savedThreshold;

    private static Dictionary<string, string> LoadReference()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "testdata", "region", "reference.txt");
        var map = new Dictionary<string, string>();
        foreach (string line in File.ReadLines(path))
        {
            int tab = line.IndexOf('\t');
            if (tab > 0)
            {
                map[line[..tab]] = line[(tab + 1)..];
            }
        }

        return map;
    }

    [Fact]
    public void SmallImagesMatchReferenceMaterializedAndStreaming()
    {
        var lines = new List<string>();
        var failures = new List<string>();
        foreach (RegionInputs.Input input in RegionInputs.Small())
        {
            foreach ((string mode, long threshold) in new[] { ("mat", _savedThreshold), ("stream", 0L) })
            {
                DecodeSourceNode.StreamingMinBytes = threshold;
                Check(input, mode, lines, failures);
            }
        }

        Finish(lines, failures, "small");
    }

    [Fact]
    public void PageSizedImagesMatchReference()
    {
        var lines = new List<string>();
        var failures = new List<string>();
        DecodeSourceNode.StreamingMinBytes = _savedThreshold;
        foreach (RegionInputs.Input input in RegionInputs.Pages())
        {
            Check(input, "default", lines, failures);
        }

        Finish(lines, failures, "page");
    }

    private static void Check(RegionInputs.Input input, string mode, List<string> lines, List<string> failures)
    {
        foreach (string format in new[] { "Gray8", "Rgb24", "Rgba32" })
        {
            foreach (string pattern in RegionPatterns.Names)
            {
                string key = $"{input.Name}|{mode}|{format}|{pattern}";
                string hash = RegionPatterns.Run(input, format, pattern);
                lines.Add($"{key}\t{hash}");
                if (Environment.GetEnvironmentVariable("ONEIMAGE_CAPTURE_REGION") == null
                    && (!s_reference.Value.TryGetValue(key, out string? expected) || expected != hash))
                {
                    failures.Add(key);
                }
            }
        }
    }

    private static void Finish(List<string> lines, List<string> failures, string part)
    {
        string? capture = Environment.GetEnvironmentVariable("ONEIMAGE_CAPTURE_REGION");
        if (capture != null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(capture)!);
            File.WriteAllLines(Path.Combine(Path.GetDirectoryName(capture)!, Path.GetFileNameWithoutExtension(capture) + "." + part + ".txt"), lines);
            return;
        }

        Assert.True(failures.Count == 0, $"{failures.Count} of {lines.Count} differ from the 1.7.0 reference, first: {string.Join("; ", failures.Take(8))}");
        Assert.True(lines.Count > 100);
    }
}

/// <summary>The read patterns of a document reader, each run on a freshly loaded image unless noted.</summary>
internal static class RegionPatterns
{
    public static readonly string[] Names = ["whole", "bands", "bands-reversed", "bands-twice", "random", "edges", "mixed"];

    public static string Run(RegionInputs.Input input, string format, string pattern)
    {
        Image image = Image.Load(input.Bytes);
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        int w = image.Width;
        int h = image.Height;
        switch (pattern)
        {
            case "whole":
                Read(image, format, new ImageRect(0, 0, w, h), hash);
                Read(image, format, new ImageRect(0, 0, w, h), hash);
                break;
            case "bands":
                foreach (ImageRect r in Bands(w, h, input.BandHeight))
                {
                    Read(image, format, r, hash);
                }

                break;
            case "bands-reversed":
                foreach (ImageRect r in Bands(w, h, input.BandHeight).Reverse())
                {
                    Read(image, format, r, hash);
                }

                break;
            case "bands-twice":
                for (int pass = 0; pass < 2; pass++)
                {
                    foreach (ImageRect r in Bands(w, h, input.BandHeight))
                    {
                        Read(image, format, r, hash);
                    }
                }

                break;
            case "random":
                foreach (ImageRect r in Randoms(w, h, 14, 99))
                {
                    Read(image, format, r, hash);
                }

                break;
            case "edges":
                foreach (ImageRect r in Edges(w, h))
                {
                    Read(image, format, r, hash);
                }

                break;
            case "mixed":
                // One image, several formats and shapes in turn: whatever the image keeps between reads must stay consistent.
                string[] formats = ["Gray8", "Rgb24", "Rgba32"];
                int i = 0;
                foreach (ImageRect r in Randoms(w, h, 5, 7).Concat(Bands(w, h, input.BandHeight)).Concat(Randoms(w, h, 5, 8)))
                {
                    Read(image, formats[i++ % 3], r, hash);
                }

                Read(image, format, new ImageRect(0, 0, w, h), hash);
                foreach (ImageRect r in Randoms(w, h, 5, 9))
                {
                    Read(image, format, r, hash);
                }

                break;
            default:
                throw new ArgumentException(pattern);
        }

        return Convert.ToHexString(hash.GetHashAndReset());
    }

    public static IEnumerable<ImageRect> Bands(int w, int h, int bandHeight)
    {
        for (int y = 0; y < h; y += bandHeight)
        {
            yield return new ImageRect(0, y, w, Math.Min(bandHeight, h - y));
        }
    }

    public static IEnumerable<ImageRect> Randoms(int w, int h, int count, int seed)
    {
        var rnd = new Random(seed);
        for (int i = 0; i < count; i++)
        {
            int rw = rnd.Next(1, Math.Max(2, Math.Min(w, 600)) + 1);
            int rh = rnd.Next(1, Math.Max(2, Math.Min(h, 500)) + 1);
            yield return new ImageRect(rnd.Next(0, w - rw + 1), rnd.Next(0, h - rh + 1), rw, rh);
        }
    }

    public static IEnumerable<ImageRect> Edges(int w, int h)
    {
        yield return new ImageRect(0, 0, 1, 1);
        yield return new ImageRect(w - 1, h - 1, 1, 1);
        yield return new ImageRect(w - 1, 0, 1, h);
        yield return new ImageRect(0, h - 1, w, 1);
        yield return new ImageRect(0, 0, w, 1);
        yield return new ImageRect(Math.Min(127, w - 1), Math.Min(127, h - 1), Math.Min(2, w - Math.Min(127, w - 1)), Math.Min(2, h - Math.Min(127, h - 1)));
        yield return new ImageRect(w / 3, h / 3, (w + 1) / 2, (h + 1) / 2);
    }

    private static void Read(Image image, string format, ImageRect r, IncrementalHash hash)
    {
        switch (format)
        {
            case "Gray8":
                Read<Gray8>(image, r, hash);
                break;
            case "Rgb24":
                Read<Rgb24>(image, r, hash);
                break;
            case "Rgba32":
                Read<Rgba32>(image, r, hash);
                break;
            default:
                throw new ArgumentException(format);
        }
    }

    private static void Read<T>(Image image, ImageRect r, IncrementalHash hash)
        where T : unmanaged, IPixel<T>
    {
        var buffer = new T[r.Width * r.Height];
        image.CopyPixelsTo<T>(buffer, r);
        hash.AppendData(MemoryMarshal.AsBytes(buffer.AsSpan()));
    }
}

/// <summary>Deterministic encoded inputs: JPEG (baseline and progressive, 4:2:0 and 4:4:4, gray, EXIF orientation) and PNG (gray, RGB, RGBA, interlaced).</summary>
internal static class RegionInputs
{
    internal sealed record Input(string Name, byte[] Bytes, int BandHeight);

    public static IEnumerable<Input> Small()
    {
        const int W = 301;
        const int H = 217;
        yield return Jpeg("jpeg-base-420", W, H, progressive: false, JpegSubsampling.Yuv420, 1, 50);
        yield return Jpeg("jpeg-base-444", W, H, progressive: false, JpegSubsampling.Yuv444, 1, 50);
        yield return Jpeg("jpeg-prog-420", W, H, progressive: true, JpegSubsampling.Yuv420, 1, 50);
        yield return Jpeg("jpeg-prog-444", W, H, progressive: true, JpegSubsampling.Yuv444, 1, 50);
        yield return Jpeg("jpeg-base-420-orient6", W, H, progressive: false, JpegSubsampling.Yuv420, 6, 50);
        yield return Jpeg("jpeg-prog-420-orient3", W, H, progressive: true, JpegSubsampling.Yuv420, 3, 50);
        yield return Jpeg("jpeg-base-444-orient8", W, H, progressive: false, JpegSubsampling.Yuv444, 8, 50);
        yield return Jpeg("jpeg-base-420-orient5", 130, 260, progressive: false, JpegSubsampling.Yuv420, 5, 50);
        yield return Png("png-gray", W, H, 1, interlaced: false, 50);
        yield return Png("png-rgb", W, H, 3, interlaced: false, 50);
        yield return Png("png-rgba", W, H, 4, interlaced: false, 50);
        yield return Png("png-gray-adam7", W, H, 1, interlaced: true, 50);
        yield return Png("png-rgb-adam7", W, H, 3, interlaced: true, 50);
        yield return Png("png-rgba-adam7", W, H, 4, interlaced: true, 50);
    }

    /// <summary>Full-size pages: an A4 scan at 300 dpi, and a 12 MP phone frame stored sideways with orientation 6.</summary>
    public static IEnumerable<Input> Pages()
    {
        yield return Jpeg("a4-jpeg-base-420", 2480, 3508, progressive: false, JpegSubsampling.Yuv420, 1, 80);
        yield return Jpeg("a4-jpeg-prog-420", 2480, 3508, progressive: true, JpegSubsampling.Yuv420, 1, 80);
        yield return Png("a4-png-rgb", 2480, 3508, 3, interlaced: false, 80);
        yield return Png("a4-png-rgb-adam7", 2480, 3508, 3, interlaced: true, 80);
        yield return Jpeg("phone-jpeg-base-444-orient6", 4032, 3024, progressive: false, JpegSubsampling.Yuv444, 6, 92);
    }

    // Smooth page-like content with text-like blocks and noise; alpha ramps for the 4-channel inputs.
    public static byte[] Pixels(int w, int h, int channels)
    {
        var px = new byte[w * h * channels];
        var rnd = new Random(w * 31 + h);
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                bool block = ((x / 11) + (y / 13)) % 4 == 0 && y > h / 8 && y < h * 3 / 4;
                int baseValue = block ? 30 + ((x * 7) + (y * 3)) % 40 : 235 - (x * 20 / w) - (y * 12 / h);
                int noise = rnd.Next(-4, 5);
                int o = ((y * w) + x) * channels;
                if (channels == 1)
                {
                    px[o] = Clamp(baseValue + noise);
                    continue;
                }

                px[o] = Clamp(baseValue + noise + (x * 40 / w));
                px[o + 1] = Clamp(baseValue + noise + (y * 30 / h) - 10);
                px[o + 2] = Clamp(baseValue + noise - 12);
                if (channels == 4)
                {
                    px[o + 3] = (byte)(x < w / 8 ? (x * 255 / Math.Max(1, w / 8)) : 255);
                }
            }
        }

        return px;
    }

    private static byte Clamp(int v) => (byte)Math.Clamp(v, 0, 255);

    private static Input Jpeg(string name, int w, int h, bool progressive, JpegSubsampling subsampling, int orientation, int bandHeight)
    {
        bool gray = name.Contains("gray", StringComparison.Ordinal);
        byte[] px = Pixels(w, h, gray ? 1 : 3);
        Image image = gray
            ? Image.FromPixels<Gray8>(MemoryMarshal.Cast<byte, Gray8>(px), w, h)
            : Image.FromPixels<Rgb24>(MemoryMarshal.Cast<byte, Rgb24>(px), w, h);
        if (orientation != 1)
        {
            // A one-entry EXIF block holding the orientation tag.
            byte[] exif = [0x4D, 0x4D, 0, 0x2A, 0, 0, 0, 8, 0, 1, 0x01, 0x12, 0, 3, 0, 0, 0, 1, 0, (byte)orientation, 0, 0, 0, 0, 0, 0];
            image = image.WithMetadata(new ImageMetadata { Exif = exif, Orientation = orientation });
        }

        using var ms = new MemoryStream();
        image.Save(ms, JpegFormat.Instance, new SaveOptions { EncoderOptions = new JpegEncoderOptions { Quality = 80, Progressive = progressive, Subsampling = subsampling } });
        return new Input(name, ms.ToArray(), bandHeight);
    }

    private static Input Png(string name, int w, int h, int channels, bool interlaced, int bandHeight)
    {
        byte[] px = Pixels(w, h, channels);
        if (!interlaced)
        {
            using var ms = new MemoryStream();
            Image image = channels switch
            {
                1 => Image.FromPixels<Gray8>(MemoryMarshal.Cast<byte, Gray8>(px), w, h),
                3 => Image.FromPixels<Rgb24>(MemoryMarshal.Cast<byte, Rgb24>(px), w, h),
                _ => Image.FromPixels<Rgba32>(MemoryMarshal.Cast<byte, Rgba32>(px), w, h),
            };
            image.Save(ms, PngFormat.Instance);
            return new Input(name, ms.ToArray(), bandHeight);
        }

        return new Input(name, Adam7(px, w, h, channels), bandHeight);
    }

    // PNG 3rd edition section 8.2: seven passes of a regular sub-grid, each scanline preceded by a filter-type byte (0 = none).
    private static byte[] Adam7(byte[] px, int w, int h, int channels)
    {
        int[] x0 = [0, 4, 0, 2, 0, 1, 0];
        int[] y0 = [0, 0, 4, 0, 2, 0, 1];
        int[] dx = [8, 8, 4, 4, 2, 2, 1];
        int[] dy = [8, 8, 8, 4, 4, 2, 2];
        var raw = new List<byte>();
        for (int pass = 0; pass < 7; pass++)
        {
            int pw = (w - x0[pass] + dx[pass] - 1) / dx[pass];
            int ph = (h - y0[pass] + dy[pass] - 1) / dy[pass];
            if (pw <= 0 || ph <= 0)
            {
                continue;
            }

            for (int row = 0; row < ph; row++)
            {
                raw.Add(0);
                int y = y0[pass] + (row * dy[pass]);
                for (int col = 0; col < pw; col++)
                {
                    int x = x0[pass] + (col * dx[pass]);
                    for (int c = 0; c < channels; c++)
                    {
                        raw.Add(px[(((y * w) + x) * channels) + c]);
                    }
                }
            }
        }

        int colorType = channels switch { 1 => 0, 3 => 2, _ => 6 };
        return MiniPng.Concat(
            MiniPng.Signature,
            MiniPng.Chunk("IHDR", MiniPng.Ihdr(w, h, 8, colorType, interlace: 1)),
            MiniPng.Chunk("IDAT", MiniPng.Zlib(raw.ToArray())),
            MiniPng.Chunk("IEND", []));
    }
}
