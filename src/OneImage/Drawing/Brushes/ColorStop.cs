namespace OneImage.Drawing;

/// <summary>
/// A color at a position along a gradient. Positions are fractions of the gradient's length (0 at its start, 1 at its end); they may
/// lie outside 0..1 but must be finite.
/// <para>
/// <b>How gradients are colored.</b> Stops are sorted by position (equal positions keep their order, so two stops at one position
/// make a hard edge). Before the first stop the first color is used and after the last stop the last color, unless the gradient's
/// <see cref="GradientRepetition"/> says otherwise. Between two stops colors blend linearly in the numbers the colors are given in,
/// which is the image's encoded values (the same as CSS, SVG and image editors do by default), not in linear light.
/// </para>
/// <para>
/// By default blending is premultiplied: red to fully transparent blue stays red while it fades, as in CSS. With <c>premultiplied: false</c>
/// the straight colors blend instead, so that red to transparent blue passes through purple; ImageSharp.Drawing does this. The two
/// agree when all stops are opaque. In a fill with <c>linear: true</c> the stop colors are converted to linear light first, so
/// blending happens in linear light and the fill then converts the same way as everything else.
/// </para>
/// </summary>
/// <param name="Position">Where the stop sits along the gradient, as a fraction of its length.</param>
/// <param name="Color">The color, with straight alpha.</param>
public readonly record struct ColorStop(float Position, Rgba32 Color);

/// <summary>What a gradient paints beyond its 0..1 range.</summary>
public enum GradientRepetition
{
    /// <summary>The end colors continue: the first color before 0 and the last after 1.</summary>
    None = 0,

    /// <summary>The gradient starts over each unit: position 1.25 looks like 0.25. Exactly 1 shows the start color.</summary>
    Repeat = 1,

    /// <summary>The gradient runs forward, then backward, then forward: 1.25 looks like 0.75.</summary>
    Reflect = 2,

    /// <summary>Nothing is painted outside 0..1 (both ends inclusive).</summary>
    DontFill = 3,
}

/// <summary>Preset patterns for <see cref="Brush.Hatch"/>. Each repeats every 4 pixels in both directions, anchored at the image origin.</summary>
public enum HatchStyle
{
    /// <summary>A horizontal line on rows 1, 5, 9, …</summary>
    Horizontal = 0,

    /// <summary>A vertical line on columns 1, 5, 9, …</summary>
    Vertical = 1,

    /// <summary>Diagonal lines rising to the right: pixels with (x + y) mod 4 = 3.</summary>
    ForwardDiagonal = 2,

    /// <summary>Diagonal lines falling to the right: pixels with (x − y) mod 4 = 0.</summary>
    BackwardDiagonal = 3,

    /// <summary>Sparse dots: 2 of every 16 pixels, at (0, 0) and (2, 2) of each cell.</summary>
    Percent10 = 4,

    /// <summary>Dots in a checker-like arrangement: 2 of every 8 pixels, at (0, 0) and (2, 1) of each 4×2 cell.</summary>
    Percent20 = 5,

    /// <summary>A thin horizontal line on rows 3, 7, 11, …</summary>
    Min = 6,
}
