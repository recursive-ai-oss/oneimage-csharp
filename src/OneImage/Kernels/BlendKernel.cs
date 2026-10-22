namespace OneImage.Kernels;

/// <summary>A blend function on one color channel of straight (non-premultiplied) values in 0..1.</summary>
internal interface ISeparableBlend
{
    /// <summary>True for Normal, where the overlay color is used as is and the row loop skips the un-premultiply.</summary>
    static abstract bool IsNormal { get; }

    /// <summary>The blended color for backdrop <paramref name="cb"/> and overlay <paramref name="cs"/>.</summary>
    static abstract float Blend(float cb, float cs);
}

/// <summary>A blend function that needs all three channels at once (hue, saturation, color, luminosity).</summary>
internal interface INonSeparableBlend
{
    static abstract void Blend(float br, float bg, float bb, float sr, float sg, float sb, out float r, out float g, out float b);
}

internal readonly struct NormalBlend : ISeparableBlend
{
    public static bool IsNormal => true;
    public static float Blend(float cb, float cs) => cs;
}

internal readonly struct MultiplyBlend : ISeparableBlend
{
    public static bool IsNormal => false;
    public static float Blend(float cb, float cs) => cb * cs;
}

internal readonly struct ScreenBlend : ISeparableBlend
{
    public static bool IsNormal => false;
    public static float Blend(float cb, float cs) => (cb + cs) - (cb * cs);
}

internal readonly struct HardLightBlend : ISeparableBlend
{
    public static bool IsNormal => false;

    public static float Blend(float cb, float cs) =>
        cs <= 0.5f ? cb * (2f * cs) : ScreenBlend.Blend(cb, (2f * cs) - 1f);
}

// Overlay is Hard Light with the two layers swapped.
internal readonly struct OverlayBlend : ISeparableBlend
{
    public static bool IsNormal => false;
    public static float Blend(float cb, float cs) => HardLightBlend.Blend(cs, cb);
}

internal readonly struct DarkenBlend : ISeparableBlend
{
    public static bool IsNormal => false;
    public static float Blend(float cb, float cs) => MathF.Min(cb, cs);
}

internal readonly struct LightenBlend : ISeparableBlend
{
    public static bool IsNormal => false;
    public static float Blend(float cb, float cs) => MathF.Max(cb, cs);
}

internal readonly struct ColorDodgeBlend : ISeparableBlend
{
    public static bool IsNormal => false;

    public static float Blend(float cb, float cs) =>
        cb <= 0f ? 0f : cs >= 1f ? 1f : MathF.Min(1f, cb / (1f - cs));
}

internal readonly struct ColorBurnBlend : ISeparableBlend
{
    public static bool IsNormal => false;

    public static float Blend(float cb, float cs) =>
        cb >= 1f ? 1f : cs <= 0f ? 0f : 1f - MathF.Min(1f, (1f - cb) / cs);
}

internal readonly struct SoftLightBlend : ISeparableBlend
{
    public static bool IsNormal => false;

    public static float Blend(float cb, float cs)
    {
        if (cs <= 0.5f)
        {
            return cb - ((1f - (2f * cs)) * cb * (1f - cb));
        }

        float d = cb <= 0.25f ? ((((16f * cb) - 12f) * cb) + 4f) * cb : MathF.Sqrt(cb);
        return cb + (((2f * cs) - 1f) * (d - cb));
    }
}

internal readonly struct DifferenceBlend : ISeparableBlend
{
    public static bool IsNormal => false;
    public static float Blend(float cb, float cs) => MathF.Abs(cb - cs);
}

internal readonly struct ExclusionBlend : ISeparableBlend
{
    public static bool IsNormal => false;
    public static float Blend(float cb, float cs) => (cb + cs) - (2f * cb * cs);
}

internal readonly struct AddBlend : ISeparableBlend
{
    public static bool IsNormal => false;
    public static float Blend(float cb, float cs) => MathF.Min(1f, cb + cs);
}

internal readonly struct SubtractBlend : ISeparableBlend
{
    public static bool IsNormal => false;
    public static float Blend(float cb, float cs) => MathF.Max(0f, cb - cs);
}

internal readonly struct LinearBurnBlend : ISeparableBlend
{
    public static bool IsNormal => false;
    public static float Blend(float cb, float cs) => MathF.Max(0f, (cb + cs) - 1f);
}

