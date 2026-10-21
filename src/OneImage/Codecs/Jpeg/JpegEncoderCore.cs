using System.Buffers;
using OneImage.Kernels;

namespace OneImage.Codecs.Jpeg;

/// <summary>Writes JPEG files: baseline or progressive, standard or optimized Huffman tables.</summary>
internal static class JpegEncoderCore
{
    public static void Encode(ImageEncodeRequest request, Stream destination, CancellationToken ct)
    {
        JpegEncoderOptions options = request.Options switch
        {
            null => new JpegEncoderOptions(),
            JpegEncoderOptions o => o,
            _ => throw new ArgumentException("JPEG encoding needs JpegEncoderOptions.", nameof(request)),
        };
        if (options.Quality is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "JPEG quality must be 1..100.");
        }

        if (options.RestartInterval is < 0 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "JPEG restart interval must be 0..65535.");
        }

        ImageFrameReader frame = request.Frames[0];
        if (frame.Width > 65535 || frame.Height > 65535)
        {
            throw new ImageOperationException("JPEG cannot represent images larger than 65535 pixels in either dimension.");
        }

        using var session = new JpegEncodeSession(frame, request.Metadata, options, destination, frame.Degree, ct);
        session.Run();
    }
}

internal sealed class JpegEncodeSession : IDisposable
{
    private sealed class Comp
    {
        public int H;
        public int V;
        public int Tq;
        public int BlocksPerLine;
        public int BlockRows;
        public int WidthInBlocks;
        public int HeightInBlocks;
        public int DcTable;
        public int AcTable;
        public short[] Blocks = [];
        public int Pred;
    }

    private sealed class ScanSpec
    {
        public int[] Comps = [];
        public int Ss;
        public int Se;
        public int Ah;
        public int Al;
    }

    private readonly ImageFrameReader _frame;
    private readonly ImageMetadata _meta;
    private readonly JpegEncoderOptions _o;
    private readonly Stream _dest;
    private readonly CancellationToken _ct;
    private readonly JpegBitWriter _w;
    private readonly JpegEntropyEncoder _enc;
    private readonly int _width;
    private readonly int _height;
    private readonly Comp[] _comps;
    private readonly int _hmax;
    private readonly int _vmax;
    private readonly int _mcusX;
    private readonly int _mcusY;
    private readonly int[][] _quant = new int[2][];
    private readonly uint[][] _recip = new uint[2][];
    private readonly JpegHuffmanEncodeTable[] _dc = [new(), new()];
    private readonly JpegHuffmanEncodeTable[] _ac = [new(), new()];
    private readonly bool _color;
    private readonly bool _cmyk;
    private bool _storesAll;
    private readonly bool[] _sentDc = new bool[2];
    private readonly bool[] _sentAc = new bool[2];
    /// <summary>Baseline images with more pixels than this (64 MP) are transformed twice for optimized tables instead of holding every block.</summary>
    internal const long MaxStoredPixels = 1L << 26;

    /// <summary>Lowers the stored-pixel limit for the calling thread so tests can reach the two-pass path with small images; 0 means the default.</summary>
    [ThreadStatic]
    internal static long StoredPixelLimitForTests;

    private bool _countAc;
    private readonly long[][] _acFreq = [new long[257], new long[257]];
    private int _slot;
    private readonly int _degree;
    private bool _sentRestartInterval;
    private int _restartCounter;
    private int _restartIndex;

    public JpegEncodeSession(ImageFrameReader frame, ImageMetadata meta, JpegEncoderOptions options, Stream dest, int degree, CancellationToken ct)
    {
        _frame = frame;
        _meta = meta;
        _degree = degree;
        _o = options;
        _dest = dest;
        _ct = ct;
        _w = new JpegBitWriter(dest);
        _enc = new JpegEntropyEncoder(_w);
        _width = frame.Width;
        _height = frame.Height;
        PixelFormat f = frame.Format;
        _cmyk = f.Model == ColorModel.Cmyk;
        _color = f.Model == ColorModel.Rgb;
        if (f.Sample != SampleType.U8 || f.HasAlpha || !(_cmyk || _color || f.Model == ColorModel.Gray))
        {
            throw new ImageOperationException($"The JPEG encoder cannot write {f} directly.");
        }

        int n = _cmyk ? 4 : _color ? 3 : 1;
        _comps = new Comp[n];
        for (int i = 0; i < n; i++)
        {
            _comps[i] = new Comp { H = 1, V = 1, Tq = (i == 1 || i == 2) && !_cmyk ? 1 : 0 };
        }

        if (_color)
        {
            (_comps[0].H, _comps[0].V) = o2(options.Subsampling);
        }

        _hmax = _comps[0].H;
        _vmax = _comps[0].V;
        _mcusX = (_width + (8 * _hmax) - 1) / (8 * _hmax);
        _mcusY = (_height + (8 * _vmax) - 1) / (8 * _vmax);
        foreach (Comp c in _comps)
        {
            c.BlocksPerLine = _mcusX * c.H;
            c.BlockRows = _mcusY * c.V;
            long dw = (((long)_width * c.H) + _hmax - 1) / _hmax;
            long dh = (((long)_height * c.V) + _vmax - 1) / _vmax;
            c.WidthInBlocks = (int)((dw + 7) / 8);
            c.HeightInBlocks = (int)((dh + 7) / 8);
            c.DcTable = c.Tq;
            c.AcTable = c.Tq;
        }

        BuildQuantTables(options.Quality);

        static (int, int) o2(JpegSubsampling s) => s switch
        {
            JpegSubsampling.Yuv444 => (1, 1),
            JpegSubsampling.Yuv422 => (2, 1),
            JpegSubsampling.Yuv440 => (1, 2),
            _ => (2, 2),
        };
    }

