namespace OneImage.Codecs.WebP;

/// <summary>Builds the prediction borders of a macroblock in the 32-byte-stride work buffer shared by the VP8 decoder and encoder.</summary>
internal static class VP8Borders
{
    // Fills the work buffer's top row, left column and corner. Beyond the frame the specification fixes the values:
    // 127 above the first row, 129 left of the first column (RFC 6386 section 12.2).
    public static void Prepare(byte[] w, byte[] py, byte[] pu, byte[] pv, int yStride, int uvStride, int mbx, int mby, int mbw)
    {
        if (mby > 0)
        {
            int rowY = ((mby * 16) - 1) * yStride;
            py.AsSpan(rowY + (mbx * 16), 16).CopyTo(w.AsSpan(VP8Decoder.YOff - VP8Decoder.Bps, 16));
            if (mbx < mbw - 1)
            {
                py.AsSpan(rowY + (mbx * 16) + 16, 4).CopyTo(w.AsSpan(VP8Decoder.YOff - VP8Decoder.Bps + 16, 4));
            }
            else
            {
                w.AsSpan(VP8Decoder.YOff - VP8Decoder.Bps + 16, 4).Fill(py[rowY + (mbx * 16) + 15]);
            }

            int rowUv = ((mby * 8) - 1) * uvStride;
            pu.AsSpan(rowUv + (mbx * 8), 8).CopyTo(w.AsSpan(VP8Decoder.UOff - VP8Decoder.Bps, 8));
            pv.AsSpan(rowUv + (mbx * 8), 8).CopyTo(w.AsSpan(VP8Decoder.VOff - VP8Decoder.Bps, 8));
        }
        else
        {
            w.AsSpan(VP8Decoder.YOff - VP8Decoder.Bps - 1, 16 + 4 + 1).Fill(127);
            w.AsSpan(VP8Decoder.UOff - VP8Decoder.Bps - 1, 8 + 1).Fill(127);
            w.AsSpan(VP8Decoder.VOff - VP8Decoder.Bps - 1, 8 + 1).Fill(127);
        }

        if (mbx > 0)
        {
            // Left column = right column of the macroblock to the left, read back from the planes.
            for (int j = 0; j < 16; j++)
            {
                w[VP8Decoder.YOff + (j * VP8Decoder.Bps) - 1] = py[((((mby * 16) + j) * yStride) + (mbx * 16)) - 1];
            }

            for (int j = 0; j < 8; j++)
            {
                int src = ((((mby * 8) + j) * uvStride) + (mbx * 8)) - 1;
                w[VP8Decoder.UOff + (j * VP8Decoder.Bps) - 1] = pu[src];
                w[VP8Decoder.VOff + (j * VP8Decoder.Bps) - 1] = pv[src];
            }

            if (mby > 0)
            {
                w[VP8Decoder.YOff - VP8Decoder.Bps - 1] = py[((((mby * 16) - 1) * yStride) + (mbx * 16)) - 1];
                int s = ((((mby * 8) - 1) * uvStride) + (mbx * 8)) - 1;
                w[VP8Decoder.UOff - VP8Decoder.Bps - 1] = pu[s];
                w[VP8Decoder.VOff - VP8Decoder.Bps - 1] = pv[s];
            }
        }
        else
        {
            for (int j = 0; j < 16; j++)
            {
                w[VP8Decoder.YOff + (j * VP8Decoder.Bps) - 1] = 129;
            }

            for (int j = 0; j < 8; j++)
            {
                w[VP8Decoder.UOff + (j * VP8Decoder.Bps) - 1] = 129;
                w[VP8Decoder.VOff + (j * VP8Decoder.Bps) - 1] = 129;
            }

            if (mby > 0)
            {
                w[VP8Decoder.YOff - VP8Decoder.Bps - 1] = 129;
                w[VP8Decoder.UOff - VP8Decoder.Bps - 1] = 129;
                w[VP8Decoder.VOff - VP8Decoder.Bps - 1] = 129;
            }
        }
    }

    // The decoder variant of Prepare: the row above the macroblock comes from copies saved before the loop filter ran,
    // because intra prediction must see unfiltered pixels (RFC 6386 section 15).
    public static void PrepareFiltered(byte[] w, byte[] py, byte[] pu, byte[] pv, byte[] topY, byte[] topU, byte[] topV, int yStride, int uvStride, int mbx, int mby, int slot, int mbw)
    {
        if (mby > 0)
        {
            topY.AsSpan(mbx * 16, 16).CopyTo(w.AsSpan(VP8Decoder.YOff - VP8Decoder.Bps, 16));
            if (mbx < mbw - 1)
            {
                topY.AsSpan((mbx * 16) + 16, 4).CopyTo(w.AsSpan(VP8Decoder.YOff - VP8Decoder.Bps + 16, 4));
            }
            else
            {
                w.AsSpan(VP8Decoder.YOff - VP8Decoder.Bps + 16, 4).Fill(topY[(mbx * 16) + 15]);
            }

            topU.AsSpan(mbx * 8, 8).CopyTo(w.AsSpan(VP8Decoder.UOff - VP8Decoder.Bps, 8));
            topV.AsSpan(mbx * 8, 8).CopyTo(w.AsSpan(VP8Decoder.VOff - VP8Decoder.Bps, 8));
        }
        else
        {
            w.AsSpan(VP8Decoder.YOff - VP8Decoder.Bps - 1, 16 + 4 + 1).Fill(127);
            w.AsSpan(VP8Decoder.UOff - VP8Decoder.Bps - 1, 8 + 1).Fill(127);
            w.AsSpan(VP8Decoder.VOff - VP8Decoder.Bps - 1, 8 + 1).Fill(127);
        }

        if (mbx > 0)
        {
            // Left column = right column of the macroblock to the left, read back from the planes.
            for (int j = 0; j < 16; j++)
            {
                w[VP8Decoder.YOff + (j * VP8Decoder.Bps) - 1] = py[((((slot * 16) + j) * yStride) + (mbx * 16)) - 1];
            }

            for (int j = 0; j < 8; j++)
            {
                int src = ((((slot * 8) + j) * uvStride) + (mbx * 8)) - 1;
                w[VP8Decoder.UOff + (j * VP8Decoder.Bps) - 1] = pu[src];
                w[VP8Decoder.VOff + (j * VP8Decoder.Bps) - 1] = pv[src];
            }

            if (mby > 0)
            {
                w[VP8Decoder.YOff - VP8Decoder.Bps - 1] = topY[(mbx * 16) - 1];
                w[VP8Decoder.UOff - VP8Decoder.Bps - 1] = topU[(mbx * 8) - 1];
                w[VP8Decoder.VOff - VP8Decoder.Bps - 1] = topV[(mbx * 8) - 1];
            }
        }
        else
        {
            for (int j = 0; j < 16; j++)
            {
                w[VP8Decoder.YOff + (j * VP8Decoder.Bps) - 1] = 129;
            }

            for (int j = 0; j < 8; j++)
            {
                w[VP8Decoder.UOff + (j * VP8Decoder.Bps) - 1] = 129;
                w[VP8Decoder.VOff + (j * VP8Decoder.Bps) - 1] = 129;
            }

            if (mby > 0)
            {
                w[VP8Decoder.YOff - VP8Decoder.Bps - 1] = 129;
                w[VP8Decoder.UOff - VP8Decoder.Bps - 1] = 129;
                w[VP8Decoder.VOff - VP8Decoder.Bps - 1] = 129;
            }
        }
    }
}
