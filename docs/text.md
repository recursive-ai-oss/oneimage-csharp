# Text

Fonts and text for `Image` and `Canvas`, in the separate package `RecursiveAI.OneImage.Text` (namespace `OneImage.Text`, no dependency beyond OneImage). Text is drawn through the vector drawing layer: the laid-out text becomes one `Path` of glyph outlines, filled with a `Brush` and/or stroked with a `Pen` like any other path. See [drawing.md](drawing.md) for brushes, pens, blend modes and anti-aliasing.

```csharp
using OneImage;
using OneImage.Drawing;
using OneImage.Text;

var fonts = new FontCollection();
FontFamily family = fonts.Add("NotoSans-Regular.ttf");
Font font = family.CreateFont(32);                       // 32 points = 32 px at the default 72 dpi

var options = new TextOptions(font)
{
    Origin = new(40, 60),
    WrappingLength = 400,
    HorizontalAlignment = HorizontalAlignment.Left,
};

Image img = Image.Load("photo.jpg")
    .DrawText("Hello, world. Wrapped at 400 px.", options, Brush.Solid(new Rgba32(255, 255, 255, 255)));

TextMetrics m = TextMeasurer.Measure("Hello", options);   // advance size, ink bounds, lines
Path outline = TextBuilder.GeneratePath("Hello", options);  // fill, stroke, transform or clip it yourself
```

## Fonts

