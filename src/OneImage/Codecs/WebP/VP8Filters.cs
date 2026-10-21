using System.Buffers;

namespace OneImage.Codecs.WebP;

/// <summary>The VP8 loop filter (RFC 6386 section 15), applied to the finished frame in macroblock order.</summary>
internal static class LoopFilter
{
    public static void FilterFrame(VP8Decoder d)
    {
        int mbw = d.MbWidth;
        int mbh = d.MbHeight;
        byte[] y = d.PlaneY;
        byte[] u = d.PlaneU;
        byte[] v = d.PlaneV;
        int ys = d.YStride;
        int uvs = d.UvStride;
        bool simple = d.FilterType == 1;

        Span<int> limits = stackalloc int[8];
        Span<int> ilevels = stackalloc int[8];
        Span<int> hevs = stackalloc int[8];
        for (int s = 0; s < 4; s++)
        {
            for (int i4 = 0; i4 < 2; i4++)
            {
                d.FilterStrength(s, i4 != 0, out limits[(s * 2) + i4], out ilevels[(s * 2) + i4], out hevs[(s * 2) + i4]);
            }
        }

        for (int mby = 0; mby < mbh; mby++)
        {
            d.Token.ThrowIfCancellationRequested();
            for (int mbx = 0; mbx < mbw; mbx++)
            {
                int mb = (mby * mbw) + mbx;
                int idx = (d.FilterSegment[mb] * 2) + d.FilterI4[mb];
                int limit = limits[idx];
                if (limit == 0)
                {
                    continue;
                }

                bool inner = d.FilterInner[mb] != 0;
                int yo = (mby * 16 * ys) + (mbx * 16);
                int uvo = (mby * 8 * uvs) + (mbx * 8);
                if (simple)
                {
                    if (mbx > 0)
                    {
                        SimpleEdge(y, yo, 1, ys, 16, (2 * (limit + 4)) + 1);
                    }

                    if (inner)
                    {
                        for (int k = 1; k <= 3; k++)
                        {
                            SimpleEdge(y, yo + (4 * k), 1, ys, 16, (2 * limit) + 1);
                        }
                    }

                    if (mby > 0)
                    {
                        SimpleEdge(y, yo, ys, 1, 16, (2 * (limit + 4)) + 1);
                    }

                    if (inner)
                    {
                        for (int k = 1; k <= 3; k++)
                        {
                            SimpleEdge(y, yo + (4 * k * ys), ys, 1, 16, (2 * limit) + 1);
                        }
                    }

                    continue;
                }

                int il = ilevels[idx];
                int hev = hevs[idx];
                int tMb = (2 * (limit + 4)) + 1;
                int tIn = (2 * limit) + 1;
                if (mbx > 0)
                {
                    MbEdge(y, yo, 1, ys, 16, tMb, il, hev);
                    MbEdge(u, uvo, 1, uvs, 8, tMb, il, hev);
                    MbEdge(v, uvo, 1, uvs, 8, tMb, il, hev);
                }

                if (inner)
                {
                    for (int k = 1; k <= 3; k++)
                    {
                        InnerEdge(y, yo + (4 * k), 1, ys, 16, tIn, il, hev);
                    }

                    InnerEdge(u, uvo + 4, 1, uvs, 8, tIn, il, hev);
                    InnerEdge(v, uvo + 4, 1, uvs, 8, tIn, il, hev);
                }

                if (mby > 0)
                {
                    MbEdge(y, yo, ys, 1, 16, tMb, il, hev);
                    MbEdge(u, uvo, uvs, 1, 8, tMb, il, hev);
                    MbEdge(v, uvo, uvs, 1, 8, tMb, il, hev);
                }

                if (inner)
                {
                    for (int k = 1; k <= 3; k++)
                    {
                        InnerEdge(y, yo + (4 * k * ys), ys, 1, 16, tIn, il, hev);
                    }

                    InnerEdge(u, uvo + (4 * uvs), uvs, 1, 8, tIn, il, hev);
                    InnerEdge(v, uvo + (4 * uvs), uvs, 1, 8, tIn, il, hev);
                }
            }
        }
    }

    private static int Abs(int v) => v < 0 ? -v : v;

    private static int SClip1(int v) => v < -128 ? -128 : v > 127 ? 127 : v;

    private static int SClip2(int v) => v < -16 ? -16 : v > 15 ? 15 : v;

    private static byte Clip1(int v) => (byte)(v < 0 ? 0 : v > 255 ? 255 : v);

