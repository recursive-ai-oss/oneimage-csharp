namespace OneImage.Codecs.Jpx;

/// <summary>A component's decoded samples over a rectangle of the component's (possibly reduced) coordinate grid.</summary>
internal sealed class JpxPlane
{
    public JpxPlane(int x0, int y0, int width, int height)
    {
        X0 = x0;
        Y0 = y0;
        Width = width;
        Height = height;
        long count = width > 0 && height > 0 ? (long)width * height : 0;
        if (count > Array.MaxLength)
        {
            throw new ImageLimitException("Plane too large.");
        }

        Data = count > 0 ? new ushort[count] : [];
    }

    public int X0 { get; }

    public int Y0 { get; }

    public int Width { get; }

    public int Height { get; }

    public ushort[] Data { get; }
}

/// <summary>Decodes one tile: tier-2 packets, tier-1 code-blocks, dequantization, inverse wavelet, component transform and level shift.</summary>
internal static class JpxTileDecoder
{
    /// <summary>Decodes a tile into the planes. Returns true when the tile data was damaged or incomplete.</summary>
    /// <param name="cs">The indexed codestream.</param>
    /// <param name="tile">Tile index.</param>
    /// <param name="options">Decode options.</param>
    /// <param name="reduction">Resolution levels to drop.</param>
    /// <param name="planes">One plane per component; tile samples outside a plane's rectangle are dropped.</param>
    /// <param name="tier1Budget">Remaining coding-pass work allowance, in sample visits; reduced by this tile.</param>
    /// <param name="ct">Cancellation.</param>
    public static bool Decode(JpxCodestream cs, int tile, JpxDecodeOptions options, int reduction, JpxPlane[] planes, ref long tier1Budget, CancellationToken ct)
    {
        JpxSiz siz = cs.Siz;
        JpxTileEntry? entry = cs.Tiles[tile];
        if (entry is null || entry.Parts.Count == 0)
        {
            return true;
        }

        int tp = tile % siz.TilesX;
        int tq = tile / siz.TilesX;
        long tx0 = Math.Max((long)siz.XTOsiz + ((long)tp * siz.XTsiz), siz.XOsiz);
        long ty0 = Math.Max((long)siz.YTOsiz + ((long)tq * siz.YTsiz), siz.YOsiz);
        long tx1 = Math.Min((long)siz.XTOsiz + ((tp + 1L) * siz.XTsiz), siz.Xsiz);
        long ty1 = Math.Min((long)siz.YTOsiz + ((tq + 1L) * siz.YTsiz), siz.Ysiz);

        JpxMarkerSet set = entry.Set;
        JpxMarkerSet main = cs.Main;
        JpxMarkerSet codSource = set.HasCod ? set : main;
        var comps = new JpxTileComp[siz.Csiz];
        long blockBudget = options.MaxCodeBlocksPerTile;
        long tagNodes = 0;
        for (int c = 0; c < comps.Length; c++)
        {
            JpxCodParams cod = set.Coc[c] ?? (set.HasCod ? set.Cod : null) ?? main.Coc[c] ?? main.Cod!;
            JpxQuant quant = set.Qcc[c] ?? set.Qcd ?? main.Qcc[c] ?? main.Qcd!;
            int roi = set.Rgn[c] >= 0 ? set.Rgn[c] : main.Rgn[c];
            if (cod.Levels < reduction)
            {
                throw new ImageFormatException("A tile has fewer decomposition levels than the requested reduction.");
            }

            comps[c] = JpxTileBuilder.Build(c, tx0, ty0, tx1, ty1, siz, cod, quant, roi, reduction, ref blockBudget, ref tagNodes);
        }

        // Memory for the coefficients of this tile.
        long bytes = 0;
        foreach (JpxTileComp tc in comps)
        {
            JpxResolution top = tc.Res[tc.KeptResolutions - 1];
            tc.BufferWidth = top.Width;
            tc.BufferHeight = top.Height;
            bytes += 4L * top.Width * top.Height;
        }

        if (bytes > options.MaxWorkingBytes)
        {
            throw new ImageLimitException("The tile's coefficient buffers exceed the working memory limit.");
        }

        // Packet data of all tile-parts, end to end.
        ReadOnlyMemory<byte> data = Join(cs, entry, false);
        ReadOnlyMemory<byte> headers = ReadOnlyMemory<byte>.Empty;
        bool packed = cs.PpmData.Length > 0 || entry.Set.Ppt.Count > 0;
        if (packed)
        {
            headers = Join(cs, entry, true);
        }

        var reader = new JpxPacketReader(comps, siz, data, headers, packed, codSource.Layers, (int)tx0, (int)ty0, (int)tx1, (int)ty1, tagNodes, ct);
        reader.ReadAll(codSource.Order, set.Poc.Count > 0 ? set.Poc : main.Poc);
        bool damaged = reader.Damaged || cs.Truncated;
        if (damaged && options.Strict)
        {
            throw new ImageFormatException("JPEG 2000 tile data is truncated or damaged.");
        }

        // Tier 1: entropy-decode every code-block into the coefficient buffers.
        var work = new List<(int Comp, int Res, int Band, int Block)>();
        long visits = 0;
        foreach (JpxTileComp tc in comps)
        {
            for (int r = 0; r < tc.KeptResolutions; r++)
            {
                JpxBand[] bands = tc.Res[r].Bands;
                for (int b = 0; b < bands.Length; b++)
                {
                    JpxCodeBlock[] blocks = bands[b].Blocks;
                    for (int i = 0; i < blocks.Length; i++)
                    {
                        if (blocks[i].FirstChunk >= 0)
                        {
                            work.Add((tc.Index, r, b, i));
                            visits += (long)blocks[i].Passes * blocks[i].Width * blocks[i].Height;
                        }
                    }
                }
            }
        }

        tier1Budget -= visits;
        if (tier1Budget < 0)
        {
            throw new ImageLimitException("JPEG 2000 coding-pass work exceeds the limit for the size of the data.");
        }

        // The budget is settled; only now allocate the coefficient buffers.
        foreach (JpxTileComp tc in comps)
        {
            if (tc.Cod.Reversible)
            {
                tc.IntCoeffs = new int[tc.BufferWidth * tc.BufferHeight];
            }
            else
            {
                tc.FloatCoeffs = new float[tc.BufferWidth * tc.BufferHeight];
            }
        }

        JpxChunk[] chunks = reader.Chunks;
        ReadOnlyMemory<byte> dataMem = data;
        int degree = Math.Max(1, options.MaxDegreeOfParallelism);
        if (degree == 1 || work.Count < 8)
        {
            var state = new BlockWorker();
            foreach ((int comp, int res, int band, int block) in work)
            {
                ct.ThrowIfCancellationRequested();
                state.Run(comps[comp], comps[comp].Res[res].Bands[band], block, chunks, dataMem.Span);
            }
        }
        else
        {
            Parallel.For(
                0,
                work.Count,
                new ParallelOptions { MaxDegreeOfParallelism = degree, CancellationToken = ct },
                () => new BlockWorker(),
                (i, _, state) =>
                {
                    (int comp, int res, int band, int block) = work[i];
                    state.Run(comps[comp], comps[comp].Res[res].Bands[band], block, chunks, dataMem.Span);
                    return state;
                },
                _ => { });
        }

        // Inverse wavelet transform.
        foreach (JpxTileComp tc in comps)
        {
            if (tc.IntCoeffs is not null)
            {
                JpxDwt.Inverse53(tc.IntCoeffs, tc.BufferWidth, tc.Res, tc.KeptResolutions, degree, ct);
            }
            else
            {
                JpxDwt.Inverse97(tc.FloatCoeffs!, tc.BufferWidth, tc.Res, tc.KeptResolutions, degree, ct);
            }
        }

        // Component transform (Annex G) over the first three components.
        if (codSource.Mct != 0)
        {
            ApplyMct(comps);
        }

        // Level shift, clamp and store.
        foreach (JpxTileComp tc in comps)
        {
            Store(tc, siz, planes[tc.Index]);
        }

        return damaged;
    }

