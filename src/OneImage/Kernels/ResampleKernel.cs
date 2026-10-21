using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace OneImage.Kernels;

/// <summary>
/// How pixels of one format map to the float working rows of the resampler: color channels first, then alpha, padded with
/// zeros to <see cref="Stride"/> floats (1, 2 or 4) so a pixel is one vector for the common 3- and 4-channel cases.
/// Working values are premultiplied by alpha and, when <see cref="Linear"/>, in linear light (sRGB transfer function).
/// </summary>
internal readonly struct ResampleLayout
{
    public ResampleLayout(PixelFormat format, bool linear)
    {
        Sample = format.Sample;
        Colors = format.ColorChannels;
        HasAlpha = format.HasAlpha;
        PremultipliedStorage = format.Alpha == AlphaMode.Premultiplied;
        Channels = format.ChannelCount;
        Linear = linear && format.Model is ColorModel.Rgb or ColorModel.Gray;
        Stride = Channels switch { 1 => 1, 2 => 2, _ => 4 };
        if (Channels > 4)
        {
            throw new ImageOperationException($"Resampling {format} is not supported.");
        }
    }

    public SampleType Sample { get; }

    public int Colors { get; }

    public bool HasAlpha { get; }

    public bool PremultipliedStorage { get; }

    public int Channels { get; }

    public bool Linear { get; }

    public int Stride { get; }
}

/// <summary>
/// Kernels of the separable resampler. All passes run on float rows; summation order is fixed per output value (taps in
/// ascending order, one multiply and one add each, no fused multiply-add), so the vector and scalar paths give bit-identical
/// results. Callers pass buffers whose sizes were checked against the weight tables by the checks at the top of each kernel.
/// </summary>
internal static class ResampleKernel
{
    /// <summary>Converts one row of stored pixels to working floats (see <see cref="ResampleLayout"/>).</summary>
    public static void UnpackRow(ReadOnlySpan<byte> src, Span<float> dst, int width, in ResampleLayout l)
    {
        int c = l.Channels;
        int cc = l.Colors;
        int p = l.Stride;
        if (src.Length < width * c * (l.Sample == SampleType.U8 ? 1 : l.Sample == SampleType.F32 ? 4 : 2) || dst.Length < width * p)
        {
            throw new ArgumentException("Row buffers are too small.");
        }

        if (l.Sample == SampleType.U8)
        {
            float[] lut = l.Linear ? SrgbTables.ToLinear8 : SrgbTables.Identity8;
            ReadOnlySpan<byte> s = src[..(width * c)];
            Span<float> d = dst[..(width * p)];
            if (!l.HasAlpha)
            {
                for (int x = 0; x < width; x++)
                {
                    int si = x * c;
                    int di = x * p;
                    for (int k = 0; k < cc; k++)
                    {
                        d[di + k] = lut[s[si + k]];
                    }

                    for (int k = cc; k < p; k++)
                    {
                        d[di + k] = 0;
                    }
                }

                return;
            }

            for (int x = 0; x < width; x++)
            {
                int si = x * c;
                int di = x * p;
                float a = s[si + cc] * (1f / 255f);
                if (!l.PremultipliedStorage)
                {
                    for (int k = 0; k < cc; k++)
                    {
                        d[di + k] = lut[s[si + k]] * a;
                    }
                }
                else if (!l.Linear)
                {
                    for (int k = 0; k < cc; k++)
                    {
                        d[di + k] = s[si + k] * (1f / 255f);
                    }
                }
                else
                {
                    // Stored color is encoded * alpha: undo the alpha, decode, apply it again.
                    for (int k = 0; k < cc; k++)
                    {
                        d[di + k] = a > 0 ? SrgbTables.ToLinear16[EncodedIndex(s[si + k] * (1f / 255f) / a)] * a : 0;
                    }
                }

                d[di + cc] = a;
                for (int k = c; k < p; k++)
                {
                    d[di + k] = 0;
                }
            }

            return;
        }

        UnpackWide(src, dst, width, l);
    }

