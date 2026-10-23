using System.Buffers;

namespace OneImage.Layers;

/// <summary>Reads the shape (alpha, after the layer's mask) an effect is drawn from.</summary>
internal interface IFxSource
{
    /// <summary>Writes the shape's alpha (0..1) for <paramref name="region"/> in document coordinates into <paramref name="dest"/>, row by row; zero where the layer has no pixels.</summary>
    void ReadAlpha(ImageRect region, float[] dest);
}

/// <summary>Which part of an effect an <see cref="FxItem"/> draws: all of it, or one side of a centered stroke.</summary>
internal enum FxPart
{
    Whole,
    OutsideHalf,
    InsideHalf,
}

/// <summary>How an effect that is drawn on top of the layer's pixels combines with them.</summary>
internal enum FxInterior
{
    /// <summary>Drawn over the backdrop below the layer's pixels.</summary>
    None,

    /// <summary>Replaces the color of the layer's own pixels and keeps their alpha (overlays).</summary>
    Recolor,

    /// <summary>Mixes into the layer's pixels within the layer's shape (inner shadow and glow, satin, inside strokes).</summary>
    Shade,
}

internal readonly record struct FxItem(LayerEffect Effect, FxPart Part)
{
    public FxInterior Interior => Effect.BelowLayer || (Effect is StrokeEffect { Position: StrokePosition.Center } && Part == FxPart.OutsideHalf)
        ? FxInterior.None
        : Effect is ColorOverlayEffect ? FxInterior.Recolor : FxInterior.Shade;

    public bool Below => Interior == FxInterior.None;
}

/// <summary>What a layer's tile job draws around its pixels: the enabled effects of its style, split by side.</summary>
internal sealed class FxPlan
{
    private FxPlan(FxItem[] below, FxItem[] above, float opacity, float fillOpacity, int reach, bool shapeFromMask)
    {
        ShapeFromMask = shapeFromMask;
        FillOpacity = fillOpacity;
        Below = below;
        Above = above;
        Opacity = opacity;
        Reach = reach;
    }

    /// <summary>Effects drawn over the backdrop under the layer's pixels (shadows, outer glows, outside strokes).</summary>
    public FxItem[] Below { get; }

    /// <summary>Effects drawn on the layer's pixels (overlays, satin, inner glows and shadows, inside strokes).</summary>
    public FxItem[] Above { get; }

    /// <summary>The layer's own opacity (without its fill opacity), which scales every effect.</summary>
    public float Opacity { get; }

    /// <summary>The layer's fill opacity, which scales the layer's pixels.</summary>
    public float FillOpacity { get; }

    public int Reach { get; }

    /// <summary>True when the shape is the mask alone.</summary>
    public bool ShapeFromMask { get; }

    public static FxPlan? Create(LayerStyle? style, float layerOpacity, float fillOpacity)
    {
        if (style == null || !style.Any)
        {
            return null;
        }

        var below = new List<FxItem>();
        var above = new List<FxItem>();
        foreach (LayerEffect e in style.Effects)
        {
            if (!e.Enabled)
            {
                continue;
            }

            if (e is StrokeEffect { Position: StrokePosition.Center })
            {
                // A centered stroke has an outer half under the layer's pixels and an inner half on them.
                below.Add(new FxItem(e, FxPart.OutsideHalf));
                above.Add(new FxItem(e, FxPart.InsideHalf));
            }
            else
            {
                var item = new FxItem(e, FxPart.Whole);
                (item.Below ? below : above).Add(item);
            }
        }

        // Stable: effects of one kind keep their list order.
        return new FxPlan([.. below.OrderBy(static i => i.Effect.Order)], [.. above.OrderBy(static i => i.Effect.Order)], layerOpacity, fillOpacity, style.Reach, style.ShapeFromMask);
    }
}

