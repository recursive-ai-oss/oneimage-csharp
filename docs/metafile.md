# Metafiles (`RecursiveAI.OneImage.Metafile`)

The package reads Windows Enhanced Metafiles (EMF) and Windows Metafiles (WMF) two ways: as an image format that is drawn at the requested size, and as a stream of device-independent drawing commands. It is clean-room: written from [MS-EMF] and [MS-WMF] only, and checked against Windows GDI output made by us. EMF+ records are not interpreted yet (see Gaps).

## Two layers

```csharp
// 1. Image format: Image.Load(emf) rasterizes at the final size.
var config = new ImageConfiguration().Add(MetafileFormat.Instance).Add(WmfFormat.Instance); // WMF is its own format
Image.Load("a.emf", new LoadOptions { Configuration = config }).Resize(width: 800).Save("a.png");

// 2. Command stream: the same interpreter, without rasterizing.
MetafileDocument doc = MetafileDocument.Load("a.emf");   // or Parse(bytes), Identify(bytes)
doc.Play(new MyVisitor());                                 // DrawingCommandVisitor, all methods optional
```

The raster sink is just a `DrawingCommandVisitor` on the drawing engine, so the command stream carries everything a renderer needs.

## The command stream

Commands are records in `OneImage.Metafile`; each has a language-neutral `Name` and `Describe()` (a name plus string parameters) and the index of the record that produced it.

| Name | Meaning |
|---|---|
| `begin-picture` | The `MetafileInfo`: kind, bounds, frame, reference device size, and `PictureRectangle`, the part of device space the picture occupies. Always first. |
| `save` / `restore` | Push / pop the (transform, clip) pair, canvas style. GDI `SaveDC`/`RestoreDC` map onto them. |
| `set-transform` | Logical coordinates → picture device units. The mapping mode, window and viewport extents and origins, and the world transform are all folded into this one matrix. |
| `clip` | Combine a clip shape (device space) with the current clip: intersect, exclude, union, xor, replace or reset. |
| `offset-clip` | Move the current clip. |
| `fill-path` | A path in logical units, a brush (solid, hatch, bitmap pattern) with its origin, the fill rule, the binary raster operation (`MixMode`) and the background fill for hatches. |
| `stroke-path` | A path with a `PenSpec`: color or brush, width in logical units, cosmetic or geometric, dashes, caps, joins, miter limit, and the raster operation. |
| `blit` | A decoded bitmap (RGBA) with source and destination rectangles, the ternary raster operation, mask or color key, and constant alpha. |
| `gradient-fill` | GDI `GradientFill`: vertices, indices, rectangle (horizontal/vertical) or triangle mode. |
| `draw-text` | Text as UTF-16 with the resolved `FontSpec` (face, height, weight, flags, escapement), color, alignment, per-character advances when the record has them, and the opaque or clipped rectangle. Fonts are not resolved here: the renderer chooses the face. |
| `comment` | A metafile comment (EMF+ records are interpreted, see below, and do not appear here). |
| `clear`, `rendering-hints`, `draw-string`, `draw-glyphs`, `draw-image` | GDI+ only: clear the surface, the smoothing / compositing / pixel-offset / interpolation / text hints, a string in a layout rectangle, glyphs at positions, a bitmap in a parallelogram. |
| `diagnostic` | Something the interpreter skipped or could not honour, with the record index, a severity and a message. |

A walk is cheap: `MetafileDocument.Parse` interprets once; `Play` can be called any number of times. Geometry, pen widths and text stay in logical units under the current transform, as on a canvas, so a consumer that draws vectors (a PDF writer, say) keeps the picture resolution-independent. `MetafileReadOptions.MeasureText` lets the caller supply text widths for records that carry none (needed for text alignment and the update-current-position mode).

## Rendering

`MetafileFormat(limits, fonts, antialias, background)`:

