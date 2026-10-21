namespace OneImage.Codecs.WebP;

/// <summary>Intra prediction of VP8 (RFC 6386 section 12), working on the 32-byte-stride macroblock buffer.</summary>
internal static class Predict
{
    private const int Bps = VP8Decoder.Bps;

    /// <summary>Predicts a 16x16 luma or 8x8 chroma block in place.</summary>
    public static void Block(byte[] w, int off, int size, int mode, bool hasLeft, bool hasTop)
    {
        int log2 = size == 16 ? 4 : 3;
        switch (mode)
        {
            case VP8Decoder.DcPred:
                {
                    int dc;
                    int sum = 0;
                    if (hasTop)
                    {
                        for (int i = 0; i < size; i++)
                        {
                            sum += w[off - Bps + i];
                        }
                    }

                    if (hasLeft)
                    {
                        for (int j = 0; j < size; j++)
                        {
                            sum += w[off + (j * Bps) - 1];
                        }
                    }

                    if (hasTop && hasLeft)
                    {
                        dc = (sum + size) >> (log2 + 1);
                    }
                    else if (hasTop || hasLeft)
                    {
                        dc = (sum + (size >> 1)) >> log2;
                    }
                    else
                    {
                        dc = 0x80;
                    }

                    for (int j = 0; j < size; j++)
                    {
                        w.AsSpan(off + (j * Bps), size).Fill((byte)dc);
                    }

                    break;
                }

            case VP8Decoder.VPred:
                for (int j = 0; j < size; j++)
                {
                    w.AsSpan(off - Bps, size).CopyTo(w.AsSpan(off + (j * Bps), size));
                }

                break;
            case VP8Decoder.HPred:
                for (int j = 0; j < size; j++)
                {
                    w.AsSpan(off + (j * Bps), size).Fill(w[off + (j * Bps) - 1]);
                }

                break;
            default:
                {
                    int tl = w[off - Bps - 1];
                    for (int j = 0; j < size; j++)
                    {
                        int l = w[off + (j * Bps) - 1] - tl;
                        for (int i = 0; i < size; i++)
                        {
                            int v = w[off - Bps + i] + l;
                            w[off + (j * Bps) + i] = (byte)(v < 0 ? 0 : v > 255 ? 255 : v);
                        }
                    }

                    break;
                }
        }
    }

    private static int Avg3(int a, int b, int c) => (a + (2 * b) + c + 2) >> 2;

    private static int Avg2(int a, int b) => (a + b + 1) >> 1;

