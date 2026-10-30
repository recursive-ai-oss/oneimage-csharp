using OneImage.Svg.Dom;

namespace OneImage.Svg.Render;

/// <summary>How a filter primitive blends two inputs (<c>feBlend</c> modes, the separable ones and the four non-separable ones).</summary>
internal enum FeBlendMode
{
    Normal,
    Multiply,
    Screen,
    Overlay,
    Darken,
    Lighten,
    ColorDodge,
    ColorBurn,
    HardLight,
    SoftLight,
    Difference,
    Exclusion,
    Hue,
    Saturation,
    Color,
    Luminosity,
}

/// <summary>The operators of <c>feComposite</c>.</summary>
internal enum FeCompositeOp
{
    Over,
    In,
    Out,
    Atop,
    Xor,
    Lighter,
    Arithmetic,
}

/// <summary>Where a primitive reads an input from: <see cref="SourceGraphic"/>, <see cref="SourceAlpha"/>, or the result of an earlier primitive (its index, zero or more).</summary>
internal static class FeIn
{
    public const int SourceGraphic = -1;
    public const int SourceAlpha = -2;
}

/// <summary>
/// One filter primitive after resolution: inputs by index, lengths in the user space of the filtered element, the subregion and the
/// color space the primitive works in. Immutable once built.
/// </summary>
internal abstract class FePrimitive
{
    /// <summary>Gets the first input (see <see cref="FeIn"/>).</summary>
    public int In1 { get; init; } = FeIn.SourceGraphic;

    /// <summary>Gets the second input of the two-input primitives.</summary>
    public int In2 { get; init; } = FeIn.SourceGraphic;

    /// <summary>Gets whether the primitive works on linear-light values (<c>color-interpolation-filters: linearRGB</c>).</summary>
    public bool Linear { get; init; }

    /// <summary>Gets the subregion in user space: x, y, width, height.</summary>
    public double SubX { get; init; }

    public double SubY { get; init; }

    public double SubW { get; init; }

    public double SubH { get; init; }

    /// <summary>Gets whether the subregion is empty or invalid, which makes the result transparent.</summary>
    public bool EmptySub { get; init; }

    /// <summary>
    /// Gets the rectangle <c>feTile</c> takes as this result's tile: the explicit subregion, or the union of the tiles of the results this
    /// primitive reads (the specification's default subregion); the filter region when it reads a standard input. Unlike
    /// <see cref="SubX"/> it does not clip the result.
    /// </summary>
    public (double X, double Y, double W, double H) Tile { get; set; }

    /// <summary>
    /// How far, in pixels, the primitive reads beyond the pixels it writes, in x and y, when one user unit is <paramref name="scaleX"/> by
    /// <paramref name="scaleY"/> pixels. Infinity means the primitive reads the whole region (a wrapping convolution).
    /// </summary>
    public virtual (double X, double Y) ReachPx(double scaleX, double scaleY) => (0, 0);

    /// <summary>
    /// <see cref="ReachPx"/> given the filter region in pixels, for primitives whose reach can be known to be pointless there (an erosion
    /// wider than the region leaves nothing, whatever it reads).
    /// </summary>
    public virtual (double X, double Y) ReachIn(double scaleX, double scaleY, PxRect region) => ReachPx(scaleX, scaleY);

    /// <summary>The inputs that are results of earlier primitives.</summary>
    public virtual IEnumerable<int> Inputs()
    {
        yield return In1;
    }
}

internal sealed class FeBlur : FePrimitive
{
    public double SigmaX { get; init; }

    public double SigmaY { get; init; }

    public override (double X, double Y) ReachPx(double scaleX, double scaleY) => (FilterBlur.ReachPx(SigmaX * scaleX), FilterBlur.ReachPx(SigmaY * scaleY));
}

internal sealed class FeOffset : FePrimitive
{
    public double Dx { get; init; }

    public double Dy { get; init; }

    public override (double X, double Y) ReachPx(double scaleX, double scaleY) => (Math.Ceiling(Math.Abs(Dx * scaleX)) + 1, Math.Ceiling(Math.Abs(Dy * scaleY)) + 1);
}

internal sealed class FeFlood : FePrimitive
{
    /// <summary>Gets the flood color in sRGB with its opacity folded into alpha.</summary>
    public Rgba32 Color { get; init; }

    public float Opacity { get; init; } = 1;

    public override IEnumerable<int> Inputs() => [];
}

internal sealed class FeMerge : FePrimitive
{
    public required int[] Layers { get; init; }

    public override IEnumerable<int> Inputs() => Layers;
}

