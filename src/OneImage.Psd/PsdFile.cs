using System.Buffers.Binary;
using System.Text;
using OneImage.Layers;

namespace OneImage.Psd;

/// <summary>
/// A Photoshop document (PSD, or PSB for very large ones) as a layered <see cref="Layers.Document"/> plus what the model does not hold:
/// per-layer <see cref="PsdLayerInfo"/>, image resources and document-level blocks, all written back unchanged.
/// </summary>
/// <remarks>
/// <para>
/// <b>Reading.</b> <see cref="Load(string, PsdReadOptions?, CancellationToken)"/> builds the whole layer tree: pixel layers with offsets,
/// pass-through and isolated groups, blend modes, opacity, fill opacity, visibility, clipping, locks, raster masks (with density and
/// feather) and the adjustment layers the model reproduces. CMYK and Lab files are converted to RGB through the color engine as the layers
/// are read (CMYK and 16/32-bit Lab become 8- and 16-bit RGB respectively), keeping the embedded profile in <see cref="SourceProfile"/>.
/// Text, smart objects, vector shapes, layer styles and every tagged block the model has no place for are kept as <see cref="PsdBlock"/>s
/// and drawn from the pixels Photoshop stored with the layer. Layer data is read lazily from the stream only as far as needed: channels are
/// decoded in bands of rows, so memory stays near the size of the document's canvases.
/// </para>
/// <para>
/// <b>Failure.</b> Anything that is not a valid PSD throws <see cref="ImageFormatException"/>; a file that exceeds <see cref="ImageLimits"/> or
/// <see cref="PsdLimits"/> throws <see cref="ImageLimitException"/> before the memory is allocated. A feature that can be left out without
/// failing (a text engine, a layer style) never throws.
/// </para>
/// <para>See <c>docs/psd.md</c> for the table of what is modelled, kept opaque and dropped.</para>
/// </remarks>
public sealed partial class PsdFile : IDisposable
{
    private readonly Dictionary<Layer, PsdLayerInfo> _infos = new();

    internal Dictionary<Layer, PsdLayerInfo> Infos => _infos;

    private PsdFile(Document document)
    {
        Document = document;
    }

    /// <summary>Gets the layered document. Dispose the <see cref="PsdFile"/> to dispose it.</summary>
    public Document Document { get; }

    /// <summary>Gets or sets the image resources written back (resolution, guides, thumbnail, ...). The embedded ICC profile is not among them: it is written from the document's color profile.</summary>
    public List<PsdResource> Resources { get; } = [];

    /// <summary>Gets or sets the document-level tagged blocks written back (patterns, annotations, ...).</summary>
    public List<PsdBlock> DocumentBlocks { get; } = [];

    /// <summary>Gets the color mode of the file this document was read from, or <see cref="PsdColorMode.Rgb"/> for a document built in memory.</summary>
    public PsdColorMode SourceColorMode { get; private set; } = PsdColorMode.Rgb;

    /// <summary>Gets the bits per channel of the file this document was read from (1, 8, 16 or 32), or the document's own depth for one built in memory.</summary>
    public int SourceBitDepth { get; private set; }

    /// <summary>Gets the source file's CMYK or Lab profile (the document itself is RGB), or <see langword="null"/> when the file has none or is RGB or gray.</summary>
    public ColorProfile? SourceProfile { get; private set; }

    /// <summary>Gets whether the file was a PSB.</summary>
    public bool WasLarge { get; private set; }

    /// <summary>Wraps a document built in memory so it can be saved. Layers get default <see cref="PsdLayerInfo"/>. The file takes ownership of <paramref name="document"/>.</summary>
    /// <param name="document">The document.</param>
    /// <returns>The file.</returns>
    public static PsdFile FromDocument(Document document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var f = new PsdFile(document) { SourceBitDepth = document.Format.Sample switch { SampleType.U16 => 16, SampleType.F32 => 32, _ => 8 } };
        return f;
    }

    /// <summary>Gets the PSD-specific information of a layer, creating a default entry for a layer that has none.</summary>
    /// <param name="layer">A layer of <see cref="Document"/>.</param>
    /// <returns>The info; editing it changes what is written.</returns>
    public PsdLayerInfo GetInfo(Layer layer)
    {
        ArgumentNullException.ThrowIfNull(layer);
        if (!_infos.TryGetValue(layer, out PsdLayerInfo? info))
        {
            info = new PsdLayerInfo
            {
                Kind = layer switch
                {
                    GroupLayer => PsdLayerKind.Group,
                    FillLayer => PsdLayerKind.SolidColorFill,
                    AdjustmentLayer => PsdLayerKind.Adjustment,
                    _ => PsdLayerKind.Pixel,
                },
            };
            _infos[layer] = info;
        }

        return info;
    }

    /// <summary>Disposes the document.</summary>
    public void Dispose() => Document.Dispose();

