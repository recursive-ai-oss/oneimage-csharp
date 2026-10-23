using System.Buffers;

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

    private static float[] Build(bool toLinear)
    {
        var t = new float[Steps + 2];
        for (int i = 0; i <= Steps; i++)
        {
            double v = i / (double)Steps;
            t[i] = (float)(toLinear
                ? (v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4))
                : (v <= 0.0031308 ? v * 12.92 : (1.055 * Math.Pow(v, 1 / 2.4)) - 0.055));
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

    public FilterBuffer(PxRect rect, bool linear)
    {
        Rect = rect;
        Linear = linear;
        _data = ArrayPool<float>.Shared.Rent(Math.Max(rect.W * rect.H * 4, 1));
        Array.Clear(_data, 0, rect.W * rect.H * 4);
    }

    public PxRect Rect { get; }

    public int Width => Rect.W;

    public int Height => Rect.H;

    public bool Linear { get; }

    /// <summary>Gets the pixel array; it may be longer than the image.</summary>
    public float[] Data => _data ?? throw new ObjectDisposedException(nameof(FilterBuffer));

    public Span<float> Pixels => Data.AsSpan(0, Rect.W * Rect.H * 4);

    public void Dispose()
    {
        float[]? d = _data;
        _data = null;
        if (d is not null)
        {
            ArrayPool<float>.Shared.Return(d);
        }
    }

    /// <summary>Makes a buffer of <paramref name="rect"/> from straight 8-bit pixels of that rectangle, converting into linear light when asked.</summary>
    public static FilterBuffer FromRgba(ReadOnlySpan<Rgba32> pixels, PxRect rect, bool linear)
    {
        var buf = new FilterBuffer(rect, linear);
        float[] d = buf.Data;
        for (int i = 0; i < rect.W * rect.H; i++)
        {
            Rgba32 p = pixels[i];
            if (p.A == 0)
            {
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

        return buf;
    }

    /// <summary>Returns this buffer's pixels in the other color space as a new buffer; the buffer itself when it is in that space already.</summary>
    public FilterBuffer InSpace(bool linear)
    {
        if (linear == Linear)
        {
            return this;
        }

        var o = new FilterBuffer(Rect, linear);
        float[] s = Data;
        float[] d = o.Data;
        int n = Rect.W * Rect.H;
        for (int i = 0; i < n; i++)
        {
            int p = i * 4;
            float a = s[p + 3];
            if (!(a > 0))
            {
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

        return o;
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
            for (int x = 0; x < crop.W; x++, p += 4)
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