- **Size.** The natural size is the header's reference device size of the frame, in pixels (`Identify` reads the header only). The decoder advertises `RenderAtSize`, so `Resize`/`Thumbnail` draw at the final size. Cosmetic pens stay one output pixel wide whatever the scale; geometric pens scale with the picture.
- **Anti-aliasing** is on by default and uses the drawing engine's exact-area coverage. `antialias: false` gives GDI-like hard edges, which is how the Windows comparison runs. Windows GDI itself does not anti-alias, so a default render differs from GDI along edges.
- **Background** is transparent unless `background` is given. Raster operations that read the destination (XOR, `PATINVERT`, `SRCAND`...) treat a transparent destination as white.
- **Fonts** come from `MetafileFontOptions`: your `FontCollection`, optionally installed system fonts, a substitution table (`Substitutions`), and a `DefaultFamily`. Common Windows faces (Arial, Segoe UI, Tahoma, Times New Roman, ...) map to similar installed families when not present. Text is drawn from outlines with the Text package; glyph positions come from the record's advances when present.
- **Sampling.** Bitmaps are sampled bilinearly in `STRETCH_HALFTONE` mode only when the stretch shrinks the bitmap (as GDI does), and nearest-neighbour otherwise.

## Geometry conventions (measured from Windows output)

Where [MS-EMF] does not say how GDI behaves, the renderer follows what Windows GDI drew for the same files (see the last section).

- **Shape boxes are inclusive.** GDI records `Rectangle(10, 10, 45, 80)` as 10, 10, 44, 79 and plays the box back covering that last pixel, so rectangles, ellipses and rounded rectangles extend one device pixel past the recorded right and bottom edge. Arc bounding boxes are used as recorded.
- **Strokes** are placed so that a one-pixel pen on integer coordinates fills one row or column of pixels: the geometry is shifted half a reference-device pixel, and box outlines are drawn half a pixel inside the box edge. `PS_INSIDEFRAME` insets by half the pen width.
- **Pens.** A legacy pen of width 0 or 1 is cosmetic (one device pixel, ignores the transform); wider legacy pens are geometric with round caps and joins. Extended pens follow `PS_COSMETIC`/`PS_GEOMETRIC`. Cosmetic dash patterns are 18/6 (dash), 3/3 (dot), 9/6/3/6 (dash-dot) and 9/3/3/3/3/3 (dash-dot-dot) pixels. A geometric pen counts in pen widths: dash 3 on 1 off, dot 1 on 1 off, dash-dot 3 1 1 1, dash-dot-dot 3 1 1 1 1 1; with round or square caps the cap belongs to the dash, so a round-capped dot is a disc the width of the pen every two widths. User-style dash lengths are in logical units.
- **Hatch brushes** are 8x8 cells in output pixels (they do not scale with the picture), anchored at the brush origin, which is also not scaled: the horizontal line is on row 3 and the vertical line on column 4 of the cell, the forward diagonal is x = y and the backward diagonal x + y = 7.
- **Region fills.** `FrameRegion` paints inside the region's edge. A raster operation other than copy keeps the brush out from under the overlap of a wide pen.
- **Region data** (clip regions) is in logical units, per the specification.
- **Gradient fills** ignore the vertex alpha.
- **`SelectClipPath` with an empty path** leaves the clip unchanged (GDI fails the call; this happens for a bracket that held only text drawn with a bitmap font).

## Record coverage

All record types 1–122 of [MS-EMF] 2.1.1 are dispatched.

