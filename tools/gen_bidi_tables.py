#!/usr/bin/env python3
"""Generates src/OneImage.Text/Unicode/BidiData.g.cs from the Unicode Character Database.

Usage: python3 tools/gen_bidi_tables.py [--version 17.0.0] [--cache DIR] [--test-data DIR]

Downloads DerivedBidiClass.txt, BidiBrackets.txt, BidiMirroring.txt, ArabicShaping.txt and DerivedGeneralCategory.txt into
--cache (default ./ucd-cache, never committed), writes the Bidi_Class ranges, the paired brackets, the mirroring pairs and the
Arabic joining types, and copies BidiTest.txt and BidiCharacterTest.txt into --test-data (default tests/testdata/unicode)
for the conformance tests.

The Unicode data files are (c) Unicode, Inc. and used under the Unicode License v3 (see THIRD-PARTY-NOTICES.md).
"""
import argparse
import os
import re
import shutil
import sys
import urllib.request

NAMES = ["L", "R", "AL", "EN", "ES", "ET", "AN", "CS", "NSM", "BN", "B", "S", "WS", "ON",
         "LRE", "LRO", "RLE", "RLO", "PDF", "LRI", "RLI", "FSI", "PDI"]
LONG = {"Left_To_Right": "L", "Right_To_Left": "R", "Arabic_Letter": "AL", "European_Number": "EN",
        "European_Separator": "ES", "European_Terminator": "ET", "Arabic_Number": "AN", "Common_Separator": "CS",
        "Nonspacing_Mark": "NSM", "Boundary_Neutral": "BN", "Paragraph_Separator": "B", "Segment_Separator": "S",
        "White_Space": "WS", "Other_Neutral": "ON", "Left_To_Right_Embedding": "LRE", "Left_To_Right_Override": "LRO",
        "Right_To_Left_Embedding": "RLE", "Right_To_Left_Override": "RLO", "Pop_Directional_Format": "PDF",
        "Left_To_Right_Isolate": "LRI", "Right_To_Left_Isolate": "RLI", "First_Strong_Isolate": "FSI",
        "Pop_Directional_Isolate": "PDI"}
# Joining types: 0 U (none), 1 L, 2 R, 3 D, 4 C, 5 T
JT = {"U": 0, "L": 1, "R": 2, "D": 3, "C": 4, "T": 5}
MAX = 0x110000

FILES = {
    "DerivedBidiClass.txt": "extracted/DerivedBidiClass.txt",
    "BidiBrackets.txt": "BidiBrackets.txt",
    "BidiMirroring.txt": "BidiMirroring.txt",
    "ArabicShaping.txt": "ArabicShaping.txt",
    "DerivedGeneralCategory.txt": "extracted/DerivedGeneralCategory.txt",
    "BidiTest.txt": "BidiTest.txt",
    "BidiCharacterTest.txt": "BidiCharacterTest.txt",
}


def fetch(version, cache):
    os.makedirs(cache, exist_ok=True)
    for name, rel in FILES.items():
        path = os.path.join(cache, name)
        if not os.path.exists(path):
            url = f"https://www.unicode.org/Public/{version}/ucd/{rel}"
            print("fetching", url, file=sys.stderr)
            urllib.request.urlretrieve(url, path)


def lines(path):
    with open(path, encoding="utf-8") as f:
        return f.read().splitlines()


