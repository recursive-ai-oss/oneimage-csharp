using OneImage.Text;

namespace OneImage.Svg.Render;

/// <summary>The fonts one <c>font-family</c> list stands for, in order, plus the fallbacks for characters none of them has.</summary>
internal sealed class FontChain
{
    private readonly SvgFontSelector _owner;
    private readonly Font[] _primary;
    private readonly double _size;
    private readonly Dictionary<int, Font?> _picked = [];
    private Font[]? _tail;

    public FontChain(SvgFontSelector owner, Font[] primary, double size)
    {
        _owner = owner;
        _primary = primary;
        _size = size;
    }

    /// <summary>Gets the first font of the list, or the first fallback when the list names none that exists; <see langword="null"/> when there is no font at all.</summary>
    public Font? First => _primary.Length > 0 ? _primary[0] : Tail().Length > 0 ? _tail![0] : null;

    private Font[] Tail() => _tail ??= _owner.FallbackFonts(_size);

    /// <summary>Returns the first font that has the character; the first font when none has it, <see langword="null"/> when there is no font.</summary>
    public Font? Pick(int codePoint)
    {
        if (_picked.TryGetValue(codePoint, out Font? hit))
        {
            return hit;
        }

        Font? found = null;
        foreach (Font f in _primary)
        {
            if (f.HasGlyph(codePoint))
            {
                found = f;
                break;
            }
        }

        if (found is null)
        {
            foreach (Font f in Tail())
            {
                if (f.HasGlyph(codePoint))
                {
                    found = f;
                    break;
                }
            }
        }

        found ??= First;
        _picked[codePoint] = found;
        return found;
    }
}

/// <summary>Finds fonts for SVG text: the fonts registered in <see cref="SvgFontOptions"/>, then installed ones, matched the CSS way.</summary>
internal sealed class SvgFontSelector
{
    private static readonly Dictionary<string, string[]> DefaultGeneric = new(StringComparer.OrdinalIgnoreCase)
    {
        ["serif"] = ["Times New Roman", "Liberation Serif", "DejaVu Serif", "Noto Serif", "Georgia"],
        ["sans-serif"] = ["Arial", "Liberation Sans", "Helvetica", "DejaVu Sans", "Noto Sans", "Segoe UI"],
        ["monospace"] = ["Courier New", "Liberation Mono", "DejaVu Sans Mono", "Noto Sans Mono", "Consolas"],
        ["cursive"] = ["Comic Sans MS", "Apple Chancery", "URW Chancery L", "Z003"],
        ["fantasy"] = ["Impact", "Papyrus", "Haettenschweiler"],
    };

    private readonly SvgFontEntry[] _entries;
    private readonly Dictionary<string, List<SvgFontEntry>> _byFamily = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string[]> _generic;
    private readonly string[] _fallback;
    private readonly bool _system;
    private readonly Dictionary<(Font, long), Font> _sized = [];
    private readonly Dictionary<(FontFamily, FontStyle), Font> _systemProto = [];
    private readonly Dictionary<(string, int, bool, long), FontChain> _chains = [];

    public double MediumFontSize { get; }

    public SvgFontSelector(SvgFontOptions? options)
    {
        MediumFontSize = options?.DefaultFontSize ?? Dom.SvgStyle.DefaultFontSize;
        if (options is null)
        {
            _entries = [];
            _generic = DefaultGeneric;
            _fallback = [];
            _system = true;
        }
        else
        {
            _entries = options.SnapshotEntries();
            _generic = new Dictionary<string, string[]>(DefaultGeneric, StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, string[]> kv in options.SnapshotGeneric())
            {
                _generic[kv.Key] = kv.Value;
            }

            _fallback = options.SnapshotFallback();
            _system = options.UseSystemFonts;
        }

        foreach (SvgFontEntry e in _entries)
        {
            if (!_byFamily.TryGetValue(e.Family, out List<SvgFontEntry>? list))
            {
                _byFamily[e.Family] = list = [];
            }

            list.Add(e);
        }
    }

    public static bool IsGeneric(string name) =>
        name.Equals("serif", StringComparison.OrdinalIgnoreCase) || name.Equals("sans-serif", StringComparison.OrdinalIgnoreCase)
        || name.Equals("monospace", StringComparison.OrdinalIgnoreCase) || name.Equals("cursive", StringComparison.OrdinalIgnoreCase)
        || name.Equals("fantasy", StringComparison.OrdinalIgnoreCase) || name.Equals("system-ui", StringComparison.OrdinalIgnoreCase);

    /// <summary>The fonts for a <c>font-family</c> list at a size, in order, as a chain with the fallbacks behind it.</summary>
    public FontChain Chain(string[] families, int weight, bool italic, double size)
    {
        long sizeBits = BitConverter.DoubleToInt64Bits(size);
        string key = string.Join('\u0001', families);
        if (_chains.TryGetValue((key, weight, italic, sizeBits), out FontChain? cached))
        {
            return cached;
        }

        var fonts = new List<Font>();
        if (size > 0 && double.IsFinite(size))
        {
            foreach (string family in families)
            {
                if (Find(family, weight, italic, size, 0) is { } f && !fonts.Contains(f))
                {
                    fonts.Add(f);
                }
            }
        }

        var chain = new FontChain(this, [.. fonts], size > 0 && double.IsFinite(size) ? size : 0);
        if (_chains.Count > 4096)
        {
            _chains.Clear();
        }

        _chains[(key, weight, italic, sizeBits)] = chain;
        return chain;
    }

