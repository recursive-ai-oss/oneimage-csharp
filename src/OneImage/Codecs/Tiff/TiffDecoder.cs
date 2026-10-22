using System.Buffers;
using OneImage.Kernels;

namespace OneImage.Codecs.Tiff;

/// <summary>Everything ReadInfo learned that the frame decodes need, so they never re-parse the directories.</summary>
internal sealed class TiffImageState
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
}

/// <summary>
/// TIFF decoder. Strips and tiles are addressed through the offset tables, so a region read touches only the blocks that
/// intersect it and the decoder advertises random access. See <see cref="TiffFormat"/> for the supported feature set.
/// </summary>
internal sealed class TiffDecoder : IImageDecoder
{
    // Offset tables, palettes and JPEG tables of all pages stay in memory; many pages pointing at the same big table must not multiply it.
    private const long MaxRetainedBytes = 128L << 20;

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
        byte[] band = ArrayPool<byte>.Shared.Rent(checked(outRowBytes * Math.Min(chunkRows, r.Height)));
        try
        {
            int rowBytes = (int)f.BlockRowBytes;
            int firstBy = r.Y / f.BlockHeight;
            int lastBy = (r.Bottom - 1) / f.BlockHeight;
            int firstBx = r.X / f.BlockWidth;
            int lastBx = (r.Right - 1) / f.BlockWidth;
            for (int by = firstBy; by <= lastBy; by++)
            {
                int blockTop = by * f.BlockHeight;
                int y0 = Math.Max(r.Y, blockTop);
                int y1 = Math.Min(r.Bottom, Math.Min(blockTop + f.BlockHeight, f.Height));
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
                            byte[] block = GetBlock(state, file, f, frameIndex, index, limits, cancellationToken);
                            for (int row = 0; row < rows; row++)
                            {
                                ReadOnlySpan<byte> src = block.AsSpan((cy + row - blockTop) * rowBytes, rowBytes);
                                Span<byte> dst = band.AsSpan((row * outRowBytes) + ((x0 - r.X) * bpp));
                                f.Converter.ConvertRow(src, plane, x0 - blockLeft, x1 - x0, dst);
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
            ArrayPool<byte>.Shared.Return(band);
        }
    }

    private static byte[] GetBlock(TiffImageState state, TiffFile file, TiffFrame f, int frameIndex, int index, ImageLimits limits, CancellationToken ct)
    {
        long key = ((long)frameIndex << 32) | (uint)index;
        return state.Cache.GetOrCompute(key, (state, file, f, index, limits, ct), static s =>
        {
            Interlocked.Increment(ref s.state.BlocksDecoded);
            return TiffBlockDecoder.Decode(s.file, s.f, s.index, s.limits, s.ct);
        });
    }
}
