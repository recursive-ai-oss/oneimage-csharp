using System.Numerics;
using OneImage.Drawing;
using OneImage.Text;
using CorePath = OneImage.Drawing.Path;
using TextFont = OneImage.Text.Font;

namespace OneImage.Metafile;

/// <summary>
/// Draws a command stream on a <see cref="Canvas"/> with the drawing engine. One instance renders one rectangle of the output; the output is the
/// picture rectangle of the file scaled to the target size.
/// </summary>
internal sealed class MetafileRenderer : DrawingCommandVisitor
{
    private readonly Canvas _canvas;
    private readonly int _w;
    private readonly int _h;
    private readonly Matrix3x2 _out;
    private readonly bool _aa;
    private readonly FontResolver _fonts;
    private readonly CancellationToken _ct;
    private readonly Stack<(Matrix3x2 Ctm, CorePath? Clip, IntersectionRule Rule)> _stack = new();
    private readonly Dictionary<DeviceIndependentBitmap, byte[]> _plain = [];
    private readonly Dictionary<DeviceIndependentBitmap, byte[]> _alpha = [];
    private Matrix3x2 _ctm = Matrix3x2.Identity;
    private CorePath? _clip;
    private IntersectionRule _clipRule = IntersectionRule.NonZero;

    public MetafileRenderer(Canvas canvas, Matrix3x2 output, bool antialias, FontResolver fonts, CancellationToken ct)
    {
        _canvas = canvas;
        _w = canvas.Width;
        _h = canvas.Height;
        _out = output;
        _aa = antialias;
        _fonts = fonts;
        _ct = ct;
    }

    private Matrix3x2 Xf => _ctm * _out;

    private static IntersectionRule Rule(FillRule r) => r == FillRule.EvenOdd ? IntersectionRule.EvenOdd : IntersectionRule.NonZero;

    // ---- state ---------------------------------------------------------------------------------------------------------

    public override void VisitSaveState(SaveStateCommand command) => _stack.Push((_ctm, _clip, _clipRule));

    public override void VisitRestoreState(RestoreStateCommand command)
    {
        if (_stack.Count > 0)
        {
            (_ctm, _clip, _clipRule) = _stack.Pop();
        }
    }

    public override void VisitSetTransform(SetTransformCommand command) => _ctm = command.Transform;

    private static CorePath Universe => CorePath.Rectangle(-4_000_000, -4_000_000, 8_000_000, 8_000_000);

    public override void VisitClip(ClipCommand command)
    {
        _ct.ThrowIfCancellationRequested();
        if (command.Mode == ClipMode.Reset)
        {
            _clip = null;
            _clipRule = IntersectionRule.NonZero;
            return;
        }

        CorePath p = (command.Path ?? MetafilePath.Empty).ToDrawingPath().Transform(_out);
        IntersectionRule rule = Rule(command.Rule);
        if (_clip is null)
        {
            switch (command.Mode)
            {
                case ClipMode.Replace:
                case ClipMode.Intersect:
                    _clip = p;
                    _clipRule = rule;
                    break;
                case ClipMode.Exclude:
                case ClipMode.Xor:
                    _clip = Universe.Subtract(p, IntersectionRule.NonZero, rule);
                    _clipRule = IntersectionRule.NonZero;
                    break;
                default:
                    break; // union with "everything" stays everything
            }

            return;
        }

        _clip = command.Mode switch
        {
            ClipMode.Replace => p,
            ClipMode.Intersect => _clip.Intersect(p, _clipRule, rule),
            ClipMode.Exclude => _clip.Subtract(p, _clipRule, rule),
            ClipMode.Union => _clip.Union(p, _clipRule, rule),
            _ => _clip.Xor(p, _clipRule, rule),
        };
        _clipRule = command.Mode == ClipMode.Replace ? rule : IntersectionRule.NonZero;
    }

    public override void VisitOffsetClip(OffsetClipCommand command)
    {
        if (_clip is null)
        {
            return;
        }

        Vector2 d = Vector2.TransformNormal(command.Delta, _out);
        _clip = _clip.Translate(d.X, d.Y);
    }

