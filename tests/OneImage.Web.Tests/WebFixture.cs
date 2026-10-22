using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OneImage;

namespace OneImage.Web.Tests;

/// <summary>A test server with the middleware in front of a terminal that answers 404 with an <c>X-Next</c> header, so pass-through is visible.</summary>
internal sealed class WebFixture : IAsyncDisposable
{
    private readonly IHost _host;
    private int _processed;

    private WebFixture(IHost host, string root)
    {
        _host = host;
        Root = root;
        Client = host.GetTestClient();
    }

    public string Root { get; }

    public string WebRoot => Path.Combine(Root, "wwwroot");

    public string CacheDir => Path.Combine(Root, "cache");

    public HttpClient Client { get; }

    public IServiceProvider Services => _host.Services;

    /// <summary>Gets how many times an image was actually processed (cache misses).</summary>
    public int Processed => Volatile.Read(ref _processed);

    public static async Task<WebFixture> StartAsync(Action<OneImageOptions>? configure = null, string? sharedRoot = null, Action<IOneImageBuilder>? customize = null)
    {
        string root = sharedRoot ?? Path.Combine(Path.GetTempPath(), "oneimage-web-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "wwwroot"));
        WebFixture? fixture = null;
        IHost host = await new HostBuilder().ConfigureWebHost(web => web
            .UseTestServer()
            .UseContentRoot(root)
            .UseWebRoot(Path.Combine(root, "wwwroot"))
            .ConfigureServices(services =>
            {
                IOneImageBuilder builder = services.AddOneImage(o =>
                {
                    configure?.Invoke(o);
                    Func<ImageProcessedContext, Task>? user = o.OnProcessedAsync;
                    o.OnProcessedAsync = async c =>
                    {
                        _ = Interlocked.Increment(ref fixture!._processed);
                        if (user is not null)
                        {
                            await user(c);
                        }
                    };
                }).ConfigureCache(c => c.CacheRootPath = Path.Combine(root, "cache"));
                customize?.Invoke(builder);
            })
            .Configure(app =>
            {
                app.UseOneImage();
                app.Run(ctx =>
                {
                    ctx.Response.StatusCode = 404;
                    ctx.Response.Headers["X-Next"] = "1";
                    return Task.CompletedTask;
                });
            })).StartAsync();
        fixture = new WebFixture(host, root);
        return fixture;
    }

    public static byte[] MakeImage(int width, int height, string format = "jpeg", bool alpha = false)
    {
        var pixels = new Rgba32[width * height];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                pixels[(y * width) + x] = new Rgba32((byte)(x * 255 / Math.Max(1, width - 1)), (byte)(y * 255 / Math.Max(1, height - 1)), (byte)((x ^ y) & 0xFF), alpha ? (byte)(x < width / 2 ? 0 : 255) : (byte)255);
            }
        }

        var writer = new System.Buffers.ArrayBufferWriter<byte>();
        Image.FromPixels<Rgba32>(pixels, width, height).Save(writer, ImageConfiguration.Default.FindByName(format)!);
        return writer.WrittenSpan.ToArray();
    }

    public string AddImage(string name, byte[] bytes)
    {
        string path = Path.Combine(WebRoot, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    public static Image Decode(byte[] bytes) => Image.Load(bytes);

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await _host.StopAsync();
        _host.Dispose();
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
