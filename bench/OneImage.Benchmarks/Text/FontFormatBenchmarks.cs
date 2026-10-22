using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Engines;
using OneImage.Text;
using OneImage.Text.Compression;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.Processing;
using SharpRgba = SixLabors.ImageSharp.PixelFormats.Rgba32;

namespace OneImage.Benchmarks.Text;

/// <summary>
/// WOFF 2.0 loading and COLR color-glyph drawing against SixLabors.Fonts 2.1.3 and ImageSharp.Drawing 2.1.7 on the same files. Loading
/// includes decompression and (for ours) reconstructing the glyf/loca tables; both libraries read outlines lazily afterwards, so the
/// "load and outline" rows touch every glyph once. The drawing rows paint a line of ten layered color glyphs onto a white
/// 640 x 100 image and read the pixels back.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(RunStrategy.Throughput, warmupCount: 3, iterationCount: 12)]
public class FontFormatBenchmarks
{
    private const string ColorLine = "\u2600\u2601\u2602\u2603\u2604\u2600\u2601\u2602\u2603\u2604";

    private byte[] _woff2 = null!;
    private byte[] _ttf = null!;
    private OneImage.Text.TextOptions _ours = null!;
    private SixLabors.Fonts.TextOptions _theirs = null!;
    private OneImage.Rgba32[] _buffer = null!;

    [GlobalSetup]
    public void Setup()
    {
        string root = System.Reflection.CustomAttributeExtensions.GetCustomAttributes<System.Reflection.AssemblyMetadataAttribute>(typeof(FontFormatBenchmarks).Assembly).First(a => a.Key == "RepoRoot").Value!;
        string fonts = Path.Combine(root, "tests", "testdata", "fonts");
        _woff2 = File.ReadAllBytes(Path.Combine(fonts, "NotoSans-Shaping-Subset.woff2"));
        _ttf = File.ReadAllBytes(Path.Combine(fonts, "NotoSans-Shaping-Subset.ttf"));

        string color = Path.Combine(fonts, "OneImageColorTest-V0.ttf");
        OneImage.Text.Font ours = new OneImage.Text.FontCollection().Add(color).CreateFont(60);
        _ours = new OneImage.Text.TextOptions(ours) { Origin = new System.Numerics.Vector2(10, 10), ColorFontSupport = OneImage.Text.ColorFontSupport.MicrosoftColrFormat };
        SixLabors.Fonts.Font theirs = new SixLabors.Fonts.FontCollection().Add(color, CultureInfo.InvariantCulture).CreateFont(60);
        _theirs = new SixLabors.Fonts.TextOptions(theirs) { Origin = new System.Numerics.Vector2(10, 10), ColorFontSupport = SixLabors.Fonts.ColorFontSupport.MicrosoftColrFormat };
        _buffer = new OneImage.Rgba32[640 * 100];
    }

    [Benchmark(Description = "OneImage: load WOFF2")]
    [BenchmarkCategory("load")]
    public int LoadWoff2Ours() => new OneImage.Text.FontCollection().Add(_woff2).Name.Length;

    [Benchmark(Description = "SixLabors.Fonts: load WOFF2")]
    [BenchmarkCategory("load")]
    public int LoadWoff2Theirs() => new SixLabors.Fonts.FontCollection().Add(new MemoryStream(_woff2), CultureInfo.InvariantCulture).Name.Length;

    [Benchmark(Description = "OneImage: load TTF (same font, for scale)")]
    [BenchmarkCategory("load")]
    public int LoadTtfOurs() => new OneImage.Text.FontCollection().Add(_ttf).Name.Length;

    [Benchmark(Description = "SixLabors.Fonts: load TTF (same font, for scale)")]
    [BenchmarkCategory("load")]
    public int LoadTtfTheirs() => new SixLabors.Fonts.FontCollection().Add(new MemoryStream(_ttf), CultureInfo.InvariantCulture).Name.Length;

