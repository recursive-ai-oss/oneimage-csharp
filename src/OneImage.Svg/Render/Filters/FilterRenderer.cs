using System.Numerics;
using OneImage.Drawing;
using OneImage.Svg.Dom;
using Path = OneImage.Drawing.Path;

namespace OneImage.Svg.Render;

/// <summary>
/// Evaluates the <c>filter</c> property of a layer: the layer's content is drawn into a pixel grid, run through each filter's primitives
/// as premultiplied float, and the result is drawn into the target. Only the part of each filter's region that the target needs (grown by
/// the primitives' reach) is computed, so a region render equals the same pixels of a whole render.
/// </summary>
internal static class FilterRenderer
{
    private const double PxLimit = 1e9;

    /// <summary>Draws the filtered content of <paramref name="layer"/> into <paramref name="target"/>, whose pixel grid <paramref name="ldev"/> maps scene space to.</summary>
    public static void Draw(Canvas target, RLayer layer, Affine ldev, int depth, SvgLimits limits, CancellationToken cancel)
    {
        RFilter[] filters = layer.Filters!;
        Affine ctm = filters[0].Ctm;
        Affine t = ldev.Multiply(ctm);
        if (!t.IsInvertible)
        {
            return;
        }

        // The filters work on a pixel grid aligned with the element's own axes, scaled to the output. Without rotation or skew that grid is
        // the target's pixel grid itself; otherwise the result is drawn back through the remaining transform.
        double sx = Math.Sqrt((t.A * t.A) + (t.B * t.B));
        double sy = Math.Sqrt((t.C * t.C) + (t.D * t.D));
        if (!(sx > 1e-9) || !(sy > 1e-9) || !double.IsFinite(sx + sy))
        {
            return;
        }

        Affine rest = t.Multiply(Affine.Scale(1 / sx, 1 / sy));
        bool direct = Math.Abs(rest.A - 1) < 1e-7 && Math.Abs(rest.D - 1) < 1e-7 && Math.Abs(rest.B) < 1e-7 && Math.Abs(rest.C) < 1e-7;
        Affine fs = direct ? t : Affine.Scale(sx, sy);

        PxRect needed;
        if (direct)
        {
            needed = new PxRect(0, 0, target.Width, target.Height);
        }
        else
        {
            (double x0, double y0, double x1, double y1) = RPatternPaint.Invert(rest).TransformRect(0, 0, target.Width, target.Height);
            needed = ToPx(x0 - 2, y0 - 2, x1 + 2, y1 + 2);
        }

        // From the last filter back to the first: what each one has to produce, and so what it has to read.
        int n = filters.Length;
        var work = new PxRect[n];
        var regions = new PxRect[n];
        PxRect need = needed;
        for (int i = n - 1; i >= 0; i--)
        {
            RFilter f = filters[i];
            (double rx, double ry, double rw, double rh) = f.Region;
            (double x0, double y0, double x1, double y1) = fs.TransformRect(rx, ry, rx + rw, ry + rh);
            regions[i] = ToPx(x0, y0, x1, y1);
            double mx = 0, my = 0;
            foreach (FePrimitive p in f.Primitives)
            {
                (double px, double py) = p.ReachPx(sx, sy);
                mx += px;
                my += py;
            }

            PxRect wk;
            if (double.IsInfinity(mx) || double.IsInfinity(my))
            {
                wk = regions[i];
            }
            else
            {
                int ex = (int)Math.Min(Math.Ceiling(mx), PxLimit);
                int ey = (int)Math.Min(Math.Ceiling(my), PxLimit);
                wk = new PxRect(need.X - ex, need.Y - ey, (int)Math.Min((long)need.W + (2L * ex), int.MaxValue), (int)Math.Min((long)need.H + (2L * ey), int.MaxValue)).Intersect(regions[i]);
            }

            if (wk.IsEmpty)
            {
                return;
            }

            if (wk.Area > limits.MaxFilterPixels)
            {
                throw new ImageLimitException($"A filter region needs {wk.Area:N0} pixels, more than the limit of {limits.MaxFilterPixels:N0}.");
            }

            work[i] = wk;
            need = wk;
        }

        cancel.ThrowIfCancellationRequested();

        // The element's content on the first filter's pixels.
        FilterBuffer? current;
        {
            PxRect wk0 = work[0];
            Affine toScene = RPatternPaint.Invert(ctm);
            Affine dev0 = Affine.Translate(-wk0.X, -wk0.Y).Multiply(fs).Multiply(toScene);
            using var canvas = new Canvas(wk0.W, wk0.H, PixelFormat.Rgba8);
            (double qx0, double qy0, double qx1, double qy1) = RPatternPaint.Invert(dev0).TransformRect(-1, -1, wk0.W + 1, wk0.H + 1);
            foreach (RNode child in layer.Children)
            {
                SceneRenderer.DrawNode(canvas, child, dev0, qx0, qy0, qx1, qy1, depth + 1, limits, cancel);
            }

            current = ReadBuffer(canvas, wk0);
        }

        try
        {
            for (int i = 0; i < n; i++)
            {
                cancel.ThrowIfCancellationRequested();
                if (i > 0)
                {
                    FilterBuffer prev = current;
                    FilterBuffer framed = prev.Reframe(work[i]);
                    current = framed.InSpace(false);
                    if (!ReferenceEquals(current, framed))
                    {
                        framed.Dispose();
                    }

                    prev.Dispose();
                }

                var ctx = new Context(filters[i], fs, ctm, regions[i], work[i], sx, sy, depth, limits, cancel);
                FilterBuffer result = Evaluate(ctx, current);
                current.Dispose();
                current = result;
            }

            Output(target, current, direct, rest);
        }
        finally
        {
            current.Dispose();
        }
    }