internal readonly struct VividLightBlend : ISeparableBlend
{
    public static bool IsNormal => false;

    public static float Blend(float cb, float cs) =>
        cs <= 0.5f ? ColorBurnBlend.Blend(cb, 2f * cs) : ColorDodgeBlend.Blend(cb, (2f * cs) - 1f);
}

internal readonly struct LinearLightBlend : ISeparableBlend
{
    public static bool IsNormal => false;
    public static float Blend(float cb, float cs) => Math.Clamp((cb + (2f * cs)) - 1f, 0f, 1f);
}

internal readonly struct PinLightBlend : ISeparableBlend
{
    public static bool IsNormal => false;

    public static float Blend(float cb, float cs) =>
        cs <= 0.5f ? MathF.Min(cb, 2f * cs) : MathF.Max(cb, (2f * cs) - 1f);
}

internal readonly struct HardMixBlend : ISeparableBlend
{
    public static bool IsNormal => false;
    public static float Blend(float cb, float cs) => cb + cs >= 1f ? 1f : 0f;
}

internal readonly struct DivideBlend : ISeparableBlend
{
    public static bool IsNormal => false;

    public static float Blend(float cb, float cs) =>
        cs <= 0f ? (cb <= 0f ? 0f : 1f) : MathF.Min(1f, cb / cs);
}

/// <summary>The W3C non-separable helpers (Compositing and Blending Level 1, section 10.2).</summary>
internal static class NonSeparable
{
    public static float Lum(float r, float g, float b) => (0.3f * r) + (0.59f * g) + (0.11f * b);

    public static float Sat(float r, float g, float b) => MathF.Max(r, MathF.Max(g, b)) - MathF.Min(r, MathF.Min(g, b));

    // SetSat: the channels keep their order; the smallest becomes 0 and the largest becomes s.
    public static void SetSat(float r, float g, float b, float s, out float outR, out float outG, out float outB)
    {
        float max = MathF.Max(r, MathF.Max(g, b));
        float min = MathF.Min(r, MathF.Min(g, b));
        if (max > min)
        {
            float k = s / (max - min);
            outR = (r - min) * k;
            outG = (g - min) * k;
            outB = (b - min) * k;
        }
        else
        {
            outR = outG = outB = 0f;
        }
    }

    public static void SetLum(float r, float g, float b, float l, out float outR, out float outG, out float outB)
    {
        float d = l - Lum(r, g, b);
        r += d;
        g += d;
        b += d;
        // ClipColor
        float lum = Lum(r, g, b);
        float n = MathF.Min(r, MathF.Min(g, b));
        float x = MathF.Max(r, MathF.Max(g, b));
        if (n < 0f)
        {
            float k = lum / (lum - n);
            r = lum + ((r - lum) * k);
            g = lum + ((g - lum) * k);
            b = lum + ((b - lum) * k);
        }

        if (x > 1f)
        {
            float k = (1f - lum) / (x - lum);
            r = lum + ((r - lum) * k);
            g = lum + ((g - lum) * k);
            b = lum + ((b - lum) * k);
        }

        outR = r;
        outG = g;
        outB = b;
    }
}

internal readonly struct HueBlend : INonSeparableBlend
{
    public static void Blend(float br, float bg, float bb, float sr, float sg, float sb, out float r, out float g, out float b)
    {
        NonSeparable.SetSat(sr, sg, sb, NonSeparable.Sat(br, bg, bb), out float tr, out float tg, out float tb);
        NonSeparable.SetLum(tr, tg, tb, NonSeparable.Lum(br, bg, bb), out r, out g, out b);
    }
}

internal readonly struct SaturationBlend : INonSeparableBlend
{
    public static void Blend(float br, float bg, float bb, float sr, float sg, float sb, out float r, out float g, out float b)
    {
        NonSeparable.SetSat(br, bg, bb, NonSeparable.Sat(sr, sg, sb), out float tr, out float tg, out float tb);
        NonSeparable.SetLum(tr, tg, tb, NonSeparable.Lum(br, bg, bb), out r, out g, out b);
    }
}

internal readonly struct ColorBlend : INonSeparableBlend
{
    public static void Blend(float br, float bg, float bb, float sr, float sg, float sb, out float r, out float g, out float b) =>
        NonSeparable.SetLum(sr, sg, sb, NonSeparable.Lum(br, bg, bb), out r, out g, out b);
}

