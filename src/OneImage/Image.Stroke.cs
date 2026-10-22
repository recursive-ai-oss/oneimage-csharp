using System.Numerics;
using OneImage.Drawing;
using Path = OneImage.Drawing.Path;

namespace OneImage;

public sealed partial class Image
{
    /// <summary>
    /// Strokes <paramref name="path"/> with <paramref name="pen"/> (lazy): paints the line along the path, with the pen's width, joins,
    /// caps and dashes, in the pen's brush. Everything else (anti-aliasing, color space, blending, opacity, transform, animation, failure)
    /// works as for <see cref="Fill(Brush, Path, bool, IntersectionRule, float, BlendMode, AlphaComposition, bool, Matrix3x2?, Path, IntersectionRule)"/>.
    /// <para>
    /// <b>How.</b> The path is turned into the outline of its stroke (<see cref="PathStrokeExtensions.GenerateOutline(Path, Pen, float)"/>) when this
    /// method is called, and that outline is filled with the non-zero rule, so the stroke's own overlaps (a self-crossing line, an inside
    /// corner) are painted once, not twice: a translucent pen is as even as an opaque one. The stroke is centered on the path: half the width
    /// lies on each side. Pixel coordinates as for fills: a line of width 1 along y = 10.5 covers row 10 exactly; along y = 10 it covers half of
    /// row 9 and half of row 10. A <paramref name="transform"/> is applied to the outline, so it scales the width too (as in SVG).
    /// </para>
    /// <para>
    /// Compared with a fill, building the outline takes time proportional to the whole path at call time, even where it lies outside the image;
    /// a path or dash pattern that is too complex throws <see cref="ImageLimitException"/> here. Where two stroke parts overlap inside one
    /// pixel along an edge, the coverage is slightly higher than the exact union.
    /// </para>
    /// </summary>
    /// <param name="pen">Width, join, cap, dashes and brush.</param>
    /// <param name="path">The center line.</param>
    /// <param name="antialias">Smooth edges. The default is <see langword="true"/>.</param>
    /// <param name="opacity">Multiplier for the brush alpha, 0 to 1. The default is 1.</param>
    /// <param name="blend">Blend mode. The default is <see cref="BlendMode.Normal"/>.</param>
    /// <param name="composition">Porter-Duff operator. The default is <see cref="AlphaComposition.SrcOver"/>.</param>
    /// <param name="linear">Blend in linear light. The default is <see langword="false"/>.</param>
    /// <param name="transform">Applied to the stroke outline (so it scales the width too). The default is none.</param>
    /// <param name="clip">Limits the drawing to the inside of this path (in image pixel coordinates, not moved by <paramref name="transform"/>): its coverage multiplies the stroke's, so only pixels inside both change, and edges blend smoothly. The default is no clip. With <paramref name="antialias"/> off the clip is sampled at pixel centers.</param>
    /// <param name="clipRule">Inside rule for the clip path. The default is <see cref="IntersectionRule.NonZero"/>.</param>
    /// <returns>The image with the stroke painted.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="pen"/> or <paramref name="path"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">An argument is out of range.</exception>
    /// <exception cref="ImageOperationException">The image is CMYK or Lab, or the composition would make an image without alpha transparent.</exception>
    /// <exception cref="ImageLimitException">The stroke is too complex.</exception>
    public Image Draw(Pen pen, Path path, bool antialias = true, float opacity = 1f, BlendMode blend = BlendMode.Normal, AlphaComposition composition = AlphaComposition.SrcOver, bool linear = false, Matrix3x2? transform = null, Path? clip = null, IntersectionRule clipRule = IntersectionRule.NonZero)
    {
        ArgumentNullException.ThrowIfNull(pen);
        ArgumentNullException.ThrowIfNull(path);
        FillSettings s = FillSettings.Check(antialias, IntersectionRule.NonZero, opacity, blend, composition, linear, transform, Format, clip, clipRule);
        Path outline = path.GenerateOutline(pen, PathStrokeExtensions.ToleranceFor(transform));
        return FillCore(pen.Brush, outline, s, new FillLabel("stroke", path, pen.Describe()));
    }

