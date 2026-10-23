namespace OneImage.Codecs.Jbig2;

/// <summary>A decoded JBIG2 page: a bi-level bitmap plus the resolution the stream states.</summary>
internal sealed class Jbig2Page
{
    internal Jbig2Page(int width, int height, byte[] data, uint xResolution, uint yResolution)
    {
        Width = width;
        Height = height;
        Stride = (width + 7) >> 3;
        Data = data;
        XResolution = xResolution;
        YResolution = yResolution;
    }

    /// <summary>Gets the width in pixels.</summary>
    public int Width { get; }

    /// <summary>Gets the height in pixels.</summary>
    public int Height { get; }

    /// <summary>Gets the bytes per row.</summary>
    public int Stride { get; }

    /// <summary>
    /// Gets the pixels: <see cref="Height"/> rows of <see cref="Stride"/> bytes, most significant bit first, the unused
    /// bits at the end of each row zero. A set bit is a black pixel (JBIG2's convention: 1 is the foreground).
    /// </summary>
    public byte[] Data { get; }

    /// <summary>Gets the horizontal resolution in pixels per metre, or 0 when the stream does not say.</summary>
    public uint XResolution { get; }

    /// <summary>Gets the vertical resolution in pixels per metre, or 0 when the stream does not say.</summary>
    public uint YResolution { get; }
}

