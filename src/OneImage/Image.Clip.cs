using System.Numerics;
using OneImage.Drawing;
using Path = OneImage.Drawing.Path;

namespace OneImage;

public sealed partial class Image
{
    /// <summary>
    /// Runs <paramref name="operations"/> only inside <paramref name="path"/> (lazy): pixels outside the path stay as they are,
    /// pixels inside become the result of the operations, and pixels along the edge are a mix by the path's anti-aliased coverage.
    /// Any chain of operations works (blur, tone, fills, color conversion within the same pixel format...).
    /// <code>img.Clip(Path.Circle(300, 200, 120), i =&gt; i.GaussianBlur(8))</code>
    /// <para>
    /// <b>Evaluation.</b> <paramref name="operations"/> is called once, now, with this image; what it returns must be an image
    /// of the same size and frame count (built lazily like any other chain). Its pixels are converted to this image's pixel
    /// format if they differ. Tiles the path cannot reach are this image's own tiles and the operations are never computed for
    /// them; tiles wholly inside the path are the operations' own tiles, so inside the path the result equals the operation
    /// result byte for byte. Operations that look at neighbors (blur) see the whole image, not only the inside of the path,
    /// so a blurred edge mixes in the pixels around the path.
    /// </para>
    /// <para>
    /// <b>Coverage.</b> As for <see cref="Fill(Brush, Path, bool, IntersectionRule, float, BlendMode, AlphaComposition, bool, Matrix3x2?, Path, IntersectionRule)"/>:
    /// exact area per pixel when anti-aliased, the pixel center otherwise. Partly covered pixels are mixed in premultiplied float
    /// values (as a layer mask would) and rounded to the format. Every figure of the path is treated as closed.
    /// </para>
    /// <para>
    /// <b>Result.</b> Size, format, metadata and frames are those of this image; every frame is clipped with the path in its own
    /// pixel grid. Output is identical for any thread count, and <see cref="Canvas.Clip"/> writes the same bytes.
    /// </para>
    /// <para>
    /// <b>Failure.</b> Argument problems, an image that is not RGB or gray, and operations that change the size or frame count
    /// throw at once. Failures of the operations or of the path (too many segments) surface when pixels are computed.
    /// </para>
    /// </summary>
    /// <param name="path">The region, in pixel coordinates.</param>
    /// <param name="operations">Builds the processed image from the one it is given. Called once, synchronously.</param>
    /// <param name="antialias">Smooth edges. The default is <see langword="true"/>.</param>
    /// <param name="rule">Inside rule for overlapping parts of the path. The default is <see cref="IntersectionRule.NonZero"/>.</param>
    /// <param name="transform">Applied to the path first (row-vector convention of <see cref="Matrix3x2"/>). The default is none.</param>
    /// <returns>The image with the operations applied inside the path.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> or <paramref name="operations"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="rule"/> is undefined or the transform is not finite.</exception>
    /// <exception cref="ImageOperationException">The image is CMYK or Lab, <paramref name="operations"/> returned null or changed the size or frame count.</exception>
    public Image Clip(Path path, Func<Image, Image> operations, bool antialias = true, IntersectionRule rule = IntersectionRule.NonZero, Matrix3x2? transform = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(operations);
        if (rule is not (IntersectionRule.NonZero or IntersectionRule.EvenOdd))
        {
            throw new ArgumentOutOfRangeException(nameof(rule));
        }

        Matrix3x2 m = transform ?? Matrix3x2.Identity;
        if (!float.IsFinite(m.M11 + m.M12 + m.M21 + m.M22 + m.M31 + m.M32))
        {
            throw new ArgumentOutOfRangeException(nameof(transform), "The transform must be finite.");
        }

        if (Format.Model is not (ColorModel.Rgb or ColorModel.Gray))
        {
            throw new ImageOperationException("Clip works on RGB and gray images; convert CMYK and Lab first.");
        }

        Image processed = operations(this) ?? throw new ImageOperationException("The clip operations returned no image.");
        if (processed._frames.Length != _frames.Length || processed.Width != Width || processed.Height != Height)
        {
            throw new ImageOperationException("The operations inside a clip must keep the image size and frame count.");
        }

        if (processed.Format != Format)
        {
            processed = processed.ConvertTo(Format);
        }

        var nodes = new Node[_frames.Length];
        var delays = new TimeSpan[nodes.Length];
        for (int i = 0; i < nodes.Length; i++)
        {
            Node n = _frames[i].Node;
            delays[i] = _frames[i].Delay;
            if (processed._frames[i].Width != n.Width || processed._frames[i].Height != n.Height)
            {
                throw new ImageOperationException("The operations inside a clip must keep the size of every frame.");
            }

            var source = new RasterSource(path, m, n.Width, n.Height, Path.DefaultTolerance);
            nodes[i] = new ClipNode(n, processed._frames[i].Node, source, antialias, rule, ClipNode.Describe(path, m, antialias, rule));
        }

        return new Image(nodes, delays, LoopCount, Metadata, SourceFormat);
    }
}
