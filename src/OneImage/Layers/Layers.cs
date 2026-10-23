using OneImage.Drawing;

namespace OneImage.Layers;

/// <summary>
/// A layer of pixels: a <see cref="Canvas"/> (in the document's pixel format) placed with its top-left corner at (<see cref="X"/>, <see cref="Y"/>)
/// in document coordinates. The canvas is yours to paint on with every <see cref="Canvas"/> operation; the document notices which
/// tiles changed at the next <see cref="Document.Snapshot"/> and recomputes only the document tiles they touch.
/// Pixels outside the document are kept but not drawn; unwritten (sparse) canvas tiles are transparent.
/// </summary>
public sealed class PixelLayer : Layer, IDisposable
{
    private int _x;
    private int _y;

    internal PixelLayer(Document document, string name, int width, int height, int x, int y)
        : base(document, LayerKind.Pixel, name)
    {
        Pixels = document.CreateCanvas(width, height, document.Format);
        _x = x;
        _y = y;
    }

    internal PixelLayer(Document document, string name, Canvas canvas, int x, int y)
        : base(document, LayerKind.Pixel, name)
    {
        Pixels = canvas;
        _x = x;
        _y = y;
    }

    /// <summary>Gets the layer's canvas.</summary>
    public Canvas Pixels { get; }

    /// <summary>Gets or sets the document x of the canvas's left edge. May be negative or past the document's width.</summary>
    public int X
    {
        get => _x;
        set => Move(value, _y);
    }

    /// <summary>Gets or sets the document y of the canvas's top edge.</summary>
    public int Y
    {
        get => _y;
        set => Move(_x, value);
    }

    /// <inheritdoc/>
    public override ImageRect Bounds => new(_x, _y, Pixels.Width, Pixels.Height);

    internal TileGrid? LastGrid { get; set; }

    internal Node? LastNode { get; set; }

    /// <summary>Places the canvas's top-left corner at (<paramref name="x"/>, <paramref name="y"/>).</summary>
    /// <param name="x">Document x.</param>
    /// <param name="y">Document y.</param>
    public void Move(int x, int y)
    {
        if (x == _x && y == _y)
        {
            return;
        }

        Changed(Bounds);
        _x = x;
        _y = y;
        Changed(Bounds);
    }

    /// <summary>
    /// Marks everything painted so far as final, which lets a document with a spill store move those tiles to disk. A
    /// <see cref="Document.Snapshot"/> does this for every layer; call this while building a large document layer by layer to keep memory near the
    /// budget. It costs one table snapshot and no pixel copies.
    /// </summary>
    public void Flush() => Pixels.SnapshotGrid();

    /// <summary>Disposes the canvas (and the mask). The layer is detached from its group first.</summary>
    public void Dispose()
    {
        Parent?.Remove(this);
        Pixels.Dispose();
        DisposeContent();
    }

    internal override void DescribeSelf(List<OpParameter> parameters)
    {
        parameters.Add(new("x", (long)_x));
        parameters.Add(new("y", (long)_y));
        parameters.Add(new("width", (long)Pixels.Width));
        parameters.Add(new("height", (long)Pixels.Height));
    }
}

/// <summary>
/// A layer that fills the whole document with a <see cref="Brush"/> (a solid color or a gradient, in the document's color space), with
/// no pixels of its own. Like any layer it can have a mask, an opacity and a blend mode. The brush is evaluated per tile on demand.
/// </summary>
public sealed class FillLayer : Layer, IDisposable
{
    private Brush _brush;
    private Node? _node;

    internal FillLayer(Document document, string name, Brush brush)
        : base(document, LayerKind.Fill, name)
    {
        _brush = brush;
    }

    /// <summary>Gets or sets the brush. Treat a brush as immutable once it is set.</summary>
    /// <exception cref="ArgumentNullException">The value is null.</exception>
    public Brush Brush
    {
        get => _brush;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            _brush = value;
            _node = null;
            Changed(Bounds);
        }
    }

    /// <inheritdoc/>
    public override ImageRect Bounds => Document.Bounds;

    /// <summary>Disposes the mask, if any. The layer is detached from its group first.</summary>
    public void Dispose()
    {
        Parent?.Remove(this);
        DisposeContent();
    }

    internal Node ContentNode()
    {
        Node? n = _node;
        if (n == null)
        {
            using var blank = new Canvas(Document.Width, Document.Height, Document.Format);
            Image filled = blank.Snapshot().Fill(_brush, OneImage.Drawing.Path.Rectangle(0, 0, Document.Width, Document.Height), antialias: false, composition: AlphaComposition.Src);
            _node = n = filled.Root;
        }

        return n;
    }

    internal override void DescribeSelf(List<OpParameter> parameters)
    {
        foreach (OpParameter p in _brush.Describe())
        {
            parameters.Add(new("brush." + p.Name, p.Value));
        }
    }
}

