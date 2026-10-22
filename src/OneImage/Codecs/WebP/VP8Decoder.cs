using System.Buffers;

namespace OneImage.Codecs.WebP;

/// <summary>
/// Decoder for the VP8 key frames of lossy WebP files (RFC 6386). Reconstruction (prediction, inverse transforms) is
/// bit-exact to the specification; the loop filter runs once over the finished frame, then rows are converted to RGB with
/// the same fancy chroma upsampling and integer YUV conversion libwebp uses by default.
/// </summary>
internal sealed class VP8Decoder
{
    internal const int Bps = 32;
    internal const int YOff = Bps + 8;
    internal const int UOff = YOff + (Bps * 16) + Bps;
    internal const int VOff = UOff + 16;

    // Sub-block mode ids follow the RFC enumeration.
    internal const int BDc = 0;
    internal const int BTm = 1;
    internal const int BVe = 2;
    internal const int BHe = 3;
    internal const int BLd = 4;
    internal const int BRd = 5;
    internal const int BVr = 6;
    internal const int BVl = 7;
    internal const int BHd = 8;
    internal const int BHu = 9;

    // 16x16 and chroma modes.
    internal const int DcPred = 0;
    internal const int TmPred = 1;
    internal const int VPred = 2;
    internal const int HPred = 3;

    private static readonly sbyte[] BModeTree = [-BDc, 1, -BTm, 2, -BVe, 3, 4, 6, -BHe, 5, -BRd, -BVr, -BLd, 7, -BVl, 8, -BHd, -BHu];
    private static readonly byte[] Zigzag = [0, 1, 4, 8, 5, 2, 3, 6, 9, 12, 13, 10, 7, 11, 14, 15];
    private static readonly byte[] Bands = [0, 1, 2, 3, 6, 4, 5, 6, 6, 6, 6, 6, 6, 6, 6, 7, 0];
    internal static readonly sbyte[] BModeTreeShared = BModeTree;

    private static readonly byte[] Cat3 = [173, 148, 140];
    private static readonly byte[] Cat4 = [176, 155, 140, 135];
    private static readonly byte[] Cat5 = [180, 157, 141, 134, 130];
    private static readonly byte[] Cat6 = [254, 254, 243, 230, 196, 177, 153, 140, 133, 130, 129];

    private readonly byte[] _data;
    private readonly int _dataLen;
    private readonly ImageLimits _limits;
    private readonly CancellationToken _token;

    private int _width;
    private int _height;
    private int _mbw;
    private int _mbh;

    // Frame header.
    private bool _useSegment;
    private bool _updateMap;
    private bool _absoluteDelta = true;
    private readonly int[] _segQuant = new int[4];
    private readonly int[] _segFilter = new int[4];
    private readonly byte[] _segProba = [255, 255, 255];
    private bool _simpleFilter;
    private int _filterLevel;
    private int _sharpness;
    private bool _useLfDelta;
    private readonly int[] _refLfDelta = new int[4];
    private readonly int[] _modeLfDelta = new int[4];
    private int _filterType;
    private int _numParts;
    private bool _useSkipProba;
    private int _skipProba;
    private readonly byte[] _coeffProbs = new byte[4 * 8 * 3 * 11];
    private readonly Quant[] _quant = new Quant[4];

    private VP8BoolReader _br;
    private readonly VP8BoolReader[] _parts = new VP8BoolReader[8];

    // Planes (macroblock-aligned).
    private byte[] _y = [];
    private byte[] _u = [];
    private byte[] _v = [];
    private int _yStride;
    private int _uvStride;

    // Per macroblock-column context.
    private byte[] _intraTop = [];
    private readonly byte[] _intraLeft = new byte[4];
    private byte[] _nzTop = [];
    private readonly byte[] _nzLeft = new byte[8];
    private byte[] _nzDcTop = [];
    private int _nzDcLeft;

    // Per macroblock data of the current row.
    private byte[] _segments = [];
    private byte[] _skips = [];

    // Per macroblock data of the frame, for the loop filter.
    private byte[] _fInner = [];
    private byte[] _fSegment = [];
    private byte[] _fI4 = [];
    private byte[] _topY = [];
    private byte[] _topU = [];
    private byte[] _topV = [];

    private readonly byte[] _work = new byte[Bps * 27];

    private VP8Decoder(byte[] data, int len, ImageLimits limits, CancellationToken token)
    {
        _data = data;
        _dataLen = len;
        _limits = limits;
        _token = token;
    }

