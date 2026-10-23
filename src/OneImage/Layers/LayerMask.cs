namespace OneImage.Layers;

/// <summary>
/// A layer mask: an 8-bit gray <see cref="Canvas"/> placed in document coordinates that scales the layer's coverage.
/// <para>
/// <b>Value.</b> The coverage is <c>mask / 255</c>, then <see cref="Density"/> <c>d</c> blends it toward fully visible:
/// <c>255·(1 − d) + d·mask</c> (density 0 ignores the mask). <see cref="Feather"/> blurs the mask first with a Gaussian of that
/// standard deviation in pixels (not in linear light; edges clamp). Outside the canvas the mask has <see cref="OutsideValue"/>
/// (before density and feather). A disabled mask is ignored. The coverage multiplies the layer's alpha after the layer's own
/// pixels are positioned and before it is blended, rounded half up on 8-bit alpha.
/// </para>
/// </summary>
public sealed class LayerMask : IDisposable
{
    private readonly Layer _owner;
    private int _x;
    private int _y;
    private byte _outside;
    private float _density = 1f;
    private float _feather;
    private bool _enabled = true;
    private TileGrid? _lastGrid;
    private MaskState? _state;
    private TileGrid? _stateGrid;

    internal LayerMask(Layer owner, ImageRect area, bool revealAll)
    {
        _owner = owner;
        _x = area.X;
        _y = area.Y;
        _outside = revealAll ? (byte)255 : (byte)0;
        Pixels = owner.Document.CreateCanvas(area.Width, area.Height, PixelFormat.Gray8);
        if (revealAll)
        {
            Pixels.Clear(new Rgba32(255, 255, 255, 255));
        }
    }

    /// <summary>Gets the mask's pixels, a <see cref="PixelFormat.Gray8"/> canvas. Paint on it directly; the next <see cref="Document.Snapshot"/> sees the change.</summary>
    public Canvas Pixels { get; }

    /// <summary>Gets or sets the document x of the canvas's left edge.</summary>
    public int X
    {
        get => _x;
        set
        {
            if (_x != value)
            {
                _x = value;
                _owner.Changed(_owner.Bounds);
            }
        }
    }

    /// <summary>Gets or sets the document y of the canvas's top edge.</summary>
    public int Y
    {
        get => _y;
        set
        {
            if (_y != value)
            {
                _y = value;
                _owner.Changed(_owner.Bounds);
            }
        }
    }

    /// <summary>Gets or sets the mask value outside the canvas, 0 (hidden) to 255 (shown).</summary>
    public byte OutsideValue
    {
        get => _outside;
        set
        {
            if (_outside != value)
            {
                _outside = value;
                _owner.Changed(_owner.Bounds);
            }
        }
    }

    /// <summary>Gets or sets the density, 0 to 1 (see the type remarks). The default is 1.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is outside 0..1 or NaN.</exception>
    public float Density
    {
        get => _density;
        set
        {
            if (!(value >= 0f && value <= 1f))
            {
                throw new ArgumentOutOfRangeException(nameof(value));
            }

            if (_density != value)
            {
                _density = value;
                _owner.Changed(_owner.Bounds);
            }
        }
    }

    /// <summary>Gets or sets the feather: the standard deviation, in pixels, of the Gaussian blur applied to the mask. 0 means none (the default).</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is negative, NaN or above 1000.</exception>
    public float Feather
    {
        get => _feather;
        set
        {
            if (!(value >= 0f && value <= 1000f))
            {
                throw new ArgumentOutOfRangeException(nameof(value));
            }

            if (_feather != value)
            {
                _feather = value;
                _owner.Changed(_owner.Bounds);
            }
        }
    }

    /// <summary>Gets or sets whether the mask is applied. The default is <see langword="true"/>.</summary>
    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (_enabled != value)
            {
                _enabled = value;
                _owner.Changed(_owner.Bounds);
            }
        }
    }

    /// <summary>Gets the area the mask canvas covers, in document coordinates.</summary>
    public ImageRect Area => new(_x, _y, Pixels.Width, Pixels.Height);

    /// <summary>Disposes the mask canvas. <see cref="Layer.RemoveMask"/> calls this.</summary>
    public void Dispose() => Pixels.Dispose();

    internal void Describe(List<OpParameter> p)
    {
        p.Add(new("mask-x", (long)_x));
        p.Add(new("mask-y", (long)_y));
        p.Add(new("mask-width", (long)Pixels.Width));
        p.Add(new("mask-height", (long)Pixels.Height));
        p.Add(new("mask-outside", (long)_outside));
        p.Add(new("mask-density", (double)_density));
        p.Add(new("mask-feather", (double)_feather));
        p.Add(new("mask-enabled", _enabled));
    }

    /// <summary>
    /// Snapshots the mask canvas, reports the tiles that changed since the last call through <paramref name="dirty"/> (document
    /// coordinates, widened by the feather reach) and returns the immutable state the renderer uses; null when disabled.
    /// </summary>
    internal MaskState? Capture(Action<ImageRect> dirty)
    {
        TileGrid grid = Pixels.SnapshotGrid();
        int margin = _feather > 0 ? (int)Math.Ceiling(_feather * 4) + 2 : 0;
        Document.DiffGrids(_lastGrid, grid, _x, _y, margin, dirty);
        _lastGrid = grid;
        if (!_enabled)
        {
            return null;
        }

        if (_state != null && ReferenceEquals(_stateGrid, grid) && _state.Matches(this))
        {
            return _state;
        }

        Image m = Image.FromNode(new GridSourceNode(grid));
        if (_feather > 0)
        {
            m = m.GaussianBlur(_feather, linear: false);
        }

        if (_density < 1f)
        {
            m = m.Levels(outBlack: 1.0 - _density);
        }

        _state = new MaskState(m.Root, Area, _outside, _density, _feather);
        _stateGrid = grid;
        return _state;
    }
}

/// <summary>The immutable render-time form of a mask.</summary>
internal sealed class MaskState
{
    private readonly byte _rawOutside;
    private readonly float _density;
    private readonly float _feather;

    public MaskState(Node node, ImageRect area, byte rawOutside, float density, float feather)
    {
        Node = node;
        Area = area;
        _rawOutside = rawOutside;
        _density = density;
        _feather = feather;
        double eff = (rawOutside * (double)density) + (255.0 * (1.0 - density));
        Outside = (byte)Math.Clamp((int)Math.Floor(eff + 0.5), 0, 255);
    }

    public Node Node { get; }

    public ImageRect Area { get; }

    /// <summary>The coverage outside the canvas, density applied.</summary>
    public byte Outside { get; }

    public bool Matches(LayerMask m) => m.Density == _density && m.Feather == _feather && m.OutsideValue == _rawOutside && m.Area == Area;
}
