namespace OneImage.Pdf.Tests.SpecTruth;

/// <summary>
/// The blend functions of ISO 32000-2 11.3.5 on colors in [0, 1]. Written straight from the formulas so the expected
/// values depend on nothing in the renderer.
/// </summary>
internal static class BlendMath
{
    public static readonly string[] Separable =
    [
        "Normal", "Multiply", "Screen", "Overlay", "Darken", "Lighten", "ColorDodge", "ColorBurn", "HardLight", "SoftLight", "Difference", "Exclusion",
    ];

    public static readonly string[] NonSeparable = ["Hue", "Saturation", "Color", "Luminosity"];

    public static double Channel(string mode, double cb, double cs) => mode switch
    {
        "Normal" => cs,
        "Multiply" => cb * cs,
        "Screen" => cb + cs - (cb * cs),
        "Overlay" => HardLight(cs, cb),
        "Darken" => Math.Min(cb, cs),
        "Lighten" => Math.Max(cb, cs),
        "ColorDodge" => cs >= 1 ? 1 : Math.Min(1, cb / (1 - cs)),
        "ColorBurn" => cb >= 1 ? 1 : (cs <= 0 ? 0 : 1 - Math.Min(1, (1 - cb) / cs)),
        "HardLight" => HardLight(cb, cs),
        "SoftLight" => SoftLight(cb, cs),
        "Difference" => Math.Abs(cb - cs),
        "Exclusion" => cb + cs - (2 * cb * cs),
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };

    private static double HardLight(double cb, double cs) =>
        cs <= 0.5 ? cb * (2 * cs) : (cb + ((2 * cs) - 1)) - (cb * ((2 * cs) - 1));

    private static double SoftLight(double cb, double cs)
    {
        if (cs <= 0.5)
        {
            return cb - ((1 - (2 * cs)) * cb * (1 - cb));
        }

        double d = cb <= 0.25 ? ((((16 * cb) - 12) * cb) + 4) * cb : Math.Sqrt(cb);
        return cb + (((2 * cs) - 1) * (d - cb));
    }

    public static (double R, double G, double B) Blend(string mode, (double R, double G, double B) cb, (double R, double G, double B) cs)
    {
        switch (mode)
        {
            case "Hue":
                return SetLum(SetSat(cs, Sat(cb)), Lum(cb));
            case "Saturation":
                return SetLum(SetSat(cb, Sat(cs)), Lum(cb));
            case "Color":
                return SetLum(cs, Lum(cb));
            case "Luminosity":
                return SetLum(cb, Lum(cs));
            default:
                return (Channel(mode, cb.R, cs.R), Channel(mode, cb.G, cs.G), Channel(mode, cb.B, cs.B));
        }
    }

    // 11.3.5.3
    private static double Lum((double R, double G, double B) c) => (0.3 * c.R) + (0.59 * c.G) + (0.11 * c.B);

    private static (double R, double G, double B) ClipColor((double R, double G, double B) c)
    {
        double l = Lum(c);
        double n = Math.Min(c.R, Math.Min(c.G, c.B));
        double x = Math.Max(c.R, Math.Max(c.G, c.B));
        if (n < 0)
        {
            c = (l + (((c.R - l) * l) / (l - n)), l + (((c.G - l) * l) / (l - n)), l + (((c.B - l) * l) / (l - n)));
        }

        if (x > 1)
        {
            c = (l + (((c.R - l) * (1 - l)) / (x - l)), l + (((c.G - l) * (1 - l)) / (x - l)), l + (((c.B - l) * (1 - l)) / (x - l)));
        }

        return c;
    }

    private static (double R, double G, double B) SetLum((double R, double G, double B) c, double l)
    {
        double d = l - Lum(c);
        return ClipColor((c.R + d, c.G + d, c.B + d));
    }

    private static double Sat((double R, double G, double B) c) => Math.Max(c.R, Math.Max(c.G, c.B)) - Math.Min(c.R, Math.Min(c.G, c.B));

    private static (double R, double G, double B) SetSat((double R, double G, double B) c, double s)
    {
        double[] v = [c.R, c.G, c.B];
        int[] idx = [0, 1, 2];
        Array.Sort(idx, (a, b) => v[a].CompareTo(v[b]));
        int min = idx[0], mid = idx[1], max = idx[2];
        double[] o = [0, 0, 0];
        if (v[max] > v[min])
        {
            o[mid] = (v[mid] - v[min]) * s / (v[max] - v[min]);
            o[max] = s;
        }

        return (o[0], o[1], o[2]);
    }
}
