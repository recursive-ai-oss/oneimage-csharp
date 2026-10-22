using System.Buffers.Binary;

namespace OneImage.Kernels;

/// <summary>
/// Row-level helpers for the TIFF codec: byte swapping, predictors, PackBits and bit reversal. They run over buffers whose
/// sizes the codec has already validated and never parse input structure.
/// </summary>
internal static class TiffKernels
{
    private static readonly byte[] s_reversed = BuildReversed();

    private static byte[] BuildReversed()
    {
        byte[] t = new byte[256];
        for (int i = 0; i < 256; i++)
        {
            int v = i;
            v = ((v & 0xF0) >> 4) | ((v & 0x0F) << 4);
            v = ((v & 0xCC) >> 2) | ((v & 0x33) << 2);
            v = ((v & 0xAA) >> 1) | ((v & 0x55) << 1);
            t[i] = (byte)v;
        }

        return t;
    }

    /// <summary>Reverses the bit order inside every byte (TIFF FillOrder 2).</summary>
    public static void ReverseBits(Span<byte> data)
    {
        byte[] t = s_reversed;
        for (int i = 0; i < data.Length; i++)
        {
            data[i] = t[data[i]];
        }
    }

    /// <summary>Swaps the byte order of 2- or 4-byte elements from <paramref name="source"/> into <paramref name="destination"/>.</summary>
    public static void SwapBytes(ReadOnlySpan<byte> source, Span<byte> destination, int elementSize)
    {
        if (elementSize == 2)
        {
            BinaryPrimitives.ReverseEndianness(SpanCast.As<ushort>(source), SpanCast.As<ushort>(destination[..source.Length]));
        }
        else
        {
            BinaryPrimitives.ReverseEndianness(SpanCast.As<uint>(source), SpanCast.As<uint>(destination[..source.Length]));
        }
    }

    /// <summary>
    /// Undoes the horizontal predictor (TIFF 6.0 section 14) on every row of <paramref name="data"/>: each sample is stored as the
    /// difference to the same channel of the pixel to its left.
    /// </summary>
    /// <param name="data">Rows of <paramref name="rowBytes"/> bytes, in file byte order.</param>
    /// <param name="rowBytes">Bytes per row; samples are 8 or 16 bits.</param>
    /// <param name="channels">Samples per pixel in the row.</param>
    /// <param name="bits">8 or 16.</param>
    /// <param name="bigEndian">Byte order of 16-bit samples.</param>
    public static void UndoHorizontal(Span<byte> data, int rowBytes, int channels, int bits, bool bigEndian)
    {
        for (int r = 0; r + rowBytes <= data.Length; r += rowBytes)
        {
            Span<byte> row = data.Slice(r, rowBytes);
            if (bits == 8)
            {
                RunningSum8(row, channels);
            }
            else if (bigEndian)
            {
                for (int i = channels * 2; i + 1 < row.Length; i += 2)
                {
                    ushort v = (ushort)(BinaryPrimitives.ReadUInt16BigEndian(row[i..]) + BinaryPrimitives.ReadUInt16BigEndian(row[(i - (channels * 2))..]));
                    BinaryPrimitives.WriteUInt16BigEndian(row[i..], v);
                }
            }
            else
            {
                for (int i = channels * 2; i + 1 < row.Length; i += 2)
                {
                    ushort v = (ushort)(BinaryPrimitives.ReadUInt16LittleEndian(row[i..]) + BinaryPrimitives.ReadUInt16LittleEndian(row[(i - (channels * 2))..]));
                    BinaryPrimitives.WriteUInt16LittleEndian(row[i..], v);
                }
            }
        }
    }

