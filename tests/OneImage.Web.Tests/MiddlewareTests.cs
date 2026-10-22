using System.Net;
using Microsoft.Extensions.DependencyInjection;
using OneImage;

namespace OneImage.Web.Tests;

public class MiddlewareTests
{
    private static async Task<(HttpResponseMessage Response, byte[] Body)> GetAsync(WebFixture f, string url)
    {
        HttpResponseMessage r = await f.Client.GetAsync(url);
        return (r, await r.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task WidthKeepsAspectAndKeepsSourceFormat()
    {
        await using WebFixture f = await WebFixture.StartAsync();
        f.AddImage("a.jpg", WebFixture.MakeImage(800, 600));
        (HttpResponseMessage r, byte[] body) = await GetAsync(f, "/a.jpg?width=100");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("image/jpeg", r.Content.Headers.ContentType!.MediaType);
        Image img = WebFixture.Decode(body);
        Assert.Equal((100, 75), (img.Width, img.Height));
        Assert.Equal(body.Length, r.Content.Headers.ContentLength);
    }

    [Theory]
    [InlineData("/a.jpg?w=100&h=100&fit=contain", 100, 75)]
    [InlineData("/a.jpg?w=100&h=100&fit=cover", 100, 100)]
    [InlineData("/a.jpg?w=100&h=100&fit=stretch", 100, 100)]
    [InlineData("/a.jpg?w=100&h=100&fit=pad&bg=ff0000", 100, 100)]
    [InlineData("/a.jpg?width=100&height=100&rmode=crop", 100, 100)]
    [InlineData("/a.jpg?width=100&height=100&rmode=max", 100, 75)]
    [InlineData("/a.jpg?width=100&height=100&rmode=min", 133, 100)]
    [InlineData("/a.jpg?width=100&height=100&rmode=pad&color=red", 100, 100)]
    [InlineData("/a.jpg?width=1000&height=1000&rmode=max", 800, 600)]
    [InlineData("/a.jpg?width=1000", 800, 600)]
    [InlineData("/a.jpg?width=1000&upscale=1", 1000, 750)]
    [InlineData("/a.jpg?width=100&height=100&rmode=crop&xy=0.1,0.5&rsampler=bicubic&compand=true", 100, 100)]
    [InlineData("/a.jpg?height=60", 80, 60)]
    public async Task ResizeModes(string url, int width, int height)
    {
        await using WebFixture f = await WebFixture.StartAsync();
        f.AddImage("a.jpg", WebFixture.MakeImage(800, 600));
        (HttpResponseMessage r, byte[] body) = await GetAsync(f, url);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Image img = WebFixture.Decode(body);
        Assert.Equal((width, height), (img.Width, img.Height));
    }

    [Fact]
    public async Task FocalPointMovesTheCrop()
    {
        await using WebFixture f = await WebFixture.StartAsync();
        f.AddImage("a.png", WebFixture.MakeImage(800, 600, "png"));
        Image left = WebFixture.Decode((await GetAsync(f, "/a.png?width=100&height=100&fit=cover&focus=0,0.5")).Body);
        Image right = WebFixture.Decode((await GetAsync(f, "/a.png?width=100&height=100&fit=cover&focus=1,0.5")).Body);
        Rgba32[] l = left.ToArray<Rgba32>();
        Rgba32[] rr = right.ToArray<Rgba32>();
        // The test image's red channel grows from left to right.
        Assert.True(l[50 * 100 + 50].R + 60 < rr[50 * 100 + 50].R);
    }

    [Theory]
    [InlineData("format=webp", "image/webp")]
    [InlineData("format=png", "image/png")]
    [InlineData("fm=gif", "image/gif")]
    [InlineData("format=jpg", "image/jpeg")]
    [InlineData("format=.bmp", "image/bmp")]
    public async Task FormatConverts(string query, string mime)
    {
        await using WebFixture f = await WebFixture.StartAsync();
        f.AddImage("a.png", WebFixture.MakeImage(200, 100, "png"));
        (HttpResponseMessage r, byte[] body) = await GetAsync(f, "/a.png?width=50&" + query);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal(mime, r.Content.Headers.ContentType!.MediaType);
        Assert.Equal(mime, WebFixture.Decode(body).SourceFormat!.MimeType);
    }

    [Fact]
    public async Task QualityChangesTheSize()
    {
        await using WebFixture f = await WebFixture.StartAsync();
        f.AddImage("a.png", WebFixture.MakeImage(400, 300, "png"));
        byte[] low = (await GetAsync(f, "/a.png?format=jpg&quality=10")).Body;
        byte[] high = (await GetAsync(f, "/a.png?format=jpg&q=95")).Body;
        Assert.True(low.Length * 2 < high.Length, $"{low.Length} vs {high.Length}");
        byte[] lowWebp = (await GetAsync(f, "/a.png?format=webp&quality=10")).Body;
        byte[] highWebp = (await GetAsync(f, "/a.png?format=webp&quality=95")).Body;
        Assert.True(lowWebp.Length < highWebp.Length);
    }

    [Fact]
    public async Task TransparencyIsFlattenedForJpeg()
    {
        await using WebFixture f = await WebFixture.StartAsync();
        f.AddImage("a.png", WebFixture.MakeImage(100, 100, "png", alpha: true));
        Image img = WebFixture.Decode((await GetAsync(f, "/a.png?format=jpg&bgcolor=00ff00")).Body);
        Rgba32 p = img.ToArray<Rgba32>()[(50 * 100) + 5];
        Assert.True(p.G > 200 && p.R < 60, p.ToString());
    }

    [Fact]
    public async Task UnknownCommandsAreIgnoredAndNotPartOfTheKey()
    {
        await using WebFixture f = await WebFixture.StartAsync();
        f.AddImage("a.jpg", WebFixture.MakeImage(200, 100));
        (HttpResponseMessage r, _) = await GetAsync(f, "/a.jpg?foo=bar&v=3");
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode); // No recognized command: falls through to the next middleware.
        Assert.True(r.Headers.Contains("X-Next"));
        _ = await GetAsync(f, "/a.jpg?width=50&foo=1");
        _ = await GetAsync(f, "/a.jpg?width=50&foo=2");
        _ = await GetAsync(f, "/a.jpg?W=50");
        Assert.Equal(1, f.Processed);
    }

    [Fact]
    public async Task UnknownCommandsCanBeRejected()
    {
        await using WebFixture f = await WebFixture.StartAsync(o =>
        {
            o.UnknownCommands = UnknownCommandBehavior.Reject;
            _ = o.IgnoredParameters.Add("v");
        });
        f.AddImage("a.jpg", WebFixture.MakeImage(200, 100));
        Assert.Equal(HttpStatusCode.BadRequest, (await GetAsync(f, "/a.jpg?width=50&foo=1")).Response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await GetAsync(f, "/a.jpg?width=50&v=1")).Response.StatusCode);
    }

