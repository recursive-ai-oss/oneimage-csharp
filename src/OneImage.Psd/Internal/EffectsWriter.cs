using OneImage.Layers;

namespace OneImage.Psd;

/// <summary>
/// Writes a layer style as an effects block (<c>lfx2</c>): version 0, descriptor version 16, and the descriptor Photoshop keeps its effects in. When the
/// style replaces one that was read, the items of the original that the model does not carry (gradient and pattern overlays, bevel and emboss, gradient
/// strokes and glows, an effect that could not be read) stay where they were, and effects that are still the very objects that were read are written back
/// as they were, including the details the model has no field for (grain, anti-aliasing, glow technique).
/// </summary>
internal static class EffectsWriter
{
    // Root keys in the order Photoshop writes them. A kind has a single-item key and, for most kinds, a key for a list of items; Photoshop uses the list
    // form once a layer has several effects of a kind.
    private static readonly (string Kind, string Single, string? Multi)[] Kinds =
    [
        ("drop-shadow", "DrSh", "dropShadowMulti"),
        ("inner-shadow", "IrSh", "innerShadowMulti"),
        ("outer-glow", "OrGl", null),
        ("color-overlay", "SoFi", "solidFillMulti"),
        ("gradient-overlay", "GrFl", "gradientFillMulti"),
        ("pattern-overlay", "patternFill", null),
        ("stroke", "FrFX", "frameFXMulti"),
        ("inner-glow", "IrGl", null),
        ("bevel-emboss", "ebbl", null),
        ("satin", "ChFX", null),
    ];

    // Where a key sits among the root items, so a kind that is new to a block lands in Photoshop's order.
    private static readonly string[][] Rank =
    [
        ["Scl "], ["masterFXSwitch"], ["DrSh", "dropShadowMulti"], ["IrSh", "innerShadowMulti"], ["OrGl"], ["SoFi", "solidFillMulti"],
        ["GrFl", "gradientFillMulti"], ["patternFill"], ["FrFX", "frameFXMulti"], ["IrGl"], ["ebbl"], ["ChFX"], ["numModifyingFX"],
    ];

    /// <summary>Returns the data of the effects block for <paramref name="style"/>, or null when there is nothing to store.</summary>
    /// <exception cref="ImageOperationException">The style holds more than one effect of a kind that PSD stores once.</exception>
    public static byte[]? Write(LayerStyle? style, FxSource? source, List<EffectPattern>? patterns = null)
    {
        DescNode root = StartFrom(source, source?.Limits ?? PsdLimits.Default, out Dictionary<(string, int), DescNode> originals);
        foreach ((string kind, string single, string? multi) in Kinds)
        {
            ApplyKind(root, kind, single, multi, style, source, originals, patterns);
        }

        int enabled = 0;
        int items = 0;
        foreach (KeyValuePair<string, DescNode> kv in root.Items)
        {
            if (kv.Key is "Scl " or "masterFXSwitch" or "numModifyingFX")
            {
                continue;
            }

            List<DescNode> members = kv.Value.Type == "VlLs" ? kv.Value.List : [kv.Value];
            foreach (DescNode m in members)
            {
                items++;
                if (m.Get("enab") is { Type: "bool", Bool: true })
                {
                    enabled++;
                }
            }
        }

        if (items == 0)
        {
            return null;
        }

        int counter = root.IndexOf("numModifyingFX");
        if (counter >= 0)
        {
            root.Items[counter] = new("numModifyingFX", DescNode.Long_(enabled));
        }

        var ms = new MemoryStream();
        ms.Write([0, 0, 0, 0, 0, 0, 0, 16]);
        root.Write(ms);
        return ms.ToArray();
    }

