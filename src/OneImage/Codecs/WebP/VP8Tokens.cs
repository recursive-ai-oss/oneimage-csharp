namespace OneImage.Codecs.WebP;

/// <summary>Receives the symbols of the coefficient token stream: either probability-table bits or fixed-probability bits.</summary>
internal interface ITokenSink
{
    /// <summary>Codes <paramref name="bit"/> with the adaptive probability at <paramref name="index"/> of the coefficient table.</summary>
    void Bit(int index, int bit);

    /// <summary>Codes <paramref name="bit"/> with a fixed probability (extra bits and signs).</summary>
    void Fixed(int prob, int bit);
}

/// <summary>Adds up the cost of tokens in 1/256 bit units for a given probability table.</summary>
internal struct CostSink : ITokenSink
{
    private readonly byte[] _probs;

    public CostSink(byte[] probs)
    {
        _probs = probs;
        Cost = 0;
    }

    public int Cost;

    public void Bit(int index, int bit) => Cost += VP8Fwd.BitCost(bit, _probs[index]);

    public void Fixed(int prob, int bit) => Cost += VP8Fwd.BitCost(bit, prob);
}

/// <summary>Counts how often each table bit is a one, to derive better probabilities.</summary>
internal struct StatsSink : ITokenSink
{
    public int[] Ones;
    public int[] Total;

    public void Bit(int index, int bit)
    {
        Total[index]++;
        Ones[index] += bit;
    }

    public void Fixed(int prob, int bit)
    {
    }
}

/// <summary>Counts table bits like <see cref="StatsSink"/>, into one pair of arrays for every macroblock and another for the ones that are coded (not skipped).</summary>
internal readonly struct DualStatsSink(int[] ones, int[] total, int[] onesCoded, int[] totalCoded, bool coded) : ITokenSink
{
    public void Bit(int index, int bit)
    {
        total[index]++;
        ones[index] += bit;
        if (coded)
        {
            totalCoded[index]++;
            onesCoded[index] += bit;
        }
    }

    public void Fixed(int prob, int bit)
    {
    }
}

/// <summary>Writes tokens with the given probability table.</summary>
internal struct WriterSink : ITokenSink
{
    private readonly byte[] _probs;
    private readonly VP8BoolWriter _bw;

    public WriterSink(byte[] probs, VP8BoolWriter bw)
    {
        _probs = probs;
        _bw = bw;
    }

    public void Bit(int index, int bit) => _bw.PutBit(bit, _probs[index]);

    public void Fixed(int prob, int bit) => _bw.PutBit(bit, prob);
}

/// <summary>Token stream of one 4x4 block (RFC 6386 section 13), mirroring the decoder's coefficient reader.</summary>
internal static class VP8Tokens
{
    private static readonly byte[][] CatProbs =
    [
        [173, 148, 140],
        [176, 155, 140, 135],
        [180, 157, 141, 134, 130],
        [254, 254, 243, 230, 196, 177, 153, 140, 133, 130, 129],
    ];

    /// <summary>Emits the tokens of one nonzero level whose "not zero" decision lives at probability index <paramref name="p"/> + 1, then its sign.</summary>
    public static void EmitLevel<TSink>(ref TSink s, int p, int a, bool negative)
        where TSink : struct, ITokenSink
    {
        s.Bit(p + 1, 1);
        if (a == 1)
        {
            s.Bit(p + 2, 0);
        }
        else
        {
            s.Bit(p + 2, 1);
            if (a <= 4)
            {
                s.Bit(p + 3, 0);
                if (a == 2)
                {
                    s.Bit(p + 4, 0);
                }
                else
                {
                    s.Bit(p + 4, 1);
                    s.Bit(p + 5, a - 3);
                }
            }
            else
            {
                s.Bit(p + 3, 1);
                if (a <= 10)
                {
                    s.Bit(p + 6, 0);
                    if (a <= 6)
                    {
                        s.Bit(p + 7, 0);
                        s.Fixed(159, a - 5);
                    }
                    else
                    {
                        s.Bit(p + 7, 1);
                        s.Fixed(165, (a - 7) >> 1);
                        s.Fixed(145, (a - 7) & 1);
                    }
                }
                else
                {
                    s.Bit(p + 6, 1);
                    int cat = a < 19 ? 0 : a < 35 ? 1 : a < 67 ? 2 : 3;
                    s.Bit(p + 8, cat >> 1);
                    s.Bit(p + 9 + (cat >> 1), cat & 1);
                    int extra = a - (3 + (8 << cat));
                    byte[] probs = CatProbs[cat];
                    for (int k = 0; k < probs.Length; k++)
                    {
                        s.Fixed(probs[k], (extra >> (probs.Length - 1 - k)) & 1);
                    }
                }
            }
        }

        s.Fixed(128, negative ? 1 : 0);
    }

