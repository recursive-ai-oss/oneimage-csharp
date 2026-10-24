using System.Buffers;
using System.Collections.Concurrent;
using OneImage.Kernels;

namespace OneImage.Codecs.Tiff;

/// <summary>Everything ReadInfo learned that the frame decodes need, so they never re-parse the directories.</summary>
internal sealed class TiffImageState : IReleasableDecoderState
{
    /// <summary>Budget for decoded strips and tiles kept between 128x128 requests.</summary>
    public const long CacheBudget = 128L << 20;

    public TiffImageState(TiffFile file, TiffFrame[] frames)
    {
        File = file;
        Frames = frames;
    }

    public TiffFile File { get; }

    public TiffFrame[] Frames { get; }

    public TiffBlockCache Cache { get; } = new(CacheBudget);

    /// <summary>Counts strips and tiles that were actually read and decompressed (cache hits do not count).</summary>
    public long BlocksDecoded;

    /// <summary>Returns every cached block to the pool; later reads decode again.</summary>
    public void ReleaseCached() => Cache.Clear();
}

/// <summary>
/// TIFF decoder. Strips and tiles are addressed through the offset tables, so a region read touches only the blocks that
/// intersect it and the decoder advertises random access. See <see cref="TiffFormat"/> for the supported feature set.
/// </summary>
internal sealed class TiffDecoder : IImageDecoder, IDirectFrameDecoder
{
    // Offset tables, palettes and JPEG tables of all pages stay in memory; many pages pointing at the same big table must not multiply it.
    private const long MaxRetainedBytes = 128L << 20;

    // Reads of at least this many pixels (and block rows up to this many raw bytes) bypass the block cache.
    private const long DirectReadPixels = 1 << 20;
    private const long MaxDirectRowBytes = 64L << 20;

    private readonly TiffFormat _format;

    public TiffDecoder(TiffFormat format) => _format = format;

    public DecoderCapabilities Capabilities => DecoderCapabilities.RandomAccess;

    public ImageInfo ReadInfo(Stream source, ImageLimits limits)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(limits);
        TiffFile file = TiffFile.Open(source, limits);
        var frames = new List<TiffFrame>();
        var infos = new List<FrameInfo>();
        var seen = new HashSet<long>();
        ImageMetadata? metadata = null;
        long next = file.FirstIfd;
        long totalPixels = 0;
        long retained = 0;
        while (next != 0)
        {
            if (!seen.Add(next))
            {
                throw new ImageFormatException("The TIFF directory chain contains a loop.");
            }

            if (frames.Count >= limits.MaxFrames)
            {
                throw new ImageLimitException($"The TIFF has more than {limits.MaxFrames} pages.");
            }

            List<TiffEntry> entries = file.ReadIfd(next, out next);
            var frame = TiffFrame.Parse(file, entries, limits);
            totalPixels += (long)frame.Width * frame.Height;
            if (totalPixels > limits.MaxTotalPixels)
            {
                throw new ImageLimitException($"Total pixel count exceeds the limit of {limits.MaxTotalPixels}.");
            }

            retained += frame.RetainedBytes;
            if (retained > MaxRetainedBytes && limits.MaxMetadataBytes != long.MaxValue)
            {
                throw new ImageLimitException("The TIFF directories hold more strip tables than the decoder will keep in memory.");
            }

            if (frames.Count == 0)
            {
                metadata = TiffMetadataIo.Read(file, entries, limits);
            }

            frames.Add(frame);
            infos.Add(new FrameInfo(frame.Width, frame.Height, frame.Format));
        }

        if (frames.Count == 0)
        {
            throw new ImageFormatException("The TIFF has no image directory.");
        }

