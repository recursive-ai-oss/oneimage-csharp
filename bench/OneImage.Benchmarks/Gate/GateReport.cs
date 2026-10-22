using System.Collections.Generic;
using System.Linq;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using BenchmarkDotNet.Attributes;

namespace OneImage.Benchmarks.Gate;

/// <summary>
/// Builds the gate report: <c>gate-report &lt;bdn-results-dir&gt; &lt;peak.tsv&gt; &lt;header.md&gt; &lt;out.md&gt;</c>. Rows come from the
/// BenchmarkDotNet JSON files, the task of each row from its <c>[BenchmarkCategory("Gate", task)]</c> tag, the library from the
/// benchmark method name; peak memory comes from the <c>gate-peak</c> lines.
/// </summary>
internal static class GateReport
{
    private static readonly string[] Native = ["skia", "netvips", "magick"];
    private static readonly HashSet<string> ThumbnailOrCodec = new(StringComparer.Ordinal)
    {
        "thumb-jpeg", "thumb-webp", "png-to-webp", "jpeg-decode", "jpeg-decode-rgba", "jpeg-encode", "png-decode", "png-encode",
        "webp-decode", "webp-encode", "gif-decode", "gif-encode", "tiff-multipage-decode", "tiff-encode", "bmp-decode", "bmp-encode",
    };

    private static readonly HashSet<string> Streaming = new(StringComparer.Ordinal)
    {
        "thumb-jpeg", "thumb-webp", "png-to-webp", "gif-resize", "cmyk-jpeg-to-srgb",
    };

    // Tasks where the comparison is against the libraries that apply the embedded ICC profile (Magick.NET, NetVips). ImageSharp
    // skips the conversion, so its row is information, not the bar: ours must beat Magick.NET in time and peak memory and
    // stay within 1.5x of NetVips in time.
    private static readonly HashSet<string> IccTasks = new(StringComparer.Ordinal) { "cmyk-jpeg-to-srgb" };

    // Ratios within 5% of 1 count as a tie: that is below the noise of a shared host.
    private const double Tie = 1.05;

    private sealed record Row(string Task, string Param, string Lib, string Title, string Method, double Ms, double AllocMiB);

    private sealed record Peak(double BeforeMiB, double PeakMiB, double Ms)
    {
        public double Delta => PeakMiB - BeforeMiB;
    }