    // ---- brushes -------------------------------------------------------------------------------------------------------

    private static readonly Dictionary<HatchKind, bool[,]> HatchCells = [];

    private static bool[,] Cells(HatchKind kind)
    {
        lock (HatchCells)
        {
            if (HatchCells.TryGetValue(kind, out bool[,]? c))
            {
                return c;
            }

            c = new bool[8, 8];
            for (int y = 0; y < 8; y++)
            {
                for (int x = 0; x < 8; x++)
                {
                    c[y, x] = kind switch
                    {
                        HatchKind.Horizontal => y == 3,
                        HatchKind.Vertical => x == 4,
                        HatchKind.ForwardDiagonal => x == y,
                        HatchKind.BackwardDiagonal => x + y == 7,
                        HatchKind.Cross => y == 3 || x == 4,
                        _ => x == y || x + y == 7,
                    };
                }
            }

            return HatchCells[kind] = c;
        }
    }

    private byte[] Rgba(DeviceIndependentBitmap dib, bool useAlpha)
    {
        Dictionary<DeviceIndependentBitmap, byte[]> cache = useAlpha ? _alpha : _plain;
        if (!cache.TryGetValue(dib, out byte[]? px))
        {
            px = dib.ToRgba(useAlpha);
            cache[dib] = px;
        }

        return px;
    }

    private static Image ImageOf(byte[] rgba, int w, int h)
    {
        var px = new Rgba32[w * h];
        for (int i = 0; i < px.Length; i++)
        {
            px[i] = new Rgba32(rgba[i * 4], rgba[(i * 4) + 1], rgba[(i * 4) + 2], rgba[(i * 4) + 3]);
        }

        return Image.FromPixels<Rgba32>(px, w, h);
    }

    private Brush? ToBrush(BrushSpec spec)
    {
        switch (spec)
        {
            case SolidBrushSpec s:
                return Brush.Solid(s.Color);
            case HatchBrushSpec h:
                {
                    // Windows applies the brush origin to the destination unscaled, whatever the stretch of the picture (measured from GDI output).
                    Vector2 o = h.Origin;
                    return Brush.Pattern(h.Foreground, h.Background ?? default, Cells(h.Kind), Matrix3x2.CreateTranslation((float)Math.Round(o.X), (float)Math.Round(o.Y)));
                }

            case PatternBrushSpec p:
                {
                    byte[] rgba = Rgba(p.Bitmap, false);
                    if (p.UseTextColors && p.Bitmap.BitCount == 1)
                    {
                        rgba = (byte[])rgba.Clone();
                        for (int i = 0; i < rgba.Length; i += 4)
                        {
                            // Index 0 of the table is the clear bit.
                            bool set = (p.Bitmap.Palette.Count > 1 ? Luma(rgba, i) > 128 : rgba[i] > 128) == IsSetBitBright(p.Bitmap);
                            Rgba32 c = set ? p.Foreground : p.Background;
                            (rgba[i], rgba[i + 1], rgba[i + 2], rgba[i + 3]) = (c.R, c.G, c.B, c.A);
                        }
                    }

                    Vector2 o = p.Origin;
                    return Brush.FromImage(ImageOf(rgba, p.Bitmap.Width, p.Bitmap.Height), null, 0, 0, Matrix3x2.CreateTranslation((float)Math.Round(o.X), (float)Math.Round(o.Y)), true);
                }

            default:
                return null;
        }
    }

    private static int Luma(byte[] p, int i) => ((p[i] * 77) + (p[i + 1] * 150) + (p[i + 2] * 29)) >> 8;

    // For a device bitmap pattern, a set bit paints the text color; the color table of a converted bitmap is black then white, so "bright" means set... GDI paints 1 bits with the text color for a monochrome bitmap brush.
    private static bool IsSetBitBright(DeviceIndependentBitmap dib) => dib.Palette.Count < 2 || (dib.Palette[1] & 0xFFFFFF) != 0 || (dib.Palette[0] & 0xFFFFFF) == 0;

