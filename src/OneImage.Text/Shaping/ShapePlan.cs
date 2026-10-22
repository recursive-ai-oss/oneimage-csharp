using System.Collections.Concurrent;
using OneImage.Text.Fonts;

namespace OneImage.Text.Shaping;

/// <summary>
/// The feature settings of one request, resolved: the tags the user turned on (with a value) or off, later entries replacing earlier
/// ones. Anything not listed keeps the script shaper's default. Immutable, with value equality so plans can be cached by it.
/// </summary>
internal sealed class FeatureSet : IEquatable<FeatureSet>
{
    public static readonly FeatureSet Empty = new([]);

    private readonly (uint Tag, int Value)[] _items; // sorted by tag, one entry per tag
    private readonly int _hash;

    private FeatureSet((uint Tag, int Value)[] items)
    {
        _items = items;
        var h = new HashCode();
        foreach ((uint t, int v) in items)
        {
            h.Add(t);
            h.Add(v);
        }

        _hash = h.ToHashCode();
    }

    /// <summary>Builds a set from settings in order; a repeated tag takes its last value.</summary>
    public static FeatureSet Create(IEnumerable<(uint Tag, int Value)> settings)
    {
        var map = new SortedDictionary<uint, int>();
        foreach ((uint tag, int value) in settings)
        {
            map[tag] = value;
        }

        if (map.Count == 0)
        {
            return Empty;
        }

        var items = new (uint, int)[map.Count];
        int i = 0;
        foreach (KeyValuePair<uint, int> kv in map)
        {
            items[i++] = (kv.Key, kv.Value);
        }

        return new FeatureSet(items);
    }

    public bool TryGet(uint tag, out int value)
    {
        foreach ((uint t, int v) in _items)
        {
            if (t == tag)
            {
                value = v;
                return true;
            }
        }

        value = 0;
        return false;
    }

    public bool Equals(FeatureSet? other) => other is not null && _items.AsSpan().SequenceEqual(other._items);

    public override bool Equals(object? obj) => obj is FeatureSet f && Equals(f);

    public override int GetHashCode() => _hash;
}

/// <summary>
/// A script-specific shaping strategy. The generic shaper covers the simple scripts (Latin, Greek, Cyrillic and everything else
/// without contextual joining or reordering). An Arabic, Indic or Universal Shaping Engine shaper overrides the feature stages and
/// the buffer hooks instead of changing the engine.
/// </summary>
internal abstract class ScriptShaper
{
    /// <summary>The GSUB features applied in order, one list per stage; a stage finishes before the next begins. Enabled features outside every list run in the last stage.</summary>
    public abstract uint[][] GsubStages { get; }

    /// <summary>
    /// True for shapers whose lookups must see zero width joiners and non-joiners as glyphs that never match through (Indic, Universal
    /// Shaping Engine): the joiners are taken out of the run once substitution is done.
    /// </summary>
    public virtual bool KeepsJoiners => false;

    /// <summary>Whether the feature is on unless the user says otherwise.</summary>
    public abstract bool DefaultOn(uint tag);

    /// <summary>Whether the feature is on unless the user says otherwise, for text set in the given direction. Right-to-left runs also turn on rtlm.</summary>
    public virtual bool DefaultOn(uint tag, bool rtl) => DefaultOn(tag) || (rtl && tag == Tag.Rtlm);

    /// <summary>
    /// The glyph mask bits a feature is restricted to, or zero when it applies to every glyph. A feature with a mask only runs on
    /// glyphs whose <see cref="GlyphInfo.Mask"/> shares a bit with it.
    /// </summary>
    public virtual uint FeatureMask(uint tag) => 0;

    /// <summary>Called on the glyph run before any lookup runs (reordering, mask setup). The generic shaper does nothing.</summary>
    public virtual void PrepareRun(GlyphBuffer buffer)
    {
    }

    /// <summary>
    /// Builds the glyph run for the characters when the shaper needs to decompose, insert or regroup them before the font's
    /// character map is consulted (split vowel signs, dotted circles, syllables). Returns null to use the plain one-glyph-per-character run.
    /// </summary>
    public virtual GlyphBuffer? BuildRun(FontFace face, ReadOnlySpan<int> codePoints, ReadOnlySpan<int> clusters) => null;

    /// <summary>True for shapers that reorder glyphs between stages and so need the <see cref="ShapeContext"/> hooks (<see cref="Prepare"/>, <see cref="AfterStage"/>).</summary>
    public virtual bool Reorders => false;

