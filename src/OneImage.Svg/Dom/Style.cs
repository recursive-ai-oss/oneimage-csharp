using System.Globalization;

namespace OneImage.Svg.Dom;

internal enum PaintKind
{
    None,
    Color,
    CurrentColor,

    /// <summary>A reference to a paint server (<c>url(#id)</c>), with its fallback.</summary>
    Server,

    ContextFill,
    ContextStroke,
}

/// <summary>The ids of the markers a shape asks for; <see langword="null"/> where there is none.</summary>
internal sealed record MarkerRefs(string? Start, string? Mid, string? End)
{
    public static MarkerRefs None { get; } = new(null, null, null);
}

/// <summary>A <c>fill</c> or <c>stroke</c> value.</summary>
/// <param name="Kind">What kind of paint.</param>
/// <param name="Color">The color for <see cref="PaintKind.Color"/> (straight alpha).</param>
/// <param name="ServerId">The referenced id for <see cref="PaintKind.Server"/>.</param>
/// <param name="Fallback">What to use when the server cannot be used: <see cref="PaintKind.None"/>, <see cref="PaintKind.Color"/> or <see cref="PaintKind.CurrentColor"/>.</param>
/// <param name="FallbackColor">The fallback color for <see cref="PaintKind.Color"/>.</param>
internal readonly record struct Paint(PaintKind Kind, Rgba32 Color = default, string? ServerId = null, PaintKind Fallback = PaintKind.None, Rgba32 FallbackColor = default)
{
    public static Paint None { get; } = new(PaintKind.None);

    public static Paint Black { get; } = new(PaintKind.Color, new Rgba32(0, 0, 0, 255));
}

/// <summary>
/// The computed values of the properties the renderer uses, for one element. Lengths stay unresolved (they need the viewport at the
/// place where they are used); everything else is final.
/// </summary>
internal struct SvgStyle
{
    public const double DefaultFontSize = 16;

    /// <summary>The <see cref="PaintOrder"/> code for fill, stroke, markers.</summary>
    public const int NormalPaintOrder = 0 | (1 << 2) | (2 << 4);

    /// <summary>Gets whether the stroke is painted before the fill.</summary>
    public readonly bool StrokeFirst => PaintIndex(1) < PaintIndex(0);

    /// <summary>Gets the position (0 first) of fill (0), stroke (1) or markers (2) in the paint order.</summary>
    public readonly int PaintIndex(int what)
    {
        for (int i = 0; i < 3; i++)
        {
            if (((PaintOrder >> (2 * i)) & 3) == what)
            {
                return i;
            }
        }

        return what;
    }

    // Inherited.
    public Paint Fill;
    public Paint Stroke;
    public double FillOpacity;
    public double StrokeOpacity;
    public bool FillEvenOdd;
    public bool ClipEvenOdd;
    public Length StrokeWidth;
    public int LineCap;
    public int LineJoin;
    public double MiterLimit;
    public Length[]? DashArray;
    public Length DashOffset;
    public Rgba32 Color;
    public double FontSize;
    public bool Visible;
    public bool Antialias;
    public bool Pixelated;

    /// <summary>The <c>paint-order</c> as three base-4 digits, first painted first: 0 fill, 1 stroke, 2 markers. Normal is fill, stroke, markers.</summary>
    public int PaintOrder;

    /// <summary>The ids of the markers (<c>marker-start</c>, <c>marker-mid</c>, <c>marker-end</c>), or <see langword="null"/> when none is set. Inherited. One shared reference, so a style stays small.</summary>
    public MarkerRefs? Markers;

    // Not inherited.
    public double Opacity;
    public bool Displayed;

    /// <summary><c>mix-blend-mode</c>: how the element's group blends with what is behind it.</summary>
    public BlendMode Blend;

    /// <summary><c>mix-blend-mode: plus-lighter</c>: the group adds to what is behind it (Porter-Duff plus) instead of blending.</summary>
    public bool Plus;

    /// <summary><c>isolation: isolate</c>: the element's group is composited on its own, so blend modes inside it only see the group.</summary>
    public bool Isolate;

    /// <summary>Gets whether the element's content has to be painted into a group of its own and composited as one.</summary>
    public readonly bool NeedsLayer => Opacity < 1 || Blend != BlendMode.Normal || Plus || Isolate;

