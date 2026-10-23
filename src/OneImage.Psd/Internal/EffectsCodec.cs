using OneImage.Layers;

namespace OneImage.Psd;

/// <summary>
/// What a layer's effects block looked like when it was read: the block, which of its descriptor items became model effects, and which item each effect
/// came from. A save of an edited style uses it to keep the items the model does not carry and to write unchanged effects back as they were.
/// </summary>
internal sealed class FxSource
{
    public PsdBlock? Block { get; set; }

    public PsdLimits Limits { get; init; } = PsdLimits.Default;

    /// <summary>The items that became effects: the key in the root descriptor and the index in its list (-1 for a single item).</summary>
    public HashSet<(string Key, int Index)> Taken { get; } = [];

    public Dictionary<LayerEffect, (string Key, int Index)> Effects { get; } = new(ReferenceEqualityComparer.Instance);
}

/// <summary>
/// Reads the layer-effects block (<c>lfx2</c>) into a <see cref="LayerStyle"/>. Effects it can reproduce are modelled; the others (gradient and
/// pattern overlays or strokes, bevel and emboss, gradient glows, random-noise grain) are counted in <see cref="Result.Skipped"/> so the loader can
/// say the style is not complete. Disabled effects are not modelled at all: they draw nothing, and the block is written back untouched.
/// </summary>
internal static class EffectsCodec
{
    internal readonly record struct Result(LayerStyle? Style, int Skipped, int Approximated, FxSource? Source = null);

    /// <summary>
    /// Reads the "effects visible" switch from the legacy effects block (<c>lrFX</c>), whose common-state item holds it. Absent or unreadable means visible.
    /// </summary>
    public static bool Visible(ReadOnlySpan<byte> legacy)
    {
        // Version (2), item count (2), then items: '8BIM', key, size (4), data. The common state is key cmnS: version (4), visible (1), unused (2).
        if (legacy.Length < 4)
        {
            return true;
        }

        int count = System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(legacy.Slice(2));
        int at = 4;
        for (int i = 0; i < count && at + 12 <= legacy.Length; i++)
        {
            string key = Fourcc.Name(System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(legacy.Slice(at + 4)));
            uint size = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(legacy.Slice(at + 8));
            at += 12;
            if (key == "cmnS")
            {
                return size < 5 || at + 5 > legacy.Length || legacy[at + 4] != 0;
            }

            if (size > legacy.Length - at)
            {
                break;
            }

            at += (int)size;
        }

        return true;
    }

    public static Result Read(ReadOnlySpan<byte> block, PsdLimits limits, ColorConverter colors, bool shapeFromMask, float? globalAngle)
    {
        // Version 0, then the descriptor with its own version (16).
        Descriptor? d;
        try
        {
            d = block.Length < 12 ? null : Descriptor.Parse(block.Slice(8), limits);
        }
        catch (ImageFormatException)
        {
            return default;
        }

        if (d == null || d.Get("masterFXSwitch") is false)
        {
            return default;
        }

        var effects = new List<LayerEffect>();
        var source = new FxSource { Limits = limits };
        int skipped = 0;
        int approximated = 0;
        int seen = 0;
        int seenDisabled = 0;
        foreach ((string key, string kind) in Keys)
        {
            if (d.Get(key) is not { } value)
            {
                continue;
            }

            if (value is List<object?> list)
            {
                for (int i = 0; i < list.Count; i++)
                {
                    if (list[i] is Descriptor item)
                    {
                        Take(item, kind, key, i);
                    }
                }
            }
            else if (value is Descriptor single)
            {
                Take(single, kind, key, -1);
            }
        }

        return new Result(effects.Count > 0 ? new LayerStyle(effects) { ShapeFromMask = shapeFromMask } : null, skipped, approximated, source);

        void Take(Descriptor item, string kind, string key, int index)
        {
            // Disabled effects are modelled too (as disabled ones) so a style survives a round trip as it was; they draw nothing, and one that cannot be
            // read, or that is past the cap, simply stays in the file.
            bool enabled = item.Get("enab") is true;
            if (!enabled)
            {
                if (++seenDisabled > limits.MaxEffectsPerLayer || effects.Count >= LayerStyle.MaxEffects)
                {
                    return;
                }
            }
            else if (++seen > limits.MaxEffectsPerLayer || effects.Count >= LayerStyle.MaxEffects)
            {
                // A hostile or absurd count: nothing past the cap is drawn.
                skipped++;
                return;
            }

            int approximatedBefore = approximated;
            LayerEffect? e = kind switch
            {
                "drop-shadow" => DropShadow(item, colors, globalAngle, ref approximated),
                "inner-shadow" => InnerShadow(item, colors, globalAngle, ref approximated),
                "outer-glow" => OuterGlow(item, colors, ref approximated),
                "inner-glow" => InnerGlow(item, colors, ref approximated),
                "stroke" => Stroke(item, colors),
                "color-overlay" => ColorOverlay(item, colors),
                "satin" => Satin(item, colors),
                _ => null,
            };
            if (!enabled)
            {
                approximated = approximatedBefore;
            }

            if (e == null)
            {
                if (enabled)
                {
                    skipped++;
                }
            }
            else
            {
                effects.Add(e);
                source.Taken.Add((key, index));
                source.Effects[e] = (key, index);
            }
        }
    }