    private static FilterBuffer ReadBuffer(Canvas canvas, PxRect rect)
    {
        Rgba32[] px = System.Buffers.ArrayPool<Rgba32>.Shared.Rent(rect.W * rect.H);
        try
        {
            canvas.CopyTo(px.AsSpan(0, rect.W * rect.H), new ImageRect(0, 0, rect.W, rect.H));
            return FilterBuffer.FromRgba(px, rect, linear: false);
        }
        finally
        {
            System.Buffers.ArrayPool<Rgba32>.Shared.Return(px);
        }
    }

    private static void Output(Canvas target, FilterBuffer result, bool direct, Affine rest)
    {
        PxRect r = result.Rect;
        Rgba32[] px = System.Buffers.ArrayPool<Rgba32>.Shared.Rent(r.W * r.H);
        try
        {
            result.ToRgba(px.AsSpan(0, r.W * r.H));
            if (direct)
            {
                PxRect dst = r.Intersect(new PxRect(0, 0, target.Width, target.Height));
                if (dst.IsEmpty)
                {
                    return;
                }

                Rgba32[] part = System.Buffers.ArrayPool<Rgba32>.Shared.Rent(dst.W * dst.H);
                try
                {
                    for (int y = 0; y < dst.H; y++)
                    {
                        Array.Copy(px, (((dst.Y - r.Y + y) * r.W) + (dst.X - r.X)), part, y * dst.W, dst.W);
                    }

                    target.CopyFrom<Rgba32>(part.AsSpan(0, dst.W * dst.H), new ImageRect(dst.X, dst.Y, dst.W, dst.H));
                }
                finally
                {
                    System.Buffers.ArrayPool<Rgba32>.Shared.Return(part);
                }

                return;
            }

            Image picture = Image.FromPixels<Rgba32>(px.AsSpan(0, r.W * r.H), r.W, r.H);
            Affine m = rest.Multiply(Affine.Translate(r.X, r.Y));
            if (!m.IsFinite)
            {
                return;
            }

            Matrix3x2 mat = m.ToMatrix3x2();
            Path quad = Path.Rectangle(0, 0, r.W, r.H).Transform(mat);
            target.Fill(Brush.FromImage(picture, ImageSampling.Bilinear, transform: mat, tile: false), quad);
        }
        finally
        {
            System.Buffers.ArrayPool<Rgba32>.Shared.Return(px);
        }
    }

    private static PxRect ToPx(double x0, double y0, double x1, double y1)
    {
        int ax = (int)Math.Clamp(Math.Floor(x0), -PxLimit, PxLimit);
        int ay = (int)Math.Clamp(Math.Floor(y0), -PxLimit, PxLimit);
        int bx = (int)Math.Clamp(Math.Ceiling(x1), -PxLimit, PxLimit);
        int by = (int)Math.Clamp(Math.Ceiling(y1), -PxLimit, PxLimit);
        return new PxRect(ax, ay, bx - ax, by - ay);
    }

    private sealed class Context
    {
        public Context(RFilter filter, Affine fs, Affine ctm, PxRect region, PxRect work, double sx, double sy, int depth, SvgLimits limits, CancellationToken cancel)
        {
            Filter = filter;
            Fs = fs;
            Ctm = ctm;
            Region = region;
            Work = work;
            Sx = sx;
            Sy = sy;
            Depth = depth;
            Limits = limits;
            Cancel = cancel;
        }

