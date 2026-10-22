namespace OneImage.Text.Fonts;

/// <summary>
/// Vertical metrics from <c>vhea</c> and <c>vmtx</c> (advance heights, top side bearings) and the vertical origins of <c>VORG</c>.
/// A damaged or partial set is ignored: the font then has no vertical metrics and callers use the fallbacks.
/// </summary>
internal sealed class VerticalMetrics
{
    private readonly ReadOnlyMemory<byte> _vmtx;
    private readonly int _numLongMetrics;
    private readonly ReadOnlyMemory<byte> _vorg;
    private readonly int _vorgCount;
    private readonly int _defaultOrigin;

    private VerticalMetrics(ReadOnlyMemory<byte> vmtx, int numLongMetrics, ReadOnlyMemory<byte> vorg, int vorgCount, int defaultOrigin)
    {
        _vmtx = vmtx;
        _numLongMetrics = numLongMetrics;
        _vorg = vorg;
        _vorgCount = vorgCount;
        _defaultOrigin = defaultOrigin;
    }

    /// <summary>Whether a <c>VORG</c> table gives the vertical origins.</summary>
    public bool HasOrigins => _vorgCount >= 0;

    /// <summary>Reads the tables; null when <c>vhea</c> or <c>vmtx</c> is missing or too short for what it declares.</summary>
    public static VerticalMetrics? TryCreate(Dictionary<uint, ReadOnlyMemory<byte>> tables, int glyphCount)
    {
        if (!tables.TryGetValue(Be.Tag("vhea"), out ReadOnlyMemory<byte> vhea) || vhea.Length < 36
            || !tables.TryGetValue(Be.Tag("vmtx"), out ReadOnlyMemory<byte> vmtx))
        {
            return null;
        }

        int n = Be.U16(vhea.Span, 34);
        if (n == 0 || 4L * n > vmtx.Length)
        {
            return null;
        }

        ReadOnlyMemory<byte> vorg = default;
        int count = -1;
        int def = 0;
        if (tables.TryGetValue(Be.Tag("VORG"), out ReadOnlyMemory<byte> v) && v.Length >= 8 && Be.U16(v.Span, 0) == 1)
        {
            int c = Be.U16(v.Span, 6);
            if (8L + (4L * c) <= v.Length)
            {
                vorg = v;
                count = c;
                def = Be.I16(v.Span, 4);
            }
        }

        return new VerticalMetrics(vmtx, n, vorg, count, def);
    }

    /// <summary>The advance height of a glyph in font units; glyphs past the long metrics share the last one.</summary>
    public int GetAdvance(int glyph)
    {
        int i = Math.Min(Math.Max(glyph, 0), _numLongMetrics - 1);
        return Be.U16(_vmtx.Span, 4 * i);
    }

    /// <summary>The top side bearing of a glyph in font units, or 0 when the table is too short.</summary>
    public int GetTopSideBearing(int glyph)
    {
        ReadOnlySpan<byte> m = _vmtx.Span;
        if (glyph < _numLongMetrics)
        {
            return Be.I16(m, (4 * Math.Max(glyph, 0)) + 2);
        }

        int o = (4 * _numLongMetrics) + (2 * (glyph - _numLongMetrics));
        return o + 2 <= m.Length ? Be.I16(m, o) : 0;
    }

    /// <summary>
    /// The distance from the top of a glyph's vertical cell down to its baseline, in font units: the <c>VORG</c> entry or default
    /// when the font has the table, else the top side bearing plus the glyph's top (<paramref name="yMax"/>), as the OpenType
    /// specification describes.
    /// </summary>
    public int GetOriginY(int glyph, int yMax)
    {
        if (_vorgCount < 0)
        {
            return GetTopSideBearing(glyph) + yMax;
        }

        ReadOnlySpan<byte> v = _vorg.Span;
        int lo = 0;
        int hi = _vorgCount - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >>> 1;
            int id = Be.U16(v, 8 + (4 * mid));
            if (id == glyph)
            {
                return Be.I16(v, 8 + (4 * mid) + 2);
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

        return _defaultOrigin;
    }
}