/// <summary>
/// Computes the coverage (alpha) of one effect over a rectangle of the document from the layer's shape. All work is float and
/// separable, over a window around the rectangle that is as large as the effect can reach, so a tile's result does not depend on how
/// the document is cut into tiles.
/// </summary>
internal static class EffectRenderer
{
    // Photoshop's "size" is a blur extent, not a standard deviation. Fitted against the shadows, glows and inner shadows of the sample documents (mean error
    // 0.2 levels on the shadows; 0.38 gave 0.4, 0.46 gave 0.43, 0.5 gave 0.76): the Gaussian that matches it has sigma = 0.41 x size.
    internal const float SigmaPerSize = 0.41f;

    /// <summary>Fills <paramref name="cov"/> (rect.Width × rect.Height) with the effect's alpha; returns false when it is all zero.</summary>
    public static bool Render(FxItem item, IFxSource source, ImageRect rect, float[] cov)
    {
        int n = rect.Width * rect.Height;
        Array.Clear(cov, 0, n);
        LayerEffect effect = item.Effect;
        switch (effect)
        {
            case DropShadowEffect d:
                return DropShadow(d, source, rect, cov);
            case InnerShadowEffect i:
                return Inner(source, rect, cov, i.Angle, i.Distance, i.Choke, i.Size, 1f, i.Contour, center: false);
            case OuterGlowEffect g:
                return OuterGlow(g, source, rect, cov);
            case InnerGlowEffect ig:
                return Inner(source, rect, cov, 0, 0, ig.Choke, ig.Size, ig.Range, ig.Contour, ig.Source == GlowSource.Center);
            case StrokeEffect s:
                return Stroke(s, item.Part, source, rect, cov);
            case ColorOverlayEffect:
                source.ReadAlpha(rect, cov);
                return true;
            case SatinEffect sa:
                return Satin(sa, source, rect, cov);
            default:
                return false;
        }
    }

    private static bool DropShadow(DropShadowEffect d, IFxSource source, ImageRect rect, float[] cov)
    {
        (double dx, double dy) = Offset(d.Angle, d.Distance);
        float[]? plane = ShiftedMatte(source, rect, dx, dy, d.Spread, d.Size, invert: false, out Plane p);
        if (plane == null)
        {
            return false;
        }

        try
        {
            SampleShifted(p, rect, dx, dy, cov);
        }
        finally
        {
            ArrayPool<float>.Shared.Return(plane);
        }

        Contour(cov, rect.Width * rect.Height, d.Contour);
        if (d.KnockedOut)
        {
            float[] own = ArrayPool<float>.Shared.Rent(rect.Width * rect.Height);
            try
            {
                source.ReadAlpha(rect, own);
                for (int i = 0; i < rect.Width * rect.Height; i++)
                {
                    cov[i] *= 1f - own[i];
                }
            }
            finally
            {
                ArrayPool<float>.Shared.Return(own);
            }
        }

        return true;
    }

    private static bool OuterGlow(OuterGlowEffect g, IFxSource source, ImageRect rect, float[] cov)
    {
        float[]? plane = ShiftedMatte(source, rect, 0, 0, g.Spread, g.Size, invert: false, out Plane p);
        if (plane == null)
        {
            return false;
        }

        try
        {
            SampleShifted(p, rect, 0, 0, cov);
        }
        finally
        {
            ArrayPool<float>.Shared.Return(plane);
        }

        Stretch(cov, rect.Width * rect.Height, g.Range);
        Contour(cov, rect.Width * rect.Height, g.Contour);
        return true;
    }

