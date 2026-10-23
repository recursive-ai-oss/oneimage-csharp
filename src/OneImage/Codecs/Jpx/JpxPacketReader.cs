using System.Numerics;

namespace OneImage.Codecs.Jpx;

/// <summary>Reads packet header bits with the bit-stuffing rule of T.800 B.10.1.</summary>
internal ref struct JpxBitReader
{
    private readonly ReadOnlySpan<byte> _data;
    private int _pos;
    private int _cur;
    private int _left;
    private bool _lastWasFF;

    public JpxBitReader(ReadOnlySpan<byte> data, int pos)
    {
        _data = data;
        _pos = pos;
        _cur = 0;
        _left = 0;
        _lastWasFF = false;
        Overrun = false;
    }

    /// <summary>True once a read ran past the end of the data; later reads return zeros.</summary>
    public bool Overrun { get; private set; }

    public readonly int Position => _pos;

    public int Bit()
    {
        if (_left == 0)
        {
            if (_pos >= _data.Length)
            {
                Overrun = true;
                return 0;
            }

            _cur = _data[_pos++];
            _left = _lastWasFF ? 7 : 8;
            _lastWasFF = _cur == 0xFF;
        }

        _left--;
        return (_cur >> _left) & 1;
    }

    public int Bits(int count)
    {
        int v = 0;
        for (int i = 0; i < count; i++)
        {
            v = (v << 1) | Bit();
        }

        return v;
    }

    /// <summary>Ends the header on a byte boundary; a final 0xFF byte is followed by a stuffed byte that is consumed too.</summary>
    public void Align()
    {
        _left = 0;
        if (_lastWasFF)
        {
            if (_pos < _data.Length)
            {
                _pos++;
            }
            else
            {
                Overrun = true;
            }

            _lastWasFF = false;
        }
    }
}

/// <summary>
/// Tier-2 decoding of a tile: walks the packets in progression order (T.800 B.12), parses each packet header (B.10) and
/// records which bytes belong to which code-block segment. Nothing here decodes entropy-coded data.
/// </summary>
internal sealed class JpxPacketReader
{
    private const long MaxPositionSteps = 1L << 27;

    private readonly JpxTileComp[] _comps;
    private readonly JpxSiz _siz;
    private readonly ReadOnlyMemory<byte> _data;
    private readonly ReadOnlyMemory<byte> _headers;
    private readonly bool _packed;
    private readonly int _layers;
    private readonly int _tx0;
    private readonly int _ty0;
    private readonly int _tx1;
    private readonly int _ty1;
    private readonly int[] _tagValue;
    private readonly int[] _tagLow;
    private readonly List<Pending> _pending = [];
    private readonly CancellationToken _cancel;
    private int _dataPos;
    private int _headerPos;
    private long _steps;

    public JpxPacketReader(JpxTileComp[] comps, JpxSiz siz, ReadOnlyMemory<byte> data, ReadOnlyMemory<byte> packedHeaders, bool packed, int layers, int tx0, int ty0, int tx1, int ty1, long tagNodes, CancellationToken cancel)
    {
        _comps = comps;
        _siz = siz;
        _data = data;
        _headers = packedHeaders;
        _packed = packed;
        _layers = layers;
        _tx0 = tx0;
        _ty0 = ty0;
        _tx1 = tx1;
        _ty1 = ty1;
        _tagValue = new int[tagNodes];
        _tagLow = new int[tagNodes];
        Array.Fill(_tagValue, int.MaxValue);
        _cancel = cancel;
    }

    /// <summary>The chunks recorded so far; blocks chain through <see cref="JpxCodeBlock.FirstChunk"/>.</summary>
    public JpxChunk[] Chunks = new JpxChunk[256];

    /// <summary>Number of chunks in <see cref="Chunks"/>.</summary>
    public int ChunkCount { get; private set; }

    /// <summary>True when the data ended or a header was invalid before all packets were read.</summary>
    public bool Damaged { get; private set; }

