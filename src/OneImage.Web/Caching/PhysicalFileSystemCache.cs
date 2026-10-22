using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Options;

namespace OneImage.Web;

/// <summary>Settings of <see cref="PhysicalFileSystemCache"/>.</summary>
public sealed class PhysicalFileSystemCacheOptions
{
    /// <summary>
    /// Gets or sets the cache folder. Defaults to <c>oneimage-cache</c> inside the application's content root, which is not the web root,
    /// so cached files are never served as static files by accident. Relative paths are relative to the content root.
    /// </summary>
    public string? CacheRootPath { get; set; }

    /// <summary>Gets or sets how many levels of sub-folders the files are spread over (0 to 8). Defaults to 2, which keeps folders small for millions of entries.</summary>
    public int CacheFolderDepth { get; set; } = 2;

    /// <summary>Gets or sets how many hash characters name each sub-folder level (1 to 8). Defaults to 2 (256 folders per level).</summary>
    public int CacheFolderNameLength { get; set; } = 2;

    /// <summary>Gets or sets how many entry records are remembered in memory so that a cache hit does not read the record file. Defaults to 8192. When it is full the memory is cleared and refills from disk.</summary>
    public int MaxIndexedEntries { get; set; } = 8192;
}

/// <summary>
/// Stores processed images as files: <c>root/ab/cd/&lt;hash&gt;.img</c> with a small <c>.meta</c> text record beside each.
/// Files are written under a temporary name and renamed, so readers never see a partial file, and several processes can share one folder.
/// The cache never deletes entries (stale ones are replaced when requested again); clear the folder to reset it.
/// </summary>
public sealed class PhysicalFileSystemCache : IImageCache
{
    private const string Magic = "oneimage-cache 1";
    private readonly string _root;
    private readonly int _depth;
    private readonly int _folderLength;
    private readonly int _nameLength;
    private readonly int _maxIndexed;
    private readonly ConcurrentDictionary<string, CachedImageInfo> _index = new(StringComparer.Ordinal);

    /// <summary>Initializes the cache from dependency injection.</summary>
    /// <param name="environment">Supplies the content root for the default folder.</param>
    /// <param name="cacheOptions">The cache settings.</param>
    /// <param name="options">The middleware settings (for the file-name length).</param>
    public PhysicalFileSystemCache(IWebHostEnvironment environment, IOptions<PhysicalFileSystemCacheOptions> cacheOptions, IOptions<OneImageOptions> options)
        : this(Resolve(environment, cacheOptions), cacheOptions?.Value, options?.Value.CacheHashLength ?? 32)
    {
    }

    /// <summary>Initializes the cache over a folder.</summary>
    /// <param name="root">The cache folder; created on first write.</param>
    /// <param name="cacheOptions">The layout settings, or <see langword="null"/> for the defaults.</param>
    /// <param name="hashLength">The number of hash characters in a file name (12 to 64).</param>
    public PhysicalFileSystemCache(string root, PhysicalFileSystemCacheOptions? cacheOptions = null, int hashLength = 32)
    {
        ArgumentException.ThrowIfNullOrEmpty(root);
        cacheOptions ??= new PhysicalFileSystemCacheOptions();
        ArgumentOutOfRangeException.ThrowIfLessThan(hashLength, 12);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(hashLength, 64);
        if (cacheOptions.CacheFolderDepth is < 0 or > 8 || cacheOptions.CacheFolderNameLength is < 1 or > 8
            || cacheOptions.CacheFolderDepth * cacheOptions.CacheFolderNameLength > hashLength)
        {
            throw new ArgumentException("The folder layout does not fit in the hash length.", nameof(cacheOptions));
        }

        _root = Path.GetFullPath(root);
        _depth = cacheOptions.CacheFolderDepth;
        _folderLength = cacheOptions.CacheFolderNameLength;
        _nameLength = hashLength;
        _maxIndexed = Math.Max(1, cacheOptions.MaxIndexedEntries);
    }

    /// <summary>Gets the absolute cache folder.</summary>
    public string RootPath => _root;