    public void Dispose()
    {
        _w.Dispose();
        foreach (Comp c in _comps)
        {
            if (c.Blocks.Length > 0)
            {
                ArrayPool<short>.Shared.Return(c.Blocks);
                c.Blocks = [];
            }
        }
    }

    private void BuildQuantTables(int quality)
    {
        // The quality scale of the reference encoder (T.81 Annex K tables scaled, clamped to 8-bit baseline range).
        int scale = quality < 50 ? 5000 / quality : 200 - (quality * 2);
        for (int t = 0; t < 2; t++)
        {
            ReadOnlySpan<byte> basis = t == 0 ? JpegConstants.StdLuminanceQuant : JpegConstants.StdChrominanceQuant;
            int[] q = new int[64];
            uint[] r = new uint[64];
            for (int i = 0; i < 64; i++)
            {
                q[i] = Math.Clamp(((basis[i] * scale) + 50) / 100, 1, 255);
                r[i] = JpegFdct.Reciprocal(q[i]);
            }

            _quant[t] = q;
            _recip[t] = r;
        }
    }

    public void Run()
    {
        bool progressive = _o.Progressive;
        bool optimize = progressive || _o.OptimizeHuffman;

        // Optimized tables need the statistics of the whole image before the first byte of the scan. Keeping every quantized
        // block costs about 3 bytes per pixel, so very large baseline images are transformed twice instead (once to count,
        // once to write) and use memory proportional to the width. Progressive scans always keep the blocks.
        bool twoPass = optimize && !progressive && (long)_width * _height > (StoredPixelLimitForTests > 0 ? StoredPixelLimitForTests : MaxStoredPixels);
        _storesAll = optimize && !twoPass;
        WriteHeaders(progressive);
        if (!optimize)
        {
            _dc[0].Build(JpegConstants.DcLumaBits, JpegConstants.DcLumaVals);
            _dc[1].Build(JpegConstants.DcChromaBits, JpegConstants.DcChromaVals);
            _ac[0].Build(JpegConstants.AcLumaBits, JpegConstants.AcLumaVals);
            _ac[1].Build(JpegConstants.AcChromaBits, JpegConstants.AcChromaVals);
            var scan = new ScanSpec { Comps = Enumerable.Range(0, _comps.Length).ToArray(), Se = 63 };
            EmitScanTables(scan, false);
            WriteScanHeader(scan, false);
            BeginScan();
            ProduceBlocks(mcuRowDone: row => EncodeMcuRowSequential(row));
            EndScan(null);
        }
        else
        {
            if (_storesAll)
            {
                foreach (Comp c in _comps)
                {
                    c.Blocks = ArrayPool<short>.Shared.Rent(checked(c.BlocksPerLine * c.BlockRows * 64));
                }

                ProduceBlocks(mcuRowDone: null);
            }

            if (progressive)
            {
                foreach (ScanSpec s in ProgressiveScript())
                {
                    RunOptimizedScan(s, true);
                }
            }
            else
            {
                RunOptimizedScan(new ScanSpec { Comps = Enumerable.Range(0, _comps.Length).ToArray(), Se = 63 }, false);
            }
        }

        _w.WriteMarker(JpegConstants.Eoi);
        _w.Flush();
    }

    // ---- headers ----

