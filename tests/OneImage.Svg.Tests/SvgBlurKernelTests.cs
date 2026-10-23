using OneImage.Svg.Render;
using Xunit;

namespace OneImage.Svg.Tests;

/// <summary>The vector blur equals its scalar reference bit for bit, on odd sizes, every box arrangement and the true-Gaussian range.</summary>
public sealed class SvgBlurKernelTests
{
    public static TheoryData<int, int, double, double> Cases() => new()
    {
        { 1, 1, 3, 3 },
        { 7, 5, 1.2, 2.5 },
        { 8, 16, 2, 0 },
        { 15, 17, 0, 4.4 },
        { 31, 33, 0.5, 0.9 },
        { 33, 31, 6.6, 6.6 },
        { 40, 9, 12, 1.9 },
        { 130, 70, 8, 8 },
        { 257, 131, 30, 20 },
        { 300, 450, 2.9, 3.1 },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void VectorEqualsReference(int w, int h, double sx, double sy)
    {
        var rnd = new Random(w * 1000 + h);
        var src = new float[w * h * 4];
        for (int i = 0; i < src.Length; i++)
        {
            // Mostly empty with bright specks, like filter input; a few values above 1 and below 0 for the premultiplied extremes.
            src[i] = rnd.Next(4) == 0 ? (float)((rnd.NextDouble() * 1.2) - 0.1) : 0f;
        }

        // Empty rows and columns take the blur's skip-the-empty-line shortcut.
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                if (y % 3 == 1 || x % 4 == 2)
                {
                    Array.Clear(src, ((y * w) + x) * 4, 4);
                }
            }
        }

        float[] expected = (float[])src.Clone();
        float[] scratch = new float[src.Length];
        float[] res = FilterBlurReference.Apply(expected, scratch, w, h, sx, sy);

        float[] actual = (float[])src.Clone();
        FilterBlur.Apply(actual, w, h, sx, sy);