        public RFilter Filter { get; }

        public Affine Fs { get; }

        public Affine Ctm { get; }

        public PxRect Region { get; }

        public PxRect Work { get; }

        public double Sx { get; }

        public double Sy { get; }

        public int Depth { get; }

        public SvgLimits Limits { get; }

        public CancellationToken Cancel { get; }
    }

    // Runs the primitives of one filter over the source (sRGB, on the work rectangle) and returns the last result; the source stays the caller's.
    private static FilterBuffer Evaluate(Context c, FilterBuffer source)
    {
        FePrimitive[] prims = c.Filter.Primitives;
        var results = new FilterBuffer?[prims.Length];
        var lastUse = new int[prims.Length];
        for (int i = 0; i < prims.Length; i++)
        {
            foreach (int input in prims[i].Inputs())
            {
                if (input >= 0)
                {
                    lastUse[input] = i;
                }
            }
        }

        try
        {
            for (int i = 0; i < prims.Length; i++)
            {
                c.Cancel.ThrowIfCancellationRequested();
                results[i] = Run(c, prims[i], source, results);
                foreach (int input in prims[i].Inputs())
                {
                    if (input >= 0 && lastUse[input] == i && input != prims.Length - 1 && results[input] is { } done)
                    {
                        done.Dispose();
                        results[input] = null;
                    }
                }
            }

            if (results.Length == 0)
            {
                return new FilterBuffer(c.Work, false);
            }

            FilterBuffer last = results[^1]!;
            results[^1] = null;
            return last;
        }
        finally
        {
            foreach (FilterBuffer? r in results)
            {
                r?.Dispose();
            }
        }
    }

    // One input of a primitive, in the primitive's color space. The caller disposes the returned buffer when it is a conversion (not one of the held buffers).
    private static FilterBuffer Input(Context c, int index, bool linear, FilterBuffer source, FilterBuffer?[] results, out bool temporary)
    {
        temporary = false;
        FilterBuffer raw;
        if (index == FeIn.SourceAlpha)
        {
            var alpha = new FilterBuffer(c.Work, linear);
            FilterOps.AlphaOnly(source.Data, alpha.Data, c.Work.W * c.Work.H);
            temporary = true;
            return alpha;
        }

        raw = index == FeIn.SourceGraphic ? source : results[index]!;
        FilterBuffer converted = raw.InSpace(linear);
        temporary = !ReferenceEquals(converted, raw);
        return converted;
    }

    private static PxRect SubRect(Context c, FePrimitive p)
    {
        if (p.EmptySub)
        {
            return new PxRect(0, 0, 0, 0);
        }

        (double x0, double y0, double x1, double y1) = c.Fs.TransformRect(p.SubX, p.SubY, p.SubX + p.SubW, p.SubY + p.SubH);
        return ToPx(x0, y0, x1, y1).Intersect(c.Region);
    }