    /// <summary>Predicts one 4x4 luma sub-block in place (RFC 6386 section 12.3).</summary>
    public static void SubBlock(byte[] w, int off, int mode)
    {
        // Edge array as in the specification: E[0..3] = left column bottom to top, E[4] = corner, E[5..12] = above row.
        Span<int> e = stackalloc int[13];
        Span<int> a = e[5..];
        int p = w[off - Bps - 1];
        for (int i = 0; i < 8; i++)
        {
            a[i] = w[off - Bps + i];
        }

        int l0 = w[off - 1];
        int l1 = w[off + Bps - 1];
        int l2 = w[off + (2 * Bps) - 1];
        int l3 = w[off + (3 * Bps) - 1];
        e[0] = l3;
        e[1] = l2;
        e[2] = l1;
        e[3] = l0;
        e[4] = p;
        Span<int> b = stackalloc int[16];
        switch (mode)
        {
            case VP8Decoder.BDc:
                {
                    int v = 4;
                    for (int i = 0; i < 4; i++)
                    {
                        v += a[i] + w[off + (i * Bps) - 1];
                    }

                    b.Fill(v >> 3);
                    break;
                }

            case VP8Decoder.BTm:
                for (int r = 0; r < 4; r++)
                {
                    int l = w[off + (r * Bps) - 1];
                    for (int c = 0; c < 4; c++)
                    {
                        int v = l + a[c] - p;
                        b[(r * 4) + c] = v < 0 ? 0 : v > 255 ? 255 : v;
                    }
                }

                break;
            case VP8Decoder.BVe:
                for (int c = 0; c < 4; c++)
                {
                    int v = Avg3(c == 0 ? p : a[c - 1], a[c], a[c + 1]);
                    b[c] = b[4 + c] = b[8 + c] = b[12 + c] = v;
                }

                break;
            case VP8Decoder.BHe:
                {
                    int v3 = Avg3(l2, l3, l3);
                    int v2 = Avg3(l1, l2, l3);
                    int v1 = Avg3(l0, l1, l2);
                    int v0 = Avg3(p, l0, l1);
                    b[0] = b[1] = b[2] = b[3] = v0;
                    b[4] = b[5] = b[6] = b[7] = v1;
                    b[8] = b[9] = b[10] = b[11] = v2;
                    b[12] = b[13] = b[14] = b[15] = v3;
                    break;
                }

            case VP8Decoder.BLd:
                b[0] = Avg3(a[0], a[1], a[2]);
                b[1] = b[4] = Avg3(a[1], a[2], a[3]);
                b[2] = b[5] = b[8] = Avg3(a[2], a[3], a[4]);
                b[3] = b[6] = b[9] = b[12] = Avg3(a[3], a[4], a[5]);
                b[7] = b[10] = b[13] = Avg3(a[4], a[5], a[6]);
                b[11] = b[14] = Avg3(a[5], a[6], a[7]);
                b[15] = Avg3(a[6], a[7], a[7]);
                break;
            case VP8Decoder.BRd:
                b[12] = Avg3(e[0], e[1], e[2]);
                b[13] = b[8] = Avg3(e[1], e[2], e[3]);
                b[14] = b[9] = b[4] = Avg3(e[2], e[3], e[4]);
                b[15] = b[10] = b[5] = b[0] = Avg3(e[3], e[4], e[5]);
                b[11] = b[6] = b[1] = Avg3(e[4], e[5], e[6]);
                b[7] = b[2] = Avg3(e[5], e[6], e[7]);
                b[3] = Avg3(e[6], e[7], e[8]);
                break;
            case VP8Decoder.BVr:
                b[12] = Avg3(e[1], e[2], e[3]);
                b[8] = Avg3(e[2], e[3], e[4]);
                b[13] = b[4] = Avg3(e[3], e[4], e[5]);
                b[9] = b[0] = Avg2(e[4], e[5]);
                b[14] = b[5] = Avg3(e[4], e[5], e[6]);
                b[10] = b[1] = Avg2(e[5], e[6]);
                b[15] = b[6] = Avg3(e[5], e[6], e[7]);
                b[11] = b[2] = Avg2(e[6], e[7]);
                b[7] = Avg3(e[6], e[7], e[8]);
                b[3] = Avg2(e[7], e[8]);
                break;
            case VP8Decoder.BVl:
                b[0] = Avg2(a[0], a[1]);
                b[4] = Avg3(a[0], a[1], a[2]);
                b[8] = b[1] = Avg2(a[1], a[2]);
                b[5] = b[12] = Avg3(a[1], a[2], a[3]);
                b[9] = b[2] = Avg2(a[2], a[3]);
                b[13] = b[6] = Avg3(a[2], a[3], a[4]);
                b[10] = b[3] = Avg2(a[3], a[4]);
                b[14] = b[7] = Avg3(a[3], a[4], a[5]);
                b[11] = Avg3(a[4], a[5], a[6]);
                b[15] = Avg3(a[5], a[6], a[7]);
                break;
            case VP8Decoder.BHd:
                b[12] = Avg2(e[0], e[1]);
                b[13] = Avg3(e[0], e[1], e[2]);
                b[8] = b[14] = Avg2(e[1], e[2]);
                b[9] = b[15] = Avg3(e[1], e[2], e[3]);
                b[10] = b[4] = Avg2(e[2], e[3]);
                b[11] = b[5] = Avg3(e[2], e[3], e[4]);
                b[6] = b[0] = Avg2(e[3], e[4]);
                b[7] = b[1] = Avg3(e[3], e[4], e[5]);
                b[2] = Avg3(e[4], e[5], e[6]);
                b[3] = Avg3(e[5], e[6], e[7]);
                break;
            default: // B_HU_PRED
                b[0] = Avg2(l0, l1);
                b[1] = Avg3(l0, l1, l2);
                b[2] = b[4] = Avg2(l1, l2);
                b[3] = b[5] = Avg3(l1, l2, l3);
                b[6] = b[8] = Avg2(l2, l3);
                b[7] = b[9] = Avg3(l2, l3, l3);
                b[10] = b[11] = b[12] = b[13] = b[14] = b[15] = l3;
                break;
        }

        for (int r = 0; r < 4; r++)
        {
            for (int c = 0; c < 4; c++)
            {
                w[off + (r * Bps) + c] = (byte)b[(r * 4) + c];
            }
        }
    }
}

/// <summary>The VP8 inverse DCT (RFC 6386 section 14.4), added to the prediction.</summary>
internal static class Idct
{
    private const int Bps = VP8Decoder.Bps;

    private static int Mul1(int a) => ((a * 20091) >> 16) + a;

    private static int Mul2(int a) => (a * 35468) >> 16;

    private static byte Clip(int v) => (byte)(v < 0 ? 0 : v > 255 ? 255 : v);

    public static void Add(ReadOnlySpan<short> input, byte[] dst, int off)
    {
        Span<int> c = stackalloc int[16];
        for (int i = 0; i < 4; i++)
        {
            int a = input[i] + input[8 + i];
            int b = input[i] - input[8 + i];
            int cc = Mul2(input[4 + i]) - Mul1(input[12 + i]);
            int d = Mul1(input[4 + i]) + Mul2(input[12 + i]);
            c[(i * 4) + 0] = a + d;
            c[(i * 4) + 1] = b + cc;
            c[(i * 4) + 2] = b - cc;
            c[(i * 4) + 3] = a - d;
        }

        for (int i = 0; i < 4; i++)
        {
            int dc = c[i] + 4;
            int a = dc + c[8 + i];
            int b = dc - c[8 + i];
            int cc = Mul2(c[4 + i]) - Mul1(c[12 + i]);
            int d = Mul1(c[4 + i]) + Mul2(c[12 + i]);
            int o = off + (i * Bps);
            dst[o] = Clip(dst[o] + ((a + d) >> 3));
            dst[o + 1] = Clip(dst[o + 1] + ((b + cc) >> 3));
            dst[o + 2] = Clip(dst[o + 2] + ((b - cc) >> 3));
            dst[o + 3] = Clip(dst[o + 3] + ((a - d) >> 3));
        }
    }
}
