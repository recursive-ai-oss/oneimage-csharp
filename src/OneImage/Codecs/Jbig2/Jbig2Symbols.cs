using System.Numerics;

namespace OneImage.Codecs.Jbig2;

/// <summary>The parameters of the text region decoding procedure (T.88 6.4.2).</summary>
internal sealed class Jbig2TextParameters
{
    /// <summary>SBHUFF.</summary>
    public bool Huffman { get; init; }

    /// <summary>SBREFINE.</summary>
    public bool Refine { get; init; }

    /// <summary>SBW.</summary>
    public int Width { get; init; }

    /// <summary>SBH.</summary>
    public int Height { get; init; }

    /// <summary>SBNUMINSTANCES.</summary>
    public long InstanceCount { get; init; }

    /// <summary>LOGSBSTRIPS; SBSTRIPS is 1 shifted left by it.</summary>
    public int LogStrips { get; init; }

    /// <summary>SBSYMS.</summary>
    public required IReadOnlyList<Jbig2Bitmap> Symbols { get; init; }

    /// <summary>SBSYMCODELEN: the length of IAID codes, or of fixed-length Huffman codes when <see cref="SymbolCodes"/> is null.</summary>
    public int SymbolCodeLength { get; init; }

    /// <summary>SBSYMCODES for the Huffman variant, or null for fixed-length codes.</summary>
    public Jbig2HuffmanTable? SymbolCodes { get; init; }

    /// <summary>SBDEFPIXEL.</summary>
    public int DefaultPixel { get; init; }

    /// <summary>SBCOMBOP.</summary>
    public Jbig2CombinationOperator CombinationOperator { get; init; }

    /// <summary>TRANSPOSED.</summary>
    public bool Transposed { get; init; }

    /// <summary>REFCORNER: 0 bottom left, 1 top left, 2 bottom right, 3 top right.</summary>
    public int ReferenceCorner { get; init; }

    /// <summary>SBDSOFFSET.</summary>
    public int DeltaSOffset { get; init; }

    /// <summary>SBHUFFFS.</summary>
    public Jbig2HuffmanTable? HuffFs { get; init; }

    /// <summary>SBHUFFDS.</summary>
    public Jbig2HuffmanTable? HuffDs { get; init; }

    /// <summary>SBHUFFDT.</summary>
    public Jbig2HuffmanTable? HuffDt { get; init; }

    /// <summary>SBHUFFRDW.</summary>
    public Jbig2HuffmanTable? HuffRdw { get; init; }

    /// <summary>SBHUFFRDH.</summary>
    public Jbig2HuffmanTable? HuffRdh { get; init; }

    /// <summary>SBHUFFRDX.</summary>
    public Jbig2HuffmanTable? HuffRdx { get; init; }

    /// <summary>SBHUFFRDY.</summary>
    public Jbig2HuffmanTable? HuffRdy { get; init; }

    /// <summary>SBHUFFRSIZE.</summary>
    public Jbig2HuffmanTable? HuffRsize { get; init; }

    /// <summary>SBRTEMPLATE.</summary>
    public int RefinementTemplate { get; init; }

    /// <summary>SBRATX1, SBRATY1, SBRATX2, SBRATY2.</summary>
    public sbyte[] RefinementAt { get; init; } = [-1, -1, -1, -1];
}

/// <summary>The text region decoding procedure (T.88 6.4).</summary>
internal static class Jbig2TextRegion
{
    /// <summary>Returns ceil(log2(n)), 0 for n of 1 or less.</summary>
    public static int CeilLog2(long n) => n <= 1 ? 0 : 64 - BitOperations.LeadingZeroCount((ulong)(n - 1));

