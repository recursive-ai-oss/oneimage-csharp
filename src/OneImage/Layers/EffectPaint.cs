using System.Globalization;

namespace OneImage.Layers;

/// <summary>How the colors of an <see cref="EffectGradientFill"/> are laid out over the shape.</summary>
public enum EffectGradientStyle
{
    /// <summary>A straight blend along the gradient's angle.</summary>
    Linear = 0,

    /// <summary>A blend from the center outward, in circles.</summary>
    Radial = 1,

    /// <summary>A sweep around the center, starting at the gradient's angle and running clockwise.</summary>
    Angle = 2,

    /// <summary>The linear blend mirrored about the center line.</summary>
    Reflected = 3,

    /// <summary>A blend from the center outward, in diamonds.</summary>
    Diamond = 4,

    /// <summary>
    /// Only for strokes and glows: the blend follows the distance from the edge of the shape (Photoshop's "shape burst"): the first color at
    /// the edge, the last color where the distance reaches the stroke's size.
    /// </summary>
    ShapeBurst = 5,
}

/// <summary>A color stop of an <see cref="EffectGradient"/>.</summary>
/// <param name="Location">The position from 0 to 1 along the gradient.</param>
/// <param name="Midpoint">Where the halfway color between this stop and the previous one sits, from 0 to 1 (0.5 is the middle).</param>
/// <param name="Color">The color (straight, 0 to 1 per channel; its alpha is ignored, see <see cref="GradientOpacityStop"/>).</param>
public readonly record struct GradientColorStop(float Location, float Midpoint, RgbaFloat Color);

/// <summary>An opacity stop of an <see cref="EffectGradient"/>.</summary>
/// <param name="Location">The position from 0 to 1 along the gradient.</param>
/// <param name="Midpoint">Where the halfway opacity between this stop and the previous one sits, from 0 to 1 (0.5 is the middle).</param>
/// <param name="Opacity">The opacity from 0 to 1.</param>
public readonly record struct GradientOpacityStop(float Location, float Midpoint, float Opacity);

/// <summary>
/// A gradient for layer effects: color stops and opacity stops that are independent, as in Photoshop's gradient editor. Between two stops the
/// blend is a straight line mixed with an ease curve by <see cref="Smoothness"/>; midpoints shift the halfway point. Immutable.
/// </summary>
public sealed class EffectGradient
{
    /// <summary>The most stops of one kind a gradient holds.</summary>
    public const int MaxStops = 256;

    private float[]? _table;

    /// <summary>Creates a gradient. Stops are sorted by location.</summary>
    /// <param name="colors">At least one color stop with locations in 0 to 1.</param>
    /// <param name="opacities">At least one opacity stop with locations in 0 to 1; null for a fully opaque gradient.</param>
    /// <param name="smoothness">The ease share between stops, from 0 (straight lines) to 1 (Photoshop's default).</param>
    /// <param name="linearLight">True to blend colors in linear light rather than in the encoded values.</param>
    /// <exception cref="ArgumentException">A list is empty or longer than <see cref="MaxStops"/>, or a value is out of range.</exception>
    public EffectGradient(IEnumerable<GradientColorStop> colors, IEnumerable<GradientOpacityStop>? opacities = null, float smoothness = 1f, bool linearLight = false)
    {
        ArgumentNullException.ThrowIfNull(colors);
        GradientColorStop[] c = [.. colors.OrderBy(static s => s.Location)];
        GradientOpacityStop[] o = opacities == null ? [new(0, 0.5f, 1), new(1, 0.5f, 1)] : [.. opacities.OrderBy(static s => s.Location)];
        if (c.Length == 0 || c.Length > MaxStops || o.Length == 0 || o.Length > MaxStops)
        {
            throw new ArgumentException($"A gradient needs 1 to {MaxStops} stops of each kind.");
        }

        foreach (GradientColorStop s in c)
        {
            if (!(s.Location >= 0 && s.Location <= 1 && s.Midpoint >= 0 && s.Midpoint <= 1 && float.IsFinite(s.Color.R) && float.IsFinite(s.Color.G) && float.IsFinite(s.Color.B)))
            {
                throw new ArgumentException("A color stop needs a location and midpoint in 0 to 1 and a finite color.", nameof(colors));
            }
        }

        foreach (GradientOpacityStop s in o)
        {
            if (!(s.Location >= 0 && s.Location <= 1 && s.Midpoint >= 0 && s.Midpoint <= 1 && s.Opacity >= 0 && s.Opacity <= 1))
            {
                throw new ArgumentException("An opacity stop needs a location, midpoint and opacity in 0 to 1.", nameof(opacities));
            }
        }

        if (!(smoothness >= 0 && smoothness <= 1))
        {
            throw new ArgumentOutOfRangeException(nameof(smoothness), "Smoothness must be between 0 and 1.");
        }

        Colors = c;
        Opacities = o;
        Smoothness = smoothness;
        LinearLight = linearLight;
    }

