using System.Buffers.Binary;
using OneImage.Kernels;

namespace OneImage.Codecs.Tiff;

/// <summary>
/// Turns one decoded row of stored samples (any bit depth, contiguous or one plane of a planar image) into
/// pixels of the frame's <see cref="PixelFormat"/>. Samples narrower than the output type are scaled to the
/// full range with rounding, <c>(v * max' + max / 2) / max</c>; min-is-white gray is inverted so that zero is
/// always black; palettes are expanded to 8-bit RGB.
/// </summary>
internal sealed class TiffPixelConverter
{
    private readonly bool _bigEndian;
    private readonly int _bits;
    private readonly int _sx;
    private readonly bool _planar;
    private readonly int _channels;
    private readonly int _sampleBytes;
    private readonly bool _float;
    private readonly bool _direct;
    private readonly bool _invert16;
    private readonly bool _hasAlpha;
    private readonly byte[]? _palette;
    private readonly byte[]? _lutC8;
    private readonly byte[]? _lutA8;
    private readonly ushort[]? _lutC16;
    private readonly ushort[]? _lutA16;

    private TiffPixelConverter(TiffFrame f, bool bigEndian, PixelFormat format, byte[]? palette, bool hasAlpha)
    {
        _bigEndian = bigEndian;
        _bits = f.Bits;
        _planar = f.Planar;
        _sx = f.SamplesPerBlockPixel;
        _channels = format.ChannelCount;
        _sampleBytes = format.BytesPerSample;
        _float = f.FloatSamples;
        _palette = palette;
        _hasAlpha = hasAlpha;
        bool invert = f.Photometric0 && format.Model == ColorModel.Gray && !_float;
        int max = (1 << _bits) - 1;
        if (palette is null && !_float)
        {
            if (_sampleBytes == 1 && (_bits < 8 || invert))
            {
                _lutC8 = BuildLut8(max, invert);
                _lutA8 = hasAlpha ? BuildLut8(max, false) : null;
            }
            else if (_sampleBytes == 2 && _bits < 16)
            {
                _lutC16 = BuildLut16(max, invert);
                _lutA16 = hasAlpha ? BuildLut16(max, false) : null;
            }
            else if (_sampleBytes == 2 && invert)
            {
                _invert16 = true;
            }
        }

        // A plain copy works when the stored row already has the output layout.
        _direct = palette is null && !_planar && _sx == _channels && _lutC8 is null && _lutC16 is null && !_invert16
            && (_float ? _bits == 32 : _bits == _sampleBytes * 8);
    }

    public static TiffPixelConverter Create(TiffFrame f, bool bigEndian, PixelFormat format, byte[]? palette, bool hasAlpha, bool isJpeg)
    {
        _ = isJpeg;
        return new TiffPixelConverter(f, bigEndian, format, palette, hasAlpha);
    }

    private static byte[] BuildLut8(int max, bool invert)
    {
        byte[] t = new byte[max + 1];
        for (int v = 0; v <= max; v++)
        {
            int s = invert ? max - v : v;
            t[v] = (byte)(((s * 255) + (max / 2)) / max);
        }

        return t;
    }

    private static ushort[] BuildLut16(int max, bool invert)
    {
        ushort[] t = new ushort[max + 1];
        for (int v = 0; v <= max; v++)
        {
            long s = invert ? max - v : v;
            t[v] = (ushort)(((s * 65535) + (max / 2)) / max);
        }

        return t;
    }

