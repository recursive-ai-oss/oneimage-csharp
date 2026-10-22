using OneImage.Kernels;

namespace OneImage.Drawing;

/// <summary>The options of one fill, after validation. Both <c>Image.Fill</c> and <c>Canvas.Fill</c> build this.</summary>
internal readonly record struct FillSettings(
    bool Antialias,
    IntersectionRule Rule,
    float Opacity,
    BlendMode Blend,
    AlphaComposition Composition,
    bool Linear,
    System.Numerics.Matrix3x2 Transform,
    float Tolerance)
{
    public static FillSettings Check(bool antialias, IntersectionRule rule, float opacity, BlendMode blend, AlphaComposition composition, bool linear, System.Numerics.Matrix3x2? transform, PixelFormat format)
    {
        if (!(opacity >= 0f && opacity <= 1f))
        {
            throw new ArgumentOutOfRangeException(nameof(opacity), "Opacity must be between 0 and 1.");
        }

        if (rule is not (IntersectionRule.NonZero or IntersectionRule.EvenOdd))
        {
            throw new ArgumentOutOfRangeException(nameof(rule));
        }

        if (!BlendNames.IsDefined(blend))
        {
            throw new ArgumentOutOfRangeException(nameof(blend));
        }

        if (!BlendNames.IsDefined(composition))
        {
            throw new ArgumentOutOfRangeException(nameof(composition));
        }

        System.Numerics.Matrix3x2 m = transform ?? System.Numerics.Matrix3x2.Identity;
        if (!float.IsFinite(m.M11 + m.M12 + m.M21 + m.M22 + m.M31 + m.M32))
        {
            throw new ArgumentOutOfRangeException(nameof(transform), "The transform must be finite.");
        }

        if (format.Model is not (ColorModel.Rgb or ColorModel.Gray))
        {
            throw new ImageOperationException("Drawing works on RGB and gray images; convert CMYK and Lab first.");
        }

        if (!format.HasAlpha && !BlendNames.KeepsOpaqueBackdropOpaque(composition))
        {
            throw new ImageOperationException($"Composition {composition} would make an image without alpha transparent; add an alpha channel first.");
        }

        return new FillSettings(antialias, rule, opacity, blend, composition, linear, m, Path.DefaultTolerance);
    }
}

/// <summary>Everything a tile needs to paint a fill: the target format, the color source and how to combine it with the pixels.</summary>
internal sealed class FillPlan
{
    private enum Fast
    {
        None,
        Rgba8,
        Rgb8,
    }

    private readonly Fast _fast;
    private readonly bool _opaqueReplace; // normal source-over at full opacity: an opaque brush pixel on a covered pixel is the result
    private readonly byte _cr, _cg, _cb;
    private readonly float _fr, _fg, _fb, _alpha; // color in 0..255 and its alpha times opacity

    private FillPlan(PixelFormat format, Brush brush, FillSettings s, float[]? solidWork)
    {
        Format = format;
        Work = new PixelFormat(format.Model, SampleType.F32, AlphaMode.Premultiplied);
        Brush = brush;
        Settings = s;
        Solid = brush.TryGetSolid(out Rgba32 color);
        if (!Solid)
        {
            _opaqueReplace = s.Blend == BlendMode.Normal && s.Composition == AlphaComposition.SrcOver && s.Opacity >= 1f;
            SolidWork = [];
            return;
        }

        int ch = Work.ChannelCount;
        if (solidWork == null)
        {
            solidWork = new float[ch];
            Span<byte> px = [color.R, color.G, color.B, color.A];
            ConvertKernel.ConvertRow(px, PixelFormat.Rgba8, SpanCast.AsBytes(solidWork.AsSpan()), Work, 1);
        }

        SolidWork = solidWork;
        float alpha = solidWork[ch - 1] * s.Opacity;
        bool normalOver = s.Blend == BlendMode.Normal && s.Composition == AlphaComposition.SrcOver;
        if (normalOver && !s.Linear && format == PixelFormat.Rgba8)
        {
            _fast = Fast.Rgba8;
        }
        else if (normalOver && !s.Linear && format == PixelFormat.Rgb8)
        {
            _fast = Fast.Rgb8;
        }

        _cr = color.R;
        _cg = color.G;
        _cb = color.B;
        _fr = color.R;
        _fg = color.G;
        _fb = color.B;
        _alpha = color.A / 255f * s.Opacity;

        // A fully covered pixel's result does not depend on the backdrop for these combinations, so it can be written directly.
        float[]? full = null;
        if (s.Composition == AlphaComposition.Clear)
        {
            full = new float[ch];
        }
        else if (s.Blend == BlendMode.Normal && s.Composition == AlphaComposition.Src)
        {
            full = new float[ch];
            for (int i = 0; i < ch; i++)
            {
                full[i] = solidWork[i] * s.Opacity;
            }
        }
        else if (normalOver && alpha >= 1f)
        {
            full = solidWork;
        }

        if (full != null)
        {
            Replace = new byte[format.BytesPerPixel];
            ConvertKernel.ConvertRow(SpanCast.AsBytes(full.AsSpan()), Work, Replace, format, 1);
        }
    }

