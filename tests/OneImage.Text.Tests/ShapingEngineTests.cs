using OneImage.Text.Fonts;
using OneImage.Text.Shaping;

namespace OneImage.Text.Tests;

/// <summary>
/// The lookup engine on hand-assembled tables: every GSUB and GPOS lookup type and format, the lookup flags, and the result of the
/// attachment pass. Glyph ids are written as numbers; glyphs 1 to 10 are bases, 11 to 13 ligatures, 20 to 25 marks.
/// </summary>
public class ShapingEngineTests
{
    private const int Glyphs = 40;

    private static readonly byte[] Classes = LayoutFont.Gdef([(1, 10, 1), (11, 13, 2), (20, 25, 3)]);

    private static int[] Advances(params (int Glyph, int Width)[] set)
    {
        int[] a = new int[Glyphs];
        Array.Fill(a, 100);
        a[0] = 500;
        for (int g = 20; g <= 25; g++)
        {
            a[g] = 0;
        }

        foreach ((int g, int w) in set)
        {
            a[g] = w;
        }

        return a;
    }

    private static FontFace GsubFont(LookupSpec[] lookups, int[]? lookupsEnabled = null, byte[]? gdef = null, FeatureSpec[]? features = null) =>
        LayoutFont.Face(LayoutFont.Font(Advances(), gdef ?? Classes, LayoutFont.Table(false, features ?? [new("ccmp", lookupsEnabled ?? [0])], lookups)));

    private static FontFace GposFont(LookupSpec[] lookups, int[]? lookupsEnabled = null, byte[]? gdef = null, int[]? advances = null) =>
        LayoutFont.Face(LayoutFont.Font(advances ?? Advances(), gdef ?? Classes, null, LayoutFont.Table(true, [new("mark", lookupsEnabled ?? [0])], lookups)));

    private static List<ShapedRunGlyph> Shape(FontFace face, int[] glyphs, params (string Tag, int Value)[] features)
    {
        var output = new List<ShapedRunGlyph>();
        TextShaper.ShapeGlyphs(face, glyphs, 0, FeatureSet.Create(features.Select(f => (Fonts.Be.Tag(f.Tag), f.Value))), output);
        return output;
    }

    private static int[] Ids(List<ShapedRunGlyph> run) => [.. run.Select(g => g.GlyphId)];

    // ---- GSUB ----

    private static LookupSpec Single1(int cover, int delta, int flags = 0) =>
        new(1, flags, new Blob().U16(1).Ref(Blob.Cov(cover)).U16(delta).Build());

    private static byte[] Single2(int[] covered, int[] replacements) =>
        new Blob().U16(2).Ref(Blob.Cov(covered)).U16(replacements.Length).U16(replacements).Build();

    [Fact]
    public void SingleSubstitutionByDeltaAndByTable()
    {
        FontFace face = GsubFont([Single1(3, 2), new LookupSpec(1, 0, Single2([4, 6], [7, 8]))], [0, 1]);
        Assert.Equal([1, 5, 5, 7, 8, 9], Ids(Shape(face, [1, 3, 3, 4, 6, 9])));
    }

    [Fact]
    public void DeltaWrapsAroundSixteenBitsAndOutOfRangeGlyphsAreRefused()
    {
        // 3 + 65535 wraps to 2; 3 + 100 is past the font's last glyph and the substitution is not made.
        Assert.Equal([2], Ids(Shape(GsubFont([Single1(3, -1)]), [3])));
        Assert.Equal([3], Ids(Shape(GsubFont([Single1(3, 100)]), [3])));
    }

    [Fact]
    public void LookupsFromAnExtensionSubtableApply()
    {
        FontFace face = GsubFont([new LookupSpec(1, 0, new Blob().U16(1).Ref(Blob.Cov(3)).U16(1).Build()) { Extension = true }]);
        Assert.Equal([4], Ids(Shape(face, [3])));
    }

    private static byte[] Multiple(int glyph, int[] sequence) =>
        new Blob().U16(1).Ref(Blob.Cov(glyph)).U16(1).Ref(new Blob().U16(sequence.Length).U16(sequence)).Build();

