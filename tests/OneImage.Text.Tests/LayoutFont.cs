using System.Buffers.Binary;
using OneImage.Text.Fonts;

namespace OneImage.Text.Tests;

/// <summary>Big-endian byte writer for hand-assembled font tables.</summary>
internal sealed class Bytes
{
    private readonly List<byte> _b = [];

    public int Length => _b.Count;

    public Bytes U16(params int[] values)
    {
        foreach (int v in values)
        {
            _b.Add((byte)(v >> 8));
            _b.Add((byte)v);
        }

        return this;
    }

    public Bytes U32(uint v)
    {
        U16((int)(v >> 16), (int)(v & 0xFFFF));
        return this;
    }

    public Bytes Add(byte[] bytes)
    {
        _b.AddRange(bytes);
        return this;
    }

    public byte[] ToArray() => [.. _b];

    public static implicit operator byte[](Bytes b) => b.ToArray();
}

/// <summary>One lookup of a test GSUB/GPOS table.</summary>
internal sealed class LookupSpec
{
    public LookupSpec(int type, int flags, params byte[][] subtables)
    {
        Type = type;
        Flags = flags;
        Subtables = subtables;
    }

    public int Type { get; }

    public int Flags { get; }

    public byte[][] Subtables { get; }

    /// <summary>Mark filtering set index, used when the flags carry the UseMarkFilteringSet bit.</summary>
    public int MarkSet { get; init; }

    /// <summary>Store every subtable behind an extension subtable.</summary>
    public bool Extension { get; init; }
}

/// <summary>A feature of a test table and the lookups it enables.</summary>
internal readonly record struct FeatureSpec(string Tag, params int[] Lookups);

/// <summary>Assembles GSUB, GPOS and GDEF tables and small TrueType fonts around them, so lookup behavior can be tested with exact bytes.</summary>
internal static class LayoutFont
{
    public static byte[] Coverage(params int[] glyphs) => new Bytes().U16(1, glyphs.Length).U16(glyphs);

    public static byte[] CoverageRanges(params (int Start, int End)[] ranges)
    {
        var b = new Bytes().U16(2, ranges.Length);
        int index = 0;
        foreach ((int s, int e) in ranges)
        {
            b.U16(s, e, index);
            index += e - s + 1;
        }

        return b;
    }

    /// <summary>A class definition (format 2) giving each listed glyph range a class.</summary>
    public static byte[] ClassDef(params (int Start, int End, int Class)[] ranges)
    {
        var b = new Bytes().U16(2, ranges.Length);
        foreach ((int s, int e, int c) in ranges)
        {
            b.U16(s, e, c);
        }

        return b;
    }

    /// <summary>GDEF with glyph classes, optional mark attachment classes and optional mark glyph sets (each a list of glyphs).</summary>
    public static byte[] Gdef((int Start, int End, int Class)[] glyphClasses, (int Start, int End, int Class)[]? markAttach = null, int[][]? markSets = null)
    {
        byte[] classDef = ClassDef(glyphClasses);
        byte[]? attach = markAttach is null ? null : ClassDef(markAttach);
        var sets = markSets is null ? null : new Bytes();
        if (markSets is not null)
        {
            // MarkGlyphSetsTable: format, count, 32-bit offsets to coverages.
            byte[][] covs = [.. markSets.Select(s => Coverage(s))];
            sets!.U16(1, covs.Length);
            int off = 4 + (4 * covs.Length);
            foreach (byte[] c in covs)
            {
                sets.U32((uint)off);
                off += c.Length;
            }

            foreach (byte[] c in covs)
            {
                sets.Add(c);
            }
        }

        int header = markSets is null ? 12 : 14;
        var b = new Bytes().U16(1, markSets is null ? 0 : 2, header, 0, 0, attach is null ? 0 : header + classDef.Length);
        if (markSets is not null)
        {
            b.U16(header + classDef.Length + (attach?.Length ?? 0));
        }

        b.Add(classDef);
        if (attach is not null)
        {
            b.Add(attach);
        }

        if (sets is not null)
        {
            b.Add(sets);
        }

        return b;
    }

