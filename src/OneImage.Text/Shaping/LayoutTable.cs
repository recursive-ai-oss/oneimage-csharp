using OneImage.Text.Fonts;

namespace OneImage.Text.Shaping;

/// <summary>One lookup of a GSUB or GPOS table: its flags and the absolute offsets of its subtables (extension subtables resolved).</summary>
internal sealed class Lookup
{
    public Lookup(int flags, int markSet, int[] offsets, byte[] types)
    {
        Flags = flags;
        MarkSet = markSet;
        Offsets = offsets;
        Types = types;
    }

    public int Flags { get; }

    /// <summary>The mark glyph set index, valid when <see cref="Flags"/> has the UseMarkFilteringSet bit.</summary>
    public int MarkSet { get; }

    public int[] Offsets { get; }

    /// <summary>The lookup type of each subtable (the extension's wrapped type when wrapped); 0 for an unknown type.</summary>
    public byte[] Types { get; }

    public int Count => Offsets.Length;
}

/// <summary>A lookup chosen by the enabled features, with the value the user gave the feature (1 = on; alternates use it as the 1-based choice).</summary>
internal readonly record struct PlannedLookup(int Index, int Value);

/// <summary>
/// A GSUB or GPOS table: the script, feature and lookup lists of the OpenType layout common header (OpenType spec, "OpenType Layout
/// Common Table Formats"). Subtables are read in place; this class only walks the three lists.
/// </summary>
internal sealed class LayoutTable
{
    private readonly int _scriptList;
    private readonly int _featureList;
    private readonly int _lookupList;
    private readonly Lookup?[] _lookups;
    private readonly bool[] _parsed;

    private LayoutTable(ReadOnlyMemory<byte> data, bool isGpos, int scriptList, int featureList, int lookupList, int lookupCount)
    {
        Data = data;
        IsGpos = isGpos;
        _scriptList = scriptList;
        _featureList = featureList;
        _lookupList = lookupList;
        LookupCount = lookupCount;
        _lookups = new Lookup?[lookupCount];
        _parsed = new bool[lookupCount];
    }

    public ReadOnlyMemory<byte> Data { get; }

    public bool IsGpos { get; }

    public int LookupCount { get; }

    /// <summary>Reads the header; a table with an unusable header is ignored (null), as if the font had none.</summary>
    public static LayoutTable? Create(ReadOnlyMemory<byte> data, bool isGpos)
    {
        try
        {
            ReadOnlySpan<byte> d = data.Span;
            if (d.Length < 10 || Be.U16(d, 0) != 1)
            {
                return null;
            }

            int scriptList = Be.U16(d, 4);
            int featureList = Be.U16(d, 6);
            int lookupList = Be.U16(d, 8);
            if (scriptList == 0 || featureList == 0 || lookupList == 0)
            {
                return null;
            }

            int lookupCount = Be.U16(d, lookupList);
            if (lookupList + 2L + (2L * lookupCount) > d.Length)
            {
                return null;
            }

            // Touch the other two list headers so a table that is cut off before them is rejected at once.
            _ = Be.U16(d, scriptList);
            _ = Be.U16(d, featureList);
            return new LayoutTable(data, isGpos, scriptList, featureList, lookupList, lookupCount);
        }
        catch (FontFormatException)
        {
            return null;
        }
    }

    /// <summary>Parses (once) the lookup at <paramref name="index"/>; null when the index is out of range or the lookup is malformed.</summary>
    public Lookup? GetLookup(int index)
    {
        if ((uint)index >= (uint)LookupCount)
        {
            return null;
        }

        if (Volatile.Read(ref _parsed[index]))
        {
            return _lookups[index];
        }

        lock (_lookups)
        {
            if (!_parsed[index])
            {
                try
                {
                    _lookups[index] = ParseLookup(index);
                }
                catch (FontFormatException)
                {
                    // A lookup that cannot be read is skipped.
                }

                Volatile.Write(ref _parsed[index], true);
            }

            return _lookups[index];
        }
    }