    [Fact]
    public void MultipleSubstitutionKeepsTheClusterAndTheCursorMovesPastTheNewGlyphs()
    {
        FontFace face = GsubFont([new LookupSpec(2, 0, Multiple(5, [6, 5, 7]))]);
        List<ShapedRunGlyph> run = Shape(face, [4, 5, 9]);
        Assert.Equal([4, 6, 5, 7, 9], Ids(run));
        Assert.Equal([0, 1, 1, 1, 2], run.Select(g => g.Cluster));
    }

    [Fact]
    public void AnEmptyMultipleSubstitutionDeletesTheGlyph() =>
        Assert.Equal([4, 9], Ids(Shape(GsubFont([new LookupSpec(2, 0, Multiple(5, []))]), [4, 5, 9])));

    [Fact]
    public void AlternateSubstitutionTakesTheFeatureValue()
    {
        byte[] alt = new Blob().U16(1).Ref(Blob.Cov(5)).U16(1).Ref(new Blob().U16(3, 10, 11, 12)).Build();
        FontFace face = GsubFont([new LookupSpec(3, 0, alt)], features: [new("ss01", 0)]);
        Assert.Equal([5], Ids(Shape(face, [5])));                    // off by default
        Assert.Equal([10], Ids(Shape(face, [5], ("ss01", 1))));
        Assert.Equal([12], Ids(Shape(face, [5], ("ss01", 3))));
        Assert.Equal([5], Ids(Shape(face, [5], ("ss01", 4))));       // beyond the list: unchanged
    }

    private static byte[] Ligatures(int first, params (int Lig, int[] Others)[] ligs)
    {
        var set = new Blob().U16(ligs.Length);
        foreach ((int lig, int[] rest) in ligs)
        {
            set.Ref(new Blob().U16(lig, rest.Length + 1).U16(rest));
        }

        return new Blob().U16(1).Ref(Blob.Cov(first)).U16(1).Ref(set).Build();
    }

    [Fact]
    public void LigaturesTryTheirEntriesInOrder()
    {
        FontFace face = GsubFont([new LookupSpec(4, 0, Ligatures(1, (12, [2, 3]), (11, [2])))]);
        Assert.Equal([12, 4], Ids(Shape(face, [1, 2, 3, 4])));
        Assert.Equal([11, 4], Ids(Shape(face, [1, 2, 4])));
        Assert.Equal([1, 3], Ids(Shape(face, [1, 3])));
        List<ShapedRunGlyph> run = Shape(face, [9, 1, 2, 3]);
        Assert.Equal([9, 12], Ids(run));
        Assert.Equal([0, 1], run.Select(g => g.Cluster));
    }

    [Fact]
    public void IgnoredMarksBetweenLigatureComponentsStayAfterTheLigature()
    {
        FontFace face = GsubFont([new LookupSpec(4, 0x8, Ligatures(1, (11, [2])))]);
        Assert.Equal([11, 21], Ids(Shape(face, [1, 21, 2])));
        // Without the flag the mark blocks the ligature.
        FontFace plain = GsubFont([new LookupSpec(4, 0, Ligatures(1, (11, [2])))]);
        Assert.Equal([1, 21, 2], Ids(Shape(plain, [1, 21, 2])));
    }

    [Fact]
    public void MarkFilteringSetsAndAttachmentClassesDecideWhichMarksAreSkipped()
    {
        byte[] gdef = LayoutFont.Gdef([(1, 10, 1), (11, 13, 2), (20, 25, 3)], [(21, 21, 1), (22, 22, 2)], [[21]]);
        byte[] lig = Ligatures(1, (11, [2]));

        // Set 0 holds mark 21: it is looked at (and blocks), mark 22 is skipped.
        FontFace set = GsubFont([new LookupSpec(4, 0x10, lig) { MarkSet = 0 }], gdef: gdef);
        Assert.Equal([1, 21, 2], Ids(Shape(set, [1, 21, 2])));
        Assert.Equal([11, 22], Ids(Shape(set, [1, 22, 2])));

        // Attachment class 1 (mark 21) is looked at, class 2 is skipped.
        FontFace attach = GsubFont([new LookupSpec(4, 0x0100, lig)], gdef: gdef);
        Assert.Equal([1, 21, 2], Ids(Shape(attach, [1, 21, 2])));
        Assert.Equal([11, 22], Ids(Shape(attach, [1, 22, 2])));
    }

