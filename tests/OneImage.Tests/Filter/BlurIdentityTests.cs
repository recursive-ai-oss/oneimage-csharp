using System.Security.Cryptography;
using Xunit;

namespace OneImage.Tests.Filter;

/// <summary>
/// Gaussian blur, box blur and unsharp mask output must stay byte for byte what it was before very wide blurs on tiny images
/// were given their own path. The reference file holds one hash per case; every case here is one the earlier code finished.
/// Set <c>ONEIMAGE_CAPTURE_BLUR</c> to a path to write a new reference file instead of comparing.
/// </summary>
public class BlurIdentityTests
{
    private static readonly (int W, int H)[] s_sizes = [(200, 150), (300, 40), (40, 300), (129, 130), (3, 300), (5, 300), (64, 64), (1, 400), (2, 90)];

    private static readonly double[] s_sigmas = [0.5, 3, 15.9, 16, 16.01, 17, 20, 30, 48];

    private static readonly (double X, double Y)[] s_anisotropic = [(18, 2), (2, 25), (40, 40)];

    private static readonly int[] s_radii = [1, 20, 47, 48, 49, 60, 100, 200];

    private static readonly double[] s_sharpenSigmas = [1, 15.5, 16, 16.2, 20, 40];

    private static readonly EdgeMode[] s_edges = [EdgeMode.Clamp, EdgeMode.Mirror, EdgeMode.Constant];

    /// <summary>The earlier code never finished when a sigma exceeded 16 times the length of its axis (or a radius 48 times the longer side); those cases are left out.</summary>
    private static bool Finishes(int w, int h, double sx, double sy) => sx <= 16.0 * w * 0.9 && sy <= 16.0 * h * 0.9 || (sx <= 16.0 && sy <= 16.0);

    private static bool BoxFinishes(int w, int h, int radius) => radius <= 48 || radius <= 40.0 * Math.Max(w, h);

    private static Image Source(string format, int w, int h)
    {
        var rng = new Random((w * 7919) + h);
        switch (format)
        {
            case "Rgba32":
                {
                    var p = new Rgba32[w * h];
                    for (int i = 0; i < p.Length; i++)
                    {
                        int x = i % w;
                        int y = i / w;
                        p[i] = new Rgba32((byte)((x * 255 / Math.Max(1, w)) ^ rng.Next(40)), (byte)((y * 255 / Math.Max(1, h)) ^ rng.Next(40)), (byte)rng.Next(256), (byte)(rng.Next(5) == 0 ? rng.Next(256) : 255));
                    }

                    return Image.FromPixels<Rgba32>(p, w, h);
                }

            case "Gray16":
                {
                    var p = new Gray16[w * h];
                    for (int i = 0; i < p.Length; i++)
                    {
                        p[i] = new Gray16((ushort)rng.Next(65536));
                    }

                    return Image.FromPixels<Gray16>(p, w, h);
                }

            default:
                {
                    var p = new GrayFloat[w * h];
                    for (int i = 0; i < p.Length; i++)
                    {
                        p[i] = new GrayFloat((float)rng.NextDouble());
                    }

                    return Image.FromPixels<GrayFloat>(p, w, h);
                }
        }
    }

    private static string Hash(Image image, string format)
    {
        var bytes = new List<byte>();
        switch (format)
        {
            case "Rgba32":
                foreach (Rgba32 p in image.ToArray<Rgba32>())
                {
                    bytes.AddRange([p.R, p.G, p.B, p.A]);
                }

                break;
            case "Gray16":
                foreach (Gray16 p in image.ToArray<Gray16>())
                {
                    bytes.AddRange(BitConverter.GetBytes(p.V));
                }

                break;
            default:
                foreach (GrayFloat p in image.ToArray<GrayFloat>())
                {
                    bytes.AddRange(BitConverter.GetBytes(p.V));
                }

                break;
        }

        return Convert.ToHexString(SHA256.HashData(bytes.ToArray()))[..16];
    }

    private static IEnumerable<(string Key, Func<Image, Image> Apply)> Operations(int w, int h)
    {
        foreach (EdgeMode edge in s_edges)
        {
            foreach (bool linear in new[] { true, false })
            {
                string tail = $"{edge}|{(linear ? "lin" : "enc")}";
                foreach (double s in s_sigmas)
                {
                    if (Finishes(w, h, s, s))
                    {
                        yield return ($"gauss|{s}|{tail}", i => i.GaussianBlur(s, null, linear, edge));
                    }
                }

                foreach ((double sx, double sy) in s_anisotropic)
                {
                    if (Finishes(w, h, sx, sy))
                    {
                        yield return ($"gauss|{sx},{sy}|{tail}", i => i.GaussianBlur(sx, sy, linear, edge));
                    }
                }

                foreach (int r in s_radii)
                {
                    if (BoxFinishes(w, h, r))
                    {
                        yield return ($"box|{r}|{tail}", i => i.BoxBlur(r, linear, edge));
                    }
                }

                foreach (double s in s_sharpenSigmas)
                {
                    if (Finishes(w, h, s, s))
                    {
                        yield return ($"sharpen|{s}|{tail}", i => i.Sharpen(1.5, s, 0.02, linear, edge));
                    }
                }
            }
        }
    }

    [Fact]
    public void BlurOutputMatchesReference()
    {
        string? capture = Environment.GetEnvironmentVariable("ONEIMAGE_CAPTURE_BLUR");
        var reference = new Dictionary<string, string>();
        if (capture == null)
        {
            foreach (string line in File.ReadLines(Path.Combine(AppContext.BaseDirectory, "testdata", "filter", "blur-hashes.txt")))
            {
                int tab = line.IndexOf('\t');
                if (tab > 0)
                {
                    reference[line[..tab]] = line[(tab + 1)..];
                }
            }
        }

        var lines = new List<string>();
        var failures = new List<string>();
        int count = 0;
        foreach (string format in new[] { "Rgba32", "Gray16", "GrayFloat" })
        {
            foreach ((int w, int h) in s_sizes)
            {
                Image source = Source(format, w, h);
                foreach ((string key, Func<Image, Image> apply) in Operations(w, h))
                {
                    // Float and 16-bit pixels in linear light use the platform's math library; compare them on Linux only.
                    string full = $"{format}|{w}x{h}|{key}";
                    string hash = Hash(apply(source), format);
                    lines.Add($"{full}\t{hash}");
                    count++;
                    bool comparable = OperatingSystem.IsLinux() || format == "Rgba32" || key.EndsWith("enc", StringComparison.Ordinal);
                    if (capture == null && comparable && (!reference.TryGetValue(full, out string? want) || want != hash))
                    {
                        failures.Add(full);
                    }
                }
            }
        }

        if (capture != null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(capture)!);
            File.WriteAllLines(capture, lines);
            return;
        }

        Assert.True(failures.Count == 0, $"{failures.Count} of {count} differ from the reference, first: {string.Join("; ", failures.Take(8))}");
        Assert.True(count > 2000, $"only {count} cases");
    }
}