    /// <summary>Called on the glyph run before any lookup runs, with the plan in hand; the default forwards to <see cref="PrepareRun(GlyphBuffer)"/>.</summary>
    public virtual void Prepare(ShapeContext context) => PrepareRun(context.Buffer);

    /// <summary>Called after the GSUB stage with the given index has run (also when the font has no lookups for it), so reordering can happen between stages.</summary>
    public virtual void AfterStage(int stage, ShapeContext context)
    {
    }

    /// <summary>The OpenType script tags to try, best first.</summary>
    public virtual uint[] ScriptTags(uint script) => [script, Tag.Dflt, Tag.DfltLower, Tag.Latn];
}

/// <summary>What a script shaper needs to see while it works on a run: the font, the plan and the glyphs.</summary>
internal sealed class ShapeContext(FontFace face, OtFace layout, ShapePlan plan, GlyphBuffer buffer)
{
    public FontFace Face { get; } = face;

    public OtFace Layout { get; } = layout;

    public ShapePlan Plan { get; } = plan;

    public GlyphBuffer Buffer { get; } = buffer;
}

internal static class Tag
{
    public static readonly uint Latn = Be.Tag("latn");
    public static readonly uint Grek = Be.Tag("grek");
    public static readonly uint Cyrl = Be.Tag("cyrl");
    public static readonly uint Dflt = Be.Tag("DFLT");
    public static readonly uint DfltLower = Be.Tag("dflt");
    public static readonly uint Kern = Be.Tag("kern");
    public static readonly uint Mark = Be.Tag("mark");
    public static readonly uint Mkmk = Be.Tag("mkmk");
    public static readonly uint Curs = Be.Tag("curs");
    public static readonly uint Ccmp = Be.Tag("ccmp");
    public static readonly uint Locl = Be.Tag("locl");
    public static readonly uint Rlig = Be.Tag("rlig");
    public static readonly uint Liga = Be.Tag("liga");
    public static readonly uint Clig = Be.Tag("clig");
    public static readonly uint Calt = Be.Tag("calt");
    public static readonly uint Rclt = Be.Tag("rclt");
    public static readonly uint Rtlm = Be.Tag("rtlm");
    public static readonly uint Arab = Be.Tag("arab");
    public static readonly uint Hebr = Be.Tag("hebr");
    public static readonly uint Syrc = Be.Tag("syrc");
    public static readonly uint Nko = Be.Tag("nko ");
}

/// <summary>Horizontal left-to-right text in the simple scripts: ccmp and locl first, then the other default substitutions, then positioning.</summary>
internal sealed class GenericShaper : ScriptShaper
{
    public static readonly GenericShaper Instance = new();

    private static readonly uint[][] Stages =
    [
        [Tag.Ccmp, Tag.Locl, Tag.Rtlm],
        [Tag.Rlig, Tag.Liga, Tag.Clig, Tag.Calt, Tag.Rclt],
    ];

    public override uint[][] GsubStages => Stages;

    public override bool DefaultOn(uint tag) =>
        tag == Tag.Ccmp || tag == Tag.Locl || tag == Tag.Rlig || tag == Tag.Liga || tag == Tag.Clig || tag == Tag.Calt
        || tag == Tag.Rclt || tag == Tag.Kern || tag == Tag.Mark || tag == Tag.Mkmk || tag == Tag.Curs;
}

/// <summary>The lookups a request runs, in order: GSUB lookups per stage and the GPOS lookups.</summary>
internal sealed class ShapePlan
{
    public PlannedLookup[][] Gsub { get; private init; } = [];

    public PlannedLookup[] Gpos { get; private init; } = [];

    /// <summary>Answers a syllable shaper's "would this feature change these glyphs" questions, remembered per plan.</summary>
    public ConcurrentDictionary<long, bool> Probes { get; } = new();

    /// <summary>Whether a GPOS lookup came from the kern feature, so the legacy kern table must stay out of the way.</summary>
    public bool HasGposKern { get; private init; }

    /// <summary>Whether the font has rtlm lookups for this script, in which case mirrored characters are left to them.</summary>
    public bool HasRtlm { get; private init; }

