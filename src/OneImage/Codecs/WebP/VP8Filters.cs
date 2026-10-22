using System.Buffers;

namespace OneImage.Codecs.WebP;

/// <summary>The VP8 loop filter (RFC 6386 section 15), applied to a row of macroblocks at a time in macroblock order.</summary>
internal sealed class LoopFilter
{
    private readonly VP8Decoder _d;
    private readonly int[] _limits = new int[8];
    private readonly int[] _ilevels = new int[8];
    private readonly int[] _hevs = new int[8];

    public LoopFilter(VP8Decoder d)
    {
        _d = d;
        for (int s = 0; s < 4; s++)
        {
            for (int i4 = 0; i4 < 2; i4++)
            {
                d.FilterStrength(s, i4 != 0, out _limits[(s * 2) + i4], out _ilevels[(s * 2) + i4], out _hevs[(s * 2) + i4]);
            }
        }
    }

    /// <summary>Filters macroblock row <paramref name="mby"/>. Rows must be filtered in order, each after it is fully reconstructed.</summary>
    public void FilterRow(int mby)
    {
        VP8Decoder d = _d;
        int mbw = d.MbWidth;
        byte[] y = d.PlaneY;
        byte[] u = d.PlaneU;
        byte[] v = d.PlaneV;
        int ys = d.YStride;
        int uvs = d.UvStride;
        bool simple = d.FilterType == 1;
        for (int mbx = 0; mbx < mbw; mbx++)
        {
            int mb = (mby * mbw) + mbx;
            int idx = (d.FilterSegment[mb] * 2) + d.FilterI4[mb];
            int limit = _limits[idx];
            if (limit == 0)
            {
                continue;
            }

            bool inner = d.FilterInner[mb] != 0;
            int yo = (mby * 16 * ys) + (mbx * 16);
            if (simple)
            {
                Kernels.WebPLoopFilterKernel.FilterSimple(y, yo, ys, mbx > 0, mby > 0, inner, limit);
            }
            else
            {
                int uvo = (mby * 8 * uvs) + (mbx * 8);
                Kernels.WebPLoopFilterKernel.FilterNormal(y, yo, ys, u, v, uvo, uvs, mbx > 0, mby > 0, inner, limit, _ilevels[idx], _hevs[idx]);
            }
        }
    }
}

/// <summary>YUV 4:2:0 to RGB conversion with libwebp-compatible "fancy" chroma upsampling and integer color math.</summary>
internal static class Upsampler
{
    private const int Batch = 16;

    // Fixed-point BT.601 video-range conversion; 14-bit precision with 6 bits of fraction left in the clip.
    private static int MultHi(int v, int coeff) => (v * coeff) >> 8;

    private static byte Clip8(int v) => (byte)((v & ~((256 << 6) - 1)) == 0 ? v >> 6 : v < 0 ? 0 : 255);

    public static void Emit(byte[] yp, byte[] up, byte[] vp, int ys, int uvs, int width, int height, byte[]? alpha, IRowSink sink, CancellationToken token)
    {
        int bpp = sink.Format.BytesPerPixel;
        int rowBytes = width * bpp;
        byte[] batch = ArrayPool<byte>.Shared.Rent(rowBytes * (Batch + 2));
        byte[] chroma = ArrayPool<byte>.Shared.Rent(4 * width);
        try
        {
            int start = 0;
            int count = 0;
            void Flush()
            {
                if (count > 0)
                {
                    sink.WriteRows(start, batch.AsSpan(0, count * rowBytes), count);
                    start += count;
                    count = 0;
                }
            }

            // Row 0 uses its own chroma row on both sides.
            Pair(yp, 0, -1, up, vp, 0, 0, batch.AsSpan(0, rowBytes), default, width, bpp, chroma);
            FillAlpha(batch.AsSpan(0, rowBytes), alpha, 0, width, bpp);
            count = 1;
            for (int r = 1; (2 * r) - 1 < height; r++)
            {
                if ((r & 7) == 0)
                {
                    token.ThrowIfCancellationRequested();
                }

                int top = (2 * r) - 1;
                bool hasBottom = top + 1 < height;
                int topUv = (r - 1) * uvs;
                int curUv = hasBottom ? r * uvs : topUv;
                Span<byte> dstTop = batch.AsSpan(count * rowBytes, rowBytes);
                Span<byte> dstBot = hasBottom ? batch.AsSpan((count + 1) * rowBytes, rowBytes) : default;
                Pair(yp, top * ys, hasBottom ? (top + 1) * ys : -1, up, vp, topUv, curUv, dstTop, dstBot, width, bpp, chroma);
                FillAlpha(dstTop, alpha, top, width, bpp);
                if (hasBottom)
                {
                    FillAlpha(dstBot, alpha, top + 1, width, bpp);
                }

                count += hasBottom ? 2 : 1;
                if (count >= Batch)
                {
                    Flush();
                }
            }

            Flush();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(batch);
            ArrayPool<byte>.Shared.Return(chroma);
        }
    }

