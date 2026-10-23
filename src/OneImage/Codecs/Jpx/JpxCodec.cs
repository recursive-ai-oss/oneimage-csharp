using System.Buffers.Binary;

namespace OneImage.Codecs.Jpx;

/// <summary>
/// A decode-only JPEG 2000 Part 1 decoder (ITU-T T.800 | ISO/IEC 15444-1) for JP2/JPX files and raw codestreams.
/// </summary>
/// <remarks>
/// <para>This codec is internal and is not registered with <see cref="ImageConfiguration"/>; it exists for PDF's JPXDecode
/// filter. It decodes every progression order, tiles and tile-parts, precincts, all code-block styles, the 5-3 and 9-7
/// wavelets, the RCT and ICT, up to 16 bits per sample (signed too), subsampled components, palettes and channel definitions.</para>
/// <para><b>Behavior.</b> Lossless (5-3) images decode bit-exactly. Lossy (9-7) output is computed in single-precision floating
/// point and rounds half up; it agrees with other decoders within one level at 8 bits. Truncated or damaged tile data decodes
/// as far as it goes unless <see cref="JpxDecodeOptions.Strict"/> is set. A reconstructed coefficient that was not fully
/// decoded sits at the middle of its uncertainty interval.</para>
/// <para><b>Safety.</b> Everything that reads the file is safe, bounds-checked code. Dimensions, component, tile, code-block and
/// precinct counts, working memory and entropy-decoding work are checked against the header before the allocation or loop they
/// guard; only <see cref="ImageException"/> types and <see cref="OperationCanceledException"/> leave the decoder.</para>
/// </remarks>
internal static class JpxCodec
{
    /// <summary>Returns true when the bytes start with the JP2 signature box or a codestream SOC+SIZ marker.</summary>
    /// <param name="data">The first bytes of the file (at least 12).</param>
    public static bool Matches(ReadOnlySpan<byte> data) => JpxFile.LooksLikeJpx(data);

    /// <summary>Reads the headers only and describes the image.</summary>
    /// <param name="data">The file or codestream.</param>
    /// <param name="options">Options; only the limits are used.</param>
    /// <exception cref="ImageFormatException">The data is not a valid JPEG 2000 header.</exception>
    /// <exception cref="ImageLimitException">A header value exceeds the limits.</exception>
    public static JpxInfo Identify(ReadOnlyMemory<byte> data, JpxDecodeOptions? options = null)
    {
        options ??= JpxDecodeOptions.Default;
        JpxFileInfo file = JpxFile.Parse(data, options);
        JpxCodestream cs = JpxCodestream.Parse(file.Codestream, options, headerOnly: true);
        return new JpxInfo
        {
            Width = (int)(JpxTileBuilder.CeilDiv(cs.Siz.Xsiz, cs.Siz.XR.Min()) - JpxTileBuilder.CeilDiv(cs.Siz.XOsiz, cs.Siz.XR.Min())),
            Height = (int)(JpxTileBuilder.CeilDiv(cs.Siz.Ysiz, cs.Siz.YR.Min()) - JpxTileBuilder.CeilDiv(cs.Siz.YOsiz, cs.Siz.YR.Min())),
            Components = cs.Siz.Csiz,
            BitDepths = cs.Siz.Precision,
            Signed = cs.Siz.Signed,
            Tiles = cs.Siz.TileCount,
            MaxReduction = MaxReduction(cs),
            Layers = cs.Main.Layers,
            ColorSpace = file.ColorSpace,
            HasPalette = file.HasPalette,
            IsFileFormat = file.IsFile,
        };
    }