    public static int Run(string[] args)
    {
        string dir = args[1];
        Dictionary<string, string> tasks = TaskMap();
        var rows = new List<Row>();
        foreach (string file in Directory.GetFiles(dir, "*-report-full-compressed.json").Order(StringComparer.Ordinal))
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(file));
            foreach (JsonElement b in doc.RootElement.GetProperty("Benchmarks").EnumerateArray())
            {
                string full = b.GetProperty("Namespace").GetString() + "." + b.GetProperty("Type").GetString() + "." + b.GetProperty("Method").GetString();
                string method = b.GetProperty("Method").GetString()!;
                if (!tasks.TryGetValue(full, out string? task))
                {
                    continue;
                }

                string param = b.GetProperty("Parameters").GetString() ?? "";
                double mean = b.GetProperty("Statistics").GetProperty("Mean").GetDouble() / 1e6;
                double alloc = b.GetProperty("Memory").GetProperty("BytesAllocatedPerOperation").GetInt64() / 1048576.0;
                rows.Add(new Row(task, param, Lib(method), b.GetProperty("MethodTitle").GetString()!.Trim('\''), method, mean, alloc));
            }
        }

        var peaks = new Dictionary<(string, string), Peak>();
        foreach (string line in File.ReadLines(args[2]))
        {
            string[] f = line.Split('\t');
            if (f.Length == 6 && f[0] == "PEAK")
            {
                peaks[(f[1], f[2])] = new Peak(D(f[3]), D(f[4]), D(f[5]));
            }
        }

        var sb = new StringBuilder();
        sb.Append(File.ReadAllText(args[3])).AppendLine();
        var gaps = new List<string>();
        var memReported = new HashSet<string>(StringComparer.Ordinal);
        var summary = new StringBuilder();
        var detail = new StringBuilder();
        summary.AppendLine("| Task | Ours ms | ImageSharp ms | Ours / ImageSharp | Fastest native wrapper | Ours / fastest | Peak ours MiB | Peak ImageSharp MiB | Peak NetVips MiB | Peak ours / ImageSharp | Peak ours / NetVips |");
        summary.AppendLine("|---|--:|--:|--:|---|--:|--:|--:|--:|--:|--:|");

        foreach (var g in rows.GroupBy(r => (r.Task, r.Param)).OrderBy(g => Array.IndexOf(GatePeak.Tasks, g.Key.Task) < 0 ? 999 : Array.IndexOf(GatePeak.Tasks, g.Key.Task)).ThenBy(g => g.Key.Task, StringComparer.Ordinal).ThenBy(g => g.Key.Param, StringComparer.Ordinal))
        {
            string task = g.Key.Task;
            string label = g.Key.Param.Length > 0 ? $"{task} ({g.Key.Param})" : task;
            Row? ours = g.Where(r => r.Lib == "oneimage").MinBy(r => r.Ms);
            Row? sharp = g.Where(r => r.Lib == "imagesharp").MinBy(r => r.Ms);
            Row? fast = g.Where(r => Native.Contains(r.Lib)).MinBy(r => r.Ms);
            peaks.TryGetValue((task, "oneimage"), out Peak? po);
            peaks.TryGetValue((task, "imagesharp"), out Peak? ps);
            peaks.TryGetValue((task, "netvips"), out Peak? pn);
            if (ours == null)
            {
                continue;
            }

            bool icc = IccTasks.Contains(task);
            Row? magick = g.Where(r => r.Lib == "magick").MinBy(r => r.Ms);
            Row? vips = g.Where(r => r.Lib == "netvips").MinBy(r => r.Ms);
            peaks.TryGetValue((task, "magick"), out Peak? pm);
            double? rs = sharp == null ? null : ours.Ms / sharp.Ms;
            double? rf = fast == null ? null : ours.Ms / fast.Ms;
            double? ms = po != null && ps != null ? po.Delta / Math.Max(ps.Delta, 1) : null;
            double? mn = po != null && pn != null ? po.Delta / Math.Max(pn.Delta, 1) : null;
            string skip = icc ? " (skips ICC)" : "";
            summary.AppendLine(string.Create(CultureInfo.InvariantCulture, $"| {label} | {ours.Ms:F1} | {Fmt(sharp?.Ms)}{skip} | {Fmt(rs, 2)}{(rs > Tie && !icc ? " ✗" : "")} | {(fast == null ? "-" : fast.Lib + " " + fast.Ms.ToString("F1", CultureInfo.InvariantCulture))} | {Fmt(rf, 2)}{(rf > 1.5 && ThumbnailOrCodec.Contains(task) ? " ✗" : "")} | {Fmt(po?.Delta)} | {Fmt(ps?.Delta)} | {Fmt(pn?.Delta)} | {Fmt(ms, 2)}{(ms > Tie && !icc ? " ✗" : "")} | {Fmt(mn, 2)}{(mn > 2 && Streaming.Contains(task) ? " ✗" : "")} |"));

            if (icc)
            {
                if (magick != null && ours.Ms > magick.Ms)
                {
                    gaps.Add($"- **{task}**: slower than Magick.NET, {ours.Ms:F1} ms vs {magick.Ms:F1} ms.");
                }

                if (po != null && pm != null && po.Delta > pm.Delta)
                {
                    gaps.Add($"- **{task}**: more peak working memory than Magick.NET, {po.Delta:F0} MiB vs {pm.Delta:F0} MiB.");
                }

                if (vips != null && ours.Ms > 1.5 * vips.Ms)
                {
                    gaps.Add($"- **{task}**: {ours.Ms / vips.Ms:F2}× NetVips ({vips.Ms:F1} ms vs ours {ours.Ms:F1} ms), beyond 1.5×.");
                }
            }

            if (rs > Tie && !icc)
            {
                gaps.Add($"- **{label}**: slower than ImageSharp (beyond a 5% tie), {ours.Ms:F1} ms vs {sharp!.Ms:F1} ms ({rs:F2}×).");
            }

            if (ms > Tie && !icc && memReported.Add(task + "/is"))
            {
                gaps.Add($"- **{task}**: more peak working memory than ImageSharp, {po!.Delta:F0} MiB vs {ps!.Delta:F0} MiB ({ms:F2}×).");
            }

            if (rf > 1.5 && ThumbnailOrCodec.Contains(task))
            {
                gaps.Add($"- **{label}**: {rf:F2}× the fastest native wrapper ({fast!.Lib} {fast.Ms:F1} ms vs ours {ours.Ms:F1} ms).");
            }

            if (mn > 2 && Streaming.Contains(task) && memReported.Add(task + "/nv"))
            {
                gaps.Add($"- **{task}**: peak working memory {mn:F2}× NetVips ({po!.Delta:F0} MiB vs {pn!.Delta:F0} MiB).");
            }

            detail.AppendLine(string.Create(CultureInfo.InvariantCulture, $"### {label}")).AppendLine();
            detail.AppendLine("| Library | Row | Mean ms | Managed alloc MiB | Peak working MiB (process total) |").AppendLine("|---|---|--:|--:|--:|");
            foreach (Row r in g.OrderBy(r => r.Ms))
            {
                // Peak memory is measured per library, not per row: shown on the library's fastest row only.
                bool first = g.Where(x => x.Lib == r.Lib).MinBy(x => x.Ms) == r;
                string pk = first && peaks.TryGetValue((task, r.Lib), out Peak? p) ? $"{p.Delta:F0} ({p.PeakMiB:F0})" : "";
                detail.AppendLine(string.Create(CultureInfo.InvariantCulture, $"| {r.Lib} | {r.Title} | {r.Ms:F1} | {r.AllocMiB:F1} | {pk} |"));
            }

            detail.AppendLine();
        }

        sb.AppendLine("## Gate summary").AppendLine();
        sb.AppendLine("Ours = our fastest row of the task. ✗ marks a miss: slower than ImageSharp, more peak working memory than ImageSharp, more than 1.5× the fastest native wrapper (thumbnail and codec tasks), more than 2× NetVips's peak working memory (streaming tasks: thumbnails, PNG→WebP, GIF resize, CMYK). Peak columns are the growth of the process's resident set during the operation, in MiB (see Method).").AppendLine();
        sb.Append(summary).AppendLine();
        if (args.Length > 5 && File.Exists(args[5]) && File.ReadLines(args[5]).Any(l => l.StartsWith("DELTAE", StringComparison.Ordinal)))
        {
            sb.AppendLine("## CMYK JPEG to sRGB: colour accuracy").AppendLine();
            sb.AppendLine("The bar for this task is Magick.NET and NetVips, which apply the embedded ICC profile, not ImageSharp, which skips the conversion (its row is information only). CIEDE2000 of each library's sRGB output against an ICC-correct reference: the CMYK samples from our decoder converted through the embedded profile with this library's exact-math (floating point, no lookup grid) conversion, relative colorimetric with black point compensation. Computed on a deterministic sample of the pixels (every 11th column of every 7th row).").AppendLine();
            sb.AppendLine("Magick.NET (relative colorimetric) and NetVips do not apply black point compensation unless asked; this library does by default, so its default output sits about 0.9 ΔE00 from theirs. The `oneimage-nobpc` row is this library with `blackPointCompensation: false` and matches them.").AppendLine();
            sb.AppendLine("The same output compared with the LittleCMS result (Magick.NET) as the reference is in the last two columns.").AppendLine();
            sb.AppendLine("| Library | Mean ΔE00 | Max ΔE00 | Pixels | Mean ΔE00 vs LittleCMS | Max ΔE00 vs LittleCMS |").AppendLine("|---|--:|--:|--:|--:|--:|");
            foreach (string line in File.ReadLines(args[5]))
            {
                string[] f = line.Split('\t');
                if (f.Length == 8 && f[0] == "DELTAE")
                {
                    sb.AppendLine(CultureInfo.InvariantCulture, $"| {f[2]}{(f[2] == "imagesharp" ? " (skips ICC conversion)" : "")} | {f[3]} | {f[4]} | {f[5]} | {f[6]} | {f[7]} |");
                }
            }

            sb.AppendLine();
        }

        sb.AppendLine("## Gaps").AppendLine();
        foreach (string gap in gaps)
        {
            sb.AppendLine(gap);
        }

        if (gaps.Count == 0)
        {
            sb.AppendLine("None.");
        }

        sb.AppendLine().AppendLine("## Rows").AppendLine().Append(detail);
        File.WriteAllText(args[4], sb.ToString());
        Console.WriteLine($"{rows.Count} rows, {peaks.Count} peak measurements, {gaps.Count} gaps -> {args[4]}");
        return 0;
    }

    private static string Fmt(double? v, int digits = 1) => v == null ? "-" : v.Value.ToString("F" + digits, CultureInfo.InvariantCulture);

    private static double D(string s) => double.Parse(s, CultureInfo.InvariantCulture);

    private static string Lib(string method)
    {
        string m = method.ToLowerInvariant();
        if (m.Contains("magick", StringComparison.Ordinal))
        {
            return "magick";
        }

        if (m.Contains("skia", StringComparison.Ordinal))
        {
            return "skia";
        }

        if (m.Contains("sharp", StringComparison.Ordinal))
        {
            return "imagesharp";
        }

        if (m.Contains("vips", StringComparison.Ordinal))
        {
            return "netvips";
        }

        return "oneimage";
    }

    private static Dictionary<string, string> TaskMap()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Type t in typeof(GateReport).Assembly.GetTypes())
        {
            foreach (MethodInfo mi in t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                BenchmarkCategoryAttribute? cat = mi.GetCustomAttribute<BenchmarkCategoryAttribute>();
                string? task = cat?.Categories.FirstOrDefault(c => c != "Gate");
                if (cat != null && task != null && cat.Categories.Contains("Gate"))
                {
                    map[t.FullName + "." + mi.Name] = task;
                }
            }
        }

        return map;
    }
}
