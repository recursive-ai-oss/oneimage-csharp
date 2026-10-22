using System.Globalization;
using System.Numerics;

namespace OneImage.Drawing;

/// <summary>
/// Says what color a fill puts down at each pixel. A fill asks its brush for one row of colors at a time, after the
/// rasterizer has decided how much of each pixel the shape covers; the brush never sees geometry.
/// <para>
/// Create brushes with <see cref="Solid"/>, <see cref="LinearGradient(Vector2, Vector2, ReadOnlySpan{ColorStop}, GradientRepetition, bool)"/>, <see cref="RadialGradient(Vector2, float, ReadOnlySpan{ColorStop}, GradientRepetition, bool)"/>, <see cref="EllipticGradient"/>,
/// <see cref="PathGradient"/>, <see cref="Pattern"/>, <see cref="Hatch"/>, <see cref="FromImage(Image, ImageRect?, int, int, Matrix3x2?, bool)"/> and <see cref="Recolor"/>, or derive
/// your own by implementing <see cref="FillRow(int, int, Span{float})"/>.
/// </para>
/// <para>
/// <b>Coordinates.</b> Brushes work in the pixel grid of the image being filled: pixel (x, y) has its center at (x + 0.5, y + 0.5).
/// A brush does not move with the shape it fills, and the fill's <c>transform</c> argument moves only the path. To place a pattern
/// or gradient relative to a shape, build the brush with that shape's position.
/// </para>
/// <para>
/// <b>Colors.</b> Brush colors are numbers in the image's own color space and encoding, like <see cref="Solid"/>. In a fill with
/// <c>linear: true</c> they are converted to linear light first; see the gradient brushes for how interpolation is affected.
/// </para>
/// </summary>
public abstract class Brush
{
    /// <summary>Initializes a custom brush.</summary>
    protected Brush()
    {
    }

    /// <summary>A brush that paints one color everywhere. The color's numbers are used as they are, in the image's own color space and encoding.</summary>
    /// <param name="color">The color, with straight (non-premultiplied) alpha.</param>
    /// <returns>The brush.</returns>
    public static Brush Solid(Rgba32 color) => new SolidBrush(color);

    /// <summary>
    /// A gradient along the line from <paramref name="start"/> to <paramref name="end"/>. A pixel's position along the gradient is the
    /// projection of its center on that line, 0 at <paramref name="start"/> and 1 at <paramref name="end"/>; lines of equal color are
    /// perpendicular to it.
    /// </summary>
    /// <param name="start">Where the gradient is at 0, in pixel coordinates.</param>
    /// <param name="end">Where the gradient is at 1. Must differ from <paramref name="start"/>.</param>
    /// <param name="stops">Color stops; see <see cref="ColorStop"/> for the rules. At least one is required.</param>
    /// <param name="repetition">What happens outside 0..1. The default is <see cref="GradientRepetition.None"/>.</param>
    /// <param name="premultiplied">Interpolate premultiplied colors (the default), or straight colors as ImageSharp.Drawing does; see <see cref="ColorStop"/>.</param>
    /// <returns>The brush.</returns>
    /// <exception cref="ArgumentException">No stops, a non-finite stop position or point, or <paramref name="start"/> equals <paramref name="end"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="repetition"/> is undefined.</exception>
    public static Brush LinearGradient(Vector2 start, Vector2 end, ReadOnlySpan<ColorStop> stops, GradientRepetition repetition = GradientRepetition.None, bool premultiplied = true) =>
        new LinearGradientBrush(start, end, stops, repetition, premultiplied);

    /// <summary>
    /// A linear gradient whose line is placed through an affine <paramref name="transform"/>: <paramref name="start"/> and <paramref name="end"/>
    /// are in the gradient's own space, and a pixel's position is the projection of its center, taken back through the inverse of
    /// <paramref name="transform"/>, on that line. Lines of equal color are therefore perpendicular to the line in gradient space
    /// (skewed in the image if the transform skews). Positions are computed in double precision.
    /// </summary>
    /// <param name="start">Where the gradient is at 0, in gradient space.</param>
    /// <param name="end">Where the gradient is at 1, in gradient space. Must differ from <paramref name="start"/>.</param>
    /// <param name="stops">Color stops; see <see cref="ColorStop"/>. At least one is required.</param>
    /// <param name="transform">Maps gradient space to pixel coordinates (row-vector convention of <see cref="Matrix3x2"/>).</param>
    /// <param name="repetition">What happens outside 0..1. The default is <see cref="GradientRepetition.None"/>.</param>
    /// <param name="premultiplied">Interpolate premultiplied colors (the default), or straight colors; see <see cref="ColorStop"/>.</param>
    /// <returns>The brush.</returns>
    /// <exception cref="ArgumentException">No stops, a non-finite value, <paramref name="start"/> equals <paramref name="end"/>, or the transform is not finite and invertible.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="repetition"/> is undefined.</exception>
    public static Brush LinearGradient(Vector2 start, Vector2 end, ReadOnlySpan<ColorStop> stops, Matrix3x2 transform, GradientRepetition repetition = GradientRepetition.None, bool premultiplied = true) =>
        new MatrixLinearGradientBrush(start, end, stops, transform, repetition, premultiplied);