    private struct Pending
    {
        public JpxBand Band;
        public int Block;
        public int Segment;
        public int Passes;
        public int Length;
    }

    /// <summary>Reads every packet the progression volumes describe, stopping quietly at the end of the data.</summary>
    /// <param name="order">The progression order of the COD marker.</param>
    /// <param name="poc">Progression order changes, or empty.</param>
    public void ReadAll(int order, List<JpxPoc> poc)
    {
        int maxRes = 0;
        foreach (JpxTileComp tc in _comps)
        {
            maxRes = Math.Max(maxRes, tc.Cod.Levels + 1);
        }

        if (poc.Count == 0)
        {
            RunVolume(new JpxPoc(0, 0, _layers, maxRes, _comps.Length, order));
            return;
        }

        foreach (JpxPoc v in poc)
        {
            if (!RunVolume(v))
            {
                return;
            }
        }
    }

    private bool RunVolume(JpxPoc v)
    {
        int le = Math.Min(v.LayerEnd, _layers);
        int maxRes = 0;
        foreach (JpxTileComp tc in _comps)
        {
            maxRes = Math.Max(maxRes, tc.Cod.Levels + 1);
        }

        int rs = v.ResStart;
        int re = Math.Min(v.ResEnd, maxRes);
        int cs = v.CompStart;
        int ce = Math.Min(v.CompEnd, _comps.Length);
        switch (v.Order)
        {
            case 0: // LRCP
                for (int l = 0; l < le; l++)
                {
                    for (int r = rs; r < re; r++)
                    {
                        for (int c = cs; c < ce; c++)
                        {
                            if (!AllPrecincts(c, r, l))
                            {
                                return false;
                            }
                        }
                    }
                }

                return true;
            case 1: // RLCP
                for (int r = rs; r < re; r++)
                {
                    for (int l = 0; l < le; l++)
                    {
                        for (int c = cs; c < ce; c++)
                        {
                            if (!AllPrecincts(c, r, l))
                            {
                                return false;
                            }
                        }
                    }
                }

                return true;
            case 2: // RPCL
                for (int r = rs; r < re; r++)
                {
                    GetSteps(r, r + 1, cs, ce, out long dx, out long dy);
                    for (long y = _ty0; y < _ty1; y = Next(y, dy))
                    {
                        for (long x = _tx0; x < _tx1; x = Next(x, dx))
                        {
                            if (!CountStep())
                            {
                                return false;
                            }

                            for (int c = cs; c < ce; c++)
                            {
                                if (!PositionPackets(c, r, x, y, le))
                                {
                                    return false;
                                }
                            }
                        }
                    }
                }

                return true;
            case 3: // PCRL
            {
                GetSteps(rs, re, cs, ce, out long dx, out long dy);
                for (long y = _ty0; y < _ty1; y = Next(y, dy))
                {
                    for (long x = _tx0; x < _tx1; x = Next(x, dx))
                    {
                        if (!CountStep())
                        {
                            return false;
                        }

                        for (int c = cs; c < ce; c++)
                        {
                            for (int r = rs; r < re; r++)
                            {
                                if (!PositionPackets(c, r, x, y, le))
                                {
                                    return false;
                                }
                            }
                        }
                    }
                }

                return true;
            }

            default: // CPRL
                for (int c = cs; c < ce; c++)
                {
                    GetSteps(rs, re, c, c + 1, out long dx, out long dy);
                    for (long y = _ty0; y < _ty1; y = Next(y, dy))
                    {
                        for (long x = _tx0; x < _tx1; x = Next(x, dx))
                        {
                            if (!CountStep())
                            {
                                return false;
                            }

                            for (int r = rs; r < re; r++)
                            {
                                if (!PositionPackets(c, r, x, y, le))
                                {
                                    return false;
                                }
                            }
                        }
                    }
                }

                return true;
        }
    }