    /// <summary>
    /// A layout table with one script (DFLT, default language system, and optionally a named language system) enabling every
    /// feature given. Subtable-internal offsets are the caller's business.
    /// </summary>
    public static byte[] Table(bool gpos, FeatureSpec[] features, LookupSpec[] lookups, (string Language, int[] Features)? language = null)
    {
        // Script list
        int[] all = [.. Enumerable.Range(0, features.Length)];
        byte[] defaultLang = new Bytes().U16(0, 0xFFFF, all.Length).U16(all);
        byte[] namedLang = language is { } l ? new Bytes().U16(0, 0xFFFF, l.Features.Length).U16(l.Features) : [];
        int langRecords = language is null ? 0 : 1;
        int scriptHeader = 4 + (6 * langRecords);
        var script = new Bytes().U16(scriptHeader, langRecords);
        if (language is { } lang)
        {
            script.Add(Tag(lang.Language)).U16(scriptHeader + defaultLang.Length);
        }

        script.Add(defaultLang).Add(namedLang);
        byte[] scriptList = new Bytes().U16(1).Add(Tag("DFLT")).U16(8).Add(script);

        // Feature list
        var featureList = new Bytes().U16(features.Length);
        int offset = 2 + (6 * features.Length);
        var featureBodies = new List<byte[]>();
        foreach (FeatureSpec f in features)
        {
            byte[] body = new Bytes().U16(0, f.Lookups.Length).U16(f.Lookups);
            featureList.Add(Tag(f.Tag)).U16(offset);
            offset += body.Length;
            featureBodies.Add(body);
        }

        foreach (byte[] body in featureBodies)
        {
            featureList.Add(body);
        }

        // Lookup list
        var lookupList = new Bytes().U16(lookups.Length);
        offset = 2 + (2 * lookups.Length);
        var lookupBodies = new List<byte[]>();
        foreach (LookupSpec lk in lookups)
        {
            byte[] body = LookupBody(gpos, lk);
            lookupList.U16(offset);
            offset += body.Length;
            lookupBodies.Add(body);
        }

        foreach (byte[] body in lookupBodies)
        {
            lookupList.Add(body);
        }

        if (offset > 0xFFFF || featureList.Length > 0xFFFF)
        {
            throw new InvalidOperationException("The test table is too large for 16-bit offsets.");
        }

        int scriptOff = 10;
        int featureOff = scriptOff + scriptList.Length;
        int lookupOff = featureOff + featureList.Length;
        return new Bytes().U16(1, 0, scriptOff, featureOff, lookupOff).Add(scriptList).Add(featureList).Add(lookupList);
    }

    private static byte[] LookupBody(bool gpos, LookupSpec lk)
    {
        bool markSet = (lk.Flags & 0x10) != 0;
        int header = 6 + (2 * lk.Subtables.Length) + (markSet ? 2 : 0);
        var b = new Bytes();
        if (lk.Extension)
        {
            int extType = gpos ? 9 : 7;
            b.U16(extType, lk.Flags, lk.Subtables.Length);
            int off = header;
            var tail = new List<byte[]>();
            foreach (byte[] sub in lk.Subtables)
            {
                b.U16(off);
                off += 8;
            }

            if (markSet)
            {
                b.U16(lk.MarkSet);
            }

            // Extension subtables first, then the subtables they point at.
            int target = header + (8 * lk.Subtables.Length);
            foreach (byte[] sub in lk.Subtables)
            {
                int here = b.Length;
                b.U16(1, lk.Type).U32((uint)(target - here));
                tail.Add(sub);
                target += sub.Length;
            }

            foreach (byte[] sub in tail)
            {
                b.Add(sub);
            }

            return b;
        }

        b.U16(lk.Type, lk.Flags, lk.Subtables.Length);
        int pos = header;
        foreach (byte[] sub in lk.Subtables)
        {
            b.U16(pos);
            pos += sub.Length;
        }

        if (markSet)
        {
            b.U16(lk.MarkSet);
        }

        foreach (byte[] sub in lk.Subtables)
        {
            b.Add(sub);
        }

        return b;
    }

    public static byte[] Tag(string tag) => [(byte)tag[0], (byte)tag[1], (byte)tag[2], (byte)tag[3]];