    /// <summary>Strokes <paramref name="path"/> with a solid pen of one color and width; see <c>Draw(Pen, Path, ...)</c>. Joins are miter (limit 4), ends are butt.</summary>
    /// <param name="color">The color, straight alpha, in the image's color space.</param>
    /// <param name="width">Line width in pixels.</param>
    /// <param name="path">The center line.</param>
    /// <param name="antialias">Smooth edges. The default is <see langword="true"/>.</param>
    /// <param name="opacity">Multiplier for the brush alpha, 0 to 1. The default is 1.</param>
    /// <param name="blend">Blend mode. The default is <see cref="BlendMode.Normal"/>.</param>
    /// <param name="composition">Porter-Duff operator. The default is <see cref="AlphaComposition.SrcOver"/>.</param>
    /// <param name="linear">Blend in linear light. The default is <see langword="false"/>.</param>
    /// <param name="transform">Applied to the stroke outline (so it scales the width too). The default is none.</param>
    /// <param name="clip">Limits the drawing to the inside of this path (in image pixel coordinates, not moved by <paramref name="transform"/>): its coverage multiplies the stroke's, so only pixels inside both change, and edges blend smoothly. The default is no clip. With <paramref name="antialias"/> off the clip is sampled at pixel centers.</param>
    /// <param name="clipRule">Inside rule for the clip path. The default is <see cref="IntersectionRule.NonZero"/>.</param>
    /// <returns>The image with the stroke painted.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">An argument is out of range.</exception>
    public Image Draw(Rgba32 color, float width, Path path, bool antialias = true, float opacity = 1f, BlendMode blend = BlendMode.Normal, AlphaComposition composition = AlphaComposition.SrcOver, bool linear = false, Matrix3x2? transform = null, Path? clip = null, IntersectionRule clipRule = IntersectionRule.NonZero)
        => Draw(Pen.Solid(color, width), path, antialias, opacity, blend, composition, linear, transform, clip, clipRule);

    /// <summary>Draws a straight line from <paramref name="from"/> to <paramref name="to"/> with <paramref name="pen"/>; see <c>Draw</c> for every rule. Identical end points draw nothing.</summary>
    /// <param name="pen">Width, join, cap, dashes and brush.</param>
    /// <param name="from">Start point.</param>
    /// <param name="to">End point.</param>
    /// <param name="antialias">Smooth edges. The default is <see langword="true"/>.</param>
    /// <param name="opacity">Multiplier for the brush alpha, 0 to 1. The default is 1.</param>
    /// <param name="blend">Blend mode. The default is <see cref="BlendMode.Normal"/>.</param>
    /// <param name="composition">Porter-Duff operator. The default is <see cref="AlphaComposition.SrcOver"/>.</param>
    /// <param name="linear">Blend in linear light. The default is <see langword="false"/>.</param>
    /// <param name="transform">Applied to the stroke outline (so it scales the width too). The default is none.</param>
    /// <returns>The image with the line painted.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="pen"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">An argument is out of range.</exception>
    public Image DrawLine(Pen pen, Vector2 from, Vector2 to, bool antialias = true, float opacity = 1f, BlendMode blend = BlendMode.Normal, AlphaComposition composition = AlphaComposition.SrcOver, bool linear = false, Matrix3x2? transform = null)
        => Draw(pen, Path.Polyline([from, to]), antialias, opacity, blend, composition, linear, transform);

    /// <summary>Draws an open chain of straight lines through <paramref name="points"/>; see <c>Draw</c>.</summary>
    /// <param name="pen">Width, join, cap, dashes and brush.</param>
    /// <param name="points">The vertices, at least two.</param>
    /// <param name="antialias">Smooth edges. The default is <see langword="true"/>.</param>
    /// <param name="opacity">Multiplier for the brush alpha, 0 to 1. The default is 1.</param>
    /// <param name="blend">Blend mode. The default is <see cref="BlendMode.Normal"/>.</param>
    /// <param name="composition">Porter-Duff operator. The default is <see cref="AlphaComposition.SrcOver"/>.</param>
    /// <param name="linear">Blend in linear light. The default is <see langword="false"/>.</param>
    /// <param name="transform">Applied to the stroke outline (so it scales the width too). The default is none.</param>
    /// <returns>The image with the line painted.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="pen"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">An argument is out of range.</exception>
    public Image DrawPolyline(Pen pen, ReadOnlySpan<Vector2> points, bool antialias = true, float opacity = 1f, BlendMode blend = BlendMode.Normal, AlphaComposition composition = AlphaComposition.SrcOver, bool linear = false, Matrix3x2? transform = null)
        => Draw(pen, Path.Polyline(points), antialias, opacity, blend, composition, linear, transform);

    /// <summary>Draws the outline of a closed polygon through <paramref name="points"/>; see <c>Draw</c>.</summary>
    /// <param name="pen">Width, join, cap, dashes and brush.</param>
    /// <param name="points">The vertices, at least three.</param>
    /// <param name="antialias">Smooth edges. The default is <see langword="true"/>.</param>
    /// <param name="opacity">Multiplier for the brush alpha, 0 to 1. The default is 1.</param>
    /// <param name="blend">Blend mode. The default is <see cref="BlendMode.Normal"/>.</param>
    /// <param name="composition">Porter-Duff operator. The default is <see cref="AlphaComposition.SrcOver"/>.</param>
    /// <param name="linear">Blend in linear light. The default is <see langword="false"/>.</param>
    /// <param name="transform">Applied to the stroke outline (so it scales the width too). The default is none.</param>
    /// <returns>The image with the line painted.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="pen"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">An argument is out of range.</exception>
    public Image DrawPolygon(Pen pen, ReadOnlySpan<Vector2> points, bool antialias = true, float opacity = 1f, BlendMode blend = BlendMode.Normal, AlphaComposition composition = AlphaComposition.SrcOver, bool linear = false, Matrix3x2? transform = null)
        => Draw(pen, Path.Polygon(points), antialias, opacity, blend, composition, linear, transform);