        return new ImageInfo(_format, infos, 0, metadata, new TiffImageState(file, [.. frames]));
    }

    public void DecodeFrame(Stream source, ImageInfo info, int frameIndex, in DecodeRequest request, IRowSink sink, ImageLimits limits, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(info);
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentNullException.ThrowIfNull(limits);
        if (info.DecoderState is not TiffImageState state)
        {
            throw new ArgumentException("The image info was not produced by the TIFF decoder.", nameof(info));
        }

        if ((uint)frameIndex >= (uint)state.Frames.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(frameIndex));
        }

        TiffFrame f = state.Frames[frameIndex];
        ImageRect r = request.Region;
        if (request.ScaleDenominator != 1 || r.X < 0 || r.Y < 0 || r.Width < 0 || r.Height < 0 || r.Right > f.Width || r.Bottom > f.Height
            || sink.Width != r.Width || sink.Height != r.Height || sink.Format != f.Format)
        {
            throw new ArgumentException("The request does not fit the frame or the sink.", nameof(request));
        }

        if (r.Width == 0 || r.Height == 0)
        {
            return;
        }

        TiffFile file = state.File.Rebind(source);
        int bpp = f.Format.BytesPerPixel;
        int outRowBytes = checked(r.Width * bpp);
        int chunkRows = Math.Clamp((1 << 20) / Math.Max(outRowBytes, 1), 1, 128);
        byte[] band = Pools<byte>.Pool.Rent(checked(outRowBytes * Math.Min(chunkRows, r.Height)));
        try
        {
            int rowBytes = (int)f.BlockRowBytes;
            int firstBy = r.Y / f.BlockHeight;
            int lastBy = (r.Bottom - 1) / f.BlockHeight;
            int firstBx = r.X / f.BlockWidth;
            int lastBx = (r.Right - 1) / f.BlockWidth;

            // A large read touches every block of the region once, so keeping them in the shared cache would only copy and retain
            // the whole frame. Such reads decode each block row into pooled buffers that are returned as soon as the row is done.
            int perRow = checked((lastBx - firstBx + 1) * f.Planes);
            long rowRaw = (long)perRow * f.BlockHeight * rowBytes;
            bool direct = (long)r.Width * r.Height >= DirectReadPixels && rowRaw <= MaxDirectRowBytes;
            byte[]?[] held = direct ? new byte[]?[perRow] : [];
            try
            {
                for (int by = firstBy; by <= lastBy; by++)
                {
                    int blockTop = by * f.BlockHeight;
                    int y0 = Math.Max(r.Y, blockTop);
                    int y1 = Math.Min(r.Bottom, Math.Min(blockTop + f.BlockHeight, f.Height));
                    if (direct)
                    {
                        ReleaseHeld(held);
                    }

                    for (int cy = y0; cy < y1; cy += chunkRows)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        int rows = Math.Min(chunkRows, y1 - cy);
                        for (int bx = firstBx; bx <= lastBx; bx++)
                        {
                            int blockLeft = bx * f.BlockWidth;
                            int x0 = Math.Max(r.X, blockLeft);
                            int x1 = Math.Min(r.Right, Math.Min(blockLeft + f.BlockWidth, f.Width));
                            for (int plane = 0; plane < f.Planes; plane++)
                            {
                                int index = (plane * f.BlocksPerPlane) + (by * f.BlocksAcross) + bx;
                                byte[] block;
                                TiffBlock? lease = null;
                                if (direct)
                                {
                                    int slot = ((bx - firstBx) * f.Planes) + plane;
                                    block = held[slot] ??= DecodeDirect(state, file, f, index, limits, cancellationToken);
                                }
                                else
                                {
                                    lease = GetBlock(state, file, f, frameIndex, index, limits, cancellationToken);
                                    block = lease.Data;
                                }

                                try
                                {
                                    for (int row = 0; row < rows; row++)
                                    {
                                        ReadOnlySpan<byte> src = block.AsSpan((cy + row - blockTop) * rowBytes, rowBytes);
                                        Span<byte> dst = band.AsSpan((row * outRowBytes) + ((x0 - r.X) * bpp));
                                        f.Converter.ConvertRow(src, plane, x0 - blockLeft, x1 - x0, dst);
                                    }
                                }
                                finally
                                {
                                    lease?.Release();
                                }
                            }
                        }

                        if (f.UnpremultiplyOnRead)
                        {
                            Span<byte> pix = band.AsSpan(0, rows * outRowBytes);
                            if (f.Format.Sample == SampleType.U16)
                            {
                                TiffKernels.Unpremultiply16(pix, f.Format.ChannelCount);
                            }
                            else
                            {
                                TiffKernels.Unpremultiply8(pix, f.Format.ChannelCount);
                            }
                        }

                        sink.WriteRows(cy - r.Y, band.AsSpan(0, rows * outRowBytes), rows);
                    }
                }
            }
            finally
            {
                ReleaseHeld(held);
            }
        }
        finally
        {
            Pools<byte>.Pool.Return(band);
        }
    }

    /// <summary>Block rows (all planes) up to this many raw bytes per worker are decoded into the caller's buffer without the tile cache.</summary>
    private const long MaxDirectUnitBytes = 16L << 20;

    /// <inheritdoc />
    public bool TryDecodeInto(Func<Stream> openSource, ImageInfo info, int frameIndex, PinnedDestination destination, PixelFormat destinationFormat, ConvertRowKernel? converter, int degree, ImageLimits limits, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(openSource);
        ArgumentNullException.ThrowIfNull(info);
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(limits);
        if (info.DecoderState is not TiffImageState state || (uint)frameIndex >= (uint)state.Frames.Length)
        {
            return false;
        }

        TiffFrame f = state.Frames[frameIndex];
        if ((long)f.Width * f.Height < DirectReadPixels || f.BlockRowBytes * f.BlockHeight * f.Planes > MaxDirectUnitBytes)
        {
            return false;
        }

        int bpp = f.Format.BytesPerPixel;
        int dbpp = destinationFormat.BytesPerPixel;
        int rowBytes = (int)f.BlockRowBytes;
        int units = f.BlocksAcross * f.BlocksDown;
        var options = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, degree), CancellationToken = cancellationToken };

        // Each unit is one strip or tile (all of its planes): decoded into pooled memory, converted row by row into its own disjoint
        // rectangle of the destination, and returned. The bytes written never depend on scheduling.
        void Unit(int u, TiffFile file)
        {
            int by = u / f.BlocksAcross;
            int bx = u % f.BlocksAcross;
            int blockTop = by * f.BlockHeight;
            int blockLeft = bx * f.BlockWidth;
            int rows = Math.Min(f.BlockHeight, f.Height - blockTop);
            int x1 = Math.Min(f.Width, blockLeft + f.BlockWidth);
            int count = x1 - blockLeft;
            var blocks = new byte[]?[f.Planes];
            byte[]? scratch = converter == null ? null : Pools<byte>.Pool.Rent(count * bpp);
            try
            {
                for (int plane = 0; plane < f.Planes; plane++)
                {
                    blocks[plane] = DecodeDirect(state, file, f, (plane * f.BlocksPerPlane) + u, limits, cancellationToken);
                }

                for (int row = 0; row < rows; row++)
                {
                    Span<byte> target = converter == null
                        ? destination.Slice((((blockTop + row) * f.Width) + blockLeft) * dbpp, count * dbpp)
                        : scratch.AsSpan(0, count * bpp);
                    for (int plane = 0; plane < f.Planes; plane++)
                    {
                        f.Converter.ConvertRow(blocks[plane].AsSpan(row * rowBytes, rowBytes), plane, 0, count, target);
                    }

                    if (f.UnpremultiplyOnRead)
                    {
                        if (f.Format.Sample == SampleType.U16)
                        {
                            TiffKernels.Unpremultiply16(target, f.Format.ChannelCount);
                        }
                        else
                        {
                            TiffKernels.Unpremultiply8(target, f.Format.ChannelCount);
                        }
                    }

                    if (converter != null)
                    {
                        converter(target, f.Format, destination.Slice((((blockTop + row) * f.Width) + blockLeft) * dbpp, count * dbpp), destinationFormat, count);
                    }
                }
            }
            finally
            {
                ReleaseHeld(blocks);
                if (scratch != null)
                {
                    Pools<byte>.Pool.Return(scratch);
                }
            }
        }

        try
        {
            if (options.MaxDegreeOfParallelism == 1 || units == 1)
            {
                using Stream stream = openSource();
                TiffFile file = state.File.Rebind(stream);
                for (int u = 0; u < units; u++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    Unit(u, file);
                }
            }
            else
            {
                // Every worker reads through its own stream, opened once and kept for all the blocks it takes. Blocks are handed out one
                // at a time: a page has only a handful of strips, and range chunking would leave some workers with twice the work of others.
                Parallel.ForEach(
                    Partitioner.Create(0, units, 1),
                    options,
                    () =>
                    {
                        Stream stream = openSource();
                        return (stream, file: state.File.Rebind(stream));
                    },
                    (range, _, local) =>
                    {
                        for (int u = range.Item1; u < range.Item2; u++)
                        {
                            Unit(u, local.file);
                        }

                        return local;
                    },
                    local => local.stream.Dispose());
            }
        }
        catch (AggregateException ex) when (ex.InnerExceptions.Count > 0)
        {
            // Surface the real error (format, limit, cancellation) rather than the wrapper.
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerExceptions[0]).Throw();
            throw;
        }

        return true;
    }

    private static void ReleaseHeld(byte[]?[] held)
    {
        for (int i = 0; i < held.Length; i++)
        {
            if (held[i] is { } b)
            {
                Pools<byte>.Pool.Return(b);
                held[i] = null;
            }
        }
    }

    private static byte[] DecodeDirect(TiffImageState state, TiffFile file, TiffFrame f, int index, ImageLimits limits, CancellationToken ct)
    {
        Interlocked.Increment(ref state.BlocksDecoded);
        return TiffBlockDecoder.DecodePooled(file, f, index, limits, ct);
    }

    private static TiffBlock GetBlock(TiffImageState state, TiffFile file, TiffFrame f, int frameIndex, int index, ImageLimits limits, CancellationToken ct)
    {
        long key = ((long)frameIndex << 32) | (uint)index;
        return state.Cache.GetOrCompute(
            key,
            (state, file, f, index, limits, ct),
            static s =>
            {
                Interlocked.Increment(ref s.state.BlocksDecoded);
                return TiffBlockDecoder.DecodePooled(s.file, s.f, s.index, s.limits, s.ct);
            },
            static s => s.f.RowsInBlock(s.index % s.f.BlocksPerPlane) * (int)s.f.BlockRowBytes);
    }
}
