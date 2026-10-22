namespace OneImage.Text.Shaping;

/// <summary>One glyph of a run while it is being shaped.</summary>
internal struct GlyphInfo
{
    public int Glyph;

    /// <summary>The UTF-16 index of the first character this glyph stands for.</summary>
    public int Cluster;

    /// <summary>The first code point this glyph stands for.</summary>
    public int CodePoint;

    /// <summary>The GDEF class (1 base, 2 ligature, 3 mark, 4 component), or one derived from the character when the font has none.</summary>
    public byte Class;

    /// <summary>Nonzero for a ligature and for the marks that sat between its components; shared by all of them.</summary>
    public ushort LigId;

    /// <summary>For a mark inside a ligature: the 1-based component it belongs to; 0 otherwise.</summary>
    public byte LigComp;

    /// <summary>The per-glyph feature bits a script shaper sets (for example the Arabic joining form); a lookup planned with a mask applies only to glyphs sharing a bit.</summary>
    public uint Mask;

    /// <summary>The script shaper's category of the character (Indic and Universal Shaping Engine shapers); kept by every glyph a substitution derives from it.</summary>
    public byte Cat;

    /// <summary>The reordering class of the glyph inside its syllable (Indic and Universal Shaping Engine shapers).</summary>
    public byte Pos;

    /// <summary>Syllable-level flags (<see cref="GlyphFlags"/>); a ligature carries the union of its components' flags.</summary>
    public byte Flags;

    /// <summary>The syllable number the glyph belongs to; syllables are numbered along the run.</summary>
    public int Syllable;

    /// <summary>The glyph id before a feature stage ran, so the stage's effect can be told afterwards.</summary>
    public int Before;
}

/// <summary>Per-glyph flags a syllable shaper keeps on <see cref="GlyphInfo.Flags"/>.</summary>
internal static class GlyphFlags
{
    public const byte Base = 1;
    public const byte Reph = 2;
    public const byte Pref = 4;

    /// <summary>A sign above the base that moves next to the base after the basic forms have run.</summary>
    public const byte Above = 8;
}

/// <summary>The placement of one glyph in font units: how far it advances the pen and where it is drawn relative to the pen.</summary>
internal struct GlyphPos
{
    public int XAdvance;
    public int YAdvance;
    public int XOffset;
    public int YOffset;

    /// <summary>The glyph this one is attached to (a mark's base, a cursive predecessor), or -1.</summary>
    public int AttachTo;

    /// <summary>Anchor difference (target anchor minus own anchor) used to place an attached glyph.</summary>
    public int AttachX;
    public int AttachY;

    /// <summary>True for a cursive link, whose horizontal part is already folded into the advances.</summary>
    public bool Cursive;

    /// <summary>
    /// How far a right-to-left glyph's offset moves when the run is laid out in display order (see <see cref="Engine.ResolveAttachments"/>).
    /// </summary>
    public int RtlShift;
}

/// <summary>The glyph run a shaper works on: glyph records that substitutions edit in place, and parallel positions once positioning starts.</summary>
internal sealed class GlyphBuffer
{
    public GlyphInfo[] Info;
    public GlyphPos[] Pos = [];
    private int _count;

    public GlyphBuffer(int capacity)
    {
        Info = new GlyphInfo[Math.Max(capacity, 4)];
    }

    public int Count => _count;

    public void Add(in GlyphInfo g)
    {
        if (_count == Info.Length)
        {
            Array.Resize(ref Info, Info.Length * 2);
        }

        Info[_count++] = g;
    }

    /// <summary>Opens <paramref name="n"/> empty slots at <paramref name="index"/>.</summary>
    public void Insert(int index, int n)
    {
        if (_count + n > Info.Length)
        {
            Array.Resize(ref Info, Math.Max(Info.Length * 2, _count + n));
        }

        Array.Copy(Info, index, Info, index + n, _count - index);
        _count += n;
    }

    public void RemoveAt(int index)
    {
        Array.Copy(Info, index + 1, Info, index, _count - index - 1);
        _count--;
    }

    /// <summary>Allocates the position records once substitution is finished; every glyph starts unattached.</summary>
    public void AllocatePositions()
    {
        Pos = new GlyphPos[_count];
        for (int i = 0; i < _count; i++)
        {
            Pos[i].AttachTo = -1;
        }
    }
}