    /// <summary>Gets the color stops, sorted by location.</summary>
    public IReadOnlyList<GradientColorStop> Colors { get; }

    /// <summary>Gets the opacity stops, sorted by location.</summary>
    public IReadOnlyList<GradientOpacityStop> Opacities { get; }

    /// <summary>Gets the ease share between stops, 0 to 1.</summary>
    public float Smoothness { get; }

    /// <summary>Gets a value indicating whether colors blend in linear light.</summary>
    public bool LinearLight { get; }

    internal const int TableSize = 4096;

    /// <summary>The gradient sampled at TableSize + 1 positions as straight RGBA floats.</summary>
    internal float[] Table
    {
        get
        {
            float[]? t = Volatile.Read(ref _table);
            if (t == null)
            {
                t = Build();
                Volatile.Write(ref _table, t);
            }

            return t;
        }
    }

    internal string Label() =>
        string.Join(';', Colors.Select(static s => string.Create(CultureInfo.InvariantCulture, $"{s.Location:0.####}:{s.Midpoint:0.##}:#{Hex(s.Color.R)}{Hex(s.Color.G)}{Hex(s.Color.B)}")))
        + "|" + string.Join(';', Opacities.Select(static s => string.Create(CultureInfo.InvariantCulture, $"{s.Location:0.####}:{s.Midpoint:0.##}:{s.Opacity:0.###}")))
        + "|" + Layer.Num(Smoothness) + (LinearLight ? "|linear" : string.Empty);

    private static string Hex(float v) => ((int)Math.Round(Math.Clamp(v, 0f, 1f) * 255f)).ToString("x2", CultureInfo.InvariantCulture);

    private float[] Build()
    {
        float[] table = new float[(TableSize + 1) * 4];
        for (int i = 0; i <= TableSize; i++)
        {
            float t = (float)i / TableSize;
            (float r, float g, float b) = ColorAt(t);
            table[i * 4] = r;
            table[(i * 4) + 1] = g;
            table[(i * 4) + 2] = b;
            table[(i * 4) + 3] = OpacityAt(t);
        }

        return table;
    }

    // The midpoint bends the blend: the halfway color sits at the midpoint instead of the middle of the span.
    private static float Bend(float s, float mid)
    {
        mid = Math.Clamp(mid, 1e-4f, 1f - 1e-4f);
        return s <= mid ? 0.5f * s / mid : 0.5f + (0.5f * (s - mid) / (1f - mid));
    }

    // Between two stops the blend is the straight line mixed with a smoothstep (zero slope at the stops); full smoothness is an even mix.
    private static float Ease(float s, float smooth) => s + (0.5f * smooth * ((s * s * (3f - (2f * s))) - s));