    private bool CountStep()
    {
        if (++_steps > MaxPositionSteps)
        {
            throw new ImageLimitException("JPEG 2000 progression order visits too many positions.");
        }

        _cancel.ThrowIfCancellationRequested();
        return true;
    }

    private static long Next(long v, long d) => ((v / d) + 1) * d;

    private bool AllPrecincts(int c, int r, int layer)
    {
        JpxTileComp tc = _comps[c];
        if (r > tc.Cod.Levels)
        {
            return true;
        }

        int n = tc.Res[r].PrecinctCount;
        for (int k = 0; k < n; k++)
        {
            if (!ReadPacket(c, r, k, layer))
            {
                return false;
            }
        }

        return true;
    }

    // The step between positions that can start a precinct, as a gcd over the components and resolutions in play.
    private void GetSteps(int rs, int re, int cs, int ce, out long dx, out long dy)
    {
        dx = 0;
        dy = 0;
        for (int c = cs; c < ce; c++)
        {
            JpxTileComp tc = _comps[c];
            for (int r = rs; r < re && r <= tc.Cod.Levels; r++)
            {
                JpxResolution res = tc.Res[r];
                int shift = tc.Cod.Levels - r;
                dx = Gcd(dx, (long)_siz.XR[c] << (res.PpX + shift));
                dy = Gcd(dy, (long)_siz.YR[c] << (res.PpY + shift));
            }
        }

        if (dx == 0)
        {
            dx = 1L << 40;
        }

        if (dy == 0)
        {
            dy = 1L << 40;
        }
    }

    private static long Gcd(long a, long b)
    {
        while (b != 0)
        {
            (a, b) = (b, a % b);
        }

        return a;
    }

    // B.12.1.3: a precinct starts at (x, y) when both are on its grid, or at the tile's first position when the
    // precinct began before the tile.
    private bool PositionPackets(int c, int r, long x, long y, int layerEnd)
    {
        JpxTileComp tc = _comps[c];
        if (r > tc.Cod.Levels)
        {
            return true;
        }

        JpxResolution res = tc.Res[r];
        if (res.PrecinctCount == 0)
        {
            return true;
        }

        int shift = tc.Cod.Levels - r;
        long xr = _siz.XR[c];
        long yr = _siz.YR[c];
        long px = res.PpX + shift;
        long py = res.PpY + shift;
        bool yOk = y % (yr << (int)py) == 0 || (y == _ty0 && (((long)res.Y0 << shift) % (1L << (int)py)) != 0);
        bool xOk = x % (xr << (int)px) == 0 || (x == _tx0 && (((long)res.X0 << shift) % (1L << (int)px)) != 0);
        if (!xOk || !yOk)
        {
            return true;
        }

        long kx = CeilDivL(x, xr << shift) >> res.PpX;
        kx -= res.X0 >> res.PpX;
        long ky = CeilDivL(y, yr << shift) >> res.PpY;
        ky -= res.Y0 >> res.PpY;
        if (kx < 0 || ky < 0 || kx >= res.PrecinctsWide || ky >= res.PrecinctsHigh)
        {
            return true;
        }

        int k = (int)(kx + (res.PrecinctsWide * ky));
        for (int l = 0; l < layerEnd; l++)
        {
            if (!ReadPacket(c, r, k, l))
            {
                return false;
            }
        }

        return true;
    }

    private static long CeilDivL(long a, long d) => (a + d - 1) / d;

    // ---- one packet ----

    private static int SegmentCapacity(int startPass, int style)
    {
        if ((style & 0x04) != 0)
        {
            return 1; // termination on every pass
        }

        if ((style & 0x01) != 0)
        {
            // Selective bypass (D.6): the first ten passes form one arithmetic segment, then a raw segment of the
            // significance and refinement passes alternates with a one-pass arithmetic segment of the cleanup pass.
            if (startPass < 10)
            {
                return 10 - startPass;
            }

            return (startPass - 10) % 3 == 0 ? 2 : 1;
        }

        return int.MaxValue;
    }