internal readonly struct LuminosityBlend : INonSeparableBlend
{
    public static void Blend(float br, float bg, float bb, float sr, float sg, float sb, out float r, out float g, out float b) =>
        NonSeparable.SetLum(br, bg, bb, NonSeparable.Lum(sr, sg, sb), out r, out g, out b);
}

/// <summary>
/// Row kernels for every blend mode and Porter–Duff composition. Rows are premultiplied float pixels with <c>colors</c> color
/// channels followed by alpha. With overlay color <c>Cs</c>, alpha <c>αs</c> (opacity applied) and backdrop <c>Cb</c>, <c>αb</c>
/// (W3C Compositing and Blending Level 1, section 9):
/// <c>Cs' = (1 − αb)·Cs + αb·B(Cb, Cs)</c>, result color <c>αs·Fa·Cs' + αb·Fb·Cb</c>, result alpha <c>αs·Fa + αb·Fb</c>.
/// No fused multiply-add is used, so every hardware path gives the same bits.
/// </summary>
internal static class BlendKernel
{
    /// <summary>Blends one row with the separable mode <typeparamref name="TMode"/>.</summary>
    public static void SeparableRow<TMode>(ReadOnlySpan<float> backdrop, ReadOnlySpan<float> overlay, Span<float> destination, int colors, float opacity, (float A0, float A1, float B0, float B1) f)
        where TMode : struct, ISeparableBlend
    {
        int channels = colors + 1;
        int pixels = destination.Length / channels;
        backdrop = backdrop[..(pixels * channels)];
        overlay = overlay[..(pixels * channels)];
        destination = destination[..(pixels * channels)];
        for (int p = 0; p < pixels; p++)
        {
            int i = p * channels;
            float sa = overlay[i + colors] * opacity;
            float da = backdrop[i + colors];
            float fa = f.A0 + (f.A1 * da);
            float fb = f.B0 + (f.B1 * sa);
            for (int c = 0; c < colors; c++)
            {
                float s = overlay[i + c] * opacity;
                float d = backdrop[i + c];
                float top;
                if (TMode.IsNormal)
                {
                    top = s * fa;
                }
                else if (sa <= 0f)
                {
                    top = 0f;
                }
                else if (da <= 0f)
                {
                    top = s * fa;
                }
                else
                {
                    float cs = Math.Clamp(s / sa, 0f, 1f);
                    float cb = Math.Clamp(d / da, 0f, 1f);
                    top = fa * (((1f - da) * s) + (sa * da * TMode.Blend(cb, cs)));
                }

                destination[i + c] = top + (d * fb);
            }

            destination[i + colors] = (sa * fa) + (da * fb);
        }
    }

    /// <summary>Blends one row with the non-separable mode <typeparamref name="TMode"/>. A gray row is treated as r = g = b.</summary>
    public static void NonSeparableRow<TMode>(ReadOnlySpan<float> backdrop, ReadOnlySpan<float> overlay, Span<float> destination, int colors, float opacity, (float A0, float A1, float B0, float B1) f)
        where TMode : struct, INonSeparableBlend
    {
        int channels = colors + 1;
        int pixels = destination.Length / channels;
        backdrop = backdrop[..(pixels * channels)];
        overlay = overlay[..(pixels * channels)];
        destination = destination[..(pixels * channels)];
        for (int p = 0; p < pixels; p++)
        {
            int i = p * channels;
            float sa = overlay[i + colors] * opacity;
            float da = backdrop[i + colors];
            float fa = f.A0 + (f.A1 * da);
            float fb = f.B0 + (f.B1 * sa);
            int g1 = colors == 1 ? 0 : 1, b1 = colors == 1 ? 0 : 2;
            float s0 = overlay[i] * opacity, s1 = overlay[i + g1] * opacity, s2 = overlay[i + b1] * opacity;
            float d0 = backdrop[i], d1 = backdrop[i + g1], d2 = backdrop[i + b1];
            float t0, t1, t2;
            if (sa <= 0f)
            {
                t0 = t1 = t2 = 0f;
            }
            else if (da <= 0f)
            {
                t0 = s0 * fa;
                t1 = s1 * fa;
                t2 = s2 * fa;
            }
            else
            {
                TMode.Blend(
                    Math.Clamp(d0 / da, 0f, 1f), Math.Clamp(d1 / da, 0f, 1f), Math.Clamp(d2 / da, 0f, 1f),
                    Math.Clamp(s0 / sa, 0f, 1f), Math.Clamp(s1 / sa, 0f, 1f), Math.Clamp(s2 / sa, 0f, 1f),
                    out float r, out float g, out float b);
                float k = sa * da;
                t0 = fa * (((1f - da) * s0) + (k * Math.Clamp(r, 0f, 1f)));
                t1 = fa * (((1f - da) * s1) + (k * Math.Clamp(g, 0f, 1f)));
                t2 = fa * (((1f - da) * s2) + (k * Math.Clamp(b, 0f, 1f)));
            }

            destination[i] = t0 + (d0 * fb);
            if (colors == 3)
            {
                destination[i + 1] = t1 + (d1 * fb);
                destination[i + 2] = t2 + (d2 * fb);
            }

            destination[i + colors] = (sa * fa) + (da * fb);
        }
    }

