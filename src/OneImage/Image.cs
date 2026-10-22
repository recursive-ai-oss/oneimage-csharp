using System.Text;

namespace OneImage;

/// <summary>
/// A lazy, immutable image: one or more frames, each a graph of operations over a source that is only computed, tile by tile,
/// when pixels are read or the image is saved. Operations return new images and map over every frame.
/// Errors in the underlying data surface when pixels are pulled, unless the image was loaded with
/// <see cref="LoadOptions.Validate"/>. Images are not disposable and are safe to share between threads.
/// </summary>
public sealed partial class Image
{
    private readonly ImageFrame[] _frames;

    internal Image(Node[] nodes, TimeSpan[] delays, int loopCount, ImageMetadata metadata, IImageFormat? sourceFormat)
    {
        _frames = new ImageFrame[nodes.Length];
        for (int i = 0; i < nodes.Length; i++)
        {
            _frames[i] = new ImageFrame(nodes[i], delays[i], i);
        }

        LoopCount = loopCount;
        Metadata = metadata;
        SourceFormat = sourceFormat;
    }

    /// <summary>Gets the width of the first frame.</summary>
    public int Width => _frames[0].Width;

    /// <summary>Gets the height of the first frame.</summary>
    public int Height => _frames[0].Height;

    /// <summary>Gets the pixel format of the first frame.</summary>
    public PixelFormat Format => _frames[0].Format;

    /// <summary>Gets the number of frames (1 for still images).</summary>
    public int FrameCount => _frames.Length;

    /// <summary>Gets the frames. Operations apply to all of them; pixel-reading members of <see cref="Image"/> use frame 0.</summary>
    public IReadOnlyList<ImageFrame> Frames => _frames;

    /// <summary>Gets the animation loop count; 0 means forever.</summary>
    public int LoopCount { get; }

    /// <summary>Gets the metadata (ICC, EXIF, XMP, IPTC blobs, orientation).</summary>
    public ImageMetadata Metadata { get; }

    /// <summary>Gets the format this image was decoded from, or <see langword="null"/> for images made in memory.</summary>
    public IImageFormat? SourceFormat { get; }

    /// <summary>Creates an image from pixels (row-major, tightly packed) by copying them into tiles.</summary>
    /// <typeparam name="T">The pixel struct.</typeparam>
    /// <param name="pixels">At least <c>width * height</c> pixels.</param>
    /// <param name="width">Width in pixels.</param>
    /// <param name="height">Height in pixels.</param>
    /// <returns>A single-frame image in format <c>T.Format</c>.</returns>
    /// <exception cref="ArgumentException"><paramref name="pixels"/> is too short.</exception>
    /// <exception cref="ImageLimitException">The size exceeds the default limits.</exception>
    public static Image FromPixels<T>(ReadOnlySpan<T> pixels, int width, int height)
        where T : unmanaged, IPixel<T>
    {
        var canvas = new Canvas(width, height, T.Format);
        canvas.CopyFrom(pixels, new ImageRect(0, 0, width, height));
        return canvas.Snapshot();
    }

    /// <summary>Crops (extracts) a rectangle (lazy). Coordinates are zero-based from the top-left; the right and bottom edges are exclusive; the rectangle must lie fully inside the image (use <see cref="Pad"/> first to extract beyond the edge). Tiles outside the rectangle are never computed. Decoders that support it decode only the rectangle.</summary>
    /// <param name="x">Left edge in source pixels.</param>
    /// <param name="y">Top edge.</param>
    /// <param name="width">Width, positive.</param>
    /// <param name="height">Height, positive.</param>
    /// <returns>The cropped image.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The rectangle is empty or not inside every frame.</exception>
    public Image Crop(int x, int y, int width, int height) => Crop(new ImageRect(x, y, width, height));

    /// <summary>Crops to a rectangle; see <see cref="Crop(int,int,int,int)"/>.</summary>
    /// <param name="rect">The rectangle.</param>
    /// <returns>The cropped image.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The rectangle is empty or not inside every frame.</exception>
    public Image Crop(ImageRect rect)
    {
        if (rect.IsEmpty)
        {
            throw new ArgumentOutOfRangeException(nameof(rect), "Crop rectangle is empty.");
        }

        foreach (ImageFrame f in _frames)
        {
            if (!rect.IsInside(f.Width, f.Height))
            {
                throw new ArgumentOutOfRangeException(nameof(rect), "Crop rectangle lies outside the image.");
            }
        }

        return Map(static (n, r) => CropNodeFor(n, r), rect);
    }

