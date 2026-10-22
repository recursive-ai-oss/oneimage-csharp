using System;
using System.IO;
using System.Threading;
using OneImage;
using SharpFuzz;

// Fuzz target for the PNG, BMP and ICO decoders. The first input byte picks the format (low two bits), whether to decode a
// sub-region (bit 2) and which frame to decode (bits 3-4); the rest is the file. Only the library's own exception types
// may come out; anything else is a finding.
internal static class Program
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
        MaxFrames = 64,
    };

    private static void Main()
    {
        Fuzzer.LibFuzzer.Run(Target);
    }

    private static void Target(ReadOnlySpan<byte> data)
    {
        if (data.Length < 8)
        {
            return;
        }

        int mode = data[0];
        IImageFormat format = (mode & 3) switch { 0 => PngFormat.Instance, 1 => BmpFormat.Instance, _ => IcoFormat.Instance };
        bool region = (mode & 4) != 0;
        int wantedFrame = (mode >> 3) & 3;
        byte[] file = data[1..].ToArray();
        try
        {
            using var ms = new MemoryStream(file);
            ImageInfo info = format.Decoder!.ReadInfo(ms, s_limits);
            int index = Math.Min(wantedFrame, info.Frames.Count - 1);
            FrameInfo f = info.Frames[index];
            var r = region ? new ImageRect(f.Width / 3, f.Height / 4, Math.Max(1, f.Width / 2), Math.Max(1, f.Height / 2)) : new ImageRect(0, 0, f.Width, f.Height);
            var sink = new DiscardSink(r.Width, r.Height, f.Format);
            format.Decoder.DecodeFrame(ms, info, index, new DecodeRequest(r), sink, s_limits, CancellationToken.None);
        }
        catch (ImageFormatException)
        {
        }
        catch (ImageLimitException)
        {
        }
    }
}
