using OneImage.Codecs.Jbig2;
using OneImage.Codecs.Jpeg;
using OneImage.Codecs.Jpx;
using OneImage.Pdf.Render;
using OnePdf;

namespace OneImage.Pdf;

/// <summary>
/// Registers the image decoders OnePDF calls for the image codecs it does not implement itself. DCT (JPEG) and JBIG2 are OneImage's own
/// decoders, and so is the JPEG 2000 one (reachable through <see cref="Jpx"/>).
/// </summary>
public static class PdfImageCodecs
{
    private static IPdfImageDecoder? s_jpx = JpxImageDecoder.Instance;
    private static IPdfImageDecoder? s_jbig2 = Jbig2ImageDecoder.Instance;

    /// <summary>
    /// Gets or sets the decoder used for <c>JPXDecode</c> images. The default is OneImage's own JPEG 2000 decoder; an image that is damaged
    /// or beyond the limits is not drawn (see <see cref="PdfFormat"/>) and the rest of the page is. Setting <see langword="null"/> draws no
    /// JPEG 2000 images: OnePDF then hands the device the undecoded data, which this package does not show.
    /// </summary>
    public static IPdfImageDecoder? Jpx
    {
        get => Volatile.Read(ref s_jpx);
        set => Volatile.Write(ref s_jpx, value);
    }

    /// <summary>
    /// Gets or sets the decoder used for <c>JBIG2Decode</c> images. The default is OneImage's own JBIG2 decoder; a stream that is damaged
    /// is not drawn (see <see cref="PdfFormat"/>) and the rest of the page is. Setting <see langword="null"/> draws no JBIG2 images.
    /// </summary>
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

/// <summary>
/// OneImage's JBIG2 decoder behind OnePDF's decoder contract. It returns the page as the standard stores it, one bit per pixel with 1 for
/// black: OnePDF applies the PDF inversion, <c>/Decode</c> and <c>/ImageMask</c> rules afterwards. The <c>/JBIG2Globals</c> segments are passed
/// separately. A damaged stream makes the decoder throw rather than give a partial page; here that is a null result, which OnePDF records as a
/// diagnostic, and the image is left out while the page carries on.
/// </summary>
internal sealed class Jbig2ImageDecoder : IPdfImageDecoder
{
    public static Jbig2ImageDecoder Instance { get; } = new();

    public PdfDecodedImage? Decode(in PdfImageDecodeRequest request, CancellationToken cancellationToken)
    {
        try
        {
            Jbig2Page page = Jbig2Codec.Decode(request.Data, request.Globals, ImageLimits.Default, null, cancellationToken);
            if (page.Width < 1 || page.Height < 1)
            {
                return null;
            }

            return new PdfDecodedImage(page.Width, page.Height, 1, 1, page.Data);
        }
        catch (ImageException)
        {
            return null;
        }
    }
}

/// <summary>
/// OneImage's JPEG 2000 decoder behind OnePDF's decoder contract. Colour channels come out in the file's colour space converted to one PDF
/// can express (sYCC to RGB; ICC-described RGB, gray and CMYK are converted to sRGB or gray here, because OnePDF cannot be handed a profile);
/// the opacity channel, if any, is last with <see cref="PdfDecodedImage.HasAlpha"/> set, and OnePDF applies <c>/SMaskInData</c>, <c>/Decode</c> and
/// masks. A file that is damaged inside its tile data gives no image at all (null), like the other decoders, so the page carries on without it.
/// </summary>
internal sealed class JpxImageDecoder : IPdfImageDecoder
{
    // An image beyond this many pixels is decoded at a reduced resolution (1/2, 1/4, ...): the page could not show the detail, and the
    // sample planes would otherwise run into the memory the render limits allow for one picture.
    private const long MaxFullPixels = 1L << 26;

    public static JpxImageDecoder Instance { get; } = new();

    public PdfDecodedImage? Decode(in PdfImageDecodeRequest request, CancellationToken cancellationToken)
    {
        try
        {
            return DecodeCore(request.Data, cancellationToken);
        }
        catch (ImageException)
        {
            return null;
        }
    }