    /// <summary>Converts <paramref name="count"/> pixels starting at pixel <paramref name="x0"/> of a stored row.</summary>
    /// <param name="raw">The stored row (after any predictor).</param>
    /// <param name="plane">The plane the row belongs to, or 0 for contiguous data.</param>
    /// <param name="x0">First pixel to convert.</param>
    /// <param name="count">Number of pixels.</param>
    /// <param name="dst">Destination: output pixels, interleaved; for a plane only that plane's channel is written.</param>
    public void ConvertRow(ReadOnlySpan<byte> raw, int plane, int x0, int count, Span<byte> dst)
    {
        if (_direct)
        {
            int bpp = _channels * _sampleBytes;
            ReadOnlySpan<byte> src = raw.Slice(x0 * bpp, count * bpp);
            if (_sampleBytes == 1 || _bigEndian == !BitConverter.IsLittleEndian)
            {
                src.CopyTo(dst);
            }
            else
            {
                TiffKernels.SwapBytes(src, dst, _sampleBytes);
            }

            return;
        }

        if (_palette is not null)
        {
            ConvertPalette(raw, x0, count, dst);
            return;
        }

        if (_planar && plane >= _channels)
        {
            return; // a surplus extra sample that has no output channel
        }

        int jFirst = _planar ? plane : 0;
        int jLast = _planar ? plane + 1 : _channels;
        int colorChannels = _channels - (_hasAlpha ? 1 : 0);
        for (int i = 0; i < count; i++)
        {
            int pixel = x0 + i;
            for (int j = jFirst; j < jLast; j++)
            {
                int idx = (pixel * _sx) + (_planar ? 0 : j);
                bool isAlpha = j >= colorChannels;
                if (_float)
                {
                    ReadOnlySpan<byte> s4 = raw.Slice(idx * 4, 4);
                    Span<byte> d4 = dst.Slice(((i * _channels) + j) * 4, 4);
                    if (_bigEndian == !BitConverter.IsLittleEndian)
                    {
                        s4.CopyTo(d4);
                    }
                    else
                    {
                        d4[0] = s4[3];
                        d4[1] = s4[2];
                        d4[2] = s4[1];
                        d4[3] = s4[0];
                    }

                    continue;
                }

                int v = ReadSample(raw, idx);
                if (_sampleBytes == 1)
                {
                    byte[]? lut = isAlpha ? _lutA8 : _lutC8;
                    dst[(i * _channels) + j] = lut is null ? (byte)v : lut[v];
                }
                else
                {
                    ushort[]? lut = isAlpha ? _lutA16 : _lutC16;
                    int o = v;
                    if (lut is not null)
                    {
                        o = lut[v];
                    }
                    else if (_invert16 && !isAlpha)
                    {
                        o = 65535 - v;
                    }

                    BinaryPrimitives.WriteUInt16LittleEndian(dst.Slice(((i * _channels) + j) * 2, 2), (ushort)o);
                }
            }
        }
    }

    private int ReadSample(ReadOnlySpan<byte> raw, int idx)
    {
        if (_bits == 8)
        {
            return raw[idx];
        }

        if (_bits == 16)
        {
            ReadOnlySpan<byte> s = raw.Slice(idx * 2, 2);
            return _bigEndian ? BinaryPrimitives.ReadUInt16BigEndian(s) : BinaryPrimitives.ReadUInt16LittleEndian(s);
        }

        long bitPos = (long)idx * _bits;
        int byteIdx = (int)(bitPos >> 3);
        int shift = (int)(bitPos & 7);
        int window = 0;
        for (int k = 0; k < 3; k++)
        {
            window <<= 8;
            if (byteIdx + k < raw.Length)
            {
                window |= raw[byteIdx + k];
            }
        }

        // TIFF 6.0 section 3: with the default fill order, samples are packed most significant bit first.
        return (window >> (24 - shift - _bits)) & ((1 << _bits) - 1);
    }

    private void ConvertPalette(ReadOnlySpan<byte> raw, int x0, int count, Span<byte> dst)
    {
        byte[] pal = _palette!;
        int entries = pal.Length / 3;
        for (int i = 0; i < count; i++)
        {
            int v = ReadSample(raw, (x0 + i) * _sx);
            if (v >= entries)
            {
                v = entries - 1; // index beyond the map: clamp rather than fail
            }

            dst[i * 3] = pal[v * 3];
            dst[(i * 3) + 1] = pal[(v * 3) + 1];
            dst[(i * 3) + 2] = pal[(v * 3) + 2];
        }
    }
}
