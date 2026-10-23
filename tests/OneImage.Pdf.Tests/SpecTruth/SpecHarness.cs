using System.Globalization;
using System.Text;

namespace OneImage.Pdf.Tests.SpecTruth;

/// <summary>
/// Tolerances used by the spec-truth cases. <see cref="Exact"/> compares against integer expectations;
/// <see cref="One"/> allows one level where the specification leaves rounding open; <see cref="Edge"/> is for
/// pixels partly covered by a shape, where only the covered area is specified (anti-aliasing is implementation-defined).
/// </summary>
internal static class Tol
{
    public const double Exact = 0;
    public const double One = 1;
    /// <summary>Partly covered pixels: 3 levels is about 1.2% of the coverage fraction of a full-contrast edge.</summary>
    public const double Edge = 3;
}

/// <summary>An expected raster at 72 dpi (one pixel per point), built from the page's analytic geometry. Colors are 0..255 floats, opaque.</summary>
internal sealed class Expected
{
    private readonly float[] _px;
    private readonly bool[] _skip;

    public Expected(int width, int height, double gray = 255)
    {
        Width = width;
        Height = height;
        _px = new float[width * height * 3];
        _skip = new bool[width * height];
        Array.Fill(_px, (float)gray);
    }

    public int Width { get; }

    public int Height { get; }

    public (double R, double G, double B) this[int x, int y]
    {
        get
        {
            int i = ((y * Width) + x) * 3;
            return (_px[i], _px[i + 1], _px[i + 2]);
        }
        set
        {
            int i = ((y * Width) + x) * 3;
            _px[i] = (float)value.Item1;
            _px[i + 1] = (float)value.Item2;
            _px[i + 2] = (float)value.Item3;
        }
    }

    /// <summary>
    /// Paints an axis-aligned rectangle given in PDF user space (origin bottom-left, y up). Each pixel takes the
    /// fraction of its area inside the rectangle times <paramref name="alpha"/>, composited source-over in device space (ISO 32000-2 11.3.3).
    /// </summary>
    public Expected Rect(double x0, double y0, double x1, double y1, (double R, double G, double B) c, double alpha = 1)
    {
        for (int py = 0; py < Height; py++)
        {
            double cy = Overlap(Height - py - 1, Height - py, y0, y1);
            if (cy <= 0)
            {
                continue;
            }

            for (int px = 0; px < Width; px++)
            {
                double cov = Overlap(px, px + 1, x0, x1) * cy;
                if (cov > 0)
                {
                    Over(px, py, c, cov * alpha);
                }
            }
        }

        return this;
    }

    /// <summary>Marks pixels where the specification leaves the result open (for example along a discontinuity); the comparison skips them.</summary>
    public Expected Ignore(Func<int, int, bool> where)
    {
        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                if (where(x, y))
                {
                    _skip[(y * Width) + x] = true;
                }
            }
        }

        return this;
    }

    public void IgnorePixel(int x, int y) => _skip[(y * Width) + x] = true;

    public bool Skipped(int x, int y) => _skip[(y * Width) + x];

    /// <summary>Source-over of a color with a given fractional opacity onto one pixel.</summary>
    public void Over(int px, int py, (double R, double G, double B) c, double a)
    {
        var o = this[px, py];
        this[px, py] = ((o.R * (1 - a)) + (c.R * a), (o.G * (1 - a)) + (c.G * a), (o.B * (1 - a)) + (c.B * a));
    }

    /// <summary>Replaces every pixel with <paramref name="f"/>(x, y), where y counts from the top of the image.</summary>
    public Expected Fill(Func<int, int, (double R, double G, double B)> f)
    {
        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                this[x, y] = f(x, y);
            }
        }

        return this;
    }

    /// <summary>Paints every pixel whose center satisfies <paramref name="inside"/> (user space coordinates of the center) with <paramref name="c"/>.</summary>
    public Expected Where(Func<double, double, bool> inside, (double R, double G, double B) c, double alpha = 1)
    {
        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                if (inside(x + 0.5, Height - y - 0.5))
                {
                    Over(x, y, c, alpha);
                }
            }
        }

        return this;
    }

    private static double Overlap(double a0, double a1, double b0, double b1) => Math.Max(0, Math.Min(a1, b1) - Math.Max(a0, b0));
}

internal static class Spec
{
    public static (double R, double G, double B) Gray(double g) => (g * 255, g * 255, g * 255);

    public static (double R, double G, double B) Rgb(double r, double g, double b) => (r * 255, g * 255, b * 255);

    public static (double R, double G, double B) Rgb8(int r, int g, int b) => (r, g, b);

    /// <summary>The library's documented DeviceCMYK conversion (generic CMYK profile, relative colorimetric with black point compensation), through the public color API.</summary>
    public static (double R, double G, double B) Cmyk(double c, double m, double y, double k)
    {
        var src = new Cmyk32[] { new((byte)Math.Round(c * 255), (byte)Math.Round(m * 255), (byte)Math.Round(y * 255), (byte)Math.Round(k * 255)) };
        Image img = Image.FromPixels<Cmyk32>(src, 1, 1).ConvertColor(ColorProfile.Srgb);
        Rgba32 p = img.ToArray<Rgba32>()[0];
        return (p.R, p.G, p.B);
    }

    public static string N(double v) => v.ToString("0.######", CultureInfo.InvariantCulture);

    /// <summary>A one-page PDF. Extra objects are numbered from 5 (1 catalog, 2 pages, 3 page, 4 content).</summary>
    public static byte[] Page(string content, string resources = "", double w = 20, double h = 20, string[]? extra = null, string pageExtra = "") =>
        TestPdf.Page(content, resources, w, h, extra, pageExtra);

