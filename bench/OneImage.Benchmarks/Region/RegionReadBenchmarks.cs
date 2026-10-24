using System.Threading;
using System.Collections.Generic;
using Microsoft.Win32.SafeHandles;
using System.Linq;
using System.IO;
using System.Diagnostics;
using System.Globalization;
using BenchmarkDotNet.Attributes;

namespace OneImage.Benchmarks.Region;

/// <summary>
/// Document-page inputs: a white page with text-like lines, a photo patch and mild sensor noise, written once per size and format
/// as a JPEG (baseline and progressive, 4:2:0) or a PNG into a directory. Deterministic.
/// </summary>
internal static class RegionData
{
    public static readonly (string Name, int Width, int Height, string Kind)[] Inputs =
    [
        ("a4-baseline.jpg", 2480, 3508, "jpeg"),
        ("a4-progressive.jpg", 2480, 3508, "jpeg-progressive"),
        ("a4-png.png", 2480, 3508, "png"),
        ("phone-baseline.jpg", 4032, 3024, "jpeg"),
        ("phone-orient6.jpg", 4032, 3024, "jpeg-orient6"),
    ];

    public static string Ensure(string dir, string name)
    {
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, name);
        if (File.Exists(path))
        {
            return path;
        }

        (string Name, int Width, int Height, string Kind) spec = Array.Find(Inputs, i => i.Name == name);
        Rgb24[] px = Page(spec.Width, spec.Height);
        Image image = Image.FromPixels<Rgb24>(px, spec.Width, spec.Height);
        using var fs = File.Create(path);
        switch (spec.Kind)
        {
            case "png":
                image.Save(fs, PngFormat.Instance);
                break;
            default:
                if (spec.Kind == "jpeg-orient6")
                {
                    // A one-entry EXIF block: orientation 6 (rotate 90 clockwise to view), as phones write for portrait shots.
                    byte[] exif = [0x4D, 0x4D, 0, 0x2A, 0, 0, 0, 8, 0, 1, 0x01, 0x12, 0, 3, 0, 0, 0, 1, 0, 6, 0, 0, 0, 0, 0, 0];
                    image = image.WithMetadata(new ImageMetadata { Exif = exif, Orientation = 6 });
                }

                image.Save(fs, JpegFormat.Instance, new SaveOptions
                {
                    EncoderOptions = new JpegEncoderOptions { Quality = 85, Progressive = spec.Kind == "jpeg-progressive" },
                });
                break;
        }

        return path;
    }

    public static Rgb24[] Page(int w, int h)
    {
        var px = new Rgb24[w * h];
        var rnd = new Random(11);
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                double v = 244 - (10.0 * x / w) - (6.0 * y / h);
                bool text = y > h / 10 && y < h * 6 / 10 && (y / 22) % 2 == 0 && x > w / 10 && x < w * 9 / 10 && ((x / 9) + (y / 22)) % 5 != 0;
                bool photo = x > w * 6 / 10 && x < w * 9 / 10 && y > h * 65 / 100 && y < h * 9 / 10;
                double r = v, g = v, b = v - 4;
                if (text)
                {
                    r = g = b = 40 + (((x * 7) + (y * 3)) % 17);
                }
                else if (photo)
                {
                    r = 128 + (90 * Math.Sin((x / 37.0) + (y / 53.0)));
                    g = 128 + (80 * Math.Cos((y / 29.0) - (x / 71.0)));
                    b = 90 + ((x - (w * 6 / 10)) * 0.2);
                }

                int noise = rnd.Next(-3, 4);
                px[(y * w) + x] = new Rgb24(C(r + noise), C(g + noise), C(b + noise));
            }
        }

        return px;
    }

    private static byte C(double v) => (byte)Math.Clamp((int)Math.Round(v), 0, 255);
}

/// <summary>The access patterns of a document-processing reader: header only, one whole read, ordered bands, random sub-rectangles.</summary>
internal static class RegionScenarios
{
    public const int BandCount = 44;

    public static readonly string[] Names =
    [
        "load", "whole-gray", "bands44-gray", "bands44-rgb", "random20-gray", "bands44-twice-gray", "random20-twice-gray",
    ];

    public static ImageRect[] Bands(int width, int height)
    {
        int bandHeight = (height + BandCount - 1) / BandCount;
        var rects = new List<ImageRect>();
        for (int y = 0; y < height; y += bandHeight)
        {
            rects.Add(new ImageRect(0, y, width, Math.Min(bandHeight, height - y)));
        }

        return [.. rects];
    }