    /// <summary>
    /// Decodes a text region: a number of symbol instances placed in strips and drawn into a bitmap.
    /// </summary>
    /// <param name="work">Limits and cancellation.</param>
    /// <param name="p">The parameters.</param>
    /// <param name="mq">The arithmetic decoder; used when <see cref="Jbig2TextParameters.Huffman"/> is false.</param>
    /// <param name="bits">The bit reader; used when it is true.</param>
    /// <param name="ints">The integer contexts (arithmetic variant).</param>
    /// <param name="grCx">The refinement contexts (GR), 8192 states.</param>
    public static Jbig2Bitmap Decode(Jbig2Work work, Jbig2TextParameters p, Jbig2ArithmeticDecoder? mq, Jbig2BitReader? bits, Jbig2IntegerContexts? ints, byte[] grCx)
    {
        work.ChargePixels(p.Width, p.Height);
        var region = new Jbig2Bitmap(p.Width, p.Height);
        if (p.DefaultPixel != 0)
        {
            region.Fill(1);
        }

        int strips = 1 << p.LogStrips;
        bool huff = p.Huffman;
        long stripT = -(long)Number(p, p.HuffDt, ints?.Dt, mq, bits) * strips;
        long firstS = 0;
        long n = 0;
        int corner = p.ReferenceCorner;
        bool right = corner is 2 or 3;
        bool bottom = corner is 0 or 2;

        while (n < p.InstanceCount)
        {
            work.Cancellation.ThrowIfCancellationRequested();
            stripT += (long)Number(p, p.HuffDt, ints?.Dt, mq, bits) * strips;
            bool first = true;
            long curS = 0;
            while (true)
            {
                if (first)
                {
                    firstS += Number(p, p.HuffFs, ints?.Fs, mq, bits);
                    curS = firstS;
                    first = false;
                }
                else
                {
                    int ids;
                    bool ok = huff ? p.HuffDs!.Decode(bits!, out ids) : ints!.Ds.Decode(mq!, out ids);
                    if (!ok)
                    {
                        break;
                    }

                    curS += (long)ids + p.DeltaSOffset;
                }

                if (n >= p.InstanceCount)
                {
                    throw new ImageFormatException("A JBIG2 text region has more symbol instances than it declares.");
                }

                int curT = 0;
                if (strips > 1)
                {
                    curT = huff ? (int)bits!.ReadBits(p.LogStrips) : Number(p, null, ints!.It, mq, bits);
                }

                long ti = stripT + curT;
                int id;
                if (huff)
                {
                    if (p.SymbolCodes is not null)
                    {
                        if (!p.SymbolCodes.Decode(bits!, out id))
                        {
                            throw new ImageFormatException("Invalid JBIG2 symbol code.");
                        }
                    }
                    else
                    {
                        id = (int)bits!.ReadBits(p.SymbolCodeLength);
                    }
                }
                else
                {
                    id = ints!.Id.Decode(mq!);
                }

                if ((uint)id >= (uint)p.Symbols.Count)
                {
                    throw new ImageFormatException("A JBIG2 text region refers to a symbol that does not exist.");
                }

                Jbig2Bitmap symbol = p.Symbols[id];
                if (p.Refine)
                {
                    int ri = huff ? bits!.ReadBit() : Number(p, null, ints!.Ri, mq, bits);
                    if (ri != 0)
                    {
                        symbol = RefineInstance(work, p, mq, bits, ints, grCx, symbol);
                    }
                }

                int wi = symbol.Width;
                int hi = symbol.Height;
                if (!p.Transposed && right)
                {
                    curS += wi - 1;
                }
                else if (p.Transposed && bottom)
                {
                    curS += hi - 1;
                }

                long si = curS;
                long x;
                long y;
                if (!p.Transposed)
                {
                    x = right ? si - wi + 1 : si;
                    y = bottom ? ti - hi + 1 : ti;
                }
                else
                {
                    x = right ? ti - wi + 1 : ti;
                    y = bottom ? si - hi + 1 : si;
                }

                region.Compose(symbol, x, y, p.CombinationOperator);
                if (!p.Transposed && !right)
                {
                    curS += wi - 1;
                }
                else if (p.Transposed && !bottom)
                {
                    curS += hi - 1;
                }

                n++;
                work.ChargeInstances(1);
            }
        }

        return region;
    }

    private static int Number(Jbig2TextParameters p, Jbig2HuffmanTable? table, Jbig2IntegerDecoder? dec, Jbig2ArithmeticDecoder? mq, Jbig2BitReader? bits)
    {
        int v;
        bool ok = p.Huffman ? table!.Decode(bits!, out v) : dec!.Decode(mq!, out v);
        if (!ok)
        {
            throw new ImageFormatException("An out-of-band value appears where a JBIG2 text region needs a number.");
        }

        return v;
    }