    /// <summary>Converts working floats back to stored pixels: un-premultiplies, encodes and rounds half up with clamping (floats are not clamped).</summary>
    public static void PackRow(ReadOnlySpan<float> src, Span<byte> dst, int width, in ResampleLayout l)
    {
        int c = l.Channels;
        int cc = l.Colors;
        int p = l.Stride;
        if (src.Length < width * p || dst.Length < width * c * (l.Sample == SampleType.U8 ? 1 : l.Sample == SampleType.F32 ? 4 : 2))
        {
            throw new ArgumentException("Row buffers are too small.");
        }

        if (l.Sample == SampleType.U8)
        {
            byte[] enc = SrgbTables.FromLinear8;
            ReadOnlySpan<float> s = src[..(width * p)];
            Span<byte> d = dst[..(width * c)];
            for (int x = 0; x < width; x++)
            {
                int si = x * p;
                int di = x * c;
                float a = l.HasAlpha ? s[si + cc] : 1f;
                float inv = a > 0 ? 1f / a : 0f;
                if (l.HasAlpha)
                {
                    d[di + cc] = (byte)Math.Clamp((int)((a * 255f) + 0.5f), 0, 255);
                }

                for (int k = 0; k < cc; k++)
                {
                    float v = s[si + k] * inv;
                    int code;
                    if (l.Linear)
                    {
                        code = enc[EncodedIndex(v)];
                    }
                    else
                    {
                        code = Math.Clamp((int)((v * 255f) + 0.5f), 0, 255);
                    }

                    if (l.PremultipliedStorage)
                    {
                        code = Math.Min(255, (int)((code * a) + 0.5f));
                    }

                    d[di + k] = (byte)code;
                }
            }

            return;
        }

        PackWide(src, dst, width, l);
    }

    private static int EncodedIndex(float v) => Math.Clamp((int)((v * 65535f) + 0.5f), 0, 65535);

    // 16-bit and floating-point samples: through normalized floats, with table or pow based transfer functions.
    private static void UnpackWide(ReadOnlySpan<byte> src, Span<float> dst, int width, in ResampleLayout l)
    {
        int c = l.Channels;
        int cc = l.Colors;
        int p = l.Stride;
        float[] tmp = ArrayPool<float>.Shared.Rent(width * c);
        try
        {
            ConvertKernel.Unpack(src, l.Sample, tmp.AsSpan(0, width * c));
            bool floatSamples = l.Sample is SampleType.F16 or SampleType.F32;
            for (int x = 0; x < width; x++)
            {
                int si = x * c;
                int di = x * p;
                float a = l.HasAlpha ? tmp[si + cc] : 1f;
                for (int k = 0; k < cc; k++)
                {
                    float v = tmp[si + k];
                    if (l.PremultipliedStorage)
                    {
                        v = a != 0 ? v / a : 0;
                    }

                    if (l.Linear)
                    {
                        v = floatSamples ? SrgbTables.Decode(v) : SrgbTables.ToLinear16[EncodedIndex(v)];
                    }

                    dst[di + k] = v * a;
                }

                if (l.HasAlpha)
                {
                    dst[di + cc] = a;
                }

                for (int k = c; k < p; k++)
                {
                    dst[di + k] = 0;
                }
            }
        }
        finally
        {
            ArrayPool<float>.Shared.Return(tmp);
        }
    }

    private static void PackWide(ReadOnlySpan<float> src, Span<byte> dst, int width, in ResampleLayout l)
    {
        int c = l.Channels;
        int cc = l.Colors;
        int p = l.Stride;
        float[] tmp = ArrayPool<float>.Shared.Rent(width * c);
        try
        {
            bool floatSamples = l.Sample is SampleType.F16 or SampleType.F32;
            for (int x = 0; x < width; x++)
            {
                int si = x * p;
                int di = x * c;
                float a = l.HasAlpha ? src[si + cc] : 1f;
                float inv = a != 0 ? 1f / a : 0f;
                for (int k = 0; k < cc; k++)
                {
                    float v = src[si + k] * inv;
                    if (!floatSamples)
                    {
                        v = Math.Clamp(v, 0f, 1f);
                    }

                    if (l.Linear)
                    {
                        v = SrgbTables.Encode(v);
                    }

                    tmp[di + k] = l.PremultipliedStorage ? v * a : v;
                }

                if (l.HasAlpha)
                {
                    tmp[di + cc] = floatSamples ? a : Math.Clamp(a, 0f, 1f);
                }
            }

            ConvertKernel.Pack(tmp.AsSpan(0, width * c), l.Sample, dst[..(width * c * (l.Sample == SampleType.F32 ? 4 : 2))]);
        }
        finally
        {
            ArrayPool<float>.Shared.Return(tmp);
        }
    }