    private static FilterBuffer Run(Context c, FePrimitive p, FilterBuffer source, FilterBuffer?[] results)
    {
        PxRect rect = c.Work;
        int w = rect.W, h = rect.H, count = w * h;
        PxRect sub = SubRect(c, p);
        FilterBuffer result;
        bool tmp1 = false, tmp2 = false;
        FilterBuffer? in1 = null, in2 = null;
        try
        {
            switch (p)
            {
                case FeBlur blur:
                {
                    in1 = Input(c, p.In1, p.Linear, source, results, out tmp1);
                    result = Blur(in1, rect, p.Linear, blur.SigmaX * c.Sx, blur.SigmaY * c.Sy);
                    break;
                }

                case FeOffset off:
                {
                    in1 = Input(c, p.In1, p.Linear, source, results, out tmp1);
                    result = new FilterBuffer(rect, p.Linear);
                    FilterOps.Offset(in1.Data, result.Data, w, h, RoundPx(off.Dx * c.Sx), RoundPx(off.Dy * c.Sy));
                    break;
                }

                case FeFlood flood:
                {
                    result = new FilterBuffer(rect, p.Linear);
                    FillFlood(result, sub, flood.Color, flood.Opacity, p.Linear);
                    break;
                }

                case FeMerge merge:
                {
                    result = new FilterBuffer(rect, p.Linear);
                    foreach (int layerIndex in merge.Layers)
                    {
                        FilterBuffer layer = Input(c, layerIndex, p.Linear, source, results, out bool t);
                        FilterOps.Over(layer.Data, result.Data, count);
                        if (t)
                        {
                            layer.Dispose();
                        }
                    }

                    break;
                }

                case FeBlend blend:
                {
                    in1 = Input(c, p.In1, p.Linear, source, results, out tmp1);
                    in2 = Input(c, p.In2, p.Linear, source, results, out tmp2);
                    result = new FilterBuffer(rect, p.Linear);
                    FilterOps.Blend(in1.Data, in2.Data, result.Data, count, blend.Mode);
                    break;
                }

                case FeComposite comp:
                {
                    in1 = Input(c, p.In1, p.Linear, source, results, out tmp1);
                    in2 = Input(c, p.In2, p.Linear, source, results, out tmp2);
                    result = new FilterBuffer(rect, p.Linear);
                    FilterOps.Composite(in1.Data, in2.Data, result.Data, count, comp.Op, (float)comp.K1, (float)comp.K2, (float)comp.K3, (float)comp.K4);
                    break;
                }

                case FeColorMatrix cm:
                {
                    in1 = Input(c, p.In1, p.Linear, source, results, out tmp1);
                    result = new FilterBuffer(rect, p.Linear);
                    FilterOps.ColorMatrix(in1.Data, result.Data, count, cm.Matrix);
                    break;
                }

                case FeConvolve conv:
                {
                    in1 = Input(c, p.In1, p.Linear, source, results, out tmp1);
                    result = new FilterBuffer(rect, p.Linear);
                    FilterOps.Convolve(in1.Data, result.Data, w, h, rect.X, rect.Y, c.Region, conv);
                    break;
                }

                case FeDropShadow shadow:
                {
                    in1 = Input(c, p.In1, p.Linear, source, results, out tmp1);
                    result = DropShadow(c, in1, shadow, rect);
                    break;
                }

                case FeImage image:
                {
                    result = Picture(c, image, rect, sub);
                    break;
                }

                default:
                    result = new FilterBuffer(rect, p.Linear);
                    break;
            }
        }
        finally
        {
            if (tmp1)
            {
                in1!.Dispose();
            }

            if (tmp2)
            {
                in2!.Dispose();
            }
        }

        result.ClearOutside(sub);
        return result;
    }

    // Offsets move whole pixels: the scaled distance is cut toward zero (fractions are dropped, not rounded).
    private static long RoundPx(double v) => (long)Math.Truncate(Math.Clamp(v, -PxLimit, PxLimit));

    private static FilterBuffer Blur(FilterBuffer input, PxRect rect, bool linear, double sx, double sy)
    {
        var a = new FilterBuffer(rect, linear);
        Array.Copy(input.Data, a.Data, rect.W * rect.H * 4);
        if (!(sx > 0) && !(sy > 0))
        {
            return a;
        }

        var b = new FilterBuffer(rect, linear);
        float[] res = FilterBlur.Apply(a.Data, b.Data, rect.W, rect.H, sx, sy);
        if (ReferenceEquals(res, b.Data))
        {
            a.Dispose();
            return b;
        }

        b.Dispose();
        return a;
    }

    private static void FillFlood(FilterBuffer dst, PxRect sub, Rgba32 color, float opacity, bool linear)
    {
        PxRect r = sub.Intersect(dst.Rect);
        if (r.IsEmpty)
        {
            return;
        }

        float a = color.A / 255f * opacity;
        float cr = color.R / 255f, cg = color.G / 255f, cb = color.B / 255f;
        if (linear)
        {
            cr = FilterColor.SrgbToLinear(cr);
            cg = FilterColor.SrgbToLinear(cg);
            cb = FilterColor.SrgbToLinear(cb);
        }

        float[] d = dst.Data;
        for (int y = r.Y; y < r.Bottom; y++)
        {
            int o = (((y - dst.Rect.Y) * dst.Rect.W) + (r.X - dst.Rect.X)) * 4;
            for (int x = 0; x < r.W; x++, o += 4)
            {
                d[o] = cr * a;
                d[o + 1] = cg * a;
                d[o + 2] = cb * a;
                d[o + 3] = a;
            }
        }
    }

