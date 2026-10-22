using System.Numerics;
using OneImage.Text.Fonts;
using OneImage.Text.Layout;
using SixLabors.Fonts;
using SixLabors.Fonts.Tables.AdvancedTypographic;
using SixLabors.ImageSharp.Drawing.Processing;

namespace OneImage.Text.Tests;

/// <summary>Records, for every glyph the reference draws, its glyph id and the lowest-left point of its outline.</summary>
internal sealed class GlyphIdRecorder : IGlyphRenderer
{
    public List<(int GlyphId, Vector2 Min, bool HasOutline)> Glyphs { get; } = [];

    private int _glyphId;
    private Vector2 _min;
    private bool _any;

    public void BeginText(in FontRectangle bounds)
    {
    }

    public bool BeginGlyph(in FontRectangle bounds, in GlyphRendererParameters parameters)
    {
        _glyphId = parameters.GlyphId;
        _min = new Vector2(float.MaxValue, float.MaxValue);
        _any = false;
        return true;
    }

    private void Touch(Vector2 p)
    {
        _any = true;
        _min = Vector2.Min(_min, p);
    }

    public void BeginFigure()
    {
    }

    public void MoveTo(Vector2 point) => Touch(point);

    public void LineTo(Vector2 point) => Touch(point);

    public void QuadraticBezierTo(Vector2 secondControlPoint, Vector2 point)
    {
        Touch(secondControlPoint);
        Touch(point);
    }

    public void CubicBezierTo(Vector2 secondControlPoint, Vector2 thirdControlPoint, Vector2 point)
    {
        Touch(secondControlPoint);
        Touch(thirdControlPoint);
        Touch(point);
    }

    public void ArcTo(float radiusX, float radiusY, float rotationDegrees, bool largeArc, bool sweep, Vector2 point) => Touch(point);

    public void EndFigure()
    {
    }

    public void EndGlyph() => Glyphs.Add((_glyphId, _min, _any));

    public void EndText()
    {
    }

    public TextDecorations EnabledDecorations() => TextDecorations.None;

    public void SetDecoration(TextDecorations textDecorations, Vector2 start, Vector2 end, float thickness)
    {
    }
}

/// <summary>Glyph ids and positions from our shaper against SixLabors.Fonts 2.1.3, used only through its public API.</summary>
public class ShapingReferenceTests
{
    internal const string Font = TestFonts.Shaping;

    internal static (List<(int Glyph, Vector2 Origin, bool HasOutline)> Ours, GlyphIdRecorder Theirs) Run(string text, string[]? features = null, KerningMode kerning = KerningMode.Normal)
    {
        FontFace face = TestFonts.Face(Font);
        var font = TestFonts.ShapingFonts.Get("Noto Sans").CreateFont(face.UnitsPerEm);
        var options = new TextOptions(font) { Kerning = kerning };
        foreach (string f in features ?? [])
        {
            options.Features.Add(f.StartsWith('-') ? FontFeature.Off(f[1..]) : FontFeature.On(f));
        }

        TextLayout layout = TextLayout.Create(text, options);
        var ours = new List<(int, Vector2, bool)>();
        foreach (LayoutLine line in layout.Lines)
        {
            for (int k = line.GlyphStart; k < line.GlyphEnd; k++)
            {
                ShapedGlyph g = layout.Glyphs[k];
                if (g.IsSpace)
                {
                    continue;
                }

                Outline o = face.GetOutline(g.GlyphId);
                Vector2 origin = new(line.Left + layout.PenX[k] + g.XOffset, line.Baseline - g.YOffset);
                ours.Add((g.GlyphId, origin, !o.IsEmpty));
            }
        }

        var refFont = Reference.Font(Font, face.UnitsPerEm);
        var ro = new RichTextOptions(refFont) { KerningMode = kerning == KerningMode.None ? SixLabors.Fonts.KerningMode.None : SixLabors.Fonts.KerningMode.Auto };
        foreach (string f in features ?? [])
        {
            if (!f.StartsWith('-'))
            {
                ro.FeatureTags = [.. ro.FeatureTags, Tag.Parse(f)];
            }
        }

        var rec = new GlyphIdRecorder();
        TextRenderer.RenderTextTo(rec, text, ro);
        return (ours, rec);
    }