internal sealed class FeBlend : FePrimitive
{
    public FeBlendMode Mode { get; init; }

    public override IEnumerable<int> Inputs() => [In1, In2];
}

internal sealed class FeComposite : FePrimitive
{
    public FeCompositeOp Op { get; init; }

    public double K1 { get; init; }

    public double K2 { get; init; }

    public double K3 { get; init; }

    public double K4 { get; init; }

    public override IEnumerable<int> Inputs() => [In1, In2];
}

internal sealed class FeColorMatrix : FePrimitive
{
    /// <summary>Gets the 4x5 matrix, row-major: each row gives the output channel from R, G, B, A and a constant, on straight color in 0..1.</summary>
    public required double[] Matrix { get; init; }
}

internal sealed class FeConvolve : FePrimitive
{
    public int OrderX { get; init; }

    public int OrderY { get; init; }

    public required double[] Kernel { get; init; }

    public double Divisor { get; init; }

    public double Bias { get; init; }

    public int TargetX { get; init; }

    public int TargetY { get; init; }

    /// <summary>Gets the edge mode: 0 duplicate, 1 wrap, 2 none.</summary>
    public int Edge { get; init; }

    public bool PreserveAlpha { get; init; }

    public override (double X, double Y) ReachPx(double scaleX, double scaleY) => Edge == 1 ? (double.PositiveInfinity, double.PositiveInfinity) : (Math.Max(OrderX, 1), Math.Max(OrderY, 1));
}

internal sealed class FeDropShadow : FePrimitive
{
    public double Dx { get; init; }

    public double Dy { get; init; }

    public double SigmaX { get; init; }

    public double SigmaY { get; init; }

    public Rgba32 Color { get; init; }

    public float Opacity { get; init; } = 1;

    public override (double X, double Y) ReachPx(double scaleX, double scaleY) => (FilterBlur.ReachPx(SigmaX * scaleX) + Math.Ceiling(Math.Abs(Dx * scaleX)) + 1, FilterBlur.ReachPx(SigmaY * scaleY) + Math.Ceiling(Math.Abs(Dy * scaleY)) + 1);
}

/// <summary>An <c>feImage</c>: a decoded picture placed in the subregion, or an element drawn like <c>use</c>.</summary>
internal sealed class FeImage : FePrimitive
{
    /// <summary>Gets the picture, or <see langword="null"/> for an element reference.</summary>
    public Image? Picture { get; init; }

    /// <summary>Gets the <c>preserveAspectRatio</c> of the picture.</summary>
    public AspectRatio Aspect { get; init; }

    /// <summary>Gets the referenced element's nodes (scene space), or <see langword="null"/> for a picture.</summary>
    public RNode[]? Nodes { get; init; }

    public override IEnumerable<int> Inputs() => [];
}

/// <summary>One filter of a <c>filter</c> property, resolved: its region, and the primitives in evaluation order. The last primitive is the result.</summary>
internal sealed class RFilter
{
    /// <summary>Gets the user space of the filtered element in scene space.</summary>
    public required Affine Ctm { get; init; }

    /// <summary>Gets the filter region in user space.</summary>
    public required (double X, double Y, double W, double H) Region { get; init; }

    public required FePrimitive[] Primitives { get; init; }
}

/// <summary>One <c>feFuncR/G/B/A</c> transfer function, on straight color in 0..1.</summary>
internal sealed class FeTransfer
{
    public enum Kind
    {
        Identity,
        Table,
        Discrete,
        Linear,
        Gamma,
    }

    public Kind Type { get; init; }

    public double[] Table { get; init; } = [];

    public double Slope { get; init; } = 1;

    public double Intercept { get; init; }

    public double Amplitude { get; init; } = 1;

    public double Exponent { get; init; } = 1;

    public double Offset { get; init; }

    /// <summary>Applies the function and clamps to 0..1.</summary>
    public float Apply(float c)
    {
        double v;
        switch (Type)
        {
            case Kind.Table when Table.Length > 0:
            {
                int n = Table.Length - 1;
                if (c >= 1 || n == 0)
                {
                    v = Table[n];
                }
                else
                {
                    double f = Math.Max(c, 0) * n;
                    int k = (int)f;
                    v = Table[k] + ((f - k) * (Table[k + 1] - Table[k]));
                }

                break;
            }

            case Kind.Discrete when Table.Length > 0:
            {
                int n = Table.Length;
                v = Table[Math.Clamp((int)(Math.Max(c, 0) * n), 0, n - 1)];
                break;
            }

            case Kind.Linear:
                v = (Slope * c) + Intercept;
                break;
            case Kind.Gamma:
                v = (Amplitude * DeterministicMath.Pow(Math.Max(c, 0), Exponent)) + Offset;
                break;
            default:
                return c;
        }

        return double.IsNaN(v) ? 0 : (float)Math.Clamp(v, 0, 1);
    }
}

