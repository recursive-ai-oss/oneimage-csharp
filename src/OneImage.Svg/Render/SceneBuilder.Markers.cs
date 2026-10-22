using OneImage.Drawing;
using OneImage.Svg.Dom;
using Path = OneImage.Drawing.Path;

namespace OneImage.Svg.Render;

// Markers: the shapes a marker element draws at the vertices of a path, line, polyline or polygon (SVG 1.1 section 11.6, SVG 2 section 13.4).
internal sealed partial class SceneBuilder
{
    private enum OrientKind
    {
        Angle,
        Auto,
        AutoStartReverse,
    }

    // What a marker element says, independent of the shape that uses it.
    private sealed class MarkerInfo
    {
        public required SvgElement El { get; init; }

        public required SvgStyle Style { get; init; }

        public OrientKind Orient { get; init; }

        public double Angle { get; init; }

        public bool UserSpace { get; init; }

        public bool HasViewBox { get; init; }

        public double Vx { get; init; }

        public double Vy { get; init; }

        public double Vw { get; init; }

        public double Vh { get; init; }

        public AspectRatio Par { get; init; }

        public Length RefX { get; init; }

        public Length RefY { get; init; }

        public Length Width { get; init; }

        public Length Height { get; init; }

        public bool ClipOverflow { get; init; }
    }

    // A marker resolved for one shape: sizes in the shape's units.
    private sealed class ResolvedMarker
    {
        public required MarkerInfo Info { get; init; }

        public double Width { get; init; }

        public double Height { get; init; }

        /// <summary>Maps marker content coordinates into the marker viewport.</summary>
        public Affine ViewBoxTransform { get; init; }

        /// <summary>The reference point in the viewport.</summary>
        public double RefX { get; init; }

        public double RefY { get; init; }

        public Viewport Vp { get; init; }
    }

    // The markers of one shape and how far they are instantiated.
    private sealed class MarkerPlan
    {
        public required List<MarkerVertex> Vertices { get; init; }

        /// <summary>The marker for the start, mid and end vertices (null where there is none).</summary>
        public required ResolvedMarker?[] Markers { get; init; }

        public required Affine Ctm { get; init; }

        public required double StrokeWidth { get; init; }

        public required Path? Clip { get; set; }

        public required ResolvedPaint CtxFill { get; init; }

        public required ResolvedPaint CtxStroke { get; init; }

        public required List<RNode> Out { get; init; }

        public int Next { get; set; }
    }

    private readonly Dictionary<SvgElement, MarkerInfo?> _markerInfo = [];

    private MarkerPlan? PlanMarkers(SvgElement e, in SvgStyle st, Frame f, Affine ctm, ResolvedPaint fillPaint, ResolvedPaint strokePaint)
    {
        if (st.Markers is not { } refs)
        {
            return null;
        }

        double strokeWidth = Math.Min(Resolve(st.StrokeWidth, Axis.Other, st, f.Vp), Pen.MaxWidth);
        ResolvedMarker?[] markers = [ResolveMarker(refs.Start, st, f), ResolveMarker(refs.Mid, st, f), ResolveMarker(refs.End, st, f)];
        if (markers[0] is null && markers[1] is null && markers[2] is null)
        {
            return null;
        }

        List<MarkerVertex> vertices = MarkerVertices(e, st, f.Vp);
        if (vertices.Count == 0)
        {
            return null;
        }

        return new MarkerPlan
        {
            Vertices = vertices,
            Markers = markers,
            Ctm = ctm,
            StrokeWidth = strokeWidth,
            Clip = f.Clip,
            CtxFill = fillPaint,
            CtxStroke = strokePaint,
            Out = [],
        };
    }