    // Inner shadow and inner glow: the area outside the shape (moved, grown, blurred), seen through the shape.
    private static bool Inner(IFxSource source, ImageRect rect, float[] cov, float angle, float distance, float choke, float size, float range, EffectContour? contour, bool center)
    {
        (double dx, double dy) = Offset(angle, distance);
        float[]? plane = ShiftedMatte(source, rect, dx, dy, choke, size, invert: true, out Plane p);
        float[] own = ArrayPool<float>.Shared.Rent(rect.Width * rect.Height);
        try
        {
            source.ReadAlpha(rect, own);
            if (plane == null)
            {
                return false;
            }

            SampleShifted(p, rect, dx, dy, cov);
            int n = rect.Width * rect.Height;
            Stretch(cov, n, range);
            if (center)
            {
                for (int i = 0; i < n; i++)
                {
                    cov[i] = 1f - cov[i];
                }
            }

            Contour(cov, n, contour);
            for (int i = 0; i < n; i++)
            {
                cov[i] *= own[i];
            }

            return true;
        }
        finally
        {
            ArrayPool<float>.Shared.Return(own);
            if (plane != null)
            {
                ArrayPool<float>.Shared.Return(plane);
            }
        }
    }

    private static bool Satin(SatinEffect s, IFxSource source, ImageRect rect, float[] cov)
    {
        (double dx, double dy) = Offset(s.Angle, s.Distance);
        float sigma = s.Size * SigmaPerSize;
        BlurPlan blur = BlurPlan.For(sigma);
        int ox = (int)Math.Round(dx);
        int oy = (int)Math.Round(dy);
        int pad = blur.Margin + 2 + Math.Max(Math.Abs(ox), Math.Abs(oy));
        ImageRect region = Layer.Widen(rect, pad);
        float[] a = ArrayPool<float>.Shared.Rent(region.Width * region.Height);
        float[] diff = ArrayPool<float>.Shared.Rent(region.Width * region.Height);
        float[] own = ArrayPool<float>.Shared.Rent(rect.Width * rect.Height);
        try
        {
            source.ReadAlpha(region, a);
            int w = region.Width;
            int h = region.Height;
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    float p1 = At(a, w, h, x - ox, y - oy);
                    float p2 = At(a, w, h, x + ox, y + oy);
                    diff[(y * w) + x] = Math.Abs(p1 - p2);
                }
            }

            blur.Apply(diff, w, h);
            int n = rect.Width * rect.Height;
            source.ReadAlpha(rect, own);
            for (int y = 0; y < rect.Height; y++)
            {
                for (int x = 0; x < rect.Width; x++)
                {
                    float v = diff[((y + pad) * w) + x + pad];
                    cov[(y * rect.Width) + x] = s.Inverted ? 1f - v : v;
                }
            }

            Contour(cov, n, s.Contour);
            for (int i = 0; i < n; i++)
            {
                cov[i] *= own[i];
            }

