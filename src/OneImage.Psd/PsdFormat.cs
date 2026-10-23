using System.Buffers.Binary;

namespace OneImage.Psd;

/// <summary>Options for writing a flat PSD through <c>Image.Save</c> (see <see cref="PsdFormat"/>).</summary>
public sealed class PsdEncoderOptions : ImageEncoderOptions
{
    /// <summary>Gets whether to write a PSB; <see langword="null"/> chooses by size (see <see cref="PsdWriteOptions.Large"/>).</summary>
    public bool? Large { get; init; }

    /// <summary>Gets how channels are compressed. The default is <see cref="PsdCompression.Rle"/>.</summary>
    public PsdCompression Compression { get; init; } = PsdCompression.Rle;
}

/// <summary>
/// The PSD and PSB image format: <c>Image.Load</c> of a Photoshop file gives its merged composite, and <c>Image.Save("x.psd")</c> writes a flat
/// file. Register it with <see cref="ImageConfiguration.Add"/>; <see cref="ImageConfiguration.Default"/> does not include it, so the package
/// trims away when unused. To work with the layers, use <see cref="PsdFile"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Reading.</b> The composite is the file's stored merged image, copied bit for bit in the file's own depth: 8, 16 or 32 bits, gray or RGB,
/// with a transparency channel when the file says its merged image has one. Bitmap files read as 8-bit gray and indexed files as RGB. CMYK and
/// Lab files are converted to sRGB through the color engine, using the embedded profile (or the generic CMYK profile) as the source. The ICC
/// profile of RGB and gray files is kept as the image's profile. A file saved without a composite ("maximize compatibility" off) is rendered
/// from its layers instead. Identify reads only the header and the image resources.
/// </para>
/// <para><b>Writing.</b> A flat file, run-length coded; images with alpha are written as one layer with a transparent merged image.</para>
/// </remarks>
public sealed class PsdFormat : IImageFormat
{
    /// <summary>Initializes the format with custom limits.</summary>
    /// <param name="limits">The PSD limits, or <see langword="null"/> for <see cref="PsdLimits.Default"/>.</param>
    /// <param name="unmatte">
    /// Whether to remove the white matte from the colors of a composite that has transparency (the default). Photoshop stores such a
    /// merged image blended over white; with <see langword="true"/> the colors are divided back out so the result is ordinary straight alpha.
    /// With <see langword="false"/> the stored samples are returned unchanged.
    /// </param>
    public PsdFormat(PsdLimits? limits = null, bool unmatte = true)
    {
        Limits = limits ?? PsdLimits.Default;
        Unmatte = unmatte;
        Decoder = new PsdDecoder(this);
    }

    /// <summary>Gets a shared instance with the default limits.</summary>
    public static PsdFormat Instance { get; } = new();

    /// <summary>Gets whether the white matte is removed from a composite that has transparency.</summary>
    public bool Unmatte { get; }

    /// <summary>Gets the PSD limits of this registration.</summary>
    public PsdLimits Limits { get; }

    /// <inheritdoc />
    public string Name => "psd";

    /// <inheritdoc />
    public IReadOnlyList<string> Extensions { get; } = [".psd", ".psb"];

    /// <inheritdoc />
    public string MimeType => "image/vnd.adobe.photoshop";

    /// <inheritdoc />
    public int HeaderLength => 6;

    /// <inheritdoc />
    public IImageDecoder? Decoder { get; }

    /// <inheritdoc />
    public IImageEncoder? Encoder { get; } = new PsdEncoder();

    /// <inheritdoc />
    public bool Matches(ReadOnlySpan<byte> header) =>
        header.Length >= 6 && header[0] == (byte)'8' && header[1] == (byte)'B' && header[2] == (byte)'P' && header[3] == (byte)'S' && header[4] == 0 && header[5] is 1 or 2;
}

internal sealed class PsdEncoder : IImageEncoder
{
    public IReadOnlyList<PixelFormat> SupportedFormats { get; } =
    [
        PixelFormat.Rgba8, PixelFormat.Rgb8, PixelFormat.Gray8, PixelFormat.GrayAlpha8, PixelFormat.Rgb16, PixelFormat.Rgba16,
        PixelFormat.Gray16, PixelFormat.RgbF32, PixelFormat.RgbaF32, PixelFormat.GrayF32,
    ];

