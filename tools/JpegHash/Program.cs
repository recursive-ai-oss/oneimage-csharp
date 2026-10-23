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
        if (args.Length >= 2 && args[0] == "gen")
        {
            Generate(args[1]);
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

        Console.Error.WriteLine("usage: gen <dir> | gen-small <dir> | hash <file-or-dir>... | time <file> [scale]");
        return 2;
    }

    private static Rgb24[] Photo()
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
                int amp = ((x / 500) + (y / 400)) % 2 == 0 ? 24 : 6;
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
