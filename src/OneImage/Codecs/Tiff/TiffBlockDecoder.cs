using System.Buffers;
using OneImage.Compression;
using OneImage.Kernels;

namespace OneImage.Codecs.Tiff;

/// <summary>Reads, decompresses and un-predicts one strip or tile into its stored raw layout.</summary>
internal static class TiffBlockDecoder
{
    private const int InputChunk = 1 << 16;

    /// <summary>Decodes block <paramref name="index"/> (planes laid out one after another). The result comes from <see cref="ArrayPool{T}"/> and is longer than the block; the caller returns it.</summary>
    public static byte[] DecodePooled(TiffFile file, TiffFrame f, int index, ImageLimits limits, CancellationToken cancellationToken)
        => DecodeCore(file, f, index, limits, pooled: true, cancellationToken);

    private static byte[] DecodeCore(TiffFile file, TiffFrame f, int index, ImageLimits limits, bool pooled, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if ((uint)index >= (uint)f.Offsets.Length || (uint)index >= (uint)f.Counts.Length)
        {
            throw new ImageFormatException("A TIFF strip or tile index is out of range.");
        }

        long offset = f.Offsets[index];
        long count = f.Counts[index];
        if (offset < 0 || count < 0 || offset > file.Length || count > file.Length - offset)
        {
            throw new ImageFormatException("A TIFF strip or tile lies outside the file.");
        }

        if (count == 0)
        {
            throw new ImageFormatException("A TIFF strip or tile is empty.");
        }

        int rows = f.RowsInBlock(index % f.BlocksPerPlane);
        int rowBytes = (int)f.BlockRowBytes;
        int expected = checked(rows * rowBytes);
        CheckSizes(f, count, rows, expected, limits);
        byte[] input = ArrayPool<byte>.Shared.Rent((int)Math.Min(count, int.MaxValue / 2));
        try
        {
            Span<byte> compressed = input.AsSpan(0, (int)count);
            file.ReadAt(offset, compressed);
            if (f.FillOrderReversed && f.Codec != TiffCodec.OldJpeg)
            {
                TiffKernels.ReverseBits(compressed);
            }

            byte[] raw;
            if (pooled)
            {
                raw = ArrayPool<byte>.Shared.Rent(expected);

                // These codecs write every byte of the block or fail, so stale pool contents never show; the others (bit-packed fax
                // data, JPEG) may leave gaps and start from zeros.
                if (f.Codec is not (TiffCodec.None or TiffCodec.Lzw or TiffCodec.Deflate or TiffCodec.PackBits))
                {
                    raw.AsSpan(0, expected).Clear();
                }
            }
            else
            {
                raw = new byte[expected];
            }

            try
            {
                Decompress(f, compressed, raw.AsSpan(0, expected), rows, rowBytes, limits, cancellationToken);
                Undo(f, raw.AsSpan(0, expected), rowBytes, file.BigEndian);
            }
            catch
            {
                if (pooled)
                {
                    ArrayPool<byte>.Shared.Return(raw);
                }

                throw;
            }

            return raw;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(input);
        }
    }

    /// <summary>
    /// Refuses blocks whose claimed raw size is out of proportion to the data present, before the raw buffer is allocated,
    /// so a tiny file cannot make the decoder reserve a huge strip.
    /// </summary>
    private static void CheckSizes(TiffFrame f, long count, int rows, int expected, ImageLimits limits)
    {
        switch (f.Codec)
        {
            case TiffCodec.None:
                if (count < expected)
                {
                    throw new ImageFormatException("A TIFF strip or tile holds less data than its size requires.");
                }

                return;
            case TiffCodec.CcittRle or TiffCodec.CcittG3 or TiffCodec.CcittG4:
                // Every row costs at least one bit, however empty it is.
                if (rows > count * 8)
                {
                    throw new ImageFormatException("A CCITT strip holds fewer codes than it has rows.");
                }

                return;
            case TiffCodec.PackBits:
                // A PackBits pair describes at most 128 bytes.
                if (expected > (count * 64) + 128)
                {
                    throw new ImageFormatException("A PackBits strip is too short for its size.");
                }

                return;
            default:
                if (double.IsFinite(limits.MaxExpansionRatio) && expected > (limits.MaxExpansionRatio * count) + 65536)
                {
                    throw new ImageLimitException("A compressed TIFF strip or tile would expand beyond the allowed ratio.");
                }

                return;
        }
    }

