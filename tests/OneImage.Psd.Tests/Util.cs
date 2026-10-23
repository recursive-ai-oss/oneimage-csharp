using System.Diagnostics;
using System.Text;
using OneImage.Layers;

namespace OneImage.Psd.Tests;

internal static class Util
{
    public static readonly PsdFormat Raw = new(unmatte: false);

    public static readonly ImageConfiguration Config = new ImageConfiguration().Add(Raw);

    public static readonly LoadOptions Load = new() { Configuration = Config };

    public static string? CorpusDir
    {
        get
        {
            string? dir = Environment.GetEnvironmentVariable("ONEIMAGE_PSD_TESTS");
            return dir != null && Directory.Exists(dir) ? dir : null;
        }
    }

    public static IEnumerable<string> CorpusFiles() =>
        CorpusDir is { } d ? Directory.EnumerateFiles(d, "*.ps?", SearchOption.AllDirectories).Order(StringComparer.Ordinal) : [];

    public static Image LoadComposite(string path) => Image.Load(path, Load);

    public static Image LoadComposite(byte[] data) => Image.Load(data, Load);

    /// <summary>
    /// Compares the stored composite (as stored: with transparency, blended over white) with a rendering blended over white. Returns the mean and
    /// maximum RGB difference in 8-bit levels, the count of channel values off by more than 8 and the mean alpha difference.
    /// </summary>
    public static (double Mean, int Max, long Over8, double AlphaMean, bool Uniform) Diff(Image stored, Image rendered)
    {
        int w = stored.Width;
        int h = stored.Height;
        if (rendered.Width != w || rendered.Height != h)
        {
            throw new InvalidOperationException($"size {w}x{h} vs {rendered.Width}x{rendered.Height}");
        }

        bool storedAlpha = stored.Format.HasAlpha;
        double sum = 0;
        double asum = 0;
        int max = 0;
        long over = 0;
        long n = 0;
        bool uniform = true;
        Rgba32 first = default;
        int rows = Math.Max(1, (1 << 20) / Math.Max(1, w));
        var pa = new Rgba32[w * rows];
        var pb = new Rgba32[w * rows];
        for (int y0 = 0; y0 < h; y0 += rows)
        {
            int r = Math.Min(rows, h - y0);
            stored.CopyPixelsTo<Rgba32>(pa.AsSpan(0, w * r), new ImageRect(0, y0, w, r));
            rendered.CopyPixelsTo<Rgba32>(pb.AsSpan(0, w * r), new ImageRect(0, y0, w, r));
            for (int i = 0; i < w * r; i++)
            {
                Rgba32 p = pa[i];
                Rgba32 q = pb[i];
                if (y0 == 0 && i == 0)
                {
                    first = p;
                }
                else if (p != first)
                {
                    uniform = false;
                }

                int qa = q.A;
                int[] d =
                [
                    Math.Abs(p.R - (q.R * qa + 255 * (255 - qa) + 127) / 255),
                    Math.Abs(p.G - (q.G * qa + 255 * (255 - qa) + 127) / 255),
                    Math.Abs(p.B - (q.B * qa + 255 * (255 - qa) + 127) / 255),
                ];
                foreach (int v in d)
                {
                    sum += v;
                    if (v > max)
                    {
                        max = v;
                    }

                    if (v > 8)
                    {
                        over++;
                    }
                }

                if (storedAlpha)
                {
                    asum += Math.Abs(p.A - qa);
                }

                n += 3;
            }
        }

        return (sum / n, max, over, asum / (n / 3), uniform);
    }

    public static byte[] Save(PsdFile file, PsdWriteOptions? options = null)
    {
        using var ms = new MemoryStream();
        file.Save(ms, options);
        return ms.ToArray();
    }
}