    private void WriteHeaders(bool progressive)
    {
        _w.WriteMarker(JpegConstants.Soi);
        if (_cmyk)
        {
            // Adobe APP14: version 100, flags 0, transform 0 (CMYK). Adobe files store ink inverted.
            _w.WriteMarker(JpegConstants.App14);
            _w.WriteUInt16(14);
            _w.WriteBytes("Adobe"u8);
            _w.WriteUInt16(100);
            _w.WriteUInt16(0);
            _w.WriteUInt16(0);
            _w.WriteByte(0);
        }
        else
        {
            _w.WriteMarker(JpegConstants.App0);
            _w.WriteUInt16(16);
            _w.WriteBytes("JFIF\0"u8);
            _w.WriteUInt16(0x0101);
            _w.WriteByte(0);
            _w.WriteUInt16(1);
            _w.WriteUInt16(1);
            _w.WriteByte(0);
            _w.WriteByte(0);
        }

        WriteMetadata();
        int tables = _comps.Length == 3 ? 2 : 1;
        for (int t = 0; t < tables; t++)
        {
            _w.WriteMarker(JpegConstants.Dqt);
            _w.WriteUInt16(67);
            _w.WriteByte(t);
            for (int i = 0; i < 64; i++)
            {
                _w.WriteByte(_quant[t][JpegConstants.Zigzag[i]]);
            }
        }

        _w.WriteMarker(progressive ? JpegConstants.Sof2 : JpegConstants.Sof0);
        _w.WriteUInt16(8 + (3 * _comps.Length));
        _w.WriteByte(8);
        _w.WriteUInt16(_height);
        _w.WriteUInt16(_width);
        _w.WriteByte(_comps.Length);
        for (int i = 0; i < _comps.Length; i++)
        {
            _w.WriteByte(i + 1);
            _w.WriteByte((_comps[i].H << 4) | _comps[i].V);
            _w.WriteByte(_comps[i].Tq);
        }
    }

    private void WriteMetadata()
    {
        // Dimension tags are corrected to the written size; the orientation tag keeps the metadata's value because
        // the pixels handed to the encoder have not been rotated by it. A malformed blob is carried through verbatim.
        ReadOnlyMemory<byte> exif = _meta.Exif;
        if (!exif.IsEmpty)
        {
            try
            {
                exif = _meta.ExifForOutput(_width, _height, _meta.Orientation);
            }
            catch (ImageFormatException)
            {
            }
        }

        if (exif.Length is > 0 and <= 65535 - 8)
        {
            _w.WriteMarker(JpegConstants.App1);
            _w.WriteUInt16(2 + 6 + exif.Length);
            _w.WriteBytes("Exif\0\0"u8);
            _w.WriteBytes(exif.Span);
        }

        ReadOnlySpan<byte> xmpId = "http://ns.adobe.com/xap/1.0/\0"u8;
        if (_meta.Xmp.Length > 0 && _meta.Xmp.Length <= 65535 - 2 - xmpId.Length)
        {
            _w.WriteMarker(JpegConstants.App1);
            _w.WriteUInt16(2 + xmpId.Length + _meta.Xmp.Length);
            _w.WriteBytes(xmpId);
            _w.WriteBytes(_meta.Xmp.Span);
        }

        if (_meta.IccProfile.Length > 0)
        {
            const int chunk = 65535 - 2 - 14;
            int count = (_meta.IccProfile.Length + chunk - 1) / chunk;
            if (count <= 255)
            {
                for (int i = 0; i < count; i++)
                {
                    ReadOnlySpan<byte> part = _meta.IccProfile.Span.Slice(i * chunk, Math.Min(chunk, _meta.IccProfile.Length - (i * chunk)));
                    _w.WriteMarker(JpegConstants.App2);
                    _w.WriteUInt16(2 + 14 + part.Length);
                    _w.WriteBytes("ICC_PROFILE\0"u8);
                    _w.WriteByte(i + 1);
                    _w.WriteByte(count);
                    _w.WriteBytes(part);
                }
            }
        }

        // IPTC-NAA records travel as Photoshop image resource 0x0404 inside an APP13 segment.
        if (_meta.Iptc.Length > 0 && _meta.Iptc.Length <= 65535 - 2 - 14 - 12 - 1)
        {
            int size = _meta.Iptc.Length;
            int padded = (size + 1) & ~1;
            _w.WriteMarker(JpegConstants.App13);
            _w.WriteUInt16(2 + 14 + 12 + padded);
            _w.WriteBytes("Photoshop 3.0\0"u8);
            _w.WriteBytes("8BIM"u8);
            _w.WriteUInt16(0x0404);
            _w.WriteUInt16(0);
            _w.WriteByte(size >> 24);
            _w.WriteByte((size >> 16) & 0xFF);
            _w.WriteByte((size >> 8) & 0xFF);
            _w.WriteByte(size & 0xFF);
            _w.WriteBytes(_meta.Iptc.Span);
            if (padded != size)
            {
                _w.WriteByte(0);
            }
        }
    }

    /// <summary>Writes the Huffman tables a scan needs that the decoder does not have yet, in component order (DC then AC for each).</summary>
    private void EmitScanTables(ScanSpec s, bool progressive)
    {
        bool needDc = !progressive || (s.Ss == 0 && s.Ah == 0);
        bool needAc = !progressive || s.Ss > 0;
        foreach (int ci in s.Comps)
        {
            Comp c = _comps[ci];
            if (needDc && !_sentDc[c.DcTable])
            {
                WriteDht(c.DcTable, _dc[c.DcTable]);
                _sentDc[c.DcTable] = true;
            }

            if (needAc && !_sentAc[c.AcTable])
            {
                WriteDht(0x10 | c.AcTable, _ac[c.AcTable]);
                _sentAc[c.AcTable] = true;
            }
        }
    }

