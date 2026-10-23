namespace OneImage.Psd;

/// <summary>PackBits, the run-length coding of PSD scan lines (also the TIFF and Macintosh ROM scheme).</summary>
internal static class Rle
{
    /// <summary>Decodes exactly <paramref name="dst"/>.Length bytes; fails if the source ends first or a run passes the destination.</summary>
    public static void Unpack(ReadOnlySpan<byte> src, Span<byte> dst)
    {
        int i = 0;
        int o = 0;
        while (o < dst.Length)
        {
            if (i >= src.Length)
            {
                throw new ImageFormatException("A run-length coded scan line ends early.");
            }

            int n = (sbyte)src[i++];
            if (n >= 0)
            {
                int count = n + 1;
                if (count > src.Length - i || count > dst.Length - o)
                {
                    throw new ImageFormatException("A run-length coded scan line is malformed.");
                }

                src.Slice(i, count).CopyTo(dst.Slice(o));
                i += count;
                o += count;
            }
            else if (n != -128)
            {
                int count = 1 - n;
                if (i >= src.Length || count > dst.Length - o)
                {
                    throw new ImageFormatException("A run-length coded scan line is malformed.");
                }

                dst.Slice(o, count).Fill(src[i++]);
                o += count;
            }
        }
    }

    /// <summary>Encodes a scan line. <paramref name="dst"/> needs at least <c>src.Length + (src.Length + 127) / 128</c> bytes. Returns the encoded length.</summary>
    public static int Pack(ReadOnlySpan<byte> src, Span<byte> dst)
    {
        int i = 0;
        int o = 0;
        while (i < src.Length)
        {
            // A run of three or more equal bytes is worth a repeat packet; shorter runs stay inside literals.
            int run = 1;
            while (i + run < src.Length && run < 128 && src[i + run] == src[i])
            {
                run++;
            }

            if (run >= 3)
            {
                dst[o++] = (byte)(1 - run);
                dst[o++] = src[i];
                i += run;
                continue;
            }

            int start = i;
            i += run;
            while (i < src.Length && i - start < 128)
            {
                int r = 1;
                while (i + r < src.Length && r < 3 && src[i + r] == src[i])
                {
                    r++;
                }

                if (r >= 3)
                {
                    break;
                }

                i++;
            }

            int len = i - start;
            dst[o++] = (byte)(len - 1);
            src.Slice(start, len).CopyTo(dst.Slice(o));
            o += len;
        }

        return o;
    }
}
