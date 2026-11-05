using System;
using System.Globalization;
using System.Text;
using OneImage;
using OneImage.Pdf;
using OneImage.Svg;

// Runs on the Mono WebAssembly runtime in a browser. Each step reports its own failure, so one run lists everything that broke.
// The first step is the path that once threw TypeLoadException for the deterministic power function: page 1 of an image-only
// (scanned-style) PDF rendered to RGBA.
int failures = 0;

void Step(string name, Func<bool> body)
{
    try
    {
        bool ok = body();
        Console.WriteLine($"{(ok ? "ok  " : "FAIL")} {name}");
        if (!ok)
        {
            failures++;
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"FAIL {name}: {ex.GetType().Name}: {ex.Message}");
        failures++;
    }
}

Step("pdf: scanned page to RGBA", () =>
{
    Image page = Image.Load(ScannedPdf(), new LoadOptions { Configuration = new ImageConfiguration().Add(new PdfFormat(new PdfRenderOptions { Dpi = 72 })) });
    Rgba32[] px = page.ToArray<Rgba32>();
    // The 2x2 image covers the page; its top-left sample is pure red.
    return page.Width == 40 && page.Height == 40 && px[(5 * 40) + 5] == new Rgba32(255, 0, 0, 255);
});

Step("pdf: A4 scan, gray JPEG", () =>
{
    // A page-sized grayscale scan stored as a JPEG, drawn over the whole page at the default 150 dpi.
    const int w = 600, h = 800;
    var gray = new Gray8[w * h];
    for (int i = 0; i < gray.Length; i++)
    {
        gray[i] = new Gray8((byte)(((i % w) / 4) + ((i / w) / 4)));
    }

    using var ms = new System.IO.MemoryStream();
    Image.FromPixels<Gray8>(gray, w, h).Save(ms, JpegFormat.Instance);
    byte[] jpeg = ms.ToArray();
    string image = $"<< /Type /XObject /Subtype /Image /Width {w} /Height {h} /ColorSpace /DeviceGray /BitsPerComponent 8 /Filter /DCTDecode /Length {jpeg.Length} >>\nstream\n"
        + Encoding.Latin1.GetString(jpeg) + "\nendstream";
    byte[] pdf = BuildPdf("q 612 0 0 792 0 0 cm /Im0 Do Q", "/XObject << /Im0 5 0 R >>", 612, 792, [image]);
    Image page = Image.Load(pdf, new LoadOptions { Configuration = new ImageConfiguration().Add(new PdfFormat()) });
    Rgba32[] px = page.ToArray<Rgba32>();
    return page.Width == 1275 && page.Height == 1650 && px[(800 * 1275) + 600].R == px[(800 * 1275) + 600].B;
});

Step("pdf: 1-bit stencil scan", () =>
{
    // A bilevel scan stored as an image mask painted in the current fill color.
    var bits = new byte[(64 / 8) * 64];
    for (int i = 0; i < bits.Length; i++)
    {
        bits[i] = (byte)(i % 3 == 0 ? 0xF0 : 0x0F);
    }

    string image = "<< /Type /XObject /Subtype /Image /Width 64 /Height 64 /ImageMask true /BitsPerComponent 1 /Length " + bits.Length + " >>\nstream\n"
        + Encoding.Latin1.GetString(bits) + "\nendstream";
    byte[] pdf = BuildPdf("0.1 0.1 0.1 rg q 64 0 0 64 0 0 cm /Im0 Do Q", "/XObject << /Im0 5 0 R >>", 64, 64, [image]);
    Image page = Image.Load(pdf, new LoadOptions { Configuration = new ImageConfiguration().Add(new PdfFormat(new PdfRenderOptions { Dpi = 72 })) });
    return page.ToArray<Rgba32>().Length == 64 * 64;
});

