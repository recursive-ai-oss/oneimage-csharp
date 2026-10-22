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
    public void FilterRow(int mby, int slot)
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
            int yo = (slot * 16 * ys) + (mbx * 16);
            if (simple)
            {
                Kernels.WebPLoopFilterKernel.FilterSimple(y, yo, ys, mbx > 0, mby > 0, inner, limit);
            }
            else
            {
                int uvo = (slot * 8 * uvs) + (mbx * 8);
                Kernels.WebPLoopFilterKernel.FilterNormal(y, yo, ys, u, v, uvo, uvs, mbx > 0, mby > 0, inner, limit, _ilevels[idx], _hevs[idx]);
            }
        }
    }
}

/// <summary>YUV 4:2:0 to RGB conversion with libwebp-compatible "fancy" chroma upsampling and integer color math.</summary>
internal sealed class Upsampler : IDisposable
{
    private const int Batch = 16;

    // Fixed-point BT.601 video-range conversion; 14-bit precision with 6 bits of fraction left in the clip.
    private static int MultHi(int v, int coeff) => (v * coeff) >> 8;

    private static byte Clip8(int v) => (byte)((v & ~((256 << 6) - 1)) == 0 ? v >> 6 : v < 0 ? 0 : 255);

    private readonly IRowSink _sink;
    private readonly int _width;
    private readonly int _height;
    private readonly int _bpp;
    private readonly int _rowBytes;
    private readonly CancellationToken _token;
    private readonly byte[] _batch;
    private readonly byte[] _chroma;
    private int _start;
    private int _count;
    private int _next = 1;
    private bool _rowZero;

    public Upsampler(IRowSink sink, int width, int height, CancellationToken token)
    {
        _sink = sink;
        _width = width;
        _height = height;
        _bpp = sink.Format.BytesPerPixel;
        _rowBytes = width * _bpp;
        _token = token;
        _batch = ArrayPool<byte>.Shared.Rent(_rowBytes * (Batch + 2));
        _chroma = ArrayPool<byte>.Shared.Rent(4 * width);
    }

    public void Dispose()
    {
        ArrayPool<byte>.Shared.Return(_batch);
        ArrayPool<byte>.Shared.Return(_chroma);
    }

    private void Flush()
    {
        if (_count > 0)
        {
            _sink.WriteRows(_start, _batch.AsSpan(0, _count * _rowBytes), _count);
            _start += _count;
            _count = 0;
        }
    }

    /// <summary>
    /// Converts every output row that only depends on chroma rows below <paramref name="finalChroma"/> (all of them when
    /// <paramref name="last"/>). The planes hold luma lines from <paramref name="lumaBase"/> and chroma lines from
    /// <paramref name="chromaBase"/> onward; lines before those are no longer needed.
    /// </summary>
    public void Advance(byte[] yp, byte[] up, byte[] vp, int ys, int uvs, int lumaBase, int chromaBase, int finalChroma, byte[]? alpha, bool last)
    {
        int width = _width;
        if (!_rowZero && (finalChroma >= 1 || last))
        {
            // Row 0 uses its own chroma row on both sides.
            Pair(yp, (0 - lumaBase) * ys, -1, up, vp, (0 - chromaBase) * uvs, (0 - chromaBase) * uvs, _batch.AsSpan(0, _rowBytes), default, width, _bpp, _chroma);
            FillAlpha(_batch.AsSpan(0, _rowBytes), alpha, 0, width, _bpp);
            _count = 1;
            _rowZero = true;
        }

        if (!_rowZero)
        {
            return;
        }

        for (int r = _next; (2 * r) - 1 < _height; r++)
        {
            int top = (2 * r) - 1;
            bool hasBottom = top + 1 < _height;
            if (!last && r >= finalChroma)
            {
                break;
            }

            if ((r & 7) == 0)
            {
                _token.ThrowIfCancellationRequested();
            }

            int topUv = (r - 1 - chromaBase) * uvs;
            int curUv = hasBottom ? (r - chromaBase) * uvs : topUv;
            Span<byte> dstTop = _batch.AsSpan(_count * _rowBytes, _rowBytes);
            Span<byte> dstBot = hasBottom ? _batch.AsSpan((_count + 1) * _rowBytes, _rowBytes) : default;
            Pair(yp, (top - lumaBase) * ys, hasBottom ? (top + 1 - lumaBase) * ys : -1, up, vp, topUv, curUv, dstTop, dstBot, width, _bpp, _chroma);
            FillAlpha(dstTop, alpha, top, width, _bpp);
            if (hasBottom)
            {
                FillAlpha(dstBot, alpha, top + 1, width, _bpp);
            }

            _count += hasBottom ? 2 : 1;
            _next = r + 1;
            if (_count >= Batch)
            {
                Flush();
            }
        }

        if (last)
        {
            Flush();
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
    /// <summary>Decodes the alpha plane into a pooled array (width * height bytes at least) that the caller returns. A lossless-compressed plane grows with the rows decoded, so a short stream cannot force a header-sized allocation.</summary>
    public static byte[] Decode(byte[] data, int length, int width, int height, ImageLimits limits, CancellationToken token)
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

            byte[] raw = ArrayPool<byte>.Shared.Rent((int)size);
            for (int y = 0; y < height; y++)
            {
                Unfilter(filter, data.AsSpan(1 + (y * width), width), raw, y, width);
            }

            return raw;
        }

        using var dec = new VP8LDecoder(data, 1, length - 1, limits, token);
        var sink = new GreenSink(width, height, filter);
        try
        {
            dec.DecodeHeaderless(width, height, sink);
        }
        catch
        {
            sink.Release();
            throw;
        }

        return sink.Plane;
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
        private readonly long _total;
        private readonly int _width;
        private readonly int _filter;
        private readonly byte[] _row;

        public GreenSink(int width, int height, int filter)
        {
            _width = width;
            _filter = filter;
            _total = (long)width * height;
            _row = new byte[width];
            Plane = ArrayPool<byte>.Shared.Rent((int)Math.Min(_total, 1 << 16));
        }

        public byte[] Plane { get; private set; }

        public void Release() => ArrayPool<byte>.Shared.Return(Plane);

        public void WriteRow(int y, ReadOnlySpan<uint> argb)
        {
            long need = (y + 1L) * _width;
            if (need > Plane.Length)
            {
                byte[] bigger = ArrayPool<byte>.Shared.Rent((int)Math.Min(Math.Max(need, Plane.Length * 2L), _total));
                Plane.AsSpan(0, y * _width).CopyTo(bigger);
                ArrayPool<byte>.Shared.Return(Plane);
                Plane = bigger;
            }

            for (int x = 0; x < _width; x++)
            {
                _row[x] = (byte)(argb[x] >> 8);
            }

            Unfilter(_filter, _row, Plane, y, _width);
        }
    }
}
