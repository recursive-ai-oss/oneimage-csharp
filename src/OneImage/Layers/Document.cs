using OneImage.Drawing;
using System.Globalization;

namespace OneImage.Layers;

/// <summary>Where and how a <see cref="Document"/> parks tiles it does not need right now.</summary>
public sealed class SpillOptions
{
    /// <summary>Gets the memory budget, in bytes, for the document's tiles. Cold tiles beyond it go to a temporary file. The default is 256 MiB.</summary>
    public long BudgetBytes { get; init; } = 256L << 20;

    /// <summary>Gets the directory for the temporary file; the default is the system temporary directory. The file is created on first use, named <c>oneimage-spill-*.tmp</c>, and removed when the document is disposed or the process ends.</summary>
    public string? Directory { get; init; }

    /// <summary>Gets the largest size the spill file may reach, in bytes. Going past it throws <see cref="ImageLimitException"/>. The default is 64 GiB.</summary>
    public long MaxFileBytes { get; init; } = 64L << 30;
}

/// <summary>Counters of a document's spill store (see <see cref="Document.SpillStatistics"/>).</summary>
/// <param name="TilesWritten">Tiles written to the spill file (a tile is written once, however often it is evicted afterwards).</param>
/// <param name="TilesRead">Tiles read back because their pixels were needed again.</param>
/// <param name="ResidentBytes">Bytes of tile memory currently in RAM.</param>
/// <param name="PeakResidentBytes">The highest <paramref name="ResidentBytes"/> seen.</param>
/// <param name="FileBytes">Bytes the spill file spans.</param>
/// <param name="BudgetBytes">The budget the store tries to stay under.</param>
public readonly record struct SpillStatistics(long TilesWritten, long TilesRead, long ResidentBytes, long PeakResidentBytes, long FileBytes, long BudgetBytes);

/// <summary>Settings of a new <see cref="Document"/>.</summary>
public sealed class DocumentOptions
{
    /// <summary>
    /// Gets the pixel format of the document and of its layers' canvases: RGB or gray, straight alpha, 8-bit, 16-bit or 32-bit float.
    /// The default is <see cref="PixelFormat.Rgba8"/>.
    /// </summary>
    public PixelFormat Format { get; init; } = PixelFormat.Rgba8;

    /// <summary>Gets the color profile the pixel values are in. The default (null) is sRGB for RGB and the sRGB-curve gray profile for gray. CMYK and Lab documents are not supported yet.</summary>
    public ColorProfile? ColorProfile { get; init; }

    /// <summary>Gets the background color (straight alpha, in the document's color space) that every render starts from. The default is transparent.</summary>
    public Rgba32 Background { get; init; }

    /// <summary>Gets whether layers blend in linear light instead of the stored (encoded) values. The default is <see langword="false"/>, which is what image editors do.</summary>
    public bool BlendInLinearLight { get; init; }

    /// <summary>Gets the spill settings, or null (the default) to keep every tile in memory.</summary>
    public SpillOptions? Spill { get; init; }

    /// <summary>
    /// Gets the memory budget, in bytes, for composited tiles kept between renders. Over it, the oldest are dropped and recomputed when needed again. The default is 256 MiB.
    /// With a <see cref="Spill"/> store the cached tiles live in it instead and are not otherwise limited.
    /// </summary>
    public long RenderCacheBytes { get; init; } = 256L << 20;

    /// <summary>Gets the limits for layer sizes. The default is <see cref="ImageConfiguration.Default"/>'s.</summary>
    public ImageLimits? Limits { get; init; }
}

/// <summary>
/// A layered document: a size, a pixel format and color space, a background and a tree of <see cref="Layer"/>s, rendered on demand.
/// <para>
/// <b>Editing.</b> Create layers with the <c>Create…</c> methods, place them with <see cref="Add{T}"/> or a group's
/// <see cref="GroupLayer.Add{T}"/>, paint on pixel layers' canvases and change layer properties in place. A document is single-writer: edit from
/// one thread. <see cref="Snapshot"/> freezes the state in O(layers) (it shares the tiles of every canvas copy-on-write) and
/// can be rendered from any thread while you keep editing.
/// </para>
/// <para>
/// <b>Rendering.</b> <see cref="Render"/> returns an <see cref="Image"/> that computes nothing until pixels are pulled. A pulled
/// tile of the document walks the layer tree for only that tile: layers that cannot touch it are skipped, and each layer is blended exactly
/// as the equivalent <see cref="Image.Composite(Image, int, int, float, bool, BlendMode, AlphaComposition)"/> call would. Composited tiles are cached per document;
/// when you edit, the next snapshot compares each canvas's tile table with the previous one and marks only the tiles that changed (and
/// the area of property changes) so a later render recomputes just those. A region render equals the same pixels of a whole render.
/// </para>
/// <para>
/// <b>Lifetime.</b> Dispose the document to release its canvases and delete the spill file. Images rendered from it must not be used afterwards
/// when a spill store is in use (they would need the deleted file); without one they remain valid.
/// </para>
/// </summary>
public sealed class Document : IDisposable
{
    private readonly ImageLimits _limits;
    private readonly SpillTileStore? _store;
    private readonly RenderCache _cache;
    private readonly bool[] _pending;
    private readonly int _tilesX;
    private readonly int _tilesY;
    private readonly ImageMetadata _metadata;
    private Rgba32 _background;
    private bool _linear;
    private Tile? _backgroundTile;
    private long _revision;
    private bool _anyPending;
    private bool _disposed;

