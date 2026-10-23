using OneImage.Codecs.Tiff;

namespace OneImage.Codecs.Jbig2;

/// <summary>The generic region (T.88 6.2) and generic refinement region (6.3) decoding procedures.</summary>
internal static class Jbig2Generic
{
    // The context that codes the "row is typical" flag (T.88 figures 8 to 11), in this decoder's bit order:
    // template pixels in reading order, the first one the most significant bit, adaptive pixels in their nominal slots.
    private static readonly int[] s_sltpContext = [0x9B25, 0x0795, 0x00E5, 0x0195];

    // Per template: bits in the row two above, how many of them lie right of the pixel; the same for the row above;
    // bits in the current row (all to the left).
    private static readonly int[] s_rowTwoBits = [5, 4, 3, 0];
    private static readonly int[] s_rowTwoAhead = [2, 2, 1, 0];
    private static readonly int[] s_rowOneBits = [7, 6, 5, 6];
    private static readonly int[] s_rowOneAhead = [3, 3, 2, 2];
    private static readonly int[] s_rowZeroBits = [4, 3, 2, 4];

    // Template pixel offsets in context bit order (most significant first). Entries that are adaptive pixels are
    // marked by the index into the AT array in s_atSlot (-1 for fixed pixels).
    private static readonly sbyte[][] s_templateX =
    [
        [-2, -1, 0, 1, 2, -3, -2, -1, 0, 1, 2, 3, -4, -3, -2, -1],
        [-1, 0, 1, 2, -2, -1, 0, 1, 2, 3, -3, -2, -1],
        [-1, 0, 1, -2, -1, 0, 1, 2, -2, -1],
        [-3, -2, -1, 0, 1, 2, -4, -3, -2, -1],
    ];

    private static readonly sbyte[][] s_templateY =
    [
        [-2, -2, -2, -2, -2, -1, -1, -1, -1, -1, -1, -1, 0, 0, 0, 0],
        [-2, -2, -2, -2, -1, -1, -1, -1, -1, -1, 0, 0, 0],
        [-2, -2, -2, -1, -1, -1, -1, -1, 0, 0],
        [-1, -1, -1, -1, -1, -1, 0, 0, 0, 0],
    ];

    private static readonly sbyte[][] s_atSlot =
    [
        [3, -1, -1, -1, 2, 1, -1, -1, -1, -1, -1, 0, -1, -1, -1, -1],
        [-1, -1, -1, -1, -1, -1, -1, -1, -1, 0, -1, -1, -1],
        [-1, -1, -1, -1, -1, -1, -1, 0, -1, -1],
        [-1, -1, -1, -1, -1, 0, -1, -1, -1, -1],
    ];

    private static readonly int[][] s_nominalAt =
    [
        [3, -1, -3, -1, 2, -2, -2, -2],
        [3, -1, 0, 0, 0, 0, 0, 0],
        [2, -1, 0, 0, 0, 0, 0, 0],
        [2, -1, 0, 0, 0, 0, 0, 0],
    ];

    /// <summary>Gets the nominal adaptive template pixel positions (Table 5) for the template, as x1, y1, x2, y2, ...</summary>
    public static ReadOnlySpan<int> NominalAt(int template) => s_nominalAt[template];

