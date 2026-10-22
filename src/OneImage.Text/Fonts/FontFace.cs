using System.Collections.Concurrent;

namespace OneImage.Text.Fonts;

/// <summary>One parsed face: header tables read eagerly and validated, outlines, cmap and kerning read on demand. Thread-safe.</summary>
internal sealed class FontFace
{
    private const int MaxCachedOutlines = 8192;
    private const int MaxCachedKerningPairs = 1 << 16;

    private readonly Dictionary<uint, ReadOnlyMemory<byte>> _tables;
    private readonly ReadOnlyMemory<byte> _hmtx;
    private readonly int _numHMetrics;
    private readonly CmapTable _cmap;
    private readonly TrueTypeGlyphs? _glyf;
    private readonly CffGlyphs? _cff;
    private readonly KernTable? _kern;
    private readonly GposKerning? _gpos;
    private readonly ConcurrentDictionary<int, Outline> _outlines = new();
    private readonly ConcurrentDictionary<uint, int> _kerning = new();

    private FontFace(Dictionary<uint, ReadOnlyMemory<byte>> tables)
    {
        _tables = tables;
        ReadOnlySpan<byte> head = Table("head", required: true).Span;
        if (head.Length < 54)
        {
            throw Be.Truncated();
        }

        UnitsPerEm = Be.U16(head, 18);
        if (UnitsPerEm < 16 || UnitsPerEm > 16384)
        {
            throw new FontFormatException("The font's units per em is out of range.");
        }

        XMin = Be.I16(head, 36);
        YMin = Be.I16(head, 38);
        XMax = Be.I16(head, 40);
        YMax = Be.I16(head, 42);
        int macStyle = Be.U16(head, 44);
        int locaFormat = Be.I16(head, 50);

        ReadOnlySpan<byte> hhea = Table("hhea", required: true).Span;
        if (hhea.Length < 36)
        {
            throw Be.Truncated();
        }

        Ascender = Be.I16(hhea, 4);
        Descender = Be.I16(hhea, 6);
        LineGap = Be.I16(hhea, 8);
        _numHMetrics = Be.U16(hhea, 34);

        ReadOnlySpan<byte> maxp = Table("maxp", required: true).Span;
        NumGlyphs = Be.U16(maxp, 4);

        _hmtx = Table("hmtx", required: true);
        _numHMetrics = Math.Min(_numHMetrics, _hmtx.Length / 4);
        if (_numHMetrics == 0)
        {
            throw new FontFormatException("The font has no horizontal metrics.");
        }

        _cmap = CmapTable.Create(Table("cmap", required: true)) ?? throw new FontFormatException("The font has no usable Unicode character map.");

        if (_tables.TryGetValue(Be.Tag("CFF "), out ReadOnlyMemory<byte> cff))
        {
            _cff = new CffGlyphs(cff);
            NumGlyphs = Math.Max(NumGlyphs, 0);
        }
        else if (_tables.ContainsKey(Be.Tag("CFF2")))
        {
            throw new FontFormatException("CFF2 outlines are not supported.");
        }
        else
        {
            _glyf = new TrueTypeGlyphs(Table("glyf", required: true), Table("loca", required: true), locaFormat != 0, NumGlyphs);
        }

        int fsSelection = 0;
        if (_tables.TryGetValue(Be.Tag("OS/2"), out ReadOnlyMemory<byte> os2m) && os2m.Length >= 78)
        {
            ReadOnlySpan<byte> os2 = os2m.Span;
            int version = Be.U16(os2, 0);
            WeightClass = Be.U16(os2, 4);
            StrikeoutSize = Be.I16(os2, 26);
            StrikeoutPosition = Be.I16(os2, 28);
            fsSelection = Be.U16(os2, 62);
            TypoAscender = Be.I16(os2, 68);
            TypoDescender = Be.I16(os2, 70);
            TypoLineGap = Be.I16(os2, 72);
            WinAscent = Be.U16(os2, 74);
            WinDescent = Be.U16(os2, 76);
            HasOs2Metrics = true;

            if (version >= 2 && os2m.Length >= 90)
            {
                XHeight = Be.I16(os2, 86);
                CapHeight = Be.I16(os2, 88);
            }
        }

        UseTypoMetrics = (fsSelection & 0x80) != 0;
        IsItalic = (fsSelection & 1) != 0 || (macStyle & 2) != 0;
        IsBold = (fsSelection & 0x20) != 0 || (macStyle & 1) != 0 || WeightClass >= 600;
        if (WeightClass == 0)
        {
            WeightClass = IsBold ? 700 : 400;
        }

        if (_tables.TryGetValue(Be.Tag("post"), out ReadOnlyMemory<byte> post) && post.Length >= 12)
        {
            UnderlinePosition = Be.I16(post.Span, 8);
            UnderlineThickness = Be.I16(post.Span, 10);
        }

        ReadOnlyMemory<byte> name = Table("name", required: false);
        string? family = NameTable.Get(name, NameTable.TypographicFamily) ?? NameTable.Get(name, NameTable.Family);
        string? sub = NameTable.Get(name, NameTable.TypographicSubfamily) ?? NameTable.Get(name, NameTable.Subfamily);
        FullName = NameTable.Get(name, NameTable.FullName);
        PostScriptName = NameTable.Get(name, NameTable.PostScriptName);
        FamilyName = family ?? FullName ?? PostScriptName ?? "Unknown";
        SubfamilyName = sub ?? (IsBold ? (IsItalic ? "Bold Italic" : "Bold") : (IsItalic ? "Italic" : "Regular"));

        if (_tables.TryGetValue(Be.Tag("GPOS"), out ReadOnlyMemory<byte> gpos))
        {
            _gpos = GposKerning.Create(gpos);
        }

        if (_gpos is null && _tables.TryGetValue(Be.Tag("kern"), out ReadOnlyMemory<byte> kern))
        {
            _kern = KernTable.Create(kern);
        }
    }