// Frames of one megapixel or more take the multi-threaded decode paths on a normal host; the browser runtime has one thread.
foreach (IImageFormat format in new IImageFormat[] { PngFormat.Instance, JpegFormat.Instance, WebPFormat.Instance, TiffFormat.Instance })
{
    IImageFormat f = format;
    Step($"codec: {f.Name} round trip of a 1.2 MP image", () =>
    {
        const int w = 1200, h = 1000;
        var px = new Rgba32[w * h];
        for (int i = 0; i < px.Length; i++)
        {
            px[i] = new Rgba32((byte)((i % w) / 5), (byte)((i / w) / 4), (byte)(i % 251), 255);
        }

        using var ms = new System.IO.MemoryStream();
        Image.FromPixels<Rgba32>(px, w, h).Save(ms, f);
        Image back = Image.Load(ms.ToArray());
        Rgba32[] outPx = back.ToArray<Rgba32>();
        Rgba32[] part = back.Crop(100, 100, 300, 300).Resize(width: 150).ToArray<Rgba32>();
        return back.Width == w && back.Height == h && outPx.Length == px.Length && part.Length == 150 * 150;
    });
}

// Frames of 16 MiB or more stream through a sliding window of tile rows with a decoder running beside the reader, and a color
// conversion on the decode converts rows as they arrive; the browser runtime has one thread, so none of that may wait on another thread.
foreach (IImageFormat format in new IImageFormat[] { PngFormat.Instance, JpegFormat.Instance })
{
    IImageFormat f = format;
    byte[]? encoded = null;

    byte[] Large()
    {
        if (encoded is not null)
        {
            return encoded;
        }

        const int w = 2500, h = 2000; // 20 MB as Rgba8
        var px = new Rgba32[w * h];
        for (int i = 0; i < px.Length; i++)
        {
            px[i] = new Rgba32((byte)((i % w) / 10), (byte)((i / w) / 8), (byte)(((i % w) + (i / w)) / 16), 255);
        }

        using var ms = new System.IO.MemoryStream();
        Image.FromPixels<Rgba32>(px, w, h).Save(ms, f);
        return encoded = ms.ToArray();
    }

    Step($"large {f.Name}: whole-frame read", () =>
    {
        Image img = Image.Load(Large());
        Rgba32[] all = img.ToArray<Rgba32>();
        return img.Width == 2500 && img.Height == 2000 && all.Length == 5_000_000 && all[(1000 * 2500) + 1250].G > 100;
    });

    Step($"large {f.Name}: region reads", () =>
    {
        Image img = Image.Load(Large());
        var region = new Rgba32[200 * 200];
        img.CopyPixelsTo<Rgba32>(region, new ImageRect(1000, 900, 200, 200));
        var top = new Rgba32[100 * 100];
        img.CopyPixelsTo<Rgba32>(top, new ImageRect(0, 0, 100, 100));
        var again = new Rgba32[200 * 200];
        img.CopyPixelsTo<Rgba32>(again, new ImageRect(1000, 900, 200, 200));
        return region.AsSpan().SequenceEqual(again) && region[(100 * 200) + 100].G > 80;
    });

    Step($"large {f.Name}: crop, resize and save", () =>
    {
        Image img = Image.Load(Large());
        using var ms = new System.IO.MemoryStream();
        img.Crop(200, 200, 2000, 1500).Resize(width: 400).Save(ms, PngFormat.Instance);
        using var thumb = new System.IO.MemoryStream();
        Image.Load(Large()).Resize(width: 300).Save(thumb, PngFormat.Instance);
        return Image.Identify(ms.ToArray()).Width == 400 && Image.Identify(thumb.ToArray()).Width == 300;
    });

    Step($"large {f.Name}: color conversion on the decode", () =>
    {
        Image img = Image.Load(Large());
        Rgba32[] p3 = img.ConvertColor(ColorProfile.DisplayP3).ToArray<Rgba32>();
        Gray8[] gray = Image.Load(Large()).ConvertTo(PixelFormat.Gray8).ToArray<Gray8>();
        return p3.Length == 5_000_000 && gray.Length == 5_000_000;
    });
}

Step("large frame: quantize", () =>
{
    var px = new Rgba32[2500 * 2000];
    for (int i = 0; i < px.Length; i++)
    {
        px[i] = new Rgba32((byte)((i % 2500) / 10), (byte)((i / 2500) / 8), 40, 255);
    }

    Rgba32[] q = Image.FromPixels<Rgba32>(px, 2500, 2000).Quantize(16).ToArray<Rgba32>();
    return q.Length == px.Length;
});