    // Several effects of one kind (the "multi" lists of newer Photoshop) are drawn in file order. The single-effect keys come first.
    private static readonly (string Key, string Kind)[] Keys =
    [
        ("DrSh", "drop-shadow"), ("dropShadowMulti", "drop-shadow"),
        ("OrGl", "outer-glow"),
        ("SoFi", "color-overlay"), ("solidFillMulti", "color-overlay"),
        ("GrFl", "gradient-overlay"), ("gradientFillMulti", "gradient-overlay"),
        ("patternFill", "pattern-overlay"),
        ("ChFX", "satin"),
        ("IrGl", "inner-glow"),
        ("IrSh", "inner-shadow"), ("innerShadowMulti", "inner-shadow"),
        ("FrFX", "stroke"), ("frameFXMulti", "stroke"),
        ("ebbl", "bevel-emboss"),
    ];

    private const float MaxCoordinate = LayerEffect.MaxDistance;

    private static DropShadowEffect? DropShadow(Descriptor d, ColorConverter colors, float? globalAngle, ref int approximated)
    {
        if (!Common(d, colors, out BlendMode blend, out Rgba32 color, out float opacity, ref approximated) ||
            !Angle(d, globalAngle, out float angle) ||
            !Number(d, "Dstn", 0, MaxCoordinate, 0, out float distance) ||
            !Number(d, "Ckmt", 0, 100, 0, out float spread) ||
            !Number(d, "blur", 0, LayerEffect.MaxSize, 0, out float size) ||
            !ReadContour(d.Get("TrnS"), out EffectContour? contour))
        {
            return null;
        }

        return new DropShadowEffect
        {
            Enabled = d.Get("enab") is true, Blend = blend, Color = color, Opacity = opacity, Angle = angle, Distance = distance, Spread = spread / 100f, Size = size, Contour = contour,
            KnockedOut = d.Get("layerConceals") is not false,
        };
    }

    private static InnerShadowEffect? InnerShadow(Descriptor d, ColorConverter colors, float? globalAngle, ref int approximated)
    {
        if (!Common(d, colors, out BlendMode blend, out Rgba32 color, out float opacity, ref approximated) ||
            !Angle(d, globalAngle, out float angle) ||
            !Number(d, "Dstn", 0, MaxCoordinate, 0, out float distance) ||
            !Number(d, "Ckmt", 0, 100, 0, out float choke) ||
            !Number(d, "blur", 0, LayerEffect.MaxSize, 0, out float size) ||
            !ReadContour(d.Get("TrnS"), out EffectContour? contour))
        {
            return null;
        }

        return new InnerShadowEffect { Enabled = d.Get("enab") is true, Blend = blend, Color = color, Opacity = opacity, Angle = angle, Distance = distance, Choke = choke / 100f, Size = size, Contour = contour };
    }

    private static OuterGlowEffect? OuterGlow(Descriptor d, ColorConverter colors, ref int approximated)
    {
        // A glow filled with a gradient (a Grad entry instead of a color) is not reproduced.
        if (d.Get("Grad") != null ||
            !Common(d, colors, out BlendMode blend, out Rgba32 color, out float opacity, ref approximated) ||
            !Number(d, "Ckmt", 0, 100, 0, out float spread) ||
            !Number(d, "blur", 0, LayerEffect.MaxSize, 0, out float size) ||
            !ReadContour(d.Get("TrnS"), out EffectContour? contour))
        {
            return null;
        }

        return Number(d, "Inpr", 0, 100, 50, out float range) ? new OuterGlowEffect { Enabled = d.Get("enab") is true, Blend = blend, Color = color, Opacity = opacity, Spread = spread / 100f, Size = size, Range = range / 100f, Contour = contour } : null;
    }