    /// <summary>Decodes an image.</summary>
    /// <param name="data">The file or codestream. The memory must not change during the call.</param>
    /// <param name="options">Options, or null for the defaults.</param>
    /// <param name="cancellationToken">Cancellation, checked between packets, code-blocks and wavelet levels.</param>
    /// <returns>The channels, colour information and flags.</returns>
    /// <exception cref="ImageFormatException">The data is not valid JPEG 2000 (or, with <see cref="JpxDecodeOptions.Strict"/>, is damaged).</exception>
    /// <exception cref="ImageLimitException">The image would exceed the limits.</exception>
    /// <exception cref="ArgumentException">The requested region lies outside the output.</exception>
    public static JpxImage Decode(ReadOnlyMemory<byte> data, JpxDecodeOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= JpxDecodeOptions.Default;
        Core c = DecodeCore(data, options, cancellationToken);
        JpxImage image = Assemble(c.File, c.Siz, c.Planes, options, c.Reduction, c.X0, c.Y0, c.Width, c.Height);
        image.Reduction = c.Reduction;
        image.Damaged = c.Damaged;
        return image;
    }

    /// <summary>
    /// Decodes the codestream components at their own sampling, without palette, channel definitions or upsampling. Each plane
    /// covers the part of its component grid that the output region needs. Used by conformance tests.
    /// </summary>
    /// <param name="data">The file or codestream.</param>
    /// <param name="options">Options, or null for the defaults.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>One plane per component, with the damage flag.</returns>
    public static (JpxPlane[] Planes, bool Damaged) DecodeComponents(ReadOnlyMemory<byte> data, JpxDecodeOptions? options = null, CancellationToken cancellationToken = default)
    {
        Core c = DecodeCore(data, options ?? JpxDecodeOptions.Default, cancellationToken);
        return (c.Planes, c.Damaged);
    }

    private sealed record Core(JpxFileInfo File, JpxSiz Siz, JpxPlane[] Planes, int Reduction, int X0, int Y0, int Width, int Height, bool Damaged);

    private static Core DecodeCore(ReadOnlyMemory<byte> data, JpxDecodeOptions options, CancellationToken cancellationToken)
    {
        if (options.ReductionLevels < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "ReductionLevels cannot be negative.");
        }

        JpxFileInfo file = JpxFile.Parse(data, options);
        JpxCodestream cs = JpxCodestream.Parse(file.Codestream, options, headerOnly: false);
        JpxSiz siz = cs.Siz;
        int d = Math.Min(options.ReductionLevels, MaxReduction(cs));

        // The output grid is the reference grid divided by the finest component sampling, so a codestream whose components are
        // all sub-sampled by the same factor (or whose first component is) yields its natural pixel size.
        int sx = siz.XR.Min();
        int sy = siz.YR.Min();
        long xs0 = JpxTileBuilder.CeilDiv(siz.XOsiz, sx);
        long ys0 = JpxTileBuilder.CeilDiv(siz.YOsiz, sy);
        long xs1 = JpxTileBuilder.CeilDiv(siz.Xsiz, sx);
        long ys1 = JpxTileBuilder.CeilDiv(siz.Ysiz, sy);
        int gx0 = (int)JpxTileBuilder.CeilShift(xs0, d);
        int gy0 = (int)JpxTileBuilder.CeilShift(ys0, d);
        int gx1 = (int)JpxTileBuilder.CeilShift(xs1, d);
        int gy1 = (int)JpxTileBuilder.CeilShift(ys1, d);
        int fullW = gx1 - gx0;
        int fullH = gy1 - gy0;
        ImageRect region = options.Region ?? new ImageRect(0, 0, fullW, fullH);
        if (region.Width <= 0 || region.Height <= 0 || !region.IsInside(fullW, fullH))
        {
            throw new ArgumentException("The region lies outside the decoded image.", nameof(options));
        }

        int outW = region.Width;
        int outH = region.Height;
        long rx0 = gx0 + region.X;
        long ry0 = gy0 + region.Y;
        long rx1 = rx0 + outW;
        long ry1 = ry0 + outH;

        int channelCount = file.HasPalette && options.ApplyPalette && file.ComponentMap.Length > 0 ? file.ComponentMap.Length : siz.Csiz;
        CheckSize(options, outW, outH, channelCount, siz.Csiz, file.Codestream.Length);

