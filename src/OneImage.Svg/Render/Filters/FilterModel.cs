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
    /// How far, in pixels, the primitive reads beyond the pixels it writes, in x and y, when one user unit is <paramref name="scaleX"/> by
    /// <paramref name="scaleY"/> pixels. Infinity means the primitive reads the whole region (a wrapping convolution).
    /// </summary>
    public virtual (double X, double Y) ReachPx(double scaleX, double scaleY) => (0, 0);

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