            return true;
        }
        finally
        {
            ArrayPool<float>.Shared.Return(a);
            ArrayPool<float>.Shared.Return(diff);
            ArrayPool<float>.Shared.Return(own);
        }
    }

    private static float At(float[] a, int w, int h, int x, int y) => (uint)x < (uint)w && (uint)y < (uint)h ? a[(y * w) + x] : 0f;

    // The stroke is a band around the edge of the shape. Outside of the shape it lies under the layer's pixels and, at an antialiased edge pixel, covers
    // the whole pixel (the layer's own partial pixel is drawn over it). Inside, it lies on the pixels and covers only the shape's part of an edge pixel.
    private static bool Stroke(StrokeEffect s, FxPart part, IFxSource source, ImageRect rect, float[] cov)
    {
        float size = s.Size;
        if (size <= 0f)
        {
            return false;
        }

        int margin = (int)Math.Ceiling(size) + 2;
        ImageRect region = Layer.Widen(rect, margin);
        float[] a = ArrayPool<float>.Shared.Rent(region.Width * region.Height);
        try
        {
            source.ReadAlpha(region, a);
            int w = region.Width;
            bool outer = s.Position == StrokePosition.Outside || part == FxPart.OutsideHalf;
            bool inner = s.Position == StrokePosition.Inside || part == FxPart.InsideHalf;
            float reach = s.Position == StrokePosition.Center ? size / 2f : size;
            using var field = new SignedDistance(a, w, region.Height, outside: outer, inside: inner);
            for (int y = 0; y < rect.Height; y++)
            {
                for (int x = 0; x < rect.Width; x++)
                {
                    int i = ((y + margin) * w) + x + margin;
                    float d = field.Value(i);
                    cov[(y * rect.Width) + x] = outer
                        ? (a[i] < 1f ? Clamp01(reach - d + 0.5f) : 0f)
                        : Clamp01(reach + d + 0.5f) * Clamp01(0.5f - d);
                }
            }

            return true;
        }
        finally
        {
            ArrayPool<float>.Shared.Return(a);
        }
    }

    /// <summary>The direction the shadow falls: away from the light, y down.</summary>
    private static (double Dx, double Dy) Offset(float angleDegrees, float distance)
    {
        double rad = angleDegrees * Math.PI / 180.0;
        return (-Math.Cos(rad) * distance, Math.Sin(rad) * distance);
    }

    private readonly struct Plane(float[] data, ImageRect region)
    {
        public float[] Data { get; } = data;

        public ImageRect Region { get; } = region;
    }

    // Reads the shape around the rectangle (moved by the offset), turns it into a matte, optionally inverts it, grows it by spread × size and blurs it by the rest.
    // The caller gets the plane (to return to the pool) and samples it at the shifted positions.
    private static float[]? ShiftedMatte(IFxSource source, ImageRect rect, double dx, double dy, float spread, float size, bool invert, out Plane plane)
    {
        float grow = spread * size;
        float blurSize = size - grow;
        BlurPlan blur = BlurPlan.For(blurSize * SigmaPerSize);
        int sx = (int)Math.Floor(-dx);
        int sy = (int)Math.Floor(-dy);
        int growMargin = grow > 0f ? (int)Math.Ceiling(grow) + 2 : 0;
        int margin = blur.Margin + growMargin + 1;
        // The window that has to be right: the rectangle moved to where the matte is sampled, plus one pixel for interpolation.
        var needed = new ImageRect(rect.X + sx, rect.Y + sy, rect.Width + 1, rect.Height + 1);
        ImageRect region = Layer.Widen(needed, margin);
        float[] data = ArrayPool<float>.Shared.Rent(region.Width * region.Height);
        plane = new Plane(data, region);
        int n = region.Width * region.Height;
        source.ReadAlpha(region, data);
        if (invert)
        {
            for (int i = 0; i < n; i++)
            {
                data[i] = 1f - data[i];
            }
        }
        else if (!AnyPositive(data, n))
        {
            ArrayPool<float>.Shared.Return(data);
            return null;
        }

        if (grow > 0f)
        {
            Dilate(data, region.Width, region.Height, grow);
        }

        blur.Apply(data, region.Width, region.Height);
        return data;
    }

    private static bool AnyPositive(float[] a, int n)
    {
        for (int i = 0; i < n; i++)
        {
            if (a[i] > 0f)
            {
                return true;
            }
        }

        return false;
    }

    // cov(x, y) = matte(x - dx, y - dy), interpolated bilinearly between the matte's pixels.
    private static void SampleShifted(Plane p, ImageRect rect, double dx, double dy, float[] cov)
    {
        double px = -dx;
        double py = -dy;
        int ix = (int)Math.Floor(px);
        int iy = (int)Math.Floor(py);
        float fx = (float)(px - ix);
        float fy = (float)(py - iy);
        ImageRect r = p.Region;
        int w = r.Width;
        float[] d = p.Data;
        for (int y = 0; y < rect.Height; y++)
        {
            int ry = rect.Y + y + iy - r.Y;
            for (int x = 0; x < rect.Width; x++)
            {
                int rx = rect.X + x + ix - r.X;
                float a = d[(ry * w) + rx];
                if (fx == 0f && fy == 0f)
                {
                    cov[(y * rect.Width) + x] = a;
                    continue;
                }

                float b = d[(ry * w) + rx + 1];
                float c = d[((ry + 1) * w) + rx];
                float e = d[((ry + 1) * w) + rx + 1];
                float top = a + (fx * (b - a));
                float bottom = c + (fx * (e - c));
                cov[(y * rect.Width) + x] = top + (fy * (bottom - top));
            }
        }
    }

    // The glow's range: the falloff reaches full strength at this fraction of itself.
    private static void Stretch(float[] cov, int n, float range)
    {
        if (range >= 1f)
        {
            return;
        }

        float k = 1f / Math.Max(range, 1e-3f);
        for (int i = 0; i < n; i++)
        {
            cov[i] = Clamp01(cov[i] * k);
        }
    }

    private static void Contour(float[] cov, int n, EffectContour? contour)
    {
        if (contour == null || contour.IsLinear)
        {
            return;
        }

        for (int i = 0; i < n; i++)
        {
            cov[i] = contour.Evaluate(cov[i]);
        }
    }

    // Moves the edge of the shape outward by r pixels (a negative matte like 1 - alpha is grown the same way).
    private static void Dilate(float[] a, int w, int h, float r)
    {
        using var field = new SignedDistance(a, w, h, outside: true, inside: true);
        for (int i = 0; i < w * h; i++)
        {
            a[i] = Clamp01(0.5f + r - field.Value(i));
        }
    }

    internal static float Clamp01(float v) => v < 0f ? 0f : v > 1f ? 1f : v;
}

