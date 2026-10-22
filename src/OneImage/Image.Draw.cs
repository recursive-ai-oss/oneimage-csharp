using System.Numerics;
using OneImage.Drawing;
using Path = OneImage.Drawing.Path;
using OneImage.Kernels;

namespace OneImage;

public sealed partial class Image
{
    // Far enough out that every edge of the rectangle lies outside any image the engine allows, so the whole image is inside.
    private static readonly Path EverythingPath = Path.Rectangle(-1, -1, (1 << 24) + 2, (1 << 24) + 2);

    /// <summary>
    /// Fills the inside of <paramref name="path"/> with <paramref name="brush"/> (lazy), anti-aliased by default.
    /// <para>
    /// <b>Geometry.</b> Path coordinates are pixel coordinates: x to the right, y down, pixel (i, j) covering [i, i+1) × [j, j+1),
    /// so a pixel center is at (i + 0.5, j + 0.5). Every figure is filled as if closed. Parts of the path outside the image are
    /// ignored. The result has the input's size, format and metadata; only tiles the path can touch are computed, and every other
    /// tile is the input's own tile.
    /// </para>
    /// <para>
    /// <b>Coverage.</b> With <paramref name="antialias"/> each pixel gets the exact fraction of its area inside the shape (curves
    /// are first flattened to straight segments within 0.1 pixel). Where parts of the outline of opposite direction cross inside one
    /// pixel the areas cancel, so such a pixel can be slightly under-covered. Without it a pixel is either fully in or fully out
    /// according to its center. <paramref name="rule"/> decides what is inside for self-overlapping paths.
    /// </para>
    /// <para>
    /// <b>Color.</b> The brush color is used as numbers in the image's own color space (an sRGB image takes sRGB values; no
    /// profile conversion happens). The operator (<paramref name="blend"/> with <paramref name="composition"/>) is applied at full
    /// strength with the brush color scaled by <paramref name="opacity"/>, and the result is mixed with the existing pixel by the
    /// coverage; for Normal source-over that is the usual alpha blend. By default values are blended as stored (encoded), as image
    /// editors do; with <paramref name="linear"/> the fill happens in linear light (the base is converted for the tiles the path
    /// can touch, which may change those pixels by rounding). Images without alpha accept only the compositions that keep an opaque
    /// backdrop opaque (SrcOver, SrcAtop, Dest, DestOver). 8-bit RGB and RGBA with Normal/SrcOver are computed in float and rounded
    /// half up; the other formats go through the premultiplied float blend kernels. Results are identical for any thread count.
    /// </para>
    /// <para>
    /// <b>Failure.</b> Argument problems throw at once. A path that flattens to too many segments
    /// (<see cref="Path.MaxFlattenedPoints"/>) or a transform that leaves the finite range fails with
    /// <see cref="ImageLimitException"/> or <see cref="ImageOperationException"/> when the pixels are computed, not here.
    /// </para>
    /// <para>
    /// <b>Animation.</b> Every frame gets the same fill, with the path in each frame's own pixel grid.
    /// </para>
    /// </summary>
    /// <param name="brush">What to paint with; see <see cref="Brush"/> for solid, gradient, pattern, image and recolor brushes.</param>
    /// <param name="path">The outline.</param>
    /// <param name="antialias">Smooth edges. The default is <see langword="true"/>.</param>
    /// <param name="rule">Inside rule for overlapping parts. The default is <see cref="IntersectionRule.NonZero"/>.</param>
    /// <param name="opacity">Multiplier for the brush alpha, 0 to 1. The default is 1.</param>
    /// <param name="blend">Blend mode. The default is <see cref="BlendMode.Normal"/>.</param>
    /// <param name="composition">Porter-Duff operator. The default is <see cref="AlphaComposition.SrcOver"/>.</param>
    /// <param name="linear">Blend in linear light instead of the encoded space. The default is <see langword="false"/>.</param>
    /// <param name="transform">Applied to the path before filling (row-vector convention of <see cref="Matrix3x2"/>). The default is none.</param>
    /// <param name="clip">Limits the drawing to the inside of this path (in image pixel coordinates, not moved by <paramref name="transform"/>): its coverage multiplies the shape's, so only pixels inside both change, and edges blend smoothly. The default is no clip. With <paramref name="antialias"/> off the clip is sampled at pixel centers.</param>
    /// <param name="clipRule">Inside rule for the clip path. The default is <see cref="IntersectionRule.NonZero"/>.</param>
    /// <returns>The image with the shape painted.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="brush"/> or <paramref name="path"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="opacity"/> is outside 0..1 (or NaN), an enum is undefined, or the transform is not finite.</exception>
    /// <exception cref="ImageOperationException">The image is CMYK or Lab, or the composition would make an image without alpha transparent.</exception>
    public Image Fill(Brush brush, Path path, bool antialias = true, IntersectionRule rule = IntersectionRule.NonZero, float opacity = 1f, BlendMode blend = BlendMode.Normal, AlphaComposition composition = AlphaComposition.SrcOver, bool linear = false, Matrix3x2? transform = null, Path? clip = null, IntersectionRule clipRule = IntersectionRule.NonZero)
    {
        ArgumentNullException.ThrowIfNull(brush);
        ArgumentNullException.ThrowIfNull(path);
        return FillCore(brush, path, FillSettings.Check(antialias, rule, opacity, blend, composition, linear, transform, Format, clip, clipRule));
    }