    // Shadow = the input's alpha, blurred, moved, tinted with the flood color; the input goes on top of it.
    private static FilterBuffer DropShadow(Context c, FilterBuffer input, FeDropShadow s, PxRect rect)
    {
        int count = rect.W * rect.H;
        var alpha = new FilterBuffer(rect, input.Linear);
        FilterOps.AlphaOnly(input.Data, alpha.Data, count);
        FilterBuffer blurred = Blur(alpha, rect, input.Linear, s.SigmaX * c.Sx, s.SigmaY * c.Sy);
        alpha.Dispose();
        var moved = new FilterBuffer(rect, input.Linear);
        FilterOps.Offset(blurred.Data, moved.Data, rect.W, rect.H, RoundPx(s.Dx * c.Sx), RoundPx(s.Dy * c.Sy));
        blurred.Dispose();

        float opacity = s.Color.A / 255f * s.Opacity;
        float cr = s.Color.R / 255f, cg = s.Color.G / 255f, cb = s.Color.B / 255f;
        if (input.Linear)
        {
            cr = FilterColor.SrgbToLinear(cr);
            cg = FilterColor.SrgbToLinear(cg);
            cb = FilterColor.SrgbToLinear(cb);
        }

        float[] m = moved.Data;
        for (int i = 0; i < count; i++)
        {
            int p = i * 4;
            float a = m[p + 3] * opacity;
            m[p] = cr * a;
            m[p + 1] = cg * a;
            m[p + 2] = cb * a;
            m[p + 3] = a;
        }

        FilterOps.Over(input.Data, m, count);
        return moved;
    }

    // An image or an element drawn into the subregion.
    private static FilterBuffer Picture(Context c, FeImage p, PxRect rect, PxRect sub)
    {
        if (p.Picture is { } picture)
        {
            return PlacePicture(c, p, picture, rect, sub);
        }

        using var canvas = new Canvas(rect.W, rect.H, PixelFormat.Rgba8);
        Affine toPx = Affine.Translate(-rect.X, -rect.Y).Multiply(c.Fs);
        if (p.Nodes is { } nodes)
        {
            Affine dev = toPx.Multiply(RPatternPaint.Invert(c.Ctm));
            (double qx0, double qy0, double qx1, double qy1) = RPatternPaint.Invert(dev).TransformRect(-1, -1, rect.W + 1, rect.H + 1);
            foreach (RNode node in nodes)
            {
                SceneRenderer.DrawNode(canvas, node, dev, qx0, qy0, qx1, qy1, c.Depth + 1, c.Limits, c.Cancel);
            }
        }

        FilterBuffer read = ReadBuffer(canvas, rect);
        FilterBuffer converted = read.InSpace(p.Linear);
        if (!ReferenceEquals(read, converted))
        {
            read.Dispose();
        }

        return converted;
    }

    // The picture scaled to its placed size in whole pixels (so its outer edge is sharp) and copied into the part of the subregion the work rectangle covers.
    private static FilterBuffer PlacePicture(Context c, FeImage p, Image picture, PxRect rect, PxRect sub)
    {
        Affine fit = p.Aspect.Fit(0, 0, picture.Width, picture.Height, p.SubW, p.SubH);
        Affine m = c.Fs.Multiply(Affine.Translate(p.SubX, p.SubY)).Multiply(fit);
        (double x0, double y0, double x1, double y1) = m.TransformRect(0, 0, picture.Width, picture.Height);
        if (!double.IsFinite(x0 + y0 + x1 + y1) || Math.Abs(x0) > PxLimit || Math.Abs(y0) > PxLimit || Math.Abs(x1) > PxLimit || Math.Abs(y1) > PxLimit)
        {
            return new FilterBuffer(rect, p.Linear);
        }

        var dest = new PxRect((int)Math.Round(x0), (int)Math.Round(y0), Math.Max((int)Math.Round(x1) - (int)Math.Round(x0), 1), Math.Max((int)Math.Round(y1) - (int)Math.Round(y0), 1));
        PxRect visible = dest.Intersect(sub).Intersect(rect);
        if (visible.IsEmpty)
        {
            return new FilterBuffer(rect, p.Linear);
        }

        if (dest.Area > c.Limits.MaxFilterPixels)
        {
            throw new ImageLimitException("A filter image is placed larger than the filter pixel limit.");
        }

        Rgba32[] scaled = picture.Resize(dest.W, dest.H, Fit.Stretch, filter: ResamplingFilter.Mitchell, linear: false).ToArray<Rgba32>(cancellationToken: c.Cancel);
        Rgba32[] frame = System.Buffers.ArrayPool<Rgba32>.Shared.Rent(rect.W * rect.H);
        try
        {
            Array.Clear(frame, 0, rect.W * rect.H);
            for (int y = visible.Y; y < visible.Bottom; y++)
            {
                Array.Copy(scaled, ((y - dest.Y) * dest.W) + (visible.X - dest.X), frame, ((y - rect.Y) * rect.W) + (visible.X - rect.X), visible.W);
            }

            return FilterBuffer.FromRgba(frame, rect, p.Linear);
        }
        finally
        {
            System.Buffers.ArrayPool<Rgba32>.Shared.Return(frame);
        }
    }
}
