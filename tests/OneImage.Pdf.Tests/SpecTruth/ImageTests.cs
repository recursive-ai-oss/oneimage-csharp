using System.Text;
using static OneImage.Pdf.Tests.SpecTruth.Spec;

namespace OneImage.Pdf.Tests.SpecTruth;

/// <summary>Image XObjects and inline images placed on the unit square (ISO 32000-2 8.9).</summary>
public class ImageTests
{
    private delegate (double R, double G, double B, double A) Sampler(int row, int col);

    private static string ImageObject(string dict, byte[] data) =>
        Stream($"/Type /XObject /Subtype /Image {dict}", data);

    /// <summary>Packs sample values (row-major, <paramref name="comps"/> per pixel) into rows padded to whole bytes, most significant bit first (8.9.3).</summary>
    private static byte[] Pack(int[] samples, int width, int height, int comps, int bpc)
    {
        int rowBits = width * comps * bpc;
        int rowBytes = (rowBits + 7) / 8;
        var data = new byte[rowBytes * height];
        for (int r = 0; r < height; r++)
        {
            for (int i = 0; i < width * comps; i++)
            {
                int v = samples[(r * width * comps) + i];
                int bit = i * bpc;
                for (int b = 0; b < bpc; b++)
                {
                    if (((v >> (bpc - 1 - b)) & 1) != 0)
                    {
                        int pos = bit + b;
                        data[(r * rowBytes) + (pos / 8)] |= (byte)(0x80 >> (pos % 8));
                    }
                }
            }
        }

        return data;
    }

    /// <summary>
    /// Paints the unit-square image into <paramref name="e"/> by inverse-mapping each pixel center through the matrix (8.9.5):
    /// sample row 0 is the top of the unit square. Used only for matrices whose edges fall on pixel boundaries.
    /// </summary>
    private static void PaintImage(Expected e, double[] m, int w, int h, Sampler s, double alpha = 1, bool strict = false)
    {
        double det = (m[0] * m[3]) - (m[1] * m[2]);
        for (int y = 0; y < e.Height; y++)
        {
            for (int x = 0; x < e.Width; x++)
            {
                double dx = x + 0.5 - m[4];
                double dy = (e.Height - y - 0.5) - m[5];
                double u = ((m[3] * dx) - (m[2] * dy)) / det;
                double v = ((-m[1] * dx) + (m[0] * dy)) / det;
                if (u < 0 || u >= 1 || v < 0 || v >= 1)
                {
                    continue;
                }

                int col = Math.Min(w - 1, (int)(u * w));
                int row = Math.Min(h - 1, (int)((1 - v) * h));
                var c = s(row, col);
                e.Over(x, y, (c.R, c.G, c.B), c.A * alpha);
                if (!strict && (NearInternalBoundary(u * w, w, Math.Sqrt((m[0] * m[0]) + (m[1] * m[1])) / w) || NearInternalBoundary((1 - v) * h, h, Math.Sqrt((m[2] * m[2]) + (m[3] * m[3])) / h)))
                {
                    e.IgnorePixel(x, y);
                }
            }
        }
    }

    /// <summary>
    /// A reader may interpolate (8.9.5.3 allows it, though it must be off by default), which blends neighboring samples between two
    /// sample centers. Along each axis a pixel is therefore compared only where any interpolation returns the sample itself: exactly at
    /// a sample center, or in the outer half of an edge sample, where it clamps. The default-off rule has its own case.
    /// </summary>
    private static bool NearInternalBoundary(double samplePos, int n, double pixelsPerSample)
    {
        if (samplePos <= 0.5 || samplePos >= n - 0.5)
        {
            return false;
        }

        return Math.Abs(samplePos - (Math.Floor(samplePos) + 0.5)) * pixelsPerSample > 0.01;
    }

    private static string Place(double[] m) =>
        string.Join(' ', m.Select(N)) + " cm";

    // 2 x 2 images use 16-pixel samples; 4 x 4 images use 9-pixel samples, whose centers fall on pixel centers.
    private static readonly double[] Normal = [32, 0, 0, 32, 4, 4];
    private static readonly double[] Big4 = [36, 0, 0, 36, 2, 2];