    private (float R, float G, float B) ColorAt(float t)
    {
        IReadOnlyList<GradientColorStop> s = Colors;
        if (t <= s[0].Location)
        {
            RgbaFloat f = s[0].Color;
            return (f.R, f.G, f.B);
        }

        for (int i = 1; i < s.Count; i++)
        {
            if (t <= s[i].Location)
            {
                GradientColorStop p = s[i - 1];
                GradientColorStop q = s[i];
                float span = q.Location - p.Location;
                float k = span > 0f ? Ease(Bend((t - p.Location) / span, q.Midpoint), Smoothness) : 1f;
                if (LinearLight)
                {
                    return (
                        Encode(Decode(p.Color.R) + ((Decode(q.Color.R) - Decode(p.Color.R)) * k)),
                        Encode(Decode(p.Color.G) + ((Decode(q.Color.G) - Decode(p.Color.G)) * k)),
                        Encode(Decode(p.Color.B) + ((Decode(q.Color.B) - Decode(p.Color.B)) * k)));
                }

                return (p.Color.R + ((q.Color.R - p.Color.R) * k), p.Color.G + ((q.Color.G - p.Color.G) * k), p.Color.B + ((q.Color.B - p.Color.B) * k));
            }
        }

        RgbaFloat last = s[^1].Color;
        return (last.R, last.G, last.B);
    }

    private float OpacityAt(float t)
    {
        IReadOnlyList<GradientOpacityStop> s = Opacities;
        if (t <= s[0].Location)
        {
            return s[0].Opacity;
        }

        for (int i = 1; i < s.Count; i++)
        {
            if (t <= s[i].Location)
            {
                GradientOpacityStop p = s[i - 1];
                GradientOpacityStop q = s[i];
                float span = q.Location - p.Location;
                float k = span > 0f ? Ease(Bend((t - p.Location) / span, q.Midpoint), Smoothness) : 1f;
                return p.Opacity + ((q.Opacity - p.Opacity) * k);
            }
        }

        return s[^1].Opacity;
    }

    private static float Decode(float v) => v <= 0.04045f ? v / 12.92f : MathF.Pow((v + 0.055f) / 1.055f, 2.4f);

    private static float Encode(float v) => v <= 0.0031308f ? v * 12.92f : (1.055f * MathF.Pow(v, 1f / 2.4f)) - 0.055f;
}

/// <summary>
/// A gradient laid out over a layer's shape, the settings shared by gradient overlays, gradient strokes and gradient glows. The gradient runs along
/// <see cref="Angle"/> through the middle of its box (the layer's bounds, or the whole document when <see cref="AlignWithLayer"/> is false), from one
/// edge of the box to the other; <see cref="Scale"/> stretches that length and <see cref="OffsetX"/>/<see cref="OffsetY"/> move the middle. Immutable.
/// Photoshop's dither noise (about one level) is not reproduced.
/// </summary>
public sealed class EffectGradientFill
{
    /// <summary>Creates gradient settings.</summary>
    /// <param name="gradient">The colors.</param>
    /// <param name="style">The layout; the default is linear.</param>
    /// <param name="angle">The angle in degrees, counter-clockwise from the right. Default 90 (Photoshop's overlay default).</param>
    /// <param name="scale">The length as a fraction (1 = 100 %), 0.1 to 10; default 1.</param>
    /// <param name="reverse">True to run the gradient backward.</param>
    /// <param name="alignWithLayer">True (the default) to lay the gradient out on the layer's bounds, false for the document.</param>
    /// <param name="offsetX">The middle's offset, as a fraction of the box width.</param>
    /// <param name="offsetY">The middle's offset, as a fraction of the box height (positive is down).</param>
    /// <exception cref="ArgumentOutOfRangeException">The scale is outside 0.1..10, or a number is not finite.</exception>
    public EffectGradientFill(EffectGradient gradient, EffectGradientStyle style = EffectGradientStyle.Linear, float angle = 90f, float scale = 1f, bool reverse = false, bool alignWithLayer = true, float offsetX = 0f, float offsetY = 0f)
    {
        ArgumentNullException.ThrowIfNull(gradient);
        if (!(scale >= 0.1f && scale <= 10f))
        {
            throw new ArgumentOutOfRangeException(nameof(scale), "Scale must be between 0.1 and 10.");
        }

        if (!float.IsFinite(angle) || !(Math.Abs(offsetX) <= 100f) || !(Math.Abs(offsetY) <= 100f))
        {
            throw new ArgumentOutOfRangeException(nameof(angle), "Angle and offsets must be finite (offsets within 100).");
        }

        Gradient = gradient;
        Style = style;
        Angle = angle;
        Scale = scale;
        Reverse = reverse;
        AlignWithLayer = alignWithLayer;
        OffsetX = offsetX;
        OffsetY = offsetY;
    }

