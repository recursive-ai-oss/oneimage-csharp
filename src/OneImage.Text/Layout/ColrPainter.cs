using System.Numerics;
using OneImage.Drawing;
using OneImage.Text.Fonts;

using Path = OneImage.Drawing.Path;

namespace OneImage.Text.Layout;

/// <summary>A COLR version 1 glyph placed in the output: what to paint, where, and the plain outline to draw if it cannot be painted.</summary>
internal sealed class ColorGlyph(FontFace face, int glyph, ColrProgram program, in Matrix3x2 matrix, int palette, Path fallback)
{
    public readonly FontFace Face = face;
    public readonly int GlyphId = glyph;
    public readonly ColrProgram Program = program;

    /// <summary>Font units (y up) to image pixels.</summary>
    public readonly Matrix3x2 Matrix = matrix;
    public readonly int Palette = palette;
    public readonly Path Fallback = fallback;
}

/// <summary>
/// Paints a flattened COLR version 1 graph into a transparent 8-bit group the size of the glyph's ink: fills go through the
/// drawing layer's gradient brushes with the glyph outlines as clips, and composite paints combine whole layers with the
/// compositing operators. The group is then put on the image like any other picture.
/// </summary>
internal static class ColrPainter
{
    /// <summary>Largest group, in pixels on a side and in total.</summary>
    private const int MaxSide = 8192;
    private const long MaxPixels = 1L << 24;

    /// <summary>Paints the glyph. Returns false when it cannot (no ink, or a group too large); the caller then draws the plain outline.</summary>
    public static bool TryCreateFill(ColorGlyph glyph, Brush? foreground, out Brush brush, out Path area)
    {
        brush = null!;
        area = null!;
        ColrProgram prog = glyph.Program;
        ColorGlyphs colors = glyph.Face.Color!;
        if (!Bounds(glyph, out int x0, out int y0, out int w, out int h))
        {
            return false;
        }

        Rgba32? fg = ForegroundColor(foreground, x0, y0);
        Matrix3x2 toLayer = glyph.Matrix * Matrix3x2.CreateTranslation(-x0, -y0);
        Path? boxPath = null;
        if (prog.HasClipBox)
        {
            boxPath = Transformed(Path.Rectangle(prog.ClipXMin, prog.ClipYMin, prog.ClipXMax - prog.ClipXMin, prog.ClipYMax - prog.ClipYMin), toLayer);
        }

        var layers = new List<Canvas> { new(w, h, PixelFormat.Rgba8) };
        foreach (ColrOp op in prog.Ops)
        {
            switch (op.Kind)
            {
                case ColrOpKind.PushLayer:
                    layers.Add(new Canvas(w, h, PixelFormat.Rgba8));
                    break;
                case ColrOpKind.Merge:
                {
                    (BlendMode blend, AlphaComposition alpha) = Map(op.Mode);
                    Image merged = layers[^2].Snapshot().Composite(layers[^1].Snapshot(), 0, 0, 1f, false, blend, alpha);
                    layers.RemoveAt(layers.Count - 1);
                    layers[^1] = merged.ToCanvas();
                    break;
                }

                case ColrOpKind.PopOver:
                {
                    Image merged = layers[^2].Snapshot().Composite(layers[^1].Snapshot());
                    layers.RemoveAt(layers.Count - 1);
                    layers[^1] = merged.ToCanvas();
                    break;
                }

                default:
                    FillOp(layers[^1], op, glyph, colors, fg, toLayer, boxPath, w, h);
                    break;
            }
        }

        brush = Brush.FromImage(layers[0].Snapshot(), offsetX: x0, offsetY: y0, tile: false);
        area = Path.Rectangle(x0, y0, w, h);
        return true;
    }

