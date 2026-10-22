using System;
using System.IO;
using System.Linq;
using Microsoft.AspNetCore.Hosting;
using System.Net.Http;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OneImage;
using OneImage.Web;

// Serves one generated image through the middleware on an ephemeral port, fetches a resized WebP and checks it.
// Published with NativeAOT in CI-style checks to prove the middleware needs no reflection.
string root = Path.Combine(Path.GetTempPath(), "oneimage-web-aot-" + Guid.NewGuid().ToString("N"));
string webRoot = Path.Combine(root, "wwwroot");
Directory.CreateDirectory(webRoot);
try
{
    var pixels = new Rgba32[320 * 200];
    for (int i = 0; i < pixels.Length; i++)
    {
        pixels[i] = new Rgba32((byte)(i % 320), (byte)(i / 320), 90, 255);
    }

    Image.FromPixels<Rgba32>(pixels, 320, 200).Save(Path.Combine(webRoot, "a.png"));

    WebApplicationBuilder builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { ContentRootPath = root, WebRootPath = webRoot });
    builder.WebHost.UseUrls("http://127.0.0.1:0");
    builder.Services.AddOneImage(o => o.HmacSecretKey = new byte[32]).ConfigureCache(c => c.CacheRootPath = Path.Combine(root, "cache"));
    await using WebApplication app = builder.Build();
    app.UseOneImage();
    await app.StartAsync();

    string address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
    string url = app.Services.GetRequiredService<ImageUrlSigner>().Sign("/a.png?width=80&format=webp");
    using var client = new HttpClient { BaseAddress = new Uri(address) };
    byte[] body = await client.GetByteArrayAsync(url);
    Image result = Image.Load(body);
    bool unsignedRejected = (await client.GetAsync("/a.png?width=80")).StatusCode == System.Net.HttpStatusCode.Forbidden;
    await app.StopAsync();

    if (result.Width != 80 || result.Height != 50 || result.SourceFormat?.Name != "webp" || !unsignedRejected)
    {
        Console.Error.WriteLine($"unexpected result {result.Width}x{result.Height} {result.SourceFormat?.Name} rejected={unsignedRejected}");
        return 1;
    }

    Console.WriteLine("ok");
    return 0;
}
finally
{
    Directory.Delete(root, recursive: true);
}