    // ---- fills and strokes ---------------------------------------------------------------------------------------------

    private bool Outside(MetafilePath p, double pad)
    {
        RectD b = p.Bounds;
        Matrix3x2 m = Xf;
        Vector2 a = Vector2.Transform(new Vector2((float)b.Left, (float)b.Top), m), c = Vector2.Transform(new Vector2((float)b.Right, (float)b.Bottom), m);
        Vector2 d = Vector2.Transform(new Vector2((float)b.Left, (float)b.Bottom), m), e = Vector2.Transform(new Vector2((float)b.Right, (float)b.Top), m);
        float x0 = Math.Min(Math.Min(a.X, c.X), Math.Min(d.X, e.X)), x1 = Math.Max(Math.Max(a.X, c.X), Math.Max(d.X, e.X));
        float y0 = Math.Min(Math.Min(a.Y, c.Y), Math.Min(d.Y, e.Y)), y1 = Math.Max(Math.Max(a.Y, c.Y), Math.Max(d.Y, e.Y));
        return x1 < -pad || y1 < -pad || x0 > _w + pad || y0 > _h + pad;
    }

    public override void VisitFillPath(FillPathCommand command)
    {
        _ct.ThrowIfCancellationRequested();
        if (command.Path.IsEmpty || command.Mix == MixMode.Nop || Outside(command.Path, 2))
        {
            return;
        }

        CorePath path = command.Path.ToDrawingPath();
        IntersectionRule rule = Rule(command.Rule);
        MixMode mix = command.Mix;
        BrushSpec spec = command.Brush;
        if (mix == MixMode.Black)
        {
            spec = new SolidBrushSpec(new Rgba32(0, 0, 0, 255));
            mix = MixMode.CopyPen;
        }
        else if (mix == MixMode.White)
        {
            spec = new SolidBrushSpec(new Rgba32(255, 255, 255, 255));
            mix = MixMode.CopyPen;
        }
        else if (mix == MixMode.NotCopyPen && spec is SolidBrushSpec ns)
        {
            spec = new SolidBrushSpec(new Rgba32((byte)~ns.Color.R, (byte)~ns.Color.G, (byte)~ns.Color.B, ns.Color.A));
            mix = MixMode.CopyPen;
        }

        if (mix == MixMode.CopyPen || spec is not SolidBrushSpec)
        {
            Brush? brush = ToBrush(spec);
            if (brush is not null)
            {
                _canvas.Fill(brush, path, _aa, rule, 1f, BlendMode.Normal, AlphaComposition.SrcOver, false, Xf, _clip, _clipRule);
            }

            return;
        }

        var color = ((SolidBrushSpec)spec).Color;
        RopCoverage(m => m.Fill(new Rgba32(255, 255, 255, 255), path, false, rule, 1f, BlendMode.Normal, AlphaComposition.SrcOver, false, Xf, _clip, _clipRule), mix, color);
    }

    private Pen MakePen(PenSpec pen, Brush brush, out Matrix3x2? transform, ref CorePath path)
    {
        Matrix3x2 xf = Xf;
        float[]? dashes = pen.Dashes is { Count: > 0 } d ? [.. d.Select(v => (float)Math.Max(v, 1e-3))] : null;
        StrokeCap cap = pen.Cap switch { LineCap.Square => StrokeCap.Square, LineCap.Flat => StrokeCap.Butt, _ => StrokeCap.Round };
        StrokeJoin join = pen.Join switch { LineJoin.Bevel => StrokeJoin.Bevel, LineJoin.Miter => StrokeJoin.Miter, _ => StrokeJoin.Round };
        float miter = (float)Math.Max(1, pen.MiterLimit);
        double scale = Math.Sqrt(Math.Abs((xf.M11 * xf.M22) - (xf.M12 * xf.M21)));
        if (pen.IsCosmetic || scale <= 0)
        {
            // One device pixel wide whatever the scale: stroke the already transformed outline.
            path = path.Transform(xf);
            transform = null;
            return new Pen(brush, 1f, join, cap, miter, dashes ?? default);
        }

        double width = Math.Max(pen.Width, 1 / scale);
        transform = xf;
        return new Pen(brush, (float)width, join, cap, miter, dashes ?? default);
    }

