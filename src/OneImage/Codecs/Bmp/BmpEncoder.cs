using System.Buffers;
using System.Buffers.Binary;

namespace OneImage;

/// <summary>
/// BMP encoder. Writes bottom-up bitmaps (the most widely understood layout): 24 bits per pixel for opaque images
/// and 32 bits per pixel with a V4 header and BGRA masks when some pixel is not opaque. Gray images are written as
/// 24-bit color. An embedded ICC profile switches to a V5 header with the profile stored after the pixels.
/// </summary>
/// <remarks>
/// RGBA input that turns out to be fully opaque is written as 24-bit RGB, so it reads back as RGB. The rows are
/// requested from the bottom of the image upward.
/// </remarks>
internal sealed class BmpEncoder : IImageEncoder
{
    public static BmpEncoder Instance { get; } = new();

    public IReadOnlyList<PixelFormat> SupportedFormats { get; } = [PixelFormat.Rgba8, PixelFormat.Rgb8];

    public bool SupportsMultipleFrames => false;

    public void Encode(ImageEncodeRequest request, Stream destination, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(destination);
        ImageFrameReader f = request.Frames[0];
        WriteBmp(destination, f.ReadRows, f.Width, f.Height, f.Format, request.Metadata, includeFileHeader: true, cancellationToken);
    }

