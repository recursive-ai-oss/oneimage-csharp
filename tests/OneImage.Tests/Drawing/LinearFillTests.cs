using System.Numerics;
using OneImage.Drawing;
using Xunit;
using IntersectionRule = OneImage.Drawing.IntersectionRule;
using Path = OneImage.Drawing.Path;

namespace OneImage.Tests.Drawing;

/// <summary>Linear-light fills and strokes on 8-bit pixels blend only the covered pixels; they must agree with the whole-tile float conversion path.</summary>
public class LinearFillTests
{
    private const int W = 300, H = 260;

    private static Path Scene() => Path.Combine(
        Path.Circle(150, 120, 90),
        Path.Star(200, 150, 6, 30, 80, 10),
        Path.ParseSvg("M10 200 C 60 120 120 300 290 230 L 270 250 Q 150 280 40 245 Z"));

    private static Image Base(PixelFormat format)
    {
        var px = new Rgba32[W * H];
        for (int y = 0; y < H; y++)
        {
            for (int x = 0; x < W; x++)
            {
                px[(y * W) + x] = new Rgba32((byte)(x * 255 / W), (byte)(y * 255 / H), (byte)((x ^ y) * 3), format.HasAlpha ? (byte)(40 + ((x + y) % 200)) : (byte)255);
            }
        }

        return Image.FromPixels<Rgba32>(px, W, H).ConvertTo(format);
    }

    private static Brush Make(string name) => name switch
    {
        "solid" => Brush.Solid(new Rgba32(220, 90, 30, 255)),
        "translucent" => Brush.Solid(new Rgba32(30, 120, 220, 140)),
        "gradient" => Brush.LinearGradient(new Vector2(30, 20), new Vector2(250, 210), [new(0f, new Rgba32(255, 0, 0, 255)), new(0.5f, new Rgba32(255, 255, 0, 200)), new(1f, new Rgba32(0, 0, 255, 255))]),
        "radial" => Brush.RadialGradient(new Vector2(150, 130), 120, [new(0f, new Rgba32(255, 255, 255, 255)), new(1f, new Rgba32(0, 0, 0, 90))]),
        "hatch" => Brush.Hatch(HatchStyle.Horizontal, new Rgba32(255, 255, 255, 255), new Rgba32(10, 20, 200, 255)),
        _ => throw new ArgumentException(name),
    };

    public static TheoryData<string> Brushes => ["solid", "translucent", "gradient", "radial", "hatch"];

    private static int MaxDiff(Image a, Image b)
    {
        Rgba32[] x = a.ToArray<Rgba32>(), y = b.ToArray<Rgba32>();
        Assert.Equal(x.Length, y.Length);
        int worst = 0;
        for (int i = 0; i < x.Length; i++)
        {
            worst = Math.Max(worst, Math.Max(Math.Max(Math.Abs(x[i].R - y[i].R), Math.Abs(x[i].G - y[i].G)), Math.Max(Math.Abs(x[i].B - y[i].B), Math.Abs(x[i].A - y[i].A))));
        }

        return worst;
    }

    // The float format takes the general path (whole tiles converted to linear float and back).
    private static Image Reference(Image img, Func<Image, Image> draw) => draw(img.ConvertTo(PixelFormat.RgbaF32)).ConvertTo(PixelFormat.Rgba8);