    /// <summary>
    /// Converts the pixel format (lazy). Rounding is half up and clamped; gray to RGB replicates, RGB to gray uses Rec. 709 weights
    /// on stored values; dropping alpha discards it, adding alpha makes pixels opaque; CMYK and Lab convert to and from the other models through the
    /// default ICC profiles (sRGB, gray, the generic CMYK profile, Lab D50) with relative colorimetric intent and black point compensation. Use
    /// <see cref="ConvertColor"/> to control profile and intent. Lab pixels must be 32-bit float.
    /// </summary>
    /// <param name="format">The target format.</param>
    /// <returns>The converted image.</returns>
    /// <exception cref="ImageOperationException">The conversion is not supported.</exception>
    public Image ConvertTo(PixelFormat format)
    {
        ColorModel from = _frames[0].Format.Model;
        if (from != format.Model && !(from is ColorModel.Gray or ColorModel.Rgb && format.Model is ColorModel.Gray or ColorModel.Rgb))
        {
            // Crossing into or out of CMYK/Lab is a color-managed conversion that honors the embedded source profile.
            return ConvertColorCore(ColorProfile.DefaultFor(format.Model), RenderingIntent.RelativeColorimetric, true, format.Sample).ConvertTo(format);
        }

        foreach (ImageFrame f in _frames)
        {
            PixelFormat s = f.Format;
            bool lab = (s.Model == ColorModel.Lab && s.Sample != SampleType.F32) || (format.Model == ColorModel.Lab && format.Sample != SampleType.F32);
            if (lab)
            {
                throw new ImageOperationException($"Cannot convert {s} to {format}.");
            }
        }

        // Behind a chain of color ops the conversion joins the chain (same arithmetic, one pass).
        return Map(
            static (n, f) => n.Format == f ? n
                : n is ToneNode && n.Format.Model is ColorModel.Gray or ColorModel.Rgb && f.Model is ColorModel.Gray or ColorModel.Rgb ? ToneNode.Build(n, new ConvertStep(f))
                : new ConvertNode(n, f),
            format);
    }

    /// <summary>Copies frame 0 into a mutable <see cref="Canvas"/>; the other frames of an animated or multi-page image are ignored (use <c>Frames[i].ToImage().ToCanvas()</c> for those). Tiles are computed lazily on first touch and cached under the global budget until written.</summary>
    /// <returns>A canvas; dispose it when done.</returns>
    public Canvas ToCanvas() => new(_frames[0].Node);

    /// <summary>Computes a rectangle of frame 0 only; for another frame use <c>Frames[i].CopyPixelsTo</c>. See <see cref="ImageFrame.CopyPixelsTo{T}"/>.</summary>
    /// <typeparam name="T">The pixel struct.</typeparam>
    /// <param name="destination">Receives the pixels.</param>
    /// <param name="rect">The rectangle.</param>
    /// <param name="cancellationToken">Cancels the computation.</param>
    /// <param name="maxDegree">Most threads to use; 0 means the default.</param>
    public void CopyPixelsTo<T>(Span<T> destination, ImageRect rect, int maxDegree = 0, CancellationToken cancellationToken = default)
        where T : unmanaged, IPixel<T> => _frames[0].CopyPixelsTo(destination, rect, maxDegree, cancellationToken);

    /// <summary>Computes frame 0 only as an array; for another frame use <c>Frames[i].ToArray</c>. See <see cref="ImageFrame.ToArray{T}"/>.</summary>
    /// <typeparam name="T">The pixel struct.</typeparam>
    /// <param name="cancellationToken">Cancels the computation.</param>
    /// <param name="maxDegree">Most threads to use; 0 means the default.</param>
    /// <returns>Row-major pixels.</returns>
    public T[] ToArray<T>(int maxDegree = 0, CancellationToken cancellationToken = default)
        where T : unmanaged, IPixel<T> => _frames[0].ToArray<T>(maxDegree, cancellationToken);

    /// <summary>Describes frame 0's operation graph as text, one node per line, outermost op first.</summary>
    /// <returns>The description.</returns>
    public string DescribeGraph()
    {
        var sb = new StringBuilder();
        var stack = new Stack<(Node Node, int Depth)>();
        stack.Push((_frames[0].Node, 0));
        while (stack.Count > 0)
        {
            (Node node, int depth) = stack.Pop();
            sb.Append(' ', depth * 2).Append(node.Descriptor).Append(" -> ").Append(node.Width).Append('x').Append(node.Height).Append(' ').Append(node.Format).Append('\n');
            for (int i = node.Inputs.Length - 1; i >= 0; i--)
            {
                stack.Push((node.Inputs[i], depth + 1));
            }
        }

        return sb.ToString();
    }

    internal static Image FromNode(Node node, TimeSpan delay = default) =>
        new([node], [delay], 0, ImageMetadata.Empty, null);

    // Crop below an orientation node (the source rectangle of the wanted pixels), so the decoder can still be asked for just that region.
    private static Node CropNodeFor(Node n, ImageRect r) => n switch
    {
        DecodeSourceNode d when d.TryPushRegion(r) is { } pushed => pushed,
        OrientNode o => o.WithInput(CropNodeFor(o.Inputs[0], o.RequiredInput(0, r))),
        _ => new CropNode(n, r),
    };

    private Image Map<TArg>(Func<Node, TArg, Node> op, TArg arg)
    {
        var nodes = new Node[_frames.Length];
        var delays = new TimeSpan[_frames.Length];
        for (int i = 0; i < nodes.Length; i++)
        {
            nodes[i] = op(_frames[i].Node, arg);
            delays[i] = _frames[i].Delay;
        }

        return new Image(nodes, delays, LoopCount, Metadata, SourceFormat);
    }
}