    /// <summary>Decodes a VP8 chunk payload and delivers RGB or RGBA rows (by the sink's format).</summary>
    public static void Decode(byte[] data, int length, byte[]? alpha, int alphaLength, IRowSink sink, ImageLimits limits, CancellationToken token)
    {
        var d = new VP8Decoder(data, length, limits, token);
        d.Run(alpha, alphaLength, sink);
    }

    private static int Clip(int v, int max) => v < 0 ? 0 : v > max ? max : v;

    private static byte Clip255(int v) => (byte)(v < 0 ? 0 : v > 255 ? 255 : v);

    private Upsampler? _upsampler;
    private byte[]? _alphaPlane;

    private void Run(byte[]? alpha, int alphaLength, IRowSink sink)
    {
        ParseHeaders();
        if (sink.Width != _width || sink.Height != _height)
        {
            throw new ImageOperationException("The sink size does not match the frame.");
        }

        _limits.CheckFrame(_width, _height);
        bool wantAlpha = sink.Format.HasAlpha;
        byte[]? alphaPlane = null;
        _yStride = _mbw * 16;
        _uvStride = _mbw * 8;
        // Only two macroblock rows of each plane are kept: the one being decoded and the one above it, which the loop filter
        // and the chroma upsampler still read. Memory is independent of the image height.
        byte[] yBuf = ArrayPool<byte>.Shared.Rent(2 * 16 * _yStride);
        byte[] uBuf = ArrayPool<byte>.Shared.Rent(2 * 8 * _uvStride);
        byte[] vBuf = ArrayPool<byte>.Shared.Rent(2 * 8 * _uvStride);
        try
        {
            _y = yBuf;
            _u = uBuf;
            _v = vBuf;
            if (wantAlpha && alpha != null && alphaLength > 0)
            {
                alphaPlane = AlphaDecoder.Decode(alpha, alphaLength, _width, _height, _limits, _token);
            }

            using var upsampler = new Upsampler(sink, _width, _height, _token);
            _upsampler = upsampler;
            _alphaPlane = alphaPlane;
            DecodeFrame();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(yBuf);
            ArrayPool<byte>.Shared.Return(uBuf);
            ArrayPool<byte>.Shared.Return(vBuf);
            if (alphaPlane != null)
            {
                ArrayPool<byte>.Shared.Return(alphaPlane);
            }
        }
    }

    // ---- headers (RFC 6386 sections 9 and 19.2) ----

    private void ParseHeaders()
    {
        if (_dataLen < 10)
        {
            throw new ImageFormatException("The VP8 chunk is too short.");
        }

        uint bits = (uint)(_data[0] | (_data[1] << 8) | (_data[2] << 16));
        bool keyFrame = (bits & 1) == 0;
        int profile = (int)((bits >> 1) & 7);
        bool show = ((bits >> 4) & 1) != 0;
        int partitionLength = (int)(bits >> 5);
        if (profile > 3)
        {
            throw new ImageFormatException("Unknown VP8 profile.");
        }

        if (!show)
        {
            throw new ImageFormatException("The VP8 frame is not displayable.");
        }

        if (!keyFrame)
        {
            throw new ImageFormatException("A VP8 key frame is required.");
        }

        if (_data[3] != 0x9d || _data[4] != 0x01 || _data[5] != 0x2a)
        {
            throw new ImageFormatException("Invalid VP8 start code.");
        }

        _width = (_data[6] | (_data[7] << 8)) & 0x3fff;
        _height = (_data[8] | (_data[9] << 8)) & 0x3fff;
        if (_width == 0 || _height == 0)
        {
            throw new ImageFormatException("Invalid VP8 frame size.");
        }

        _limits.CheckFrame(_width, _height);
        _mbw = (_width + 15) >> 4;
        _mbh = (_height + 15) >> 4;
        int start = 10;
        if (partitionLength > _dataLen - start)
        {
            throw new ImageFormatException("Invalid VP8 partition length.");
        }

        _br.Init(_data, start, start + partitionLength);
        _br.GetBit(0x80); // color space
        _br.GetBit(0x80); // clamping type: the decoder always clamps
        ParseSegmentHeader();
        ParseFilterHeader();
        ParsePartitions(start + partitionLength);
        ParseQuant();
        _br.GetBit(0x80); // refresh entropy probs: irrelevant for a single frame
        ParseProba();
        if (_br.Eof)
        {
            throw new ImageFormatException("The VP8 frame header is truncated.");
        }
    }