    public static ShapePlan Build(OtFace face, ScriptShaper shaper, uint script, uint language, FeatureSet features, bool rtl = false)
    {
        uint[] scripts = shaper.ScriptTags(script);
        uint[][] stages = shaper.GsubStages;
        PlannedLookup[][] gsub = [];
        bool rtlm = false;
        if (face.Gsub is { } gs)
        {
            var perStage = new SortedDictionary<int, (int, uint)>[stages.Length];
            for (int i = 0; i < perStage.Length; i++)
            {
                perStage[i] = [];
            }

            Collect(gs, scripts, language, shaper, features, rtl, stages, perStage, out _, out rtlm);
            gsub = [.. perStage.Select(Flatten)];
        }

        PlannedLookup[] gpos = [];
        bool kern = false;
        if (face.Gpos is { } gp)
        {
            var one = new SortedDictionary<int, (int, uint)>[1] { [] };
            Collect(gp, scripts, language, shaper, features, rtl, [], one, out kern, out _);
            gpos = Flatten(one[0]);
        }

        return new ShapePlan { Gsub = gsub, Gpos = gpos, HasGposKern = kern, HasRtlm = rtlm };
    }

    private static PlannedLookup[] Flatten(SortedDictionary<int, (int Value, uint Mask)> d) => [.. d.Select(kv => new PlannedLookup(kv.Key, kv.Value.Value, kv.Value.Mask))];

    // Walks the feature list of the selected language system and files each enabled feature's lookups under its stage.
    private static void Collect(LayoutTable table, uint[] scripts, uint language, ScriptShaper shaper, FeatureSet user, bool rtl, uint[][] stages, SortedDictionary<int, (int Value, uint Mask)>[] into, out bool kern, out bool rtlm)
    {
        kern = false;
        rtlm = false;
        try
        {
            List<int>? indices = table.FeatureIndices(scripts, language, out int required);
            if (indices is null)
            {
                return;
            }

            foreach (int fi in indices)
            {
                uint tag = table.FeatureTag(fi);
                int value;
                if (fi == required)
                {
                    value = 1;
                }
                else if (user.TryGet(tag, out int v))
                {
                    value = v;
                }
                else
                {
                    value = shaper.DefaultOn(tag, rtl) ? 1 : 0;
                }

                if (value <= 0)
                {
                    continue;
                }

                int stage = into.Length - 1;
                for (int s = 0; s < stages.Length; s++)
                {
                    if (Array.IndexOf(stages[s], tag) >= 0)
                    {
                        stage = s;
                        break;
                    }
                }

                var found = new SortedDictionary<int, int>();
                table.AddFeatureLookups(fi, value, found);
                uint mask = shaper.FeatureMask(tag);
                foreach (KeyValuePair<int, int> kv in found)
                {
                    // A lookup two features ask for applies to the glyphs of either; one without a mask applies to all.
                    into[stage][kv.Key] = into[stage].TryGetValue(kv.Key, out (int Value, uint Mask) old)
                        ? (Math.Max(old.Value, kv.Value), old.Mask == 0 || mask == 0 ? 0 : old.Mask | mask)
                        : (kv.Value, mask);
                }

                kern |= tag == Tag.Kern;
                rtlm |= tag == Tag.Rtlm && found.Count > 0;
            }
        }
        catch (FontFormatException)
        {
            // A script or feature list that cannot be read enables nothing.
        }
    }
}

/// <summary>The layout data of one face: GDEF, GSUB, GPOS and the shape plans built from them. Thread-safe.</summary>
internal sealed class OtFace
{
    private const int MaxPlans = 128;

    private readonly ConcurrentDictionary<(uint, uint, FeatureSet, ScriptShaper, bool), ShapePlan> _plans = new();

    public OtFace(Dictionary<uint, ReadOnlyMemory<byte>> tables, int numGlyphs)
    {
        NumGlyphs = numGlyphs;
        if (tables.TryGetValue(Be.Tag("GDEF"), out ReadOnlyMemory<byte> gdef))
        {
            Gdef = Gdef.Create(gdef);
        }

        if (tables.TryGetValue(Be.Tag("GSUB"), out ReadOnlyMemory<byte> gsub))
        {
            Gsub = LayoutTable.Create(gsub, isGpos: false);
        }

        if (tables.TryGetValue(Be.Tag("GPOS"), out ReadOnlyMemory<byte> gpos))
        {
            Gpos = LayoutTable.Create(gpos, isGpos: true);
        }
    }

    public int NumGlyphs { get; }

    public Gdef? Gdef { get; }

    public LayoutTable? Gsub { get; }

    public LayoutTable? Gpos { get; }

    public ShapePlan GetPlan(ScriptShaper shaper, uint script, uint language, FeatureSet features, bool rtl = false)
    {
        var key = (script, language, features, shaper, rtl);
        if (_plans.TryGetValue(key, out ShapePlan? plan))
        {
            return plan;
        }

        plan = ShapePlan.Build(this, shaper, script, language, features, rtl);
        if (_plans.Count >= MaxPlans)
        {
            _plans.Clear();
        }

        _plans[key] = plan;
        return plan;
    }
}