    private static Jbig2Bitmap RefineInstance(Jbig2Work work, Jbig2TextParameters p, Jbig2ArithmeticDecoder? mq, Jbig2BitReader? bits, Jbig2IntegerContexts? ints, byte[] grCx, Jbig2Bitmap original)
    {
        int rdw = Number(p, p.HuffRdw, ints?.Rdw, mq, bits);
        int rdh = Number(p, p.HuffRdh, ints?.Rdh, mq, bits);
        int rdx = Number(p, p.HuffRdx, ints?.Rdx, mq, bits);
        int rdy = Number(p, p.HuffRdy, ints?.Rdy, mq, bits);
        long w = (long)original.Width + rdw;
        long h = (long)original.Height + rdh;
        work.CheckSymbol(w, h);
        int dx = (rdw >> 1) + rdx;
        int dy = (rdh >> 1) + rdy;
        if (!p.Huffman)
        {
            return Jbig2Generic.DecodeRefinement(work, mq!, grCx, (int)w, (int)h, p.RefinementTemplate, original, dx, dy, false, p.RefinementAt);
        }

        int size = Number(p, p.HuffRsize, null, mq, bits);
        bits!.Align();
        int start = bits.BytePosition;
        bits.SkipBytes(size);
        var local = new Jbig2ArithmeticDecoder(bits.Data, start, start + size);
        return Jbig2Generic.DecodeRefinement(work, local, grCx, (int)w, (int)h, p.RefinementTemplate, original, dx, dy, false, p.RefinementAt);
    }
}

/// <summary>The parameters of the symbol dictionary decoding procedure (T.88 6.5.2).</summary>
internal sealed class Jbig2SymbolDictionaryParameters
{
    /// <summary>SDHUFF.</summary>
    public bool Huffman { get; init; }

    /// <summary>SDREFAGG.</summary>
    public bool RefineAggregate { get; init; }

    /// <summary>SDINSYMS.</summary>
    public required IReadOnlyList<Jbig2Bitmap> InputSymbols { get; init; }

    /// <summary>SDNUMNEWSYMS.</summary>
    public int NewSymbolCount { get; init; }

    /// <summary>SDNUMEXSYMS.</summary>
    public int ExportCount { get; init; }

    /// <summary>SDHUFFDH.</summary>
    public Jbig2HuffmanTable? HuffDh { get; init; }

    /// <summary>SDHUFFDW.</summary>
    public Jbig2HuffmanTable? HuffDw { get; init; }

    /// <summary>SDHUFFBMSIZE.</summary>
    public Jbig2HuffmanTable? HuffBitmapSize { get; init; }

    /// <summary>SDHUFFAGGINST.</summary>
    public Jbig2HuffmanTable? HuffAggregateInstances { get; init; }

    /// <summary>SDTEMPLATE.</summary>
    public int Template { get; init; }

    /// <summary>SDATX1, SDATY1, ... (eight values).</summary>
    public int[] At { get; init; } = [];

    /// <summary>SDRTEMPLATE.</summary>
    public int RefinementTemplate { get; init; }

    /// <summary>SDRATX1, SDRATY1, SDRATX2, SDRATY2.</summary>
    public sbyte[] RefinementAt { get; init; } = [-1, -1, -1, -1];
}

