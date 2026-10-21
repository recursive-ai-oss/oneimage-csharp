namespace OneImage.Codecs.Tiff;

/// <summary>
/// Decodes a complete baseline JPEG stream into interleaved 8-bit samples.
/// </summary>
/// <param name="jpeg">The complete stream.</param>
/// <param name="width">Expected width in pixels.</param>
/// <param name="height">Expected height in pixels.</param>
/// <param name="components">Expected number of components.</param>
/// <param name="ycbcrToRgb">Convert three YCbCr components to RGB (as libjpeg does for TIFF's YCbCr photometric); CMYK/YCCK is delivered unconverted.</param>
/// <param name="destination">Receives <c>width * height * components</c> bytes. A stream whose size or component count disagrees is an <see cref="ImageFormatException"/>.</param>
internal delegate void JpegBlockDecoder(ReadOnlySpan<byte> jpeg, int width, int height, int components, bool ycbcrToRgb, Span<byte> destination);

/// <summary>
/// The seam between the TIFF codec and the JPEG codec. TIFF-specific work (merging the shared JPEGTables into each
/// strip's abbreviated stream) lives here; turning a complete JPEG stream into samples is delegated to
/// <see cref="DecodeJpeg"/>, which is a stub until the JPEG codec is wired in.
/// </summary>
internal static class TiffJpegSeam
{
    /// <summary>Gets or sets the JPEG decoder used for compression 7 and 6; null until the JPEG codec is connected.</summary>
    internal static JpegBlockDecoder? DecodeJpeg { get; set; }

    /// <summary>Gets whether a JPEG decoder is wired in.</summary>
    internal static bool IsAvailable => DecodeJpeg is not null;

    /// <summary>Decodes one JPEG-compressed strip, tile or whole old-style stream.</summary>
    public static void DecodeBlock(byte[]? tables, ReadOnlySpan<byte> data, int width, int height, int components, bool ycbcrToRgb, Span<byte> destination)
    {
        JpegBlockDecoder? hook = DecodeJpeg;
        if (hook is null)
        {
            throw new ImageFormatException("JPEG-compressed TIFF is not supported yet: no JPEG decoder is available.");
        }

        if (tables is null || tables.Length < 4)
        {
            hook(data, width, height, components, ycbcrToRgb, destination);
            return;
        }

        hook(Merge(tables, data), width, height, components, ycbcrToRgb, destination);
    }

    /// <summary>
    /// Builds one complete stream from the shared tables (SOI, DQT/DHT, EOI) and a strip's abbreviated stream (SOI, frame, scan, EOI):
    /// the tables' SOI and the strip's SOI are each kept once and the tables' trailing EOI is dropped.
    /// </summary>
    internal static byte[] Merge(ReadOnlySpan<byte> tables, ReadOnlySpan<byte> data)
    {
        if (tables.Length >= 2 && tables[^2] == 0xFF && tables[^1] == 0xD9)
        {
            tables = tables[..^2];
        }

        if (data.Length >= 2 && data[0] == 0xFF && data[1] == 0xD8)
        {
            data = data[2..];
        }

        byte[] merged = new byte[tables.Length + data.Length];
        tables.CopyTo(merged);
        data.CopyTo(merged.AsSpan(tables.Length));
        return merged;
    }
}
