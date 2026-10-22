using System;
using System.IO;
using System.Threading;
using OneImage;

// Fuzz target for the JPEG decoder: the first input byte picks the scale and whether to decode a sub-region, the rest is the file.
// Only the library's own exception types may come out; anything else is a finding.
internal static class JpegTarget
{
    private sealed class DiscardSink : IRowSink
    {
        private int _next;

        public DiscardSink(int width, int height, PixelFormat format)
        {
            Width = width;
            Height = height;
            Format = format;
        }

        public int Width { get; }

        public int Height { get; }

        public PixelFormat Format { get; }

        public void WriteRows(int y, ReadOnlySpan<byte> rows, int rowCount)
        {
            if (y != _next || y + rowCount > Height || rows.Length < rowCount * Width * Format.BytesPerPixel)
            {
                throw new InvalidOperationException("decoder delivered rows out of order");
            }

            _next += rowCount;
        }
    }

    private static readonly ImageLimits s_limits = new()
    {
        MaxPixelsPerFrame = 4_000_000,
        MaxDimension = 8192,
        MaxMetadataBytes = 1_000_000,
    };

    public static void Run(ReadOnlySpan<byte> data)
    {
        if (data.Length < 4)
        {
            return;
        }

        int mode = data[0];
        int scale = 1 << (mode & 3);
        bool region = (mode & 4) != 0;
        byte[] file = data[1..].ToArray();
        try
        {
            using var ms = new MemoryStream(file);
            ImageInfo info = JpegFormat.Instance.ReadInfo(ms, s_limits);
            FrameInfo f = info.Frames[0];
            int w = (f.Width + scale - 1) / scale;
            int h = (f.Height + scale - 1) / scale;
            var r = region ? new ImageRect(w / 3, h / 4, Math.Max(1, w / 2), Math.Max(1, h / 2)) : new ImageRect(0, 0, w, h);
            var sink = new DiscardSink(r.Width, r.Height, f.Format);
            JpegFormat.Instance.DecodeFrame(ms, info, 0, new DecodeRequest(r, scale), sink, s_limits, CancellationToken.None);
        }
        catch (ImageFormatException)
        {
        }
        catch (ImageLimitException)
        {
        }
    }
}
