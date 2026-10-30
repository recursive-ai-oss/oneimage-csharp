using System.Buffers;
using OneImage.Kernels;

namespace OneImage.Svg.Render;

/// <summary>A rectangle of whole pixels in the filter's pixel space.</summary>
internal readonly record struct PxRect(int X, int Y, int W, int H)
{
    public int Right => X + W;

    public int Bottom => Y + H;

    public bool IsEmpty => W <= 0 || H <= 0;

    public long Area => IsEmpty ? 0 : (long)W * H;

    public PxRect Intersect(PxRect o)
    {
        int x0 = Math.Max(X, o.X);
        int y0 = Math.Max(Y, o.Y);
        int x1 = Math.Min(Right, o.Right);
        int y1 = Math.Min(Bottom, o.Bottom);
        return x1 <= x0 || y1 <= y0 ? new PxRect(x0, y0, 0, 0) : new PxRect(x0, y0, x1 - x0, y1 - y0);
    }
}

/// <summary>sRGB transfer functions as tables (4096 steps, interpolated), so a filter converts without a power function per sample.</summary>
internal static class FilterColor
{
    private const int Steps = 4096;
    private static readonly float[] ToLinearTable = Build(toLinear: true);
    private static readonly float[] ToSrgbTable = Build(toLinear: false);

    /// <summary>The table of the sRGB to linear curve, for the vector kernels.</summary>
    internal static ReadOnlySpan<float> ToLinear => ToLinearTable;

    /// <summary>The table of the linear to sRGB curve, for the vector kernels.</summary>
    internal static ReadOnlySpan<float> ToSrgb => ToSrgbTable;

    private static float[] Build(bool toLinear)
    {
        var t = new float[Steps + 2];
        for (int i = 0; i <= Steps; i++)
        {
            double v = i / (double)Steps;
            t[i] = (float)(toLinear
                ? (v <= 0.04045 ? v / 12.92 : DeterministicMath.Pow((v + 0.055) / 1.055, 2.4))
                : (v <= 0.0031308 ? v * 12.92 : (1.055 * DeterministicMath.Pow(v, 1 / 2.4)) - 0.055));
        }

        t[Steps + 1] = t[Steps];
        return t;
    }

    public static float SrgbToLinear(float v) => Lookup(ToLinearTable, v);

    public static float LinearToSrgb(float v) => Lookup(ToSrgbTable, v);

    private static float Lookup(float[] table, float v)
    {
        if (!(v > 0))
        {
            return 0;
        }

        if (v >= 1)
        {
            return 1;
        }

        float f = v * Steps;
        int i = (int)f;
        float t = f - i;
        return table[i] + ((table[i + 1] - table[i]) * t);
    }
}

/// <summary>
/// Premultiplied float RGBA over a rectangle of filter pixels, in either encoded sRGB or linear light. Rented from the array pool and
/// returned on <see cref="Dispose"/>.
/// </summary>
internal sealed class FilterBuffer : IDisposable
{
    private float[]? _data;

    // 8-bit pixels (rented, owned) a source buffer was read from and has not yet been expanded to float: expanding straight into linear light
    // skips the float sRGB copy when nothing asks for it.
    private Rgba32[]? _bytes;

    public FilterBuffer(PxRect rect, bool linear)
        : this(rect, linear, clear: true)
    {
    }

    /// <summary>Makes a buffer; with <paramref name="clear"/> false its pixels are whatever the pool held, for a caller that overwrites all of them.</summary>
    public FilterBuffer(PxRect rect, bool linear, bool clear)
    {
        Rect = rect;
        Linear = linear;
        _data = Pools<float>.Pool.Rent(Math.Max(rect.W * rect.H * 4, 1));
        if (clear)
        {
            Array.Clear(_data, 0, rect.W * rect.H * 4);
        }
    }

    private FilterBuffer(PxRect rect, Rgba32[] bytes)
    {
        Rect = rect;
        Linear = false;
        _bytes = bytes;
    }

    /// <summary>Tests set this to false to run the scalar conversions in place of the vector kernels.</summary>
    internal static bool UseKernels { get; set; } = true;

    public PxRect Rect { get; }

    public int Width => Rect.W;

    public int Height => Rect.H;

    public bool Linear { get; }

