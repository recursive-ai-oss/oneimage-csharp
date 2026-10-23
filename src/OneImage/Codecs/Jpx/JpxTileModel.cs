namespace OneImage.Codecs.Jpx;

/// <summary>A code-block's tier-2 state and its place in a sub-band (T.800 B.7 and B.10).</summary>
internal struct JpxCodeBlock
{
    public int X0;
    public int Y0;
    public int Width;
    public int Height;

    /// <summary>Coding passes announced so far.</summary>
    public int Passes;

    /// <summary>Passes already in the open codeword segment.</summary>
    public int SegPasses;

    /// <summary>Index of the open codeword segment.</summary>
    public int Segment;

    /// <summary>The Lblock state variable (B.10.7.1).</summary>
    public int Lblock;

    /// <summary>Number of missing most significant bit-planes (B.10.5).</summary>
    public int ZeroBitPlanes;

    public bool Included;

    /// <summary>Head and tail of this block's chain in the tile's chunk list, or -1.</summary>
    public int FirstChunk;

    public int LastChunk;
}

/// <summary>One packet's contribution of bytes to a code-block's codeword segment.</summary>
internal struct JpxChunk
{
    public int Next;
    public int Offset;
    public int Length;
    public int Segment;
    public int Passes;
}

/// <summary>The code-blocks of one precinct in one sub-band, as a grid.</summary>
internal struct JpxPrecinctBand
{
    /// <summary>Index of the first block in <see cref="JpxBand.Blocks"/>; blocks are in raster order.</summary>
    public int First;

    public int GridWidth;
    public int GridHeight;

    /// <summary>Offset of the inclusion tag tree; the zero-bit-plane tree follows it.</summary>
    public int TagOffset;
}

/// <summary>A sub-band of a tile-component (T.800 B.5).</summary>
internal sealed class JpxBand
{
    public int Orient; // 0 LL, 1 HL, 2 LH, 3 HH
    public int X0;
    public int Y0;
    public int X1;
    public int Y1;
    public int Mb; // magnitude bit-planes: guard bits + exponent - 1
    public float Step; // reconstruction step (irreversible); 1 for reversible
    public int OffsetX; // where the band sits in the tile-component's coefficient buffer
    public int OffsetY;
    public JpxPrecinctBand[] Precincts = [];
    public JpxCodeBlock[] Blocks = [];
}

/// <summary>A resolution level of a tile-component.</summary>
internal sealed class JpxResolution
{
    public int X0;
    public int Y0;
    public int X1;
    public int Y1;
    public int PpX;
    public int PpY;
    public int PrecinctsWide;
    public int PrecinctsHigh;
    public JpxBand[] Bands = [];

    /// <summary>For each precinct, the number of layers whose packets were already read.</summary>
    public int[] LayersDone = [];

    public int Width => X1 - X0;

    public int Height => Y1 - Y0;

    public int PrecinctCount => PrecinctsWide * PrecinctsHigh;
}

/// <summary>A tile-component: its geometry, coding parameters and resolution levels.</summary>
internal sealed class JpxTileComp
{
    public int Index;
    public int X0;
    public int Y0;
    public int X1;
    public int Y1;
    public JpxCodParams Cod = null!;
    public JpxQuant Quant = null!;
    public int RoiShift;
    public int Precision;
    public JpxResolution[] Res = [];

    /// <summary>Resolutions that are decoded (the rest only have their packet headers read).</summary>
    public int KeptResolutions;

    public int[]? IntCoeffs;
    public float[]? FloatCoeffs;

    /// <summary>Stride and size of the coefficient buffer, the rectangle of the highest decoded resolution.</summary>
    public int BufferWidth;

    public int BufferHeight;
}

/// <summary>Builds the tile geometry of T.800 Annex B from the headers.</summary>
internal static class JpxTileBuilder
{
    public static long CeilShift(long v, int shift) => (v + (1L << shift) - 1) >> shift;

    public static long CeilDiv(long v, int d) => (v + d - 1) / d;