    private static void Decompress(TiffFrame f, ReadOnlySpan<byte> input, Span<byte> raw, int rows, int rowBytes, ImageLimits limits, CancellationToken ct)
    {
        switch (f.Codec)
        {
            case TiffCodec.None:
                if (input.Length < raw.Length)
                {
                    throw new ImageFormatException("A TIFF strip or tile holds less data than its size requires.");
                }

                input[..raw.Length].CopyTo(raw);
                break;

            case TiffCodec.Lzw:
            {
                // Old-style (pre-6.0) streams start with a clear code packed least significant bit first: 0x00, then a byte with bit 0 set.
                bool oldStyle = input.Length >= 2 && input[0] == 0 && (input[1] & 1) == 1;
                LzwVariant variant = oldStyle ? LzwVariant.Gif : LzwVariant.Tiff;
                LzwDecoder d = CodecCache.RentLzwDecoder(variant, 8, raw.Length + 8192L, limits.MaxExpansionRatio);
                Pump(input, raw, (i, o, out c, out w) => d.Decode(i, o, out c, out w));
                CodecCache.Return(d, variant);
                break;
            }

            case TiffCodec.Deflate:
            {
                Inflater inf = CodecCache.RentInflater(zlib: true, raw.Length + 8192L, limits.MaxExpansionRatio);
                Pump(input, raw, (i, o, out c, out w) => inf.Inflate(i, o, out c, out w));
                CodecCache.Return(inf);
                break;
            }

            case TiffCodec.PackBits:
                if (TiffKernels.PackBitsDecode(input, raw) < raw.Length)
                {
                    throw new ImageFormatException("A PackBits strip ended early.");
                }

                break;

            case TiffCodec.CcittRle:
            case TiffCodec.CcittG3:
            case TiffCodec.CcittG4:
                TiffCcitt.Decode(input, f.BlockWidth, rows, f.Codec, f.T4Options, raw, ct);
                // Like libtiff, the decoded bits are the run colors (1 = black run) whatever the photometric tag says; the tag
                // then decides what a 1 sample means, exactly as for uncompressed data.
                break;

            case TiffCodec.Jpeg:
            case TiffCodec.OldJpeg:
                TiffJpegSeam.DecodeBlock(f.Codec == TiffCodec.Jpeg ? f.JpegTables : null, input, f.BlockWidth, rows, f.SamplesPerPixel, f.YCbCr, raw);
                break;

            default:
                throw new ImageFormatException("Unsupported TIFF compression.");
        }

        _ = rowBytes;
    }

    private delegate InflateStatus Step(ReadOnlySpan<byte> input, Span<byte> output, out int consumed, out int written);

    private static void Pump(ReadOnlySpan<byte> input, Span<byte> raw, Step step)
    {
        int ip = 0;
        int op = 0;
        while (op < raw.Length)
        {
            InflateStatus s = step(input[ip..], raw[op..], out int c, out int w);
            ip += c;
            op += w;
            if (op >= raw.Length)
            {
                return;
            }

            if (s == InflateStatus.Done || (s == InflateStatus.NeedInput && ip >= input.Length) || (c == 0 && w == 0))
            {
                throw new ImageFormatException("A compressed TIFF strip or tile ended before all of its data was decoded.");
            }
        }
    }

    private static void Undo(TiffFrame f, Span<byte> raw, int rowBytes, bool bigEndian)
    {
        if (f.Predictor == 2)
        {
            TiffKernels.UndoHorizontal(raw, rowBytes, f.SamplesPerBlockPixel, f.Bits, bigEndian);
        }
        else if (f.Predictor == 3)
        {
            byte[] scratch = ArrayPool<byte>.Shared.Rent(rowBytes);
            try
            {
                TiffKernels.UndoFloat(raw, rowBytes, f.SamplesPerBlockPixel, scratch);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(scratch);
            }
        }
    }
}