    private static DescNode StartFrom(FxSource? source, PsdLimits limits, out Dictionary<(string, int), DescNode> originals)
    {
        originals = [];
        if (source?.Block is { } block && block.Data.Length >= 12)
        {
            try
            {
                DescNode parsed = DescNode.Parse(block.Data.Span.Slice(8), limits);
                if (parsed.Get("masterFXSwitch") is not { Type: "bool", Bool: false })
                {
                    foreach (KeyValuePair<string, DescNode> kv in parsed.Items)
                    {
                        if (kv.Value.Type == "VlLs")
                        {
                            for (int i = 0; i < kv.Value.List.Count; i++)
                            {
                                originals[(kv.Key, i)] = kv.Value.List[i];
                            }
                        }
                        else
                        {
                            originals[(kv.Key, -1)] = kv.Value;
                        }
                    }

                    return parsed;
                }
            }
            catch (ImageFormatException)
            {
                // Unreadable now means it was unreadable when the style was read, and nothing of it can be kept.
            }
            catch (ImageLimitException)
            {
            }
        }

        originals.Clear();
        return new DescNode { Type = "Objc", Name = DescNode.CountedString("", terminator: true), ClassId = "null" }
            .Add("Scl ", DescNode.Unitf("#Prc", 100))
            .Add("masterFXSwitch", DescNode.Bool_(true));
    }

    private static void ApplyKind(DescNode root, string kind, string single, string? multi, LayerStyle? style, FxSource? source, Dictionary<(string, int), DescNode> originals, List<EffectPattern>? patterns)
    {
        // The items of the original that stay: whatever the model did not take.
        var final = new List<DescNode>();
        string[] keys = multi == null ? [single] : [single, multi];
        foreach (string key in keys)
        {
            if (root.Get(key) is not { } node)
            {
                continue;
            }

            if (node.Type == "VlLs")
            {
                for (int i = 0; i < node.List.Count; i++)
                {
                    if (source == null || !source.Taken.Contains((key, i)))
                    {
                        final.Add(node.List[i]);
                    }
                }
            }
            else if (source == null || !source.Taken.Contains((key, -1)))
            {
                final.Add(node);
            }
        }

        var fresh = new List<(LayerEffect Effect, DescNode Node, bool FromFile)>();
        if (style != null)
        {
            foreach (LayerEffect e in style.Effects)
            {
                if (e.Name != kind)
                {
                    continue;
                }

                if (source != null && source.Effects.TryGetValue(e, out (string Key, int Index) id) && originals.TryGetValue(id, out DescNode? original))
                {
                    fresh.Add((e, original, true));
                }
                else
                {
                    fresh.Add((e, Build(e, patterns), false));
                }
            }
        }

        if (multi == null && fresh.Count + final.Count > 1)
        {
            // Photoshop keeps one outer glow, inner glow and satin per layer. A disabled one that came from the file is only the placeholder it writes for
            // every kind, so it gives way to an effect that was added.
            var added = fresh.Where(static f => !f.FromFile || f.Effect.Enabled).ToList();
            if (added.Count > 0 && added.Count < fresh.Count)
            {
                fresh = added;
            }

            if (fresh.Count + final.Count > 1)
            {
                throw new ImageOperationException($"A PSD layer holds one {kind} effect, but the layer's style has {fresh.Count + final.Count}.");
            }
        }

        foreach ((_, DescNode node, _) in fresh)
        {
            final.Add(node);
        }

        // Where the kind goes: where its first key was, else in Photoshop's order.
        int at = root.IndexOf(single);
        int atMulti = multi == null ? -1 : root.IndexOf(multi);
        bool hadMulti = atMulti >= 0;
        bool hadSingle = at >= 0;
        int position = at >= 0 && atMulti >= 0 ? Math.Min(at, atMulti) : Math.Max(at, atMulti);
        root.Items.RemoveAll(kv => kv.Key == single || kv.Key == multi);
        if (final.Count == 0)
        {
            return;
        }

        if (position < 0)
        {
            position = InsertionPoint(root, single);
        }

        bool asList = multi != null && (final.Count > 1 || (hadMulti && !hadSingle));
        if (asList)
        {
            DescNode list = DescNode.ListOf();
            list.List.AddRange(final);
            root.Items.Insert(position, new(multi!, list));
        }
        else
        {
            root.Items.Insert(position, new(single, final[0]));
        }
    }

