using System.Numerics;
using SixLabors.Fonts;
using SixLabors.ImageSharp.Drawing.Processing;
using RefFont = SixLabors.Fonts.Font;
using RefFontCollection = SixLabors.Fonts.FontCollection;
using RefTextOptions = SixLabors.Fonts.TextOptions;

namespace OneImage.Text.Tests;

/// <summary>SixLabors.Fonts 2.1.3, used only through its public API as a black-box reference.</summary>
internal static class Reference
{
    private static readonly Dictionary<string, RefFontCollection> Collections = [];

    public static RefFont Font(string file, float size, int face = 0, string? family = null)
    {
        string key = file;
        lock (Collections)
        {
            if (!Collections.TryGetValue(key, out RefFontCollection? c))
            {
                c = new RefFontCollection();
                if (file.EndsWith(".ttc", StringComparison.Ordinal))
                {
                    c.AddCollection(TestFonts.PathOf(file));
                }
                else
                {
                    c.Add(TestFonts.PathOf(file));
                }

                Collections[key] = c;
            }

            SixLabors.Fonts.FontFamily[] fams = [.. c.Families];
            SixLabors.Fonts.FontFamily fam = family is null ? fams[0] : c.Get(family);
            SixLabors.Fonts.FontStyle style = face == 1 ? SixLabors.Fonts.FontStyle.Bold : SixLabors.Fonts.FontStyle.Regular;
            return fam.CreateFont(size, style);
        }
    }

    public static RefTextOptions Options(RefFont font, TextOptions o)
    {
        var r = new RichTextOptions(font)
        {
            Dpi = o.Dpi,
            Origin = o.Origin,
            LineSpacing = o.LineSpacing,
            TabWidth = o.TabWidth,
            WrappingLength = o.WrappingLength > 0 ? o.WrappingLength : -1,
            KerningMode = o.Kerning == KerningMode.None ? SixLabors.Fonts.KerningMode.None : SixLabors.Fonts.KerningMode.Auto,
            HorizontalAlignment = o.HorizontalAlignment switch { HorizontalAlignment.Center => SixLabors.Fonts.HorizontalAlignment.Center, HorizontalAlignment.Right => SixLabors.Fonts.HorizontalAlignment.Right, _ => SixLabors.Fonts.HorizontalAlignment.Left },
            VerticalAlignment = o.VerticalAlignment switch { VerticalAlignment.Center => SixLabors.Fonts.VerticalAlignment.Center, VerticalAlignment.Bottom => SixLabors.Fonts.VerticalAlignment.Bottom, _ => SixLabors.Fonts.VerticalAlignment.Top },
            TextAlignment = o.TextAlignment switch { TextAlignment.Center => SixLabors.Fonts.TextAlignment.Center, TextAlignment.End => SixLabors.Fonts.TextAlignment.End, _ => SixLabors.Fonts.TextAlignment.Start },
        };
        return r;
    }
}

/// <summary>Records the outline commands of every glyph the reference renders, as absolute points.</summary>
internal sealed class OutlineRecorder : IGlyphRenderer
{
    public readonly List<(char Verb, Vector2[] Points)> Commands = [];

    public void BeginText(in FontRectangle bounds)
    {
    }

    public bool BeginGlyph(in FontRectangle bounds, in GlyphRendererParameters parameters) => true;

    public void BeginFigure()
    {
    }

    public void MoveTo(Vector2 point) => Commands.Add(('M', [point]));

    public void LineTo(Vector2 point) => Commands.Add(('L', [point]));

    public void QuadraticBezierTo(Vector2 secondControlPoint, Vector2 point) => Commands.Add(('Q', [secondControlPoint, point]));

    public void CubicBezierTo(Vector2 secondControlPoint, Vector2 thirdControlPoint, Vector2 point) => Commands.Add(('C', [secondControlPoint, thirdControlPoint, point]));

    public void ArcTo(float radiusX, float radiusY, float rotationDegrees, bool largeArc, bool sweep, Vector2 point) => Commands.Add(('A', [point]));

    public void EndFigure() => Commands.Add(('Z', []));

    public void EndGlyph()
    {
    }

    public void EndText()
    {
    }

    public SixLabors.Fonts.TextDecorations EnabledDecorations() => SixLabors.Fonts.TextDecorations.None;

    public void SetDecoration(SixLabors.Fonts.TextDecorations textDecorations, Vector2 start, Vector2 end, float thickness)
    {
    }
}