    [Theory]
    [MemberData(nameof(Brushes))]
    public void FillMatchesTheWholeTilePath(string name)
    {
        Image b = Base(PixelFormat.Rgba8);
        Image fast = b.Fill(Make(name), Scene(), linear: true);
        Image slow = Reference(b, i => i.Fill(Make(name), Scene(), linear: true));
        Assert.True(MaxDiff(fast, slow) <= 1, name);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void AllEightBitLayoutsMatch(int which)
    {
        PixelFormat f = new[] { PixelFormat.Rgb8, PixelFormat.Gray8, PixelFormat.GrayAlpha8, PixelFormat.Rgba8 }[which];
        Image b = Base(f);
        foreach (string name in new[] { "translucent", "gradient" })
        {
            Image fast = b.Fill(Make(name), Scene(), linear: true, blend: BlendMode.Multiply);
            Image slow = b.ConvertTo(f.Model == ColorModel.Gray ? new PixelFormat(ColorModel.Gray, SampleType.F32, f.Alpha) : new PixelFormat(ColorModel.Rgb, SampleType.F32, f.Alpha))
                .Fill(Make(name), Scene(), linear: true, blend: BlendMode.Multiply).ConvertTo(f);
            Assert.True(MaxDiff(fast, slow) <= 1, $"{f} {name}");
        }
    }

    [Fact]
    public void StrokeMatches()
    {
        Image b = Base(PixelFormat.Rgba8);
        var pen = new Pen(Brush.Solid(new Rgba32(250, 250, 20, 200)), 9, StrokeJoin.Round);
        Image fast = b.Draw(pen, Scene(), linear: true);
        Image slow = Reference(b, i => i.Draw(pen, Scene(), linear: true));
        Assert.True(MaxDiff(fast, slow) <= 1);
    }

    [Fact]
    public void AliasedSourceOverMatchesDoubleReference()
    {
        // Without anti-aliasing every pixel is fully in or out, so the result is straight source-over in double precision.
        Image b = Base(PixelFormat.Rgba8);
        var color = new Rgba32(200, 40, 120, 128);
        Rgba32[] before = b.ToArray<Rgba32>();
        Rgba32[] after = b.Fill(color, Scene(), antialias: false, linear: true).ToArray<Rgba32>();
        Rgba32[] mask = b.Fill(new Rgba32(255, 255, 255, 255), Scene(), antialias: false).ToArray<Rgba32>();
        int changed = 0;
        for (int i = 0; i < before.Length; i++)
        {
            if (mask[i].Equals(before[i]))
            {
                Assert.Equal(before[i], after[i]);
                continue;
            }

            changed++;
            double sa = color.A / 255.0, ba = before[i].A / 255.0, oa = sa + (ba * (1 - sa));
            Rgba32 e = after[i];
            Assert.InRange(Math.Abs((oa * 255) - e.A), 0, 0.51);
            AssertChannel(Lin(color.R), Lin(before[i].R), sa, ba, oa, e.R);
            AssertChannel(Lin(color.G), Lin(before[i].G), sa, ba, oa, e.G);
            AssertChannel(Lin(color.B), Lin(before[i].B), sa, ba, oa, e.B);
        }

        Assert.True(changed > 1000);
    }

    private static double Lin(byte v)
    {
        double c = v / 255.0;
        return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
    }

    private static void AssertChannel(double s, double b, double sa, double ba, double oa, byte actual)
    {
        double lin = ((s * sa) + (b * ba * (1 - sa))) / oa;
        double enc = lin <= 0.0031308 ? lin * 12.92 : (1.055 * Math.Pow(lin, 1 / 2.4)) - 0.055;
        Assert.InRange(Math.Abs((enc * 255) - actual), 0, 1.0);
    }

    [Theory]
    [MemberData(nameof(Brushes))]
    public void CanvasAndDegreesAgree(string name)
    {
        Image b = Base(PixelFormat.Rgba8);
        Rgba32[] one = b.Fill(Make(name), Scene(), linear: true).ToArray<Rgba32>(maxDegree: 1);
        Rgba32[] eight = b.Fill(Make(name), Scene(), linear: true).ToArray<Rgba32>(maxDegree: 8);
        Assert.True(one.AsSpan().SequenceEqual(eight));

        using Canvas c = b.ToCanvas();
        c.Fill(Make(name), Scene(), linear: true);
        Assert.True(one.AsSpan().SequenceEqual(c.Snapshot().ToArray<Rgba32>()));
    }
}
