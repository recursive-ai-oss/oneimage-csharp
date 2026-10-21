using System.Buffers;
using System.Diagnostics.CodeAnalysis;

namespace OneImage;

/// <summary>Settings for loading and identifying images.</summary>
public sealed class LoadOptions
{
    /// <summary>Gets the configuration (formats, default limits); <see cref="ImageConfiguration.Default"/> when null.</summary>
    public ImageConfiguration? Configuration { get; init; }

    /// <summary>Gets the limits for this call; the configuration's limits when null. <see cref="ImageLimits.Unrestricted"/> opts out.</summary>
    public ImageLimits? Limits { get; init; }

    /// <summary>
    /// Gets a value indicating whether to decode every frame once at load so corrupt data fails up front instead of at the first
    /// pixel pull. Costs a full decode; decoded sequential frames are kept, so the work is not repeated.
    /// </summary>
    public bool Validate { get; init; }

    /// <summary>Gets the most threads validation may use; 0 means the default.</summary>
    public int MaxDegree { get; init; }
}

/// <summary>Settings for saving images.</summary>
public sealed class SaveOptions
{
    /// <summary>Gets the format to write; required for streams and buffer writers, otherwise inferred from the file extension.</summary>
    public IImageFormat? Format { get; init; }

    /// <summary>Gets the configuration used to look up the format by extension; <see cref="ImageConfiguration.Default"/> when null.</summary>
    public ImageConfiguration? Configuration { get; init; }

    /// <summary>Gets the limits handed to the encoder; the configuration's limits when null.</summary>
    public ImageLimits? Limits { get; init; }

    /// <summary>Gets codec-specific settings (quality, compression level, ...), or <see langword="null"/> for the codec's defaults.</summary>
    public ImageEncoderOptions? EncoderOptions { get; init; }

    /// <summary>Gets the most threads rendering may use; 0 means <see cref="ImageEngine.DefaultCallDegree"/>. Output bytes do not depend on it.</summary>
    public int MaxDegree { get; init; }
}

public sealed partial class Image
{
    // ---- Loading ----

    /// <summary>
    /// Opens an image file. Only the headers are read now; the file is reopened when pixels are pulled, and if its size or
    /// modification time changed by then, <see cref="ImageSourceChangedException"/> is thrown.
    /// </summary>
    /// <param name="path">The file.</param>
    /// <param name="options">Optional settings.</param>
    /// <param name="cancellationToken">Cancels validation.</param>
    /// <returns>The lazy image.</returns>
    /// <exception cref="ImageFormatException">Unrecognized or malformed data.</exception>
    /// <exception cref="ImageLimitException">A limit is exceeded.</exception>
    /// <exception cref="IOException">The file cannot be read.</exception>
    public static Image Load(string path, LoadOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(path);
        return LoadFrom(new FileImageSource(path), options, cancellationToken);
    }

    /// <summary>Opens an image from a stream. The remaining bytes are copied into pooled memory now; the caller keeps the stream and may close it.</summary>
    /// <param name="stream">A readable stream positioned at the image.</param>
    /// <param name="options">Optional settings.</param>
    /// <param name="cancellationToken">Cancels validation.</param>
    /// <returns>The lazy image.</returns>
    /// <exception cref="ImageFormatException">Unrecognized or malformed data.</exception>
    /// <exception cref="ImageLimitException">A limit is exceeded.</exception>
    public static Image Load(Stream stream, LoadOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        PooledBytes bytes = PooledBytes.CopyFrom(stream);
        return LoadFrom(new MemoryImageSource(bytes.Memory, bytes), options, cancellationToken);
    }

