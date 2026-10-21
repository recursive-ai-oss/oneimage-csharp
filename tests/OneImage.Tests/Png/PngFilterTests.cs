using System.Runtime.Intrinsics.X86;
using OneImage.Kernels;

namespace OneImage.Tests.Png;

public class PngFilterTests
{
    private static byte PaethRef(int a, int b, int c)
    {
        int p = a + b - c;
        int pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return (byte)(pa <= pb && pa <= pc ? a : pb <= pc ? b : c);
    }

    private static byte[] UnfilterRef(int filter, byte[] row, byte[] prev, int bpp)
    {
        byte[] o = (byte[])row.Clone();
        for (int i = 0; i < o.Length; i++)
        {
            int a = i >= bpp ? o[i - bpp] : 0;
            int b = prev[i];
            int c = i >= bpp ? prev[i - bpp] : 0;
            int pred = filter switch { 1 => a, 2 => b, 3 => (a + b) >> 1, 4 => PaethRef(a, b, c), _ => 0 };
            o[i] = (byte)(row[i] + pred);
        }

        return o;
    }

    [Fact]
    public void UnfilterMatchesTheReferenceForAllFiltersPixelSizesAndLengths()
    {
        var rnd = new Random(1);
        foreach (int bpp in new[] { 1, 2, 3, 4, 6, 8 })
        {
            foreach (int len in new[] { 1, 2, 3, 4, 5, 7, 8, 15, 16, 17, 31, 32, 33, 63, 64, 65, 255, 1000 })
            {
                int n = len * bpp;
                byte[] row = new byte[n];
                byte[] prev = new byte[n];
                rnd.NextBytes(row);
                rnd.NextBytes(prev);
                for (int filter = 0; filter <= 4; filter++)
                {
                    byte[] expected = UnfilterRef(filter, row, prev, bpp);
                    byte[] actual = (byte[])row.Clone();
                    PngFilter.Unfilter(filter, actual, prev, bpp);
                    Assert.Equal(expected, actual);
                }
            }
        }
    }

    [Fact]
    public void ForwardFiltersInvertThroughUnfilterAndScoreMatchesScalar()
    {
        var rnd = new Random(2);
        foreach (int bpp in new[] { 1, 3, 4, 8 })
        {
            foreach (int len in new[] { 1, 7, 9, 33, 130, 777 })
            {
                int n = len * bpp;
                byte[] raw = new byte[n];
                byte[] prev = new byte[n];
                rnd.NextBytes(raw);
                rnd.NextBytes(prev);
                for (int filter = 0; filter <= 4; filter++)
                {
                    byte[] filtered = new byte[n];
                    PngFilter.Apply(filter, raw, prev, filtered, bpp);
                    byte[] back = (byte[])filtered.Clone();
                    PngFilter.Unfilter(filter, back, prev, bpp);
                    Assert.Equal(raw, back);
                    Assert.Equal(PngFilter.SumAbsScalar(filtered), PngFilter.SumAbs(filtered));
                }
            }
        }

        // -128 counts as 128 on every path, including long all-extreme inputs.
        byte[] extreme = Enumerable.Repeat((byte)0x80, 1000).ToArray();
        Assert.Equal(128000, PngFilter.SumAbs(extreme));
        Assert.Equal(128000, PngFilter.SumAbsScalar(extreme));
        // Prove the hardware knobs used for the scalar and AVX2 runs took effect.
        if (Environment.GetEnvironmentVariable("DOTNET_EnableAVX2") == "0" || Environment.GetEnvironmentVariable("DOTNET_EnableHWIntrinsic") == "0")
        {
            Assert.False(Avx2.IsSupported);
        }

        if (Environment.GetEnvironmentVariable("DOTNET_EnableHWIntrinsic") == "0")
        {
            Assert.False(System.Numerics.Vector.IsHardwareAccelerated);
        }

        TestContext.Current.SendDiagnosticMessage($"Avx2.IsSupported={Avx2.IsSupported}, Vector<byte>.Count={System.Numerics.Vector<byte>.Count}, Vector.IsHardwareAccelerated={System.Numerics.Vector.IsHardwareAccelerated}");
    }
}