    [Fact]
    public void IgnoreBaseAndIgnoreLigatureFlagsSkipThoseGlyphs()
    {
        byte[] lig = Ligatures(21, (22, [23]));
        // marks 21 and 23 with a base between: IgnoreBaseGlyphs steps over it.
        Assert.Equal([22, 1], Ids(Shape(GsubFont([new LookupSpec(4, 0x2, lig)]), [21, 1, 23])));
        Assert.Equal([21, 1, 23], Ids(Shape(GsubFont([new LookupSpec(4, 0, lig)]), [21, 1, 23])));
        Assert.Equal([22, 12], Ids(Shape(GsubFont([new LookupSpec(4, 0x4, lig)]), [21, 12, 23])));
    }

    private static Blob Records(Blob b, params (int Seq, int Lookup)[] records)
    {
        foreach ((int s, int l) in records)
        {
            b.U16(s, l);
        }

        return b;
    }

    private static byte[] Context3(int[][] input, params (int Seq, int Lookup)[] records)
    {
        var b = new Blob().U16(3, input.Length, records.Length);
        foreach (int[] cov in input)
        {
            b.Ref(Blob.Cov(cov));
        }

        return Records(b, records).Build();
    }

    private static byte[] Chain3(int[][] backtrack, int[][] input, int[][] lookahead, params (int Seq, int Lookup)[] records)
    {
        var b = new Blob().U16(3, backtrack.Length);
        foreach (int[] c in backtrack)
        {
            b.Ref(Blob.Cov(c));
        }

        b.U16(input.Length);
        foreach (int[] c in input)
        {
            b.Ref(Blob.Cov(c));
        }

        b.U16(lookahead.Length);
        foreach (int[] c in lookahead)
        {
            b.Ref(Blob.Cov(c));
        }

        return Records(b.U16(records.Length), records).Build();
    }

    [Fact]
    public void ContextFormat3RunsNestedLookupsAtTheMatchedPositions()
    {
        // lookup 0: context on 1 2 3 -> lookup 1 (single 2 -> 20) at the second glyph
        LookupSpec[] lookups = [new(5, 0, Context3([[1], [2], [3]], (1, 1))), Single1(2, 18)];
        FontFace face = GsubFont(lookups);
        Assert.Equal([1, 20, 3], Ids(Shape(face, [1, 2, 3])));
        Assert.Equal([1, 2, 4], Ids(Shape(face, [1, 2, 4])));
    }

    [Fact]
    public void ALaterRecordSeesPositionsMovedByAnEarlierNestedInsertion()
    {
        // 1 2 3: first record doubles glyph 2 (lookup 1), second turns glyph 3 into 30 (lookup 2). The 3 has moved one place right.
        LookupSpec[] lookups =
        [
            new(5, 0, Context3([[1], [2], [3]], (1, 1), (2, 2))),
            new(2, 0, Multiple(2, [2, 2])),
            Single1(3, 27),
        ];
        Assert.Equal([1, 2, 2, 30], Ids(Shape(GsubFont(lookups), [1, 2, 3])));
    }

    [Fact]
    public void ContextSkipsIgnoredGlyphsWhileMatching()
    {
        LookupSpec[] lookups = [new(5, 0x8, Context3([[1], [2]], (1, 1))), Single1(2, 18)];
        Assert.Equal([1, 21, 20], Ids(Shape(GsubFont(lookups), [1, 21, 2])));
    }

    [Fact]
    public void ContextFormat1MatchesGlyphSequences()
    {
        var rule = new Blob().U16(2, 1, 2, 0, 1);                       // 1 2 -> lookup 1 at position 0
        var set = new Blob().U16(1).Ref(rule);
        byte[] ctx = new Blob().U16(1).Ref(Blob.Cov(1)).U16(1).Ref(set).Build();
        FontFace face = GsubFont([new(5, 0, ctx), Single1(1, 9)]);
        Assert.Equal([10, 2], Ids(Shape(face, [1, 2])));
        Assert.Equal([1, 3], Ids(Shape(face, [1, 3])));
    }