    private ResolvedMarker? ResolveMarker(string? id, in SvgStyle st, Frame f)
    {
        if (id is null || !_doc.Ids.TryGetValue(id, out SvgElement? el) || el.Name != "marker")
        {
            return null;
        }

        if (!_markerInfo.TryGetValue(el, out MarkerInfo? info))
        {
            info = ReadMarker(el);
            _markerInfo[el] = info;
        }

        if (info is null)
        {
            return null;
        }

        double w = Resolve(info.Width, Axis.X, st, f.Vp);
        double h = Resolve(info.Height, Axis.Y, st, f.Vp);
        if (!(w > 0) || !(h > 0) || !double.IsFinite(w + h))
        {
            return null;
        }

        Affine vb = info.HasViewBox ? info.Par.Fit(info.Vx, info.Vy, info.Vw, info.Vh, w, h) : Affine.Identity;
        (double rx, double ry) = vb.Apply(Resolve(info.RefX, Axis.X, st, f.Vp), Resolve(info.RefY, Axis.Y, st, f.Vp));
        return new ResolvedMarker
        {
            Info = info,
            Width = w,
            Height = h,
            ViewBoxTransform = vb,
            RefX = rx,
            RefY = ry,
            Vp = info.HasViewBox ? new Viewport(info.Vw, info.Vh) : new Viewport(w, h),
        };
    }

    private MarkerInfo? ReadMarker(SvgElement el)
    {
        OrientKind orient = OrientKind.Angle;
        double angle = 0;
        string? o = el.Attr("orient")?.Trim();
        if (o == "auto")
        {
            orient = OrientKind.Auto;
        }
        else if (o == "auto-start-reverse")
        {
            orient = OrientKind.AutoStartReverse;
        }
        else if (o is not null && TryAngle(o, out double a))
        {
            angle = a;
        }

        bool hasVb = SvgValues.TryViewBox(el.Attr("viewBox"), out double vx, out double vy, out double vw, out double vh) && vw > 0 && vh > 0;

        // Markers clip to their viewport unless overflow is visible or auto; inherit takes the parent's value, which is visible when nothing sets it.
        string? overflow = el.Prop("overflow");
        for (SvgElement? p = el.Parent; overflow == "inherit"; p = p.Parent)
        {
            overflow = p is null ? "visible" : p.Prop("overflow") ?? "visible";
            if (p is null)
            {
                break;
            }
        }

        SvgStyle style = StyleOf(el);
        return new MarkerInfo
        {
            El = el,
            Style = style,
            Orient = orient,
            Angle = angle,
            UserSpace = el.Attr("markerUnits")?.Trim() == "userSpaceOnUse",
            HasViewBox = hasVb,
            Vx = vx,
            Vy = vy,
            Vw = vw,
            Vh = vh,
            Par = AspectRatio.Parse(el.Attr("preserveAspectRatio")),
            RefX = ParseLength(el.Attr("refX")),
            RefY = ParseLength(el.Attr("refY")),
            Width = el.Attr("markerWidth") is { } mw && Length.TryParse(mw, out Length lw) ? lw : new Length(3, LengthUnit.Px),
            Height = el.Attr("markerHeight") is { } mh && Length.TryParse(mh, out Length lh) ? lh : new Length(3, LengthUnit.Px),
            ClipOverflow = overflow is not ("visible" or "auto"),
        };
    }

    // An angle with an optional unit (deg, grad, rad, turn), in degrees.
    private static bool TryAngle(string text, out double degrees)
    {
        degrees = 0;
        ReadOnlySpan<char> t = text.AsSpan().Trim();
        double factor = 1;
        if (t.EndsWith("deg", StringComparison.Ordinal))
        {
            t = t[..^3];
        }
        else if (t.EndsWith("grad", StringComparison.Ordinal))
        {
            t = t[..^4];
            factor = 0.9;
        }
        else if (t.EndsWith("rad", StringComparison.Ordinal))
        {
            t = t[..^3];
            factor = 180 / Math.PI;
        }
        else if (t.EndsWith("turn", StringComparison.Ordinal))
        {
            t = t[..^4];
            factor = 360;
        }

        if (!double.TryParse(t, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double v) || !double.IsFinite(v))
        {
            return false;
        }

        degrees = v * factor;
        return true;
    }