    private static ReadOnlyMemory<byte> Join(JpxCodestream cs, JpxTileEntry entry, bool headers)
    {
        if (headers)
        {
            // Packed packet headers: PPM data per tile-part, or the PPT segments of each tile-part in index order.
            var list = new List<ReadOnlyMemory<byte>>();
            foreach (JpxTilePart part in entry.Parts)
            {
                if (cs.PpmData.Length > 0)
                {
                    if (part.PpmOffset >= 0)
                    {
                        list.Add(cs.PpmData.AsMemory(part.PpmOffset, part.PpmLength));
                    }
                }
                else
                {
                    var segs = entry.Set.Ppt.Skip(part.PptStart).Take(part.PptEnd - part.PptStart).OrderBy(p => p.Z);
                    foreach ((int _, int offset, int length) in segs)
                    {
                        list.Add(cs.Data.Slice(offset, length));
                    }
                }
            }

            return Concat(list);
        }

        if (entry.Parts.Count == 1)
        {
            return cs.Data.Slice(entry.Parts[0].DataOffset, entry.Parts[0].DataLength);
        }

        var all = new List<ReadOnlyMemory<byte>>(entry.Parts.Count);
        foreach (JpxTilePart part in entry.Parts)
        {
            all.Add(cs.Data.Slice(part.DataOffset, part.DataLength));
        }

        return Concat(all);
    }