        Assert.Equal(res.AsSpan(0, src.Length).ToArray(), actual);
    }

    [Fact]
    public void ForcedScalarEqualsReference()
    {
        var rnd = new Random(5);
        int w = 50, h = 41;
        var src = new float[w * h * 4];
        for (int i = 0; i < src.Length; i++)
        {
            src[i] = (float)rnd.NextDouble();
        }

        float[] a = (float[])src.Clone();
        float[] b = (float[])src.Clone();
        FilterBlur.Apply(a, w, h, 5, 1.5);
        FilterBlur.ForceScalar = true;
        try
        {
            FilterBlur.Apply(b, w, h, 5, 1.5);
        }
        finally
        {
            FilterBlur.ForceScalar = false;
        }

        Assert.Equal(a, b);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ColorConversionsEqualScalar(bool linear)
    {
        var rnd = new Random(linear ? 11 : 12);
        int w = 37, h = 9;
        var rect = new PxRect(3, 4, w, h);
        var px = new Rgba32[w * h];
        for (int i = 0; i < px.Length; i++)
        {
            px[i] = rnd.Next(5) switch
            {
                0 => default,
                1 => new Rgba32((byte)rnd.Next(256), (byte)rnd.Next(256), (byte)rnd.Next(256), 0),
                2 => new Rgba32((byte)rnd.Next(256), (byte)rnd.Next(256), (byte)rnd.Next(256), (byte)rnd.Next(1, 4)),
                _ => new Rgba32((byte)rnd.Next(256), (byte)rnd.Next(256), (byte)rnd.Next(256), (byte)rnd.Next(256)),
            };
        }

        // 8-bit -> float, float -> other space, float -> 8-bit, each with the kernels and with the scalar code.
        var results = new List<(float[] Floats, float[] Other, Rgba32[] Bytes)>();
        foreach (bool kernels in new[] { true, false })
        {
            FilterBuffer.UseKernels = kernels;
            try
            {
                using FilterBuffer a = FilterBuffer.FromRgba(px, rect, linear);
                using FilterBuffer b = a.InSpace(!linear);
                var bytes = new Rgba32[w * h];
                b.ToRgba(bytes);
                var inner = new Rgba32[(w - 5) * (h - 2)];
                a.ToRgba(inner, new PxRect(5, 5, w - 5 - 2, h - 2 - 1));
                results.Add((a.Pixels.ToArray(), b.Pixels.ToArray(), bytes));
            }
            finally
            {
                FilterBuffer.UseKernels = true;
            }
        }

        Assert.Equal(results[1].Floats, results[0].Floats);
        Assert.Equal(results[1].Other, results[0].Other);
        Assert.Equal(results[1].Bytes, results[0].Bytes);
    }

    [Fact]
    public void ColorConversionsOfFloatsEqualScalar()
    {
        // Premultiplied values outside the usual range: negative, above 1, color above alpha, tiny alpha, zero alpha, NaN and infinity.
        var rnd = new Random(21);
        int n = 301;
        var rect = new PxRect(0, 0, n, 1);
        var src = new float[n * 4];
        float[] odd = [0f, -0f, 1f, 1.5f, -0.25f, 1e-30f, 1e-40f, 0.5f, float.NaN, float.PositiveInfinity, float.NegativeInfinity, 3e38f];
        for (int i = 0; i < src.Length; i++)
        {
            src[i] = rnd.Next(3) == 0 ? odd[rnd.Next(odd.Length)] : (float)(rnd.NextDouble() * 1.3);
        }

        foreach (bool toLinear in new[] { true, false })
        {
            var outs = new List<(float[], Rgba32[])>();
            foreach (bool kernels in new[] { true, false })
            {
                FilterBuffer.UseKernels = kernels;
                try
                {
                    using var a = new FilterBuffer(rect, !toLinear);
                    src.CopyTo(a.Data, 0);
                    using FilterBuffer b = a.InSpace(toLinear);
                    var bytes = new Rgba32[n];
                    a.ToRgba(bytes);
                    outs.Add((b.Pixels.ToArray(), bytes));
                }
                finally
                {
                    FilterBuffer.UseKernels = true;
                }
            }

            // NaN compares unequal to itself; compare the bits.
            Assert.Equal(outs[1].Item1.Select(BitConverter.SingleToInt32Bits), outs[0].Item1.Select(BitConverter.SingleToInt32Bits));
            Assert.Equal(outs[1].Item2, outs[0].Item2);
        }
    }

    [Fact]
    public void DeferredSourceEqualsExpandThenConvert()
    {
        var rnd = new Random(31);
        int w = 41, h = 7;
        var rect = new PxRect(0, 0, w, h);
        var px = new Rgba32[w * h];
        for (int i = 0; i < px.Length; i++)
        {
            px[i] = new Rgba32((byte)rnd.Next(256), (byte)rnd.Next(256), (byte)rnd.Next(256), (byte)(rnd.Next(3) == 0 ? 0 : rnd.Next(1, 256)));
        }

        FilterBuffer.UseKernels = false;
        float[] expected;
        try
        {
            using FilterBuffer a = FilterBuffer.FromRgba(px, rect, linear: false);
            using FilterBuffer b = a.InSpace(true);
            expected = b.Pixels.ToArray();
        }
        finally
        {
            FilterBuffer.UseKernels = true;
        }

        using FilterBuffer source = MakeDeferred(px, rect);
        using FilterBuffer linear = source.InSpace(true);
        Assert.Equal(expected.Select(BitConverter.SingleToInt32Bits), linear.Pixels.ToArray().Select(BitConverter.SingleToInt32Bits));

        // The sRGB float view of the same source is what FromRgba gives.
        using FilterBuffer plain = FilterBuffer.FromRgba(px, rect, linear: false);
        Assert.Equal(plain.Pixels.ToArray(), MakeDeferred(px, rect).Pixels.ToArray());
    }

    private static FilterBuffer MakeDeferred(Rgba32[] px, PxRect rect)
    {
        Rgba32[] rented = System.Buffers.ArrayPool<Rgba32>.Shared.Rent(px.Length);
        px.CopyTo(rented, 0);
        return FilterBuffer.FromBytes(rented, rect);
    }
}