    /// <summary>
    /// A TrueType font of <paramref name="advances"/>.Length glyphs (all empty outlines). Character 'A'+i maps to glyph i+1, so glyph
    /// ids can be written as letters.
    /// </summary>
    public static byte[] Font(int[] advances, byte[]? gdef = null, byte[]? gsub = null, byte[]? gpos = null, byte[]? kern = null)
    {
        int n = advances.Length;
        byte[] head = new byte[54];
        BinaryPrimitives.WriteUInt16BigEndian(head.AsSpan(18), 1000);
        BinaryPrimitives.WriteInt16BigEndian(head.AsSpan(50), 1);
        byte[] hhea = new byte[36];
        BinaryPrimitives.WriteInt16BigEndian(hhea.AsSpan(4), 800);
        BinaryPrimitives.WriteInt16BigEndian(hhea.AsSpan(6), -200);
        BinaryPrimitives.WriteUInt16BigEndian(hhea.AsSpan(34), (ushort)n);
        byte[] maxp = new byte[6];
        BinaryPrimitives.WriteUInt16BigEndian(maxp.AsSpan(4), (ushort)n);
        byte[] hmtx = new byte[4 * n];
        for (int i = 0; i < n; i++)
        {
            BinaryPrimitives.WriteUInt16BigEndian(hmtx.AsSpan(4 * i), (ushort)advances[i]);
        }

        // cmap format 6: codes 'A'.. map to glyphs 1..n-1.
        int entries = n - 1;
        var cmap = new Bytes().U16(0, 1, 3, 1).U32(12).U16(6, 10 + (2 * entries), 0, 0x41, entries);
        for (int i = 1; i < n; i++)
        {
            cmap.U16(i);
        }

        byte[] loca = new byte[4 * (n + 1)];
        var tables = new List<(string Tag, byte[] Data)>
        {
            ("head", head), ("hhea", hhea), ("maxp", maxp), ("hmtx", hmtx), ("cmap", cmap), ("glyf", new byte[4]), ("loca", loca),
        };
        if (gdef is not null)
        {
            tables.Add(("GDEF", gdef));
        }

        if (gsub is not null)
        {
            tables.Add(("GSUB", gsub));
        }

        if (gpos is not null)
        {
            tables.Add(("GPOS", gpos));
        }

        if (kern is not null)
        {
            tables.Add(("kern", kern));
        }

        tables.Sort((a, b) => string.CompareOrdinal(a.Tag, b.Tag));
        var ms = new MemoryStream();
        Span<byte> w = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(w, 0x00010000);
        ms.Write(w);
        BinaryPrimitives.WriteUInt16BigEndian(w, (ushort)tables.Count);
        ms.Write(w[..2]);
        ms.Write(new byte[6]);
        int offset = 12 + (16 * tables.Count);
        foreach ((string tag, byte[] data) in tables)
        {
            ms.Write(Tag(tag));
            ms.Write(new byte[4]);
            BinaryPrimitives.WriteUInt32BigEndian(w, (uint)offset);
            ms.Write(w);
            BinaryPrimitives.WriteUInt32BigEndian(w, (uint)data.Length);
            ms.Write(w);
            offset += (data.Length + 3) & ~3;
        }

        foreach ((_, byte[] data) in tables)
        {
            ms.Write(data);
            ms.Write(new byte[((data.Length + 3) & ~3) - data.Length]);
        }

        return ms.ToArray();
    }

    public static FontFace Face(byte[] font) => FontFile.Parse(font)[0];
}

/// <summary>
/// A table under construction whose 16-bit offsets point at other blobs. <see cref="Build"/> lays the referenced blobs out after this
/// one (each once, however often it is referenced) and fills in the offsets, measured from the start of the blob holding the field.
/// </summary>
internal sealed class Blob
{
    private readonly List<byte> _data = [];
    private readonly List<(int Position, Blob Target)> _refs = [];

    public Blob U16(params int[] values)
    {
        foreach (int v in values)
        {
            _data.Add((byte)(v >> 8));
            _data.Add((byte)v);
        }

        return this;
    }

    /// <summary>Writes a 16-bit offset to <paramref name="target"/>, or a null offset when it is null.</summary>
    public Blob Ref(Blob? target)
    {
        if (target is not null)
        {
            _refs.Add((_data.Count, target));
        }

        _data.Add(0);
        _data.Add(0);
        return this;
    }

    public byte[] Build()
    {
        var buf = new List<byte>(_data);
        var placed = new Dictionary<Blob, int>(ReferenceEqualityComparer.Instance);
        foreach ((int pos, Blob target) in _refs)
        {
            if (!placed.TryGetValue(target, out int off))
            {
                off = buf.Count;
                buf.AddRange(target.Build());
                placed[target] = off;
            }

            buf[pos] = (byte)(off >> 8);
            buf[pos + 1] = (byte)off;
        }

        return [.. buf];
    }

    public static Blob Cov(params int[] glyphs) => new Blob().U16(1, glyphs.Length).U16(glyphs);

    public static Blob Classes(params (int Start, int End, int Class)[] ranges)
    {
        var b = new Blob().U16(2, ranges.Length);
        foreach ((int s, int e, int c) in ranges)
        {
            b.U16(s, e, c);
        }

        return b;
    }

    public static Blob Anchor(int x, int y) => new Blob().U16(1, x, y);
}
