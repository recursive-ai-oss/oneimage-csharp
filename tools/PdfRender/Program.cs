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

    private sealed class ImageProbe : OnePdf.PdfDevice
    {
        public override void DrawImage(in OnePdf.PdfImage image, in OnePdf.PdfGraphicsState state)
        {
            OnePdf.PdfImageData? d = image.GetData();
            Console.WriteLine(d is null ? "image: null" : $"image {image.Name}: {d.Width}x{d.Height} decoded={d.IsDecoded} stencil={d.IsStencil} space={d.ColorSpace} alpha={d.Alpha?.Width}x{d.Alpha?.Height} filter={d.EncodedFilter}");
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