    /// <summary>Gets the colors.</summary>
    public EffectGradient Gradient { get; }

    /// <summary>Gets the layout.</summary>
    public EffectGradientStyle Style { get; }

    /// <summary>Gets the angle in degrees, counter-clockwise from the right.</summary>
    public float Angle { get; }

    /// <summary>Gets the length as a fraction (1 = 100 %).</summary>
    public float Scale { get; }

    /// <summary>Gets a value indicating whether the gradient runs backward.</summary>
    public bool Reverse { get; }

    /// <summary>Gets a value indicating whether the gradient is laid out on the layer's bounds rather than the document.</summary>
    public bool AlignWithLayer { get; }

    /// <summary>Gets the middle's horizontal offset as a fraction of the box width.</summary>
    public float OffsetX { get; }

    /// <summary>Gets the middle's vertical offset as a fraction of the box height.</summary>
    public float OffsetY { get; }

    internal void Describe(List<OpParameter> p, string prefix)
    {
        p.Add(new(prefix + "gradient", Gradient.Label()));
        p.Add(new(prefix + "gradient-style", Style.ToString().ToLowerInvariant()));
        p.Add(new(prefix + "gradient-angle", (double)Angle));
        p.Add(new(prefix + "gradient-scale", (double)Scale));
        p.Add(new(prefix + "gradient-reverse", Reverse));
        p.Add(new(prefix + "gradient-align", AlignWithLayer));
        p.Add(new(prefix + "gradient-offset-x", (double)OffsetX));
        p.Add(new(prefix + "gradient-offset-y", (double)OffsetY));
    }

    /// <summary>Paints straight RGBA for a row of pixels given the box, the stroke/glow size for shape burst is handled by the caller.</summary>
    internal Painter CreatePainter(ImageRect box, ImageRect document) => new(this, AlignWithLayer ? box : document);

    internal sealed class Painter
    {
        private readonly EffectGradientFill _fill;
        private readonly float _cx, _cy, _dx, _dy, _invLength, _invRadius, _angle;

        public Painter(EffectGradientFill fill, ImageRect box)
        {
            _fill = fill;
            double w = Math.Max(box.Width, 1e-3);
            double h = Math.Max(box.Height, 1e-3);
            double a = fill.Angle * Math.PI / 180.0;
            double cos = Math.Cos(a);
            double sin = Math.Sin(a);
            _dx = (float)cos;
            _dy = (float)-sin;
            _cx = (float)(box.X + (w / 2) + (fill.OffsetX * w));
            _cy = (float)(box.Y + (h / 2) + (fill.OffsetY * h));
            // The gradient runs along a line through the center of the box from one edge to the other; the scale stretches that line. For radial,
            // reflected and diamond layouts the radius is half of it. (Fitted to Photoshop's gradient fill rasters.)
            double chord = Math.Min(Math.Abs(cos) > 1e-9 ? w / Math.Abs(cos) : double.MaxValue, Math.Abs(sin) > 1e-9 ? h / Math.Abs(sin) : double.MaxValue);
            double length = fill.Scale * chord;
            _invLength = (float)(1.0 / Math.Max(length, 1e-6));
            _invRadius = (float)(2.0 / Math.Max(length, 1e-6));
            _angle = (float)a;
        }

