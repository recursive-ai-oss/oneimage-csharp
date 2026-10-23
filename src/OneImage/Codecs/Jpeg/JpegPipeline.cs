using System.Buffers;
using System.Numerics;
using OneImage.Kernels;

namespace OneImage.Codecs.Jpeg;

/// <summary>How the decoded components map to output channels.</summary>
internal enum JpegColorKind
{
    Gray,
    YCbCr,
    Rgb,
    Cmyk,
    Ycck,
}

/// <summary>The color interpretation chosen from the markers (the same rules the reference decoder applies).</summary>
internal readonly record struct JpegColorPlan(JpegColorKind Kind, bool Invert)
{
    public static JpegColorPlan Decide(JpegFrame f, JpegHeaders h)
    {
        switch (f.Components.Length)
        {
            case 1:
                return new JpegColorPlan(JpegColorKind.Gray, false);
            case 3:
                if (h.Jfif)
                {
                    return new JpegColorPlan(JpegColorKind.YCbCr, false);
                }

                if (h.AdobeTransform >= 0)
                {
                    return new JpegColorPlan(h.AdobeTransform == 0 ? JpegColorKind.Rgb : JpegColorKind.YCbCr, false);
                }

                bool rgbIds = f.Components[0].Id == 'R' && f.Components[1].Id == 'G' && f.Components[2].Id == 'B';
                return new JpegColorPlan(rgbIds ? JpegColorKind.Rgb : JpegColorKind.YCbCr, false);
            default:
                // Adobe writers store CMYK inverted (255 = no ink); other 4-component files are taken as plain CMYK.
                if (h.AdobeTransform >= 0)
                {
                    return new JpegColorPlan(h.AdobeTransform == 0 ? JpegColorKind.Cmyk : JpegColorKind.Ycck, true);
                }

                return new JpegColorPlan(JpegColorKind.Cmyk, false);
        }
    }

    public PixelFormat Format(int precision)
    {
        SampleType st = precision == 8 ? SampleType.U8 : SampleType.U16;
        return Kind switch
        {
            JpegColorKind.Gray => new PixelFormat(ColorModel.Gray, st),
            JpegColorKind.YCbCr or JpegColorKind.Rgb => new PixelFormat(ColorModel.Rgb, st),
            _ => new PixelFormat(ColorModel.Cmyk, st),
        };
    }
}

/// <summary>Per-thread counters the tests read to prove which transform sizes a decode used.</summary>
internal static class JpegDiagnostics
{
    /// <summary>Gets or sets the number of full 8x8 inverse transforms run on this thread.</summary>
    [ThreadStatic]
    public static long FullBlocks;

    /// <summary>Gets or sets the number of reduced (4x4, 2x2, 1x1) inverse transforms run on this thread.</summary>
    [ThreadStatic]
    public static long ReducedBlocks;
}