    private static int InsertionPoint(DescNode root, string key)
    {
        int mine = RankOf(key);
        for (int i = 0; i < root.Items.Count; i++)
        {
            if (RankOf(root.Items[i].Key) > mine)
            {
                return i;
            }
        }

        return root.Items.Count;
    }

    private static int RankOf(string key)
    {
        for (int i = 0; i < Rank.Length; i++)
        {
            if (Array.IndexOf(Rank[i], key) >= 0)
            {
                return i;
            }
        }

        return int.MaxValue;
    }

    // The descriptor of one effect, with the items Photoshop writes for its kind.
    private static DescNode Build(LayerEffect e, List<EffectPattern>? patterns)
    {
        switch (e)
        {
            case DropShadowEffect s:
                return Begin("DrSh", s)
                    .Add("Md  ", Blend(s.Blend)).Add("Clr ", Color(s.Color)).Add("Opct", Percent(s.Opacity))
                    .Add("uglg", DescNode.Bool_(false)).Add("lagl", Angle(s.Angle))
                    .Add("Dstn", Pixels(s.Distance)).Add("Ckmt", Pixels(s.Spread * 100f)).Add("blur", Pixels(s.Size))
                    .Add("Nose", DescNode.Unitf("#Prc", 0)).Add("AntA", DescNode.Bool_(false)).Add("TrnS", Contour(s.Contour))
                    .Add("layerConceals", DescNode.Bool_(s.KnockedOut));
            case InnerShadowEffect s:
                return Begin("IrSh", s)
                    .Add("Md  ", Blend(s.Blend)).Add("Clr ", Color(s.Color)).Add("Opct", Percent(s.Opacity))
                    .Add("uglg", DescNode.Bool_(false)).Add("lagl", Angle(s.Angle))
                    .Add("Dstn", Pixels(s.Distance)).Add("Ckmt", Pixels(s.Choke * 100f)).Add("blur", Pixels(s.Size))
                    .Add("Nose", DescNode.Unitf("#Prc", 0)).Add("AntA", DescNode.Bool_(false)).Add("TrnS", Contour(s.Contour));
            case OuterGlowEffect g:
                return InterpolationOf(Begin("OrGl", g)
                    .Add("Md  ", Blend(g.Blend)).Add(g.Gradient == null ? "Clr " : "Grad", g.Gradient == null ? Color(g.Color) : Gradient(g.Gradient)).Add("Opct", Percent(g.Opacity))
                    .Add("GlwT", DescNode.Enum("BETE", "SfBL")).Add("Ckmt", Pixels(g.Spread * 100f)).Add("blur", Pixels(g.Size))
                    .Add("Nose", DescNode.Unitf("#Prc", 0)).Add("ShdN", DescNode.Unitf("#Prc", 0)).Add("AntA", DescNode.Bool_(false))
                    .Add("TrnS", Contour(g.Contour)).Add("Inpr", DescNode.Unitf("#Prc", Pct(g.Range))), g.Gradient);
            case InnerGlowEffect g:
                return InterpolationOf(Begin("IrGl", g)
                    .Add("Md  ", Blend(g.Blend)).Add(g.Gradient == null ? "Clr " : "Grad", g.Gradient == null ? Color(g.Color) : Gradient(g.Gradient)).Add("Opct", Percent(g.Opacity))
                    .Add("GlwT", DescNode.Enum("BETE", "SfBL")).Add("Ckmt", Pixels(g.Choke * 100f)).Add("blur", Pixels(g.Size))
                    .Add("Nose", DescNode.Unitf("#Prc", 0)).Add("ShdN", DescNode.Unitf("#Prc", 0)).Add("AntA", DescNode.Bool_(false))
                    .Add("TrnS", Contour(g.Contour)).Add("Inpr", DescNode.Unitf("#Prc", Pct(g.Range)))
                    .Add("glwS", DescNode.Enum("IGSr", g.Source == GlowSource.Center ? "SrcC" : "SrcE")), g.Gradient);
            case StrokeEffect k:
                {
                    DescNode d = Begin("FrFX", k)
                        .Add("Styl", DescNode.Enum("FStl", k.Position switch { StrokePosition.Inside => "InsF", StrokePosition.Center => "CtrF", _ => "OutF" }))
                        .Add("PntT", DescNode.Enum("FrFl", k.Gradient != null ? "GrFl" : k.PatternFill != null ? "Ptrn" : "SClr")).Add("Md  ", Blend(k.Blend)).Add("Opct", Percent(k.Opacity))
                        .Add("Sz  ", Pixels(k.Size)).Add("Clr ", Color(k.Color));
                    if (k.Gradient != null)
                    {
                        AddGradientFill(d, k.Gradient);
                    }
                    else if (k.PatternFill != null)
                    {
                        d.Add("Ptrn", PatternRef(k.PatternFill.Pattern, patterns)).Add("Scl ", Percent(k.PatternFill.Scale)).Add("Lnkd", DescNode.Bool_(k.PatternFill.LinkWithLayer))
                            .Add("phase", Obj("Pnt ").Add("Hrzn", DescNode.Unitf("#Prc", Exact(k.PatternFill.PhaseX))).Add("Vrtc", DescNode.Unitf("#Prc", Exact(k.PatternFill.PhaseY))));
                    }

                    return d.Add("overprint", DescNode.Bool_(false));
                }

            case GradientOverlayEffect go:
                {
                    DescNode d = Begin("GrFl", go).Add("Md  ", Blend(go.Blend)).Add("Opct", Percent(go.Opacity));
                    AddGradientFill(d, go.Fill);
                    return d;
                }

            case PatternOverlayEffect po:
                return Begin("patternFill", po).Add("Md  ", Blend(po.Blend)).Add("Opct", Percent(po.Opacity))
                    .Add("Ptrn", PatternRef(po.Fill.Pattern, patterns)).Add("Scl ", Percent(po.Fill.Scale)).Add("Algn", DescNode.Bool_(po.Fill.LinkWithLayer))
                    .Add("phase", Obj("Pnt ").Add("Hrzn", DescNode.Dbl(Exact(po.Fill.PhaseX))).Add("Vrtc", DescNode.Dbl(Exact(po.Fill.PhaseY))));
            case BevelEmbossEffect b:
                return Begin("ebbl", b)
                    .Add("hglM", Blend(b.HighlightBlend)).Add("hglC", Color(b.HighlightColor)).Add("hglO", Percent(b.HighlightOpacity * b.Opacity))
                    .Add("sdwM", Blend(b.ShadowBlend)).Add("sdwC", Color(b.ShadowColor)).Add("sdwO", Percent(b.ShadowOpacity * b.Opacity))
                    .Add("bvlT", DescNode.Enum("bvlT", b.Technique switch { BevelTechnique.ChiselHard => "PrBL", BevelTechnique.ChiselSoft => "Slmt", _ => "SfBL" }))
                    .Add("bvlS", DescNode.Enum("BESl", b.Style switch { BevelStyle.OuterBevel => "OtrB", BevelStyle.Emboss => "Embs", BevelStyle.PillowEmboss => "PlEb", BevelStyle.StrokeEmboss => "strokeEmboss", _ => "InrB" }))
                    .Add("uglg", DescNode.Bool_(false)).Add("lagl", Angle(b.Angle)).Add("Lald", DescNode.Unitf("#Ang", Exact(b.Altitude)))
                    .Add("srgR", Percent(b.Depth)).Add("blur", Pixels(b.Size)).Add("bvlD", DescNode.Enum("BESs", b.Up ? "In  " : "Out "))
                    .Add("TrnS", Contour(b.GlossContour)).Add("antialiasGloss", DescNode.Bool_(false)).Add("Sftn", Pixels(b.Soften))
                    .Add("useShape", DescNode.Bool_(false)).Add("useTexture", DescNode.Bool_(false));
            case ColorOverlayEffect o:
                return Begin("SoFi", o).Add("Md  ", Blend(o.Blend)).Add("Clr ", Color(o.Color)).Add("Opct", Percent(o.Opacity));
            case SatinEffect t:
                return Begin("ChFX", t)
                    .Add("Md  ", Blend(t.Blend)).Add("Clr ", Color(t.Color)).Add("AntA", DescNode.Bool_(true)).Add("Invr", DescNode.Bool_(t.Inverted))
                    .Add("Opct", Percent(t.Opacity)).Add("lagl", Angle(t.Angle)).Add("Dstn", Pixels(t.Distance)).Add("blur", Pixels(t.Size))
                    .Add("MpgS", Contour(t.Contour));
            default:
                throw new ImageOperationException($"The layer effect '{e.Name}' has no PSD equivalent.");
        }
    }