    private bool Fail()
    {
        Damaged = true;
        return false;
    }

    private bool ReadPacket(int c, int r, int k, int layer)
    {
        JpxTileComp tc = _comps[c];
        JpxResolution res = tc.Res[r];
        if (layer < res.LayersDone[k])
        {
            return true; // an earlier progression volume already delivered it
        }

        _cancel.ThrowIfCancellationRequested();
        ReadOnlySpan<byte> data = _data.Span;
        if (!_packed && _dataPos >= data.Length)
        {
            return Fail();
        }

        // Start-of-packet marker segment (A.8.1): six bytes, ignored.
        if (data.Length - _dataPos >= 6 && data[_dataPos] == 0xFF && data[_dataPos + 1] == 0x91)
        {
            _dataPos += 6;
        }

        ReadOnlySpan<byte> src = _packed ? _headers.Span : data;
        int srcPos = _packed ? _headerPos : _dataPos;
        if (_packed && srcPos >= src.Length)
        {
            return Fail();
        }

        var br = new JpxBitReader(src, srcPos);
        _pending.Clear();
        int style = tc.Cod.Style;
        if (br.Bit() != 0)
        {
            foreach (JpxBand band in res.Bands)
            {
                JpxPrecinctBand pb = band.Precincts[k];
                int gw = pb.GridWidth;
                int n = gw * pb.GridHeight;
                int treeSize = (int)JpxTileBuilder.TagNodeCount(gw, pb.GridHeight);
                for (int i = 0; i < n; i++)
                {
                    ref JpxCodeBlock cb = ref band.Blocks[pb.First + i];
                    bool included;
                    if (cb.Included)
                    {
                        included = br.Bit() != 0;
                    }
                    else
                    {
                        included = TagDecode(pb.TagOffset, gw, pb.GridHeight, i % gw, i / gw, layer + 1, ref br);
                    }

                    if (br.Overrun)
                    {
                        return Fail();
                    }

                    if (!included)
                    {
                        continue;
                    }

                    if (!cb.Included)
                    {
                        int zbp = -1;
                        for (int t = 1; t <= 96; t++)
                        {
                            if (TagDecode(pb.TagOffset + treeSize, gw, pb.GridHeight, i % gw, i / gw, t, ref br))
                            {
                                zbp = t - 1;
                                break;
                            }

                            if (br.Overrun)
                            {
                                return Fail();
                            }
                        }

                        if (zbp < 0)
                        {
                            return Fail();
                        }

                        if (band.Mb + tc.RoiShift - zbp > 30)
                        {
                            throw new ImageFormatException("JPEG 2000 dynamic range above 30 bit-planes is not supported.");
                        }

                        cb.ZeroBitPlanes = zbp;
                        cb.Included = true;
                    }

                    int passes = ReadPassCount(ref br);
                    while (br.Bit() != 0)
                    {
                        if (++cb.Lblock > 28 || br.Overrun)
                        {
                            return Fail();
                        }
                    }

                    int bitPlanes = band.Mb + tc.RoiShift - cb.ZeroBitPlanes;
                    if (bitPlanes <= 0 || cb.Passes + passes > (3 * bitPlanes) - 2)
                    {
                        // More passes than the block has bit-planes for (or no bit-planes at all).
                        return Fail();
                    }

                    int remaining = passes;
                    while (remaining > 0)
                    {
                        int cap = SegmentCapacity(cb.Passes - cb.SegPasses, style);
                        int take = Math.Min(remaining, cap - cb.SegPasses);
                        int bits = cb.Lblock + (31 - BitOperations.LeadingZeroCount((uint)take));
                        if (bits > 31)
                        {
                            return Fail();
                        }

                        int len = br.Bits(bits);
                        if (br.Overrun)
                        {
                            return Fail();
                        }

                        _pending.Add(new Pending { Band = band, Block = pb.First + i, Segment = cb.Segment, Passes = take, Length = len });
                        cb.SegPasses += take;
                        cb.Passes += take;
                        remaining -= take;
                        if (cb.SegPasses >= cap)
                        {
                            cb.Segment++;
                            cb.SegPasses = 0;
                        }
                    }
                }
            }
        }

        br.Align();
        if (br.Overrun)
        {
            return Fail();
        }

        int pos = br.Position;

        // End-of-packet-header marker (A.8.2).
        if (src.Length - pos >= 2 && src[pos] == 0xFF && src[pos + 1] == 0x92)
        {
            pos += 2;
        }

        if (_packed)
        {
            _headerPos = pos;
        }
        else
        {
            _dataPos = pos;
        }

        bool keep = r < tc.KeptResolutions;
        bool complete = true;
        foreach (Pending p in _pending)
        {
            int avail = data.Length - _dataPos;
            int len = p.Length;
            if (len > avail)
            {
                len = Math.Max(avail, 0);
                complete = false;
            }

            if (keep)
            {
                AddChunk(p.Band, p.Block, _dataPos, len, p.Segment, p.Passes);
            }

            _dataPos += len;
        }

        res.LayersDone[k] = layer + 1;
        return complete || Fail();
    }

