using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace OneImage.Web;

/// <summary>
/// Signs and verifies image URLs. The signed text is the request path (decoded, including the path base) followed by <c>?</c> and the
/// raw query string without the <c>hmac</c> parameter, exactly as the client sends it, so reordering or editing any parameter
/// invalidates the token. The token is the lower-case hexadecimal HMAC.
/// </summary>
public static class HmacUtilities
{
    /// <summary>The query parameter that carries the token.</summary>
    public const string TokenCommand = "hmac";

    /// <summary>Computes the token of a value.</summary>
    /// <param name="value">The text to sign.</param>
    /// <param name="key">The secret.</param>
    /// <param name="algorithm">The hash function.</param>
    /// <returns>Lower-case hex: 64, 96 or 128 characters.</returns>
    public static string ComputeToken(string value, ReadOnlySpan<byte> key, HmacAlgorithm algorithm = HmacAlgorithm.Sha256)
    {
        ArgumentNullException.ThrowIfNull(value);
        byte[] data = Encoding.UTF8.GetBytes(value);
        byte[] mac = algorithm switch
        {
            HmacAlgorithm.Sha256 => HMACSHA256.HashData(key, data),
            HmacAlgorithm.Sha384 => HMACSHA384.HashData(key, data),
            HmacAlgorithm.Sha512 => HMACSHA512.HashData(key, data),
            _ => throw new ArgumentOutOfRangeException(nameof(algorithm)),
        };
        return Convert.ToHexStringLower(mac);
    }

    /// <summary>Returns the text that is signed for a path and a raw query string.</summary>
    /// <param name="path">The decoded path including any path base.</param>
    /// <param name="rawQuery">The query string, with or without the leading <c>?</c>; a <c>hmac</c> parameter in it is left out.</param>
    /// <returns>The signed text.</returns>
    public static string SignedText(string path, string? rawQuery)
    {
        ArgumentNullException.ThrowIfNull(path);
        string q = (rawQuery ?? string.Empty).TrimStart('?');
        var sb = new StringBuilder(path.Length + q.Length + 1);
        sb.Append(path);
        bool first = true;
        foreach (string part in q.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = part.IndexOf('=', StringComparison.Ordinal);
            string name = eq < 0 ? part : part[..eq];
            if (string.Equals(name, TokenCommand, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            sb.Append(first ? '?' : '&').Append(part);
            first = false;
        }

        return sb.ToString();
    }

    /// <summary>Checks the <c>hmac</c> parameter of a request.</summary>
    /// <param name="request">The request.</param>
    /// <param name="key">The secret.</param>
    /// <param name="algorithm">The hash function.</param>
    /// <returns><see langword="true"/> if there is exactly one token and it matches (compared in constant time).</returns>
    public static bool Verify(HttpRequest request, ReadOnlySpan<byte> key, HmacAlgorithm algorithm = HmacAlgorithm.Sha256)
    {
        ArgumentNullException.ThrowIfNull(request);
        Microsoft.Extensions.Primitives.StringValues tokens = request.Query[TokenCommand];
        if (tokens.Count != 1 || string.IsNullOrEmpty(tokens[0]))
        {
            return false;
        }

        string expected = ComputeToken(SignedText((request.PathBase + request.Path).Value ?? string.Empty, request.QueryString.Value), key, algorithm);
        string given = tokens[0]!.ToLowerInvariant();
        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(given));
    }
}

/// <summary>Adds the <c>hmac</c> token to image URLs. Registered by <c>AddOneImage</c> with the key from <see cref="OneImageOptions"/>.</summary>
public sealed class ImageUrlSigner
{
    private readonly OneImageOptions _options;

    /// <summary>Initializes the signer.</summary>
    /// <param name="options">The middleware settings that hold the key.</param>
    public ImageUrlSigner(IOptions<OneImageOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
    }

    /// <summary>Gets whether a key is configured, i.e. whether <see cref="Sign"/> adds a token.</summary>
    public bool IsEnabled => _options.HmacSecretKey is not null;

    /// <summary>Signs a URL, given as it will be sent to the browser: an absolute path (with the path base) and optional query string.</summary>
    /// <param name="pathAndQuery">For example <c>/img/photo.jpg?width=300&amp;format=webp</c>. Percent-escapes in the path are decoded for signing, as the server decodes them.</param>
    /// <returns>The same URL with <c>hmac=</c> appended, or unchanged when no key is configured.</returns>
    public string Sign(string pathAndQuery)
    {
        ArgumentNullException.ThrowIfNull(pathAndQuery);
        if (_options.HmacSecretKey is not { } key)
        {
            return pathAndQuery;
        }

        int q = pathAndQuery.IndexOf('?', StringComparison.Ordinal);
        string path = q < 0 ? pathAndQuery : pathAndQuery[..q];
        string query = q < 0 ? string.Empty : pathAndQuery[(q + 1)..];
        string token = HmacUtilities.ComputeToken(HmacUtilities.SignedText(Uri.UnescapeDataString(path), query), key, _options.HmacAlgorithm);
        string separator = q < 0 ? "?" : (query.Length == 0 ? string.Empty : "&");
        return pathAndQuery + separator + HmacUtilities.TokenCommand + "=" + token;
    }
}