        // One plane per component, covering the part of its grid the output needs.
        var planes = new JpxPlane[siz.Csiz];
        for (int c = 0; c < planes.Length; c++)
        {
            long cx0 = JpxTileBuilder.CeilShift(JpxTileBuilder.CeilDiv(siz.XOsiz, siz.XR[c]), d);
            long cx1 = JpxTileBuilder.CeilShift(JpxTileBuilder.CeilDiv(siz.Xsiz, siz.XR[c]), d);
            long cy0 = JpxTileBuilder.CeilShift(JpxTileBuilder.CeilDiv(siz.YOsiz, siz.YR[c]), d);
            long cy1 = JpxTileBuilder.CeilShift(JpxTileBuilder.CeilDiv(siz.Ysiz, siz.YR[c]), d);
            if (cx1 <= cx0 || cy1 <= cy0)
            {
                planes[c] = new JpxPlane(0, 0, 0, 0);
                continue;
            }

            long kx0 = Math.Clamp(rx0 * sx / siz.XR[c], cx0, cx1 - 1);
            long kx1 = Math.Clamp((rx1 - 1) * sx / siz.XR[c], cx0, cx1 - 1);
            long ky0 = Math.Clamp(ry0 * sy / siz.YR[c], cy0, cy1 - 1);
            long ky1 = Math.Clamp((ry1 - 1) * sy / siz.YR[c], cy0, cy1 - 1);
            planes[c] = new JpxPlane((int)kx0, (int)ky0, (int)(kx1 - kx0 + 1), (int)(ky1 - ky0 + 1));
        }

        // Decode the tiles that touch the region.
        long tier1Budget = double.IsInfinity(options.Limits.MaxExpansionRatio)
            ? long.MaxValue
            : (1L << 27) + (2048L * file.Codestream.Length);
        bool damaged = cs.Truncated;
        var wanted = new List<int>();
        for (int t = 0; t < siz.TileCount; t++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int tp = t % siz.TilesX;
            int tq = t / siz.TilesX;
            long tx0 = Math.Max((long)siz.XTOsiz + ((long)tp * siz.XTsiz), siz.XOsiz);
            long ty0 = Math.Max((long)siz.YTOsiz + ((long)tq * siz.YTsiz), siz.YOsiz);
            long tx1 = Math.Min((long)siz.XTOsiz + ((tp + 1L) * siz.XTsiz), siz.Xsiz);
            long ty1 = Math.Min((long)siz.YTOsiz + ((tq + 1L) * siz.YTsiz), siz.Ysiz);
            long ax0 = JpxTileBuilder.CeilShift(JpxTileBuilder.CeilDiv(tx0, sx), d);
            long ay0 = JpxTileBuilder.CeilShift(JpxTileBuilder.CeilDiv(ty0, sy), d);
            long ax1 = JpxTileBuilder.CeilShift(JpxTileBuilder.CeilDiv(tx1, sx), d);
            long ay1 = JpxTileBuilder.CeilShift(JpxTileBuilder.CeilDiv(ty1, sy), d);
            if (ax1 <= rx0 || ax0 >= rx1 || ay1 <= ry0 || ay0 >= ry1)
            {
                continue;
            }

            wanted.Add(t);
        }

