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

    // Kept on the node, so this frame and any single-frame image made from it (ToImage) share one set of planes.
    private FramePlanes Planes => Node.Planes;

    /// <summary>
    /// Gives back the memory this frame holds that came from decoding and can be computed again: the plane kept for region reads
    /// (see <see cref="CopyPixelsTo{T}(Span{T}, ImageRect, int, CancellationToken)"/>), the rows a sequential decode holds, a decode that was materialized into tiles, decoder block
    /// caches, and the cached tiles of every operation between the decode and this frame (a gray conversion, a resize, ...).
    /// The frame and every image made from it stay valid; a later read decodes and computes again, giving the same pixels.
    /// Frames and images derived from the same load share one decode, so they recompute too. Pixels you copied out are yours and are not touched.
    /// Does nothing for frames that are not decoded lazily (made from pixels or a <see cref="Canvas"/>).
    /// Memory returned to the process-wide tile pool (at most 48 MiB, reused by later work) is not held by the frame and is not affected.
    /// </summary>
    public void ReleaseDecodedPixels() => Node.ReleaseDecoded([Node]);

    /// <summary>Returns this frame as a single-frame <see cref="Image"/> (no metadata).</summary>
    /// <returns>The image.</returns>
    public Image ToImage() => Image.FromNode(Node, Delay);

    /// <summary>
    /// Computes the pixels of a rectangle into <paramref name="destination"/> with the region plane enabled. Same as
    /// <see cref="CopyPixelsTo{T}(Span{T}, ImageRect, bool, int, CancellationToken)"/> with <c>useRegionCache</c> <c>true</c>, which describes the details.
    /// </summary>
    /// <typeparam name="T">The pixel struct.</typeparam>
    /// <param name="destination">Receives <c>rect.Width * rect.Height</c> tightly packed pixels.</param>
    /// <param name="rect">The rectangle, inside the frame.</param>
    /// <param name="maxDegree">Most threads to use; 0 uses <see cref="ImageEngine.DefaultCallDegree"/>, capped by <see cref="ImageEngine.MaxDegree"/>.</param>
    /// <param name="cancellationToken">Cancels the computation.</param>
    public void CopyPixelsTo<T>(Span<T> destination, ImageRect rect, int maxDegree = 0, CancellationToken cancellationToken = default)
        where T : unmanaged, IPixel<T> => CopyPixelsTo(destination, rect, true, maxDegree, cancellationToken);

    /// <summary>
    /// Computes the pixels of a rectangle into <paramref name="destination"/>, converting to <typeparamref name="T"/> when the frame
    /// format differs (round half up, clamped to range). Tiles are computed in parallel; the result does not depend on the degree.
    /// The first whole-frame read of a small image from a sequential decoder (PNG, GIF, WebP, ...) decodes straight into
    /// <paramref name="destination"/> without keeping tiles, so a second read of the same image decodes once more and then keeps them.
    /// Regions of a large JPEG or PNG (16 MiB or more of pixels in its own format) are read from a sliding window of decoded rows. The first
    /// region read keeps nothing beyond that window. From the second region read on, the rows read are kept in a plane in the requested
    /// pixel format, filled from the top down only as far as the reads need, so any number of bands or rectangles costs about one decode
    /// plus the copies. The plane is at most <see cref="ImageEngine.RegionCacheBytes"/> (default 64 MiB) per frame; a frame whose plane
    /// would not fit decodes again for reads behind the window, as before. That is the memory cost: a Gray8 plane of an A4 page at 300 dpi
    /// is about 8.7 MB, an Rgb24 plane about 26 MB. <see cref="ReleaseDecodedPixels"/> gives it back, and <paramref name="useRegionCache"/>
    /// <c>false</c> avoids it for one call. The bytes read never depend on the cache.
    /// </summary>
    /// <typeparam name="T">The pixel struct.</typeparam>
    /// <param name="destination">Receives <c>rect.Width * rect.Height</c> tightly packed pixels.</param>
    /// <param name="rect">The rectangle, inside the frame.</param>
    /// <param name="cancellationToken">Cancels the computation.</param>
    /// <param name="maxDegree">Most threads to use; 0 uses <see cref="ImageEngine.DefaultCallDegree"/>, capped by <see cref="ImageEngine.MaxDegree"/>.</param>
    /// <param name="useRegionCache">
    /// Defaults to <c>true</c>. Pass <c>false</c> to keep this region read from building or counting toward the per-frame plane described
    /// above (<see cref="ImageEngine.RegionCacheBytes"/> is process-wide and stays as it is). The read is served by the sliding window, so
    /// reading bands from the top down decodes the image once and keeps only a few tile rows. A read that goes back above the window, or a
    /// rotated frame, decodes again from the top for this call and drops the rows it kept when it returns; that is the price, in time, of
    /// holding no more memory. A plane that already exists is still used. Whole-frame reads are unaffected. After the last such read,
    /// <see cref="ReleaseDecodedPixels"/> gives back the window and the decoder thread.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">The rectangle is outside the frame.</exception>
    /// <exception cref="ArgumentException"><paramref name="destination"/> is too short.</exception>
    /// <exception cref="ImageFormatException">The underlying data is corrupt.</exception>
    /// <exception cref="ImageSourceChangedException">The source file changed since load.</exception>
    public void CopyPixelsTo<T>(Span<T> destination, ImageRect rect, bool useRegionCache, int maxDegree = 0, CancellationToken cancellationToken = default)
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
                if ((Node.PlanesIfCreated?.TryReadWhole(T.Format, SpanCast.AsBytes(destination)) ?? false)
                    || Node.TryReadDirect(SpanCast.AsBytes(destination), T.Format, context.Degree, cancellationToken))
                {
                    return;
                }
            }
            else if (!rect.IsEmpty && rect.IsInside(Node.Width, Node.Height) && (ImageEngine.RegionCacheBytes > 0 || !useRegionCache) && Planes.Applies)
            {
                if (!useRegionCache)
                {
                    ReadWithoutPlane(destination, rect, context);
                    return;
                }

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

    // A region read of a streaming decode that builds no plane: it uses a plane another caller made if there is one, otherwise the sliding
    // window, and a read that has to go back may keep rows (a rotated frame needs them) only until it returns.
    private void ReadWithoutPlane<T>(Span<T> destination, ImageRect rect, RenderContext context)
        where T : unmanaged, IPixel<T>
    {
        if (Planes.TryRead(T.Format, rect, SpanCast.AsBytes(destination), context, create: false))
        {
            return;
        }

        try
        {
            Render.ReadRegion(Node, rect, SpanCast.AsBytes(destination), 0, T.Format, context.WithoutKeptRows());
        }
        finally
        {
            foreach (DecodeSourceNode d in Node.DecodeSources(Node))
            {
                if (d.Retaining)
                {
                    d.ReleaseDecoded();
                }
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