    /// <summary>Creates an empty document: a background and an empty root group.</summary>
    /// <param name="width">Width in pixels, positive.</param>
    /// <param name="height">Height in pixels, positive.</param>
    /// <param name="options">Format, color space, background and storage; null for the defaults (8-bit sRGB RGBA, transparent, in memory).</param>
    /// <exception cref="ArgumentOutOfRangeException">A dimension is not positive.</exception>
    /// <exception cref="ArgumentException">The format is not RGB or gray with straight alpha and 8-bit, 16-bit or float samples, or the profile does not match its color model.</exception>
    /// <exception cref="ImageLimitException">The size exceeds the limits.</exception>
    public Document(int width, int height, DocumentOptions? options = null)
    {
        options ??= new DocumentOptions();
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        PixelFormat f = options.Format;
        if (f.Model is not (ColorModel.Rgb or ColorModel.Gray) || f.Alpha != AlphaMode.Straight || f.Sample is not (SampleType.U8 or SampleType.U16 or SampleType.F32))
        {
            throw new ArgumentException("A document needs an RGB or gray format with straight alpha and 8-bit, 16-bit or 32-bit float samples.", nameof(options));
        }

        _limits = options.Limits ?? ImageConfiguration.Default.Limits;
        _limits.CheckFrame(width, height);
        ColorProfile profile = options.ColorProfile ?? ColorProfile.DefaultFor(f.Model);
        if (profile.Model != f.Model)
        {
            throw new ArgumentException("The color profile does not match the document's color model.", nameof(options));
        }

        Width = width;
        Height = height;
        Format = f;
        ColorProfile = profile;
        _metadata = options.ColorProfile == null ? ImageMetadata.Empty : ImageMetadata.Empty.WithIccProfile(profile.IccBytes);
        _background = options.Background;
        _linear = options.BlendInLinearLight;
        if (options.Spill is { } spill)
        {
            _store = new SpillTileStore(spill.BudgetBytes, spill.Directory, spill.MaxFileBytes);
        }

        _tilesX = (width + Tile.Size - 1) >> Tile.Shift;
        _tilesY = (height + Tile.Size - 1) >> Tile.Shift;
        _pending = new bool[_tilesX * _tilesY];
        _cache = new RenderCache(_tilesX * _tilesY, options.RenderCacheBytes, _store);
        Root = new GroupLayer(this, "Root");
        Invalidate(Bounds);
    }

    /// <summary>Gets the width in pixels.</summary>
    public int Width { get; }

    /// <summary>Gets the height in pixels.</summary>
    public int Height { get; }

    /// <summary>Gets the pixel format of the document and its canvases.</summary>
    public PixelFormat Format { get; }

    /// <summary>Gets the color profile the document's pixel values are in.</summary>
    public ColorProfile ColorProfile { get; }

    /// <summary>Gets the rectangle (0, 0, <see cref="Width"/>, <see cref="Height"/>).</summary>
    public ImageRect Bounds => new(0, 0, Width, Height);

    /// <summary>Gets the root group. Its own opacity, mask and blend mode are ignored: its children are composited onto the background.</summary>
    public GroupLayer Root { get; }

    /// <summary>Gets or sets the background color (straight alpha, in the document's color space) renders start from.</summary>
    public Rgba32 Background
    {
        get => _background;
        set
        {
            if (!_background.Equals(value))
            {
                _background = value;
                _backgroundTile = null;
                Invalidate(Bounds);
            }
        }
    }