    public bool SupportsMultipleFrames => false;

    public void Encode(ImageEncodeRequest request, Stream destination, CancellationToken cancellationToken)
    {
        ImageFrameReader frame = request.Frames[0];
        var options = request.Options as PsdEncoderOptions ?? new PsdEncoderOptions();
        var src = new RawFrameSource(frame);
        var spec = new WriteSpec
        {
            Width = frame.Width,
            Height = frame.Height,
            Depth = src.Depth,
            Gray = frame.Format.Model == ColorModel.Gray,
            CompositeAlpha = src.HasAlpha,
            Icc = request.Metadata.IccProfile.Length > 0 ? request.Metadata.IccProfile.ToArray() : null,
            Composite = src,
        };
        if (src.HasAlpha)
        {
            // Transparency in a PSD belongs to a layer; the merged image then carries it as its first extra channel.
            var layer = new LayerEntry { Name = "Layer 0", Right = frame.Width, Bottom = frame.Height, Pixels = new RawFrameSource(frame) };
            layer.Blocks.Add(new PsdBlock("luni", UnicodeName("Layer 0")));
            spec.Layers.Add(layer);
        }

        PsdWriter.Write(spec, destination, new PsdWriteOptions { Large = options.Large, Compression = options.Compression }, cancellationToken);
    }

    private static byte[] UnicodeName(string name)
    {
        byte[] utf = System.Text.Encoding.BigEndianUnicode.GetBytes(name);
        byte[] data = new byte[4 + utf.Length + 2];
        BinaryPrimitives.WriteUInt32BigEndian(data, (uint)(name.Length + 1));
        utf.CopyTo(data, 4);
        return data;
    }
}

internal sealed class PsdDecoder : IImageDecoder
{
    private readonly PsdFormat _format;

    public PsdDecoder(PsdFormat format) => _format = format;

    public DecoderCapabilities Capabilities => DecoderCapabilities.None;

    private sealed class State
    {
        public required PsdHeader Header { get; init; }

        public required long DataPosition { get; init; }

        public required byte[] Palette { get; init; }

        public required bool Alpha { get; init; }

        public required bool HasComposite { get; init; }

        public required PixelFormat Format { get; init; }

        public byte[]? Icc { get; init; }
    }

    public ImageInfo ReadInfo(Stream source, ImageLimits limits)
    {
        var io = new FileIo(source);
        PsdStructure s = PsdParser.Parse(io, _format.Limits, limits, layers: false);
        PsdHeader h = s.Header;
        long remaining = io.Length - s.ImageDataPosition;
        bool hasComposite = remaining >= 2;
        int nColor = BandConverter.ColorChannelCount(h.Mode);
        bool alpha = s.CompositeHasAlpha && h.Channels > nColor && h.Mode is not (PsdColorMode.Indexed or PsdColorMode.Bitmap or PsdColorMode.Multichannel);
        if (hasComposite && h.Mode != PsdColorMode.Multichannel)
        {
            // Some writers store a stub of raw image data (a few bytes of an otherwise empty file). Too little raw data for the image means
            // there is no usable composite, the same as a file that ends after the layers; the layers are rendered instead.
            Span<byte> code = stackalloc byte[2];
            io.ReadAt(s.ImageDataPosition, code);
            if (BinaryPrimitives.ReadUInt16BigEndian(code) == 0)
            {
                long needed = (long)(nColor + (alpha ? 1 : 0)) * ChannelReader.RowBytesFor(h.Width, h.Depth) * h.Height;
                hasComposite = remaining - 2 >= needed;
            }
        }

        PixelFormat format = hasComposite ? CompositeFormat(h, alpha) : PsdFile.DocumentFormatFor(h).Format;
        if (!hasComposite)
        {
            alpha = true;
        }

        var meta = ImageMetadata.Empty;
        if (s.IccProfile is { Length: > 0 } icc && h.Mode is PsdColorMode.Rgb or PsdColorMode.Grayscale or PsdColorMode.Duotone or PsdColorMode.Bitmap)
        {
            meta = meta.WithIccProfile(icc);
        }

        var state = new State
        {
            Header = h,
            DataPosition = s.ImageDataPosition,
            Palette = s.ColorModeData,
            Alpha = alpha,
            HasComposite = hasComposite,
            Format = format,
            Icc = s.IccProfile,
        };
        return new ImageInfo(_format, [new FrameInfo(h.Width, h.Height, format)], 0, meta, state);
    }

