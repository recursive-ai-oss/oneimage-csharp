// Measures the deterministic colour-transfer functions against the platform math library and prints the hashes that
// SrgbTransferTests checks, so a platform without a test runner (browser WebAssembly, for one) can be compared too.
//
//   dotnet run -c Release --project tools/TransferSweep -- hashes
//   dotnet run -c Release --project tools/TransferSweep -- sweep 16      exhaustive over every float in [0, 1], 16 threads
//   dotnet run -c Release --project tools/TransferSweep -- double
//   dotnet run -c Release --project tools/TransferSweep -- cbrt 16
//   dotnet run -c Release --project tools/TransferSweep -- speed
using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using OneImage;
using OneImage.Kernels;

static float LibmDecode(float v) => v <= 0.04045f ? v / 12.92f : MathF.Pow((v + 0.055f) / 1.055f, 2.4f);
static float LibmEncode(float v) => v <= 0.0031308f ? v * 12.92f : (1.055f * MathF.Pow(v, 1f / 2.4f)) - 0.055f;
static int Ulps(float a, float b) => Math.Abs(BitConverter.SingleToInt32Bits(a) - BitConverter.SingleToInt32Bits(b));
static int UlpsD(double a, double b) => (int)Math.Min(int.MaxValue, Math.Abs(BitConverter.DoubleToInt64Bits(a) - BitConverter.DoubleToInt64Bits(b)));

static string Hash(float[] t)
{
    var bytes = new byte[t.Length * 4];
    for (int i = 0; i < t.Length; i++)
    {
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i * 4), t[i]);
    }

    return Convert.ToHexString(SHA256.HashData(bytes));
}

string mode = args.Length > 0 ? args[0] : "hashes";
int threads = args.Length > 1 ? int.Parse(args[1]) : Environment.ProcessorCount;

