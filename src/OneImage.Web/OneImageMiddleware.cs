using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Headers;
using Microsoft.Net.Http.Headers;
using Microsoft.Extensions.Logging;

namespace OneImage.Web;

/// <summary>The result of one processing run, served from memory or from a cache entry.</summary>
internal sealed class ServedImage
{
    public byte[]? Data { get; init; }

    public CachedImage? Cached { get; init; }

    public required string ContentType { get; init; }

    public required long Length { get; init; }
}

internal static partial class MiddlewareLog
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "The image cache could not store a result; it is served uncached.")]
    public static partial void CacheWriteFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "A source image was rejected: {Reason}")]
    public static partial void SourceRejected(ILogger logger, string reason);
}

/// <summary>The image middleware. Created by <c>UseOneImage</c>.</summary>
internal sealed class OneImageMiddleware : IDisposable
{
    private readonly RequestDelegate _next;
    private readonly OneImageOptions _options;
    private readonly ImageConfiguration _config;
    private readonly IImageProvider[] _providers;
    private readonly IImageCache _cache;
    private readonly ICacheKey _keys;
    private readonly ICacheHash _hash;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _gate;
    private readonly ConcurrentDictionary<string, Lazy<Task<ServedImage>>> _inFlight = new(StringComparer.Ordinal);

    public OneImageMiddleware(RequestDelegate next, OneImageOptions options, IEnumerable<IImageProvider> providers, IImageCache cache, ICacheKey keys, ICacheHash hash, ILogger logger)
    {
        options.Validate();
        _next = next;
        _options = options;
        _config = options.Configuration ?? ImageConfiguration.Default;
        _providers = [.. providers];
        _cache = cache;
        _keys = keys;
        _hash = hash;
        _logger = logger;
        _gate = new SemaphoreSlim(options.MaxConcurrentProcessing);
    }

    public void Dispose() => _gate.Dispose();

    public async Task InvokeAsync(HttpContext context)
    {
        HttpRequest request = context.Request;
        if (!HttpMethods.IsGet(request.Method) && !HttpMethods.IsHead(request.Method))
        {
            await _next(context);
            return;
        }

        string path = request.Path.Value ?? string.Empty;
        string ext = Path.GetExtension(path);
        if (ext.Length == 0 || _config.FindByExtension(ext) is not { Decoder: not null })
        {
            await _next(context);
            return;
        }

        try
        {
            if (!await HandleAsync(context))
            {
                await _next(context);
            }
        }
        catch (RequestRejectedException e)
        {
            await FailAsync(context, e.StatusCode, e.Message);
        }
        catch (ImageLimitException e)
        {
            MiddlewareLog.SourceRejected(_logger, e.Message);
            await FailAsync(context, 413, "The image exceeds the configured limits.");
        }
        catch (ImageFormatException e)
        {
            MiddlewareLog.SourceRejected(_logger, e.Message);
            await FailAsync(context, 415, "The source is not a readable image.");
        }
        catch (ImageOperationException e)
        {
            MiddlewareLog.SourceRejected(_logger, e.Message);
            await FailAsync(context, 422, "The image could not be processed.");
        }
    }

    private static async Task FailAsync(HttpContext context, int status, string message)
    {
        if (context.Response.HasStarted)
        {
            context.Abort();
            return;
        }

        context.Response.Clear();
        context.Response.StatusCode = status;
        context.Response.ContentType = "text/plain; charset=utf-8";
        await context.Response.WriteAsync(message, context.RequestAborted);
    }