    // The format a composite is delivered in: the file's own depth; CMYK and Lab are converted to RGB.
    private static PixelFormat CompositeFormat(PsdHeader h, bool alpha)
    {
        switch (h.Mode)
        {
            case PsdColorMode.Bitmap:
                return PixelFormat.Gray8;
            case PsdColorMode.Grayscale or PsdColorMode.Duotone:
                return h.Depth switch
                {
                    8 => alpha ? PixelFormat.GrayAlpha8 : PixelFormat.Gray8,
                    16 => alpha ? PixelFormat.Rgba16 : PixelFormat.Gray16,
                    _ => alpha ? PixelFormat.RgbaF32 : PixelFormat.GrayF32,
                };
            case PsdColorMode.Indexed:
                return PixelFormat.Rgb8;
            case PsdColorMode.Cmyk:
                return alpha ? PixelFormat.Rgba8 : PixelFormat.Rgb8;
            case PsdColorMode.Lab:
                return h.Depth == 8 ? (alpha ? PixelFormat.Rgba8 : PixelFormat.Rgb8) : (alpha ? PixelFormat.Rgba16 : PixelFormat.Rgb16);
            case PsdColorMode.Multichannel:
                throw new ImageFormatException("Multichannel PSD files are not supported.");
            default:
                return h.Depth switch
                {
                    8 => alpha ? PixelFormat.Rgba8 : PixelFormat.Rgb8,
                    16 => alpha ? PixelFormat.Rgba16 : PixelFormat.Rgb16,
                    _ => alpha ? PixelFormat.RgbaF32 : PixelFormat.RgbF32,
                };
        }
    }