    /// <summary>Loads a PSD or PSB file.</summary>
    /// <param name="path">The file.</param>
    /// <param name="options">Limits and storage options; <see langword="null"/> for the defaults.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The document. Dispose it.</returns>
    /// <exception cref="ImageFormatException">The file is not a valid PSD or PSB, or uses an unsupported color mode (multichannel).</exception>
    /// <exception cref="ImageLimitException">The file exceeds a limit.</exception>
    public static PsdFile Load(string path, PsdReadOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(path);
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.RandomAccess);
        return Load(fs, options, cancellationToken);
    }

    /// <summary>Loads a PSD or PSB file from a seekable stream. The stream is read during this call only; the caller keeps it.</summary>
    /// <param name="stream">A readable, seekable stream positioned anywhere; reading starts at its beginning.</param>
    /// <param name="options">Limits and storage options; <see langword="null"/> for the defaults.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The document. Dispose it.</returns>
    /// <exception cref="ImageFormatException">The data is not a valid PSD or PSB, or uses an unsupported color mode.</exception>
    /// <exception cref="ImageLimitException">The file exceeds a limit.</exception>
    public static PsdFile Load(Stream stream, PsdReadOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanSeek || !stream.CanRead)
        {
            throw new ArgumentException("The stream must be readable and seekable.", nameof(stream));
        }

        options ??= new PsdReadOptions();
        ImageLimits limits = options.Limits ?? ImageConfiguration.Default.Limits;
        PsdLimits psd = options.PsdLimits ?? PsdLimits.Default;
        var io = new FileIo(stream);
        PsdStructure s = PsdParser.Parse(io, psd, limits, layers: true);
        return Build(s, options, limits, psd, cancellationToken);
    }

    /// <summary>Loads a PSD or PSB file from memory.</summary>
    /// <param name="data">The file's bytes.</param>
    /// <param name="options">Limits and storage options; <see langword="null"/> for the defaults.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The document. Dispose it.</returns>
    /// <exception cref="ImageFormatException">The data is not a valid PSD or PSB, or uses an unsupported color mode.</exception>
    /// <exception cref="ImageLimitException">The file exceeds a limit.</exception>
    public static PsdFile Load(ReadOnlyMemory<byte> data, PsdReadOptions? options = null, CancellationToken cancellationToken = default)
    {
        using var ms = new MemoryStream(data.ToArray(), writable: false);
        return Load(ms, options, cancellationToken);
    }

    // The format a document uses for a file's mode and depth: gray stays gray at 8 bits; everything else is RGB.
    internal static (PixelFormat Format, SampleKind Kind, bool Gray) DocumentFormatFor(PsdHeader h)
    {
        switch (h.Mode)
        {
            case PsdColorMode.Multichannel:
                throw new ImageFormatException("Multichannel PSD files are not supported.");
            case PsdColorMode.Bitmap:
                return (PixelFormat.GrayAlpha8, SampleKind.U8, true);
            case PsdColorMode.Grayscale or PsdColorMode.Duotone:
                return h.Depth switch
                {
                    8 => (PixelFormat.GrayAlpha8, SampleKind.U8, true),
                    16 => (PixelFormat.Rgba16, SampleKind.U16, false),
                    _ => (PixelFormat.RgbaF32, SampleKind.F32, false),
                };
            case PsdColorMode.Indexed or PsdColorMode.Cmyk:
                return (PixelFormat.Rgba8, SampleKind.U8, false);
            case PsdColorMode.Lab:
                return h.Depth == 8 ? (PixelFormat.Rgba8, SampleKind.U8, false) : (PixelFormat.Rgba16, SampleKind.U16, false);
            default:
                return h.Depth switch
                {
                    8 => (PixelFormat.Rgba8, SampleKind.U8, false),
                    16 => (PixelFormat.Rgba16, SampleKind.U16, false),
                    32 => (PixelFormat.RgbaF32, SampleKind.F32, false),
                    _ => throw new ImageFormatException($"{h.Depth}-bit RGB is not a valid PSD depth."),
                };
        }
    }

    private static PsdFile Build(PsdStructure s, PsdReadOptions options, ImageLimits limits, PsdLimits psd, CancellationToken ct)
    {
        PsdHeader h = s.Header;
        (PixelFormat format, SampleKind kind, bool gray) = DocumentFormatFor(h);
        ColorProfile? iccProfile = null;
        ColorProfile? sourceProfile = null;
        if (s.IccProfile is { Length: > 0 } icc && ColorProfile.TryFromIcc(icc, out ColorProfile? parsed) && parsed != null)
        {
            if (h.Mode is PsdColorMode.Cmyk or PsdColorMode.Lab)
            {
                sourceProfile = parsed;
            }
            else if (parsed.Model == format.Model)
            {
                iccProfile = parsed;
            }
        }

        var docOptions = new DocumentOptions { Format = format, ColorProfile = iccProfile, Limits = limits, Spill = options.Spill };
        var doc = new Document(h.Width, h.Height, docOptions);
        PsdFile file;
        try
        {
            file = new PsdFile(doc)
            {
                SourceColorMode = h.Mode,
                SourceBitDepth = h.Depth,
                SourceProfile = sourceProfile,
                WasLarge = h.Psb,
            };
            file.Resources.AddRange(s.Resources);
            file.DocumentBlocks.AddRange(s.GlobalBlocks);
            var loader = new LayerLoader(file, s, kind, gray, sourceProfile, limits, psd, ct);
            loader.Run();
        }
        catch
        {
            doc.Dispose();
            throw;
        }

        return file;
    }
}
