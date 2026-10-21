using System.Runtime.InteropServices;

namespace OneImage.Tests.Jpeg;

/// <summary>Collects delivered rows into one array.</summary>
internal sealed class ArraySink : IRowSink
{
    private readonly byte[] _data;
    private readonly int _rowBytes;

    public ArraySink(int width, int height, PixelFormat format)
    {
        Width = width;
        Height = height;
        Format = format;
        _rowBytes = width * format.BytesPerPixel;
        _data = new byte[_rowBytes * height];
    }

    public int Width { get; }

    public int Height { get; }

    public PixelFormat Format { get; }

    public int RowsWritten { get; private set; }

    public byte[] Data => _data;

    public void WriteRows(int y, ReadOnlySpan<byte> rows, int rowCount)
    {
        if (y != RowsWritten || y + rowCount > Height)
        {
            throw new ImageOperationException("rows out of order");
        }

        rows[..(rowCount * _rowBytes)].CopyTo(_data.AsSpan(y * _rowBytes));
        RowsWritten += rowCount;
    }
}

internal static class JpegTestSupport
{
    public static string Dir { get; } = Path.Combine(AppContext.BaseDirectory, "testdata", "jpeg");

    public static IEnumerable<string> CorpusNames() =>
        Directory.GetFiles(Dir, "*.ref").Select(f => Path.GetFileNameWithoutExtension(f)!).OrderBy(n => n, StringComparer.Ordinal);

    public static byte[] Read(string name) => File.ReadAllBytes(Path.Combine(Dir, name + ".jpg"));

    public static (ImageInfo Info, byte[] Data, PixelFormat Format) Decode(byte[] jpeg, int scale = 1, ImageRect? region = null, ImageLimits? limits = null)
    {
        limits ??= ImageLimits.Default;
        using var ms = new MemoryStream(jpeg);
        ImageInfo info = JpegFormat.Instance.ReadInfo(ms, limits);
        FrameInfo fi = info.Frames[0];
        int ow = (fi.Width + scale - 1) / scale;
        int oh = (fi.Height + scale - 1) / scale;
        ImageRect r = region ?? new ImageRect(0, 0, ow, oh);
        var sink = new ArraySink(r.Width, r.Height, fi.Format);
        JpegFormat.Instance.DecodeFrame(ms, info, 0, new DecodeRequest(r, scale), sink, limits, CancellationToken.None);
        if (sink.RowsWritten != r.Height)
        {
            throw new InvalidOperationException("incomplete");
        }

        return (info, sink.Data, fi.Format);
    }

    /// <summary>Splits a .ref file into the four scales (1, 2, 4, 8).</summary>
    public static byte[][] ReadRef(string name, int width, int height, int comps, int precision)
    {
        byte[] all = File.ReadAllBytes(Path.Combine(Dir, name + ".ref"));
        int bps = precision == 8 ? 1 : 2;
        var parts = new byte[4][];
        int off = 0;
        int i = 0;
        foreach (int d in new[] { 1, 2, 4, 8 })
        {
            int len = ((width + d - 1) / d) * ((height + d - 1) / d) * comps * bps;
            parts[i++] = all.AsSpan(off, len).ToArray();
            off += len;
        }

        return parts;
    }

    public static (int Max, double Mean) Compare(ReadOnlySpan<byte> ours, ReadOnlySpan<byte> reference, int precision, bool invert, int maxValue)
    {
        int max = 0;
        long sum = 0;
        int n = reference.Length / (precision == 8 ? 1 : 2);
        for (int i = 0; i < n; i++)
        {
            int a;
            int b;
            if (precision == 8)
            {
                a = ours[i];
                b = reference[i];
            }
            else
            {
                a = MemoryMarshal.Cast<byte, ushort>(ours)[i] >> 4;
                b = reference[2 * i] | (reference[(2 * i) + 1] << 8);
            }

            if (invert)
            {
                b = maxValue - b;
            }

            int d = Math.Abs(a - b);
            max = Math.Max(max, d);
            sum += d;
        }

        return (max, n == 0 ? 0 : (double)sum / n);
    }
}