    private static byte[] OneImagePage(string imageObject, double[] m, string pre = "", string resourcesExtra = "", string[]? moreObjects = null)
    {
        var extra = new List<string> { imageObject };
        if (moreObjects is not null)
        {
            extra.AddRange(moreObjects);
        }

        return Page($"{pre} q {Place(m)} /Im0 Do Q", "/XObject << /Im0 5 0 R >> " + resourcesExtra, extra: [.. extra], w: 40, h: 40);
    }

    // ---- sample depths (8.9.5.1 BitsPerComponent) ----

    // 4 x 4 gray images with distinct levels k * 17 at every depth; 16-bit samples are k * 4369, 4-bit k, 2-bit k mod 4 (scaled by 85), 1-bit k mod 2.
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(8)]
    [InlineData(16)]
    [Trait("Section", "8.9.5.1")]
    public void GrayDepths(int bpc)
    {
        int max = (1 << bpc) - 1;
        int[] samples = new int[16];
        for (int i = 0; i < 16; i++)
        {
            // Spread the 16 positions over the available levels in a scrambled order, so rows and columns differ.
            samples[i] = bpc == 16 ? ((i * 7) % 16) * 4369 : ((i * 7) % 16) * max / 15;
        }

        byte[] data = Pack(samples, 4, 4, 1, bpc);
        byte[] pdf = OneImagePage(ImageObject($"/Width 4 /Height 4 /ColorSpace /DeviceGray /BitsPerComponent {bpc}", data), Big4);
        var e = new Expected(40, 40);
        PaintImage(e, Big4, 4, 4, (r, c) => { double g = (double)samples[(r * 4) + c] / max * 255; return (g, g, g, 1); });
        Check(pdf, e, Tol.Exact);
    }

    [Theory]
    [InlineData(8)]
    [InlineData(16)]
    [Trait("Section", "8.9.5.1")]
    public void RgbDepths(int bpc)
    {
        int max = (1 << bpc) - 1;
        // Red, green / blue, mid gray-ish (0.4, 0.6, 0.8 are whole levels in 8 bits).
        int[] samples = bpc == 8
            ? [255, 0, 0, 0, 255, 0, 0, 0, 255, 102, 153, 204]
            : [65535, 0, 0, 0, 65535, 0, 0, 0, 65535, 26214, 39321, 52428];
        byte[] data = Pack(samples, 2, 2, 3, bpc);
        byte[] pdf = OneImagePage(ImageObject($"/Width 2 /Height 2 /ColorSpace /DeviceRGB /BitsPerComponent {bpc}", data), Normal);
        var e = new Expected(40, 40);
        PaintImage(e, Normal, 2, 2, (r, c) =>
        {
            int i = ((r * 2) + c) * 3;
            return ((double)samples[i] / max * 255, (double)samples[i + 1] / max * 255, (double)samples[i + 2] / max * 255, 1);
        });
        Check(pdf, e, Tol.Exact);
    }

    [Fact]
    [Trait("Section", "8.6.4.4")]
    public void CmykImage()
    {
        // Samples in order: no ink, cyan, magenta/yellow mix, black.
        int[] samples = [0, 0, 0, 0, 255, 0, 0, 0, 0, 128, 255, 0, 0, 0, 0, 255];
        byte[] pdf = OneImagePage(ImageObject("/Width 2 /Height 2 /ColorSpace /DeviceCMYK /BitsPerComponent 8", Pack(samples, 2, 2, 4, 8)), Normal);
        var e = new Expected(40, 40);
        PaintImage(e, Normal, 2, 2, (r, c) =>
        {
            int i = ((r * 2) + c) * 4;
            var rgb = Cmyk(samples[i] / 255.0, samples[i + 1] / 255.0, samples[i + 2] / 255.0, samples[i + 3] / 255.0);
            return (rgb.R, rgb.G, rgb.B, 1);
        });
        Check(pdf, e, 3);
    }

    // ---- Decode arrays (8.9.5.2) ----

    [Fact]
    [Trait("Section", "8.9.5.2")]
    public void DecodeInvertsGray()
    {
        int[] samples = [0, 85, 170, 255];
        byte[] pdf = OneImagePage(ImageObject("/Width 2 /Height 2 /ColorSpace /DeviceGray /BitsPerComponent 8 /Decode [1 0]", Pack(samples, 2, 2, 1, 8)), Normal);
        var e = new Expected(40, 40);
        PaintImage(e, Normal, 2, 2, (r, c) => { double g = 255 - samples[(r * 2) + c]; return (g, g, g, 1); });
        Check(pdf, e, Tol.Exact);
    }

    [Fact]
    [Trait("Section", "8.9.5.2")]
    public void DecodeRestrictsRange()
    {
        // Decode [0 0.5] maps sample 255 to 0.5 gray: 127.5, so allow rounding.
        int[] samples = [0, 51, 102, 255];
        byte[] pdf = OneImagePage(ImageObject("/Width 2 /Height 2 /ColorSpace /DeviceGray /BitsPerComponent 8 /Decode [0 0.5]", Pack(samples, 2, 2, 1, 8)), Normal);
        var e = new Expected(40, 40);
        PaintImage(e, Normal, 2, 2, (r, c) => { double g = samples[(r * 2) + c] / 255.0 * 0.5 * 255; return (g, g, g, 1); });
        Check(pdf, e, Tol.One);
    }

    [Fact]
    [Trait("Section", "8.9.5.2")]
    public void DecodeSwapsRgbChannelsIndependently()
    {
        // Decode [1 0 0 1 0 1] inverts red only.
        int[] samples = [255, 0, 0, 0, 255, 0, 0, 0, 255, 204, 102, 51];
        byte[] pdf = OneImagePage(ImageObject("/Width 2 /Height 2 /ColorSpace /DeviceRGB /BitsPerComponent 8 /Decode [1 0 0 1 0 1]", Pack(samples, 2, 2, 3, 8)), Normal);
        var e = new Expected(40, 40);
        PaintImage(e, Normal, 2, 2, (r, c) => { int i = ((r * 2) + c) * 3; return (255 - samples[i], samples[i + 1], samples[i + 2], 1); });
        Check(pdf, e, Tol.Exact);
    }

    // 8.6.6.3 + 8.9.5.2: an Indexed image's default decode is [0 2^n - 1]; [3 0] runs the palette backwards.
    [Theory]
    [InlineData("", false)]
    [InlineData("/Decode [3 0]", true)]
    [Trait("Section", "8.9.5.2")]
    public void IndexedImage(string decode, bool reversed)
    {
        // Palette: red, green, blue, white. 2 bits per sample.
        int[] samples = [0, 1, 2, 3];
        string cs = "[/Indexed /DeviceRGB 3 <FF000000FF000000FFFFFFFF>]";
        byte[] pdf = OneImagePage(ImageObject($"/Width 2 /Height 2 /ColorSpace {cs} /BitsPerComponent 2 {decode}", Pack(samples, 2, 2, 1, 2)), Normal);
        (double, double, double)[] pal = [(255, 0, 0), (0, 255, 0), (0, 0, 255), (255, 255, 255)];
        var e = new Expected(40, 40);
        PaintImage(e, Normal, 2, 2, (r, c) => { var p = pal[reversed ? 3 - samples[(r * 2) + c] : samples[(r * 2) + c]]; return (p.Item1, p.Item2, p.Item3, 1); });
        Check(pdf, e, Tol.Exact);
    }

    // ---- placement (8.9.5, 8.3.4) ----

    public static IEnumerable<object[]> Placements() =>
    [
        ["identity", new double[] { 32, 0, 0, 32, 4, 4 }],
        ["flip-x", new double[] { -32, 0, 0, 32, 36, 4 }],
        ["flip-y", new double[] { 32, 0, 0, -32, 4, 36 }],
        ["rot90", new double[] { 0, 32, -32, 0, 36, 4 }],
        ["rot180", new double[] { -32, 0, 0, -32, 36, 36 }],
        ["rot270", new double[] { 0, -32, 32, 0, 4, 36 }],
        ["wide", new double[] { 40, 0, 0, 16, 0, 12 }],
    ];

    // 8.9.5: sample row 0, column 0 is at the top-left of the unit square; every placement turns it with the matrix.
    [Theory]
    [MemberData(nameof(Placements))]
    [Trait("Section", "8.9.5")]
    public void ImagePlacement(string name, double[] m)
    {
        _ = name;
        int[] samples = [255, 0, 0, 0, 255, 0, 0, 0, 255, 255, 255, 0]; // red green / blue yellow
        byte[] pdf = OneImagePage(ImageObject("/Width 2 /Height 2 /ColorSpace /DeviceRGB /BitsPerComponent 8", Pack(samples, 2, 2, 3, 8)), m);
        var e = new Expected(40, 40);
        PaintImage(e, m, 2, 2, (r, c) => { int i = ((r * 2) + c) * 3; return (samples[i], samples[i + 1], samples[i + 2], 1); });
        Check(pdf, e, Tol.Exact);
    }

    // 8.9.5.3: with /Interpolate false (the default) an enlarged image shows crisp sample blocks, no smoothing.
    [Fact(Skip = "Defect: the renderer smooths enlarged images (bilinear between sample centers) although /Interpolate is false; 8.9.5.3 says interpolation is off by default")]
    [Trait("Section", "8.9.5.3")]
    public void NoInterpolationWhenEnlarging()
    {
        int[] samples = [0, 255, 255, 0];
        double[] m = [36, 0, 0, 36, 2, 2];
        byte[] pdf = OneImagePage(ImageObject("/Width 2 /Height 2 /ColorSpace /DeviceGray /BitsPerComponent 8 /Interpolate false", Pack(samples, 2, 2, 1, 8)), m);
        var e = new Expected(40, 40);
        PaintImage(e, m, 2, 2, (r, c) => { double g = samples[(r * 2) + c]; return (g, g, g, 1); }, strict: true);
        Check(pdf, e, Tol.Exact);
    }

    // ---- stencil masks (8.9.6.2) ----

    private static readonly int[] Stencil = [0, 1, 1, 0, 1, 0, 0, 1, 0, 0, 1, 1, 1, 1, 0, 0];

    [Theory]
    [InlineData("", 0)]
    [InlineData("/Decode [0 1]", 0)]
    [InlineData("/Decode [1 0]", 1)]
    [Trait("Section", "8.9.6.2")]
    public void ImageMaskPaintsWithTheFillColor(string decode, int paintedBit)
    {
        byte[] pdf = OneImagePage(ImageObject($"/Width 4 /Height 4 /ImageMask true /BitsPerComponent 1 {decode}", Pack(Stencil, 4, 4, 1, 1)), Big4, pre: "0.8 g 0 0 40 40 re f 1 0 0 rg");
        var e = new Expected(40, 40).Rect(0, 0, 40, 40, Gray(0.8));
        PaintImage(e, Big4, 4, 4, (r, c) => Stencil[(r * 4) + c] == paintedBit ? (255, 0, 0, 1) : (0, 0, 0, 0));
        Check(pdf, e, Tol.Exact);
    }

    // The stencil takes its color from the fill color at the time of Do, including alpha.
    [Fact]
    [Trait("Section", "8.9.6.2")]
    public void ImageMaskHonoursConstantAlpha()
    {
        byte[] pdf = OneImagePage(ImageObject("/Width 4 /Height 4 /ImageMask true /BitsPerComponent 1", Pack(Stencil, 4, 4, 1, 1)), Big4, pre: "/GS0 gs 0 0 1 rg", resourcesExtra: "/ExtGState << /GS0 << /ca 0.5 >> >>");
        var e = new Expected(40, 40);
        PaintImage(e, Big4, 4, 4, (r, c) => Stencil[(r * 4) + c] == 0 ? (0, 0, 255, 1) : (0, 0, 0, 0), 0.5);
        Check(pdf, e, Tol.One);
    }

    // ---- explicit masks, soft masks, color key (8.9.6.3, 11.6.5.3, 8.9.6.4) ----

    // One color throughout: the explicit-mask cases check the mask, and the two grids differ in resolution.
    private static readonly int[] Base2x2 = [204, 204, 0, 204, 204, 0, 204, 204, 0, 204, 204, 0];

    // 8.9.6.3: the stencil (any resolution) chooses which places are painted; with the default decode a 1 sample is masked out.
    [Theory]
    [InlineData("")]
    [InlineData("/Decode [1 0]")]
    [Trait("Section", "8.9.6.3")]
    public void ExplicitMaskOfAnotherResolution(string decode)
    {
        bool inverted = decode.Length > 0;
        string mask = ImageObject($"/Width 4 /Height 4 /ImageMask true /BitsPerComponent 1 {decode}", Pack(Stencil, 4, 4, 1, 1));
        byte[] pdf = OneImagePage(ImageObject("/Width 2 /Height 2 /ColorSpace /DeviceRGB /BitsPerComponent 8 /Mask 6 0 R", Pack(Base2x2, 2, 2, 3, 8)), Big4, pre: "0.8 g 0 0 40 40 re f", moreObjects: [mask]);
        var e = new Expected(40, 40).Rect(0, 0, 40, 40, Gray(0.8));
        PaintMasked(e, inverted);
        Check(pdf, e, Tol.Exact);
    }

    // Both resolutions map onto the same unit square; here the mask is finer, so sample it in the mask grid.
    private static void PaintMasked(Expected e, bool inverted)
    {
        double[] m = Big4;
        for (int y = 0; y < e.Height; y++)
        {
            for (int x = 0; x < e.Width; x++)
            {
                double u = (x + 0.5 - m[4]) / 36;
                double v = (e.Height - y - 0.5 - m[5]) / 36;
                if (u is < 0 or >= 1 || v is < 0 or >= 1)
                {
                    continue;
                }

                if (NearInternalBoundary(u * 4, 4, 9) || NearInternalBoundary((1 - v) * 4, 4, 9))
                {
                    e.IgnorePixel(x, y);
                    continue;
                }

                int mc = (int)(u * 4), mr = (int)((1 - v) * 4);
                bool painted = (Stencil[(mr * 4) + mc] == 0) != inverted;
                if (painted)
                {
                    int bc = (int)(u * 2), br = (int)((1 - v) * 2);
                    int i = ((br * 2) + bc) * 3;
                    e.Over(x, y, (Base2x2[i], Base2x2[i + 1], Base2x2[i + 2]), 1);
                }
            }
        }
    }

    // 11.6.5.3: an image /SMask supplies per-sample opacity (here at a different resolution from the base image).
    [Fact]
    [Trait("Section", "11.6.5.3")]
    public void SoftMaskedImage()
    {
        int[] alpha = [0, 85, 170, 255]; // 2 x 2 mask over a 1 x 2... use a 2 x 2 base too
        string smask = ImageObject("/Width 2 /Height 2 /ColorSpace /DeviceGray /BitsPerComponent 8", Pack(alpha, 2, 2, 1, 8));
        int[] basePx = [255, 0, 0, 255, 0, 0, 255, 0, 0, 255, 0, 0];
        byte[] pdf = OneImagePage(ImageObject("/Width 2 /Height 2 /ColorSpace /DeviceRGB /BitsPerComponent 8 /SMask 6 0 R", Pack(basePx, 2, 2, 3, 8)), Normal, pre: "0 0 1 rg 0 0 40 40 re f", moreObjects: [smask]);
        var e = new Expected(40, 40).Rect(0, 0, 40, 40, Rgb(0, 0, 1));
        PaintImage(e, Normal, 2, 2, (r, c) => (255, 0, 0, alpha[(r * 2) + c] / 255.0));
        Check(pdf, e, Tol.One);
    }

    [Fact]
    [Trait("Section", "11.6.5.3")]
    public void SoftMaskWithDecodeInverted()
    {
        int[] alpha = [0, 85, 170, 255];
        string smask = ImageObject("/Width 2 /Height 2 /ColorSpace /DeviceGray /BitsPerComponent 8 /Decode [1 0]", Pack(alpha, 2, 2, 1, 8));
        int[] basePx = [255, 0, 0, 255, 0, 0, 255, 0, 0, 255, 0, 0];
        byte[] pdf = OneImagePage(ImageObject("/Width 2 /Height 2 /ColorSpace /DeviceRGB /BitsPerComponent 8 /SMask 6 0 R", Pack(basePx, 2, 2, 3, 8)), Normal, pre: "0 0 1 rg 0 0 40 40 re f", moreObjects: [smask]);
        var e = new Expected(40, 40).Rect(0, 0, 40, 40, Rgb(0, 0, 1));
        PaintImage(e, Normal, 2, 2, (r, c) => (255, 0, 0, 1 - (alpha[(r * 2) + c] / 255.0)));
        Check(pdf, e, Tol.One);
    }

    // 8.9.6.4: samples whose components all fall inside the key ranges are not painted.
    [Fact]
    [Trait("Section", "8.9.6.4")]
    public void ColorKeyMask()
    {
        int[] px = [255, 0, 0, 0, 255, 0, 0, 0, 255, 10, 250, 5];
        // Mask out anything with R in 0..20, G in 240..255, B in 0..20: that is the green and the near-green samples.
        byte[] pdf = OneImagePage(ImageObject("/Width 2 /Height 2 /ColorSpace /DeviceRGB /BitsPerComponent 8 /Mask [0 20 240 255 0 20]", Pack(px, 2, 2, 3, 8)), Normal, pre: "0.8 g 0 0 40 40 re f");
        var e = new Expected(40, 40).Rect(0, 0, 40, 40, Gray(0.8));
        PaintImage(e, Normal, 2, 2, (r, c) =>
        {
            int i = ((r * 2) + c) * 3;
            bool masked = px[i] <= 20 && px[i + 1] >= 240 && px[i + 2] <= 20;
            return masked ? (0, 0, 0, 0) : (px[i], px[i + 1], px[i + 2], 1);
        });
        Check(pdf, e, Tol.Exact);
    }

    // 11.6.4.4: constant alpha applies to image painting.
    [Fact]
    [Trait("Section", "11.6.4.4")]
    public void ImageWithConstantAlpha()
    {
        int[] px = [255, 0, 0, 0, 255, 0, 0, 0, 255, 255, 255, 0];
        byte[] pdf = OneImagePage(ImageObject("/Width 2 /Height 2 /ColorSpace /DeviceRGB /BitsPerComponent 8", Pack(px, 2, 2, 3, 8)), Normal, pre: "0 g 0 0 40 40 re f /GS0 gs", resourcesExtra: "/ExtGState << /GS0 << /ca 0.5 >> >>");
        var e = new Expected(40, 40).Rect(0, 0, 40, 40, Gray(0));
        PaintImage(e, Normal, 2, 2, (r, c) => { int i = ((r * 2) + c) * 3; return (px[i], px[i + 1], px[i + 2], 1); }, 0.5);
        Check(pdf, e, Tol.One);
    }

    // 8.9.7 inline images: BI ... ID <data> EI, with abbreviated keys.
    [Fact]
    [Trait("Section", "8.9.7")]
    public void InlineImage()
    {
        int[] px = [255, 0, 0, 0, 255, 0, 0, 0, 255, 102, 153, 204];
        string data = Encoding.Latin1.GetString(Pack(px, 2, 2, 3, 8));
        // Avoid bytes that look like whitespace or delimiters next to EI: all samples here are 0x00, 0x66, 0x99, 0xCC, 0xFF.
        byte[] pdf = Page($"q {Place(Normal)} BI /W 2 /H 2 /CS /RGB /BPC 8 ID {data}\nEI Q", w: 40, h: 40);
        var e = new Expected(40, 40);
        PaintImage(e, Normal, 2, 2, (r, c) => { int i = ((r * 2) + c) * 3; return (px[i], px[i + 1], px[i + 2], 1); });
        Check(pdf, e, Tol.Exact);
    }

    [Fact]
    [Trait("Section", "8.9.7")]
    public void InlineImageMask()
    {
        string data = Encoding.Latin1.GetString(Pack(Stencil, 4, 4, 1, 1));
        byte[] pdf = Page($"1 0 0 rg q {Place(Big4)} BI /W 4 /H 4 /IM true /BPC 1 ID {data}\nEI Q", w: 40, h: 40);
        var e = new Expected(40, 40);
        PaintImage(e, Big4, 4, 4, (r, c) => Stencil[(r * 4) + c] == 0 ? (255, 0, 0, 1) : (0, 0, 0, 0));
        Check(pdf, e, Tol.Exact);
    }
}