    private static InnerGlowEffect? InnerGlow(Descriptor d, ColorConverter colors, ref int approximated)
    {
        if (d.Get("Grad") != null ||
            !Common(d, colors, out BlendMode blend, out Rgba32 color, out float opacity, ref approximated) ||
            !Number(d, "Ckmt", 0, 100, 0, out float choke) ||
            !Number(d, "blur", 0, LayerEffect.MaxSize, 0, out float size) ||
            !Number(d, "Inpr", 0, 100, 50, out float range) ||
            !ReadContour(d.Get("TrnS"), out EffectContour? contour))
        {
            return null;
        }

        return new InnerGlowEffect
        {
            Enabled = d.Get("enab") is true, Blend = blend, Color = color, Opacity = opacity, Choke = choke / 100f, Size = size, Range = range / 100f, Contour = contour,
            Source = d.Get("glwS") is "SrcC" ? GlowSource.Center : GlowSource.Edge,
        };
    }

    private static StrokeEffect? Stroke(Descriptor d, ColorConverter colors)
    {
        // Only solid-color strokes; a gradient or pattern stroke keeps today's behavior.
        if (d.Get("PntT") is string paint && paint != "SClr")
        {
            return null;
        }

        int none = 0;
        if (!Common(d, colors, out BlendMode blend, out Rgba32 color, out float opacity, ref none) ||
            !Number(d, "Sz  ", 0, LayerEffect.MaxSize, 1, out float size))
        {
            return null;
        }

        StrokePosition position;
        switch (d.Get("Styl") as string)
        {
            case "OutF":
                position = StrokePosition.Outside;
                break;
            case "InsF":
                position = StrokePosition.Inside;
                break;
            case "CtrF":
                position = StrokePosition.Center;
                break;
            default:
                return null;
        }

        return new StrokeEffect { Enabled = d.Get("enab") is true, Blend = blend, Color = color, Opacity = opacity, Size = size, Position = position };
    }

    private static ColorOverlayEffect? ColorOverlay(Descriptor d, ColorConverter colors)
    {
        int none = 0;
        return Common(d, colors, out BlendMode blend, out Rgba32 color, out float opacity, ref none)
            ? new ColorOverlayEffect { Enabled = d.Get("enab") is true, Blend = blend, Color = color, Opacity = opacity }
            : null;
    }

    private static SatinEffect? Satin(Descriptor d, ColorConverter colors)
    {
        int none = 0;
        if (!Common(d, colors, out BlendMode blend, out Rgba32 color, out float opacity, ref none) ||
            !Number(d, "lagl", -360, 360, 19, out float angle) ||
            !Number(d, "Dstn", 0, MaxCoordinate, 11, out float distance) ||
            !Number(d, "blur", 0, LayerEffect.MaxSize, 14, out float size) ||
            !ReadContour(d.Get("MpgS"), out EffectContour? contour))
        {
            return null;
        }

        return new SatinEffect { Enabled = d.Get("enab") is true, Blend = blend, Color = color, Opacity = opacity, Angle = angle, Distance = distance, Size = size, Inverted = d.Get("Invr") is not false, Contour = contour };
    }

    // Mode, color and opacity, shared by every effect. Grain (random noise) is dropped and counted as an approximation.
    private static bool Common(Descriptor d, ColorConverter colors, out BlendMode blend, out Rgba32 color, out float opacity, ref int approximated)
    {
        blend = BlendMode.Normal;
        color = default;
        opacity = 0;
        if (MapBlend(d.Get("Md  ")) is not { } mode || d.Get("Clr ") is not Descriptor clr || colors.ToRgb(clr) is not { } rgb || !Number(d, "Opct", 0, 100, 100, out float pct))
        {
            return false;
        }

        if (d.Number("Nose") is > 0)
        {
            approximated++;
        }

        blend = mode;
        color = new Rgba32((byte)Math.Round(rgb.R * 255), (byte)Math.Round(rgb.G * 255), (byte)Math.Round(rgb.B * 255), 255);
        opacity = pct / 100f;
        return true;
    }

    // An effect that "uses global light" takes the document's light angle (image resource 1037) instead of the angle stored with it.
    private static bool Angle(Descriptor d, float? globalAngle, out float angle)
    {
        if (d.Get("uglg") is true && globalAngle is { } g)
        {
            angle = g;
            return true;
        }

        return Number(d, "lagl", -360, 360, 120, out angle);
    }

    /// <summary>Reads the document's global light angle (image resource 1037: a signed 32-bit number of degrees), or null.</summary>
    public static float? GlobalAngle(IEnumerable<PsdResource> resources)
    {
        foreach (PsdResource r in resources)
        {
            if (r.Id == 1037 && r.Data.Length >= 4)
            {
                int v = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(r.Data.Span);
                return v >= -360 && v <= 360 ? v : null;
            }
        }

        return null;
    }