/// <summary>
/// The JBIG2 (ITU-T T.88 | ISO/IEC 14492) decoder. It is decode-only and internal: the PDF bridge reaches it for
/// <c>/JBIG2Decode</c> images, and it is not part of the default <c>ImageConfiguration</c>.
/// </summary>
/// <remarks>
/// <para>Supported: the embedded stream organisation of PDF (segments only, optionally preceded by a globals stream) and
/// the standalone file organisations (sequential and random access). Generic regions (MMR; arithmetic templates 0 to 3
/// with typical prediction and any adaptive pixels), generic refinement regions, symbol dictionaries (arithmetic and
/// Huffman, refinement/aggregate coding, retained contexts), text regions (arithmetic and Huffman, refinement, all
/// reference corners and transposition), pattern dictionaries, halftone regions, custom and standard Huffman tables,
/// intermediate and immediate regions, striped pages (also of unknown height) and every combination operator.</para>
/// <para>Not supported: the extended generic template with twelve adaptive pixels (a later amendment), colour
/// extensions, and the lossless profile checks. Extension and profile segments are skipped.</para>
/// <para>Every size is checked against <see cref="ImageLimits"/> and <see cref="Jbig2Limits"/> before it is allocated.
/// Malformed data throws <see cref="ImageFormatException"/>; limits throw <see cref="ImageLimitException"/>.</para>
/// </remarks>
internal static class Jbig2Codec
{
    private static readonly byte[] s_fileId = [0x97, 0x4A, 0x42, 0x32, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>
    /// Decodes the page of a JBIG2 stream embedded in another file, as PDF stores it: a sequence of segments with no
    /// file header, preceded by the segments of a shared globals stream when there is one.
    /// </summary>
    /// <param name="data">The embedded stream (the image's own segments).</param>
    /// <param name="globals">The globals stream (<c>/JBIG2Globals</c>), or empty.</param>
    /// <param name="limits">Page size limits; <see cref="ImageLimits.Default"/> when null.</param>
    /// <param name="jbig2Limits">JBIG2-specific limits; <see cref="Jbig2Limits.Default"/> when null.</param>
    /// <param name="cancellationToken">Observed once per row of the larger regions.</param>
    /// <returns>The first page of the stream.</returns>
    /// <exception cref="ImageFormatException">The data is malformed or has no page.</exception>
    /// <exception cref="ImageLimitException">A limit is exceeded.</exception>
    /// <exception cref="OperationCanceledException">Cancelled.</exception>
    public static Jbig2Page Decode(ReadOnlyMemory<byte> data, ReadOnlyMemory<byte> globals, ImageLimits? limits = null, Jbig2Limits? jbig2Limits = null, CancellationToken cancellationToken = default)
    {
        var session = new Jbig2Session(new Jbig2Work(limits ?? ImageLimits.Default, jbig2Limits ?? Jbig2Limits.Default, cancellationToken), 0);
        if (!globals.IsEmpty)
        {
            session.ProcessSequential(globals.ToArray(), 0);
        }

        session.ProcessSequential(data.ToArray(), 0);
        return session.Finish();
    }

    /// <summary>
    /// Decodes one page of a standalone JBIG2 file (sequential or random-access organisation, with the file header).
    /// </summary>
    /// <param name="file">The file.</param>
    /// <param name="pageNumber">The page to decode, starting at 1.</param>
    /// <param name="limits">Page size limits; <see cref="ImageLimits.Default"/> when null.</param>
    /// <param name="jbig2Limits">JBIG2-specific limits; <see cref="Jbig2Limits.Default"/> when null.</param>
    /// <param name="cancellationToken">Observed once per row of the larger regions.</param>
    /// <exception cref="ImageFormatException">The data is malformed or the page does not exist.</exception>
    /// <exception cref="ImageLimitException">A limit is exceeded.</exception>
    public static Jbig2Page DecodeFile(ReadOnlyMemory<byte> file, int pageNumber = 1, ImageLimits? limits = null, Jbig2Limits? jbig2Limits = null, CancellationToken cancellationToken = default)
    {
        byte[] buf = file.ToArray();
        ArgumentOutOfRangeException.ThrowIfLessThan(pageNumber, 1);

        if (buf.Length < 9 || !buf.AsSpan(0, 8).SequenceEqual(s_fileId))
        {
            throw new ImageFormatException("The data does not start with the JBIG2 file header.");
        }

        int flags = buf[8];
        int pos = 9;
        if ((flags & 2) == 0)
        {
            pos += 4;
        }

        if (pos > buf.Length)
        {
            throw new ImageFormatException("The JBIG2 file header is truncated.");
        }

        var session = new Jbig2Session(new Jbig2Work(limits ?? ImageLimits.Default, jbig2Limits ?? Jbig2Limits.Default, cancellationToken), pageNumber);
        if ((flags & 1) != 0)
        {
            session.ProcessSequential(buf, pos);
        }
        else
        {
            session.ProcessRandomAccess(buf, pos);
        }

        return session.Finish();
    }

    /// <summary>
    /// Reads the size of the first page of an embedded stream from its page information segment, without decoding any
    /// region. Returns false when the stream has none within the data given; the height is 0 when the stream does not
    /// state it up front.
    /// </summary>
    /// <param name="data">The embedded stream.</param>
    /// <param name="width">Receives the page width.</param>
    /// <param name="height">Receives the page height, or 0 if unknown.</param>
    public static bool TryReadPageSize(ReadOnlySpan<byte> data, out int width, out int height)
    {
        width = 0;
        height = 0;
        int pos = 0;
        for (int guard = 0; guard < 1 << 12 && pos < data.Length; guard++)
        {
            if (!Jbig2Session.TryParseHeader(data, pos, out Jbig2SegmentHeader h) || h.DataLength < 0)
            {
                return false;
            }

            long end = h.DataStart + h.DataLength;
            if (h.Type == 48)
            {
                if (end > data.Length || h.DataLength < 8)
                {
                    return false;
                }

                uint w = (uint)((data[h.DataStart] << 24) | (data[h.DataStart + 1] << 16) | (data[h.DataStart + 2] << 8) | data[h.DataStart + 3]);
                uint hh = (uint)((data[h.DataStart + 4] << 24) | (data[h.DataStart + 5] << 16) | (data[h.DataStart + 6] << 8) | data[h.DataStart + 7]);
                if (w > int.MaxValue)
                {
                    return false;
                }

                width = (int)w;
                height = hh is 0xFFFFFFFF or > int.MaxValue ? 0 : (int)hh;
                return true;
            }

            if (end > data.Length)
            {
                return false;
            }

            pos = (int)end;
        }

        return false;
    }
}