`FontCollection` loads TrueType (glyf, quadratic) and OpenType with CFF outlines (CFF1: name-keyed and CID-keyed, local and global subroutines, flex), TrueType Collections (`AddCollection` adds every face) and WOFF 1.0 (decompressed with the library's own inflate). WOFF2, CFF2 and TrueType/Type 1 hinting are not read; a variable font draws its default instance. Add from a path, a `Stream` or memory; faces with the same family name join one `FontFamily`, one face per `FontStyle`. `FontFamily.CreateFont(size, style)` picks the closest face when the style is missing and never synthesizes bold or slant.

Tables read: `cmap` (formats 0, 4, 6, 12, 13; the best Unicode subtable), `head`, `hhea`, `hmtx`, `maxp`, `OS/2`, `name`, `post`, `loca`/`glyf`, `CFF `, `kern` (format 0, Microsoft and Apple headers) and `GPOS` pair adjustment for kerning. Fonts are untrusted input: parsing is bounds-checked safe code, counts are checked against table lengths before anything is allocated, composite glyphs are flattened with an explicit stack (depth 8, 512 components), and the charstring interpreter bounds its operand stack, subroutine depth and operator count. A malformed font throws `FontFormatException` (an `ImageFormatException`), at load for the header tables and lazily, when the glyph or kerning data is first used, for the rest. `fuzz/run.sh font` covers all of it.

`FontMetrics` reports design units: `Ascender`, `Descender` (negative), `LineGap`, `LineHeight`, underline and strikeout, x-height and cap height. The vertical metrics are the OS/2 typographic ones when the font sets the `USE_TYPO_METRICS` flag, else `hhea`.

## Units and layout

`Font.Size` is in points. Pixels = points × `TextOptions.Dpi` / 72, and the default dpi is 72, so a size is the pixel height of the em square. Everything else (origin, wrapping length, results) is pixels, y down. There is no hinting and no rounding: advances, positions and bounds are unrounded floats, so a string's advance is the exact sum of its glyph advances in font units times the scale.

- **Lines.** Line breaks (LF, CR, CRLF, VT, FF, NEL, U+2028, U+2029) always start a line, except one at the very end of the text, which does not open an empty line. The line pitch is `Size × Dpi/72 × LineSpacing`; that is one em at the default spacing, not the font's own line height. The font's ascent and descent are centered in the line box: baseline = ascender − (natural line height − pitch) / 2.
- **Wrapping.** `WrappingLength` ≤ 0 means none. Lines break after spaces and after a hyphen between letters or digits (not before a digit). Spaces at the end of a line may hang past the limit and are left out of its width. A word longer than the limit keeps its own line and overflows. Tabs advance `TabWidth` space widths (default 4).
- **Kerning** is on by default (`KerningMode.Normal`): GPOS pair adjustment from the `kern` feature of the Latin script (default language system; the default script if the font has no Latin one), or the legacy `kern` table when there is no GPOS kerning. (If a font has no Latin script the default script's, else the first script's default language system is used.)
- **Alignment.** `HorizontalAlignment` puts a box on the origin (left edge, center or right edge); the box is as wide as the widest line. `TextAlignment` Start keeps lines at the box's left edge. Center and End move each line by its slack against the wrapping length (the widest line when there is none), placing it in a box of that width positioned by the horizontal alignment. This reproduces ImageSharp's behavior, including that a centered block does not center its lines unless asked to. `VerticalAlignment` puts the block (lines × pitch tall) on the origin.
- **Measuring.** `TextMeasurer.Measure` gives `TextMetrics`: `Advance` (widest line without trailing spaces × lines × pitch; independent of origin and alignment), `Bounds` (the ink, exact curve extents, positioned), `LineCount` and per-line `LineMetrics`. `GetCharacterBounds` gives each character's advance box and ink box. Zero-width format characters (U+200B–U+200F, U+2060, U+FEFF, soft hyphen, bidi controls) are dropped from the layout.
- **Missing glyphs** draw the font's `.notdef` glyph. There is no fallback yet.

## Drawing

`DrawText(text, options, brush, pen, ...)` exists for `Image` (lazy: layout happens at the call, pixels when the image is read) and `Canvas` (in place), plus overloads taking a color. The optional parameters are the fill ones: `antialias`, `opacity`, `blend`, `composition`, `linear`. With both a brush and a pen the outline is drawn over the fill. Glyph outlines are filled with the non-zero rule. `TextBuilder.GeneratePath` returns the same path as an ordinary `Path`, for filling, stroking or transforming yourself.

## Agreement with ImageSharp

Checked against SixLabors.Fonts 2.1.3 and ImageSharp.Drawing 2.1.7 through their public APIs (tests in `tests/OneImage.Text.Tests`).

- Glyph outlines: the same contours and points (TrueType, CFF-CID and composite glyphs) to within 0.01 font unit. The reference starts contours at a different point and inserts zero-length lines; the comparison normalizes both.
- Advances and kerning: equal to 0.01 px at one pixel per font unit, for GPOS kerning, the `kern` table and CFF with GPOS.
- Line counts, advance size and ink bounds: equal (0.01 and 0.05 px) across sizes, dpi, wrapping lengths, line spacing and all alignment combinations in the test matrix.
- Rendering: black text on white, mean level difference 0.03 to 0.28 over the whole image (3 to 5 over pixels where either draws ink), maximum 27 to 106 levels at single edge pixels. Positions are identical; the rest is coverage. We compute exact-area coverage; ImageSharp's scanline rasterizer over- and under-covers some thin or touching edges (see [drawing.md](drawing.md)).

Differences you will meet, all deliberate or pending the later units:

- **No ligatures or other GSUB/GPOS features yet.** ImageSharp substitutes ligatures such as `fi`, so glyphs and, in wrapped text, line breaks can differ for words containing them (ImageSharp over-measures a line that holds a ligature when it decides where to wrap).
- **Line breaking is spaces and hyphens only,** not Unicode UAX #14: ImageSharp keeps a closing quotation mark, the spaces after it and an opening bracket on one line (rule LB15); we break at the spaces.
- **Tabs** advance `TabWidth` spaces. ImageSharp's `TabWidth` had no effect on the font we probed, which has no tab glyph.
- **Empty lines.** Text that starts with a line break measures two lines of one pitch each; ImageSharp adds extra height for the leading empty line. A line of only spaces keeps its full width here; ImageSharp reported one space's width for two. Text with no ink has zero-size bounds, where ImageSharp reports a box.
- **No text decorations, rich-text runs or text along a path yet.**

## Where the next pieces plug in

`Layout/TextLayout.cs` is the whole layout in four steps with narrow seams between them.

- **Shaping** is `Shape`: code points in, `ShapedGlyph`s (glyph id, cluster, advance) out. A GSUB/GPOS engine replaces it and feeds the same glyph records, with offsets added for marks; nothing after it knows about cmap or kerning. `GposKerning` is the pair-adjustment piece of that engine.
- **Line breaking** is `CanBreakBefore`, a predicate over adjacent glyphs. Unicode line breaking replaces it with a break-opportunity list; the greedy fill that consumes it stays.
- **Fallback** and **runs** extend the paragraph step: a paragraph becomes runs, each with its own face, shaped separately; `ShapedGlyph` gains the face index and `TextOptions` the fallback families.
- **Color fonts** swap the per-glyph outline lookup in `AppendTo` and the ink box for layers; `Font`'s outline access is already per glyph.
- **Decorations** draw rectangles from `LineMetrics` (baseline, left, advance) and the font's underline and strikeout metrics.
- **Text on a path** replaces the single matrix `AppendTo` builds per glyph (scale, translation) with one that follows the path.
- **WOFF2** is one more case in `FontFile.Parse` (Brotli is in the BCL).

## ImageSharp names

| SixLabors.Fonts / ImageSharp.Drawing | OneImage.Text |
|---|---|
| `FontCollection.Add(path / stream)`, `AddCollection` | `FontCollection.Add(path / stream / memory)`, `AddCollection` |
| `FontFamily.CreateFont(size, style)` | `FontFamily.CreateFont(size, style)` |
| `RichTextOptions` / `TextOptions` | `TextOptions` (`Origin`, `WrappingLength`, `LineSpacing`, `TabWidth`, `Dpi`, alignments, `Kerning`) |
| `TextMeasurer.MeasureAdvance` / `MeasureBounds` / `MeasureSize` / `CountLines` | `TextMeasurer.MeasureAdvance` / `MeasureBounds` / `Measure(...).Bounds.Size` / `CountLines` |
| `TextMeasurer.TryMeasureCharacterBounds` | `TextMeasurer.GetCharacterBounds` (`Ink`) |
| `ctx.DrawText(options, text, brush, pen)` | `img.DrawText(text, options, brush, pen)` / `canvas.DrawText(...)` |
| `TextBuilder.GenerateGlyphs` | `TextBuilder.GeneratePath` |
| `TextRenderer` + `IGlyphRenderer` | `TextBuilder.GeneratePath`, then walk or transform the `Path` |