    // The vertices of the element's geometry, in its own coordinates.
    private List<MarkerVertex> MarkerVertices(SvgElement e, in SvgStyle st, Viewport vp)
    {
        if (_markerVertices.TryGetValue(e, out List<MarkerVertex>? cached))
        {
            return cached;
        }

        var rec = new MarkerPath();
        switch (e.Name)
        {
            case "path":
                if (e.Attr("d") is { } d)
                {
                    PathData.Parse(d, out _, out _, rec);
                }

                break;
            case "line":
                rec.MoveTo(Coord.F(Num(e, "x1", Axis.X, st, vp)), Coord.F(Num(e, "y1", Axis.Y, st, vp)));
                rec.LineTo(Coord.F(Num(e, "x2", Axis.X, st, vp)), Coord.F(Num(e, "y2", Axis.Y, st, vp)));
                break;
            case "rect":
            {
                double x = Num(e, "x", Axis.X, st, vp);
                double y = Num(e, "y", Axis.Y, st, vp);
                double w = Num(e, "width", Axis.X, st, vp);
                double h = Num(e, "height", Axis.Y, st, vp);
                double? rxo = RadiusOrNull(e, "rx", Axis.X, st, vp);
                double? ryo = RadiusOrNull(e, "ry", Axis.Y, st, vp);
                double rx = Math.Min(rxo ?? ryo ?? 0, w / 2);
                double ry = Math.Min(ryo ?? rxo ?? 0, h / 2);
                if (!(w > 0) || !(h > 0))
                {
                    break;
                }

                // The same outline Rect() draws: a plain rectangle, or straight sides joined by quarter arcs.
                if (rx > 0 && ry > 0)
                {
                    rec.MoveTo(x + rx, y);
                    rec.LineTo(x + w - rx, y);
                    rec.ArcTo(rx, ry, 0, false, true, x + w, y + ry);
                    rec.LineTo(x + w, y + h - ry);
                    rec.ArcTo(rx, ry, 0, false, true, x + w - rx, y + h);
                    rec.LineTo(x + rx, y + h);
                    rec.ArcTo(rx, ry, 0, false, true, x, y + h - ry);
                    rec.LineTo(x, y + ry);
                    rec.ArcTo(rx, ry, 0, false, true, x + rx, y);
                }
                else
                {
                    rec.MoveTo(x, y);
                    rec.LineTo(x + w, y);
                    rec.LineTo(x + w, y + h);
                    rec.LineTo(x, y + h);
                }

                rec.Close();
                break;
            }

            case "circle" or "ellipse":
            {
                double cx = Num(e, "cx", Axis.X, st, vp);
                double cy = Num(e, "cy", Axis.Y, st, vp);
                (double rx, double ry) = e.Name == "circle" ? (Num(e, "r", Axis.Other, st, vp), Num(e, "r", Axis.Other, st, vp)) : Radii(e, st, vp, "rx", "ry", false);
                if (!(rx > 0) || !(ry > 0))
                {
                    break;
                }

                // Four quarter arcs from the rightmost point, clockwise on screen, then closed.
                rec.MoveTo(cx + rx, cy);
                rec.ArcTo(rx, ry, 0, false, true, cx, cy + ry);
                rec.ArcTo(rx, ry, 0, false, true, cx - rx, cy);
                rec.ArcTo(rx, ry, 0, false, true, cx, cy - ry);
                rec.ArcTo(rx, ry, 0, false, true, cx + rx, cy);
                rec.Close();
                break;
            }

            default:
                if (e.Attr("points") is { } pts)
                {
                    List<double> n = SvgValues.NumberList(pts, out _);
                    int count = n.Count / 2;
                    if (count >= 2)
                    {
                        rec.MoveTo(Coord.F(n[0]), Coord.F(n[1]));
                        for (int i = 1; i < count; i++)
                        {
                            rec.LineTo(Coord.F(n[2 * i]), Coord.F(n[(2 * i) + 1]));
                        }

                        if (e.Name == "polygon")
                        {
                            rec.Close();
                        }
                    }
                }

                break;
        }

        List<MarkerVertex> vertices = rec.Vertices();
        _markerVertices[e] = vertices;
        return vertices;
    }

