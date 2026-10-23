using OneImage;

namespace OneImage.Psd;

/// <summary>
/// Turns bands of PSD channel rows (any color mode and depth) into planes of the sample type a document or frame stores, converting
/// CMYK and Lab through the color engine. One converter serves one stream of bands of one width.
/// </summary>
internal sealed class BandConverter
{
    private readonly PsdColorMode _mode;
    private readonly int _depth;
    private readonly SampleKind _kind;
    private readonly int _width;
    private readonly byte[]? _palette;
    private readonly ColorProfile _docProfile;
    private readonly ImageMetadata? _sourceMeta;
    private readonly Planes _planes = new();
    private readonly int _maxRows;
    private byte[][]? _tmpU8;
    private float[][]? _tmpF;

    public BandConverter(PsdColorMode mode, int depth, SampleKind kind, int width, int maxRows, byte[]? palette, ColorProfile docProfile, byte[]? sourceIcc)
    {
        _mode = mode;
        _depth = depth;
        _kind = kind;
        _width = width;
        _maxRows = maxRows;
        _palette = palette;
        _docProfile = docProfile;
        if (sourceIcc is { Length: > 0 })
        {
            _sourceMeta = ImageMetadata.Empty.WithIccProfile(sourceIcc);
        }

        int n = ColorChannelCount(mode);
        _planes.Count = n == 1 && mode != PsdColorMode.Indexed ? 1 : 3;
        int len = checked(width * maxRows);
        switch (kind)
        {
            case SampleKind.U8:
                _planes.U8 = NewPlanes<byte>(len);
                break;
            case SampleKind.U16:
                _planes.U16 = NewPlanes<ushort>(len);
                break;
            default:
                _planes.F32 = NewPlanes<float>(len);
                break;
        }
    }

    public static int ColorChannelCount(PsdColorMode mode) => mode switch
    {
        PsdColorMode.Rgb or PsdColorMode.Lab => 3,
        PsdColorMode.Cmyk => 4,
        _ => 1,
    };

    private static T[][] NewPlanes<T>(int len) => [new T[len], new T[len], new T[len], new T[len]];

    /// <summary>Converts a band. <paramref name="color"/> holds the rows of each color channel (rows x row bytes), <paramref name="alpha"/> the transparency channel or null.</summary>
    public Planes Convert(byte[][] color, byte[]? alpha, int rows, int rowBytes)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(rows, _maxRows);

        int count = _width * rows;
        switch (_mode)
        {
            case PsdColorMode.Cmyk:
                ConvertCmyk(color, rows, rowBytes);
                break;
            case PsdColorMode.Lab:
                ConvertLab(color, rows, rowBytes);
                break;
            case PsdColorMode.Indexed:
                ConvertIndexed(color[0], rows, rowBytes);
                break;
            default:
                for (int c = 0; c < _planes.Count; c++)
                {
                    ConvertPlane(color[c], rows, rowBytes, c);
                }

                break;
        }

        _planes.HasAlpha = alpha != null;
        if (alpha != null)
        {
            ConvertPlane(alpha, rows, rowBytes, 3);
        }