    /// <summary>Opens an image from memory without copying; the caller must not modify the memory while the image (or anything derived from it) is in use.</summary>
    /// <param name="data">The encoded bytes.</param>
    /// <param name="options">Optional settings.</param>
    /// <param name="cancellationToken">Cancels validation.</param>
    /// <returns>The lazy image.</returns>
    /// <exception cref="ImageFormatException">Unrecognized or malformed data.</exception>
    /// <exception cref="ImageLimitException">A limit is exceeded.</exception>
    public static Image Load(ReadOnlyMemory<byte> data, LoadOptions? options = null, CancellationToken cancellationToken = default) =>
        LoadFrom(new MemoryImageSource(data), options, cancellationToken);

    /// <summary>Like <see cref="Load(string,LoadOptions?,CancellationToken)"/> but returns false instead of throwing <see cref="ImageFormatException"/> or <see cref="ImageLimitException"/>. I/O errors still throw.</summary>
    /// <param name="path">The file.</param>
    /// <param name="image">The image on success.</param>
    /// <param name="options">Optional settings.</param>
    /// <param name="cancellationToken">Cancels validation.</param>
    /// <returns><see langword="true"/> on success.</returns>
    public static bool TryLoad(string path, [NotNullWhen(true)] out Image? image, LoadOptions? options = null, CancellationToken cancellationToken = default)
    {
        try
        {
            image = Load(path, options, cancellationToken);
            return true;
        }
        catch (Exception ex) when (ex is ImageFormatException or ImageLimitException)
        {
            image = null;
            return false;
        }
    }

    /// <summary>Like <see cref="Load(ReadOnlyMemory{byte},LoadOptions?,CancellationToken)"/> but returns false for invalid or over-limit data.</summary>
    /// <param name="data">The encoded bytes.</param>
    /// <param name="image">The image on success.</param>
    /// <param name="options">Optional settings.</param>
    /// <param name="cancellationToken">Cancels validation.</param>
    /// <returns><see langword="true"/> on success.</returns>
    public static bool TryLoad(ReadOnlyMemory<byte> data, [NotNullWhen(true)] out Image? image, LoadOptions? options = null, CancellationToken cancellationToken = default)
    {
        try
        {
            image = Load(data, options, cancellationToken);
            return true;
        }
        catch (Exception ex) when (ex is ImageFormatException or ImageLimitException)
        {
            image = null;
            return false;
        }
    }

    /// <summary>Asynchronously opens an image file (header parsing runs on the thread pool).</summary>
    /// <param name="path">The file.</param>
    /// <param name="options">Optional settings.</param>
    /// <param name="cancellationToken">Cancels the load.</param>
    /// <returns>The lazy image.</returns>
    public static Task<Image> LoadAsync(string path, LoadOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(path);
        return Task.Run(() => Load(path, options, cancellationToken), cancellationToken);
    }

    /// <summary>Asynchronously reads a stream into pooled memory, then opens the image.</summary>
    /// <param name="stream">A readable stream positioned at the image.</param>
    /// <param name="options">Optional settings.</param>
    /// <param name="cancellationToken">Cancels the load.</param>
    /// <returns>The lazy image.</returns>
    public static async Task<Image> LoadAsync(Stream stream, LoadOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        PooledBytes bytes = await PooledBytes.CopyFromAsync(stream, cancellationToken).ConfigureAwait(false);
        return LoadFrom(new MemoryImageSource(bytes.Memory, bytes), options, cancellationToken);
    }

    /// <summary>Reads the headers of an image file without decoding pixels.</summary>
    /// <param name="path">The file.</param>
    /// <param name="options">Optional settings (<see cref="LoadOptions.Validate"/> is ignored).</param>
    /// <returns>Format, frames, metadata.</returns>
    /// <exception cref="ImageFormatException">Unrecognized or malformed data.</exception>
    /// <exception cref="ImageLimitException">A limit is exceeded.</exception>
    public static ImageInfo Identify(string path, LoadOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        using Stream s = new FileImageSource(path).Open();
        return Probe(s, options).Info;
    }