    public int UnitsPerEm { get; }

    public int NumGlyphs { get; }

    public int XMin { get; }

    public int YMin { get; }

    public int XMax { get; }

    public int YMax { get; }

    public int Ascender { get; }

    public int Descender { get; }

    public int LineGap { get; }

    public int TypoAscender { get; }

    public int TypoDescender { get; }

    public int TypoLineGap { get; }

    public int WinAscent { get; }

    public int WinDescent { get; }

    public bool HasOs2Metrics { get; }

    public bool UseTypoMetrics { get; }

    public int WeightClass { get; private set; }

    public bool IsItalic { get; }

    public bool IsBold { get; }

    public int UnderlinePosition { get; }

    public int UnderlineThickness { get; }

    public int StrikeoutPosition { get; }

    public int StrikeoutSize { get; }

    public int XHeight { get; }

    public int CapHeight { get; }

    public string FamilyName { get; }

    public string SubfamilyName { get; }

    public string? FullName { get; }

    public string? PostScriptName { get; }

    public bool IsCff => _cff is not null;

    public static FontFace Create(Dictionary<uint, ReadOnlyMemory<byte>> tables) => new(tables);

    /// <summary>The glyph for a code point; 0 (.notdef) when the font lacks it.</summary>
    public int GetGlyphIndex(int codePoint) => _cmap.Lookup(codePoint);

    /// <summary>Advance width in font units.</summary>
    public int GetAdvance(int glyph)
    {
        int i = Math.Min(Math.Max(glyph, 0), _numHMetrics - 1);
        return Be.U16(_hmtx.Span, 4 * i);
    }

    /// <summary>Left side bearing in font units.</summary>
    public int GetLeftSideBearing(int glyph)
    {
        ReadOnlySpan<byte> h = _hmtx.Span;
        if (glyph < _numHMetrics)
        {
            return Be.I16(h, (4 * Math.Max(glyph, 0)) + 2);
        }

        int o = (4 * _numHMetrics) + (2 * (glyph - _numHMetrics));
        return o + 2 <= h.Length ? Be.I16(h, o) : 0;
    }

    /// <summary>The unhinted outline in font units, y up. Cached; shared between callers, so treat as read-only.</summary>
    public Outline GetOutline(int glyph)
    {
        if (_outlines.TryGetValue(glyph, out Outline? o))
        {
            return o;
        }

        o = _cff is not null ? _cff.Load(glyph) : _glyf!.Load(glyph);
        if (_outlines.Count >= MaxCachedOutlines)
        {
            _outlines.Clear();
        }

        _outlines[glyph] = o;
        return o;
    }

    /// <summary>Kerning between two glyphs: the change to the first glyph's advance, in font units.</summary>
    public int GetKerning(int left, int right)
    {
        if (_gpos is null && _kern is null)
        {
            return 0;
        }

        // Pair lookups walk coverage tables and class definitions; text repeats the same pairs, so remember them.
        uint key = ((uint)left << 16) | (uint)(right & 0xFFFF);
        if (_kerning.TryGetValue(key, out int cached))
        {
            return cached;
        }

        int value = _gpos is not null ? _gpos.Get(left, right) : _kern!.Get(left, right);
        if (_kerning.Count >= MaxCachedKerningPairs)
        {
            _kerning.Clear();
        }

        _kerning[key] = value;
        return value;
    }

    private ReadOnlyMemory<byte> Table(string tag, bool required)
    {
        if (_tables.TryGetValue(Be.Tag(tag), out ReadOnlyMemory<byte> t))
        {
            return t;
        }

        if (required)
        {
            throw new FontFormatException($"The font has no '{tag}' table.");
        }

        return default;
    }
}