    // Puts a shape with markers into the tree: its fill, stroke and markers in paint order, in a group when the shape has opacity or a blend mode.
    private void EmitMarkers(Stack<Frame> stack, Frame f, in SvgStyle st, Affine ctm, MarkerPlan plan, RPath? node, bool layered, Deco? deco)
    {
        RLayer? layer = null;
        List<RNode> target = f.Out;
        int depth = f.LayerDepth;
        if (layered)
        {
            if (depth + 1 > _limits.MaxLayerDepth)
            {
                throw new ImageLimitException($"Groups with opacity are nested more than {_limits.MaxLayerDepth} deep.");
            }

            layer = new RLayer { Children = [], Opacity = (float)st.Opacity, Blend = st.Blend };
            target = layer.Children;
            depth++;
        }

        var fillPart = new List<RNode>();
        var strokePart = new List<RNode>();
        if (node is not null)
        {
            if (node.HasFill && node.HasStroke)
            {
                fillPart.Add(node.Part(fill: true));
                strokePart.Add(node.Part(fill: false));
            }
            else if (node.HasFill)
            {
                fillPart.Add(node);
            }
            else if (node.HasStroke)
            {
                strokePart.Add(node);
            }
        }

        var parts = new List<RNode>[3];
        for (int i = 0; i < 3; i++)
        {
            int what = (st.PaintOrder >> (2 * i)) & 3;
            parts[i] = what == 0 ? fillPart : what == 1 ? strokePart : plan.Out;
        }

        stack.Push(new Frame
        {
            Kids = Array.Empty<SvgElement>(),
            Style = st,
            Ctm = ctm,
            Vp = f.Vp,
            Clip = plan.Clip,
            Out = target,
            Layer = layer,
            LayerParent = f.Out,
            LayerDepth = depth,
            UseDepth = f.UseDepth,
            CtxFill = f.CtxFill,
            CtxStroke = f.CtxStroke,
            OutStart = layer is null ? f.Out.Count : 0,
            Deco = deco,
            Marker = new MarkerFrame { Parts = parts, Pending = plan },
        });
    }

    // Instantiates the next marker of the plan that is not already being drawn (a marker inside itself draws nothing); false when none is left.
    private bool PushNextMarker(Stack<Frame> stack, Frame carrier)
    {
        MarkerPlan p = carrier.Marker!.Pending!;
        int last = p.Vertices.Count - 1;
        while (p.Next <= last)
        {
            int i = p.Next++;
            ResolvedMarker? m = p.Markers[i == 0 ? 0 : i == last ? 2 : 1];
            if (m is null || _activeMarkers.Contains(m.Info.El))
            {
                continue;
            }

            MarkerInfo info = m.Info;
            MarkerVertex v = p.Vertices[i];
            double scale = info.UserSpace ? 1 : p.StrokeWidth;
            if (!(scale > 0))
            {
                continue;
            }

            Count();
            double angle = info.Orient switch
            {
                OrientKind.Auto => v.Angle,
                OrientKind.AutoStartReverse => i == 0 ? v.Angle + 180 : v.Angle,
                _ => info.Angle,
            };
            double rad = angle * Math.PI / 180;
            double cos = Math.Cos(rad);
            double sin = Math.Sin(rad);

            // Vertex, direction, stroke-width scale, then the reference point of the marker's viewport to the origin.
            Affine place = p.Ctm.Multiply(Affine.Translate(v.X, v.Y)).Multiply(new Affine(cos, sin, -sin, cos, 0, 0)).Multiply(Affine.Scale(scale, scale)).Multiply(Affine.Translate(-m.RefX, -m.RefY));
            Affine full = place.Multiply(m.ViewBoxTransform);
            if (!full.IsFinite || Math.Abs(full.A) > Coord.Limit || Math.Abs(full.B) > Coord.Limit || Math.Abs(full.C) > Coord.Limit
                || Math.Abs(full.D) > Coord.Limit || Math.Abs(full.E) > Coord.Limit || Math.Abs(full.F) > Coord.Limit)
            {
                continue;
            }

            Path? clip = p.Clip;
            if (info.ClipOverflow)
            {
                Path rect = Path.Rectangle(0, 0, Coord.F(m.Width), Coord.F(m.Height)).Transform(place.ToMatrix3x2());
                clip = clip is null ? rect : clip.Intersect(rect);
            }

            _activeMarkers.Add(info.El);
            HashSet<SvgElement> chain = _active;
            _active = [];
            stack.Push(new Frame
            {
                Kids = info.El.Children,
                Style = info.Style,
                Ctm = full,
                Vp = m.Vp,
                Clip = clip,
                Out = p.Out,
                LayerParent = p.Out,
                LayerDepth = carrier.LayerDepth,
                UseDepth = carrier.UseDepth,
                CtxFill = p.CtxFill,
                CtxStroke = p.CtxStroke,
                OutStart = p.Out.Count,
                Marker = new MarkerFrame { Target = info.El, SavedUseChain = chain },
            });
            return true;
        }

        return false;
    }
}
