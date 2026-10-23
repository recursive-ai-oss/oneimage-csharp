namespace OneImage.Text.Fonts;

/// <summary>
/// Layered color glyphs from the <c>COLR</c> (version 0 records) and <c>CPAL</c> tables. A base glyph maps to a run of layer
/// glyphs painted bottom to top, each with a palette entry or the foreground color. Version 1 paint graphs are not read: a glyph
/// that only has one draws as its plain outline. Damaged tables are ignored (the font then has no color glyphs), like damaged layout tables.
/// </summary>
internal sealed partial class ColorGlyphs
{
    /// <summary>Palette index meaning "use the text color" (CPAL/COLR specification).</summary>
    public const int ForegroundIndex = 0xFFFF;

    private readonly ReadOnlyMemory<byte> _colr;
    private readonly ReadOnlyMemory<byte> _cpal;
    private readonly int _baseCount;
    private readonly int _baseOffset;
    private readonly int _layerCount;
    private readonly int _layerOffset;
    private readonly int _paletteEntries;
    private readonly int _paletteCount;
    private readonly int _colorOffset;
    private readonly int _colorCount;

    private ColorGlyphs(ReadOnlyMemory<byte> colr, ReadOnlyMemory<byte> cpal)
    {
        _colr = colr;
        _cpal = cpal;
        ReadOnlySpan<byte> c = colr.Span;
        _baseCount = Be.U16(c, 2);
        _baseOffset = Offset(Be.U32(c, 4), 6L * _baseCount, c.Length);
        _layerOffset = Offset(Be.U32(c, 8), 4L * Be.U16(c, 12), c.Length);
        _layerCount = Be.U16(c, 12);

        ReadGraphHeader(c);

        ReadOnlySpan<byte> p = cpal.Span;
        _paletteEntries = Be.U16(p, 2);
        _paletteCount = Be.U16(p, 4);
        _colorCount = Be.U16(p, 6);
        _colorOffset = Offset(Be.U32(p, 8), 4L * _colorCount, p.Length);
        if (_paletteCount == 0 || 12L + (2L * _paletteCount) > p.Length)
        {
            throw Be.Truncated();
        }

        for (int i = 0; i < _paletteCount; i++)
        {
            // Each palette is a run of entries inside the color record array.
            if ((long)Be.U16(p, 12 + (2 * i)) + _paletteEntries > _colorCount)
            {
                throw Be.Truncated();
            }
        }
    }

    /// <summary>Whether the table is version 1 and has a base glyph list of paint graphs.</summary>
    public bool HasPaintGraphs => _paintGraphCount > 0;

    /// <summary>Number of palettes in <c>CPAL</c>; at least 1.</summary>
    public int PaletteCount => _paletteCount;

    /// <summary>Reads the tables; null when either is missing, a version other than 0 or 1, or damaged.</summary>
    public static ColorGlyphs? TryCreate(ReadOnlyMemory<byte> colr, ReadOnlyMemory<byte> cpal)
    {
        if (colr.Length < 14 || cpal.Length < 12)
        {
            return null;
        }

        try
        {
            if (Be.U16(colr.Span, 0) > 1 || Be.U16(cpal.Span, 0) > 1)
            {
                return null;
            }

            var g = new ColorGlyphs(colr, cpal);
            return g._baseCount == 0 && !g.HasPaintGraphs ? null : g;
        }
        catch (FontFormatException)
        {
            return null;
        }
    }

    private static int Offset(uint offset, long size, int tableLength)
    {
        if (offset > tableLength || size > tableLength - offset)
        {
            throw Be.Truncated();
        }

        return (int)offset;
    }

    /// <summary>Finds the layers of a base glyph (binary search over the sorted base glyph records).</summary>
    /// <param name="glyph">The base glyph id.</param>
    /// <param name="first">Index of the first layer record.</param>
    /// <param name="count">Number of layers; 0 when the glyph is not a version 0 color glyph.</param>
    public bool TryGetLayers(int glyph, out int first, out int count)
    {
        ReadOnlySpan<byte> c = _colr.Span;
        int lo = 0;
        int hi = _baseCount - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >>> 1;
            int at = _baseOffset + (6 * mid);
            int id = Be.U16(c, at);
            if (id == glyph)
            {
                first = Be.U16(c, at + 2);
                count = Be.U16(c, at + 4);
                if (count > 0 && first + count <= _layerCount)
                {
                    return true;
                }

                break;
            }

            if (id < glyph)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        first = 0;
        count = 0;
        return false;
    }

    /// <summary>One layer record: the layer's glyph and its palette index (<see cref="ForegroundIndex"/> for the text color).</summary>
    public (int Glyph, int PaletteIndex) GetLayer(int index)
    {
        ReadOnlySpan<byte> c = _colr.Span;
        int at = _layerOffset + (4 * index);
        return (Be.U16(c, at), Be.U16(c, at + 2));
    }

    /// <summary>
    /// The color of a palette entry as straight RGBA, or null when the entry does not exist. A palette number past the last
    /// palette selects palette 0.
    /// </summary>
    public Rgba32? GetColor(int palette, int entry)
    {
        if ((uint)palette >= (uint)_paletteCount)
        {
            palette = 0;
        }

        if ((uint)entry >= (uint)_paletteEntries)
        {
            return null;
        }

        ReadOnlySpan<byte> p = _cpal.Span;
        int record = Be.U16(p, 12 + (2 * palette)) + entry;
        int at = _colorOffset + (4 * record);

        // Color records are stored blue, green, red, alpha.
        return new Rgba32(Be.U8(p, at + 2), Be.U8(p, at + 1), Be.U8(p, at), Be.U8(p, at + 3));
    }
}