    /// <summary>Gets or sets whether layers blend in linear light (see <see cref="DocumentOptions.BlendInLinearLight"/>).</summary>
    public bool BlendInLinearLight
    {
        get => _linear;
        set
        {
            if (_linear != value)
            {
                _linear = value;
                Invalidate(Bounds);
            }
        }
    }

    /// <summary>Gets how many composited tiles the document has computed so far (a diagnostic; it counts every render of every snapshot).</summary>
    public long TilesComputed => _cache.Computed;

    /// <summary>Gets how many tile requests were answered from the document's cache of composited tiles.</summary>
    public long TilesReused => _cache.Reused;

    /// <summary>Gets the spill store's counters, or null when the document keeps everything in memory.</summary>
    public SpillStatistics? SpillStatistics => _store?.Statistics;

    /// <summary>Creates a pixel layer with a new, empty (transparent) canvas in the document's format. It is not part of the document until it is added to a group.</summary>
    /// <param name="name">The layer name.</param>
    /// <param name="width">Canvas width; the default is the document's.</param>
    /// <param name="height">Canvas height; the default is the document's.</param>
    /// <param name="x">Document x of the canvas's left edge.</param>
    /// <param name="y">Document y of the canvas's top edge.</param>
    /// <returns>The layer.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A size is not positive.</exception>
    /// <exception cref="ImageLimitException">The size exceeds the limits.</exception>
    public PixelLayer CreatePixelLayer(string name, int? width = null, int? height = null, int x = 0, int y = 0)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return new PixelLayer(this, name, width ?? Width, height ?? Height, x, y);
    }

    /// <summary>
    /// Creates a pixel layer holding a copy of <paramref name="source"/>, converted to the document's color profile and pixel format
    /// (first frame only). The pixels are computed now, a tile row at a time, and stored in the document's tiles.
    /// </summary>
    /// <param name="name">The layer name.</param>
    /// <param name="source">The image.</param>
    /// <param name="x">Document x of the image's left edge.</param>
    /// <param name="y">Document y of the image's top edge.</param>
    /// <param name="cancellationToken">Cancels the copy.</param>
    /// <returns>The layer.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> is null.</exception>
    /// <exception cref="ImageOperationException">The image cannot be converted to the document's color model.</exception>
    public PixelLayer CreatePixelLayer(string name, Image source, int x = 0, int y = 0, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(source);
        Image img = source;
        if (img.Format.Model != Format.Model)
        {
            img = img.ConvertTo(new PixelFormat(Format.Model, img.Format.Sample, img.Format.Alpha));
        }

        if (!(ReferenceEquals(img.ColorProfile, ColorProfile) || (img.ColorProfile.Model == ColorProfile.Model && img.ColorProfile.IccBytes.Span.SequenceEqual(ColorProfile.IccBytes.Span))))
        {
            img = img.ConvertColor(ColorProfile);
        }

        if (!img.Format.HasAlpha)
        {
            img = img.AddAlpha();
        }

        img = img.ConvertTo(Format);
        Canvas canvas = CreateCanvas(img.Width, img.Height, Format);
        try
        {
            int bpp = Format.BytesPerPixel;
            byte[] row = new byte[checked(img.Width * Tile.Size * bpp)];
            RenderContext ctx = RenderContext.Create(0, cancellationToken);
            Node node = img.Root;
            for (int y0 = 0; y0 < img.Height; y0 += Tile.Size)
            {
                var strip = new ImageRect(0, y0, img.Width, Math.Min(Tile.Size, img.Height - y0));
                global::OneImage.Render.ReadRegion(node, strip, row, 0, Format, ctx);
                canvas.WriteBytes(strip, row.AsSpan(0, strip.Width * strip.Height * bpp), Format);
                canvas.SnapshotGrid();
            }
        }
        catch
        {
            canvas.Dispose();
            throw;
        }

        return new PixelLayer(this, name, canvas, x, y);
    }

    /// <summary>Creates an empty group (pass-through by default). It is not part of the document until it is added to a group.</summary>
    /// <param name="name">The layer name.</param>
    /// <param name="passThrough">Pass-through (true, the default) or isolated; see <see cref="GroupLayer"/>.</param>
    /// <returns>The group.</returns>
    public GroupLayer CreateGroup(string name, bool passThrough = true)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return new GroupLayer(this, name) { PassThrough = passThrough };
    }

    /// <summary>Creates a layer filling the whole document with <paramref name="brush"/> (a solid color or gradient in the document's color space).</summary>
    /// <param name="name">The layer name.</param>
    /// <param name="brush">The brush.</param>
    /// <returns>The layer.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="brush"/> is null.</exception>
    public FillLayer CreateFillLayer(string name, Brush brush)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(brush);
        return new FillLayer(this, name, brush);
    }

    /// <summary>Creates a layer filling the whole document with one color (straight alpha, in the document's color space).</summary>
    /// <param name="name">The layer name.</param>
    /// <param name="color">The color.</param>
    /// <returns>The layer.</returns>
    public FillLayer CreateFillLayer(string name, Rgba32 color) => CreateFillLayer(name, Brush.Solid(color));

    /// <summary>Creates an adjustment layer.</summary>
    /// <param name="name">The layer name.</param>
    /// <param name="adjustment">The point operation.</param>
    /// <returns>The layer.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="adjustment"/> is null.</exception>
    public AdjustmentLayer CreateAdjustmentLayer(string name, Adjustment adjustment)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(adjustment);
        return new AdjustmentLayer(this, name, adjustment);
    }

    /// <summary>Puts <paramref name="layer"/> on top of the root group.</summary>
    /// <param name="layer">A detached layer of this document.</param>
    /// <returns>The layer.</returns>
    public T Add<T>(T layer)
        where T : Layer => Root.Add(layer);

    /// <summary>
    /// Freezes the document's current state. The snapshot shares the layers' tiles copy-on-write, so it costs O(layers + tiles
    /// compared) and no pixel copies, and later edits do not change it. Tiles that changed since the previous snapshot are marked for recomputation.
    /// </summary>
    /// <returns>An immutable snapshot that can be rendered from any thread.</returns>
    /// <exception cref="ObjectDisposedException">The document is disposed.</exception>
    public DocumentSnapshot Snapshot()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        LayerState[] top = CaptureChildren(Root);
        if (_anyPending)
        {
            _revision++;
            _cache.MarkChanged(_pending, _revision);
            Array.Clear(_pending);
            _anyPending = false;
        }

        _backgroundTile ??= MakeBackground();
        return new DocumentSnapshot(this, top, _revision, _cache, _backgroundTile, _linear, _metadata, _store);
    }

    /// <summary>Renders the current state: <c>Snapshot().Render()</c>.</summary>
    /// <returns>A lazy image of the document's size and format; its color profile is the document's.</returns>
    public Image Render() => Snapshot().Render();

    /// <summary>Describes the document and its layers in a serializable form (see <see cref="LayerDescription"/>).</summary>
    /// <returns>The description; the children of the root are the top-level layers.</returns>
    public LayerDescription Describe()
    {
        var p = new List<OpParameter>
        {
            new("width", (long)Width),
            new("height", (long)Height),
            new("format", Format.ToString()),
            new("profile", ColorProfile.IccBytes.Length > 0 ? ProfileLabel() : "none"),
            new("background", "#" + _background.R.ToString("x2", CultureInfo.InvariantCulture) + _background.G.ToString("x2", CultureInfo.InvariantCulture) + _background.B.ToString("x2", CultureInfo.InvariantCulture) + _background.A.ToString("x2", CultureInfo.InvariantCulture)),
            new("blend-in-linear-light", _linear),
        };
        return new LayerDescription("document", p, Root.DescribeChildren());
    }

    /// <summary>Disposes every layer's canvas and the spill store (deleting its file).</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Root.Dispose();
        _cache.Clear();
        _store?.Dispose();
    }

    internal Canvas CreateCanvas(int width, int height, PixelFormat format)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        _limits.CheckFrame(width, height);
        return new Canvas(width, height, format, _store);
    }

    /// <summary>Marks the document tiles under <paramref name="area"/> (document coordinates, may exceed the document) as changed.</summary>
    internal void Invalidate(ImageRect area)
    {
        long x0 = Math.Max(0, (long)area.X);
        long y0 = Math.Max(0, (long)area.Y);
        long x1 = Math.Min(Width, (long)area.X + area.Width);
        long y1 = Math.Min(Height, (long)area.Y + area.Height);
        if (x0 >= x1 || y0 >= y1)
        {
            return;
        }

        int tx0 = (int)(x0 >> Tile.Shift);
        int tx1 = (int)((x1 - 1) >> Tile.Shift);
        int ty0 = (int)(y0 >> Tile.Shift);
        int ty1 = (int)((y1 - 1) >> Tile.Shift);
        for (int ty = ty0; ty <= ty1; ty++)
        {
            _pending.AsSpan((ty * _tilesX) + tx0, tx1 - tx0 + 1).Fill(true);
        }

        _anyPending = true;
    }

    /// <summary>
    /// Compares two tile tables of one canvas and reports, through <paramref name="dirty"/>, the rectangles (shifted by the offset
    /// and widened by <paramref name="margin"/>) of tiles whose storage differs. A missing earlier table means everything is new.
    /// </summary>
    internal static void DiffGrids(TileGrid? previous, TileGrid current, int offsetX, int offsetY, int margin, Action<ImageRect> dirty)
    {
        if (ReferenceEquals(previous, current))
        {
            return;
        }

        if (previous == null || previous.TilesX != current.TilesX || previous.TilesY != current.TilesY)
        {
            dirty(Widen(new ImageRect(offsetX, offsetY, current.Width, current.Height), margin));
            return;
        }

        for (int ty = 0; ty < current.TilesY; ty++)
        {
            int run = -1;
            for (int tx = 0; tx <= current.TilesX; tx++)
            {
                bool changed = tx < current.TilesX
                    && !ReferenceEquals(previous.Tiles[(ty * current.TilesX) + tx], current.Tiles[(ty * current.TilesX) + tx]);
                if (changed && run < 0)
                {
                    run = tx;
                }
                else if (!changed && run >= 0)
                {
                    int x = run << Tile.Shift;
                    int y = ty << Tile.Shift;
                    int w = Math.Min(current.Width - x, (tx - run) << Tile.Shift);
                    int h = Math.Min(current.Height - y, Tile.Size);
                    dirty(Widen(new ImageRect(offsetX + x, offsetY + y, w, h), margin));
                    run = -1;
                }
            }
        }
    }

    private static ImageRect Widen(ImageRect r, int margin) =>
        margin == 0 ? r : new ImageRect(r.X - margin, r.Y - margin, r.Width + (2 * margin), r.Height + (2 * margin));

    private LayerState[] CaptureChildren(GroupLayer group)
    {
        var list = new LayerState[group.ChildList.Count];
        for (int i = 0; i < list.Length; i++)
        {
            list[i] = Capture(group.ChildList[i]);
        }

        return list;
    }

    private LayerState Capture(Layer layer)
    {
        MaskState? mask = layer.Mask?.Capture(Invalidate);
        Node? node = null;
        LayerState[] children = [];
        Adjustment? adjustment = null;
        switch (layer)
        {
            case PixelLayer p:
                {
                    TileGrid grid = p.Pixels.SnapshotGrid();
                    DiffGrids(p.LastGrid, grid, p.X, p.Y, p.Style?.DirtyReach ?? 0, Invalidate);
                    if (!ReferenceEquals(p.LastGrid, grid) || p.LastNode == null)
                    {
                        p.LastNode = new GridSourceNode(grid);
                    }

                    p.LastGrid = grid;
                    node = p.LastNode;
                    break;
                }

            case FillLayer f:
                node = f.ContentNode();
                break;
            case AdjustmentLayer a:
                adjustment = a.Adjustment;
                break;
            case GroupLayer g:
                children = CaptureChildren(g);
                break;
        }

        FxPlan? fx = layer.Kind is LayerKind.Pixel or LayerKind.Fill ? FxPlan.Create(layer.Style, layer.Opacity, layer.FillOpacity) : null;
        return new LayerState(layer.Kind, layer.Visible, layer.EffectiveOpacity, layer.BlendMode, layer.ClipToBelow, layer.Bounds, node, (layer as PixelLayer)?.LastGrid, mask, (layer as GroupLayer)?.PassThrough ?? false, children, adjustment, fx);
    }

    private Tile MakeBackground()
    {
        if (_background.A == 0 && _background.R == 0 && _background.G == 0 && _background.B == 0)
        {
            return Tile.Zero(Format);
        }

        int bpp = Format.BytesPerPixel;
        byte[] src = new byte[Tile.Size * 4];
        for (int i = 0; i < Tile.Size; i++)
        {
            src[(i * 4) + 0] = _background.R;
            src[(i * 4) + 1] = _background.G;
            src[(i * 4) + 2] = _background.B;
            src[(i * 4) + 3] = _background.A;
        }

        var t = Tile.Rent(Tile.ByteSize(Format), clear: false);
        byte[] row = new byte[Tile.Size * bpp];
        OneImage.Kernels.ConvertKernel.ConvertRow(src, PixelFormat.Rgba8, row, Format, Tile.Size);
        Span<byte> span = t.Span;
        for (int r = 0; r < Tile.Size; r++)
        {
            row.CopyTo(span.Slice(r * Tile.Size * bpp, Tile.Size * bpp));
        }

        return t;
    }

    private string ProfileLabel() => ColorProfile.Model == ColorModel.Gray ? "gray" : "rgb-" + (ColorProfile.Description ?? "icc");
}