    private void WriteDht(int tcth, JpegHuffmanEncodeTable t)
    {
        _w.WriteMarker(JpegConstants.Dht);
        _w.WriteUInt16(2 + 1 + 16 + t.Count);
        _w.WriteByte(tcth);
        for (int i = 1; i <= 16; i++)
        {
            _w.WriteByte(t.Bits[i]);
        }

        _w.WriteBytes(t.Values.AsSpan(0, t.Count));
    }

    private void WriteScanHeader(ScanSpec s, bool progressive)
    {
        if (_o.RestartInterval > 0 && !_sentRestartInterval)
        {
            _w.WriteMarker(JpegConstants.Dri);
            _w.WriteUInt16(4);
            _w.WriteUInt16(_o.RestartInterval);
            _sentRestartInterval = true;
        }

        _w.WriteMarker(JpegConstants.Sos);
        _w.WriteUInt16(6 + (2 * s.Comps.Length));
        _w.WriteByte(s.Comps.Length);
        foreach (int ci in s.Comps)
        {
            _w.WriteByte(ci + 1);
            int td = _comps[ci].DcTable;
            int ta = _comps[ci].AcTable;
            if (progressive)
            {
                // A progressive scan uses only DC or only AC tables; the unused selector is written as zero.
                if (s.Ss == 0)
                {
                    td = s.Ah == 0 ? td : 0;
                    ta = 0;
                }
                else
                {
                    td = 0;
                }
            }

            _w.WriteByte((td << 4) | ta);
        }

        _w.WriteByte(s.Ss);
        _w.WriteByte(progressive ? s.Se : 63);
        _w.WriteByte((s.Ah << 4) | s.Al);
    }

    // ---- pixel pipeline: rows -> component planes -> quantized blocks ----

    /// <summary>Per-worker buffers for turning one MCU row of pixels into quantized blocks.</summary>
    private sealed class RowScratch
    {
        public byte[][] Full = [];
        public byte[][] Planes = [];
        public int[] Coef = new int[64];
        public long[][] AcFreq = [new long[257], new long[257]];
    }

    /// <summary>
    /// Produces the quantized blocks of every MCU row. Pixels are read in order, a batch of MCU rows at a time; the batch's rows
    /// are transformed in parallel (each row is independent) and handed to <paramref name="mcuRowDone"/> in order. The bytes
    /// produced do not depend on the degree of parallelism.
    /// </summary>
    private void ProduceBlocks(Action<int>? mcuRowDone)
    {
        _countAc = _storesAll && !_o.Progressive;
        int rowsPerMcu = 8 * _vmax;
        int fw = _mcusX * 8 * _hmax;
        int channels = _frame.Format.ChannelCount;
        int workers = Math.Max(1, Math.Min(_degree, _mcusY));
        int batch = workers == 1 ? 1 : workers * 2;
        int inputBytes = Math.Max(1, _width * channels * rowsPerMcu);
        var inputs = new byte[batch][];
        var scratch = new RowScratch[workers];
        short[][] streamBlocks = new short[_comps.Length][];
        try
        {
            for (int i = 0; i < batch; i++)
            {
                inputs[i] = ArrayPool<byte>.Shared.Rent(inputBytes);
            }

            for (int w = 0; w < workers; w++)
            {
                var sc = new RowScratch { Full = new byte[_comps.Length][], Planes = new byte[_comps.Length][] };
                scratch[w] = sc;
                for (int i = 0; i < _comps.Length; i++)
                {
                    sc.Full[i] = ArrayPool<byte>.Shared.Rent(fw * rowsPerMcu);
                    sc.Planes[i] = ArrayPool<byte>.Shared.Rent(_comps[i].BlocksPerLine * 8 * 8 * _comps[i].V);
                }
            }

            // The streaming path holds one batch of MCU rows of blocks.
            if (!_storesAll)
            {
                for (int i = 0; i < _comps.Length; i++)
                {
                    streamBlocks[i] = ArrayPool<short>.Shared.Rent(checked(_comps[i].BlocksPerLine * _comps[i].V * 64 * batch));
                    _comps[i].Blocks = streamBlocks[i];
                }
            }

            for (int first = 0; first < _mcusY; first += batch)
            {
                _ct.ThrowIfCancellationRequested();
                int count = Math.Min(batch, _mcusY - first);
                for (int k = 0; k < count; k++)
                {
                    int y0 = (first + k) * rowsPerMcu;
                    _frame.ReadRows(y0, Math.Min(rowsPerMcu, _height - y0), inputs[k]);
                }

                void DoRow(int k, RowScratch sc)
                {
                    int my = first + k;
                    int avail = Math.Min(rowsPerMcu, _height - (my * rowsPerMcu));
                    int groups = (avail + _vmax - 1) / _vmax;
                    int usable = groups * _vmax;
                    ConvertRows(inputs[k], avail, usable, sc.Full, fw);
                    for (int ci = 0; ci < _comps.Length; ci++)
                    {
                        Downsample(sc.Full[ci], fw, usable, _comps[ci], sc.Planes[ci], groups);
                        QuantizeRow(_comps[ci], my, k, sc.Planes[ci], sc.Coef);
                        if (_countAc)
                        {
                            CountRowAc(_comps[ci], my, sc.AcFreq[_comps[ci].Tq]);
                        }
                    }
                }

                if (workers == 1 || count == 1)
                {
                    for (int k = 0; k < count; k++)
                    {
                        DoRow(k, scratch[0]);
                    }
                }
                else
                {
                    int used = Math.Min(workers, count);
                    Parallel.For(0, used, new ParallelOptions { MaxDegreeOfParallelism = used, CancellationToken = _ct }, w =>
                    {
                        for (int k = w; k < count; k += used)
                        {
                            DoRow(k, scratch[w]);
                        }
                    });
                }

                if (mcuRowDone is not null)
                {
                    for (int k = 0; k < count; k++)
                    {
                        _slot = k;
                        mcuRowDone(first + k);
                    }
                }
            }

            if (_countAc)
            {
                foreach (RowScratch sc in scratch)
                {
                    for (int t = 0; t < 2; t++)
                    {
                        for (int i = 0; i < 257; i++)
                        {
                            _acFreq[t][i] += sc.AcFreq[t][i];
                        }
                    }
                }
            }
        }
        finally
        {
            foreach (byte[]? b in inputs)
            {
                if (b is not null)
                {
                    ArrayPool<byte>.Shared.Return(b);
                }
            }

            foreach (RowScratch? sc in scratch)
            {
                if (sc is null)
                {
                    continue;
                }

                foreach (byte[]? b in sc.Full)
                {
                    if (b is not null)
                    {
                        ArrayPool<byte>.Shared.Return(b);
                    }
                }

                foreach (byte[]? b in sc.Planes)
                {
                    if (b is not null)
                    {
                        ArrayPool<byte>.Shared.Return(b);
                    }
                }
            }

            if (!_storesAll)
            {
                for (int i = 0; i < _comps.Length; i++)
                {
                    if (streamBlocks[i] is not null)
                    {
                        ArrayPool<short>.Shared.Return(streamBlocks[i]);
                    }

                    _comps[i].Blocks = [];
                }
            }
        }
    }

