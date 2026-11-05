using OneImage.Text;
using TextFont = OneImage.Text.Font;

namespace OneImage.Metafile;

/// <summary>
/// Where the renderer finds fonts for text in a metafile. A face name from the file is looked up in <see cref="Fonts"/> first, then (if
/// <see cref="UseSystemFonts"/>) among the installed fonts, then through <see cref="Substitutions"/>, then by the font's family and pitch, and
/// finally <see cref="DefaultFamily"/>. Text for which no font is found draws nothing.
/// </summary>
public sealed class MetafileFontOptions
{
    /// <summary>Gets the fonts to look in before the installed fonts, or <see langword="null"/> for none. Add font files to it with <see cref="FontCollection"/>'s methods.</summary>
    public FontCollection? Fonts { get; init; }

    /// <summary>Gets a value indicating whether installed fonts are used (default <see langword="true"/>).</summary>
    public bool UseSystemFonts { get; init; } = true;

    /// <summary>
    /// Gets extra substitutions, from a face name (compared ignoring case) to the family names to try instead, most preferred first. They are
    /// tried before the built-in table, which maps the common Windows faces (Arial, Times New Roman, Courier New, Segoe UI, ...) to metric-compatible
    /// and widely installed families.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>>? Substitutions { get; init; }

    /// <summary>Gets the family to use when nothing else matches, or <see langword="null"/> to take the first sans-serif family found.</summary>
    public string? DefaultFamily { get; init; }
}