    /// <summary>Draws a chain of cubic Bézier curves: the first point, then three points (two controls and an end) per curve; see <c>Draw</c>.</summary>
    /// <param name="pen">Width, join, cap, dashes and brush.</param>
    /// <param name="points">1 + 3 × n points, n at least 1.</param>
    /// <param name="antialias">Smooth edges. The default is <see langword="true"/>.</param>
    /// <param name="opacity">Multiplier for the brush alpha, 0 to 1. The default is 1.</param>
    /// <param name="blend">Blend mode. The default is <see cref="BlendMode.Normal"/>.</param>
    /// <param name="composition">Porter-Duff operator. The default is <see cref="AlphaComposition.SrcOver"/>.</param>
    /// <param name="linear">Blend in linear light. The default is <see langword="false"/>.</param>
    /// <param name="transform">Applied to the stroke outline (so it scales the width too). The default is none.</param>
    /// <returns>The image with the line painted.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="pen"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">An argument is out of range.</exception>
    /// <exception cref="ArgumentException"><paramref name="points"/> does not hold 1 + 3 × n points.</exception>
    public Image DrawBeziers(Pen pen, ReadOnlySpan<Vector2> points, bool antialias = true, float opacity = 1f, BlendMode blend = BlendMode.Normal, AlphaComposition composition = AlphaComposition.SrcOver, bool linear = false, Matrix3x2? transform = null)
        => Draw(pen, StrokeShapes.Beziers(points), antialias, opacity, blend, composition, linear, transform);

    /// <summary>Draws the outline of a rectangle; see <c>Draw</c>. The stroke is centered on the rectangle's edge.</summary>
    /// <param name="pen">Width, join, cap, dashes and brush.</param>
    /// <param name="x">Left edge.</param>
    /// <param name="y">Top edge.</param>
    /// <param name="width">Width of the rectangle.</param>
    /// <param name="height">Height of the rectangle.</param>
    /// <param name="antialias">Smooth edges. The default is <see langword="true"/>.</param>
    /// <param name="opacity">Multiplier for the brush alpha, 0 to 1. The default is 1.</param>
    /// <param name="blend">Blend mode. The default is <see cref="BlendMode.Normal"/>.</param>
    /// <param name="composition">Porter-Duff operator. The default is <see cref="AlphaComposition.SrcOver"/>.</param>
    /// <param name="linear">Blend in linear light. The default is <see langword="false"/>.</param>
    /// <param name="transform">Applied to the stroke outline (so it scales the width too). The default is none.</param>
    /// <returns>The image with the line painted.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="pen"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">An argument is out of range.</exception>
    public Image DrawRectangle(Pen pen, float x, float y, float width, float height, bool antialias = true, float opacity = 1f, BlendMode blend = BlendMode.Normal, AlphaComposition composition = AlphaComposition.SrcOver, bool linear = false, Matrix3x2? transform = null)
        => Draw(pen, Path.Rectangle(x, y, width, height), antialias, opacity, blend, composition, linear, transform);

    /// <summary>Draws the outline of an ellipse; see <c>Draw</c>. The stroke is centered on the ellipse's edge.</summary>
    /// <param name="pen">Width, join, cap, dashes and brush.</param>
    /// <param name="cx">Center x.</param>
    /// <param name="cy">Center y.</param>
    /// <param name="rx">Horizontal radius.</param>
    /// <param name="ry">Vertical radius.</param>
    /// <param name="antialias">Smooth edges. The default is <see langword="true"/>.</param>
    /// <param name="opacity">Multiplier for the brush alpha, 0 to 1. The default is 1.</param>
    /// <param name="blend">Blend mode. The default is <see cref="BlendMode.Normal"/>.</param>
    /// <param name="composition">Porter-Duff operator. The default is <see cref="AlphaComposition.SrcOver"/>.</param>
    /// <param name="linear">Blend in linear light. The default is <see langword="false"/>.</param>
    /// <param name="transform">Applied to the stroke outline (so it scales the width too). The default is none.</param>
    /// <returns>The image with the line painted.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="pen"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">An argument is out of range.</exception>
    public Image DrawEllipse(Pen pen, float cx, float cy, float rx, float ry, bool antialias = true, float opacity = 1f, BlendMode blend = BlendMode.Normal, AlphaComposition composition = AlphaComposition.SrcOver, bool linear = false, Matrix3x2? transform = null)
        => Draw(pen, Path.Ellipse(cx, cy, rx, ry), antialias, opacity, blend, composition, linear, transform);
}
