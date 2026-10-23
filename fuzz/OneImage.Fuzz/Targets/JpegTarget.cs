using System;
using System.IO;
using System.Threading;
using OneImage;
using OneImage.Codecs.Jpeg;

// Fuzz target for the JPEG decoder: the first input byte picks the scale and whether to decode a sub-region, the rest is the file.
// Every input is decoded with and without the 12-bit lookahead tables and with each helper-thread split of the transform,
// and the outcomes (pixels, or the kind of failure) must agree. Only the library's own exception types may come out; anything else is a finding.
internal static class JpegTarget
{
    private sealed class HashSink : IRowSink
    {
        private int _next;

        public HashSink(int width, int height, PixelFormat format)
        {
            Width = width;
            Height = height;
            Format = format;
        }

        public int Width { get; }

        public int Height { get; }

        public PixelFormat Format { get; }

        public ulong Hash { get; private set; } = 14695981039346656037UL;

        public void WriteRows(int y, ReadOnlySpan<byte> rows, int rowCount)
        {
            int n = rowCount * Width * Format.BytesPerPixel;
            if (y != _next || y + rowCount > Height || rows.Length < n)
            {
                throw new InvalidOperationException("decoder delivered rows out of order");
            }

            ulong h = Hash;
            foreach (byte b in rows[..n])
            {
                h = (h ^ b) * 1099511628211UL;
            }

            Hash = h;
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
        JpegDecoderCore.StageOverride = 0;
        string withLook = Decode(file, scale, region, lookahead: true);
        string without = Decode(file, scale, region, lookahead: false);
        if (withLook != without)
        {
            throw new InvalidOperationException($"lookahead changed the outcome: {withLook} / {without}");
        }

        // Which of two failing stages reports first can vary, so failures compare by kind only.
        foreach (int stages in new[] { 1, 2 })
        {
            JpegDecoderCore.StageOverride = stages;
            try
            {
                string staged = Decode(file, scale, region, lookahead: true);
                if (withLook.StartsWith("ok", StringComparison.Ordinal) ? staged != withLook : staged.Split(' ')[0] != withLook.Split(' ')[0])
                {
                    throw new InvalidOperationException($"stage split {stages} changed the outcome: {withLook} / {staged}");
                }
            }
            finally
            {
                JpegDecoderCore.StageOverride = 0;
            }
        }
    }

    private static string Decode(byte[] file, int scale, bool region, bool lookahead)
    {
        JpegScanDecoder.LookMinPixels = lookahead ? 0 : int.MaxValue;
        try
        {
            using var ms = new MemoryStream(file);
            ImageInfo info = JpegFormat.Instance.ReadInfo(ms, s_limits);
            FrameInfo f = info.Frames[0];
            int w = (f.Width + scale - 1) / scale;
            int h = (f.Height + scale - 1) / scale;
            var r = region ? new ImageRect(w / 3, h / 4, Math.Max(1, w / 2), Math.Max(1, h / 2)) : new ImageRect(0, 0, w, h);
            var sink = new HashSink(r.Width, r.Height, f.Format);
            JpegFormat.Instance.DecodeFrame(ms, info, 0, new DecodeRequest(r, scale), sink, s_limits, CancellationToken.None);
            return "ok " + sink.Hash;
        }
        catch (ImageFormatException e)
        {
            return "format " + e.Message;
        }
        catch (ImageLimitException e)
        {
            return "limit " + e.Message;
        }
    }
}
