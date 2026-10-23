using System.Globalization;

namespace OneImage.Layers;

/// <summary>What a <see cref="Layer"/> is.</summary>
public enum LayerKind
{
    /// <summary>Pixels from a <see cref="Canvas"/> placed at an offset (<see cref="PixelLayer"/>).</summary>
    Pixel = 0,

    /// <summary>A group of layers (<see cref="GroupLayer"/>).</summary>
    Group = 1,

    /// <summary>A brush filling the whole document (<see cref="FillLayer"/>).</summary>
    Fill = 2,

    /// <summary>A point operation applied to everything below it (<see cref="AdjustmentLayer"/>).</summary>
    Adjustment = 3,
}

/// <summary>
/// Lock flags of a layer. They are plain data that an editor reads (and PSD files carry); the document model itself never refuses an edit because of them.
/// </summary>
[Flags]
public enum LayerLocks
{
    /// <summary>Nothing is locked.</summary>
    None = 0,

    /// <summary>Painting on transparent pixels is locked.</summary>
    TransparentPixels = 1,

    /// <summary>Painting on the layer's pixels is locked.</summary>
    Pixels = 2,

    /// <summary>Moving the layer is locked.</summary>
    Position = 4,

    /// <summary>Everything is locked.</summary>
    All = TransparentPixels | Pixels | Position,
}

/// <summary>
/// A serializable description of a layer or document: a language-neutral kind name, parameters that are only
/// <see cref="long"/>, <see cref="double"/>, <see cref="bool"/> or <see cref="string"/>, and the children of a group.
/// Parameter names are lower-case and hyphenated (<c>fill-opacity</c>, <c>mask-density</c>); blend modes use the names of
/// <see cref="Image.Composite(Image, int, int, float, bool, BlendMode, AlphaComposition)"/>'s descriptors (<c>multiply</c>, <c>color-dodge</c>) and a
/// pass-through group says <c>pass-through</c>.
/// </summary>
/// <param name="Kind">One of <c>document</c>, <c>group</c>, <c>pixel</c>, <c>fill</c>, <c>adjustment</c>.</param>
/// <param name="Parameters">The parameters, in a fixed order.</param>
/// <param name="Children">The children of a group or the document, bottom first; empty for other layers.</param>
public sealed record LayerDescription(string Kind, IReadOnlyList<OpParameter> Parameters, IReadOnlyList<LayerDescription> Children);

/// <summary>
/// A layer of a <see cref="Document"/>. Layers are created by the document's <c>Create…</c> methods, placed into a
/// <see cref="GroupLayer"/> and then edited in place; every property change is picked up by the next <see cref="Document.Snapshot"/>.
/// A layer belongs to one document and is not thread-safe: edit from one thread and hand snapshots (or rendered images) to others.
/// <para>
/// <b>Compositing.</b> A layer is laid over everything below it in its group with <see cref="BlendMode"/> and source-over
/// coverage, exactly as <see cref="Image.Composite(Image, int, int, float, bool, BlendMode, AlphaComposition)"/> would, at an effective opacity of
/// <see cref="Opacity"/> × <see cref="FillOpacity"/>. Without layer styles the two multiply; they are kept apart because file formats store both.
/// </para>
/// </summary>
public abstract class Layer
{
    private string _name;
    private bool _visible = true;
    private float _opacity = 1f;
    private float _fillOpacity = 1f;
    private BlendMode _blend;
    private bool _clip;
    private LayerMask? _mask;

    internal Layer(Document document, LayerKind kind, string name)
    {
        Document = document;
        Kind = kind;
        _name = name ?? string.Empty;
    }

    /// <summary>Gets the document this layer belongs to.</summary>
    public Document Document { get; }

    /// <summary>Gets what kind of layer this is.</summary>
    public LayerKind Kind { get; }

    /// <summary>Gets the group this layer sits in, or <see langword="null"/> when it is detached (or is the root).</summary>
    public GroupLayer? Parent { get; internal set; }

    /// <summary>Gets or sets the layer's name. Names need not be unique. Null becomes the empty string.</summary>
    public string Name
    {
        get => _name;
        set => _name = value ?? string.Empty;
    }

    /// <summary>Gets or sets whether the layer takes part in the render. The default is <see langword="true"/>. A hidden layer hides the layers clipped to it too.</summary>
    public bool Visible
    {
        get => _visible;
        set => Set(ref _visible, value);
    }

    /// <summary>Gets or sets the opacity, 0 to 1, applied to the whole layer including its mask. The default is 1.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is outside 0..1 or NaN.</exception>
    public float Opacity
    {
        get => _opacity;
        set => Set(ref _opacity, CheckUnit(value));
    }

    /// <summary>
    /// Gets or sets the fill opacity, 0 to 1. It multiplies <see cref="Opacity"/>; the model has no layer styles for it to leave
    /// alone, so the two are interchangeable today (Photoshop treats a few blend modes differently under fill; this model does not).
    /// The default is 1.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is outside 0..1 or NaN.</exception>
    public float FillOpacity
    {
        get => _fillOpacity;
        set => Set(ref _fillOpacity, CheckUnit(value));
    }

    /// <summary>
    /// Gets or sets how the layer's colors combine with what is below (all <see cref="OneImage.BlendMode"/> values). The default is
    /// <see cref="OneImage.BlendMode.Normal"/>. A <see cref="GroupLayer"/> that is pass-through ignores it.
    /// </summary>
    public BlendMode BlendMode
    {
        get => _blend;
        set
        {
            if (!BlendNames.IsDefined(value))
            {
                throw new ArgumentOutOfRangeException(nameof(value));
            }

            Set(ref _blend, value);
        }
    }

