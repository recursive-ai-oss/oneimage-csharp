using System;
using System.IO;
using System.Threading;
using OneImage;

/// <summary>
/// Decoders for the container formats (PNG, BMP, ICO, WebP) fed through the decoder interface directly, with the first input byte
/// choosing a sub-region (bit 2), a frame (bits 3-4), and then through the public load path.
/// </summary>
internal static class CodecTargets
{
    internal sealed class DiscardSink : IRowSink
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

    public static void Png(ReadOnlySpan<byte> data) => Run(PngFormat.Instance, data);

    public static void Bmp(ReadOnlySpan<byte> data) => Run(BmpFormat.Instance, data);

    public static void Ico(ReadOnlySpan<byte> data) => Run(IcoFormat.Instance, data);

    public static void WebP(ReadOnlySpan<byte> data) => Run(new WebPFormat(), data);

    private static void Run(IImageFormat format, ReadOnlySpan<byte> data)
    {
        if (data.Length < 8)
        {
            return;
        }

        int mode = data[0];
        bool region = (mode & 4) != 0;
        int wantedFrame = (mode >> 3) & 3;
        byte[] file = data[1..].ToArray();
        Targets.Guard(() =>
        {
            using var ms = new MemoryStream(file);
            ImageInfo info = format.Decoder!.ReadInfo(ms, Targets.Limits);
            int index = Math.Min(wantedFrame, info.Frames.Count - 1);
            FrameInfo f = info.Frames[index];
            var r = region ? new ImageRect(f.Width / 3, f.Height / 4, Math.Max(1, f.Width / 2), Math.Max(1, f.Height / 2)) : new ImageRect(0, 0, f.Width, f.Height);
            var sink = new DiscardSink(r.Width, r.Height, f.Format);
            format.Decoder.DecodeFrame(ms, info, index, new DecodeRequest(r), sink, Targets.Limits, CancellationToken.None);
        });

        // The public path: lazy load, then pull a region and the last frame as Rgba32.
        Targets.Guard(() =>
        {
            var options = new LoadOptions { Limits = Targets.Limits, MaxDegree = 1 };
            Image image = Image.Load(file, options);
            Rgba32[] buffer = new Rgba32[(int)Math.Min((long)image.Width * image.Height, 4_000_000)];
            int w = image.Width;
            int h = image.Height;
            if ((long)w * h <= buffer.Length)
            {
                image.Frames[0].CopyPixelsTo<Rgba32>(buffer, new ImageRect(0, 0, w, h), maxDegree: 1);
            }

            if (image.FrameCount > 1)
            {
                ImageFrame last = image.Frames[image.FrameCount - 1];
                if ((long)last.Width * last.Height <= buffer.Length)
                {
                    last.CopyPixelsTo<Rgba32>(buffer, new ImageRect(0, 0, last.Width, last.Height), maxDegree: 1);
                }
            }
        });
    }
}