        // Many tiles: one tile per thread, each decoded serially (no nested fork-join per code-block batch or wavelet level). The
        // coefficient memory of the tiles in flight is capped at half the working-memory limit; otherwise tiles are decoded one at
        // a time with the parallelism inside the tile. Output does not depend on the choice: tiles write disjoint samples.
        int degree = Math.Max(1, options.MaxDegreeOfParallelism);
        long tileBytes = 4L * siz.XTsiz * siz.YTsiz * siz.Csiz;
        if (degree > 1 && wanted.Count >= 2 && tileBytes * Math.Min(degree, wanted.Count) <= options.MaxWorkingBytes / 2)
        {
            JpxDecodeOptions serial = options.WithDegree(1);
            var errors = new ImageException?[wanted.Count];
            int firstFailure = int.MaxValue;
            int anyDamaged = 0;
            long[] budget = [tier1Budget];
            Parallel.For(
                0,
                wanted.Count,
                new ParallelOptions { MaxDegreeOfParallelism = Math.Min(degree, wanted.Count), CancellationToken = cancellationToken },
                i =>
                {
                    // A failed tile makes later tiles pointless, but never earlier ones: the lowest failing index is always the one reported.
                    if (i > Volatile.Read(ref firstFailure))
                    {
                        return;
                    }

                    try
                    {
                        if (JpxTileDecoder.Decode(cs, wanted[i], serial, d, planes, ref budget[0], cancellationToken))
                        {
                            Volatile.Write(ref anyDamaged, 1);
                        }
                    }
                    catch (ImageException e)
                    {
                        errors[i] = e;
                        int seen;
                        while (i < (seen = Volatile.Read(ref firstFailure)) && Interlocked.CompareExchange(ref firstFailure, i, seen) != seen)
                        {
                        }
                    }
                });
            foreach (ImageException? e in errors)
            {
                if (e is not null)
                {
                    System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e).Throw();
                }
            }

