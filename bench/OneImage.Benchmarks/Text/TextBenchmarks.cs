using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Engines;
using OneImage.Text;
using System.Globalization;
using System.IO;
using System.Linq;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.Processing;
using Ours = OneImage;
using OursMeasurer = OneImage.Text.TextMeasurer;
using SharpMeasurer = SixLabors.Fonts.TextMeasurer;
using OursFont = OneImage.Text.Font;
using OursOptions = OneImage.Text.TextOptions;
using SharpRgba = SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32>;

namespace OneImage.Benchmarks.Text;

/// <summary>
/// Fonts and text against SixLabors.Fonts 2.1.3 and ImageSharp.Drawing 2.1.7, one font file (a Noto Sans subset) for both: loading it, measuring
/// a 120-word paragraph at 24 px wrapped at 900 px, and drawing it onto a 1000 × 1000 RGBA image. OneImage draws on a Canvas in place, as
/// ImageSharp mutates its image; both rows clear the image first. The "image" rows build the lazy pipeline and copy the pixels out.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(RunStrategy.Throughput, warmupCount: 3, iterationCount: 12)]
public class TextBenchmarks : IDisposable
{
    private const int Size = 1000;
    private const string Paragraph =
        "Typography is the craft of arranging type so that written language is legible, readable and appealing when displayed. " +
        "The arrangement of type involves selecting typefaces, point sizes, line lengths, line spacing and letter spacing, and " +
        "adjusting the space between pairs of letters. The term typography is also applied to the style, arrangement and appearance " +
        "of the letters, numbers and symbols created by the process. Type design is a closely related craft, sometimes considered " +
        "part of typography; most typographers do not design typefaces, and some type designers do not consider themselves typographers. " +
        "Typography also may be used as an ornamental and decorative device, unrelated to the communication of information.";

    private string _fontPath = null!;
    private byte[] _fontBytes = null!;
    private OursOptions _ours = null!;
    private RichTextOptions _theirs = null!;
    private Ours.Canvas _canvas = null!;
    private Ours.Rgba32[] _source = null!;
    private Ours.Rgba32[] _out = null!;
    private SharpRgba _sharp = null!;
    private SixLabors.ImageSharp.PixelFormats.Rgba32[] _sharpSource = null!;
    private SixLabors.ImageSharp.PixelFormats.Rgba32[] _sharpOut = null!;
    private readonly Ours.Rgba32 _white = new(255, 255, 255, 255);
    private readonly Ours.Rgba32 _black = new(0, 0, 0, 255);

    [GlobalSetup]
    public void Setup()
    {
        _fontPath = FindFont("NotoSans-Subset-Regular.ttf");
        _fontBytes = File.ReadAllBytes(_fontPath);
        OursFont ours = new OneImage.Text.FontCollection().Add(_fontPath).CreateFont(24);
        var fam = new SixLabors.Fonts.FontCollection().Add(_fontPath, CultureInfo.InvariantCulture);
        SixLabors.Fonts.Font theirs = fam.CreateFont(24);
        _ours = new OursOptions(ours) { Origin = new System.Numerics.Vector2(50, 50), WrappingLength = 900 };
        _theirs = new RichTextOptions(theirs) { Origin = new System.Numerics.Vector2(50, 50), WrappingLength = 900 };
        _canvas = new Ours.Canvas(Size, Size, Ours.PixelFormat.Rgba8);
        _sharp = new SharpRgba(Size, Size);
        _source = new Ours.Rgba32[Size * Size];
        Array.Fill(_source, _white);
        _out = new Ours.Rgba32[Size * Size];
        _sharpSource = new SixLabors.ImageSharp.PixelFormats.Rgba32[Size * Size];
        Array.Fill(_sharpSource, new SixLabors.ImageSharp.PixelFormats.Rgba32(255, 255, 255, 255));
        _sharpOut = new SixLabors.ImageSharp.PixelFormats.Rgba32[Size * Size];
    }

    [GlobalCleanup]
    public void Dispose()
    {
        _canvas?.Dispose();
        _sharp?.Dispose();
        GC.SuppressFinalize(this);
    }

