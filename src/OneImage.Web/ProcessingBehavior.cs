namespace OneImage.Web;

/// <summary>Whether an image provider's sources are processed only on request or always.</summary>
public enum ProcessingBehavior
{
    /// <summary>The image is processed only when the request carries at least one recognized command (after the parse hook ran). Other requests fall through to the next middleware, typically static files. This is the default.</summary>
    CommandOnly = 0,

    /// <summary>Every image request is processed, even without commands: the image is re-encoded with the default settings (EXIF removed unless <see cref="OneImageOptions.KeepMetadata"/>).</summary>
    All = 1,
}

/// <summary>What the middleware does with a query-string parameter it does not recognize.</summary>
public enum UnknownCommandBehavior
{
    /// <summary>The parameter is ignored. It is not part of the cache key, so junk parameters cannot multiply cache entries. This is the default.</summary>
    Ignore = 0,

    /// <summary>The request fails with 400 Bad Request. Parameters used by other middleware on the same URL then need <see cref="OneImageOptions.IgnoredParameters"/>.</summary>
    Reject = 1,
}

/// <summary>The hash function of the HMAC that signs image URLs.</summary>
public enum HmacAlgorithm
{
    /// <summary>HMAC-SHA-256 (64 hex characters). This is the default.</summary>
    Sha256 = 0,

    /// <summary>HMAC-SHA-384 (96 hex characters).</summary>
    Sha384 = 1,

    /// <summary>HMAC-SHA-512 (128 hex characters).</summary>
    Sha512 = 2,
}