    /// <inheritdoc />
    public async ValueTask<CachedImage?> GetAsync(string hash, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(hash);
        string basePath = BasePath(hash);
        if (!_index.TryGetValue(hash, out CachedImageInfo? info))
        {
            try
            {
                string text = await File.ReadAllTextAsync(basePath + ".meta", cancellationToken).ConfigureAwait(false);
                info = Parse(text);
            }
            catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
            {
                return null;
            }

            if (info is null || !string.Equals(info.FullHash, hash, StringComparison.Ordinal))
            {
                return null;
            }

            Remember(hash, info);
        }

        string dataPath = basePath + ".img";
        return new CachedImage(info, () => new FileStream(dataPath, new FileStreamOptions
        {
            Mode = FileMode.Open,
            Access = FileAccess.Read,
            Share = FileShare.ReadWrite | FileShare.Delete,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
            BufferSize = 4096,
        }));
    }

    /// <inheritdoc />
    public async ValueTask SetAsync(string hash, ReadOnlyMemory<byte> data, CachedImageInfo info, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(hash);
        ArgumentNullException.ThrowIfNull(info);
        string basePath = BasePath(hash);
        Directory.CreateDirectory(Path.GetDirectoryName(basePath)!);
        string tag = Guid.NewGuid().ToString("N");
        string tempData = basePath + "." + tag + ".tmp";
        string tempMeta = basePath + "." + tag + ".mtmp";
        try
        {
            await File.WriteAllBytesAsync(tempData, data, cancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(tempMeta, Format(info), cancellationToken).ConfigureAwait(false);
            File.Move(tempData, basePath + ".img", overwrite: true);
            File.Move(tempMeta, basePath + ".meta", overwrite: true); // The record is written last: it is what makes the entry visible.
        }
        finally
        {
            TryDelete(tempData);
            TryDelete(tempMeta);
        }

        Remember(hash, info);
    }

    private static string Resolve(IWebHostEnvironment environment, IOptions<PhysicalFileSystemCacheOptions> cacheOptions)
    {
        ArgumentNullException.ThrowIfNull(environment);
        string? configured = cacheOptions?.Value.CacheRootPath;
        string contentRoot = environment.ContentRootPath ?? Directory.GetCurrentDirectory();
        return string.IsNullOrEmpty(configured) ? Path.Combine(contentRoot, "oneimage-cache") : Path.Combine(contentRoot, configured);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // A leftover temporary file is harmless.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private void Remember(string hash, CachedImageInfo info)
    {
        if (_index.Count >= _maxIndexed)
        {
            _index.Clear();
        }

        _index[hash] = info;
    }

    private string BasePath(string hash)
    {
        string name = hash.Length > _nameLength ? hash[.._nameLength] : hash;
        string dir = _root;
        for (int i = 0; i < _depth; i++)
        {
            dir = Path.Combine(dir, name.Substring(i * _folderLength, _folderLength));
        }

        return Path.Combine(dir, name);
    }

    private static string Format(CachedImageInfo i)
    {
        CultureInfo c = CultureInfo.InvariantCulture;
        return string.Join('\n', Magic, i.FullHash, i.ContentType, i.ContentLength.ToString(c), i.SourceLastModified.UtcTicks.ToString(c), i.SourceLength.ToString(c), i.CreatedUtc.UtcTicks.ToString(c));
    }

    private static CachedImageInfo? Parse(string text)
    {
        string[] l = text.Split('\n');
        CultureInfo c = CultureInfo.InvariantCulture;
        if (l.Length < 7 || l[0] != Magic
            || !long.TryParse(l[3], NumberStyles.None, c, out long length)
            || !long.TryParse(l[4], NumberStyles.None, c, out long srcTicks)
            || !long.TryParse(l[5], NumberStyles.AllowLeadingSign, c, out long srcLength)
            || !long.TryParse(l[6], NumberStyles.None, c, out long createdTicks)
            || srcTicks > DateTime.MaxValue.Ticks || createdTicks > DateTime.MaxValue.Ticks)
        {
            return null;
        }

        return new CachedImageInfo(l[1], l[2], length, new DateTimeOffset(srcTicks, TimeSpan.Zero), srcLength, new DateTimeOffset(createdTicks, TimeSpan.Zero));
    }
}