    // A glow gradient blends in linear light when the descriptor says so, as a gradient overlay does.
    private static DescNode InterpolationOf(DescNode d, EffectGradient? gradient) =>
        gradient is { LinearLight: true } ? d.Add("gradientsInterpolationMethod", DescNode.Enum("gradientInterpolationMethodType", "Lnr ")) : d;

    // The settings shared by gradient overlays and gradient strokes.
    private static void AddGradientFill(DescNode d, EffectGradientFill fill)
    {
        d.Add("Grad", Gradient(fill.Gradient));
        if (fill.Gradient.LinearLight)
        {
            d.Add("gradientsInterpolationMethod", DescNode.Enum("gradientInterpolationMethodType", "Lnr "));
        }

        d.Add("Angl", Angle(fill.Angle))
            .Add("Type", DescNode.Enum("GrdT", fill.Style switch
            {
                EffectGradientStyle.Radial => "Rdl ",
                EffectGradientStyle.Angle => "Angl",
                EffectGradientStyle.Reflected => "Rflc",
                EffectGradientStyle.Diamond => "Dmnd",
                EffectGradientStyle.ShapeBurst => "shapeburst",
                _ => "Lnr ",
            }))
            .Add("Rvrs", DescNode.Bool_(fill.Reverse)).Add("Dthr", DescNode.Bool_(false)).Add("Algn", DescNode.Bool_(fill.AlignWithLayer))
            .Add("Scl ", Percent(fill.Scale))
            .Add("Ofst", Obj("Pnt ").Add("Hrzn", Percent(fill.OffsetX)).Add("Vrtc", Percent(fill.OffsetY)));
    }