internal sealed class FeComponentTransfer : FePrimitive
{
    /// <summary>Gets the functions for red, green, blue and alpha.</summary>
    public required FeTransfer[] Funcs { get; init; }
}

internal sealed class FeMorphology : FePrimitive
{
    public bool Dilate { get; init; }

    public double RadiusX { get; init; }

    public double RadiusY { get; init; }

    /// <summary>The window radius in whole pixels (the scaled radius rounded to nearest; below one half there is no window on that axis).</summary>
    public static int WindowPx(double radiusPx) => double.IsFinite(radiusPx) ? (int)Math.Min(Math.Floor(radiusPx + 0.5), 1 << 30) : 1 << 30;

    public override (double X, double Y) ReachPx(double scaleX, double scaleY) => (WindowPx(RadiusX * scaleX), WindowPx(RadiusY * scaleY));

    public override (double X, double Y) ReachIn(double scaleX, double scaleY, PxRect region)
    {
        (double rx, double ry) = ReachPx(scaleX, scaleY);
        if (!Dilate && ((rx > 0 && (2 * rx) + 1 > region.W) || (ry > 0 && (2 * ry) + 1 > region.H)))
        {
            // The window leaves the region everywhere, so every pixel sees transparent black and the result is empty without reading anything.
            return (0, 0);
        }

        return (rx, ry);
    }
}

/// <summary>Repeats the subregion of its input over its own subregion.</summary>
internal sealed class FeTile : FePrimitive
{
    public override (double X, double Y) ReachPx(double scaleX, double scaleY) => (double.PositiveInfinity, double.PositiveInfinity);
}

internal sealed class FeTurbulence : FePrimitive
{
    public double BaseX { get; init; }

    public double BaseY { get; init; }

    public int Octaves { get; init; } = 1;

    public int Seed { get; init; }

    public bool Stitch { get; init; }

    public bool Fractal { get; init; }

    public override IEnumerable<int> Inputs() => [];
}

internal sealed class FeDisplacement : FePrimitive
{
    public double Scale { get; init; }

    /// <summary>Gets the channel (0 red .. 3 alpha) read for x and for y.</summary>
    public int XChannel { get; init; } = 3;

    public int YChannel { get; init; } = 3;

    public override IEnumerable<int> Inputs() => [In1, In2];

    public override (double X, double Y) ReachPx(double scaleX, double scaleY) => (Math.Ceiling(Math.Abs(Scale * scaleX) / 2) + 1, Math.Ceiling(Math.Abs(Scale * scaleY) / 2) + 1);
}

internal enum LightKind
{
    Distant,
    Point,
    Spot,
}

/// <summary>A lighting primitive (<c>feDiffuseLighting</c> or <c>feSpecularLighting</c>) with its light source, in user units.</summary>
internal sealed class FeLighting : FePrimitive
{
    public bool Specular { get; init; }

    public double SurfaceScale { get; init; } = 1;

    /// <summary>Gets <c>diffuseConstant</c> or <c>specularConstant</c>.</summary>
    public double Constant { get; init; } = 1;

    public double Exponent { get; init; } = 1;

    /// <summary>Gets the normal-estimation distances in user units, or 0 for one pixel.</summary>
    public double UnitX { get; init; }

    public double UnitY { get; init; }

    public Rgba32 Color { get; init; }

    public LightKind Kind { get; init; }

    public double Azimuth { get; init; }

    public double Elevation { get; init; }

    public double X { get; init; }

    public double Y { get; init; }

    public double Z { get; init; }

    public double PointsAtX { get; init; }

    public double PointsAtY { get; init; }

    public double PointsAtZ { get; init; }

    public double SpotExponent { get; init; } = 1;

    /// <summary>Gets the cone half-angle in degrees, or NaN for no cone.</summary>
    public double ConeAngle { get; init; } = double.NaN;

    public override (double X, double Y) ReachPx(double scaleX, double scaleY) =>
        (Math.Ceiling(UnitX > 0 ? Math.Min(UnitX * scaleX, 1 << 20) : 1) + 1, Math.Ceiling(UnitY > 0 ? Math.Min(UnitY * scaleY, 1 << 20) : 1) + 1);
}
