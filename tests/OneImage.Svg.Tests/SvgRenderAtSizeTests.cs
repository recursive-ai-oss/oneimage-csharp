using System.Text;
using OneImage.Svg.Dom;
using OneImage.Svg.Render;
using OneImage.Svg.Xml;
using Xunit;

namespace OneImage.Svg.Tests;

/// <summary>Render at the final size, region rendering and the limits on instantiation.</summary>
[Collection(SvgSerial.Name)]
public class SvgRenderAtSizeTests
{
    /// <summary>Wraps the SVG decoder and records every request the core sends it.</summary>
    private sealed class RecordingFormat : IImageFormat, IImageDecoder
    {
        public List<DecodeRequest> Requests { get; } = [];

        public string Name => "svg";

        public IReadOnlyList<string> Extensions => SvgFormat.Instance.Extensions;

        public string MimeType => SvgFormat.Instance.MimeType;

        public int HeaderLength => SvgFormat.Instance.HeaderLength;

        public IImageDecoder? Decoder => this;

        public IImageEncoder? Encoder => null;

        public DecoderCapabilities Capabilities => SvgFormat.Instance.Decoder!.Capabilities;

        public bool Matches(ReadOnlySpan<byte> header) => SvgFormat.Instance.Matches(header);

        public ImageInfo ReadInfo(Stream source, ImageLimits limits)
        {
            ImageInfo inner = SvgFormat.Instance.Decoder!.ReadInfo(source, limits);
            return new ImageInfo(this, inner.Frames, inner.LoopCount, inner.Metadata, inner.DecoderState);
        }

        public void DecodeFrame(Stream source, ImageInfo info, int frameIndex, in DecodeRequest request, IRowSink sink, ImageLimits limits, CancellationToken cancellationToken)
        {
            lock (Requests)
            {
                Requests.Add(request);
            }

            SvgFormat.Instance.Decoder!.DecodeFrame(source, info, frameIndex, request, sink, limits, cancellationToken);
        }
    }

    private const string Ns = SvgRenderTests.Ns;

    // Thin strokes and curves: a raster downscale of a large render differs visibly from a native small render.
    private static string Drawing(string size) =>
        $"<svg {Ns} viewBox='0 0 100 100' {size}>" +
        "<rect x='5' y='5' width='90' height='90' fill='#2040c0'/>" +
        "<circle cx='50' cy='50' r='30' fill='none' stroke='white' stroke-width='0.7'/>" +
        "<path d='M10 90 C 30 10, 70 10, 90 90' fill='none' stroke='#ffcc00' stroke-width='0.9'/>" +
        "<path d='M 20 20 L 80 80 M 80 20 L 20 80' stroke='black' stroke-width='0.5'/></svg>";

    private static (int Max, int Differing) Compare(Rgba32[] a, Rgba32[] b)
    {
        Assert.Equal(a.Length, b.Length);
        int max = 0;
        int differing = 0;
        for (int i = 0; i < a.Length; i++)
        {
            int d = Math.Max(Math.Max(Math.Abs(a[i].R - b[i].R), Math.Abs(a[i].G - b[i].G)), Math.Max(Math.Abs(a[i].B - b[i].B), Math.Abs(a[i].A - b[i].A)));
            max = Math.Max(max, d);
            differing += d > 0 ? 1 : 0;
        }

        return (max, differing);
    }

    [Fact]
    public void ResizeRendersAtTheTargetSizeInsteadOfScalingARaster()
    {
        byte[] data = Encoding.UTF8.GetBytes(Drawing("width='256' height='256'"));
        var recorder = new RecordingFormat();
        var options = new LoadOptions { Configuration = new ImageConfiguration().Add(recorder) };

        Image resized = Image.Load(data, options).Resize(width: 64);
        Assert.Equal((64, 64), (resized.Width, resized.Height));
        Rgba32[] viaResize = resized.ToArray<Rgba32>();
        lock (recorder.Requests)
        {
            Assert.NotEmpty(recorder.Requests);
            Assert.All(recorder.Requests, r =>
            {
                Assert.Equal(64, r.TargetWidth);
                Assert.Equal(64, r.TargetHeight);
                Assert.True(r.Region.Right <= 64 && r.Region.Bottom <= 64);
            });
        }

        // The same document drawn directly at 64 px: the same pixels.
        Rgba32[] direct = Image.Load(Encoding.UTF8.GetBytes(Drawing("width='64' height='64'")), SvgRenderTests.Options).ToArray<Rgba32>();
        (int max, _) = Compare(viaResize, direct);
        Assert.True(max <= 1, $"max difference {max}");

        // And not the same as shrinking a 256 px raster.
        Image large = Image.Load(data, SvgRenderTests.Options);
        Rgba32[] big = large.ToArray<Rgba32>();
        Image shrunk = Image.FromPixels(big, 256, 256).Resize(width: 64);
        (int rasterMax, int rasterDiffering) = Compare(shrunk.ToArray<Rgba32>(), direct);
        Assert.True(rasterMax > 8 && rasterDiffering > 100, $"raster downscale differs by {rasterMax} at {rasterDiffering} pixels");
    }