    // Each sample depends on the one a pixel to its left. Reading that one back from memory makes every step wait for the store
    // just before it; carrying the previous pixel in locals turns the chain into plain register adds.
    internal static void RunningSum8(Span<byte> row, int channels)
    {
        switch (channels)
        {
            case 1:
            {
                byte a = row.Length > 0 ? row[0] : (byte)0;
                for (int i = 1; i < row.Length; i++)
                {
                    a = (byte)(a + row[i]);
                    row[i] = a;
                }

                break;
            }

            case 3:
            {
                if (row.Length < 3)
                {
                    break;
                }

                byte a = row[0];
                byte b = row[1];
                byte c = row[2];
                int i = 3;
                for (; i + 3 <= row.Length; i += 3)
                {
                    Span<byte> px = row.Slice(i, 3);
                    a = (byte)(a + px[0]);
                    b = (byte)(b + px[1]);
                    c = (byte)(c + px[2]);
                    px[0] = a;
                    px[1] = b;
                    px[2] = c;
                }

                for (; i < row.Length; i++)
                {
                    row[i] = (byte)(row[i] + row[i - 3]);
                }

                break;
            }

            case 4:
            {
                if (row.Length < 4)
                {
                    break;
                }

                byte a = row[0];
                byte b = row[1];
                byte c = row[2];
                byte d = row[3];
                int i = 4;
                for (; i + 4 <= row.Length; i += 4)
                {
                    Span<byte> px = row.Slice(i, 4);
                    a = (byte)(a + px[0]);
                    b = (byte)(b + px[1]);
                    c = (byte)(c + px[2]);
                    d = (byte)(d + px[3]);
                    px[0] = a;
                    px[1] = b;
                    px[2] = c;
                    px[3] = d;
                }

                for (; i < row.Length; i++)
                {
                    row[i] = (byte)(row[i] + row[i - 4]);
                }

                break;
            }

            default:
                for (int i = channels; i < row.Length; i++)
                {
                    row[i] = (byte)(row[i] + row[i - channels]);
                }

                break;
        }
    }

    /// <summary>Applies the horizontal predictor to little-endian rows in place (the encoder side of <see cref="UndoHorizontal"/>).</summary>
    public static void ApplyHorizontal(Span<byte> data, int rowBytes, int channels, int bits)
    {
        for (int r = 0; r + rowBytes <= data.Length; r += rowBytes)
        {
            Span<byte> row = data.Slice(r, rowBytes);
            if (bits == 8)
            {
                for (int i = row.Length - 1; i >= channels; i--)
                {
                    row[i] = (byte)(row[i] - row[i - channels]);
                }
            }
            else
            {
                for (int i = row.Length - 2; i >= channels * 2; i -= 2)
                {
                    ushort v = (ushort)(BinaryPrimitives.ReadUInt16LittleEndian(row[i..]) - BinaryPrimitives.ReadUInt16LittleEndian(row[(i - (channels * 2))..]));
                    BinaryPrimitives.WriteUInt16LittleEndian(row[i..], v);
                }
            }
        }
    }

    /// <summary>
    /// Undoes the floating point predictor (Adobe TIFF Technical Note 3) in place and leaves native little-endian floats.
    /// Each row stores the bytes of all samples split into four lanes, most significant lane first, with the lanes
    /// byte-wise delta coded across the whole row.
    /// </summary>
    public static void UndoFloat(Span<byte> data, int rowBytes, int channels, Span<byte> scratch)
    {
        int count = rowBytes / 4;
        for (int r = 0; r + rowBytes <= data.Length; r += rowBytes)
        {
            Span<byte> row = data.Slice(r, rowBytes);
            for (int i = channels; i < row.Length; i++)
            {
                row[i] = (byte)(row[i] + row[i - channels]);
            }

            row.CopyTo(scratch);
            for (int k = 0; k < count; k++)
            {
                row[(4 * k) + 3] = scratch[k];
                row[(4 * k) + 2] = scratch[count + k];
                row[(4 * k) + 1] = scratch[(2 * count) + k];
                row[4 * k] = scratch[(3 * count) + k];
            }
        }
    }

    /// <summary>Applies the floating point predictor to little-endian float rows in place.</summary>
    public static void ApplyFloat(Span<byte> data, int rowBytes, int channels, Span<byte> scratch)
    {
        int count = rowBytes / 4;
        for (int r = 0; r + rowBytes <= data.Length; r += rowBytes)
        {
            Span<byte> row = data.Slice(r, rowBytes);
            for (int k = 0; k < count; k++)
            {
                scratch[k] = row[(4 * k) + 3];
                scratch[count + k] = row[(4 * k) + 2];
                scratch[(2 * count) + k] = row[(4 * k) + 1];
                scratch[(3 * count) + k] = row[4 * k];
            }

            for (int i = rowBytes - 1; i >= channels; i--)
            {
                scratch[i] = (byte)(scratch[i] - scratch[i - channels]);
            }

            scratch[..rowBytes].CopyTo(row);
        }
    }

