using System.Security.Cryptography;

namespace OneImage.Tools;

/// <summary>
/// Decode-hash tool for the JPEG decoder. <c>gen &lt;dir&gt;</c> writes 24 MP baseline files with and without restart
/// intervals; <c>hash &lt;file-or-dir&gt;...</c> prints a SHA-256 of the decoded pixels of each file at scales 1, 1/2, 1/4 and
/// 1/8 and at per-call degrees 1, 4 and 16, so two builds (or two hardware settings) can be diffed line by line.
/// A file that fails to decode prints the exception type instead of a hash.
/// </summary>
internal static class Program
{
    private const int Width = 6000;
    private const int Height = 4000;

    private static int Main(string[] args)
    {
        // JPEG_PIPELINE=0 decodes scans one after the other, 2 or more forces side-by-side scans; JPEG_MASKREFINE=0 uses the coefficient-by-coefficient refinement.
        if (Environment.GetEnvironmentVariable("JPEG_PIPELINE") is { } pl)
        {
            OneImage.Codecs.Jpeg.JpegScanPipeline.Override = int.Parse(pl);
        }

        if (Environment.GetEnvironmentVariable("JPEG_MASKREFINE") is { } mr)
        {
            OneImage.Codecs.Jpeg.JpegReader.UseMaskRefine = mr != "0";
        }

        if (args.Length >= 2 && args[0] == "gen")
        {
            Generate(args[1]);
            return 0;
        }

        if (args.Length >= 2 && args[0] == "gen-prog")
        {
            GenerateProgressive(args[1]);
            return 0;
        }

        if (args[0] == "maskbench")
        {
            var blocks = new short[1_500_000 * 64];
            var rnd0 = new Random(1);
            for (int i = 0; i < blocks.Length; i++) { if (rnd0.Next(4) == 0) blocks[i] = 1; }
            for (int rep = 0; rep < 5; rep++)
            {
                long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                ulong acc = 0;
                for (int b = 0; b < 1_500_000; b++) { acc += OneImage.Kernels.JpegRefineKernels.NonzeroZigzagMask(blocks.AsSpan(b * 64, 64)); }
                Console.WriteLine($"{System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds:F1} ms {acc}");
            }
            return 0;
        }

        if (args.Length >= 3 && args[0] == "stress")
        {
            byte[] jpeg = File.ReadAllBytes(args[1]);
            var counts = new SortedDictionary<string, int>();
            for (int i = 0; i < int.Parse(args[2]); i++)
            {
                string r = Decode(jpeg, 1);
                counts[r] = counts.GetValueOrDefault(r) + 1;
            }

            foreach (var kv in counts) { Console.WriteLine($"{kv.Value} {kv.Key}"); }
            return 0;
        }

        if (args.Length >= 2 && args[0] == "diff")
        {
            byte[] jpeg = File.ReadAllBytes(args[1]);
            OneImage.Codecs.Jpeg.JpegScanPipeline.Override = 0;
            Rgb24[] reference = Image.Load(jpeg).ToArray<Rgb24>();
            OneImage.Codecs.Jpeg.JpegScanPipeline.Override = 3;
            for (int i = 0; i < 500; i++)
            {
                Image img = Image.Load(jpeg);
                Rgb24[] got = img.ToArray<Rgb24>();
                int bad = 0, minx = int.MaxValue, maxx = -1, miny = int.MaxValue, maxy = -1;
                for (int j = 0; j < got.Length; j++)
                {
                    if (!got[j].Equals(reference[j])) { bad++; int x = j % img.Width, y = j / img.Width; minx = Math.Min(minx, x); maxx = Math.Max(maxx, x); miny = Math.Min(miny, y); maxy = Math.Max(maxy, y); }
                }

                if (bad > 0) { Console.WriteLine($"run {i}: {bad} pixels differ, x {minx}..{maxx} y {miny}..{maxy}; first: ref {reference[miny * img.Width + minx]} got {got[miny * img.Width + minx]}"); }
            }

            return 0;
        }

        if (args.Length >= 3 && args[0] == "allocs")
        {
            byte[] jpeg = File.ReadAllBytes(args[1]);
            int scale = int.Parse(args[2]);
            for (int round = 0; round < 3; round++)
            {
                long a0 = GC.GetTotalAllocatedBytes(true);
                for (int i = 0; i < 10; i++)
                {
                    using var ms = new MemoryStream(jpeg);
                    ImageInfo info = JpegFormat.Instance.ReadInfo(ms, ImageLimits.Default);
                    FrameInfo fi = info.Frames[0];
                    int ow = (fi.Width + scale - 1) / scale;
                    int oh = (fi.Height + scale - 1) / scale;
                    var sink = new NullSink(ow, oh, fi.Format);
                    JpegFormat.Instance.DecodeFrame(ms, info, 0, new DecodeRequest(new ImageRect(0, 0, ow, oh), scale), sink, ImageLimits.Default, System.Threading.CancellationToken.None);
                }

                Console.WriteLine($"decoder only 1/{scale}: {(GC.GetTotalAllocatedBytes(true) - a0) / 10 / 1024} KB per decode");
            }

            return 0;
        }

        if (args[0] == "poolbench")
        {
            foreach (int size in new[] { 1 << 16, 1 << 20, 1 << 22, 1 << 24 })
            {
                for (int round = 0; round < 3; round++)
                {
                    long a0 = GC.GetTotalAllocatedBytes(true);
                    for (int i = 0; i < 20; i++)
                    {
                        byte[] b = System.Buffers.ArrayPool<byte>.Shared.Rent(size);
                        System.Buffers.ArrayPool<byte>.Shared.Return(b);
                    }

                    Console.WriteLine($"size {size}: {(GC.GetTotalAllocatedBytes(true) - a0) / 20 / 1024} KB per rent/return");
                }
            }

            return 0;
        }

        if (args.Length >= 2 && args[0] == "gen-small-prog")
        {
            GenerateSmallProgressive(args[1]);
            return 0;
        }

        if (args.Length >= 2 && args[0] == "gen-small")
        {
            GenerateSmall(args[1]);
            return 0;
        }

        if (args.Length >= 2 && args[0] == "hash")
        {
            Hash(args[1..]);
            return 0;
        }

        if (args.Length >= 2 && args[0] == "time")
        {
            Time(args[1], args.Length > 2 ? int.Parse(args[2]) : 1);
            return 0;
        }

        Console.Error.WriteLine("usage: gen <dir> | gen-prog <dir> | gen-small-prog <dir> | gen-small <dir> | hash <file-or-dir>... | time <file> [scale]");
        return 2;
    }

