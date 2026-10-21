using System.Buffers.Binary;
using System.Globalization;
using OneImage.Kernels;

namespace OneImage.Tools;

/// <summary>
/// Builds the generic CMYK lookup tables from the unmodified FOGRA39 characterization data.
///
/// Usage: GenericCmyk data/FOGRA39.txt src/OneImage/Color/Data/generic-cmyk.bin [--search]
///
/// A2B: the 1485 measured patches are interpolated with a polyharmonic spline (cubic kernel plus a linear term) in
/// dot-gain-warped ink space, expressed as paper-relative CIELAB, then sampled onto a regular grid with per-ink input
/// curves. B2A: for every CIELAB grid node an ink combination is found by Levenberg-Marquardt on the A2B table with a
/// black generation rule and a 300% total ink limit; out-of-gamut targets are pulled toward the neutral axis.
/// Everything is deterministic: no random numbers, per-node independent work.
/// </summary>
internal static class Program
{
    private const double TotalInkLimit = 3.0;
    private const double D50X = 0.9642, D50Z = 0.8249;

    private static int Main(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("usage: GenericCmyk <FOGRA39.txt> <output.bin> [--search]");
            return 2;
        }

        Sample[] samples = LoadSamples(args[0], out double[] paper);
        Console.WriteLine($"{samples.Length} distinct patches, paper XYZ {paper[0]:F2} {paper[1]:F2} {paper[2]:F2}");
        if (args.Length > 2 && args[2] == "--search")
        {
            foreach (double p in new[] { 1.0, 1.3, 1.6, 2.0 })
            {
                foreach (int g in new[] { 9, 11, 13, 17 })
                {
                    var rbf = new Rbf(samples, p, 0.0);
                    A2B a2b = A2B.Build(rbf, g, p);
                    (double mean, double max) = a2b.Fit(samples);
                    Console.WriteLine($"warp p={p} grid={g}: mean dE00 {mean:F3} max {max:F3}  ({g * g * g * g * 6 / 1024} KB)");
                }
            }

            return 0;
        }

        if (args.Length > 2 && args[2] == "--cv")
        {
            foreach (double p in new[] { 1.0, 1.3, 1.6 })
            {
                foreach (double lambda in new[] { 0.0, 1e-4, 1e-3 })
                {
                    double sum = 0, max = 0;
                    int count = 0;
                    for (int fold = 0; fold < 5; fold++)
                    {
                        Sample[] train = samples.Where((s, i) => i % 5 != fold || s.Ink.All(v => v is 0 or 1)).ToArray();
                        var rbf = new Rbf(train, p, lambda);
                        for (int i = fold; i < samples.Length; i += 5)
                        {
                            if (samples[i].Ink.All(v => v is 0 or 1))
                            {
                                continue;
                            }

                            double[] lab = rbf.Eval(samples[i].Ink.Select(x => Warp(x, p)).ToArray());
                            double e = DeltaE2000(lab, samples[i].Lab);
                            sum += e;
                            max = Math.Max(max, e);
                            count++;
                        }
                    }

                    Console.WriteLine($"cv warp p={p} smoothing={lambda}: mean dE00 {sum / count:F3} max {max:F3}");
                }
            }

            return 0;
        }

        const double warp = 1.0;
        const int gridA2B = 11;
        const int gridB2A = 17;
        var rbfFit = new Rbf(samples, warp, 0.0);
        A2B table = A2B.Build(rbfFit, gridA2B, warp);
        (double m, double mx) = table.Fit(samples);
        Console.WriteLine($"A2B grid {gridA2B}, warp {warp}: mean dE00 {m:F3}, max {mx:F3}");

