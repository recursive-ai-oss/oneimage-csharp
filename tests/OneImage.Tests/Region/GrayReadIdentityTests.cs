using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Xunit;

namespace OneImage.Tests.Region;

/// <summary>
/// Reading RGB and RGBA images as gray (8-bit, 16-bit, gray+alpha) must return exactly the bytes the float conversion path produced
/// before RGB-to-gray got an integer path. The reference file holds one hash per (input, streaming mode, destination format, read
/// pattern) captured from version 1.8.0; set <c>ONEIMAGE_CAPTURE_GRAY</c> to a path to write a new reference file instead of comparing.
/// </summary>
[Collection(MemoryMeasurement.Name)]
public sealed class GrayReadIdentityTests : IDisposable
{
    private static readonly Lazy<Dictionary<string, string>> s_reference = new(LoadReference);
    private static readonly string[] s_formats = ["Gray8", "GrayAlpha8", "Gray16", "Rgb24"];
    private static readonly string[] s_patterns = ["whole", "toarray", "bands", "bands-reversed", "random", "edges", "mixed"];
    private readonly long _savedThreshold = DecodeSourceNode.StreamingMinBytes;

    public void Dispose() => DecodeSourceNode.StreamingMinBytes = _savedThreshold;

    private static Dictionary<string, string> LoadReference()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "testdata", "gray", "reference.txt");
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
        foreach (RegionInputs.Input input in Inputs())
        {
            foreach ((string mode, long threshold) in new[] { ("mat", _savedThreshold), ("stream", 0L) })
            {
                DecodeSourceNode.StreamingMinBytes = threshold;
                foreach (string format in s_formats)
                {
                    foreach (string pattern in s_patterns)
                    {
                        Check($"{input.Name}|{mode}|{format}|{pattern}", () => Run(input, format, pattern, null), lines, failures);
                    }
                }
            }
        }

        Finish(lines, failures, "small");
    }

    [Fact]
    public void ToneOpsOnSmallImagesMatchReference()
    {
        var lines = new List<string>();
        var failures = new List<string>();
        foreach (RegionInputs.Input input in Inputs().Where(i => i.Name is "jpeg-base-420" or "png-rgba" or "png-rgb-adam7" or "webp-lossless-rgba" or "tiff-rgb"))
        {
            foreach (string op in new[] { "gray-linear", "gray-encoded", "gray-linear-format", "gray-encoded-format", "gray-gamma", "gray-invert-format", "remove-alpha" })
            {
                foreach (string format in s_formats)
                {
                    foreach (string pattern in new[] { "whole", "bands", "random" })
                    {
                        Check($"{input.Name}|{op}|{format}|{pattern}", () => Run(input, format, pattern, op), lines, failures);
                    }
                }
            }
        }

        Finish(lines, failures, "tone");
    }

    [Fact]
    public void PageSizedImagesMatchReference()
    {
        var lines = new List<string>();
        var failures = new List<string>();
        DecodeSourceNode.StreamingMinBytes = _savedThreshold;
        foreach (RegionInputs.Input input in RegionInputs.Pages())
        {
            foreach (string format in new[] { "Gray8", "GrayAlpha8", "Gray16" })
            {
                foreach (string pattern in new[] { "whole", "toarray", "bands", "random" })
                {
                    Check($"{input.Name}|default|{format}|{pattern}", () => Run(input, format, pattern, null), lines, failures);
                }
            }
        }

        Finish(lines, failures, "page");
    }

    private static void Check(string key, Func<string> compute, List<string> lines, List<string> failures)
    {
        string hash = compute();
        lines.Add($"{key}\t{hash}");
        if (Environment.GetEnvironmentVariable("ONEIMAGE_CAPTURE_GRAY") == null
            && (!s_reference.Value.TryGetValue(key, out string? expected) || expected != hash))
        {
            failures.Add(key);
        }
    }

    private static void Finish(List<string> lines, List<string> failures, string part)
    {
        string? capture = Environment.GetEnvironmentVariable("ONEIMAGE_CAPTURE_GRAY");
        if (capture != null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(capture)!);
            File.WriteAllLines(Path.Combine(Path.GetDirectoryName(capture)!, Path.GetFileNameWithoutExtension(capture) + "." + part + ".txt"), lines);
            return;
        }

        Assert.True(failures.Count == 0, $"{failures.Count} of {lines.Count} differ from the 1.8.0 reference, first: {string.Join("; ", failures.Take(8))}");
        Assert.True(lines.Count > 40);
    }

    private static Image Apply(Image image, string? op) => op switch
    {
        null => image,
        "gray-linear" => image.Grayscale(linear: true),
        "gray-encoded" => image.Grayscale(linear: false),
        "gray-linear-format" => image.Grayscale(linear: true, toGrayFormat: true),
        "gray-encoded-format" => image.Grayscale(linear: false, toGrayFormat: true),
        "gray-gamma" => image.Grayscale(linear: false, toGrayFormat: true).Gamma(1.8),
        "gray-invert-format" => image.Invert().Grayscale(linear: false, toGrayFormat: true),
        "remove-alpha" => image.RemoveAlpha(),
        _ => throw new ArgumentException(op),
    };

    private static string Run(RegionInputs.Input input, string format, string pattern, string? op)
    {
        Image image = Apply(Image.Load(input.Bytes), op);
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        int w = image.Width;
        int h = image.Height;
        var whole = new ImageRect(0, 0, w, h);
        switch (pattern)
        {
            case "whole":
                Read(image, format, whole, hash);
                break;
            case "toarray":
                ReadAll(image, format, hash);
                break;
            case "bands":
                foreach (ImageRect r in RegionPatterns.Bands(w, h, input.BandHeight))
                {
                    Read(image, format, r, hash);
                }

                break;
            case "bands-reversed":
                foreach (ImageRect r in RegionPatterns.Bands(w, h, input.BandHeight).Reverse())
                {
                    Read(image, format, r, hash);
                }

                break;
            case "random":
                foreach (ImageRect r in RegionPatterns.Randoms(w, h, 14, 99))
                {
                    Read(image, format, r, hash);
                }

                break;
            case "edges":
                foreach (ImageRect r in RegionPatterns.Edges(w, h))
                {
                    Read(image, format, r, hash);
                }

                break;
            case "mixed":
                string[] formats = ["Gray8", "Rgb24", "GrayAlpha8", "Gray16"];
                int i = 0;
                foreach (ImageRect r in RegionPatterns.Randoms(w, h, 5, 7).Concat(RegionPatterns.Bands(w, h, input.BandHeight)))
                {
                    Read(image, formats[i++ % 4], r, hash);
                }

                Read(image, format, whole, hash);
                break;
            default:
                throw new ArgumentException(pattern);
        }

        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static void Read(Image image, string format, ImageRect r, IncrementalHash hash)
    {
        switch (format)
        {
            case "Gray8":
                Read<Gray8>(image, r, hash);
                break;
            case "GrayAlpha8":
                Read<GrayAlpha8>(image, r, hash);
                break;
            case "Gray16":
                Read<Gray16>(image, r, hash);
                break;
            case "Rgb24":
                Read<Rgb24>(image, r, hash);
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

    private static void ReadAll(Image image, string format, IncrementalHash hash)
    {
        switch (format)
        {
            case "Gray8":
                hash.AppendData(MemoryMarshal.AsBytes(image.ToArray<Gray8>().AsSpan()));
                break;
            case "GrayAlpha8":
                hash.AppendData(MemoryMarshal.AsBytes(image.ToArray<GrayAlpha8>().AsSpan()));
                break;
            case "Gray16":
                hash.AppendData(MemoryMarshal.AsBytes(image.ToArray<Gray16>().AsSpan()));
                break;
            default:
                hash.AppendData(MemoryMarshal.AsBytes(image.ToArray<Rgb24>().AsSpan()));
                break;
        }
    }

    /// <summary>The small JPEG and PNG inputs of the region tests plus indexed PNG, WebP, TIFF and GIF encodes of the same page-like pixels.</summary>
    private static IEnumerable<RegionInputs.Input> Inputs()
    {
        foreach (RegionInputs.Input i in RegionInputs.Small().Where(i => !i.Name.Contains("gray", StringComparison.Ordinal)))
        {
            yield return i;
        }

        const int W = 301;
        const int H = 217;
        Image rgb = Image.FromPixels<Rgb24>(MemoryMarshal.Cast<byte, Rgb24>(RegionInputs.Pixels(W, H, 3)), W, H);
        Image rgba = Image.FromPixels<Rgba32>(MemoryMarshal.Cast<byte, Rgba32>(RegionInputs.Pixels(W, H, 4)), W, H);
        yield return Encode("png-indexed", rgb, PngFormat.Instance, new PngEncoderOptions { ColorMode = PngColorMode.Indexed });
        yield return Encode("png-indexed-rgba", rgba, PngFormat.Instance, new PngEncoderOptions { ColorMode = PngColorMode.Indexed });
        yield return Encode("webp-lossy-rgb", rgb, WebPFormat.Instance, new WebPEncoderOptions { Quality = 70 });
        yield return Encode("webp-lossy-rgba", rgba, WebPFormat.Instance, new WebPEncoderOptions { Quality = 70 });
        yield return Encode("webp-lossless-rgb", rgb, WebPFormat.Instance, new WebPEncoderOptions { Lossless = true });
        yield return Encode("webp-lossless-rgba", rgba, WebPFormat.Instance, new WebPEncoderOptions { Lossless = true });
        yield return Encode("tiff-rgb", rgb, TiffFormat.Instance, new TiffEncoderOptions());
        yield return Encode("tiff-rgba", rgba, TiffFormat.Instance, new TiffEncoderOptions { Compression = TiffCompression.Deflate });
        yield return Encode("gif", rgb, GifFormat.Instance, new GifEncoderOptions());
    }

    private static RegionInputs.Input Encode(string name, Image image, IImageFormat format, ImageEncoderOptions options)
    {
        using var ms = new MemoryStream();
        image.Save(ms, format, new SaveOptions { EncoderOptions = options });
        return new RegionInputs.Input(name, ms.ToArray(), 50);
    }
}