    private static bool Number(Descriptor d, string key, double min, double max, double fallback, out float value)
    {
        double v = d.Number(key) ?? fallback;
        value = (float)v;
        return double.IsFinite(v) && v >= min && v <= max;
    }

    // The contour is a list of points on a 0..255 square. Anything that is not a clean increasing curve is not reproduced.
    private static bool ReadContour(object? shape, out EffectContour? contour)
    {
        contour = null;
        if (shape is not Descriptor sh || sh.Get("Crv ") is not List<object?> pts)
        {
            return true;
        }

        if (pts.Count < 2 || pts.Count > 256)
        {
            return false;
        }

        var list = new List<ContourPoint>(pts.Count);
        foreach (object? o in pts)
        {
            // Written so that NaN fails, and compared as floats (two close doubles can become one float).
            if (o is not Descriptor p || p.Number("Hrzn") is not { } x || p.Number("Vrtc") is not { } y || !(x >= 0 && x <= 255 && y >= 0 && y <= 255))
            {
                return false;
            }

            float fx = (float)(x / 255.0);
            if (list.Count > 0 && !(fx > list[^1].X))
            {
                return false;
            }

            list.Add(new ContourPoint(fx, (float)(y / 255.0)));
        }

        var c = new EffectContour(list);
        contour = c.IsLinear ? null : c;
        return true;
    }

    private static readonly (string Name, BlendMode Mode)[] BlendNames =
    [
        ("Nrml", BlendMode.Normal), ("normal", BlendMode.Normal),
        ("Mltp", BlendMode.Multiply), ("multiply", BlendMode.Multiply),
        ("Scrn", BlendMode.Screen), ("screen", BlendMode.Screen),
        ("Ovrl", BlendMode.Overlay), ("overlay", BlendMode.Overlay),
        ("Drkn", BlendMode.Darken), ("darken", BlendMode.Darken),
        ("Lghn", BlendMode.Lighten), ("lighten", BlendMode.Lighten),
        ("CDdg", BlendMode.ColorDodge), ("colorDodge", BlendMode.ColorDodge),
        ("CBrn", BlendMode.ColorBurn), ("colorBurn", BlendMode.ColorBurn),
        ("HrdL", BlendMode.HardLight), ("HdLg", BlendMode.HardLight), ("hardLight", BlendMode.HardLight),
        ("SftL", BlendMode.SoftLight), ("SfLg", BlendMode.SoftLight), ("softLight", BlendMode.SoftLight),
        ("Dfrn", BlendMode.Difference), ("difference", BlendMode.Difference),
        ("Xclu", BlendMode.Exclusion), ("exclusion", BlendMode.Exclusion),
        ("linearDodge", BlendMode.LinearDodge), ("LnrD", BlendMode.LinearDodge), ("LnDg", BlendMode.LinearDodge),
        ("linearBurn", BlendMode.LinearBurn), ("LnrB", BlendMode.LinearBurn), ("LnBr", BlendMode.LinearBurn),
        ("vividLight", BlendMode.VividLight), ("VvdL", BlendMode.VividLight), ("VvLg", BlendMode.VividLight),
        ("linearLight", BlendMode.LinearLight), ("LnrL", BlendMode.LinearLight), ("LnLg", BlendMode.LinearLight),
        ("pinLight", BlendMode.PinLight), ("PnLg", BlendMode.PinLight), ("PinL", BlendMode.PinLight),
        ("hardMix", BlendMode.HardMix), ("HrdM", BlendMode.HardMix), ("HrMx", BlendMode.HardMix),
        ("Hue ", BlendMode.Hue), ("H   ", BlendMode.Hue), ("hue", BlendMode.Hue),
        ("Strt", BlendMode.Saturation), ("saturation", BlendMode.Saturation),
        ("Clr ", BlendMode.Color), ("color", BlendMode.Color),
        ("Lmns", BlendMode.Luminosity), ("luminosity", BlendMode.Luminosity),
        ("blendSubtraction", BlendMode.Subtract), ("Sbtr", BlendMode.Subtract),
        ("blendDivide", BlendMode.Divide), ("Dvd ", BlendMode.Divide),
    ];

    // Dissolve and the darker/lighter color modes have no equivalent; an effect that uses one is not reproduced.
    private static BlendMode? MapBlend(object? v)
    {
        if (v is string s)
        {
            foreach ((string name, BlendMode mode) in BlendNames)
            {
                if (name == s)
                {
                    return mode;
                }
            }
        }

        return null;
    }
}