        byte[] a2bTag = table.ToLut16();
        byte[] b2aTag = B2A.Build(table, gridB2A);
        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms))
        {
            w.Write(0x31504D43u); // 'CMP1'
            for (int i = 0; i < 3; i++)
            {
                w.Write(BitConverter.DoubleToInt64Bits(paper[i] / 100.0));
            }

            w.Write(a2bTag.Length);
            w.Write(a2bTag);
            w.Write(b2aTag.Length);
            w.Write(b2aTag);
        }

        File.WriteAllBytes(args[1], ms.ToArray());
        Console.WriteLine($"wrote {args[1]}: A2B {a2bTag.Length} bytes, B2A {b2aTag.Length} bytes");
        return 0;
    }

    private readonly record struct Sample(double[] Ink, double[] Lab);

    private static Sample[] LoadSamples(string path, out double[] paper)
    {
        var map = new Dictionary<string, (double[] Ink, double[] Xyz, int N)>();
        bool inData = false;
        foreach (string raw in File.ReadLines(path))
        {
            string line = raw.Trim();
            if (line == "BEGIN_DATA")
            {
                inData = true;
                continue;
            }

            if (line == "END_DATA")
            {
                break;
            }

            if (!inData || line.Length == 0)
            {
                continue;
            }

            string[] f = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            double[] ink = [Parse(f[1]) / 100, Parse(f[2]) / 100, Parse(f[3]) / 100, Parse(f[4]) / 100];
            double[] xyz = [Parse(f[5]), Parse(f[6]), Parse(f[7])];
            string key = string.Join(',', ink.Select(v => v.ToString("F4", CultureInfo.InvariantCulture)));
            if (map.TryGetValue(key, out (double[] Ink, double[] Xyz, int N) e))
            {
                for (int i = 0; i < 3; i++)
                {
                    e.Xyz[i] += xyz[i];
                }

                map[key] = (e.Ink, e.Xyz, e.N + 1);
            }
            else
            {
                map[key] = (ink, xyz, 1);
            }
        }

        var list = map.Values.OrderBy(v => v.Ink[0]).ThenBy(v => v.Ink[1]).ThenBy(v => v.Ink[2]).ThenBy(v => v.Ink[3]).ToList();
        (double[] Ink, double[] Xyz, int N) white = list.First(v => v.Ink.All(x => x == 0));
        double[] pw = white.Xyz.Select(x => x / white.N).ToArray();
        paper = pw;
        return list.Select(v =>
        {
            double[] xyz = [v.Xyz[0] / v.N, v.Xyz[1] / v.N, v.Xyz[2] / v.N];

            // Paper-relative colorimetry: scale each channel so the paper becomes the D50 white (ICC relative colorimetric).
            double x = xyz[0] / pw[0] * D50X;
            double y = xyz[1] / pw[1];
            double z = xyz[2] / pw[2] * D50Z;
            return new Sample(v.Ink, XyzToLab(x, y, z));
        }).ToArray();
    }

    private static double Parse(string s) => double.Parse(s, CultureInfo.InvariantCulture);

    internal static double[] XyzToLab(double x, double y, double z)
    {
        double fx = F(x / D50X), fy = F(y), fz = F(z / D50Z);
        return [(116 * fy) - 16, 500 * (fx - fy), 200 * (fy - fz)];
    }

    internal static double[] LabToXyz(double l, double a, double b)
    {
        double fy = (l + 16) / 116, fx = fy + (a / 500), fz = fy - (b / 200);
        return [FInv(fx) * D50X, FInv(fy), FInv(fz) * D50Z];
    }

    private static double F(double t) => t > 216.0 / 24389 ? Math.Cbrt(t) : (((24389.0 / 27 * t) + 16) / 116);

    private static double FInv(double f) => f * f * f > 216.0 / 24389 ? f * f * f : (((116 * f) - 16) * 27 / 24389);

    /// <summary>The dot-gain warp: ink coverage to table coordinate. Denser grid nodes at low coverage, where color changes fastest.</summary>
    internal static double Warp(double x, double p) => 1 - Math.Pow(1 - x, p);

    internal static double Unwarp(double t, double p) => 1 - Math.Pow(1 - t, 1 / p);

    internal static double DeltaE2000(double[] c1, double[] c2)
    {
        const double Rad = Math.PI / 180;
        double l1 = c1[0], a1 = c1[1], b1 = c1[2], l2 = c2[0], a2 = c2[1], b2 = c2[2];
        double cb = (Math.Sqrt((a1 * a1) + (b1 * b1)) + Math.Sqrt((a2 * a2) + (b2 * b2))) / 2;
        double g = 0.5 * (1 - Math.Sqrt(Math.Pow(cb, 7) / (Math.Pow(cb, 7) + Math.Pow(25, 7))));
        double ap1 = (1 + g) * a1, ap2 = (1 + g) * a2;
        double cp1 = Math.Sqrt((ap1 * ap1) + (b1 * b1)), cp2 = Math.Sqrt((ap2 * ap2) + (b2 * b2));
        double hp1 = (ap1 == 0 && b1 == 0) ? 0 : Math.Atan2(b1, ap1) / Rad;
        double hp2 = (ap2 == 0 && b2 == 0) ? 0 : Math.Atan2(b2, ap2) / Rad;
        if (hp1 < 0)
        {
            hp1 += 360;
        }

        if (hp2 < 0)
        {
            hp2 += 360;
        }

        double dh = cp1 * cp2 == 0 ? 0 : Math.Abs(hp2 - hp1) <= 180 ? hp2 - hp1 : hp2 - hp1 > 180 ? hp2 - hp1 - 360 : hp2 - hp1 + 360;
        double dH = 2 * Math.Sqrt(cp1 * cp2) * Math.Sin(dh * Rad / 2);
        double lb = (l1 + l2) / 2, cpb = (cp1 + cp2) / 2;
        double hb = cp1 * cp2 == 0 ? hp1 + hp2 : Math.Abs(hp1 - hp2) <= 180 ? (hp1 + hp2) / 2 : hp1 + hp2 < 360 ? (hp1 + hp2 + 360) / 2 : (hp1 + hp2 - 360) / 2;
        double t = 1 - (0.17 * Math.Cos((hb - 30) * Rad)) + (0.24 * Math.Cos(2 * hb * Rad)) + (0.32 * Math.Cos(((3 * hb) + 6) * Rad)) - (0.20 * Math.Cos(((4 * hb) - 63) * Rad));
        double dTheta = 30 * Math.Exp(-Math.Pow((hb - 275) / 25, 2));
        double rc = 2 * Math.Sqrt(Math.Pow(cpb, 7) / (Math.Pow(cpb, 7) + Math.Pow(25, 7)));
        double sl = 1 + (0.015 * Math.Pow(lb - 50, 2) / Math.Sqrt(20 + Math.Pow(lb - 50, 2)));
        double sc = 1 + (0.045 * cpb), sh = 1 + (0.015 * cpb * t);
        double rt = -Math.Sin(2 * dTheta * Rad) * rc;
        double dl = (l2 - l1) / sl, dc = (cp2 - cp1) / sc, dhh = dH / sh;
        return Math.Sqrt((dl * dl) + (dc * dc) + (dhh * dhh) + (rt * dc * dhh));
    }

    /// <summary>Polyharmonic spline (kernel r^3 with an affine term) through the measured patches.</summary>
    private sealed class Rbf
    {
        private readonly double[][] _pts;
        private readonly double[][] _w; // per output: n kernel weights then 5 affine coefficients
        private readonly double _p;

        public Rbf(Sample[] samples, double warp, double smoothing)
        {
            _p = warp;
            int n = samples.Length;
            _pts = samples.Select(s => s.Ink.Select(x => Warp(x, warp)).ToArray()).ToArray();
            int dim = n + 5;
            var a = new double[dim * dim];
            for (int i = 0; i < n; i++)
            {
                for (int j = 0; j < n; j++)
                {
                    double r = Dist(_pts[i], _pts[j]);
                    a[(i * dim) + j] = r * r * r;
                }

                a[(i * dim) + i] += smoothing;
                a[(i * dim) + n] = 1;
                a[(n * dim) + i] = 1;
                for (int k = 0; k < 4; k++)
                {
                    a[(i * dim) + n + 1 + k] = _pts[i][k];
                    a[((n + 1 + k) * dim) + i] = _pts[i][k];
                }
            }

            var rhs = new double[3][];
            for (int o = 0; o < 3; o++)
            {
                rhs[o] = new double[dim];
                for (int i = 0; i < n; i++)
                {
                    rhs[o][i] = samples[i].Lab[o];
                }
            }

            _w = Solve(a, dim, rhs);
        }

        public double[] Eval(double[] warped)
        {
            int n = _pts.Length;
            var result = new double[3];
            for (int o = 0; o < 3; o++)
            {
                double[] w = _w[o];
                double s = w[n] + (w[n + 1] * warped[0]) + (w[n + 2] * warped[1]) + (w[n + 3] * warped[2]) + (w[n + 4] * warped[3]);
                for (int i = 0; i < n; i++)
                {
                    double r = Dist(_pts[i], warped);
                    s += w[i] * r * r * r;
                }

                result[o] = s;
            }

            return result;
        }

        public double Warp => _p;

        private static double Dist(double[] a, double[] b)
        {
            double d0 = a[0] - b[0], d1 = a[1] - b[1], d2 = a[2] - b[2], d3 = a[3] - b[3];
            return Math.Sqrt((d0 * d0) + (d1 * d1) + (d2 * d2) + (d3 * d3));
        }

        /// <summary>Dense LU solve with partial pivoting for several right-hand sides.</summary>
        private static double[][] Solve(double[] a, int n, double[][] rhs)
        {
            var piv = new int[n];
            for (int k = 0; k < n; k++)
            {
                int p = k;
                double best = Math.Abs(a[(k * n) + k]);
                for (int i = k + 1; i < n; i++)
                {
                    double v = Math.Abs(a[(i * n) + k]);
                    if (v > best)
                    {
                        best = v;
                        p = i;
                    }
                }

                piv[k] = p;
                if (p != k)
                {
                    for (int j = 0; j < n; j++)
                    {
                        (a[(k * n) + j], a[(p * n) + j]) = (a[(p * n) + j], a[(k * n) + j]);
                    }
                }

                double d = a[(k * n) + k];
                Parallel.For(k + 1, n, i =>
                {
                    double f = a[(i * n) + k] / d;
                    a[(i * n) + k] = f;
                    if (f != 0)
                    {
                        for (int j = k + 1; j < n; j++)
                        {
                            a[(i * n) + j] -= f * a[(k * n) + j];
                        }
                    }
                });
            }

            var result = new double[rhs.Length][];
            for (int r = 0; r < rhs.Length; r++)
            {
                double[] x = (double[])rhs[r].Clone();
                for (int k = 0; k < n; k++)
                {
                    if (piv[k] != k)
                    {
                        (x[k], x[piv[k]]) = (x[piv[k]], x[k]);
                    }
                }

                for (int i = 1; i < n; i++)
                {
                    double s = x[i];
                    for (int j = 0; j < i; j++)
                    {
                        s -= a[(i * n) + j] * x[j];
                    }

                    x[i] = s;
                }

                for (int i = n - 1; i >= 0; i--)
                {
                    double s = x[i];
                    for (int j = i + 1; j < n; j++)
                    {
                        s -= a[(i * n) + j] * x[j];
                    }

                    x[i] = s / a[(i * n) + i];
                }

                result[r] = x;
            }

            return result;
        }
    }

    /// <summary>The device-to-PCS table: a grid over warped inks holding legacy-encoded CIELAB.</summary>
    private sealed class A2B
    {
        private const int InputEntries = 1024;
        private readonly Grid _grid;
        private readonly double _warp;

        private A2B(Grid grid, double warp)
        {
            _grid = grid;
            _warp = warp;
        }

        public int Points => _grid.Dims[0];

        public static A2B Build(Rbf rbf, int g, double warp)
        {
            var grid = new Grid(4, g, 3);
            int total = grid.Entries;
            Parallel.For(0, total, e =>
            {
                int rem = e;
                var t = new double[4];
                for (int a = 3; a >= 0; a--)
                {
                    t[a] = (rem % g) / (double)(g - 1);
                    rem /= g;
                }

                // Node t is at warped coordinate t; the RBF works in warped space already.
                double[] lab = rbf.Eval(t);
                grid.Data[(e * 4) + 0] = (float)EncodeL(lab[0]);
                grid.Data[(e * 4) + 1] = (float)EncodeAb(lab[1]);
                grid.Data[(e * 4) + 2] = (float)EncodeAb(lab[2]);
            });
            return new A2B(grid, warp);
        }

        /// <summary>Legacy 16-bit CIELAB (ICC.1 v2 lut16): L 0..100 maps to 0..0xFF00, a and b offset by 128 at 256 steps per unit.</summary>
        private static double EncodeL(double l) => Math.Clamp(l / 100 * 65280 / 65535, 0, 1);

        private static double EncodeAb(double v) => Math.Clamp((v + 128) * 256 / 65535, 0, 1);

        public double[] Lab(double c, double m, double y, double k)
        {
            Span<float> src = stackalloc float[4];
            src[0] = (float)Warp(c, _warp);
            src[1] = (float)Warp(m, _warp);
            src[2] = (float)Warp(y, _warp);
            src[3] = (float)Warp(k, _warp);
            Span<float> dst = stackalloc float[3];
            GridKernel.Eval(_grid, src, dst, 1);
            return
            [
                dst[0] * 65535.0 / 65280 * 100,
                (dst[1] * 65535.0 / 256) - 128,
                (dst[2] * 65535.0 / 256) - 128,
            ];
        }

        public (double Mean, double Max) Fit(Sample[] samples)
        {
            double sum = 0, max = 0;
            foreach (Sample s in samples)
            {
                double e = DeltaE2000(Lab(s.Ink[0], s.Ink[1], s.Ink[2], s.Ink[3]), s.Lab);
                sum += e;
                max = Math.Max(max, e);
            }

            return (sum / samples.Length, max);
        }

        public byte[] ToLut16()
        {
            int g = Points;
            var ms = new MemoryStream();
            Lut16Header(ms, 4, 3, g, InputEntries, 2);
            for (int c = 0; c < 4; c++)
            {
                for (int i = 0; i < InputEntries; i++)
                {
                    WriteU16(ms, Math.Round(Warp((double)i / (InputEntries - 1), _warp) * 65535));
                }
            }

            for (int e = 0; e < _grid.Entries; e++)
            {
                for (int c = 0; c < 3; c++)
                {
                    WriteU16(ms, Math.Round(_grid.Data[(e * 4) + c] * 65535.0));
                }
            }

            for (int c = 0; c < 3; c++)
            {
                WriteU16(ms, 0);
                WriteU16(ms, 65535);
            }

            return ms.ToArray();
        }
    }

    private static void Lut16Header(MemoryStream ms, int inCh, int outCh, int grid, int inEntries, int outEntries)
    {
        ms.Write("mft2"u8);
        ms.Write(new byte[4]);
        ms.WriteByte((byte)inCh);
        ms.WriteByte((byte)outCh);
        ms.WriteByte((byte)grid);
        ms.WriteByte(0);
        Span<byte> v = stackalloc byte[4];
        for (int i = 0; i < 9; i++)
        {
            BinaryPrimitives.WriteInt32BigEndian(v, i % 4 == 0 ? 65536 : 0);
            ms.Write(v);
        }

        WriteU16(ms, inEntries);
        WriteU16(ms, outEntries);
    }

    private static void WriteU16(MemoryStream ms, double v)
    {
        Span<byte> b = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(b, (ushort)Math.Clamp(v, 0, 65535));
        ms.Write(b);
    }

    /// <summary>The PCS-to-device table: CIELAB grid nodes to inks, by inverting <see cref="A2B"/> with a black generation rule and an ink limit.</summary>
    private static class B2A
    {
        public static byte[] Build(A2B a2b, int g)
        {
            int total = g * g * g;
            var inks = new double[total][];
            Parallel.For(0, total, e =>
            {
                int ia = e / (g * g), ib = (e / g) % g, ic = e % g;

                // Table coordinates decode like the profile reader: L = 100 * 65535/65280 * x, a and b = x * 65535/256 - 128.
                double l = Math.Min(100.0, ia / (double)(g - 1) * 65535 / 65280 * 100);
                double a = (ib / (double)(g - 1) * 65535 / 256) - 128;
                double b = (ic / (double)(g - 1) * 65535 / 256) - 128;
                inks[e] = Invert(a2b, l, a, b);
            });

            var ms = new MemoryStream();
            Lut16Header(ms, 3, 4, g, 2, 2);
            for (int c = 0; c < 3; c++)
            {
                WriteU16(ms, 0);
                WriteU16(ms, 65535);
            }

            for (int e = 0; e < total; e++)
            {
                for (int c = 0; c < 4; c++)
                {
                    WriteU16(ms, Math.Round(inks[e][c] * 65535));
                }
            }

            for (int c = 0; c < 4; c++)
            {
                WriteU16(ms, 0);
                WriteU16(ms, 65535);
            }

            return ms.ToArray();
        }

        private static double[] Invert(A2B a2b, double l, double a, double b)
        {
            (double[] ink, double err) = SolveTarget(a2b, l, a, b);
            if (err <= 1.0)
            {
                return ink;
            }

            // Out of gamut: keep lightness and hue, reduce chroma until the target is reachable.
            double lo = 0, hi = 1;
            (double[] ink, double err) best = SolveTarget(a2b, l, 0, 0);
            if (best.err > 0.5)
            {
                return best.ink; // Lightness itself is unreachable (darker than the darkest ink combination).
            }

            for (int i = 0; i < 12; i++)
            {
                double mid = (lo + hi) / 2;
                (double[] ink, double err) r = SolveTarget(a2b, l, a * mid, b * mid);
                if (r.err <= 0.5)
                {
                    lo = mid;
                    best = r;
                }
                else
                {
                    hi = mid;
                }
            }

            return best.ink;
        }

        private static (double[] Ink, double Err) SolveTarget(A2B a2b, double l, double a, double b)
        {
            double chroma = Math.Sqrt((a * a) + (b * b));

            // Black generation: dark and nearly neutral colors take black, saturated colors keep to CMY.
            double dark = Math.Clamp((72 - l) / (72 - 16), 0, 1);
            double sat = Math.Clamp((chroma - 10) / 60, 0, 1);
            double k = Math.Pow(dark, 1.4) * (1 - (0.85 * sat * sat * (3 - (2 * sat))));
            double[] cmy = InitialGuess(l, a, b, k);
            (double[] Ink, double Err) result = Optimize(a2b, cmy, k, l, a, b);
            while (result.Ink[0] + result.Ink[1] + result.Ink[2] + result.Ink[3] > TotalInkLimit + 0.002 && k < 1)
            {
                k = Math.Min(1, k + 0.08);
                result = Optimize(a2b, [result.Ink[0], result.Ink[1], result.Ink[2]], k, l, a, b);
            }

            return result;
        }

        private static double[] InitialGuess(double l, double a, double b, double k)
        {
            double[] xyz = LabToXyz(l, a, b);

            // Linear sRGB from D50 XYZ (Bradford-adapted matrix), then crude ink amounts.
            double r = (3.1338561 * xyz[0]) - (1.6168667 * xyz[1]) - (0.4906146 * xyz[2]);
            double g = (-0.9787684 * xyz[0]) + (1.9161415 * xyz[1]) + (0.0334540 * xyz[2]);
            double bl = (0.0719453 * xyz[0]) - (0.2289914 * xyz[1]) + (1.4052427 * xyz[2]);
            double[] c = [1 - Enc(r), 1 - Enc(g), 1 - Enc(bl)];
            return c.Select(v => Math.Clamp((v - (0.6 * k)) / Math.Max(0.2, 1 - k), 0, 1)).ToArray();
        }

        private static double Enc(double v) => Math.Pow(Math.Clamp(v, 0, 1), 1 / 2.2);

        private static (double[] Ink, double Err) Optimize(A2B a2b, double[] cmy, double k, double l, double a, double b)
        {
            double[] u = (double[])cmy.Clone();
            double lambda = 1.0;
            double cost = Cost(a2b, u, k, l, a, b, out double[] res);
            for (int iter = 0; iter < 40; iter++)
            {
                var jac = new double[4][];
                for (int row = 0; row < 4; row++)
                {
                    jac[row] = new double[3];
                }

                for (int j = 0; j < 3; j++)
                {
                    double[] up = (double[])u.Clone();
                    double h = u[j] > 0.99 ? -0.005 : 0.005;
                    up[j] = u[j] + h;
                    Cost(a2b, up, k, l, a, b, out double[] r2);
                    for (int row = 0; row < 4; row++)
                    {
                        jac[row][j] = (r2[row] - res[row]) / h;
                    }
                }

                var jtj = new double[3, 3];
                var jtr = new double[3];
                for (int row = 0; row < 4; row++)
                {
                    for (int i = 0; i < 3; i++)
                    {
                        jtr[i] += jac[row][i] * res[row];
                        for (int j = 0; j < 3; j++)
                        {
                            jtj[i, j] += jac[row][i] * jac[row][j];
                        }
                    }
                }

                bool improved = false;
                for (int attempt = 0; attempt < 8 && !improved; attempt++)
                {
                    double[]? delta = Solve3(jtj, jtr, lambda);
                    double[] cand = new double[3];
                    for (int i = 0; i < 3; i++)
                    {
                        cand[i] = Math.Clamp(u[i] - (delta?[i] ?? 0), 0, 1);
                    }

                    double c2 = Cost(a2b, cand, k, l, a, b, out double[] r3);
                    if (c2 < cost)
                    {
                        improved = true;
                        u = cand;
                        cost = c2;
                        res = r3;
                        lambda = Math.Max(lambda / 3, 1e-4);
                    }
                    else
                    {
                        lambda *= 4;
                    }
                }

                if (!improved || cost < 1e-6)
                {
                    break;
                }
            }

            double[] lab = a2b.Lab(u[0], u[1], u[2], k);
            double err = DeltaE2000(lab, [l, a, b]);
            return ([u[0], u[1], u[2], k], err);
        }

        private static double Cost(A2B a2b, double[] u, double k, double l, double a, double b, out double[] res)
        {
            double[] lab = a2b.Lab(u[0], u[1], u[2], k);
            double over = Math.Max(0, u[0] + u[1] + u[2] + k - TotalInkLimit);
            res = [lab[0] - l, lab[1] - a, lab[2] - b, 40 * over];
            return (res[0] * res[0]) + (res[1] * res[1]) + (res[2] * res[2]) + (res[3] * res[3]);
        }

        private static double[]? Solve3(double[,] m, double[] r, double lambda)
        {
            double[,] a = (double[,])m.Clone();
            for (int i = 0; i < 3; i++)
            {
                a[i, i] += lambda * (1 + a[i, i]);
            }

            double det = (a[0, 0] * ((a[1, 1] * a[2, 2]) - (a[1, 2] * a[2, 1]))) - (a[0, 1] * ((a[1, 0] * a[2, 2]) - (a[1, 2] * a[2, 0]))) + (a[0, 2] * ((a[1, 0] * a[2, 1]) - (a[1, 1] * a[2, 0])));
            if (Math.Abs(det) < 1e-18)
            {
                return null;
            }

            double[] x = new double[3];
            for (int c = 0; c < 3; c++)
            {
                double[,] t = (double[,])a.Clone();
                for (int i = 0; i < 3; i++)
                {
                    t[i, c] = r[i];
                }

                double d = (t[0, 0] * ((t[1, 1] * t[2, 2]) - (t[1, 2] * t[2, 1]))) - (t[0, 1] * ((t[1, 0] * t[2, 2]) - (t[1, 2] * t[2, 0]))) + (t[0, 2] * ((t[1, 0] * t[2, 1]) - (t[1, 1] * t[2, 0])));
                x[c] = d / det;
            }

            return x;
        }
    }
}
