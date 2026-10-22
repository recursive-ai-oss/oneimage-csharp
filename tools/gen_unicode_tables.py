#!/usr/bin/env python3
"""Generates src/OneImage.Text/Unicode/UnicodeData.g.cs from the Unicode Character Database.

Usage: gen_unicode_tables.py [--version 17.0.0] [--cache DIR] [--test-data DIR]

Downloads the UCD files it needs into --cache (default: ./ucd-cache, never committed), writes the compact property
tables used for line breaking (UAX #14) and grapheme cluster boundaries (UAX #29), and copies LineBreakTest.txt and
GraphemeBreakTest.txt into --test-data (default: tests/testdata/unicode) for the conformance tests.

Tables are two-stage tries over 64-code-point blocks with identical blocks stored once. Code points from U+40000 up
(except the tag/variation-selector block at U+E0000) share one default value.

The Unicode data files are (c) Unicode, Inc. and used under the Unicode License v3 (see THIRD-PARTY-NOTICES.md).
"""
import argparse
import os
import re
import shutil
import sys
import urllib.request

FILES = {
    "DerivedLineBreak.txt": "extracted/DerivedLineBreak.txt",
    "DerivedEastAsianWidth.txt": "extracted/DerivedEastAsianWidth.txt",
    "DerivedGeneralCategory.txt": "extracted/DerivedGeneralCategory.txt",
    "UnicodeData.txt": "UnicodeData.txt",
    "GraphemeBreakProperty.txt": "auxiliary/GraphemeBreakProperty.txt",
    "emoji-data.txt": "emoji/emoji-data.txt",
    "DerivedCoreProperties.txt": "DerivedCoreProperties.txt",
    "LineBreakTest.txt": "auxiliary/LineBreakTest.txt",
    "GraphemeBreakTest.txt": "auxiliary/GraphemeBreakTest.txt",
}

# Classes that survive the resolution of AI, SG, XX, SA and CJ (UAX #14 LB1). The order is the numeric value.
LB_CLASSES = ("BK CR LF CM NL WJ ZW GL SP ZWJ B2 BA BB HY HH CB CL CP EX IN NS OP QU IS NU PO PR SY "
              "AK AL AP AS EB EM H2 H3 HL ID JL JV JT RI VF VI").split()
GB_CLASSES = "Other CR LF Control Extend ZWJ RI Prepend SpacingMark L V T LV LVT".split()
INCB = {"None": 0, "Consonant": 1, "Linker": 2, "Extend": 3}

MAX = 0x110000
BLOCK = 64


def fetch(version, cache):
    os.makedirs(cache, exist_ok=True)
    for name, rel in FILES.items():
        path = os.path.join(cache, name)
        if not os.path.exists(path):
            url = f"https://www.unicode.org/Public/{version}/ucd/{rel}"
            print("fetching", url, file=sys.stderr)
            urllib.request.urlretrieve(url, path)


def ranges(path, want_column=1):
    """Yields (first, last, value) for 'XXXX[..YYYY] ; value' lines."""
    for line in open(path, encoding="utf-8"):
        line = line.split("#", 1)[0].strip()
        if not line:
            continue
        parts = [p.strip() for p in line.split(";")]
        a = parts[0].split("..")
        lo = int(a[0], 16)
        hi = int(a[-1], 16)
        yield lo, hi, parts[want_column:]


def fill(arr, path, col=1, mapper=lambda v: v[0]):
    for lo, hi, v in ranges(path, col):
        val = mapper(v)
        for c in range(lo, hi + 1):
            arr[c] = val


def compress(get, default):
    """Two-stage compression of get(cp) over the covered ranges."""
    covered = list(range(0, 0x40000, BLOCK)) + list(range(0xE0000, 0xE1000, BLOCK))
    blocks = []
    seen = {}
    index = []
    for start in covered:
        block = tuple(get(c) for c in range(start, start + BLOCK))
        if block not in seen:
            seen[block] = len(blocks)
            blocks.append(block)
        index.append(seen[block])
    return index, blocks