    /// <returns><see langword="false"/> if the request is not ours and must continue down the pipeline.</returns>
    private async Task<bool> HandleAsync(HttpContext context)
    {
        HttpRequest request = context.Request;
        CancellationToken ct = context.RequestAborted;
        if ((request.QueryString.Value?.Length ?? 0) > _options.MaxQueryLength)
        {
            throw new RequestRejectedException(400, "The query string is too long.");
        }

        Dictionary<string, string> commands = CommandParser.ReadRequest(request, _options);
        if (commands.Count > 0 && _options.HmacSecretKey is { } key && !HmacUtilities.Verify(request, key, _options.HmacAlgorithm))
        {
            throw new RequestRejectedException(403, "The image URL is not signed correctly.");
        }

        if (_options.OnParseCommandsAsync is { } parse)
        {
            await parse(new ImageCommandContext(context, commands));
        }

        // Providers are asked in order; the first one that matches and has the file wins. Without commands only providers that
        // process everything take part, so plain requests keep falling through to static files.
        IImageSource? source = null;
        foreach (IImageProvider candidate in _providers)
        {
            if (!candidate.Match(context) || (commands.Count == 0 && candidate.ProcessingBehavior == ProcessingBehavior.CommandOnly))
            {
                continue;
            }

            source = await candidate.GetAsync(context, ct);
            if (source is not null)
            {
                break;
            }
        }

        if (source is null)
        {
            return false;
        }

        if (source.Length > _options.MaxSourceBytes)
        {
            throw new RequestRejectedException(413, "The source image is too large.");
        }

        ProcessingCommands pc = CommandParser.Normalize(commands, _options, _config);
        string canonical = _options.CacheSalt.Length == 0 ? pc.ToCanonicalString() : pc.ToCanonicalString() + ";salt=" + _options.CacheSalt;
        string hash = _hash.Create(_keys.Create(context, canonical));
        string etag = MakeETag(hash, source);
        DateTimeOffset lastModified = TruncateToSeconds(source.LastModified);

        if (IsNotModified(request, etag, lastModified))
        {
            await SendNotModifiedAsync(context, etag, lastModified);
            return true;
        }

        ServedImage? served = null;
        CachedImage? cached = await _cache.GetAsync(hash, ct);
        if (cached is not null && IsFresh(cached.Info, source))
        {
            served = TryOpen(cached);
        }

        if (served is null)
        {
            served = await ProcessSharedAsync(context, source, pc, hash, ct);
        }

        await SendAsync(context, served, etag, lastModified, ct);
        return true;
    }

    private bool IsFresh(CachedImageInfo info, IImageSource source) =>
        info.SourceLastModified.UtcTicks == source.LastModified.UtcTicks
        && info.SourceLength == source.Length
        && DateTimeOffset.UtcNow - info.CreatedUtc <= _options.CacheMaxAge;