    [Fact]
    public void ContextFormat2MatchesClassSequences()
    {
        Blob classes = Blob.Classes((1, 3, 1), (4, 6, 2));
        var rule = new Blob().U16(2, 1, 2, 1, 1);                        // class 1 then class 2 -> lookup 1 at position 1
        var set = new Blob().U16(1).Ref(rule);
        byte[] ctx = new Blob().U16(2).Ref(Blob.Cov(1, 2, 3)).Ref(classes).U16(2).Ref(null).Ref(set).Build();
        FontFace face = GsubFont([new(5, 0, ctx), Single1(5, 4)]);
        Assert.Equal([2, 9], Ids(Shape(face, [2, 5])));
        Assert.Equal([2, 7], Ids(Shape(face, [2, 7])));
    }

    [Fact]
    public void ChainedContextFormat3ChecksBacktrackAndLookahead()
    {
        // backtrack is written nearest glyph first
        LookupSpec[] lookups = [new(6, 0, Chain3([[5], [4]], [[1]], [[6]], (0, 1))), Single1(1, 19)];
        FontFace face = GsubFont(lookups);
        Assert.Equal([4, 5, 20, 6], Ids(Shape(face, [4, 5, 1, 6])));
        Assert.Equal([5, 4, 1, 6], Ids(Shape(face, [5, 4, 1, 6])));
        Assert.Equal([4, 5, 1, 7], Ids(Shape(face, [4, 5, 1, 7])));
        Assert.Equal([5, 1, 6], Ids(Shape(face, [5, 1, 6])));
    }

    [Fact]
    public void ChainedContextFormat1AndFormat2()
    {
        // format 1: backtrack glyph 5, input 1, lookahead glyph 6
        var rule1 = new Blob().U16(1, 5, 1, 1, 6, 1, 0, 1);
        byte[] f1 = new Blob().U16(1).Ref(Blob.Cov(1)).U16(1).Ref(new Blob().U16(1).Ref(rule1)).Build();
        FontFace face1 = GsubFont([new(6, 0, f1), Single1(1, 19)]);
        Assert.Equal([5, 20, 6], Ids(Shape(face1, [5, 1, 6])));
        Assert.Equal([5, 1, 7], Ids(Shape(face1, [5, 1, 7])));

        // format 2: backtrack class 2 (glyph 5), input class 1 (glyph 1), lookahead class 3 (glyph 6)
        Blob cd = Blob.Classes((1, 1, 1), (5, 5, 2), (6, 6, 3));
        var rule2 = new Blob().U16(1, 2, 1, 1, 3, 1, 0, 1);
        byte[] f2 = new Blob().U16(2).Ref(Blob.Cov(1)).Ref(cd).Ref(cd).Ref(cd).U16(2).Ref(null).Ref(new Blob().U16(1).Ref(rule2)).Build();
        FontFace face2 = GsubFont([new(6, 0, f2), Single1(1, 19)]);
        Assert.Equal([5, 20, 6], Ids(Shape(face2, [5, 1, 6])));
        Assert.Equal([6, 1, 6], Ids(Shape(face2, [6, 1, 6])));
    }

    [Fact]
    public void ReverseChainingRunsFromTheEnd()
    {
        // glyph 1 becomes 20 after a glyph 1. Backwards, the second 1 changes first and then counts as a different glyph; forwards it would not.
        byte[] rev = new Blob().U16(1).Ref(Blob.Cov(1)).U16(1).Ref(Blob.Cov(1)).U16(0).U16(1, 20).Build();
        FontFace face = GsubFont([new(8, 0, rev)]);
        Assert.Equal([1, 20, 20], Ids(Shape(face, [1, 1, 1])));
    }

    [Fact]
    public void ANestedLookupCannotRecurseWithoutEnd()
    {
        // lookup 0 calls itself at the same position: the nesting limit stops it, the glyph is still substituted once.
        LookupSpec[] lookups = [new(5, 0, Context3([[1]], (0, 0), (0, 1))), Single1(1, 9)];
        Assert.Equal([10], Ids(Shape(GsubFont(lookups), [1])));
    }

