using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OneImage;
using SixLabors.ImageSharp.Web.DependencyInjection;

namespace OneImage.Web.Benchmarks;

/// <summary>
/// A resize request through an in-process test server, against OneImage.Web and ImageSharp.Web 3.2.0 on the same source file.
/// "Cold" asks for a variant that is not cached yet; "cached" repeats a request whose result is in the cache.
/// Both sides run in the same process with the same client, so the shared request overhead cancels out of the comparison.
/// </summary>
[MemoryDiagnoser]
[ShortRunJob]
public class WebRequestBenchmarks
{
    private string _root = null!;
    private IHost _ours = null!;
    private IHost _theirs = null!;
    private HttpClient _oursClient = null!;
    private HttpClient _theirsClient = null!;
    private int _counter;

    [Params("6000x4000", "1600x1200")]
    public string Source { get; set; } = "6000x4000";

    [GlobalSetup]
    public async Task Setup()
    {
        string[] dims = Source.Split('x');
        int w = int.Parse(dims[0], System.Globalization.CultureInfo.InvariantCulture);
        int h = int.Parse(dims[1], System.Globalization.CultureInfo.InvariantCulture);
        _root = Path.Combine(Path.GetTempPath(), "oneimage-webbench-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, "wwwroot"));
        var pixels = new Rgba32[w * h];
        var rnd = new Random(7);
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                // Smooth gradients plus mild noise: photo-like enough for JPEG to produce a realistic file size.
                pixels[(y * w) + x] = new Rgba32((byte)((x * 255 / w) + rnd.Next(8)), (byte)((y * 255 / h) + rnd.Next(8)), (byte)(((x + y) >> 4) + rnd.Next(8)), 255);
            }
        }

        var writer = new System.Buffers.ArrayBufferWriter<byte>();
        Image.FromPixels<Rgba32>(pixels, w, h).Save(writer, ImageConfiguration.Default.FindByName("jpeg")!, new SaveOptions { EncoderOptions = new JpegEncoderOptions { Quality = 85 } });
        File.WriteAllBytes(Path.Combine(_root, "wwwroot", "photo.jpg"), writer.WrittenSpan.ToArray());

        _ours = await new HostBuilder().ConfigureWebHost(web => web.UseTestServer().UseContentRoot(_root).UseWebRoot(Path.Combine(_root, "wwwroot"))
            .ConfigureServices(s => s.AddOneImage().ConfigureCache(c => c.CacheRootPath = Path.Combine(_root, "ours-cache")))
            .Configure(app =>
            {
                app.UseOneImage();
                app.Run(c => { c.Response.StatusCode = 404; return Task.CompletedTask; });
            })).StartAsync();
        _theirs = await new HostBuilder().ConfigureWebHost(web => web.UseTestServer().UseContentRoot(_root).UseWebRoot(Path.Combine(_root, "wwwroot"))
            .ConfigureServices(s => s.AddImageSharp().Configure<SixLabors.ImageSharp.Web.Caching.PhysicalFileSystemCacheOptions>(c => c.CacheRootPath = Path.Combine(_root, "theirs-cache")))
            .Configure(app =>
            {
                app.UseImageSharp();
                app.Run(c => { c.Response.StatusCode = 404; return Task.CompletedTask; });
            })).StartAsync();
        _oursClient = _ours.GetTestClient();
        _theirsClient = _theirs.GetTestClient();

        // Warm up both (JIT, first-use costs) and fill the cached variant.
        _ = await _oursClient.GetByteArrayAsync("/photo.jpg?width=400&height=300&rmode=max");
        _ = await _theirsClient.GetByteArrayAsync("/photo.jpg?width=400&height=300&rmode=max");
        _ = await _oursClient.GetByteArrayAsync("/photo.jpg?width=400&height=1000&rmode=max");
        _ = await _theirsClient.GetByteArrayAsync("/photo.jpg?width=400&height=1000&rmode=max");
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        _oursClient.Dispose();
        _theirsClient.Dispose();
        await _ours.StopAsync();
        await _theirs.StopAsync();
        _ours.Dispose();
        _theirs.Dispose();
        Directory.Delete(_root, recursive: true);
    }

    // The height only makes the cache key unique: with rmode=max and a 400 px width the box is always bound by the width.
    private string ColdUrl() => "/photo.jpg?width=400&rmode=max&height=" + (2000 + Interlocked.Increment(ref _counter)).ToString(System.Globalization.CultureInfo.InvariantCulture);

    [Benchmark(Description = "OneImage.Web cold")]
    public async Task<int> OursCold() => (await _oursClient.GetByteArrayAsync(ColdUrl())).Length;

    [Benchmark(Description = "ImageSharp.Web 3.2.0 cold")]
    public async Task<int> TheirsCold() => (await _theirsClient.GetByteArrayAsync(ColdUrl())).Length;

    [Benchmark(Description = "OneImage.Web cached")]
    public async Task<int> OursCached() => (await _oursClient.GetByteArrayAsync("/photo.jpg?width=400&height=300&rmode=max")).Length;

    [Benchmark(Description = "ImageSharp.Web 3.2.0 cached")]
    public async Task<int> TheirsCached() => (await _theirsClient.GetByteArrayAsync("/photo.jpg?width=400&height=300&rmode=max")).Length;
}

internal static class Program
{
    private static void Main(string[] args) => BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
}