    /// <summary>Converts input pixels to full-resolution component rows, replicating the last pixel to the padded width and the last row to a whole row group.</summary>
    private void ConvertRows(byte[] input, int avail, int usable, byte[][] full, int fw)
    {
        int ch = _frame.Format.ChannelCount;
        for (int y = 0; y < usable; y++)
        {
            ReadOnlySpan<byte> src = input.AsSpan(Math.Min(y, avail - 1) * _width * ch, _width * ch);
            if (_color)
            {
                Span<byte> yy = full[0].AsSpan(y * fw, fw);
                Span<byte> cb = full[1].AsSpan(y * fw, fw);
                Span<byte> cr = full[2].AsSpan(y * fw, fw);
                for (int x = 0; x < _width; x++)
                {
                    int r = src[3 * x];
                    int g = src[(3 * x) + 1];
                    int b = src[(3 * x) + 2];
                    yy[x] = (byte)(((19595 * r) + (38470 * g) + (7471 * b) + 32768) >> 16);
                    cb[x] = (byte)(((-11059 * r) - (21709 * g) + (32768 * b) + (128 << 16) + 32767) >> 16);
                    cr[x] = (byte)(((32768 * r) - (27439 * g) - (5329 * b) + (128 << 16) + 32767) >> 16);
                }

                yy[_width..].Fill(yy[_width - 1]);
                cb[_width..].Fill(cb[_width - 1]);
                cr[_width..].Fill(cr[_width - 1]);
            }
            else if (_cmyk)
            {
                for (int k = 0; k < 4; k++)
                {
                    Span<byte> p = full[k].AsSpan(y * fw, fw);
                    for (int x = 0; x < _width; x++)
                    {
                        p[x] = (byte)(255 - src[(4 * x) + k]);
                    }

                    p[_width..].Fill(p[_width - 1]);
                }
            }
            else
            {
                Span<byte> p = full[0].AsSpan(y * fw, fw);
                src.CopyTo(p);
                p[_width..].Fill(p[_width - 1]);
            }
        }
    }