    public void DecodeFrame(Stream source, ImageInfo info, int frameIndex, in DecodeRequest request, IRowSink sink, ImageLimits limits, CancellationToken cancellationToken)
    {
        var state = (State)info.DecoderState!;
        PsdHeader h = state.Header;
        if (request.ScaleDenominator != 1 || request.Region.X < 0 || request.Region.Y < 0 || request.Region.Right > h.Width || request.Region.Bottom > h.Height)
        {
            throw new ImageOperationException("The requested PSD region lies outside the image.");
        }

        var io = new FileIo(source);
        if (!state.HasComposite)
        {
            RenderFromLayers(source, state, request, sink, limits, cancellationToken);
            return;
        }

        PsdLimits psd = _format.Limits;
        Span<byte> code = stackalloc byte[2];
        io.ReadAt(state.DataPosition, code);
        int compression = BinaryPrimitives.ReadUInt16BigEndian(code);
        var shared = new ChannelReader.SharedData(io, state.DataPosition + 2, io.Length - state.DataPosition - 2, compression, h.Channels, h.Width, h.Height, h.Depth, h.Psb, psd, limits);
        int nColor = BandConverter.ColorChannelCount(h.Mode);
        var color = new ChannelReader[nColor];
        for (int c = 0; c < nColor; c++)
        {
            color[c] = shared.Channel(c);
        }

        ChannelReader? alpha = state.Alpha ? shared.Channel(nColor) : null;
        PixelFormat fmt = state.Format;
        SampleKind kind = fmt.Sample switch { SampleType.U16 => SampleKind.U16, SampleType.F32 => SampleKind.F32, _ => SampleKind.U8 };
        int rowBytes = color[0].RowBytes;
        int w = h.Width;
        int rows = Math.Clamp((int)(8L * 1024 * 1024 / Math.Max(1, (long)w * 16)), 1, 64);
        var conv = new BandConverter(h.Mode, h.Depth, kind, w, rows, state.Palette, ColorProfile.Srgb, h.Mode is PsdColorMode.Cmyk or PsdColorMode.Lab ? state.Icc : null);
        byte[][] colorBuf = new byte[nColor][];
        for (int c = 0; c < nColor; c++)
        {
            colorBuf[c] = new byte[rows * rowBytes];
        }

        byte[]? alphaBuf = alpha != null ? new byte[rows * rowBytes] : null;
        int pixelBytes = fmt.BytesPerPixel;
        byte[] outBuf = new byte[rows * w * pixelBytes];
        ImageRect region = request.Region;
        int regionRowBytes = region.Width * pixelBytes;
        byte[] regionBuf = region.Width == w ? outBuf : new byte[rows * regionRowBytes];

        // Rows before the region still have to be read for run-length data only if skipped bands are cheap to skip: they are not read at all.
        int first = region.Y / rows * rows;
        for (int y0 = first; y0 < region.Bottom; y0 += rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int n = Math.Min(rows, h.Height - y0);
            for (int c = 0; c < nColor; c++)
            {
                for (int y = 0; y < n; y++)
                {
                    color[c].ReadRow(y0 + y, colorBuf[c].AsSpan(y * rowBytes, rowBytes));
                }
            }

            if (alpha != null)
            {
                for (int y = 0; y < n; y++)
                {
                    alpha.ReadRow(y0 + y, alphaBuf.AsSpan(y * rowBytes, rowBytes));
                }
            }

            Planes planes = conv.Convert(colorBuf, alphaBuf, n, rowBytes);
            if (_format.Unmatte && alpha != null)
            {
                RemoveMatte(planes, kind, w * n);
            }

            WriteRaw(planes, fmt, kind, w * n, outBuf);

            int from = Math.Max(region.Y, y0);
            int to = Math.Min(region.Bottom, y0 + n);
            if (to <= from)
            {
                continue;
            }

            if (region.Width != w)
            {
                for (int y = from; y < to; y++)
                {
                    outBuf.AsSpan(((y - y0) * w + region.X) * pixelBytes, regionRowBytes).CopyTo(regionBuf.AsSpan((y - from) * regionRowBytes));
                }

                sink.WriteRows(from - region.Y, regionBuf.AsSpan(0, (to - from) * regionRowBytes), to - from);
            }
            else
            {
                sink.WriteRows(from - region.Y, outBuf.AsSpan((from - y0) * w * pixelBytes, (to - from) * w * pixelBytes), to - from);
            }
        }
    }

    // Photoshop stores the merged image of a document with transparency blended over white: c' = c * a + white * (1 - a). Undo it.
    private static void RemoveMatte(Planes p, SampleKind kind, int count)
    {
        int planes = p.Count;
        for (int c = 0; c < planes; c++)
        {
            for (int i = 0; i < count; i++)
            {
                switch (kind)
                {
                    case SampleKind.U8:
                        {
                            int a = p.U8![3][i];
                            if (a is > 0 and < 255)
                            {
                                int v = (p.U8[c][i] - 255 + a) * 255;
                                p.U8[c][i] = (byte)Math.Clamp((v + a / 2) / a, 0, 255);
                            }

                            break;
                        }

                    case SampleKind.U16:
                        {
                            long a = p.U16![3][i];
                            if (a is > 0 and < 65535)
                            {
                                long v = (p.U16[c][i] - 65535 + a) * 65535;
                                p.U16[c][i] = (ushort)Math.Clamp((v + a / 2) / a, 0, 65535);
                            }

                            break;
                        }

                    default:
                        {
                            float a = p.F32![3][i];
                            if (a is > 0f and < 1f)
                            {
                                p.F32[c][i] = (p.F32[c][i] - 1f + a) / a;
                            }

                            break;
                        }
                }
            }
        }
    }