    private static DescNode Gradient(EffectGradient g)
    {
        DescNode colors = DescNode.ListOf();
        foreach (GradientColorStop c in g.Colors)
        {
            colors.List.Add(Obj("Clrt")
                .Add("Clr ", Obj("RGBC").Add("Rd  ", DescNode.Dbl(Channel(c.Color.R))).Add("Grn ", DescNode.Dbl(Channel(c.Color.G))).Add("Bl  ", DescNode.Dbl(Channel(c.Color.B))))
                .Add("Type", DescNode.Enum("Clry", "UsrS")).Add("Lctn", DescNode.Long_((long)Math.Round(c.Location * 4096.0))).Add("Mdpn", DescNode.Long_((long)Math.Round(c.Midpoint * 100.0))));
        }

        DescNode opacities = DescNode.ListOf();
        foreach (GradientOpacityStop o in g.Opacities)
        {
            opacities.List.Add(Obj("TrnS").Add("Opct", Percent(o.Opacity)).Add("Lctn", DescNode.Long_((long)Math.Round(o.Location * 4096.0))).Add("Mdpn", DescNode.Long_((long)Math.Round(o.Midpoint * 100.0))));
        }

        return Obj("Grdn").Add("Nm  ", DescNode.Str("Custom")).Add("GrdF", DescNode.Enum("GrdF", "CstS")).Add("Intr", DescNode.Dbl(Math.Round(g.Smoothness * 4096.0)))
            .Add("Clrs", colors).Add("Trns", opacities);
    }

