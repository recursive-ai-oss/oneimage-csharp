using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Xunit;

namespace OneImage.Tests.Region;

/// <summary>
/// Pixels read after <c>ReleaseDecodedPixels</c> (on the image, on a frame, through a frame's single-frame image) and with the
/// per-call plane bypass must be the same bytes as reads that never released anything. The reference file holds one hash per
/// (input, streaming mode, destination format, read pattern) captured from version 1.8.1; set <c>ONEIMAGE_CAPTURE_RELEASE</c> to a
/// path to write a new reference file instead of comparing.
/// </summary>
[Collection(MemoryMeasurement.Name)]
public sealed class ReleaseIdentityTests : IDisposable
{
    private static readonly Lazy<Dictionary<string, string>> s_reference = new(LoadReference);
    private static readonly string[] s_formats = ["Gray8", "Rgb24", "Rgba32"];
    private static readonly string[] s_patterns = ["whole-release-whole", "bands-release-bands", "random-release-random", "frame-release", "toimage-release", "toimage-bands", "mixed-release"];
    private readonly long _savedThreshold = DecodeSourceNode.StreamingMinBytes;

    public void Dispose() => DecodeSourceNode.StreamingMinBytes = _savedThreshold;

    private static Dictionary<string, string> LoadReference()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "testdata", "region", "release-reference.txt");
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

    private static IEnumerable<RegionInputs.Input> SmallInputs()
    {
        foreach (RegionInputs.Input i in RegionInputs.Small())
        {
            yield return i;
        }

        const int W = 301;
        const int H = 217;
        Image rgb = Image.FromPixels<Rgb24>(MemoryMarshal.Cast<byte, Rgb24>(RegionInputs.Pixels(W, H, 3)), W, H);
        Image gray = Image.FromPixels<Gray8>(MemoryMarshal.Cast<byte, Gray8>(RegionInputs.Pixels(W, H, 1)), W, H);
        yield return Encode("tiff-rgb", rgb, TiffFormat.Instance, new TiffEncoderOptions());
        yield return Encode("tiff-gray-deflate", gray, TiffFormat.Instance, new TiffEncoderOptions { Compression = TiffCompression.Deflate });
    }

    private static RegionInputs.Input Encode(string name, Image image, IImageFormat format, ImageEncoderOptions options)
    {
        using var ms = new MemoryStream();
        image.Save(ms, format, new SaveOptions { EncoderOptions = options });
        return new RegionInputs.Input(name, ms.ToArray(), 50);
    }

    [Fact]
    public void SmallImagesMatchReferenceMaterializedAndStreaming()
    {
        var lines = new List<string>();
        var failures = new List<string>();
        foreach (RegionInputs.Input input in SmallInputs())
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
        foreach (string format in s_formats)
        {
            foreach (string pattern in s_patterns)
            {
                string key = $"{input.Name}|{mode}|{format}|{pattern}";
                string hash = Run(input, format, pattern);
                lines.Add($"{key}\t{hash}");
                if (Environment.GetEnvironmentVariable("ONEIMAGE_CAPTURE_RELEASE") == null
                    && (!s_reference.Value.TryGetValue(key, out string? expected) || expected != hash))
                {
                    failures.Add(key);
                }
            }
        }
    }

    private static void Finish(List<string> lines, List<string> failures, string part)
    {
        string? capture = Environment.GetEnvironmentVariable("ONEIMAGE_CAPTURE_RELEASE");
        if (capture != null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(capture)!);
            File.WriteAllLines(Path.Combine(Path.GetDirectoryName(capture)!, Path.GetFileNameWithoutExtension(capture) + "." + part + ".txt"), lines);
            return;
        }

        Assert.True(failures.Count == 0, $"{failures.Count} of {lines.Count} differ from the 1.8.1 reference, first: {string.Join("; ", failures.Take(8))}");
        Assert.True(lines.Count > 20);
    }

    private static string Run(RegionInputs.Input input, string format, string pattern)
    {
        Image image = Image.Load(input.Bytes);
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        int w = image.Width;
        int h = image.Height;
        var whole = new ImageRect(0, 0, w, h);
        IEnumerable<ImageRect> bands = RegionPatterns.Bands(w, h, input.BandHeight);
        IEnumerable<ImageRect> randoms = RegionPatterns.Randoms(w, h, 8, 31);
        switch (pattern)
        {
            case "whole-release-whole":
                Read(image, format, whole, hash);
                image.ReleaseDecodedPixels();
                Read(image, format, whole, hash);
                ReadAll(bands, image, format, hash);
                image.ReleaseDecodedPixels();
                ReadAll(bands, image, format, hash);
                break;
            case "bands-release-bands":
                ReadAll(bands, image, format, hash);
                ReadAll(bands, image, format, hash);
                image.ReleaseDecodedPixels();
                ReadAll(bands, image, format, hash);
                break;
            case "random-release-random":
                ReadAll(randoms, image, format, hash);
                ReadAll(randoms, image, format, hash);
                image.ReleaseDecodedPixels();
                ReadAll(randoms, image, format, hash);
                Read(image, format, whole, hash);
                break;
            case "frame-release":
                ReadAll(bands, image, format, hash);
                image.Frames[0].ReleaseDecodedPixels();
                ReadAll(randoms, image, format, hash);
                image.Frames[0].ReleaseDecodedPixels();
                Read(image, format, whole, hash);
                break;
            case "toimage-release":
                Image single = image.Frames[0].ToImage();
                ReadAll(bands, single, format, hash);
                ReadAll(bands, single, format, hash);
                single.ReleaseDecodedPixels();
                ReadAll(bands, single, format, hash);
                Read(image, format, whole, hash);
                break;
            case "toimage-bands":
                Image other = image.Frames[0].ToImage();
                ReadAll(randoms, image, format, hash);
                ReadAll(bands, other, format, hash);
                ReadAll(bands, other, format, hash);
                break;
            case "mixed-release":
                string[] formats = ["Gray8", "Rgb24", "Rgba32"];
                int i = 0;
                foreach (ImageRect r in randoms.Concat(bands))
                {
                    Read(image, formats[i++ % 3], r, hash);
                }

                image.ReleaseDecodedPixels();
                foreach (ImageRect r in randoms.Concat(bands))
                {
                    Read(image, formats[i++ % 3], r, hash);
                }

                Read(image, format, whole, hash);
                break;
            default:
                throw new ArgumentException(pattern);
        }

        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static void ReadAll(IEnumerable<ImageRect> rects, Image image, string format, IncrementalHash hash)
    {
        foreach (ImageRect r in rects)
        {
            Read(image, format, r, hash);
        }
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
            default:
                Read<Rgba32>(image, r, hash);
                break;
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
