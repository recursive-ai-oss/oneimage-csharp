using System.Text;

namespace OneImage.Svg.Render;

/// <summary>Reads <c>data:</c> URIs. The only external resource an SVG may name is data it carries itself; everything else is dangling.</summary>
internal static class DataUri
{
    /// <summary>
    /// Decodes <c>data:[&lt;media type&gt;][;base64],&lt;data&gt;</c> (RFC 2397). Base64 payloads ignore white space, as the XML attribute
    /// usually wraps lines; other payloads are percent-decoded. Returns <see langword="false"/> for anything that is not a well-formed
    /// <c>data:</c> URI.
    /// </summary>
    /// <param name="href">The attribute value.</param>
    /// <param name="maxBytes">The largest decoded payload accepted.</param>
    /// <param name="data">The decoded bytes.</param>
    /// <exception cref="ImageLimitException">The payload is larger than <paramref name="maxBytes"/> (decided from its encoded length, before decoding).</exception>
    public static bool TryDecode(string href, int maxBytes, out byte[] data)
    {
        data = [];
        ReadOnlySpan<char> s = href.AsSpan().Trim();
        if (!s.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        s = s[5..];
        int comma = s.IndexOf(',');
        if (comma < 0)
        {
            return false;
        }

        ReadOnlySpan<char> header = s[..comma].Trim();
        ReadOnlySpan<char> payload = s[(comma + 1)..];
        bool base64 = header.EndsWith(";base64", StringComparison.OrdinalIgnoreCase);
        if (payload.Length / 4L * 3 > maxBytes + 3)
        {
            throw new ImageLimitException($"An image data URI is larger than {maxBytes} bytes.");
        }

        if (base64)
        {
            byte[] buffer = new byte[(payload.Length / 4 * 3) + 3];
            if (!Convert.TryFromBase64Chars(payload, buffer, out int written))
            {
                return false;
            }

            data = written == buffer.Length ? buffer : buffer.AsSpan(0, written).ToArray();
        }
        else
        {
            var bytes = new List<byte>(payload.Length);
            Span<byte> one = stackalloc byte[4];
            for (int i = 0; i < payload.Length; i++)
            {
                char c = payload[i];
                if (c == '%' && i + 2 < payload.Length && char.IsAsciiHexDigit(payload[i + 1]) && char.IsAsciiHexDigit(payload[i + 2]))
                {
                    bytes.Add(Convert.ToByte(payload.Slice(i + 1, 2).ToString(), 16));
                    i += 2;
                }
                else if (c < 0x80)
                {
                    bytes.Add((byte)c);
                }
                else
                {
                    int n = Encoding.UTF8.GetBytes(payload.Slice(i, char.IsHighSurrogate(c) && i + 1 < payload.Length ? 2 : 1), one);
                    for (int k = 0; k < n; k++)
                    {
                        bytes.Add(one[k]);
                    }

                    if (char.IsHighSurrogate(c) && i + 1 < payload.Length)
                    {
                        i++;
                    }
                }
            }

            data = [.. bytes];
        }

        if (data.Length > maxBytes)
        {
            throw new ImageLimitException($"An image data URI is larger than {maxBytes} bytes.");
        }

        return data.Length > 0;
    }
}