| Area | Records |
|---|---|
| Header, EOF, comments | `HEADER`, `EOF`, `COMMENT` (EMF+ records inside it are interpreted, see EMF+) |
| State | `SETMAPMODE`, window/viewport extents and origins, `SCALE*EXTEX`, `SETWORLDTRANSFORM`, `MODIFYWORLDTRANSFORM`, `SAVEDC`/`RESTOREDC`, `SETBKMODE`, `SETBKCOLOR`, `SETTEXTCOLOR`, `SETTEXTALIGN`, `SETTEXTJUSTIFICATION`, `SETPOLYFILLMODE`, `SETROP2`, `SETSTRETCHBLTMODE`, `SETARCDIRECTION`, `SETMITERLIMIT`, `SETBRUSHORGEX`, `SETLAYOUT` (left-to-right only) |
| Objects | `CREATEPEN`, `EXTCREATEPEN`, `CREATEBRUSHINDIRECT` (solid, hollow, hatch), `CREATEMONOBRUSH`, `CREATEDIBPATTERNBRUSHPT`, `EXTCREATEFONTINDIRECTW`, `SELECTOBJECT` (including stock objects), `DELETEOBJECT`; palette and color-space objects are accepted and ignored |
| Lines and shapes | `MOVETOEX`, `LINETO`, `POLYLINE`/`POLYLINETO`/`POLYGON`/`POLYBEZIER`/`POLYBEZIERTO` (16 and 32-bit forms), `POLYPOLYLINE`/`POLYPOLYGON`, `POLYDRAW`, `RECTANGLE`, `ELLIPSE`, `ROUNDRECT`, `ARC`, `ARCTO`, `CHORD`, `PIE`, `ANGLEARC`, `SETPIXELV` |
| Paths | `BEGINPATH`, `ENDPATH`, `CLOSEFIGURE`, `FILLPATH`, `STROKEPATH`, `STROKEANDFILLPATH`, `ABORTPATH`, `FLATTENPATH`, `SELECTCLIPPATH` |
| Clipping and regions | `INTERSECTCLIPRECT`, `EXCLUDECLIPRECT`, `OFFSETCLIPRGN`, `EXTSELECTCLIPRGN`, `FILLRGN`, `FRAMERGN`, `INVERTRGN`, `PAINTRGN` |
| Bitmaps | `BITBLT`, `STRETCHBLT`, `MASKBLT`, `PLGBLT`, `STRETCHDIBITS`, `SETDIBITSTODEVICE`, `ALPHABLEND`, `TRANSPARENTBLT`, `PATBLT`; DIBs of 1, 4, 8, 16, 24 and 32 bits, RLE4/RLE8 and bit fields |
| Gradients | `GRADIENTFILL` |
| Text | `EXTTEXTOUTA`/`W`, `POLYTEXTOUTA`/`W`, `SMALLTEXTOUT` |
| Ignored with an info or warning diagnostic | color management and adjustment, pixel format, driver escapes, OpenGL, font-mapper records |

Binary raster operations (`SETROP2`, 16 modes) and ternary operations (the 256 `BITBLT` codes) are computed per pixel by a pass over the covered area.

## WMF

`WmfFormat` (name `wmf`, `.wmf`, `image/wmf`) registers next to `MetafileFormat`; `MetafileDocument.Parse`/`Load`/`Identify` accept both and `MetafileDocument.IsWmf` tells them apart. Placeable (APM header) and non-placeable files are read.

**Approach.** Each WMF record is translated in memory to the equivalent EMF record and played by the EMF interpreter (Windows itself converts the same way), so every convention above applies and the command stream is unchanged. Diagnostics name the WMF record index and function. WMF specifics:

- **Objects.** Slots are allocated lowest-free as GDI does (an EMF handle is slot + 1). Palettes and regions are tracked by the converter; regions become inline region data.
- **Size.** A placeable file is its bounding box divided by units-per-inch, at 96 px per inch. Without a header: the first `SetWindowExt` in pixels, else the bounds of the drawn coordinates, else 256x256. The reference device is a synthetic 96 dpi 1024x768 display. Until the file sets them, the mapping is anisotropic with window = bounding box and viewport = the whole picture.
- **Boxes** are right/bottom exclusive as written; the EMF inclusive expansion is not applied.
- **Embedded EMF.** A file carrying `META_ESCAPE` / `ENHANCED_METAFILE` comment chunks is reassembled (at most `MetafileLimits.MaxEmbeddedEmfBytes`, 64 MiB, checked against the declared size before allocating) and played through the EMF interpreter instead of the WMF records, unless `MetafileReadOptions.PreferEmbeddedEmf` is false. A damaged embedded EMF falls back to the WMF records with a diagnostic. `Identify` still reports the WMF header's size.
- **Pseudo-records** for window/viewport offsets and text character extra are internal.

