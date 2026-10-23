using System.Text;
using System.Text.RegularExpressions;
using OneImage.Codecs.Jpx;

namespace OneImage.Tests.Jpx;

/// <summary>
/// ITU-T T.803 conformance, Class 1 streams (Profile 0 and 1): every component of every stream is decoded and compared with the
/// reference image of the test suite under the suite's own limits (peak error and mean squared error per component, which for the
/// lossless streams are zero). The streams and references are not in the repository: <c>tools/fetch-jpx-tests.sh</c> downloads them and
/// <c>ONEIMAGE_JPX_CORPUS</c> names the folder; without it the test does nothing.
/// </summary>
public class JpxConformanceTests
{
    // stream, component, reduction levels, allowed peak error, allowed mean squared error.
    private static readonly string[] Rows =
    [
        "p0_01,0,0,0,0",
        "p0_02,0,0,0,0",
        "p0_03,0,0,0,0",
        "p0_04,0,0,5,0.776",
        "p0_04,1,0,4,0.626",
        "p0_04,2,0,6,1.070",
        "p0_05,0,0,2,0.319",
        "p0_05,1,0,2,0.323",
        "p0_05,2,0,2,0.317",
        "p0_05,3,0,0,0",
        "p0_06,0,0,635,11287",
        "p0_06,1,0,403,6124",
        "p0_06,2,0,378,3968",
        "p0_06,3,0,0,0",
        "p0_07,0,0,0,0",
        "p0_07,1,0,0,0",
        "p0_07,2,0,0,0",
        "p0_08,0,1,0,0",
        "p0_08,1,1,0,0",
        "p0_08,2,1,0,0",
        "p0_09,0,0,0,0",
        "p0_10,0,0,0,0",
        "p0_10,1,0,0,0",
        "p0_10,2,0,0,0",
        "p0_11,0,0,0,0",
        "p0_12,0,0,0,0",
        "p0_13,0,0,0,0",
        "p0_13,1,0,0,0",
        "p0_13,2,0,0,0",
        "p0_13,3,0,0,0",
        "p0_14,0,0,0,0",
        "p0_14,1,0,0,0",
        "p0_14,2,0,0,0",
        "p0_15,0,0,0,0",
        "p0_16,0,0,0,0",
        "p1_01,0,0,0,0",
        "p1_02,0,0,5,0.765",
        "p1_02,1,0,4,0.616",
        "p1_02,2,0,6,1.051",
        "p1_03,0,0,2,0.311",
        "p1_03,1,0,2,0.280",
        "p1_03,2,0,1,0.267",
        "p1_03,3,0,0,0",
        "p1_04,0,0,624,3080",
        "p1_05,0,0,40,8.458",
        "p1_05,1,0,40,9.716",
        "p1_05,2,0,40,10.154",
        "p1_06,0,0,2,0.600",
        "p1_06,1,0,2,0.600",
        "p1_06,2,0,2,0.600",
        "p1_07,0,0,0,0",
        "p1_07,1,0,0,0",
    ];

    [Fact]
    public void ClassOneStreamsMatchTheReferenceImages()
    {
        string? dir = Environment.GetEnvironmentVariable("ONEIMAGE_JPX_CORPUS");
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
        {
            return;
        }

        string streams = Path.Combine(dir, "input", "conformance");
        string refs = Path.Combine(dir, "baseline", "conformance");
        var table = new StringBuilder();
        var failures = new List<string>();
        foreach (string row in Rows)
        {
            string[] f = row.Split(',');
            string stream = f[0];
            int comp = int.Parse(f[1]);
            int reduction = int.Parse(f[2]);
            double peakAllowed = double.Parse(f[3], System.Globalization.CultureInfo.InvariantCulture);
            double mseAllowed = double.Parse(f[4], System.Globalization.CultureInfo.InvariantCulture);
            string pgx = Path.Combine(refs, "c1" + stream + "_" + comp + ".pgx");
            string result;
            try
            {
                byte[] bytes = File.ReadAllBytes(Path.Combine(streams, stream + ".j2k"));
                (int bits, bool signed, int rw, int rh, int[] expected) = ReadPgx(pgx);
                var options = new JpxDecodeOptions { ReductionLevels = reduction, MaxComponents = 16384 };
                (JpxPlane[] planes, _) = JpxCodec.DecodeComponents(bytes, options);
                JpxPlane plane = planes[comp];
                if (plane.Width != rw || plane.Height != rh)
                {
                    // The reference of a few streams is only a window of the image.
                    options = new JpxDecodeOptions { ReductionLevels = reduction, MaxComponents = 16384, Region = new ImageRect(0, 0, rw, rh) };
                    plane = JpxCodec.DecodeComponents(bytes, options).Planes[comp];
                }

                if (plane.Width != rw || plane.Height != rh)
                {
                    result = $"SIZE {plane.Width}x{plane.Height} expected {rw}x{rh}";
                    failures.Add($"{stream}[{comp}] {result}");
                }
                else
                {
                    int offset = signed ? 1 << (bits - 1) : 0;
                    int peak = 0;
                    double sum = 0;
                    for (int i = 0; i < expected.Length; i++)
                    {
                        int d = Math.Abs(plane.Data[i] - offset - expected[i]);
                        peak = Math.Max(peak, d);
                        sum += (double)d * d;
                    }

                    double mse = sum / expected.Length;
                    bool ok = peak <= peakAllowed && mse <= mseAllowed + 1e-9;
                    result = $"{(ok ? "ok" : "FAIL")} peak {peak}/{peakAllowed} mse {mse:F3}/{mseAllowed}";
                    if (!ok)
                    {
                        failures.Add($"{stream}[{comp}] {result}");
                    }
                }
            }
            catch (Exception e) when (e is ImageException or IOException)
            {
                result = "EXCEPTION " + e.Message;
                failures.Add($"{stream}[{comp}] {result}");
            }

            table.AppendLine($"{stream} comp {comp} r{reduction}: {result}");
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures) + Environment.NewLine + table);
    }

    private static (int Bits, bool Signed, int Width, int Height, int[] Data) ReadPgx(string path)
    {
        byte[] b = File.ReadAllBytes(path);
        int nl = Array.IndexOf(b, (byte)10);
        string[] hd = Encoding.ASCII.GetString(b, 0, nl).Split([' ', '\r'], StringSplitOptions.RemoveEmptyEntries);
        bool little = hd[1] == "LM";
        bool signed;
        int bits;
        int k = 2;
        if (hd[k] is "+" or "-")
        {
            signed = hd[k] == "-";
            k++;
            bits = int.Parse(hd[k]);
        }
        else if (hd[k][0] is '-' or '+')
        {
            signed = hd[k][0] == '-';
            bits = int.Parse(hd[k][1..]);
        }
        else
        {
            signed = false;
            bits = int.Parse(hd[k]);
        }

        int w = int.Parse(hd[k + 1]);
        int h = int.Parse(hd[k + 2]);
        int bytes = bits > 8 ? 2 : 1;
        var data = new int[w * h];
        int pos = nl + 1;
        for (int i = 0; i < data.Length; i++)
        {
            int v = bytes == 1 ? b[pos] : (little ? b[pos] | (b[pos + 1] << 8) : (b[pos] << 8) | b[pos + 1]);
            pos += bytes;
            if (signed)
            {
                int m = 1 << (bits - 1);
                v = (v & (m - 1)) - (v & m);
            }

            data[i] = v;
        }

        return (bits, signed, w, h, data);
    }
}