    private static ReadOnlyMemory<byte> Concat(List<ReadOnlyMemory<byte>> parts)
    {
        if (parts.Count == 1)
        {
            return parts[0];
        }

        long total = 0;
        foreach (ReadOnlyMemory<byte> p in parts)
        {
            total += p.Length;
        }

        var buf = new byte[total];
        int at = 0;
        foreach (ReadOnlyMemory<byte> p in parts)
        {
            p.Span.CopyTo(buf.AsSpan(at));
            at += p.Length;
        }

        return buf;
    }

    // ---- component transform ----

    private static void ApplyMct(JpxTileComp[] comps)
    {
        if (comps.Length < 3)
        {
            throw new ImageFormatException("A component transform needs three components.");
        }

        JpxTileComp c0 = comps[0];
        JpxTileComp c1 = comps[1];
        JpxTileComp c2 = comps[2];
        if (c0.BufferWidth != c1.BufferWidth || c0.BufferWidth != c2.BufferWidth || c0.BufferHeight != c1.BufferHeight || c0.BufferHeight != c2.BufferHeight)
        {
            throw new ImageFormatException("Components of a component transform differ in size.");
        }

        int n = c0.BufferWidth * c0.BufferHeight;
        if (c0.IntCoeffs is not null && c1.IntCoeffs is not null && c2.IntCoeffs is not null)
        {
            int[] y0 = c0.IntCoeffs;
            int[] y1 = c1.IntCoeffs;
            int[] y2 = c2.IntCoeffs;
            for (int i = 0; i < n; i++)
            {
                // G.2.2: inverse reversible component transform.
                int g = y0[i] - ((y2[i] + y1[i]) >> 2);
                int r = y2[i] + g;
                int b = y1[i] + g;
                y0[i] = r;
                y1[i] = g;
                y2[i] = b;
            }
        }
        else if (c0.FloatCoeffs is not null && c1.FloatCoeffs is not null && c2.FloatCoeffs is not null)
        {
            float[] y0 = c0.FloatCoeffs;
            float[] y1 = c1.FloatCoeffs;
            float[] y2 = c2.FloatCoeffs;
            for (int i = 0; i < n; i++)
            {
                // G.3.2: inverse irreversible component transform.
                float y = y0[i];
                float cb = y1[i];
                float cr = y2[i];
                y0[i] = y + (1.402f * cr);
                y1[i] = y - (0.34413f * cb) - (0.71414f * cr);
                y2[i] = y + (1.772f * cb);
            }
        }
        else
        {
            throw new ImageFormatException("A component transform mixes reversible and irreversible components.");
        }
    }

    // ---- level shift and output ----

    private static void Store(JpxTileComp tc, JpxSiz siz, JpxPlane plane)
    {
        JpxResolution top = tc.Res[tc.KeptResolutions - 1];
        int x0 = Math.Max(top.X0, plane.X0);
        int x1 = Math.Min(top.X1, plane.X0 + plane.Width);
        int y0 = Math.Max(top.Y0, plane.Y0);
        int y1 = Math.Min(top.Y1, plane.Y0 + plane.Height);
        if (x1 <= x0 || y1 <= y0)
        {
            return;
        }

        int prec = siz.Precision[tc.Index];
        int max = (1 << prec) - 1;
        int shift = 1 << (prec - 1);
        int stride = tc.BufferWidth;
        for (int y = y0; y < y1; y++)
        {
            Span<ushort> dst = plane.Data.AsSpan(((y - plane.Y0) * plane.Width) + (x0 - plane.X0), x1 - x0);
            int srcRow = ((y - top.Y0) * stride) + (x0 - top.X0);
            if (tc.IntCoeffs is not null)
            {
                ReadOnlySpan<int> src = tc.IntCoeffs.AsSpan(srcRow, dst.Length);
                for (int i = 0; i < dst.Length; i++)
                {
                    // Unsigned samples were level shifted at the encoder; signed ones are stored offset to unsigned.
                    dst[i] = (ushort)Math.Clamp(src[i] + shift, 0, max);
                }
            }
            else
            {
                ReadOnlySpan<float> src = tc.FloatCoeffs.AsSpan(srcRow, dst.Length);
                for (int i = 0; i < dst.Length; i++)
                {
                    float v = Math.Clamp(src[i] + shift, 0f, max);
                    dst[i] = (ushort)(int)(v + 0.5f);
                }
            }
        }
    }