    private void ParseSegmentHeader()
    {
        _useSegment = _br.GetBit(0x80) != 0;
        if (!_useSegment)
        {
            _updateMap = false;
            return;
        }

        _updateMap = _br.GetBit(0x80) != 0;
        if (_br.GetBit(0x80) != 0)
        {
            _absoluteDelta = _br.GetBit(0x80) != 0;
            for (int s = 0; s < 4; s++)
            {
                _segQuant[s] = _br.GetBit(0x80) != 0 ? _br.GetSignedValue(7) : 0;
            }

            for (int s = 0; s < 4; s++)
            {
                _segFilter[s] = _br.GetBit(0x80) != 0 ? _br.GetSignedValue(6) : 0;
            }
        }

        if (_updateMap)
        {
            for (int s = 0; s < 3; s++)
            {
                _segProba[s] = _br.GetBit(0x80) != 0 ? (byte)_br.GetValue(8) : (byte)255;
            }
        }
    }

    private void ParseFilterHeader()
    {
        _simpleFilter = _br.GetBit(0x80) != 0;
        _filterLevel = _br.GetValue(6);
        _sharpness = _br.GetValue(3);
        _useLfDelta = _br.GetBit(0x80) != 0;
        if (_useLfDelta && _br.GetBit(0x80) != 0)
        {
            for (int i = 0; i < 4; i++)
            {
                if (_br.GetBit(0x80) != 0)
                {
                    _refLfDelta[i] = _br.GetSignedValue(6);
                }
            }

            for (int i = 0; i < 4; i++)
            {
                if (_br.GetBit(0x80) != 0)
                {
                    _modeLfDelta[i] = _br.GetSignedValue(6);
                }
            }
        }

        // A frame level of zero disables the filter whatever the segments say.
        _filterType = _filterLevel == 0 ? 0 : _simpleFilter ? 1 : 2;
    }

    private void ParsePartitions(int start)
    {
        _numParts = 1 << _br.GetValue(2);
        int lastPart = _numParts - 1;
        int sizeLeft = _dataLen - start;
        if (sizeLeft < 3 * lastPart)
        {
            throw new ImageFormatException("Truncated VP8 partition table.");
        }

        int partStart = start + (lastPart * 3);
        sizeLeft -= lastPart * 3;
        for (int p = 0; p < lastPart; p++)
        {
            int psize = _data[start + (p * 3)] | (_data[start + (p * 3) + 1] << 8) | (_data[start + (p * 3) + 2] << 16);
            if (psize > sizeLeft)
            {
                psize = sizeLeft;
            }

            _parts[p].Init(_data, partStart, partStart + psize);
            partStart += psize;
            sizeLeft -= psize;
        }

        _parts[lastPart].Init(_data, partStart, partStart + sizeLeft);
        if (partStart >= _dataLen)
        {
            throw new ImageFormatException("The VP8 token partitions are missing.");
        }
    }

    private void ParseQuant()
    {
        int baseQ = _br.GetValue(7);
        int dqy1Dc = _br.GetBit(0x80) != 0 ? _br.GetSignedValue(4) : 0;
        int dqy2Dc = _br.GetBit(0x80) != 0 ? _br.GetSignedValue(4) : 0;
        int dqy2Ac = _br.GetBit(0x80) != 0 ? _br.GetSignedValue(4) : 0;
        int dquvDc = _br.GetBit(0x80) != 0 ? _br.GetSignedValue(4) : 0;
        int dquvAc = _br.GetBit(0x80) != 0 ? _br.GetSignedValue(4) : 0;
        for (int i = 0; i < 4; i++)
        {
            int q;
            if (_useSegment)
            {
                q = _segQuant[i];
                if (!_absoluteDelta)
                {
                    q += baseQ;
                }
            }
            else
            {
                if (i > 0)
                {
                    _quant[i] = _quant[0];
                    continue;
                }

                q = baseQ;
            }

            // RFC 6386 section 14.1: Y2 and chroma factors are scaled or clamped relative to the tables.
            ref Quant m = ref _quant[i];
            m.Y1Dc = VP8Tables.DcQLookup[Clip(q + dqy1Dc, 127)];
            m.Y1Ac = VP8Tables.AcQLookup[Clip(q, 127)];
            m.Y2Dc = VP8Tables.DcQLookup[Clip(q + dqy2Dc, 127)] * 2;
            m.Y2Ac = (VP8Tables.AcQLookup[Clip(q + dqy2Ac, 127)] * 101581) >> 16;
            if (m.Y2Ac < 8)
            {
                m.Y2Ac = 8;
            }

            m.UvDc = VP8Tables.DcQLookup[Clip(q + dquvDc, 117)];
            m.UvAc = VP8Tables.AcQLookup[Clip(q + dquvAc, 127)];
        }
    }