    /// <summary>Creates one tile-component with all resolutions, bands, precincts and code-blocks, checking the counts against the limits first.</summary>
    /// <param name="comp">Component index.</param>
    /// <param name="tx0">Tile rectangle on the reference grid.</param>
    /// <param name="ty0">Top of the tile.</param>
    /// <param name="tx1">Right edge.</param>
    /// <param name="ty1">Bottom edge.</param>
    /// <param name="siz">Image header.</param>
    /// <param name="cod">Coding style for this component in this tile.</param>
    /// <param name="quant">Quantization for this component in this tile.</param>
    /// <param name="roiShift">Region-of-interest shift or -1.</param>
    /// <param name="reduction">Resolution levels to drop.</param>
    /// <param name="blockBudget">Remaining code-block and precinct allowance for the tile; reduced by what this call creates.</param>
    /// <param name="tagNodes">Running total of tag tree nodes (inclusion and zero-bit-plane trees).</param>
    public static JpxTileComp Build(int comp, long tx0, long ty0, long tx1, long ty1, JpxSiz siz, JpxCodParams cod, JpxQuant quant, int roiShift, int reduction, ref long blockBudget, ref long tagNodes)
    {
        int nl = cod.Levels;
        var tc = new JpxTileComp
        {
            Index = comp,
            X0 = (int)CeilDiv(tx0, siz.XR[comp]),
            Y0 = (int)CeilDiv(ty0, siz.YR[comp]),
            X1 = (int)CeilDiv(tx1, siz.XR[comp]),
            Y1 = (int)CeilDiv(ty1, siz.YR[comp]),
            Cod = cod,
            Quant = quant,
            RoiShift = roiShift < 0 ? 0 : roiShift,
            Precision = siz.Precision[comp],
            KeptResolutions = nl - reduction + 1,
            Res = new JpxResolution[nl + 1],
        };

        int guard = quant.Guard;
        for (int r = 0; r <= nl; r++)
        {
            int sh = nl - r;
            var res = new JpxResolution
            {
                X0 = (int)CeilShift(tc.X0, sh),
                Y0 = (int)CeilShift(tc.Y0, sh),
                X1 = (int)CeilShift(tc.X1, sh),
                Y1 = (int)CeilShift(tc.Y1, sh),
                PpX = cod.PpX[r],
                PpY = cod.PpY[r],
            };

            if (res.X1 > res.X0 && res.Y1 > res.Y0)
            {
                res.PrecinctsWide = (int)(CeilShift(res.X1, res.PpX) - (res.X0 >> res.PpX));
                res.PrecinctsHigh = (int)(CeilShift(res.Y1, res.PpY) - (res.Y0 >> res.PpY));
            }

            long precincts = (long)res.PrecinctsWide * res.PrecinctsHigh;
            blockBudget -= precincts;
            if (blockBudget < 0)
            {
                throw new ImageLimitException("JPEG 2000 tile declares too many precincts.");
            }

            res.LayersDone = new int[precincts];
            tc.Res[r] = res;

            int nbands = r == 0 ? 1 : 3;
            res.Bands = new JpxBand[nbands];
            for (int bi = 0; bi < nbands; bi++)
            {
                int orient = r == 0 ? 0 : bi + 1;
                int nb = r == 0 ? nl : nl - r + 1;
                int xob = orient is 1 or 3 ? 1 : 0;
                int yob = orient is 2 or 3 ? 1 : 0;
                var band = new JpxBand { Orient = orient };
                if (r > 0)
                {
                    JpxResolution below = tc.Res[r - 1];
                    band.OffsetX = xob != 0 ? below.Width : 0;
                    band.OffsetY = yob != 0 ? below.Height : 0;
                }

                if (r == 0)
                {
                    band.X0 = res.X0;
                    band.Y0 = res.Y0;
                    band.X1 = res.X1;
                    band.Y1 = res.Y1;
                }
                else
                {
                    long half = 1L << (nb - 1);
                    band.X0 = (int)CeilShift(tc.X0 - half * xob, nb);
                    band.X1 = (int)CeilShift(tc.X1 - half * xob, nb);
                    band.Y0 = (int)CeilShift(tc.Y0 - half * yob, nb);
                    band.Y1 = (int)CeilShift(tc.Y1 - half * yob, nb);
                }

                SetQuant(tc, band, r, orient, quant, nl);
                res.Bands[bi] = band;
                CountBlocks(res, band, r, cod, ref blockBudget, ref tagNodes);
            }
        }

        return tc;
    }

    private static void SetQuant(JpxTileComp tc, JpxBand band, int r, int orient, JpxQuant q, int nl)
    {
        int idx = r == 0 ? 0 : 3 * (r - 1) + orient;
        int exp;
        int mant;
        if (q.Style == 1)
        {
            // E.5: derived quantization, one (exponent, mantissa) pair for the lowest band.
            exp = q.Exp[0] - (r == 0 ? 0 : r - 1);
            mant = q.Mant[0];
        }
        else
        {
            if (idx >= q.Exp.Length)
            {
                throw new ImageFormatException("The quantization marker lists too few sub-bands.");
            }

            exp = q.Exp[idx];
            mant = q.Mant[idx];
        }

        if (exp < 0)
        {
            throw new ImageFormatException("Invalid quantization exponent.");
        }

        band.Mb = q.Guard + exp - 1;
        if (tc.Cod.Reversible)
        {
            band.Step = 1f;
        }
        else if (q.Style == 0)
        {
            band.Step = 1f;
        }
        else
        {
            int gain = orient == 0 ? 0 : orient == 3 ? 2 : 1;
            int rb = tc.Precision + gain;
            band.Step = (float)(Math.Pow(2, rb - exp) * (1.0 + mant / 2048.0));
        }
    }

