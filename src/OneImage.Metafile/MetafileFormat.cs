using System.Buffers.Binary;

namespace OneImage.Metafile;

/// <summary>
/// The enhanced metafile format registration: recognizes EMF and decodes it by interpreting its records and drawing them with the drawing engine.
/// Register it with <see cref="ImageConfiguration.Add"/>; <see cref="ImageConfiguration.Default"/> does not include it, so the package trims away
/// when unused.
/// </summary>
/// <remarks>
/// <para>
/// The natural size of the image is the picture frame of the header in pixels of the reference device (usually a 96 dpi display), rounded up. When
/// a resize sits directly on the loaded image the picture is drawn at the final size (see <see cref="DecoderCapabilities.RenderAtSize"/>) instead of
/// being scaled afterwards. Output is 8-bit sRGB straight RGBA, transparent where nothing is drawn; operations that read the destination (inverting
/// raster operations, XOR pens) treat transparent pixels as white.
/// </para>
/// <para>
/// GDI itself draws without anti-aliasing. This renderer anti-aliases by default, which looks better when the picture is scaled; set
/// <c>antialias</c> to <see langword="false"/> to get aliased edges that are closer to GDI at 1:1 scale. Text and bitmap stretching are not affected
/// by that choice beyond edge coverage.
/// </para>
/// <para>
/// To work with the drawing commands instead of pixels, use <see cref="MetafileDocument"/>.
/// </para>
/// </remarks>
public sealed class MetafileFormat : IImageFormat
{
    /// <summary>Initializes the format.</summary>
    /// <param name="limits">The metafile limits, or <see langword="null"/> for <see cref="MetafileLimits.Default"/>.</param>
    /// <param name="fonts">Where text fonts come from, or <see langword="null"/> for the installed fonts only.</param>
    /// <param name="antialias">Whether edges are anti-aliased (the default); <see langword="false"/> samples every shape at pixel centers, like GDI.</param>
    /// <param name="background">The color of pixels nothing draws on, or <see langword="null"/> for transparent.</param>
    public MetafileFormat(MetafileLimits? limits = null, MetafileFontOptions? fonts = null, bool antialias = true, Rgba32? background = null)
    {
        Limits = limits ?? MetafileLimits.Default;
        Fonts = fonts ?? new MetafileFontOptions();
        Antialias = antialias;
        Background = background;
        Decoder = new MetafileDecoder(this);
    }

    /// <summary>Gets a shared instance with the default settings.</summary>
    public static MetafileFormat Instance { get; } = new();

    /// <summary>Gets the limits of this registration.</summary>
    public MetafileLimits Limits { get; }

    /// <summary>Gets the font options.</summary>
    public MetafileFontOptions Fonts { get; }

    /// <summary>Gets a value indicating whether edges are anti-aliased.</summary>
    public bool Antialias { get; }

    /// <summary>Gets the background color, or <see langword="null"/> for transparent.</summary>
    public Rgba32? Background { get; }

    /// <inheritdoc />
    public string Name => "emf";

    /// <inheritdoc />
    public IReadOnlyList<string> Extensions { get; } = [".emf"];

    /// <inheritdoc />
    public string MimeType => "image/emf";

    /// <inheritdoc />
    public int HeaderLength => 48;

    /// <inheritdoc />
    public IImageDecoder? Decoder { get; }

    /// <inheritdoc />
    public IImageEncoder? Encoder => null;

    /// <inheritdoc />
    public bool Matches(ReadOnlySpan<byte> header) => MetafileDocument.IsEmf(header);
}

internal sealed class MetafileDecoder : IImageDecoder
{
    private readonly MetafileFormat _format;

    public MetafileDecoder(MetafileFormat format) => _format = format;

    public DecoderCapabilities Capabilities => DecoderCapabilities.RandomAccess | DecoderCapabilities.RenderAtSize;

    /// <summary>What a decoded image remembers between tiles: the interpreted document, built once from the first stream that asks for pixels.</summary>
    private sealed class State
    {
        private readonly object _gate = new();
        private MetafileDocument? _doc;
        private Exception? _failure;

