#!/usr/bin/env python3
"""Builds the test font for OpenType shaping in tests/testdata/fonts.

Usage: make-shaping-fonts.py <NotoSans-Regular.ttf> <outdir>
Requires fontTools. The output is committed; this script documents how it was made.

* NotoSans-Shaping-Subset.ttf: Noto Sans (SIL OFL 1.1) cut down to Latin, Greek, Cyrillic and the combining marks, with all of its
  GSUB/GPOS/GDEF layout kept (ligatures, ccmp compositions, locl forms, mark and mark-to-mark positioning, extension lookups).
"""
import os
import sys
from fontTools import subset
from fontTools.ttLib import TTFont

src, out = sys.argv[1:3]

unicodes = (list(range(0x20, 0x7F)) + list(range(0xA0, 0x180)) + list(range(0x300, 0x370)) + list(range(0x370, 0x400))
            + list(range(0x400, 0x500)) + [0x2013, 0x2014, 0x2018, 0x2019, 0x201C, 0x201D, 0x2026, 0x20AC] + list(range(0xFB00, 0xFB07)))
opts = subset.Options()
opts.layout_features = ['*']
opts.layout_scripts = ['*']
opts.name_IDs = ['*']
opts.notdef_outline = True
opts.hinting = False
opts.drop_tables += ['DSIG']
font = TTFont(src)
s = subset.Subsetter(opts)
s.populate(unicodes=unicodes)
s.subset(font)
font.save(os.path.join(out, 'NotoSans-Shaping-Subset.ttf'))