    /// <summary>
    /// Decodes a generic region with arithmetic coding (T.88 6.2.5): the bitmap is read pixel by pixel, each pixel
    /// coded in the context of a template of already-decoded neighbours.
    /// </summary>
    /// <param name="work">Limits and cancellation.</param>
    /// <param name="mq">The arithmetic decoder.</param>
    /// <param name="cx">The 65536 adaptive states of the GB contexts; updated.</param>
    /// <param name="width">GBW.</param>
    /// <param name="height">GBH.</param>
    /// <param name="template">GBTEMPLATE, 0 to 3.</param>
    /// <param name="typicalPrediction">TPGDON.</param>
    /// <param name="skip">The SKIP bitmap, or null.</param>
    /// <param name="at">The adaptive pixel positions x1, y1, ... (8 values for template 0, 2 otherwise).</param>
    public static Jbig2Bitmap DecodeArithmetic(Jbig2Work work, Jbig2ArithmeticDecoder mq, byte[] cx, int width, int height, int template, bool typicalPrediction, Jbig2Bitmap? skip, ReadOnlySpan<int> at)
    {
        int atCount = template == 0 ? 4 : 1;
        if (at.Length < atCount * 2)
        {
            throw new ImageFormatException("A JBIG2 region is missing its adaptive template pixels.");
        }

        for (int i = 0; i < atCount; i++)
        {
            // The adaptive pixels must lie in the part of the field that is already decoded (T.88 figure 7).
            int ax = at[2 * i];
            int ay = at[(2 * i) + 1];
            if (ay > 0 || (ay == 0 && ax >= 0))
            {
                throw new ImageFormatException("A JBIG2 adaptive template pixel lies on or after the pixel being decoded.");
            }
        }

        work.ChargePixels(width, height);
        var bitmap = new Jbig2Bitmap(width, height);
        if (width == 0 || height == 0)
        {
            return bitmap;
        }

        bool nominal = true;
        for (int i = 0; i < atCount * 2; i++)
        {
            nominal &= at[i] == s_nominalAt[template][i];
        }

        if (nominal)
        {
            DecodeFast(work, mq, cx, bitmap, template, typicalPrediction, skip);
        }
        else
        {
            DecodeGeneral(work, mq, cx, bitmap, template, typicalPrediction, skip, at);
        }

        return bitmap;
    }

    private static void DecodeFast(Jbig2Work work, Jbig2ArithmeticDecoder mq, byte[] cx, Jbig2Bitmap bitmap, int template, bool typicalPrediction, Jbig2Bitmap? skip)
    {
        const int Pad = 8;
        int width = bitmap.Width;
        int height = bitmap.Height;
        int n2 = s_rowTwoBits[template];
        int a2 = s_rowTwoAhead[template];
        int n1 = s_rowOneBits[template];
        int a1 = s_rowOneAhead[template];
        int n0 = s_rowZeroBits[template];
        int m2 = (1 << n2) - 1;
        int m1 = (1 << n1) - 1;
        int m0 = (1 << n0) - 1;
        int shift2 = n1 + n0;
        int sltp = s_sltpContext[template];
        byte[] cur = new byte[width + (2 * Pad)];
        byte[] above = new byte[width + (2 * Pad)];
        byte[] above2 = new byte[width + (2 * Pad)];
        int ltp = 0;
        for (int y = 0; y < height; y++)
        {
            work.Cancellation.ThrowIfCancellationRequested();
            if (typicalPrediction)
            {
                ltp ^= mq.Decode(cx, sltp);
            }

            if (ltp != 0)
            {
                Array.Copy(above, cur, cur.Length);
            }
            else
            {
                int l2 = 0;
                int l1 = 0;
                int l0 = 0;
                if (n2 > 0)
                {
                    for (int k = 0; k <= a2; k++)
                    {
                        l2 = (l2 << 1) | above2[Pad + k];
                    }
                }

                for (int k = 0; k <= a1; k++)
                {
                    l1 = (l1 << 1) | above[Pad + k];
                }

                int p2 = Pad + a2 + 1;
                int p1 = Pad + a1 + 1;
                for (int x = 0; x < width; x++)
                {
                    int bit = 0;
                    if (skip is null || skip.Get(x, y) == 0)
                    {
                        bit = mq.Decode(cx, (l2 << shift2) | (l1 << n0) | l0);
                    }

                    cur[Pad + x] = (byte)bit;
                    l2 = ((l2 << 1) | above2[p2 + x]) & m2;
                    l1 = ((l1 << 1) | above[p1 + x]) & m1;
                    l0 = ((l0 << 1) | bit) & m0;
                }
            }

            PackRow(cur, Pad, bitmap, y);
            (above2, above, cur) = (above, cur, above2);
        }
    }

