using System.Net;
using System.Net.Http.Headers;
using OneImage;

namespace OneImage.Web.Tests;

public class CacheTests
{
    [Fact]
    public async Task SecondRequestIsServedFromTheCache()
    {
        await using WebFixture f = await WebFixture.StartAsync();
        f.AddImage("a.jpg", WebFixture.MakeImage(400, 300));
        byte[] first = await f.Client.GetByteArrayAsync("/a.jpg?width=100");
        byte[] second = await f.Client.GetByteArrayAsync("/a.jpg?w=100"); // Another spelling of the same request.
        byte[] third = await f.Client.GetByteArrayAsync("/a.jpg?width=100&unknown=1");
        Assert.Equal(first, second);
        Assert.Equal(first, third);
        Assert.Equal(1, f.Processed);
        _ = await f.Client.GetByteArrayAsync("/a.jpg?width=101");
        Assert.Equal(2, f.Processed);
    }

    [Fact]
    public async Task TheCacheSurvivesARestartAndLivesOnDisk()
    {
        string root = Path.Combine(Path.GetTempPath(), "oneimage-web-" + Guid.NewGuid().ToString("N"));
        byte[] first;
        await using (WebFixture one = await WebFixture.StartAsync(sharedRoot: root))
        {
            one.AddImage("a.jpg", WebFixture.MakeImage(400, 300));
            first = await one.Client.GetByteArrayAsync("/a.jpg?width=100");
            Assert.Equal(1, one.Processed);
            string[] files = Directory.GetFiles(one.CacheDir, "*", SearchOption.AllDirectories);
            Assert.Equal(2, files.Length);
            // Default layout: two levels of two characters, then a 32-character name.
            string rel = Path.GetRelativePath(one.CacheDir, files.First(x => x.EndsWith(".img", StringComparison.Ordinal)));
            string[] parts = rel.Split(Path.DirectorySeparatorChar);
            Assert.Equal(3, parts.Length);
            Assert.Equal(2, parts[0].Length);
            Assert.Equal(2, parts[1].Length);
            Assert.Equal(32 + 4, parts[2].Length);
            Assert.StartsWith(parts[0] + parts[1], parts[2], StringComparison.Ordinal);
        }

        // The first fixture deleted its folder when disposed; recreate the scenario with the folder kept.
        root = Path.Combine(Path.GetTempPath(), "oneimage-web-" + Guid.NewGuid().ToString("N"));
        WebFixture a = await WebFixture.StartAsync(sharedRoot: root);
        a.AddImage("a.jpg", WebFixture.MakeImage(400, 300));
        first = await a.Client.GetByteArrayAsync("/a.jpg?width=100");
        a.Client.Dispose();
        await using WebFixture b = await WebFixture.StartAsync(sharedRoot: root);
        byte[] again = await b.Client.GetByteArrayAsync("/a.jpg?width=100");
        Assert.Equal(first, again);
        Assert.Equal(0, b.Processed);
    }

    [Fact]
    public async Task CacheLayoutIsConfigurable()
    {
        await using WebFixture f = await WebFixture.StartAsync(o => o.CacheHashLength = 16, customize: b => b.ConfigureCache(c =>
        {
            c.CacheFolderDepth = 3;
            c.CacheFolderNameLength = 1;
        }));
        f.AddImage("a.jpg", WebFixture.MakeImage(100, 100));
        _ = await f.Client.GetByteArrayAsync("/a.jpg?width=10");
        string img = Directory.GetFiles(f.CacheDir, "*.img", SearchOption.AllDirectories).Single();
        string[] parts = Path.GetRelativePath(f.CacheDir, img).Split(Path.DirectorySeparatorChar);
        Assert.Equal(4, parts.Length);
        Assert.All(parts.Take(3), p => Assert.Single(p));
        Assert.Equal(16 + 4, parts[3].Length);
    }

