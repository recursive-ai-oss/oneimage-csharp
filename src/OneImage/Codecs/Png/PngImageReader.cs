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
        int batchRows = Math.Max(1, BatchBytes / rawRowLen);
        batchRows = Math.Min(batchRows, h);
        int outRowMax = checked(w * outBpp);

        byte[]? raw = null;
        byte[]? prev = null;
        byte[]? outBuf = null;
        byte[]? frame = null;
        using var reader = new PngDataReader(src, fd.DataOffset, fd.IsFdat, total, limits.MaxExpansionRatio);
        try
        {
            raw = ArrayPool<byte>.Shared.Rent(checked(batchRows * rawRowLen));
            prev = ArrayPool<byte>.Shared.Rent((int)rawRowMax);
            outBuf = ArrayPool<byte>.Shared.Rent(checked(batchRows * outRowMax));
            if (!st.Interlaced)
            {
                Array.Clear(prev, 0, (int)rawRowMax);
                int rows = Math.Min(h, rowsWanted);
                for (int y0 = 0; y0 < rows; y0 += batchRows)
                {
                    ct.ThrowIfCancellationRequested();
                    int n = Math.Min(batchRows, rows - y0);
                    Batch(reader, st, w, n, rawRowLen, bpp, raw, prev, outBuf, outRowMax);
                    target.Rows(y0, outBuf, n);
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

            target.Rows(0, frame.AsSpan(0, (int)frameBytes), h);
        }
        finally
        {
            ContainerIo.Return(ref raw);
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