    // Compares glyph ids exactly and glyph origins to a hundredth of a font unit. The reference reports where each outline lies, so
    // its origin is that minus the outline's own lowest-left point, which is read from the same font.
    internal static (int Glyphs, double Worst) Compare(string label, string text, string[]? features = null, KerningMode kerning = KerningMode.Normal)
    {
        FontFace face = TestFonts.Face(Font);
        var (ours, theirs) = Run(text, features, kerning);
        var theirList = theirs.Glyphs.Where(g => g.HasOutline).ToList();
        var ourList = ours.Where(g => g.HasOutline).ToList();
        string a = string.Join(' ', ourList.Select(g => g.Glyph));
        string b = string.Join(' ', theirList.Select(g => g.GlyphId));
        Assert.True(a == b, $"{label}: glyph ids differ\n ours   {a}\n theirs {b}");
        double worst = 0;
        for (int i = 0; i < ourList.Count; i++)
        {
            Outline o = face.GetOutline(ourList[i].Glyph);
            float minX = float.MaxValue, minY = float.MaxValue;
            for (int k = 0; k + 1 < o.Coords.Length; k += 2)
            {
                minX = Math.Min(minX, o.Coords[k]);
                minY = Math.Min(minY, -o.Coords[k + 1]);
            }

            Vector2 origin = theirList[i].Min - new Vector2(minX, minY);
            worst = Math.Max(worst, Math.Max(Math.Abs(origin.X - ourList[i].Origin.X), Math.Abs(origin.Y - ourList[i].Origin.Y)));
        }

        TestReport.Line($"SHAPING {label}: {ourList.Count} glyphs, largest origin difference {worst:0.####}");
        return (ourList.Count, worst);
    }

    public static TheoryData<string, string?> Cases => new()
    {
        // Ligatures (liga) and pair kerning
        { "fi fl ffi ffl ff office fluffy affix", null },
        { "AVATAR To Wa Ty r. P,", null },
        { "Hello, World! 0123456789 + 1/2", null },
        // Latin marks: positioned on a base, composed by ccmp, stacked
        { "e\u0301 a\u0308 o\u0302\u0301 e\u0323\u0301 A\u030A n\u0303 i\u0307\u0301 y\u0308 E\u0304\u0301", null },
        { "a\u0328 O\u0328\u0304 u\u031B o\u031B\u0301 l\u00B7 L\u00B7 s\u0326 T\u0326", null },
        { "fi\u0301 f\u0301i ffi\u0302 \u0301fi", null },
        // Greek and Cyrillic with combining marks
        { "\u03B1\u0301 \u03B9\u0308\u0301 \u03B5\u0300 \u03C5\u0308\u0301 \u03BF\u0313 \u0391\u0301", null },
        { "\u03B1\u0306\u0313\u0301 \u03B9\u0304\u0314\u0300 \u03AC\u03AD \u0391\u0399\u03A9", null },
        { "\u0438\u0306 \u0430\u0301 \u0435\u0308 \u044B\u0301 \u0423\u0306 \u0430\u0306\u0301 \u044E\u0308", null },
        { "\u041F\u0440\u0438\u0432\u0435\u0442, \u043C\u0438\u0440! \u0411\u0431 \u0413\u0433 \u0414\u0434 \u041F\u043F \u0422\u0442", null },
        // Mixed scripts in one run
        { "Hello \u03BA\u03CC\u03C3\u03BC\u03B5 \u043C\u0438\u0440 fi", null },
        // Optional features
        { "Small Caps 123 fi", "smcp" },
        { "Small Caps 123", "c2sc" },
        { "Old 0123456789 style", "onum" },
        { "Lining 0123456789", "lnum" },
        { "Tab 0123456789 il", "tnum" },
        { "Prop 0123456789", "pnum" },
        { "Slashed 0 zero 100", "zero" },
        { "1/2 3/4 11/16", "frac" },
        { "x2 H2O 10th 1st 2nd a1", "sups" },
        { "x2 H2O", "subs" },
        { "1a 2o 3rd Nº", "ordn" },
        { "Case (HELLO) [x] -a- \u00ABA\u00BB", "case" },
        { "Stylistic alternates Gagy fi", "ss03" },
        { "Stylistic alternates Gagy fi", "ss04" },
        { "Stylistic alternates Gagy fi", "salt" },
        { "Alternates abcdefg 123", "aalt" },
        { "Numerators 12 denominators 34", "numr" },
        { "Numerators 12 denominators 34", "dnom" },
        { "smcp and onum together 2024", "smcp,onum" },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void GlyphIdsAndPositionsMatchTheReference(string text, string? features)
    {
        string[]? f = features?.Split(',');
        var (n, worst) = Compare(text + (features is null ? "" : " [" + features + "]"), text, f);
        Assert.True(n > 0);
        Assert.True(worst < 0.01, $"largest origin difference {worst}");
    }
}