/// <summary>The symbol dictionary decoding procedure (T.88 6.5).</summary>
internal static class Jbig2SymbolDictionary
{
    /// <summary>
    /// Decodes a symbol dictionary's data and returns the exported symbols.
    /// </summary>
    /// <param name="work">Limits and cancellation.</param>
    /// <param name="p">The parameters.</param>
    /// <param name="data">The array holding the segment data.</param>
    /// <param name="start">First byte of the coded data (after the segment header fields).</param>
    /// <param name="end">End of the segment data.</param>
    /// <param name="gbCx">The generic region contexts (GB): in, the starting statistics; out, the final ones. Unused by the Huffman variant.</param>
    /// <param name="grCx">The generic refinement contexts (GR), likewise.</param>
    public static Jbig2Bitmap[] Decode(Jbig2Work work, Jbig2SymbolDictionaryParameters p, byte[] data, int start, int end, byte[] gbCx, byte[] grCx)
    {
        Jbig2Limits lim = work.Jbig2;
        if (p.NewSymbolCount > lim.MaxSymbolsPerDictionary || p.ExportCount > lim.MaxSymbolsPerDictionary)
        {
            throw new ImageLimitException("A JBIG2 symbol dictionary declares more symbols than the limit allows.");
        }

        int numIn = p.InputSymbols.Count;
        long total = (long)numIn + p.NewSymbolCount;
        if (total > lim.MaxSymbolsPerDictionary)
        {
            throw new ImageLimitException("A JBIG2 symbol dictionary with its imports holds more symbols than the limit allows.");
        }

        bool huff = p.Huffman;
        bool refAgg = p.RefineAggregate;
        int codeLength = Jbig2TextRegion.CeilLog2(total);
        if (huff)
        {
            codeLength = Math.Max(codeLength, 1);
        }

        Jbig2ArithmeticDecoder? mq = null;
        Jbig2BitReader? bits = null;
        Jbig2IntegerContexts? ints = null;
        if (huff)
        {
            bits = new Jbig2BitReader(data, start, end);
        }
        else
        {
            mq = new Jbig2ArithmeticDecoder(data, start, end);
            ints = new Jbig2IntegerContexts(codeLength);
        }

        // SBSYMS while decoding: the imported symbols, then the new symbols decoded so far.
        var all = new List<Jbig2Bitmap>(Math.Min((int)total, 1 << 14));
        all.AddRange(p.InputSymbols);
        var widths = new List<int>();
        long hcHeight = 0;
        int decoded = 0;
        int emptyClasses = 0;
        while (decoded < p.NewSymbolCount)
        {
            if (emptyClasses > (1 << 16))
            {
                throw new ImageFormatException("A JBIG2 symbol dictionary has too many empty height classes.");
            }

            int before = decoded;
            work.Cancellation.ThrowIfCancellationRequested();
            int hcdh = DecodeNumber(huff, p.HuffDh, ints?.Dh, mq, bits);
            hcHeight += hcdh;
            if (hcHeight < 0 || hcHeight > work.Limits.MaxDimension)
            {
                throw new ImageFormatException("A JBIG2 symbol dictionary has a height class of invalid height.");
            }

            long symWidth = 0;
            long totWidth = 0;
            int hcFirst = decoded;
            while (true)
            {
                int dw;
                bool ok = huff ? p.HuffDw!.Decode(bits!, out dw) : ints!.Dw.Decode(mq!, out dw);
                if (!ok)
                {
                    break;
                }

                if (decoded >= p.NewSymbolCount)
                {
                    throw new ImageFormatException("A JBIG2 symbol dictionary defines more symbols than it declares.");
                }

                symWidth += dw;
                totWidth += symWidth;
                work.CheckSymbol(symWidth, hcHeight);
                if (!huff || refAgg)
                {
                    Jbig2Bitmap bs = refAgg
                        ? DecodeRefinedSymbol(work, p, mq, bits, ints, grCx, all, (int)symWidth, (int)hcHeight, codeLength)
                        : Jbig2Generic.DecodeArithmetic(work, mq!, gbCx, (int)symWidth, (int)hcHeight, p.Template, false, null, p.At);
                    work.ChargeRetained((long)bs.Stride * bs.Height);
                    all.Add(bs);
                }
                else
                {
                    widths.Add((int)symWidth);
                }

                decoded++;
            }

            emptyClasses = decoded == before ? emptyClasses + 1 : 0;
            if (huff && !refAgg)
            {
                // 6.5.9: the symbols of the height class, side by side in one MMR-coded (or raw) bitmap.
                if (totWidth > work.Limits.MaxDimension * 16L)
                {
                    throw new ImageLimitException("A JBIG2 height class collective bitmap is too wide.");
                }

                work.CheckRegion(totWidth, hcHeight);
                int size = Next(p.HuffBitmapSize!, bits!);
                bits!.Align();
                int pos = bits.BytePosition;
                Jbig2Bitmap collective;
                if (size == 0)
                {
                    work.ChargePixels(totWidth, hcHeight);
                    collective = new Jbig2Bitmap((int)totWidth, (int)hcHeight);
                    int rowBytes = collective.Stride;
                    bits.SkipBytes(rowBytes * (int)hcHeight);
                    Array.Copy(data, pos, collective.Data, 0, rowBytes * (int)hcHeight);
                }
                else
                {
                    bits.SkipBytes(size);
                    collective = Jbig2Generic.DecodeMmr(work, data, pos, pos + size, (int)totWidth, (int)hcHeight, out _);
                }

                int x = 0;
                for (int i = hcFirst; i < decoded; i++)
                {
                    int w = widths[i];
                    Jbig2Bitmap sym = collective.Extract(x, 0, w, (int)hcHeight);
                    work.ChargeRetained((long)sym.Stride * sym.Height);
                    all.Add(sym);
                    x += w;
                }
            }
        }

        // 6.5.10: the export flags, as alternating runs of "not exported" and "exported".
        var exported = new List<Jbig2Bitmap>();
        long index = 0;
        int flag = 0;
        long runs = 0;
        while (index < total)
        {
            if (++runs > (2 * total) + 4)
            {
                throw new ImageFormatException("A JBIG2 symbol dictionary has an export list that does not end.");
            }

            int run = DecodeNumber(huff, Jbig2HuffmanTable.Standard(1), ints?.Ex, mq, bits);
            if (run < 0 || index + run > total)
            {
                throw new ImageFormatException("A JBIG2 symbol dictionary export run is out of range.");
            }

            if (flag == 1)
            {
                for (long i = index; i < index + run; i++)
                {
                    exported.Add(all[(int)i]);
                }
            }

            index += run;
            flag ^= 1;
        }

        return [.. exported];
    }

