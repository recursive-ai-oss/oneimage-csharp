using System.Globalization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;

namespace OneImage.Web;

/// <summary>A request that cannot be served as asked; carries the HTTP status and a short message that never echoes the input.</summary>
internal sealed class RequestRejectedException : Exception
{
    public RequestRejectedException(int statusCode, string message)
        : base(message)
    {
        StatusCode = statusCode;
    }

    public int StatusCode { get; }
}

/// <summary>Reads the query string into canonical commands and validates them into <see cref="ProcessingCommands"/>.</summary>
internal static class CommandParser
{
    internal const string HmacName = "hmac";

    // Alias (our short names and the ImageSharp.Web spellings) -> canonical name.
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["width"] = "width",
        ["w"] = "width",
        ["height"] = "height",
        ["h"] = "height",
        ["fit"] = "fit",
        ["rmode"] = "mode",
        ["anchor"] = "anchor",
        ["ranchor"] = "anchor",
        ["focus"] = "focus",
        ["xy"] = "focus",
        ["filter"] = "filter",
        ["rsampler"] = "filter",
        ["bg"] = "bg",
        ["color"] = "color",
        ["bgcolor"] = "flatten",
        ["format"] = "format",
        ["fm"] = "format",
        ["quality"] = "quality",
        ["q"] = "quality",
        ["orient"] = "orient",
        ["autoorient"] = "orient",
        ["upscale"] = "upscale",
        ["compand"] = "compand",
        ["preset"] = "preset",
    };

    /// <summary>Collects the recognized commands of a request, expanding a preset. Returns an empty dictionary when there are none.</summary>
    public static Dictionary<string, string> ReadRequest(HttpRequest request, OneImageOptions options)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var explicitValues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, Microsoft.Extensions.Primitives.StringValues> pair in request.Query)
        {
            if (string.Equals(pair.Key, HmacName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!Aliases.TryGetValue(pair.Key, out string? name) || (options.PresetsOnly && name != "preset"))
            {
                if (!options.PresetsOnly && options.UnknownCommands == UnknownCommandBehavior.Reject && !options.IgnoredParameters.Contains(pair.Key))
                {
                    throw new RequestRejectedException(400, "Unknown image command.");
                }

                continue;
            }

            Add(explicitValues, name, pair.Value);
        }

        if (explicitValues.Remove("preset", out string? presetName))
        {
            if (!options.Presets.TryGetValue(presetName, out string? presetText))
            {
                throw new RequestRejectedException(400, "Unknown preset.");
            }

            foreach (KeyValuePair<string, Microsoft.Extensions.Primitives.StringValues> pair in QueryHelpers.ParseQuery(presetText))
            {
                if (Aliases.TryGetValue(pair.Key, out string? name) && name != "preset")
                {
                    Add(result, name, pair.Value);
                }
            }
        }

        foreach (KeyValuePair<string, string> pair in explicitValues)
        {
            result[pair.Key] = pair.Value;
        }

        return result;
    }

    private static void Add(Dictionary<string, string> into, string name, Microsoft.Extensions.Primitives.StringValues values)
    {
        if (values.Count != 1)
        {
            throw new RequestRejectedException(400, "A command may be given only once.");
        }

        string value = values[0] ?? string.Empty;
        if (into.TryGetValue(name, out string? existing) && !string.Equals(existing, value, StringComparison.Ordinal))
        {
            throw new RequestRejectedException(400, "Conflicting spellings of the same command.");
        }

        into[name] = value;
    }

    /// <summary>Validates canonical commands against the options and the codecs.</summary>
    public static ProcessingCommands Normalize(IDictionary<string, string> c, OneImageOptions options, ImageConfiguration config)
    {
        var r = new ProcessingCommands { AutoOrient = options.AutoOrient, KeepMetadata = options.KeepMetadata };
        r.Width = Dimension(c, "width", options.MaxWidth);
        r.Height = Dimension(c, "height", options.MaxHeight);

        bool hasMode = c.TryGetValue("mode", out string? mode);
        bool hasFit = c.TryGetValue("fit", out string? fit);
        if (hasMode && hasFit)
        {
            throw new RequestRejectedException(400, "Give either fit or rmode, not both.");
        }

        Fit f = options.DefaultFit;
        bool upscale = options.DefaultUpscale;
        bool minMode = false;
        if (hasFit)
        {
            f = Lower(fit!) switch
            {
                "contain" => Fit.Contain,
                "cover" or "crop" => Fit.Cover,
                "stretch" => Fit.Stretch,
                "pad" => Fit.Pad,
                _ => throw Bad("fit"),
            };
        }
        else if (hasMode)
        {
            switch (Lower(mode!))
            {
                case "crop":
                    (f, upscale) = (Fit.Cover, true);
                    break;
                case "pad":
                    (f, upscale) = (Fit.Pad, true);
                    break;
                case "boxpad":
                    (f, upscale) = (Fit.Pad, false);
                    break;
                case "max":
                    (f, upscale) = (Fit.Contain, false);
                    break;
                case "min":
                    (f, upscale, minMode) = (Fit.Stretch, true, true);
                    break;
                case "stretch":
                    (f, upscale) = (Fit.Stretch, true);
                    break;
                default:
                    throw Bad("rmode");
            }
        }

        bool bothDims = r.Width is not null && r.Height is not null;
        if (!bothDims)
        {
            // One dimension keeps the aspect ratio whatever the fit, so the fit does not distinguish requests.
            f = Fit.Contain;
            minMode = false;
        }

        r.Fit = f;
        r.CoverWithoutCrop = minMode;
        if (c.TryGetValue("upscale", out string? up))
        {
            upscale = Bool(up, "upscale");
        }

        r.Upscale = upscale;
        r.Anchor = c.TryGetValue("anchor", out string? anchor) ? ParseAnchor(anchor) : Anchor.Center;
        if (c.TryGetValue("focus", out string? focus))
        {
            (double X, double Y) point = ParseFocus(focus);
            if (f == Fit.Cover && bothDims)
            {
                r.Focus = point;
            }
        }

        r.Filter = c.TryGetValue("filter", out string? filter) ? ParseFilter(filter) : ResamplingFilter.Auto;
        if (c.TryGetValue("compand", out string? compand))
        {
            _ = Bool(compand, "compand"); // Resampling always runs in linear light; accepted for compatibility.
        }

        Rgba32? bg = c.TryGetValue("bg", out string? bgText) ? ParseColor(bgText, "bg") : null;
        r.PadColor = c.TryGetValue("color", out string? padText) ? ParseColor(padText, "color") : bg;
        r.FlattenColor = c.TryGetValue("flatten", out string? flatText) ? ParseColor(flatText, "bgcolor") : bg;

        if (c.TryGetValue("format", out string? formatText))
        {
            IImageFormat? format = config.FindByName(formatText) ?? config.FindByExtension(formatText.StartsWith('.') ? formatText : "." + formatText);
            if (format is null || format.Encoder is null || !options.AllowedFormats.Contains(format.Name))
            {
                throw Bad("format");
            }

            r.Format = format;
        }

        if (c.TryGetValue("quality", out string? qualityText))
        {
            if (!int.TryParse(qualityText, NumberStyles.None, CultureInfo.InvariantCulture, out int q) || q is < 1 or > 100)
            {
                throw Bad("quality");
            }

            r.Quality = q;
        }

        if (c.TryGetValue("orient", out string? orient))
        {
            r.AutoOrient = Bool(orient, "orient");
        }

        return r;
    }

    private static RequestRejectedException Bad(string name) => new(400, "Invalid value for '" + name + "'.");

    private static string Lower(string s) => s.Trim().Replace("-", string.Empty, StringComparison.Ordinal).Replace("_", string.Empty, StringComparison.Ordinal).ToLowerInvariant();

    private static int? Dimension(IDictionary<string, string> c, string name, int max)
    {
        if (!c.TryGetValue(name, out string? text))
        {
            return null;
        }

        if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int v) || v < 1)
        {
            throw Bad(name);
        }

        if (v > max)
        {
            throw new RequestRejectedException(400, "The requested " + name + " is above the limit of " + max.ToString(CultureInfo.InvariantCulture) + ".");
        }

        return v;
    }

    private static bool Bool(string text, string name) => Lower(text) switch
    {
        "1" or "true" or "yes" or "on" => true,
        "0" or "false" or "no" or "off" => false,
        _ => throw Bad(name),
    };

    private static Anchor ParseAnchor(string text) => Lower(text) switch
    {
        "center" or "centre" or "middle" => Anchor.Center,
        "top" => Anchor.Top,
        "bottom" => Anchor.Bottom,
        "left" => Anchor.Left,
        "right" => Anchor.Right,
        "topleft" => Anchor.TopLeft,
        "topright" => Anchor.TopRight,
        "bottomleft" => Anchor.BottomLeft,
        "bottomright" => Anchor.BottomRight,
        "smart" => Anchor.Smart,
        _ => throw Bad("anchor"),
    };

    private static ResamplingFilter ParseFilter(string text) => Lower(text) switch
    {
        "auto" => ResamplingFilter.Auto,
        "nearest" or "nearestneighbor" => ResamplingFilter.Nearest,
        "box" => ResamplingFilter.Box,
        "bilinear" or "triangle" => ResamplingFilter.Bilinear,
        "catmullrom" or "bicubic" or "cubic" => ResamplingFilter.CatmullRom,
        "mitchell" or "mitchellnetravali" => ResamplingFilter.Mitchell,
        "lanczos2" => ResamplingFilter.Lanczos2,
        "lanczos3" or "lanczos" => ResamplingFilter.Lanczos3,
        _ => throw Bad("filter"),
    };

    private static (double X, double Y) ParseFocus(string text)
    {
        string[] parts = text.Split(',');
        if (parts.Length != 2
            || !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double x)
            || !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double y)
            || !(x is >= 0 and <= 1) || !(y is >= 0 and <= 1))
        {
            throw Bad("focus");
        }

        return (x, y);
    }

    private static Rgba32 ParseColor(string text, string name)
    {
        string t = text.Trim().TrimStart('#');
        switch (t.ToLowerInvariant())
        {
            case "white": return new Rgba32(255, 255, 255, 255);
            case "black": return new Rgba32(0, 0, 0, 255);
            case "transparent": return new Rgba32(0, 0, 0, 0);
            case "red": return new Rgba32(255, 0, 0, 255);
            case "green": return new Rgba32(0, 128, 0, 255);
            case "blue": return new Rgba32(0, 0, 255, 255);
            case "yellow": return new Rgba32(255, 255, 0, 255);
            case "gray" or "grey": return new Rgba32(128, 128, 128, 255);
        }

        if (t.Contains(',', StringComparison.Ordinal))
        {
            string[] parts = t.Split(',');
            if (parts.Length is 3 or 4)
            {
                var v = new byte[4] { 0, 0, 0, 255 };
                for (int i = 0; i < parts.Length; i++)
                {
                    if (!byte.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out v[i]))
                    {
                        throw Bad(name);
                    }
                }

                return new Rgba32(v[0], v[1], v[2], v[3]);
            }

            throw Bad(name);
        }

        // Hex: rgb, rgba, rrggbb or rrggbbaa.
        if (t.Length is 3 or 4)
        {
            t = string.Concat(t.Select(ch => new string(ch, 2)));
        }

        if ((t.Length is not (6 or 8)) || !uint.TryParse(t, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out uint raw))
        {
            throw Bad(name);
        }

        if (t.Length == 6)
        {
            raw = (raw << 8) | 0xFF;
        }

        return new Rgba32((byte)(raw >> 24), (byte)(raw >> 16), (byte)(raw >> 8), (byte)raw);
    }
}