    [Fact]
    public async Task ResponseHeaders()
    {
        await using WebFixture f = await WebFixture.StartAsync(o => o.BrowserMaxAge = TimeSpan.FromHours(2));
        string path = f.AddImage("a.jpg", WebFixture.MakeImage(400, 300));
        File.SetLastWriteTimeUtc(path, new DateTime(2024, 5, 6, 7, 8, 9, DateTimeKind.Utc));
        HttpResponseMessage r = await f.Client.GetAsync("/a.jpg?width=100");
        Assert.Equal("public, max-age=7200", r.Headers.CacheControl!.ToString());
        Assert.NotNull(r.Headers.ETag);
        Assert.StartsWith("\"", r.Headers.ETag!.Tag, StringComparison.Ordinal);
        Assert.Equal(new DateTimeOffset(2024, 5, 6, 7, 8, 9, TimeSpan.Zero), r.Content.Headers.LastModified);
        Assert.Equal("nosniff", r.Headers.GetValues("X-Content-Type-Options").Single());
    }

    [Fact]
    public async Task ZeroBrowserAgeMeansRevalidate()
    {
        await using WebFixture f = await WebFixture.StartAsync(o => o.BrowserMaxAge = TimeSpan.Zero);
        f.AddImage("a.jpg", WebFixture.MakeImage(100, 100));
        HttpResponseMessage r = await f.Client.GetAsync("/a.jpg?width=10");
        Assert.True(r.Headers.CacheControl!.NoCache);
    }

    [Fact]
    public async Task ConditionalRequestsGet304WithoutProcessing()
    {
        await using WebFixture f = await WebFixture.StartAsync();
        f.AddImage("a.jpg", WebFixture.MakeImage(400, 300));
        HttpResponseMessage first = await f.Client.GetAsync("/a.jpg?width=100");
        EntityTagHeaderValue etag = first.Headers.ETag!;
        DateTimeOffset modified = first.Content.Headers.LastModified!.Value;
        Assert.Equal(1, f.Processed);

        var byTag = new HttpRequestMessage(HttpMethod.Get, "/a.jpg?width=100");
        byTag.Headers.IfNoneMatch.Add(etag);
        HttpResponseMessage r1 = await f.Client.SendAsync(byTag);
        Assert.Equal(HttpStatusCode.NotModified, r1.StatusCode);
        Assert.Empty(await r1.Content.ReadAsByteArrayAsync());
        Assert.Equal(etag, r1.Headers.ETag);

        var byDate = new HttpRequestMessage(HttpMethod.Get, "/a.jpg?width=100");
        byDate.Headers.IfModifiedSince = modified;
        Assert.Equal(HttpStatusCode.NotModified, (await f.Client.SendAsync(byDate)).StatusCode);

        var wrongTag = new HttpRequestMessage(HttpMethod.Get, "/a.jpg?width=100");
        wrongTag.Headers.IfNoneMatch.Add(new EntityTagHeaderValue("\"other\""));
        Assert.Equal(HttpStatusCode.OK, (await f.Client.SendAsync(wrongTag)).StatusCode);

        // A different variant has a different tag.
        var otherVariant = new HttpRequestMessage(HttpMethod.Get, "/a.jpg?width=90");
        otherVariant.Headers.IfNoneMatch.Add(etag);
        Assert.Equal(HttpStatusCode.OK, (await f.Client.SendAsync(otherVariant)).StatusCode);

        // 304s for a never-seen request did not process anything.
        Assert.Equal(2, f.Processed);
    }