    public override void VisitStrokePath(StrokePathCommand command)
    {
        _ct.ThrowIfCancellationRequested();
        if (command.Path.IsEmpty || command.Mix == MixMode.Nop || Outside(command.Path, command.Pen.IsCosmetic ? 2 : Math.Max(2, command.Pen.Width * 2)))
        {
            return;
        }

        PenSpec pen = command.Pen;
        MixMode mix = command.Mix;
        Rgba32 color = pen.Color;
        if (mix == MixMode.Black)
        {
            color = new Rgba32(0, 0, 0, 255);
            mix = MixMode.CopyPen;
        }
        else if (mix == MixMode.White)
        {
            color = new Rgba32(255, 255, 255, 255);
            mix = MixMode.CopyPen;
        }
        else if (mix == MixMode.NotCopyPen && pen.Brush is null)
        {
            color = new Rgba32((byte)~color.R, (byte)~color.G, (byte)~color.B, color.A);
            mix = MixMode.CopyPen;
        }

        if (mix == MixMode.CopyPen || pen.Brush is not null)
        {
            if (pen.GapColor is { } gap && pen.Dashes is not null)
            {
                DrawStroke(_canvas, pen with { Dashes = null }, command.Path, gap, _aa);
            }

            DrawStroke(_canvas, pen, command.Path, color, _aa);
            return;
        }

        RopCoverage(m => DrawStroke(m, pen, command.Path, new Rgba32(255, 255, 255, 255), false), mix, color);
    }

    private void DrawStroke(Canvas target, PenSpec pen, MetafilePath mpath, Rgba32 color, bool aa)
    {
        CorePath path = mpath.ToDrawingPath();
        Brush brush = pen.Brush is not null && ToBrush(pen.Brush) is { } pb ? pb : Brush.Solid(color);
        Pen p = MakePen(pen, brush, out Matrix3x2? transform, ref path);
        target.Draw(p, path, aa, 1f, BlendMode.Normal, AlphaComposition.SrcOver, false, transform, _clip, _clipRule);
    }

    // ---- binary raster operations --------------------------------------------------------------------------------------

    private static byte Binary(MixMode mode, byte p, byte d) => mode switch
    {
        MixMode.Black => 0,
        MixMode.NotMergePen => (byte)~(p | d),
        MixMode.MaskNotPen => (byte)(~p & d),
        MixMode.NotCopyPen => (byte)~p,
        MixMode.MaskPenNot => (byte)(p & ~d),
        MixMode.Not => (byte)~d,
        MixMode.XorPen => (byte)(p ^ d),
        MixMode.NotMaskPen => (byte)~(p & d),
        MixMode.MaskPen => (byte)(p & d),
        MixMode.NotXorPen => (byte)~(p ^ d),
        MixMode.Nop => d,
        MixMode.MergeNotPen => (byte)(~p | d),
        MixMode.CopyPen => p,
        MixMode.MergePenNot => (byte)(p | ~d),
        MixMode.MergePen => (byte)(p | d),
        _ => 255,
    };

    private static void WhiteBehind(Rgba32[] px, int i)
    {
        Rgba32 c = px[i];
        if (c.A == 255)
        {
            return;
        }

        int a = c.A;
        px[i] = new Rgba32((byte)(((c.R * a) + (255 * (255 - a))) / 255), (byte)(((c.G * a) + (255 * (255 - a))) / 255), (byte)(((c.B * a) + (255 * (255 - a))) / 255), 255);
    }