        public MetafileDocument Document(Func<MetafileDocument> build)
        {
            lock (_gate)
            {
                if (_doc is { } ready)
                {
                    return ready;
                }

                if (_failure is { } failed)
                {
                    System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failed).Throw();
                }

                try
                {
                    return _doc = build();
                }
                catch (Exception ex) when (ex is ImageException)
                {
                    // A hostile file costs its interpretation once, not once per tile.
                    _failure = ex;
                    throw;
                }
            }
        }
    }

    public ImageInfo ReadInfo(Stream source, ImageLimits limits)
    {
        byte[] bytes = ReadHead(source);
        MetafileInfo info = MetafileDocument.Identify(bytes, new MetafileReadOptions { Limits = _format.Limits });
        double w = Math.Ceiling(info.Width - 1e-9), h = Math.Ceiling(info.Height - 1e-9);
        if (!(w <= ImageLimits.EngineMaxDimension) || !(h <= ImageLimits.EngineMaxDimension))
        {
            throw new ImageLimitException("The metafile's natural size is too large.");
        }

        int pw = (int)Math.Max(1, w), ph = (int)Math.Max(1, h);
        limits.CheckFrame(pw, ph);
        return new ImageInfo(_format, [new FrameInfo(pw, ph, PixelFormat.Rgba8)], 0, null, new State());
    }

    private static byte[] ReadHead(Stream source)
    {
        // The header and the first records are enough to describe the picture.
        var buf = new byte[4096];
        int len = 0;
        while (len < buf.Length)
        {
            int n = source.Read(buf, len, buf.Length - len);
            if (n == 0)
            {
                break;
            }

            len += n;
        }

        return buf.AsSpan(0, len).ToArray();
    }

    public void DecodeFrame(Stream source, ImageInfo info, int frameIndex, in DecodeRequest request, IRowSink sink, ImageLimits limits, CancellationToken cancellationToken)
    {
        FrameInfo frame = info.Frames[frameIndex];
        int tw = request.TargetWidth > 0 ? request.TargetWidth : frame.Width;
        int th = request.TargetHeight > 0 ? request.TargetHeight : frame.Height;
        limits.CheckFrame(tw, th);
        ImageRect region = request.Region;
        if (region.X < 0 || region.Y < 0 || region.Right > tw || region.Bottom > th || sink.Width != region.Width || sink.Height != region.Height)
        {
            throw new ImageOperationException("The requested metafile region lies outside the target size.");
        }

        var state = (State)info.DecoderState!;
        MetafileDocument doc = state.Document(() => BuildDocument(source, cancellationToken));
        using var canvas = new Canvas(region.Width, region.Height, PixelFormat.Rgba8);
        if (_format.Background is { } bg)
        {
            canvas.Clear(bg);
        }

        RectD pic = doc.Info.PictureRectangle;
        float sx = (float)(tw / pic.Width), sy = (float)(th / pic.Height);
        var output = System.Numerics.Matrix3x2.CreateTranslation((float)-pic.Left, (float)-pic.Top)
            * System.Numerics.Matrix3x2.CreateScale(sx, sy)
            * System.Numerics.Matrix3x2.CreateTranslation(-region.X, -region.Y);
        var renderer = new MetafileRenderer(canvas, output, _format.Antialias, new FontResolver(_format.Fonts), cancellationToken);
        doc.Play(renderer);
        int length = region.Width * region.Height * 4;
        byte[] bytes = Pools<byte>.Pool.Rent(length);
        try
        {
            canvas.CopyToBytes(bytes.AsSpan(0, length), new ImageRect(0, 0, region.Width, region.Height));
            sink.WriteRows(0, bytes.AsSpan(0, length), region.Height);
        }
        finally
        {
            Pools<byte>.Pool.Return(bytes);
        }
    }

    private MetafileDocument BuildDocument(Stream source, CancellationToken cancellationToken)
    {
        source.Position = 0;
        byte[] bytes = MetafileDocument.ReadAll(source, _format.Limits.MaxFileBytes);
        var resolver = new FontResolver(_format.Fonts);
        return MetafileDocument.Parse(bytes, new MetafileReadOptions { Limits = _format.Limits, MeasureText = resolver.Measure }, cancellationToken);
    }
}