    /// <summary>Box-averages a full-resolution component into <paramref name="plane"/> (width BlocksPerLine * 8, V * 8 rows), then pads rows below the data by replication.</summary>
    private void Downsample(byte[] src, int fw, int usableRows, Comp c, byte[] plane, int groups)
    {
        int hx = _hmax / c.H;
        int vx = _vmax / c.V;
        int pw = c.BlocksPerLine * 8;
        int outRows = groups * c.V;
        for (int oy = 0; oy < outRows; oy++)
        {
            Span<byte> dst = plane.AsSpan(oy * pw, pw);
            if (hx == 1 && vx == 1)
            {
                src.AsSpan(oy * fw, pw).CopyTo(dst);
            }
            else if (hx == 2 && vx == 2)
            {
                ReadOnlySpan<byte> a = src.AsSpan(2 * oy * fw, fw);
                ReadOnlySpan<byte> b = src.AsSpan(((2 * oy) + 1) * fw, fw);
                int bias = 1;
                for (int x = 0; x < pw; x++)
                {
                    dst[x] = (byte)((a[2 * x] + a[(2 * x) + 1] + b[2 * x] + b[(2 * x) + 1] + bias) >> 2);
                    bias ^= 3;
                }
            }
            else if (hx == 2)
            {
                ReadOnlySpan<byte> a = src.AsSpan(oy * fw, fw);
                int bias = 0;
                for (int x = 0; x < pw; x++)
                {
                    dst[x] = (byte)((a[2 * x] + a[(2 * x) + 1] + bias) >> 1);
                    bias ^= 1;
                }
            }
            else
            {
                ReadOnlySpan<byte> a = src.AsSpan(2 * oy * fw, fw);
                ReadOnlySpan<byte> b = src.AsSpan(((2 * oy) + 1) * fw, fw);
                for (int x = 0; x < pw; x++)
                {
                    dst[x] = (byte)((a[x] + b[x] + 1) >> 1);
                }
            }
        }

        for (int oy = outRows; oy < c.V * 8; oy++)
        {
            plane.AsSpan((outRows - 1) * pw, pw).CopyTo(plane.AsSpan(oy * pw, pw));
        }
    }

    /// <summary>
    /// Transforms and quantizes one MCU row of a component. Blocks wholly outside the component's real extent are
    /// "dummy" blocks: no AC data and the DC of the block before them, so they cost almost nothing to code.
    /// </summary>
    private void QuantizeRow(Comp c, int my, int slot, byte[] plane, int[] coef)
    {
        int table = c.Tq;
        int pw = c.BlocksPerLine * 8;
        for (int by = 0; by < c.V; by++)
        {
            int absRow = (my * c.V) + by;
            int rowIndex = _storesAll ? absRow : (slot * c.V) + by;
            Span<short> row = c.Blocks.AsSpan(rowIndex * c.BlocksPerLine * 64, c.BlocksPerLine * 64);
            bool realRow = absRow < c.HeightInBlocks;
            for (int bx = 0; bx < c.BlocksPerLine; bx++)
            {
                Span<short> blk = row.Slice(bx * 64, 64);
                if (realRow && bx < c.WidthInBlocks)
                {
                    JpegFdct.ForwardQuantize(plane.AsSpan((by * 8 * pw) + (bx * 8)), pw, _quant[table], _recip[table], JpegConstants.Zigzag, blk);
                }
                else
                {
                    blk.Clear();
                    if (realRow)
                    {
                        // Right-edge dummy block: repeat the DC of the block before it.
                        blk[0] = row[(bx - 1) * 64];
                    }
                    else if (by > 0)
                    {
                        // Bottom dummy row: DC of the last block of the row above within the same MCU.
                        int srcCol = ((bx / c.H) * c.H) + c.H - 1;
                        blk[0] = c.Blocks[(((rowIndex - 1) * c.BlocksPerLine) + srcCol) * 64];
                    }
                }
            }
        }

    }

    /// <summary>Adds the AC symbol counts of one MCU row of freshly quantized blocks (the same blocks, in any order, that the scan will code).</summary>
    private void CountRowAc(Comp c, int my, long[] freq)
    {
        for (int by = 0; by < c.V; by++)
        {
            int absRow = (my * c.V) + by;
            if (_comps.Length == 1 && absRow >= c.HeightInBlocks)
            {
                continue;
            }

            int cols = _comps.Length == 1 ? c.WidthInBlocks : c.BlocksPerLine;
            Span<short> row = c.Blocks.AsSpan(absRow * c.BlocksPerLine * 64, c.BlocksPerLine * 64);
            for (int bx = 0; bx < cols; bx++)
            {
                JpegEntropyEncoder.CountAc(row.Slice(bx * 64, 64), freq);
            }
        }
    }

    // ---- entropy coding ----

    private void BeginScan()
    {
        foreach (Comp c in _comps)
        {
            c.Pred = 0;
        }

        _enc.Reset();
        _restartCounter = 0;
        _restartIndex = 0;
    }

    private void EndScan(JpegHuffmanEncodeTable? ac)
    {
        if (ac is not null)
        {
            _enc.EmitEobRun(ac);
        }

        _w.AlignWithOnes();
    }

    private void RestartIfDue(JpegHuffmanEncodeTable? acForEob)
    {
        int ri = _o.RestartInterval;
        if (ri == 0)
        {
            return;
        }

        if (_restartCounter == ri)
        {
            if (acForEob is not null)
            {
                _enc.EmitEobRun(acForEob);
            }

            _w.AlignWithOnes();
            if (!_w.Counting)
            {
                _w.WriteMarker(0xD0 + (_restartIndex & 7));
            }

            _restartIndex++;
            _restartCounter = 0;
            foreach (Comp c in _comps)
            {
                c.Pred = 0;
            }
        }

        _restartCounter++;
    }