    private static void FillAlpha(Span<byte> row, byte[]? alpha, int y, int width, int bpp)
    {
        if (bpp != 4)
        {
            return;
        }

        if (alpha == null)
        {
            for (int x = 0; x < width; x++)
            {
                row[(x * 4) + 3] = 255;
            }
        }
        else
        {
            int o = y * width;
            for (int x = 0; x < width; x++)
            {
                row[(x * 4) + 3] = alpha[o + x];
            }
        }
    }

    // Converts one or two luma rows sharing the chroma rows topUv (nearer the top output row) and curUv.
    private static void Pair(byte[] yp, int yTop, int yBot, byte[] up, byte[] vp, int topUv, int curUv, Span<byte> dstTop, Span<byte> dstBot, int width, int bpp, byte[] chroma)
    {
        bool bottom = yBot >= 0;
        int cw = ((width - 1) >> 1) + 1;
        Span<byte> c = chroma;
        Span<byte> uT = c.Slice(0, width);
        Span<byte> vT = c.Slice(width, width);
        Span<byte> uB = c.Slice(2 * width, width);
        Span<byte> vB = c.Slice(3 * width, width);
        Chroma(up.AsSpan(topUv, cw), up.AsSpan(curUv, cw), width, uT, uB, bottom);
        Chroma(vp.AsSpan(topUv, cw), vp.AsSpan(curUv, cw), width, vT, vB, bottom);
        YuvRow(yp.AsSpan(yTop, width), uT, vT, dstTop, width, bpp);
        if (bottom)
        {
            YuvRow(yp.AsSpan(yBot, width), uB, vB, dstBot, width, bpp);
        }
    }

    // Fancy upsampling of one chroma plane (libwebp's 9-3-3-1 kernel): t is the chroma row nearer the top output row, c the other.
    private static void Chroma(ReadOnlySpan<byte> t, ReadOnlySpan<byte> c, int width, Span<byte> top, Span<byte> bot, bool bottom)
    {
        int lastPair = (width - 1) >> 1;
        t = t[..(lastPair + 1)];
        c = c[..(lastPair + 1)];
        top = top[..width];
        int tl = t[0];
        int l = c[0];
        top[0] = (byte)(((3 * tl) + l + 2) >> 2);
        if (bottom)
        {
            bot = bot[..width];
            bot[0] = (byte)(((3 * l) + tl + 2) >> 2);
        }

        for (int x = 1; x <= lastPair; x++)
        {
            int tc = t[x];
            int uc = c[x];
            int avg = tl + tc + l + uc + 8;
            int d12 = (avg + (2 * (tc + l))) >> 3;
            int d03 = (avg + (2 * (tl + uc))) >> 3;
            top[(2 * x) - 1] = (byte)((d12 + tl) >> 1);
            top[2 * x] = (byte)((d03 + tc) >> 1);
            if (bottom)
            {
                bot[(2 * x) - 1] = (byte)((d03 + l) >> 1);
                bot[2 * x] = (byte)((d12 + uc) >> 1);
            }

            tl = tc;
            l = uc;
        }

        if ((width & 1) == 0)
        {
            top[width - 1] = (byte)(((3 * tl) + l + 2) >> 2);
            if (bottom)
            {
                bot[width - 1] = (byte)(((3 * l) + tl + 2) >> 2);
            }
        }
    }