    /// <summary>
    /// A radial gradient between two circles (a focal gradient, as SVG 2 defines it): the gradient is 0 on the circle of radius
    /// <paramref name="focalRadius"/> around <paramref name="focalCenter"/> and 1 on the circle of radius <paramref name="radius"/>
    /// around <paramref name="center"/>, and a pixel takes the position of the circle, of those growing linearly between the two, that
    /// passes through its center, preferring the larger position and ignoring circles of negative radius. When the focal circle lies
    /// inside the end circle every pixel is reached; otherwise the circles form a cone and pixels outside it are transparent. Equal,
    /// concentric circles paint only their disc, in the first color. The gradient is placed through <paramref name="transform"/>.
    /// </summary>
    /// <param name="center">Center of the end circle, in gradient space.</param>
    /// <param name="radius">Radius of the end circle, positive.</param>
    /// <param name="focalCenter">Center of the start circle, in gradient space.</param>
    /// <param name="focalRadius">Radius of the start circle, zero or more.</param>
    /// <param name="stops">Color stops; see <see cref="ColorStop"/>. At least one is required.</param>
    /// <param name="transform">Maps gradient space to pixel coordinates (row-vector convention). The default is none.</param>
    /// <param name="repetition">What happens outside 0..1. The default is <see cref="GradientRepetition.None"/>.</param>
    /// <param name="premultiplied">Interpolate premultiplied colors (the default), or straight colors; see <see cref="ColorStop"/>.</param>
    /// <returns>The brush.</returns>
    /// <exception cref="ArgumentException">No stops, a non-finite value, or a transform that is not finite and invertible.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="radius"/> is not positive, <paramref name="focalRadius"/> is negative, or <paramref name="repetition"/> is undefined.</exception>
    public static Brush RadialGradient(Vector2 center, float radius, Vector2 focalCenter, float focalRadius, ReadOnlySpan<ColorStop> stops, Matrix3x2? transform = null, GradientRepetition repetition = GradientRepetition.None, bool premultiplied = true) =>
        new ConicalGradientBrush(focalCenter, focalRadius, center, radius, stops, transform ?? Matrix3x2.Identity, repetition, premultiplied);

    /// <summary>
    /// A gradient that is 0 at <paramref name="center"/> and 1 at distance <paramref name="radius"/>, changing linearly with distance.
    /// </summary>
    /// <param name="center">The center, in pixel coordinates.</param>
    /// <param name="radius">The distance at which the gradient reaches 1, positive.</param>
    /// <param name="stops">Color stops; see <see cref="ColorStop"/>. At least one is required.</param>
    /// <param name="repetition">What happens beyond the radius. The default is <see cref="GradientRepetition.None"/>.</param>
    /// <param name="premultiplied">Interpolate premultiplied colors (the default), or straight colors; see <see cref="ColorStop"/>.</param>
    /// <returns>The brush.</returns>
    /// <exception cref="ArgumentException">No stops, or a non-finite stop position, point or radius.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="radius"/> is not positive, or <paramref name="repetition"/> is undefined.</exception>
    public static Brush RadialGradient(Vector2 center, float radius, ReadOnlySpan<ColorStop> stops, GradientRepetition repetition = GradientRepetition.None, bool premultiplied = true) =>
        new RadialGradientBrush(center, radius, stops, repetition, premultiplied);

