using System.Collections.Concurrent;

namespace OneImage;

/// <summary>How colors that cannot be reproduced exactly are mapped. Values match the ICC header intent codes.</summary>
public enum RenderingIntent
{
    /// <summary>Photographic mapping using the profile's perceptual table (A2B0/B2A0), falling back to relative colorimetric data.</summary>
    Perceptual = 0,

    /// <summary>Colors keep their values relative to each medium's white; out-of-gamut colors are clipped. The default.</summary>
    RelativeColorimetric = 1,

    /// <summary>Vivid mapping using the saturation table (A2B2/B2A2), falling back to the perceptual table.</summary>
    Saturation = 2,

    /// <summary>Colors keep their absolute values, including the paper or medium white (no white point adaptation).</summary>
    AbsoluteColorimetric = 3,
}

/// <summary>
/// A color profile: an ICC profile (version 2 or 4) parsed for use as the source or destination of a color conversion. Built-in
/// profiles are generated in code and parse through the same path as profiles read from files. Profiles are immutable and thread-safe.
/// Supported: matrix/TRC and gray TRC profiles, lut8, lut16, lutAToB and lutBToA tables, sampled and parametric curves.
/// Not supported (rejected with <see cref="ImageFormatException"/>): DeviceLink, named color, abstract profiles and iccMAX.
/// </summary>
public sealed class ColorProfile
{
    private static readonly ConcurrentDictionary<string, ColorProfile> s_builtIn = new();

    private readonly Lazy<ColorProfile?> _linear;
    private readonly Lazy<string> _contentKey;

