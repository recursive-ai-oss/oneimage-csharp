using OneImage.Codecs.Tiff;

namespace OneImage.Codecs.Jpeg;

/// <summary>Decodes the JPEG streams inside TIFF files (compression 6 and 7) with the JPEG codec.</summary>
internal static class JpegTiffBridge
{
    /// <summary>Connects the JPEG codec to the TIFF codec; called when the default codecs are registered.</summary>
    public static void Connect() => TiffJpegSeam.DecodeJpeg ??= Decode;

    private sealed class SpanSink : IRowSink
    {
        private readonly byte[] _data;
        private int _rows;

        public SpanSink(int width, int height, PixelFormat format)
        {
            Width = width;
            Height = height;
            Format = format;
            _data = new byte[checked(width * height * format.BytesPerPixel)];
        }

        public int Width { get; }

        public int Height { get; }

        public PixelFormat Format { get; }

        public byte[] Data => _data;

        public int Rows => _rows;

        public void WriteRows(int y, ReadOnlySpan<byte> rows, int rowCount)
        {
            int rowBytes = Width * Format.BytesPerPixel;
            if (y != _rows || rowCount < 0 || rowCount > Height - y)
            {
                throw new ImageOperationException("JPEG rows arrived out of order.");
            }

            rows[..(rowCount * rowBytes)].CopyTo(_data.AsSpan(y * rowBytes));
            _rows += rowCount;
        }
    }

    /// <summary>
    /// The TIFF seam implementation: baseline 8-bit streams only. Three-component data is converted from YCbCr only when
    /// <paramref name="ycbcrToRgb"/> says so; four-component data is delivered as stored (no Adobe inversion).
    /// </summary>
    internal static void Decode(ReadOnlySpan<byte> jpeg, int width, int height, int components, bool ycbcrToRgb, Span<byte> destination)
    {
        using var ms = new MemoryStream(jpeg.ToArray());
        ImageLimits limits = ImageLimits.Default;
        JpegReader reader = new(ms);
        JpegHeaders h;
        try
        {
            h = JpegParser.ReadHeaders(reader, limits, new JpegTables(), collectMetadata: false);
        }
        finally
        {
            reader.Dispose();
        }

        JpegFrame f = h.Frame!;
        if (f.Width != width || f.Height != height || f.Components.Length != components || f.Precision != 8)
        {
            throw new ImageFormatException("The JPEG stream inside the TIFF does not match the TIFF's dimensions or sample layout.");
        }

        ms.Position = 0;
        JpegColorPlan Plan(JpegFrame fr, JpegHeaders hd) => components switch
        {
            1 => new JpegColorPlan(JpegColorKind.Gray, false),
            3 => new JpegColorPlan(ycbcrToRgb ? JpegColorKind.YCbCr : JpegColorKind.Rgb, false),
            _ => new JpegColorPlan(hd.AdobeTransform == 2 ? JpegColorKind.Ycck : JpegColorKind.Cmyk, false),
        };

        PixelFormat format = Plan(f, h).Format(8);
        var sink = new SpanSink(width, height, format);
        JpegDecoderCore.Decode(ms, new DecodeRequest(new ImageRect(0, 0, width, height)), sink, limits, CancellationToken.None, Plan);
        if (sink.Rows != height || destination.Length < sink.Data.Length)
        {
            throw new ImageFormatException("The JPEG stream inside the TIFF is incomplete.");
        }

        sink.Data.CopyTo(destination);
    }
}
