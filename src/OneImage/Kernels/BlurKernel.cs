using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace OneImage.Kernels;

/// <summary>
/// Line kernels of the SVG filter blur: premultiplied float RGBA lines (four floats per pixel) with a box blur or a true Gaussian over each line,
/// computed in double precision with a running sum, the way the scalar reference does it, so results are bit for bit the same. The four channels of
/// a pixel are one 256-bit vector of doubles. Lines are addressed by an offset, a distance between lines and a distance between pixels (in
/// floats), so a row (pixels 4 floats apart) and a column (pixels a stride apart) are the same call, and the work is a pure function of its
/// arguments. Four lines at a time give the CPU four independent sums, because the add-then-subtract of one running sum is a latency chain.
/// Callers validate nothing here: every index is checked against the spans before any pointer is formed.
/// </summary>
internal static unsafe class BlurKernel
{
    /// <summary>Whether the vector kernels can run on this machine; callers use the scalar reference otherwise.</summary>
    public static bool IsSupported => Avx.IsSupported;

    /// <summary>
    /// Box blur of <paramref name="lines"/> lines of <paramref name="n"/> pixels: out[i] = mean of in[i - left .. i - left + size - 1], zero outside.
    /// The source and destination must not overlap.
    /// </summary>
    public static void Box(ReadOnlySpan<float> src, int srcOffset, int srcLine, int srcPix, Span<float> dst, int dstOffset, int dstLine, int dstPix, int lines, int n, int size, int left)
    {
        if (lines <= 0 || n <= 0)
        {
            return;
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(size, 1);
        Require(src.Length, srcOffset, srcLine, srcPix, lines, n);
        Require(dst.Length, dstOffset, dstLine, dstPix, lines, n);
        fixed (float* s = src)
        fixed (float* d = dst)
        {
            int j = 0;
            for (; j + 4 <= lines; j += 4)
            {
                Box4(s + srcOffset + (j * (nint)srcLine), srcLine, srcPix, d + dstOffset + (j * (nint)dstLine), dstLine, dstPix, n, size, left);
            }

            for (; j < lines; j++)
            {
                Box1(s + srcOffset + (j * (nint)srcLine), srcPix, d + dstOffset + (j * (nint)dstLine), dstPix, n, size, left);
            }
        }
    }

    /// <summary>Gaussian blur with a (2r+1)-tap kernel of <paramref name="lines"/> lines of <paramref name="n"/> pixels, taps in order from the left, zero outside. Source and destination must not overlap.</summary>
    public static void Gauss(ReadOnlySpan<float> src, int srcOffset, int srcLine, int srcPix, Span<float> dst, int dstOffset, int dstLine, int dstPix, int lines, int n, ReadOnlySpan<double> kernel)
    {
        if (lines <= 0 || n <= 0)
        {
            return;
        }

        ArgumentOutOfRangeException.ThrowIfEqual(kernel.Length & 1, 0);
        Require(src.Length, srcOffset, srcLine, srcPix, lines, n);
        Require(dst.Length, dstOffset, dstLine, dstPix, lines, n);
        int r = kernel.Length / 2;
        fixed (float* s = src)
        fixed (float* d = dst)
        fixed (double* kp = kernel)
        {
            for (int j = 0; j < lines; j++)
            {
                float* sl = s + srcOffset + (j * (nint)srcLine);
                float* dl = d + dstOffset + (j * (nint)dstLine);
                for (int x = 0; x < n; x++)
                {
                    Vector256<double> acc = Vector256<double>.Zero;
                    int k0 = Math.Max(-r, -x);
                    int k1 = Math.Min(r, n - 1 - x);
                    for (int k = k0; k <= k1; k++)
                    {
                        acc = Avx.Add(acc, Avx.Multiply(Vector256.Create(kp[k + r]), Load(sl + ((x + k) * (nint)srcPix))));
                    }

                    Store(dl + (x * (nint)dstPix), acc);
                }
            }
        }
    }

    /// <summary>Copies lines of pixels between two layouts. Source and destination must not overlap.</summary>
    public static void Copy(ReadOnlySpan<float> src, int srcOffset, int srcLine, int srcPix, Span<float> dst, int dstOffset, int dstLine, int dstPix, int lines, int n)
    {
        if (lines <= 0 || n <= 0)
        {
            return;
        }

        Require(src.Length, srcOffset, srcLine, srcPix, lines, n);
        Require(dst.Length, dstOffset, dstLine, dstPix, lines, n);
        fixed (float* s = src)
        fixed (float* d = dst)
        {
            for (int j = 0; j < lines; j++)
            {
                float* sl = s + srcOffset + (j * (nint)srcLine);
                float* dl = d + dstOffset + (j * (nint)dstLine);
                for (int x = 0; x < n; x++)
                {
                    Sse.Store(dl + (x * (nint)dstPix), Sse.LoadVector128(sl + (x * (nint)srcPix)));
                }
            }
        }
    }

    /// <summary>Whether any float of the lines differs from zero (a negative zero counts as zero; NaN does not).</summary>
    public static bool AnyNonZero(ReadOnlySpan<float> src, int srcOffset, int srcLine, int srcPix, int lines, int n)
    {
        if (lines <= 0 || n <= 0)
        {
            return false;
        }

        Require(src.Length, srcOffset, srcLine, srcPix, lines, n);
        fixed (float* s = src)
        {
            Vector128<float> zero = Vector128<float>.Zero;
            for (int j = 0; j < lines; j++)
            {
                float* sl = s + srcOffset + (j * (nint)srcLine);
                Vector128<float> any = zero;
                for (int x = 0; x < n; x++)
                {
                    any = Sse.Or(any, Sse.CompareNotEqual(Sse.LoadVector128(sl + (x * (nint)srcPix)), zero));
                }

                if (Sse.MoveMask(any) != 0)
                {
                    return true;
                }
            }
        }

        return false;
    }

    // Every pixel of every line (four floats from its start) must lie inside the span.
    private static void Require(int length, int offset, int lineStep, int pixStep, int lines, int n)
    {
        if (offset < 0 || lineStep < 0 || pixStep < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(offset));
        }

        long last = offset + ((long)(lines - 1) * lineStep) + ((long)(n - 1) * pixStep) + 4;
        if (last > length)
        {
            throw new ArgumentException("The lines do not fit the buffer.");
        }
    }