def emit_array(name, ctype, vals, per_line=16):
    """Emits a byte table; 16-bit values are stored little-endian (read with BinaryPrimitives) so the data is a plain
    read-only blob in the assembly with no allocation or static constructor."""
    if ctype == "ushort":
        flat = []
        for v in vals:
            flat += [v & 255, v >> 8]
    else:
        flat = list(vals)
    out = [f"    internal static ReadOnlySpan<byte> {name} => ["]
    for i in range(0, len(flat), 24):
        out.append("        " + ", ".join(str(v) for v in flat[i:i + 24]) + ",")
    out.append("    ];")
    return "\n".join(out)


def fill_with_defaults(path, long_names):
    """Reads a derived file: explicit lines win, then the '# @missing: range; value' lines apply, in file order, to
    whatever is still unset (the later, narrower defaults are listed after the catch-all one)."""
    arr = [None] * MAX
    for lo, hi, v in ranges(path):
        for c in range(lo, hi + 1):
            arr[c] = v[0]
    for line in open(path, encoding="utf-8"):
        m = re.match(r"#\s*@missing:\s*([0-9A-F]+)\.\.([0-9A-F]+);\s*(\w+)", line)
        if m:
            lo, hi, name = int(m.group(1), 16), int(m.group(2), 16), long_names[m.group(3)]
            if lo == 0 and hi == MAX - 1:
                base = name
                continue
            for c in range(lo, hi + 1):
                if arr[c] is None:
                    arr[c] = name
    return [base if v is None else v for v in arr]