    private static void YuvRow(ReadOnlySpan<byte> y, ReadOnlySpan<byte> u, ReadOnlySpan<byte> v, Span<byte> dst, int width, int bpp)
    {
        y = y[..width];
        u = u[..width];
        v = v[..width];
        dst = dst[..(width * bpp)];
        int o = 0;
        for (int x = 0; x < width; x++, o += bpp)
        {
            int yy = MultHi(y[x], 19077);
            int uu = u[x];
            int vv = v[x];
            dst[o] = Clip8(yy + MultHi(vv, 26149) - 14234);
            dst[o + 1] = Clip8(yy - MultHi(uu, 6419) - MultHi(vv, 13320) + 8708);
            dst[o + 2] = Clip8(yy + MultHi(uu, 33050) - 17685);
        }
    }
}

/// <summary>Decodes the ALPH chunk of a lossy file (RFC 9649 section 2.7.1.2) into an 8-bit plane.</summary>
internal static class AlphaDecoder
{
    public static void Decode(byte[] data, int length, int width, int height, byte[] plane, ImageLimits limits, CancellationToken token)
    {
        if (length < 1)
        {
            throw new ImageFormatException("The alpha chunk is empty.");
        }

        int method = data[0] & 3;
        int filter = (data[0] >> 2) & 3;
        if (method > 1)
        {
            throw new ImageFormatException("Unknown alpha compression method.");
        }

        if (method == 0)
        {
            long size = (long)width * height;
            if (length - 1 < size)
            {
                throw new ImageFormatException("The alpha plane is truncated.");
            }

            for (int y = 0; y < height; y++)
            {
                Unfilter(filter, data.AsSpan(1 + (y * width), width), plane, y, width);
            }

            return;
        }

        using var dec = new VP8LDecoder(data, 1, length - 1, limits, token);
        dec.DecodeHeaderless(width, height, new GreenSink(plane, width, filter));
    }

    private static void Unfilter(int filter, ReadOnlySpan<byte> input, byte[] plane, int y, int width)
    {
        Span<byte> cur = plane.AsSpan(y * width, width);
        ReadOnlySpan<byte> prev = y > 0 ? plane.AsSpan((y - 1) * width, width) : default;
        if (filter == 0)
        {
            input.CopyTo(cur);
            return;
        }

        if (y == 0)
        {
            // The first row is predicted from the left for every filter type.
            cur[0] = input[0];
            for (int x = 1; x < width; x++)
            {
                cur[x] = (byte)(input[x] + cur[x - 1]);
            }

            return;
        }

        switch (filter)
        {
            case 1:
                cur[0] = (byte)(input[0] + prev[0]);
                for (int x = 1; x < width; x++)
                {
                    cur[x] = (byte)(input[x] + cur[x - 1]);
                }

                break;
            case 2:
                for (int x = 0; x < width; x++)
                {
                    cur[x] = (byte)(input[x] + prev[x]);
                }

                break;
            default:
                cur[0] = (byte)(input[0] + prev[0]);
                for (int x = 1; x < width; x++)
                {
                    int g = cur[x - 1] + prev[x] - prev[x - 1];
                    cur[x] = (byte)(input[x] + (g < 0 ? 0 : g > 255 ? 255 : g));
                }

                break;
        }
    }

    private sealed class GreenSink : IArgbRowSink
    {
        private readonly byte[] _plane;
        private readonly int _width;
        private readonly int _filter;
        private readonly byte[] _row;

        public GreenSink(byte[] plane, int width, int filter)
        {
            _plane = plane;
            _width = width;
            _filter = filter;
            _row = new byte[width];
        }

        public void WriteRow(int y, ReadOnlySpan<uint> argb)
        {
            for (int x = 0; x < _width; x++)
            {
                _row[x] = (byte)(argb[x] >> 8);
            }

            Unfilter(_filter, _row, _plane, y, _width);
        }
    }
}