/// <summary>
/// A Gaussian-like blur. Small sigmas use the exact kernel; larger ones three box blurs of matched variance, whose cost does not
/// grow with the size.
/// </summary>
internal readonly struct BlurPlan
{
    private readonly float[]? _kernel;
    private readonly int[]? _radii;

    private BlurPlan(float[]? kernel, int[]? radii, int margin)
    {
        _kernel = kernel;
        _radii = radii;
        Margin = margin;
    }

    /// <summary>How far, in pixels, the blur reaches: pixels farther than this from a window edge are exact.</summary>
    public int Margin { get; }

    public static BlurPlan For(float sigma)
    {
        if (!(sigma > 0.05f))
        {
            return new BlurPlan(null, null, 0);
        }

        if (sigma <= 2.5f)
        {
            int r = (int)Math.Ceiling(sigma * 3f);
            var k = new float[(2 * r) + 1];
            double sum = 0;
            for (int i = -r; i <= r; i++)
            {
                double v = Math.Exp(-(i * (double)i) / (2.0 * sigma * sigma));
                k[i + r] = (float)v;
                sum += v;
            }

            for (int i = 0; i < k.Length; i++)
            {
                k[i] = (float)(k[i] / sum);
            }

            return new BlurPlan(k, null, r);
        }

        // Three box blurs of widths wl and wl + 2 whose variance adds up to sigma squared.
        const int passes = 3;
        double ideal = Math.Sqrt((12.0 * sigma * sigma / passes) + 1.0);
        int wl = (int)Math.Floor(ideal);
        if (wl % 2 == 0)
        {
            wl--;
        }

        int wu = wl + 2;
        double m = ((12.0 * sigma * sigma) - (passes * wl * wl) - (4.0 * passes * wl) - (3.0 * passes)) / (-4.0 * wl - 4.0);
        int small = (int)Math.Round(m);
        var radii = new int[passes];
        int margin = 0;
        for (int i = 0; i < passes; i++)
        {
            radii[i] = ((i < small ? wl : wu) - 1) / 2;
            margin += radii[i];
        }

        return new BlurPlan(null, radii, margin);
    }

    public void Apply(float[] data, int w, int h)
    {
        if (_kernel == null && _radii == null)
        {
            return;
        }

        float[] tmp = ArrayPool<float>.Shared.Rent(w * h);
        try
        {
            float[] line = ArrayPool<float>.Shared.Rent(Math.Max(w, h) + 1);
            float[] outLine = ArrayPool<float>.Shared.Rent(Math.Max(w, h) + 1);
            try
            {
                // Horizontal: rows in place.
                for (int y = 0; y < h; y++)
                {
                    Span<float> row = data.AsSpan(y * w, w);
                    Line(row, row, w, line, outLine);
                }

                // Vertical: columns through a scratch line.
                for (int x = 0; x < w; x++)
                {
                    for (int y = 0; y < h; y++)
                    {
                        tmp[y] = data[(y * w) + x];
                    }

                    Line(tmp.AsSpan(0, h), tmp.AsSpan(0, h), h, line, outLine);
                    for (int y = 0; y < h; y++)
                    {
                        data[(y * w) + x] = tmp[y];
                    }
                }
            }
            finally
            {
                ArrayPool<float>.Shared.Return(line);
                ArrayPool<float>.Shared.Return(outLine);
            }
        }
        finally
        {
            ArrayPool<float>.Shared.Return(tmp);
        }
    }

    // Blurs one line; the source is zero outside it.
    private void Line(ReadOnlySpan<float> src, Span<float> dst, int n, float[] a, float[] b)
    {
        src.CopyTo(a);
        if (_kernel != null)
        {
            int r = (_kernel.Length - 1) / 2;
            for (int i = 0; i < n; i++)
            {
                int lo = Math.Max(0, i - r);
                int hi = Math.Min(n - 1, i + r);
                float s = 0f;
                for (int j = lo; j <= hi; j++)
                {
                    s += a[j] * _kernel[j - i + r];
                }

                b[i] = s;
            }
        }
        else
        {
            float[] from = a;
            float[] to = b;
            foreach (int r in _radii!)
            {
                double window = (2 * r) + 1;
                double sum = 0;
                for (int j = 0; j <= Math.Min(n - 1, r); j++)
                {
                    sum += from[j];
                }

                for (int i = 0; i < n; i++)
                {
                    to[i] = (float)(sum / window);
                    int add = i + r + 1;
                    int drop = i - r;
                    if (add < n)
                    {
                        sum += from[add];
                    }

                    if (drop >= 0)
                    {
                        sum -= from[drop];
                    }
                }

                (from, to) = (to, from);
            }

            if (!ReferenceEquals(from, b))
            {
                Array.Copy(from, b, n);
            }
        }

        b.AsSpan(0, n).CopyTo(dst);
    }
}