/// <summary>
/// Turns decoded coefficient rows into output rows: inverse DCT (possibly reduced for shrink-on-load) into
/// per-component sample planes, chroma upsampling, color conversion, and delivery to the row sink. Only the rows
/// and columns the requested region needs are transformed.
/// </summary>
internal sealed class JpegPipeline<T> : IDisposable
    where T : unmanaged, IBinaryInteger<T>
{
    private sealed class Plane
    {
        public JpegComponent Src = null!;
        public int Ss;
        public int HExp;
        public int VExp;
        public int PlaneW;
        public int RowsPerMcu;
        public int Dw;
        public int Dh;
        public int BxLo;
        public int BxHi;
        public T[] Ring = [];
        public T[] Tmp = [];
    }

    private readonly JpegFrame _f;
    private readonly JpegColorPlan _plan;
    private readonly IRowSink _sink;
    private readonly Plane[] _planes;
    private readonly int _precision;
    private readonly int _maxValue;
    private readonly bool _fancy;
    private readonly int _outW;
    private readonly int _outH;
    private readonly int _mr;
    private readonly int _x0;
    private readonly int _y0;
    private readonly int _w;
    private readonly int _h;
    private readonly int _channels;
    private readonly int _firstEmit;
    private readonly int _lastEmit;
    private readonly T[] _outBuf;
    private int _written;

    public JpegPipeline(JpegFrame frame, JpegColorPlan plan, int scale, ImageRect region, IRowSink sink)
    {
        _f = frame;
        _plan = plan;
        _sink = sink;
        _precision = frame.Precision;
        _maxValue = (1 << _precision) - 1;
        int n = 8 / scale;
        _fancy = n > 1;
        _outW = (frame.Width + scale - 1) / scale;
        _outH = (frame.Height + scale - 1) / scale;
        _x0 = region.X;
        _y0 = region.Y;
        _w = region.Width;
        _h = region.Height;
        if (_x0 < 0 || _y0 < 0 || _w <= 0 || _h <= 0 || (long)_x0 + _w > _outW || (long)_y0 + _h > _outH)
        {
            throw new ArgumentException("Requested region lies outside the scaled image.");
        }

        _mr = frame.Vmax * n;
        _channels = plan.Kind switch
        {
            JpegColorKind.Gray => 1,
            JpegColorKind.YCbCr or JpegColorKind.Rgb => 3,
            _ => 4,
        };
        _firstEmit = _y0 / _mr;
        _lastEmit = (_y0 + _h - 1) / _mr;
        _planes = new Plane[frame.Components.Length];
        for (int i = 0; i < _planes.Length; i++)
        {
            JpegComponent c = frame.Components[i];
            int ss = n;
            while (ss < 8 && (frame.Hmax * n) % (c.H * ss * 2) == 0 && (frame.Vmax * n) % (c.V * ss * 2) == 0)
            {
                ss *= 2;
            }

            var p = new Plane
            {
                Src = c,
                Ss = ss,
                HExp = frame.Hmax * n / (c.H * ss),
                VExp = frame.Vmax * n / (c.V * ss),
                PlaneW = c.BlocksPerLine * ss,
                RowsPerMcu = c.V * ss,
            };
            p.Dw = (int)((((long)frame.Width * c.H * ss) + (frame.Hmax * 8) - 1) / (frame.Hmax * 8));
            p.Dh = (int)((((long)frame.Height * c.V * ss) + (frame.Vmax * 8) - 1) / (frame.Vmax * 8));
            int sLo = Math.Max(0, (_x0 / p.HExp) - 1);
            int sHi = Math.Min(p.Dw - 1, ((_x0 + _w - 1) / p.HExp) + 1);
            p.BxLo = sLo / ss;
            p.BxHi = Math.Min(c.BlocksPerLine, (sHi / ss) + 1);
            p.Ring = Rent(3 * p.RowsPerMcu * p.PlaneW);
            p.Tmp = Rent((p.PlaneW * p.HExp) + 16);
            _planes[i] = p;
        }

        _outBuf = Rent(_mr * _w * _channels);
    }

    /// <summary>Gets whether only DC coefficients influence the output (every component is reduced to one sample per block).</summary>
    public bool DcOnly
    {
        get
        {
            foreach (Plane p in _planes)
            {
                if (p.Ss != 1)
                {
                    return false;
                }
            }

            return true;
        }
    }

    /// <summary>Gets, per component index, whether only that component's DC coefficient influences the output.</summary>
    public bool[] DcOnlyComponents()
    {
        var r = new bool[_planes.Length];
        foreach (Plane p in _planes)
        {
            r[p.Src.Index] = p.Ss == 1;
        }

        return r;
    }

    public int RowsWritten => _written;

    private static T[] Rent(int length)
    {
        T[] a = ArrayPool<T>.Shared.Rent(length);
        a.AsSpan(0, length).Clear();
        return a;
    }

    public void Dispose()
    {
        foreach (Plane p in _planes)
        {
            ArrayPool<T>.Shared.Return(p.Ring);
            ArrayPool<T>.Shared.Return(p.Tmp);
        }

        ArrayPool<T>.Shared.Return(_outBuf);
    }

    /// <summary>
    /// Takes MCU row <paramref name="r"/> of coefficients (rows arrive in order). Transforms what the region needs and
    /// delivers any output rows that became complete.
    /// </summary>
    /// <returns><see langword="false"/> when everything requested has been delivered and decoding can stop.</returns>
    public bool AddMcuRow(int r, JpegCoefficientStore store)
    {
        if (r >= _firstEmit - 1 && r <= _lastEmit + 1)
        {
            IdctRow(r, store);
        }
        else
        {
            foreach (Plane p in _planes)
            {
                for (int br = 0; br < p.Src.V; br++)
                {
                    store.Release(p.Src, (r * p.Src.V) + br);
                }
            }
        }

        // Row r-1 can be emitted once r supplied its lower context; the last row has no lower neighbour.
        if (r >= 1 && r - 1 >= _firstEmit && r - 1 <= _lastEmit)
        {
            Emit(r - 1);
        }

        if (r == _f.McusY - 1 && r >= _firstEmit && r <= _lastEmit)
        {
            Emit(r);
        }

        return _written < _h;
    }

    private void IdctRow(int r, JpegCoefficientStore store)
    {
        int slot = r % 3;
        foreach (Plane p in _planes)
        {
            int[] quant = p.Src.Quant ?? throw new ImageFormatException("JPEG component has no quantization table.");
            for (int br = 0; br < p.Src.V; br++)
            {
                Span<short> row = store.Row(p.Src, (r * p.Src.V) + br);
                int rowBase = ((slot * p.RowsPerMcu) + (br * p.Ss)) * p.PlaneW;
                if (p.Ss == 8)
                {
                    JpegDiagnostics.FullBlocks += p.BxHi - p.BxLo;
                }
                else
                {
                    JpegDiagnostics.ReducedBlocks += p.BxHi - p.BxLo;
                }

                if (p.Ss == 1)
                {
                    JpegIdct.DcRow(row.Slice(p.BxLo * 64), p.BxHi - p.BxLo, quant[0], p.Ring.AsSpan(rowBase + p.BxLo), _precision);
                    store.Release(p.Src, (r * p.Src.V) + br);
                    continue;
                }

                for (int bx = p.BxLo; bx < p.BxHi; bx++)
                {
                    JpegIdct.Block(row.Slice(bx * 64, 64), quant, p.Ring.AsSpan(rowBase + (bx * p.Ss)), p.PlaneW, p.Ss, _precision);
                }

                store.Release(p.Src, (r * p.Src.V) + br);
            }
        }
    }

    private static ReadOnlySpan<T> PlaneRow(Plane p, int g)
    {
        g = Math.Clamp(g, 0, p.Dh - 1);
        int mcu = g / p.RowsPerMcu;
        int within = g - (mcu * p.RowsPerMcu);
        int off = (((mcu % 3) * p.RowsPerMcu) + within) * p.PlaneW;
        return p.Ring.AsSpan(off, p.PlaneW);
    }

    /// <summary>Returns output row <paramref name="y"/> of a component at output resolution.</summary>
    private ReadOnlySpan<T> RowFor(Plane p, int y)
    {
        if (p.HExp == 1 && p.VExp == 1)
        {
            return PlaneRow(p, y);
        }

        if (p.VExp == 2 && _fancy && (p.HExp == 1 || (p.HExp == 2 && p.Dw > 2)))
        {
            int near = y >> 1;
            int far = (y & 1) == 0 ? near - 1 : near + 1;
            ReadOnlySpan<T> nr = PlaneRow(p, near);
            ReadOnlySpan<T> fr = PlaneRow(p, far);
            if (p.HExp == 2)
            {
                JpegSampling.UpsampleH2V2(nr, fr, p.Dw, p.Tmp);
            }
            else
            {
                JpegSampling.UpsampleH1V2(nr, fr, p.Dw, (y & 1) == 0 ? 1 : 2, p.Tmp);
            }

            return p.Tmp;
        }

        if (p.VExp == 1 && p.HExp == 2 && _fancy && p.Dw > 2)
        {
            JpegSampling.UpsampleH2V1(PlaneRow(p, y), p.Dw, p.Tmp);
            return p.Tmp;
        }

        JpegSampling.Replicate(PlaneRow(p, y / p.VExp), p.Dw, p.HExp, p.Tmp);
        return p.Tmp;
    }

    private void Emit(int q)
    {
        int yStart = Math.Max(q * _mr, _y0);
        int yEnd = Math.Min(Math.Min((q + 1) * _mr, _y0 + _h), _outH);
        int count = yEnd - yStart;
        if (count <= 0)
        {
            return;
        }

        int rowLen = _w * _channels;
        for (int y = yStart; y < yEnd; y++)
        {
            Span<T> dst = _outBuf.AsSpan((y - yStart) * rowLen, rowLen);
            ReadOnlySpan<T> a = RowFor(_planes[0], y).Slice(_x0, _w);
            switch (_plan.Kind)
            {
                case JpegColorKind.Gray:
                    a.CopyTo(dst);
                    break;
                case JpegColorKind.YCbCr:
                    JpegSampling.YccToRgb(a, RowFor(_planes[1], y).Slice(_x0, _w), RowFor(_planes[2], y).Slice(_x0, _w), dst, _w, _maxValue);
                    break;
                case JpegColorKind.Rgb:
                    JpegSampling.InterleaveRgb(a, RowFor(_planes[1], y).Slice(_x0, _w), RowFor(_planes[2], y).Slice(_x0, _w), dst, _w);
                    break;
                case JpegColorKind.Cmyk:
                    JpegSampling.InterleaveCmyk(
                        a, RowFor(_planes[1], y).Slice(_x0, _w), RowFor(_planes[2], y).Slice(_x0, _w), RowFor(_planes[3], y).Slice(_x0, _w), dst, _w, _maxValue, _plan.Invert);
                    break;
                default:
                    JpegSampling.YcckToCmyk(
                        a, RowFor(_planes[1], y).Slice(_x0, _w), RowFor(_planes[2], y).Slice(_x0, _w), RowFor(_planes[3], y).Slice(_x0, _w), dst, _w, _maxValue, _plan.Invert);
                    break;
            }
        }

        Span<T> all = _outBuf.AsSpan(0, count * rowLen);
        JpegSampling.ScaleTo16(all, _precision);
        _sink.WriteRows(_written, SpanCast.AsBytes<T>(all), count);
        _written += count;
    }
}