        /// <summary>The position 0..1 along the gradient of the pixel at (x, y).</summary>
        public float Position(int x, int y)
        {
            // Photoshop evaluates the position at the pixel's own integer coordinates, not at its center.
            float px = x - _cx;
            float py = y - _cy;
            float u = (px * _dx) + (py * _dy);
            float t;
            switch (_fill.Style)
            {
                case EffectGradientStyle.Linear:
                    t = 0.5f + (u * _invLength);
                    break;
                case EffectGradientStyle.Reflected:
                    t = MathF.Abs(u) * _invRadius;
                    break;
                case EffectGradientStyle.Radial:
                    t = MathF.Sqrt((px * px) + (py * py)) * _invRadius;
                    break;
                case EffectGradientStyle.Diamond:
                    {
                        float v = (-px * _dy) + (py * _dx);
                        t = (MathF.Abs(u) + MathF.Abs(v)) * _invRadius;
                        break;
                    }

                default:
                    {
                        float turn = (_angle - MathF.Atan2(-py, px)) / (2f * MathF.PI);
                        t = turn - MathF.Floor(turn);
                        break;
                    }
            }

            return _fill.Reverse ? 1f - t : t;
        }

        /// <summary>Writes the straight RGBA of position <paramref name="t"/> (clamped to 0..1).</summary>
        public void Lookup(float t, Span<float> o)
        {
            if (!(t > 0f))
            {
                t = 0f;
            }
            else if (t > 1f)
            {
                t = 1f;
            }

            float f = t * EffectGradient.TableSize;
            int i = (int)f;
            if (i >= EffectGradient.TableSize)
            {
                i = EffectGradient.TableSize - 1;
            }

            float k = f - i;
            int a = i * 4;
            float[] tb = _fill.Gradient.Table;
            for (int c = 0; c < 4; c++)
            {
                o[c] = tb[a + c] + ((tb[a + 4 + c] - tb[a + c]) * k);
            }
        }
    }
}

/// <summary>A pattern tile for <see cref="EffectPatternFill"/>: straight 8-bit RGBA pixels, row by row. Immutable.</summary>
public sealed class EffectPattern
{
    /// <summary>The most pixels a pattern holds.</summary>
    public const int MaxPixels = 1 << 24;

    private readonly byte[] _rgba;

    /// <summary>Creates a pattern.</summary>
    /// <param name="width">The width in pixels.</param>
    /// <param name="height">The height in pixels.</param>
    /// <param name="rgba">Width x height x 4 bytes of straight RGBA; copied.</param>
    /// <param name="id">An identifier that keeps the pattern recognizable when a file is written (for a PSD, its unique id); may be null.</param>
    /// <exception cref="ArgumentException">The size is not positive, has more than <see cref="MaxPixels"/> pixels, or does not match the data.</exception>
    public EffectPattern(int width, int height, ReadOnlySpan<byte> rgba, string? id = null)
    {
        if (width < 1 || height < 1 || (long)width * height > MaxPixels || rgba.Length != (long)width * height * 4)
        {
            throw new ArgumentException("A pattern needs a positive size of at most 2^24 pixels and width x height x 4 bytes.");
        }

        Width = width;
        Height = height;
        Id = id;
        _rgba = rgba.ToArray();
    }

    /// <summary>Gets the width in pixels.</summary>
    public int Width { get; }

    /// <summary>Gets the height in pixels.</summary>
    public int Height { get; }

    /// <summary>Gets the identifier, or null.</summary>
    public string? Id { get; }

    /// <summary>Gets the pixels as straight RGBA bytes.</summary>
    public ReadOnlySpan<byte> Pixels => _rgba;
}

