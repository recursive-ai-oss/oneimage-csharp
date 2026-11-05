using System.Buffers.Binary;

namespace OneImage.Metafile;

/// <summary>
/// Reassembles an enhanced metafile that a Windows metafile carries in META_ESCAPE / MFCOMMENT records ([MS-WMF] 2.3.6.25): the EMF is cut
/// into chunks of at most 8,192 bytes, one chunk per record, each saying how many bytes remain after it.
/// </summary>
internal static class EmbeddedEmf
{
    private const uint Ident = 0x43464D57; // "WMFC"
    private const int DataAt = 44;

    /// <summary>Returns the EMF, or null when there is none or it is damaged (then <paramref name="problem"/> says why).</summary>
    public static byte[]? Find(ReadOnlySpan<byte> d, int offset, MetafileLimits limits, out string? problem)
    {
        problem = null;
        byte[]? emf = null;
        long total = 0, filled = 0;
        long records = 0;
        int off = offset;
        while (off + 6 <= d.Length)
        {
            uint words = BinaryPrimitives.ReadUInt32LittleEndian(d[off..]);
            int function = BinaryPrimitives.ReadUInt16LittleEndian(d[(off + 4)..]);
            if (words < 3 || (ulong)off + (words * 2UL) > (ulong)d.Length || function == 0)
            {
                break;
            }

            ReadOnlySpan<byte> r = d.Slice(off, (int)(words * 2));
            off += r.Length;
            if (function != 0x0626 || r.Length < DataAt || BinaryPrimitives.ReadUInt16LittleEndian(r[6..]) != 0x000F
                || BinaryPrimitives.ReadUInt32LittleEndian(r[10..]) != Ident || BinaryPrimitives.ReadUInt32LittleEndian(r[14..]) != 1)
            {
                continue;
            }

            records++;
            uint count = BinaryPrimitives.ReadUInt32LittleEndian(r[28..]);
            uint current = BinaryPrimitives.ReadUInt32LittleEndian(r[32..]);
            uint remaining = BinaryPrimitives.ReadUInt32LittleEndian(r[36..]);
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(r[40..]);
            if (emf is null)
            {
                if (size > limits.MaxEmbeddedEmfBytes)
                {
                    throw new ImageLimitException($"The embedded enhanced metafile is larger than {limits.MaxEmbeddedEmfBytes} bytes.");
                }

                if (size < 88 || count == 0)
                {
                    problem = "The embedded enhanced metafile declares an impossible size; it is ignored.";
                    return null;
                }

                total = size;
                emf = new byte[size];
            }

            // Every chunk must agree with the first about the whole, carry the bytes it says, and leave exactly the remainder.
            if (size != total || current > r.Length - DataAt || filled + current > total || remaining != total - filled - current || records > count)
            {
                problem = "The chunks of the embedded enhanced metafile are inconsistent; it is ignored.";
                return null;
            }

            r.Slice(DataAt, (int)current).CopyTo(emf.AsSpan((int)filled));
            filled += current;
            if (filled == total)
            {
                break;
            }
        }

        if (emf is null)
        {
            return null;
        }

        if (filled != total || !MetafileDocument.IsEmf(emf))
        {
            problem = "The embedded enhanced metafile is incomplete or not an enhanced metafile; it is ignored.";
            return null;
        }

        return emf;
    }
}