    private static void FillOp(Canvas canvas, ColrOp op, ColorGlyph glyph, ColorGlyphs colors, Rgba32? fg, in Matrix3x2 toLayer, Path? boxPath, int w, int h)
    {
        Matrix3x2 gradientSpace = op.Matrix * toLayer;
        Brush? brush = MakeBrush(op, colors, glyph.Palette, fg, gradientSpace);
        if (brush is null)
        {
            return;
        }

        // The innermost glyph is the shape; the glyphs around it (and the clip box) only restrict it.
        Path area;
        Path? clip = boxPath;
        if (op.Clip is null)
        {
            area = Path.Rectangle(0, 0, w, h);
        }
        else
        {
            area = GlyphPath(glyph.Face, op.Clip, toLayer);
            for (ColrClip? c = op.Clip.Parent; c is not null; c = c.Parent)
            {
                Path p = GlyphPath(glyph.Face, c, toLayer);
                clip = clip is null ? p : clip.Intersect(p);
            }
        }

        canvas.Fill(brush, area, true, IntersectionRule.NonZero, 1f, BlendMode.Normal, AlphaComposition.SrcOver, false, null, clip);
    }

    private static Path GlyphPath(FontFace face, ColrClip clip, in Matrix3x2 toLayer)
    {
        var b = new PathBuilder();
        GlyphPaths.Append(b, face.GetOutline(clip.Glyph), clip.Matrix * toLayer);
        return b.Build();
    }

    private static Path Transformed(Path p, in Matrix3x2 m) => p.Transform(m);

    private static Rgba32? ForegroundColor(Brush? brush, int x, int y)
    {
        if (brush is null)
        {
            return null;
        }

        if (brush.TryGetSolid(out Rgba32 c))
        {
            return c;
        }

        Span<float> px = stackalloc float[4];
        brush.FillRow(x, y, px);
        static byte B(float v) => (byte)Math.Clamp((int)MathF.Round(v * 255f), 0, 255);
        return new Rgba32(B(px[0]), B(px[1]), B(px[2]), B(px[3]));
    }

    private static Rgba32 StopColor(ColorGlyphs colors, int palette, int index, float alpha, Rgba32? fg)
    {
        Rgba32 c;
        if (index == ColorGlyphs.ForegroundIndex)
        {
            c = fg ?? default;
        }
        else
        {
            c = colors.GetColor(palette, index) ?? default;
        }

        float a = Math.Clamp(alpha, 0f, 1f) * c.A;
        return new Rgba32(c.R, c.G, c.B, (byte)Math.Clamp((int)MathF.Round(a), 0, 255));
    }

    private static Brush? MakeBrush(ColrOp op, ColorGlyphs colors, int palette, Rgba32? fg, in Matrix3x2 m)
    {
        if (op.Fill == ColrFillKind.Solid)
        {
            return Brush.Solid(StopColor(colors, palette, op.Palette, op.Alpha, fg));
        }

        var stops = new ColorStop[op.Stops.Length];
        for (int i = 0; i < stops.Length; i++)
        {
            ColrStop s = op.Stops[i];
            stops[i] = new ColorStop(s.Offset, StopColor(colors, palette, s.Palette, s.Alpha, fg));
        }

        if (stops.Length == 1)
        {
            return Brush.Solid(stops[0].Color);
        }

        // Repeat and reflect work on the span between the first and the last stop: rescale the stops to 0..1 and move the
        // geometry to the circles, points or angles at the old first and last offsets.
        float first = stops[0].Position;
        float last = stops[^1].Position;
        if (last <= first)
        {
            // All stops at one offset: a hard edge from the first color to the last with pad, nothing with repeat or reflect.
            if (op.Extend != 0)
            {
                return null;
            }

            first = 0f;
            last = 1f;
        }

        for (int i = 0; i < stops.Length; i++)
        {
            stops[i] = stops[i] with { Position = (stops[i].Position - first) / (last - first) };
        }

        GradientRepetition rep = op.Extend switch { 1 => GradientRepetition.Repeat, 2 => GradientRepetition.Reflect, _ => GradientRepetition.None };
        if (!Matrix3x2.Invert(m, out Matrix3x2 inverse))
        {
            return null;
        }

        switch (op.Fill)
        {
            case ColrFillKind.Linear:
            {
                // The isolines run parallel to p0 -> p2; the position along the gradient is the part of p0 -> p1 across them.
                var p0 = new Vector2(op.X0, op.Y0);
                Vector2 d = new Vector2(op.X1, op.Y1) - p0;
                Vector2 r = new Vector2(op.X2, op.Y2) - p0;
                var n = new Vector2(-r.Y, r.X);
                float nn = n.LengthSquared();
                float dn = Vector2.Dot(d, n);
                if (nn == 0 || MathF.Abs(dn) < 1e-9f * MathF.Max(1f, nn))
                {
                    return null;
                }

                Vector2 end = p0 + (n * (dn / nn));
                return Brush.LinearGradient(p0 + ((end - p0) * first), p0 + ((end - p0) * last), stops, m, rep, premultiplied: false);
            }

            case ColrFillKind.Radial:
            {
                var o0 = new Vector2(op.X0, op.Y0);
                var o1 = new Vector2(op.X1, op.Y1);
                Vector2 c0 = o0 + ((o1 - o0) * first);
                Vector2 c1 = o0 + ((o1 - o0) * last);
                float r0 = MathF.Max(0f, op.R0 + ((op.R1 - op.R0) * first));
                float r1 = op.R0 + ((op.R1 - op.R0) * last);
                if (r1 <= 0 && r0 <= 0)
                {
                    return null;
                }

                if (r1 <= 0)
                {
                    // The brush wants a positive end radius: run the gradient the other way around.
                    Array.Reverse(stops);
                    for (int i = 0; i < stops.Length; i++)
                    {
                        stops[i] = stops[i] with { Position = 1f - stops[i].Position };
                    }

                    return Brush.RadialGradient(c0, r0, c1, 0, stops, m, rep, premultiplied: false);
                }

                return Brush.RadialGradient(c1, r1, c0, r0, stops, m, rep, premultiplied: false);
            }

            default:
            {
                return op.StartAngle == op.EndAngle && op.Extend != 0 ? null : new SweepGradientBrush(new Vector2(op.X0, op.Y0), op.StartAngle, op.EndAngle, first, last, stops, op.Extend, inverse);
            }
        }
    }

