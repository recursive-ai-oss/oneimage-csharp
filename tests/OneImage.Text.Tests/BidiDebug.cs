using OneImage.Text.Layout;

namespace OneImage.Text.Tests;

public class BidiDebug
{
    [Fact]
    public void Dump()
    {
        string text = Environment.GetEnvironmentVariable("BIDI_TEXT") ?? "";
        if (text.Length == 0)
        {
            return;
        }

        var (o, t, layout) = BidiHarness.Run(text, 40, x => { x.Origin = new System.Numerics.Vector2(20, 10); x.TextDirection = Enum.Parse<TextDirection>(Environment.GetEnvironmentVariable("BIDI_DIR") ?? "Auto"); x.WrappingLength = float.Parse(Environment.GetEnvironmentVariable("BIDI_WRAP") ?? "0", System.Globalization.CultureInfo.InvariantCulture); }, r => { r.Origin = new System.Numerics.Vector2(20, 10); float w = float.Parse(Environment.GetEnvironmentVariable("BIDI_WRAP") ?? "0", System.Globalization.CultureInfo.InvariantCulture); r.WrappingLength = w > 0 ? w : -1; });
        var sb = new System.Text.StringBuilder();
        foreach (LayoutLine line in layout.Lines)
        {
            for (int k = line.GlyphStart; k < line.GlyphEnd; k++)
            {
                ShapedGlyph g = layout.Glyphs[k];
                sb.Append($"U+{g.CodePoint:X4}/g{g.GlyphId}@{line.Left + layout.PenX[k]:0.##}+{g.XOffset:0.##} lv{g.Level} ");
            }

            sb.AppendLine();
        }

        File.WriteAllText(Environment.GetEnvironmentVariable("BIDI_OUT")!, sb + "\nTHEIRS " + BidiHarness.Describe(t) + "\nOURS " + BidiHarness.Describe(o));
    }
}