    /// <summary>
    /// A gradient over concentric ellipses. The first axis runs from <paramref name="center"/> to <paramref name="axisEnd"/> and
    /// reaches 1 at <paramref name="axisEnd"/>; the perpendicular axis is <paramref name="axisRatio"/> times as long.
    /// </summary>
    /// <param name="center">The center, in pixel coordinates.</param>
    /// <param name="axisEnd">The end of the first axis; must differ from <paramref name="center"/>. Its direction rotates the ellipse.</param>
    /// <param name="axisRatio">Length of the perpendicular axis divided by that of the first, positive.</param>
    /// <param name="stops">Color stops; see <see cref="ColorStop"/>. At least one is required.</param>
    /// <param name="repetition">What happens outside the ellipse. The default is <see cref="GradientRepetition.None"/>.</param>
    /// <param name="premultiplied">Interpolate premultiplied colors (the default), or straight colors; see <see cref="ColorStop"/>.</param>
    /// <param name="squaredDistance">
    /// Use the square of the normalized distance as the position, as ImageSharp.Drawing 2.1.7 does: colors then change slowly near
    /// the center and quickly near the edge. The default is <see langword="false"/>, which changes linearly with distance like <see cref="RadialGradient(Vector2, float, ReadOnlySpan{ColorStop}, GradientRepetition, bool)"/>.
    /// </param>
    /// <returns>The brush.</returns>
    /// <exception cref="ArgumentException">No stops, a non-finite value, or <paramref name="axisEnd"/> equals <paramref name="center"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="axisRatio"/> is not positive, or <paramref name="repetition"/> is undefined.</exception>
    public static Brush EllipticGradient(Vector2 center, Vector2 axisEnd, float axisRatio, ReadOnlySpan<ColorStop> stops, GradientRepetition repetition = GradientRepetition.None, bool premultiplied = true, bool squaredDistance = false) =>
        new EllipticGradientBrush(center, axisEnd, axisRatio, stops, repetition, premultiplied, squaredDistance);

    /// <summary>
    /// Blends colors from the corners of a polygon to a center color. The polygon is cut into triangles that share the center; inside
    /// each, the color is the barycentric blend of the center color and the two vertex colors. Pixels outside the polygon (all of the
    /// triangles) are transparent, which a fill's own shape normally hides. Colors blend as straight (non-premultiplied) values.
    /// </summary>
    /// <param name="points">The vertices in order, at least three, in pixel coordinates.</param>
    /// <param name="colors">One color per vertex.</param>
    /// <param name="centerColor">The color at the center; the default is the average of <paramref name="colors"/>.</param>
    /// <param name="center">The center; the default is the average of the vertices.</param>
    /// <returns>The brush.</returns>
    /// <exception cref="ArgumentException">Fewer than three points, a different number of colors, or a non-finite point.</exception>
    public static Brush PathGradient(ReadOnlySpan<Vector2> points, ReadOnlySpan<Rgba32> colors, Rgba32? centerColor = null, Vector2? center = null) =>
        new PathGradientBrush(points, colors, centerColor, center);

    /// <summary>
    /// Tiles a small bitmap of two colors: <paramref name="foreground"/> where the pattern is <see langword="true"/>, else
    /// <paramref name="background"/>. Cell (row, column) of the pattern covers one pixel; tile (0, 0) has its corner at the image origin.
    /// </summary>
    /// <param name="foreground">The color of set cells.</param>
    /// <param name="background">The color of clear cells.</param>
    /// <param name="pattern">Cells indexed [row, column]; each dimension from 1 to 4096.</param>
    /// <param name="transform">Maps pattern space to image space (row-vector convention of <see cref="Matrix3x2"/>), for scaling, rotation or shifting. Each pixel takes the cell under its center, no smoothing. The default is none.</param>
    /// <returns>The brush.</returns>
    /// <exception cref="ArgumentException">The pattern is empty or too large, or the transform is not finite and invertible.</exception>
    public static Brush Pattern(Rgba32 foreground, Rgba32 background, bool[,] pattern, Matrix3x2? transform = null) =>
        new PatternBrush(foreground, background, pattern, transform);

    /// <summary>A preset hatch pattern; see <see cref="HatchStyle"/> and <see cref="Pattern"/>.</summary>
    /// <param name="style">Which preset.</param>
    /// <param name="foreground">The color of the lines or dots.</param>
    /// <param name="background">The color between them. The default is transparent.</param>
    /// <param name="transform">As for <see cref="Pattern"/>. The default is none (one pattern cell per pixel).</param>
    /// <returns>The brush.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="style"/> is undefined.</exception>
    /// <exception cref="ArgumentException">The transform is not finite and invertible.</exception>
    public static Brush Hatch(HatchStyle style, Rgba32 foreground, Rgba32 background = default, Matrix3x2? transform = null) =>
        new PatternBrush(foreground, background, PatternBrush.Preset(style), transform);

