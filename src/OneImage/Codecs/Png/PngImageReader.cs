using System.Buffers;
using OneImage.Kernels;

namespace OneImage;

/// <summary>Receives natural-format rows of a decoded PNG (sub)image, top to bottom, tightly packed.</summary>
internal interface IPngRowTarget
{
    void Rows(int y, ReadOnlySpan<byte> rows, int count);
}

/// <summary>Reads a PNG image's scanlines: inflate, unfilter, unpack, per batch of rows. Interlaced images are assembled in memory.</summary>
internal static class PngImageReader
{
    private static readonly int[] Xo = [0, 4, 0, 2, 0, 1, 0];
    private static readonly int[] Yo = [0, 0, 4, 0, 2, 0, 1];
    private static readonly int[] Dx = [8, 8, 4, 4, 2, 2, 1];
    private static readonly int[] Dy = [8, 8, 8, 4, 4, 2, 2];

    private const int BatchBytes = 1 << 16;

    // Big non-interlaced images inflate the next batch on a pool thread while this thread unfilters the current one. Inflating
    // is the larger half of the work, so decode time approaches the inflate time. Smaller images stay on one thread: the
    // handoff would cost more than it saves.
    private const long PipelineMinBytes = 1 << 20;
    private const int PipelineBatchBytes = 1 << 17;

    // The inflater decodes a little ahead of what is asked for (its window); the cap must not fire on that read-ahead.
    private const long InflateSlack = 1 << 20;

    // Decodes the image whose data starts at fd and sends its rows to target. Non-interlaced images stop after rowsWanted rows.
    public static void Decode(Stream src, PngInfo st, in PngFrameDesc fd, IPngRowTarget target, int rowsWanted, ImageLimits limits, CancellationToken ct)
    {
        int w = fd.Width;
        int h = fd.Height;
        int channels = st.Channels;
        int depth = st.BitDepth;
        int bpp = Math.Max(1, ((channels * depth) + 7) / 8);
        int outBpp = st.Natural.BytesPerPixel;
        long rawRowMax = RowBytes(w, channels, depth);
        if (rawRowMax >= int.MaxValue / 2)
        {
            throw new ImageLimitException("A scanline is too large.");
        }

        long total = 0;
        if (st.Interlaced)
        {
            for (int p = 0; p < 7; p++)
            {
                (int pw, int ph) = PassSize(p, w, h);
                if (pw > 0 && ph > 0)
                {
                    total += (long)ph * (1 + RowBytes(pw, channels, depth));
                }
            }
        }
        else
        {
            total = (long)h * (1 + rawRowMax);
        }

        int rawRowLen = (int)rawRowMax + 1;
        bool pipelined = !st.Interlaced && (long)Math.Min(h, rowsWanted) * rawRowLen >= PipelineMinBytes;
        int batchRows = Math.Max(1, (pipelined ? PipelineBatchBytes : BatchBytes) / rawRowLen);
        batchRows = Math.Min(batchRows, h);
        int outRowMax = checked(w * outBpp);

        byte[]? raw = null;
        byte[]? raw2 = null;
        Task? pending = null;
        byte[]? prev = null;
        byte[]? outBuf = null;
        byte[]? frame = null;
        using var reader = new PngDataReader(src, fd.DataOffset, fd.IsFdat, total + InflateSlack, limits.MaxExpansionRatio);
        try
        {
            raw = ArrayPool<byte>.Shared.Rent(checked(batchRows * rawRowLen));
            if (pipelined)
            {
                raw2 = ArrayPool<byte>.Shared.Rent(raw.Length);
            }

            prev = ArrayPool<byte>.Shared.Rent((int)rawRowMax);
            outBuf = ArrayPool<byte>.Shared.Rent(checked(batchRows * outRowMax));
            if (!st.Interlaced)
            {
                Array.Clear(prev, 0, (int)rawRowMax);
                int rows = Math.Min(h, rowsWanted);
                if (raw2 != null)
                {
                    byte[][] bufs = [raw, raw2];
                    int batches = (rows + batchRows - 1) / batchRows;
                    reader.ReadExactly(raw.AsSpan(0, Math.Min(batchRows, rows) * rawRowLen));
                    for (int b = 0; b < batches; b++)
                    {
                        ct.ThrowIfCancellationRequested();
                        if (b + 1 < batches)
                        {
                            byte[] next = bufs[(b + 1) & 1];
                            int bytes = Math.Min(batchRows, rows - ((b + 1) * batchRows)) * rawRowLen;
                            PngDataReader rd = reader;
                            pending = Task.Run(() => rd.ReadExactly(next.AsSpan(0, bytes)), ct);
                        }

                        int y = b * batchRows;
                        int cnt = Math.Min(batchRows, rows - y);
                        Unfilter(st, w, cnt, rawRowLen, bpp, bufs[b & 1], prev, outBuf, outRowMax);
                        target.Rows(y, outBuf, cnt);
                        if (pending != null)
                        {
                            Task t = pending;
                            pending = null;
                            t.GetAwaiter().GetResult();
                        }
                    }

                    reader.VerifyCurrentChunk();
                    return;
                }

                for (int y0 = 0; y0 < rows; y0 += batchRows)
                {
                    ct.ThrowIfCancellationRequested();
                    int n = Math.Min(batchRows, rows - y0);
                    Batch(reader, st, w, n, rawRowLen, bpp, raw, prev, outBuf, outRowMax);
                    target.Rows(y0, outBuf, n);
                }

                if (rows == h)
                {
                    reader.VerifyCurrentChunk();
                }

                return;
            }

            long frameBytes = (long)h * outRowMax;
            if (frameBytes > Array.MaxLength)
            {
                throw new ImageLimitException("An interlaced image of this size cannot be assembled in memory.");
            }

            frame = ArrayPool<byte>.Shared.Rent((int)frameBytes);
            for (int p = 0; p < 7; p++)
            {
                (int pw, int ph) = PassSize(p, w, h);
                if (pw == 0 || ph == 0)
                {
                    continue;
                }

                int passRawLen = (int)RowBytes(pw, channels, depth) + 1;
                int passOutRow = pw * outBpp;
                Array.Clear(prev, 0, passRawLen - 1);
                for (int y0 = 0; y0 < ph; y0 += batchRows)
                {
                    ct.ThrowIfCancellationRequested();
                    int n = Math.Min(batchRows, ph - y0);
                    Batch(reader, st, pw, n, passRawLen, bpp, raw, prev, outBuf, passOutRow);
                    for (int r = 0; r < n; r++)
                    {
                        int y = Yo[p] + ((y0 + r) * Dy[p]);
                        Span<byte> dstRow = frame.AsSpan(y * outRowMax, outRowMax);
                        ReadOnlySpan<byte> srcRow = outBuf.AsSpan(r * passOutRow, passOutRow);
                        for (int i = 0, x = Xo[p]; i < pw; i++, x += Dx[p])
                        {
                            srcRow.Slice(i * outBpp, outBpp).CopyTo(dstRow.Slice(x * outBpp, outBpp));
                        }
                    }
                }
            }

            reader.VerifyCurrentChunk();
            target.Rows(0, frame.AsSpan(0, (int)frameBytes), h);
        }
        finally
        {
            // A background inflate may still be writing into a buffer; wait for it before the buffers go back to the pool.
            // Its own failure, if any, is not reported when another error is already leaving.
            if (pending != null)
            {
                try
                {
                    pending.Wait(CancellationToken.None);
                }
                catch (AggregateException)
                {
                }
            }

            ContainerIo.Return(ref raw);
            ContainerIo.Return(ref raw2);
            ContainerIo.Return(ref prev);
            ContainerIo.Return(ref outBuf);
            ContainerIo.Return(ref frame);
        }
    }

