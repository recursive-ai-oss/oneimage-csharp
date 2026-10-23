using System.IO;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Engines;
using ImageMagick;
using OneImage.Layers;
using OneImage.Psd;

namespace OneImage.Benchmarks.Psd;

/// <summary>
/// Reading a 4000 x 3000 PSD with three full-size layers (run-length coded, about 36 MB): the merged image alone, and every layer.
/// ImageMagick's <c>MagickImage</c> returns the merged image and <c>MagickImageCollection</c> every layer. Our full read loads the layer tree
/// and renders it, which is more work than ImageMagick's (it decodes the layers but does not composite them).
/// </summary>
[MemoryDiagnoser]
[SimpleJob(RunStrategy.Throughput, warmupCount: 1, iterationCount: 5)]
public class PsdReadBenchmarks : IDisposable
{
    private const int W = 4000, H = 3000;
    private string _path = null!;
    private Rgba32[] _out = null!;
    private ImageConfiguration _config = null!;

    [GlobalSetup]
    public void Setup()
    {
        _path = Path.Combine(Path.GetTempPath(), "oneimage-bench-large.psd");
        _out = new Rgba32[W * H];
        _config = new ImageConfiguration().Add(PsdFormat.Instance);
        if (File.Exists(_path))
        {
            return;
        }

        var doc = new Document(W, H, new DocumentOptions { Background = new Rgba32(255, 255, 255, 255) });
        var row = new Rgba32[W * Tile.Size];
        for (int seed = 1; seed <= 3; seed++)
        {
            PixelLayer layer = doc.CreatePixelLayer("layer" + seed);
            for (int y0 = 0; y0 < H; y0 += Tile.Size)
            {
                int rows = Math.Min(Tile.Size, H - y0);
                for (int r = 0; r < rows; r++)
                {
                    for (int x = 0; x < W; x++)
                    {
                        // Smooth blocks of 4 pixels with a diagonal ramp, so run-length coding finds runs as it would in flat artwork.
                        row[(r * W) + x] = new Rgba32((byte)((x >> 2) + seed * 40), (byte)(((y0 + r) >> 2) + seed * 17), (byte)(((x + y0 + r) >> 3) * seed), 200);
                    }
                }

                layer.Pixels.CopyFrom<Rgba32>(row.AsSpan(0, W * rows), new ImageRect(0, y0, W, rows));
            }

            layer.Opacity = 0.6f;
            doc.Add(layer);
        }

        using PsdFile file = PsdFile.FromDocument(doc);
        file.Save(_path);
    }

    [Benchmark(Description = "PSD merged image to Rgba32, 12 MP")]
    public Rgba32 OursComposite()
    {
        Image img = Image.Load(_path, new LoadOptions { Configuration = _config });
        img.CopyPixelsTo<Rgba32>(_out, new ImageRect(0, 0, W, H));
        return _out[^1];
    }

    [Benchmark(Description = "PSD all layers loaded and flattened to Rgba32, 12 MP")]
    public Rgba32 OursLayers()
    {
        using PsdFile file = PsdFile.Load(_path);
        file.Document.Render().CopyPixelsTo<Rgba32>(_out, new ImageRect(0, 0, W, H));
        return _out[^1];
    }

    [Benchmark(Baseline = true, Description = "Magick.NET MagickImage (merged image)")]
    public uint MagickComposite()
    {
        using var img = new MagickImage(_path);
        return img.Width;
    }

    [Benchmark(Description = "Magick.NET MagickImageCollection (all layers)")]
    public int MagickLayers()
    {
        using var all = new MagickImageCollection(_path);
        return all.Count;
    }

    public void Dispose() => GC.SuppressFinalize(this);
}