    /// <summary>
    /// Paints with the pixels of an image, repeated like tiles. The source is read on first use, as 8-bit straight RGBA, and its
    /// first frame is used. Source pixel (0, 0) of <paramref name="region"/> lands at the image origin shifted by the offset.
    /// </summary>
    /// <param name="source">The picture to paint with. Its pixels are not read until a tile is painted, and a failure to read them surfaces then.</param>
    /// <param name="region">The part of the source to use. The default is all of it.</param>
    /// <param name="offsetX">Moves the tiling right by this many pixels. The default is 0.</param>
    /// <param name="offsetY">Moves the tiling down. The default is 0.</param>
    /// <param name="transform">Maps source space to image space (row-vector convention), applied before the offset. Each pixel takes the source pixel under its center, no smoothing. The default is none.</param>
    /// <param name="tile">Repeat the source (the default); otherwise pixels outside the placed source are transparent.</param>
    /// <returns>The brush.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="region"/> is empty or outside the source.</exception>
    /// <exception cref="ArgumentException">The transform is not finite and invertible.</exception>
    public static Brush FromImage(Image source, ImageRect? region = null, int offsetX = 0, int offsetY = 0, Matrix3x2? transform = null, bool tile = true) =>
        new ImageBrush(source, region, offsetX, offsetY, transform, tile);

    /// <summary>
    /// Like <see cref="FromImage(Image, ImageRect?, int, int, Matrix3x2?, bool)"/>, with a choice of how pixels between source pixel centers are read.
    /// </summary>
    /// <param name="source">The picture to paint with. Its pixels are not read until a tile is painted.</param>
    /// <param name="sampling">How a pixel reads the source; see <see cref="ImageSampling"/>.</param>
    /// <param name="region">The part of the source to use. The default is all of it.</param>
    /// <param name="offsetX">Moves the tiling right by this many pixels. The default is 0.</param>
    /// <param name="offsetY">Moves the tiling down. The default is 0.</param>
    /// <param name="transform">Maps source space to image space (row-vector convention), applied before the offset. The default is none.</param>
    /// <param name="tile">Repeat the source (the default); otherwise pixels outside the placed source are transparent, and with bilinear sampling the edge fades over half a source pixel.</param>
    /// <returns>The brush.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="region"/> is empty or outside the source, or <paramref name="sampling"/> is undefined.</exception>
    /// <exception cref="ArgumentException">The transform is not finite and invertible.</exception>
    public static Brush FromImage(Image source, ImageSampling sampling, ImageRect? region = null, int offsetX = 0, int offsetY = 0, Matrix3x2? transform = null, bool tile = true) =>
        new ImageBrush(source, region, offsetX, offsetY, transform, tile, sampling);

    /// <summary>
    /// Replaces a color in the existing pixels: pixels close to <paramref name="source"/> are painted toward <paramref name="target"/>,
    /// and the closer, the more. With distance d (Euclidean over straight R, G, B, A in 0..1) the strength is
    /// <c>max(0, 1 − d² / (4 · threshold))</c>, so it is 1 for an exact match and falls to 0 at d = 2·√threshold. The pixel
    /// then becomes <paramref name="target"/> mixed in by that strength (the target painted with its alpha times the strength).
    /// </summary>
    /// <param name="source">The color to look for.</param>
    /// <param name="target">The color to put in its place.</param>
    /// <param name="threshold">How far from <paramref name="source"/> colors are still affected, 0 or more. 0 changes exact matches only. The default is 0.1.</param>
    /// <returns>The brush.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="threshold"/> is negative or not finite.</exception>
    public static Brush Recolor(Rgba32 source, Rgba32 target, float threshold = 0.1f) => new RecolorBrush(source, target, threshold);

    /// <summary>Gets the color if this brush paints one color everywhere, which lets fills skip the per-row work. The default is <see langword="false"/>.</summary>
    /// <param name="color">The color, when the result is <see langword="true"/>.</param>
    /// <returns><see langword="true"/> if the brush paints <paramref name="color"/> everywhere.</returns>
    public virtual bool TryGetSolid(out Rgba32 color)
    {
        color = default;
        return false;
    }