    // Device bounds of the glyph's ink: the union over every fill of the intersection of its clip glyphs, cut by the clip box.
    private static bool Bounds(ColorGlyph glyph, out int x0, out int y0, out int w, out int h)
    {
        x0 = y0 = w = h = 0;
        float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
        bool any = false;
        foreach (ColrOp op in glyph.Program.Ops)
        {
            if (op.Kind != ColrOpKind.Fill)
            {
                continue;
            }

            if (op.Clip is null)
            {
                // Paint with no outline fills the clip box, or else the plain glyph's ink.
                if (glyph.Program.HasClipBox)
                {
                    Add(glyph.Program.ClipXMin, glyph.Program.ClipYMin, glyph.Program.ClipXMax, glyph.Program.ClipYMax, glyph.Matrix);
                }
                else
                {
                    return false;
                }

                continue;
            }

            // The tightest of the clip glyphs' boxes bounds the visible part.
            float bx0 = float.MinValue, by0 = float.MinValue, bx1 = float.MaxValue, by1 = float.MaxValue;
            for (ColrClip? c = op.Clip; c is not null; c = c.Parent)
            {
                Outline o = glyph.Face.GetOutline(c.Glyph);
                if (o.IsEmpty)
                {
                    bx0 = float.MaxValue;
                    break;
                }

                (float ax, float ay, float bx, float by) = o.TightBox();
                Box(ax, ay, bx, by, c.Matrix * glyph.Matrix, out float u0, out float v0, out float u1, out float v1);
                bx0 = MathF.Max(bx0, u0);
                by0 = MathF.Max(by0, v0);
                bx1 = MathF.Min(bx1, u1);
                by1 = MathF.Min(by1, v1);
            }

            if (bx0 < bx1 && by0 < by1)
            {
                minX = MathF.Min(minX, bx0);
                minY = MathF.Min(minY, by0);
                maxX = MathF.Max(maxX, bx1);
                maxY = MathF.Max(maxY, by1);
                any = true;
            }
        }

        void Add(float ax, float ay, float bx, float by, in Matrix3x2 m)
        {
            Box(ax, ay, bx, by, m, out float u0, out float v0, out float u1, out float v1);
            minX = MathF.Min(minX, u0);
            minY = MathF.Min(minY, v0);
            maxX = MathF.Max(maxX, u1);
            maxY = MathF.Max(maxY, v1);
            any = true;
        }

        if (!any)
        {
            return false;
        }

        if (glyph.Program.HasClipBox)
        {
            Box(glyph.Program.ClipXMin, glyph.Program.ClipYMin, glyph.Program.ClipXMax, glyph.Program.ClipYMax, glyph.Matrix, out float u0, out float v0, out float u1, out float v1);
            minX = MathF.Max(minX, u0);
            minY = MathF.Max(minY, v0);
            maxX = MathF.Min(maxX, u1);
            maxY = MathF.Min(maxY, v1);
        }

        if (!(minX < maxX && minY < maxY) || !float.IsFinite(minX + maxX + minY + maxY) || Math.Abs(minX) > 1e7f || Math.Abs(maxX) > 1e7f || Math.Abs(minY) > 1e7f || Math.Abs(maxY) > 1e7f)
        {
            return false;
        }

        x0 = (int)MathF.Floor(minX) - 1;
        y0 = (int)MathF.Floor(minY) - 1;
        w = (int)MathF.Ceiling(maxX) + 1 - x0;
        h = (int)MathF.Ceiling(maxY) + 1 - y0;
        return w > 0 && h > 0 && w <= MaxSide && h <= MaxSide && (long)w * h <= MaxPixels;
    }

