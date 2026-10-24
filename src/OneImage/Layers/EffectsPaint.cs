using System.Buffers;

namespace OneImage.Layers;

internal static partial class EffectRenderer
{
    /// <summary>
    /// Fills <paramref name="rgba"/> (rect.Width × rect.Height × 4 floats, straight color) with the per-pixel paint of an effect that is not one solid
    /// color: gradient and pattern overlays, gradient and pattern strokes and gradient glows. Returns false for the others, which use the effect's color.
    /// <paramref name="cov"/> is the coverage <see cref="Render"/> produced for the same rectangle.
    /// </summary>
    public static bool Paint(FxItem item, IFxSource source, ImageRect rect, float[] cov, float[] rgba)
    {
        switch (item.Effect)
        {
            case GradientOverlayEffect g:
                PaintGradient(g.Fill, source, rect, rgba);
                return true;
            case PatternOverlayEffect p:
                PaintPattern(p.Fill, source, rect, rgba);
                return true;
            case StrokeEffect { Gradient: { } sg } s:
                if (sg.Style == EffectGradientStyle.ShapeBurst)
                {
                    PaintShapeBurst(s, item.Part, sg, source, rect, rgba);
                }
                else
                {
                    PaintGradient(sg, source, rect, rgba);
                }

                return true;
            case StrokeEffect { PatternFill: { } sp }:
                PaintPattern(sp, source, rect, rgba);
                return true;
            case OuterGlowEffect { Gradient: { } og }:
                PaintGlow(og, cov, rect, rgba);
                return true;
            case InnerGlowEffect { Gradient: { } ig }:
                PaintGlow(ig, cov, rect, rgba);
                return true;
            default:
                return false;
        }
    }

    private static void PaintGradient(EffectGradientFill fill, IFxSource source, ImageRect rect, float[] rgba)
    {
        EffectGradientFill.Painter painter = fill.CreatePainter(source.Box, source.Document);
        for (int y = 0; y < rect.Height; y++)
        {
            for (int x = 0; x < rect.Width; x++)
            {
                painter.Lookup(painter.Position(rect.X + x, rect.Y + y), rgba.AsSpan(((y * rect.Width) + x) * 4, 4));
            }
        }
    }

    private static void PaintPattern(EffectPatternFill fill, IFxSource source, ImageRect rect, float[] rgba)
    {
        for (int y = 0; y < rect.Height; y++)
        {
            fill.FillRow(source.Box, rect.X, rect.Y + y, rgba.AsSpan(y * rect.Width * 4, rect.Width * 4));
        }
    }

    // Shape burst: the position along the gradient is the distance from the edge of the shape, relative to the stroke's size.
    private static void PaintShapeBurst(StrokeEffect s, FxPart part, EffectGradientFill fill, IFxSource source, ImageRect rect, float[] rgba)
    {
        float size = Math.Max(s.Size, 1e-3f);
        int margin = (int)Math.Ceiling(s.Size) + 2;
        ImageRect region = Layer.Widen(rect, margin);
        float[] a = Pools<float>.Pool.Rent(region.Width * region.Height);
        try
        {
            source.ReadAlpha(region, a);
            bool outer = s.Position == StrokePosition.Outside || part == FxPart.OutsideHalf;
            bool inner = s.Position == StrokePosition.Inside || part == FxPart.InsideHalf;
            using var field = new SignedDistance(a, region.Width, region.Height, outside: outer, inside: inner);
            EffectGradientFill.Painter painter = fill.CreatePainter(source.Box, source.Document);
            for (int y = 0; y < rect.Height; y++)
            {
                for (int x = 0; x < rect.Width; x++)
                {
                    float d = field.Value(((y + margin) * region.Width) + x + margin);
                    float t = Math.Abs(d) / size;
                    painter.Lookup(fill.Reverse ? 1f - t : t, rgba.AsSpan(((y * rect.Width) + x) * 4, 4));
                }
            }
        }
        finally
        {
            Pools<float>.Pool.Return(a);
        }
    }

    // A gradient glow: the gradient's first color sits at the shape and the last where the glow has faded out, so the position is the glow's falloff
    // turned around. The coverage then carries only the shape of the glow; the gradient's opacity stops multiply into it.
    private static void PaintGlow(EffectGradient gradient, float[] cov, ImageRect rect, float[] rgba)
    {
        float[] table = gradient.Table;
        int n = rect.Width * rect.Height;
        for (int i = 0; i < n; i++)
        {
            float t = 1f - Clamp01(cov[i]);
            float f = t * EffectGradient.TableSize;
            int k = Math.Min((int)f, EffectGradient.TableSize - 1);
            float fr = f - k;
            int a = k * 4;
            for (int c = 0; c < 4; c++)
            {
                rgba[(i * 4) + c] = table[a + c] + ((table[a + 4 + c] - table[a + c]) * fr);
            }
        }
    }

