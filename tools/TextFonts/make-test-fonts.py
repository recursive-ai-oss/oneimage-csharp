#!/usr/bin/env python3
"""Builds the small test fonts in tests/testdata/fonts from Noto fonts (SIL OFL 1.1).

Usage: make-test-fonts.py <NotoSans-Regular.ttf> <NotoSans-Bold.ttf> <NotoSansCJK-Regular.ttc> <outdir>
Requires fontTools. The output is committed; this script only documents how it was made.
"""
import sys
from fontTools import subset
from fontTools.ttLib import TTFont, TTCollection, newTable
from fontTools.ttLib.tables import _k_e_r_n

reg, bold, cjk, out = sys.argv[1:5]

LATIN = list(range(0x20, 0x7F)) + list(range(0xA0, 0x100)) + list(range(0x391, 0x3CA)) + list(range(0x410, 0x450)) + [0x2013, 0x2014, 0x2018, 0x2019, 0x201C, 0x201D, 0x2026, 0x20AC]


def do_subset(path, unicodes, number=0, drop_gpos=False):
    opts = subset.Options()
    opts.layout_features = ['*']
    opts.name_IDs = ['*']
    opts.notdef_outline = True
    opts.hinting = False
    opts.drop_tables += ['DSIG']
    if drop_gpos:
        opts.drop_tables += ['GPOS']
    font = TTFont(path, fontNumber=number)
    s = subset.Subsetter(opts)
    s.populate(unicodes=unicodes)
    s.subset(font)
    return font


regular = do_subset(reg, LATIN)
regular.save(out + '/NotoSans-Subset-Regular.ttf')

# The bold face carries only a legacy kern table (format 0), so the kern path has a font of its own.
b = do_subset(bold, LATIN, drop_gpos=True)
cmap = b.getBestCmap()
k = newTable('kern')
k.version = 0
sub = _k_e_r_n.KernTable_format_0()
sub.coverage = 1
sub.format = 0
sub.kernTable = {}
for pair, v in [('AV', -120), ('AT', -110), ('VA', -120), ('To', -90), ('Wa', -50), ('AW', -60)]:
    sub.kernTable[(cmap[ord(pair[0])], cmap[ord(pair[1])])] = v
k.kernTables = [sub]
b['kern'] = k
b.save(out + '/NotoSans-Subset-Bold-kern.ttf')

collection = TTCollection()
collection.fonts = [TTFont(out + '/NotoSans-Subset-Regular.ttf'), TTFont(out + '/NotoSans-Subset-Bold-kern.ttf')]
collection.save(out + '/NotoSans-Subset.ttc')

# CID-keyed CFF with subroutines: Latin, kana and a few ideographs.
cff = do_subset(cjk, LATIN + list(range(0x3041, 0x3094)) + [0x4E00, 0x4E8C, 0x4E09, 0x65E5, 0x672C, 0x8A9E], number=0)
cff.save(out + '/NotoSansCJK-Subset.otf')

# The same glyphs as the regular face, wrapped as WOFF 1.0 (zlib-compressed tables).
w = TTFont(out + '/NotoSans-Subset-Regular.ttf')
w.flavor = 'woff'
w.save(out + '/NotoSans-Subset-Regular.woff')