    /// <summary>The fonts behind a chain: the configured fallback families, the generic serif family, then every added font.</summary>
    public Font[] FallbackFonts(double size)
    {
        var list = new List<Font>();
        if (!(size > 0) || !double.IsFinite(size))
        {
            return [];
        }

        foreach (string name in _fallback)
        {
            if (Find(name, 400, false, size, 0) is { } f && !list.Contains(f))
            {
                list.Add(f);
            }
        }

        if (Find("serif", 400, false, size, 0) is { } serif && !list.Contains(serif))
        {
            list.Add(serif);
        }

        foreach (SvgFontEntry e in _entries)
        {
            Font f = Sized(e.Proto, size);
            if (!list.Contains(f))
            {
                list.Add(f);
            }
        }

        return [.. list];
    }

    private Font? Find(string name, int weight, bool italic, double size, int depth)
    {
        if (IsGeneric(name))
        {
            if (depth > 0)
            {
                return null;
            }

            string key = name.Equals("system-ui", StringComparison.OrdinalIgnoreCase) ? "sans-serif" : name;
            if (_generic.TryGetValue(key, out string[]? candidates))
            {
                foreach (string c in candidates)
                {
                    if (Find(c, weight, italic, size, depth + 1) is { } f)
                    {
                        return f;
                    }
                }
            }

            return null;
        }

        if (_byFamily.TryGetValue(name, out List<SvgFontEntry>? entries))
        {
            return Sized(Best(entries, weight, italic).Proto, size);
        }

        if (_system && SystemFonts.TryGet(name, out FontFamily family))
        {
            FontStyle style = weight >= 600 ? (italic ? FontStyle.BoldItalic : FontStyle.Bold) : (italic ? FontStyle.Italic : FontStyle.Regular);
            if (!_systemProto.TryGetValue((family, style), out Font? proto))
            {
                _systemProto[(family, style)] = proto = family.CreateFont(1, style);
            }

            return Sized(proto, size);
        }

        return null;
    }

    private readonly Dictionary<Font, bool> _smallCaps = [];

    /// <summary>Whether the face has small-capital glyphs for lowercase letters (the smcp feature does something to an "a").</summary>
    public bool HasSmallCaps(Font font)
    {
        if (_smallCaps.TryGetValue(font, out bool known))
        {
            return known;
        }

        var on = new ShapingOptions();
        on.Features.Add(new FontFeature("smcp", 1));
        var off = new ShapingOptions();
        IReadOnlyList<ShapedCluster> a = TextShaping.ShapeRun("a", font, false, on);
        IReadOnlyList<ShapedCluster> b = TextShaping.ShapeRun("a", font, false, off);
        bool result = a.Count == 1 && b.Count == 1 && (a[0].Advance != b[0].Advance || a[0].GetOutline().Bounds != b[0].GetOutline().Bounds);
        _smallCaps[font] = result;
        return result;
    }

    /// <summary>The same face at a fraction of the size (synthesized small capitals).</summary>
    public Font Scaled(Font font, double factor) => Sized(font, font.Size * factor);

    private Font Sized(Font proto, double size)
    {
        (Font, long) key = (proto, BitConverter.DoubleToInt64Bits(size));
        if (!_sized.TryGetValue(key, out Font? f))
        {
            if (_sized.Count > 8192)
            {
                _sized.Clear();
            }

            // A size too large or small for a float ("1e300") would be infinity or zero in the font, which refuses it: keep it in a range
            // where the glyphs are still finite numbers.
            _sized[key] = f = proto.WithSize((float)Math.Clamp(size, 1e-4, 1e6));
        }

        return f;
    }

    // CSS Fonts 4 font matching among the faces of one family: slant first, then weight.
    private static SvgFontEntry Best(List<SvgFontEntry> entries, int weight, bool italic)
    {
        List<SvgFontEntry> pool = entries.FindAll(e => e.Italic == italic);
        if (pool.Count == 0)
        {
            pool = entries;
        }

        SvgFontEntry? exact = pool.Find(e => e.Weight == weight);
        if (exact is not null)
        {
            return exact;
        }

        var sorted = new List<SvgFontEntry>(pool);
        sorted.Sort((a, b) => a.Weight.CompareTo(b.Weight));
        SvgFontEntry? FirstAbove(int lo, int hi) => sorted.Find(e => e.Weight > lo && e.Weight <= hi);
        SvgFontEntry? LastBelow(int w) => sorted.FindLast(e => e.Weight < w);
        SvgFontEntry? result;
        if (weight is >= 400 and <= 500)
        {
            result = FirstAbove(weight, 500) ?? LastBelow(weight) ?? FirstAbove(500, int.MaxValue);
        }
        else if (weight < 400)
        {
            result = LastBelow(weight) ?? FirstAbove(weight, int.MaxValue);
        }
        else
        {
            result = FirstAbove(weight, int.MaxValue) ?? LastBelow(weight);
        }

        return result ?? sorted[0];
    }
}
