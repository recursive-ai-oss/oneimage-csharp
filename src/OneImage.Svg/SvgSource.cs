using System.Buffers.Binary;
using OneImage.Compression;
using OneImage.Svg.Xml;

namespace OneImage.Svg;

/// <summary>Reads the bytes of a document from a stream, undoing gzip compression (<c>.svgz</c>) with the library's own inflate.</summary>
internal static class SvgSource
{
    /// <summary>Returns the document bytes, decompressed when the data is gzip. Larger documents fail with <see cref="ImageLimitException"/>.</summary>
    /// <param name="source">A stream positioned at the first byte.</param>
    /// <param name="limits">The SVG limits.</param>
    /// <param name="imageLimits">The expansion-ratio limit applies to gzip data.</param>
    /// <param name="prefixBytes">When positive, only the first this many (decompressed) bytes are wanted, and a longer document is not an error.</param>
    /// <param name="truncated">Set when a prefix was requested and the document is longer.</param>
    public static byte[] Read(Stream source, SvgLimits limits, ImageLimits imageLimits, int prefixBytes, out bool truncated)
    {
        // A gzip file may be smaller than its content but never larger than the cap, so the same cap bounds the input read.
        int cap = limits.MaxDocumentBytes;
        bool prefixOnly = prefixBytes > 0;
        int maxBytes = prefixOnly ? Math.Min(prefixBytes, cap) : cap;
        int readLimit = prefixOnly ? Math.Min(cap, Math.Max(maxBytes, 64 * 1024)) : cap;
        byte[] raw = ReadAll(source, readLimit, out bool inputTruncated);
        if (!IsGzip(raw))
        {
            truncated = inputTruncated || raw.Length > maxBytes;
            if (!prefixOnly && inputTruncated)
            {
                throw new ImageLimitException($"The document is larger than {cap} bytes.");
            }

            return raw.Length > maxBytes ? raw.AsSpan(0, maxBytes).ToArray() : raw;
        }

        return Gunzip(raw, maxBytes, imageLimits.MaxExpansionRatio, !prefixOnly, out truncated);
    }

    public static bool IsGzip(ReadOnlySpan<byte> data) => data.Length >= 3 && data[0] == 0x1F && data[1] == 0x8B && data[2] == 8;

    private static byte[] ReadAll(Stream source, int limit, out bool truncated)
    {
        byte[] buffer = new byte[source.CanSeek ? (int)Math.Min(Math.Max(source.Length - source.Position, 0) + 1, (long)limit + 1) : 8192];
        int len = 0;
        truncated = false;
        while (true)
        {
            if (len == buffer.Length)
            {
                if (len > limit)
                {
                    break;
                }

                Array.Resize(ref buffer, (int)Math.Min((long)len * 2, (long)limit + 1));
            }

            int n = source.Read(buffer, len, buffer.Length - len);
            if (n == 0)
            {
                break;
            }

            len += n;
            if (len > limit)
            {
                truncated = true;
                len = limit;
                break;
            }
        }

        return len == buffer.Length ? buffer : buffer.AsSpan(0, len).ToArray();
    }

    /// <summary>
    /// Decompresses a single-member gzip stream (RFC 1952). With <paramref name="strict"/> the deflate data must be complete and
    /// the CRC-32 and size trailer must match as far as it is present (a missing or cut-short trailer is accepted); otherwise decoding
    /// stops quietly at <paramref name="maxBytes"/> or at the end of the input.
    /// </summary>
    internal static byte[] Gunzip(ReadOnlySpan<byte> gz, int maxBytes, double maxRatio, bool strict, out bool truncated)
    {
        int pos = HeaderLength(gz);
        truncated = false;
        using var inflater = new Inflater(zlib: false, long.MaxValue, maxRatio);
        byte[] buf = new byte[Math.Min(maxBytes + 1L, Math.Max(64 * 1024L, gz.Length * 4L))];
        int len = 0;
        InflateStatus status;
        while (true)
        {
            if (len == buf.Length)
            {
                if (len > maxBytes)
                {
                    break;
                }

                Array.Resize(ref buf, (int)Math.Min((long)len * 2, (long)maxBytes + 1));
            }

            status = inflater.Inflate(gz[pos..], buf.AsSpan(len), out int consumed, out int written);
            pos += consumed;
            len += written;
            if (status == InflateStatus.Done)
            {
                break;
            }

            if (status == InflateStatus.NeedInput)
            {
                if (strict)
                {
                    throw new ImageFormatException("The gzip stream is truncated.");
                }

                return buf.AsSpan(0, len).ToArray();
            }

            if (!strict && len >= maxBytes)
            {
                truncated = true;
                return buf.AsSpan(0, maxBytes).ToArray();
            }
        }

        if (len > maxBytes)
        {
            if (!strict)
            {
                truncated = true;
                return buf.AsSpan(0, maxBytes).ToArray();
            }

            throw new ImageLimitException($"The decompressed document is larger than {maxBytes} bytes.");
        }

        if (strict)
        {
            // The deflate data ended properly, so the document is complete. A trailer that is missing or cut short is accepted
            // (other readers show such files); the parts that are present must match.
            ReadOnlySpan<byte> trailer = gz[pos..];
            if ((trailer.Length >= 4 && BinaryPrimitives.ReadUInt32LittleEndian(trailer) != Crc32.Update(0, buf.AsSpan(0, len)))
                || (trailer.Length >= 8 && BinaryPrimitives.ReadUInt32LittleEndian(trailer[4..]) != (uint)len))
            {
                throw new ImageFormatException("The gzip checksum does not match.");
            }
        }

        return buf.AsSpan(0, len).ToArray();
    }

    // RFC 1952 section 2.3: fixed 10 bytes, then optional extra field, file name, comment and header CRC.
    private static int HeaderLength(ReadOnlySpan<byte> gz)
    {
        if (gz.Length < 10 || gz[0] != 0x1F || gz[1] != 0x8B || gz[2] != 8)
        {
            throw new ImageFormatException("Not a gzip stream.");
        }

        int flags = gz[3];
        if ((flags & 0xE0) != 0)
        {
            throw new ImageFormatException("Unsupported gzip flags.");
        }

        int p = 10;
        if ((flags & 4) != 0)
        {
            if (gz.Length < p + 2)
            {
                throw new ImageFormatException("The gzip header is truncated.");
            }

            p += 2 + BinaryPrimitives.ReadUInt16LittleEndian(gz[p..]);
        }

        for (int bit = 8; bit <= 16; bit <<= 1)
        {
            if ((flags & bit) != 0)
            {
                int zero = p <= gz.Length ? gz[p..].IndexOf((byte)0) : -1;
                if (zero < 0)
                {
                    throw new ImageFormatException("The gzip header is truncated.");
                }

                p += zero + 1;
            }
        }

        if ((flags & 2) != 0)
        {
            p += 2;
        }

        return p <= gz.Length ? p : throw new ImageFormatException("The gzip header is truncated.");
    }
}