| Area | WMF functions |
|---|---|
| State | `SaveDC`, `RestoreDC`, `SetMapMode`, `SetWindowOrg/Ext`, `SetViewportOrg/Ext`, `OffsetWindowOrg`, `OffsetViewportOrg`, `ScaleWindowExt`, `ScaleViewportExt`, `SetBkMode`, `SetBkColor`, `SetTextColor`, `SetTextAlign`, `SetTextJustification`, `SetTextCharExtra`, `SetPolyFillMode`, `SetROP2`, `SetStretchBltMode`, `SetMapperFlags` (ignored), `SetRelAbs` (ignored) |
| Objects | `CreatePenIndirect`, `CreateBrushIndirect` (solid, hollow, hatch, DIB pattern), `DibCreatePatternBrush`, `CreatePatternBrush`, `CreateFontIndirect`, `CreateRegion`, `CreatePalette`, `SelectObject`, `DeleteObject`, `SelectPalette` |
| Drawing | `MoveTo`, `LineTo`, `Polyline`, `Polygon`, `PolyPolygon`, `Rectangle`, `RoundRect`, `Ellipse`, `Arc`, `Pie`, `Chord`, `SetPixel` |
| Text | `TextOut`, `ExtTextOut` (cp1252; the charset is not applied; character widths are used) |
| Bitmaps | `BitBlt`, `StretchBlt`, `DibBitBlt`, `DibStretchBlt`, `StretchDIB`, `SetDIBitsToDevice`, `PatBlt`; the forms without a bitmap are pattern fills |
| Clipping and regions | `IntersectClipRect`, `ExcludeClipRect`, `OffsetClipRgn`, `SelectClipRegion` (an index naming no region resets), `FillRegion`, `FrameRegion`, `InvertRegion`, `PaintRegion` |
| Escapes | embedded EMF (above); the rest are ignored (printing control records are silent) |
| Skipped with a diagnostic | `FloodFill`, `ExtFloodFill`, palette changes that don't affect RGB (silent), bitmaps of 4 and 8 bpp in `Bitmap16`, `DIB_PAL_COLORS` |

For a bottom-up DIB the source rows of `StretchDIB` count from the bottom (as GDI). `Bitmap16` is read at 1, 16, 24 and 32 bpp; 1 bpp is black and white.

### WMF checked against Windows

The same 15 drawings were recorded as WMF (placeable and non-placeable, identical results) and played by `PlayMetaFile` at 256x256 into a white bitmap; `WmfReferenceTests` uses the same pixel comparison as for EMF (per-case floor 95%, text_styled 90%).

| Case | 256 px | Case | 256 px |
|---|---|---|---|
| arcs | 99.29 | rop2 | 98.61 |
| clip_rect | 100.00 | state_stack | 99.22 |
| dib_pattern | 99.90 | stock_objects | 96.43 |
| hatch | 99.90 | stretchdibits | 99.25 |
| lines_pens | 99.80 | text_basic | 96.54 |
| pixels_misc | 99.27 | text_styled | 91.75 |
| polys | 99.66 | rect_basic | 99.64 |
| regions | 99.64 | | |

The remaining differences are the same as for EMF; text_styled is lowest because of the font faces GDI used.

## Gaps (each produces a diagnostic)

`WIDENPATH`, `SETMETARGN`, `EXTFLOODFILL`, text inside a path bracket, glyph-index text (treated as code points), vertical advances in `ExtTextOut` (`PDY`), the `XformSrc` of blits, `DIB_PAL_COLORS` bitmaps, JPEG or PNG compressed DIBs, right-to-left layout, and EMF+ records. Skipped records never stop the interpretation.