    private static void Box(float ax, float ay, float bx, float by, in Matrix3x2 m, out float x0, out float y0, out float x1, out float y1)
    {
        Vector2 a = Vector2.Transform(new Vector2(ax, ay), m);
        Vector2 b = Vector2.Transform(new Vector2(bx, by), m);
        Vector2 c = Vector2.Transform(new Vector2(ax, by), m);
        Vector2 d = Vector2.Transform(new Vector2(bx, ay), m);
        x0 = MathF.Min(MathF.Min(a.X, b.X), MathF.Min(c.X, d.X));
        x1 = MathF.Max(MathF.Max(a.X, b.X), MathF.Max(c.X, d.X));
        y0 = MathF.Min(MathF.Min(a.Y, b.Y), MathF.Min(c.Y, d.Y));
        y1 = MathF.Max(MathF.Max(a.Y, b.Y), MathF.Max(c.Y, d.Y));
    }

    // COLR composite modes (OpenType COLR, PaintComposite) as blend modes and Porter-Duff operators.
    private static (BlendMode, AlphaComposition) Map(int mode) => mode switch
    {
        0 => (BlendMode.Normal, AlphaComposition.Clear),
        1 => (BlendMode.Normal, AlphaComposition.Src),
        2 => (BlendMode.Normal, AlphaComposition.Dest),
        3 => (BlendMode.Normal, AlphaComposition.SrcOver),
        4 => (BlendMode.Normal, AlphaComposition.DestOver),
        5 => (BlendMode.Normal, AlphaComposition.SrcIn),
        6 => (BlendMode.Normal, AlphaComposition.DestIn),
        7 => (BlendMode.Normal, AlphaComposition.SrcOut),
        8 => (BlendMode.Normal, AlphaComposition.DestOut),
        9 => (BlendMode.Normal, AlphaComposition.SrcAtop),
        10 => (BlendMode.Normal, AlphaComposition.DestAtop),
        11 => (BlendMode.Normal, AlphaComposition.Xor),
        12 => (BlendMode.Add, AlphaComposition.SrcOver),
        13 => (BlendMode.Screen, AlphaComposition.SrcOver),
        14 => (BlendMode.Overlay, AlphaComposition.SrcOver),
        15 => (BlendMode.Darken, AlphaComposition.SrcOver),
        16 => (BlendMode.Lighten, AlphaComposition.SrcOver),
        17 => (BlendMode.ColorDodge, AlphaComposition.SrcOver),
        18 => (BlendMode.ColorBurn, AlphaComposition.SrcOver),
        19 => (BlendMode.HardLight, AlphaComposition.SrcOver),
        20 => (BlendMode.SoftLight, AlphaComposition.SrcOver),
        21 => (BlendMode.Difference, AlphaComposition.SrcOver),
        22 => (BlendMode.Exclusion, AlphaComposition.SrcOver),
        23 => (BlendMode.Multiply, AlphaComposition.SrcOver),
        24 => (BlendMode.Hue, AlphaComposition.SrcOver),
        25 => (BlendMode.Saturation, AlphaComposition.SrcOver),
        26 => (BlendMode.Color, AlphaComposition.SrcOver),
        _ => (BlendMode.Luminosity, AlphaComposition.SrcOver),
    };
}