    /// <summary>
    /// Writes a BMP (or, for icons, a bare DIB with the file header omitted). For icons the stored height is doubled
    /// and the AND mask is appended.
    /// </summary>
    internal static void WriteBmp(Stream dest, PngRowReader rows, int width, int height, PixelFormat format, ImageMetadata meta, bool includeFileHeader, CancellationToken ct, bool icon = false)
    {
        bool hasAlpha = format == PixelFormat.Rgba8 && (icon || ScanAlpha(rows, width, height, ct));
        int bpp = hasAlpha ? 32 : 24;
        int srcBpp = format.BytesPerPixel;
        long stride = Stride(width, bpp);
        bool v5 = !icon && meta.IccProfile.Length > 0;
        int headerSize = v5 ? 124 : hasAlpha ? 108 : 40;
        if (icon)
        {
            headerSize = 40;
        }

        long pixelBytes = stride * height;
        long maskStride = icon ? Stride(width, 1) : 0;
        long maskBytes = maskStride * height;
        long iccBytes = v5 ? meta.IccProfile.Length : 0;
        long fileHeader = includeFileHeader ? 14 : 0;
        long dataOffset = fileHeader + headerSize;
        long total = dataOffset + pixelBytes + maskBytes + iccBytes;
        if (total > uint.MaxValue)
        {
            throw new ImageLimitException("The image is too large for a BMP file.");
        }

        // The size is known now, so a growable MemoryStream is sized once instead of doubling (and copying) its way up; on a
        // fixed-size one the writes below would fail the same way.
        if (dest is MemoryStream { CanWrite: true } ms && total <= Array.MaxLength - ms.Position && ms.Capacity < ms.Position + total)
        {
            ms.Capacity = (int)(ms.Position + total);
        }

        byte[] head = new byte[dataOffset];
        if (includeFileHeader)
        {
            head[0] = (byte)'B';
            head[1] = (byte)'M';
            BinaryPrimitives.WriteUInt32LittleEndian(head.AsSpan(2), (uint)total);
            BinaryPrimitives.WriteUInt32LittleEndian(head.AsSpan(10), (uint)dataOffset);
        }

        Span<byte> h = head.AsSpan((int)fileHeader);
        BinaryPrimitives.WriteUInt32LittleEndian(h, (uint)headerSize);
        BinaryPrimitives.WriteInt32LittleEndian(h[4..], width);
        BinaryPrimitives.WriteInt32LittleEndian(h[8..], icon ? height * 2 : height);
        BinaryPrimitives.WriteUInt16LittleEndian(h[12..], 1);
        BinaryPrimitives.WriteUInt16LittleEndian(h[14..], (ushort)bpp);
        BinaryPrimitives.WriteUInt32LittleEndian(h[16..], headerSize >= 108 && hasAlpha ? 3u : 0u);
        BinaryPrimitives.WriteUInt32LittleEndian(h[20..], (uint)(pixelBytes + maskBytes));
        if (headerSize >= 108)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(h[40..], 0x00FF0000);
            BinaryPrimitives.WriteUInt32LittleEndian(h[44..], 0x0000FF00);
            BinaryPrimitives.WriteUInt32LittleEndian(h[48..], 0x000000FF);
            BinaryPrimitives.WriteUInt32LittleEndian(h[52..], hasAlpha ? 0xFF000000 : 0);
            // Color space type: 'MBED' for an embedded profile, else LCS_sRGB.
            BinaryPrimitives.WriteUInt32LittleEndian(h[56..], v5 ? 0x4D424544u : 0x73524742u);
            if (v5)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(h[104..], 0);
                BinaryPrimitives.WriteUInt32LittleEndian(h[108..], 4); // LCS_GM_IMAGES
                BinaryPrimitives.WriteUInt32LittleEndian(h[112..], (uint)(headerSize + pixelBytes));
                BinaryPrimitives.WriteUInt32LittleEndian(h[116..], (uint)iccBytes);
            }
        }

        dest.Write(head);

        int stripRows = Math.Max(1, Math.Min(height, (1 << 20) / Math.Max(1, width * srcBpp)));
        byte[] src = ArrayPool<byte>.Shared.Rent(stripRows * width * srcBpp);
        byte[] outBlock = ArrayPool<byte>.Shared.Rent((int)(stripRows * stride));
        byte[]? mask = icon ? ArrayPool<byte>.Shared.Rent((int)(maskBytes)) : null;
        try
        {
            // Bottom-up: the last image rows come first in the file.
            for (int yEnd = height; yEnd > 0; yEnd -= stripRows)
            {
                ct.ThrowIfCancellationRequested();
                int n = Math.Min(stripRows, yEnd);
                int y0 = yEnd - n;
                rows(y0, n, src);
                for (int i = 0; i < n; i++)
                {
                    ReadOnlySpan<byte> s = src.AsSpan((n - 1 - i) * width * srcBpp, width * srcBpp);
                    Span<byte> d = outBlock.AsSpan((int)(i * stride), (int)stride);
                    if (hasAlpha)
                    {
                        Kernels.BmpPixels.Swap4(s, d, width);
                    }
                    else if (srcBpp == 4)
                    {
                        Kernels.BmpPixels.Narrow4(s, d, width);
                    }
                    else
                    {
                        Kernels.BmpPixels.Swap3(s, d, width);
                    }

                    // Row padding (to a multiple of four bytes) is zero.
                    d[(width * (hasAlpha ? 4 : 3))..].Clear();

                    if (mask != null)
                    {
                        // AND mask row for the same image row: 1 = transparent.
                        int fileRow = height - 1 - (y0 + (n - 1 - i));
                        Span<byte> m = mask.AsSpan((int)(fileRow * maskStride), (int)maskStride);
                        m.Clear();
                        if (hasAlpha)
                        {
                            for (int x = 0; x < width; x++)
                            {
                                if (s[(x * 4) + 3] == 0)
                                {
                                    m[x >> 3] |= (byte)(0x80 >> (x & 7));
                                }
                            }
                        }
                    }
                }

                dest.Write(outBlock, 0, (int)(n * stride));
            }

            if (mask != null)
            {
                dest.Write(mask, 0, (int)maskBytes);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(src);
            ArrayPool<byte>.Shared.Return(outBlock);
            if (mask != null)
            {
                ArrayPool<byte>.Shared.Return(mask);
            }
        }

        if (v5)
        {
            dest.Write(meta.IccProfile.Span);
        }
    }

    private static long Stride(int width, int bpp) => ((((long)width * bpp) + 31) / 32) * 4;

    private static bool ScanAlpha(PngRowReader rows, int width, int height, CancellationToken ct)
    {
        int stripRows = Math.Max(1, Math.Min(height, (1 << 20) / (width * 4)));
        byte[] buf = ArrayPool<byte>.Shared.Rent(stripRows * width * 4);
        try
        {
            for (int y = 0; y < height; y += stripRows)
            {
                ct.ThrowIfCancellationRequested();
                int n = Math.Min(stripRows, height - y);
                rows(y, n, buf);
                ReadOnlySpan<byte> px = buf.AsSpan(0, n * width * 4);
                for (int i = 3; i < px.Length; i += 4)
                {
                    if (px[i] != 255)
                    {
                        return true;
                    }
                }
            }

            return false;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buf);
        }
    }
}