    public static ImageRect[] Randoms(int width, int height)
    {
        var rnd = new Random(5);
        var rects = new ImageRect[20];
        for (int i = 0; i < rects.Length; i++)
        {
            int w = rnd.Next(200, Math.Min(1200, width) + 1);
            int h = rnd.Next(150, Math.Min(900, height) + 1);
            rects[i] = new ImageRect(rnd.Next(0, width - w + 1), rnd.Next(0, height - h + 1), w, h);
        }

        return rects;
    }

    /// <summary>Runs a scenario on an image already loaded; returns a value that depends on the pixels read.</summary>
    public static long Read(Image image, string scenario, byte[] buffer)
    {
        long sum = 0;
        switch (scenario)
        {
            case "load":
                return image.Width + image.Height;
            case "whole-gray":
                image.CopyPixelsTo<Gray8>(Gray(buffer), new ImageRect(0, 0, image.Width, image.Height));
                return buffer[buffer.Length / 2];
            case "bands44-gray":
            case "bands44-twice-gray":
                for (int pass = scenario.Contains("twice", StringComparison.Ordinal) ? 2 : 1; pass > 0; pass--)
                {
                    foreach (ImageRect r in Bands(image.Width, image.Height))
                    {
                        image.CopyPixelsTo<Gray8>(Gray(buffer), r);
                        sum += buffer[(r.Width * r.Height) / 2];
                    }
                }

                return sum;
            case "bands44-rgb":
                foreach (ImageRect r in Bands(image.Width, image.Height))
                {
                    image.CopyPixelsTo<Rgb24>(System.Runtime.InteropServices.MemoryMarshal.Cast<byte, Rgb24>(buffer.AsSpan()), r);
                    sum += buffer[(r.Width * r.Height) / 2];
                }

                return sum;
            case "random20-gray":
            case "random20-twice-gray":
                for (int pass = scenario.Contains("twice", StringComparison.Ordinal) ? 2 : 1; pass > 0; pass--)
                {
                    foreach (ImageRect r in Randoms(image.Width, image.Height))
                    {
                        image.CopyPixelsTo<Gray8>(Gray(buffer), r);
                        sum += buffer[(r.Width * r.Height) / 2];
                    }
                }

                return sum;
            default:
                throw new ArgumentException(scenario);
        }
    }

    private static Span<Gray8> Gray(byte[] buffer) => System.Runtime.InteropServices.MemoryMarshal.Cast<byte, Gray8>(buffer.AsSpan());

    public static byte[] Buffer(int width, int height) => new byte[(long)width * height * 3];

    /// <summary>Load, then read; the unit of the peak runner and the benchmarks.</summary>
    public static long LoadAndRead(string path, string scenario, byte[] buffer)
    {
        Image image = Image.Load(path);
        if (scenario == "load")
        {
            ImageInfo info = Image.Identify(path);
            return image.Width + info.Frames[0].Height;
        }

        return Read(image, scenario, buffer);
    }
}

/// <summary>Peak and resident memory of one scenario in one process: <c>region-peak &lt;scenario&gt; &lt;input&gt; [warmups] [count]</c>.</summary>
internal static class RegionPeak
{
    public static int Run(string[] args)
    {
        string dir = Environment.GetEnvironmentVariable("REGION_DATA") ?? Path.Combine(Path.GetTempPath(), "region-data");
        string scenario = args[1];
        string name = args[2];
        int warmups = args.Length > 3 && scenario != "cold-load" ? int.Parse(args[3], CultureInfo.InvariantCulture) : 2;
        int count = args.Length > 4 && scenario != "cold-load" ? int.Parse(args[4], CultureInfo.InvariantCulture) : 3;
        string path = RegionData.Ensure(dir, name);
        if (scenario == "cold-load")
        {
            return ColdLoad(path, args.Length > 3 ? args[3] : "path");
        }

        (string Name, int Width, int Height, string Kind) spec = Array.Find(RegionData.Inputs, i => i.Name == name);
        byte[] buffer = RegionScenarios.Buffer(spec.Width, spec.Height);

        for (int i = 0; i < warmups; i++)
        {
            RegionScenarios.LoadAndRead(path, scenario, buffer);
        }

        GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        File.WriteAllText("/proc/self/clear_refs", "5");
        long before = Status("VmRSS:");
        var sw = new Stopwatch();
        long keep = 0;
        for (int i = 0; i < count; i++)
        {
            sw.Start();
            keep += RegionScenarios.LoadAndRead(path, scenario, buffer);
            sw.Stop();
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: false);
        }

