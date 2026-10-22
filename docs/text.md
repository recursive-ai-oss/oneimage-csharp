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

Tables read: `cmap` (formats 0, 4, 6, 12, 13; the best Unicode subtable), `head`, `hhea`, `hmtx`, `maxp`, `OS/2`, `name`, `post`, `loca`/`glyf`, `CFF `, `kern` (format 0, Microsoft and Apple headers), `GDEF`, `GSUB` and `GPOS` (see [Shaping](#shaping)). Fonts are untrusted input: parsing is bounds-checked safe code, counts are checked against table lengths before anything is allocated, composite glyphs are flattened with an explicit stack (depth 8, 512 components), and the charstring interpreter bounds its operand stack, subroutine depth and operator count. A malformed font throws `FontFormatException` (an `ImageFormatException`), at load for the header tables and lazily, when the glyph data is first used, for the rest. Damaged `GDEF`, `GSUB` and `GPOS` tables never throw: what cannot be read is ignored (see [Shaping](#shaping)). `fuzz/run.sh font` covers all of it.

`FontMetrics` reports design units: `Ascender`, `Descender` (negative), `LineGap`, `LineHeight`, underline and strikeout, x-height and cap height. The vertical metrics are the OS/2 typographic ones when the font sets the `USE_TYPO_METRICS` flag, else `hhea`.

## Units and layout

`Font.Size` is in points. Pixels = points × `TextOptions.Dpi` / 72, and the default dpi is 72, so a size is the pixel height of the em square. Everything else (origin, wrapping length, results) is pixels, y down. There is no hinting and no rounding: advances, positions and bounds are unrounded floats, so a string's advance is the exact sum of its glyph advances in font units times the scale.

- **Lines.** Line breaks (LF, CR, CRLF, VT, FF, NEL, U+2028, U+2029) always start a line, except one at the very end of the text, which does not open an empty line. The line pitch is `Size × Dpi/72 × LineSpacing`; that is one em at the default spacing, not the font's own line height. The font's ascent and descent are centered in the line box: baseline = ascender − (natural line height − pitch) / 2.
- **Wrapping.** `WrappingLength` ≤ 0 means none. Lines end at the break opportunities of the Unicode line breaking algorithm (see [Line breaking and segmentation](#line-breaking-and-segmentation)). Spaces at the end of a line may hang past the limit and are left out of its width. By default a word longer than the limit keeps its own line and overflows (`WordBreaking.BreakWord` breaks it). Tabs advance `TabWidth` space widths (default 4).
- **Kerning** is on by default (`KerningMode.Normal`): the `kern` feature of the font (GPOS pair adjustment), or the legacy `kern` table when the font has no GPOS kerning. `KerningMode.None` switches the `kern` feature off; see [Shaping](#shaping).
- **Alignment.** `HorizontalAlignment` puts a box on the origin (left edge, center or right edge); the box is as wide as the widest line. `TextAlignment` Start keeps lines at the box's left edge. Center and End move each line by its slack against the wrapping length (the widest line when there is none), placing it in a box of that width positioned by the horizontal alignment. This reproduces ImageSharp's behavior, including that a centered block does not center its lines unless asked to. `VerticalAlignment` puts the block (lines × pitch tall) on the origin.
- **Measuring.** `TextMeasurer.Measure` gives `TextMetrics`: `Advance` (widest line without trailing spaces × lines × pitch; independent of origin and alignment), `Bounds` (the ink, exact curve extents, positioned), `LineCount` and per-line `LineMetrics`. `GetCharacterBounds` gives each character's (grapheme cluster's) advance box and ink box. Zero-width format characters (U+200B–U+200F, U+2060, U+FEFF, soft hyphen, bidi controls) are dropped from the layout.
- **Missing glyphs** draw the font's `.notdef` glyph. There is no fallback yet.

## Shaping

Text is shaped before it is laid out: code points go through the font's character map, then GSUB substitutions, then GPOS positioning, and the result is a run of glyphs with advances and offsets. Layout (wrapping, alignment, measuring, drawing) works on those glyphs only. The engine follows the OpenType specification and works for left-to-right Latin, Greek, Cyrillic and every other script that needs no joining or reordering.

- **Tables.** `GDEF` glyph classes, mark attachment classes and mark glyph sets. `GSUB` lookup types 1 single, 2 multiple, 3 alternate, 4 ligature, 5 context, 6 chained context (all three formats of each context), 7 extension and 8 reverse chaining single. `GPOS` types 1 single, 2 pair (formats 1 and 2), 3 cursive, 4 mark-to-base, 5 mark-to-ligature, 6 mark-to-mark, 7 context, 8 chained context and 9 extension. All lookup flags apply: ignore base glyphs, ligatures and marks, mark attachment type, and mark filtering sets (a filtering set wins over an attachment type). Device and variation-index tables inside value records and anchors are skipped, since text is unhinted and a variable font draws its default instance.
- **Lookup order.** Lookups of the enabled features run in lookup-list order, as the specification says, in two stages: `ccmp` and `locl` first, then everything else. So `smcp` on `fi` changes the letters to small capitals before `liga` looks for a ligature, and the ligature does not form; ImageSharp ligates first.
- **Script and language.** Each stretch of one script (Latin, Greek, Cyrillic; spaces, digits and punctuation join the stretch they are in; combining marks follow their base; nothing substitutes or kerns across a change of script) is shaped with that script's feature set, falling back to `DFLT`, `dflt`, then `latn` when the font lacks the script. `TextOptions.Language` (a BCP 47 tag such as `"sr"`, `"ro"`, `"tr"`, `"ca"`, or an OpenType language tag in capitals such as `"SRB"`) picks the font's language system for the script; a language the font does not have uses the script's default. The default is no language.
- **Features.** The defaults for horizontal text are `ccmp`, `locl`, `rlig`, `liga`, `clig`, `calt`, `rclt`, `kern`, `mark`, `mkmk` and `curs`. `TextOptions.Features` takes `FontFeature` settings in order, later ones replacing earlier ones: `FontFeature.On("smcp")`, `FontFeature.Off("liga")`, or a value (`new FontFeature("salt", 2)` picks the second alternate; a value past the font's alternates leaves the glyph alone). `Kerning = KerningMode.None` is the same as `Off("kern")`, and an explicit `kern` setting wins over it. A tag the font does not have does nothing. A feature outside the default stages (`smcp`, `onum`, `dlig`, `ss01`...) runs in the second stage with the defaults.
- **Marks.** A mark is placed from the anchors of the font's `mark` and `mkmk` lookups: on the base, on a ligature's component (a mark that sat between components in the text belongs to the component it followed), or on the mark before it. A combining sequence the font composes through `ccmp` (a base and a mark becoming one glyph, or several marks becoming one) is substituted before positioning. The offsets move the glyph's outline only: advances, wrapping and `GetCharacterBounds` boxes still follow the pen.
- **Clusters.** A glyph stands for the characters from its `Index` on; a ligature is one glyph for several characters, so `GetCharacterBounds` returns one box for `fi`, starting at the `f`. A zero width non-joiner (U+200C) ends a shaping run, so nothing ligates or kerns across it; a zero width joiner is dropped.
- **Limits.** Fonts are untrusted. Every read is bounds-checked and a subtable that cannot be read is skipped. Contexts nest lookups at most 6 deep. A run may grow to eight glyphs per character plus 256, and may attempt at most 65,536 + 1,024 per character subtable applications (and 2^26 overall; a subtable that fails to read counts as 2,048); a run that exhausts the budget keeps what it has and applies no further lookups. A damaged GSUB/GPOS/GDEF header means the table is ignored, as if the font had none. Nothing here throws.

What the engine does not do yet: right-to-left, vertical and joining scripts (Arabic, Indic, Southeast Asian shapers), canonical reordering and composition of combining marks (a mark sequence is shaped as typed; the font's own `ccmp` handles the compositions it has), contour-point anchors, feature ranges inside a string, Apple `morx`/`kerx` layout, and the `GSUB`/`GPOS` FeatureVariations table. A script-specific shaper plugs in as a `ScriptShaper` (its feature stages and a hook on the glyph run) without touching the lookup engine.

## Line breaking and segmentation

Where a wrapped line may end is decided by the Unicode line breaking algorithm, UAX #14, with the data of Unicode 17.0 (`UnicodeData.g.cs`, generated by `tools/gen_unicode_tables.py` from the Unicode Character Database and checked in; about 70 KiB of read-only tables, no runtime dependency). The default rules are used as written, with the small kana of class CJ treated as NS (the annex's strict reading). All of `LineBreakTest.txt` (19,338 cases) and `GraphemeBreakTest.txt` (766 cases) pass; the tests read the unmodified files from `tests/testdata/unicode`.

- **What breaks.** After spaces, hyphens (HY, HH) and en/em dashes, around the soft hyphen (U+00AD, a break after it, nothing drawn), after the zero width space, between ideographs and kana but not before closing punctuation or small kana, after a solidus before a letter ("a/b" in a URL or path), between emoji sequences and flag pairs but never inside one, and after hard breaks. It does not break in "$1,234.56", "-5.25", "12.5%", "(s)he", inside a word, after an opening bracket or quote even across spaces, before a closing bracket or quote, around the no-break space (U+00A0, U+202F), the word joiner (U+2060) or the zero width joiner, or inside a Korean syllable block. A line break character in the text always starts a new line first; the algorithm runs on each paragraph.
- **Grapheme clusters.** UAX #29 extended grapheme clusters (combining marks, emoji ZWJ sequences and modifiers, flags, Hangul jamo and syllables, Indic conjuncts) are never split: a line never starts inside one in any `WordBreaking` mode, and `GetCharacterBounds` returns one box per cluster (`Index` and `CodePoint` are the cluster's first code point; the box and ink cover the whole cluster).
- **`TextOptions.WordBreaking`** (needs a `WrappingLength`; names as in ImageSharp):
  - `Standard` (default): lines end only at the opportunities above.
  - `BreakAll`: any cluster boundary is an opportunity (not in front of white space), so lines are filled to the length. CJK text fills exactly too.
  - `KeepAll`: `Standard`, but no opportunity right after an East Asian wide or fullwidth character, so CJK text breaks only at spaces. Other text is unchanged.
  - `BreakWord`: `Standard`, except a word that does not fit even on a line of its own is moved to a fresh line and then broken between clusters so it fills lines instead of overflowing (the CSS `overflow-wrap: break-word` behavior).
- **`TextOptions.TextJustification`** (needs a `WrappingLength`): `InterWord` adds the same width to every space inside a line (U+0020 and U+00A0, not tabs) and `InterCharacter` adds the same width after every cluster but the last, in both cases until the line is exactly the wrapping length. Like ImageSharp, every line is stretched, the last one and lines before a line break included; a line with nothing to widen, or one that is already too long, is left alone. Positions agree with SixLabors.Fonts 2.1.3 to 0.0001 px for both modes and all three text alignments.
- **Cost.** Segmentation is linear, runs once per paragraph over pooled arrays and allocates nothing in steady state (tested). Table lookups are two-stage over 64-code-point blocks.

## Drawing

`DrawText(text, options, brush, pen, ...)` exists for `Image` (lazy: layout happens at the call, pixels when the image is read) and `Canvas` (in place), plus overloads taking a color. The optional parameters are the fill ones: `antialias`, `opacity`, `blend`, `composition`, `linear`. With both a brush and a pen the outline is drawn over the fill. Glyph outlines are filled with the non-zero rule. `TextBuilder.GeneratePath` returns the same path as an ordinary `Path`, for filling, stroking or transforming yourself.

## Agreement with ImageSharp

Checked against SixLabors.Fonts 2.1.3 and ImageSharp.Drawing 2.1.7 through their public APIs (tests in `tests/OneImage.Text.Tests`).

- Glyph outlines: the same contours and points (TrueType, CFF-CID and composite glyphs) to within 0.01 font unit. The reference starts contours at a different point and inserts zero-length lines; the comparison normalizes both.
- Advances and kerning: equal to 0.01 px at one pixel per font unit, for GPOS kerning, the `kern` table and CFF with GPOS.
- Line counts, advance size and ink bounds: equal (0.01 and 0.05 px) across sizes, dpi, wrapping lengths, line spacing and all alignment combinations in the test matrix.
- Rendering: black text on white, mean level difference 0.03 to 0.28 over the whole image (3 to 5 over pixels where either draws ink), maximum 27 to 106 levels at single edge pixels. Positions are identical; the rest is coverage. We compute exact-area coverage; ImageSharp's scanline rasterizer over- and under-covers some thin or touching edges (see [drawing.md](drawing.md)).

Differences you will meet, all deliberate or pending the later units:

- **Ligatures and wrapping.** We shape first and wrap on the shaped glyphs, so a line holding `fi`, `ffl` and the like is measured with the ligature glyph's own advance. ImageSharp substitutes the same ligatures but over-measures a line that holds one when it decides where to wrap (60 random 8-word texts with `fi`/`ff`/`fl` words: the line count differs in 33; the same texts without those letters: in none). A single line's advance is equal to 0.0001 px with and without ligatures.
- **Feature order and `numr`/`dnom`.** Lookups run in lookup-list order, so `smcp` before `liga` (above). ImageSharp leaves the digits alone when asked for `numr` or `dnom` on Noto Sans; we substitute the font's numerator and denominator forms, as the font defines them. Everything else we compared (ligatures, `ccmp` compositions, marks and stacked marks in Latin, Greek and Cyrillic, `smcp`, `c2sc`, `onum`, `lnum`, `tnum`, `pnum`, `zero`, `frac`, `sups`, `subs`, `ordn`, `case`, `ss03`, `ss04`, `salt`, `aalt`) gives the same glyph ids and origins to 0.002 font units.
- **Language** cannot be chosen in ImageSharp 2.1.3's `TextOptions`, so language forms (`locl`) are checked against the font's own tables, not against it.
- **Wrapping against ImageSharp.** Ordinary text (words separated by spaces, with the usual punctuation, quotation marks, brackets, numbers with separators, no-break spaces, Greek and Cyrillic) wraps at the same places in `Standard` and `KeepAll`: 1,590 combinations of five texts, three sizes and 53 wrapping lengths, none different; `BreakAll` and `BreakWord` agree in 95% and 91% of them. Where opportunities are not after spaces the two part, for these reasons (each pinned by a test in `tests/OneImage.Text.Tests/WrapReferenceTests.cs`):
  - ImageSharp fills a line exactly when it ends at a space, but after a hyphen, a soft hyphen, a zero width space or an ideograph it also wants room for the next character, so it ends the line one opportunity early: with "mm-mm-mm" it needs the width of one more "m" (18.75 px at 20 px) than the line occupies; CJK text holds one character fewer per line. We fill exactly: a line is accepted when its text fits. This explains most (83%, against 57% for an exact fill) of the differences on hyphenated, soft-hyphenated, zero-width-space and CJK text; the rest is how ImageSharp treats soft hyphens and some hyphen cases.
  - ImageSharp ends a line between a prefix or sign and its number ("$ | 1,234.56", "- | 5.25"); UAX #14 rule LB25 keeps them together, as do we.
  - ImageSharp implements an older revision of the line breaking rules (UAX #14 revision 37). Under rules LB15a-d, added since, an opening quotation mark stays with the word after it even across spaces: "word “ quoted" wraps as "word" then "“ quoted" here, and as "word “" then "quoted" there.
  - ImageSharp never breaks after a solidus, so a long URL stays on one line; we break after "/" before a letter as UAX #14 allows, which wraps URLs and paths.
  - `KeepAll` in ImageSharp stops wrapping after the first line that cannot be broken to fit, and its `BreakWord` starts a too-long word on the current line; ours moves it to a fresh line first.
- **Tabs** advance `TabWidth` spaces. ImageSharp's `TabWidth` had no effect on the font we probed, which has no tab glyph.
- **Empty lines.** Text that starts with a line break measures two lines of one pitch each; ImageSharp adds extra height for the leading empty line. A line of only spaces keeps its full width here; ImageSharp reported one space's width for two. Text with no ink has zero-size bounds, where ImageSharp reports a box.
- **No text decorations, rich-text runs or text along a path yet.**

## Where the next pieces plug in

`Layout/TextLayout.cs` is the whole layout in four steps with narrow seams between them.

- **Shaping** is `Shape` calling `Shaping/TextShaper`: code points in, `ShapedGlyph`s (glyph id, cluster, advance with kerning, offsets) out. A script-specific shaper (Arabic joining, Indic and Universal Shaping Engine reordering) is a `ScriptShaper` with its own feature stages; the lookup engine (`Shaping/Engine`) is shared.
- **Line breaking** is `PrepareBreaks` and `CanBreakBefore`: the break opportunities and cluster starts of a paragraph are computed from its text (`Unicode/LineBreaker.cs`, `Unicode/Graphemes.cs`) and reach the glyphs only through each glyph's text offset, so a shaper that merges or reorders glyphs needs no change here. Hyphenation, language-tagged tailoring and dictionary breaking for Thai, Lao, Khmer and Burmese (their characters are treated as letters) would extend `LineBreaker`.
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
| `RichTextOptions` / `TextOptions` | `TextOptions` (`Origin`, `WrappingLength`, `LineSpacing`, `TabWidth`, `Dpi`, alignments, `Kerning`, `WordBreaking`, `TextJustification`, `Language`, `Features`) |
| `TextOptions.FeatureTags` (`Tag`) | `TextOptions.Features` (`FontFeature`, with a value and off switches) |
| `TextMeasurer.MeasureAdvance` / `MeasureBounds` / `MeasureSize` / `CountLines` | `TextMeasurer.MeasureAdvance` / `MeasureBounds` / `Measure(...).Bounds.Size` / `CountLines` |
| `TextMeasurer.TryMeasureCharacterBounds` | `TextMeasurer.GetCharacterBounds` (`Ink`) |
| `ctx.DrawText(options, text, brush, pen)` | `img.DrawText(text, options, brush, pen)` / `canvas.DrawText(...)` |
| `TextBuilder.GenerateGlyphs` | `TextBuilder.GeneratePath` |
| `TextRenderer` + `IGlyphRenderer` | `TextBuilder.GeneratePath`, then walk or transform the `Path` |
