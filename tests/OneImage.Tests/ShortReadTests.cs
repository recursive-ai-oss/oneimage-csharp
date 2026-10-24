using Microsoft.Win32.SafeHandles;

namespace OneImage.Tests;

/// <summary>
/// Files must decode to the same pixels however the operating system or the caller's stream slices its reads. Every case
/// decodes through readers that return short, seeded counts and compares with a normal decode of the same bytes.
/// Set <c>ONEIMAGE_SHORTREAD_ROUNDS</c> for a longer sweep over more seeds.
/// </summary>
[Collection(MemoryMeasurement.Name)]
public sealed class ShortReadTests : IDisposable
{
    private readonly List<string> _files = [];

    public void Dispose()
    {
        HandleStream.TestReadAt = null;
        foreach (string f in _files)
        {
            File.Delete(f);
        }
    }

    public static IEnumerable<object[]> PngCases()
    {
        // Below and above the 1 MiB pipelined-inflate size; few large and many small IDAT chunks.
        yield return ["gray8-small", 300, 200, "gray8", 1 << 20, false];
        yield return ["rgb8-pipelined-many-idat", 900, 700, "rgb8", 1500, false];
        yield return ["rgb8-pipelined-big-idat", 900, 700, "rgb8", 1 << 22, false];
        yield return ["rgba8-pipelined", 800, 600, "rgba8", 20000, false];
        yield return ["rgba16-pipelined", 700, 400, "rgba16", 9000, false];
        yield return ["rgba8-interlaced", 700, 500, "rgba8", 4000, true];
    }

    [Theory]
    [MemberData(nameof(PngCases))]
    public void PngDecodesIdenticallyThroughShortReads(string name, int w, int h, string layout, int idat, bool interlaced)
    {
        byte[] file = MakePng(w, h, layout, idat, interlaced);
        Rgba64[] expected = ShortReadFixtures.All(Image.Load(file));
        Assert.True(expected.Length == w * h, name);
        Sweep(file, expected);
    }

    [Fact]
    public void LargeStreamingPngDecodesIdenticallyThroughShortReads()
    {
        // 4200 x 1000 x 4 bytes is over the 16 MiB size that streams through a sliding window of tile rows.
        byte[] file = MakePng(4200, 1000, "rgba8", 60000, false);
        Rgba64[] expected = ShortReadFixtures.All(Image.Load(file));
        Sweep(file, expected);
    }

    public static IEnumerable<object[]> OtherFormats()
    {
        yield return ["jpeg"];
        yield return ["jpeg-progressive"];
        yield return ["tiff"];
        yield return ["gif"];
        yield return ["webp-lossy"];
        yield return ["webp-lossless"];
        yield return ["bmp"];
        yield return ["ico"];
    }

    [Theory]
    [MemberData(nameof(OtherFormats))]
    public void OtherCodecsDecodeIdenticallyThroughShortReads(string kind)
    {
        const int W = 700;
        const int H = 500;
        Image src = OneImage.Tests.Png.PngTestUtil.FromRaw(OneImage.Tests.Png.PngGen.Photo(W, H, PixelFormat.Rgba8, 3), W, H, PixelFormat.Rgba8);
        using var ms = new MemoryStream();
        IImageFormat format = kind switch
        {
            "jpeg" or "jpeg-progressive" => JpegFormat.Instance,
            "tiff" => TiffFormat.Instance,
            "gif" => GifFormat.Instance,
            "webp-lossy" or "webp-lossless" => WebPFormat.Instance,
            "bmp" => BmpFormat.Instance,
            _ => IcoFormat.Instance,
        };
        if (kind == "ico")
        {
            src = src.Resize(width: 128, height: 128);
        }

        ImageEncoderOptions? options = kind switch
        {
            "jpeg-progressive" => new JpegEncoderOptions { Progressive = true },
            "webp-lossless" => new WebPEncoderOptions { Lossless = true },
            _ => null,
        };
        src.Save(ms, format, new SaveOptions { EncoderOptions = options });
        byte[] file = ms.ToArray();
        Rgba64[] expected = ShortReadFixtures.All(Image.Load(file));
        Sweep(file, expected);
    }

    private static byte[] MakePng(int w, int h, string layout, int idat, bool interlaced) => interlaced
        ? ShortReadFixtures.InterlacedPng(w, h, idat)
        : ShortReadFixtures.Png(w, h, layout switch
        {
            "gray8" => PixelFormat.Gray8,
            "rgb8" => PixelFormat.Rgb8,
            "rgba8" => PixelFormat.Rgba8,
            _ => PixelFormat.Rgba16,
        }, idat);

    private void Sweep(byte[] file, Rgba64[] expected)
    {
        Image probe = Image.Load(file);
        int w = probe.Width;
        int h = probe.Height;
        int rounds = int.TryParse(Environment.GetEnvironmentVariable("ONEIMAGE_SHORTREAD_ROUNDS"), out int r) ? r : 2;
        string path = Path.Combine(Path.GetTempPath(), $"oneimage-shortread-{Guid.NewGuid():N}.png");
        File.WriteAllBytes(path, file);
        _files.Add(path);
        ImageRect band = new(0, h / 3, w, Math.Min(h - (h / 3), 150));
        Gray8[] expectedBand = new Gray8[band.Width * band.Height];
        Image.Load(file).CopyPixelsTo<Gray8>(expectedBand, band);

        int shorts = 0;
        bool slow = Environment.GetEnvironmentVariable("ONEIMAGE_SHORTREAD_SLOW") == "1";
        for (int round = 0; round < rounds; round++)
        {
            int seed = 1000 + round;
            int max = round % 2 == 0 ? 3000 : 200_000;

            // A stream that hands out short reads.
            var s = new ShortReadStream(file, seed, max);
            Assert.True(expected.AsSpan().SequenceEqual(ShortReadFixtures.All(Image.Load(s))), $"stream whole seed {seed}");

            // A file whose positional reads come back short.
            var gate = new object();
            var rnd = new Random(seed);
            HandleStream.TestReadAt = (SafeFileHandle handle, Span<byte> buffer, long offset) =>
            {
                int n;
                int nap;
                lock (gate)
                {
                    n = Math.Min(buffer.Length, 1 + rnd.Next(max));
                    nap = slow && rnd.Next(8) == 0 ? rnd.Next(3) : -1;
                }

                if (nap >= 0)
                {
                    // A loaded disk: the reader on a pool thread falls behind the thread that unfilters.
                    Thread.Sleep(nap);
                }

                if (n < buffer.Length)
                {
                    Interlocked.Increment(ref shorts);
                }

                return System.IO.RandomAccess.Read(handle, buffer[..n], offset);
            };

            Assert.True(expected.AsSpan().SequenceEqual(ShortReadFixtures.All(Image.Load(path))), $"path whole seed {seed}");

            foreach (bool cache in new[] { true, false })
            {
                Image img = Image.Load(path);
                for (int pass = 0; pass < 2; pass++)
                {
                    Gray8[] got = new Gray8[expectedBand.Length];
                    img.CopyPixelsTo<Gray8>(got, band, cache);
                    Assert.True(expectedBand.AsSpan().SequenceEqual(got), $"band cache={cache} pass {pass} seed {seed}");
                    img.ReleaseDecodedPixels();
                }

                Assert.True(expected.AsSpan().SequenceEqual(ShortReadFixtures.All(img)), $"whole after release cache={cache} seed {seed}");
            }

            HandleStream.TestReadAt = null;
        }

        Assert.True(shorts > 0 || file.Length < 100_000, "the hook made no read short");
    }
}
