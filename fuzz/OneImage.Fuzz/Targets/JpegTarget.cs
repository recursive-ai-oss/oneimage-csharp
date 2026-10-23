using System;
using System.IO;
using System.Threading;
using OneImage;
using OneImage.Codecs.Jpeg;

// Fuzz target for the JPEG decoder: the first input byte picks the scale and whether to decode a sub-region, the rest is the file.
// Every input is decoded with and without the 12-bit lookahead tables and with each helper-thread split of the transform,
// and the outcomes (pixels, or the kind of failure) must agree. Baseline files with restart intervals are also decoded with the
// intervals spread over worker threads, which must give the sequential decoder's pixels, or its failure and the rows it delivered before it. Only the library's own exception types may come out; anything else is a finding.
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

        public int Rows => _next;

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
        JpegScanDecoder.ParallelOverride = 0;
        JpegScanPipeline.Override = 0;
        JpegReader.UseMaskRefine = true;
        string withLook = Decode(file, scale, region, lookahead: true);
        string without = Decode(file, scale, region, lookahead: false);
        if (withLook != without)
        {
            throw new InvalidOperationException($"lookahead changed the outcome: {withLook} / {without}");
        }

        // On one thread the rows delivered before a failure are deterministic too, so the parallel decode must match the whole outcome.
        int[] chunks = [1, 2, 3, 5, 17, 64, 128, 400];
        JpegScanDecoder.ParallelOverride = 2 + ((mode >> 3) & 3);
        JpegScanDecoder.ParallelChunkOverride = chunks[(mode >> 5) & 7];
        try
        {
            string parallel = Decode(file, scale, region, lookahead: true);
            if (parallel != withLook)
            {
                throw new InvalidOperationException($"parallel restart intervals changed the outcome: {withLook} / {parallel}");
            }
        }
        finally
        {
            JpegScanDecoder.ParallelOverride = 0;
            JpegScanDecoder.ParallelChunkOverride = 0;
        }

        // Progressive refinement read coefficient by coefficient is the reference for the masked reader, rows delivered included.
        JpegReader.UseMaskRefine = false;
        try
        {
            string plain = Decode(file, scale, region, lookahead: true);
            if (plain != withLook)
            {
                throw new InvalidOperationException($"masked refinement changed the outcome: {withLook} / {plain}");
            }
        }
        finally
        {
            JpegReader.UseMaskRefine = true;
        }

        // Progressive scans decoded side by side give the same pixels, or the same exception; they may hand over rows of a file that
        // fails later, so a failure compares by kind and message.
        foreach (int stages in new[] { 0, 1, 0, 1 })
        {
            JpegDecoderCore.StageOverride = stages;
            JpegScanPipeline.Override = 2 + (mode >> 6);
            try
            {
                string side = Decode(file, scale, region, lookahead: true);
                bool same = withLook.StartsWith("ok", StringComparison.Ordinal) ? side == withLook : Reason(side) == Reason(withLook);
                if (!same)
                {
                    throw new InvalidOperationException($"scans decoded side by side changed the outcome: {withLook} / {side}");
                }
            }
            finally
            {
                JpegDecoderCore.StageOverride = 0;
                JpegScanPipeline.Override = 0;
            }
        }

        // Which of two failing stages reports first can vary, so failures compare by kind only.
        foreach (int stages in new[] { 1, 2 })
        {
            JpegDecoderCore.StageOverride = stages;
            JpegScanDecoder.ParallelOverride = stages == 2 ? 3 : 0;
            JpegScanDecoder.ParallelChunkOverride = 2;
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
                JpegScanDecoder.ParallelOverride = 0;
                JpegScanDecoder.ParallelChunkOverride = 0;
            }
        }
    }

    // The kind and message of a failure outcome, without the rows it delivered.
    private static string Reason(string outcome)
    {
        int i = outcome.LastIndexOf(" rows=", StringComparison.Ordinal);
        return i < 0 ? outcome : outcome[..i];
    }

    // After a failure: how many rows reached the sink and what they held.
    private static string Delivered(HashSink? sink) => sink is null ? "" : $" rows={sink.Rows} hash={sink.Hash}";

    private static string Decode(byte[] file, int scale, bool region, bool lookahead)
    {
        JpegScanDecoder.LookMinPixels = lookahead ? 0 : int.MaxValue;
        HashSink? sink = null;
        try
        {
            using var ms = new MemoryStream(file);
            ImageInfo info = JpegFormat.Instance.ReadInfo(ms, s_limits);
            FrameInfo f = info.Frames[0];
            int w = (f.Width + scale - 1) / scale;
            int h = (f.Height + scale - 1) / scale;
            var r = region ? new ImageRect(w / 3, h / 4, Math.Max(1, w / 2), Math.Max(1, h / 2)) : new ImageRect(0, 0, w, h);
            sink = new HashSink(r.Width, r.Height, f.Format);
            JpegFormat.Instance.DecodeFrame(ms, info, 0, new DecodeRequest(r, scale), sink, s_limits, CancellationToken.None);
            return "ok " + sink.Hash;
        }
        catch (ImageFormatException e)
        {
            return "format " + e.Message + Delivered(sink);
        }
        catch (ImageLimitException e)
        {
            return "limit " + e.Message + Delivered(sink);
        }
    }
}