    /// <summary>
    /// Expands PackBits (TIFF 6.0 section 9) into <paramref name="output"/>. Runs that would overrun the output are cut at its end.
    /// </summary>
    /// <returns>The number of bytes produced; less than the output length means the input ended early.</returns>
    public static int PackBitsDecode(ReadOnlySpan<byte> input, Span<byte> output)
    {
        int ip = 0;
        int op = 0;
        while (op < output.Length && ip < input.Length)
        {
            sbyte n = (sbyte)input[ip++];
            if (n >= 0)
            {
                int len = Math.Min(n + 1, Math.Min(input.Length - ip, output.Length - op));
                input.Slice(ip, len).CopyTo(output[op..]);
                ip += n + 1;
                op += len;
                if (len < n + 1 && op < output.Length)
                {
                    break; // literal run cut off by the end of the data
                }
            }
            else if (n != -128)
            {
                if (ip >= input.Length)
                {
                    break;
                }

                int len = Math.Min(1 - n, output.Length - op);
                output.Slice(op, len).Fill(input[ip++]);
                op += len;
            }
        }

        return op;
    }

    /// <summary>PackBits-encodes <paramref name="row"/> into <paramref name="output"/> (which must hold at least <c>row.Length + row.Length / 128 + 1</c> bytes).</summary>
    public static int PackBitsEncode(ReadOnlySpan<byte> row, Span<byte> output)
    {
        int ip = 0;
        int op = 0;
        while (ip < row.Length)
        {
            int run = 1;
            while (ip + run < row.Length && run < 128 && row[ip + run] == row[ip])
            {
                run++;
            }

            if (run >= 3 || (run == 2 && ip + run >= row.Length))
            {
                output[op++] = (byte)(1 - run);
                output[op++] = row[ip];
                ip += run;
                continue;
            }

            int start = ip;
            ip += run;
            while (ip < row.Length && ip - start < 128)
            {
                if (ip + 2 < row.Length && row[ip] == row[ip + 1] && row[ip] == row[ip + 2])
                {
                    break;
                }

                ip++;
            }

            int len = ip - start;
            output[op++] = (byte)(len - 1);
            row.Slice(start, len).CopyTo(output[op..]);
            op += len;
        }

        return op;
    }

    /// <summary>Converts associated (premultiplied) 8-bit samples to straight alpha; the alpha channel is the last of <paramref name="channels"/>.</summary>
    public static void Unpremultiply8(Span<byte> pixels, int channels)
    {
        for (int i = 0; i + channels <= pixels.Length; i += channels)
        {
            int a = pixels[i + channels - 1];
            for (int c = 0; c < channels - 1; c++)
            {
                pixels[i + c] = a == 0 ? (byte)0 : (byte)Math.Min(255, ((pixels[i + c] * 255) + (a / 2)) / a);
            }
        }
    }

    /// <summary>Converts associated 16-bit samples (little-endian) to straight alpha.</summary>
    public static void Unpremultiply16(Span<byte> pixels, int channels)
    {
        for (int i = 0; i + (channels * 2) <= pixels.Length; i += channels * 2)
        {
            int a = BinaryPrimitives.ReadUInt16LittleEndian(pixels[(i + ((channels - 1) * 2))..]);
            for (int c = 0; c < channels - 1; c++)
            {
                Span<byte> p = pixels.Slice(i + (c * 2), 2);
                int v = BinaryPrimitives.ReadUInt16LittleEndian(p);
                BinaryPrimitives.WriteUInt16LittleEndian(p, a == 0 ? (ushort)0 : (ushort)Math.Min(65535, (((long)v * 65535) + (a / 2)) / a));
            }
        }
    }
}