public class CorpusReport
{
    [Fact]
    public void WriteReport()
    {
        string? report = Environment.GetEnvironmentVariable("ONEIMAGE_PSD_REPORT");
        if (Util.CorpusDir == null || report == null)
        {
            return;
        }

        var sb = new StringBuilder();
        sb.AppendLine("file\tbytes\tmode\tdepth\tsize\tlayers\tload\tcomposite\tmean\tmax\tover8\talpha\tfeatures\tcomps\tms");
        foreach (string path in Util.CorpusFiles())
        {
            string rel = System.IO.Path.GetRelativePath(Util.CorpusDir, path);
            var sw = Stopwatch.StartNew();
            string load = "ok";
            string comp = "ok";
            string mode = "?";
            string depth = "?";
            string size = "?";
            int layers = -1;
            string mean = "", max = "", over = "\t"; string features = "";
            string comps = "";
            try
            {
                using PsdFile f = PsdFile.Load(path);
                mode = f.SourceColorMode.ToString();
                depth = f.SourceBitDepth.ToString();
                size = f.Document.Width + "x" + f.Document.Height;
                layers = Count(f.Document.Root);
                using (var fsx = File.OpenRead(path))
                {
                    var io = new FileIo(fsx);
                    PsdStructure st = PsdParser.Parse(io, PsdLimits.Default, ImageLimits.Unrestricted, layers: true);
                    var counts = new int[4];
                    var two = new byte[2];
                    foreach (RawLayer rl in st.Layers)
                    {
                        foreach (RawChannel ch in rl.Channels)
                        {
                            if (ch.Length >= 2)
                            {
                                io.ReadAt(ch.Position, two);
                                int code = two[1];
                                if (code < 4)
                                {
                                    counts[code]++;
                                }
                            }
                        }
                    }

                    comps = $"{counts[0]}/{counts[1]}/{counts[2]}/{counts[3]}";
                }

                var fs = new SortedSet<string>();
                Features(f, f.Document.Root, fs);
                features = string.Join(",", fs);
                try
                {
                    Image stored = Util.LoadComposite(path);
                    if ((long)stored.Width * stored.Height <= 24_000_000)
                    {
                        (double m, int mx, long o, double am, bool un) = Util.Diff(stored, f.Document.Render());
                        mean = m.ToString("F3", System.Globalization.CultureInfo.InvariantCulture);
                        max = mx.ToString(System.Globalization.CultureInfo.InvariantCulture);
                        over = o.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\t" + am.ToString("F3", System.Globalization.CultureInfo.InvariantCulture);
                        if (un)
                        {
                            features += "storedUniform";
                        }
                    }
                }
                catch (Exception ex)
                {
                    comp = ex.GetType().Name + ": " + ex.Message.Split('\n')[0];
                }
            }
            catch (Exception ex)
            {
                load = ex.GetType().Name + ": " + ex.Message.Split('\n')[0];
            }

            sb.AppendLine($"{rel}\t{new FileInfo(path).Length}\t{mode}\t{depth}\t{size}\t{layers}\t{load}\t{comp}\t{mean}\t{max}\t{over}\t{features}\t{comps}\t{sw.ElapsedMilliseconds}");
        }

        File.WriteAllText(report, sb.ToString());
    }

    private static void Features(PsdFile f, GroupLayer g, SortedSet<string> into)
    {
        foreach (Layer l in g.Children)
        {
            PsdLayerInfo i = f.GetInfo(l);
            if (i.Kind is PsdLayerKind.Text or PsdLayerKind.SmartObject or PsdLayerKind.UnsupportedAdjustment || (i.Kind == PsdLayerKind.GradientOrPatternFill && l is not FillLayer))
            {
                into.Add(i.Kind.ToString());
            }

            if (i.Kind == PsdLayerKind.Shape && l.Bounds.Width * l.Bounds.Height <= 1)
            {
                into.Add("ShapeNoPixels");
            }

            if (i.SourceBlendKey != null)
            {
                into.Add("blend:" + i.SourceBlendKey);
            }

            if (i.EffectsNotDrawn > 0 || (i.Blocks.Any(static b => b.Key == "lrFX") && !i.Blocks.Any(static b => b.Key is "lfx2" or "lmfx")))
            {
                into.Add("effects");
            }

            if (i.EffectsApproximated > 0)
            {
                into.Add("effects-approx");
            }

            if (l is GroupLayer sub)
            {
                Features(f, sub, into);
            }
        }
    }

    private static int Count(GroupLayer g)
    {
        int n = 0;
        foreach (Layer l in g.Children)
        {
            n++;
            if (l is GroupLayer sub)
            {
                n += Count(sub);
            }
        }

        return n;
    }
}