    /// <summary>Reads the headers of an image in a stream. A seekable stream at position 0 is read in place (and rewound); otherwise the remaining bytes are copied first.</summary>
    /// <param name="stream">The stream.</param>
    /// <param name="options">Optional settings.</param>
    /// <returns>Format, frames, metadata.</returns>
    public static ImageInfo Identify(Stream stream, LoadOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (stream.CanSeek && stream.Position == 0)
        {
            try
            {
                return Probe(stream, options).Info;
            }
            finally
            {
                stream.Position = 0;
            }
        }

        PooledBytes bytes = PooledBytes.CopyFrom(stream);
        using var ms = new ReadOnlyMemoryStream(bytes.Memory);
        ImageInfo info = Probe(ms, options).Info;
        GC.KeepAlive(bytes);
        return info;
    }

    /// <summary>Reads the headers of an image in memory.</summary>
    /// <param name="data">The encoded bytes.</param>
    /// <param name="options">Optional settings.</param>
    /// <returns>Format, frames, metadata.</returns>
    public static ImageInfo Identify(ReadOnlyMemory<byte> data, LoadOptions? options = null)
    {
        using var ms = new ReadOnlyMemoryStream(data);
        return Probe(ms, options).Info;
    }

    /// <summary>Like <see cref="Identify(ReadOnlyMemory{byte},LoadOptions?)"/> but returns false for unrecognized, invalid or over-limit data.</summary>
    /// <param name="data">The encoded bytes.</param>
    /// <param name="info">The description on success.</param>
    /// <param name="options">Optional settings.</param>
    /// <returns><see langword="true"/> on success.</returns>
    public static bool TryIdentify(ReadOnlyMemory<byte> data, [NotNullWhen(true)] out ImageInfo? info, LoadOptions? options = null)
    {
        try
        {
            info = Identify(data, options);
            return true;
        }
        catch (Exception ex) when (ex is ImageFormatException or ImageLimitException)
        {
            info = null;
            return false;
        }
    }

    /// <summary>Like <see cref="Identify(string,LoadOptions?)"/> but returns false for unrecognized, invalid or over-limit data. I/O errors still throw.</summary>
    /// <param name="path">The file.</param>
    /// <param name="info">The description on success.</param>
    /// <param name="options">Optional settings.</param>
    /// <returns><see langword="true"/> on success.</returns>
    public static bool TryIdentify(string path, [NotNullWhen(true)] out ImageInfo? info, LoadOptions? options = null)
    {
        try
        {
            info = Identify(path, options);
            return true;
        }
        catch (Exception ex) when (ex is ImageFormatException or ImageLimitException)
        {
            info = null;
            return false;
        }
    }

    /// <summary>Asynchronously reads the headers of a stream.</summary>
    /// <param name="stream">The stream.</param>
    /// <param name="options">Optional settings.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>Format, frames, metadata.</returns>
    public static async Task<ImageInfo> IdentifyAsync(Stream stream, LoadOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        PooledBytes bytes = await PooledBytes.CopyFromAsync(stream, cancellationToken).ConfigureAwait(false);
        ImageInfo info = Identify(bytes.Memory, options);
        GC.KeepAlive(bytes);
        return info;
    }

    /// <summary>Asynchronously reads the headers of a file.</summary>
    /// <param name="path">The file.</param>
    /// <param name="options">Optional settings.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>Format, frames, metadata.</returns>
    public static Task<ImageInfo> IdentifyAsync(string path, LoadOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(path);
        return Task.Run(() => Identify(path, options), cancellationToken);
    }

    // ---- Saving ----