    [Fact]
    public async Task ChangedSourceInvalidatesTheCacheAndTheTag()
    {
        await using WebFixture f = await WebFixture.StartAsync();
        string path = f.AddImage("a.jpg", WebFixture.MakeImage(400, 300));
        File.SetLastWriteTimeUtc(path, new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        HttpResponseMessage one = await f.Client.GetAsync("/a.jpg?width=100");
        byte[] oneBody = await one.Content.ReadAsByteArrayAsync();

        File.WriteAllBytes(path, WebFixture.MakeImage(200, 100));
        File.SetLastWriteTimeUtc(path, new DateTime(2024, 1, 2, 0, 0, 0, DateTimeKind.Utc));
        var conditional = new HttpRequestMessage(HttpMethod.Get, "/a.jpg?width=100");
        conditional.Headers.IfNoneMatch.Add(one.Headers.ETag!);
        HttpResponseMessage two = await f.Client.SendAsync(conditional);
        Assert.Equal(HttpStatusCode.OK, two.StatusCode);
        Assert.NotEqual(one.Headers.ETag, two.Headers.ETag);
        Assert.Equal(2, f.Processed);
        Assert.NotEqual(oneBody, await two.Content.ReadAsByteArrayAsync());
        // Back to a cache hit afterwards.
        _ = await f.Client.GetByteArrayAsync("/a.jpg?width=100");
        Assert.Equal(2, f.Processed);
    }

    [Fact]
    public async Task SameLengthSourceWithNewTimestampIsReprocessed()
    {
        await using WebFixture f = await WebFixture.StartAsync();
        byte[] bytes = WebFixture.MakeImage(100, 100);
        string path = f.AddImage("a.jpg", bytes);
        _ = await f.Client.GetByteArrayAsync("/a.jpg?width=10");
        File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(path).AddMinutes(5));
        _ = await f.Client.GetByteArrayAsync("/a.jpg?width=10");
        Assert.Equal(2, f.Processed);
    }

    [Fact]
    public async Task ExpiredEntriesAreReprocessed()
    {
        await using WebFixture f = await WebFixture.StartAsync(o => o.CacheMaxAge = TimeSpan.FromMilliseconds(1));
        f.AddImage("a.jpg", WebFixture.MakeImage(100, 100));
        _ = await f.Client.GetByteArrayAsync("/a.jpg?width=10");
        await Task.Delay(30);
        _ = await f.Client.GetByteArrayAsync("/a.jpg?width=10");
        Assert.Equal(2, f.Processed);
    }

    [Fact]
    public async Task ADeletedCacheFileIsRebuilt()
    {
        await using WebFixture f = await WebFixture.StartAsync();
        f.AddImage("a.jpg", WebFixture.MakeImage(100, 100));
        byte[] first = await f.Client.GetByteArrayAsync("/a.jpg?width=10");
        foreach (string file in Directory.GetFiles(f.CacheDir, "*.img", SearchOption.AllDirectories))
        {
            File.Delete(file);
        }

        Assert.Equal(first, await f.Client.GetByteArrayAsync("/a.jpg?width=10"));
        Assert.Equal(2, f.Processed);
    }

    [Fact]
    public async Task SaltChangesTheKey()
    {
        string root = Path.Combine(Path.GetTempPath(), "oneimage-web-" + Guid.NewGuid().ToString("N"));
        WebFixture a = await WebFixture.StartAsync(sharedRoot: root);
        a.AddImage("a.jpg", WebFixture.MakeImage(100, 100));
        _ = await a.Client.GetByteArrayAsync("/a.jpg?width=10");
        a.Client.Dispose();
        await using WebFixture b = await WebFixture.StartAsync(o => o.CacheSalt = "v2", sharedRoot: root);
        _ = await b.Client.GetByteArrayAsync("/a.jpg?width=10");
        Assert.Equal(1, b.Processed);
    }

    [Fact]
    public async Task ConcurrentIdenticalRequestsAreProcessedOnce()
    {
        await using WebFixture f = await WebFixture.StartAsync(o => o.OnBeforeSaveAsync = _ => Task.Delay(300));
        f.AddImage("a.jpg", WebFixture.MakeImage(400, 300));
        Task<byte[]>[] requests = Enumerable.Range(0, 24).Select(_ => f.Client.GetByteArrayAsync("/a.jpg?width=100")).ToArray();
        byte[][] results = await Task.WhenAll(requests);
        Assert.Equal(1, f.Processed);
        Assert.All(results, r => Assert.Equal(results[0], r));
    }

