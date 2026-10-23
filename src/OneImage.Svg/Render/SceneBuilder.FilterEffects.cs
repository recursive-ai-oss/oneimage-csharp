using OneImage.Svg.Dom;

namespace OneImage.Svg.Render;

// Filters, part two: component transfer, morphology, tile, turbulence, displacement map and the lighting primitives.
internal sealed partial class SceneBuilder
{
    // The octave at index k adds at most 2^-k, so past ten octaves nothing changes at 8 bits (the specification allows clamping).
    private const int MaxTurbulenceOctaves = 10;

    // Builds the primitives of part two; returns null for any other element name.
    private FePrimitive? BuildEffectPrimitive(SvgElement e, int in1, int in2, bool linear, bool primObj, Box? box, double sx, double sy, double sw, double sh, bool empty)
    {
        double bw = primObj ? box!.Value.W : 1;
        double bh = primObj ? box!.Value.H : 1;
        FePrimitive Passthrough() => new FeColorMatrix { In1 = in1, Linear = linear, Matrix = IdentityMatrix, SubX = sx, SubY = sy, SubW = sw, SubH = sh, EmptySub = empty };

        switch (e.Name)
        {
            case "feComponentTransfer":
            {
                var funcs = new FeTransfer[4];
                for (int i = 0; i < 4; i++)
                {
                    funcs[i] = TransferFunction(e, "feFunc" + "RGBA"[i]);
                }

                return new FeComponentTransfer { In1 = in1, Linear = linear, Funcs = funcs, SubX = sx, SubY = sy, SubW = sw, SubH = sh, EmptySub = empty };
            }

            case "feMorphology":
            {
                double[]? r = Numbers(e.Attr("radius"));
                if (r is null || r.Length is 0 or > 2 || r.Any(v => v < 0 || !double.IsFinite(v)))
                {
                    return Passthrough();
                }

                double rx = r[0];
                double ry = r.Length == 2 ? r[1] : r[0];
                if (rx <= 0 && ry <= 0)
                {
                    return Passthrough();
                }

                return new FeMorphology { In1 = in1, Linear = linear, Dilate = e.Attr("operator")?.Trim() == "dilate", RadiusX = rx * bw, RadiusY = ry * bh, SubX = sx, SubY = sy, SubW = sw, SubH = sh, EmptySub = empty };
            }

            case "feTile":
                return new FeTile { In1 = in1, Linear = linear, SubX = sx, SubY = sy, SubW = sw, SubH = sh, EmptySub = empty };

            case "feTurbulence":
                return TurbulenceOf(e, linear, sx, sy, sw, sh, empty);

            case "feDisplacementMap":
                return new FeDisplacement
                {
                    In1 = in1,
                    In2 = in2,
                    Linear = linear,
                    Scale = Number(e.Attr("scale"), 0) * ((bw + bh) / 2),
                    XChannel = ChannelIndex(e.Attr("xChannelSelector")),
                    YChannel = ChannelIndex(e.Attr("yChannelSelector")),
                    SubX = sx,
                    SubY = sy,
                    SubW = sw,
                    SubH = sh,
                    EmptySub = empty,
                };

            case "feDiffuseLighting" or "feSpecularLighting":
                return (FePrimitive?)LightingOf(e, in1, linear, primObj, box, sx, sy, sw, sh, empty) ?? new FeMerge { Layers = [], Linear = linear, SubX = sx, SubY = sy, SubW = sw, SubH = sh, EmptySub = empty };

            default:
                return null;
        }
    }

    private static int ChannelIndex(string? v) => v?.Trim() switch { "R" => 0, "G" => 1, "B" => 2, _ => 3 };

    private static FeTransfer TransferFunction(SvgElement e, string name)
    {
        SvgElement? func = null;
        foreach (SvgElement c in e.Children)
        {
            if (c.Name == name)
            {
                func = c;
            }
        }

        if (func is null)
        {
            return new FeTransfer();
        }

        FeTransfer.Kind kind = func.Attr("type")?.Trim() switch
        {
            "table" => FeTransfer.Kind.Table,
            "discrete" => FeTransfer.Kind.Discrete,
            "linear" => FeTransfer.Kind.Linear,
            "gamma" => FeTransfer.Kind.Gamma,
            _ => FeTransfer.Kind.Identity,
        };
        return new FeTransfer
        {
            Type = kind,
            Table = Numbers(func.Attr("tableValues")) ?? [],
            Slope = Number(func.Attr("slope"), 1),
            Intercept = Number(func.Attr("intercept"), 0),
            Amplitude = Number(func.Attr("amplitude"), 1),
            Exponent = Number(func.Attr("exponent"), 1),
            Offset = Number(func.Attr("offset"), 0),
        };
    }