    private ColorProfile(byte[] icc, IccData data, Func<ColorProfile?>? linear)
    {
        IccBytes = icc;
        Data = data;
        _linear = new Lazy<ColorProfile?>(linear ?? (() => LinearOf(this)));
        _contentKey = new Lazy<string>(() => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(IccBytes.Span)));
    }

    /// <summary>Gets the sRGB profile (IEC 61966-2-1 primaries and transfer curve) in the smallest form that carries it: an ICC v4 profile of about 500 bytes. Use it to embed on thumbnails.</summary>
    public static ColorProfile Srgb => Get("srgb", () => Matrix("sRGB", Chromaticities.Srgb, SrgbCurve(), compact: true), () => LinearSrgb);

    /// <summary>Gets the same profile as <see cref="Srgb"/>; named for call sites that embed the profile.</summary>
    public static ColorProfile SrgbCompact => Srgb;

    /// <summary>Gets Display P3 (DCI-P3 primaries, D65 white, sRGB transfer curve).</summary>
    public static ColorProfile DisplayP3 => Get("p3", () => Matrix(Name("Display P3"), Chromaticities.DisplayP3, SrgbCurve()), () => LinearDisplayP3);

    /// <summary>Gets a wide-gamut RGB working space with the primaries and gamma 2.19921875 of the common 1998 wide-gamut standard, under this library's own name.</summary>
    public static ColorProfile WideGamutRgb => Get("wide", () => Matrix(Name("Wide Gamut RGB"), Chromaticities.Wide, IccWriter.GammaCurve(563.0 / 256.0)), () => LinearWideGamutRgb);

    /// <summary>Gets ProPhoto RGB (D50 white, gamma 1.8).</summary>
    public static ColorProfile ProPhoto => Get("pro", () => Matrix(Name("ProPhoto RGB"), Chromaticities.ProPhoto, IccWriter.GammaCurve(1.8)), () => LinearProPhoto);

    /// <summary>Gets Rec. 2020 primaries with the BT.709/BT.2020 transfer curve (standard dynamic range).</summary>
    public static ColorProfile Rec2020 => Get("2020", () => Matrix(Name("Rec2020"), Chromaticities.Rec2020, IccWriter.ParametricCurve(3, 1 / 0.45, 1 / 1.099296826809442, 0.099296826809442 / 1.099296826809442, 1 / 4.5, 0.081242858298635)), () => LinearRec2020);

    /// <summary>Gets sRGB primaries with a linear transfer curve.</summary>
    public static ColorProfile LinearSrgb => Get("lsrgb", () => Matrix(Name("Linear sRGB"), Chromaticities.Srgb, IccWriter.IdentityCurve()), () => LinearSrgb);

    /// <summary>Gets Display P3 primaries with a linear transfer curve.</summary>
    public static ColorProfile LinearDisplayP3 => Get("lp3", () => Matrix(Name("Linear Display P3"), Chromaticities.DisplayP3, IccWriter.IdentityCurve()), () => LinearDisplayP3);

    /// <summary>Gets the wide-gamut working space (see <see cref="WideGamutRgb"/>) with a linear transfer curve.</summary>
    public static ColorProfile LinearWideGamutRgb => Get("lwide", () => Matrix(Name("Linear Wide Gamut RGB"), Chromaticities.Wide, IccWriter.IdentityCurve()), () => LinearWideGamutRgb);

    /// <summary>Gets ProPhoto primaries with a linear transfer curve.</summary>
    public static ColorProfile LinearProPhoto => Get("lpro", () => Matrix(Name("Linear ProPhoto RGB"), Chromaticities.ProPhoto, IccWriter.IdentityCurve()), () => LinearProPhoto);

    /// <summary>Gets Rec. 2020 primaries with a linear transfer curve.</summary>
    public static ColorProfile LinearRec2020 => Get("l2020", () => Matrix(Name("Linear Rec2020"), Chromaticities.Rec2020, IccWriter.IdentityCurve()), () => LinearRec2020);

    /// <summary>Gets the default gray profile: D50 gray with the sRGB transfer curve.</summary>
    public static ColorProfile Gray => Get("gray", () => GrayProfile(Name("Gray (sRGB curve)"), SrgbCurve()), () => LinearGray);

    /// <summary>Gets gray with a linear transfer curve.</summary>
    public static ColorProfile LinearGray => Get("lgray", () => GrayProfile(Name("Linear Gray"), IccWriter.IdentityCurve()), () => LinearGray);

    /// <summary>Gets CIE L*a*b* with a D50 white point (L 0..100, a and b in natural units). Lab pixels are float; the profile has no tables.</summary>
    public static ColorProfile LabD50 => Get("lab", MakeLab, () => null);

    /// <summary>
    /// Gets the generic CMYK profile, used for untagged CMYK. It is generated from published FOGRA39 characterization data
    /// (see the third-party notice) and carries this library's own name; it is not a certified or endorsed FOGRA profile.
    /// It has A2B and B2A tables for all intents, and a total ink limit of about 300%.
    /// </summary>
    public static ColorProfile GenericCmyk => Get("cmyk", GenericCmykBuilder.Build, () => null);

    /// <summary>Gets the color model the profile's device values use.</summary>
    public ColorModel Model => Data.Model;

    /// <summary>Gets the profile description, or an empty string when the profile has none.</summary>
    public string Description => Data.Description;

    /// <summary>Gets the ICC major version, for example 4 for v4.3.</summary>
    public int IccMajorVersion => Data.VersionMajor;

    /// <summary>Gets a value indicating whether the profile is a matrix/TRC or gray TRC profile whose curves are all linear.</summary>
    public bool IsLinear => Data.IsLinearMatrixShaper;

    /// <summary>Gets the profile bytes (ICC format). The returned memory must not be modified.</summary>
    public ReadOnlyMemory<byte> IccBytes { get; }

    /// <summary>
    /// Gets the linear-light variant of this profile (same primaries and white, linear curves) for RGB and gray matrix profiles,
    /// or <see langword="null"/> for table-based, CMYK and Lab profiles, where "linear" has no single meaning.
    /// </summary>
    public ColorProfile? LinearVariant => _linear.Value;

    /// <summary>Gets a key that is equal for profiles with identical bytes, so equal embedded profiles from different images share cached transforms.</summary>
    internal string ContentKey => _contentKey.Value;

    internal IccData Data { get; }

    /// <summary>Parses an ICC profile.</summary>
    /// <param name="icc">The profile bytes; copied.</param>
    /// <returns>The profile.</returns>
    /// <exception cref="ImageFormatException">The data is not a valid or supported ICC profile.</exception>
    /// <exception cref="ImageLimitException">The profile or one of its tables is larger than the supported maximum.</exception>
    public static ColorProfile FromIcc(ReadOnlySpan<byte> icc) => FromBytes(icc.ToArray());

    /// <summary>Parses an ICC profile without throwing.</summary>
    /// <param name="icc">The profile bytes.</param>
    /// <param name="profile">The profile on success.</param>
    /// <returns><see langword="true"/> when the data is a supported profile.</returns>
    public static bool TryFromIcc(ReadOnlySpan<byte> icc, out ColorProfile? profile)
    {
        try
        {
            profile = FromIcc(icc);
            return true;
        }
        catch (ImageException)
        {
            profile = null;
            return false;
        }
    }

    /// <summary>Returns the default profile for pixels of a color model: sRGB, gray, generic CMYK or Lab D50.</summary>
    /// <param name="model">The color model.</param>
    /// <returns>The profile.</returns>
    public static ColorProfile DefaultFor(ColorModel model) => model switch
    {
        ColorModel.Gray => Gray,
        ColorModel.Rgb => Srgb,
        ColorModel.Cmyk => GenericCmyk,
        _ => LabD50,
    };

    /// <inheritdoc/>
    public override string ToString() => Description.Length > 0 ? Description : $"ICC {Model} profile";

    // ---- construction ---------------------------------------------------------------------------------------------

    internal static ColorProfile FromBytes(byte[] icc)
    {
        IccData d = IccData.Parse(icc);

        // Prove the profile converts in the device-to-PCS direction before handing it out, so later failures cannot be format errors.
        _ = d.BuildToPcs(RenderingIntent.RelativeColorimetric);
        return new ColorProfile(icc, d, null);
    }

    internal static string Name(string suffix) => Library.Name + " " + suffix;

    private static ColorProfile Get(string key, Func<ColorProfile> make, Func<ColorProfile?> linear)
    {
        if (s_builtIn.TryGetValue(key, out ColorProfile? p))
        {
            return p;
        }

        ColorProfile built = make();

        // Re-wrap with the link to the linear sibling so built-ins resolve each other without parsing again.
        return s_builtIn.GetOrAdd(key, new ColorProfile(built.IccBytes.ToArray(), built.Data, linear));
    }

    private static byte[] SrgbCurve() =>
        IccWriter.ParametricCurve(3, 2.4, 1 / 1.055, 0.055 / 1.055, 1 / 12.92, 0.04045);

    private static ColorProfile Matrix(string description, double[] xy, byte[] curve, bool compact = false)
    {
        double[] m = Chromaticities.AdaptedColorants(xy);
        var w = new IccWriter(IccData.Sig("mntr"), "RGB ", "XYZ ", 4, 3);
        w.Add("desc", w.Text(description, true));
        w.Add("wtpt", IccWriter.Xyz(Pcs.D50X, Pcs.D50Y, Pcs.D50Z));
        w.Add("rXYZ", IccWriter.Xyz(m[0], m[3], m[6]));
        w.Add("gXYZ", IccWriter.Xyz(m[1], m[4], m[7]));
        w.Add("bXYZ", IccWriter.Xyz(m[2], m[5], m[8]));
        w.Add("rTRC", curve);
        w.Add("gTRC", curve);
        w.Add("bTRC", curve);
        w.Add("cprt", w.Text(compact ? "CC0" : "Generated by " + Library.Name + "; no copyright, free to use", false));
        return FromBytes(w.Build());
    }

    private static ColorProfile GrayProfile(string description, byte[] curve)
    {
        var w = new IccWriter(IccData.Sig("mntr"), "GRAY", "XYZ ", 4, 3);
        w.Add("desc", w.Text(description, true));
        w.Add("wtpt", IccWriter.Xyz(Pcs.D50X, Pcs.D50Y, Pcs.D50Z));
        w.Add("kTRC", curve);
        w.Add("cprt", w.Text("Generated by " + Library.Name + "; no copyright, free to use", false));
        return FromBytes(w.Build());
    }

    private static ColorProfile MakeLab()
    {
        var w = new IccWriter(IccData.Sig("spac"), "Lab ", "Lab ", 4, 3);
        w.Add("desc", w.Text(Name("Lab D50"), true));
        w.Add("wtpt", IccWriter.Xyz(Pcs.D50X, Pcs.D50Y, Pcs.D50Z));
        w.Add("cprt", w.Text("Generated by " + Library.Name + "; no copyright, free to use", false));
        return FromBytes(w.Build());
    }

    private static ColorProfile? LinearOf(ColorProfile p)
    {
        if (p.Data.IsLinearMatrixShaper)
        {
            return p;
        }

        if (p.Data.Model == ColorModel.Rgb && p.Data.IsMatrixShaper(RenderingIntent.RelativeColorimetric, output: false))
        {
            double[] m = p.Data.ReadColorants();
            var w = new IccWriter(IccData.Sig("mntr"), "RGB ", "XYZ ", 4, 3);
            w.Add("desc", w.Text("Linear " + (p.Description.Length > 0 ? p.Description : "RGB"), true));
            w.Add("wtpt", IccWriter.Xyz(Pcs.D50X, Pcs.D50Y, Pcs.D50Z));
            w.Add("rXYZ", IccWriter.Xyz(m[0], m[3], m[6]));
            w.Add("gXYZ", IccWriter.Xyz(m[1], m[4], m[7]));
            w.Add("bXYZ", IccWriter.Xyz(m[2], m[5], m[8]));
            byte[] id = IccWriter.IdentityCurve();
            w.Add("rTRC", id);
            w.Add("gTRC", id);
            w.Add("bTRC", id);
            w.Add("cprt", w.Text("Derived by " + Library.Name, false));
            return FromBytes(w.Build());
        }

        if (p.Data.Model == ColorModel.Gray && p.Data.IsMatrixShaper(RenderingIntent.RelativeColorimetric, output: false))
        {
            return LinearGray;
        }

        return null;
    }
}