    private void AddChunk(JpxBand band, int block, int offset, int length, int segment, int passes)
    {
        if (ChunkCount == Chunks.Length)
        {
            Array.Resize(ref Chunks, Chunks.Length * 2);
        }

        int idx = ChunkCount++;
        Chunks[idx] = new JpxChunk { Next = -1, Offset = offset, Length = length, Segment = segment, Passes = passes };
        ref JpxCodeBlock cb = ref band.Blocks[block];
        if (cb.LastChunk >= 0)
        {
            Chunks[cb.LastChunk].Next = idx;
        }
        else
        {
            cb.FirstChunk = idx;
        }

        cb.LastChunk = idx;
    }

    // B.10.6, Table B.4.
    private static int ReadPassCount(ref JpxBitReader br)
    {
        if (br.Bit() == 0)
        {
            return 1;
        }

        if (br.Bit() == 0)
        {
            return 2;
        }

        int v = br.Bits(2);
        if (v != 3)
        {
            return 3 + v;
        }

        v = br.Bits(5);
        if (v != 31)
        {
            return 6 + v;
        }

        return 37 + br.Bits(7);
    }

    // Tag tree decoding (B.10.2): returns whether the leaf's value is below the threshold, reading only the bits still needed.
    private bool TagDecode(int offset, int w, int h, int x, int y, int threshold, ref JpxBitReader br)
    {
        Span<int> levelOffset = stackalloc int[34];
        Span<int> levelWidth = stackalloc int[34];
        int levels = 0;
        int lw = w;
        int lh = h;
        int acc = 0;
        while (true)
        {
            levelOffset[levels] = acc;
            levelWidth[levels] = lw;
            acc += lw * lh;
            levels++;
            if (lw == 1 && lh == 1)
            {
                break;
            }

            lw = (lw + 1) >> 1;
            lh = (lh + 1) >> 1;
        }

        int low = 0;
        int node = 0;
        for (int k = levels - 1; k >= 0; k--)
        {
            node = offset + levelOffset[k] + ((y >> k) * levelWidth[k]) + (x >> k);
            if (low > _tagLow[node])
            {
                _tagLow[node] = low;
            }
            else
            {
                low = _tagLow[node];
            }

            while (low < threshold && low < _tagValue[node])
            {
                if (br.Bit() != 0)
                {
                    _tagValue[node] = low;
                }
                else
                {
                    low++;
                }

                if (br.Overrun)
                {
                    return false;
                }
            }

            _tagLow[node] = low;
        }

        return _tagValue[node] < threshold;
    }

    /// <summary>The tag tree value of a leaf whose value has been decoded.</summary>
    public int Value(int node) => _tagValue[node];
}