    private static void PackRow(byte[] row, int offset, Jbig2Bitmap bitmap, int y)
    {
        int width = bitmap.Width;
        int o = y * bitmap.Stride;
        byte[] data = bitmap.Data;
        int x = 0;
        for (; x + 8 <= width; x += 8)
        {
            int p = offset + x;
            data[o++] = (byte)((row[p] << 7) | (row[p + 1] << 6) | (row[p + 2] << 5) | (row[p + 3] << 4)
                | (row[p + 4] << 3) | (row[p + 5] << 2) | (row[p + 6] << 1) | row[p + 7]);
        }

        if (x < width)
        {
            int b = 0;
            for (int k = 0; k < width - x; k++)
            {
                b |= row[offset + x + k] << (7 - k);
            }

            data[o] = (byte)b;
        }
    }

    // Adaptive pixels away from their nominal places: every template pixel is read from the bitmap being decoded.
    private static void DecodeGeneral(Jbig2Work work, Jbig2ArithmeticDecoder mq, byte[] cx, Jbig2Bitmap bitmap, int template, bool typicalPrediction, Jbig2Bitmap? skip, ReadOnlySpan<int> at)
    {
        sbyte[] tx = s_templateX[template];
        sbyte[] ty = s_templateY[template];
        sbyte[] slot = s_atSlot[template];
        int count = tx.Length;
        var dx = new int[count];
        var dy = new int[count];
        for (int i = 0; i < count; i++)
        {
            dx[i] = slot[i] >= 0 ? at[2 * slot[i]] : tx[i];
            dy[i] = slot[i] >= 0 ? at[(2 * slot[i]) + 1] : ty[i];
        }

        int width = bitmap.Width;
        int height = bitmap.Height;
        int sltp = s_sltpContext[template];
        int ltp = 0;
        for (int y = 0; y < height; y++)
        {
            work.Cancellation.ThrowIfCancellationRequested();
            if (typicalPrediction)
            {
                ltp ^= mq.Decode(cx, sltp);
            }

            if (ltp != 0)
            {
                if (y > 0)
                {
                    Array.Copy(bitmap.Data, (y - 1) * bitmap.Stride, bitmap.Data, y * bitmap.Stride, bitmap.Stride);
                }

                continue;
            }

            for (int x = 0; x < width; x++)
            {
                if (skip is not null && skip.Get(x, y) != 0)
                {
                    continue;
                }

                int ctx = 0;
                for (int k = 0; k < count; k++)
                {
                    ctx = (ctx << 1) | bitmap.Get(x + dx[k], y + dy[k]);
                }

                if (mq.Decode(cx, ctx) != 0)
                {
                    bitmap.Set(x, y, 1);
                }
            }
        }
    }

    /// <summary>
    /// Decodes a generic region coded with MMR (T.88 6.2.6), the Group 4 fax coding, using the TIFF fax decoder.
    /// </summary>
    /// <param name="work">Limits and cancellation.</param>
    /// <param name="data">The array holding the coded data.</param>
    /// <param name="start">First coded byte.</param>
    /// <param name="end">End of the coded data.</param>
    /// <param name="width">GBW.</param>
    /// <param name="height">GBH.</param>
    /// <param name="end2">Receives the offset after the coded block (past its end-of-block code, when there is one).</param>
    public static Jbig2Bitmap DecodeMmr(Jbig2Work work, byte[] data, int start, int end, int width, int height, out int end2)
    {
        work.CheckDensity(width, height, end - start);
        work.ChargePixels(width, height);
        var bitmap = new Jbig2Bitmap(width, height);
        end2 = start;
        if (width == 0 || height == 0)
        {
            return bitmap;
        }

        int used = TiffCcitt.DecodeMmr(new ReadOnlySpan<byte>(data, start, end - start), width, height, bitmap.Data, work.Cancellation);
        end2 = start + used;
        return bitmap;
    }