    private void ParseProba()
    {
        ReadOnlySpan<byte> update = VP8Tables.CoeffUpdateProbs;
        ReadOnlySpan<byte> def = VP8Tables.DefaultCoeffProbs;
        for (int i = 0; i < _coeffProbs.Length; i++)
        {
            _coeffProbs[i] = _br.GetBit(update[i]) != 0 ? (byte)_br.GetValue(8) : def[i];
        }

        _useSkipProba = _br.GetBit(0x80) != 0;
        if (_useSkipProba)
        {
            _skipProba = _br.GetValue(8);
        }
    }

    // ---- macroblock loop ----

    private void DecodeFrame()
    {
        int mbw = _mbw;
        _intraTop = new byte[mbw * 4];
        _nzTop = new byte[mbw * 8];
        _nzDcTop = new byte[mbw];
        _segments = new byte[mbw];
        _skips = new byte[mbw];
        _fInner = new byte[mbw * _mbh];
        _fSegment = new byte[mbw * _mbh];
        _fI4 = new byte[mbw * _mbh];
        _topY = new byte[mbw * 16];
        _topU = new byte[mbw * 8];
        _topV = new byte[mbw * 8];
        LoopFilter? filter = _filterType > 0 ? new LoopFilter(this) : null;

        // Parsing the bitstream (modes and coefficients) does not depend on pixels, so large frames run it on a second thread,
        // a few macroblock rows ahead of the reconstruction, loop filter and color conversion. The output is identical.
        bool pipelined = (long)_width * _height >= PipelineMinPixels && _mbh >= 4 && Environment.ProcessorCount > 1;
        int bufCount = pipelined ? 3 : 1;
        var rows = new RowData[bufCount];
        for (int i = 0; i < bufCount; i++)
        {
            rows[i] = new RowData(mbw);
        }

        if (!pipelined)
        {
            for (int mby = 0; mby < _mbh; mby++)
            {
                ParseRow(mby, rows[0]);
                ConsumeRow(mby, rows[0], filter);
            }

            return;
        }

        using var free = new SemaphoreSlim(bufCount, bufCount);
        using var filled = new SemaphoreSlim(0);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(_token);
        Exception? parseError = null;
        Task producer = Task.Factory.StartNew(() =>
        {
            try
            {
                for (int mby = 0; mby < _mbh; mby++)
                {
                    free.Wait(stop.Token);
                    ParseRow(mby, rows[mby % bufCount]);
                    filled.Release();
                }
            }
            catch (Exception e)
            {
                // Handed to the consuming thread, which rethrows it unchanged (or is already unwinding).
                parseError = e;
                filled.Release();
            }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        try
        {
            for (int mby = 0; mby < _mbh; mby++)
            {
                filled.Wait(_token);
                if (Volatile.Read(ref parseError) is Exception error)
                {
                    System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
                }

                ConsumeRow(mby, rows[mby % bufCount], filter);
                free.Release();
            }
        }
        finally
        {
            stop.Cancel();
            producer.Wait();
        }
    }

    private const int PipelineMinPixels = 1 << 17;

    // Modes and coefficients of one macroblock row, handed from the parsing thread to the reconstruction thread.
    private sealed class RowData
    {
        public RowData(int mbw)
        {
            IsI4 = new byte[mbw];
            YModes = new byte[mbw];
            UvModes = new byte[mbw];
            BModes = new byte[mbw * 16];
            Coeffs = new short[mbw * 384];
            Bits = new uint[mbw * 24];
        }

        public byte[] IsI4 { get; }

        public byte[] YModes { get; }

        public byte[] UvModes { get; }

        public byte[] BModes { get; }

        public short[] Coeffs { get; }

        public uint[] Bits { get; }
    }

    private void ParseRow(int mby, RowData rd)
    {
        _token.ThrowIfCancellationRequested();
        ParseIntraModeRow(rd);
        if (_br.Eof)
        {
            throw new ImageFormatException("The VP8 frame is truncated.");
        }

        ref VP8BoolReader tokens = ref _parts[mby & (_numParts - 1)];
        Array.Clear(_nzLeft);
        _nzDcLeft = 0;
        for (int mbx = 0; mbx < _mbw; mbx++)
        {
            DecodeMacroblock(ref tokens, mbx, mby, rd);
            if (tokens.Eof)
            {
                throw new ImageFormatException("The VP8 frame is truncated.");
            }
        }
    }

    private void ConsumeRow(int mby, RowData rd, LoopFilter? filter)
    {
        int mbw = _mbw;
        for (int mbx = 0; mbx < mbw; mbx++)
        {
            Reconstruct(mbx, mby, rd);
        }

        // Prediction of the next row needs this row's unfiltered bottom pixels, so keep them before filtering.
        int slot = mby == 0 ? 0 : 1;
        _y.AsSpan((((slot * 16) + 15) * _yStride), mbw * 16).CopyTo(_topY);
        _u.AsSpan((((slot * 8) + 7) * _uvStride), mbw * 8).CopyTo(_topU);
        _v.AsSpan((((slot * 8) + 7) * _uvStride), mbw * 8).CopyTo(_topV);
        filter?.FilterRow(mby, slot);

        // Rows that no later macroblock row can change are converted and sent on: luma and chroma lines the next row's top
        // edge filter cannot reach (it rewrites the last three lines of this row).
        bool last = mby == _mbh - 1;
        _upsampler!.Advance(_y, _u, _v, _yStride, _uvStride, 16 * (mby - slot), 8 * (mby - slot), (8 * mby) + 5, _alphaPlane, last);
        if (mby > 0 && !last)
        {
            // The current row becomes the "row above" for the next one.
            Buffer.BlockCopy(_y, 16 * _yStride, _y, 0, 16 * _yStride);
            Buffer.BlockCopy(_u, 8 * _uvStride, _u, 0, 8 * _uvStride);
            Buffer.BlockCopy(_v, 8 * _uvStride, _v, 0, 8 * _uvStride);
        }
    }

    private void ParseIntraModeRow(RowData rd)
    {
        Array.Clear(_intraLeft);
        for (int mbx = 0; mbx < _mbw; mbx++)
        {
            int segment = 0;
            if (_updateMap)
            {
                segment = _br.GetBit(_segProba[0]) == 0 ? _br.GetBit(_segProba[1]) : _br.GetBit(_segProba[2]) + 2;
            }

            _segments[mbx] = (byte)segment;
            _skips[mbx] = (byte)(_useSkipProba ? _br.GetBit(_skipProba) : 0);

            bool i4 = _br.GetBit(145) == 0;
            rd.IsI4[mbx] = (byte)(i4 ? 1 : 0);
            if (!i4)
            {
                int ymode = _br.GetBit(156) != 0 ? (_br.GetBit(128) != 0 ? TmPred : HPred) : (_br.GetBit(163) != 0 ? VPred : DcPred);
                rd.YModes[mbx] = (byte)ymode;
                byte b = ymode switch { DcPred => BDc, TmPred => BTm, VPred => BVe, _ => BHe };
                _intraTop.AsSpan(mbx * 4, 4).Fill(b);
                _intraLeft.AsSpan().Fill(b);
            }
            else
            {
                ReadOnlySpan<byte> probs = VP8Tables.KeyFrameBModeProbs;
                Span<byte> modes = rd.BModes.AsSpan(mbx * 16, 16);
                for (int y = 0; y < 4; y++)
                {
                    int ymode = _intraLeft[y];
                    for (int x = 0; x < 4; x++)
                    {
                        int pb = ((_intraTop[(mbx * 4) + x] * 10) + ymode) * 9;
                        int i = BModeTree[_br.GetBit(probs[pb])];
                        while (i > 0)
                        {
                            i = BModeTree[(2 * i) + _br.GetBit(probs[pb + i])];
                        }

                        ymode = -i;
                        _intraTop[(mbx * 4) + x] = (byte)ymode;
                        modes[(y * 4) + x] = (byte)ymode;
                    }

                    _intraLeft[y] = (byte)ymode;
                }
            }

            rd.UvModes[mbx] = (byte)(_br.GetBit(142) == 0 ? DcPred : _br.GetBit(114) == 0 ? VPred : _br.GetBit(183) != 0 ? TmPred : HPred);
        }
    }

    private void DecodeMacroblock(ref VP8BoolReader tokens, int mbx, int mby, RowData rd)
    {
        int segment = _segments[mbx];
        bool i4 = rd.IsI4[mbx] != 0;
        int mbIndex = (mby * _mbw) + mbx;
        Span<short> coeffs = rd.Coeffs.AsSpan(mbx * 384, 384);
        coeffs.Clear();
        uint nonZeroY = 0;
        uint nonZeroUv = 0;
        bool skip = _skips[mbx] != 0;
        Span<uint> blockBits = rd.Bits.AsSpan(mbx * 24, 24);
        blockBits.Clear();
        Span<byte> top = _nzTop.AsSpan(mbx * 8, 8);
        if (!skip)
        {
            ref Quant q = ref _quant[segment];
            int first;
            int ytype;
            if (!i4)
            {
                Span<short> dc = stackalloc short[16];
                dc.Clear();
                int ctx = _nzDcTop[mbx] + _nzDcLeft;
                int nz = GetCoeffs(ref tokens, 1, ctx, q.Y2Dc, q.Y2Ac, 0, dc);
                _nzDcLeft = nz > 0 ? 1 : 0;
                _nzDcTop[mbx] = (byte)_nzDcLeft;
                if (nz > 1)
                {
                    TransformWht(dc, coeffs);
                }
                else
                {
                    int dc0 = (dc[0] + 3) >> 3;
                    for (int i = 0; i < 256; i += 16)
                    {
                        coeffs[i] = (short)dc0;
                    }
                }

                first = 1;
                ytype = 0;
            }
            else
            {
                first = 0;
                ytype = 3;
            }

            for (int by = 0; by < 4; by++)
            {
                for (int bx = 0; bx < 4; bx++)
                {
                    int blk = (by * 4) + bx;
                    int ctx = _nzLeft[by] + top[bx];
                    int nz = GetCoeffs(ref tokens, ytype, ctx, q.Y1Dc, q.Y1Ac, first, coeffs.Slice(blk * 16, 16));
                    byte l = nz > first ? (byte)1 : (byte)0;
                    _nzLeft[by] = l;
                    top[bx] = l;
                    uint code = nz > 3 ? 3u : nz > 1 ? 2u : (coeffs[blk * 16] != 0 ? 1u : 0u);
                    blockBits[blk] = code;
                    nonZeroY |= code;
                }
            }

            for (int ch = 0; ch < 2; ch++)
            {
                int nzBase = 4 + (ch * 2);
                for (int by = 0; by < 2; by++)
                {
                    for (int bx = 0; bx < 2; bx++)
                    {
                        int blk = 16 + (ch * 4) + (by * 2) + bx;
                        int ctx = _nzLeft[nzBase + by] + top[nzBase + bx];
                        int nz = GetCoeffs(ref tokens, 2, ctx, q.UvDc, q.UvAc, 0, coeffs.Slice(blk * 16, 16));
                        byte l = nz > 0 ? (byte)1 : (byte)0;
                        _nzLeft[nzBase + by] = l;
                        top[nzBase + bx] = l;
                        uint code = nz > 3 ? 3u : nz > 1 ? 2u : (coeffs[blk * 16] != 0 ? 1u : 0u);
                        blockBits[blk] = code;
                        nonZeroUv |= code;
                    }
                }
            }

            skip = (nonZeroY | nonZeroUv) == 0;
        }
        else
        {
            top.Clear();
            _nzLeft.AsSpan().Clear();
            if (!i4)
            {
                _nzDcTop[mbx] = 0;
                _nzDcLeft = 0;
            }
        }

        _fSegment[mbIndex] = (byte)segment;
        _fI4[mbIndex] = (byte)(i4 ? 1 : 0);
        _fInner[mbIndex] = (byte)((i4 || !skip) ? 1 : 0);
    }

    // Reads the coefficient tokens of one block (RFC 6386 section 13) and returns the position after the last token.
    private int GetCoeffs(ref VP8BoolReader brRef, int type, int ctx, int dcq, int acq, int n, Span<short> output)
    {
        // Work on a local copy so the reader state lives in registers, then write it back.
        VP8BoolReader br = brRef;
        byte[] p = _coeffProbs;
        int pb = (((type * 8) + Bands[n]) * 3 + ctx) * 11;
        for (; n < 16; n++)
        {
            if (br.GetBit(p[pb]) == 0)
            {
                brRef = br;
                return n; // end of block
            }

            while (br.GetBit(p[pb + 1]) == 0)
            {
                // a zero coefficient; the next token's context is 0
                pb = (((type * 8) + Bands[++n]) * 3) * 11;
                if (n == 16)
                {
                    brRef = br;
                    return 16;
                }
            }

            int nextBand = ((type * 8) + Bands[n + 1]) * 33;
            int v;
            if (br.GetBit(p[pb + 2]) == 0)
            {
                v = 1;
                pb = nextBand + 11;
            }
            else
            {
                v = GetLargeValue(ref br, p, pb);
                pb = nextBand + 22;
            }

            output[Zigzag[n]] = (short)(br.GetSigned(v) * (n > 0 ? acq : dcq));
        }

        brRef = br;
        return 16;
    }

    private static int GetLargeValue(ref VP8BoolReader br, byte[] p, int pb)
    {
        int v;
        if (br.GetBit(p[pb + 3]) == 0)
        {
            v = br.GetBit(p[pb + 4]) == 0 ? 2 : 3 + br.GetBit(p[pb + 5]);
        }
        else if (br.GetBit(p[pb + 6]) == 0)
        {
            if (br.GetBit(p[pb + 7]) == 0)
            {
                v = 5 + br.GetBit(159);
            }
            else
            {
                v = 7 + (2 * br.GetBit(165));
                v += br.GetBit(145);
            }
        }
        else
        {
            int bit1 = br.GetBit(p[pb + 8]);
            int bit0 = br.GetBit(p[pb + 9 + bit1]);
            int cat = (2 * bit1) + bit0;
            byte[] tab = cat switch { 0 => Cat3, 1 => Cat4, 2 => Cat5, _ => Cat6 };
            v = 0;
            foreach (byte t in tab)
            {
                v += v + br.GetBit(t);
            }

            v += 3 + (8 << cat);
        }

        return v;
    }

    // Inverse Walsh-Hadamard transform of the Y2 block into the DC of the 16 luma blocks (RFC 6386 section 14.3).
    internal static void TransformWht(ReadOnlySpan<short> input, Span<short> output)
    {
        Span<int> tmp = stackalloc int[16];
        for (int i = 0; i < 4; i++)
        {
            int a0 = input[i] + input[12 + i];
            int a1 = input[4 + i] + input[8 + i];
            int a2 = input[4 + i] - input[8 + i];
            int a3 = input[i] - input[12 + i];
            tmp[i] = a0 + a1;
            tmp[8 + i] = a0 - a1;
            tmp[4 + i] = a3 + a2;
            tmp[12 + i] = a3 - a2;
        }

        int o = 0;
        for (int i = 0; i < 4; i++)
        {
            int dc = tmp[i * 4] + 3;
            int a0 = dc + tmp[(i * 4) + 3];
            int a1 = tmp[(i * 4) + 1] + tmp[(i * 4) + 2];
            int a2 = tmp[(i * 4) + 1] - tmp[(i * 4) + 2];
            int a3 = dc - tmp[(i * 4) + 3];
            output[o] = (short)((a0 + a1) >> 3);
            output[o + 16] = (short)((a3 + a2) >> 3);
            output[o + 32] = (short)((a0 - a1) >> 3);
            output[o + 48] = (short)((a3 - a2) >> 3);
            o += 64;
        }
    }

    // ---- reconstruction ----

    private void Reconstruct(int mbx, int mby, RowData rd)
    {
        bool i4 = rd.IsI4[mbx] != 0;
        Span<short> coeffs = rd.Coeffs.AsSpan(mbx * 384, 384);
        ReadOnlySpan<uint> blockBits = rd.Bits.AsSpan(mbx * 24, 24);
        byte[] w = _work;
        int slot = mby == 0 ? 0 : 1;
        PrepareBorders(mbx, mby);
        if (!i4)
        {
            Predict.Block(w, YOff, 16, rd.YModes[mbx], mbx > 0, mby > 0);
            for (int blk = 0; blk < 16; blk++)
            {
                AddBlock(w, YOff + ((blk >> 2) * 4 * Bps) + ((blk & 3) * 4), coeffs.Slice(blk * 16, 16), blockBits[blk]);
            }
        }
        else
        {
            // The pixels right of the macroblock's top row serve every sub-block row on the right edge.
            for (int r = 3; r < 16; r += 4)
            {
                for (int k = 0; k < 4; k++)
                {
                    w[YOff + (r * Bps) + 16 + k] = w[YOff - Bps + 16 + k];
                }
            }

            for (int blk = 0; blk < 16; blk++)
            {
                int off = YOff + ((blk >> 2) * 4 * Bps) + ((blk & 3) * 4);
                Predict.SubBlock(w, off, rd.BModes[(mbx * 16) + blk]);
                AddBlock(w, off, coeffs.Slice(blk * 16, 16), blockBits[blk]);
            }
        }

        Predict.Block(w, UOff, 8, rd.UvModes[mbx], mbx > 0, mby > 0);
        Predict.Block(w, VOff, 8, rd.UvModes[mbx], mbx > 0, mby > 0);
        for (int blk = 0; blk < 4; blk++)
        {
            int o = ((blk >> 1) * 4 * Bps) + ((blk & 1) * 4);
            AddBlock(w, UOff + o, coeffs.Slice((16 + blk) * 16, 16), blockBits[16 + blk]);
            AddBlock(w, VOff + o, coeffs.Slice((20 + blk) * 16, 16), blockBits[20 + blk]);
        }

        // Store the macroblock into the planes.
        for (int j = 0; j < 16; j++)
        {
            w.AsSpan(YOff + (j * Bps), 16).CopyTo(_y.AsSpan((((slot * 16) + j) * _yStride) + (mbx * 16), 16));
        }

        for (int j = 0; j < 8; j++)
        {
            int dst = (((slot * 8) + j) * _uvStride) + (mbx * 8);
            w.AsSpan(UOff + (j * Bps), 8).CopyTo(_u.AsSpan(dst, 8));
            w.AsSpan(VOff + (j * Bps), 8).CopyTo(_v.AsSpan(dst, 8));
        }
    }

    private void PrepareBorders(int mbx, int mby) =>
        VP8Borders.PrepareFiltered(_work, _y, _u, _v, _topY, _topU, _topV, _yStride, _uvStride, mbx, mby, mby == 0 ? 0 : 1, _mbw);

    private static void AddBlock(byte[] dst, int off, ReadOnlySpan<short> coeffs, uint code)
    {
        if (code == 0)
        {
            return;
        }

        if (code == 1)
        {
            int dc = (coeffs[0] + 4) >> 3;
            for (int j = 0; j < 4; j++)
            {
                for (int i = 0; i < 4; i++)
                {
                    dst[off + (j * Bps) + i] = Clip255(dst[off + (j * Bps) + i] + dc);
                }
            }

            return;
        }

        Idct.Add(coeffs, dst, off);
    }

    // ---- accessors for the loop filter ----

    internal int MbWidth => _mbw;

    internal int MbHeight => _mbh;

    internal byte[] PlaneY => _y;

    internal byte[] PlaneU => _u;

    internal byte[] PlaneV => _v;

    internal int YStride => _yStride;

    internal int UvStride => _uvStride;

    internal byte[] FilterInner => _fInner;

    internal byte[] FilterSegment => _fSegment;

    internal byte[] FilterI4 => _fI4;

    internal int FilterType => _filterType;

    internal CancellationToken Token => _token;

    /// <summary>Computes the loop filter parameters for a segment and prediction type (RFC 6386 section 15 and 9.6).</summary>
    internal void FilterStrength(int segment, bool i4, out int limit, out int ilevel, out int hevThreshold)
    {
        int level;
        if (_useSegment)
        {
            level = _segFilter[segment];
            if (!_absoluteDelta)
            {
                level += _filterLevel;
            }
        }
        else
        {
            level = _filterLevel;
        }

        if (_useLfDelta)
        {
            level += _refLfDelta[0];
            if (i4)
            {
                level += _modeLfDelta[0];
            }
        }

        level = level < 0 ? 0 : level > 63 ? 63 : level;
        limit = 0;
        ilevel = 0;
        hevThreshold = 0;
        if (level > 0)
        {
            int il = level;
            if (_sharpness > 0)
            {
                il >>= _sharpness > 4 ? 2 : 1;
                if (il > 9 - _sharpness)
                {
                    il = 9 - _sharpness;
                }
            }

            if (il < 1)
            {
                il = 1;
            }

            ilevel = il;
            limit = (2 * level) + il;
            hevThreshold = level >= 40 ? 2 : level >= 15 ? 1 : 0;
        }
    }

    private struct Quant
    {
        public int Y1Dc;
        public int Y1Ac;
        public int Y2Dc;
        public int Y2Ac;
        public int UvDc;
        public int UvAc;
    }
}
