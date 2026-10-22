using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;

namespace OneImage.Web;

/// <summary>Builds the cache key of a request: a string that identifies the source and the processing. Equal keys must mean equal results.</summary>
public interface ICacheKey
{
    /// <summary>Creates the key.</summary>
    /// <param name="context">The request.</param>
    /// <param name="commands">The canonical text of the normalized commands (<see cref="ProcessingCommands.ToCanonicalString"/>), already including <see cref="OneImageOptions.CacheSalt"/>.</param>
    /// <returns>The key.</returns>
    string Create(HttpContext context, string commands);
}

/// <summary>Turns a cache key into a short, file-name-safe identifier.</summary>
public interface ICacheHash
{
    /// <summary>Hashes the key.</summary>
    /// <param name="key">The cache key.</param>
    /// <returns>The complete lower-case hexadecimal hash. The cache uses a prefix of it as the file name and stores the rest to detect collisions.</returns>
    string Create(string key);
}

/// <summary>The default key: the request path (without host) and the commands. Case-sensitive, like the file systems it usually fronts on Linux.</summary>
public sealed class PathCacheKey : ICacheKey
{
    /// <inheritdoc />
    public string Create(HttpContext context, string commands)
    {
        ArgumentNullException.ThrowIfNull(context);
        return string.Concat(context.Request.Path.Value, "\n", commands);
    }
}

/// <summary>A key that also contains the host name, for sites that serve several domains from one cache.</summary>
public sealed class HostPathCacheKey : ICacheKey
{
    /// <inheritdoc />
    public string Create(HttpContext context, string commands)
    {
        ArgumentNullException.ThrowIfNull(context);
        return string.Concat((context.Request.Host.Value ?? string.Empty).ToLowerInvariant(), "\n", context.Request.Path.Value, "\n", commands);
    }
}

/// <summary>The default hash: SHA-256 of the key's UTF-8 bytes, as 64 lower-case hex characters.</summary>
public sealed class Sha256CacheHash : ICacheHash
{
    /// <inheritdoc />
    public string Create(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
    }
}

/// <summary>What the cache records about a stored result.</summary>
public sealed class CachedImageInfo
{
    /// <summary>Initializes the record.</summary>
    /// <param name="fullHash">The complete key hash.</param>
    /// <param name="contentType">The MIME type of the stored bytes.</param>
    /// <param name="contentLength">The size of the stored bytes.</param>
    /// <param name="sourceLastModified">The source's <see cref="IImageSource.LastModified"/> when it was processed.</param>
    /// <param name="sourceLength">The source's <see cref="IImageSource.Length"/> when it was processed.</param>
    /// <param name="createdUtc">When the result was stored.</param>
    public CachedImageInfo(string fullHash, string contentType, long contentLength, DateTimeOffset sourceLastModified, long sourceLength, DateTimeOffset createdUtc)
    {
        FullHash = fullHash;
        ContentType = contentType;
        ContentLength = contentLength;
        SourceLastModified = sourceLastModified;
        SourceLength = sourceLength;
        CreatedUtc = createdUtc;
    }

    /// <summary>Gets the complete key hash.</summary>
    public string FullHash { get; }

    /// <summary>Gets the MIME type.</summary>
    public string ContentType { get; }

    /// <summary>Gets the size in bytes.</summary>
    public long ContentLength { get; }

    /// <summary>Gets the source modification time the result was made from.</summary>
    public DateTimeOffset SourceLastModified { get; }

    /// <summary>Gets the source length the result was made from.</summary>
    public long SourceLength { get; }

    /// <summary>Gets when the result was stored.</summary>
    public DateTimeOffset CreatedUtc { get; }
}

/// <summary>A stored result: its record and a way to read the bytes.</summary>
public sealed class CachedImage
{
    private readonly Func<Stream> _open;

    /// <summary>Initializes the entry.</summary>
    /// <param name="info">The record.</param>
    /// <param name="open">Opens the stored bytes for reading; throws <see cref="FileNotFoundException"/> or <see cref="DirectoryNotFoundException"/> if they vanished.</param>
    public CachedImage(CachedImageInfo info, Func<Stream> open)
    {
        ArgumentNullException.ThrowIfNull(info);
        ArgumentNullException.ThrowIfNull(open);
        Info = info;
        _open = open;
    }

    /// <summary>Gets the record.</summary>
    public CachedImageInfo Info { get; }

    /// <summary>Opens the stored bytes. The caller disposes the stream.</summary>
    /// <returns>A readable stream.</returns>
    public Stream OpenRead() => _open();
}

/// <summary>Stores processed images. The default is <see cref="PhysicalFileSystemCache"/>.</summary>
public interface IImageCache
{
    /// <summary>Looks up a stored result.</summary>
    /// <param name="hash">The complete key hash (<see cref="ICacheHash.Create"/>). A cache that shortens it must still verify <see cref="CachedImageInfo.FullHash"/>.</param>
    /// <param name="cancellationToken">Cancels the lookup.</param>
    /// <returns>The entry, or <see langword="null"/> if there is none or it is unusable.</returns>
    ValueTask<CachedImage?> GetAsync(string hash, CancellationToken cancellationToken);

    /// <summary>Stores a result, replacing an older one. Readers must see either the old or the complete new result, never a partial one.</summary>
    /// <param name="hash">The complete key hash.</param>
    /// <param name="data">The encoded image.</param>
    /// <param name="info">The record to store with it.</param>
    /// <param name="cancellationToken">Cancels the store.</param>
    /// <returns>A task that completes when the result can be read back.</returns>
    ValueTask SetAsync(string hash, ReadOnlyMemory<byte> data, CachedImageInfo info, CancellationToken cancellationToken);
}