    /// <summary>Gets the pixel array; it may be longer than the image.</summary>
    public float[] Data
    {
        get
        {
            if (_data is not null)
            {
                return _data;
            }

            ObjectDisposedException.ThrowIf(_bytes is null, this);

            // Expanded on first use, in sRGB.
            int count = Rect.W * Rect.H;
            float[] d = Pools<float>.Pool.Rent(Math.Max(count * 4, 1));
            Fill(_bytes, d, count, linear: false);
            return _data = d;
        }
    }

    public Span<float> Pixels => Data.AsSpan(0, Rect.W * Rect.H * 4);

    public void Dispose()
    {
        float[]? d = _data;
        Rgba32[]? b = _bytes;
        _data = null;
        _bytes = null;
        if (d is not null)
        {
            Pools<float>.Pool.Return(d);
        }

        if (b is not null)
        {
            Pools<Rgba32>.Pool.Return(b);
        }
    }

    /// <summary>
    /// Makes a source buffer in sRGB from straight 8-bit pixels of <paramref name="rect"/>, which the buffer owns from now on (a rented array
    /// it returns to the pool). Nothing is expanded to float until a primitive reads it.
    /// </summary>
    public static FilterBuffer FromBytes(Rgba32[] pooled, PxRect rect) => new(rect, pooled);

    /// <summary>Makes a buffer of <paramref name="rect"/> from straight 8-bit pixels of that rectangle, converting into linear light when asked.</summary>
    public static FilterBuffer FromRgba(ReadOnlySpan<Rgba32> pixels, PxRect rect, bool linear)
    {
        var buf = new FilterBuffer(rect, linear, clear: false);
        Fill(pixels, buf.Data, rect.W * rect.H, linear);
        return buf;
    }

    // Straight 8-bit pixels to premultiplied float, in linear light when asked; transparent pixels become zeros.
    private static void Fill(ReadOnlySpan<Rgba32> pixels, Span<float> d, int count, bool linear)
    {
        int done = UseKernels && FilterColorKernel.IsSupported ? FilterColorKernel.FromRgba(pixels, d, count, linear ? FilterColor.ToLinear : default) : 0;
        for (int i = done; i < count; i++)
        {
            Rgba32 p = pixels[i];
            if (p.A == 0)
            {
                d.Slice(i * 4, 4).Clear();
                continue;
            }

            float a = p.A / 255f;
            float r = p.R / 255f;
            float g = p.G / 255f;
            float b = p.B / 255f;
            if (linear)
            {
                r = FilterColor.SrgbToLinear(r);
                g = FilterColor.SrgbToLinear(g);
                b = FilterColor.SrgbToLinear(b);
            }

            int o = i * 4;
            d[o] = r * a;
            d[o + 1] = g * a;
            d[o + 2] = b * a;
            d[o + 3] = a;
        }
    }

    /// <summary>Returns this buffer's pixels in the other color space as a new buffer; the buffer itself when it is in that space already.</summary>
    public FilterBuffer InSpace(bool linear)
    {
        if (linear == Linear)
        {
            return this;
        }

        if (_data is null && _bytes is not null && linear && UseKernels && FilterColorKernel.IsSupported)
        {
            // Straight from 8 bits to linear light, with the roundings of expanding to sRGB float first and converting after.
            var fused = new FilterBuffer(Rect, true, clear: false);
            int total = Rect.W * Rect.H;
            int finished = FilterColorKernel.FromRgbaConvert(_bytes, fused.Data, total, FilterColor.ToLinear);
            if (finished < total)
            {
                Fill(_bytes.AsSpan(finished, total - finished), fused.Data.AsSpan(finished * 4), total - finished, linear: false);
                ConvertRun(fused.Data, fused.Data, finished, total, linear: true);
            }

            return fused;
        }

        var o = new FilterBuffer(Rect, linear, clear: false);
        float[] s = Data;
        float[] d = o.Data;
        int n = Rect.W * Rect.H;
        int done = UseKernels && FilterColorKernel.IsSupported ? FilterColorKernel.Convert(s, d, n, linear ? FilterColor.ToLinear : FilterColor.ToSrgb) : 0;
        ConvertRun(s, d, done, n, linear);
        return o;
    }