/// <summary>
/// A pattern laid over a layer's shape: <see cref="Pattern"/> tiled from the layer's top-left corner (or the document's when
/// <see cref="LinkWithLayer"/> is false), shifted by <see cref="PhaseX"/>/<see cref="PhaseY"/> and resized by <see cref="Scale"/> with
/// nearest-neighbor sampling. Immutable.
/// </summary>
public sealed class EffectPatternFill
{
    /// <summary>Creates pattern settings.</summary>
    /// <param name="pattern">The tile.</param>
    /// <param name="scale">The size as a fraction (1 = 100 %), 0.01 to 10; default 1.</param>
    /// <param name="linkWithLayer">True (the default) to start the tiling at the layer's corner, false at the document's.</param>
    /// <param name="phaseX">The horizontal shift in pixels.</param>
    /// <param name="phaseY">The vertical shift in pixels.</param>
    /// <exception cref="ArgumentOutOfRangeException">The scale is outside 0.01..10 or a shift is not finite.</exception>
    public EffectPatternFill(EffectPattern pattern, float scale = 1f, bool linkWithLayer = true, float phaseX = 0f, float phaseY = 0f)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        if (!(scale >= 0.01f && scale <= 10f))
        {
            throw new ArgumentOutOfRangeException(nameof(scale), "Scale must be between 0.01 and 10.");
        }

        if (!(Math.Abs(phaseX) <= LayerEffect.MaxDistance) || !(Math.Abs(phaseY) <= LayerEffect.MaxDistance))
        {
            throw new ArgumentOutOfRangeException(nameof(phaseX), "A pattern shift must be a number within 30000 pixels.");
        }

        Pattern = pattern;
        Scale = scale;
        LinkWithLayer = linkWithLayer;
        PhaseX = phaseX;
        PhaseY = phaseY;
    }

    /// <summary>Gets the tile.</summary>
    public EffectPattern Pattern { get; }

    /// <summary>Gets the size as a fraction (1 = 100 %).</summary>
    public float Scale { get; }

    /// <summary>Gets a value indicating whether the tiling starts at the layer's corner rather than the document's.</summary>
    public bool LinkWithLayer { get; }

    /// <summary>Gets the horizontal shift in pixels.</summary>
    public float PhaseX { get; }

    /// <summary>Gets the vertical shift in pixels.</summary>
    public float PhaseY { get; }

    internal void Describe(List<OpParameter> p, string prefix)
    {
        p.Add(new(prefix + "pattern", Pattern.Id ?? string.Create(CultureInfo.InvariantCulture, $"{Pattern.Width}x{Pattern.Height}")));
        p.Add(new(prefix + "pattern-scale", (double)Scale));
        p.Add(new(prefix + "pattern-link", LinkWithLayer));
        p.Add(new(prefix + "pattern-phase-x", (double)PhaseX));
        p.Add(new(prefix + "pattern-phase-y", (double)PhaseY));
    }

    /// <summary>Writes straight RGBA for a row of pixels (the span length / 4) starting at document pixel (x, y).</summary>
    internal void FillRow(ImageRect box, int x, int y, Span<float> rgba)
    {
        int w = Pattern.Width;
        int h = Pattern.Height;
        ReadOnlySpan<byte> px = Pattern.Pixels;
        // The tile starts at the layer's left edge plus one pixel and its top edge (measured on Photoshop's pattern fills); the document origin when not linked.
        double ox = (LinkWithLayer ? box.X + 1 : 0) + PhaseX;
        double oy = (LinkWithLayer ? box.Y : 0) + PhaseY;
        double inv = 1.0 / Scale;
        int n = rgba.Length / 4;
        int iy = Mod((int)Math.Floor((y - oy) * inv), h);
        for (int i = 0; i < n; i++)
        {
            int ix = Scale == 1f ? Mod((int)Math.Floor(x + i - ox), w) : Mod((int)Math.Floor((x + i - ox) * inv), w);
            int s = ((iy * w) + ix) * 4;
            rgba[i * 4] = px[s] * (1f / 255f);
            rgba[(i * 4) + 1] = px[s + 1] * (1f / 255f);
            rgba[(i * 4) + 2] = px[s + 2] * (1f / 255f);
            rgba[(i * 4) + 3] = px[s + 3] * (1f / 255f);
        }
    }

    private static int Mod(int v, int m)
    {
        int r = v % m;
        return r < 0 ? r + m : r;
    }
}