    /// <summary>Fills <paramref name="path"/> with one color; see <see cref="Fill(Brush, Path, bool, IntersectionRule, float, BlendMode, AlphaComposition, bool, Matrix3x2?, Path, IntersectionRule)"/> for every detail.</summary>
    /// <param name="color">The color, straight alpha, in the image's color space.</param>
    /// <param name="path">The outline.</param>
    /// <param name="antialias">Smooth edges. The default is <see langword="true"/>.</param>
    /// <param name="rule">Inside rule for overlapping parts. The default is <see cref="IntersectionRule.NonZero"/>.</param>
    /// <param name="opacity">Multiplier for the color's alpha, 0 to 1. The default is 1.</param>
    /// <param name="blend">Blend mode. The default is <see cref="BlendMode.Normal"/>.</param>
    /// <param name="composition">Porter-Duff operator. The default is <see cref="AlphaComposition.SrcOver"/>.</param>
    /// <param name="linear">Blend in linear light. The default is <see langword="false"/>.</param>
    /// <param name="transform">Applied to the path before filling. The default is none.</param>
    /// <param name="clip">Limits the drawing to the inside of this path (in image pixel coordinates, not moved by <paramref name="transform"/>): its coverage multiplies the shape's, so only pixels inside both change, and edges blend smoothly. The default is no clip. With <paramref name="antialias"/> off the clip is sampled at pixel centers.</param>
    /// <param name="clipRule">Inside rule for the clip path. The default is <see cref="IntersectionRule.NonZero"/>.</param>
    /// <returns>The image with the shape painted.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">An argument is out of range.</exception>
    /// <exception cref="ImageOperationException">The image is CMYK or Lab, or the composition would make an image without alpha transparent.</exception>
    public Image Fill(Rgba32 color, Path path, bool antialias = true, IntersectionRule rule = IntersectionRule.NonZero, float opacity = 1f, BlendMode blend = BlendMode.Normal, AlphaComposition composition = AlphaComposition.SrcOver, bool linear = false, Matrix3x2? transform = null, Path? clip = null, IntersectionRule clipRule = IntersectionRule.NonZero) =>
        Fill(Brush.Solid(color), path, antialias, rule, opacity, blend, composition, linear, transform, clip, clipRule);

    /// <summary>
    /// Lays one color over every pixel (lazy), combined with the existing pixels by <paramref name="blend"/> and
    /// <paramref name="composition"/> exactly as a fill of a shape covering the whole image would. Use <see cref="Clear"/> to replace the pixels instead.
    /// </summary>
    /// <param name="color">The color, straight alpha, in the image's color space.</param>
    /// <param name="opacity">Multiplier for the color's alpha, 0 to 1. The default is 1.</param>
    /// <param name="blend">Blend mode. The default is <see cref="BlendMode.Normal"/>.</param>
    /// <param name="composition">Porter-Duff operator. The default is <see cref="AlphaComposition.SrcOver"/>.</param>
    /// <param name="linear">Blend in linear light. The default is <see langword="false"/>.</param>
    /// <returns>The filled image.</returns>
    /// <exception cref="ArgumentOutOfRangeException">An argument is out of range.</exception>
    /// <exception cref="ImageOperationException">The image is CMYK or Lab, or the composition would make an image without alpha transparent.</exception>
    public Image Fill(Rgba32 color, float opacity = 1f, BlendMode blend = BlendMode.Normal, AlphaComposition composition = AlphaComposition.SrcOver, bool linear = false) =>
        Fill(Brush.Solid(color), EverythingPath, false, IntersectionRule.NonZero, opacity, blend, composition, linear);