    private void EncodeMcuRowSequential(int my)
    {
        // Streaming: blocks of this MCU row are in each component's buffer, row 'by' at offset by.
        for (int mx = 0; mx < _mcusX; mx++)
        {
            RestartIfDue(null);
            for (int ci = 0; ci < _comps.Length; ci++)
            {
                Comp c = _comps[ci];
                for (int by = 0; by < c.V; by++)
                {
                    for (int bx = 0; bx < c.H; bx++)
                    {
                        int idx = ((((_slot * c.V) + by) * c.BlocksPerLine) + (mx * c.H) + bx) * 64;
                        _enc.EncodeBaseline(c.Blocks.AsSpan(idx, 64), ref c.Pred, _dc[c.DcTable], _ac[c.AcTable]);
                    }
                }
            }
        }

        _ = my;
    }

    /// <summary>Runs one scan over the stored coefficients: a statistics pass, table output, then the coding pass.</summary>
    private void RunOptimizedScan(ScanSpec s, bool progressive)
    {
        bool needDc = !progressive || (s.Ss == 0 && s.Ah == 0);
        bool needAc = !progressive || s.Ss > 0;

        // Component-to-table mapping: luma uses table 0, the others table 1.
        foreach (int ci in s.Comps)
        {
            _comps[ci].DcTable = _comps[ci].Tq;
            _comps[ci].AcTable = _comps[ci].Tq;
        }

        foreach (JpegHuffmanEncodeTable t in _dc)
        {
            Array.Clear(t.Freq);
        }

        foreach (JpegHuffmanEncodeTable t in _ac)
        {
            Array.Clear(t.Freq);
        }

        _w.Counting = true;
        _enc.DcOnly = _countAc;
        EncodeStoredScan(s, progressive);
        _enc.DcOnly = false;
        _w.Counting = false;
        if (_countAc)
        {
            // The AC symbols were counted while the blocks were produced.
            for (int t = 0; t < 2; t++)
            {
                for (int i = 0; i < 257; i++)
                {
                    _ac[t].Freq[i] += _acFreq[t][i];
                }
            }
        }

        bool dc0 = false, dc1 = false, ac0 = false, ac1 = false;
        foreach (int ci in s.Comps)
        {
            if (needDc)
            {
                if (_comps[ci].DcTable == 0)
                {
                    dc0 = true;
                }
                else
                {
                    dc1 = true;
                }
            }

            if (needAc)
            {
                if (_comps[ci].AcTable == 0)
                {
                    ac0 = true;
                }
                else
                {
                    ac1 = true;
                }
            }
        }

        if (dc0)
        {
            _dc[0].BuildOptimal();
        }

        if (dc1)
        {
            _dc[1].BuildOptimal();
        }

        if (ac0)
        {
            _ac[0].BuildOptimal();
        }

        if (ac1)
        {
            _ac[1].BuildOptimal();
        }

        Array.Clear(_sentDc);
        Array.Clear(_sentAc);
        EmitScanTables(s, progressive);
        WriteScanHeader(s, progressive);
        if (!progressive && _storesAll && _comps.Length > 1 && _o.RestartInterval == 0 && _degree > 1 && _mcusY >= 8)
        {
            EncodeStoredScanParallel();
        }
        else
        {
            EncodeStoredScan(s, progressive);
        }
    }

    /// <summary>
    /// Codes the stored blocks of a baseline interleaved scan in parallel: bands of MCU rows are coded into private raw bit
    /// buffers (the first DC predictor of a band is the DC of the block coded just before it) and appended in order, where
    /// byte stuffing is applied. The bytes equal those of the sequential coder.
    /// </summary>
    private void EncodeStoredScanParallel()
    {
        BeginScan();
        int bands = Math.Min(_mcusY, _degree * 4);
        var raw = new JpegBitWriter[bands];
        try
        {
            Parallel.For(0, bands, new ParallelOptions { MaxDegreeOfParallelism = _degree, CancellationToken = _ct }, b =>
            {
                int my0 = (int)((long)_mcusY * b / bands);
                int my1 = (int)((long)_mcusY * (b + 1) / bands);
                var w = new JpegBitWriter();
                raw[b] = w;
                var enc = new JpegEntropyEncoder(w);
                var pred = new int[_comps.Length];
                if (my0 > 0)
                {
                    for (int ci = 0; ci < _comps.Length; ci++)
                    {
                        Comp c = _comps[ci];
                        pred[ci] = c.Blocks[((((my0 * c.V) - 1) * c.BlocksPerLine) + c.BlocksPerLine - 1) * 64];
                    }
                }

                for (int my = my0; my < my1; my++)
                {
                    for (int mx = 0; mx < _mcusX; mx++)
                    {
                        for (int ci = 0; ci < _comps.Length; ci++)
                        {
                            Comp c = _comps[ci];
                            for (int by = 0; by < c.V; by++)
                            {
                                for (int bx = 0; bx < c.H; bx++)
                                {
                                    int idx = ((((my * c.V) + by) * c.BlocksPerLine) + (mx * c.H) + bx) * 64;
                                    enc.EncodeBaseline(c.Blocks.AsSpan(idx, 64), ref pred[ci], _dc[c.DcTable], _ac[c.AcTable]);
                                }
                            }
                        }
                    }
                }
            });

            for (int b = 0; b < bands; b++)
            {
                _w.AppendRaw(raw[b]);
                raw[b].Dispose();
                raw[b] = null!;
            }
        }
        finally
        {
            foreach (JpegBitWriter? w in raw)
            {
                w?.Dispose();
            }
        }

        EndScan(null);
    }