/// <summary>Primaries and white points (CIE xy) and the Bradford adaptation to the ICC D50 PCS.</summary>
internal static class Chromaticities
{
    // R, G, B, white (x, y each).
    public static readonly double[] Srgb = [0.64, 0.33, 0.30, 0.60, 0.15, 0.06, 0.3127, 0.3290];
    public static readonly double[] DisplayP3 = [0.68, 0.32, 0.265, 0.69, 0.15, 0.06, 0.3127, 0.3290];
    public static readonly double[] Wide = [0.64, 0.33, 0.21, 0.71, 0.15, 0.06, 0.3127, 0.3290];
    public static readonly double[] ProPhoto = [0.7347, 0.2653, 0.1596, 0.8404, 0.0366, 0.0001, 0.3457, 0.3585];
    public static readonly double[] Rec2020 = [0.708, 0.292, 0.170, 0.797, 0.131, 0.046, 0.3127, 0.3290];

    private static readonly double[] s_bradford =
    [
        0.8951, 0.2664, -0.1614,
        -0.7502, 1.7135, 0.0367,
        0.0389, -0.0685, 1.0296,
    ];

    /// <summary>Returns the row-major colorant matrix (columns R, G, B as XYZ) adapted from the space's white to D50.</summary>
    public static double[] AdaptedColorants(double[] xy)
    {
        double[] w = [xy[6] / xy[7], 1.0, (1 - xy[6] - xy[7]) / xy[7]];
        double[] p =
        [
            xy[0] / xy[1], xy[2] / xy[3], xy[4] / xy[5],
            1, 1, 1,
            (1 - xy[0] - xy[1]) / xy[1], (1 - xy[2] - xy[3]) / xy[3], (1 - xy[4] - xy[5]) / xy[5],
        ];
        double[] pinv = IccData.Invert3(p);
        double[] s = Mul3Vec(pinv, w);
        var m = new double[9];
        for (int r = 0; r < 3; r++)
        {
            for (int c = 0; c < 3; c++)
            {
                m[(r * 3) + c] = p[(r * 3) + c] * s[c];
            }
        }

        // Bradford: scale in cone space by (D50 cone response / source white cone response).
        double[] d50 = [Pcs.D50X, Pcs.D50Y, Pcs.D50Z];
        double[] cs = Mul3Vec(s_bradford, w);
        double[] cd = Mul3Vec(s_bradford, d50);
        double[] scale = [cd[0] / cs[0], 0, 0, 0, cd[1] / cs[1], 0, 0, 0, cd[2] / cs[2]];
        double[] adapt = Mul3(IccData.Invert3(s_bradford), Mul3(scale, s_bradford));
        return Mul3(adapt, m);
    }

    private static double[] Mul3Vec(double[] a, double[] v) =>
        [(a[0] * v[0]) + (a[1] * v[1]) + (a[2] * v[2]), (a[3] * v[0]) + (a[4] * v[1]) + (a[5] * v[2]), (a[6] * v[0]) + (a[7] * v[1]) + (a[8] * v[2])];

    private static double[] Mul3(double[] a, double[] b)
    {
        var r = new double[9];
        for (int i = 0; i < 3; i++)
        {
            for (int j = 0; j < 3; j++)
            {
                r[(i * 3) + j] = (a[i * 3] * b[j]) + (a[(i * 3) + 1] * b[3 + j]) + (a[(i * 3) + 2] * b[6 + j]);
            }
        }

        return r;
    }
}