    // Pixels from..to of premultiplied float, un-premultiplied, through the transfer curve into the other space and premultiplied again
    // (source and destination may be the same array); pixels without alpha become zeros.
    private static void ConvertRun(float[] s, float[] d, int from, int to, bool linear)
    {
        for (int i = from; i < to; i++)
        {
            int p = i * 4;
            float a = s[p + 3];
            if (!(a > 0))
            {
                d.AsSpan(p, 4).Clear();
                continue;
            }

            float inv = 1f / a;
            float r = Math.Min(s[p] * inv, 1f);
            float g = Math.Min(s[p + 1] * inv, 1f);
            float b = Math.Min(s[p + 2] * inv, 1f);
            if (linear)
            {
                r = FilterColor.SrgbToLinear(r);
                g = FilterColor.SrgbToLinear(g);
                b = FilterColor.SrgbToLinear(b);
            }
            else
            {
                r = FilterColor.LinearToSrgb(r);
                g = FilterColor.LinearToSrgb(g);
                b = FilterColor.LinearToSrgb(b);
            }

            d[p] = r * a;
            d[p + 1] = g * a;
            d[p + 2] = b * a;
            d[p + 3] = a;
        }
    }

    /// <summary>Writes the pixels as straight 8-bit sRGB (rounded once) into <paramref name="dest"/>, a rectangle of the buffer's size.</summary>
    public void ToRgba(Span<Rgba32> dest) => ToRgba(dest, Rect);

    /// <summary>Converts the part <paramref name="crop"/> of the buffer (which must lie inside it) to straight 8-bit RGBA, row after row.</summary>
    public void ToRgba(Span<Rgba32> dest, PxRect crop)
    {
        float[] s = Data;
        int o = 0;
        for (int y = crop.Y; y < crop.Bottom; y++)
        {
            int p = (((y - Rect.Y) * Rect.W) + (crop.X - Rect.X)) * 4;
            int start = 0;
            if (UseKernels && FilterColorKernel.IsSupported)
            {
                start = FilterColorKernel.ToRgba(s.AsSpan(p), dest.Slice(o, crop.W), crop.W, Linear ? FilterColor.ToSrgb : default);
                o += start;
                p += start * 4;
            }

            for (int x = start; x < crop.W; x++, p += 4)
            {
                float a = s[p + 3];
                if (!(a > 0))
                {
                    dest[o++] = default;
                    continue;
                }

                a = Math.Min(a, 1f);
                float inv = 1f / a;
                float r = Math.Min(s[p] * inv, 1f);
                float g = Math.Min(s[p + 1] * inv, 1f);
                float b = Math.Min(s[p + 2] * inv, 1f);
                if (Linear)
                {
                    r = FilterColor.LinearToSrgb(r);
                    g = FilterColor.LinearToSrgb(g);
                    b = FilterColor.LinearToSrgb(b);
                }

                dest[o++] = new Rgba32(Byte(r), Byte(g), Byte(b), Byte(a));
            }
        }
    }

    private static byte Byte(float v) => (byte)(Math.Clamp(v, 0f, 1f) * 255f + 0.5f);

    /// <summary>Makes a buffer of <paramref name="rect"/> holding what this buffer has there, and nothing (transparent) elsewhere.</summary>
    public FilterBuffer Reframe(PxRect rect)
    {
        var o = new FilterBuffer(rect, Linear);
        PxRect common = rect.Intersect(Rect);
        if (common.IsEmpty)
        {
            return o;
        }

        float[] s = Data;
        float[] d = o.Data;
        for (int y = common.Y; y < common.Bottom; y++)
        {
            Array.Copy(s, (((y - Rect.Y) * Rect.W) + (common.X - Rect.X)) * 4, d, (((y - rect.Y) * rect.W) + (common.X - rect.X)) * 4, common.W * 4);
        }

        return o;
    }

    /// <summary>Makes pixels outside <paramref name="keep"/> transparent.</summary>
    public void ClearOutside(PxRect keep)
    {
        PxRect k = keep.Intersect(Rect);
        float[] d = Data;
        for (int y = 0; y < Rect.H; y++)
        {
            int gy = Rect.Y + y;
            int row = y * Rect.W * 4;
            if (k.IsEmpty || gy < k.Y || gy >= k.Bottom)
            {
                Array.Clear(d, row, Rect.W * 4);
                continue;
            }

            int left = k.X - Rect.X;
            int right = k.Right - Rect.X;
            if (left > 0)
            {
                Array.Clear(d, row, left * 4);
            }

            if (right < Rect.W)
            {
                Array.Clear(d, row + (right * 4), (Rect.W - right) * 4);
            }
        }
    }
}