internal sealed class FontResolver
{
    private static readonly Dictionary<string, string[]> Builtin = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Arial"] = ["Liberation Sans", "Arimo", "Helvetica", "DejaVu Sans", "Noto Sans"],
        ["Helvetica"] = ["Liberation Sans", "Arimo", "Arial", "DejaVu Sans", "Noto Sans"],
        ["MS Sans Serif"] = ["Liberation Sans", "Arimo", "Arial", "DejaVu Sans", "Noto Sans"],
        ["Microsoft Sans Serif"] = ["Liberation Sans", "Arimo", "Arial", "DejaVu Sans", "Noto Sans"],
        ["Segoe UI"] = ["Noto Sans", "Open Sans", "DejaVu Sans", "Liberation Sans", "Arial"],
        ["Tahoma"] = ["DejaVu Sans", "Liberation Sans", "Arial", "Noto Sans"],
        ["Verdana"] = ["DejaVu Sans", "Liberation Sans", "Arial", "Noto Sans"],
        ["Calibri"] = ["Carlito", "Noto Sans", "Liberation Sans", "Arial"],
        ["Times New Roman"] = ["Liberation Serif", "Tinos", "Times", "DejaVu Serif", "Noto Serif"],
        ["Times"] = ["Liberation Serif", "Tinos", "Times New Roman", "DejaVu Serif", "Noto Serif"],
        ["Cambria"] = ["Caladea", "Liberation Serif", "DejaVu Serif", "Noto Serif"],
        ["Georgia"] = ["DejaVu Serif", "Liberation Serif", "Noto Serif"],
        ["Courier New"] = ["Liberation Mono", "Cousine", "Courier", "DejaVu Sans Mono", "Noto Sans Mono"],
        ["Courier"] = ["Liberation Mono", "Cousine", "Courier New", "DejaVu Sans Mono", "Noto Sans Mono"],
        ["Consolas"] = ["Inconsolata", "DejaVu Sans Mono", "Liberation Mono", "Noto Sans Mono"],
        ["Terminal"] = ["Liberation Mono", "DejaVu Sans Mono", "Noto Sans Mono"],
        ["Fixedsys"] = ["Liberation Mono", "DejaVu Sans Mono", "Noto Sans Mono"],
        ["System"] = ["Liberation Sans", "Arimo", "DejaVu Sans", "Noto Sans", "Arial"],
    };

    private static readonly string[] Sans = ["Liberation Sans", "Arimo", "DejaVu Sans", "Noto Sans", "Arial", "Helvetica"];
    private static readonly string[] Serif = ["Liberation Serif", "Tinos", "DejaVu Serif", "Noto Serif", "Times New Roman"];
    private static readonly string[] Mono = ["Liberation Mono", "Cousine", "DejaVu Sans Mono", "Noto Sans Mono", "Courier New"];

    private readonly MetafileFontOptions _options;
    private readonly Dictionary<(string, int), FontFamily?> _families = [];
    private readonly Dictionary<(FontFamily, FontStyle, int), TextFont> _fonts = [];

    public FontResolver(MetafileFontOptions? options) => _options = options ?? new MetafileFontOptions();

    private bool TryFamily(string name, out FontFamily family)
    {
        if (_options.Fonts is { } fc && fc.TryGet(name, out family))
        {
            return true;
        }

        if (_options.UseSystemFonts && SystemFonts.TryGet(name, out family))
        {
            return true;
        }

        family = null!;
        return false;
    }

    public FontFamily? Find(FontSpec spec)
    {
        int family = spec.PitchAndFamily & 0xF0;
        int pitch = spec.PitchAndFamily & 3;
        if (_families.TryGetValue((spec.FaceName, spec.PitchAndFamily), out FontFamily? hit))
        {
            return hit;
        }

        FontFamily? found = null;
        var tried = new List<string>();
        if (spec.FaceName.Length > 0)
        {
            tried.Add(spec.FaceName);
            if (_options.Substitutions is { } subs && subs.TryGetValue(spec.FaceName, out IReadOnlyList<string>? custom))
            {
                tried.AddRange(custom);
            }

            if (Builtin.TryGetValue(spec.FaceName, out string[]? list))
            {
                tried.AddRange(list);
            }
        }

        tried.AddRange(pitch == 1 || family == 0x30 && pitch == 1 ? Mono : family == 0x10 ? Serif : Sans);
        foreach (string name in tried)
        {
            if (TryFamily(name, out FontFamily f))
            {
                found = f;
                break;
            }
        }

        if (found is null && _options.DefaultFamily is { } def && TryFamily(def, out FontFamily df))
        {
            found = df;
        }

        _families[(spec.FaceName, spec.PitchAndFamily)] = found;
        return found;
    }

    /// <summary>The em size in logical units: a negative height is the em; a positive one is the cell height, converted with the font's metrics.</summary>
    public double EmSize(FontSpec fs)
    {
        double h = fs.Height;
        if (h < 0)
        {
            return -h;
        }

        if (h == 0)
        {
            return 16;
        }

        TextFont? probe = CreateFont(fs, 100);
        if (probe is null)
        {
            return h;
        }

        FontMetrics fm = probe.Metrics;
        double cell = fm.Ascender + Math.Abs(fm.Descender);
        return cell <= 0 ? h : h * fm.UnitsPerEm / cell;
    }

    /// <summary>The advance width of a string, in logical units.</summary>
    public double Measure(FontSpec spec, string text)
    {
        TextFont? f = CreateFont(spec, EmSize(spec));
        if (f is null)
        {
            return text.Length * Math.Abs(spec.Height) * 0.5;
        }

        double w = 0;
        for (int i = 0; i < text.Length; i++)
        {
            int cp = text[i];
            if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                cp = char.ConvertToUtf32(text[i], text[i + 1]);
                i++;
            }

            w += f.GetAdvance(cp);
        }

        return w;
    }

    public TextFont? CreateFont(FontSpec spec, double emSize)
    {
        FontFamily? fam = Find(spec);
        if (fam is null)
        {
            return null;
        }

        bool bold = spec.Weight >= 600;
        FontStyle style = bold ? (spec.Italic ? FontStyle.BoldItalic : FontStyle.Bold) : (spec.Italic ? FontStyle.Italic : FontStyle.Regular);
        // Sizes are cached per quarter pixel so that equal text sizes share one font.
        int key = (int)Math.Round(emSize * 4);
        if (!_fonts.TryGetValue((fam, style, key), out TextFont? f))
        {
            f = fam.CreateFont((float)Math.Max(key / 4.0, 0.25), style);
            _fonts[(fam, style, key)] = f;
        }

        return f;
    }
}
