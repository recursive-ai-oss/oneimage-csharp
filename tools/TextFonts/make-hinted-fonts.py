#!/usr/bin/env python3
"""Builds the hinted test fonts in tests/testdata/fonts: a Latin subset of Noto Sans (ttfautohint instructions) that keeps fpgm,
prep, cvt and the glyph programs. SIL OFL 1.1.

Usage: make-hinted-fonts.py <NotoSans-Regular.ttf> <outdir>
Requires fontTools. The output is committed; this script only documents how it was made.
"""
import sys
from fontTools import subset
from fontTools.ttLib import TTFont

noto, out = sys.argv[1:3]
LATIN = list(range(0x20, 0x7F)) + list(range(0xA0, 0x100))


def do_subset(path, dest):
    opts = subset.Options()
    opts.layout_features = ['kern']
    opts.name_IDs = ['*']
    opts.notdef_outline = True
    opts.hinting = True
    opts.drop_tables += ['DSIG']
    font = TTFont(path)
    s = subset.Subsetter(opts)
    s.populate(unicodes=LATIN)
    s.subset(font)
    font.save(dest)


do_subset(noto, out + '/NotoSans-Hinted-Subset.ttf')
