#!/usr/bin/env python3
"""Builds the COLR/CPAL test fonts in tests/testdata/fonts from scratch with fontTools (geometric shapes drawn here; no third-party glyphs).

Usage: make-color-fonts.py <outdir>
Writes OneImageColorTest-V0.ttf (COLR version 0, two CPAL palettes) and OneImageColorTest-V1.ttf (the same, plus a COLR version 1
paint graph for one extra glyph). The output is committed; this script documents how it was made.

Code points: U+0041 'A' a plain triangle (no color), U+2600 three overlapping shapes, U+2601 two layers where one uses the
foreground color (palette index 0xFFFF), U+2602 a half-transparent layer over an opaque one, U+2603 colors that differ between
the two palettes, U+2604 a base glyph with a layer that is a composite of two components, U+2610 (V1 font only) a glyph that only
has a version 1 paint graph.
"""
import sys

from fontTools.colorLib.builder import buildCOLR, buildCPAL
from fontTools.fontBuilder import FontBuilder
from fontTools.pens.ttGlyphPen import TTGlyphPen
from fontTools.ttLib.tables import otTables as ot

out = sys.argv[1]
UPM = 1000


def poly(points):
    p = TTGlyphPen(None)
    p.moveTo(points[0])
    for q in points[1:]:
        p.lineTo(q)
    p.closePath()
    return p.glyph()


def disc(cx, cy, r):
    # Eight on-curve points with quadratic off-curve points between them (a circle to within a fraction of a percent).
    import math
    p = TTGlyphPen(None)
    k = r / math.cos(math.pi / 8)
    pts = []
    for i in range(8):
        a = i * math.pi / 4
        pts.append(((cx + r * math.cos(a), cy + r * math.sin(a)), (cx + k * math.cos(a + math.pi / 8), cy + k * math.sin(a + math.pi / 8))))
    p.moveTo((round(pts[0][0][0]), round(pts[0][0][1])))
    for i in range(8):
        on = pts[(i + 1) % 8][0]
        p.qCurveTo((round(pts[i][1][0]), round(pts[i][1][1])), (round(on[0]), round(on[1])))
    p.closePath()
    return p.glyph()


def rect(x0, y0, x1, y1):
    return poly([(x0, y0), (x0, y1), (x1, y1), (x1, y0)])


def composite(parts):
    p = TTGlyphPen({n: g for n, g in parts_glyphs.items()})
    for name, dx, dy in parts:
        p.addComponent(name, (1, 0, 0, 1, dx, dy))
    return p.glyph()


parts_glyphs = {}
glyphs = {
    ".notdef": rect(100, 0, 500, 700),
    "space": TTGlyphPen(None).glyph(),
    "A": poly([(50, 0), (350, 700), (650, 0)]),
    "sq": rect(100, 0, 700, 600),
    "tri": poly([(100, 0), (400, 700), (700, 0)]),
    "dot": disc(400, 350, 300),
    "bar": rect(0, 250, 800, 450),
    "half": rect(100, 0, 400, 700),
    "ring": disc(400, 350, 200),
    "sq2": rect(0, 0, 300, 300),
}
parts_glyphs.update(glyphs)
glyphs["pair"] = composite([("sq2", 0, 0), ("sq2", 400, 400)])
for name in ("base_tri", "base_fg", "base_alpha", "base_pal", "base_comp", "base_v1"):
    glyphs[name] = rect(50, 0, 750, 700)     # the plain outline shown when color is off

order = [".notdef", "space", "A", "sq", "tri", "dot", "bar", "half", "ring", "sq2", "pair", "base_tri", "base_fg", "base_alpha", "base_pal", "base_comp", "base_v1"]
cmap = {0x20: "space", 0x41: "A", 0x2600: "base_tri", 0x2601: "base_fg", 0x2602: "base_alpha", 0x2603: "base_pal", 0x2604: "base_comp", 0x2610: "base_v1"}

fb = FontBuilder(UPM, isTTF=True)
fb.setupGlyphOrder(order)
fb.setupCharacterMap(cmap)
fb.setupGlyf({n: glyphs[n] for n in order})
metrics = {n: (800 if n not in (".notdef", "space") else 500, 0) for n in order}
# The last glyph differs so that hmtx keeps one full metric record per glyph (a shorter hmtx is valid, but a test font should not depend on that).
metrics["base_v1"] = (790, 0)
fb.setupHorizontalMetrics({n: (w, getattr(glyphs[n], "xMin", 0) if glyphs[n].numberOfContours else 0) for n, (w, _) in metrics.items()})
fb.setupHorizontalHeader(ascent=800, descent=-200)
fb.setupNameTable({"familyName": "OneImage Color Test", "styleName": "Regular", "uniqueFontIdentifier": "OneImageColorTest-V0", "fullName": "OneImage Color Test", "psName": "OneImageColorTest"})
fb.setupOS2(sTypoAscender=800, sTypoDescender=-200, usWinAscent=800, usWinDescent=200)
fb.setupPost()

palettes = [
    [(0.9, 0.1, 0.1, 1.0), (0.1, 0.6, 0.2, 1.0), (0.1, 0.2, 0.9, 1.0), (1.0, 0.8, 0.0, 1.0), (0.0, 0.0, 0.0, 0.5)],
    [(0.1, 0.9, 0.9, 1.0), (0.9, 0.1, 0.8, 1.0), (0.3, 0.3, 0.3, 1.0), (0.5, 0.9, 0.1, 1.0), (1.0, 1.0, 1.0, 0.25)],
]
cpal = buildCPAL(palettes)

layers_v0 = {
    "base_tri": [("sq", 0), ("tri", 1), ("dot", 2)],
    "base_fg": [("bar", 0xFFFF), ("ring", 3)],
    "base_alpha": [("sq", 0), ("dot", 4)],
    "base_pal": [("half", 0), ("ring", 1), ("bar", 2)],
    "base_comp": [("pair", 1), ("ring", 0)],
}


def save(name, colr):
    font = fb.font
    font["COLR"] = colr
    font["CPAL"] = cpal
    font.save(f"{out}/{name}")


save("OneImageColorTest-V0.ttf", buildCOLR(layers_v0, version=0))

paints = dict(layers_v0)
paints["base_v1"] = {
    "Format": ot.PaintFormat.PaintGlyph,
    "Glyph": "dot",
    "Paint": {"Format": ot.PaintFormat.PaintSolid, "PaletteIndex": 3, "Alpha": 1.0},
}
save("OneImageColorTest-V1.ttf", buildCOLR(paints))
print("ok")