Step("pdf: calibrated gray fill", () =>
{
    string content = "/CS0 cs 0.5 sc 0 0 20 20 re f";
    byte[] pdf = BuildPdf(content, "/ColorSpace << /CS0 [/CalGray << /WhitePoint [0.9505 1 1.089] /Gamma 2.2 >>] >>", 20, 20, []);
    Image page = Image.Load(pdf, new LoadOptions { Configuration = new ImageConfiguration().Add(new PdfFormat(new PdfRenderOptions { Dpi = 72 })) });
    Rgba32 p = page.ToArray<Rgba32>()[(10 * 20) + 10];
    return p.R == p.G && p.R > 90 && p.R < 200;
});

Step("color: sRGB to Display P3 and tone curve", () =>
{
    var rgba = new Rgba32[32 * 32];
    for (int i = 0; i < rgba.Length; i++)
    {
        rgba[i] = new Rgba32((byte)(i * 7), (byte)(i * 3), (byte)i, 255);
    }

    Image img = Image.FromPixels<Rgba32>(rgba, 32, 32);
    Rgba32[] p3 = img.ConvertColor(ColorProfile.DisplayP3).ToArray<Rgba32>();
    Rgba32[] toned = img.Gamma(1.8).Exposure(0.5).ToArray<Rgba32>();
    Rgba32[] resized = img.Resize(width: 12).ToArray<Rgba32>();
    Rgba32[] lab = img.ConvertTo(PixelFormat.LabF32).ConvertTo(PixelFormat.Rgba8).ToArray<Rgba32>();
    return p3.Length == 1024 && toned.Length == 1024 && resized.Length == 144 && lab.Length == 1024 && toned[500] != rgba[500];
});

Step("svg: filter", () =>
{
    byte[] svg = Encoding.UTF8.GetBytes(
        "<svg xmlns='http://www.w3.org/2000/svg' width='40' height='40'><defs><filter id='f'><feGaussianBlur stdDeviation='2'/></filter>"
        + "<filter id='g'><feColorMatrix type='saturate' values='0.3'/></filter></defs>"
        + "<rect x='8' y='8' width='24' height='24' fill='#c03020' filter='url(#f)'/><circle cx='20' cy='20' r='6' fill='#2060c0' filter='url(#g)'/></svg>");
    Image img = Image.Load(svg, new LoadOptions { Configuration = new ImageConfiguration().Add(SvgFormat.Instance) });
    Rgba32[] px = img.ToArray<Rgba32>();
    return img.Width == 40 && px[(20 * 40) + 20].A == 255;
});

Console.WriteLine(failures == 0 ? "wasm smoke: all steps passed" : $"wasm smoke: {failures} step(s) failed");
return failures == 0 ? 0 : 1;

// A 40 x 40 point page that is one 2 x 2 RGB image (red, green / blue, white), as a scanner would produce.
static byte[] ScannedPdf()
{
    byte[] samples = [255, 0, 0, 0, 255, 0, 0, 0, 255, 255, 255, 255];
    string image = "<< /Type /XObject /Subtype /Image /Width 2 /Height 2 /ColorSpace /DeviceRGB /BitsPerComponent 8 /Length 12 >>\nstream\n"
        + Encoding.Latin1.GetString(samples) + "\nendstream";
    return BuildPdf("q 40 0 0 40 0 0 cm /Im0 Do Q", "/XObject << /Im0 5 0 R >>", 40, 40, [image]);
}

static byte[] BuildPdf(string content, string resources, int width, int height, string[] extra)
{
    var objects = new System.Collections.Generic.List<string>
    {
        "<< /Type /Catalog /Pages 2 0 R >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        string.Create(CultureInfo.InvariantCulture, $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {width} {height}] /Contents 4 0 R /Resources << {resources} >> >>"),
        string.Create(CultureInfo.InvariantCulture, $"<< /Length {content.Length} >>\nstream\n{content}\nendstream"),
    };
    objects.AddRange(extra);
    var sb = new StringBuilder("%PDF-1.4\n");
    var offsets = new int[objects.Count];
    for (int i = 0; i < objects.Count; i++)
    {
        offsets[i] = sb.Length;
        sb.Append(CultureInfo.InvariantCulture, $"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
    }

    int xref = sb.Length;
    sb.Append(CultureInfo.InvariantCulture, $"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
    foreach (int o in offsets)
    {
        sb.Append(o.ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n \n");
    }

    sb.Append(CultureInfo.InvariantCulture, $"trailer\n<< /Root 1 0 R /Size {objects.Count + 1} >>\nstartxref\n{xref}\n%%EOF\n");
    return Encoding.Latin1.GetBytes(sb.ToString());
}