        _ = count;
        return _planes;
    }

    private void ConvertPlane(byte[] src, int rows, int rowBytes, int plane)
    {
        for (int y = 0; y < rows; y++)
        {
            ReadOnlySpan<byte> row = src.AsSpan(y * rowBytes, rowBytes);
            int o = y * _width;
            switch (_kind)
            {
                case SampleKind.U8:
                    Samples.ToU8(row, _depth, _width, _planes.U8![plane].AsSpan(o, _width));
                    break;
                case SampleKind.U16:
                    Samples.ToU16(row, _depth, _width, _planes.U16![plane].AsSpan(o, _width));
                    break;
                default:
                    Samples.ToF32(row, _depth, _width, _planes.F32![plane].AsSpan(o, _width));
                    break;
            }
        }
    }

    private void ConvertIndexed(byte[] src, int rows, int rowBytes)
    {
        byte[] pal = _palette ?? throw new ImageFormatException("An indexed-color file has no palette.");
        if (pal.Length < 768)
        {
            throw new ImageFormatException("The palette of an indexed-color file is shorter than 768 bytes.");
        }

        for (int y = 0; y < rows; y++)
        {
            for (int x = 0; x < _width; x++)
            {
                int idx = src[y * rowBytes + x];
                int o = y * _width + x;
                for (int c = 0; c < 3; c++)
                {
                    byte v = pal[c * 256 + idx];
                    SetFromU8(c, o, v);
                }
            }
        }
    }

    private void SetFromU8(int plane, int o, byte v)
    {
        switch (_kind)
        {
            case SampleKind.U8:
                _planes.U8![plane][o] = v;
                break;
            case SampleKind.U16:
                _planes.U16![plane][o] = (ushort)(v * 257);
                break;
            default:
                _planes.F32![plane][o] = v / 255f;
                break;
        }
    }

    private void SetFromF32(int plane, int o, float v)
    {
        switch (_kind)
        {
            case SampleKind.U8:
                _planes.U8![plane][o] = Samples.F32To8(v);
                break;
            case SampleKind.U16:
                _planes.U16![plane][o] = Samples.F32To16(v);
                break;
            default:
                _planes.F32![plane][o] = v;
                break;
        }
    }

    // PSD stores CMYK inverted (255 is no ink); the color engine takes 0 as no ink.
    private void ConvertCmyk(byte[][] color, int rows, int rowBytes)
    {
        int count = _width * rows;
        _tmpU8 ??= [new byte[_width * _maxRows], new byte[_width * _maxRows], new byte[_width * _maxRows], new byte[_width * _maxRows]];
        var px = new Cmyk32[count];
        for (int c = 0; c < 4; c++)
        {
            for (int y = 0; y < rows; y++)
            {
                Samples.ToU8(color[c].AsSpan(y * rowBytes, rowBytes), _depth, _width, _tmpU8[c].AsSpan(y * _width, _width));
            }
        }

        for (int i = 0; i < count; i++)
        {
            px[i] = new Cmyk32((byte)(255 - _tmpU8[0][i]), (byte)(255 - _tmpU8[1][i]), (byte)(255 - _tmpU8[2][i]), (byte)(255 - _tmpU8[3][i]));
        }

        Image img = Image.FromPixels<Cmyk32>(px, _width, rows);
        if (_sourceMeta != null)
        {
            img = img.WithMetadata(_sourceMeta);
        }

        var rgb = new Rgb24[count];
        img.ConvertColor(_docProfile).CopyPixelsTo<Rgb24>(rgb, new ImageRect(0, 0, _width, rows));
        for (int i = 0; i < count; i++)
        {
            SetFromU8(0, i, rgb[i].R);
            SetFromU8(1, i, rgb[i].G);
            SetFromU8(2, i, rgb[i].B);
        }
    }

    // 8-bit Lab: L 0..255 is 0..100, a and b are offset by 128. 16-bit uses the same ranges scaled to 0..65535.
    private void ConvertLab(byte[][] color, int rows, int rowBytes)
    {
        if (_depth == 32)
        {
            throw new ImageFormatException("32-bit Lab files are not supported.");
        }

        int count = _width * rows;
        _tmpF ??= [new float[_width * _maxRows], new float[_width * _maxRows], new float[_width * _maxRows]];
        for (int c = 0; c < 3; c++)
        {
            for (int y = 0; y < rows; y++)
            {
                Samples.ToF32(color[c].AsSpan(y * rowBytes, rowBytes), _depth, _width, _tmpF[c].AsSpan(y * _width, _width));
            }
        }

        var px = new LabFloat[count];
        for (int i = 0; i < count; i++)
        {
            px[i] = new LabFloat(_tmpF[0][i] * 100f, _tmpF[1][i] * 255f - 128f, _tmpF[2][i] * 255f - 128f);
        }

        Image img = Image.FromPixels<LabFloat>(px, _width, rows);
        var rgb = new RgbFloat[count];
        img.ConvertColor(_docProfile).CopyPixelsTo<RgbFloat>(rgb, new ImageRect(0, 0, _width, rows));
        for (int i = 0; i < count; i++)
        {
            SetFromF32(0, i, rgb[i].R);
            SetFromF32(1, i, rgb[i].G);
            SetFromF32(2, i, rgb[i].B);
        }
    }
}

/// <summary>Interleaves converted planes into pixel structs or raw frame bytes.</summary>
internal static class Interleave
{
    public static void ToRgba32(Planes p, int count, Rgba32[] dst)
    {
        byte[][] u = p.U8!;
        for (int i = 0; i < count; i++)
        {
            byte r = u[0][i];
            byte g = p.Count == 1 ? r : u[1][i];
            byte b = p.Count == 1 ? r : u[2][i];
            dst[i] = new Rgba32(r, g, b, p.HasAlpha ? u[3][i] : (byte)255);
        }
    }

    public static void ToGrayAlpha8(Planes p, int count, GrayAlpha8[] dst)
    {
        byte[][] u = p.U8!;
        for (int i = 0; i < count; i++)
        {
            dst[i] = new GrayAlpha8(u[0][i], p.HasAlpha ? u[3][i] : (byte)255);
        }
    }

    public static void ToRgba64(Planes p, int count, Rgba64[] dst)
    {
        ushort[][] u = p.U16!;
        for (int i = 0; i < count; i++)
        {
            ushort r = u[0][i];
            ushort g = p.Count == 1 ? r : u[1][i];
            ushort b = p.Count == 1 ? r : u[2][i];
            dst[i] = new Rgba64(r, g, b, p.HasAlpha ? u[3][i] : (ushort)65535);
        }
    }

    public static void ToRgbaFloat(Planes p, int count, RgbaFloat[] dst)
    {
        float[][] u = p.F32!;
        for (int i = 0; i < count; i++)
        {
            float r = u[0][i];
            float g = p.Count == 1 ? r : u[1][i];
            float b = p.Count == 1 ? r : u[2][i];
            dst[i] = new RgbaFloat(r, g, b, p.HasAlpha ? u[3][i] : 1f);
        }
    }
}