    /// <summary>
    /// Box-averages groups of <paramref name="factor"/> pixels: output pixel i is the mean of source pixels
    /// [i * factor, min((i + 1) * factor, sourcePixels)). The last group may be shorter and is averaged over what exists.
    /// </summary>
    public static void ReduceRow(ReadOnlySpan<float> src, Span<float> dst, int stride, int factor, int sourcePixels, int outputPixels)
    {
        if (src.Length < sourcePixels * stride || dst.Length < outputPixels * stride || (long)outputPixels * factor < sourcePixels || (long)(outputPixels - 1) * factor >= sourcePixels)
        {
            throw new ArgumentException("Row buffers do not match the reduction.");
        }

        ref float s0 = ref MemoryMarshal.GetReference(src);
        ref float d0 = ref MemoryMarshal.GetReference(dst);
        for (int i = 0; i < outputPixels; i++)
        {
            int first = i * factor;
            int n = Math.Min(factor, sourcePixels - first);
            float inv = 1f / n;
            if (stride == 4)
            {
                Vector128<float> acc = Vector128.LoadUnsafe(ref s0, (nuint)(first * 4));
                for (int j = 1; j < n; j++)
                {
                    acc += Vector128.LoadUnsafe(ref s0, (nuint)((first + j) * 4));
                }

                (acc * inv).StoreUnsafe(ref d0, (nuint)(i * 4));
            }
            else
            {
                for (int k = 0; k < stride; k++)
                {
                    float acc = Unsafe.Add(ref s0, (first * stride) + k);
                    for (int j = 1; j < n; j++)
                    {
                        acc += Unsafe.Add(ref s0, ((first + j) * stride) + k);
                    }

                    Unsafe.Add(ref d0, (i * stride) + k) = acc * inv;
                }
            }
        }
    }

    /// <summary><c>dst[i] += src[i]</c>.</summary>
    public static void AddRow(ReadOnlySpan<float> src, Span<float> dst)
    {
        if (src.Length < dst.Length)
        {
            throw new ArgumentException("Source row is shorter than the destination.");
        }

        ref float s0 = ref MemoryMarshal.GetReference(src);
        ref float d0 = ref MemoryMarshal.GetReference(dst);
        int i = 0;
        if (Vector256.IsHardwareAccelerated)
        {
            for (; i + 8 <= dst.Length; i += 8)
            {
                (Vector256.LoadUnsafe(ref d0, (nuint)i) + Vector256.LoadUnsafe(ref s0, (nuint)i)).StoreUnsafe(ref d0, (nuint)i);
            }
        }

        for (; i < dst.Length; i++)
        {
            Unsafe.Add(ref d0, i) += Unsafe.Add(ref s0, i);
        }
    }

    /// <summary><c>dst[i] *= factor</c>.</summary>
    public static void ScaleRow(Span<float> dst, float factor)
    {
        ref float d0 = ref MemoryMarshal.GetReference(dst);
        int i = 0;
        if (Vector256.IsHardwareAccelerated)
        {
            for (; i + 8 <= dst.Length; i += 8)
            {
                (Vector256.LoadUnsafe(ref d0, (nuint)i) * factor).StoreUnsafe(ref d0, (nuint)i);
            }
        }

        for (; i < dst.Length; i++)
        {
            Unsafe.Add(ref d0, i) *= factor;
        }
    }

