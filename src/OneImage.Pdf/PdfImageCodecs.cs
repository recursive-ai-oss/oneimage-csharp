using OneImage.Codecs.Jpeg;
using OneImage.Pdf.Render;
using OnePdf;

namespace OneImage.Pdf;

/// <summary>
/// Registers the image decoders OnePDF calls for the image codecs it does not implement itself. DCT (JPEG) is OneImage's own decoder; the
/// JPEG 2000 and JBIG2 decoders attach through <see cref="Jpx"/> and <see cref="Jbig2"/> when OneImage has them.
/// </summary>
public static class PdfImageCodecs
{
    private static IPdfImageDecoder? s_jpx;
    private static IPdfImageDecoder? s_jbig2;

    /// <summary>
    /// Gets or sets the decoder used for <c>JPXDecode</c> images. <see langword="null"/> (the default) draws no JPEG 2000 images: OnePDF hands
    /// the device the undecoded data, which this package does not show. Set it before the first <see cref="PdfFormat"/> is created or call
    /// <see cref="Register"/> afterwards.
    /// </summary>
    public static IPdfImageDecoder? Jpx
    {
        get => Volatile.Read(ref s_jpx);
        set => Volatile.Write(ref s_jpx, value);
    }

    /// <summary>Gets or sets the decoder used for <c>JBIG2Decode</c> images; see <see cref="Jpx"/>.</summary>
    public static IPdfImageDecoder? Jbig2
    {
        get => Volatile.Read(ref s_jbig2);
        set => Volatile.Write(ref s_jbig2, value);
    }

    /// <summary>
    /// Registers this package's decoders (DCT always, JPX and JBIG2 when set) in <paramref name="registry"/>, replacing what was registered for
    /// those filters.
    /// </summary>
    /// <param name="registry">The registry to fill, for example <see cref="PdfImageDecoderRegistry.Default"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="registry"/> is null.</exception>
    public static void Register(PdfImageDecoderRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        registry.Register("DCTDecode", DctImageDecoder.Instance);
        if (Jpx is { } jpx)
        {
            registry.Register("JPXDecode", jpx);
        }

        if (Jbig2 is { } jbig2)
        {
            registry.Register("JBIG2Decode", jbig2);
        }
    }

    /// <summary>Fills the shared registry with what is missing, never replacing a decoder the application registered itself.</summary>
    internal static void RegisterDefaults()
    {
        PdfImageDecoderRegistry shared = PdfImageDecoderRegistry.Default;
        if (!shared.TryGet("DCTDecode", out _))
        {
            shared.Register("DCTDecode", DctImageDecoder.Instance);
        }

        if (Jpx is { } jpx && !shared.TryGet("JPXDecode", out _))
        {
            shared.Register("JPXDecode", jpx);
        }

        if (Jbig2 is { } jbig2 && !shared.TryGet("JBIG2Decode", out _))
        {
            shared.Register("JBIG2Decode", jbig2);
        }
    }
}

/// <summary>OneImage's JPEG decoder behind OnePDF's decoder contract: components as stored after the color transform, nothing inverted.</summary>
internal sealed class DctImageDecoder : IPdfImageDecoder
{
    public static DctImageDecoder Instance { get; } = new();

    public PdfDecodedImage? Decode(in PdfImageDecodeRequest request, CancellationToken cancellationToken)
    {
        try
        {
            return DecodeCore(request, cancellationToken);
        }
        catch (ImageException)
        {
            // OnePDF records the failure and shows the image as undecodable.
            return null;
        }
    }

    private static PdfDecodedImage? DecodeCore(in PdfImageDecodeRequest request, CancellationToken ct)
    {
        byte[] data = request.Data.ToArray();
        ImageLimits limits = ImageLimits.Default;
        JpegHeaders headers;
        using (var ms = new MemoryStream(data))
        {
            JpegReader reader = new(ms);
            try
            {
                headers = JpegParser.ReadHeaders(reader, limits, new JpegTables(), collectMetadata: false);
            }
            finally
            {
                reader.Dispose();
            }
        }

        JpegFrame f = headers.Frame ?? throw new ImageFormatException("The JPEG has no frame.");
        int comps = f.Components.Length;
        if (comps is not (1 or 3 or 4))
        {
            return null;
        }

        // ISO 32000-2 7.4.8: the Adobe marker, when present, decides the transform; otherwise /ColorTransform; otherwise YCC for three components.
        int adobe = headers.AdobeTransform;
        bool transform = adobe >= 0 ? adobe != 0 : request.ColorTransform is { } ct1 ? ct1 != 0 : comps == 3;
        JpegColorPlan Plan(JpegFrame fr, JpegHeaders hd) => comps switch
        {
            1 => new JpegColorPlan(JpegColorKind.Gray, false),
            3 => new JpegColorPlan(transform ? JpegColorKind.YCbCr : JpegColorKind.Rgb, false),
            _ => new JpegColorPlan(transform ? JpegColorKind.Ycck : JpegColorKind.Cmyk, false),
        };

        int precision = f.Precision;
        PixelFormat format = Plan(f, headers).Format(precision);
        limits.CheckFrame(f.Width, f.Height);
        var sink = new CollectSink(f.Width, f.Height, format);
        using (var ms = new MemoryStream(data))
        {
            JpegDecoderCore.Decode(ms, new DecodeRequest(new ImageRect(0, 0, f.Width, f.Height)), sink, limits, ct, Plan);
        }

        if (sink.Rows != f.Height)
        {
            return null;
        }

        byte[] samples = sink.Data;
        int bits = 8;
        if (precision > 8)
        {
            // 12-bit samples are delivered as 16-bit little-endian values widened to the full range; PDF wants big-endian 16-bit.
            bits = 16;
            for (int i = 0; i + 1 < samples.Length; i += 2)
            {
                (samples[i], samples[i + 1]) = (samples[i + 1], samples[i]);
            }
        }

        return new PdfDecodedImage(f.Width, f.Height, comps, bits, samples)
        {
            AdobeTransform = adobe >= 0 ? adobe : null,
        };
    }

    private sealed class CollectSink : IRowSink
    {
        public CollectSink(int width, int height, PixelFormat format)
        {
            Width = width;
            Height = height;
            Format = format;
            Data = new byte[checked((long)width * height * format.BytesPerPixel)];
        }

        public int Width { get; }

        public int Height { get; }

        public PixelFormat Format { get; }

        public byte[] Data { get; }

        public int Rows { get; private set; }

        public void WriteRows(int y, ReadOnlySpan<byte> rows, int rowCount)
        {
            int rowBytes = Width * Format.BytesPerPixel;
            if (y != Rows || rowCount < 0 || rowCount > Height - y)
            {
                throw new ImageOperationException("JPEG rows arrived out of order.");
            }

            rows[..(rowCount * rowBytes)].CopyTo(Data.AsSpan(y * rowBytes));
            Rows += rowCount;
        }
    }
}