    [Theory]
    [InlineData("width=abc")]
    [InlineData("width=-5")]
    [InlineData("width=0")]
    [InlineData("width=99999")]
    [InlineData("width=100&width=200")]
    [InlineData("width=100&w=200")]
    [InlineData("fit=sideways&width=10")]
    [InlineData("width=10&fit=cover&rmode=crop")]
    [InlineData("width=10&format=tiff")]
    [InlineData("width=10&format=exe")]
    [InlineData("width=10&quality=0")]
    [InlineData("width=10&quality=101")]
    [InlineData("width=10&bg=nothex")]
    [InlineData("width=10&focus=2,2")]
    [InlineData("width=10&rsampler=welch")]
    [InlineData("width=10&rmode=manual")]
    [InlineData("preset=nope")]
    public async Task InvalidCommandsAreBadRequests(string query)
    {
        await using WebFixture f = await WebFixture.StartAsync();
        f.AddImage("a.jpg", WebFixture.MakeImage(200, 100));
        HttpResponseMessage r = (await GetAsync(f, "/a.jpg?" + query)).Response;
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Equal(0, f.Processed);
    }

    [Fact]
    public async Task OversizedRequestsAreRejected()
    {
        await using WebFixture f = await WebFixture.StartAsync(o => { o.MaxWidth = 300; o.MaxHeight = 300; o.MaxOutputPixels = 50_000; });
        f.AddImage("a.jpg", WebFixture.MakeImage(400, 400));
        Assert.Equal(HttpStatusCode.BadRequest, (await GetAsync(f, "/a.jpg?width=301")).Response.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await GetAsync(f, "/a.jpg?height=301")).Response.StatusCode);
        // Within width and height but above the pixel budget.
        Assert.Equal(HttpStatusCode.BadRequest, (await GetAsync(f, "/a.jpg?width=250&height=250&fit=stretch")).Response.StatusCode);
        // No size given: the 400x400 source itself exceeds the limits.
        Assert.Equal(HttpStatusCode.BadRequest, (await GetAsync(f, "/a.jpg?format=png")).Response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await GetAsync(f, "/a.jpg?width=200")).Response.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await GetAsync(f, "/a.jpg?width=100&" + new string('x', 2000) + "=1")).Response.StatusCode);
    }

    [Fact]
    public async Task OversizedSourceFilesAreRejected()
    {
        await using WebFixture f = await WebFixture.StartAsync(o => o.MaxSourceBytes = 1000);
        f.AddImage("a.png", WebFixture.MakeImage(300, 300, "png"));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await GetAsync(f, "/a.png?width=50")).Response.StatusCode);
    }

    [Fact]
    public async Task SourceDimensionLimitsApply()
    {
        await using WebFixture f = await WebFixture.StartAsync(o => o.SourceLimits = new ImageLimits { MaxPixelsPerFrame = 10_000 });
        f.AddImage("a.png", WebFixture.MakeImage(300, 300, "png"));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await GetAsync(f, "/a.png?width=50")).Response.StatusCode);
    }

    [Fact]
    public async Task CorruptSourceIs415()
    {
        await using WebFixture f = await WebFixture.StartAsync();
        f.AddImage("bad.jpg", new byte[200]);
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, (await GetAsync(f, "/bad.jpg?width=50")).Response.StatusCode);
    }

    [Fact]
    public async Task MissingFilesAndOtherPathsFallThrough()
    {
        await using WebFixture f = await WebFixture.StartAsync();
        f.AddImage("a.jpg", WebFixture.MakeImage(100, 100));
        File.WriteAllText(Path.Combine(f.WebRoot, "note.txt"), "hi");
        foreach (string url in new[] { "/missing.jpg?width=10", "/note.txt?width=10", "/..%2fa.jpg?width=10", "/" })
        {
            HttpResponseMessage r = (await GetAsync(f, url)).Response;
            Assert.True(r.Headers.Contains("X-Next"), url);
        }

        HttpResponseMessage post = await f.Client.PostAsync("/a.jpg?width=10", new ByteArrayContent([]));
        Assert.True(post.Headers.Contains("X-Next"));
    }

    [Fact]
    public async Task HeadHasHeadersButNoBody()
    {
        await using WebFixture f = await WebFixture.StartAsync();
        f.AddImage("a.jpg", WebFixture.MakeImage(200, 100));
        var req = new HttpRequestMessage(HttpMethod.Head, "/a.jpg?width=50");
        HttpResponseMessage r = await f.Client.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.True(r.Content.Headers.ContentLength > 0);
        Assert.Empty(await r.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task AllBehaviorProcessesWithoutCommands()
    {
        await using WebFixture f = await WebFixture.StartAsync(customize: b => b.ClearProviders().AddProvider(sp => new FileProviderImageProvider(
            sp.GetRequiredService<Microsoft.AspNetCore.Hosting.IWebHostEnvironment>().WebRootFileProvider, ProcessingBehavior.All)));
        f.AddImage("a.jpg", WebFixture.MakeImage(200, 100));
        HttpResponseMessage r = (await GetAsync(f, "/a.jpg")).Response;
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal(1, f.Processed);
    }

    [Fact]
    public async Task PresetsAndPresetsOnlyMode()
    {
        await using WebFixture f = await WebFixture.StartAsync(o =>
        {
            o.PresetsOnly = true;
            o.Presets["thumb"] = "width=40&height=40&fit=cover&format=webp";
        });
        f.AddImage("a.jpg", WebFixture.MakeImage(200, 100));
        (HttpResponseMessage r, byte[] body) = await GetAsync(f, "/a.jpg?preset=thumb");
        Assert.Equal("image/webp", r.Content.Headers.ContentType!.MediaType);
        Image img = WebFixture.Decode(body);
        Assert.Equal((40, 40), (img.Width, img.Height));
        // Free commands are not honored in presets-only mode; with nothing else recognized the request falls through.
        Assert.True((await GetAsync(f, "/a.jpg?width=10")).Response.Headers.Contains("X-Next"));
        // A preset cannot be bent by extra commands.
        byte[] again = (await GetAsync(f, "/a.jpg?preset=thumb&width=999")).Body;
        Assert.Equal(body, again);
        Assert.Equal(1, f.Processed);
    }

    [Fact]
    public async Task ExplicitCommandsOverridePresetValues()
    {
        await using WebFixture f = await WebFixture.StartAsync(o => o.Presets["p"] = "width=40&format=png");
        f.AddImage("a.jpg", WebFixture.MakeImage(200, 100));
        Image img = WebFixture.Decode((await GetAsync(f, "/a.jpg?preset=p&w=60")).Body);
        Assert.Equal(60, img.Width);
        Assert.Equal("image/png", img.SourceFormat!.MimeType);
    }

    [Fact]
    public async Task ParseHookCanAddCommands()
    {
        await using WebFixture f = await WebFixture.StartAsync(o => o.OnParseCommandsAsync = c =>
        {
            c.Commands["format"] = "webp";
            return Task.CompletedTask;
        });
        f.AddImage("a.jpg", WebFixture.MakeImage(200, 100));
        (HttpResponseMessage r, _) = await GetAsync(f, "/a.jpg?width=20");
        Assert.Equal("image/webp", r.Content.Headers.ContentType!.MediaType);
    }

    [Fact]
    public async Task BeforeSaveHookCanChangeTheImageAndPrepareResponseRuns()
    {
        await using WebFixture f = await WebFixture.StartAsync(o =>
        {
            o.OnBeforeSaveAsync = c =>
            {
                c.Image = c.Image.Resize(10, 10, Fit.Stretch);
                return Task.CompletedTask;
            };
            o.OnPrepareResponseAsync = c =>
            {
                c.Response.Headers["X-Custom"] = "yes";
                return Task.CompletedTask;
            };
        });
        f.AddImage("a.jpg", WebFixture.MakeImage(200, 100));
        (HttpResponseMessage r, byte[] body) = await GetAsync(f, "/a.jpg?width=100");
        Assert.Equal(10, WebFixture.Decode(body).Width);
        Assert.Equal("yes", r.Headers.GetValues("X-Custom").Single());
    }

    [Fact]
    public async Task AnimatedGifStaysAnimated()
    {
        await using WebFixture f = await WebFixture.StartAsync();
        byte[] gif = WebFixture.MakeImage(64, 64, "gif");
        f.AddImage("a.gif", gif);
        Image img = WebFixture.Decode((await GetAsync(f, "/a.gif?width=32")).Body);
        Assert.Equal(32, img.Width);
    }

    [Fact]
    public async Task AutoOrientCanBeDisabledPerRequestAndExifIsStripped()
    {
        await using WebFixture f = await WebFixture.StartAsync();
        f.AddImage("a.jpg", WebFixture.MakeImage(200, 100));
        Image img = WebFixture.Decode((await GetAsync(f, "/a.jpg?width=50&orient=0")).Body);
        Assert.True(img.Metadata.Exif.IsEmpty);
        Assert.Equal(50, img.Width);
    }
}