    internal static (int Width, int Height) PassSize(int pass, int w, int h) =>
        (Math.Max(0, (w - Xo[pass] + Dx[pass] - 1) / Dx[pass]), Math.Max(0, (h - Yo[pass] + Dy[pass] - 1) / Dy[pass]));

    private static long RowBytes(int width, int channels, int depth) => (((long)width * channels * depth) + 7) / 8;

    // Reads n rows of width pw into outBuf (unfiltered and unpacked); prev carries the last unfiltered row to the next batch.
    private static void Batch(PngDataReader reader, PngInfo st, int pw, int n, int rawRowLen, int bpp, byte[] raw, byte[] prev, byte[] outBuf, int outRow)
    {
        reader.ReadExactly(raw.AsSpan(0, n * rawRowLen));
        Unfilter(st, pw, n, rawRowLen, bpp, raw, prev, outBuf, outRow);
    }

    private static void Unfilter(PngInfo st, int pw, int n, int rawRowLen, int bpp, byte[] raw, byte[] prev, byte[] outBuf, int outRow)
    {
        int rowBytes = rawRowLen - 1;
        ReadOnlySpan<byte> above = prev.AsSpan(0, rowBytes);
        for (int r = 0; r < n; r++)
        {
            Span<byte> line = raw.AsSpan(r * rawRowLen, rawRowLen);
            int filter = line[0];
            if (filter > 4)
            {
                throw new ImageFormatException($"Invalid scanline filter type {filter}.");
            }

            Span<byte> row = line[1..];
            PngFilter.Unfilter(filter, row, above, bpp);
            PngPixels.Unpack(st.Unpack, row, outBuf.AsSpan(r * outRow, outRow), pw);
            above = row;
        }

        above.CopyTo(prev);
    }
}
