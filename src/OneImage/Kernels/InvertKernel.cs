using System.Buffers;
using System.Runtime.Intrinsics;

namespace OneImage.Kernels;

/// <summary>
/// Invert: replaces every color channel c by its complement (max - c, where max is 255, 65535 or 1.0); alpha is kept.
/// With premultiplied alpha the result is alpha - c, which is the premultiplied form of the inverted straight color.
/// Float results are not clamped. <see cref="InvertScalar"/> is the reference; <see cref="Invert"/> adds vector paths
/// for alpha-less 8/16-bit and float32 rows and must match it bit for bit.
/// </summary>
internal static class InvertKernel
{
    public static void Invert(ReadOnlySpan<byte> source, Span<byte> destination, PixelFormat format)
    {
        if (!format.HasAlpha)
        {
            if (format.Sample is SampleType.U8 or SampleType.U16)
            {
                ComplementBytes(source, destination[..source.Length]);
                return;
            }

            if (format.Sample == SampleType.F32)
            {
                OneMinusFloats(SpanCast.As<float>(source), SpanCast.As<float>(destination[..source.Length]));
                return;
            }
        }

        InvertScalar(source, destination, format);
    }

    public static void InvertScalar(ReadOnlySpan<byte> source, Span<byte> destination, PixelFormat format)
    {
        int ch = format.ChannelCount;
        int colors = format.ColorChannels;
        int bpp = format.BytesPerPixel;
        int width = source.Length / bpp;
        int n = width * ch;
        float[] buf = Pools<float>.Pool.Rent(n);
        try
        {
            Span<float> v = buf.AsSpan(0, n);
            ConvertKernel.Unpack(source, format.Sample, v);
            for (int x = 0; x < width; x++)
            {
                int i = x * ch;
                float a = format.Alpha == AlphaMode.Premultiplied ? v[i + colors] : 1f;
                for (int k = 0; k < colors; k++)
                {
                    v[i + k] = a - v[i + k];
                }
            }

            ConvertKernel.Pack(v, format.Sample, destination[..source.Length]);
        }
        finally
        {
            Pools<float>.Pool.Return(buf);
        }
    }

    private static void ComplementBytes(ReadOnlySpan<byte> src, Span<byte> dst)
    {
        int i = 0;
        if (Vector256.IsHardwareAccelerated)
        {
            for (; i + 32 <= src.Length; i += 32)
            {
                Vector256.OnesComplement(Vector256.Create(src.Slice(i, 32))).CopyTo(dst.Slice(i, 32));
            }
        }

        if (Vector128.IsHardwareAccelerated)
        {
            for (; i + 16 <= src.Length; i += 16)
            {
                Vector128.OnesComplement(Vector128.Create(src.Slice(i, 16))).CopyTo(dst.Slice(i, 16));
            }
        }

        for (; i < src.Length; i++)
        {
            dst[i] = (byte)~src[i];
        }
    }

    private static void OneMinusFloats(ReadOnlySpan<float> src, Span<float> dst)
    {
        int i = 0;
        if (Vector256.IsHardwareAccelerated)
        {
            Vector256<float> one = Vector256.Create(1f);
            for (; i + 8 <= src.Length; i += 8)
            {
                (one - Vector256.Create(src.Slice(i, 8))).CopyTo(dst.Slice(i, 8));
            }
        }

        if (Vector128.IsHardwareAccelerated)
        {
            Vector128<float> one = Vector128.Create(1f);
            for (; i + 4 <= src.Length; i += 4)
            {
                (one - Vector128.Create(src.Slice(i, 4))).CopyTo(dst.Slice(i, 4));
            }
        }

        for (; i < src.Length; i++)
        {
            dst[i] = 1f - src[i];
        }
    }
}