    private void RopCoverage(Action<Canvas> drawMask, MixMode mode, Rgba32 pen)
    {
        using var mask = new Canvas(_w, _h, PixelFormat.Rgba8);
        drawMask(mask);
        var m = new Rgba32[_w * _h];
        var d = new Rgba32[_w * _h];
        mask.CopyTo<Rgba32>(m, new ImageRect(0, 0, _w, _h));
        _canvas.CopyTo<Rgba32>(d, new ImageRect(0, 0, _w, _h));
        for (int i = 0; i < d.Length; i++)
        {
            if (m[i].A < 128)
            {
                continue;
            }

            WhiteBehind(d, i);
            Rgba32 c = d[i];
            d[i] = new Rgba32(Binary(mode, pen.R, c.R), Binary(mode, pen.G, c.G), Binary(mode, pen.B, c.B), 255);
        }

        _canvas.CopyFrom<Rgba32>(d, new ImageRect(0, 0, _w, _h));
    }

    private static byte Ternary(uint rop, byte p, byte s, byte d)
    {
        int index = (int)((rop >> 16) & 0xFF);
        int result = 0;
        for (int i = 0; i < 8; i++)
        {
            if (((index >> i) & 1) == 0)
            {
                continue;
            }

            int pm = (i & 4) != 0 ? p : ~p, sm = (i & 2) != 0 ? s : ~s, dm = (i & 1) != 0 ? d : ~d;
            result |= pm & sm & dm;
        }

        return (byte)result;
    }

    // ---- bitmaps -------------------------------------------------------------------------------------------------------

    public override void VisitBlit(BlitCommand c)
    {
        _ct.ThrowIfCancellationRequested();
        Vector2 br = c.DestTopRight + c.DestBottomLeft - c.DestTopLeft;
        var quad = new MetafilePathBuilder(8);
        quad.MoveTo(c.DestTopLeft);
        quad.LineTo(c.DestTopRight);
        quad.LineTo(br);
        quad.LineTo(c.DestBottomLeft);
        quad.Close();
        MetafilePath mq = quad.Build();
        if (Outside(mq, 2))
        {
            return;
        }

        CorePath path = mq.ToDrawingPath();
        Brush? source = c.Bitmap is null ? null : SourceBrush(c);
        if (c.Kind == BlitKind.AlphaBlend && source is not null)
        {
            _canvas.Fill(source, path, _aa, IntersectionRule.NonZero, c.ConstantAlpha / 255f, BlendMode.Normal, AlphaComposition.SrcOver, false, Xf, _clip, _clipRule);
            return;
        }

        if (c.Kind == BlitKind.TransparentColor && source is not null)
        {
            _canvas.Fill(source, path, _aa, IntersectionRule.NonZero, 1f, BlendMode.Normal, AlphaComposition.SrcOver, false, Xf, _clip, _clipRule);
            return;
        }

        uint rop = c.Rop;
        Brush? pattern = c.Brush is null ? null : ToBrush(c.Brush);
        if (rop == 0x00CC0020 && source is not null && c.Mask is null)
        {
            _canvas.Fill(source, path, false, IntersectionRule.NonZero, 1f, BlendMode.Normal, AlphaComposition.SrcOver, false, Xf, _clip, _clipRule);
            return;
        }

        if (rop == 0x00F00021 && pattern is not null)
        {
            _canvas.Fill(pattern, path, false, IntersectionRule.NonZero, 1f, BlendMode.Normal, AlphaComposition.SrcOver, false, Xf, _clip, _clipRule);
            return;
        }

        if (rop == 0x00000042 || rop == 0x00FF0062)
        {
            _canvas.Fill(rop == 0x42 ? new Rgba32(0, 0, 0, 255) : new Rgba32(255, 255, 255, 255), path, false, IntersectionRule.NonZero, 1f, BlendMode.Normal, AlphaComposition.SrcOver, false, Xf, _clip, _clipRule);
            return;
        }

        // The general case: combine source, pattern and destination pixel by pixel inside the aliased dest area.
        using var mask = new Canvas(_w, _h, PixelFormat.Rgba8);
        mask.Fill(new Rgba32(255, 255, 255, 255), path, false, IntersectionRule.NonZero, 1f, BlendMode.Normal, AlphaComposition.SrcOver, false, Xf, _clip, _clipRule);
        var m = new Rgba32[_w * _h];
        mask.CopyTo<Rgba32>(m, new ImageRect(0, 0, _w, _h));
        Rgba32[] s = Layer(source, path);
        Rgba32[] p = Layer(pattern, path);
        Rgba32[]? mk = null;
        var d = new Rgba32[_w * _h];
        _canvas.CopyTo<Rgba32>(d, new ImageRect(0, 0, _w, _h));
        _ = mk;
        for (int i = 0; i < d.Length; i++)
        {
            if (m[i].A < 128)
            {
                continue;
            }

            WhiteBehind(d, i);
            Rgba32 dd = d[i], ss = s[i], pp = p[i];
            d[i] = new Rgba32(Ternary(rop, pp.R, ss.R, dd.R), Ternary(rop, pp.G, ss.G, dd.G), Ternary(rop, pp.B, ss.B, dd.B), 255);
        }

        _canvas.CopyFrom<Rgba32>(d, new ImageRect(0, 0, _w, _h));
    }