def main():
    here = os.path.dirname(os.path.abspath(__file__))
    root = os.path.dirname(here)
    ap = argparse.ArgumentParser()
    ap.add_argument("--version", default="17.0.0")
    ap.add_argument("--cache", default=os.path.join(os.getcwd(), "ucd-cache"))
    ap.add_argument("--test-data", default=os.path.join(root, "tests", "testdata", "unicode"))
    ap.add_argument("--out", default=os.path.join(root, "src", "OneImage.Text", "Unicode", "BidiData.g.cs"))
    a = ap.parse_args()
    fetch(a.version, a.cache)
    p = lambda n: os.path.join(a.cache, n)

    cls = [0] * MAX
    for line in lines(p("DerivedBidiClass.txt")):
        m = re.match(r"# @missing: ([0-9A-F]+)\.\.([0-9A-F]+); (\w+)", line)
        if m:
            lo, hi, n = int(m[1], 16), int(m[2], 16), NAMES.index(LONG[m[3]])
            for c in range(lo, hi + 1):
                cls[c] = n
    for line in lines(p("DerivedBidiClass.txt")):
        line = line.split("#")[0].strip()
        if not line:
            continue
        m = re.match(r"([0-9A-F]+)(?:\.\.([0-9A-F]+))?\s*;\s*(\w+)", line)
        lo = int(m[1], 16)
        hi = int(m[2], 16) if m[2] else lo
        n = NAMES.index(m[3])
        for c in range(lo, hi + 1):
            cls[c] = n
    runs = []
    start = 0
    for c in range(1, MAX + 1):
        if c == MAX or cls[c] != cls[start]:
            if cls[start] != 0:  # L is the default
                runs.append((start, c - 1, cls[start]))
            start = c

    brackets = []
    for line in lines(p("BidiBrackets.txt")):
        line = line.split("#")[0].strip()
        if line:
            x, y, t = [s.strip() for s in line.split(";")]
            brackets.append((int(x, 16), int(y, 16), 1 if t == "o" else 2))

    mirror = []
    for line in lines(p("BidiMirroring.txt")):
        line = line.split("#")[0].strip()
        if line:
            x, y = [s.strip() for s in line.split(";")]
            mirror.append((int(x, 16), int(y, 16)))
    mirror.sort()

    # Joining types: listed ones, else T for Mn, Me and Cf, else U (ArabicShaping.txt header).
    gc = ["Cn"] * MAX
    for line in lines(p("DerivedGeneralCategory.txt")):
        line = line.split("#")[0].strip()
        if line:
            m = re.match(r"([0-9A-F]+)(?:\.\.([0-9A-F]+))?\s*;\s*(\w+)", line)
            lo = int(m[1], 16)
            hi = int(m[2], 16) if m[2] else lo
            for c in range(lo, hi + 1):
                gc[c] = m[3]
    jt = [5 if gc[c] in ("Mn", "Me", "Cf") else 0 for c in range(MAX)]
    for line in lines(p("ArabicShaping.txt")):
        line = line.split("#")[0].strip()
        if line:
            f = [s.strip() for s in line.split(";")]
            jt[int(f[0], 16)] = JT[f[2]]
    jruns = []
    start = 0
    for c in range(1, MAX + 1):
        if c == MAX or jt[c] != jt[start]:
            if jt[start] != 0:
                jruns.append((start, c - 1, jt[start]))
            start = c

    def emit(name, doc, rows, width):
        out = [f"    /// <summary>{doc}</summary>", f"    internal static ReadOnlySpan<int> {name} =>", "    ["]
        for i in range(0, len(rows), 6):
            out.append("        " + ", ".join(", ".join(f"0x{v:X}" if k < width - 1 or name in ("Mirroring",) else str(v) for k, v in enumerate(r)) for r in rows[i:i + 6]) + ",")
        out.append("    ];")
        return "\n".join(out)

    out = [
        "// <auto-generated>",
        f"// Generated by tools/gen_bidi_tables.py from the Unicode Character Database {a.version}. Do not edit.",
        "// Unicode data files are Copyright (c) Unicode, Inc. and used under the Unicode License v3 (THIRD-PARTY-NOTICES.md).",
        "// </auto-generated>",
        "namespace OneImage.Text.Unicode;",
        "",
        "internal static class BidiData",
        "{",
        f'    internal const string Version = "{a.version}";',
        "",
        emit("ClassRanges", "Triples of (first code point, last code point, Bidi_Class) for every range whose class is not L; ordered by code point.", runs, 3),
        "",
        emit("Brackets", "Triples of (code point, Bidi_Paired_Bracket, type: 1 open, 2 close) ordered by code point.", brackets, 3),
        "",
        emit("Mirroring", "Pairs of (code point, Bidi_Mirroring_Glyph) ordered by code point.", mirror, 2),
        "",
        emit("JoiningRanges", "Triples of (first, last, Joining_Type: 1 L, 2 R, 3 D, 4 C, 5 T) for every range that is not U; ordered by code point.", jruns, 3),
        "}",
    ]
    with open(a.out, "w", encoding="utf-8", newline="\n") as f:
        f.write("\n".join(out) + "\n")

    os.makedirs(a.test_data, exist_ok=True)
    for n in ("BidiTest.txt", "BidiCharacterTest.txt"):
        shutil.copyfile(p(n), os.path.join(a.test_data, n))
    print(f"bidi: {len(runs)} class ranges, {len(brackets)} brackets, {len(mirror)} mirrors, {len(jruns)} joining ranges", file=sys.stderr)


if __name__ == "__main__":
    main()