    public PixelFormat Format { get; }

    public PixelFormat Work { get; }

    public Brush Brush { get; }

    public FillSettings Settings { get; }

    public bool Solid { get; }

    /// <summary>The brush color as premultiplied working-format floats (alpha not yet scaled by opacity); empty for non-solid brushes.</summary>
    public float[] SolidWork { get; }

    /// <summary>The bytes of a fully covered pixel when they do not depend on the backdrop; otherwise null.</summary>
    public byte[]? Replace { get; }

    public static FillPlan Create(PixelFormat format, Brush brush, FillSettings settings, float[]? solidWork = null)
    {
        return new FillPlan(format, brush, settings, solidWork);
    }

    /// <summary>Paints the rasterized coverage in <paramref name="s"/> into <paramref name="tile"/> (a tile in <see cref="Format"/>, rows of Tile.Size pixels).</summary>
    public void Paint(Span<byte> tile, int tw, int th, int originX, int originY, RasterScratch s)
    {
        int bpp = Format.BytesPerPixel;
        for (int r = 0; r < th; r++)
        {
            int a = s.SpanStart[r], b = s.SpanEnd[r];
            if (a >= b)
            {
                continue;
            }

            ReadOnlySpan<float> cov = s.Coverage.AsSpan(r * Tile.Size, Tile.Size);
            Span<byte> row = tile.Slice(r * Tile.Size * bpp, tw * bpp);
            switch (_fast)
            {
                case Fast.Rgba8:
                    PaintRgba8(row, cov, a, b);
                    break;
                case Fast.Rgb8:
                    PaintRgb8(row, cov, a, b);
                    break;
                default:
                    PaintGeneral(row, cov, a, b, originX, originY + r, s);
                    break;
            }
        }
    }

    private void PaintRgba8(Span<byte> row, ReadOnlySpan<float> cov, int a, int b)
    {
        for (int x = a; x < b; x++)
        {
            float c = cov[x];
            if (c <= 0f)
            {
                continue;
            }

            float sa = c * _alpha;
            Span<byte> p = row.Slice(x * 4, 4);
            if (sa >= 1f)
            {
                p[0] = _cr;
                p[1] = _cg;
                p[2] = _cb;
                p[3] = 255;
                continue;
            }

            byte ba = p[3];
            float oa, k;
            if (ba == 255)
            {
                oa = 255f;
                k = sa;
            }
            else if (ba == 0)
            {
                // Nothing underneath: the straight color stands, only its alpha is partial.
                p[0] = _cr;
                p[1] = _cg;
                p[2] = _cb;
                p[3] = (byte)((sa * 255f) + 0.5f);
                continue;
            }
            else
            {
                float baf = ba * (1f / 255f);
                float ra = sa + (baf * (1f - sa));
                oa = ra * 255f;
                k = sa / ra;
            }

            p[0] = (byte)(p[0] + ((_fr - p[0]) * k) + 0.5f);
            p[1] = (byte)(p[1] + ((_fg - p[1]) * k) + 0.5f);
            p[2] = (byte)(p[2] + ((_fb - p[2]) * k) + 0.5f);
            p[3] = (byte)(oa + 0.5f);
        }
    }

    private void PaintRgb8(Span<byte> row, ReadOnlySpan<float> cov, int a, int b)
    {
        for (int x = a; x < b; x++)
        {
            float c = cov[x];
            if (c <= 0f)
            {
                continue;
            }

            float sa = c * _alpha;
            Span<byte> p = row.Slice(x * 3, 3);
            if (sa >= 1f)
            {
                p[0] = _cr;
                p[1] = _cg;
                p[2] = _cb;
                continue;
            }

            p[0] = (byte)(p[0] + ((_fr - p[0]) * sa) + 0.5f);
            p[1] = (byte)(p[1] + ((_fg - p[1]) * sa) + 0.5f);
            p[2] = (byte)(p[2] + ((_fb - p[2]) * sa) + 0.5f);
        }
    }