    [Fact]
    public void ThumbnailRendersAtTheTargetSize()
    {
        byte[] data = Encoding.UTF8.GetBytes(Drawing("width='256' height='256'"));
        var recorder = new RecordingFormat();
        var options = new LoadOptions { Configuration = new ImageConfiguration().Add(recorder) };
        Image thumb = Image.Thumbnail(data, 64, loadOptions: options);
        Rgba32[] px = thumb.ToArray<Rgba32>();
        Assert.Equal((64, 64), (thumb.Width, thumb.Height));
        lock (recorder.Requests)
        {
            Assert.All(recorder.Requests, r => Assert.Equal((64, 64), (r.TargetWidth, r.TargetHeight)));
        }

        Rgba32[] direct = Image.Load(Encoding.UTF8.GetBytes(Drawing("width='64' height='64'")), SvgRenderTests.Options).ToArray<Rgba32>();
        (int max, _) = Compare(px, direct);
        Assert.True(max <= 1, $"max difference {max}");
    }

    [Fact]
    public void CoverAndUpscaleRenderSharpAndPushTheCropDown()
    {
        byte[] data = Encoding.UTF8.GetBytes(Drawing("width='100' height='100'"));
        var recorder = new RecordingFormat();
        var options = new LoadOptions { Configuration = new ImageConfiguration().Add(recorder) };

        // Cover 300x100 of a square: renders 300x300 and crops the middle band as a region of the decode.
        Image cover = Image.Load(data, options).Resize(300, 100, Fit.Cover);
        Rgba32[] px = cover.ToArray<Rgba32>();
        Assert.Equal((300, 100), (cover.Width, cover.Height));
        lock (recorder.Requests)
        {
            Assert.All(recorder.Requests, r => Assert.Equal((300, 300), (r.TargetWidth, r.TargetHeight)));
            Assert.All(recorder.Requests, r => Assert.True(r.Region.Y >= 100 && r.Region.Bottom <= 200, $"region {r.Region}"));
        }

        Rgba32[] full = Image.Load(Encoding.UTF8.GetBytes(Drawing("width='300' height='300'")), SvgRenderTests.Options).ToArray<Rgba32>();
        Rgba32[] middle = new Rgba32[300 * 100];
        Array.Copy(full, 100 * 300, middle, 0, middle.Length);
        Assert.True(Compare(px, middle).Max <= 1);
    }

    [Fact]
    public void ARegionEqualsTheSamePixelsOfTheWholeRenderAndDrawsOnlyWhatItTouches()
    {
        // 40 x 40 small squares of varying colors, on a 4000 x 4000 document rendered at 4096.
        var sb = new StringBuilder($"<svg {Ns} viewBox='0 0 4000 4000' width='4000' height='4000'>");
        for (int y = 0; y < 40; y++)
        {
            for (int x = 0; x < 40; x++)
            {
                sb.Append($"<circle cx='{(x * 100) + 50}' cy='{(y * 100) + 50}' r='{30 + ((x + y) % 15)}' fill='rgb({(x * 6) % 256},{(y * 6) % 256},{((x * y) + 40) % 256})' stroke='black' stroke-width='3'/>");
            }
        }

        sb.Append("</svg>");
        SvgScene scene = BuildScene(sb.ToString());
        Assert.NotNull(scene.Grid);
        const int Size = 4096;
        var full = new Capture(Size, Size);
        SceneRenderer.Render(scene, Size, Size, new ImageRect(0, 0, Size, Size), full, new SvgLimits(), CancellationToken.None);

        var region = new ImageRect(1000, 2050, 256, 256);
        var part = new Capture(region.Width, region.Height);
        long before = SceneRenderer.PathsDrawn;
        SceneRenderer.Render(scene, Size, Size, region, part, new SvgLimits(), CancellationToken.None);
        long drawn = SceneRenderer.PathsDrawn - before;

        Rgba32[] expected = new Rgba32[region.Width * region.Height];
        for (int y = 0; y < region.Height; y++)
        {
            Array.Copy(full.Pixels, ((region.Y + y) * Size) + region.X, expected, y * region.Width, region.Width);
        }

        (int max, int differing) = Compare(part.Pixels, expected);
        Assert.True(max <= 1, $"region differs by up to {max} at {differing} pixels");

        // 1600 circles, each filled and stroked (3200 draws for everything); a 256 px region at 4096 px touches a few.
        Assert.InRange(drawn, 1, 40);
    }

    [Fact]
    public void RegionsThroughTheImageApiMatchTheFullRender()
    {
        byte[] data = Encoding.UTF8.GetBytes(Drawing("width='1024' height='1024'"));
        Image img = Image.Load(data, SvgRenderTests.Options);
        Rgba32[] all = img.ToArray<Rgba32>();
        var rect = new ImageRect(300, 200, 130, 129);
        Rgba32[] part = new Rgba32[rect.Width * rect.Height];
        img.CopyPixelsTo<Rgba32>(part, rect);
        Rgba32[] expected = new Rgba32[part.Length];
        for (int y = 0; y < rect.Height; y++)
        {
            Array.Copy(all, ((rect.Y + y) * 1024) + rect.X, expected, y * rect.Width, rect.Width);
        }

        Assert.Equal(expected, part);
    }