    /// <summary>
    /// Writes the brush color for the pixels (<paramref name="x"/>.., <paramref name="y"/>) as straight RGBA floats, four per pixel,
    /// usually in 0..1, in the image's color space. <c>rgba.Length / 4</c> pixels are wanted. Called from several threads at once on
    /// different rows and tiles, so it must not change shared state, and for the same pixel it must always give the same numbers
    /// (output must not depend on thread count). It must not throw for any coordinates, including far outside the image.
    /// </summary>
    /// <param name="x">Image column of the first pixel; may be negative or beyond the image for a brush used elsewhere.</param>
    /// <param name="y">Image row.</param>
    /// <param name="rgba">Where to write; its length is a multiple of 4.</param>
    public abstract void FillRow(int x, int y, Span<float> rgba);

    /// <summary>
    /// Gets a value indicating whether the brush needs the pixels already in the image to decide its color, as <see cref="Recolor"/> does.
    /// When <see langword="true"/>, fills call <see cref="FillRow(int, int, ReadOnlySpan{float}, Span{float})"/>. The default is <see langword="false"/>.
    /// </summary>
    public virtual bool ReadsBackdrop => false;

    /// <summary>
    /// Like <see cref="FillRow(int, int, Span{float})"/>, with the pixels currently in the image under the run. Only called when
    /// <see cref="ReadsBackdrop"/> is true. The default ignores the backdrop.
    /// </summary>
    /// <param name="x">Image column of the first pixel.</param>
    /// <param name="y">Image row.</param>
    /// <param name="backdrop">The existing pixels as straight RGBA floats, four per pixel (alpha 1 for images without alpha), in the space the fill works in: linear light when the fill is linear.</param>
    /// <param name="rgba">Where to write the brush color, same length as <paramref name="backdrop"/>.</param>
    public virtual void FillRow(int x, int y, ReadOnlySpan<float> backdrop, Span<float> rgba) => FillRow(x, y, rgba);

    /// <summary>
    /// Describes the brush in an op descriptor: language-neutral name and serializable parameters. The default says only that it is a
    /// custom brush, so graphs containing different custom brushes still compare unequal only by identity; override to describe yours.
    /// </summary>
    /// <returns>The parameters, starting with <c>brush</c> (the kind).</returns>
    public virtual IReadOnlyList<OpParameter> Describe() => [new("brush", "custom"), new("type", GetType().Name)];

    /// <summary>Images the brush reads from, to be listed as inputs of the fill node.</summary>
    internal virtual Node[] Sources => [];

    /// <summary>
    /// The brush as it should look when the fill works in linear light: the colors it holds (stops, vertices, foreground...) are
    /// mapped before they are mixed, so interpolation happens in linear light. The default maps each row the brush produces.
    /// </summary>
    internal virtual Brush Linearized(ColorMap map) => new MappedBrush(this, map);

    internal static string Hex(Rgba32 c) => string.Create(CultureInfo.InvariantCulture, $"{c.R:x2}{c.G:x2}{c.B:x2}{c.A:x2}");
}

/// <summary>A brush with one color.</summary>
internal sealed class SolidBrush(Rgba32 color) : Brush
{
    public override bool TryGetSolid(out Rgba32 c)
    {
        c = color;
        return true;
    }

    public override void FillRow(int x, int y, Span<float> rgba)
    {
        for (int i = 0; i + 3 < rgba.Length; i += 4)
        {
            rgba[i] = color.R / 255f;
            rgba[i + 1] = color.G / 255f;
            rgba[i + 2] = color.B / 255f;
            rgba[i + 3] = color.A / 255f;
        }
    }

    public override IReadOnlyList<OpParameter> Describe() =>
        [new("brush", "solid"), new("r", (long)color.R), new("g", (long)color.G), new("b", (long)color.B), new("a", (long)color.A)];
}

/// <summary>Wraps a brush and converts its output colors with a <see cref="ColorMap"/>.</summary>
internal sealed class MappedBrush(Brush inner, ColorMap map) : Brush
{
    public override bool ReadsBackdrop => inner.ReadsBackdrop;

    internal override Node[] Sources => inner.Sources;

    public override void FillRow(int x, int y, Span<float> rgba)
    {
        inner.FillRow(x, y, rgba);
        map.Apply(rgba);
    }

    public override void FillRow(int x, int y, ReadOnlySpan<float> backdrop, Span<float> rgba)
    {
        inner.FillRow(x, y, backdrop, rgba);
        map.Apply(rgba);
    }

    public override IReadOnlyList<OpParameter> Describe() => inner.Describe();
}
