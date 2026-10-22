#!/usr/bin/env python3
"""Builds the WOFF 2.0 test fonts in tests/testdata/fonts from the Noto test subsets (SIL OFL 1.1).

Usage: make-woff2-fonts.py <woff2_compress> <fontsdir>
Needs the reference `woff2_compress` tool, fontTools and the brotli module; they are used only to make test data. The output is committed.

Besides the plain conversions it writes NotoSans-Subset-Regular-hmtx.woff2: the same font with the optional hmtx transform
(WOFF 2.0 section 5.4: left side bearings dropped, rebuilt from the glyph boxes), which the reference tool does not emit.
"""
import os
import shutil
import struct
import subprocess
import sys
import tempfile

import brotli
from fontTools.ttLib import TTFont

tool, fonts = sys.argv[1:3]

KNOWN = [
    "cmap", "head", "hhea", "hmtx", "maxp", "name", "OS/2", "post", "cvt ", "fpgm", "glyf", "loca", "prep", "CFF ", "VORG", "EBDT",
    "EBLC", "gasp", "hdmx", "kern", "LTSH", "PCLT", "VDMX", "vhea", "vmtx", "BASE", "GDEF", "GPOS", "GSUB", "EBSC", "JSTF", "MATH",
    "CBDT", "CBLC", "COLR", "CPAL", "SVG ", "sbix", "acnt", "avar", "bdat", "bloc", "bsln", "cvar", "fdsc", "feat", "fmtx", "fvar",
    "gvar", "hsty", "just", "lcar", "mort", "morx", "opbd", "prop", "trak", "Zapf", "Silf", "Glat", "Gloc", "Feat", "Sill",
]
assert len(KNOWN) == 63


def base128(v):
    out = [v & 0x7F]
    v >>= 7
    while v:
        out.append(0x80 | (v & 0x7F))
        v >>= 7
    return bytes(reversed(out))


def read_base128(d, p):
    acc = 0
    while True:
        b = d[p]
        p += 1
        acc = (acc << 7) | (b & 0x7F)
        if not b & 0x80:
            return acc, p


def convert(src, dst_dir):
    shutil.copy(src, dst_dir)
    subprocess.run([tool, os.path.join(dst_dir, os.path.basename(src))], check=True, capture_output=True)


with tempfile.TemporaryDirectory() as tmp:
    for name in ("NotoSans-Subset-Regular.ttf", "NotoSansCJK-Subset.otf", "NotoSans-Subset.ttc", "NotoSans-Shaping-Subset.ttf"):
        convert(os.path.join(fonts, name), tmp)
        w = os.path.splitext(name)[0] + ".woff2"
        shutil.copy(os.path.join(tmp, w), os.path.join(fonts, w))

    # The hmtx variant: rewrite the directory entry and the table block of the regular face.
    data = open(os.path.join(fonts, "NotoSans-Subset-Regular.woff2"), "rb").read()
    num_tables = struct.unpack(">H", data[12:14])[0]
    comp_size = struct.unpack(">I", data[20:24])[0]
    p = 48
    entries = []
    for _ in range(num_tables):
        flags = data[p]
        p += 1
        tag = KNOWN[flags & 63] if flags & 63 != 63 else data[p:p + 4].decode("latin1")
        if flags & 63 == 63:
            p += 4
        orig, p = read_base128(data, p)
        stored = orig
        if (tag in ("glyf", "loca") and (flags >> 6) == 0):
            stored, p = read_base128(data, p)
        entries.append((flags, tag, orig, stored))
    block = brotli.decompress(data[p:p + comp_size])
    font = TTFont(os.path.join(fonts, "NotoSans-Subset-Regular.ttf"))
    glyf = font["glyf"]
    hmtx = font["hmtx"]
    order = font.getGlyphOrder()
    n_h = font["hhea"].numberOfHMetrics
    assert n_h == len(order), "the test font has monospaced-run glyphs; this script only handles the proportional run"
    for g in order:
        gl = glyf[g]
        xmin = getattr(gl, "xMin", 0) if gl.numberOfContours != 0 else 0
        assert hmtx[g][1] == xmin, (g, hmtx[g], xmin)

    out_dir = bytearray()
    out_block = bytearray()
    pos = 0
    for flags, tag, orig, stored in entries:
        chunk = block[pos:pos + stored]
        pos += stored
        if tag == "hmtx":
            adv = b"".join(struct.pack(">H", hmtx[g][0]) for g in order)
            # Flags 3: both left-side-bearing arrays are absent (the monospaced run is empty anyway).
            new = bytes([3]) + adv
            out_dir += bytes([(flags & 63) | 0x40]) + base128(orig) + base128(len(new))
            out_block += new
        else:
            out_dir += bytes([flags])
            if flags & 63 == 63:
                out_dir += tag.encode("latin1")
            out_dir += base128(orig)
            if tag in ("glyf", "loca") and (flags >> 6) == 0:
                out_dir += base128(stored)
            out_block += chunk
    comp = brotli.compress(bytes(out_block), quality=11)
    header = bytearray(data[:48])
    total = 48 + len(out_dir) + len(comp)
    header[8:12] = struct.pack(">I", total)
    header[20:24] = struct.pack(">I", len(comp))
    open(os.path.join(fonts, "NotoSans-Subset-Regular-hmtx.woff2"), "wb").write(bytes(header) + bytes(out_dir) + comp)
print("ok")