    // step: distance between samples across the edge; along: distance between successive positions on the edge.
    private static void SimpleEdge(byte[] p, int o, int step, int along, int count, int t2)
    {
        for (int i = 0; i < count; i++, o += along)
        {
            int p1 = p[o - (2 * step)];
            int p0 = p[o - step];
            int q0 = p[o];
            int q1 = p[o + step];
            if ((4 * Abs(p0 - q0)) + Abs(p1 - q1) <= t2)
            {
                DoFilter2(p, o, step, p1, p0, q0, q1);
            }
        }
    }

    private static void MbEdge(byte[] p, int o, int step, int along, int count, int t2, int ithresh, int hevThresh)
    {
        for (int i = 0; i < count; i++, o += along)
        {
            if (!NeedsFilter2(p, o, step, t2, ithresh))
            {
                continue;
            }

            if (Hev(p, o, step, hevThresh))
            {
                DoFilter2(p, o, step, p[o - (2 * step)], p[o - step], p[o], p[o + step]);
            }
            else
            {
                DoFilter6(p, o, step);
            }
        }
    }

    private static void InnerEdge(byte[] p, int o, int step, int along, int count, int t2, int ithresh, int hevThresh)
    {
        for (int i = 0; i < count; i++, o += along)
        {
            if (!NeedsFilter2(p, o, step, t2, ithresh))
            {
                continue;
            }

            if (Hev(p, o, step, hevThresh))
            {
                DoFilter2(p, o, step, p[o - (2 * step)], p[o - step], p[o], p[o + step]);
            }
            else
            {
                DoFilter4(p, o, step);
            }
        }
    }

    private static bool NeedsFilter2(byte[] p, int o, int step, int t2, int it)
    {
        int p3 = p[o - (4 * step)];
        int p2 = p[o - (3 * step)];
        int p1 = p[o - (2 * step)];
        int p0 = p[o - step];
        int q0 = p[o];
        int q1 = p[o + step];
        int q2 = p[o + (2 * step)];
        int q3 = p[o + (3 * step)];
        if ((4 * Abs(p0 - q0)) + Abs(p1 - q1) > t2)
        {
            return false;
        }

        return Abs(p3 - p2) <= it && Abs(p2 - p1) <= it && Abs(p1 - p0) <= it && Abs(q3 - q2) <= it && Abs(q2 - q1) <= it && Abs(q1 - q0) <= it;
    }

    private static bool Hev(byte[] p, int o, int step, int thresh) =>
        Abs(p[o - (2 * step)] - p[o - step]) > thresh || Abs(p[o + step] - p[o]) > thresh;

    // Adjusts the two pixels next to the edge, using the outer taps.
    private static void DoFilter2(byte[] p, int o, int step, int p1, int p0, int q0, int q1)
    {
        int a = (3 * (q0 - p0)) + SClip1(p1 - q1);
        int a1 = SClip2((a + 4) >> 3);
        int a2 = SClip2((a + 3) >> 3);
        p[o - step] = Clip1(p0 + a2);
        p[o] = Clip1(q0 - a1);
    }

    private static void DoFilter4(byte[] p, int o, int step)
    {
        int p1 = p[o - (2 * step)];
        int p0 = p[o - step];
        int q0 = p[o];
        int q1 = p[o + step];
        int a = 3 * (q0 - p0);
        int a1 = SClip2((a + 4) >> 3);
        int a2 = SClip2((a + 3) >> 3);
        int a3 = (a1 + 1) >> 1;
        p[o - (2 * step)] = Clip1(p1 + a3);
        p[o - step] = Clip1(p0 + a2);
        p[o] = Clip1(q0 - a1);
        p[o + step] = Clip1(q1 - a3);
    }

    private static void DoFilter6(byte[] p, int o, int step)
    {
        int p2 = p[o - (3 * step)];
        int p1 = p[o - (2 * step)];
        int p0 = p[o - step];
        int q0 = p[o];
        int q1 = p[o + step];
        int q2 = p[o + (2 * step)];
        int a = SClip1((3 * (q0 - p0)) + SClip1(p1 - q1));
        int a1 = ((27 * a) + 63) >> 7;
        int a2 = ((18 * a) + 63) >> 7;
        int a3 = ((9 * a) + 63) >> 7;
        p[o - (3 * step)] = Clip1(p2 + a3);
        p[o - (2 * step)] = Clip1(p1 + a2);
        p[o - step] = Clip1(p0 + a1);
        p[o] = Clip1(q0 - a1);
        p[o + step] = Clip1(q1 - a2);
        p[o + (2 * step)] = Clip1(q2 - a3);
    }
}

