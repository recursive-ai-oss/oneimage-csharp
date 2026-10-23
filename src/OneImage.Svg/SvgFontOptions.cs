using OneImage.Svg.Render;
using OneImage.Text;

namespace OneImage.Svg;

/// <summary>
/// The fonts SVG text is drawn with. Register font files with <see cref="AddFont(string)"/> and friends, optionally map the generic
/// families (<c>serif</c>, <c>sans-serif</c>, <c>monospace</c>, <c>cursive</c>, <c>fantasy</c>) to family names, and pass the options to
/// <see cref="SvgFormat"/>. Text whose font cannot be found draws nothing, so a document that shows text needs either a font added here or
/// an installed font that its <c>font-family</c> names.
/// </summary>
/// <remarks>
/// The options are read when the <see cref="SvgFormat"/> is created; fonts added afterwards are not seen by it. Fonts named by a document are
/// never loaded from the document's own bytes or from a path it supplies: <c>@font-face</c> is ignored, as are external references.
/// </remarks>
public sealed class SvgFontOptions
{
    private readonly List<SvgFontEntry> _entries = [];
    private readonly Dictionary<string, string[]> _generic = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _fallback = [];
    private double _defaultFontSize = 16;

    /// <summary>Initializes empty options: no fonts added, installed fonts used, the default generic families.</summary>
    public SvgFontOptions()
    {
    }

    internal SvgFontOptions(SvgFontOptions other)
    {
        _entries.AddRange(other._entries);
        foreach (KeyValuePair<string, string[]> kv in other._generic)
        {
            _generic[kv.Key] = kv.Value;
        }

        _fallback.AddRange(other._fallback);
        UseSystemFonts = other.UseSystemFonts;
        _defaultFontSize = other._defaultFontSize;
    }

    /// <summary>
    /// Gets or sets whether families that are not among the added fonts are looked up among the fonts installed on the machine. The default is
    /// <see langword="true"/>. Switch it off for output that must not depend on the machine.
    /// </summary>
    public bool UseSystemFonts { get; set; } = true;

    /// <summary>
    /// Gets or sets the size in pixels of the CSS <c>medium</c> font, which is the <c>font-size</c> text has when nothing in the document sets one.
    /// The default is 16, as in browsers. The named sizes step by 1.2 around it (<c>large</c> is 1.2 times, <c>small</c> one 1.2th of it), and
    /// <c>em</c>, <c>%</c> and <c>rem</c> sizes that reach the top of the document are relative to it. Another renderer's test suite may be
    /// made with 12.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not a finite number above zero.</exception>
    public double DefaultFontSize
    {
        get => _defaultFontSize;
        set
        {
            if (!(value > 0) || !double.IsFinite(value))
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "The default font size must be a finite number above zero.");
            }

            _defaultFontSize = value;
        }
    }

    /// <summary>
    /// Gets the family names tried, in order, for characters the document's own <c>font-family</c> list cannot show, before the generic
    /// <c>serif</c> family and then any added font. Empty by default.
    /// </summary>
    public IList<string> FallbackFamilies => _fallback;

    /// <summary>Adds the fonts of a font file (TrueType, OpenType/CFF, a collection, WOFF or WOFF2). Each face is matched by its family name, weight and slant.</summary>
    /// <param name="path">The file.</param>
    /// <returns>The names of the families the file adds.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is <see langword="null"/>.</exception>
    /// <exception cref="FontFormatException">The file is not a usable font.</exception>
    /// <exception cref="IOException">The file cannot be read.</exception>
    public IReadOnlyList<string> AddFont(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return AddFont(File.ReadAllBytes(path));
    }

    /// <summary>Adds the fonts of a font file held in memory; see <see cref="AddFont(string)"/>.</summary>
    /// <param name="data">The file's bytes (copied).</param>
    /// <returns>The names of the families the data adds.</returns>
    /// <exception cref="FontFormatException">The data is not a usable font.</exception>
    public IReadOnlyList<string> AddFont(ReadOnlyMemory<byte> data)
    {
        var collection = new FontCollection();
        IReadOnlyList<FontFamily> families = collection.AddCollection(data);
        var names = new List<string>();
        var seen = new HashSet<(FontFamily, FontStyle)>();
        foreach (FontFamily family in families)
        {
            foreach (FontStyle style in family.GetAvailableStyles())
            {
                if (!seen.Add((family, style)))
                {
                    continue;
                }

                Font proto = family.CreateFont(1, style);
                _entries.Add(new SvgFontEntry(family.Name, FontFaceInfo.GetWeight(proto), FontFaceInfo.GetIsItalic(proto), proto));
                if (!names.Contains(family.Name))
                {
                    names.Add(family.Name);
                }
            }
        }

        return names;
    }

    /// <summary>Adds the fonts of a font file read from a stream; see <see cref="AddFont(string)"/>.</summary>
    /// <param name="stream">The stream, read to its end.</param>
    /// <returns>The names of the families the stream adds.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is <see langword="null"/>.</exception>
    /// <exception cref="FontFormatException">The data is not a usable font.</exception>
    public IReadOnlyList<string> AddFont(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return AddFont(ms.ToArray());
    }

    /// <summary>
    /// Maps a generic family to the family names tried for it, in order (each looked up among the added fonts, then the installed ones).
    /// The defaults name common system fonts for each generic family.
    /// </summary>
    /// <param name="generic">One of <c>serif</c>, <c>sans-serif</c>, <c>monospace</c>, <c>cursive</c>, <c>fantasy</c> (<c>system-ui</c> maps like <c>sans-serif</c>).</param>
    /// <param name="families">The family names.</param>
    /// <exception cref="ArgumentException"><paramref name="generic"/> is not a generic family name.</exception>
    public void SetGenericFamily(string generic, params string[] families)
    {
        ArgumentNullException.ThrowIfNull(generic);
        ArgumentNullException.ThrowIfNull(families);
        if (!SvgFontSelector.IsGeneric(generic))
        {
            throw new ArgumentException("Not a generic font family name.", nameof(generic));
        }

        _generic[generic] = [.. families];
    }

    internal SvgFontEntry[] SnapshotEntries() => [.. _entries];

    internal Dictionary<string, string[]> SnapshotGeneric() => new(_generic, StringComparer.OrdinalIgnoreCase);

    internal string[] SnapshotFallback() => [.. _fallback];
}

/// <summary>One face registered with <see cref="SvgFontOptions"/>.</summary>
internal sealed record SvgFontEntry(string Family, int Weight, bool Italic, Font Proto);
