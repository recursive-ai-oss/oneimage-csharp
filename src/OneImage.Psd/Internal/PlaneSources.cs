using System.Buffers.Binary;

namespace OneImage.Psd;

/// <summary>Supplies bands of rows as big-endian PSD sample planes: color planes first, then the alpha plane if there is one.</summary>
internal interface IBandSource
{
    int Width { get; }

    int Height { get; }

    int Depth { get; }

    int ColorPlanes { get; }

    bool HasAlpha { get; }

    /// <summary>Fills <paramref name="planes"/> (each at least rows x width x bytes per sample) with rows <paramref name="y"/>..<paramref name="y"/>+<paramref name="rows"/>.</summary>
    void Read(int y, int rows, byte[][] planes, CancellationToken ct);
}

internal static class Be
{
    public static void Put8(byte[] plane, int i, byte v) => plane[i] = v;

    public static void Put16(byte[] plane, int i, ushort v) => BinaryPrimitives.WriteUInt16BigEndian(plane.AsSpan(i * 2), v);

    public static void PutF(byte[] plane, int i, float v) => BinaryPrimitives.WriteSingleBigEndian(plane.AsSpan(i * 4), v);
}

/// <summary>A canvas or rendered image of one of the document pixel types.</summary>
internal sealed class TypedSource : IBandSource
{
    private readonly Func<int, int, byte[][], int, int, bool> _read;

    public TypedSource(int width, int height, int depth, int colorPlanes, bool hasAlpha, Func<int, int, byte[][], int, int, bool> read)
    {
        Width = width;
        Height = height;
        Depth = depth;
        ColorPlanes = colorPlanes;
        HasAlpha = hasAlpha;
        _read = read;
    }

    public int Width { get; }

    public int Height { get; }

    public int Depth { get; }

    public int ColorPlanes { get; }

    public bool HasAlpha { get; }

    public void Read(int y, int rows, byte[][] planes, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        _read(y, rows, planes, Width, rows);
    }

    /// <summary>Creates a source over a pixel reader. <paramref name="copy"/> fetches <c>rows</c> rows starting at y into the array.</summary>
    public static IBandSource For<TPix>(PixelFormat format, int width, int height, bool withAlpha, Action<int, int, TPix[]> copy)
        where TPix : unmanaged
    {
        TPix[]? buffer = null;
        int planes = format.Model == ColorModel.Gray ? 1 : 3;
        int depth = format.Sample switch { SampleType.U16 => 16, SampleType.F32 => 32, _ => 8 };
        return new TypedSource(width, height, depth, planes, withAlpha, (y, _, dst, w, rows) =>
        {
            buffer ??= new TPix[w * 128];
            if (buffer.Length < w * rows)
            {
                buffer = new TPix[w * rows];
            }

            copy(y, rows, buffer);
            Split(format, buffer, w * rows, dst, withAlpha);
            return true;
        });
    }

    private static void Split<TPix>(PixelFormat format, TPix[] px, int count, byte[][] planes, bool withAlpha)
    {
        switch (px)
        {
            case Rgba32[] p:
                for (int i = 0; i < count; i++)
                {
                    planes[0][i] = p[i].R;
                    planes[1][i] = p[i].G;
                    planes[2][i] = p[i].B;
                    if (withAlpha)
                    {
                        planes[3][i] = p[i].A;
                    }
                }

                break;
            case GrayAlpha8[] p:
                for (int i = 0; i < count; i++)
                {
                    planes[0][i] = p[i].V;
                    if (withAlpha)
                    {
                        planes[1][i] = p[i].A;
                    }
                }

                break;
            case Rgba64[] p:
                for (int i = 0; i < count; i++)
                {
                    Be.Put16(planes[0], i, p[i].R);
                    Be.Put16(planes[1], i, p[i].G);
                    Be.Put16(planes[2], i, p[i].B);
                    if (withAlpha)
                    {
                        Be.Put16(planes[3], i, p[i].A);
                    }
                }

                break;
            case RgbaFloat[] p:
                for (int i = 0; i < count; i++)
                {
                    Be.PutF(planes[0], i, p[i].R);
                    Be.PutF(planes[1], i, p[i].G);
                    Be.PutF(planes[2], i, p[i].B);
                    if (withAlpha)
                    {
                        Be.PutF(planes[3], i, p[i].A);
                    }
                }

                break;
            case Gray8[] p:
                for (int i = 0; i < count; i++)
                {
                    planes[0][i] = p[i].V;
                }

                break;
            default:
                throw new NotSupportedException($"Pixel format {format} cannot be written.");
        }
    }
}

/// <summary>Raw interleaved frame bytes (as an encoder receives them) split into planes.</summary>
internal sealed class RawFrameSource : IBandSource
{
    private readonly ImageFrameReader _frame;
    private readonly PixelFormat _format;
    private byte[]? _buf;

    public RawFrameSource(ImageFrameReader frame)
    {
        _frame = frame;
        _format = frame.Format;
        Width = frame.Width;
        Height = frame.Height;
        Depth = _format.Sample switch { SampleType.U16 => 16, SampleType.F32 => 32, _ => 8 };
        ColorPlanes = _format.ColorChannels;
        HasAlpha = _format.HasAlpha;
    }

    public int Width { get; }

    public int Height { get; }

    public int Depth { get; }

    public int ColorPlanes { get; }

    public bool HasAlpha { get; }

    public void Read(int y, int rows, byte[][] planes, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        int bps = _format.BytesPerSample;
        int pixel = _format.BytesPerPixel;
        int stride = Width * pixel;
        _buf ??= new byte[stride * 128];
        if (_buf.Length < stride * rows)
        {
            _buf = new byte[stride * rows];
        }

        _frame.ReadRegion(new ImageRect(0, y, Width, rows), _buf, stride);
        int ch = _format.ChannelCount;
        int n = Width * rows;
        for (int c = 0; c < ch; c++)
        {
            byte[] plane = planes[c];
            for (int i = 0; i < n; i++)
            {
                ReadOnlySpan<byte> s = _buf.AsSpan(i * pixel + c * bps, bps);
                switch (_format.Sample)
                {
                    case SampleType.U8:
                        plane[i] = s[0];
                        break;
                    case SampleType.U16:
                        BinaryPrimitives.WriteUInt16BigEndian(plane.AsSpan(i * 2), BitConverter.ToUInt16(s));
                        break;
                    default:
                        BinaryPrimitives.WriteSingleBigEndian(plane.AsSpan(i * 4), BitConverter.ToSingle(s));
                        break;
                }
            }
        }
    }
}

/// <summary>One constant value per plane over a rectangle (fill layers).</summary>
internal sealed class ConstantSource : IBandSource
{
    private readonly byte[][] _values;

    public ConstantSource(int width, int height, int depth, int colorPlanes, bool hasAlpha, byte[][] valuesPerPlane)
    {
        Width = width;
        Height = height;
        Depth = depth;
        ColorPlanes = colorPlanes;
        HasAlpha = hasAlpha;
        _values = valuesPerPlane;
    }

    public int Width { get; }

    public int Height { get; }

    public int Depth { get; }

    public int ColorPlanes { get; }

    public bool HasAlpha { get; }

    public void Read(int y, int rows, byte[][] planes, CancellationToken ct)
    {
        int bps = Depth / 8;
        int n = Width * rows;
        for (int p = 0; p < _values.Length; p++)
        {
            byte[] v = _values[p];
            for (int i = 0; i < n; i++)
            {
                v.CopyTo(planes[p].AsSpan(i * bps));
            }
        }
    }
}
