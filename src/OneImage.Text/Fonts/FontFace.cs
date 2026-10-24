using System.Collections.Concurrent;
using OneFontFace = global::OneFont.FontFace;
using OneFontOutline = global::OneFont.GlyphOutline;

namespace OneImage.Text.Fonts;

/// <summary>
/// One face as the layout needs it: a OneFont face (tables, character map, metrics, unhinted outlines, shaping) together with what this
/// package adds on top, which is vertical metrics, color glyph tables, TrueType hinting and the outline form the drawing code takes.
/// Thread-safe.
/// </summary>
internal sealed class FontFace
{
    private const int MaxCachedOutlines = 8192;
    private const int MaxCachedHinters = 16;

    private readonly ConcurrentDictionary<int, Outline> _outlines = new();
    private readonly ConcurrentDictionary<int, HinterSlot> _hinters = new();
    private readonly ConcurrentDictionary<(int Glyph, int Size), Outline> _hinted = new();
    private readonly object _glyfGate = new();
    private TrueTypeGlyphs? _glyf;
    private bool _glyfRead;

    public FontFace(OneFontFace face)
    {
        Inner = face;
        Vertical = VerticalMetrics.TryCreate(face, NumGlyphs);
        Color = ColorGlyphs.TryCreate(face.GetTable("COLR"), face.GetTable("CPAL"));

        // The OS/2 subscript and superscript recommendations: sizes are not in OneFont's API, so they are read from the table.
        ReadOnlySpan<byte> os2 = face.GetTable("OS/2").Span;
        if (os2.Length >= 78)
        {
            SubscriptXSize = Be.I16(os2, 10);
            SubscriptYSize = Be.I16(os2, 12);
            SubscriptYOffset = Be.I16(os2, 16);
            SuperscriptXSize = Be.I16(os2, 18);
            SuperscriptYSize = Be.I16(os2, 20);
            SuperscriptYOffset = Be.I16(os2, 24);
        }
    }

    /// <summary>The OneFont face.</summary>
    public OneFontFace Inner { get; }

    /// <summary>Vertical metrics (<c>vhea</c>, <c>vmtx</c>, <c>VORG</c>), or null when the font has none.</summary>
    public VerticalMetrics? Vertical { get; }

    /// <summary>The layered color glyphs (COLR version 0 records and CPAL), or null when the font has none or they are damaged.</summary>
    public ColorGlyphs? Color { get; }

    public int UnitsPerEm => Inner.UnitsPerEm;

    public int NumGlyphs => Inner.GlyphCount;

    public int WeightClass => Inner.Weight;

    public bool IsItalic => Inner.IsItalic;

    public bool IsBold => Inner.IsBold;

    public string FamilyName => Inner.FamilyName;

    public string SubfamilyName => Inner.SubfamilyName;

    public string? FullName => Inner.FullName;

    public int UnderlinePosition => Inner.Metrics.UnderlinePosition;

    public int UnderlineThickness => Inner.Metrics.UnderlineThickness;

    public int StrikeoutPosition => Inner.Metrics.StrikeoutPosition;

    public int StrikeoutSize => Inner.Metrics.StrikeoutSize;

    // OS/2 subscript and superscript recommendations, in font units (0 when the font has no OS/2 table).
    public int SubscriptXSize { get; }

    public int SubscriptYSize { get; }

    public int SubscriptYOffset { get; }

    public int SuperscriptXSize { get; }

    public int SuperscriptYSize { get; }

    public int SuperscriptYOffset { get; }

    // OneFont reads tables on demand and reports damage with its own exception type; callers of this package expect ours.
    public int GetGlyphIndex(int codePoint)
    {
        try
        {
            return Inner.GetGlyphIndex(codePoint);
        }
        catch (OneFont.FontFormatException ex)
        {
            throw new FontFormatException(ex.Message);
        }
    }

    /// <summary>Horizontal advance in font units; glyphs past the long metrics share the last one.</summary>
    public int GetAdvance(int glyph)
    {
        try
        {
            return Inner.GetAdvance(glyph);
        }
        catch (OneFont.FontFormatException ex)
        {
            throw new FontFormatException(ex.Message);
        }
    }

    /// <summary>The unhinted outline in font units. Cached; shared between callers, so treat as read-only.</summary>
    public Outline GetOutline(int glyph)
    {
        if (_outlines.TryGetValue(glyph, out Outline? o))
        {
            return o;
        }

        try
        {
            o = ToOutline(Inner.GetOutline(glyph));
        }
        catch (OneFont.FontFormatException ex)
        {
            throw new FontFormatException(ex.Message);
        }

        if (_outlines.Count >= MaxCachedOutlines)
        {
            _outlines.Clear();
        }

        _outlines[glyph] = o;
        return o;
    }

    private static Outline ToOutline(OneFontOutline source)
    {
        if (source.IsEmpty)
        {
            return Outline.Empty;
        }

        var b = new OutlineBuilder();
        source.Write(b);
        return b.Build();
    }

    private sealed class HinterSlot(TrueTypeHinter? hinter)
    {
        public readonly TrueTypeHinter? Hinter = hinter;
    }

    // The glyf reader is only needed for hinting (unhinted outlines come from OneFont), so it is set up on first use.
    private TrueTypeGlyphs? Glyf()
    {
        lock (_glyfGate)
        {
            if (!_glyfRead)
            {
                if (!Inner.IsCff)
                {
                    ReadOnlySpan<byte> head = Inner.GetTable("head").Span;
                    int locaFormat = head.Length >= 52 ? Be.I16(head, 50) : 0;
                    _glyf = new TrueTypeGlyphs(Inner.GetTable("glyf"), Inner.GetTable("loca"), locaFormat != 0, NumGlyphs);
                }

                _glyfRead = true;
            }

            return _glyf;
        }
    }

    /// <summary>
    /// The outline grid-fitted for a pixel size by the font's TrueType instructions (y positions only). Falls back to the unhinted
    /// outline for CFF fonts, fonts without programs, sizes outside 4 to 400 and glyphs whose program fails. Cached per size.
    /// </summary>
    public Outline GetHintedOutline(int glyph, float pixelSize)
    {
        if (Inner.IsCff || !(pixelSize >= 4 && pixelSize <= 400) || Glyf() is not { } glyf)
        {
            return GetOutline(glyph);
        }

        int sizeKey = BitConverter.SingleToInt32Bits(pixelSize);
        if (_hinted.TryGetValue((glyph, sizeKey), out Outline? cached))
        {
            return cached;
        }

        if (!_hinters.TryGetValue(sizeKey, out HinterSlot? slot))
        {
            slot = new HinterSlot(TrueTypeHinter.Create(UnitsPerEm, pixelSize, Inner.GetTable("fpgm"), Inner.GetTable("prep"), Inner.GetTable("cvt "), Inner.GetTable("maxp")));
            if (_hinters.Count >= MaxCachedHinters)
            {
                _hinters.Clear();
            }

            _hinters[sizeKey] = slot;
        }

        Outline o = slot.Hinter is null ? GetOutline(glyph) : glyf.Load(glyph, slot.Hinter);
        if (_hinted.Count >= MaxCachedOutlines)
        {
            _hinted.Clear();
        }

        _hinted[(glyph, sizeKey)] = o;
        return o;
    }
}