    /// <summary>
    /// Gets or sets whether the layer is clipped to the layer below it. The layer is then drawn only where the nearest layer below it that
    /// is not itself clipped (its <i>base</i>) has coverage, and the base and its clipped layers are blended into what is below as one
    /// unit using the base's blend mode, opacity and mask (Photoshop's "blend clipped layers as group"). A clipped layer at the bottom
    /// of its group has no base and is drawn normally. The default is <see langword="false"/>.
    /// </summary>
    public bool ClipToBelow
    {
        get => _clip;
        set
        {
            if (_clip != value)
            {
                _clip = value;
                Changed(Parent?.Bounds ?? Bounds);
            }
        }
    }

    /// <summary>Gets or sets the lock flags (plain data). The default is <see cref="LayerLocks.None"/>.</summary>
    public LayerLocks Locks { get; set; }

    /// <summary>Gets the layer's mask, or <see langword="null"/>.</summary>
    public LayerMask? Mask => _mask;

    /// <summary>
    /// Gets the area of the document this layer can change, in document coordinates and clipped to nothing: the canvas rectangle for a
    /// pixel layer, the union of the children for a group, the whole document for fill and adjustment layers. May extend past the document.
    /// </summary>
    public abstract ImageRect Bounds { get; }

    internal float EffectiveOpacity => _opacity * _fillOpacity;

    /// <summary>
    /// Adds a mask. A mask is an 8-bit gray <see cref="Canvas"/> in document coordinates (<see cref="LayerMask.X"/>, <see cref="LayerMask.Y"/>): 255 shows the layer,
    /// 0 hides it, and everything outside the canvas has the value <see cref="LayerMask.OutsideValue"/>.
    /// </summary>
    /// <param name="revealAll">Start fully white (the layer shows everywhere; outside value 255) rather than fully black (hidden; outside value 0).</param>
    /// <param name="area">The area the mask canvas covers, in document coordinates; the default is the layer's bounds clipped to the document (the whole document for non-pixel layers).</param>
    /// <returns>The new mask.</returns>
    /// <exception cref="InvalidOperationException">The layer already has a mask.</exception>
    public LayerMask AddMask(bool revealAll = true, ImageRect? area = null)
    {
        if (_mask != null)
        {
            throw new InvalidOperationException("The layer already has a mask.");
        }

        ImageRect r = area ?? (Kind == LayerKind.Pixel ? Bounds.Intersect(Document.Bounds) : Document.Bounds);
        if (r.IsEmpty)
        {
            r = new ImageRect(0, 0, 1, 1);
        }

        var mask = new LayerMask(this, r, revealAll);
        _mask = mask;
        Changed(Bounds);
        return mask;
    }

    /// <summary>Removes the mask (the layer shows everywhere again). The mask's canvas is disposed.</summary>
    public void RemoveMask()
    {
        if (_mask == null)
        {
            return;
        }

        LayerMask m = _mask;
        _mask = null;
        m.Dispose();
        Changed(Bounds);
    }

    /// <summary>Describes this layer (see <see cref="LayerDescription"/>).</summary>
    /// <returns>The description, with children for a group.</returns>
    public LayerDescription Describe()
    {
        var p = new List<OpParameter>
        {
            new("name", _name),
            new("visible", _visible),
            new("opacity", (double)_opacity),
            new("fill-opacity", (double)_fillOpacity),
            new("blend", BlendLabel()),
            new("clip-to-below", _clip),
            new("lock-transparent-pixels", (Locks & LayerLocks.TransparentPixels) != 0),
            new("lock-pixels", (Locks & LayerLocks.Pixels) != 0),
            new("lock-position", (Locks & LayerLocks.Position) != 0),
        };
        DescribeSelf(p);
        _mask?.Describe(p);
        return new LayerDescription(KindName(Kind), p, DescribeChildren());
    }

    internal static string KindName(LayerKind kind) => kind switch
    {
        LayerKind.Pixel => "pixel",
        LayerKind.Group => "group",
        LayerKind.Fill => "fill",
        _ => "adjustment",
    };

    /// <summary>Records that the layer's look changed inside <paramref name="area"/> (document coordinates).</summary>
    internal void Changed(ImageRect area)
    {
        if (IsAttached)
        {
            Document.Invalidate(area);
        }
    }

    internal bool IsAttached
    {
        get
        {
            Layer l = this;
            while (l.Parent != null)
            {
                l = l.Parent;
            }

            return ReferenceEquals(l, Document.Root);
        }
    }

    internal virtual string BlendLabel() => BlendNames.Name(_blend) ?? "normal";

    internal virtual void DescribeSelf(List<OpParameter> parameters)
    {
    }

    internal virtual IReadOnlyList<LayerDescription> DescribeChildren() => [];

    internal virtual void DisposeContent()
    {
        _mask?.Dispose();
    }

    internal static string Num(double v) => v.ToString("R", CultureInfo.InvariantCulture);

    private static float CheckUnit(float value) =>
        value >= 0f && value <= 1f ? value : throw new ArgumentOutOfRangeException(nameof(value), "The value must be between 0 and 1.");

    private void Set<T>(ref T field, T value)
        where T : struct
    {
        if (!EqualityComparer<T>.Default.Equals(field, value))
        {
            field = value;
            Changed(Bounds);
        }
    }
}