    private static ServedImage? TryOpen(CachedImage cached)
    {
        // The file is opened when it is served; check here that it still exists with the recorded size.
        try
        {
            using Stream s = cached.OpenRead();
            return s.Length == cached.Info.ContentLength ? new ServedImage { Cached = cached, ContentType = cached.Info.ContentType, Length = cached.Info.ContentLength } : null;
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
    }

    private async Task<ServedImage> ProcessSharedAsync(HttpContext context, IImageSource source, ProcessingCommands pc, string hash, CancellationToken ct)
    {
        // The source version is part of the flight key, so a request for a newer source never joins a run on the older one.
        string flightKey = string.Concat(hash, "|", source.LastModified.UtcTicks.ToString(System.Globalization.CultureInfo.InvariantCulture), "|", source.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var mine = new Lazy<Task<ServedImage>>(() => RunAsync(context, source, pc, hash, flightKey));
        Lazy<Task<ServedImage>> flight = _inFlight.GetOrAdd(flightKey, mine);
        bool leader = ReferenceEquals(flight, mine);

        // The request that started the work stays until it ends, because the hooks use its HttpContext; the others may give up.
        return leader ? await flight.Value : await flight.Value.WaitAsync(ct);
    }

    private async Task<ServedImage> RunAsync(HttpContext context, IImageSource source, ProcessingCommands pc, string hash, string flightKey)
    {
        try
        {
            await _gate.WaitAsync(CancellationToken.None);
            try
            {
                // Another process, or a run that just finished, may have produced this result already.
                CachedImage? again = await _cache.GetAsync(hash, CancellationToken.None);
                if (again is not null && IsFresh(again.Info, source) && TryOpen(again) is { } reused)
                {
                    return reused;
                }

                byte[] bytes = await ReadSourceAsync(source);
                Image img = ImagePipeline.Build(bytes, pc, _options, _config);
                ImagePipeline.CheckOutput(img, _options);
                IImageFormat format = ImagePipeline.ChooseFormat(pc, img, _options, _config);
                if (_options.OnBeforeSaveAsync is { } beforeSave)
                {
                    var saveContext = new ImageSaveContext(context, pc, img);
                    await beforeSave(saveContext);
                    img = saveContext.Image;
                }

                byte[] data = ImagePipeline.Encode(img, format, pc, _options, _config);
                var info = new CachedImageInfo(hash, format.MimeType, data.Length, source.LastModified, source.Length, DateTimeOffset.UtcNow);
                try
                {
                    await _cache.SetAsync(hash, data, info, CancellationToken.None);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    MiddlewareLog.CacheWriteFailed(_logger, e);
                }

                if (_options.OnProcessedAsync is { } processed)
                {
                    await processed(new ImageProcessedContext(context, pc, format.MimeType, data.Length));
                }

                return new ServedImage { Data = data, ContentType = format.MimeType, Length = data.Length };
            }
            finally
            {
                _gate.Release();
            }
        }
        finally
        {
            _ = _inFlight.TryRemove(flightKey, out _);
        }
    }

    private async Task<byte[]> ReadSourceAsync(IImageSource source)
    {
        await using Stream stream = await source.OpenReadAsync(CancellationToken.None);
        long cap = _options.MaxSourceBytes;
        using var buffer = new MemoryStream(source.Length is > 0 and <= int.MaxValue ? (int)Math.Min(source.Length, cap) : 0);
        byte[] chunk = System.Buffers.ArrayPool<byte>.Shared.Rent(81920);
        try
        {
            int n;
            while ((n = await stream.ReadAsync(chunk, CancellationToken.None)) > 0)
            {
                if (buffer.Length + n > cap)
                {
                    throw new RequestRejectedException(413, "The source image is too large.");
                }

                buffer.Write(chunk, 0, n);
            }
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(chunk);
        }

        return buffer.ToArray();
    }

    private static string MakeETag(string hash, IImageSource source)
    {
        byte[] h = SHA256.HashData(Encoding.UTF8.GetBytes(string.Concat(hash, "|", source.LastModified.UtcTicks.ToString(System.Globalization.CultureInfo.InvariantCulture), "|", source.Length.ToString(System.Globalization.CultureInfo.InvariantCulture))));
        return "\"" + Convert.ToHexStringLower(h.AsSpan(0, 16)) + "\"";
    }

    private static DateTimeOffset TruncateToSeconds(DateTimeOffset t) => new(t.UtcTicks - (t.UtcTicks % TimeSpan.TicksPerSecond), TimeSpan.Zero);

    private static bool IsNotModified(HttpRequest request, string etag, DateTimeOffset lastModified)
    {
        RequestHeaders headers = request.GetTypedHeaders();
        IList<EntityTagHeaderValue> none = headers.IfNoneMatch;
        if (none.Count > 0)
        {
            var current = new EntityTagHeaderValue(etag);
            foreach (EntityTagHeaderValue candidate in none)
            {
                if (candidate.Equals(EntityTagHeaderValue.Any) || candidate.Compare(current, useStrongComparison: false))
                {
                    return true;
                }
            }

            return false;
        }

        return headers.IfModifiedSince is { } since && lastModified <= since;
    }

    private void SetCommonHeaders(HttpContext context, string etag, DateTimeOffset lastModified)
    {
        HttpResponse response = context.Response;
        ResponseHeaders typed = response.GetTypedHeaders();
        typed.ETag = new EntityTagHeaderValue(etag);
        typed.LastModified = lastModified;
        response.Headers.CacheControl = _options.BrowserMaxAge == TimeSpan.Zero
            ? "no-cache"
            : "public, max-age=" + ((long)_options.BrowserMaxAge.TotalSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture);
        response.Headers.XContentTypeOptions = "nosniff";
    }

    private async Task SendNotModifiedAsync(HttpContext context, string etag, DateTimeOffset lastModified)
    {
        context.Response.StatusCode = StatusCodes.Status304NotModified;
        SetCommonHeaders(context, etag, lastModified);
        if (_options.OnPrepareResponseAsync is { } prepare)
        {
            await prepare(context);
        }
    }

    private async Task SendAsync(HttpContext context, ServedImage served, string etag, DateTimeOffset lastModified, CancellationToken ct)
    {
        HttpResponse response = context.Response;
        Stream? file = null;
        try
        {
            if (served.Data is null)
            {
                file = served.Cached!.OpenRead();
            }

            response.StatusCode = StatusCodes.Status200OK;
            response.ContentType = served.ContentType;
            response.ContentLength = served.Length;
            SetCommonHeaders(context, etag, lastModified);
            if (_options.OnPrepareResponseAsync is { } prepare)
            {
                await prepare(context);
            }

            if (HttpMethods.IsHead(context.Request.Method))
            {
                return;
            }

            if (file is null)
            {
                await response.Body.WriteAsync(served.Data, ct);
            }
            else
            {
                await file.CopyToAsync(response.Body, ct);
            }
        }
        finally
        {
            if (file is not null)
            {
                await file.DisposeAsync();
            }
        }
    }
}
