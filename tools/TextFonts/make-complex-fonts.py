#!/usr/bin/env python3
"""Builds the Indic, Universal Shaping Engine, Thai and Lao test fonts in tests/testdata/fonts.

Usage: make-complex-fonts.py <dir-with-Noto-fonts> <outdir>
Requires fontTools. The output is committed; this script documents how it was made.

The inputs are the Noto fonts (SIL OFL 1.1) built by the Noto project, for example
https://github.com/notofonts/notofonts.github.io/raw/main/fonts/NotoSansDevanagari/unhinted/ttf/NotoSansDevanagari-Regular.ttf
(NotoSerifTibetan for Tibetan). Each subset keeps the script's Unicode block plus the space, no-break space, joiners, dotted
circle and the common punctuation, with all GSUB/GPOS/GDEF layout kept and hinting removed.
"""
import os
import sys
from fontTools import subset
from fontTools.ttLib import TTFont

src, out = sys.argv[1:3]

BLOCKS = {
    "NotoSansDevanagari": [(0x900, 0x97F), (0xA8E0, 0xA8FF)],
    "NotoSansBengali": [(0x980, 0x9FF)],
    "NotoSansGurmukhi": [(0xA00, 0xA7F)],
    "NotoSansGujarati": [(0xA80, 0xAFF)],
    "NotoSansOriya": [(0xB00, 0xB7F)],
    "NotoSansTamil": [(0xB80, 0xBFF)],
    "NotoSansTelugu": [(0xC00, 0xC7F)],
    "NotoSansKannada": [(0xC80, 0xCFF)],
    "NotoSansMalayalam": [(0xD00, 0xD7F)],
    "NotoSansSinhala": [(0xD80, 0xDFF)],
    "NotoSansKhmer": [(0x1780, 0x17FF), (0x19E0, 0x19FF)],
    "NotoSansMyanmar": [(0x1000, 0x109F), (0xA9E0, 0xA9FF), (0xAA60, 0xAA7F)],
    "NotoSerifTibetan": [(0xF00, 0xFFF)],
    "NotoSansThai": [(0xE00, 0xE7F)],
    "NotoSansLao": [(0xE80, 0xEFF)],
    "NotoSansBalinese": [(0x1B00, 0x1B7F)],
    "NotoSansJavanese": [(0xA980, 0xA9DF)],
}
COMMON = [(0x20, 0x20), (0xA0, 0xA0), (0x2D, 0x2D), (0x200B, 0x200D), (0x2013, 0x2014), (0x25CC, 0x25CC), (0x30, 0x39)]

for name, ranges in BLOCKS.items():
    path = os.path.join(src, name + "-Regular.ttf")
    unicodes = [c for lo, hi in ranges + COMMON for c in range(lo, hi + 1)]
    opts = subset.Options()
    opts.layout_features = ["*"]
    opts.layout_scripts = ["*"]
    opts.name_IDs = ["*"]
    opts.notdef_outline = True
    opts.hinting = False
    opts.drop_tables += ["DSIG"]
    font = TTFont(path)
    s = subset.Subsetter(opts)
    s.populate(unicodes=unicodes)
    s.subset(font)
    dst = os.path.join(out, name + "-Subset-Regular.ttf")
    font.save(dst)
    print(dst, os.path.getsize(dst))