    /// <summary>A one-page PDF whose page dictionary carries exactly the given box and rotation entries (no default MediaBox).</summary>
    public static byte[] Boxes(string boxes, string content, string resources = "") => Doc(
        "<< /Type /Catalog /Pages 2 0 R >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        $"<< /Type /Page /Parent 2 0 R {boxes} /Contents 4 0 R /Resources << {resources} >> >>",
        Stream("", content));

    /// <summary>A stream object body.</summary>
    public static string Stream(string dict, string data) =>
        "<< " + dict + " /Length " + data.Length.ToString(CultureInfo.InvariantCulture) + " >>\nstream\n" + data + "\nendstream";

    /// <summary>A stream object whose data is raw bytes (Latin-1 mapped), for image samples.</summary>
    public static string Stream(string dict, byte[] data) =>
        "<< " + dict + " /Length " + data.Length.ToString(CultureInfo.InvariantCulture) + " >>\nstream\n" + Encoding.Latin1.GetString(data) + "\nendstream";

    /// <summary>
    /// Builds a document from raw object bodies (object 1 is the first). Streams passed as strings are written Latin-1, so binary samples survive.
    /// </summary>
    public static byte[] Doc(params string[] objects) => TestPdf.Build(objects.Select(TestPdf.Ascii).ToList());

    public static Rgba32[] Pix(byte[] pdf, PdfRenderOptions? options = null)
    {
        Image img = RenderTests.Load(pdf, options ?? new PdfRenderOptions { Dpi = 72 });
        return img.ToArray<Rgba32>();
    }

    public static Image RenderImage(byte[] pdf, PdfRenderOptions? options = null)
    {
        // Debugging aid: set ONEIMAGE_SPEC_TRUTH_DUMP to a directory to keep every generated PDF (named by content hash) for inspection in other viewers.
        string? dump = Environment.GetEnvironmentVariable("ONEIMAGE_SPEC_TRUTH_DUMP");
        if (!string.IsNullOrEmpty(dump))
        {
            Directory.CreateDirectory(dump);
            string name = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(pdf))[..12].ToLowerInvariant();
            File.WriteAllBytes(Path.Combine(dump, name + ".pdf"), pdf);
            File.AppendAllText(Path.Combine(dump, "index.txt"), name + " " + TestContext.Current.TestCase?.TestCaseDisplayName + "\n");
        }

        return RenderTests.Load(pdf, options ?? new PdfRenderOptions { Dpi = 72 });
    }

    /// <summary>Compares a rendered page with an expected raster; fails with the first differing pixels, which are listed with their row from the top.</summary>
    public static void Check(byte[] pdf, Expected e, double tol, PdfRenderOptions? options = null)
    {
        Image img = RenderImage(pdf, options);
        Assert.Equal(e.Width, img.Width);
        Assert.Equal(e.Height, img.Height);
        Check(img.ToArray<Rgba32>(), e, tol);
    }

    public static void Check(Rgba32[] actual, Expected e, double tol)
    {
        var bad = new StringBuilder();
        int count = 0;
        double worst = 0;
        for (int y = 0; y < e.Height; y++)
        {
            for (int x = 0; x < e.Width; x++)
            {
                if (e.Skipped(x, y))
                {
                    continue;
                }

                Rgba32 a = actual[(y * e.Width) + x];
                var x0 = e[x, y];
                // Exact cases are compared with the expectation rounded to a level; others against the real value.
                double r = tol == 0 ? Math.Floor(x0.R + 0.5) : x0.R;
                double g = tol == 0 ? Math.Floor(x0.G + 0.5) : x0.G;
                double b = tol == 0 ? Math.Floor(x0.B + 0.5) : x0.B;
                double d = Math.Max(Math.Abs(a.R - r), Math.Max(Math.Abs(a.G - g), Math.Abs(a.B - b)));
                if (a.A != 255)
                {
                    d = Math.Max(d, 255 - a.A);
                }

                if (d > tol + 1e-9)
                {
                    count++;
                    worst = Math.Max(worst, d);
                    if (count <= 6)
                    {
                        bad.Append(CultureInfo.InvariantCulture, $"\n  ({x},{y}) expected ({x0.R:0.#},{x0.G:0.#},{x0.B:0.#}) got ({a.R},{a.G},{a.B},{a.A})");
                    }
                }
            }
        }

        Assert.True(count == 0, $"{count} pixel(s) differ by more than {tol} (worst {worst:0.#}):{bad}");
    }

    /// <summary>Checks one pixel (x from the left, y from the top) against a color.</summary>
    public static void Pixel(Rgba32[] actual, int width, int x, int y, (double R, double G, double B) c, double tol)
    {
        Rgba32 a = actual[(y * width) + x];
        double d = Math.Max(Math.Abs(a.R - c.R), Math.Max(Math.Abs(a.G - c.G), Math.Abs(a.B - c.B)));
        Assert.True(d <= tol + 1e-9, $"pixel ({x},{y}) expected ({c.R:0.#},{c.G:0.#},{c.B:0.#}) got ({a.R},{a.G},{a.B})");
    }

    /// <summary>Sum of (255 - luminance-channel) darkness over a region as an area in pixels, for anti-aliased shapes: the covered area of a black shape on white.</summary>
    public static double BlackArea(Rgba32[] actual, int width, int x0, int y0, int x1, int y1)
    {
        double sum = 0;
        for (int y = y0; y < y1; y++)
        {
            for (int x = x0; x < x1; x++)
            {
                sum += (255 - actual[(y * width) + x].G) / 255.0;
            }
        }

        return sum;
    }
}