    // Any format, any blend mode and brush: the operator is applied at full strength to a run of pixels and the result is
    // mixed back with the backdrop by the coverage. For Normal/source-over that equals scaling the alpha by the coverage.
    private void PaintGeneral(Span<byte> row, ReadOnlySpan<float> cov, int a, int b, int originX, int y, RasterScratch s)
    {
        int bpp = Format.BytesPerPixel;
        int x = a;
        while (x < b)
        {
            if (cov[x] <= 0f)
            {
                x++;
                continue;
            }

            if (Replace != null && cov[x] >= 1f)
            {
                int e = x;
                while (e < b && cov[e] >= 1f)
                {
                    row.Slice(e * bpp, bpp).Clear();
                    Replace.CopyTo(row.Slice(e * bpp, bpp));
                    e++;
                }

                x = e;
                continue;
            }

            int end = x + 1;
            while (end < b && cov[end] > 0f && (Replace == null || cov[end] < 1f))
            {
                end++;
            }

            BlendRun(row, cov, x, end, originX, y, s);
            x = end;
        }
    }

    private void BlendRun(Span<byte> row, ReadOnlySpan<float> cov, int x0, int x1, int originX, int y, RasterScratch s)
    {
        int n = x1 - x0;
        int ch = Work.ChannelCount;
        int count = n * ch;
        int bpp = Format.BytesPerPixel;
        Span<byte> pixels = row.Slice(x0 * bpp, n * bpp);
        Span<float> rgba = default;
        if (!Solid)
        {
            rgba = s.BrushRow.AsSpan(0, n * 4);
            if (Brush.ReadsBackdrop)
            {
                Span<float> backdrop = s.Straight.AsSpan(0, n * 4);
                ConvertKernel.ConvertRow(pixels, Format, SpanCast.AsBytes(backdrop), PixelFormat.RgbaF32, n);
                Brush.FillRow(originX + x0, y, backdrop, rgba);
            }
            else
            {
                Brush.FillRow(originX + x0, y, rgba);
            }

            // Opaque brush colors over fully covered pixels replace them: no need to read the backdrop and blend.
            if (_opaqueReplace && AllOpaqueAndCovered(cov.Slice(x0, n), rgba))
            {
                if (Format == PixelFormat.Rgba8)
                {
                    BrushKernel.RgbaF32ToRgba8(rgba, pixels);
                }
                else
                {
                    ConvertKernel.ConvertRow(SpanCast.AsBytes(rgba), PixelFormat.RgbaF32, pixels, Format, n);
                }

                return;
            }
        }

        Span<float> back = s.Back.AsSpan(0, count), over = s.Over.AsSpan(0, count), result = s.Result.AsSpan(0, count);
        ConvertKernel.ConvertRow(pixels, Format, SpanCast.AsBytes(back), Work, n);
        if (Solid)
        {
            for (int i = 0; i < n; i++)
            {
                SolidWork.CopyTo(over.Slice(i * ch, ch));
            }
        }
        else
        {
            ConvertKernel.ConvertRow(SpanCast.AsBytes(rgba), PixelFormat.RgbaF32, SpanCast.AsBytes(over), Work, n);
        }

        FillSettings st = Settings;
        BlendKernel.Row(st.Blend, back, over, result, Work.ColorChannels, st.Opacity, st.Composition);
        for (int i = 0; i < n; i++)
        {
            float c = cov[x0 + i];
            if (c < 1f)
            {
                for (int k = 0; k < ch; k++)
                {
                    int j = (i * ch) + k;
                    result[j] = back[j] + ((result[j] - back[j]) * c);
                }
            }
        }

        ConvertKernel.ConvertRow(SpanCast.AsBytes(result), Work, pixels, Format, n);
    }

    private static bool AllOpaqueAndCovered(ReadOnlySpan<float> cov, ReadOnlySpan<float> rgba)
    {
        for (int i = 0; i < cov.Length; i++)
        {
            if (cov[i] < 1f || rgba[(i * 4) + 3] < 1f)
            {
                return false;
            }
        }

        return true;
    }
}