/// <summary>
/// A signed distance, in pixels, from each pixel's center to the edge of a shape given as alpha: negative inside, positive outside.
/// Pixels with partial alpha place the edge inside themselves (<c>0.5 − alpha</c> from their center); every other pixel is the exact
/// Euclidean distance to the nearest pixel that holds some of the shape (outside) or some of the background (inside), plus that
/// pixel's own offset.
/// </summary>
internal sealed class SignedDistance : IDisposable
{
    private readonly float[] _alpha;
    private readonly int _w;
    private float[]? _outside;
    private float[]? _inside;

    public SignedDistance(float[] alpha, int width, int height, bool outside, bool inside)
    {
        _alpha = alpha;
        _w = width;
        int n = width * height;
        if (outside)
        {
            _outside = ArrayPool<float>.Shared.Rent(n);
            Field(alpha, width, height, sourceIsShape: true, _outside);
        }

        if (inside)
        {
            _inside = ArrayPool<float>.Shared.Rent(n);
            Field(alpha, width, height, sourceIsShape: false, _inside);
        }
    }

    public float Value(int index)
    {
        float a = _alpha[index];
        if (a > 0f && a < 1f)
        {
            return 0.5f - a;
        }

        if (a <= 0f)
        {
            return _outside != null ? _outside[index] : 0.5f;
        }

        return _inside != null ? _inside[index] : -0.5f;
    }