    private static Vector256<double> Load(float* p) => Avx.ConvertToVector256Double(Sse.LoadVector128(p));

    private static void Store(float* p, Vector256<double> v) => Sse.Store(p, Avx.ConvertToVector128Single(v));

    private static void Box1(float* s, int sp, float* d, int dp, int n, int size, int left)
    {
        int hi = size - 1 - left;
        Vector256<double> inv = Vector256.Create(1.0 / size);
        Vector256<double> a = Vector256<double>.Zero;
        int init = Math.Min(n - 1, hi);
        for (int k = 0; k <= init; k++)
        {
            a = Avx.Add(a, Load(s + (k * (nint)sp)));
        }

        for (int i = 0; i < n; i++)
        {
            Store(d + (i * (nint)dp), Avx.Multiply(a, inv));
            int add = i + 1 + hi;
            if (add < n)
            {
                a = Avx.Add(a, Load(s + (add * (nint)sp)));
            }

            int rem = i - left;
            if (rem >= 0)
            {
                a = Avx.Subtract(a, Load(s + (rem * (nint)sp)));
            }
        }
    }

    private static void Box4(float* s0, int sl, int sp, float* d0, int dl, int dp, int n, int size, int left)
    {
        float* s1 = s0 + sl, s2 = s1 + sl, s3 = s2 + sl;
        float* d1 = d0 + dl, d2 = d1 + dl, d3 = d2 + dl;
        int hi = size - 1 - left;
        Vector256<double> inv = Vector256.Create(1.0 / size);
        Vector256<double> a = Vector256<double>.Zero, b = a, c = a, e = a;
        int init = Math.Min(n - 1, hi);
        for (int k = 0; k <= init; k++)
        {
            nint o = k * (nint)sp;
            a = Avx.Add(a, Load(s0 + o));
            b = Avx.Add(b, Load(s1 + o));
            c = Avx.Add(c, Load(s2 + o));
            e = Avx.Add(e, Load(s3 + o));
        }

        for (int i = 0; i < n; i++)
        {
            nint od = i * (nint)dp;
            Store(d0 + od, Avx.Multiply(a, inv));
            Store(d1 + od, Avx.Multiply(b, inv));
            Store(d2 + od, Avx.Multiply(c, inv));
            Store(d3 + od, Avx.Multiply(e, inv));
            int add = i + 1 + hi;
            if (add < n)
            {
                nint o = add * (nint)sp;
                a = Avx.Add(a, Load(s0 + o));
                b = Avx.Add(b, Load(s1 + o));
                c = Avx.Add(c, Load(s2 + o));
                e = Avx.Add(e, Load(s3 + o));
            }

            int rem = i - left;
            if (rem >= 0)
            {
                nint o = rem * (nint)sp;
                a = Avx.Subtract(a, Load(s0 + o));
                b = Avx.Subtract(b, Load(s1 + o));
                c = Avx.Subtract(c, Load(s2 + o));
                e = Avx.Subtract(e, Load(s3 + o));
            }
        }
    }
}