    // Lays out the code-blocks of every precinct of the band. The counting pass sizes the arrays, the second fills them.
    private static void CountBlocks(JpxResolution res, JpxBand band, int r, JpxCodParams cod, ref long blockBudget, ref long tagNodes)
    {
        int ppx = res.PpX;
        int ppy = res.PpY;
        int cellShiftX = r == 0 ? ppx : ppx - 1;
        int cellShiftY = r == 0 ? ppy : ppy - 1;
        int xcb = Math.Min(cod.XcbExp, cellShiftX);
        int ycb = Math.Min(cod.YcbExp, cellShiftY);
        int pw = res.PrecinctsWide;
        int ph = res.PrecinctsHigh;
        var precincts = new JpxPrecinctBand[(long)pw * ph];
        long total = 0;
        long nodes = 0;

        for (int pj = 0; pj < ph; pj++)
        {
            for (int pi = 0; pi < pw; pi++)
            {
                BlockGrid(res, band, pi, pj, cellShiftX, cellShiftY, xcb, ycb, out _, out _, out int gw, out int gh);
                total += (long)gw * gh;
                if (gw > 0 && gh > 0)
                {
                    nodes += 2 * TagNodeCount(gw, gh);
                }

                if (total > blockBudget)
                {
                    throw new ImageLimitException("JPEG 2000 tile declares too many code-blocks.");
                }
            }
        }

        blockBudget -= total;
        long baseOffset = tagNodes;
        tagNodes += nodes;
        band.Blocks = new JpxCodeBlock[total];
        int first = 0;
        long tagOffset = baseOffset;
        for (int pj = 0; pj < ph; pj++)
        {
            for (int pi = 0; pi < pw; pi++)
            {
                BlockGrid(res, band, pi, pj, cellShiftX, cellShiftY, xcb, ycb, out int gx0, out int gy0, out int gw, out int gh);
                ref JpxPrecinctBand pb = ref precincts[(long)pj * pw + pi];
                pb.First = first;
                pb.GridWidth = gw;
                pb.GridHeight = gh;
                pb.TagOffset = (int)tagOffset;
                if (gw > 0 && gh > 0)
                {
                    GetPrecinctArea(res, band, pi, pj, cellShiftX, cellShiftY, out long ax0, out long ay0, out long ax1, out long ay1);
                    int cbw = 1 << xcb;
                    int cbh = 1 << ycb;
                    for (int gy = 0; gy < gh; gy++)
                    {
                        for (int gx = 0; gx < gw; gx++)
                        {
                            long bx0 = Math.Max(ax0, (long)(gx0 + gx) * cbw);
                            long by0 = Math.Max(ay0, (long)(gy0 + gy) * cbh);
                            long bx1 = Math.Min(ax1, (long)(gx0 + gx + 1) * cbw);
                            long by1 = Math.Min(ay1, (long)(gy0 + gy + 1) * cbh);
                            band.Blocks[first++] = new JpxCodeBlock
                            {
                                X0 = (int)bx0,
                                Y0 = (int)by0,
                                Width = (int)(bx1 - bx0),
                                Height = (int)(by1 - by0),
                                Lblock = 3,
                                FirstChunk = -1,
                                LastChunk = -1,
                            };
                        }
                    }

                    tagOffset += 2 * TagNodeCount(gw, gh);
                }
            }
        }

        band.Precincts = precincts;
    }

    private static void GetPrecinctArea(JpxResolution res, JpxBand band, int pi, int pj, int shiftX, int shiftY, out long ax0, out long ay0, out long ax1, out long ay1)
    {
        long cellX = ((long)(res.X0 >> res.PpX) + pi) << shiftX;
        long cellY = ((long)(res.Y0 >> res.PpY) + pj) << shiftY;
        ax0 = Math.Max(cellX, band.X0);
        ay0 = Math.Max(cellY, band.Y0);
        ax1 = Math.Min(cellX + (1L << shiftX), band.X1);
        ay1 = Math.Min(cellY + (1L << shiftY), band.Y1);
    }

    private static void BlockGrid(JpxResolution res, JpxBand band, int pi, int pj, int shiftX, int shiftY, int xcb, int ycb, out int gx0, out int gy0, out int gw, out int gh)
    {
        GetPrecinctArea(res, band, pi, pj, shiftX, shiftY, out long ax0, out long ay0, out long ax1, out long ay1);
        if (ax1 <= ax0 || ay1 <= ay0)
        {
            gx0 = gy0 = gw = gh = 0;
            return;
        }

        gx0 = (int)(ax0 >> xcb);
        gy0 = (int)(ay0 >> ycb);
        gw = (int)(CeilShift(ax1, xcb) - gx0);
        gh = (int)(CeilShift(ay1, ycb) - gy0);
    }

    /// <summary>Total node count of a tag tree over a <paramref name="w"/> by <paramref name="h"/> grid.</summary>
    public static long TagNodeCount(int w, int h)
    {
        if (w <= 0 || h <= 0)
        {
            return 0;
        }

        long n = 0;
        while (true)
        {
            n += (long)w * h;
            if (w == 1 && h == 1)
            {
                return n;
            }

            w = (w + 1) >> 1;
            h = (h + 1) >> 1;
        }
    }
}