    private static string FindFont(string name)
    {
        string root = System.Reflection.CustomAttributeExtensions.GetCustomAttributes<System.Reflection.AssemblyMetadataAttribute>(typeof(TextBenchmarks).Assembly).First(a => a.Key == "RepoRoot").Value!;
        return Path.Combine(root, "tests", "testdata", "fonts", name);
    }

    [Benchmark(Description = "OneImage: load font (TrueType, 51 KB)")]
    [BenchmarkCategory("load")]
    public object LoadOurs() => new OneImage.Text.FontCollection().Add(_fontBytes);

    [Benchmark(Description = "SixLabors.Fonts: load font (TrueType, 51 KB)")]
    [BenchmarkCategory("load")]
    public object LoadTheirs()
    {
        using var ms = new MemoryStream(_fontBytes);
        return new SixLabors.Fonts.FontCollection().Add(ms, CultureInfo.InvariantCulture);
    }

    [Benchmark(Description = "OneImage: measure paragraph (advance)")]
    public float MeasureAdvanceOurs() => OursMeasurer.MeasureAdvance(Paragraph, _ours).Height;

    [Benchmark(Description = "SixLabors.Fonts: measure paragraph (advance)")]
    public float MeasureAdvanceTheirs() => SharpMeasurer.MeasureAdvance(Paragraph, _theirs).Height;

    [Benchmark(Description = "OneImage: measure paragraph (ink bounds)")]
    public float MeasureBoundsOurs() => OursMeasurer.MeasureBounds(Paragraph, _ours).Height;

    [Benchmark(Description = "SixLabors.Fonts: measure paragraph (ink bounds)")]
    public float MeasureBoundsTheirs() => SharpMeasurer.MeasureBounds(Paragraph, _theirs).Height;

    [Benchmark(Description = "OneImage: paragraph as path")]
    public int PathOurs() => TextBuilder.GeneratePath(Paragraph, _ours).Verbs.Length;

    [Benchmark(Description = "ImageSharp.Drawing: paragraph as path")]
    public int PathTheirs() => SixLabors.ImageSharp.Drawing.TextBuilder.GenerateGlyphs(Paragraph, _theirs).Bounds.GetHashCode();

    [Benchmark(Description = "OneImage: draw paragraph 24 px on 1000x1000 (canvas)")]
    public Ours.Rgba32 DrawOurs()
    {
        _canvas.Clear(_white);
        _canvas.DrawText(Paragraph, _ours, _black);
        return _canvas[500, 100];
    }

    [Benchmark(Description = "ImageSharp.Drawing: draw paragraph 24 px on 1000x1000")]
    public SixLabors.ImageSharp.PixelFormats.Rgba32 DrawTheirs()
    {
        _sharp.Mutate(c => c.Clear(SixLabors.ImageSharp.Color.White).DrawText(_theirs, Paragraph, SixLabors.ImageSharp.Color.Black));
        return _sharp[500, 100];
    }

    [Benchmark(Description = "OneImage: draw paragraph, pixels in and out (image)")]
    public Ours.Rgba32 DrawImageOurs()
    {
        Ours.Image.FromPixels<Ours.Rgba32>(_source, Size, Size).DrawText(Paragraph, _ours, _black).CopyPixelsTo<Ours.Rgba32>(_out, new Ours.ImageRect(0, 0, Size, Size));
        return _out[500 * Size];
    }

    [Benchmark(Description = "ImageSharp.Drawing: draw paragraph, pixels in and out")]
    public SixLabors.ImageSharp.PixelFormats.Rgba32 DrawImageTheirs()
    {
        using SharpRgba img = SixLabors.ImageSharp.Image.LoadPixelData<SixLabors.ImageSharp.PixelFormats.Rgba32>(_sharpSource, Size, Size);
        img.Mutate(c => c.DrawText(_theirs, Paragraph, SixLabors.ImageSharp.Color.Black));
        img.CopyPixelDataTo(_sharpOut);
        return _sharpOut[500 * Size];
    }
}
