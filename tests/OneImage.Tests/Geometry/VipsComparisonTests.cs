using Xunit;
using Vips = NetVips.Image;

namespace OneImage.Tests.Geometry;

/// <summary>Compares the geometry ops with libvips on in-memory pixels. Exact ops must be equal; resampled ones are reported.</summary>
public class VipsComparisonTests
{
    private const int W = 321;
    private const int H = 207;

    private static byte[] SmoothRgb()
    {
        byte[] raw = new byte[W * H * 3];
        for (int y = 0; y < H; y++)
        {
            for (int x = 0; x < W; x++)
            {
                int i = ((y * W) + x) * 3;
                raw[i] = (byte)(128 + (100 * Math.Sin(x / 17.0) * Math.Cos(y / 23.0)));
                raw[i + 1] = (byte)((x * 255) / W);
                raw[i + 2] = (byte)(128 + (90 * Math.Sin((x + y) / 31.0)));
            }
        }

        return raw;
    }

    private static Image Ours(byte[] raw) => Resample.ResampleAccuracyTests.ImageOf(raw, W, H, PixelFormat.Rgb8);

    [Fact]
    public void ExactOpsEqualLibvips()
    {
        byte[] raw = SmoothRgb();
        using Vips v = Vips.NewFromMemory(raw, W, H, 3, NetVips.Enums.BandFormat.Uchar);
        Image o = Ours(raw);
        (string Name, Image Mine, Vips Theirs)[] cases =
        [
            ("rot90", o.Rotate(90), v.Rot(NetVips.Enums.Angle.D90)),
            ("rot180", o.Rotate(180), v.Rot(NetVips.Enums.Angle.D180)),
            ("rot270", o.Rotate(270), v.Rot(NetVips.Enums.Angle.D270)),
            ("flip h", o.Flip(FlipAxis.Horizontal), v.Flip(NetVips.Enums.Direction.Horizontal)),
            ("flip v", o.Flip(FlipAxis.Vertical), v.Flip(NetVips.Enums.Direction.Vertical)),
        ];
        foreach ((string name, Image mine, Vips theirs) in cases)
        {
            using (theirs)
            {
                Assert.Equal((theirs.Width, theirs.Height), (mine.Width, mine.Height));
                Assert.True(theirs.WriteToMemory<byte>().AsSpan().SequenceEqual(Resample.ResampleAccuracyTests.ToBytes(mine, PixelFormat.Rgb8)), name);
            }
        }
    }

    [Fact]
    public void Rotate17IsCloseToLibvips()
    {
        byte[] raw = SmoothRgb();
        using Vips v = Vips.NewFromMemory(raw, W, H, 3, NetVips.Enums.BandFormat.Uchar);
        Image o = Ours(raw);
        double best = double.MaxValue;
        foreach (double sign in new[] { 1.0, -1.0 })
        {
            using Vips t = v.Rotate(17 * sign);
            Image mine = o.Rotate(17, filter: ResamplingFilter.CatmullRom, linear: false);
            (double mean, int max, int cw, int ch) = Compare(mine, t);
            TestContext.Current.SendDiagnosticMessage($"vs libvips rotate 17 (vips angle sign {sign:+0;-0}): ours {mine.Width}x{mine.Height}, vips {t.Width}x{t.Height}, central {cw}x{ch}: mean abs diff {mean:F3}, max {max}");
            best = Math.Min(best, mean);
        }

        Assert.True(best < 4.0, $"mean abs difference {best:F3}");
    }

    // Compares the central region (60% of the smaller output), aligned by the output centers.
    private static (double Mean, int Max, int W, int H) Compare(Image mine, Vips theirs)
    {
        // The two libraries place the rotated output with different sub-pixel conventions, so allow a whole-pixel shift and keep the best.
        (double Mean, int Max, int W, int H) best = (double.MaxValue, 0, 0, 0);
        foreach ((int dx, int dy) in new[] { (0, 0), (1, 0), (-1, 0), (0, 1), (0, -1), (1, 1), (-1, -1), (1, -1), (-1, 1) })
        {
            var r = CompareAt(mine, theirs, dx, dy);
            if (r.Mean < best.Mean)
            {
                best = r;
            }
        }

        return best;
    }

    private static (double Mean, int Max, int W, int H) CompareAt(Image mine, Vips theirs, int shiftX, int shiftY)
    {
        int cw = (int)(Math.Min(mine.Width, theirs.Width) * 0.6);
        int ch = (int)(Math.Min(mine.Height, theirs.Height) * 0.6);
        int ox = ((mine.Width - cw) / 2) + shiftX;
        int oy = ((mine.Height - ch) / 2) + shiftY;
        int vx = (theirs.Width - cw) / 2;
        int vy = (theirs.Height - ch) / 2;
        byte[] a = new byte[cw * ch * 3];
        mine.Frames[0].CopyPixelsTo<Rgb24>(System.Runtime.InteropServices.MemoryMarshal.Cast<byte, Rgb24>(a.AsSpan()), new ImageRect(ox, oy, cw, ch));
        using Vips crop = theirs.ExtractArea(vx, vy, cw, ch);
        byte[] b = crop.WriteToMemory<byte>();
        long sum = 0;
        int max = 0;
        for (int i = 0; i < a.Length; i++)
        {
            int d = Math.Abs(a[i] - b[i]);
            sum += d;
            max = Math.Max(max, d);
        }

        return ((double)sum / a.Length, max, cw, ch);
    }
}