    // Constants of the lighting model, fitted to the bevels of the sample documents.
    private const float BevelSlopeScale = 1f;

    // The same blur-per-size ratio as the other effects.
    private const float SmoothSigmaPerSize = EffectRenderer.SigmaPerSize;
    private const float ChiselSoftSigma = 1.0f;

    /// <summary>
    /// The lighting of a bevel or emboss: the shape's signed edge distance becomes a height field, whose slope is lit from the effect's angle and
    /// altitude. The highlight item (Sub 0) gets the surfaces turned toward the light, the shadow item (Sub 1) those turned away.
    /// </summary>
    private static bool Bevel(BevelEmbossEffect b, FxItem item, IFxSource source, ImageRect rect, float[] cov)
    {
        float size = Math.Max(b.Size, 0.01f);
        int margin = (int)Math.Ceiling(b.Size + b.Soften) + 4;
        ImageRect region = Layer.Widen(rect, margin);
        int w = region.Width;
        int h = region.Height;
        float[] a = Pools<float>.Pool.Rent(w * h);
        float[] height = Pools<float>.Pool.Rent(w * h);
        try
        {
            source.ReadAlpha(region, a);
            float sign = b.Up ? 1f : -1f;
            float scale = sign * b.Depth * size * BevelSlopeScale;
            if (b.Technique == BevelTechnique.Smooth)
            {
                // A smooth bevel is the shape blurred by the size: the blurred step is the rounded slope, and the slope is continuous also in the
                // middle of a shape that is thinner than the bevel (where a distance-based ramp has creases).
                Array.Copy(a, height, w * h);
                BlurPlan.For(b.Size * SmoothSigmaPerSize).Apply(height, w, h);
                if (b.Style == BevelStyle.PillowEmboss)
                {
                    for (int i = 0; i < w * h; i++)
                    {
                        height[i] = scale * 2f * Math.Abs(height[i] - 0.5f);
                    }
                }
                else
                {
                    for (int i = 0; i < w * h; i++)
                    {
                        height[i] *= scale;
                    }
                }
            }
            else
            {
                using var field = new SignedDistance(a, w, h, outside: true, inside: true);
                for (int i = 0; i < w * h; i++)
                {
                    // Distance into the shape (negative outside).
                    float u = -field.Value(i);
                    height[i] = scale * Profile(b, u, size);
                }

                if (b.Technique == BevelTechnique.ChiselSoft)
                {
                    BlurPlan.For(ChiselSoftSigma).Apply(height, w, h);
                }
            }

            if (b.Soften > 0f)
            {
                BlurPlan.For(b.Soften * SigmaPerSize).Apply(height, w, h);
            }

            double alt = b.Altitude * Math.PI / 180.0;
            double ang = b.Angle * Math.PI / 180.0;
            float lx = (float)(Math.Cos(alt) * Math.Cos(ang));
            float ly = (float)(-Math.Cos(alt) * Math.Sin(ang));
            float lz = (float)Math.Sin(alt);
            float flat = lz;
            bool highlight = item.Sub == 0;
            bool outsidePart = item.Part == FxPart.OutsideHalf;
            bool any = false;
            for (int y = 0; y < rect.Height; y++)
            {
                int row = ((y + margin) * w) + margin;
                for (int x = 0; x < rect.Width; x++)
                {
                    int i = row + x;
                    float own = a[i];
                    float gx = (height[i + 1] - height[i - 1]) * 0.5f;
                    float gy = (height[i + w] - height[i - w]) * 0.5f;
                    float lit = ((-gx * lx) - (gy * ly) + lz) / MathF.Sqrt(1f + (gx * gx) + (gy * gy));
                    float v = lit >= flat ? (lit - flat) / (1f - flat) : (lit - flat) / flat;
                    if (b.GlossContour != null)
                    {
                        v = (b.GlossContour.Evaluate((v + 1f) * 0.5f) * 2f) - 1f;
                    }

                    float c = highlight ? Math.Max(0f, v) : Math.Max(0f, -v);
                    c *= outsidePart ? 1f - own : own;
                    cov[(y * rect.Width) + x] = Clamp01(c);
                    any |= c > 0f;
                }
            }

            return any;
        }
        finally
        {
            Pools<float>.Pool.Return(a);
            Pools<float>.Pool.Return(height);
        }
    }

    // The height (0..1 of the full rise) at u pixels inside the edge, for the bevel's style and technique.
    private static float Profile(BevelEmbossEffect b, float u, float size)
    {
        float t = b.Style switch
        {
            BevelStyle.InnerBevel => u / size,
            BevelStyle.OuterBevel => 1f + (u / size),
            BevelStyle.PillowEmboss => Math.Abs(u) / (size * 0.5f),
            _ => 0.5f + (u / size),
        };
        t = Clamp01(t);
        return t;
    }
}