    /// <summary>
    /// Renders and writes the image to a file; the format comes from <see cref="SaveOptions.Format"/> or the extension.
    /// Frames are converted to a pixel format the encoder supports; encoders that cannot write animation get frame 0 only.
    /// A partly written file is deleted if saving fails. Source-data errors surface here for lazy images.
    /// </summary>
    /// <param name="path">The destination file (overwritten).</param>
    /// <param name="options">Optional settings.</param>
    /// <param name="cancellationToken">Cancels rendering and encoding.</param>
    /// <exception cref="ImageOperationException">No format could be determined, or the format has no encoder.</exception>
    public void Save(string path, SaveOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(path);
        IImageFormat format = ResolveFormat(path, options);
        bool ok = false;
        try
        {
            using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                SaveCore(fs, format, options, cancellationToken);
            }

            ok = true;
        }
        finally
        {
            if (!ok)
            {
                DeleteQuietly(path);
            }
        }
    }

    /// <summary>Renders and writes the image to a stream (which is left open).</summary>
    /// <param name="stream">The destination.</param>
    /// <param name="format">The format to write.</param>
    /// <param name="options">Optional settings.</param>
    /// <param name="cancellationToken">Cancels rendering and encoding.</param>
    /// <exception cref="ImageOperationException">The format has no encoder.</exception>
    public void Save(Stream stream, IImageFormat format, SaveOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(format);
        SaveCore(stream, format, options, cancellationToken);
    }

    /// <summary>Renders and writes the image into a buffer writer.</summary>
    /// <param name="writer">The destination.</param>
    /// <param name="format">The format to write.</param>
    /// <param name="options">Optional settings.</param>
    /// <param name="cancellationToken">Cancels rendering and encoding.</param>
    public void Save(IBufferWriter<byte> writer, IImageFormat format, SaveOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(format);
        using var s = new BufferWriterStream(writer);
        SaveCore(s, format, options, cancellationToken);
    }

    /// <summary>Asynchronous <see cref="Save(string,SaveOptions?,CancellationToken)"/>: encodes on the thread pool into memory, then writes the file asynchronously.</summary>
    /// <param name="path">The destination file.</param>
    /// <param name="options">Optional settings.</param>
    /// <param name="cancellationToken">Cancels the save.</param>
    /// <returns>A task completing when the file is written.</returns>
    public async Task SaveAsync(string path, SaveOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(path);
        IImageFormat format = ResolveFormat(path, options);
        using var ms = new MemoryStream();
        await Task.Run(() => SaveCore(ms, format, options, cancellationToken), cancellationToken).ConfigureAwait(false);
        ms.Position = 0;
        bool ok = false;
        try
        {
            var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous);
            await using (fs.ConfigureAwait(false))
            {
                await ms.CopyToAsync(fs, cancellationToken).ConfigureAwait(false);
            }

            ok = true;
        }
        finally
        {
            if (!ok)
            {
                DeleteQuietly(path);
            }
        }
    }

    /// <summary>Asynchronous save to a stream: encodes on the thread pool into memory, then copies asynchronously.</summary>
    /// <param name="stream">The destination (left open).</param>
    /// <param name="format">The format to write.</param>
    /// <param name="options">Optional settings.</param>
    /// <param name="cancellationToken">Cancels the save.</param>
    /// <returns>A task completing when everything is written.</returns>
    public async Task SaveAsync(Stream stream, IImageFormat format, SaveOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(format);
        using var ms = new MemoryStream();
        await Task.Run(() => SaveCore(ms, format, options, cancellationToken), cancellationToken).ConfigureAwait(false);
        ms.Position = 0;
        await ms.CopyToAsync(stream, cancellationToken).ConfigureAwait(false);
    }

    // ---- Internals ----

    private static Image LoadFrom(ImageSource source, LoadOptions? options, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        (IImageDecoder decoder, ImageInfo info, ImageLimits limits) probe;
        using (Stream s = source.Open())
        {
            probe = Probe(s, options);
        }

        var session = new DecodeSession(source, probe.decoder, probe.info, probe.limits);
        int n = probe.info.Frames.Count;
        var nodes = new Node[n];
        var delays = new TimeSpan[n];
        for (int i = 0; i < n; i++)
        {
            nodes[i] = new DecodeSourceNode(session, i);
            delays[i] = probe.info.Frames[i].Delay;
        }

        if (options?.Validate == true)
        {
            // Decode each frame once now. Sequential frames keep their tiles, so the work is not repeated on first pull.
            foreach (Node node in nodes)
            {
                ((DecodeSourceNode)node).Validate(cancellationToken);
            }
        }

        return new Image(nodes, delays, probe.info.LoopCount, probe.info.Metadata, probe.info.Format);
    }

    private static (IImageDecoder Decoder, ImageInfo Info, ImageLimits Limits) Probe(Stream stream, LoadOptions? options)
    {
        ImageConfiguration config = options?.Configuration ?? ImageConfiguration.Default;
        ImageLimits limits = options?.Limits ?? config.Limits;
        byte[] header = new byte[config.HeaderLength];
        int got = 0;
        while (got < header.Length)
        {
            int n = stream.Read(header, got, header.Length - got);
            if (n == 0)
            {
                break;
            }

            got += n;
        }

        IImageFormat format = config.Detect(header.AsSpan(0, got)) ?? throw new ImageFormatException("The data is not in a recognized image format.");
        IImageDecoder decoder = format.Decoder ?? throw new ImageOperationException($"Format '{format.Name}' has no decoder.");
        stream.Position = 0;
        ImageInfo info = decoder.ReadInfo(stream, limits);

        // Re-check what the decoder reported even if it already enforced the limits: this is the allocation gate.
        limits.Check(info);
        for (int i = 0; i < info.Frames.Count; i++)
        {
            if (info.Frames[i].Format.BytesPerPixel <= 0)
            {
                throw new ImageFormatException("Frame has an invalid pixel format.");
            }
        }

        return (decoder, info, limits);
    }

    private static IImageFormat ResolveFormat(string path, SaveOptions? options)
    {
        if (options?.Format is { } f)
        {
            return f;
        }

        ImageConfiguration config = options?.Configuration ?? ImageConfiguration.Default;
        return config.FindByExtension(path) ?? throw new ImageOperationException($"Cannot infer an image format from '{Path.GetFileName(path)}'; set SaveOptions.Format.");
    }

    private void SaveCore(Stream destination, IImageFormat format, SaveOptions? options, CancellationToken cancellationToken)
    {
        IImageEncoder encoder = format.Encoder ?? throw new ImageOperationException($"Format '{format.Name}' has no encoder.");
        ImageConfiguration config = options?.Configuration ?? ImageConfiguration.Default;
        ImageLimits limits = options?.Limits ?? config.Limits;
        if (encoder.SupportedFormats.Count == 0)
        {
            throw new ImageOperationException($"Encoder for '{format.Name}' declares no pixel formats.");
        }

        RenderContext context = RenderContext.Create(options?.MaxDegree ?? 0, cancellationToken);
        int count = encoder.SupportsMultipleFrames ? _frames.Length : 1;
        var readers = new ImageFrameReader[count];
        for (int i = 0; i < count; i++)
        {
            readers[i] = ImageFrameReader.Create(_frames[i], ChooseFormat(_frames[i].Format, encoder.SupportedFormats), context);
        }

        encoder.Encode(new ImageEncodeRequest(readers, LoopCount, Metadata, options?.EncoderOptions, limits), destination, cancellationToken);
    }

    private static PixelFormat ChooseFormat(PixelFormat frame, IReadOnlyList<PixelFormat> supported)
    {
        foreach (PixelFormat s in supported)
        {
            if (s == frame)
            {
                return s;
            }
        }

        foreach (PixelFormat s in supported)
        {
            if (s.Model == frame.Model && s.Sample == frame.Sample)
            {
                return s;
            }
        }

        foreach (PixelFormat s in supported)
        {
            if (s.Model == frame.Model)
            {
                return s;
            }
        }

        return supported[0];
    }

    private static void DeleteQuietly(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // The original failure is the interesting one.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