    // A color channel from 0..1 to 0..255 the way a person would type it, so reading it back gives the same float.
    private static double Channel(float v) => (double)((decimal)v * 255m);

    private static DescNode PatternRef(EffectPattern pattern, List<EffectPattern>? patterns)
    {
        patterns?.Add(pattern);
        string id = FillWriter.PatternId(pattern);
        return Obj("Ptrn").Add("Nm  ", DescNode.Str("Pattern " + id[..8])).Add("Idnt", DescNode.Str(id));
    }

    private static DescNode Begin(string cls, LayerEffect e) =>
        new DescNode { Type = "Objc", Name = DescNode.CountedString("", terminator: true), ClassId = cls }
            .Add("enab", DescNode.Bool_(e.Enabled)).Add("present", DescNode.Bool_(true)).Add("showInDialog", DescNode.Bool_(true));

    private static DescNode Obj(string cls) => new() { Type = "Objc", Name = DescNode.CountedString("", terminator: true), ClassId = cls };

    // A float the way a person typed it: 0.3f is 0.3 and not 0.30000001192..., so converting back to float lands on the same value.
    private static double Exact(float f) => (double)(decimal)f;

    private static double Pct(float fraction) => (double)((decimal)fraction * 100m);

    private static DescNode Percent(float fraction) => DescNode.Unitf("#Prc", Pct(fraction));

    private static DescNode Pixels(float v) => DescNode.Unitf("#Pxl", Exact(v));

    private static DescNode Angle(float degrees) => DescNode.Unitf("#Ang", Exact(degrees is >= -360f and <= 360f ? degrees : degrees % 360f));

    private static DescNode Color(Rgba32 c) =>
        Obj("RGBC").Add("Rd  ", DescNode.Dbl(c.R)).Add("Grn ", DescNode.Dbl(c.G)).Add("Bl  ", DescNode.Dbl(c.B));

    private static DescNode Contour(EffectContour? contour)
    {
        DescNode curve = DescNode.ListOf();
        IReadOnlyList<ContourPoint> points = contour is { IsLinear: false } ? contour.Points : EffectContour.Linear.Points;
        foreach (ContourPoint p in points)
        {
            curve.List.Add(Obj("CrPt").Add("Hrzn", DescNode.Dbl((double)((decimal)p.X * 255m))).Add("Vrtc", DescNode.Dbl((double)((decimal)p.Y * 255m))));
        }

        return Obj("ShpC").Add("Nm  ", DescNode.Str(contour is { IsLinear: false } ? "Custom" : "Linear")).Add("Crv ", curve);
    }

    private static DescNode Blend(BlendMode mode) => DescNode.Enum("BlnM", BlendCode(mode));

    private static string BlendCode(BlendMode mode) => mode switch
    {
        BlendMode.Multiply => "Mltp",
        BlendMode.Screen => "Scrn",
        BlendMode.Overlay => "Ovrl",
        BlendMode.Darken => "Drkn",
        BlendMode.Lighten => "Lghn",
        BlendMode.ColorDodge => "CDdg",
        BlendMode.ColorBurn => "CBrn",
        BlendMode.HardLight => "HrdL",
        BlendMode.SoftLight => "SftL",
        BlendMode.Difference => "Dfrn",
        BlendMode.Exclusion => "Xclu",
        BlendMode.LinearDodge => "linearDodge",
        BlendMode.LinearBurn => "linearBurn",
        BlendMode.VividLight => "vividLight",
        BlendMode.LinearLight => "linearLight",
        BlendMode.PinLight => "pinLight",
        BlendMode.HardMix => "hardMix",
        BlendMode.Hue => "H   ",
        BlendMode.Saturation => "Strt",
        BlendMode.Color => "Clr ",
        BlendMode.Luminosity => "Lmns",
        BlendMode.Subtract => "blendSubtraction",
        BlendMode.Divide => "blendDivide",
        _ => "Nrml",
    };
}