    private void EncodeStoredScan(ScanSpec s, bool progressive)
    {
        BeginScan();
        if (!_storesAll)
        {
            ProduceBlocks(mcuRowDone: EncodeMcuRowSequential);
            EndScan(null);
            return;
        }

        bool interleaved = s.Comps.Length > 1;
        JpegHuffmanEncodeTable? lastAc = null;
        int rows;
        int cols;
        if (interleaved)
        {
            rows = _mcusY;
            cols = _mcusX;
        }
        else
        {
            Comp only = _comps[s.Comps[0]];
            rows = only.HeightInBlocks;
            cols = only.WidthInBlocks;
        }

        for (int my = 0; my < rows; my++)
        {
            _ct.ThrowIfCancellationRequested();
            for (int mx = 0; mx < cols; mx++)
            {
                RestartIfDue(progressive && s.Ss > 0 ? _ac[_comps[s.Comps[0]].AcTable] : null);
                if (interleaved)
                {
                    foreach (int ci in s.Comps)
                    {
                        Comp c = _comps[ci];
                        for (int by = 0; by < c.V; by++)
                        {
                            for (int bx = 0; bx < c.H; bx++)
                            {
                                int idx = ((((my * c.V) + by) * c.BlocksPerLine) + (mx * c.H) + bx) * 64;
                                EncodeBlock(s, progressive, c, idx, ref lastAc);
                            }
                        }
                    }
                }
                else
                {
                    Comp c = _comps[s.Comps[0]];
                    EncodeBlock(s, progressive, c, ((my * c.BlocksPerLine) + mx) * 64, ref lastAc);
                }
            }
        }

        EndScan(progressive && s.Ss > 0 ? _ac[_comps[s.Comps[0]].AcTable] : null);
    }

    private void EncodeBlock(ScanSpec s, bool progressive, Comp c, int idx, ref JpegHuffmanEncodeTable? lastAc)
    {
        ReadOnlySpan<short> blk = c.Blocks.AsSpan(idx, 64);
        if (!progressive)
        {
            _enc.EncodeBaseline(blk, ref c.Pred, _dc[c.DcTable], _ac[c.AcTable]);
        }
        else if (s.Ss == 0)
        {
            if (s.Ah == 0)
            {
                _enc.EncodeDcFirst(blk, ref c.Pred, _dc[c.DcTable], s.Al);
            }
            else
            {
                _enc.EncodeDcRefine(blk, s.Al);
            }
        }
        else
        {
            lastAc = _ac[c.AcTable];
            if (s.Ah == 0)
            {
                _enc.EncodeAcFirst(blk, lastAc, s.Ss, s.Se, s.Al);
            }
            else
            {
                _enc.EncodeAcRefine(blk, lastAc, s.Ss, s.Se, s.Al);
            }
        }
    }

    /// <summary>The scan sequence of the reference encoder's default progression (low-frequency detail first, then refinement).</summary>
    private List<ScanSpec> ProgressiveScript()
    {
        int n = _comps.Length;
        int[] all = Enumerable.Range(0, n).ToArray();
        var list = new List<ScanSpec>
        {
            new() { Comps = all, Ss = 0, Se = 0, Ah = 0, Al = 1 },
            new() { Comps = [0], Ss = 1, Se = 5, Ah = 0, Al = 2 },
        };
        for (int i = n - 1; i >= 1; i--)
        {
            list.Add(new ScanSpec { Comps = [i], Ss = 1, Se = 63, Ah = 0, Al = 1 });
        }

        list.Add(new ScanSpec { Comps = [0], Ss = 6, Se = 63, Ah = 0, Al = 2 });
        list.Add(new ScanSpec { Comps = [0], Ss = 1, Se = 63, Ah = 2, Al = 1 });
        list.Add(new ScanSpec { Comps = all, Ss = 0, Se = 0, Ah = 1, Al = 0 });
        for (int i = n - 1; i >= 1; i--)
        {
            list.Add(new ScanSpec { Comps = [i], Ss = 1, Se = 63, Ah = 1, Al = 0 });
        }

        list.Add(new ScanSpec { Comps = [0], Ss = 1, Se = 63, Ah = 1, Al = 0 });
        return list;
    }
}