## EMF+ (GDI+)

Files recorded by GDI+ keep their drawing in EMF+ records inside `EMR_COMMENT` records. The interpreter walks them with a graphics state of its own (world, page and container transforms, saved states) and produces the same command stream as for GDI records.

- **Dual and EMF+-only files.** Once the EMF+ header is seen the GDI records are skipped, as GDI+ itself does, so a dual file is drawn once: the EMF+ drawing wins. Records between an `EmfPlusGetDC` record and the next EMF+ record are GDI records and are played, in the EMF+ world transform that was in force. (Windows renders both kinds of file the same, and so does the test below.)
- **Units.** EMF+ coordinates are in reference-device pixels: the world transform, then the page transform (`SetPageTransform`; inches, points, millimeters, documents and display units use the header's dpi), then the container base. They are folded into one `set-transform`. The pixel grid needs no half-pixel shift (shapes at integer coordinates cover the same pixels as in GDI+).
- **Containers.** `BeginContainer` maps its source rectangle onto its destination rectangle in the parent's coordinates and starts a fresh world transform; its page unit is taken as pixels. `Save`/`Restore`/`EndContainer` share one stack and map to `save`/`restore`.
- **Objects.** Brushes (solid, hatch, texture, linear and path gradient), pens (dash styles and custom dashes, dash offset and cap, joins, inset alignment on rectangles and ellipses), paths, regions (a tree of rectangles and paths, combined with the exact path booleans), images (bitmaps in all pixel formats, compressed images through the core decoders, embedded EMF files played as commands inside their destination), fonts, string formats and image attributes, including objects that are split across records. Limits: `MaxPlusObjectBytes` for an object (and the sum of its parts), `MaxNestedMetafileDepth` for metafiles inside images; points, regions, bitmaps, saves and text use the existing limits.
- **Drawing.** Clear, fill and draw of rectangles, polygons, ellipses, pies, arcs, lines, Beziers, cardinal splines (open, closed, with offset and segment count), paths and regions; images (`DrawImage`, `DrawImagePoints`), strings and driver strings; clip records with all combine modes (Complement is replaced by Replace, with a warning), reset and offset.
- **Hints.** Smoothing (anti-aliasing), source-copy compositing and the interpolation mode change the raster output; the others are carried in `rendering-hints`.
- **Fidelity choices measured against GDI+.** Arc and pie angles are ray angles from the ellipse center, converted to the ellipse's parametric angle. A path's winding fill mode is bit `0x2000` of its flags (the table in [MS-EMFPLUS] does not list it). Strings without a string format have a margin of 1/6 em on both sides, as GDI+ draws them; the first baseline is the ascent below the top of the layout rectangle.
- **Gaps (each reports a diagnostic).** Triangle, anchor and custom pen line caps (drawn flat), compound pens (drawn solid), image effects and color matrices, path gradient blend ramps, `EmfPlusStrokeFillPath` (no defined layout), WMF images inside EMF+, multi-format records, glyph-index driver strings (taken as characters), and the 54 hatch styles that are dithers or weaves are approximations of the GDI+ patterns.

## Limits and safety

`MetafileLimits` bounds the file size, record and command counts, object table, path points, bitmap pixels (per bitmap and in total), save depth, region rectangles, text length, dash entries, and a total work budget; every size read from a record is checked against the record's own length and these limits before an allocation, and a bad record is skipped with a diagnostic rather than failing the file. A file that breaks a limit throws `ImageLimitException`; a file with a bad header throws `ImageFormatException`. Interpretation is iterative (no recursion on input) and checks for cancellation.

## Checked against Windows

`tests/testdata/metafile/oracle` holds 25 EMF files recorded with real GDI calls (rectangles, pens, arcs, polygons, Beziers, paths, clips, hatch and DIB brushes, bitmaps, ROPs, text, world transforms, mapping modes, gradients, alpha blending, regions, DC save/restore, stock objects) together with what `PlayEnhMetaFile` drew for each into a white 32-bit bitmap of 256x256 and 512x512 (a 150% display, ClearType on; the header's reference device is 2496x1664 px, 317x211 mm). Both the files and the images are generated by us; the notice beside them says so. `WindowsReferenceTests` renders each file without anti-aliasing over white, stretched to the same size, and counts pixels whose channels differ by more than 48; `EveryCaseAgreesWithWindowsOnMostPixels` keeps each case above 95%.

| Case | 256 px | 512 px | Case | 256 px | 512 px |
|---|---|---|---|---|---|
| alphablend | 100.00 | 100.00 | polys | 99.20 | 99.68 |
| arcs | 99.22 | 99.69 | rect_basic | 99.60 | 99.72 |
| beziers | 99.77 | 99.86 | regions | 99.24 | 99.29 |
| bitblt_dc | 99.84 | 99.87 | rop2 | 99.36 | 98.95 |
| clip_path | 99.82 | 99.70 | state_stack | 99.10 | 99.33 |
| clip_rect | 99.95 | 99.98 | stock_objects | 95.80 | 98.61 |
| dib_pattern | 98.86 | 99.38 | stretchdibits | 99.16 | 99.68 |
| geom_pens | 99.66 | 99.79 | text_basic | 98.12 | 98.73 |
| gradient | 99.77 | 99.97 | text_styled | 97.41 | 97.35 |
| hatch | 97.35 | 99.05 | world_xform | 99.05 | 99.28 |
| lines_pens | 99.14 | 99.66 | mapmodes | 98.18 | 99.24 |
| mmtext_raw | 98.50 | 99.38 | paths | 97.29 | 97.28 |
| pixels_misc | 99.58 | 99.65 | | | |

The pictures are 384 px wide at their natural size, so both comparison sizes are fractional scales, and GDI rounds shape corners to its own grid: most of the remaining differences are one-pixel edge placement. The rest: text in fonts other than Arial (the system raster fonts and Arial itself are not available to the renderer), the smoothing GDI applies to stretched bitmaps, `WidenPath` and text in paths (not interpreted).

`tests/testdata/metafile/oracle-gdiplus` holds 12 drawings recorded by GDI+ as EMF+ only and as EMF+ dual, with what GDI+ rendered for each (`WindowsGdiPlusReferenceTests`; the page-unit case could not be recorded by GDI+). Agreement on pixels (tolerance 48 per channel, anti-aliased, test fonts instead of Arial) for both variants, which render identically:

| Case | 256x256 | 160x120 | Case | 256x256 | 160x120 |
|---|---|---|---|---|---|
| antialias | 99.12 | 98.23 | gradients | 98.09 | 97.30 |
| brushes | 92.49 | 91.08 | images | 99.55 | 99.93 |
| clip | 99.42 | 98.48 | paths_curves | 98.59 | 97.76 |
| compositing | 100.00 | 100.00 | pens | 96.98 | 94.74 |
| fill_alpha | 99.73 | 98.89 | text | 93.44 | 92.45 |
| transform_container | 99.02 | 98.30 | | | |

The brushes case loses most on the hatch patterns that are approximations and on texture phase at a scale of two thirds; the text case on the fonts (the machine had Arial, the tests use Noto Sans); the pens case on triangle caps and compound lines.

The pictures are 384 px wide at their natural size, so both comparison sizes are fractional scales, and GDI rounds shape corners to its own grid: most of the remaining differences are one-pixel edge placement. The rest: text in fonts other than Arial (the system raster fonts and Arial itself are not available to the renderer), the smoothing GDI applies to stretched bitmaps, `WidenPath` and text in paths (not interpreted), and ROP2 modes other than copy where GDI does not apply a fill under a wide pen's overlap twice (`rop2`).