    [Fact]
    public async Task ConcurrentDistinctRequestsAreBoundedByMaxConcurrentProcessing()
    {
        int running = 0;
        int peak = 0;
        await using WebFixture f = await WebFixture.StartAsync(o =>
        {
            o.MaxConcurrentProcessing = 2;
            o.OnBeforeSaveAsync = async ctx =>
            {
                int now = Interlocked.Increment(ref running);
                int seen;
                while ((seen = Volatile.Read(ref peak)) < now && Interlocked.CompareExchange(ref peak, now, seen) != seen)
                {
                }

                await Task.Delay(50);
                _ = Interlocked.Decrement(ref running);
            };
        });
        f.AddImage("a.jpg", WebFixture.MakeImage(200, 200));
        await Task.WhenAll(Enumerable.Range(10, 8).Select(w => f.Client.GetByteArrayAsync("/a.jpg?width=" + w)));
        Assert.Equal(8, f.Processed);
        Assert.True(peak <= 2, "peak " + peak);
    }
}

public class SecurityTests
{
    private static readonly byte[] Key = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();

    [Theory]
    [InlineData(HmacAlgorithm.Sha256, 64)]
    [InlineData(HmacAlgorithm.Sha384, 96)]
    [InlineData(HmacAlgorithm.Sha512, 128)]
    public async Task SignedUrlsWorkAndOthersAreForbidden(HmacAlgorithm algorithm, int tokenLength)
    {
        await using WebFixture f = await WebFixture.StartAsync(o =>
        {
            o.HmacSecretKey = Key;
            o.HmacAlgorithm = algorithm;
        });
        f.AddImage("a.jpg", WebFixture.MakeImage(400, 300));
        var signer = (ImageUrlSigner)f.Services.GetService(typeof(ImageUrlSigner))!;
        Assert.True(signer.IsEnabled);

        string url = signer.Sign("/a.jpg?width=100&format=webp");
        Assert.Equal(tokenLength, url[(url.IndexOf("hmac=", StringComparison.Ordinal) + 5)..].Length);
        Assert.Equal(HttpStatusCode.OK, (await f.Client.GetAsync(url)).StatusCode);
        string upper = url[..(url.IndexOf("hmac=", StringComparison.Ordinal) + 5)] + url[(url.IndexOf("hmac=", StringComparison.Ordinal) + 5)..].ToUpperInvariant();
        Assert.Equal(HttpStatusCode.OK, (await f.Client.GetAsync(upper)).StatusCode);

        Assert.Equal(HttpStatusCode.Forbidden, (await f.Client.GetAsync("/a.jpg?width=100&format=webp")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await f.Client.GetAsync(url.Replace("width=100", "width=101", StringComparison.Ordinal))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await f.Client.GetAsync("/a.jpg?width=100&format=webp&hmac=" + new string('0', tokenLength))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await f.Client.GetAsync("/a.jpg?width=100&format=webp&hmac=")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await f.Client.GetAsync(url + "&hmac=" + url[(url.IndexOf("hmac=", StringComparison.Ordinal) + 5)..])).StatusCode);
        // The token belongs to the path too.
        f.AddImage("b.jpg", WebFixture.MakeImage(400, 300));
        Assert.Equal(HttpStatusCode.Forbidden, (await f.Client.GetAsync(url.Replace("/a.jpg", "/b.jpg", StringComparison.Ordinal))).StatusCode);
        Assert.Equal(1, f.Processed);
    }