/// <summary>
/// An adjustment layer: a point operation (levels, curves, hue/saturation, …) applied to everything below it in its scope, then
/// blended back with the layer's opacity, mask and blend mode. Where the opacity or mask is partial, the result is the
/// linear mix of the backdrop and the adjusted backdrop in premultiplied form; with a blend mode other than Normal the adjusted
/// backdrop is first blended over the backdrop with that mode. Alpha is untouched by the adjustment itself.
/// </summary>
public sealed class AdjustmentLayer : Layer, IDisposable
{
    private Adjustment _adjustment;

    internal AdjustmentLayer(Document document, string name, Adjustment adjustment)
        : base(document, LayerKind.Adjustment, name)
    {
        _adjustment = adjustment;
    }

    /// <summary>Gets or sets the adjustment.</summary>
    /// <exception cref="ArgumentNullException">The value is null.</exception>
    public Adjustment Adjustment
    {
        get => _adjustment;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            _adjustment = value;
            Changed(Bounds);
        }
    }

    /// <inheritdoc/>
    public override ImageRect Bounds => Document.Bounds;

    /// <summary>Disposes the mask, if any. The layer is detached from its group first.</summary>
    public void Dispose()
    {
        Parent?.Remove(this);
        DisposeContent();
    }

    internal override void DescribeSelf(List<OpParameter> parameters)
    {
        parameters.Add(new("adjustment", _adjustment.Name));
        foreach (OpParameter p in _adjustment.Parameters)
        {
            parameters.Add(new("adjustment." + p.Name, p.Value));
        }
    }
}

/// <summary>
/// A group of layers, bottom first. The document's <see cref="Document.Root"/> is a group.
/// <para>
/// <b>Pass-through</b> (the default): the children are composited directly onto what is below, so their blend modes and adjustment
/// layers see the backdrop. The group's opacity and mask then mix the result with the untouched backdrop
/// (<c>backdrop + k·(result − backdrop)</c>, premultiplied), and its blend mode is ignored.
/// <b>Isolated</b>: the children are composited onto a transparent surface first, and that surface is laid over the backdrop as one layer
/// with the group's blend mode, opacity and mask; adjustment layers and blend modes inside only see the group's own content.
/// </para>
/// </summary>
public sealed class GroupLayer : Layer, IDisposable
{
    /// <summary>The deepest nesting of groups the model accepts (the root is level 0).</summary>
    public const int MaxDepth = 64;

    private readonly List<Layer> _children = [];
    private bool _passThrough = true;

    internal GroupLayer(Document document, string name)
        : base(document, LayerKind.Group, name)
    {
    }

    /// <summary>Gets the children, bottom first.</summary>
    public IReadOnlyList<Layer> Children => _children;

    internal List<Layer> ChildList => _children;

    /// <summary>Gets or sets whether the group is pass-through (the default) or isolated; see the type remarks.</summary>
    public bool PassThrough
    {
        get => _passThrough;
        set
        {
            if (_passThrough != value)
            {
                _passThrough = value;
                Changed(Bounds);
            }
        }
    }

    internal override int ChildReach
    {
        get
        {
            int reach = 0;
            foreach (Layer c in _children)
            {
                reach = Math.Max(reach, c.Reach);
            }

            return reach;
        }
    }

    /// <inheritdoc/>
    public override ImageRect Bounds
    {
        get
        {
            ImageRect? union = null;
            foreach (Layer c in _children)
            {
                ImageRect b = c.Bounds;
                if (b.IsEmpty)
                {
                    continue;
                }

                union = union is { } u ? Union(u, b) : b;
            }

            return union ?? new ImageRect(0, 0, 0, 0);
        }
    }

    /// <summary>Puts <paramref name="layer"/> on top of the group.</summary>
    /// <param name="layer">A detached layer of this group's document.</param>
    /// <returns>The layer, for chaining.</returns>
    /// <exception cref="ArgumentException">The layer belongs to another document, already has a parent, or would nest groups deeper than <see cref="MaxDepth"/> or into itself.</exception>
    public T Add<T>(T layer)
        where T : Layer => Insert(_children.Count, layer);