    private static PdfDecodedImage? DecodeCore(ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        JpxInfo info = JpxCodec.Identify(data);
        int reduction = 0;
        long pixels = (long)info.Width * info.Height;
        while (pixels > MaxFullPixels && reduction < info.MaxReduction)
        {
            reduction++;
            pixels = (pixels + 3) / 4;
        }

        JpxImage image = JpxCodec.Decode(data, new JpxDecodeOptions { ReductionLevels = reduction }, ct);
        if (image.Damaged || image.Width < 1 || image.Height < 1)
        {
            return null;
        }

        int colors = image.ColorChannelCount;
        bool alpha = image.AlphaChannel >= 0 && image.AlphaChannel == colors && image.ChannelCount == colors + 1;
        if (colors < 1)
        {
            return null;
        }

        int depth = 0;
        for (int c = 0; c < image.ChannelCount; c++)
        {
            depth = Math.Max(depth, image.BitDepths[c]);
        }

        int bits = depth <= 8 ? 8 : 16;
        int total = colors + (alpha ? 1 : 0);
        PdfColorSpace? space = image.ColorSpace switch
        {
            JpxColorSpace.Srgb or JpxColorSpace.ESrgb => colors == 3 ? PdfColorSpace.Rgb : null,
            JpxColorSpace.Sycc => colors == 3 && image.ConvertedFromSycc ? PdfColorSpace.Rgb : null,
            JpxColorSpace.Gray => colors == 1 ? PdfColorSpace.Gray : null,
            JpxColorSpace.Cmyk => colors == 4 ? PdfColorSpace.Cmyk : null,
            _ => null,
        };

        byte[] samples = image.ToInterleaved(bits, total);
        if (image.ColorSpace == JpxColorSpace.Icc && colors is 1 or 3 or 4)
        {
            (samples, colors, bits) = ConvertIcc(image, samples, bits, colors, alpha, out space);
        }

        return new PdfDecodedImage(image.Width, image.Height, colors, bits, samples)
        {
            HasAlpha = alpha,
            ColorSpace = space,
        };
    }

    /// <summary>Converts the colour channels of an ICC-described image to sRGB (or the gray profile), keeping the opacity channel as it is.</summary>
    private static (byte[] Samples, int Colors, int Bits) ConvertIcc(JpxImage image, byte[] samples, int bits, int colors, bool alpha, out PdfColorSpace? space)
    {
        space = null;
        if (!ColorProfile.TryFromIcc(image.IccProfile.Span, out ColorProfile? profile) || profile is null)
        {
            return (samples, colors, bits);
        }

        if (profile.Model != (colors switch { 1 => ColorModel.Gray, 3 => ColorModel.Rgb, _ => ColorModel.Cmyk }))
        {
            return (samples, colors, bits);
        }

        // Work in 8 bits: the conversion tables are 8-bit grids in the engine's fast path anyway.
        byte[] eight = bits == 8 ? samples : To8(samples);
        int total = colors + (alpha ? 1 : 0);
        int w = image.Width, h = image.Height;
        bool toGray = colors == 1;
        int outColors = toGray ? 1 : 3;
        ColorProfile dest = toGray ? ColorProfile.Gray : ColorProfile.Srgb;
        ColorTransform transform = ColorTransform.Get(profile, dest, RenderingIntent.RelativeColorimetric, blackPointCompensation: false);
        PixelFormat sf = colors switch { 1 => PixelFormat.Gray8, 3 => PixelFormat.Rgb8, _ => PixelFormat.Cmyk8 };
        PixelFormat df = toGray ? PixelFormat.Gray8 : PixelFormat.Rgb8;
        int outTotal = outColors + (alpha ? 1 : 0);
        var result = new byte[checked(w * h * outTotal)];
        var srcRow = new byte[w * colors];
        var dstRow = new byte[w * outColors];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int s = ((y * w) + x) * total;
                for (int c = 0; c < colors; c++)
                {
                    srcRow[(x * colors) + c] = eight[s + c];
                }
            }

            transform.ConvertRow(srcRow, sf, dstRow, df, w);
            for (int x = 0; x < w; x++)
            {
                int d = ((y * w) + x) * outTotal;
                for (int c = 0; c < outColors; c++)
                {
                    result[d + c] = dstRow[(x * outColors) + c];
                }

                if (alpha)
                {
                    result[d + outColors] = eight[(((y * w) + x) * total) + colors];
                }
            }
        }

        space = toGray ? PdfColorSpace.Gray : PdfColorSpace.Rgb;
        return (result, outColors, 8);
    }

    private static byte[] To8(byte[] sixteen)
    {
        var r = new byte[sixteen.Length / 2];
        for (int i = 0; i < r.Length; i++)
        {
            r[i] = sixteen[2 * i];
        }

        return r;
    }
}