    // Native-endian interleaved bytes for the frame format. A gray plane is repeated for RGB frames; a missing alpha plane is opaque.
    private static void WriteRaw(Planes p, PixelFormat fmt, SampleKind kind, int count, byte[] dst)
    {
        int channels = fmt.ChannelCount;
        int colorOut = fmt.ColorChannels;
        bool alphaOut = fmt.HasAlpha;
        int bps = fmt.BytesPerSample;
        int px = fmt.BytesPerPixel;
        for (int c = 0; c < channels; c++)
        {
            bool isAlpha = alphaOut && c == channels - 1;
            int plane = isAlpha ? 3 : (p.Count == 1 ? 0 : c);
            bool opaque = isAlpha && !p.HasAlpha;
            for (int i = 0; i < count; i++)
            {
                int o = i * px + c * bps;
                switch (kind)
                {
                    case SampleKind.U8:
                        dst[o] = opaque ? (byte)255 : p.U8![plane][i];
                        break;
                    case SampleKind.U16:
                        BitConverter.TryWriteBytes(dst.AsSpan(o, 2), opaque ? (ushort)65535 : p.U16![plane][i]);
                        break;
                    default:
                        BitConverter.TryWriteBytes(dst.AsSpan(o, 4), opaque ? 1f : p.F32![plane][i]);
                        break;
                }
            }
        }

        _ = colorOut;
    }

    // A file saved without a composite is rendered from its layers.
    private void RenderFromLayers(Stream source, State state, in DecodeRequest request, IRowSink sink, ImageLimits limits, CancellationToken ct)
    {
        source.Position = 0;
        using PsdFile file = PsdFile.Load(source, new PsdReadOptions { Limits = limits, PsdLimits = _format.Limits }, ct);
        Image image = file.Document.Render();
        PixelFormat fmt = state.Format;
        ImageRect region = request.Region;
        int rows = Math.Clamp((int)(4L * 1024 * 1024 / Math.Max(1, (long)region.Width * fmt.BytesPerPixel)), 1, 64);
        for (int y0 = 0; y0 < region.Height; y0 += rows)
        {
            ct.ThrowIfCancellationRequested();
            int n = Math.Min(rows, region.Height - y0);
            var rect = new ImageRect(region.X, region.Y + y0, region.Width, n);
            byte[] bytes = new byte[n * region.Width * fmt.BytesPerPixel];
            if (fmt == PixelFormat.GrayAlpha8)
            {
                var px = new GrayAlpha8[region.Width * n];
                image.CopyPixelsTo<GrayAlpha8>(px, rect, cancellationToken: ct);
                for (int i = 0; i < px.Length; i++)
                {
                    bytes[i * 2] = px[i].V;
                    bytes[i * 2 + 1] = px[i].A;
                }
            }
            else if (fmt == PixelFormat.Rgba16)
            {
                var px = new Rgba64[region.Width * n];
                image.CopyPixelsTo<Rgba64>(px, rect, cancellationToken: ct);
                for (int i = 0; i < px.Length; i++)
                {
                    BitConverter.TryWriteBytes(bytes.AsSpan(i * 8, 2), px[i].R);
                    BitConverter.TryWriteBytes(bytes.AsSpan(i * 8 + 2, 2), px[i].G);
                    BitConverter.TryWriteBytes(bytes.AsSpan(i * 8 + 4, 2), px[i].B);
                    BitConverter.TryWriteBytes(bytes.AsSpan(i * 8 + 6, 2), px[i].A);
                }
            }
            else if (fmt == PixelFormat.RgbaF32)
            {
                var px = new RgbaFloat[region.Width * n];
                image.CopyPixelsTo<RgbaFloat>(px, rect, cancellationToken: ct);
                for (int i = 0; i < px.Length; i++)
                {
                    BitConverter.TryWriteBytes(bytes.AsSpan(i * 16, 4), px[i].R);
                    BitConverter.TryWriteBytes(bytes.AsSpan(i * 16 + 4, 4), px[i].G);
                    BitConverter.TryWriteBytes(bytes.AsSpan(i * 16 + 8, 4), px[i].B);
                    BitConverter.TryWriteBytes(bytes.AsSpan(i * 16 + 12, 4), px[i].A);
                }
            }
            else
            {
                var px = new Rgba32[region.Width * n];
                image.CopyPixelsTo<Rgba32>(px, rect, cancellationToken: ct);
                for (int i = 0; i < px.Length; i++)
                {
                    bytes[i * 4] = px[i].R;
                    bytes[i * 4 + 1] = px[i].G;
                    bytes[i * 4 + 2] = px[i].B;
                    bytes[i * 4 + 3] = px[i].A;
                }
            }

            sink.WriteRows(y0, bytes, n);
        }
    }
}