            damaged |= anyDamaged != 0;
        }
        else
        {
            foreach (int t in wanted)
            {
                cancellationToken.ThrowIfCancellationRequested();
                damaged |= JpxTileDecoder.Decode(cs, t, options, d, planes, ref tier1Budget, cancellationToken);
            }
        }

        return new Core(file, siz, planes, d, (int)rx0, (int)ry0, outW, outH, damaged);
    }

    private static int MaxReduction(JpxCodestream cs)
    {
        // Tile-header COD/COC can lower this further; those tiles then fail typed when the reduction exceeds them.
        int min = cs.Main.Cod!.Levels;
        for (int c = 0; c < cs.Siz.Csiz; c++)
        {
            min = Math.Min(min, (cs.Main.Coc[c] ?? cs.Main.Cod).Levels);
        }

        return min;
    }

    private static void CheckSize(JpxDecodeOptions options, int w, int h, int channels, int components, int inputBytes)
    {
        long samples = (long)w * h * channels;
        double ratio = options.Limits.MaxExpansionRatio;
        if (!double.IsInfinity(ratio))
        {
            // Blank images legitimately compress to a few bytes, so a floor of 64M samples is always allowed.
            double allowed = Math.Max(1 << 26, ratio * inputBytes);
            if (samples > allowed)
            {
                throw new ImageLimitException($"JPEG 2000 image of {samples} samples is too large for {inputBytes} bytes of data.");
            }
        }

        long bytes = (2L * samples) + (2L * w * h * components);
        if (bytes > options.MaxWorkingBytes)
        {
            throw new ImageLimitException($"JPEG 2000 image needs {bytes} bytes of sample memory, over the limit of {options.MaxWorkingBytes}.");
        }
    }

    // ---- assembling channels ----

    private static JpxImage Assemble(JpxFileInfo file, JpxSiz siz, JpxPlane[] planes, JpxDecodeOptions options, int d, int rx0, int ry0, int w, int h)
    {
        var channels = new List<ushort[]>();
        var depths = new List<int>();
        var signed = new List<bool>();
        var cache = new ushort[planes.Length][];

        ushort[] Component(int c)
        {
            return cache[c] ??= Resample(planes[c], siz.XR[c], siz.YR[c], siz.XR.Min(), siz.YR.Min(), rx0, ry0, w, h);
        }

        if (file.HasPalette && options.ApplyPalette && file.ComponentMap.Length > 0)
        {
            foreach ((int comp, int type, int column) in file.ComponentMap)
            {
                if (comp >= siz.Csiz)
                {
                    throw new ImageFormatException("Component mapping names a component that does not exist.");
                }

                if (type == 1)
                {
                    if (column >= file.PaletteColumns.Length)
                    {
                        throw new ImageFormatException("Component mapping names a palette column that does not exist.");
                    }

                    ushort[] table = file.PaletteValues[column];
                    ushort[] src = Component(comp);
                    var dst = new ushort[src.Length];
                    int last = table.Length - 1;
                    for (int i = 0; i < src.Length; i++)
                    {
                        dst[i] = table[Math.Min(src[i], last)];
                    }

                    channels.Add(dst);
                    depths.Add(file.PaletteColumns[column].Bits);
                    signed.Add(file.PaletteColumns[column].Signed);
                }
                else
                {
                    channels.Add(Component(comp));
                    depths.Add(siz.Precision[comp]);
                    signed.Add(siz.Signed[comp]);
                }
            }
        }
        else
        {
            for (int c = 0; c < siz.Csiz; c++)
            {
                channels.Add(Component(c));
                depths.Add(siz.Precision[c]);
                signed.Add(siz.Signed[c]);
            }
        }

        // Order colour channels first and the opacity channel last, per the channel definitions.
        int n = channels.Count;
        var order = new List<int>();
        int alpha = -1;
        bool premultiplied = false;
        int colorCount;
        if (file.ChannelDefinitions.Length > 0)
        {
            var colors = new SortedDictionary<int, int>();
            var extras = new List<int>();
            var seen = new bool[n];
            foreach ((int ch, int type, int assoc) in file.ChannelDefinitions)
            {
                if (ch >= n || seen[ch])
                {
                    continue;
                }

                seen[ch] = true;
                if (type == 0 && assoc >= 1 && assoc != 65535 && !colors.ContainsKey(assoc))
                {
                    colors[assoc] = ch;
                }
                else if ((type == 1 || type == 2) && assoc == 0 && alpha < 0)
                {
                    alpha = ch;
                    premultiplied = type == 2;
                }
                else
                {
                    extras.Add(ch);
                }
            }

            order.AddRange(colors.Values);
            colorCount = order.Count;
            order.AddRange(extras);
            for (int ch = 0; ch < n; ch++)
            {
                if (!seen[ch])
                {
                    order.Add(ch);
                }
            }

            if (alpha >= 0)
            {
                order.Add(alpha);
            }
        }
        else
        {
            for (int ch = 0; ch < n; ch++)
            {
                order.Add(ch);
            }

            colorCount = ColorChannels(file, n);
        }

        var outPlanes = new ushort[order.Count][];
        var outDepths = new int[order.Count];
        var outSigned = new bool[order.Count];
        for (int i = 0; i < order.Count; i++)
        {
            outPlanes[i] = channels[order[i]];
            outDepths[i] = depths[order[i]];
            outSigned[i] = signed[order[i]];
        }

        var image = new JpxImage(w, h, outPlanes, outDepths, outSigned)
        {
            ColorSpace = file.ColorSpace,
            IccProfile = file.Icc,
            ColorChannelCount = colorCount,
            AlphaChannel = alpha >= 0 ? order.Count - 1 : -1,
            AlphaPremultiplied = premultiplied,
        };

        if (options.ConvertSycc && file.ColorSpace == JpxColorSpace.Sycc && colorCount >= 3 && !file.HasPalette)
        {
            ConvertSycc(image);
        }

        _ = d;
        return image;
    }

    private static int ColorChannels(JpxFileInfo file, int channelCount)
    {
        switch (file.ColorSpace)
        {
            case JpxColorSpace.Gray:
                return Math.Min(1, channelCount);
            case JpxColorSpace.Srgb:
            case JpxColorSpace.Sycc:
            case JpxColorSpace.CieLab:
            case JpxColorSpace.ESrgb:
            case JpxColorSpace.RommRgb:
                return Math.Min(3, channelCount);
            case JpxColorSpace.Cmyk:
                return Math.Min(4, channelCount);
            case JpxColorSpace.Icc when file.Icc.Length >= 20:
                ReadOnlySpan<byte> sig = file.Icc.Span.Slice(16, 4);
                if (sig.SequenceEqual("GRAY"u8))
                {
                    return Math.Min(1, channelCount);
                }

                if (sig.SequenceEqual("RGB "u8) || sig.SequenceEqual("Lab "u8))
                {
                    return Math.Min(3, channelCount);
                }

                if (sig.SequenceEqual("CMYK"u8))
                {
                    return Math.Min(4, channelCount);
                }

                return channelCount;
            default:
                return channelCount;
        }
    }

    /// <summary>
    /// Replicates a component plane to the output grid: output pixel (X, Y) of the reduced reference grid reads component
    /// sample (floor(X * sx / XR), floor(Y * sy / YR)), where sx and sy are the finest component sampling,, clamped to the component's extent. No interpolation is applied.
    /// </summary>
    private static ushort[] Resample(JpxPlane p, int xr, int yr, int sx, int sy, int rx0, int ry0, int w, int h)
    {
        long count = (long)w * h;
        if (count > Array.MaxLength)
        {
            throw new ImageLimitException("Output too large.");
        }

        if (p.Width == w && p.Height == h && xr == sx && yr == sy && p.X0 == rx0 && p.Y0 == ry0)
        {
            return p.Data;
        }

        var dst = new ushort[count];
        if (p.Width == 0 || p.Height == 0)
        {
            return dst;
        }

        var cols = new int[w];
        for (int x = 0; x < w; x++)
        {
            long k = Math.Clamp((rx0 + (long)x) * sx / xr, p.X0, p.X0 + p.Width - 1);
            cols[x] = (int)(k - p.X0);
        }

        for (int y = 0; y < h; y++)
        {
            long k = Math.Clamp((ry0 + (long)y) * sy / yr, p.Y0, p.Y0 + p.Height - 1);
            ReadOnlySpan<ushort> srcRow = p.Data.AsSpan((int)(k - p.Y0) * p.Width, p.Width);
            Span<ushort> dstRow = dst.AsSpan(y * w, w);
            for (int x = 0; x < w; x++)
            {
                dstRow[x] = srcRow[cols[x]];
            }
        }

        return dst;
    }

    // sYCC to sRGB (T.800 Annex M / IEC 61966-2-1 amendment 1) in 16.16 fixed point, so every platform gets the same bits.
    private static void ConvertSycc(JpxImage image)
    {
        int depth = image.BitDepths[0];
        if (image.BitDepths[1] != depth || image.BitDepths[2] != depth || image.Signed[0] || image.Signed[1] || image.Signed[2])
        {
            return;
        }

        int mid = 1 << (depth - 1);
        int max = (1 << depth) - 1;
        ushort[] y = image.Planes[0];
        ushort[] cb = image.Planes[1];
        ushort[] cr = image.Planes[2];
        if (ReferenceEquals(y, cb) || ReferenceEquals(y, cr) || ReferenceEquals(cb, cr))
        {
            return;
        }

        for (int i = 0; i < y.Length; i++)
        {
            long yy = (long)y[i] << 16;
            int u = cb[i] - mid;
            int v = cr[i] - mid;
            long r = (yy + (91881L * v) + 32768) >> 16;
            long g = (yy - (22554L * u) - (46802L * v) + 32768) >> 16;
            long b = (yy + (116130L * u) + 32768) >> 16;
            y[i] = (ushort)Math.Clamp(r, 0, max);
            cb[i] = (ushort)Math.Clamp(g, 0, max);
            cr[i] = (ushort)Math.Clamp(b, 0, max);
        }

        image.ColorSpace = JpxColorSpace.Srgb;
        image.ConvertedFromSycc = true;
    }
}
