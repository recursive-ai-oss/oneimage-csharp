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

`FontCollection` loads TrueType (glyf, quadratic) and OpenType with CFF outlines (CFF1: name-keyed and CID-keyed, local and global subroutines, flex), TrueType Collections (`AddCollection` adds every face) WOFF 1.0 (decompressed with the library's own inflate) and WOFF 2.0 (see [WOFF2](#woff2)). CFF2 is not read, and Type 1 hinting is ignored; TrueType instructions run only when asked for (see [Hinting](#hinting)). A variable font draws its default instance. Add from a path, a `Stream` or memory; faces with the same family name join one `FontFamily`, one face per `FontStyle`. `FontFamily.CreateFont(size, style)` picks the closest face when the style is missing and never synthesizes bold or slant.

Tables read: `cmap` (formats 0, 4, 6, 12, 13; the best Unicode subtable), `head`, `hhea`, `hmtx`, `maxp`, `OS/2`, `name`, `post`, `loca`/`glyf`, `CFF `, `kern` (format 0, Microsoft and Apple headers), `GDEF`, `GSUB` and `GPOS` (see [Shaping](#shaping)), and `COLR` and `CPAL` (see [Color fonts](#color-fonts)). Fonts are untrusted input: parsing is bounds-checked safe code, counts are checked against table lengths before anything is allocated, composite glyphs are flattened with an explicit stack (depth 8, 512 components), and the charstring interpreter bounds its operand stack, subroutine depth and operator count. A malformed font throws `FontFormatException` (an `ImageFormatException`), at load for the header tables and lazily, when the glyph data is first used, for the rest. Damaged `GDEF`, `GSUB`, `GPOS`, `COLR` and `CPAL` tables never throw: what cannot be read is ignored (see [Shaping](#shaping)). `fuzz/run.sh font` covers all of it, and `fuzz/run.sh brotli` the decompressor under WOFF2.

`FontMetrics` reports design units: `Ascender`, `Descender` (negative), `LineGap`, `LineHeight`, underline and strikeout, x-height and cap height. The vertical metrics are the OS/2 typographic ones when the font sets the `USE_TYPO_METRICS` flag, else `hhea`.

## WOFF2

`FontCollection.Add` and `AddCollection` read WOFF 2.0 files (W3C WOFF 2.0, 2018) from a path, a stream or memory, whether they hold one font or a collection (`AddCollection` adds every face; shared tables are decoded once). Everything the format defines is read: the table directory with known and custom tags, the Brotli-compressed table block, the transformed `glyf`/`loca` pair (the streams of contour counts, points, flags, coordinates, composites, boxes and instructions, with the overlap flags) and the transformed `hmtx` (side bearings rebuilt from the glyph boxes). The metadata and private blocks are skipped. A transformed `glyf` gets a plain `loca` back in the format the header names, and `head.indexToLocFormat` is corrected if the file disagrees with itself. Reconstructed glyphs are written with plain 16-bit coordinates, so the table can be larger than the original; the outlines are the same.

The decompressor is the library's own Brotli (RFC 7932, decode only, safe managed code, with the static dictionary embedded as a resource); the BCL's `BrotliDecoder` wraps a native library and is not used. The Brotli stream is untrusted like everything else in the file: the output buffer grows with the data actually produced and never to a size read from the file, and it stops at the sum of the table sizes the directory declares (each at most 128 MiB, all at most 256 MiB) and at 2048 bytes of output per input byte (plus 64 KiB), so a tiny file that declares a huge table fails with a `FontFormatException` after allocating a few kilobytes. The window is the output itself, so there is no separate history buffer. Unknown table transforms, a missing composite box, a mismatched `glyf`/`loca` pair and extra or missing table bytes are rejected, as the specification says.

Measured on the 45 KB WOFF2 of the Noto Sans shaping subset (shared workstation, load 6 to 12, .NET 10.0.11, BenchmarkDotNet, local smoke numbers): loading it takes 1.0 ms and 557 KB against SixLabors.Fonts 2.1.3's 1.35 ms and 2.5 MB. The Brotli decoder alone runs at roughly 330 MB/s on text and 0.9 GB/s on font-like binary data, about half the speed of the native decoder behind `System.IO.Compression`.

## Units and layout

`Font.Size` is in points. Pixels = points × `TextOptions.Dpi` / 72, and the default dpi is 72, so a size is the pixel height of the em square. Everything else (origin, wrapping length, results) is pixels, y down. By default there is no hinting and no rounding ([Hinting](#hinting) is opt-in and changes drawn outlines only): advances, positions and bounds are unrounded floats, so a string's advance is the exact sum of its glyph advances in font units times the scale.

- **Lines.** Line breaks (LF, CR, CRLF, VT, FF, NEL, U+2028, U+2029) always start a line, except one at the very end of the text, which does not open an empty line. The line pitch is `Size × Dpi/72 × LineSpacing`; that is one em at the default spacing, not the font's own line height. The font's ascent and descent are centered in the line box: baseline = ascender − (natural line height − pitch) / 2.
- **Wrapping.** `WrappingLength` ≤ 0 means none. Lines end at the break opportunities of the Unicode line breaking algorithm (see [Line breaking and segmentation](#line-breaking-and-segmentation)). Spaces at the end of a line may hang past the limit and are left out of its width. By default a word longer than the limit keeps its own line and overflows (`WordBreaking.BreakWord` breaks it). Tabs advance `TabWidth` space widths (default 4).
- **Kerning** is on by default (`KerningMode.Normal`): the `kern` feature of the font (GPOS pair adjustment), or the legacy `kern` table when the font has no GPOS kerning. `KerningMode.None` switches the `kern` feature off; see [Shaping](#shaping).
- **Alignment.** `HorizontalAlignment` puts a box on the origin (left edge, center or right edge); the box is as wide as the widest line. `TextAlignment` Start keeps lines at the box's left edge. Center and End move each line by its slack against the wrapping length (the widest line when there is none), placing it in a box of that width positioned by the horizontal alignment. This reproduces ImageSharp's behavior, including that a centered block does not center its lines unless asked to. `VerticalAlignment` puts the block (lines × pitch tall) on the origin.
- **Measuring.** `TextMeasurer.Measure` gives `TextMetrics`: `Advance` (widest line without trailing spaces × lines × pitch; independent of origin and alignment), `Bounds` (the ink, exact curve extents, positioned), `LineCount` and per-line `LineMetrics`. `GetCharacterBounds` gives each character's (grapheme cluster's) advance box and ink box. Zero-width format characters (U+200B–U+200F, U+2060, U+FEFF, soft hyphen, bidi controls) are dropped from the layout.
- **Missing glyphs** draw the font's `.notdef` glyph, unless a fallback family has the character (see [Font fallback](#font-fallback)).

## Shaping

Text is shaped before it is laid out: code points go through the font's character map, then GSUB substitutions, then GPOS positioning, and the result is a run of glyphs with advances and offsets. Layout (wrapping, alignment, measuring, drawing) works on those glyphs only. The engine follows the OpenType specification and works for every script that needs no reordering, plus Arabic-style joining and Hebrew (see [Arabic and Hebrew](#arabic-and-hebrew)) and the Indic, Southeast Asian and USE scripts (see [Complex scripts](#complex-scripts-indic-southeast-asian-and-the-universal-shaping-engine)).

- **Tables.** `GDEF` glyph classes, mark attachment classes and mark glyph sets. `GSUB` lookup types 1 single, 2 multiple, 3 alternate, 4 ligature, 5 context, 6 chained context (all three formats of each context), 7 extension and 8 reverse chaining single. `GPOS` types 1 single, 2 pair (formats 1 and 2), 3 cursive, 4 mark-to-base, 5 mark-to-ligature, 6 mark-to-mark, 7 context, 8 chained context and 9 extension. All lookup flags apply: ignore base glyphs, ligatures and marks, mark attachment type, and mark filtering sets (a filtering set wins over an attachment type). Device and variation-index tables inside value records and anchors are skipped, since text is unhinted and a variable font draws its default instance.
- **Lookup order.** Lookups of the enabled features run in lookup-list order, as the specification says, in two stages: `ccmp` and `locl` first, then everything else. So `smcp` on `fi` changes the letters to small capitals before `liga` looks for a ligature, and the ligature does not form; ImageSharp ligates first.
- **Script and language.** Each stretch of one script (Latin, Greek, Cyrillic; spaces, digits and punctuation join the stretch they are in; combining marks follow their base; nothing substitutes or kerns across a change of script) is shaped with that script's feature set, falling back to `DFLT`, `dflt`, then `latn` when the font lacks the script. `TextOptions.Language` (a BCP 47 tag such as `"sr"`, `"ro"`, `"tr"`, `"ca"`, or an OpenType language tag in capitals such as `"SRB"`) picks the font's language system for the script; a language the font does not have uses the script's default. The default is no language.
- **Features.** The defaults for horizontal text are `ccmp`, `locl`, `rlig`, `liga`, `clig`, `calt`, `rclt`, `kern`, `mark`, `mkmk` and `curs`. `TextOptions.Features` takes `FontFeature` settings in order, later ones replacing earlier ones: `FontFeature.On("smcp")`, `FontFeature.Off("liga")`, or a value (`new FontFeature("salt", 2)` picks the second alternate; a value past the font's alternates leaves the glyph alone). `Kerning = KerningMode.None` is the same as `Off("kern")`, and an explicit `kern` setting wins over it. A tag the font does not have does nothing. A feature outside the default stages (`smcp`, `onum`, `dlig`, `ss01`...) runs in the second stage with the defaults.
- **Marks.** A mark is placed from the anchors of the font's `mark` and `mkmk` lookups: on the base, on a ligature's component (a mark that sat between components in the text belongs to the component it followed), or on the mark before it. A combining sequence the font composes through `ccmp` (a base and a mark becoming one glyph, or several marks becoming one) is substituted before positioning. The offsets move the glyph's outline only: advances, wrapping and `GetCharacterBounds` boxes still follow the pen.
- **Clusters.** A glyph stands for the characters from its `Index` on; a ligature is one glyph for several characters, so `GetCharacterBounds` returns one box for `fi`, starting at the `f`. A zero width non-joiner (U+200C), a zero width space (U+200B) and a soft hyphen (U+00AD) end a shaping run, so nothing ligates or kerns across them (the last two are line break opportunities, which a ligature would otherwise swallow); the other zero width format characters are dropped. A base and its combining marks, and the glyphs of one character the font splits into several, share one box, so wrapping and `GetCharacterBounds` work on grapheme clusters whatever the glyph run looks like; a line never ends inside a ligature.
- **Limits.** Fonts are untrusted. Every read is bounds-checked and a subtable that cannot be read is skipped. Contexts nest lookups at most 6 deep. A run may grow to eight glyphs per character plus 256, and may attempt at most 65,536 + 1,024 per character subtable applications (and 2^26 overall; a subtable that fails to read counts as 2,048); a run that exhausts the budget keeps what it has and applies no further lookups. A damaged GSUB/GPOS/GDEF header means the table is ignored, as if the font had none. Nothing here throws.

What the engine does not do yet: canonical reordering and composition of combining marks (a mark sequence is shaped as typed; the font's own `ccmp` handles the compositions it has), contour-point anchors, feature ranges inside a string, Apple `morx`/`kerx` layout, and the `GSUB`/`GPOS` FeatureVariations table. A script-specific shaper plugs in as a `ScriptShaper` (its feature stages and a hook on the glyph run) without touching the lookup engine.

## Direction and right-to-left text

Text is split into paragraphs at line feeds, and each paragraph runs the Unicode Bidirectional Algorithm (UAX #9, Unicode 17 data; the official `BidiTest.txt` and `BidiCharacterTest.txt` pass in full, and they are committed as test data). `TextOptions.TextDirection` sets the base direction: `Auto` (the default) takes each paragraph's direction from its first strong character and falls back to left to right; `LeftToRight` and `RightToLeft` force the paragraph level, and the algorithm still reorders embedded runs of the other direction. Plain left-to-right text, with no right-to-left capable character, skips the algorithm entirely.

- **Reordering.** Runs are reordered per wrapped line (rules L1 and L2), on the characters of the line, so a line break in the middle of a mixed paragraph reorders each line on its own. Trailing white space of a line takes the paragraph level and hangs past the line's visible end. Brackets are mirrored (rule L4 and bracket pairs, rule N0), by code point, or through the font's `rtlm` feature when it has one. Explicit embeddings, overrides and isolates (up to depth 125) are honored; their control characters have no advance and no glyph.
- **Alignment.** `TextAlignment.Start` and `End` follow the paragraph direction: `Start` is the right edge of a right-to-left paragraph. `HorizontalAlignment` is the physical left, center or right of the block and ignores direction. Justification stretches every line but the last and counts hanging spaces out.
- **Metrics.** `Lines` report characters in logical order: `Start` and `Length` are always a range of the string. `GetCharacterBounds` boxes are placed where the characters are drawn. A line never breaks inside a grapheme cluster.
- **Limits.** Embedding depth is bounded at 125 and the bracket stack at 63, as the algorithm says; run counts are bounded by the text length. Hostile text (160,000 characters of nested controls, brackets or alternating directions) lays out in time linear in its length (45 to 255 ms in the tests), and the `bidi` fuzz target covers it.

## Arabic and Hebrew

Arabic, Syriac and N'Ko go through a joining shaper. Each letter's form comes from the Joining_Type of its neighbours (transparent marks and ZWJ/ZWNJ are looked through or break the join as Unicode says), and the form features run in this order: `ccmp`, `locl`, `rtlm`; then `isol`, `fina`, `fin2`, `fin3`, `medi`, `med2`, `init` (each only on the letters of its form); then `rlig`; then `calt`; then `liga`, `clig`, `rclt`, `mset`; and `kern`, `mark`, `mkmk`, `curs` for positioning. Lam-alef and other ligatures come from the font's lookups, so a font without them gets plain joined forms. Digits after Arabic text pick up the Arabic script's lookups. Syriac Alaph forms (`fin2`, `fin3`, `med2` selection) are not implemented. Cursive attachment in right-to-left runs is implemented but no test font has `curs`, so it is untested.

Hebrew needs no joining: the generic shaper and the bidirectional algorithm place the letters, and points and cantillation marks attach through the font's `mark` and `mkmk` lookups. A mark keeps its offset from its base when a run is reversed, so marks stay on their letters.

Kerning does not cross a change of direction. The script of digits, spaces and punctuation is taken from the paragraph's neighbouring letters, so they use the lookups of the text around them.

### Agreement with SixLabors.Fonts

Checked against SixLabors.Fonts 2.1.3 through its public API, on one merged Noto Sans / Noto Sans Arabic / Noto Sans Hebrew subset (`tests/testdata/fonts/NotoSans-Bidi-Subset.ttf`, SIL Open Font License) covering Arabic joining forms, lam-alef, marks, tatweel, Arabic-Indic digits, Hebrew with points, and mixed English, Arabic and Hebrew paragraphs with numbers and brackets, in `Auto` and `RightToLeft`. Glyph ids are equal; glyph origins agree to 0.01 px except in the cases below. Line counts, line widths and the glyph order of each line are equal for four mixed paragraphs at four wrapping lengths.

Deliberate differences:

- **(a) Kerning across Hebrew points.** The font's kern lookup ignores marks, so we kern the letters on either side of a point; SixLabors.Fonts does not. Up to 2 px at 40 px; identical with kerning off.
- **(b) The space after a Hebrew letter** across a change of direction is 10 font units (0.4 px at 40 px) wider in SixLabors.Fonts; we use the font's advance.
- **(c) Direction controls.** SixLabors.Fonts gives embedding, isolate and override controls a 0.6 em advance each; we give them none. Glyph ids still agree.
- **(d) Forced direction.** With `TextDirection.LeftToRight` SixLabors.Fonts does no reordering at all, and with `RightToLeft` it reverses the whole line, even Latin words and digits. We apply UAX #9 with the paragraph level forced, so Latin words and numbers in a right-to-left paragraph stay readable.
- **(e) One direction per text or per paragraph.** SixLabors.Fonts takes one direction for the whole string; we take it per paragraph (UAX #9 rule P1), so a string with an English line and an Arabic line lays out each in its own direction.
- **(f) Alignment of wrapped right-to-left paragraphs** that mix Latin words or numbers: SixLabors.Fonts left-aligns some lines of such a paragraph; every line of ours starts at the paragraph's start edge, the right.
- **(g) Font fallback.** With a fallback face SixLabors.Fonts applies the primary font's GDEF and GPOS to fallback glyph ids, and digits after Hebrew in a Hebrew-primary font come out with zero advance. We shape each face with its own tables.

## Complex scripts: Indic, Southeast Asian and the Universal Shaping Engine

Three shapers sit on the `ScriptShaper` seam, chosen by the script of a run (tags come from generated Unicode tables, `tools/gen_unicode_tables.py`, which also derives Indic_Syllabic_Category, Indic_Positional_Category and the canonical two-part decompositions from the Unicode Character Database 17.0.0).

- **Indic shaper:** Devanagari, Bengali, Gurmukhi, Gujarati, Oriya, Tamil, Telugu, Kannada and Malayalam, following Microsoft's Indic script-development documents (the v2 tags `dev2`, `bng2`... first, then the v1 tags). Text is cut into syllables (consonant clusters with halants, ZWJ/ZWNJ, matras, nukta, modifiers, standalone vowels), split vowels are decomposed, the base consonant is found, a reph is moved, pre-base matras go to the front, and features run in the documented order: `locl`, `ccmp`, then `nukt`, `akhn`, `rphf`, `rkrf`, `pref`, `blwf`, `abvf`, `half`, `pstf`, `vatu`, `cjct`, then `init`, `pres`, `abvs`, `blws`, `psts`, `haln`, `dist`, `abvm`, `blwm` with the default positioning features. A mark with no base gets a dotted circle (U+25CC) as its base.
- **Universal Shaping Engine shaper:** Khmer, Myanmar, Sinhala, Tibetan, Balinese, Javanese and the other scripts of the USE list, from the USE specification (syllable categories from the Indic_Syllabic_Category and Indic_Positional_Category properties) with Khmer and Myanmar specifics: coeng + ro, pre-base vowels and medials, the Khmer split vowels (U+17BE, 17BF, 17C0, 17C4, 17C5 become U+17C1 plus a sign), kinzi and stacked consonants.
- **Thai and Lao:** SARA AM (U+0E33, U+0EB3) is split into NIKHAHIT and SARA AA, and the nikhahit moves in front of the tone mark that precedes it, as the Thai script document says. Marks are placed by the font's `mark`/`mkmk` lookups and contextual substitutions.
- **Line breaking** for Thai, Lao, Khmer and Myanmar stays UAX #14 without dictionaries: their letters are class SA and are treated as letters, so there is a break opportunity only at spaces, punctuation and explicit zero width spaces, not inside a word. Text without spaces therefore wraps only where the author put a ZWSP. Grapheme clusters (UAX #29) keep a syllable's marks with their base.
- **Limits.** A syllable is cut after 48 glyphs; runs over 4,096 glyphs are shaped in pieces of 2,048 glyphs cut at syllable boundaries, so the cost is linear (400,000 hostile characters shape in under 2 s). Reordering and dotted circle insertion are bounded by the syllable cut. Fonts with damaged tables behave as for any script (see Shaping).
- **Choices that follow the documents, not the reference.** ZWJ and ZWNJ are never skipped by lookup flags; their glyphs are removed before positioning. ZWJ after a consonant and halant asks for the half form, ZWNJ denies it. A reph forms only when the font's `rphf` changes Ra + halant. Malayalam needs a ZWJ for a reph. The base consonant is found with probes of the font's `pref`, `blwf` and `pstf` lookups. A syllable's characters share one cluster value.

### Agreement with SixLabors.Fonts

Checked against SixLabors.Fonts 2.1.3 through its public API with 17 Noto subsets (`tests/testdata/fonts/Noto*-Subset-Regular.ttf`, SIL Open Font License, made by `tools/TextFonts/make-complex-fonts.py`) on 1,042 cases (`tests/testdata/complex/*.txt`: matras, reph, half forms, conjuncts, nukta, joiners, broken clusters, Tamil/Malayalam split vowels, Khmer coeng and pre-base vowels, Myanmar medials, Sinhala and Tibetan stacks, SARA AM). Glyph ids must be equal and glyph origins within 0.01 font units. The reference throws on 26 of the cases (NullReferenceException or IndexOutOfRangeException inside SixLabors.Fonts); those are not compared.

| script | cases | exact match | reference throws | deliberate differences |
|---|---|---|---|---|
| Devanagari | 157 | 149 | 0 | 8 |
| Bengali | 83 | 75 | 0 | 8 |
| Gurmukhi | 57 | 43 | 11 | 3 |
| Gujarati | 75 | 65 | 1 | 9 |
| Oriya | 67 | 63 | 0 | 4 |
| Tamil | 76 | 73 | 0 | 3 |
| Telugu | 75 | 61 | 4 | 10 |
| Kannada | 71 | 58 | 0 | 13 |
| Malayalam | 89 | 76 | 5 | 8 |
| Khmer | 54 | 43 | 5 | 6 |
| Myanmar | 39 | 16 | 0 | 23 |
| Sinhala | 32 | 26 | 0 | 6 |
| Tibetan | 31 | 25 | 0 | 6 |
| Thai | 45 | 41 | 0 | 4 |
| Lao | 27 | 25 | 0 | 2 |
| Balinese | 35 | 31 | 0 | 4 |
| Javanese | 29 | 27 | 0 | 2 |
| total | 1,042 | 897 | 26 | 119 |

Every difference is listed in `tests/testdata/complex/known-differences.txt` with a reason; the test fails if a listed case starts to match or an unlisted one differs. The reasons, and the rule each follows:

- **broken-cluster (61).** A mark without a base: we insert the dotted circle first, in logical order, and the mark follows it (a pre-base matra moves in front of the circle as it would in front of a consonant), as the Indic and USE documents describe. The reference puts the circle after the mark.
- **myanmar-reorder (21).** The reference does no reordering for Myanmar. We follow USE: the pre-base vowel E (U+1031) and medial Ra (U+103C) move before the consonant, a virama joins two consonants into the font's stacked form, and a consonant + asat + virama before a consonant becomes the font's kinzi. Checked on the glyph runs: for U+1000 U+1039 U+1000 we draw the stacked glyph where the reference draws the base, a visible virama and the base again; for U+1000 U+1031 the vowel comes first; for U+1004 U+103A U+1039 U+1000 we draw the font's kinzi glyph where the reference draws raw letters. Medial ya + wa (U+103B U+103D) uses the font's own ligature for the pair.
- **tibetan-extra-glyph (4).** For U+0F40 followed by U+0F73 or U+0F81 (and the sequence with U+0F71 U+0F72) we output the consonant with aa, then the vowel sign; the reference appends an extra dotted circle glyph (U+25CC), treating a valid stack as a broken one. We do not.
- **joiner-kerning (4).** ZWJ/ZWNJ glyphs are removed after substitution, so pair adjustment sees the neighbours next to each other. The reference positions with the joiner in between.
- **ignore-marks-pair (4), below-mark-offset (3).** Malayalam i/ii: a pair lookup with IgnoreMarks does not apply to a second glyph the GDEF table calls a mark (OpenType lookup flags); the reference applies it (5 font units). Gujarati u/uu/vocalic r: the font's contextual `dist`/`blwm` single positioning applies in ours and not in the reference.
- **sara-am (5).** The nikhahit goes in front of the tone mark (Thai script document); the reference leaves the tone mark and nikhahit overlapping.
- **kannada-conjunct (3).** We form the font's conjunct and conjunct + matra ligatures; the reference places the matra before the below-base consonant and breaks the ligature.
- **bengali-form (4), contextual-form (8), prebase-reorder (2).** The font's contextual variants or the ordering follow the font's tables and the documents in ours; the reference skips them. Checked on the glyph runs for Bengali ka + hasant + ka (we form the font's conjunct, the reference draws ka, a visible hasant and ka), Sinhala ka + al-lakuna + ZWJ + ra + e (the left vowel comes first in ours) and Khmer coeng + ka. The remaining members (Balinese, Javanese, Thai tone-mark variants, Sinhala ra + al-lakuna + ZWJ alone) differ because ours applies the font's substitution; we did not derive each one from a rule in the documents, so treat those as unproven and not as known errors of the reference.

Tolerances are exact for glyph ids and 0.01 font units for origins (lossless layout arithmetic). A font with the older v1 script tags only is shaped through the v1 tags; the v1-specific behaviors of the older engines (for example the pre-v2 reph handling) are not reproduced.

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

## Color fonts

Color glyphs are on by default, as in SixLabors.Fonts (whose `TextOptions.ColorFontSupport` defaults to its COLR format): `TextOptions.ColorFontSupport` is `ColorFontSupport.MicrosoftColrFormat`, so `DrawText` on `Image` and `Canvas` paints the layers of COLR glyphs when the font has them. Set `ColorFontSupport.None` to draw every glyph as a plain outline filled with the brush. Fonts without COLR are not affected:

```csharp
var options = new TextOptions(font) { ColorPaletteIndex = 1 };            // color on by default; palette 1 of the font
img.DrawText("\u2600 sunny", options, Brush.Solid(new Rgba32(0, 0, 0, 255)));
```

- **What is read.** `COLR` version 0 (a base glyph is a stack of layer glyphs, painted bottom to top) with `CPAL` palettes (versions 0 and 1; the palette labels and types are ignored). `COLR` version 1 paint graphs are painted too, see [COLR version 1](#colr-version-1).
- **Palette.** `ColorPaletteIndex` (default 0) selects the `CPAL` palette; an index past the last palette selects palette 0, and `Font.ColorPaletteCount` tells how many there are. A layer whose entry does not exist is left out.
- **Foreground layers.** Palette index 0xFFFF means the text color: those layers are filled with the brush you pass, and nothing is drawn for them when there is none. Layers with a palette color are painted with it whatever the brush is (also when you pass only a pen). `opacity`, `blend`, `composition`, `antialias` and `linear` apply to every layer.
- **Pen.** The pen outlines the plain glyphs and the foreground layers, over all fills, as SixLabors.Fonts does; layers with their own color get no outline.
- **Order.** Plain glyphs first, then layer 0 of every color glyph, then layer 1, and so on, with layers of equal index and color merged into one fill. Colors of glyphs that overlap each other can therefore stack differently from glyph-by-glyph painting.
- **Layout is unchanged:** advances, wrapping and measuring use the base glyph. `TextBuilder.GeneratePath` and measuring never depend on `ColorFontSupport`; text as a path is the monochrome outline. `Font.HasColorGlyph(codePoint)` tells whether a code point has layers.
- **Damaged tables** are ignored and the font then has no color glyphs. With `ColorFontSupport.None`, or in a font or a text without color glyphs, the output is byte for byte the monochrome path.
- **In runs and fallback faces.** Color glyphs work wherever a glyph comes from: a run's own font, a font of a different size, or a fallback face. The foreground entry (0xFFFF) takes the brush of the run that owns the character (the brush you pass when the run has none), layers with a palette color keep that color, and the run's pen (or the one you pass) outlines the plain and foreground parts after all fills. The palette is `ColorPaletteIndex` of the options, applied to every face (an index past a face's last palette selects its palette 0).

## COLR version 1

Fonts with a `COLR` version 1 table (`BaseGlyphList`, `LayerList`, optional `ClipList`) paint their paint graphs when `ColorFontSupport` is on (the default). Glyphs that have only version 0 records still go through the layer path above; a version 1 glyph takes precedence when a font has both.

- **What is painted.** Solid fills; linear, radial and sweep gradients (with extend modes); glyph clips; layers; `PaintTransform`/`Translate`/`Scale`/`Rotate`/`Skew` (with their around-center forms); `PaintComposite` with all Porter-Duff and blend modes (`PLUS` is mapped to additive blending); `PaintColrGlyph` reuse. The glyph is drawn into a group the size of its ink and the group is filled onto the target, so opacity, `blend` and clipping apply to the whole glyph. The foreground color (palette index 0xFFFF) takes the run's brush; for a non-solid brush the color is sampled at the glyph's top-left pixel, and with no brush the foreground stops are transparent.
- **Budgets.** A paint graph is flattened with an explicit stack to at most 4096 nodes and depth 64; exponential sharing, very deep graphs, truncated tables and cycles cannot exhaust time or memory. A graph over budget, or a cycle, draws the plain glyph outline instead (Skia draws nothing for a cycle).
- **Differences.** Variable (`Var`) paint formats use their default values (no variation instance). Gradients interpolate non-premultiplied (straight) color, as Skia does; stops are rescaled to the first-to-last span. Edges of clip boxes are anti-aliased. Layout, measuring and `TextBuilder.GeneratePath` ignore paint graphs, as for version 0.
- **Agreement.** SixLabors.Fonts 2.1.3 has no version 1 support, so the reference is SkiaSharp (Skia's COLRv1 painter) on the googlefonts/color-fonts test font (`ColrV1-TestGlyphs.ttf`, Apache-2.0): 211 glyphs at 64 px, mean difference 0.36 levels (205 of 213 glyphs below 1.0; the worst non-cycle glyph 2.5, among the clip-box glyphs), excluding the two cyclic glyphs. The Skia comparison runs on Linux x64 only and is skipped where the native asset is missing.

## Vertical layout

`TextOptions.LayoutMode` takes the members of SixLabors.Fonts' `LayoutMode`: `HorizontalTopBottom` (default), `HorizontalBottomTop` (lines stack upward from the origin; each line still reads left to right), and the vertical modes `VerticalLeftRight`, `VerticalRightLeft`, `VerticalMixedLeftRight` and `VerticalMixedRightLeft`. In a vertical mode a line is a column: `WrappingLength` limits its height, `TextAlignment` works along it, and columns stack to the right (`LeftRight`) or left (`RightLeft`). `Measure(...).Advance` is (columns width, longest column).

- **Metrics.** Upright glyphs advance by `vmtx` (else the line height) and sit by `VORG` or the top side bearing (else one ascender below the cell top); the `vert` and `vrt2` features are on and `kern` is off for them, unless `TextOptions.Features` says otherwise. The plain vertical modes stand every character upright. The mixed modes follow UAX #50 Vertical_Orientation (Unicode 17): `U` and `Tu` stand, others lie rotated a quarter turn clockwise and advance by their horizontal advance. `Tr` and `Tu` are treated as `R` and `U` (no per-font test for a vertical form).
- **Placement.** Upright ideographs and kana (`U`, `Tu`) fill the em square, centered in the column; upright characters that would otherwise lie down (Latin in the plain modes) are centered by their ink. The rule was derived by probing SixLabors.Fonts 2.1.3.
- **Not applied in vertical modes:** bidirectional reordering, decorations and text on a path.
- **Agreement.** Against SixLabors.Fonts 2.1.3 on the CJK and Latin test fonts, all five non-default modes, wrapping off and at 100 px, at 40 and 24 px: 296 of 296 measurement and per-character ink cases within 0.02-0.03 px; 432 of 432 alignment cases (3 x 3 x 3 alignments, two wraps, two texts, four vertical modes).
- **Differences.** (1) In the mixed modes, when one column holds both upright and rotated characters, the reference widens the column by 0.282 em with the CJK test font (24 cases; cause not derived); ours keeps the column at the line pitch. (2) Wrapped CJK text: the reference ends a line one opportunity early after an ideograph, as in horizontal text (see above). (3) The behavior listed under "Not applied".

## Hinting

`TextOptions.HintingMode` is `None` by default. `HintingMode.Standard` grid-fits TrueType glyphs at the drawing size by running the font's own instructions (`fpgm`, `prep`, `cvt `, glyph programs), in safe code, with the rules of backward-compatible rendering as DirectWrite does for grayscale text: only y positions move, `GETINFO` reports interpreter version 40, and no point moves after both `IUP` instructions have run.

- **What changes.** Drawn outlines (`DrawText`, `TextBuilder.GeneratePath`) only. Measuring, advances, wrapping and x positions are exactly what `None` gives, so layout never depends on the hinting mode. Composite glyphs are hinted per component; the composite's own instructions are not run.
- **When it is skipped** (the unhinted outline is used): CFF fonts, fonts without instructions, `Path` text, vertical modes, super- and subscript runs, sizes below 4 or above 400 px per em, and any glyph or font program that fails.
- **Safety.** Font programs are untrusted. Budgets: 2^20 instructions for `fpgm`/`prep` and 2^18 per glyph, call depth 32, stack, storage, CVT, twilight points and function counts clamped to `maxp` and fixed caps, `SLOOP` and `LOOPCALL` counts paid for in instructions, bounds-checked indexes, no function definitions in glyph programs. Any violation aborts that program: the glyph (or the whole font at that size, for a failing setup program) is drawn unhinted, and nothing throws. Hinters and hinted outlines are cached per face and size (16 sizes, 8192 glyphs) and safe to use from many threads.
- **Agreement.** Black text on white at 9-16 px (24 cases: three strings, eight sizes) from a Latin subset of Noto Sans (ttfautohint instructions) against SixLabors.Fonts 2.1.3 with `HintingMode.Standard`: mean absolute level difference 0.45 (worst case 0.79), against 1.47 for our unhinted text and 1.49 between the reference's own hinted and unhinted output; hinting moves us closer in 24 of 24 cases. With a hand-hinted system font (DejaVu Sans or Liberation Sans, not committed) one exploratory run gave 0.70 against 1.40. Exact agreement is not expected for hand-hinted fonts.
- **Not supported:** other rendering modes (ClearType-style x hinting, the native ClearType mode of interpreter version 35) and hinting of rotated text.

## Rich text: runs, decorations and attributes

`TextOptions.TextRuns` gives parts of the text their own look. A `TextRun(start, end)` covers UTF-16 offsets `[start, end)` of the string passed to the call and can set:

| Property | Default | Effect |
|---|---|---|
| `Font` | null: `TextOptions.Font` | Face and size of the run. A run can use any family and style. |
| `TextAttributes` | `None` | `Superscript` or `Subscript` (below). |
| `TextDecorations` | `None` | Any of `Underline`, `Strikeout`, `Overline`. |
| `Brush`, `Pen` | null: the brush and pen of the `DrawText` call | Paint of the run's glyphs and of its filled decorations. |
| `UnderlinePen`, `StrikeoutPen`, `OverlinePen` | null | Strokes that decoration as a line with this pen (its width is the thickness) instead of filling a rectangle. |

```csharp
var options = new TextOptions(regular24) { Origin = new(20, 20), WrappingLength = 500 };
options.TextRuns.Add(new TextRun(0, 5) { Font = bold40, Brush = Brush.Solid(red), TextDecorations = TextDecorations.Underline });
options.TextRuns.Add(new TextRun(12, 13) { TextAttributes = TextAttributes.Superscript });
image.DrawText("Hello, rich world", options, Brush.Solid(black));
```

- **Resolution.** A character belongs to the last run in the list that covers it, otherwise to no run (the options' font, no decoration, the call's paint). Ranges are clamped to the text and empty ones ignored, so the work is linear in the text plus the number of runs however runs overlap. A run is read when the layout runs; `Clone()` copies runs.
- **Mixed sizes and faces.** Shaping, kerning and ligatures stay inside a stretch of one run and face. Each glyph carries its own scale, so widths, wrapping and alignment mix sizes freely. A line is as tall as the largest line pitch (`size × dpi/72 × LineSpacing`) of any glyph on it, and its baseline sits at the largest "ascent in the line box" (the font's ascent, with the leading split evenly above and below as described under *Units and layout*). Lines stack without gaps; an empty line takes the metrics of the run at its position. `LineMetrics` reports the line's own `Ascender`, `Descender` and `LineHeight`. A tab is `TabWidth` spaces of the space glyph of the run it sits in.
- **Decorations.** Positions come from the font of each glyph: the underline at `post` underline position and thickness, the strikeout at `OS/2` strikeout position and size, the overline one em plus the underline distance above the baseline with the underline's thickness (fonts with no such data get 1/20 em thick lines, an underline 1/10 em below and a strikeout 3/10 em above the baseline). The measures are the centers of the lines. A decoration runs from the pen position of the first decorated glyph to the end of the advance of the last one, as one rectangle across neighbouring glyphs of the same run, position and thickness, so it is continuous across glyphs, spaces and kerning; it restarts when the font or size changes, ends at the last visible glyph of each line (trailing spaces at a wrap are not underlined) and starts again on the next line. Decorations are painted like their run's glyphs, in the same path, and are left out of `Bounds` and ink boxes.
- **Super- and subscript.** The glyphs are scaled by the font's `OS/2` superscript or subscript size (x and y separately, by default 65 % by 60 % of the em), advances scale with them, and the baseline moves by the recommended y offset scaled by the vertical size (up for superscript, down for subscript). The line pitch of such a run is scaled by the vertical size too. Horizontal offsets are not applied. Decorations on such a run sit on the shifted baseline with scaled thickness.
- **Painting.** `DrawText` draws one path per distinct brush/pen pair (runs that share a brush and pen share a path), so a text with default paint is still one fill. `TextBuilder.GeneratePath` returns everything as one path, decorations as rectangles (a decoration stroked with its own pen has no outline and is left out). `DrawText` with every run painted accepts a null brush and pen.
- **Limits.** At most `TextOptions.MaxTextRuns` (16,384) runs; more throw `ImageLimitException`. Layout cost does not depend on how runs overlap; one style object is created per (run, face) actually used.

## Font fallback

`TextOptions.FallbackFontFamilies` lists families tried, in order, for characters the run's font cannot map. Up to `MaxFallbackFamilies` (32); more throw `ImageLimitException`.

- **Choosing.** For each grapheme cluster the first code point (zero-width and format characters and tabs never choose) is looked up in the run's face, then in the face of each fallback family that matches the run's style (or is closest to it), at the run's size. The first face with a glyph (a nonzero glyph id in its character map) shapes the whole cluster, so a base and its combining marks stay together. When no face has it, the primary face draws its missing-glyph box.
- **Shaping.** A stretch of one face is one shaping run, so ligatures and kerning never cross a change of face, and line breaking and wrapping work on the shaped glyphs as before.
- **Metrics.** A line takes its baseline from the largest ascent of the faces used on it, so a line with a fallback face of a taller ascent sits lower than the same line without it (the Noto Sans CJK subset, with an hhea ascent of 1.16 em against Noto Sans's 1.069 em, moves a line by 1.92 px at 40 px). Line height is the largest pitch (size-based), so it does not grow with a fallback face. Each face's own `OS/2`/`hhea` rule picks its ascender (typographic metrics when `USE_TYPO_METRICS` is set).
- **Cost.** One character-map lookup per cluster per face tried, only when fallback families are given.

## Text on a path

`TextOptions.Path` lays the text along a `Path`. The text is laid out exactly as straight text would be (wrapping, alignment, line spacing, origin, runs, fallback), treating the path as the top edge of the first line, and each glyph is then moved onto the path:

- A glyph's anchor is the middle of its advance. Its **distance** along the path is the layout x of the anchor (origin and alignment included), so `HorizontalAlignment.Center` centers about the *start* of the path, not its middle, and an origin x moves text along it.
- The glyph is **rotated about its anchor** to the direction of the path at that distance and is not bent: a rigid move per glyph. Its offset from the path is its layout y, to the right of the direction of travel in a y-down image (so text on a left-to-right line stands below it, and a clockwise circle carries text on the inside). Later lines are further to the right; `VerticalAlignment` and `Origin.Y` move the whole block off the path.
- **Past the ends.** An open path continues straight: before the start along its first segment, after the end along its last. A closed path wraps around: glyphs beyond the perimeter start over at the beginning, and the closing edge is a real edge.
- **Which path.** The path is flattened (0.1 px) and the first contour with a length is used; a path without length throws `ArgumentException`. Measuring (`TextMeasurer`) ignores the path.
- **Decorations** follow the path glyph by glyph (each piece is a rectangle rotated with its glyph), so on a curve neighbouring pieces overlap on the inside and open a thin wedge on the outside.
- **Cost.** One flatten, then a binary search over the cumulative segment lengths per glyph: linear in glyphs, logarithmic in path points. 100,000 glyphs on a path of 200,000 points take about 110 ms.

## System fonts

`SystemFonts` finds installed fonts without native calls: it scans the standard directories (Windows: the system Fonts folder and `%LOCALAPPDATA%\Microsoft\Windows\Fonts`; macOS: `/System/Library/Fonts`, `/Library/Fonts`, `/Network/Library/Fonts`, `~/Library/Fonts`; elsewhere: `/usr/share/fonts`, `/usr/local/share/fonts`, `$XDG_DATA_DIRS/*/fonts`, `~/.fonts` and `$XDG_DATA_HOME/fonts` or `~/.local/share/fonts`). `SystemFonts.Installed` is the process-wide `SystemFontSet`; `new SystemFontSet(directories)` scans directories of your choice.

```csharp
Font font = SystemFonts.CreateFont("DejaVu Sans", 24, FontStyle.Bold);
IReadOnlyList<string> names = SystemFonts.FamilyNames;   // sorted, nothing loaded
```

- **Lazy.** Nothing happens until the first lookup. The first call that needs names lists the files (`.ttf`, `.otf`, `.ttc`, `.otc`, to a depth of 8, at most 100,000 files) and reads just the table directory and `name` table of each, never glyph data: 10 ms for directories holding 1,413 files (277 families) on a warm cache on the Linux host this was built on. The result is kept for the life of the set; fonts installed afterwards are not noticed.
- **Loading.** `TryGet`/`Get`/`CreateFont` parse the files of that one family (all its styles) the first time and return the same `FontFamily` afterwards. A file that no longer parses is skipped; its family may then be missing or partial. WOFF files are not scanned (their tables are compressed).
- **Safe.** Header reads are bounds-checked against the file length and stop at 256 faces per collection, 256 tables and 1 MiB of names; unreadable directories and files are skipped without an error. Family names are the ones `FontFamily.Name` gives (the typographic family when the font has one).

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
- **Color glyph advances.** We use the base glyph's advance, as the OpenType specification and the layout of every other glyph do. With color enabled SixLabors.Fonts 2.1.3 advances by the advance of the glyph's first layer instead; the two agree in fonts whose layers are as wide as their base glyph (the test font), and differ in others.
- **Color rendering** on the test font (layered shapes, foreground color, a half-transparent layer, two palettes, a composite layer): brush only, mean 0.07 levels over the image and at most 28 at single edge pixels; with a pen, mean 1.9 to 2.7 levels, which is the half-pixel stroke placement difference described in [drawing.md](drawing.md). COLR version 1 paint graphs are not painted by SixLabors.Fonts 2.1.3; see [COLR version 1](#colr-version-1) for the comparison used instead.
- **WOFF2** has no ImageSharp difference worth naming: faces, outlines (point for point), advances, side bearings, kerning and shaped text from the WOFF2 of each test font (TrueType, CFF, a collection, and a file using the `hmtx` transform) equal those of the source font.
- **Rich text** (probed through the public API of SixLabors.Fonts 2.1.3 and ImageSharp.Drawing 2.1.7; tolerances are what the tests assert):
  - *Runs with mixed sizes and faces*, with and without wrapping: glyph ink boxes agree to 0.0002 px, advance and line count exactly. In some run configurations (a run without a font next to plain text, or runs of different sizes) the reference applies an extra pair adjustment to `r` followed by some letters (-20 units) and, in one probed case, to `o` followed by `w` (-60 units; in mixed-size text it is scaled by the size of one of the runs) that it does not apply to the same text without runs; we apply the font's `GPOS` kerning only, and the tests use text without the pairs.
  - *Overlapping runs*: the reference drops the rest of an outer run after a nested one (an inner run in the middle of an outer run leaves the outer run's decorations off the text after it); we resolve every character to the last run covering it.
  - *Decorations*: segment ends and heights agree to 0.01 px, thickness to 0.01 px, in plain, wrapped, mixed-size and raised runs. The reference reports one segment per glyph; we report one per line and run, and the filled rectangle is the same area.
  - *Super- and subscript*: ink boxes agree to 0.0001 px.
  - *Fallback without wrapping*: ink boxes agree exactly. *With wrapping* the line count agrees but the reference declines some break opportunities where a fallback face is involved (for `ABあいCD` at 150 px it keeps two kana on a line where the text fits three); we wrap by the same rule as without fallback.
  - *Text on a path*: glyph vertices agree to 0.0003 px on lines (horizontal, diagonal, reversed, steep), alignments, origin, wrapping and multi-line text, and past the end of an open path. On a 120-segment circle ten of twelve glyphs agree to 0.001 px; two sit one segment step (up to 1.9 px at their far corners) away from the reference's. The reference does not place glyphs on the closing edge of a closed polygon (they continue straight past the last explicit vertex) while still wrapping at the full perimeter; we follow the closing edge. We also take the first contour of a multi-contour path.
  - *Rendering* (black on white, 420x200, mean absolute level difference over all pixels; `RichRenderTests`): runs with underline, strikeout and overline 0.62 (max 248), a wrapped paragraph with a larger run and superscript 0.39 (max 254), fallback text 0.13 (max 133), text along a polyline 0.07 (max 99). Glyph pixels agree like plain `DrawText` does. The large maxima are decoration lines: both libraries agree on where a decoration is (segment ends, center and thickness within 0.01 px) but the reference rasterizes the thin rectangle about 0.3 to 0.7 px higher or lower than that geometry, so a one or two pixel line differs in whole rows; we paint the reported geometry exactly.
  - *Layout and hinting modes*: the reference's `HorizontalBottomTop` mode places glyphs as top-to-bottom does; ours stacks lines upward. Vertical layout and hinting are compared in [Vertical layout](#vertical-layout) and [Hinting](#hinting).

## Shaping clusters for your own layout

`TextShaping` (in `OneImage.Text`) exposes what a caller needs to lay text out itself — SVG text, with a position for every character, is the user — without the library's own paragraph layout:

```csharp
byte[] levels = TextShaping.GetBidiLevels(text, TextDirection.LeftToRight, out int paragraphLevel);  // UAX #9 level per UTF-16 unit
int[] visual = TextShaping.GetVisualOrder(levels);                                                   // logical indexes in visual order
IReadOnlyList<ShapedCluster> clusters = TextShaping.ShapeRun(text, font, rightToLeft: false, new ShapingOptions { Language = "tr" });
foreach (ShapedCluster c in clusters)
{
    // c.Start and c.Length: UTF-16 units of the input; c.Advance: pixels at the font size, kerning included;
    // c.GetOutline(): the glyphs as a Path with the pen at (0, 0), y down, in pixels; c.HasMissingGlyph.
}
```

- `GetBidiLevels(string, TextDirection, out int paragraphLevel)` runs the algorithm on one paragraph (`Auto` picks the level from the first strong character) and returns one level per UTF-16 unit. `GetVisualOrder(ReadOnlySpan<byte> levels)` reverses runs per rule L2 and returns the logical index at each visual position; an empty input gives an empty array.
- `ShapeRun(ReadOnlySpan<char>, Font, bool rightToLeft = false, ShapingOptions? = null)` shapes one run in one font and returns its clusters in logical order. A cluster is what cannot be positioned apart (a letter with its marks, a ligature, a syllable), so each can be placed, rotated or stretched on its own. Runs longer than `TextShaping.MaxRunLength` (2^24) throw `ArgumentException`. The run is not split by script or direction: the caller does that, and passes `rightToLeft` for odd levels. A character the font lacks gives a cluster with `HasMissingGlyph` true and the font's missing-glyph outline.
- `ShapingOptions`: `Language` (BCP 47, default the font's default language system), `Kerning` (`KerningMode`, default `Normal`) and `Features` (`FontFeature` list, empty by default, applied on top of the script's defaults; value 0 switches a feature off).
- `ShapedCluster.GetOutline()` builds the outline once and returns the same `Path` afterwards. Outlines are unhinted. Nothing is cached across runs.
- `FontFaceInfo.GetWeight(Font)`, `GetIsItalic(Font)`, `GetSuperscriptRaise(Font)` and `GetSubscriptDrop(Font)` read the face's OS/2 weight class, italic flag and the superscript and subscript offsets in pixels at the font's size (0 when the face has none).

## Where the next pieces plug in

`Layout/TextLayout.cs` is the whole layout in four steps with narrow seams between them.

- **Shaping** is `Shape` calling `Shaping/TextShaper`: code points in, `ShapedGlyph`s (glyph id, cluster, advance with kerning, offsets) out. Script-specific shapers (Arabic joining, Indic, Universal Shaping Engine and Thai/Lao) are `ScriptShaper`s with its own feature stages; the lookup engine (`Shaping/Engine`) is shared.
- **Line breaking** is `PrepareBreaks` and `CanBreakBefore`: the break opportunities and cluster starts of a paragraph are computed from its text (`Unicode/LineBreaker.cs`, `Unicode/Graphemes.cs`) and reach the glyphs only through each glyph's text offset, so a shaper that merges or reorders glyphs needs no change here. Hyphenation, language-tagged tailoring and dictionary breaking for Thai, Lao, Khmer and Burmese (their characters are treated as letters, so there is no break inside a word) would extend `LineBreaker`.
- **Color fonts** beyond COLR (SVG and bitmap glyphs) extend the color branch of `BuildParts`, which already turns the layers of a version 0 color glyph into one fill per layer index and color, and hands a version 1 glyph to `ColrPainter`.
- **Runs, fallback, decorations and paths** live in `Layout/TextLayout.cs`: `ResolveRuns` maps every offset to a run, `Shape` splits shaping runs by (run, face) and every `ShapedGlyph` carries a `GlyphStyle` (face, scale, shift, line and decoration measures); `BuildParts` writes outlines and decoration rectangles per paint, with `GlyphMatrix` giving the straight or on-path placement (`Layout/PathRoute.cs`).

## ImageSharp names

| SixLabors.Fonts / ImageSharp.Drawing | OneImage.Text |
|---|---|
| `FontCollection.Add(path / stream)`, `AddCollection` | `FontCollection.Add(path / stream / memory)`, `AddCollection` |
| `FontFamily.CreateFont(size, style)` | `FontFamily.CreateFont(size, style)` |
| `RichTextOptions` / `TextOptions` | `TextOptions` (`Origin`, `WrappingLength`, `LineSpacing`, `TabWidth`, `Dpi`, alignments, `Kerning`, `WordBreaking`, `TextJustification`, `Language`, `Features`, `TextRuns`, `FallbackFontFamilies`, `Path`) |
| `RichTextRun` / `TextRun` (`Start`, `End`, `Font`, `TextAttributes`, `TextDecorations`, `Brush`, `Pen`, `UnderlinePen`, `StrikeoutPen`, `OverlinePen`) | `TextRun` with the same members (`Start`/`End` are UTF-16 offsets, End exclusive) |
| `SystemFonts.Get` / `CreateFont` / `Families` | `SystemFonts.Get` / `CreateFont` / `FamilyNames` (names only until a family is requested) |
| `TextBuilder.GenerateGlyphs(text, path, options)` | `TextOptions.Path` with `TextBuilder.GeneratePath` or `DrawText` |
| `TextOptions.FeatureTags` (`Tag`) | `TextOptions.Features` (`FontFeature`, with a value and off switches) |
| `TextMeasurer.MeasureAdvance` / `MeasureBounds` / `MeasureSize` / `CountLines` | `TextMeasurer.MeasureAdvance` / `MeasureBounds` / `Measure(...).Bounds.Size` / `CountLines` |
| `TextMeasurer.TryMeasureCharacterBounds` | `TextMeasurer.GetCharacterBounds` (`Ink`) |
| `ctx.DrawText(options, text, brush, pen)` | `img.DrawText(text, options, brush, pen)` / `canvas.DrawText(...)` |
| `TextOptions.ColorFontSupport` (`MicrosoftColrFormat`) | `TextOptions.ColorFontSupport` (`MicrosoftColrFormat` by default; also `ColorPaletteIndex`) |
| `TextOptions.LayoutMode` | `TextOptions.LayoutMode` (same members) |
| `TextOptions.HintingMode` (`None`, `Standard`) | `TextOptions.HintingMode` (`None` default, `Standard`) |
| `TextBuilder.GenerateGlyphs` | `TextBuilder.GeneratePath` |
| `TextRenderer` + `IGlyphRenderer` | `TextBuilder.GeneratePath`, then walk or transform the `Path` |
