# Metafiles (`RecursiveAI.OneImage.Metafile`)

The package reads Windows Enhanced Metafiles (EMF) two ways: as an image format that is drawn at the requested size, and as a stream of device-independent drawing commands. It is clean-room: written from [MS-EMF] only, and checked against Windows GDI output made by us. EMF+ and WMF are not interpreted yet (see Gaps).

## Two layers

```csharp
// 1. Image format: Image.Load(emf) rasterizes at the final size.
var config = new ImageConfiguration().Add(MetafileFormat.Instance);
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
| `comment` | A metafile comment (EMF+ is detected and reported, not interpreted). |
| `diagnostic` | Something the interpreter skipped or could not honour, with the record index, a severity and a message. |

A walk is cheap: `MetafileDocument.Parse` interprets once; `Play` can be called any number of times. Geometry, pen widths and text stay in logical units under the current transform, as on a canvas, so a consumer that draws vectors (a PDF writer, say) keeps the picture resolution-independent. `MetafileReadOptions.MeasureText` lets the caller supply text widths for records that carry none (needed for text alignment and the update-current-position mode).

## Rendering

`MetafileFormat(limits, fonts, antialias, background)`:

- **Size.** The natural size is the header's reference device size of the frame, in pixels (`Identify` reads the header only). The decoder advertises `RenderAtSize`, so `Resize`/`Thumbnail` draw at the final size. Cosmetic pens stay one output pixel wide whatever the scale; geometric pens scale with the picture.
- **Anti-aliasing** is on by default and uses the drawing engine's exact-area coverage. `antialias: false` gives GDI-like hard edges, which is how the Windows comparison runs. Windows GDI itself does not anti-alias, so a default render differs from GDI along edges.
- **Background** is transparent unless `background` is given. Raster operations that read the destination (XOR, `PATINVERT`, `SRCAND`...) treat a transparent destination as white.
- **Fonts** come from `MetafileFontOptions`: your `FontCollection`, optionally installed system fonts, a substitution table (`Substitutions`), and a `DefaultFamily`. Common Windows faces (Arial, Times New Roman, Courier New, ...) map to generic families when not present. Text is drawn from outlines with the Text package; glyph positions come from the record's advances when present.
- **Sampling.** Bitmaps are drawn with nearest-neighbour sampling when `STRETCH_DELETESCANS`/`STRETCH_ANDSCANS`/`ORSCANS` is set and bilinear otherwise.

## Geometry conventions (defaults to confirm against Windows output)

These are choices where GDI's behaviour is not written in [MS-EMF]; each is kept in one place and noted as such in the code.

- **Strokes** are placed so that a one-pixel pen on integer coordinates fills one row or column of pixels (the geometry is shifted half a reference-device pixel). Rectangle, ellipse and round-rectangle outlines are drawn half a pixel inside the right and bottom edges, as GDI excludes them. `PS_INSIDEFRAME` insets by half the pen width.
- **Pens.** A legacy pen of width 0 or 1 is cosmetic (one device pixel, ignores the transform); wider legacy pens are geometric with round caps and joins. Extended pens follow `PS_COSMETIC`/`PS_GEOMETRIC`. Cosmetic dash patterns are 18/6 (dash), 3/3 (dot), 9/6/3/6 (dash-dot) and 9/3/3/3/3/3 (dash-dot-dot) pixels; a geometric pen scales those by width/6.
- **Hatch brushes** are 8×8 patterns anchored at the brush origin; horizontal and vertical lines are on row/column 4, diagonals pass through the corners.
- **Region data** (clip regions) is in logical units, per the specification.
- **Gradient fills** ignore the vertex alpha.

## Record coverage

All record types 1–122 of [MS-EMF] 2.1.1 are dispatched.

| Area | Records |
|---|---|
| Header, EOF, comments | `HEADER`, `EOF`, `COMMENT` (EMF+ detected, not interpreted) |
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

## Gaps (each produces a diagnostic)

`WIDENPATH`, `SETMETARGN`, `EXTFLOODFILL`, text inside a path bracket, glyph-index text (treated as code points), vertical advances in `ExtTextOut` (`PDY`), the `XformSrc` of blits, `DIB_PAL_COLORS` bitmaps, JPEG or PNG compressed DIBs, right-to-left layout, EMF+ records, and WMF (planned). Skipped records never stop the interpretation.

## Limits and safety

`MetafileLimits` bounds the file size, record and command counts, object table, path points, bitmap pixels (per bitmap and in total), save depth, region rectangles, text length, dash entries, and a total work budget; every size read from a record is checked against the record's own length and these limits before an allocation, and a bad record is skipped with a diagnostic rather than failing the file. A file that breaks a limit throws `ImageLimitException`; a file with a bad header throws `ImageFormatException`. Interpretation is iterative (no recursion on input) and checks for cancellation.

## Checked against Windows

See the comparison section below once the Windows reference renderings are in the repository.