/// <summary>YUV 4:2:0 to RGB conversion with libwebp-compatible "fancy" chroma upsampling and integer color math.</summary>
internal static class Upsampler
{
    private const int Batch = 16;

    // Fixed-point BT.601 video-range conversion; 14-bit precision with 6 bits of fraction left in the clip.
    private static int MultHi(int v, int coeff) => (v * coeff) >> 8;

    private static byte Clip8(int v) => (byte)((v & ~((256 << 6) - 1)) == 0 ? v >> 6 : v < 0 ? 0 : 255);

    private static void Put(Span<byte> dst, int x, int bpp, int y, int u, int v)
    {
        int yy = MultHi(y, 19077);
        int o = x * bpp;
        dst[o] = Clip8(yy + MultHi(v, 26149) - 14234);
        dst[o + 1] = Clip8(yy - MultHi(u, 6419) - MultHi(v, 13320) + 8708);
        dst[o + 2] = Clip8(yy + MultHi(u, 33050) - 17685);
    }

    public static void Emit(byte[] yp, byte[] up, byte[] vp, int ys, int uvs, int width, int height, byte[]? alpha, IRowSink sink, CancellationToken token)
    {
        int bpp = sink.Format.BytesPerPixel;
        int rowBytes = width * bpp;
        byte[] batch = ArrayPool<byte>.Shared.Rent(rowBytes * (Batch + 2));
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
            Pair(yp, 0, -1, up, vp, 0, 0, batch.AsSpan(0, rowBytes), default, width, bpp);
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
                Pair(yp, top * ys, hasBottom ? (top + 1) * ys : -1, up, vp, topUv, curUv, dstTop, dstBot, width, bpp);
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
    private static void Pair(byte[] yp, int yTop, int yBot, byte[] up, byte[] vp, int topUv, int curUv, Span<byte> dstTop, Span<byte> dstBot, int width, int bpp)
    {
        bool bottom = yBot >= 0;
        int lastPair = (width - 1) >> 1;
        int tlU = up[topUv];
        int tlV = vp[topUv];
        int lU = up[curUv];
        int lV = vp[curUv];
        Put(dstTop, 0, bpp, yp[yTop], ((3 * tlU) + lU + 2) >> 2, ((3 * tlV) + lV + 2) >> 2);
        if (bottom)
        {
            Put(dstBot, 0, bpp, yp[yBot], ((3 * lU) + tlU + 2) >> 2, ((3 * lV) + tlV + 2) >> 2);
        }

        for (int x = 1; x <= lastPair; x++)
        {
            int tU = up[topUv + x];
            int tV = vp[topUv + x];
            int uU = up[curUv + x];
            int uV = vp[curUv + x];

            int avgU = tlU + tU + lU + uU + 8;
            int avgV = tlV + tV + lV + uV + 8;
            int d12U = (avgU + (2 * (tU + lU))) >> 3;
            int d12V = (avgV + (2 * (tV + lV))) >> 3;
            int d03U = (avgU + (2 * (tlU + uU))) >> 3;
            int d03V = (avgV + (2 * (tlV + uV))) >> 3;

            Put(dstTop, (2 * x) - 1, bpp, yp[yTop + (2 * x) - 1], (d12U + tlU) >> 1, (d12V + tlV) >> 1);
            Put(dstTop, 2 * x, bpp, yp[yTop + (2 * x)], (d03U + tU) >> 1, (d03V + tV) >> 1);
            if (bottom)
            {
                Put(dstBot, (2 * x) - 1, bpp, yp[yBot + (2 * x) - 1], (d03U + lU) >> 1, (d03V + lV) >> 1);
                Put(dstBot, 2 * x, bpp, yp[yBot + (2 * x)], (d12U + uU) >> 1, (d12V + uV) >> 1);
            }

            tlU = tU;
            tlV = tV;
            lU = uU;
            lV = uV;
        }

        if ((width & 1) == 0)
        {
            Put(dstTop, width - 1, bpp, yp[yTop + width - 1], ((3 * tlU) + lU + 2) >> 2, ((3 * tlV) + lV + 2) >> 2);
            if (bottom)
            {
                Put(dstBot, width - 1, bpp, yp[yBot + width - 1], ((3 * lU) + tlU + 2) >> 2, ((3 * lV) + tlV + 2) >> 2);
            }
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
