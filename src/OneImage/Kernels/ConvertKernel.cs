using System.Buffers;

namespace OneImage.Kernels;

/// <summary>
/// Row-wise pixel format conversion. Scalar reference implementation going through float32.
/// Rounding: integer results are round-half-up of the exact scaled value, clamped to the type's range; NaN becomes 0.
/// Gray to RGB replicates; RGB to gray uses Rec. 709 luma weights (0.2126, 0.7152, 0.0722) on the stored values without
/// linearization (color-managed conversion is a separate op). Dropping alpha discards it without flattening.
/// Adding alpha makes pixels opaque. CMYK and Lab convert to and from the other models through the default ICC profiles
/// (see <see cref="ColorTransform"/>); Lab pixels must be 32-bit float.
/// </summary>
internal static class ConvertKernel
{
    public static void ConvertRow(ReadOnlySpan<byte> source, PixelFormat sourceFormat, Span<byte> destination, PixelFormat destinationFormat, int width)
    {
        int sbpp = sourceFormat.BytesPerPixel;
        int dbpp = destinationFormat.BytesPerPixel;
        ReadOnlySpan<byte> src = source[..(width * sbpp)];
        Span<byte> dst = destination[..(width * dbpp)];
        if (sourceFormat == destinationFormat)
        {
            src.CopyTo(dst);
            return;
        }

        bool sameModel = sourceFormat.Model == destinationFormat.Model;
        bool grayRgb = (sourceFormat.Model == ColorModel.Gray && destinationFormat.Model == ColorModel.Rgb)
            || (sourceFormat.Model == ColorModel.Rgb && destinationFormat.Model == ColorModel.Gray);
        if (!sameModel && !grayRgb)
        {
            // CMYK and Lab cross models through the default profiles (relative colorimetric, black point compensation).
            ColorTransform.Default(sourceFormat.Model, destinationFormat.Model).ConvertRow(source, sourceFormat, destination, destinationFormat, width);
            return;
        }

        int sch = sourceFormat.ChannelCount;
        int dch = destinationFormat.ChannelCount;
        float[] sbuf = ArrayPool<float>.Shared.Rent(width * sch);
        float[] dbuf = ArrayPool<float>.Shared.Rent(width * dch);
        try
        {
            Unpack(src, sourceFormat.Sample, sbuf.AsSpan(0, width * sch));
            ConvertFloats(sbuf, sourceFormat, dbuf, destinationFormat, width);
            Pack(dbuf.AsSpan(0, width * dch), destinationFormat.Sample, dst);
        }
        finally
        {
            ArrayPool<float>.Shared.Return(sbuf);
            ArrayPool<float>.Shared.Return(dbuf);
        }
    }

    public static void Unpack(ReadOnlySpan<byte> src, SampleType type, Span<float> dst)
    {
        switch (type)
        {
            case SampleType.U8:
                for (int i = 0; i < dst.Length; i++)
                {
                    dst[i] = src[i] / 255f;
                }

                break;
            case SampleType.U16:
                ReadOnlySpan<ushort> u16 = SpanCast.As<ushort>(src);
                for (int i = 0; i < dst.Length; i++)
                {
                    dst[i] = u16[i] / 65535f;
                }

                break;
            case SampleType.F16:
                ReadOnlySpan<Half> f16 = SpanCast.As<Half>(src);
                for (int i = 0; i < dst.Length; i++)
                {
                    dst[i] = (float)f16[i];
                }

                break;
            default:
                SpanCast.As<float>(src)[..dst.Length].CopyTo(dst);
                break;
        }
    }

    public static void Pack(ReadOnlySpan<float> src, SampleType type, Span<byte> dst)
    {
        switch (type)
        {
            case SampleType.U8:
                for (int i = 0; i < src.Length; i++)
                {
                    float v = (src[i] * 255f) + 0.5f;
                    dst[i] = !(v > 0f) ? (byte)0 : v >= 255f ? (byte)255 : (byte)v;
                }

                break;
            case SampleType.U16:
                Span<ushort> u16 = SpanCast.As<ushort>(dst);
                for (int i = 0; i < src.Length; i++)
                {
                    float v = (src[i] * 65535f) + 0.5f;
                    u16[i] = !(v > 0f) ? (ushort)0 : v >= 65535f ? (ushort)65535 : (ushort)v;
                }

                break;
            case SampleType.F16:
                Span<Half> f16 = SpanCast.As<Half>(dst);
                for (int i = 0; i < src.Length; i++)
                {
                    f16[i] = (Half)src[i];
                }

                break;
            default:
                src.CopyTo(SpanCast.As<float>(dst));
                break;
        }
    }

    private static void ConvertFloats(float[] s, PixelFormat sf, float[] d, PixelFormat df, int width)
    {
        int sch = sf.ChannelCount;
        int dch = df.ChannelCount;
        int scolors = sf.ColorChannels;
        int dcolors = df.ColorChannels;
        bool unPremultiply = sf.Alpha == AlphaMode.Premultiplied;
        bool premultiply = df.Alpha == AlphaMode.Premultiplied;
        bool toRgb = sf.Model == ColorModel.Gray && df.Model == ColorModel.Rgb;
        bool toGray = sf.Model == ColorModel.Rgb && df.Model == ColorModel.Gray;
        Span<float> c = stackalloc float[4];
        for (int x = 0; x < width; x++)
        {
            int si = x * sch;
            int di = x * dch;
            float a = sf.HasAlpha ? s[si + scolors] : 1f;
            for (int k = 0; k < scolors; k++)
            {
                float v = s[si + k];
                c[k] = unPremultiply ? (a > 0f ? v / a : 0f) : v;
            }

            if (toRgb)
            {
                c[1] = c[0];
                c[2] = c[0];
            }
            else if (toGray)
            {
                c[0] = (0.2126f * c[0]) + (0.7152f * c[1]) + (0.0722f * c[2]);
            }

            for (int k = 0; k < dcolors; k++)
            {
                d[di + k] = premultiply ? c[k] * a : c[k];
            }

            if (df.HasAlpha)
            {
                d[di + dcolors] = a;
            }
        }
    }
}