    [Fact]
    public void RunawayGrowthStopsAtTheGlyphLimit()
    {
        // Forty lookups that each double glyph 1 would make 2^40 glyphs.
        LookupSpec[] lookups = [.. Enumerable.Range(0, 40).Select(_ => new LookupSpec(2, 0, Multiple(1, [1, 1])))];
        FontFace face = GsubFont(lookups, [.. Enumerable.Range(0, 40)]);
        List<ShapedRunGlyph> run = Shape(face, [1, 1, 1, 1]);
        Assert.InRange(run.Count, 4, 8 * 4 + 256);
    }

    [Fact]
    public void ARunThatWouldNeedEndlessWorkIsCutOff()
    {
        // 200 lookups of 20 subtables each, none of which match, over 2000 glyphs: eight million attempts, far over the budget.
        byte[] miss = Single1(9, 1).Subtables[0];
        LookupSpec[] lookups = [.. Enumerable.Range(0, 200).Select(_ => new LookupSpec(1, 0, [.. Enumerable.Repeat(miss, 20)]))];
        FontFace face = GsubFont(lookups, [.. Enumerable.Range(0, 200)]);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        List<ShapedRunGlyph> run = Shape(face, [.. Enumerable.Repeat(1, 2000)]);
        sw.Stop();
        Assert.Equal(2000, run.Count);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), $"took {sw.Elapsed}");
    }

    // ---- GPOS ----

    private static LookupSpec PairF1(int first, int second, int vf1, int[] v1, int vf2 = 0, int[]? v2 = null) =>
        new(2, 0, PairSubtableF1(first, second, vf1, v1, vf2, v2));

    private static byte[] PairSubtableF1(int first, int second, int vf1, int[] v1, int vf2 = 0, int[]? v2 = null) =>
        new Blob().U16(1).Ref(Blob.Cov(first)).U16(vf1, vf2, 1).Ref(new Blob().U16(1, second).U16(v1).U16(v2 ?? [])).Build();

    [Fact]
    public void SinglePositioningAdjustsAdvanceAndOffset()
    {
        byte[] single = new Blob().U16(1).Ref(Blob.Cov(3)).U16(0x5, 7, 30).Build();     // xPlacement 7, xAdvance 30
        FontFace face = GposFont([new LookupSpec(1, 0, single)]);
        List<ShapedRunGlyph> run = Shape(face, [1, 3]);
        Assert.Equal(100, run[0].XAdvance);
        Assert.Equal(130, run[1].XAdvance);
        Assert.Equal(7, run[1].XOffset);

        byte[] arr = new Blob().U16(2).Ref(Blob.Cov(3, 4)).U16(0x2, 2, 11, 22).Build();  // yPlacement per glyph
        List<ShapedRunGlyph> run2 = Shape(GposFont([new LookupSpec(1, 0, arr)]), [3, 4]);
        Assert.Equal([11, 22], run2.Select(g => g.YOffset));
    }

    [Fact]
    public void PairPositioningFormat1AdjustsTheFirstGlyph()
    {
        FontFace face = GposFont([PairF1(1, 2, 0x4, [-20])]);
        Assert.Equal([80, 100, 100], Shape(face, [1, 2, 3]).Select(g => g.XAdvance));
    }

    [Fact]
    public void ASecondValueConsumesTheSecondGlyphOtherwiseItCanStartTheNextPair()
    {
        byte[] p12 = PairSubtableF1(1, 2, 0x4, [-20]);
        byte[] p23 = new Blob().U16(1).Ref(Blob.Cov(2)).U16(0x4, 0, 1).Ref(new Blob().U16(1, 3, -30)).Build();
        // Both pairs in one lookup (two subtables): pair 1-2 consumes only glyph 1, so 2-3 also applies.
        FontFace face = GposFont([new LookupSpec(2, 0, p12, p23)]);
        Assert.Equal([80, 70, 100], Shape(face, [1, 2, 3]).Select(g => g.XAdvance));

        // With a value for the second glyph, that glyph is used up and cannot kern with its successor.
        byte[] p12b = PairSubtableF1(1, 2, 0x4, [-20], 0x4, [5]);
        FontFace used = GposFont([new LookupSpec(2, 0, p12b, p23)]);
        Assert.Equal([80, 105, 100], Shape(used, [1, 2, 3]).Select(g => g.XAdvance));
    }

    [Fact]
    public void ASubtableThatCoversTheGlyphButNotThePairLetsTheNextOneTry()
    {
        byte[] first = PairSubtableF1(1, 2, 0x4, [-20]);
        byte[] second = PairSubtableF1(1, 3, 0x4, [-40]);
        FontFace face = GposFont([new LookupSpec(2, 0, first, second)]);
        Assert.Equal(60, Shape(face, [1, 3])[0].XAdvance);
        Assert.Equal(80, Shape(face, [1, 2])[0].XAdvance);
    }

    [Fact]
    public void PairPositioningFormat2UsesClasses()
    {
        // class1: glyph 1 -> 1; class2: glyph 2 -> 1. records [class1][class2], class1Count 2, class2Count 2.
        Blob c1 = Blob.Classes((1, 1, 1));
        Blob c2 = Blob.Classes((2, 2, 1));
        byte[] sub = new Blob().U16(2).Ref(Blob.Cov(1)).U16(0x4, 0).Ref(c1).Ref(c2).U16(2, 2).U16(0, 0, 0, -60).Build();
        FontFace face = GposFont([new LookupSpec(2, 0, sub)]);
        Assert.Equal(40, Shape(face, [1, 2])[0].XAdvance);
        Assert.Equal(100, Shape(face, [1, 3])[0].XAdvance);   // class 0 pair: record is zero
    }

    [Fact]
    public void PairPositioningSkipsGlyphsTheLookupIgnores()
    {
        byte[] sub = PairSubtableF1(1, 2, 0x4, [-20]);
        FontFace face = GposFont([new LookupSpec(2, 0x4, sub)]);
        Assert.Equal(80, Shape(face, [1, 12, 2])[0].XAdvance);       // the ligature in between is not a neighbour
        FontFace strict = GposFont([new LookupSpec(2, 0, sub)]);
        Assert.Equal(100, Shape(strict, [1, 12, 2])[0].XAdvance);
    }

    [Fact]
    public void CursiveAttachmentJoinsExitToEntryAndCarriesTheBaselineShift()
    {
        Blob exit1 = Blob.Anchor(100, 10);
        Blob entry2 = Blob.Anchor(20, 0);
        byte[] sub = new Blob().U16(1).Ref(Blob.Cov(1, 2)).U16(2).Ref(null).Ref(exit1).Ref(entry2).Ref(null).Build();
        FontFace face = GposFont([new LookupSpec(3, 0, sub)]);
        List<ShapedRunGlyph> run = Shape(face, [1, 2]);
        Assert.Equal(100, run[0].XAdvance);
        Assert.Equal(80, run[1].XAdvance);
        Assert.Equal(-20, run[1].XOffset);
        Assert.Equal(10, run[1].YOffset);
    }

    private static byte[] MarkToBaseSubtable(int markGlyph, int baseGlyph, (int X, int Y) mark, (int X, int Y) bas) =>
        new Blob().U16(1).Ref(Blob.Cov(markGlyph)).Ref(Blob.Cov(baseGlyph)).U16(1)
            .Ref(new Blob().U16(1, 0).Ref(Blob.Anchor(mark.X, mark.Y)))
            .Ref(new Blob().U16(1).Ref(Blob.Anchor(bas.X, bas.Y)))
            .Build();

    [Fact]
    public void MarkToBaseAnchorsTheMarkOnTheBase()
    {
        byte[] sub = MarkToBaseSubtable(21, 1, (50, 0), (200, 700));
        FontFace face = GposFont([new LookupSpec(4, 0, sub)]);
        List<ShapedRunGlyph> run = Shape(face, [1, 21]);
        // The pen is 100 to the right of the base; the anchors meet at 200 - 50 = 150 from the base's origin.
        Assert.Equal(50, run[1].XOffset);
        Assert.Equal(700, run[1].YOffset);

        // Marks stacked on one base each find that base, skipping marks in between.
        List<ShapedRunGlyph> two = Shape(face, [1, 21, 21]);
        Assert.Equal([50, 50], [two[1].XOffset, two[2].XOffset]);
    }

    [Fact]
    public void MarkToBaseNeedsACoveredBaseAndFallsThroughOtherwise()
    {
        FontFace face = GposFont([new LookupSpec(4, 0, MarkToBaseSubtable(21, 1, (0, 0), (200, 700)))]);
        List<ShapedRunGlyph> run = Shape(face, [2, 21]);                 // glyph 2 is not covered as a base
        Assert.Equal(0, run[1].XOffset);
        Assert.Equal(0, run[1].YOffset);
    }

    [Fact]
    public void MarkToMarkStacksOnTheMarkBeforeIt()
    {
        byte[] baseSub = MarkToBaseSubtable(21, 1, (50, 0), (200, 700));
        byte[] mm = new Blob().U16(1).Ref(Blob.Cov(22)).Ref(Blob.Cov(21)).U16(1)
            .Ref(new Blob().U16(1, 0).Ref(Blob.Anchor(10, 0)))
            .Ref(new Blob().U16(1).Ref(Blob.Anchor(30, 120)))
            .Build();
        FontFace face = GposFont([new LookupSpec(4, 0, baseSub), new LookupSpec(6, 0, mm)], [0, 1]);
        List<ShapedRunGlyph> run = Shape(face, [1, 21, 22]);
        // mark 21: offset (50, 700). mark 22 anchors at 21's anchor (30,120) + its own offset, (the first mark has no advance).
        Assert.Equal(50, run[1].XOffset);
        Assert.Equal(50 + (30 - 10), run[2].XOffset);
        Assert.Equal(700 + 120, run[2].YOffset);
    }

    [Fact]
    public void MarkToLigatureUsesTheComponentTheMarkFollowed()
    {
        // Ligature 11 = 1 + 2 with a mark between: GSUB makes [11, 21] and tags the mark with component 1.
        byte[] lig = Ligatures(1, (11, [2]));
        Blob la = new Blob().U16(2).Ref(Blob.Anchor(100, 700)).Ref(Blob.Anchor(300, 710));        // two components, one mark class
        byte[] ml = new Blob().U16(1).Ref(Blob.Cov(21)).Ref(Blob.Cov(11)).U16(1)
            .Ref(new Blob().U16(1, 0).Ref(Blob.Anchor(0, 0)))
            .Ref(new Blob().U16(1).Ref(la))
            .Build();
        byte[] gsub = LayoutFont.Table(false, [new FeatureSpec("ccmp", 0)], [new LookupSpec(4, 0x8, lig)]);
        byte[] gpos = LayoutFont.Table(true, [new FeatureSpec("mark", 0)], [new LookupSpec(5, 0, ml)]);
        FontFace face = LayoutFont.Face(LayoutFont.Font(Advances((11, 200)), Classes, gsub, gpos));

        List<ShapedRunGlyph> between = Shape(face, [1, 21, 2]);
        Assert.Equal([11, 21], Ids(between));
        Assert.Equal(100 - 200, between[1].XOffset);                   // component 1 anchor at x=100, pen travel 200
        Assert.Equal(700, between[1].YOffset);

        List<ShapedRunGlyph> after = Shape(face, [1, 2, 21]);
        Assert.Equal(300 - 200, after[1].XOffset);                     // a mark after the ligature goes on the last component
        Assert.Equal(710, after[1].YOffset);
    }

    [Fact]
    public void ContextualPositioningRunsNestedPositioningLookups()
    {
        byte[] single = new Blob().U16(1).Ref(Blob.Cov(2)).U16(0x4, 33).Build();
        LookupSpec[] lookups = [new(7, 0, Context3([[1], [2]], (1, 1))), new(1, 0, single)];
        List<ShapedRunGlyph> run = Shape(GposFont(lookups), [1, 2]);
        Assert.Equal(133, run[1].XAdvance);
        Assert.Equal(100, Shape(GposFont(lookups), [3, 2])[1].XAdvance);
    }

    [Fact]
    public void ExtensionPositioningLookupsApply()
    {
        FontFace face = GposFont([new LookupSpec(2, 0, PairSubtableF1(1, 2, 0x4, [-20])) { Extension = true }]);
        Assert.Equal(80, Shape(face, [1, 2])[0].XAdvance);
    }

    [Fact]
    public void FeatureValueZeroTurnsADefaultFeatureOff()
    {
        FontFace face = GposFont([PairF1(1, 2, 0x4, [-20])]);
        Assert.Equal(100, Shape(face, [1, 2], ("mark", 0))[0].XAdvance);
    }
}