    /// <summary>
    /// Decodes a generic refinement region (T.88 6.3.5): each pixel is coded in the context of already-decoded pixels
    /// of the new bitmap and the pixels around the corresponding position of a reference bitmap.
    /// </summary>
    /// <param name="work">Limits and cancellation.</param>
    /// <param name="mq">The arithmetic decoder.</param>
    /// <param name="cx">The 8192 adaptive states of the GR contexts; updated.</param>
    /// <param name="width">GRW.</param>
    /// <param name="height">GRH.</param>
    /// <param name="template">GRTEMPLATE, 0 or 1.</param>
    /// <param name="reference">GRREFERENCE.</param>
    /// <param name="referenceDx">GRREFERENCEDX.</param>
    /// <param name="referenceDy">GRREFERENCEDY.</param>
    /// <param name="typicalPrediction">TPGRON.</param>
    /// <param name="at">GRATX1, GRATY1, GRATX2, GRATY2 (template 0 only).</param>
    public static Jbig2Bitmap DecodeRefinement(Jbig2Work work, Jbig2ArithmeticDecoder mq, byte[] cx, int width, int height, int template, Jbig2Bitmap reference, int referenceDx, int referenceDy, bool typicalPrediction, ReadOnlySpan<sbyte> at)
    {
        // Context bit order (most significant first): pixels of the new bitmap, then of the reference.
        // Offsets are relative to the pixel being decoded (new bitmap) or to its position in the reference.
        int[] nx;
        int[] ny;
        int[] rx;
        int[] ry;
        int sltp;
        if (template == 0)
        {
            if (at.Length < 4)
            {
                throw new ImageFormatException("A JBIG2 refinement region is missing its adaptive template pixels.");
            }

            if (at[1] > 0 || (at[1] == 0 && at[0] >= 0))
            {
                throw new ImageFormatException("A JBIG2 adaptive template pixel lies on or after the pixel being decoded.");
            }

            nx = [at[0], 0, 1, -1];
            ny = [at[1], -1, -1, 0];
            rx = [at[2], 0, 1, -1, 0, 1, -1, 0, 1];
            ry = [at[3], -1, -1, 0, 0, 0, 1, 1, 1];
            sltp = 0x10;
        }
        else
        {
            nx = [-1, 0, 1, -1];
            ny = [-1, -1, -1, 0];
            rx = [0, -1, 0, 1, 0, 1];
            ry = [-1, 0, 0, 0, 1, 1];
            sltp = 0x8;
        }

        work.ChargePixels(width, height);
        var bitmap = new Jbig2Bitmap(width, height);
        int ltp = 0;
        for (int y = 0; y < height; y++)
        {
            work.Cancellation.ThrowIfCancellationRequested();
            if (typicalPrediction)
            {
                ltp ^= mq.Decode(cx, sltp);
            }

            int ry0 = y - referenceDy;
            for (int x = 0; x < width; x++)
            {
                int rx0 = x - referenceDx;
                if (ltp != 0)
                {
                    // T.88 6.3.5.6 step 3 d: a pixel whose 3x3 reference neighbourhood is uniform takes that value.
                    int v = reference.Get(rx0, ry0);
                    bool uniform = true;
                    for (int j = -1; j <= 1 && uniform; j++)
                    {
                        for (int i = -1; i <= 1; i++)
                        {
                            if (reference.Get(rx0 + i, ry0 + j) != v)
                            {
                                uniform = false;
                                break;
                            }
                        }
                    }

                    if (uniform)
                    {
                        if (v != 0)
                        {
                            bitmap.Set(x, y, 1);
                        }

                        continue;
                    }
                }

                int ctx = 0;
                for (int k = 0; k < nx.Length; k++)
                {
                    ctx = (ctx << 1) | bitmap.Get(x + nx[k], y + ny[k]);
                }

                for (int k = 0; k < rx.Length; k++)
                {
                    ctx = (ctx << 1) | reference.Get(rx0 + rx[k], ry0 + ry[k]);
                }

                if (mq.Decode(cx, ctx) != 0)
                {
                    bitmap.Set(x, y, 1);
                }
            }
        }

        return bitmap;
    }
}