    private static int Next(Jbig2HuffmanTable table, Jbig2BitReader bits)
    {
        if (!table.Decode(bits, out int v))
        {
            throw new ImageFormatException("An out-of-band value appears where a JBIG2 symbol dictionary needs a number.");
        }

        return v;
    }

    private static int DecodeNumber(bool huff, Jbig2HuffmanTable? table, Jbig2IntegerDecoder? dec, Jbig2ArithmeticDecoder? mq, Jbig2BitReader? bits)
    {
        int v;
        bool ok = huff ? table!.Decode(bits!, out v) : dec!.Decode(mq!, out v);
        if (!ok)
        {
            throw new ImageFormatException("An out-of-band value appears where a JBIG2 symbol dictionary needs a number.");
        }

        return v;
    }

    // 6.5.8.2: a symbol coded by refining another symbol (one instance) or by drawing several symbols (an aggregation).
    private static Jbig2Bitmap DecodeRefinedSymbol(Jbig2Work work, Jbig2SymbolDictionaryParameters p, Jbig2ArithmeticDecoder? mq, Jbig2BitReader? bits, Jbig2IntegerContexts? ints, byte[] grCx, List<Jbig2Bitmap> all, int width, int height, int codeLength)
    {
        bool huff = p.Huffman;
        int instances = DecodeNumber(huff, p.HuffAggregateInstances, ints?.Ai, mq, bits);
        if (instances < 1)
        {
            throw new ImageFormatException("A JBIG2 symbol aggregation has no symbol instances.");
        }

        work.ChargeInstances(instances);
        if (instances > 1)
        {
            var tp = new Jbig2TextParameters
            {
                Huffman = huff,
                Refine = true,
                Width = width,
                Height = height,
                InstanceCount = instances,
                LogStrips = 0,
                Symbols = all,
                SymbolCodeLength = codeLength,
                CombinationOperator = Jbig2CombinationOperator.Or,
                ReferenceCorner = 1,
                HuffFs = huff ? Jbig2HuffmanTable.Standard(6) : null,
                HuffDs = huff ? Jbig2HuffmanTable.Standard(8) : null,
                HuffDt = huff ? Jbig2HuffmanTable.Standard(11) : null,
                HuffRdw = huff ? Jbig2HuffmanTable.Standard(15) : null,
                HuffRdh = huff ? Jbig2HuffmanTable.Standard(15) : null,
                HuffRdx = huff ? Jbig2HuffmanTable.Standard(15) : null,
                HuffRdy = huff ? Jbig2HuffmanTable.Standard(15) : null,
                HuffRsize = huff ? Jbig2HuffmanTable.Standard(1) : null,
                RefinementTemplate = p.RefinementTemplate,
                RefinementAt = p.RefinementAt,
            };
            return Jbig2TextRegion.Decode(work, tp, mq, bits, ints, grCx);
        }

        int id = huff ? (int)bits!.ReadBits(codeLength) : ints!.Id.Decode(mq!);
        if ((uint)id >= (uint)all.Count)
        {
            throw new ImageFormatException("A JBIG2 symbol refinement refers to a symbol that does not exist yet.");
        }

        int rdx = DecodeNumber(huff, huff ? Jbig2HuffmanTable.Standard(15) : null, ints?.Rdx, mq, bits);
        int rdy = DecodeNumber(huff, huff ? Jbig2HuffmanTable.Standard(15) : null, ints?.Rdy, mq, bits);
        Jbig2ArithmeticDecoder refinementDecoder;
        if (huff)
        {
            int size = Next(Jbig2HuffmanTable.Standard(1), bits!);
            bits!.Align();
            int pos = bits.BytePosition;
            bits.SkipBytes(size);
            refinementDecoder = new Jbig2ArithmeticDecoder(bits.Data, pos, pos + size);
        }
        else
        {
            refinementDecoder = mq!;
        }

        return Jbig2Generic.DecodeRefinement(work, refinementDecoder, grCx, width, height, p.RefinementTemplate, all[id], rdx, rdy, false, p.RefinementAt);
    }
}
