using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using OneImage.Svg;

namespace OneImage.Benchmarks;

/// <summary>
/// <c>svg-hash</c>: renders the committed benchmark and sample documents at fixed sizes (whole frame and as a 3 x 2 grid of regions)
/// and prints a SHA-256 per output, so two builds can be compared byte for byte.
/// </summary>
internal static class SvgHash
{
    public static int Run()
    {
        string root = CustomAttributeExtensions.GetCustomAttributes<AssemblyMetadataAttribute>(typeof(SvgHash).Assembly).First(a => a.Key == "RepoRoot").Value!;
        string dir = Path.Combine(root, "tests", "testdata", "svg");
        string fontDir = Path.Combine(root, "tests", "testdata", "fonts");
        var fonts = new SvgFontOptions { UseSystemFonts = false };
        fonts.AddFont(Path.Combine(fontDir, "NotoSans-Subset-Regular.ttf"));
        fonts.AddFont(Path.Combine(fontDir, "NotoSans-Subset-Bold-kern.ttf"));
        var options = new LoadOptions { Configuration = new ImageConfiguration().Add(new SvgFormat(fonts: fonts)) };

        var docs = new List<(string Name, byte[] Data, int Width)>();
        foreach ((string file, int width) in new[] { ("icon-24.svg", 256), ("illustration.svg", 1024), ("markers.svg", 1024), ("markers-10k.svg", 1024), ("gradients.svg", 700), ("patterns.svg", 700), ("clip-mask-features.svg", 600), ("clip-mask-cycles.svg", 400), ("css-structure.svg", 500), ("filters.svg", 500), ("text.svg", 600) })
        {
            docs.Add((file, File.ReadAllBytes(Path.Combine(dir, file)), width));
        }

        docs.Add(("text-page", Encoding.UTF8.GetBytes(SvgTextBenchmarks.Document()), 800));
        docs.Add(("filters-effects.svg", File.ReadAllBytes(Path.Combine(dir, "filters-effects.svg")), 500));
        docs.Add(("filter-blur8", Encoding.UTF8.GetBytes(SvgFilterBenchmarks.BlurDocument()), 1024));
        docs.Add(("filter-blur30", Encoding.UTF8.GetBytes(SvgFilterBenchmarks.BlurDocument(30)), 2048));
        docs.Add(("filter-blur1.5", Encoding.UTF8.GetBytes(SvgFilterBenchmarks.BlurDocument(1.5)), 1024));
        docs.Add(("filter-blur-small-h", Encoding.UTF8.GetBytes(SvgFilterBenchmarks.BlurDocument(0.9)), 333));
        docs.Add(("filter-shadow", Encoding.UTF8.GetBytes(SvgFilterBenchmarks.ShadowDocument()), 1024));
        docs.Add(("filter-lit", Encoding.UTF8.GetBytes(SvgFilterBenchmarks.LitDocument()), 1024));
        docs.Add(("filter-icon", Encoding.UTF8.GetBytes(SvgFilterBenchmarks.IconDocument()), 256));
        foreach ((string name, byte[] data, int width) in docs)
        {
            Image image = Image.Load(data, options).Resize(width: width);
            using var sha = SHA256.Create();
            Rgba32[] whole = image.ToArray<Rgba32>();
            sha.TransformFinalBlock(OneImage.Kernels.SpanCast.AsBytes<Rgba32>(whole.AsSpan()).ToArray(), 0, whole.Length * 4);
            string full = Convert.ToHexString(sha.Hash!)[..16];

            // Region reads: a 3 x 2 grid of uneven pieces, which also covers the renderer's per-region path.
            using var sha2 = SHA256.Create();
            int w = image.Width;
            int h = image.Height;
            for (int gy = 0; gy < 2; gy++)
            {
                for (int gx = 0; gx < 3; gx++)
                {
                    int x0 = w * gx / 3 + (gx == 0 ? 0 : 5);
                    int x1 = w * (gx + 1) / 3 + (gx == 2 ? 0 : 5);
                    int y0 = h * gy / 2;
                    int y1 = h * (gy + 1) / 2;
                    var px = new Rgba32[(x1 - x0) * (y1 - y0)];
                    image.CopyPixelsTo<Rgba32>(px, new ImageRect(x0, y0, x1 - x0, y1 - y0));
                    byte[] b = OneImage.Kernels.SpanCast.AsBytes<Rgba32>(px.AsSpan()).ToArray();
                    sha2.TransformBlock(b, 0, b.Length, null, 0);
                }
            }

            sha2.TransformFinalBlock([], 0, 0);
            Console.WriteLine($"{name} {w}x{h} full={full} regions={Convert.ToHexString(sha2.Hash!)[..16]}");
        }

        return 0;
    }
}