    [Fact]
    public void UseCyclesDrawNothingAndCostNothing()
    {
        string svg = $"<svg {Ns} viewBox='0 0 10 10' width='10' height='10'>" +
            "<g id='a'><rect width='5' height='5' fill='red'/><use href='#b'/></g>" +
            "<g id='b'><use href='#a'/></g>" +
            "<use id='self' href='#self'/><rect x='5' y='5' width='5' height='5' fill='blue'/></svg>";
        Rgba32[] px = SvgRenderTests.Load(svg).ToArray<Rgba32>();
        Assert.Equal(new Rgba32(255, 0, 0, 255), px[(2 * 10) + 2]);
        Assert.Equal(new Rgba32(0, 0, 255, 255), px[(7 * 10) + 7]);
    }

    [Fact]
    public void ExponentialUseFanOutFailsClosedUnderTheInstanceBudget()
    {
        // Every level uses the previous one twice: 2^28 rectangles if it were expanded.
        var sb = new StringBuilder($"<svg {Ns} width='10' height='10'><g id='l0'><rect width='1' height='1'/></g>");
        for (int i = 1; i <= 28; i++)
        {
            sb.Append($"<g id='l{i}'><use href='#l{i - 1}'/><use href='#l{i - 1}'/></g>");
        }

        sb.Append("<use href='#l28'/></svg>");
        Image img = SvgRenderTests.Load(sb.ToString());
        long bytesBefore = GC.GetTotalAllocatedBytes(true);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        Assert.Throws<ImageLimitException>(() => img.ToArray<Rgba32>());
        clock.Stop();
        long allocated = GC.GetTotalAllocatedBytes(true) - bytesBefore;
        TestContext.Current.SendDiagnosticMessage($"fan-out refused after {clock.ElapsedMilliseconds} ms, {allocated / 1024 / 1024} MiB allocated");
        Assert.True(allocated < 300L * 1024 * 1024, $"{allocated / 1024 / 1024} MiB");
        Assert.True(clock.ElapsedMilliseconds < 5000, $"{clock.ElapsedMilliseconds} ms");

        // The refusal is remembered: pulling again does not redo the work.
        clock.Restart();
        Assert.Throws<ImageLimitException>(() => img.ToArray<Rgba32>());
        Assert.True(clock.ElapsedMilliseconds < 200, $"{clock.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void DeepUseChainsAreRefused()
    {
        var sb = new StringBuilder($"<svg {Ns} width='10' height='10'><g id='l0'><rect width='1' height='1'/></g>");
        for (int i = 1; i <= 60; i++)
        {
            sb.Append($"<g id='l{i}'><use href='#l{i - 1}'/></g>");
        }

        sb.Append("<use href='#l60'/></svg>");
        Assert.Throws<ImageLimitException>(() => SvgRenderTests.Load(sb.ToString()).ToArray<Rgba32>());
    }

    [Fact]
    public void NestedOpacityGroupsDeeperThanTheLimitAreRefused()
    {
        var sb = new StringBuilder($"<svg {Ns} width='10' height='10'>");
        for (int i = 0; i < 40; i++)
        {
            sb.Append("<g opacity='0.9'>");
        }

        sb.Append("<rect width='5' height='5'/>");
        for (int i = 0; i < 40; i++)
        {
            sb.Append("</g>");
        }

        sb.Append("</svg>");
        Assert.Throws<ImageLimitException>(() => SvgRenderTests.Load(sb.ToString()).ToArray<Rgba32>());
    }

    private static SvgScene BuildScene(string svg)
    {
        XmlDocument xml = XmlParser.Parse(Encoding.UTF8.GetBytes(svg), SvgLimits.Default);
        return SceneBuilder.Build(SvgDocument.Build(xml), SvgLimits.Default, CancellationToken.None);
    }

    private sealed class Capture : IRowSink
    {
        public Capture(int width, int height)
        {
            Width = width;
            Height = height;
            Pixels = new Rgba32[width * height];
        }

        public int Width { get; }

        public int Height { get; }

        public PixelFormat Format => PixelFormat.Rgba8;

        public Rgba32[] Pixels { get; }

        public void WriteRows(int y, ReadOnlySpan<byte> rows, int rowCount)
        {
            for (int i = 0; i < rowCount * Width; i++)
            {
                int o = i * 4;
                Pixels[(y * Width) + i] = new Rgba32(rows[o], rows[o + 1], rows[o + 2], rows[o + 3]);
            }
        }
    }
}

/// <summary>Tests that read process-wide counters run one at a time.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SvgSerial
{
    public const string Name = "svg-serial";
}
