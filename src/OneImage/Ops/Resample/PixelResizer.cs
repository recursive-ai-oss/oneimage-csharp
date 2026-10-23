using System.Buffers;
using OneImage.Kernels;

namespace OneImage;

/// <summary>
/// Resizes raw pixel spans into caller-provided spans without allocating: the same separable resampler as
/// <see cref="Image.Resize"/>, with the same output bytes, for callers that resize many small images in a loop.
/// <para>
/// <b>Allocation.</b> The resizer keeps its working buffers (tap tables, a window of filtered rows, one row of floats) between
/// calls and grows them only when a call needs more than any earlier one, so after a warm-up on the largest size the
/// steady state allocates nothing, not even for a different source or target size on every call. Buffers come from
/// <see cref="ArrayPool{T}"/> when they grow and go back to it on <see cref="Dispose"/> or <see cref="Trim"/>. A call must be
/// given spans that do not overlap.
/// </para>
/// <para>
/// <b>Scratch size.</b> Per band (see Threads), about <c>4 * (cap * W + S + 2 * R) * channels</c> bytes, where <c>W</c> is the target
/// width, <c>S</c> the source width, <c>R</c> the source width after the integer box pre-reduce (see <see cref="Image.Resize"/>),
/// <c>channels</c> is 1, 2 or 4 (3-channel pixels are padded to 4) and <c>cap</c> is at most twice the number of vertical taps of
/// the filter, rounded up to a multiple of 8 for gray (about 24 rows for Lanczos3 at 2:1; never more than the source height after
/// reduction), plus a few tap tables of about <c>(W + H)</c> entries times the taps. The source height does not matter: rows
/// stream through a sliding window. Buffers are rounded up to a power of two by the pool, so the real figure
/// (<see cref="ScratchBytes"/>) can be up to twice that. The total over all bands is bounded by the <c>maxScratchBytes</c> given to
/// the constructor; a call that would need more throws <see cref="ImageLimitException"/> before allocating.
/// </para>
/// <para>
/// <b>Threads.</b> An instance is not thread-safe: use one per thread (or lock around it). A call that overlaps another call on the
/// same instance throws <see cref="InvalidOperationException"/>. A call whose source has at least 65,536 pixels and whose result has
/// at least eight rows per band is split into bands of output rows (up to <c>maxDegree</c>; the calling thread takes part, the
/// others are pool work items that are queued without allocating). Every output pixel is computed the same way whichever band
/// it falls in, so the bytes do not depend on the degree or on scheduling. Pass <c>maxDegree: 1</c> to stay on the calling thread.
/// </para>
/// <para>
/// <b>Results.</b> Filters, edge handling, linear-light conversion, alpha premultiplication, the box pre-reduce for ratios of 4
/// or more, and rounding (half up, clamped) are exactly those of <see cref="Image.Resize"/> with <see cref="Fit.Stretch"/>
/// to the same size, and the bytes are identical on every hardware path. A resize to the source size copies the pixels.
/// </para>
/// </summary>
public sealed class PixelResizer : IDisposable
{
    /// <summary>The default bound of <see cref="ScratchBytes"/>, 256 MiB.</summary>
    public const long DefaultMaxScratchBytes = 256L << 20;

    private const int MaxDimension = 1 << 24;
    private const long MinParallelPixels = 1 << 16;
    private const int MinRowsPerBand = 8;

    // Value of the claim counter while no call is spreading bands, so a work item that starts late finds nothing to take.
    private const int ClaimsClosed = int.MaxValue / 2;

    private readonly long _maxScratch;
    private readonly int _requestedDegree;
    private readonly AxisTables _tablesX = new();
    private readonly AxisTables _tablesY = new();
    private readonly List<Band> _bands = [new()];
    private readonly List<Worker> _workers = [];
    private readonly ManualResetEventSlim _done = new(false, 100);
    private readonly PinnedCall _call;
    private int _busy;
    private bool _disposed;