    private static FePrimitive TurbulenceOf(SvgElement e, bool linear, double sx, double sy, double sw, double sh, bool empty)
    {
        double fx = 0, fy = 0;
        bool invalid = false;
        if (e.Attr("baseFrequency") is { } bf)
        {
            double[]? n = Numbers(bf);
            if (n is null || n.Length is 0 or > 2 || n.Any(v => v < 0 || !double.IsFinite(v)))
            {
                invalid = true;
            }
            else
            {
                fx = n[0];
                fy = n.Length == 2 ? n[1] : n[0];
            }
        }

        int octaves = 1;
        if (e.Attr("numOctaves") is { } no)
        {
            double v = Number(no, 1);
            if (v < 0 || v != Math.Floor(v))
            {
                invalid = true;
            }
            else
            {
                octaves = (int)Math.Min(v, MaxTurbulenceOctaves);
            }
        }

        double seed = Number(e.Attr("seed"), 0);
        bool fractal = e.Attr("type")?.Trim() == "fractalNoise";
        if (invalid)
        {
            // An invalid parameter makes the primitive produce transparent black.
            return new FeMerge { Layers = [], Linear = linear, SubX = sx, SubY = sy, SubW = sw, SubH = sh, EmptySub = empty };
        }

        return new FeTurbulence
        {
            Linear = linear,
            BaseX = fx,
            BaseY = fy,
            Octaves = octaves,
            Seed = (int)Math.Clamp(Math.Truncate(seed), int.MinValue, int.MaxValue),
            Stitch = e.Attr("stitchTiles")?.Trim() == "stitch",
            Fractal = fractal,
            SubX = sx,
            SubY = sy,
            SubW = sw,
            SubH = sh,
            EmptySub = empty,
        };
    }

    private FeLighting? LightingOf(SvgElement e, int in1, bool linear, bool primObj, Box? box, double sx, double sy, double sw, double sh, bool empty)
    {
        SvgElement? light = null;
        foreach (SvgElement c in e.Children)
        {
            if (c.Name is "feDistantLight" or "fePointLight" or "feSpotLight")
            {
                light = c;
                break;
            }
        }

        if (light is null)
        {
            return null;
        }

        bool specular = e.Name == "feSpecularLighting";
        Rgba32 color = new(255, 255, 255, 255);
        if (OwnProp(e, "lighting-color") is { } lc)
        {
            string t = lc.Trim();
            if (t.Equals("currentColor", StringComparison.OrdinalIgnoreCase))
            {
                color = DefinedStyle(e).Color;
            }
            else if (CssColor.TryParse(t, out Rgba32 parsed))
            {
                color = parsed;
            }
        }

        double kul = 0, kulY = 0;
        if (Numbers(e.Attr("kernelUnitLength")) is { Length: 1 or 2 } k && k.All(v => v > 0 && double.IsFinite(v)))
        {
            kul = k[0];
            kulY = k.Length == 2 ? k[1] : k[0];
        }

        double bw = primObj ? box!.Value.W : 1;
        double bh = primObj ? box!.Value.H : 1;
        double constant = Number(e.Attr(specular ? "specularConstant" : "diffuseConstant"), 1);
        double exponent = Number(e.Attr("specularExponent"), 1);

        // A specular exponent outside 1..128 is an error, which leaves the primitive's result transparent. A negative constant is not: it
        // lights nothing and leaves opaque black.
        if (specular && !(exponent >= 1 && exponent <= 128))
        {
            return null;
        }

        LightKind kind = light.Name switch { "feDistantLight" => LightKind.Distant, "fePointLight" => LightKind.Point, _ => LightKind.Spot };
        double cone = double.NaN;
        if (kind == LightKind.Spot && light.Attr("limitingConeAngle") is { } lca)
        {
            double v = Number(lca, double.NaN);
            cone = v;
        }

        double spotExp = Number(light.Attr("specularExponent"), 1);
        if (!(spotExp > 0))
        {
            spotExp = 1;
        }

        return new FeLighting
        {
            In1 = in1,
            Linear = linear,
            Specular = specular,
            SurfaceScale = Number(e.Attr("surfaceScale"), 1),
            Constant = constant,
            Exponent = exponent,
            UnitX = kul * bw,
            UnitY = kulY * bh,
            Color = color,
            Kind = kind,
            Azimuth = Number(light.Attr("azimuth"), 0),
            Elevation = Number(light.Attr("elevation"), 0),

            // The light's position is in user units even with primitiveUnits=objectBoundingBox, as in the reference renderings.
            X = Number(light.Attr("x"), 0),
            Y = Number(light.Attr("y"), 0),
            Z = Number(light.Attr("z"), 0),
            PointsAtX = Number(light.Attr("pointsAtX"), 0),
            PointsAtY = Number(light.Attr("pointsAtY"), 0),
            PointsAtZ = Number(light.Attr("pointsAtZ"), 0),
            SpotExponent = spotExp,
            ConeAngle = cone,
            SubX = sx,
            SubY = sy,
            SubW = sw,
            SubH = sh,
            EmptySub = empty,
        };
    }
}