    [Benchmark(Description = "OneImage: draw ten color glyphs")]
    [BenchmarkCategory("color")]
    public byte DrawColorOurs()
    {
        var white = new OneImage.Rgba32[640 * 100];
        Array.Fill(white, new OneImage.Rgba32(255, 255, 255, 255));
        OneImage.Image img = OneImage.Image.FromPixels<OneImage.Rgba32>(white, 640, 100).DrawText(ColorLine, _ours, new OneImage.Rgba32(255, 0, 0, 255));
        img.CopyPixelsTo<OneImage.Rgba32>(_buffer, new ImageRect(0, 0, 640, 100));
        return _buffer[3200].R;
    }

    [Benchmark(Description = "ImageSharp.Drawing: draw ten color glyphs")]
    [BenchmarkCategory("color")]
    public byte DrawColorTheirs()
    {
        using var img = new SixLabors.ImageSharp.Image<SharpRgba>(640, 100, SixLabors.ImageSharp.Color.White);
        var options = new RichTextOptions(_theirs.Font) { Origin = _theirs.Origin, ColorFontSupport = _theirs.ColorFontSupport };
        img.Mutate(c => c.DrawText(options, ColorLine, SixLabors.ImageSharp.Color.Red));
        return img[320, 5].R;
    }
}

/// <summary>Our Brotli decoder against System.IO.Compression's (a wrapper over the native library); information only.</summary>
[MemoryDiagnoser]
[SimpleJob(RunStrategy.Throughput, warmupCount: 3, iterationCount: 12)]
public class BrotliBenchmarks
{
    private byte[] _text = null!;
    private byte[] _textCompressed = null!;
    private byte[] _bin = null!;
    private byte[] _binCompressed = null!;
    private byte[] _output = null!;

    [GlobalSetup]
    public void Setup()
    {
        string root = System.Reflection.CustomAttributeExtensions.GetCustomAttributes<System.Reflection.AssemblyMetadataAttribute>(typeof(BrotliBenchmarks).Assembly).First(a => a.Key == "RepoRoot").Value!;
        string dir = Path.Combine(root, "tests", "testdata", "brotli");
        _text = File.ReadAllBytes(Path.Combine(dir, "alice29.txt"));
        _textCompressed = File.ReadAllBytes(Path.Combine(dir, "alice29.txt.compressed"));

        // A font-like binary: the glyf table of the test font, repeated with small changes, compressed the way a WOFF2 encoder would (quality 11).
        byte[] font = File.ReadAllBytes(Path.Combine(root, "tests", "testdata", "fonts", "NotoSansCJK-Subset.otf"));
        _bin = new byte[font.Length * 8];
        var rng = new Random(3);
        for (int i = 0; i < 8; i++)
        {
            font.CopyTo(_bin, i * font.Length);
            for (int k = 0; k < 200; k++)
            {
                _bin[(i * font.Length) + rng.Next(font.Length)] ^= (byte)rng.Next(256);
            }
        }

        byte[] buf = new byte[BrotliEncoder.GetMaxCompressedLength(_bin.Length)];
        BrotliEncoder.TryCompress(_bin, buf, out int n, 11, 22);
        _binCompressed = buf.AsSpan(0, n).ToArray();
        _output = new byte[_bin.Length];
    }

    [Benchmark(Description = "OneImage: decode alice29 (152 KB)")]
    [BenchmarkCategory("text")]
    public int TextOurs() => Brotli.Decode(_textCompressed, 1 << 24, 1 << 20, _text.Length, out int n)[n - 1];

    [Benchmark(Description = "System.IO.Compression: decode alice29 (152 KB)")]
    [BenchmarkCategory("text")]
    public int TextBcl() { BrotliDecoder.TryDecompress(_textCompressed, _output, out int n); return n; }

    [Benchmark(Description = "OneImage: decode font-like binary (1 MB)")]
    [BenchmarkCategory("binary")]
    public int BinaryOurs() => Brotli.Decode(_binCompressed, 1 << 26, 1 << 20, _bin.Length, out int n)[n - 1];

    [Benchmark(Description = "System.IO.Compression: decode font-like binary (1 MB)")]
    [BenchmarkCategory("binary")]
    public int BinaryBcl() { BrotliDecoder.TryDecompress(_binCompressed, _output, out int n); return n; }
}
