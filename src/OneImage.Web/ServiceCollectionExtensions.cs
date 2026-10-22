using System.Diagnostics.CodeAnalysis;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace OneImage.Web;

/// <summary>Customizes the services <c>AddOneImage</c> registered.</summary>
public interface IOneImageBuilder
{
    /// <summary>Gets the service collection.</summary>
    Microsoft.Extensions.DependencyInjection.IServiceCollection Services { get; }

    /// <summary>Changes the middleware settings. Can be called more than once; calls apply in order.</summary>
    /// <param name="configure">The change.</param>
    /// <returns>This builder.</returns>
    IOneImageBuilder Configure(Action<OneImageOptions> configure);

    /// <summary>Adds an image provider. Providers are asked in the order they were added; the first whose <see cref="IImageProvider.Match"/> says yes is used.</summary>
    /// <typeparam name="T">The provider type, created by dependency injection.</typeparam>
    /// <returns>This builder.</returns>
    IOneImageBuilder AddProvider<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T>()
        where T : class, IImageProvider;

    /// <summary>Adds an image provider created by a factory.</summary>
    /// <param name="factory">Creates the provider once.</param>
    /// <returns>This builder.</returns>
    IOneImageBuilder AddProvider(Func<IServiceProvider, IImageProvider> factory);

    /// <summary>Removes every registered provider, including the default web root provider.</summary>
    /// <returns>This builder.</returns>
    IOneImageBuilder ClearProviders();

    /// <summary>Replaces the cache.</summary>
    /// <typeparam name="T">The cache type, created by dependency injection.</typeparam>
    /// <returns>This builder.</returns>
    IOneImageBuilder SetCache<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T>()
        where T : class, IImageCache;

    /// <summary>Replaces the cache with one created by a factory.</summary>
    /// <param name="factory">Creates the cache once.</param>
    /// <returns>This builder.</returns>
    IOneImageBuilder SetCache(Func<IServiceProvider, IImageCache> factory);

    /// <summary>Replaces the cache key builder.</summary>
    /// <typeparam name="T">The key type.</typeparam>
    /// <returns>This builder.</returns>
    IOneImageBuilder SetCacheKey<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T>()
        where T : class, ICacheKey;

    /// <summary>Replaces the cache hash.</summary>
    /// <typeparam name="T">The hash type.</typeparam>
    /// <returns>This builder.</returns>
    IOneImageBuilder SetCacheHash<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T>()
        where T : class, ICacheHash;
}

/// <summary>Registration of the image middleware.</summary>
public static class OneImageServiceCollectionExtensions
{
    /// <summary>
    /// Registers the image middleware services: the web root provider, the disk cache under <c>oneimage-cache</c> in the content root,
    /// the path cache key, SHA-256 cache hash and <see cref="ImageUrlSigner"/>. Add <c>app.UseOneImage()</c> before static files.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Changes the middleware settings, or <see langword="null"/> for the defaults.</param>
    /// <returns>A builder to replace providers, cache, key and hash.</returns>
    public static IOneImageBuilder AddOneImage(this IServiceCollection services, Action<OneImageOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddOptions();
        services.TryAddSingleton<IImageCache, PhysicalFileSystemCache>();
        services.TryAddSingleton<ICacheKey, PathCacheKey>();
        services.TryAddSingleton<ICacheHash, Sha256CacheHash>();
        services.TryAddSingleton<ImageUrlSigner>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IImageProvider, WebRootImageProvider>());
        var builder = new Builder(services);
        if (configure is not null)
        {
            builder.Configure(configure);
        }

        return builder;
    }

    /// <summary>Changes the settings of the disk cache.</summary>
    /// <param name="builder">The builder.</param>
    /// <param name="configure">The change.</param>
    /// <returns>The builder.</returns>
    public static IOneImageBuilder ConfigureCache(this IOneImageBuilder builder, Action<PhysicalFileSystemCacheOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        _ = builder.Services.Configure(configure);
        return builder;
    }

    /// <summary>
    /// Adds the image middleware to the pipeline. Put it before <c>UseStaticFiles</c> so that requests with commands are handled here and plain
    /// requests fall through. Requires <c>AddOneImage</c>.
    /// </summary>
    /// <param name="app">The application builder.</param>
    /// <returns>The application builder.</returns>
    public static IApplicationBuilder UseOneImage(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        IServiceProvider sp = app.ApplicationServices;
        OneImageOptions options = sp.GetRequiredService<IOptions<OneImageOptions>>().Value;
        IEnumerable<IImageProvider> providers = sp.GetServices<IImageProvider>();
        IImageCache cache = sp.GetRequiredService<IImageCache>();
        ICacheKey key = sp.GetRequiredService<ICacheKey>();
        ICacheHash hash = sp.GetRequiredService<ICacheHash>();
        ILogger logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger("OneImage.Web");
        return app.Use(next =>
        {
            // Constructed directly rather than through UseMiddleware, which would need reflection.
            var middleware = new OneImageMiddleware(next, options, providers, cache, key, hash, logger);
            _ = sp.GetService<Microsoft.Extensions.Hosting.IHostApplicationLifetime>()?.ApplicationStopped.Register(middleware.Dispose);
            return middleware.InvokeAsync;
        });
    }

    private sealed class Builder : IOneImageBuilder
    {
        public Builder(IServiceCollection services) => Services = services;

        public IServiceCollection Services { get; }

        public IOneImageBuilder Configure(Action<OneImageOptions> configure)
        {
            ArgumentNullException.ThrowIfNull(configure);
            _ = Services.Configure(configure);
            return this;
        }

        public IOneImageBuilder AddProvider<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T>()
            where T : class, IImageProvider
        {
            Services.AddSingleton<IImageProvider, T>();
            return this;
        }

        public IOneImageBuilder AddProvider(Func<IServiceProvider, IImageProvider> factory)
        {
            ArgumentNullException.ThrowIfNull(factory);
            Services.AddSingleton(factory);
            return this;
        }

        public IOneImageBuilder ClearProviders()
        {
            _ = Services.RemoveAll<IImageProvider>();
            return this;
        }

        public IOneImageBuilder SetCache<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T>()
            where T : class, IImageCache
        {
            _ = Services.RemoveAll<IImageCache>();
            Services.AddSingleton<IImageCache, T>();
            return this;
        }

        public IOneImageBuilder SetCache(Func<IServiceProvider, IImageCache> factory)
        {
            ArgumentNullException.ThrowIfNull(factory);
            _ = Services.RemoveAll<IImageCache>();
            Services.AddSingleton(factory);
            return this;
        }

        public IOneImageBuilder SetCacheKey<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T>()
            where T : class, ICacheKey
        {
            _ = Services.RemoveAll<ICacheKey>();
            Services.AddSingleton<ICacheKey, T>();
            return this;
        }

        public IOneImageBuilder SetCacheHash<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T>()
            where T : class, ICacheHash
        {
            _ = Services.RemoveAll<ICacheHash>();
            Services.AddSingleton<ICacheHash, T>();
            return this;
        }
    }
}