switch (mode)
{
    case "hashes":
        {
            Console.WriteLine("platform   " + System.Runtime.InteropServices.RuntimeInformation.OSDescription + " " + System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture);
            Console.WriteLine("decode8    " + Hash(SrgbTransfer.Decode8Table));
            Console.WriteLine("encode     " + Hash(SrgbTransfer.EncodeTable));
            Console.WriteLine("toLinear16 " + Hash(SrgbTables.ToLinear16));
            Console.WriteLine("fromLinear8 " + Convert.ToHexString(SHA256.HashData(SrgbTables.FromLinear8)));
            var values = new List<float>();
            for (int bits = 0; bits <= 0x3F800000; bits += 4099)
            {
                float v = BitConverter.Int32BitsToSingle(bits);
                values.Add(SrgbTransfer.DecodeOne(v));
                values.Add(SrgbTransfer.EncodeOne(v));
            }

            foreach (float v in new[] { 1f, -0.5f, 0.04045f, 0.0031308f, 2f, 1e-30f })
            {
                values.Add(SrgbTransfer.DecodeOne(v));
                values.Add(SrgbTransfer.EncodeOne(v));
            }

            Console.WriteLine("singleValue " + Hash([.. values]));
            double[] gammas = [1.0 / 3, 0.45, 0.5, 1 / 2.4, 1 / 2.2, 1.0, 1.8, 2.0, 2.2, 2.4, 2.6, 3.0, 1.0 / 1.8, 1.0 / 2.6, 5.0 / 3, 0.9, 1.1, 4.0 / 3];
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buf = new byte[8];
            foreach (double g in gammas)
            {
                foreach (int den in new[] { 255, 65535 })
                {
                    for (int i = 1; i <= den; i += den == 255 ? 1 : 7)
                    {
                        BinaryPrimitives.WriteDoubleLittleEndian(buf, DeterministicMath.Pow((double)i / den, g));
                        sha.AppendData(buf);
                    }
                }
            }

            for (int i = -400; i <= 400; i++)
            {
                BinaryPrimitives.WriteDoubleLittleEndian(buf, DeterministicMath.Pow(2.0, i / 100.0));
                sha.AppendData(buf);
            }

            Console.WriteLine("powDouble  " + Convert.ToHexString(sha.GetHashAndReset()));
            var buf4 = new byte[4];
            for (int bits = BitConverter.SingleToInt32Bits(0.0008f); bits < BitConverter.SingleToInt32Bits(4f); bits += 5003)
            {
                BinaryPrimitives.WriteSingleLittleEndian(buf4, DeterministicMath.CbrtF(BitConverter.Int32BitsToSingle(bits)));
                sha.AppendData(buf4);
            }

            Console.WriteLine("cbrt       " + Convert.ToHexString(sha.GetHashAndReset()));
            break;
        }

    case "sweep":
        {
            // Every float in [0, 1] through DecodeOne and EncodeOne, against the platform formula (the 1.10.2 code).
            long total = 0x3F800000L + 1;
            long chunk = 1 << 20;
            long chunks = (total + chunk - 1) / chunk;
            var decBad = new long[chunks];
            var encBad = new long[chunks];
            var fastBad = new long[chunks];
            var decMax = new int[chunks];
            var encMax = new int[chunks];
            var sw = Stopwatch.StartNew();
            Parallel.For(0, chunks, new ParallelOptions { MaxDegreeOfParallelism = threads }, c =>
            {
                long end = Math.Min(total, (c + 1) * chunk);
                for (long i = c * chunk; i < end; i++)
                {
                    float v = BitConverter.Int32BitsToSingle((int)i);
                    float a = LibmDecode(v), b = SrgbTransfer.DecodeOne(v);
                    if (a != b)
                    {
                        decBad[c]++;
                        decMax[c] = Math.Max(decMax[c], Ulps(a, b));
                    }

                    a = LibmEncode(v);
                    b = SrgbTransfer.EncodeOne(v);
                    if (a != b)
                    {
                        encBad[c]++;
                        encMax[c] = Math.Max(encMax[c], Ulps(a, b));
                    }

                    // The fast path against the exact double-double path, on the arguments the two curves actually use.
                    float x1 = (v + 0.055f) / 1.055f;
                    if (v > 0.04045f && DeterministicMath.PowF(x1, 2.4f) != DeterministicMath.PowFReference(x1, 2.4f))
                    {
                        fastBad[c]++;
                    }

                    if (v > 0.0031308f && DeterministicMath.PowF(v, 1f / 2.4f) != DeterministicMath.PowFReference(v, 1f / 2.4f))
                    {
                        fastBad[c]++;
                    }
                }
            });
            Console.WriteLine($"floats in [0,1]: {total} ({sw.Elapsed.TotalSeconds:F0} s, {threads} threads)");
            Console.WriteLine($"DecodeOne differs from MathF.Pow formula: {decBad.Sum()} ({decBad.Sum() * 100.0 / total:F4}%), max {decMax.Max()} ulp");
            Console.WriteLine($"EncodeOne differs from MathF.Pow formula: {encBad.Sum()} ({encBad.Sum() * 100.0 / total:F4}%), max {encMax.Max()} ulp");
            Console.WriteLine($"fast path differs from exact path: {fastBad.Sum()}");
            break;
        }

    case "double":
        {
            double[] gs = [1.0 / 3, 0.45, 0.5, 1 / 2.4, 1 / 2.2, 1.0, 1.8, 2.0, 2.2, 2.4, 2.6, 3.0, 1.0 / 1.8, 1.0 / 2.6, 5.0 / 3, 0.9, 1.1, 4.0 / 3, 0.35, 1.0 / 0.45];
            foreach (int den in new[] { 255, 65535 })
            {
                long bad = 0, n = 0;
                int max = 0;
                foreach (double g in gs)
                {
                    for (int i = 1; i <= den; i++)
                    {
                        double x = (double)i / den;
                        double a = Math.Pow(x, g), b = DeterministicMath.Pow(x, g);
                        n++;
                        if (a != b)
                        {
                            bad++;
                            max = Math.Max(max, UlpsD(a, b));
                        }
                    }
                }

                Console.WriteLine($"Pow vs Math.Pow, x = i/{den}, {gs.Length} exponents: {bad}/{n} differ, max {max} ulp");
            }

            {
                long bad = 0, n = 0;
                int max = 0;
                for (int i = -4000; i <= 4000; i++)
                {
                    double s = i / 1000.0;
                    double a = Math.Pow(2.0, s), b = DeterministicMath.Pow(2.0, s);
                    n++;
                    if (a != b)
                    {
                        bad++;
                        max = Math.Max(max, UlpsD(a, b));
                    }
                }

                Console.WriteLine($"2^s, s in [-4,4] step 0.001: {bad}/{n} differ, max {max} ulp");
            }

            {
                long bad = 0, n = 0;
                int max = 0;
                for (int i = 0; i <= 1000000; i++)
                {
                    double v = i / 1e6;
                    double a = Math.Pow((v + 0.055) / 1.055, 2.4), b = DeterministicMath.Pow((v + 0.055) / 1.055, 2.4);
                    n++;
                    if (a != b)
                    {
                        bad++;
                        max = Math.Max(max, UlpsD(a, b));
                    }

                    a = Math.Pow(v, 1 / 2.4);
                    b = DeterministicMath.Pow(v, 1 / 2.4);
                    n++;
                    if (a != b)
                    {
                        bad++;
                        max = Math.Max(max, UlpsD(a, b));
                    }
                }

                Console.WriteLine($"double sRGB forms on 10^6 steps: {bad}/{n} differ, max {max} ulp");
            }

            break;
        }

    case "cbrt":
        {
            int lo = BitConverter.SingleToInt32Bits(0.0008f), hi = BitConverter.SingleToInt32Bits(4f);
            long n = hi - lo;
            long bad = 0;
            int max = 0;
            object gate = new();
            Parallel.For(0, (n >> 16) + 1, new ParallelOptions { MaxDegreeOfParallelism = threads }, c =>
            {
                long b = 0;
                int m = 0;
                for (long i = lo + (c << 16); i < Math.Min(hi, lo + ((c + 1) << 16)); i++)
                {
                    float v = BitConverter.Int32BitsToSingle((int)i);
                    float a = MathF.Cbrt(v), d = DeterministicMath.CbrtF(v);
                    if (a != d)
                    {
                        b++;
                        m = Math.Max(m, Ulps(a, d));
                    }
                }

                lock (gate)
                {
                    bad += b;
                    max = Math.Max(max, m);
                }
            });
            Console.WriteLine($"CbrtF vs MathF.Cbrt over every float in [0.0008, 4): {n} floats, {bad} differ, max {max} ulp");
            break;
        }

    case "speed":
        {
            float acc = 0;
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < 5000000; i++)
            {
                acc += MathF.Pow(0.3f + (i * 1e-9f), 2.4f);
            }

            Console.WriteLine($"MathF.Pow             {sw.Elapsed.TotalMilliseconds * 1e6 / 5000000:F1} ns  ({acc})");
            sw.Restart();
            acc = 0;
            for (int i = 0; i < 5000000; i++)
            {
                acc += DeterministicMath.PowF(0.3f + (i * 1e-9f), 2.4f);
            }

            Console.WriteLine($"DeterministicMath.PowF {sw.Elapsed.TotalMilliseconds * 1e6 / 5000000:F1} ns  ({acc})");
            double dacc = 0;
            sw.Restart();
            for (int i = 0; i < 1000000; i++)
            {
                dacc += Math.Pow(0.3 + (i * 1e-9), 2.4);
            }

            Console.WriteLine($"Math.Pow              {sw.Elapsed.TotalMilliseconds * 1e6 / 1000000:F1} ns  ({dacc})");
            sw.Restart();
            dacc = 0;
            for (int i = 0; i < 1000000; i++)
            {
                dacc += DeterministicMath.Pow(0.3 + (i * 1e-9), 2.4);
            }

            Console.WriteLine($"DeterministicMath.Pow  {sw.Elapsed.TotalMilliseconds * 1e6 / 1000000:F1} ns  ({dacc})");
            break;
        }
}