    /// <summary>
    /// Horizontal pass: output pixel i is the weighted sum of <c>count[i]</c> source pixels starting at source index
    /// <c>start[i]</c>. <paramref name="src"/> holds source pixels from index <paramref name="srcFirst"/> on.
    /// </summary>
    public static void HorizontalFilter(
        ReadOnlySpan<float> src,
        int srcFirst,
        Span<float> dst,
        int stride,
        ReadOnlySpan<int> start,
        ReadOnlySpan<int> count,
        ReadOnlySpan<int> offset,
        ReadOnlySpan<float> weights)
    {
        int n = start.Length;
        if (count.Length < n || offset.Length < n || dst.Length < n * stride)
        {
            throw new ArgumentException("Output buffers do not match the weight table.");
        }

        for (int i = 0; i < n; i++)
        {
            long first = start[i] - srcFirst;
            if (first < 0 || count[i] <= 0 || (first + count[i]) * stride > src.Length || (long)offset[i] + count[i] > weights.Length)
            {
                throw new ArgumentException("Weight table reads outside the source row.");
            }
        }

        ref float s0 = ref MemoryMarshal.GetReference(src);
        ref float w0 = ref MemoryMarshal.GetReference(weights);
        ref float d0 = ref MemoryMarshal.GetReference(dst);
        for (int i = 0; i < n; i++)
        {
            int first = start[i] - srcFirst;
            int taps = count[i];
            ref float w = ref Unsafe.Add(ref w0, offset[i]);
            if (stride == 4)
            {
                Vector128<float> acc = Vector128.LoadUnsafe(ref s0, (nuint)(first * 4)) * Unsafe.Add(ref w, 0);
                for (int t = 1; t < taps; t++)
                {
                    acc += Vector128.LoadUnsafe(ref s0, (nuint)((first + t) * 4)) * Unsafe.Add(ref w, t);
                }

                acc.StoreUnsafe(ref d0, (nuint)(i * 4));
            }
            else
            {
                for (int k = 0; k < stride; k++)
                {
                    float acc = Unsafe.Add(ref s0, (first * stride) + k) * Unsafe.Add(ref w, 0);
                    for (int t = 1; t < taps; t++)
                    {
                        acc += Unsafe.Add(ref s0, ((first + t) * stride) + k) * Unsafe.Add(ref w, t);
                    }

                    Unsafe.Add(ref d0, (i * stride) + k) = acc;
                }
            }
        }
    }

    /// <summary>
    /// Vertical pass: <c>dst[j] = sum over t of weights[t] * rows[(firstRow + t) * rowStride + j]</c> for j below
    /// <c>dst.Length</c>, with the same per-element summation order whichever vector width is used.
    /// </summary>
    public static void VerticalFilter(ReadOnlySpan<float> rows, int rowStride, int firstRow, ReadOnlySpan<float> weights, Span<float> dst)
    {
        if (weights.Length == 0 || firstRow < 0 || dst.Length > rowStride || ((long)(firstRow + weights.Length - 1) * rowStride) + dst.Length > rows.Length)
        {
            throw new ArgumentException("Weights read outside the intermediate rows.");
        }

        ref float r0 = ref MemoryMarshal.GetReference(rows);
        ref float w0 = ref MemoryMarshal.GetReference(weights);
        ref float d0 = ref MemoryMarshal.GetReference(dst);
        int taps = weights.Length;
        nuint baseIndex = (nuint)firstRow * (nuint)rowStride;
        int j = 0;
        if (Vector256.IsHardwareAccelerated)
        {
            for (; j + 8 <= dst.Length; j += 8)
            {
                Vector256<float> acc = Vector256.LoadUnsafe(ref r0, baseIndex + (nuint)j) * w0;
                for (int t = 1; t < taps; t++)
                {
                    acc += Vector256.LoadUnsafe(ref r0, baseIndex + ((nuint)t * (nuint)rowStride) + (nuint)j) * Unsafe.Add(ref w0, t);
                }

                acc.StoreUnsafe(ref d0, (nuint)j);
            }
        }

        if (Vector128.IsHardwareAccelerated)
        {
            for (; j + 4 <= dst.Length; j += 4)
            {
                Vector128<float> acc = Vector128.LoadUnsafe(ref r0, baseIndex + (nuint)j) * w0;
                for (int t = 1; t < taps; t++)
                {
                    acc += Vector128.LoadUnsafe(ref r0, baseIndex + ((nuint)t * (nuint)rowStride) + (nuint)j) * Unsafe.Add(ref w0, t);
                }

                acc.StoreUnsafe(ref d0, (nuint)j);
            }
        }

        for (; j < dst.Length; j++)
        {
            float acc = Unsafe.Add(ref r0, baseIndex + (nuint)j) * w0;
            for (int t = 1; t < taps; t++)
            {
                acc += Unsafe.Add(ref r0, baseIndex + ((nuint)t * (nuint)rowStride) + (nuint)j) * Unsafe.Add(ref w0, t);
            }

            Unsafe.Add(ref d0, j) = acc;
        }
    }
}