    /// <summary>Emits the tokens of <paramref name="levels"/> (zigzag order, from index <paramref name="first"/>) and returns the index of the last nonzero level or -1.</summary>
    public static int EmitBlock<TSink>(ref TSink s, ReadOnlySpan<short> levels, int type, int first, int ctx)
        where TSink : struct, ITokenSink
    {
        int last = -1;
        for (int i = 15; i >= first; i--)
        {
            if (levels[i] != 0)
            {
                last = i;
                break;
            }
        }

        int p = (((type * 8) + VP8Fwd.Bands[first]) * 3 + ctx) * 11;
        if (last < 0)
        {
            s.Bit(p, 0);
            return -1;
        }

        bool prevZero = false;
        for (int n = first; n <= last; n++)
        {
            p = (((type * 8) + VP8Fwd.Bands[n]) * 3 + ctx) * 11;
            if (!prevZero)
            {
                s.Bit(p, 1);
            }

            int v = levels[n];
            int a = v < 0 ? -v : v;
            if (a == 0)
            {
                s.Bit(p + 1, 0);
                ctx = 0;
                prevZero = true;
                continue;
            }

            EmitLevel(ref s, p, a, v < 0);
            ctx = a == 1 ? 1 : 2;
            prevZero = false;
        }

        if (last < 15)
        {
            p = (((type * 8) + VP8Fwd.Bands[last + 1]) * 3 + ctx) * 11;
            s.Bit(p, 0);
        }

        return last;
    }
}

/// <summary>
/// Bit costs of coefficient blocks under the default token probabilities, from tables built once with the same token
/// walk the writer uses, so a cost is always the sum <see cref="VP8Tokens.EmitBlock{TSink}"/> would add with a <see cref="CostSink"/>.
/// </summary>
internal static class VP8Cost
{
    private const int MaxTabulated = 66;
    private const int Contexts = 4 * 8 * 3;

    private static readonly byte[] Probs = VP8Tables.DefaultCoeffProbs.ToArray();

    // Per (type, band, context): cost of a nonzero level of magnitude 1..MaxTabulated, from its "not zero" decision on, sign included.
    private static readonly int[] LevelCost = BuildLevelCosts();

    private static int[] BuildLevelCosts()
    {
        int[] t = new int[Contexts * (MaxTabulated + 1)];
        for (int c = 0; c < Contexts; c++)
        {
            for (int a = 1; a <= MaxTabulated; a++)
            {
                var sink = new CostSink(Probs);
                VP8Tokens.EmitLevel(ref sink, c * 11, a, false);
                t[(c * (MaxTabulated + 1)) + a] = sink.Cost;
            }
        }

        return t;
    }

    /// <summary>Returns the cost in 1/256 bit of the tokens of a block, as <see cref="VP8Tokens.EmitBlock{TSink}"/> with default probabilities would count it.</summary>
    public static int BlockCost(ReadOnlySpan<short> levels, int type, int first, int ctx)
    {
        int last = -1;
        for (int i = 15; i >= first; i--)
        {
            if (levels[i] != 0)
            {
                last = i;
                break;
            }
        }

        ReadOnlySpan<byte> bands = VP8Fwd.Bands;
        ReadOnlySpan<byte> probs = Probs;
        ReadOnlySpan<ushort> cost0 = VP8Fwd.BitCost0;
        ReadOnlySpan<ushort> cost1 = VP8Fwd.BitCost1;
        int c = ((type * 8) + bands[first]) * 3 + ctx;
        if (last < 0)
        {
            return cost0[probs[c * 11]];
        }

        int cost = 0;
        bool prevZero = false;
        for (int n = first; n <= last; n++)
        {
            c = ((type * 8) + bands[n]) * 3 + ctx;
            if (!prevZero)
            {
                cost += cost1[probs[c * 11]];
            }

            int v = levels[n];
            int a = v < 0 ? -v : v;
            if (a == 0)
            {
                cost += cost0[probs[(c * 11) + 1]];
                ctx = 0;
                prevZero = true;
                continue;
            }

            if (a <= MaxTabulated)
            {
                cost += LevelCost[(c * (MaxTabulated + 1)) + a];
            }
            else
            {
                var sink = new CostSink(Probs);
                VP8Tokens.EmitLevel(ref sink, c * 11, a, false);
                cost += sink.Cost;
            }

            ctx = a == 1 ? 1 : 2;
            prevZero = false;
        }

        if (last < 15)
        {
            c = ((type * 8) + bands[last + 1]) * 3 + ctx;
            cost += cost0[probs[c * 11]];
        }

        return cost;
    }
}