    private Rgba32[] Layer(Brush? brush, CorePath path)
    {
        var px = new Rgba32[_w * _h];
        if (brush is null)
        {
            return px;
        }

        using var tmp = new Canvas(_w, _h, PixelFormat.Rgba8);
        tmp.Fill(brush, path, false, IntersectionRule.NonZero, 1f, BlendMode.Normal, AlphaComposition.SrcOver, false, Xf, null, IntersectionRule.NonZero);
        tmp.CopyTo<Rgba32>(px, new ImageRect(0, 0, _w, _h));
        return px;
    }

    private Brush? SourceBrush(BlitCommand c)
    {
        DeviceIndependentBitmap dib = c.Bitmap!;
        bool useAlpha = c.Kind == BlitKind.AlphaBlend && c.UsePerPixelAlpha;
        byte[] all = Rgba(dib, useAlpha);
        int left = (int)Math.Clamp(Math.Floor(c.Source.Left), 0, dib.Width), top = (int)Math.Clamp(Math.Floor(c.Source.Top), 0, dib.Height);
        int right = (int)Math.Clamp(Math.Ceiling(c.Source.Right), 0, dib.Width), bottom = (int)Math.Clamp(Math.Ceiling(c.Source.Bottom), 0, dib.Height);
        int sw = right - left, sh = bottom - top;
        if (sw <= 0 || sh <= 0)
        {
            return null;
        }

        var crop = new byte[sw * sh * 4];
        for (int y = 0; y < sh; y++)
        {
            Buffer.BlockCopy(all, (((top + y) * dib.Width) + left) * 4, crop, y * sw * 4, sw * 4);
        }

        if (c.Kind == BlitKind.TransparentColor)
        {
            for (int i = 0; i < crop.Length; i += 4)
            {
                if (crop[i] == c.TransparentColor.R && crop[i + 1] == c.TransparentColor.G && crop[i + 2] == c.TransparentColor.B)
                {
                    crop[i + 3] = 0;
                }
            }
        }

        if (c.Mask is { } mask)
        {
            byte[] mp = Rgba(mask, false);
            for (int y = 0; y < sh; y++)
            {
                for (int x = 0; x < sw; x++)
                {
                    int mx = (int)c.MaskOrigin.X + x, my = (int)c.MaskOrigin.Y + y;
                    bool keep = mx >= 0 && my >= 0 && mx < mask.Width && my < mask.Height && mp[((my * mask.Width) + mx) * 4] > 127;
                    if (!keep)
                    {
                        crop[((y * sw) + x) * 4 + 3] = 0;
                    }
                }
            }
        }

        Vector2 u = (c.DestTopRight - c.DestTopLeft) / sw, v = (c.DestBottomLeft - c.DestTopLeft) / sh;
        var s2l = new Matrix3x2(u.X, u.Y, v.X, v.Y, c.DestTopLeft.X, c.DestTopLeft.Y);
        // GDI's halftone mode averages when it shrinks a bitmap and repeats pixels when it enlarges one (measured from Windows output).
        Matrix3x2 total = s2l * Xf;
        bool shrinks = Math.Sqrt((total.M11 * total.M11) + (total.M12 * total.M12)) < 1 - 1e-6 || Math.Sqrt((total.M21 * total.M21) + (total.M22 * total.M22)) < 1 - 1e-6;
        ImageSampling sampling = c.Stretch == StretchMode.Halftone && shrinks ? ImageSampling.Bilinear : ImageSampling.Nearest;
        return Brush.FromImage(ImageOf(crop, sw, sh), sampling, null, 0, 0, total, false);
    }