    public void Dispose()
    {
        if (_outside != null)
        {
            ArrayPool<float>.Shared.Return(_outside);
            _outside = null;
        }

        if (_inside != null)
        {
            ArrayPool<float>.Shared.Return(_inside);
            _inside = null;
        }
    }

    private const float Far = 1e12f;

    // Distance to the nearest source pixel (a pixel with alpha > 0 for the outside field, < 1 for the inside field), added to or subtracted
    // from the source pixel's own edge offset. Two passes of the exact squared-distance transform, remembering which pixel won.
    private static void Field(float[] alpha, int w, int h, bool sourceIsShape, float[] result)
    {
        int n = w * h;
        float[] g = ArrayPool<float>.Shared.Rent(n);
        int[] srcRow = ArrayPool<int>.Shared.Rent(n);
        int m = Math.Max(w, h);
        float[] f = ArrayPool<float>.Shared.Rent(m);
        float[] d = ArrayPool<float>.Shared.Rent(m);
        int[] arg = ArrayPool<int>.Shared.Rent(m);
        int[] v = ArrayPool<int>.Shared.Rent(m);
        float[] z = ArrayPool<float>.Shared.Rent(m + 1);
        try
        {
            for (int x = 0; x < w; x++)
            {
                for (int y = 0; y < h; y++)
                {
                    float a = alpha[(y * w) + x];
                    f[y] = (sourceIsShape ? a > 0f : a < 1f) ? 0f : Far;
                }

                Transform(f, d, arg, v, z, h);
                for (int y = 0; y < h; y++)
                {
                    g[(y * w) + x] = d[y];
                    srcRow[(y * w) + x] = arg[y];
                }
            }

            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    f[x] = g[(y * w) + x];
                }

                Transform(f, d, arg, v, z, w);
                for (int x = 0; x < w; x++)
                {
                    float dist2 = d[x];
                    if (dist2 >= Far / 2)
                    {
                        result[(y * w) + x] = sourceIsShape ? 1e6f : -1e6f;
                        continue;
                    }

                    int sx = arg[x];
                    int sy = srcRow[(y * w) + sx];
                    float sa = alpha[(sy * w) + sx];
                    float edge = 0.5f - sa;
                    float dist = MathF.Sqrt(dist2);
                    result[(y * w) + x] = sourceIsShape ? edge + dist : edge - dist;
                }
            }
        }
        finally
        {
            ArrayPool<float>.Shared.Return(g);
            ArrayPool<int>.Shared.Return(srcRow);
            ArrayPool<float>.Shared.Return(f);
            ArrayPool<float>.Shared.Return(d);
            ArrayPool<int>.Shared.Return(arg);
            ArrayPool<int>.Shared.Return(v);
            ArrayPool<float>.Shared.Return(z);
        }
    }

    // One dimension of the squared Euclidean distance transform: d[q] = min over p of (q - p)^2 + f[p], and arg[q] the p that wins.
    private static void Transform(float[] f, float[] d, int[] arg, int[] v, float[] z, int n)
    {
        int k = 0;
        v[0] = 0;
        z[0] = float.NegativeInfinity;
        z[1] = float.PositiveInfinity;
        for (int q = 1; q < n; q++)
        {
            float s;
            while (true)
            {
                int p = v[k];
                s = ((f[q] + ((float)q * q)) - (f[p] + ((float)p * p))) / (2f * (q - p));
                if (s <= z[k])
                {
                    k--;
                    continue;
                }

                break;
            }

            k++;
            v[k] = q;
            z[k] = s;
            z[k + 1] = float.PositiveInfinity;
        }

        k = 0;
        for (int q = 0; q < n; q++)
        {
            while (z[k + 1] < q)
            {
                k++;
            }

            int p = v[k];
            d[q] = ((q - p) * (float)(q - p)) + f[p];
            arg[q] = p;
        }
    }
}