/// <summary>
/// A sweep (conic) gradient: the angle around a center, counter-clockwise in the glyph's own y-up space, mapped from the start
/// angle to the end angle onto the color stops (offsets <c>first</c> to <c>last</c> of the angle range, stored rescaled to 0..1), with pad, repeat or reflect beyond them. Colors are interpolated as straight (non-premultiplied) values.
/// </summary>
internal sealed class SweepGradientBrush : Brush
{
    private readonly Vector2 _center;
    private readonly float _start;
    private readonly float _end;
    private readonly int _extend;
    private readonly float _first;
    private readonly float _scale;
    private readonly Matrix3x2 _inverse;
    private readonly float[] _pos;
    private readonly float[] _color;

    public SweepGradientBrush(Vector2 center, float startDegrees, float endDegrees, float first, float last, ColorStop[] stops, int extend, in Matrix3x2 inverse)
    {
        _first = first;
        _scale = 1f / (last - first);
        _center = center;
        _start = startDegrees;
        _end = endDegrees;
        _extend = extend;
        _inverse = inverse;
        _pos = new float[stops.Length];
        _color = new float[stops.Length * 4];
        for (int i = 0; i < stops.Length; i++)
        {
            _pos[i] = stops[i].Position;
            _color[(4 * i)] = stops[i].Color.R / 255f;
            _color[(4 * i) + 1] = stops[i].Color.G / 255f;
            _color[(4 * i) + 2] = stops[i].Color.B / 255f;
            _color[(4 * i) + 3] = stops[i].Color.A / 255f;
        }
    }

    public override void FillRow(int x, int y, Span<float> rgba)
    {
        for (int i = 0; i + 3 < rgba.Length; i += 4)
        {
            Vector2 p = Vector2.Transform(new Vector2(x + (i / 4) + 0.5f, y + 0.5f), _inverse) - _center;
            float deg = MathF.Atan2(p.Y, p.X) * (180f / MathF.PI);
            // The stops were rescaled to 0..1 over the color line's own span, which is where repeat and reflect work.
            float t = (Position(deg) - _first) * _scale;
            Color(t, rgba.Slice(i, 4));
        }
    }

    // The position along the gradient of an angle: the angle is taken in [0, 360) and mapped linearly from the start angle to the
    // end angle, with no wrapping around 360 (a range from 270 to 440 has nothing past 360), as Skia draws it. A reversed range
    // (end before start) runs the gradient backward; equal angles make a hard edge: the first color before, the last color from there on.
    private float Position(float deg)
    {
        float a = deg - (360f * MathF.Floor(deg / 360f));
        float span = _end - _start;
        if (span == 0)
        {
            return a < _start ? -1f : 2f;
        }

        return (a - _start) / span;
    }

    private void Color(float t, Span<float> o)
    {
        if (_extend == 1)
        {
            t -= MathF.Floor(t);
        }
        else if (_extend == 2)
        {
            float f = t - (2f * MathF.Floor(t / 2f));
            t = f <= 1f ? f : 2f - f;
        }

        int n = _pos.Length;
        float r;
        float g;
        float b;
        float a;
        if (t <= _pos[0])
        {
            (r, g, b, a) = (_color[0], _color[1], _color[2], _color[3]);
        }
        else if (t >= _pos[n - 1])
        {
            int k = 4 * (n - 1);
            (r, g, b, a) = (_color[k], _color[k + 1], _color[k + 2], _color[k + 3]);
        }
        else
        {
            int i = 1;
            while (_pos[i] < t)
            {
                i++;
            }

            float span = _pos[i] - _pos[i - 1];
            float f = span > 0 ? (t - _pos[i - 1]) / span : 1f;
            int k0 = 4 * (i - 1);
            int k1 = 4 * i;
            r = _color[k0] + ((_color[k1] - _color[k0]) * f);
            g = _color[k0 + 1] + ((_color[k1 + 1] - _color[k0 + 1]) * f);
            b = _color[k0 + 2] + ((_color[k1 + 2] - _color[k0 + 2]) * f);
            a = _color[k0 + 3] + ((_color[k1 + 3] - _color[k0 + 3]) * f);
        }

        o[0] = r;
        o[1] = g;
        o[2] = b;
        o[3] = a;
    }
}