    // ---- gradients -----------------------------------------------------------------------------------------------------

    public override void VisitGradientFill(GradientFillCommand c)
    {
        _ct.ThrowIfCancellationRequested();
        if (c.Mode == GradientMode.Triangle)
        {
            for (int i = 0; i + 2 < c.Indices.Count; i += 3)
            {
                GradientVertex a = c.Vertices[c.Indices[i]], b = c.Vertices[c.Indices[i + 1]], d = c.Vertices[c.Indices[i + 2]];
                Vector2 pa = Vector2.Transform(a.Position, Xf), pb = Vector2.Transform(b.Position, Xf), pd = Vector2.Transform(d.Position, Xf);
                Vector2 center = (pa + pb + pd) / 3;
                Rgba32 cc = new((byte)((a.Color.R + b.Color.R + d.Color.R) / 3), (byte)((a.Color.G + b.Color.G + d.Color.G) / 3), (byte)((a.Color.B + b.Color.B + d.Color.B) / 3), 255);
                Brush brush = Brush.PathGradient([pa, pb, pd], [a.Color, b.Color, d.Color], cc, center);
                var tri = new PathBuilder().MoveTo(pa).LineTo(pb).LineTo(pd).Close().Build();
                _canvas.Fill(brush, tri, _aa, IntersectionRule.NonZero, 1f, BlendMode.Normal, AlphaComposition.SrcOver, false, null, _clip, _clipRule);
            }

            return;
        }

        bool horizontal = c.Mode == GradientMode.Horizontal;
        for (int i = 0; i + 1 < c.Indices.Count; i += 2)
        {
            GradientVertex a = c.Vertices[c.Indices[i]], b = c.Vertices[c.Indices[i + 1]];
            float x0 = Math.Min(a.Position.X, b.Position.X), x1 = Math.Max(a.Position.X, b.Position.X);
            float y0 = Math.Min(a.Position.Y, b.Position.Y), y1 = Math.Max(a.Position.Y, b.Position.Y);
            if (x1 <= x0 || y1 <= y0)
            {
                continue;
            }

            Vector2 start = horizontal ? new Vector2(a.Position.X, 0) : new Vector2(0, a.Position.Y);
            Vector2 end = horizontal ? new Vector2(b.Position.X, 0) : new Vector2(0, b.Position.Y);
            if (start == end)
            {
                continue;
            }

            Brush brush = Brush.LinearGradient(start, end, [new ColorStop(0f, a.Color), new ColorStop(1f, b.Color)], Xf, GradientRepetition.None, false);
            _canvas.Fill(brush, CorePath.Rectangle(x0, y0, x1 - x0, y1 - y0), _aa, IntersectionRule.NonZero, 1f, BlendMode.Normal, AlphaComposition.SrcOver, false, Xf, _clip, _clipRule);
        }
    }

    // ---- text ----------------------------------------------------------------------------------------------------------