    // The call in flight; written before the bands start and read-only while they run.
    private RawSpan _src;
    private RawSpan _dst;
    private ResampleLayout _layout;
    private int _sw;
    private int _sh;
    private int _dw;
    private int _dh;
    private int _srcStride;
    private int _dstStride;
    private int _rx;
    private int _ry;
    private int _redCols;
    private int _redRows;
    private int _cap;
    private int _bandCount;
    private bool _fuse;
    private bool _useBlock;
    private WeightBuffers _wx = null!;
    private WeightBuffers _wy = null!;
    private CancellationToken _ct;
    private int _claim = ClaimsClosed;
    private int _pending;
    private Exception? _error;

    /// <summary>Creates a resizer with empty buffers.</summary>
    /// <param name="maxScratchBytes">The most working memory the resizer may hold, at least 1 MiB. Defaults to <see cref="DefaultMaxScratchBytes"/>.</param>
    /// <param name="maxDegree">The most threads one call may use; 0 or less (the default) means <see cref="ImageEngine.DefaultCallDegree"/>, and the result never exceeds <see cref="ImageEngine.MaxDegree"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxScratchBytes"/> is below 1 MiB.</exception>
    public PixelResizer(long maxScratchBytes = DefaultMaxScratchBytes, int maxDegree = 0)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxScratchBytes, 1L << 20);
        _maxScratch = maxScratchBytes;
        _requestedDegree = maxDegree;
        _call = new PinnedCall(this);
    }

    /// <summary>Gets the bytes of working memory the resizer currently holds (buffer capacities).</summary>
    public long ScratchBytes
    {
        get
        {
            long total = _tablesX.CapacityBytes + _tablesY.CapacityBytes;
            foreach (Band band in _bands)
            {
                total += band.CapacityBytes;
            }

            return total;
        }
    }

    /// <summary>
    /// The size <see cref="Image.Resize"/> scales to for these arguments, before any crop or padding: the final size for
    /// <see cref="Fit.Contain"/>, <see cref="Fit.Stretch"/> and calls with only a width or a height (the other side is rounded half up, at
    /// least 1). Use it to size the destination of <see cref="Resize{TPixel}"/>.
    /// </summary>
    /// <param name="sourceWidth">Source width, at least 1.</param>
    /// <param name="sourceHeight">Source height, at least 1.</param>
    /// <param name="width">Target width, or <see langword="null"/> to derive it.</param>
    /// <param name="height">Target height, or <see langword="null"/> to derive it.</param>
    /// <param name="fit">How the image fits a box; defaults to <see cref="Fit.Contain"/>.</param>
    /// <param name="upscale">When <see langword="false"/> the image is never enlarged. Defaults to <see langword="true"/>.</param>
    /// <returns>The width and height to resample to.</returns>
    /// <exception cref="ArgumentException">Neither width nor height is given.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A size is not positive or <paramref name="fit"/> is undefined.</exception>
    /// <exception cref="ImageLimitException">The result would exceed the engine's dimension limits.</exception>
    public static (int Width, int Height) GetSize(int sourceWidth, int sourceHeight, int? width = null, int? height = null, Fit fit = Fit.Contain, bool upscale = true)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sourceWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sourceHeight);
        ResizePlan plan = ResizePlan.Compute(sourceWidth, sourceHeight, width, height, fit, upscale);
        return (plan.ResampleWidth, plan.ResampleHeight);
    }

    /// <summary>
    /// Resizes typed pixels (see the class remarks for allocation, scratch and threading). Pixels are read as straight (not
    /// premultiplied) alpha where the format has alpha; RGB and gray are treated as sRGB when <paramref name="linear"/> is set.
    /// Use <see cref="ResizeRaw"/> for premultiplied or other formats without a pixel struct.
    /// </summary>
    /// <typeparam name="TPixel">A pixel struct such as <see cref="Gray8"/>, <see cref="Gray16"/>, <see cref="Rgb24"/> or <see cref="Rgba32"/>.</typeparam>
    /// <param name="source">The source pixels, row-major.</param>
    /// <param name="sourceWidth">Source width, at least 1.</param>
    /// <param name="sourceHeight">Source height, at least 1.</param>
    /// <param name="destination">Receives the result; only the first <paramref name="destinationWidth"/> pixels of each row are written.</param>
    /// <param name="destinationWidth">Target width, at least 1 (see <see cref="GetSize"/>).</param>
    /// <param name="destinationHeight">Target height, at least 1.</param>
    /// <param name="filter">The reconstruction filter. <see cref="ResamplingFilter.Auto"/> (the default) uses Lanczos3 on an axis that shrinks and Catmull-Rom on one that grows or stays; see <see cref="Image.Resize"/> for the other filters.</param>
    /// <param name="linear">Resample in linear light. Defaults to <see langword="true"/>.</param>
    /// <param name="sourceStride">Pixels between the starts of source rows; 0 (the default) means <paramref name="sourceWidth"/>.</param>
    /// <param name="destinationStride">Pixels between the starts of destination rows; 0 (the default) means <paramref name="destinationWidth"/>.</param>
    /// <param name="cancellationToken">Checked once per output row.</param>
    /// <exception cref="ArgumentException">A span is too short for its size and stride, or the spans overlap.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A size or stride is invalid, or <paramref name="filter"/> is undefined.</exception>
    /// <exception cref="ImageLimitException">A dimension exceeds 2^24, or the call would need more scratch than the limit.</exception>
    /// <exception cref="InvalidOperationException">Another call on this instance is running.</exception>
    /// <exception cref="ObjectDisposedException">The resizer was disposed.</exception>
    /// <exception cref="OperationCanceledException">The token was cancelled; the destination is partly written.</exception>
    public void Resize<TPixel>(
        ReadOnlySpan<TPixel> source,
        int sourceWidth,
        int sourceHeight,
        Span<TPixel> destination,
        int destinationWidth,
        int destinationHeight,
        ResamplingFilter filter = ResamplingFilter.Auto,
        bool linear = true,
        int sourceStride = 0,
        int destinationStride = 0,
        CancellationToken cancellationToken = default)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        int size = SpanCast.SizeOf<TPixel>();
        ResizeRaw(
            SpanCast.AsBytes(source),
            TPixel.Format,
            sourceWidth,
            sourceHeight,
            ToBytes(sourceStride, size),
            SpanCast.AsBytes(destination),
            destinationWidth,
            destinationHeight,
            ToBytes(destinationStride, size),
            filter,
            linear,
            cancellationToken);
    }

    /// <summary>
    /// Resizes pixels of any format with up to four channels (including premultiplied alpha, CMYK and Lab) given as bytes. Behaves as
    /// <see cref="Resize{TPixel}"/>; premultiplied storage is handled as <see cref="Image.Resize"/> handles it.
    /// </summary>
    /// <param name="source">The source bytes, row-major.</param>
    /// <param name="format">The pixel format of both source and destination.</param>
    /// <param name="sourceWidth">Source width in pixels, at least 1.</param>
    /// <param name="sourceHeight">Source height, at least 1.</param>
    /// <param name="sourceStride">Bytes between the starts of source rows; 0 means a tightly packed row.</param>
    /// <param name="destination">Receives the result.</param>
    /// <param name="destinationWidth">Target width, at least 1.</param>
    /// <param name="destinationHeight">Target height, at least 1.</param>
    /// <param name="destinationStride">Bytes between the starts of destination rows; 0 means a tightly packed row.</param>
    /// <param name="filter">The reconstruction filter. Defaults to <see cref="ResamplingFilter.Auto"/>.</param>
    /// <param name="linear">Resample in linear light. Defaults to <see langword="true"/>.</param>
    /// <param name="cancellationToken">Checked once per output row.</param>
    /// <exception cref="ArgumentException">A span is too short for its size and stride, or the spans overlap.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A size or stride is invalid, or <paramref name="filter"/> is undefined.</exception>
    /// <exception cref="ImageLimitException">A dimension exceeds 2^24, or the call would need more scratch than the limit.</exception>
    /// <exception cref="ImageOperationException">The format has more than four channels.</exception>
    /// <exception cref="InvalidOperationException">Another call on this instance is running.</exception>
    /// <exception cref="ObjectDisposedException">The resizer was disposed.</exception>
    /// <exception cref="OperationCanceledException">The token was cancelled; the destination is partly written.</exception>
    public void ResizeRaw(
        ReadOnlySpan<byte> source,
        PixelFormat format,
        int sourceWidth,
        int sourceHeight,
        int sourceStride,
        Span<byte> destination,
        int destinationWidth,
        int destinationHeight,
        int destinationStride,
        ResamplingFilter filter = ResamplingFilter.Auto,
        bool linear = true,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sourceWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sourceHeight);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(destinationWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(destinationHeight);
        ArgumentOutOfRangeException.ThrowIfNegative(sourceStride);
        ArgumentOutOfRangeException.ThrowIfNegative(destinationStride);
        if (!Enum.IsDefined(filter))
        {
            throw new ArgumentOutOfRangeException(nameof(filter));
        }

        if (Math.Max(Math.Max(sourceWidth, sourceHeight), Math.Max(destinationWidth, destinationHeight)) > MaxDimension)
        {
            throw new ImageLimitException("Resize dimensions exceed 2^24.");
        }

        int bpp = format.BytesPerPixel;
        long srcRow = (long)sourceWidth * bpp;
        long dstRow = (long)destinationWidth * bpp;
        long srcStride = sourceStride == 0 ? srcRow : sourceStride;
        long dstStride = destinationStride == 0 ? dstRow : destinationStride;
        if (srcStride < srcRow)
        {
            throw new ArgumentOutOfRangeException(nameof(sourceStride), "The stride is smaller than a row.");
        }

        if (dstStride < dstRow)
        {
            throw new ArgumentOutOfRangeException(nameof(destinationStride), "The stride is smaller than a row.");
        }

        if (((sourceHeight - 1) * srcStride) + srcRow > source.Length)
        {
            throw new ArgumentException("The source is too short for its size and stride.", nameof(source));
        }

        if (((destinationHeight - 1) * dstStride) + dstRow > destination.Length)
        {
            throw new ArgumentException("The destination is too short for its size and stride.", nameof(destination));
        }

        if (source.Overlaps(destination))
        {
            throw new ArgumentException("The source and destination overlap.", nameof(destination));
        }

        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
        {
            throw new InvalidOperationException("A PixelResizer is not thread-safe: another call is running on this instance.");
        }

        try
        {
            if (sourceWidth == destinationWidth && sourceHeight == destinationHeight)
            {
                for (int y = 0; y < sourceHeight; y++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    source.Slice((int)(y * srcStride), (int)srcRow).CopyTo(destination.Slice((int)(y * dstStride), (int)dstRow));
                }

                return;
            }

            Run(source, format, sourceWidth, sourceHeight, (int)srcStride, destination, destinationWidth, destinationHeight, (int)dstStride, filter, linear, cancellationToken);
        }
        finally
        {
            Volatile.Write(ref _busy, 0);
        }
    }

    /// <summary>Hands all working buffers back to the pool. The resizer stays usable and allocates again on the next call.</summary>
    /// <exception cref="InvalidOperationException">A call is running on this instance.</exception>
    public void Trim()
    {
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
        {
            throw new InvalidOperationException("A call is running on this instance.");
        }

        try
        {
            ReleaseAll();
        }
        finally
        {
            Volatile.Write(ref _busy, 0);
        }
    }

    /// <summary>Hands the working buffers back to the pool. Further calls throw <see cref="ObjectDisposedException"/>.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (Interlocked.CompareExchange(ref _busy, 1, 0) == 0)
        {
            ReleaseAll();
        }
    }

    private static int ToBytes(int stridePixels, int pixelSize)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(stridePixels);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(stridePixels, int.MaxValue / pixelSize);
        return stridePixels * pixelSize;
    }

    private void ReleaseAll()
    {
        foreach (Band band in _bands)
        {
            band.Release();
        }

        _tablesX.Dispose();
        _tablesY.Dispose();
    }

    private void Run(
        ReadOnlySpan<byte> source,
        PixelFormat format,
        int sw,
        int sh,
        int srcStride,
        Span<byte> destination,
        int dw,
        int dh,
        int dstStride,
        ResamplingFilter filter,
        bool linear,
        CancellationToken ct)
    {
        var layout = new ResampleLayout(format, linear);
        int p = layout.Stride;

        // Per axis exactly what ResampleNode.Create does: resolve Auto, then the integer box pre-reduce for ratios of 4 or more.
        ResamplingFilter filterX = ResampleNode.Resolve(filter, sw, dw);
        ResamplingFilter filterY = ResampleNode.Resolve(filter, sh, dh);
        int rx = filterX == ResamplingFilter.Nearest ? 1 : ResampleNode.ReduceFactor(sw, dw);
        int ry = filterY == ResamplingFilter.Nearest ? 1 : ResampleNode.ReduceFactor(sh, dh);
        int redCols = (sw + rx - 1) / rx;
        int redRows = (sh + ry - 1) / ry;
        double scaleX = (double)sw / dw / rx;
        double scaleY = (double)sh / dh / ry;

        int degree = ImageEngine.ResolveDegree(_requestedDegree);
        int bands = (long)sw * sh >= MinParallelPixels ? Math.Max(1, Math.Min(degree, dh / MinRowsPerBand)) : 1;

        // Check what the tap tables and the buffers will take before growing anything.
        bool fuse = ResampleKernel.CanFuseReduce(layout);
        bool useBlock = ResampleRowsKernel.IsAccelerated && ResampleRowsKernel.Handles(layout);
        long hstride = (long)dw * p;
        long estimate = WeightBuffers.EstimateBytes(dw, scaleX, filterX) + WeightBuffers.EstimateBytes(dh, scaleY, filterY);
        long fixedFloats = hstride + (rx > 1 && fuse ? 0 : (long)sw * p) + (rx > 1 ? (long)redCols * p : 0) + (ry > 1 ? (long)redCols * p : 0) + ((long)Math.Max(sw, dw) * layout.Channels) + (useBlock ? (long)redCols * ResampleRowsKernel.Rows : 0);
        if ((estimate + (fixedFloats * sizeof(float) * bands)) > _maxScratch)
        {
            throw new ImageLimitException("The resize needs more working memory than the resizer allows.");
        }

        WeightBuffers wx = _tablesX.Get(redCols, dw, scaleX, filterX);
        WeightBuffers wy = _tablesY.Get(redRows, dh, scaleY, filterY);

        // The vertical pass reads a window of at most `cap` horizontally filtered rows: twice the widest tap set, so the window
        // advances in few steps and each row is filtered once.
        int maxTaps = 1;
        for (int o = 0; o < dh; o++)
        {
            maxTaps = Math.Max(maxTaps, wy.Count[o]);
        }

        int cap = Math.Min(redRows, useBlock ? ((2 * maxTaps) + 7) & ~7 : 2 * maxTaps);
        long windowFloats = (long)cap * hstride;
        if ((((windowFloats + fixedFloats) * sizeof(float)) * bands) + _tablesX.CapacityBytes + _tablesY.CapacityBytes > _maxScratch || windowFloats > Array.MaxLength)
        {
            throw new ImageLimitException("The resize needs more working memory than the resizer allows.");
        }

        while (_bands.Count < bands)
        {
            _bands.Add(new Band());
        }

        for (int b = 0; b < bands; b++)
        {
            _bands[b].Grow(windowFloats, hstride, rx > 1 && fuse ? 0 : (long)sw * p, rx > 1 ? (long)redCols * p : 0, ry > 1 ? (long)redCols * p : 0, layout.Sample == SampleType.U8 ? 0 : (long)Math.Max(sw, dw) * layout.Channels, useBlock ? (long)redCols * ResampleRowsKernel.Rows : 0);
        }

        while (_workers.Count < bands - 1)
        {
            _workers.Add(new Worker(this));
        }

        _layout = layout;
        _sw = sw;
        _sh = sh;
        _dw = dw;
        _dh = dh;
        _srcStride = srcStride;
        _dstStride = dstStride;
        _rx = rx;
        _ry = ry;
        _redCols = redCols;
        _redRows = redRows;
        _cap = cap;
        _fuse = fuse;
        _useBlock = useBlock;
        _bandCount = bands;
        _wx = wx;
        _wy = wy;
        _ct = ct;
        _error = null;
        Pinned.Run(source, destination, _call);
    }

    // Called with both spans pinned: runs every band, on this thread and on pool threads that claim bands too.
    private void Execute(RawSpan source, RawSpan destination)
    {
        _src = source;
        _dst = destination;
        if (_bandCount == 1)
        {
            RunBand(0);
            return;
        }

        _done.Reset();
        Volatile.Write(ref _pending, _bandCount);
        Volatile.Write(ref _claim, 0);
        for (int i = 0; i < _bandCount - 1; i++)
        {
            ThreadPool.UnsafeQueueUserWorkItem(_workers[i], preferLocal: false);
        }

        ClaimBands();
        _done.Wait();
        Volatile.Write(ref _claim, ClaimsClosed);
        Exception? error = _error;
        _error = null;
        if (error != null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
        }
    }

    // Takes bands until none are left. A work item that starts late (after the call is over) finds none and returns.
    private void ClaimBands()
    {
        while (true)
        {
            int b = Interlocked.Increment(ref _claim) - 1;
            if (b >= _bandCount)
            {
                return;
            }

            try
            {
                if (Volatile.Read(ref _error) == null)
                {
                    RunBand(b);
                }
            }
            catch (Exception e)
            {
                // Handed to the calling thread, which rethrows it; the other bands stop early.
                Interlocked.CompareExchange(ref _error, e, null);
            }
            finally
            {
                if (Interlocked.Decrement(ref _pending) == 0)
                {
                    _done.Set();
                }
            }
        }
    }

    // Output rows [b * dh / bands, (b + 1) * dh / bands). Every output pixel depends only on its own taps, so the split is invisible in the result.
    private void RunBand(int b)
    {
        Band band = _bands[b];
        int oy0 = (int)((long)b * _dh / _bandCount);
        int oy1 = (int)((long)(b + 1) * _dh / _bandCount);
        WeightBuffers wx = _wx;
        WeightBuffers wy = _wy;
        ReadOnlySpan<byte> source = _src.AsReadOnly();
        Span<byte> destination = _dst.AsSpan();
        int dw = _dw;
        int p = _layout.Stride;
        int h = dw * p;
        int cap = _cap;
        Span<float> rows = band.Rows.AsSpan(0, cap * h);
        Span<float> outRow = band.Out.AsSpan(0, h);
        int maxEnd = 0;
        for (int o = oy0; o < oy1; o++)
        {
            maxEnd = Math.Max(maxEnd, wy.Start[o] + wy.Count[o]);
        }

        var pass = new RowPass
        {
            Source = source,
            SourceStride = _srcStride,
            Width = _sw,
            Height = _sh,
            Layout = _layout,
            Fuse = _fuse,
            Rx = _rx,
            Ry = _ry,
            RedCols = _redCols,
            RowF = band.RowF,
            Red = band.Red,
            Acc = band.Acc,
            Tmp = band.Tmp,
            Block = band.Block,
            Start = wx.Start.AsSpan(0, dw),
            Count = wx.Count.AsSpan(0, dw),
            Offset = wx.Offset.AsSpan(0, dw),
            Weights = wx.Weights,
        };

        int obpp = _layout.BytesPerPixel;
        int bufStart = 0;
        int bufRows = 0;
        for (int oy = oy0; oy < oy1; oy++)
        {
            _ct.ThrowIfCancellationRequested();
            int s = wy.Start[oy];
            int count = wy.Count[oy];
            if (s < bufStart || s + count > bufStart + bufRows)
            {
                // Slide the window to start at this output's first row: keep what overlaps, filter the rest.
                int bufEnd = bufStart + bufRows;
                int kept = s >= bufStart && s < bufEnd ? bufEnd - s : 0;
                if (kept > 0)
                {
                    rows.Slice((s - bufStart) * h, kept * h).CopyTo(rows);
                }

                int want = Math.Min(cap, maxEnd - s);
                int limit = Math.Min(cap, _redRows - s);
                int r = kept;
                while (r < want)
                {
                    // Eight rows at a time where the kernel supports it (it may run a few rows past `want`, never past the window).
                    if (_useBlock && r + ResampleRowsKernel.Rows <= limit)
                    {
                        pass.ReducedRows8(s + r, rows.Slice(r * h), h);
                        r += ResampleRowsKernel.Rows;
                    }
                    else
                    {
                        pass.ReducedRow(s + r, rows.Slice(r * h, h));
                        r++;
                    }
                }

                bufStart = s;
                bufRows = Math.Max(r, kept);
            }

            ResampleKernel.VerticalFilter(rows, h, s - bufStart, wy.Weights.AsSpan(wy.Offset[oy], count), outRow);
            ResampleKernel.PackRow(outRow, destination.Slice(oy * _dstStride, dw * obpp), dw, _layout, band.Tmp);
        }
    }

    // Pins the caller's spans for the call.
    private sealed class PinnedCall(PixelResizer owner) : IPinnedJob
    {
        public void Run(RawSpan source, RawSpan destination) => owner.Execute(source, destination);
    }

    private sealed class Worker(PixelResizer owner) : IThreadPoolWorkItem
    {
        public void Execute() => owner.ClaimBands();
    }

    // Working buffers of one band.
    private sealed class Band
    {
        public float[] Rows = [];
        public float[] Out = [];
        public float[] RowF = [];
        public float[] Red = [];
        public float[] Acc = [];
        public float[] Tmp = [];
        public float[] Block = [];

        public long CapacityBytes => ((long)(Rows.Length + Out.Length + RowF.Length + Red.Length + Acc.Length + Tmp.Length + Block.Length)) * sizeof(float);

        public void Grow(long rows, long outRow, long rowF, long red, long acc, long tmp, long block)
        {
            Grow(ref Rows, rows);
            Grow(ref Out, outRow);
            Grow(ref RowF, rowF);
            Grow(ref Red, red);
            Grow(ref Acc, acc);
            Grow(ref Tmp, tmp);
            Grow(ref Block, block);
        }

        public void Release()
        {
            Drop(ref Rows);
            Drop(ref Out);
            Drop(ref RowF);
            Drop(ref Red);
            Drop(ref Acc);
            Drop(ref Tmp);
            Drop(ref Block);
        }

        private static void Grow(ref float[] array, long length)
        {
            if (array.Length >= length)
            {
                return;
            }

            Drop(ref array);
            array = ArrayPool<float>.Shared.Rent((int)length);
        }

        private static void Drop(ref float[] array)
        {
            if (array.Length > 0)
            {
                ArrayPool<float>.Shared.Return(array);
                array = [];
            }
        }
    }

    // The per-row work of the horizontal stage: one source row (or one group of ry source rows) to one filtered row.
    private ref struct RowPass
    {
        public ReadOnlySpan<byte> Source;
        public int SourceStride;
        public int Width;
        public int Height;
        public ResampleLayout Layout;
        public bool Fuse;
        public int Rx;
        public int Ry;
        public int RedCols;
        public Span<float> RowF;
        public Span<float> Red;
        public Span<float> Acc;
        public Span<float> Tmp;
        public Span<float> Block;
        public ReadOnlySpan<int> Start;
        public ReadOnlySpan<int> Count;
        public ReadOnlySpan<int> Offset;
        public ReadOnlySpan<float> Weights;

        // The row that goes through the horizontal filter for reduced row j: the mean of source rows [j * Ry, min((j + 1) * Ry, Height)),
        // each box-reduced by Rx.
        private ReadOnlySpan<float> FilterInput(int j)
        {
            int y0 = j * Ry;
            if (Ry == 1)
            {
                return SourceRow(y0);
            }

            int y1 = Math.Min(y0 + Ry, Height);
            Span<float> acc = Acc.Slice(0, RedCols * Layout.Stride);
            for (int y = y0; y < y1; y++)
            {
                ReadOnlySpan<float> cur = SourceRow(y);
                if (y == y0)
                {
                    cur.CopyTo(acc);
                }
                else
                {
                    ResampleKernel.AddRow(cur, acc);
                }
            }

            ResampleKernel.ScaleRow(acc, 1f / (y1 - y0));
            return acc;
        }

        public void ReducedRow(int j, Span<float> destination) =>
            ResampleKernel.HorizontalFilter(FilterInput(j), 0, destination, Layout.Stride, Start, Count, Offset, Weights);

        // Reduced rows j0..j0+7 of a one-channel layout, filtered together; row r of the result lands at destination[r * stride].
        public void ReducedRows8(int j0, Span<float> destination, int stride)
        {
            if (Rx == 1 && Ry == 1 && Layout.Sample == SampleType.U8)
            {
                // Eight consecutive source rows go straight into the interleaved block.
                ResampleRowsKernel.UnpackGray8Block(Source.Slice(j0 * SourceStride), SourceStride, Block, Width, Layout.Linear);
            }
            else
            {
                for (int lane = 0; lane < ResampleRowsKernel.Rows; lane++)
                {
                    ResampleRowsKernel.ScatterRow(FilterInput(j0 + lane), Block, lane);
                }
            }

            ResampleRowsKernel.HorizontalFilterRows(Block.Slice(0, RedCols * ResampleRowsKernel.Rows), destination, stride, Start, Count, Offset, Weights);
        }

        private ReadOnlySpan<float> SourceRow(int y)
        {
            int p = Layout.Stride;
            ReadOnlySpan<byte> row = Source.Slice(y * SourceStride, Width * Layout.BytesPerPixel);
            if (Rx > 1 && Fuse)
            {
                ResampleKernel.UnpackReduceRow(row, Red, Width, Rx, RedCols, Layout);
                return Red.Slice(0, RedCols * p);
            }

            ResampleKernel.UnpackRow(row, RowF, Width, Layout, Tmp);
            ReadOnlySpan<float> cur = RowF.Slice(0, Width * p);
            if (Rx > 1)
            {
                ResampleKernel.ReduceRow(cur, Red, p, Rx, Width, RedCols);
                cur = Red.Slice(0, RedCols * p);
            }

            return cur;
        }
    }

    // The last few tap tables of one axis. A caller that keeps resizing to the same few sizes (every crop of a page at height 32)
    // then skips the filter evaluations, which are most of the work for small images.
    private sealed class AxisTables : IDisposable
    {
        private const int Slots = 4;
        private readonly WeightBuffers[] _buffers = [new(), new(), new(), new()];
        private readonly (int Source, int Output, long Scale, ResamplingFilter Filter, bool Valid)[] _keys = new (int, int, long, ResamplingFilter, bool)[Slots];
        private int _next;

        public long CapacityBytes
        {
            get
            {
                long total = 0;
                foreach (WeightBuffers b in _buffers)
                {
                    total += b.CapacityBytes;
                }

                return total;
            }
        }

        public WeightBuffers Get(int source, int output, double scale, ResamplingFilter filter)
        {
            long bits = BitConverter.DoubleToInt64Bits(scale);
            for (int i = 0; i < Slots; i++)
            {
                if (_keys[i] == (source, output, bits, filter, true))
                {
                    return _buffers[i];
                }
            }

            int slot = _next;
            _next = (_next + 1) % Slots;
            _keys[slot].Valid = false;
            _buffers[slot].Fill(source, output, scale, filter);
            _keys[slot] = (source, output, bits, filter, true);
            return _buffers[slot];
        }

        public void Dispose()
        {
            for (int i = 0; i < Slots; i++)
            {
                _buffers[i].Dispose();
                _keys[i].Valid = false;
            }
        }
    }
}