    public static SvgStyle Initial => new()
    {
        Fill = Paint.Black,
        Stroke = Paint.None,
        FillOpacity = 1,
        StrokeOpacity = 1,
        StrokeWidth = new Length(1, LengthUnit.Px),
        LineCap = 0,
        LineJoin = 0,
        MiterLimit = 4,
        DashOffset = Length.Zero,
        Color = new Rgba32(0, 0, 0, 255),
        FontSize = DefaultFontSize,
        Visible = true,
        Antialias = true,
        PaintOrder = NormalPaintOrder,
        Pixelated = false,
        Opacity = 1,
        Displayed = true,
    };

    /// <summary>
    /// Computes an element's style from its declared properties and its parent's computed style.
    /// An invalid declaration is ignored, as if it had not been written.
    /// </summary>
    /// <param name="e">The element.</param>
    /// <param name="parent">The parent's computed style (for <c>use</c>d content, the <c>use</c> element's).</param>
    /// <param name="rootFontSize">The root element's font size, for <c>rem</c>.</param>
    public static SvgStyle Compute(SvgElement e, in SvgStyle parent, double rootFontSize)
    {
        SvgStyle s = parent;
        s.Opacity = 1;
        s.Displayed = true;
        s.Blend = BlendMode.Normal;
        s.Plus = false;
        s.Isolate = false;
        if (e.Props.Count == 0)
        {
            return s;
        }

        // font-size and color first: other values (em lengths, currentColor) are evaluated against the element's own.
        if (e.Props.TryGetValue("font-size", out string? fs))
        {
            s.FontSize = fs == "initial" ? DefaultFontSize : ParseFontSize(fs, parent.FontSize, rootFontSize) ?? parent.FontSize;
        }

        if (e.Props.TryGetValue("color", out string? col))
        {
            if (col == "inherit")
            {
                s.Color = parent.Color;
            }
            else if (col == "initial")
            {
                s.Color = Initial.Color;
            }
            else if (CssColor.TryParse(col, out Rgba32 c))
            {
                s.Color = c;
            }
        }

        foreach ((string name, string value) in e.Props)
        {
            if (value == "inherit")
            {
                InheritOne(ref s, name, parent, e);
                continue;
            }

            if (value == "initial")
            {
                InitialOne(ref s, name);
                continue;
            }

            if (value == "unset")
            {
                // unset is inherit for inherited properties and initial for the others.
                if (Inherited(name))
                {
                    InheritOne(ref s, name, parent, e);
                }
                else
                {
                    InitialOne(ref s, name);
                }

                continue;
            }

            switch (name)
            {
                case "fill":
                    if (TryPaint(value, out Paint pf))
                    {
                        s.Fill = pf;
                    }

                    break;
                case "stroke":
                    if (TryPaint(value, out Paint ps))
                    {
                        s.Stroke = ps;
                    }

                    break;
                case "fill-opacity":
                    if (TryOpacity(value, out double fo))
                    {
                        s.FillOpacity = fo;
                    }

                    break;
                case "stroke-opacity":
                    if (TryOpacity(value, out double so))
                    {
                        s.StrokeOpacity = so;
                    }

                    break;
                case "opacity":
                    if (TryOpacity(value, out double o))
                    {
                        s.Opacity = o;
                    }

                    break;
                case "paint-order":
                    if (TryPaintOrder(value, out int po))
                    {
                        s.PaintOrder = po;
                    }

                    break;
                case "fill-rule":
                    if (value is "evenodd" or "nonzero")
                    {
                        s.FillEvenOdd = value == "evenodd";
                    }

                    break;
                case "clip-rule":
                    if (value is "evenodd" or "nonzero")
                    {
                        s.ClipEvenOdd = value == "evenodd";
                    }

                    break;
                case "stroke-width":
                    // A negative width is an error that SVG leaves undefined; the stroke is dropped rather than drawn at the default.
                    if (Length.TryParse(value, out Length w))
                    {
                        s.StrokeWidth = w;
                    }

                    break;
                case "stroke-linecap":
                    if (value is "butt" or "round" or "square")
                    {
                        s.LineCap = value == "butt" ? 0 : value == "round" ? 1 : 2;
                    }

                    break;
                case "stroke-linejoin":
                    // SVG 2 adds miter-clip and arcs; both fall back as the specification allows (to miter and miter).
                    if (value is "miter" or "round" or "bevel" or "miter-clip" or "arcs")
                    {
                        s.LineJoin = value == "round" ? 1 : value == "bevel" ? 2 : 0;
                    }

                    break;
                case "stroke-miterlimit":
                    if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double ml) && double.IsFinite(ml))
                    {
                        s.MiterLimit = ml;
                    }

                    break;
                case "stroke-dasharray":
                    s.DashArray = ParseDashArray(value, s.DashArray);
                    break;
                case "stroke-dashoffset":
                    if (Length.TryParse(value, out Length d))
                    {
                        s.DashOffset = d;
                    }

                    break;
                case "visibility":
                    if (value is "visible" or "hidden" or "collapse")
                    {
                        s.Visible = value == "visible";
                    }

                    break;
                case "display":
                    s.Displayed = value != "none";
                    break;
                case "mix-blend-mode":
                    if (value == "plus-lighter")
                    {
                        s.Blend = BlendMode.Normal;
                        s.Plus = true;
                    }
                    else if (TryBlend(value, out BlendMode bm))
                    {
                        s.Blend = bm;
                        s.Plus = false;
                    }

                    break;
                case "isolation":
                    if (value is "auto" or "isolate")
                    {
                        s.Isolate = value == "isolate";
                    }

                    break;
                case "marker-start":
                    if (TryMarker(value, out string? ms))
                    {
                        s.Markers = (s.Markers ?? MarkerRefs.None) with { Start = ms };
                    }

                    break;
                case "marker-mid":
                    if (TryMarker(value, out string? mm))
                    {
                        s.Markers = (s.Markers ?? MarkerRefs.None) with { Mid = mm };
                    }

                    break;
                case "marker-end":
                    if (TryMarker(value, out string? me))
                    {
                        s.Markers = (s.Markers ?? MarkerRefs.None) with { End = me };
                    }

                    break;
                case "marker":
                    if (TryMarker(value, out string? all))
                    {
                        s.Markers = new MarkerRefs(all, all, all);
                    }

                    break;
                case "image-rendering":
                    if (value is "optimizeSpeed" or "pixelated" or "crisp-edges" or "auto" or "optimizeQuality" or "smooth" or "high-quality")
                    {
                        s.Pixelated = value is "optimizeSpeed" or "pixelated" or "crisp-edges";
                    }

                    break;
                case "shape-rendering":
                    if (value is "optimizeSpeed" or "crispEdges" or "geometricPrecision" or "auto")
                    {
                        s.Antialias = value is "geometricPrecision" or "auto";
                    }

                    break;
            }
        }

        return s;
    }

    private static void InheritOne(ref SvgStyle s, string name, in SvgStyle p, SvgElement e)
    {
        switch (name)
        {
            case "fill": s.Fill = p.Fill; break;
            case "stroke": s.Stroke = p.Stroke; break;
            case "fill-opacity": s.FillOpacity = p.FillOpacity; break;
            case "stroke-opacity": s.StrokeOpacity = p.StrokeOpacity; break;
            case "opacity": s.Opacity = p.Opacity; break;
            case "fill-rule": s.FillEvenOdd = p.FillEvenOdd; break;
            case "paint-order": s.PaintOrder = p.PaintOrder; break;
            case "clip-rule": s.ClipEvenOdd = p.ClipEvenOdd; break;
            case "stroke-width": s.StrokeWidth = p.StrokeWidth; break;
            case "stroke-linecap": s.LineCap = p.LineCap; break;
            case "stroke-linejoin": s.LineJoin = p.LineJoin; break;
            case "stroke-miterlimit": s.MiterLimit = p.MiterLimit; break;
            case "stroke-dasharray": s.DashArray = p.DashArray; break;
            case "stroke-dashoffset": s.DashOffset = p.DashOffset; break;
            case "visibility": s.Visible = p.Visible; break;
            case "shape-rendering": s.Antialias = p.Antialias; break;
            case "image-rendering": s.Pixelated = p.Pixelated; break;
            case "display": s.Displayed = true; break;
            case "marker-start": s.Markers = (s.Markers ?? MarkerRefs.None) with { Start = p.Markers?.Start }; break;
            case "marker-mid": s.Markers = (s.Markers ?? MarkerRefs.None) with { Mid = p.Markers?.Mid }; break;
            case "marker-end": s.Markers = (s.Markers ?? MarkerRefs.None) with { End = p.Markers?.End }; break;
            case "marker": s.Markers = p.Markers; break;
            case "mix-blend-mode": s.Blend = p.Blend; s.Plus = p.Plus; break;
            case "isolation": s.Isolate = p.Isolate; break;
            case "font-size": s.FontSize = p.FontSize; break;
        }

        _ = e;
    }

    /// <summary>Parses <c>paint-order</c>: <c>normal</c>, or one to three of fill, stroke, markers (each once); the ones left out follow in the normal order.</summary>
    private static bool TryPaintOrder(string value, out int code)
    {
        code = NormalPaintOrder;
        string[] parts = value.Split([' ', '\t', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            return false;
        }

        if (parts.Length == 1 && parts[0] == "normal")
        {
            return true;
        }

        if (parts.Length > 3)
        {
            return false;
        }

        var order = new List<int>(3);
        foreach (string part in parts)
        {
            int what = part switch { "fill" => 0, "stroke" => 1, "markers" => 2, _ => -1 };
            if (what < 0 || order.Contains(what))
            {
                return false;
            }

            order.Add(what);
        }

        for (int what = 0; what < 3; what++)
        {
            if (!order.Contains(what))
            {
                order.Add(what);
            }
        }

        code = order[0] | (order[1] << 2) | (order[2] << 4);
        return true;
    }

    private static bool Inherited(string name) => name is "fill" or "stroke" or "fill-opacity" or "stroke-opacity" or "fill-rule" or "paint-order" or "clip-rule"
        or "stroke-width" or "stroke-linecap" or "stroke-linejoin" or "stroke-miterlimit" or "stroke-dasharray" or "stroke-dashoffset"
        or "visibility" or "shape-rendering" or "image-rendering" or "font-size" or "color" or "marker" or "marker-start" or "marker-mid" or "marker-end";

    private static void InitialOne(ref SvgStyle s, string name)
    {
        SvgStyle i = Initial;
        switch (name)
        {
            case "fill": s.Fill = i.Fill; break;
            case "stroke": s.Stroke = i.Stroke; break;
            case "fill-opacity": s.FillOpacity = i.FillOpacity; break;
            case "stroke-opacity": s.StrokeOpacity = i.StrokeOpacity; break;
            case "opacity": s.Opacity = i.Opacity; break;
            case "fill-rule": s.FillEvenOdd = i.FillEvenOdd; break;
            case "paint-order": s.PaintOrder = i.PaintOrder; break;
            case "clip-rule": s.ClipEvenOdd = i.ClipEvenOdd; break;
            case "stroke-width": s.StrokeWidth = i.StrokeWidth; break;
            case "stroke-linecap": s.LineCap = i.LineCap; break;
            case "stroke-linejoin": s.LineJoin = i.LineJoin; break;
            case "stroke-miterlimit": s.MiterLimit = i.MiterLimit; break;
            case "stroke-dasharray": s.DashArray = i.DashArray; break;
            case "stroke-dashoffset": s.DashOffset = i.DashOffset; break;
            case "visibility": s.Visible = i.Visible; break;
            case "shape-rendering": s.Antialias = i.Antialias; break;
            case "image-rendering": s.Pixelated = i.Pixelated; break;
            case "marker-start": s.Markers = (s.Markers ?? MarkerRefs.None) with { Start = null }; break;
            case "marker-mid": s.Markers = (s.Markers ?? MarkerRefs.None) with { Mid = null }; break;
            case "marker-end": s.Markers = (s.Markers ?? MarkerRefs.None) with { End = null }; break;
            case "marker": s.Markers = null; break;
            case "mix-blend-mode": s.Blend = BlendMode.Normal; s.Plus = false; break;
            case "isolation": s.Isolate = false; break;
            case "display": s.Displayed = true; break;
            case "font-size": s.FontSize = i.FontSize; break;
            case "color": s.Color = i.Color; break;
        }
    }

    // CSS Compositing: the blend modes of mix-blend-mode (plus-lighter is handled apart: it is a Porter-Duff operator).
    private static bool TryBlend(string value, out BlendMode mode)
    {
        mode = value switch
        {
            "normal" => BlendMode.Normal,
            "multiply" => BlendMode.Multiply,
            "screen" => BlendMode.Screen,
            "overlay" => BlendMode.Overlay,
            "darken" => BlendMode.Darken,
            "lighten" => BlendMode.Lighten,
            "color-dodge" => BlendMode.ColorDodge,
            "color-burn" => BlendMode.ColorBurn,
            "hard-light" => BlendMode.HardLight,
            "soft-light" => BlendMode.SoftLight,
            "difference" => BlendMode.Difference,
            "exclusion" => BlendMode.Exclusion,
            "hue" => BlendMode.Hue,
            "saturation" => BlendMode.Saturation,
            "color" => BlendMode.Color,
            "luminosity" => BlendMode.Luminosity,
            _ => (BlendMode)(-1),
        };
        return mode >= 0;
    }

    // A marker reference: none, or url(#id) (anything else is an invalid declaration).
    internal static bool TryMarker(string value, out string? id)
    {
        id = null;
        ReadOnlySpan<char> v = value.AsSpan().Trim();
        if (v.Equals("none", StringComparison.Ordinal))
        {
            return true;
        }

        if (!v.StartsWith("url(", StringComparison.OrdinalIgnoreCase) || v.IndexOf(')') is not (var close and > 0) || !v[(close + 1)..].Trim().IsEmpty)
        {
            return false;
        }

        ReadOnlySpan<char> iri = v[4..close].Trim().Trim('\'').Trim('"').Trim();
        if (iri.Length > 1 && iri[0] == '#')
        {
            id = iri[1..].ToString();
        }

        return true;
    }

    private static bool TryOpacity(string v, out double result)
    {
        result = 1;
        ReadOnlySpan<char> t = v.AsSpan().Trim();
        bool percent = t.EndsWith("%");
        if (percent)
        {
            t = t[..^1];
        }

        if (!double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) || !double.IsFinite(d))
        {
            return false;
        }

        result = Math.Clamp(percent ? d / 100 : d, 0, 1);
        return true;
    }

    /// <summary>Parses a <c>fill</c>/<c>stroke</c> value: <c>none</c>, a color, <c>currentColor</c>, <c>context-*</c> or <c>url(#id) [fallback]</c>.</summary>
    private static bool TryPaint(string value, out Paint paint)
    {
        paint = default;
        ReadOnlySpan<char> v = value.AsSpan().Trim();
        if (v.StartsWith("url(", StringComparison.OrdinalIgnoreCase))
        {
            int close = v.IndexOf(')');
            if (close < 0)
            {
                return false;
            }

            ReadOnlySpan<char> iri = v[4..close].Trim().Trim('\'').Trim('"').Trim();
            string? id = iri.Length > 1 && iri[0] == '#' ? iri[1..].ToString() : null;
            ReadOnlySpan<char> rest = v[(close + 1)..].Trim();
            PaintKind fb = PaintKind.None;
            Rgba32 fc = default;
            if (!rest.IsEmpty)
            {
                if (rest.Equals("none", StringComparison.Ordinal))
                {
                    fb = PaintKind.None;
                }
                else if (rest.Equals("currentColor", StringComparison.OrdinalIgnoreCase))
                {
                    fb = PaintKind.CurrentColor;
                }
                else if (CssColor.TryParse(rest, out fc))
                {
                    fb = PaintKind.Color;
                }
                else
                {
                    return false;
                }
            }

            // A reference to something that is not in this document (a file or a URL) is never fetched; it is a dangling reference.
            paint = id is null ? (fb == PaintKind.None ? Paint.None : new Paint(fb, fc)) : new Paint(PaintKind.Server, default, id, fb, fc);
            return true;
        }

        if (v.Equals("none", StringComparison.Ordinal))
        {
            paint = Paint.None;
            return true;
        }

        if (v.Equals("currentColor", StringComparison.OrdinalIgnoreCase))
        {
            paint = new Paint(PaintKind.CurrentColor);
            return true;
        }

        if (v.Equals("context-fill", StringComparison.Ordinal))
        {
            paint = new Paint(PaintKind.ContextFill);
            return true;
        }

        if (v.Equals("context-stroke", StringComparison.Ordinal))
        {
            paint = new Paint(PaintKind.ContextStroke);
            return true;
        }

        if (CssColor.TryParse(v, out Rgba32 c))
        {
            paint = new Paint(PaintKind.Color, c);
            return true;
        }

        return false;
    }

    private static Length[]? ParseDashArray(string value, Length[]? current)
    {
        string v = value.Trim();
        if (v == "none")
        {
            return null;
        }

        var list = new List<Length>();
        foreach (string part in v.Split([' ', ',', '\t', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (!Length.TryParse(part, out Length l) || l.Value < 0)
            {
                return current;
            }

            list.Add(l);
        }

        return list.Count == 0 ? current : [.. list];
    }

    private static double? ParseFontSize(string value, double parentSize, double rootSize)
    {
        string v = value.Trim();
        switch (v)
        {
            case "xx-small": return 9;
            case "x-small": return 10;
            case "small": return 13;
            case "medium": return 16;
            case "large": return 18;
            case "x-large": return 24;
            case "xx-large": return 32;
            case "larger": return parentSize * 1.2;
            case "smaller": return parentSize / 1.2;
        }

        if (!Length.TryParse(v, out Length l) || l.Value < 0)
        {
            return null;
        }

        return l.Unit switch
        {
            LengthUnit.Px => l.Value,
            LengthUnit.Em => l.Value * parentSize,
            LengthUnit.Ex => l.Value * parentSize / 2,
            LengthUnit.Percent => l.Value * parentSize / 100,
            LengthUnit.Rem => l.Value * rootSize,
            _ => null,
        };
    }
}