def main():
    here = os.path.dirname(os.path.abspath(__file__))
    root = os.path.dirname(here)
    ap = argparse.ArgumentParser()
    ap.add_argument("--version", default="17.0.0")
    ap.add_argument("--cache", default=os.path.join(os.getcwd(), "ucd-cache"))
    ap.add_argument("--test-data", default=os.path.join(root, "tests", "testdata", "unicode"))
    ap.add_argument("--out", default=os.path.join(root, "src", "OneImage.Text", "Unicode", "UnicodeData.g.cs"))
    a = ap.parse_args()
    fetch(a.version, a.cache)
    p = lambda n: os.path.join(a.cache, n)

    gc = ["Cn"] * MAX
    fill(gc, p("DerivedGeneralCategory.txt"))
    # Code points that exist in UnicodeData but are not listed as assigned elsewhere are covered by the derived file.

    lb = fill_with_defaults(p("DerivedLineBreak.txt"), {"Unknown": "XX", "Ideographic": "ID", "Prefix_Numeric": "PR"})
    eaw = fill_with_defaults(p("DerivedEastAsianWidth.txt"), {"Neutral": "N", "Wide": "W"})

    def resolve(c):
        v = lb[c]
        if v in ("AI", "SG", "XX"):
            return "AL"
        if v == "SA":
            return "CM" if gc[c] in ("Mn", "Mc") else "AL"
        if v == "CJ":
            return "NS"
        return v

    extpict = [False] * MAX
    for lo, hi, v in ranges(p("emoji-data.txt")):
        if v[0] == "Extended_Pictographic":
            for c in range(lo, hi + 1):
                extpict[c] = True

    gb = ["Other"] * MAX
    fill(gb, p("GraphemeBreakProperty.txt"), mapper=lambda v: "RI" if v[0] == "Regional_Indicator" else v[0])
    incb = [0] * MAX
    for lo, hi, v in ranges(p("DerivedCoreProperties.txt")):
        if v[0] == "InCB":
            for c in range(lo, hi + 1):
                incb[c] = INCB[v[1]]

    lbid = {n: i for i, n in enumerate(LB_CLASSES)}
    gbid = {n: i for i, n in enumerate(GB_CLASSES)}

    def lbval(c):
        v = lbid[resolve(c)]
        if eaw[c] in ("F", "W", "H"):
            v |= 1 << 6
        if gc[c] == "Pi":
            v |= 1 << 7
        if gc[c] == "Pf":
            v |= 1 << 8
        if extpict[c] and gc[c] == "Cn":
            v |= 1 << 9
        return v

    def gbval(c):
        v = gbid[gb[c]]
        if extpict[c]:
            v |= 1 << 4
        v |= incb[c] << 5
        return v

    li, lbk = compress(lbval, None)
    gi, gbk = compress(gbval, None)
    # Default value for everything outside the covered ranges (unassigned or private use, no special properties).
    lb_default = lbid["AL"]
    gb_default = 0
    assert all(lbval(c) == lb_default for c in range(0x40000, 0xE0000, 997)), "uncovered range is not uniform"
    assert all(gbval(c) == gb_default for c in range(0x40000, 0xE0000, 997))
    assert all(lbval(c) == lb_default for c in range(0xE1000, MAX, 997) if gc[c] in ("Co", "Cn") and lb[c] == "XX")

    lbflat = [v for b in lbk for v in b]
    gbflat = [v for b in gbk for v in b]

    with open(a.out, "w", encoding="utf-8", newline="\n") as f:
        f.write("// <auto-generated>\n")
        f.write(f"// Generated by tools/gen_unicode_tables.py from the Unicode Character Database {a.version}. Do not edit.\n")
        f.write("// Unicode data files are Copyright (c) Unicode, Inc. and used under the Unicode License v3 (THIRD-PARTY-NOTICES.md).\n")
        f.write("// </auto-generated>\n")
        f.write("namespace OneImage.Text.Unicode;\n\n")
        f.write("/// <summary>Line_Break classes after the default resolution of AI, SG, XX, SA and CJ (UAX #14 rule LB1).</summary>\n")
        f.write("internal enum LineBreakClass : byte\n{\n")
        for i, n in enumerate(LB_CLASSES):
            f.write(f"    {n} = {i},\n")
        f.write("}\n\n")
        f.write("/// <summary>Grapheme_Cluster_Break values.</summary>\n")
        f.write("internal enum GraphemeClass : byte\n{\n")
        for i, n in enumerate(GB_CLASSES):
            f.write(f"    {n} = {i},\n")
        f.write("}\n\n")
        f.write("/// <summary>Packed per-code-point property tables. The 16-bit tables hold little-endian pairs of bytes.</summary>\n")
        f.write("internal static class UnicodeData\n{\n")
        f.write(f"    internal const string Version = \"{a.version}\";\n")
        f.write(f"    internal const ushort LineBreakDefault = {lb_default};\n")
        f.write(f"    internal const byte GraphemeDefault = {gb_default};\n")
        f.write(f"    internal const int BlockShift = {BLOCK.bit_length() - 1};\n\n")
        f.write("    // Line break value: bits 0-5 class, 6 East_Asian_Width F/W/H, 7 Gc=Pi, 8 Gc=Pf, 9 Extended_Pictographic and Gc=Cn.\n")
        f.write(emit_array("LineBreakIndex", "ushort", li) + "\n\n")
        f.write(emit_array("LineBreakBlocks", "ushort", lbflat) + "\n\n")
        f.write("    // Grapheme value: bits 0-3 class, 4 Extended_Pictographic, 5-6 Indic_Conjunct_Break (0 none, 1 consonant, 2 linker, 3 extend).\n")
        f.write(emit_array("GraphemeIndex", "ushort", gi) + "\n\n")
        f.write(emit_array("GraphemeBlocks", "byte", gbflat) + "\n")
        f.write("}\n")

    os.makedirs(a.test_data, exist_ok=True)
    for n in ("LineBreakTest.txt", "GraphemeBreakTest.txt"):
        shutil.copyfile(p(n), os.path.join(a.test_data, n))
    nbytes = len(li) * 2 + len(lbflat) * 2 + len(gi) * 2 + len(gbflat)
    print(f"line break: {len(lbk)} blocks; grapheme: {len(gbk)} blocks; table data {nbytes} bytes", file=sys.stderr)


if __name__ == "__main__":
    main()
