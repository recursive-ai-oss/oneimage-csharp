using System.Drawing;
using OneImage.Layers;

namespace OneImage.Psd.Tests;

/// <summary>
/// Checks the fill geometry against the rasters Photoshop stored with the fill layers of the sample files (see tools/fetch-psd-tests.sh): the
/// gradient or pattern drawn from the settings must agree with Photoshop's own pixels. Skipped when the sample files are not available.
/// </summary>
public class FillCorpusTests
{
    [Fact]
    public void DrawnFillsAgreeWithPhotoshopsStoredRasters()
    {
        if (Util.CorpusDir == null)
        {
            return;
        }

        var failures = new List<string>();
        int checkedLayers = 0;
        foreach (string path in Util.CorpusFiles())
        {
            string rel = System.IO.Path.GetRelativePath(Util.CorpusDir, path);
            PsdFile f;
            try
            {
                f = PsdFile.Load(path, new PsdReadOptions { DrawFills = false });
            }
            catch (ImageFormatException)
            {
                continue; // multichannel
            }

            using (f)
            {
                Walk(f, f.Document.Root, rel, failures, ref checkedLayers);
            }
        }

        Assert.True(checkedLayers > 100, $"only {checkedLayers} fill layers were compared");
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    private static void Walk(PsdFile f, GroupLayer g, string rel, List<string> failures, ref int compared)
    {
        foreach (Layer l in g.Children)
        {
            if (l is GroupLayer sub)
            {
                Walk(f, sub, rel, failures, ref compared);
                continue;
            }

            PsdLayerInfo info = f.GetInfo(l);
            PsdBlock? block = info.Blocks.FirstOrDefault(static b => b.Key is "GdFl" or "PtFl");
            if (block == null || l is not PixelLayer pl || pl.Pixels.Width * pl.Pixels.Height <= 1)
            {
                continue;
            }

            Document doc = f.Document;
            RectangleF box = new(0, 0, doc.Width, doc.Height);
            bool shape = false;
            if (info.Blocks.FirstOrDefault(static b => b.Key is "vmsk" or "vsms") is { } vm &&
                VectorMask.Parse(vm.Data.Span, doc.Width, doc.Height, 0, 0, out _, out _) is { } path && path.Bounds.Width > 0)
            {
                box = path.Bounds;
                shape = true;
            }

            OneImage.Drawing.Brush? brush;
            double limit;
            if (block.Key == "GdFl")
            {
                GradientFillSpec? spec = FillDescriptors.ParseGradient(block.Data.Span, PsdLimits.Default, new ColorConverter(doc.ColorProfile, null));
                if (spec == null)
                {
                    continue; // not reproduced; keeps the stored pixels
                }

                if (!spec.Align)
                {
                    box = new RectangleF(0, 0, doc.Width, doc.Height);
                }
                else if (!shape)
                {
                    box = new RectangleF(pl.X, pl.Y, pl.Pixels.Width, pl.Pixels.Height);
                }

                if (Math.Min(box.Width, box.Height) < PsdGradientBrush.MinimumSide)
                {
                    continue;
                }

                brush = new PsdGradientBrush(spec, box);
                // Photoshop dithers most of these and snaps the direction and length to whole pixels (a 60-degree reflected gradient on 32 px ends up
                // about 3 degrees off, which is the worst case here at 6.8 levels); the others agree within about 4.
                limit = 7;
            }
            else
            {
                brush = FillDescriptors.MakePatternBrush(block.Data.Span, PsdLimits.Default, new PatternCache(f.DocumentBlocks), box, shape);
                limit = 0.001;
            }

            if (brush == null)
            {
                continue;
            }

            int w = pl.Pixels.Width, h = pl.Pixels.Height;
            var px = new Rgba32[w * h];
            pl.Pixels.CopyTo<Rgba32>(px, new ImageRect(0, 0, w, h));
            double sum = 0;
            long n = 0;
            float[] row = new float[w * 4];
            for (int y = 0; y < h; y++)
            {
                brush.FillRow(pl.X, pl.Y + y, row);
                for (int x = 0; x < w; x++)
                {
                    Rgba32 p = px[(y * w) + x];
                    if (p.A != 255)
                    {
                        continue;
                    }

                    sum += Math.Abs(p.R - (int)Math.Round(row[x * 4] * 255)) + Math.Abs(p.G - (int)Math.Round(row[(x * 4) + 1] * 255)) + Math.Abs(p.B - (int)Math.Round(row[(x * 4) + 2] * 255));
                    n += 3;
                }
            }

            if (n == 0)
            {
                continue;
            }

            compared++;
            double mean = sum / n;
            if (mean > limit)
            {
                failures.Add($"{rel} '{l.Name}' {block.Key}: mean error {mean:F2} levels over {n / 3} interior pixels (limit {limit})");
            }
        }
    }
}