    /// <summary>Puts <paramref name="layer"/> at <paramref name="index"/> (0 is the bottom).</summary>
    /// <param name="index">0..<c>Children.Count</c>.</param>
    /// <param name="layer">A detached layer of this group's document.</param>
    /// <returns>The layer, for chaining.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is out of range.</exception>
    /// <exception cref="ArgumentException">The layer belongs to another document, already has a parent, or would nest groups deeper than <see cref="MaxDepth"/> or into itself.</exception>
    public T Insert<T>(int index, T layer)
        where T : Layer
    {
        ArgumentNullException.ThrowIfNull(layer);
        if ((uint)index > (uint)_children.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        if (!ReferenceEquals(layer.Document, Document))
        {
            throw new ArgumentException("The layer belongs to another document.", nameof(layer));
        }

        if (layer.Parent != null || ReferenceEquals(layer, Document.Root))
        {
            throw new ArgumentException("The layer already has a parent; remove it first.", nameof(layer));
        }

        int depth = Depth() + (layer is GroupLayer g ? g.Height() : 0);
        if (depth > MaxDepth)
        {
            throw new ArgumentException($"Groups may nest at most {MaxDepth} deep.", nameof(layer));
        }

        for (GroupLayer? a = this; a != null; a = a.Parent)
        {
            if (ReferenceEquals(a, layer))
            {
                throw new ArgumentException("A group cannot contain itself.", nameof(layer));
            }
        }

        _children.Insert(index, layer);
        layer.Parent = this;
        ChangedAround(index, index, layer);
        return layer;
    }

    /// <summary>Takes <paramref name="layer"/> out of the group without disposing it (it can be added elsewhere).</summary>
    /// <param name="layer">The layer.</param>
    /// <returns>True if it was a child.</returns>
    public bool Remove(Layer layer)
    {
        int i = _children.IndexOf(layer);
        if (i < 0)
        {
            return false;
        }

        ChangedAround(i, i, layer);
        _children.RemoveAt(i);
        layer.Parent = null;
        return true;
    }

    /// <summary>Moves a child to a new position among its siblings (0 is the bottom).</summary>
    /// <param name="layer">A child of this group.</param>
    /// <param name="newIndex">The index it has afterwards, 0..<c>Children.Count - 1</c>.</param>
    /// <exception cref="ArgumentException"><paramref name="layer"/> is not a child.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="newIndex"/> is out of range.</exception>
    public void Move(Layer layer, int newIndex)
    {
        int i = _children.IndexOf(layer);
        if (i < 0)
        {
            throw new ArgumentException("Not a child of this group.", nameof(layer));
        }

        if ((uint)newIndex >= (uint)_children.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(newIndex));
        }

        if (i == newIndex)
        {
            return;
        }

        _children.RemoveAt(i);
        _children.Insert(newIndex, layer);
        ChangedAround(Math.Min(i, newIndex), Math.Max(i, newIndex), null);
    }

    /// <summary>Disposes the children and the mask. The group is detached from its parent first.</summary>
    public void Dispose()
    {
        Parent?.Remove(this);
        foreach (Layer c in _children.ToArray())
        {
            (c as IDisposable)?.Dispose();
        }

        DisposeContent();
    }

    internal override string BlendLabel() => _passThrough ? "pass-through" : base.BlendLabel();

    internal override void DescribeSelf(List<OpParameter> parameters) => parameters.Add(new("pass-through", _passThrough));

    internal override IReadOnlyList<LayerDescription> DescribeChildren()
    {
        var list = new List<LayerDescription>(_children.Count);
        foreach (Layer c in _children)
        {
            list.Add(c.Describe());
        }

        return list;
    }

    // Marks what a structural edit can change: the moved layer and the siblings it passed. A clipping run is the one thing that couples
    // a layer to its neighbors beyond overlap, so an edit touching one marks every sibling.
    private void ChangedAround(int from, int to, Layer? edited)
    {
        if (!IsAttached)
        {
            return;
        }

        bool clipped = edited is { ClipToBelow: true };
        for (int k = Math.Max(0, from - 1); k <= Math.Min(_children.Count - 1, to + 1); k++)
        {
            clipped |= _children[k].ClipToBelow;
        }

        if (clipped)
        {
            Changed(Bounds);
            if (edited != null)
            {
                Document.Invalidate(Widen(edited.Bounds, edited.Reach));
            }

            return;
        }

        if (edited != null)
        {
            Document.Invalidate(Widen(edited.Bounds, edited.Reach));
        }

        for (int k = from; k <= Math.Min(to, _children.Count - 1); k++)
        {
            Document.Invalidate(Widen(_children[k].Bounds, _children[k].Reach));
        }
    }

    private int Depth()
    {
        int d = 0;
        for (GroupLayer? a = Parent; a != null; a = a.Parent)
        {
            d++;
        }

        return d;
    }

    // Levels of groups below this one, counting this one: a group with only pixel children is 1.
    private int Height()
    {
        int h = 0;
        var stack = new Stack<(GroupLayer Group, int Level)>();
        stack.Push((this, 1));
        while (stack.Count > 0)
        {
            (GroupLayer g, int level) = stack.Pop();
            h = Math.Max(h, level);
            foreach (Layer c in g._children)
            {
                if (c is GroupLayer cg)
                {
                    stack.Push((cg, level + 1));
                }
            }
        }

        return h;
    }

    private static ImageRect Union(ImageRect a, ImageRect b)
    {
        long x0 = Math.Min(a.X, b.X);
        long y0 = Math.Min(a.Y, b.Y);
        long x1 = Math.Max((long)a.Right, b.Right);
        long y1 = Math.Max((long)a.Bottom, b.Bottom);
        return new ImageRect((int)x0, (int)y0, (int)Math.Min(x1 - x0, int.MaxValue), (int)Math.Min(y1 - y0, int.MaxValue));
    }
}
