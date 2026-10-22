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

    /// <summary>Whether the feature is on unless the user says otherwise.</summary>
    public abstract bool DefaultOn(uint tag);

    /// <summary>Called on the glyph run before any lookup runs (reordering, mask setup). The generic shaper does nothing.</summary>
    public virtual void PrepareRun(GlyphBuffer buffer)
    {
    }

    /// <summary>The OpenType script tags to try, best first.</summary>
    public virtual uint[] ScriptTags(uint script) => [script, Tag.Dflt, Tag.DfltLower, Tag.Latn];
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
}

/// <summary>Horizontal left-to-right text in the simple scripts: ccmp and locl first, then the other default substitutions, then positioning.</summary>
internal sealed class GenericShaper : ScriptShaper
{
    public static readonly GenericShaper Instance = new();

    private static readonly uint[][] Stages =
    [
        [Tag.Ccmp, Tag.Locl],
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

    /// <summary>Whether a GPOS lookup came from the kern feature, so the legacy kern table must stay out of the way.</summary>
    public bool HasGposKern { get; private init; }

    public static ShapePlan Build(OtFace face, ScriptShaper shaper, uint script, uint language, FeatureSet features)
    {
        uint[] scripts = shaper.ScriptTags(script);
        uint[][] stages = shaper.GsubStages;
        PlannedLookup[][] gsub = [];
        if (face.Gsub is { } gs)
        {
            var perStage = new SortedDictionary<int, int>[stages.Length];
            for (int i = 0; i < perStage.Length; i++)
            {
                perStage[i] = [];
            }

            Collect(gs, scripts, language, shaper, features, stages, perStage, out _);
            gsub = [.. perStage.Select(Flatten)];
        }

        PlannedLookup[] gpos = [];
        bool kern = false;
        if (face.Gpos is { } gp)
        {
            var one = new SortedDictionary<int, int>[1] { [] };
            Collect(gp, scripts, language, shaper, features, [], one, out kern);
            gpos = Flatten(one[0]);
        }

        return new ShapePlan { Gsub = gsub, Gpos = gpos, HasGposKern = kern };
    }

    private static PlannedLookup[] Flatten(SortedDictionary<int, int> d) => [.. d.Select(kv => new PlannedLookup(kv.Key, kv.Value))];

    // Walks the feature list of the selected language system and files each enabled feature's lookups under its stage.
    private static void Collect(LayoutTable table, uint[] scripts, uint language, ScriptShaper shaper, FeatureSet user, uint[][] stages, SortedDictionary<int, int>[] into, out bool kern)
    {
        kern = false;
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
                    value = shaper.DefaultOn(tag) ? 1 : 0;
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

                table.AddFeatureLookups(fi, value, into[stage]);
                kern |= tag == Tag.Kern;
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

    private readonly ConcurrentDictionary<(uint, uint, FeatureSet), ShapePlan> _plans = new();

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

    public ShapePlan GetPlan(ScriptShaper shaper, uint script, uint language, FeatureSet features)
    {
        var key = (script, language, features);
        if (_plans.TryGetValue(key, out ShapePlan? plan))
        {
            return plan;
        }

        plan = ShapePlan.Build(this, shaper, script, language, features);
        if (_plans.Count >= MaxPlans)
        {
            _plans.Clear();
        }

        _plans[key] = plan;
        return plan;
    }
}