    /// <summary>
    /// Replaces every pixel with <paramref name="color"/> (lazy). On an image with alpha the color's alpha is kept (a transparent
    /// color makes a transparent image); on an image without alpha the color is flattened over the existing pixels, so only an
    /// opaque color replaces them.
    /// </summary>
    /// <param name="color">The color, straight alpha, in the image's color space.</param>
    /// <returns>The cleared image.</returns>
    /// <exception cref="ImageOperationException">The image is CMYK or Lab.</exception>
    public Image Clear(Rgba32 color) =>
        Fill(Brush.Solid(color), EverythingPath, false, IntersectionRule.NonZero, 1f, BlendMode.Normal, Format.HasAlpha ? AlphaComposition.Src : AlphaComposition.SrcOver);

    /// <summary>The first frame's node (a canvas snapshot has exactly one).</summary>
    internal Node Root => _frames[0].Node;

    internal Image FillCore(Brush brush, Path path, in FillSettings s, FillLabel? label = null)
    {
        var nodes = new Node[_frames.Length];
        var delays = new TimeSpan[nodes.Length];
        for (int i = 0; i < nodes.Length; i++)
        {
            delays[i] = _frames[i].Delay;
        }

        if (!s.Linear)
        {
            FillPlan plan = FillPlan.Create(Format, brush, s);
            for (int i = 0; i < nodes.Length; i++)
            {
                Node n = _frames[i].Node;
                nodes[i] = new FillNode(n, new RasterSource(path, s.Transform, n.Width, n.Height, s.Tolerance), plan, FillNode.Describe("fill", brush, path, s, label), ClipSource(s, n));
            }

            return new Image(nodes, delays, LoopCount, Metadata, SourceFormat);
        }

        // Linear light: fill in the linear variant of the profile, convert back, and keep the untouched tiles from the original.
        Image lin = ToLinearLight(out ColorProfile original);
        float[]? solid = brush.TryGetSolid(out Rgba32 c) ? LinearSolid(c, original, lin.Format) : null;
        Brush linBrush = solid != null ? brush : brush.Linearized(ColorMap.Create(this));
        FillPlan linPlan = FillPlan.Create(lin.Format, linBrush, s, solid);
        var sources = new RasterSource[nodes.Length];
        var filled = new Node[nodes.Length];
        for (int i = 0; i < nodes.Length; i++)
        {
            Node ln = lin._frames[i].Node;
            sources[i] = new RasterSource(path, s.Transform, ln.Width, ln.Height, s.Tolerance);
            filled[i] = new FillNode(ln, sources[i], linPlan, FillNode.Describe("fill", brush, path, s, label), ClipSource(s, ln));
        }

        Image processed = new Image(filled, delays, LoopCount, lin.Metadata, SourceFormat).FromLinearLight(original, Format);
        for (int i = 0; i < nodes.Length; i++)
        {
            nodes[i] = new FillSelectNode(_frames[i].Node, processed._frames[i].Node, sources[i], FillNode.Describe("fill-linear", brush, path, s, label));
        }

        return new Image(nodes, delays, LoopCount, Metadata, SourceFormat);
    }

    private static RasterSource? ClipSource(in FillSettings s, Node n) =>
        s.Clip == null ? null : new RasterSource(s.Clip, System.Numerics.Matrix3x2.Identity, n.Width, n.Height, s.Tolerance);

    // The brush color in linear light, premultiplied, in the working format of a float fill: a one-pixel image carries it through
    // the same profile conversion the pixels get.
    private static float[] LinearSolid(Rgba32 color, ColorProfile original, PixelFormat linearFormat)
    {
        using var canvas = new Canvas(1, 1, PixelFormat.Rgba8);
        canvas.CopyFrom<Rgba32>([color], new ImageRect(0, 0, 1, 1));
        Image px = canvas.View();
        if (original.Model == ColorModel.Gray)
        {
            px = px.ConvertTo(PixelFormat.GrayAlpha8);
        }

        Image lin = px.AssignProfile(original).ToLinearLight(out _);
        var work = new PixelFormat(linearFormat.Model, SampleType.F32, AlphaMode.Premultiplied);
        var values = new float[work.ChannelCount];
        Render.ReadRegion(lin._frames[0].Node, new ImageRect(0, 0, 1, 1), SpanCast.AsBytes(values.AsSpan()), 0, work, RenderContext.Create(1, CancellationToken.None));

        // The one-pixel graph is done: give back its tiles rather than leave a pooled buffer to the garbage collector.
        Node.ReleaseAfterSave([lin._frames[0].Node]);
        return values;
    }
}