    private static Rgb24[] Photo(bool noisy = false)
    {
        var px = new Rgb24[Width * Height];
        var rnd = new Random(11);
        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                double r = 128 + (90 * Math.Sin((x / 137.0) + (y / 253.0)));
                double g = 128 + (80 * Math.Cos((y / 129.0) - (x / 371.0)));
                double b = 60 + (x * 140.0 / Width) + (y * 50.0 / Height);
                if (((x / 400) + (y / 300)) % 3 == 0)
                {
                    r = 230 - (r * 0.3);
                }

                // Sensor-like noise, strongest in the busy tiles, so the entropy-coded data is large.
                int amp = noisy ? 48 : ((x / 500) + (y / 400)) % 2 == 0 ? 24 : 6;
                double tex = rnd.Next(-amp, amp + 1) + (5 * Math.Sin(x * 0.9) * Math.Cos(y * 0.7));
                px[(y * Width) + x] = new Rgb24(C(r + tex), C(g + tex), C(b + tex));
            }
        }

        return px;
    }

    private static byte C(double v) => (byte)Math.Clamp((int)Math.Round(v), 0, 255);

    private static void Generate(string dir)
    {
        Directory.CreateDirectory(dir);
        Rgb24[] px = Photo();
        var image = Image.FromPixels<Rgb24>(px, Width, Height);

        // Restart intervals in MCUs: none, 8, one 4:2:0 row (375), 1000, 3 rows, a fifth of the image.
        foreach (int restart in new[] { 0, 8, 375, 1000, 1125, 30000 })
        {
            foreach ((string name, JpegSubsampling sub) in new[] { ("420", JpegSubsampling.Yuv420), ("444", JpegSubsampling.Yuv444) })
            {
                string path = Path.Combine(dir, $"photo-{name}-r{restart}.jpg");
                using var fs = File.Create(path);
                image.Save(fs, JpegFormat.Instance, new SaveOptions { EncoderOptions = new JpegEncoderOptions { Quality = 90, Subsampling = sub, RestartInterval = restart } });
                Console.WriteLine($"{path} {fs.Length}");
            }
        }
    }

    // 24 MP progressive files: smooth and noisy content, 4:2:0 and 4:4:4, the default scan script (with successive
    // approximation) and a spectral-selection-only script, and a few with restart intervals.
    private static void GenerateProgressive(string dir)
    {
        Directory.CreateDirectory(dir);
        foreach (bool noisy in new[] { false, true })
        {
            var image = Image.FromPixels<Rgb24>(Photo(noisy), Width, Height);
            foreach ((string name, JpegSubsampling sub) in new[] { ("420", JpegSubsampling.Yuv420), ("444", JpegSubsampling.Yuv444) })
            {
                foreach (bool plain in new[] { false, true })
                {
                    foreach (int restart in new[] { 0, 375 })
                    {
                        if (restart != 0 && name == "444")
                        {
                            continue;
                        }

                        string path = Path.Combine(dir, $"prog-{(noisy ? "noisy" : "smooth")}-{name}-{(plain ? "spectral" : "sa")}-r{restart}.jpg");
                        OneImage.Codecs.Jpeg.JpegEncodeSession.SpectralOnlyScript = plain;
                        using var fs = File.Create(path);
                        image.Save(fs, JpegFormat.Instance, new SaveOptions { EncoderOptions = new JpegEncoderOptions { Quality = noisy ? 95 : 90, Subsampling = sub, Progressive = true, RestartInterval = restart } });
                        Console.WriteLine($"{path} {fs.Length}");
                    }
                }
            }
        }

        OneImage.Codecs.Jpeg.JpegEncodeSession.SpectralOnlyScript = false;
    }

    // Small progressive files for the regression corpus and as fuzzing seeds: spectral selection only, and successive approximation with restart intervals.
    private static void GenerateSmallProgressive(string dir)
    {
        Directory.CreateDirectory(dir);
        const int w = 104;
        const int h = 72;
        var rnd = new Random(9);
        var px = new Rgb24[w * h];
        for (int i = 0; i < px.Length; i++)
        {
            int x = i % w;
            int y = i / w;
            px[i] = new Rgb24(C((x * 2) + rnd.Next(-40, 41)), C((y * 3) + rnd.Next(-40, 41)), C(((x + y) * 1.5) + rnd.Next(-20, 21)));
        }

        var image = Image.FromPixels<Rgb24>(px, w, h);
        foreach ((string name, JpegSubsampling sub, int restart, bool plain) in new[] { ("prog-spectral-420", JpegSubsampling.Yuv420, 0, true), ("prog-spectral-444-restart", JpegSubsampling.Yuv444, 6, true), ("prog-sa-422-restart", JpegSubsampling.Yuv422, 5, false) })
        {
            string path = Path.Combine(dir, name + ".jpg");
            OneImage.Codecs.Jpeg.JpegEncodeSession.SpectralOnlyScript = plain;
            using var fs = File.Create(path);
            image.Save(fs, JpegFormat.Instance, new SaveOptions { EncoderOptions = new JpegEncoderOptions { Quality = 88, Subsampling = sub, Progressive = true, RestartInterval = restart } });
            Console.WriteLine($"{path} {fs.Length}");
        }

        OneImage.Codecs.Jpeg.JpegEncodeSession.SpectralOnlyScript = false;
    }

    // Small files with many restart intervals, for the regression corpus and as fuzzing seeds.
    private static void GenerateSmall(string dir)
    {
        Directory.CreateDirectory(dir);
        const int w = 96;
        const int h = 64;
        var rnd = new Random(3);
        var px = new Rgb24[w * h];
        for (int i = 0; i < px.Length; i++)
        {
            int x = i % w;
            int y = i / w;
            px[i] = new Rgb24(C((x * 2) + rnd.Next(-30, 31)), C((y * 3) + rnd.Next(-30, 31)), C(((x + y) * 1.5) + rnd.Next(-10, 11)));
        }

        var image = Image.FromPixels<Rgb24>(px, w, h);
        foreach ((string name, JpegSubsampling sub, int restart) in new[] { ("420-r1", JpegSubsampling.Yuv420, 1), ("420-r4", JpegSubsampling.Yuv420, 4), ("444-r5", JpegSubsampling.Yuv444, 5), ("422-r2", JpegSubsampling.Yuv422, 2) })
        {
            string path = Path.Combine(dir, $"restart-many-{name}.jpg");
            using var fs = File.Create(path);
            image.Save(fs, JpegFormat.Instance, new SaveOptions { EncoderOptions = new JpegEncoderOptions { Quality = 80, Subsampling = sub, RestartInterval = restart } });
            Console.WriteLine($"{path} {fs.Length}");
        }
    }

    private static void Hash(string[] paths)
    {
        var files = new List<string>();
        foreach (string p in paths)
        {
            if (Directory.Exists(p))
            {
                files.AddRange(Directory.GetFiles(p, "*.jpg"));
            }
            else
            {
                files.Add(p);
            }
        }

        files.Sort(StringComparer.Ordinal);
        Console.Error.WriteLine($"Avx2.IsSupported={System.Runtime.Intrinsics.X86.Avx2.IsSupported} Vector256.IsHardwareAccelerated={System.Runtime.Intrinsics.Vector256.IsHardwareAccelerated} Vector512.IsHardwareAccelerated={System.Runtime.Intrinsics.Vector512.IsHardwareAccelerated} Vector128.IsHardwareAccelerated={System.Runtime.Intrinsics.Vector128.IsHardwareAccelerated}");
        int saved = ImageEngine.DefaultCallDegree;
        try
        {
            foreach (string file in files)
            {
                byte[] jpeg = File.ReadAllBytes(file);
                foreach (int scale in new[] { 1, 2, 4, 8 })
                {
                    foreach (int degree in new[] { 1, 4, 16 })
                    {
                        ImageEngine.DefaultCallDegree = degree;
                        Console.WriteLine($"{Path.GetFileName(file)} 1/{scale} d{degree} {Decode(jpeg, scale)}");
                    }
                }
            }
        }
        finally
        {
            ImageEngine.DefaultCallDegree = saved;
        }
    }

    private static void Time(string file, int scale)
    {
        byte[] jpeg = File.ReadAllBytes(file);
        Rgb24[]? buffer = null;
        var times = new List<double>();
        for (int i = 0; i < 40; i++)
        {
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            Image img = Image.Load(jpeg);
            Image scaled = scale == 1 ? img : img.Resize(width: (img.Width + scale - 1) / scale, height: (img.Height + scale - 1) / scale);
            buffer ??= new Rgb24[scaled.Width * scaled.Height];
            scaled.CopyPixelsTo<Rgb24>(buffer, new ImageRect(0, 0, scaled.Width, scaled.Height));
            times.Add(System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds);
        }

        long a0 = GC.GetTotalAllocatedBytes(true);
        for (int i = 0; i < 10; i++) { Image img = Image.Load(jpeg); Image scaled = scale == 1 ? img : img.Resize(width: (img.Width + scale - 1) / scale, height: (img.Height + scale - 1) / scale); scaled.CopyPixelsTo<Rgb24>(buffer!, new ImageRect(0, 0, scaled.Width, scaled.Height)); }
        Console.WriteLine($"allocated per decode: {(GC.GetTotalAllocatedBytes(true) - a0) / 10 / 1024} KB");
        times.RemoveRange(0, 10);
        times.Sort();
        Console.WriteLine($"{Path.GetFileName(file)} 1/{scale} degree {ImageEngine.DefaultCallDegree}: median {times[times.Count / 2]:F1} ms, min {times[0]:F1} ms");
    }

    private static string Decode(byte[] jpeg, int scale)
    {
        try
        {
            Image img = Image.Load(jpeg);
            int w = (img.Width + scale - 1) / scale;
            int h = (img.Height + scale - 1) / scale;
            Image scaled = scale == 1 ? img : img.Resize(width: w, height: h);
            return Convert.ToHexString(SHA256.HashData(System.Runtime.InteropServices.MemoryMarshal.AsBytes(scaled.ToArray<Rgb24>().AsSpan()))) + $" {scaled.Width}x{scaled.Height}";
        }
        catch (ImageFormatException e)
        {
            return "error:" + e.GetType().Name;
        }
        catch (ImageLimitException e)
        {
            return "error:" + e.GetType().Name;
        }
    }
}

internal sealed class NullSink : IRowSink
{
    public NullSink(int width, int height, PixelFormat format)
    {
        Width = width;
        Height = height;
        Format = format;
    }

    public int Width { get; }

    public int Height { get; }

    public PixelFormat Format { get; }

    public void WriteRows(int y, ReadOnlySpan<byte> rows, int rowCount)
    {
    }
}
