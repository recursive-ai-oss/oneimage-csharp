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

    public static Result Read(ReadOnlySpan<byte> block, PsdLimits limits, ColorConverter colors, bool shapeFromMask, float? globalAngle, PatternCache? patterns = null, Func<ImageRect?>? alignBox = null)
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

        return new Result(effects.Count > 0 ? new LayerStyle(effects) { ShapeFromMask = shapeFromMask, AlignBox = effects.Any(NeedsBox) ? alignBox?.Invoke() : null } : null, skipped, approximated, source);

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
                "stroke" => Stroke(item, colors, patterns),
                "color-overlay" => ColorOverlay(item, colors),
                "gradient-overlay" => GradientOverlay(item, colors),
                "pattern-overlay" => PatternOverlay(item, patterns),
                "bevel-emboss" => BevelEmboss(item, colors, globalAngle, ref approximated),
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

    // Gradients and patterns that are laid out on the layer need to know its shape's bounds.
    private static bool NeedsBox(LayerEffect e) => e switch
    {
        GradientOverlayEffect g => g.Fill.AlignWithLayer,
        PatternOverlayEffect p => p.Fill.LinkWithLayer,
        StrokeEffect s => (s.Gradient is { AlignWithLayer: true }) || (s.PatternFill is { LinkWithLayer: true }),
        _ => false,
    };

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
        if (!Glow(d, colors, out BlendMode blend, out Rgba32 color, out float opacity, out EffectGradient? gradient, ref approximated) ||
            !Number(d, "Ckmt", 0, 100, 0, out float spread) ||
            !Number(d, "blur", 0, LayerEffect.MaxSize, 0, out float size) ||
            !ReadContour(d.Get("TrnS"), out EffectContour? contour))
        {
            return null;
        }

        return Number(d, "Inpr", 0, 100, 50, out float range) ? new OuterGlowEffect { Enabled = d.Get("enab") is true, Blend = blend, Color = color, Opacity = opacity, Spread = spread / 100f, Size = size, Range = range / 100f, Contour = contour, Gradient = gradient } : null;
    }

    private static InnerGlowEffect? InnerGlow(Descriptor d, ColorConverter colors, ref int approximated)
    {
        if (!Glow(d, colors, out BlendMode blend, out Rgba32 color, out float opacity, out EffectGradient? gradient, ref approximated) ||
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
            Source = d.Get("glwS") is "SrcC" ? GlowSource.Center : GlowSource.Edge, Gradient = gradient,
        };
    }

    private static StrokeEffect? Stroke(Descriptor d, ColorConverter colors, PatternCache? patterns)
    {
        // The paint: a solid color, a gradient or a pattern.
        EffectGradientFill? gradient = null;
        EffectPatternFill? pattern = null;
        if (d.Get("PntT") is string paint && paint != "SClr")
        {
            if (paint == "GrFl")
            {
                gradient = ReadGradientFill(d, colors, allowShapeBurst: true);
                if (gradient == null)
                {
                    return null;
                }
            }
            else if (paint == "Ptrn")
            {
                pattern = ReadPatternFill(d, patterns, linkKey: "Lnkd");
                if (pattern == null)
                {
                    return null;
                }
            }
            else
            {
                return null;
            }
        }

        int none = 0;
        if (!Common(d, colors, out BlendMode blend, out Rgba32 color, out float opacity, ref none, needColor: gradient == null && pattern == null) ||
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

        return new StrokeEffect { Enabled = d.Get("enab") is true, Blend = blend, Color = color, Opacity = opacity, Size = size, Position = position, Gradient = gradient, PatternFill = pattern };
    }

    private static GradientOverlayEffect? GradientOverlay(Descriptor d, ColorConverter colors)
    {
        int none = 0;
        if (!Common(d, colors, out BlendMode blend, out _, out float opacity, ref none, needColor: false) || ReadGradientFill(d, colors, allowShapeBurst: false) is not { } fill)
        {
            return null;
        }

        return new GradientOverlayEffect { Enabled = d.Get("enab") is true, Blend = blend, Opacity = opacity, Fill = fill };
    }

    private static PatternOverlayEffect? PatternOverlay(Descriptor d, PatternCache? patterns)
    {
        int none = 0;
        if (!Common(d, null!, out BlendMode blend, out _, out float opacity, ref none, needColor: false) || ReadPatternFill(d, patterns, linkKey: "Algn") is not { } fill)
        {
            return null;
        }

        return new PatternOverlayEffect { Enabled = d.Get("enab") is true, Blend = blend, Opacity = opacity, Fill = fill };
    }

    // The gradient of a glow: a Grad entry instead of a color.
    private static bool Glow(Descriptor d, ColorConverter colors, out BlendMode blend, out Rgba32 color, out float opacity, out EffectGradient? gradient, ref int approximated)
    {
        gradient = null;
        if (d.Get("Grad") is Descriptor g)
        {
            if (ReadGradient(g, d, colors) is not { } gr)
            {
                blend = default;
                color = default;
                opacity = 0;
                return false;
            }

            gradient = gr;
            return Common(d, colors, out blend, out color, out opacity, ref approximated, needColor: false);
        }

        return Common(d, colors, out blend, out color, out opacity, ref approximated);
    }

    // Custom (stop) gradients only: noise gradients come from Photoshop's own random number generator.
    private static EffectGradient? ReadGradient(Descriptor grad, Descriptor owner, ColorConverter colors)
    {
        if (grad.Get("GrdF") is not string form || form != "CstS" ||
            grad.Get("Clrs") is not List<object?> clrs || clrs.Count == 0 || clrs.Count > EffectGradient.MaxStops ||
            grad.Get("Trns") is not List<object?> trns || trns.Count == 0 || trns.Count > EffectGradient.MaxStops)
        {
            return null;
        }

        // Perceptual interpolation is another curve that is not reproduced; classic and the newer "smooth" name blend encoded values, linear blends light.
        bool linear = false;
        if (owner.Get("gradientsInterpolationMethod") is string method)
        {
            if (method == "Lnr ")
            {
                linear = true;
            }
            else if (method is not ("Gcls" or "Smoo"))
            {
                return null;
            }
        }

        var cs = new List<GradientColorStop>(clrs.Count);
        foreach (object? o in clrs)
        {
            if (o is not Descriptor stop || stop.Get("Type") is not string type || type != "UsrS" || stop.Get("Clr ") is not Descriptor clr ||
                stop.Number("Lctn") is not { } loc || colors.ToRgb(clr) is not { } rgb || !(loc >= 0 && loc <= 4096))
            {
                return null;
            }

            cs.Add(new GradientColorStop((float)(loc / 4096.0), (float)Math.Clamp((stop.Number("Mdpn") ?? 50) / 100.0, 0, 1), new RgbaFloat(rgb.R, rgb.G, rgb.B, 1f)));
        }

        var os = new List<GradientOpacityStop>(trns.Count);
        foreach (object? o in trns)
        {
            if (o is not Descriptor stop || stop.Number("Opct") is not { } opct || stop.Number("Lctn") is not { } loc || !(loc >= 0 && loc <= 4096))
            {
                return null;
            }

            os.Add(new GradientOpacityStop((float)(loc / 4096.0), (float)Math.Clamp((stop.Number("Mdpn") ?? 50) / 100.0, 0, 1), (float)Math.Clamp(opct / 100.0, 0, 1)));
        }

        return new EffectGradient(cs, os, (float)Math.Clamp((grad.Number("Intr") ?? 4096) / 4096.0, 0, 1), linear);
    }

    private static EffectGradientFill? ReadGradientFill(Descriptor d, ColorConverter colors, bool allowShapeBurst)
    {
        if (d.Get("Grad") is not Descriptor grad || ReadGradient(grad, d, colors) is not { } gradient)
        {
            return null;
        }

        EffectGradientStyle style;
        switch (d.Get("Type") as string)
        {
            case "Lnr ":
                style = EffectGradientStyle.Linear;
                break;
            case "Rdl ":
                style = EffectGradientStyle.Radial;
                break;
            case "Angl":
                style = EffectGradientStyle.Angle;
                break;
            case "Rflc":
                style = EffectGradientStyle.Reflected;
                break;
            case "Dmnd":
                style = EffectGradientStyle.Diamond;
                break;
            case "shapeburst" when allowShapeBurst:
                style = EffectGradientStyle.ShapeBurst;
                break;
            default:
                return null;
        }

        double dx = 0;
        double dy = 0;
        if (d.Get("Ofst") is Descriptor ofst)
        {
            dx = ofst.Number("Hrzn") ?? 0;
            dy = ofst.Number("Vrtc") ?? 0;
        }

        double angle = d.Number("Angl") ?? 90;
        double scale = (d.Number("Scl ") ?? 100) / 100.0;
        if (!(Math.Abs(angle) <= 3600) || !(scale >= 0.1 && scale <= 10) || !(Math.Abs(dx) <= 10000) || !(Math.Abs(dy) <= 10000))
        {
            return null;
        }

        return new EffectGradientFill(gradient, style, (float)angle, (float)scale, d.Get("Rvrs") is true, d.Get("Algn") is not false, (float)(dx / 100.0), (float)(dy / 100.0));
    }

    private static EffectPatternFill? ReadPatternFill(Descriptor d, PatternCache? patterns, string linkKey)
    {
        if (patterns == null || d.Get("Ptrn") is not Descriptor ptrn || ptrn.Get("Idnt") is not string id)
        {
            return null;
        }

        id = id.TrimEnd('\0');
        PatternImage? image = patterns.Find(id);
        if (image == null)
        {
            return null;
        }

        double scale = (d.Number("Scl ") ?? 100) / 100.0;
        double px = 0;
        double py = 0;
        if (d.Get("phase") is Descriptor phase)
        {
            px = phase.Number("Hrzn") ?? 0;
            py = phase.Number("Vrtc") ?? 0;
        }

        if (!(scale >= 0.01 && scale <= 10) || !(Math.Abs(px) <= LayerEffect.MaxDistance) || !(Math.Abs(py) <= LayerEffect.MaxDistance))
        {
            return null;
        }

        return new EffectPatternFill(new EffectPattern(image.Width, image.Height, image.Rgba, id), (float)scale, d.Get(linkKey) is not false, (float)px, (float)py);
    }

    private static BevelEmbossEffect? BevelEmboss(Descriptor d, ColorConverter colors, float? globalAngle, ref int approximated)
    {
        if (MapBlend(d.Get("hglM")) is not { } hMode || MapBlend(d.Get("sdwM")) is not { } sMode ||
            d.Get("hglC") is not Descriptor hc || colors.ToRgb(hc) is not { } hRgb ||
            d.Get("sdwC") is not Descriptor sc || colors.ToRgb(sc) is not { } sRgb ||
            !Number(d, "hglO", 0, 100, 75, out float hOpacity) || !Number(d, "sdwO", 0, 100, 75, out float sOpacity) ||
            !Angle(d, globalAngle, out float angle) ||
            !Number(d, "Lald", 0, 90, 30, out float altitude) ||
            !Number(d, "srgR", 0, BevelEmbossEffect.MaxDepth * 100, 100, out float depth) ||
            !Number(d, "blur", 0, LayerEffect.MaxSize, 5, out float size) ||
            !Number(d, "Sftn", 0, BevelEmbossEffect.MaxSoften, 0, out float soften) ||
            !ReadContour(d.Get("TrnS"), out EffectContour? gloss))
        {
            return null;
        }

        BevelStyle style;
        switch (d.Get("bvlS") as string)
        {
            case "InrB":
                style = BevelStyle.InnerBevel;
                break;
            case "OtrB":
                style = BevelStyle.OuterBevel;
                break;
            case "Embs":
                style = BevelStyle.Emboss;
                break;
            case "PlEb":
                style = BevelStyle.PillowEmboss;
                break;
            case "strokeEmboss":
                style = BevelStyle.StrokeEmboss;
                break;
            default:
                return null;
        }

        BevelTechnique technique;
        switch (d.Get("bvlT") as string)
        {
            case "SfBL":
                technique = BevelTechnique.Smooth;
                break;
            case "PrBL":
                technique = BevelTechnique.ChiselHard;
                break;
            case "Slmt":
                technique = BevelTechnique.ChiselSoft;
                break;
            default:
                return null;
        }

        // The bevel contour ("use shape") and the texture are not modelled; the effect is drawn without them.
        if (d.Get("useShape") is true || d.Get("useTexture") is true)
        {
            approximated++;
        }

        return new BevelEmbossEffect
        {
            Enabled = d.Get("enab") is true, Style = style, Technique = technique, Depth = depth / 100f, Up = (d.Get("bvlD") as string)?.TrimEnd() is not "Out",
            Size = size, Soften = soften, Angle = angle, Altitude = altitude,
            HighlightBlend = hMode, HighlightColor = new Rgba32((byte)Math.Round(hRgb.R * 255), (byte)Math.Round(hRgb.G * 255), (byte)Math.Round(hRgb.B * 255), 255), HighlightOpacity = hOpacity / 100f,
            ShadowBlend = sMode, ShadowColor = new Rgba32((byte)Math.Round(sRgb.R * 255), (byte)Math.Round(sRgb.G * 255), (byte)Math.Round(sRgb.B * 255), 255), ShadowOpacity = sOpacity / 100f,
            GlossContour = gloss,
        };
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
    private static bool Common(Descriptor d, ColorConverter colors, out BlendMode blend, out Rgba32 color, out float opacity, ref int approximated, bool needColor = true)
    {
        blend = BlendMode.Normal;
        color = default;
        opacity = 0;
        (float R, float G, float B) rgb = default;
        if (MapBlend(d.Get("Md  ")) is not { } mode || !Number(d, "Opct", 0, 100, 100, out float pct))
        {
            return false;
        }

        // A gradient or pattern paint has no single color; a stroke still stores one that is not used.
        if (d.Get("Clr ") is Descriptor clr && colors?.ToRgb(clr) is { } found)
        {
            rgb = found;
        }
        else if (needColor)
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