    /// <summary>Blends one row with <paramref name="mode"/>, choosing the specialized loop once per row.</summary>
    public static void Row(BlendMode mode, ReadOnlySpan<float> backdrop, ReadOnlySpan<float> overlay, Span<float> destination, int colors, float opacity, AlphaComposition composition)
    {
        var f = BlendNames.Factors(composition);
        switch (mode)
        {
            case BlendMode.Normal: SeparableRow<NormalBlend>(backdrop, overlay, destination, colors, opacity, f); break;
            case BlendMode.Multiply: SeparableRow<MultiplyBlend>(backdrop, overlay, destination, colors, opacity, f); break;
            case BlendMode.Screen: SeparableRow<ScreenBlend>(backdrop, overlay, destination, colors, opacity, f); break;
            case BlendMode.Overlay: SeparableRow<OverlayBlend>(backdrop, overlay, destination, colors, opacity, f); break;
            case BlendMode.Darken: SeparableRow<DarkenBlend>(backdrop, overlay, destination, colors, opacity, f); break;
            case BlendMode.Lighten: SeparableRow<LightenBlend>(backdrop, overlay, destination, colors, opacity, f); break;
            case BlendMode.ColorDodge: SeparableRow<ColorDodgeBlend>(backdrop, overlay, destination, colors, opacity, f); break;
            case BlendMode.ColorBurn: SeparableRow<ColorBurnBlend>(backdrop, overlay, destination, colors, opacity, f); break;
            case BlendMode.HardLight: SeparableRow<HardLightBlend>(backdrop, overlay, destination, colors, opacity, f); break;
            case BlendMode.SoftLight: SeparableRow<SoftLightBlend>(backdrop, overlay, destination, colors, opacity, f); break;
            case BlendMode.Difference: SeparableRow<DifferenceBlend>(backdrop, overlay, destination, colors, opacity, f); break;
            case BlendMode.Exclusion: SeparableRow<ExclusionBlend>(backdrop, overlay, destination, colors, opacity, f); break;
            case BlendMode.Add: SeparableRow<AddBlend>(backdrop, overlay, destination, colors, opacity, f); break;
            case BlendMode.Subtract: SeparableRow<SubtractBlend>(backdrop, overlay, destination, colors, opacity, f); break;
            case BlendMode.LinearBurn: SeparableRow<LinearBurnBlend>(backdrop, overlay, destination, colors, opacity, f); break;
            case BlendMode.VividLight: SeparableRow<VividLightBlend>(backdrop, overlay, destination, colors, opacity, f); break;
            case BlendMode.LinearLight: SeparableRow<LinearLightBlend>(backdrop, overlay, destination, colors, opacity, f); break;
            case BlendMode.PinLight: SeparableRow<PinLightBlend>(backdrop, overlay, destination, colors, opacity, f); break;
            case BlendMode.HardMix: SeparableRow<HardMixBlend>(backdrop, overlay, destination, colors, opacity, f); break;
            case BlendMode.Divide: SeparableRow<DivideBlend>(backdrop, overlay, destination, colors, opacity, f); break;
            case BlendMode.Hue: NonSeparableRow<HueBlend>(backdrop, overlay, destination, colors, opacity, f); break;
            case BlendMode.Saturation: NonSeparableRow<SaturationBlend>(backdrop, overlay, destination, colors, opacity, f); break;
            case BlendMode.Color: NonSeparableRow<ColorBlend>(backdrop, overlay, destination, colors, opacity, f); break;
            case BlendMode.Luminosity: NonSeparableRow<LuminosityBlend>(backdrop, overlay, destination, colors, opacity, f); break;
            default: throw new ArgumentOutOfRangeException(nameof(mode));
        }
    }
}
