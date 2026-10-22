using Microsoft.AspNetCore.Http;

namespace OneImage.Web;

/// <summary>Finds the source image for a request. Implement this to serve images from a database, blob storage or anywhere else.</summary>
public interface IImageProvider
{
    /// <summary>Gets whether images of this provider are processed only when a request carries commands, or always.</summary>
    ProcessingBehavior ProcessingBehavior { get; }

    /// <summary>Returns whether this provider may serve the request, judged from the path alone and without I/O (for instance a path prefix). Matching providers are asked in registration order until one returns a source.</summary>
    /// <param name="context">The request.</param>
    /// <returns><see langword="true"/> to look for the image here.</returns>
    bool Match(HttpContext context);

    /// <summary>Looks up the source image of the request.</summary>
    /// <param name="context">The request.</param>
    /// <param name="cancellationToken">Cancels the lookup.</param>
    /// <returns>The source, or <see langword="null"/> if it does not exist, in which case the request continues down the pipeline (normally to a 404).</returns>
    ValueTask<IImageSource?> GetAsync(HttpContext context, CancellationToken cancellationToken);
}

/// <summary>One source image: what the middleware needs to decide whether a cached result is still valid, and to read the bytes.</summary>
public interface IImageSource
{
    /// <summary>Gets the time the source last changed. A cached result made from an earlier or different state is discarded; it is also the <c>Last-Modified</c> of the response.</summary>
    DateTimeOffset LastModified { get; }

    /// <summary>Gets the size of the source in bytes, or -1 if unknown. Together with <see cref="LastModified"/> it identifies the source version.</summary>
    long Length { get; }

    /// <summary>Opens the source for reading. The caller disposes the stream.</summary>
    /// <param name="cancellationToken">Cancels the open.</param>
    /// <returns>A readable stream over the encoded image.</returns>
    ValueTask<Stream> OpenReadAsync(CancellationToken cancellationToken);
}

/// <summary>Serves images from any <see cref="Microsoft.Extensions.FileProviders.IFileProvider"/>.</summary>
public class FileProviderImageProvider : IImageProvider
{
    private readonly Microsoft.Extensions.FileProviders.IFileProvider _files;
    private readonly PathString _prefix;

    /// <summary>Initializes the provider.</summary>
    /// <param name="files">Where the files are.</param>
    /// <param name="processingBehavior">Whether to process only on commands (the default) or always.</param>
    /// <param name="requestPathPrefix">The URL path prefix that selects this provider and is removed before looking up the file, for instance <c>/img</c>. Empty (the default) matches every path.</param>
    public FileProviderImageProvider(Microsoft.Extensions.FileProviders.IFileProvider files, ProcessingBehavior processingBehavior = ProcessingBehavior.CommandOnly, PathString requestPathPrefix = default)
    {
        ArgumentNullException.ThrowIfNull(files);
        _files = files;
        ProcessingBehavior = processingBehavior;
        _prefix = requestPathPrefix;
    }

    /// <inheritdoc />
    public ProcessingBehavior ProcessingBehavior { get; }

    /// <inheritdoc />
    public bool Match(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return !_prefix.HasValue || context.Request.Path.StartsWithSegments(_prefix);
    }

    /// <inheritdoc />
    public ValueTask<IImageSource?> GetAsync(HttpContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        PathString path = context.Request.Path;
        if (_prefix.HasValue)
        {
            if (!path.StartsWithSegments(_prefix, out PathString rest))
            {
                return ValueTask.FromResult<IImageSource?>(null);
            }

            path = rest;
        }

        string sub = path.Value ?? string.Empty;
        if (sub.Contains('\0', StringComparison.Ordinal) || sub.Contains("..", StringComparison.Ordinal))
        {
            return ValueTask.FromResult<IImageSource?>(null);
        }

        Microsoft.Extensions.FileProviders.IFileInfo info = _files.GetFileInfo(sub);
        IImageSource? source = info.Exists && !info.IsDirectory ? new FileInfoSource(info) : null;
        return ValueTask.FromResult(source);
    }

    private sealed class FileInfoSource : IImageSource
    {
        private readonly Microsoft.Extensions.FileProviders.IFileInfo _info;

        public FileInfoSource(Microsoft.Extensions.FileProviders.IFileInfo info) => _info = info;

        public DateTimeOffset LastModified => _info.LastModified;

        public long Length => _info.Length;

        public ValueTask<Stream> OpenReadAsync(CancellationToken cancellationToken) => ValueTask.FromResult(_info.CreateReadStream());
    }
}

/// <summary>Serves images from the application's web root (<c>wwwroot</c>). This is what <c>AddOneImage()</c> registers by default.</summary>
public sealed class WebRootImageProvider : FileProviderImageProvider
{
    /// <summary>Initializes the provider.</summary>
    /// <param name="environment">The hosting environment whose web root is served.</param>
    public WebRootImageProvider(Microsoft.AspNetCore.Hosting.IWebHostEnvironment environment)
        : base((environment ?? throw new ArgumentNullException(nameof(environment))).WebRootFileProvider)
    {
    }
}

/// <summary>Serves images from a folder on disk. Paths that leave the folder, hidden files and dot-files are never served.</summary>
public sealed class PhysicalFileImageProvider : FileProviderImageProvider, IDisposable
{
    private readonly Microsoft.Extensions.FileProviders.PhysicalFileProvider _physical;

    /// <summary>Initializes the provider.</summary>
    /// <param name="rootDirectory">The folder that holds the images.</param>
    /// <param name="processingBehavior">Whether to process only on commands (the default) or always.</param>
    /// <param name="requestPathPrefix">The URL path prefix that selects this provider, for instance <c>/photos</c>.</param>
    public PhysicalFileImageProvider(string rootDirectory, ProcessingBehavior processingBehavior = ProcessingBehavior.CommandOnly, PathString requestPathPrefix = default)
        : this(new Microsoft.Extensions.FileProviders.PhysicalFileProvider(rootDirectory), processingBehavior, requestPathPrefix)
    {
    }

    private PhysicalFileImageProvider(Microsoft.Extensions.FileProviders.PhysicalFileProvider physical, ProcessingBehavior processingBehavior, PathString requestPathPrefix)
        : base(physical, processingBehavior, requestPathPrefix)
    {
        _physical = physical;
    }

    /// <summary>Releases the file provider.</summary>
    public void Dispose() => _physical.Dispose();
}