        double ms = sw.Elapsed.TotalMilliseconds / count;
        long peak = Status("VmHWM:");

        // Resident memory while one loaded image is still alive after the reads (what a reader that keeps its page pays).
        Image live = Image.Load(path);
        RegionScenarios.Read(live, scenario == "load" ? "load" : scenario, buffer);
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: false);
        long afterLive = Status("VmRSS:");
        Console.WriteLine($"PEAK\t{scenario}\t{name}\t{before / 1024.0:F1}\t{peak / 1024.0:F1}\t{(peak - before) / 1024.0:F1}\t{ms:F1}\t{(afterLive - before) / 1024.0:F1}\t{keep & 1}");
        GC.KeepAlive(live);
        return 0;
    }

    /// <summary>
    /// Resident set around loads in a fresh process: <c>region-peak cold-load &lt;input&gt; [path|stream|memory]</c> reads the file, then reports the resident-set change of the first
    /// Load (one-time costs included), a second Load (per-image cost), and 20 more kept images.
    /// </summary>
    private static int ColdLoad(string path, string mode)
    {
        byte[] bytes = File.ReadAllBytes(path);
        if (mode == "detail")
        {
            return ColdDetail(path, bytes);
        }

        long start = Status("VmRSS:");
        Image first = Load(path, bytes, mode);
        long afterFirst = Status("VmRSS:");
        Image second = Load(path, bytes, mode);
        long afterSecond = Status("VmRSS:");
        var keep = new List<Image> { first, second };
        for (int i = 0; i < 20; i++)
        {
            keep.Add(Load(path, bytes, mode));
        }

        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: false);
        long after22 = Status("VmRSS:");
        Console.WriteLine($"COLD\t{Path.GetFileName(path)}\t{mode}\tfile {bytes.Length / 1048576.0:F1}\tstart {start / 1024.0:F1}\tfirst Load +{(afterFirst - start) / 1024.0:F1}\tsecond +{(afterSecond - afterFirst) / 1024.0:F1}\t20 more +{(after22 - afterSecond) / 1024.0:F1} MB");
        GC.KeepAlive(keep);
        return 0;
    }

    // Where the first Load's memory goes: managed allocation, jitted code, and what the runtime committed.
    private static int ColdDetail(string path, byte[] bytes)
    {
        void Report(string step, long rss0, long alloc0, long jit0, long il0)
        {
            Console.WriteLine($"  {step,-28} rss +{(Status("VmRSS:") - rss0) / 1024.0,5:F1} MB  allocated +{(GC.GetTotalAllocatedBytes(false) - alloc0) / 1048576.0,5:F2} MB  jitted +{System.Runtime.JitInfo.GetCompiledMethodCount(false) - jit0,4} methods / {(System.Runtime.JitInfo.GetCompiledILBytes(false) - il0) / 1024.0,6:F0} KB IL");
        }

        Console.WriteLine($"DETAIL {Path.GetFileName(path)} start rss {Status("VmRSS:") / 1024.0:F1} MB");
        long rss = Status("VmRSS:");
        long alloc = GC.GetTotalAllocatedBytes(false);
        long jit = System.Runtime.JitInfo.GetCompiledMethodCount(false);
        long il = System.Runtime.JitInfo.GetCompiledILBytes(false);
        _ = ImageConfiguration.Default;
        Report("ImageConfiguration.Default", rss, alloc, jit, il);
        Pause("A");
        FileInfo fi = new(path);
        Report("FileInfo", rss, alloc, jit, il);
        using (SafeFileHandle h = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, FileOptions.RandomAccess))
        {
            Span<byte> hdr = stackalloc byte[64];
            RandomAccess.Read(h, hdr, 0);
        }

        Report("File.OpenHandle + read", rss, alloc, jit, il);
        using JitListener? listener = Environment.GetEnvironmentVariable("REGION_JITLIST") != null ? new JitListener() : null;
        Image a = Image.Load(path);
        listener?.Dump();
        Report("Load(path)", rss, alloc, jit, il);
        Pause("B");
        ImageInfo info = Image.Identify(new ReadOnlyMemory<byte>(bytes));
        Report("+ Identify(memory)", rss, alloc, jit, il);
        Image b = Image.Load(path);
        Report("+ Load(path) again", rss, alloc, jit, il);
        GC.KeepAlive(fi);
        GC.KeepAlive(a);
        GC.KeepAlive(b);
        GC.KeepAlive(info);
        return 0;
    }

    private static Image Load(string path, byte[] bytes, string mode)
    {
        switch (mode)
        {
            case "stream":
                using (var ms = new MemoryStream(bytes))
                {
                    Image i = Image.Load(ms);
                    _ = i.Width;
                    return i;
                }

            case "memory":
                return Image.Load(bytes);
            default:
                return Image.Load(path);
        }
    }

    // With REGION_PAUSE set, waits so an outside tool can snapshot /proc/<pid>/smaps at a known point.
    private static void Pause(string tag)
    {
        if (Environment.GetEnvironmentVariable("REGION_PAUSE") != null)
        {
            Console.WriteLine($"PAUSE {tag} {Environment.ProcessId}");
            Thread.Sleep(2500);
        }
    }

    private static long Status(string key)
    {
        foreach (string line in File.ReadLines("/proc/self/status"))
        {
            if (line.StartsWith(key, StringComparison.Ordinal))
            {
                return long.Parse(line.AsSpan(key.Length).Trim().TrimEnd(" kB".AsSpan()), CultureInfo.InvariantCulture);
            }
        }

        return 0;
    }
}