    private Lookup ParseLookup(int index)
    {
        ReadOnlySpan<byte> d = Data.Span;
        int lookup = _lookupList + Be.U16(d, _lookupList + 2 + (2 * index));
        int type = Be.U16(d, lookup);
        int flags = Be.U16(d, lookup + 2);
        int count = Be.U16(d, lookup + 4);
        if (lookup + 6L + (2L * count) > d.Length)
        {
            throw Be.Truncated();
        }

        int markSet = (flags & 0x10) != 0 ? Be.U16(d, lookup + 6 + (2 * count)) : 0;
        int extension = IsGpos ? 9 : 7;
        int maxType = IsGpos ? 9 : 8;
        var offsets = new int[count];
        var types = new byte[count];
        for (int s = 0; s < count; s++)
        {
            int sub = lookup + Be.U16(d, lookup + 6 + (2 * s));
            int t = type;
            if (t == extension)
            {
                if (Be.U16(d, sub) != 1)
                {
                    continue;
                }

                t = Be.U16(d, sub + 2);
                long target = sub + (long)Be.U32(d, sub + 4);
                if (target >= d.Length)
                {
                    continue;
                }

                sub = (int)target;
                if (t == extension)
                {
                    continue; // an extension may not wrap another extension
                }
            }

            if (t < 1 || t > maxType)
            {
                continue;
            }

            offsets[s] = sub;
            types[s] = (byte)t;
        }

        return new Lookup(flags, markSet, offsets, types);
    }

    /// <summary>
    /// Finds the script table for the first of <paramref name="scripts"/> the font has, then the language system for
    /// <paramref name="language"/> (the script's default when the font has no such language), and returns the feature-list indices
    /// it enables, with the required feature (or -1) in <paramref name="required"/>. Null when no script matches.
    /// </summary>
    public List<int>? FeatureIndices(ReadOnlySpan<uint> scripts, uint language, out int required)
    {
        required = -1;
        ReadOnlySpan<byte> d = Data.Span;
        int script = -1;
        foreach (uint tag in scripts)
        {
            script = FindScript(d, tag);
            if (script >= 0)
            {
                break;
            }
        }

        if (script < 0)
        {
            return null;
        }

        int langSys = -1;
        if (language != 0)
        {
            int n = Be.U16(d, script + 2);
            if (script + 4L + (6L * n) > d.Length)
            {
                throw Be.Truncated();
            }

            for (int i = 0; i < n; i++)
            {
                if (Be.U32(d, script + 4 + (6 * i)) == language)
                {
                    langSys = script + Be.U16(d, script + 8 + (6 * i));
                    break;
                }
            }
        }

        if (langSys < 0)
        {
            int def = Be.U16(d, script);
            if (def == 0)
            {
                return null;
            }

            langSys = script + def;
        }

        var result = new List<int>();
        int req = Be.U16(d, langSys + 2);
        if (req != 0xFFFF)
        {
            required = req;
            result.Add(req);
        }

        int count = Be.U16(d, langSys + 4);
        if (langSys + 6L + (2L * count) > d.Length)
        {
            throw Be.Truncated();
        }

        for (int i = 0; i < count; i++)
        {
            result.Add(Be.U16(d, langSys + 6 + (2 * i)));
        }

        return result;
    }

    private int FindScript(ReadOnlySpan<byte> d, uint tag)
    {
        int n = Be.U16(d, _scriptList);
        if (_scriptList + 2L + (6L * n) > d.Length)
        {
            throw Be.Truncated();
        }

        for (int i = 0; i < n; i++)
        {
            if (Be.U32(d, _scriptList + 2 + (6 * i)) == tag)
            {
                return _scriptList + Be.U16(d, _scriptList + 6 + (6 * i));
            }
        }

        return -1;
    }

    /// <summary>The tag of feature record <paramref name="featureIndex"/>, or 0 when the index is out of range.</summary>
    public uint FeatureTag(int featureIndex)
    {
        ReadOnlySpan<byte> d = Data.Span;
        int n = Be.U16(d, _featureList);
        return featureIndex < n ? Be.U32(d, _featureList + 2 + (6 * featureIndex)) : 0;
    }

    /// <summary>Adds the lookup indices of a feature to <paramref name="into"/>.</summary>
    public void AddFeatureLookups(int featureIndex, int value, SortedDictionary<int, int> into)
    {
        ReadOnlySpan<byte> d = Data.Span;
        int n = Be.U16(d, _featureList);
        if (featureIndex >= n)
        {
            return;
        }

        int feature = _featureList + Be.U16(d, _featureList + 2 + (6 * featureIndex) + 4);
        int count = Be.U16(d, feature + 2);
        if (feature + 4L + (2L * count) > d.Length)
        {
            throw Be.Truncated();
        }

        for (int i = 0; i < count; i++)
        {
            int li = Be.U16(d, feature + 4 + (2 * i));
            if (li < LookupCount)
            {
                into[li] = into.TryGetValue(li, out int old) ? Math.Max(old, value) : value;
            }
        }
    }
}