    [Fact]
    public async Task SigningMatchesTheDocumentedFormula()
    {
        await using WebFixture f = await WebFixture.StartAsync(o => o.HmacSecretKey = Key);
        string token = HmacUtilities.ComputeToken("/a b.jpg?width=100", Key);
        Assert.Equal(token, HmacUtilities.ComputeToken(HmacUtilities.SignedText("/a b.jpg", "?width=100&hmac=zzz"), Key));
        var signer = (ImageUrlSigner)f.Services.GetService(typeof(ImageUrlSigner))!;
        Assert.Equal("/a%20b.jpg?width=100&hmac=" + token, signer.Sign("/a%20b.jpg?width=100"));
        Assert.StartsWith("/a.jpg?hmac=", signer.Sign("/a.jpg"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RequestsWithoutCommandsNeedNoTokenAndUnsignedJunkFallsThrough()
    {
        await using WebFixture f = await WebFixture.StartAsync(o => o.HmacSecretKey = Key);
        f.AddImage("a.jpg", WebFixture.MakeImage(100, 100));
        Assert.True((await f.Client.GetAsync("/a.jpg")).Headers.Contains("X-Next"));
        Assert.True((await f.Client.GetAsync("/a.jpg?junk=1")).Headers.Contains("X-Next"));
    }

    [Fact]
    public async Task PresetsStillNeedTheTokenWhenAKeyIsSet()
    {
        await using WebFixture f = await WebFixture.StartAsync(o =>
        {
            o.HmacSecretKey = Key;
            o.Presets["t"] = "width=20";
        });
        f.AddImage("a.jpg", WebFixture.MakeImage(100, 100));
        Assert.Equal(HttpStatusCode.Forbidden, (await f.Client.GetAsync("/a.jpg?preset=t")).StatusCode);
        var signer = (ImageUrlSigner)f.Services.GetService(typeof(ImageUrlSigner))!;
        Assert.Equal(HttpStatusCode.OK, (await f.Client.GetAsync(signer.Sign("/a.jpg?preset=t"))).StatusCode);
    }

    [Fact]
    public async Task SignerIsANoOpWithoutAKey()
    {
        await using WebFixture f = await WebFixture.StartAsync();
        var signer = (ImageUrlSigner)f.Services.GetService(typeof(ImageUrlSigner))!;
        Assert.False(signer.IsEnabled);
        Assert.Equal("/a.jpg?width=1", signer.Sign("/a.jpg?width=1"));
    }

    [Fact]
    public void InvalidOptionsFailAtStartup()
    {
        var o = new OneImageOptions { CacheHashLength = 5 };
        Assert.Throws<ArgumentOutOfRangeException>(o.Validate);
        Assert.Throws<ArgumentException>(new OneImageOptions { HmacSecretKey = [] }.Validate);
    }
}

public class ProviderTests
{
    [Fact]
    public async Task PhysicalProviderWithPrefixAndTraversalProtection()
    {
        string dir = Path.Combine(Path.GetTempPath(), "oneimage-photos-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "p.jpg"), WebFixture.MakeImage(200, 100));
        File.WriteAllBytes(Path.Combine(dir, ".hidden.jpg"), WebFixture.MakeImage(200, 100));
        try
        {
            await using WebFixture f = await WebFixture.StartAsync(customize: b => b.ClearProviders().AddProvider(_ => new PhysicalFileImageProvider(dir, requestPathPrefix: "/photos")));
            Image ok = WebFixture.Decode(await f.Client.GetByteArrayAsync("/photos/p.jpg?width=50"));
            Assert.Equal(50, ok.Width);
            Assert.True((await f.Client.GetAsync("/p.jpg?width=50")).Headers.Contains("X-Next"));
            Assert.True((await f.Client.GetAsync("/photos/.hidden.jpg?width=50")).Headers.Contains("X-Next"));
            Assert.True((await f.Client.GetAsync("/photos/%2e%2e/p.jpg?width=50")).Headers.Contains("X-Next"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private sealed class MemoryProvider : IImageProvider
    {
        private readonly byte[] _bytes = WebFixture.MakeImage(120, 60, "png");

        public ProcessingBehavior ProcessingBehavior => ProcessingBehavior.CommandOnly;

        public bool Match(Microsoft.AspNetCore.Http.HttpContext context) => context.Request.Path.StartsWithSegments("/mem");

        public ValueTask<IImageSource?> GetAsync(Microsoft.AspNetCore.Http.HttpContext context, CancellationToken cancellationToken) =>
            ValueTask.FromResult<IImageSource?>(new Source(_bytes));

        private sealed class Source(byte[] bytes) : IImageSource
        {
            public DateTimeOffset LastModified => new(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);

            public long Length => bytes.Length;

            public ValueTask<Stream> OpenReadAsync(CancellationToken cancellationToken) => ValueTask.FromResult<Stream>(new MemoryStream(bytes));
        }
    }

    [Fact]
    public async Task CustomProvidersPlugIn()
    {
        await using WebFixture f = await WebFixture.StartAsync(customize: b => b.AddProvider<MemoryProvider>());
        Image img = WebFixture.Decode(await f.Client.GetByteArrayAsync("/mem/anything.png?width=60"));
        Assert.Equal((60, 30), (img.Width, img.Height));
    }

    [Fact]
    public async Task CustomCacheAndKeyPlugIn()
    {
        await using WebFixture f = await WebFixture.StartAsync(customize: b => b.SetCacheKey<HostPathCacheKey>().SetCacheHash<Sha256CacheHash>());
        f.AddImage("a.jpg", WebFixture.MakeImage(100, 100));
        _ = await f.Client.GetByteArrayAsync("/a.jpg?width=10");
        _ = await f.Client.GetByteArrayAsync("/a.jpg?width=10");
        Assert.Equal(1, f.Processed);
    }
}

public class TagHelperTests
{
    [Fact]
    public void AddsCommandsAndTheToken()
    {
        byte[] key = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16];
        var options = Microsoft.Extensions.Options.Options.Create(new OneImageOptions { HmacSecretKey = key });
        var helper = new ImageTagHelper(new ImageUrlSigner(options), new Microsoft.AspNetCore.Mvc.Routing.UrlHelperFactory());
        helper.Commands["width"] = "300";
        helper.Commands["format"] = "webp";
        var attributes = new Microsoft.AspNetCore.Razor.TagHelpers.TagHelperAttributeList { { "src", "/photos/a.jpg" } };
        var context = new Microsoft.AspNetCore.Razor.TagHelpers.TagHelperContext(attributes, new Dictionary<object, object>(), "id");
        var output = new Microsoft.AspNetCore.Razor.TagHelpers.TagHelperOutput("img", attributes, (_, _) => Task.FromResult<Microsoft.AspNetCore.Razor.TagHelpers.TagHelperContent>(new Microsoft.AspNetCore.Razor.TagHelpers.DefaultTagHelperContent()));
        helper.Process(context, output);
        string src = output.Attributes["src"].Value!.ToString()!;
        Assert.StartsWith("/photos/a.jpg?width=300&format=webp&hmac=", src, StringComparison.Ordinal);
        string token = src[(src.IndexOf("hmac=", StringComparison.Ordinal) + 5)..];
        Assert.Equal(HmacUtilities.ComputeToken("/photos/a.jpg?width=300&format=webp", key), token);
    }

    [Fact]
    public void LeavesPlainImagesAlone()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new OneImageOptions());
        var helper = new ImageTagHelper(new ImageUrlSigner(options), new Microsoft.AspNetCore.Mvc.Routing.UrlHelperFactory());
        var attributes = new Microsoft.AspNetCore.Razor.TagHelpers.TagHelperAttributeList { { "src", "/a.jpg" } };
        var context = new Microsoft.AspNetCore.Razor.TagHelpers.TagHelperContext(attributes, new Dictionary<object, object>(), "id");
        var output = new Microsoft.AspNetCore.Razor.TagHelpers.TagHelperOutput("img", attributes, (_, _) => Task.FromResult<Microsoft.AspNetCore.Razor.TagHelpers.TagHelperContent>(new Microsoft.AspNetCore.Razor.TagHelpers.DefaultTagHelperContent()));
        helper.Process(context, output);
        Assert.Equal("/a.jpg", output.Attributes["src"].Value);
    }
}