/// <summary>Lists the methods the JIT compiled while it is alive, largest IL first (to see what a first call costs).</summary>
internal sealed class JitListener : System.Diagnostics.Tracing.EventListener
{
    private readonly List<(string Name, long Il)> _methods = [];

    protected override void OnEventSourceCreated(System.Diagnostics.Tracing.EventSource source)
    {
        if (source.Name == "Microsoft-Windows-DotNETRuntime")
        {
            EnableEvents(source, System.Diagnostics.Tracing.EventLevel.Verbose, (System.Diagnostics.Tracing.EventKeywords)0x10);
        }
    }

    protected override void OnEventWritten(System.Diagnostics.Tracing.EventWrittenEventArgs e)
    {
        if (e.EventName?.StartsWith("MethodJittingStarted", StringComparison.Ordinal) == true && e.Payload != null)
        {
            lock (_methods)
            {
                _methods.Add(($"{e.Payload[4]}.{e.Payload[5]}", Convert.ToInt64(e.Payload[3], CultureInfo.InvariantCulture)));
            }
        }
    }

    public void Dump()
    {
        Thread.Sleep(300);
        lock (_methods)
        {
            foreach ((string name, long il) in _methods.OrderByDescending(m => m.Il).Take(12))
            {
                Console.WriteLine($"    jit {il,6} IL  {name}");
            }
        }
    }
}

/// <summary>BenchmarkDotNet rows for the region-read scenarios (file inputs are generated into <c>REGION_DATA</c>).</summary>
[MemoryDiagnoser]
public class RegionReadBenchmarks
{
    private string _path = null!;
    private byte[] _buffer = null!;
    private Image _loaded = null!;

    [Params("a4-baseline.jpg", "a4-progressive.jpg", "a4-png.png")]
    public string Input { get; set; } = "";

    [Params("load", "whole-gray", "bands44-gray", "random20-gray")]
    public string Scenario { get; set; } = "";

    [GlobalSetup]
    public void Setup()
    {
        string dir = Environment.GetEnvironmentVariable("REGION_DATA") ?? Path.Combine(Path.GetTempPath(), "region-data");
        _path = RegionData.Ensure(dir, Input);
        (string Name, int Width, int Height, string Kind) spec = Array.Find(RegionData.Inputs, i => i.Name == Input);
        _buffer = RegionScenarios.Buffer(spec.Width, spec.Height);
        _loaded = Image.Load(_path);
    }

    /// <summary>Load, then the scenario's reads.</summary>
    [Benchmark(Description = "load + scenario")]
    public long LoadAndRead() => RegionScenarios.LoadAndRead(_path, Scenario, _buffer);

    /// <summary>The scenario's reads on an image that was loaded (and read once) before the measurement.</summary>
    [Benchmark(Description = "scenario on a loaded image")]
    public long ReadLoaded()
    {
        if (Scenario == "load")
        {
            return _loaded.Width;
        }

        return RegionScenarios.Read(_loaded, Scenario, _buffer);
    }
}
