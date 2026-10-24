using OneImage.Kernels;

namespace OneImage;

/// <summary>
/// One frame of an <see cref="Image"/>: a lazy, immutable grid of pixels plus its display time.
/// Pixels are computed on demand when read; reading can fail late (corrupt data, a changed source file) with the typed image exceptions.
/// </summary>
public sealed partial class ImageFrame
{
    internal ImageFrame(Node node, TimeSpan delay, int index)
    {
        Node = node;
        Delay = delay;
        Index = index;
    }

    /// <summary>Gets the zero-based index in <see cref="Image.Frames"/>.</summary>
    public int Index { get; }

    /// <summary>Gets the width in pixels.</summary>
    public int Width => Node.Width;

    /// <summary>Gets the height in pixels.</summary>
    public int Height => Node.Height;

    /// <summary>Gets the pixel format pixels are stored (and cached) in.</summary>
    public PixelFormat Format => Node.Format;

    /// <summary>Gets the display time of this frame (zero for still images).</summary>
    public TimeSpan Delay { get; }

    internal Node Node { get; }

    private FramePlanes? _planes;

    private FramePlanes Planes => Volatile.Read(ref _planes) ?? Interlocked.CompareExchange(ref _planes, new FramePlanes(Node), null) ?? _planes!;

    /// <summary>Gives back everything this frame keeps decoded for region reads, including rows held by the shared decode; later reads decode again.</summary>
    internal void ReleaseDecodedPixels()
    {
        Volatile.Read(ref _planes)?.Release();
        foreach (DecodeSourceNode d in Node.DecodeSources(Node))
        {
            d.ReleaseDecoded();
        }
    }

    /// <summary>Returns this frame as a single-frame <see cref="Image"/> (no metadata).</summary>
    /// <returns>The image.</returns>
    public Image ToImage() => Image.FromNode(Node, Delay);

    /// <summary>
    /// Computes the pixels of a rectangle into <paramref name="destination"/>, converting to <typeparamref name="T"/> when the frame
    /// format differs (round half up, clamped to range). Tiles are computed in parallel; the result does not depend on the degree.
    /// The first whole-frame read of a small image from a sequential decoder (PNG, GIF, WebP, ...) decodes straight into
    /// <paramref name="destination"/> without keeping tiles, so a second read of the same image decodes once more and then keeps them.
    /// </summary>
    /// <typeparam name="T">The pixel struct.</typeparam>
    /// <param name="destination">Receives <c>rect.Width * rect.Height</c> tightly packed pixels.</param>
    /// <param name="rect">The rectangle, inside the frame.</param>
    /// <param name="cancellationToken">Cancels the computation.</param>
    /// <param name="maxDegree">Most threads to use; 0 uses <see cref="ImageEngine.DefaultCallDegree"/>, capped by <see cref="ImageEngine.MaxDegree"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException">The rectangle is outside the frame.</exception>
    /// <exception cref="ArgumentException"><paramref name="destination"/> is too short.</exception>
    /// <exception cref="ImageFormatException">The underlying data is corrupt.</exception>
    /// <exception cref="ImageSourceChangedException">The source file changed since load.</exception>
    public void CopyPixelsTo<T>(Span<T> destination, ImageRect rect, int maxDegree = 0, CancellationToken cancellationToken = default)
        where T : unmanaged, IPixel<T>
    {
        if ((long)rect.Width * rect.Height > destination.Length)
        {
            throw new ArgumentException("Destination too short for the rectangle.", nameof(destination));
        }

        try
        {
            RenderContext context = RenderContext.Create(maxDegree, cancellationToken);
            if (rect.X == 0 && rect.Y == 0 && rect.Width == Node.Width && rect.Height == Node.Height)
            {
                if ((Volatile.Read(ref _planes)?.TryReadWhole(T.Format, SpanCast.AsBytes(destination)) ?? false)
                    || Node.TryReadDirect(SpanCast.AsBytes(destination), T.Format, context.Degree, cancellationToken))
                {
                    return;
                }
            }
            else if (!rect.IsEmpty && rect.IsInside(Node.Width, Node.Height) && ImageEngine.RegionCacheBytes > 0 && Planes.Applies)
            {
                // A region of a streaming decode: from the second read on the rows come from a plane kept in the requested format,
                // and until then a read that goes back above the sliding window keeps the rows it decodes rather than starting over.
                if (Planes.TryRead(T.Format, rect, SpanCast.AsBytes(destination), context))
                {
                    return;
                }

                context = context.WithRetainDecode();
            }

            Render.ReadRegion(Node, rect, SpanCast.AsBytes(destination), 0, T.Format, context);
        }
        finally
        {
            if (rect.X == 0 && rect.Y == 0 && rect.Width == Node.Width && rect.Height == Node.Height)
            {
                // A whole-frame read is a one-shot pass like a save: give back what only this pipeline held (a shrunk decode's tiles).
                Node.ReleaseAfterSave([Node]);
            }
        }
    }

    /// <summary>Computes the whole frame as a new array in <typeparamref name="T"/> (converting if needed).</summary>
    /// <typeparam name="T">The pixel struct.</typeparam>
    /// <param name="cancellationToken">Cancels the computation.</param>
    /// <param name="maxDegree">Most threads to use; 0 means the default.</param>
    /// <returns>Row-major pixels.</returns>
    /// <exception cref="ImageLimitException">The array would exceed the maximum array size.</exception>
    public T[] ToArray<T>(int maxDegree = 0, CancellationToken cancellationToken = default)
        where T : unmanaged, IPixel<T>
    {
        long count = (long)Width * Height;
        if (count > Array.MaxLength)
        {
            throw new ImageLimitException("The frame is too large for one array; read it in regions.");
        }

        var result = new T[count];
        CopyPixelsTo<T>(result, new ImageRect(0, 0, Width, Height), maxDegree, cancellationToken);
        return result;
    }
}