    // ---- tier-1 worker ----

    private sealed class BlockWorker
    {
        private readonly JpxBlockDecoder _decoder = new();
        private JpxSegment[] _segments = new JpxSegment[32];
        private int _segmentCount;
        private byte[] _data = new byte[4096];

        public void Run(JpxTileComp tc, JpxBand band, int blockIndex, JpxChunk[] chunks, ReadOnlySpan<byte> tileData)
        {
            ref JpxCodeBlock cb = ref band.Blocks[blockIndex];
            int total = 0;
            for (int ci = cb.FirstChunk; ci >= 0; ci = chunks[ci].Next)
            {
                total += chunks[ci].Length;
            }

            if (_data.Length < total + 8)
            {
                _data = new byte[Math.Max(_data.Length * 2, total + 8)];
            }

            _segmentCount = 0;
            int at = 0;
            int segStart = 0;
            int segPasses = 0;
            int curSeg = -1;
            for (int ci = cb.FirstChunk; ci >= 0; ci = chunks[ci].Next)
            {
                JpxChunk ch = chunks[ci];
                if (ch.Segment != curSeg)
                {
                    if (curSeg >= 0)
                    {
                        AddSegment(new JpxSegment(segStart, at - segStart, segPasses));
                    }

                    curSeg = ch.Segment;
                    segStart = at;
                    segPasses = 0;
                }

                tileData.Slice(ch.Offset, ch.Length).CopyTo(_data.AsSpan(at));
                at += ch.Length;
                segPasses += ch.Passes;
            }

            if (curSeg >= 0)
            {
                AddSegment(new JpxSegment(segStart, at - segStart, segPasses));
            }

            int bitPlanes = band.Mb + tc.RoiShift - cb.ZeroBitPlanes;
            _decoder.Decode(_data, _segments.AsSpan(0, _segmentCount), cb.Width, cb.Height, bitPlanes, band.Orient, tc.Cod.Style);
            Write(tc, band, ref cb);
        }

        private void AddSegment(JpxSegment seg)
        {
            if (_segmentCount == _segments.Length)
            {
                Array.Resize(ref _segments, _segments.Length * 2);
            }

            _segments[_segmentCount++] = seg;
        }

        private void Write(JpxTileComp tc, JpxBand band, ref JpxCodeBlock cb)
        {
            ReadOnlySpan<int> res = _decoder.Result;
            int w = cb.Width;
            int h = cb.Height;
            int stride = tc.BufferWidth;
            int dx = band.OffsetX + (cb.X0 - band.X0);
            int dy = band.OffsetY + (cb.Y0 - band.Y0);
            int roi = tc.RoiShift;
            if (tc.IntCoeffs is not null)
            {
                int[] dst = tc.IntCoeffs;
                for (int y = 0; y < h; y++)
                {
                    int o = ((dy + y) * stride) + dx;
                    for (int x = 0; x < w; x++)
                    {
                        int v = res[(y * w) + x];
                        if (v == 0)
                        {
                            continue;
                        }

                        int m = Math.Abs(v) >> 1;
                        if (roi > 0 && m >= (1 << roi))
                        {
                            m >>= roi;
                        }

                        dst[o + x] = v < 0 ? -m : m;
                    }
                }
            }
            else
            {
                float[] dst = tc.FloatCoeffs!;
                float scale = 0.5f * band.Step;
                for (int y = 0; y < h; y++)
                {
                    int o = ((dy + y) * stride) + dx;
                    for (int x = 0; x < w; x++)
                    {
                        int v = res[(y * w) + x];
                        if (v == 0)
                        {
                            continue;
                        }

                        if (roi > 0)
                        {
                            int m = Math.Abs(v);
                            if ((m >> 1) >= (1 << roi))
                            {
                                m >>= roi;
                            }

                            v = v < 0 ? -m : m;
                        }

                        dst[o + x] = v * scale;
                    }
                }
            }
        }
    }
}