    public override void VisitDrawText(DrawTextCommand c)
    {
        _ct.ThrowIfCancellationRequested();
        if (c.OpaqueRectangle is { } or)
        {
            Rect(or, c.OpaqueColor);
        }

        if (c.Text.Length == 0)
        {
            return;
        }

        FontSpec fs = c.Font;
        double em = _fonts.EmSize(fs);
        TextFont? font = _fonts.CreateFont(fs, em);
        if (font is null)
        {
            return;
        }

        FontMetrics fm = font.Metrics;
        double k = em / fm.UnitsPerEm;
        double asc = fm.Ascender * k, desc = Math.Abs(fm.Descender) * k;
        var cps = new List<(int Cp, int Unit)>();
        for (int i = 0; i < c.Text.Length; i++)
        {
            int cp = c.Text[i];
            if (char.IsHighSurrogate(c.Text[i]) && i + 1 < c.Text.Length && char.IsLowSurrogate(c.Text[i + 1]))
            {
                cp = char.ConvertToUtf32(c.Text[i], c.Text[i + 1]);
                cps.Add((cp, i));
                i++;
                continue;
            }

            cps.Add((cp, i));
        }

        var adv = new double[cps.Count];
        double total = 0;
        int breaks = c.BreakCount;
        for (int i = 0; i < adv.Length; i++)
        {
            (int cp, int unit) = cps[i];
            double a = c.Advances is { } dx && unit < dx.Count ? dx[unit] : font.GetAdvance(cp) + c.CharacterExtra;
            if (cp == 32 && breaks > 0 && c.BreakExtra != 0 && c.Advances is null)
            {
                a += c.BreakExtra / Math.Max(c.BreakCount, 1);
                breaks--;
            }

            adv[i] = a;
            total += a;
        }

        double ox = c.Origin.X, oy = c.Origin.Y;
        double x0 = c.Horizontal switch { TextHorizontalAlignment.Center => ox - (total / 2), TextHorizontalAlignment.Right => ox - total, _ => ox };
        double baseline = c.Vertical switch { TextVerticalAlignment.Top => oy + asc, TextVerticalAlignment.Bottom => oy - desc, _ => oy };
        float angle = (float)(-fs.Escapement / 10.0 * Math.PI / 180);
        Matrix3x2 rot = Matrix3x2.CreateRotation(angle, c.Origin);
        Matrix3x2 xf = rot * Xf;
        CorePath? savedClip = _clip;
        IntersectionRule savedRule = _clipRule;
        if (c.ClipRectangle is { } cr)
        {
            CorePath q = CorePath.Rectangle((float)Math.Min(cr.Left, cr.Right), (float)Math.Min(cr.Top, cr.Bottom), (float)Math.Abs(cr.Width), (float)Math.Abs(cr.Height)).Transform(Xf);
            _clip = _clip is null ? q : _clip.Intersect(q, _clipRule, IntersectionRule.NonZero);
            _clipRule = IntersectionRule.NonZero;
        }

        try
        {
            if (c.Background is { } bg)
            {
                _canvas.Fill(bg, CorePath.Rectangle((float)x0, (float)(baseline - asc), (float)total, (float)(asc + desc)), _aa, IntersectionRule.NonZero, 1f, BlendMode.Normal, AlphaComposition.SrcOver, false, xf, _clip, _clipRule);
            }

            var b = new PathBuilder();
            double pen = x0;
            for (int i = 0; i < cps.Count; i++)
            {
                CorePath g = font.GetGlyphPath(cps[i].Cp);
                b.AddPath(g.Translate((float)pen, (float)baseline));
                pen += adv[i];
            }

            if (fs.Underline)
            {
                double top = baseline - (fm.UnderlinePosition * k);
                b.AddPath(CorePath.Rectangle((float)x0, (float)top, (float)total, (float)Math.Max(fm.UnderlineThickness * k, 1)));
            }

            if (fs.StrikeOut)
            {
                double top = baseline - (fm.StrikeoutPosition * k);
                b.AddPath(CorePath.Rectangle((float)x0, (float)top, (float)total, (float)Math.Max(fm.StrikeoutSize * k, 1)));
            }

            _canvas.Fill(c.Color, b.Build(), _aa && fs.Quality != 3, IntersectionRule.NonZero, 1f, BlendMode.Normal, AlphaComposition.SrcOver, false, xf, _clip, _clipRule);
        }
        finally
        {
            _clip = savedClip;
            _clipRule = savedRule;
        }
    }

    private void Rect(RectD r, Rgba32 color)
    {
        RectD n = r.Normalized;
        _canvas.Fill(color, CorePath.Rectangle((float)n.Left, (float)n.Top, (float)n.Width, (float)n.Height), _aa, IntersectionRule.NonZero, 1f, BlendMode.Normal, AlphaComposition.SrcOver, false, Xf, _clip, _clipRule);
    }
}
