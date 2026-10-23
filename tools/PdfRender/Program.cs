using System.Diagnostics;
using System.Globalization;
using System.Threading;
using OneImage.Pdf;

namespace OneImage.Tools;

/// <summary>
/// Renders PDF pages to PNG files for the render-gate harness (tools/pdf-gate). <c>render &lt;in.pdf&gt; &lt;out.png&gt; [dpi] [page]</c> draws one page;
/// <c>batch &lt;list.txt&gt; &lt;outdir&gt; [dpi] [timeoutSeconds]</c> draws page 1 of every file named in the list (a path per line, optionally followed by a tab, the output name, a tab and the password) and prints one
/// tab-separated line per file: name, status (ok, error, timeout), milliseconds, width, height and the message.
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length >= 3 && args[0] == "render")
        {
            double dpi = args.Length > 3 ? double.Parse(args[3], CultureInfo.InvariantCulture) : 150;
            int page = args.Length > 4 ? int.Parse(args[4], CultureInfo.InvariantCulture) : 0;
            Render(args[1], args[2], dpi, page, 0, out _, out _);
            return 0;
        }

        if (args.Length >= 2 && args[0] == "dump")
        {
            // The display list of page 1 in OnePDF's text form, to see what a page asks the renderer to draw.
            using var doc = OnePdf.PdfDocument.Open(File.ReadAllBytes(args[1]), new OnePdf.PdfOpenOptions { Password = args.Length > 2 ? args[2] : null });
            var list = new OnePdf.PdfDisplayList();
            OnePdf.PdfPageInterpretExtensions.Interpret(doc.Pages[0], list, new OnePdf.PdfInterpretOptions { GlyphOutlines = false });
            Console.Write(list.ToText());
            return 0;
        }

        if (args.Length >= 2 && args[0] == "glyphs")
        {
            // The outline of the first glyphs of the first text run on page 1, as OnePDF hands them to the device.
            using var doc = OnePdf.PdfDocument.Open(File.ReadAllBytes(args[1]));
            OnePdf.PdfPageInterpretExtensions.Interpret(doc.Pages[0], new GlyphProbe(), new OnePdf.PdfInterpretOptions { GlyphOutlines = true });
            return 0;
        }

        if (args.Length >= 2 && args[0] == "images")
        {
            // What the image decoders make of each image on page 1: size, color space, decoded or not.
            PdfFormat unused = PdfFormat.Instance;
            using var doc = OnePdf.PdfDocument.Open(File.ReadAllBytes(args[1]), new OnePdf.PdfOpenOptions { Password = args.Length > 2 ? args[2] : null });
            OnePdf.PdfPageInterpretExtensions.Interpret(doc.Pages[0], new ImageProbe(), new OnePdf.PdfInterpretOptions { GlyphOutlines = false });
            foreach (OnePdf.PdfDiagnostic d in doc.Diagnostics)
            {
                Console.WriteLine(d);
            }

            return 0;
        }

        if (args.Length >= 3 && args[0] == "batch")
        {
            double dpi = args.Length > 3 ? double.Parse(args[3], CultureInfo.InvariantCulture) : 150;
            int timeout = args.Length > 4 ? int.Parse(args[4], CultureInfo.InvariantCulture) : 120;
            Directory.CreateDirectory(args[2]);
            foreach (string line in File.ReadLines(args[1]))
            {
                string[] cols = line.Trim().Split('	');
                string path = cols[0];
                if (path.Length == 0)
                {
                    continue;
                }

                string name = cols.Length > 1 ? cols[1] : Path.GetFileNameWithoutExtension(path);
                var sw = Stopwatch.StartNew();
                try
                {
                    Render(path, Path.Combine(args[2], name + ".png"), dpi, 0, timeout, out int w, out int h, cols.Length > 2 && cols[2].Length > 0 ? cols[2] : null);
                    Console.WriteLine($"{name}\tok\t{sw.ElapsedMilliseconds}\t{w}\t{h}\t");
                }
                catch (OperationCanceledException)
                {
                    Console.WriteLine($"{name}\ttimeout\t{sw.ElapsedMilliseconds}\t0\t0\t");
                }
                catch (ImageException ex)
                {
                    Console.WriteLine($"{name}\terror\t{sw.ElapsedMilliseconds}\t0\t0\t{ex.GetType().Name}: {ex.Message.ReplaceLineEndings(" ")}");
                }
            }

            return 0;
        }

        Console.Error.WriteLine("usage: render <in.pdf> <out.png> [dpi] [page] | dump <in.pdf> [password] | batch <list.txt> <outdir> [dpi] [timeoutSeconds]");
        return 2;
    }

    private sealed class GlyphProbe : OnePdf.PdfDevice
    {
        private int _runs;

        public override void ShowGlyphRun(in OnePdf.PdfGlyphRun run, in OnePdf.PdfGraphicsState state)
        {
            if (_runs++ != int.Parse(Environment.GetEnvironmentVariable("GLYPH_RUN") ?? "0", CultureInfo.InvariantCulture))
            {
                return;
            }

            // Both fill rules of the first glyph, 400 px square, next to each other.
            {
                OnePdf.PdfPath o = run.Glyphs.Span[0].Outline;
                var b = new OneImage.Drawing.PathBuilder();
                var vs = o.VerbBytes.Span;
                var ps = o.Points.Span;
                int q = 0;
                for (int k = 0; k < vs.Length; k++)
                {
                    switch (vs[k])
                    {
                        case 0: b.MoveTo((float)(ps[q] * 360 + 20), (float)(380 - ps[q + 1] * 360)); q += 2; break;
                        case 1: b.LineTo((float)(ps[q] * 360 + 20), (float)(380 - ps[q + 1] * 360)); q += 2; break;
                        case 2: b.CubicBezierTo((float)(ps[q] * 360 + 20), (float)(380 - ps[q + 1] * 360), (float)(ps[q + 2] * 360 + 20), (float)(380 - ps[q + 3] * 360), (float)(ps[q + 4] * 360 + 20), (float)(380 - ps[q + 5] * 360)); q += 6; break;
                        default: b.Close(); break;
                    }
                }

                OneImage.Drawing.Path path = b.Build();
                using var canvas = new Canvas(820, 400, PixelFormat.Rgba8);
                canvas.Clear(new Rgba32(255, 255, 255, 255));
                canvas.Fill(new Rgba32(0, 0, 0, 255), path, true, OneImage.Drawing.IntersectionRule.NonZero);
                canvas.Fill(new Rgba32(0, 0, 0, 255), path, true, OneImage.Drawing.IntersectionRule.EvenOdd, 1f, BlendMode.Normal, AlphaComposition.SrcOver, false, System.Numerics.Matrix3x2.CreateTranslation(420, 0));
                canvas.Snapshot().Save("glyph.png");
            }

            for (int i = 0; i < Math.Min(4, run.Glyphs.Length); i++)
            {
                OnePdf.PdfGlyph g = run.Glyphs.Span[i];
                Console.WriteLine($"glyph {i} id={g.GlyphId} u={g.Unicode} w={g.Width} verbs={g.Outline.VerbBytes.Length} points={g.Outline.Points.Length / 2}");
                var v = g.Outline.VerbBytes.Span;
                var pts = g.Outline.Points.Span;
                int p = 0;
                for (int k = 0; k < v.Length; k++)
                {
                    int n = v[k] switch { 0 => 2, 1 => 2, 2 => 6, _ => 0 };
                    Console.WriteLine("  " + v[k] + " " + string.Join(' ', pts.Slice(p, Math.Min(n, pts.Length - p)).ToArray().Select(x => x.ToString("0.###", CultureInfo.InvariantCulture))));
                    p += n;
                }
            }
        }
    }

    private sealed class ImageProbe : OnePdf.PdfDevice
    {
        public override void DrawImage(in OnePdf.PdfImage image, in OnePdf.PdfGraphicsState state)
        {
            OnePdf.PdfImageData? d = image.GetData();
            Console.WriteLine(d is null ? "image: null" : $"image {image.Name}: {d.Width}x{d.Height} decoded={d.IsDecoded} stencil={d.IsStencil} space={d.ColorSpace} alpha={d.Alpha?.Width}x{d.Alpha?.Height} filter={d.EncodedFilter} alphaStats={Stats(d.Alpha)} samples={d.Samples.Length}");
        }

        private static string Stats(OnePdf.PdfImageAlpha? a)
        {
            if (a is null) return "none";
            var v = a.Values.Span; int mn = 255, mx = 0; long sum = 0;
            foreach (byte b in v) { mn = Math.Min(mn, b); mx = Math.Max(mx, b); sum += b; }
            return $"{mn}..{mx} mean {(v.Length == 0 ? 0 : sum / v.Length)} len {v.Length}";
        }
    }

    private static void Render(string input, string output, double dpi, int page, int timeoutSeconds, out int width, out int height, string? password = null)
    {
        var config = new ImageConfiguration().Add(new PdfFormat(new PdfRenderOptions { Dpi = dpi, FirstPage = page, PageCount = 1, Password = password }));
        using var cts = timeoutSeconds > 0 ? new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds)) : new CancellationTokenSource();
        Image img = Image.Load(input, new LoadOptions { Configuration = config });
        width = img.Width;
        height = img.Height;
        img.Save(output, null, cts.Token);
    }
}
