#!/usr/bin/env python3
"""Sorts the pages of the gate corpus into content classes from the display lists `PdfRender dump` writes, so agreement can be reported by what a page contains.

Usage: classify.py <dumps-dir> <manifest.tsv> <out.tsv>
Classes, first match wins:
  forms         the page has a widget annotation
  transparency  a transparency group, a soft mask, a constant alpha below 1 or a blend mode other than Normal is used
  images        pictures cover at least 20% of the page
  dense-text    400 or more glyphs
  vector        40 or more fills and strokes (and fewer than 400 glyphs)
  sparse        everything else (little text, few shapes)
"""
import os, re, sys

dumps, manifest, out = sys.argv[1:4]
GS = re.compile(r"ctm=\[([-\d.eE+ ]+)\].*? fa=([\d.]+) sa=([\d.]+) bm=(\w+)")
PAGE = re.compile(r"size=([\d.]+) ([\d.]+)")
rows = []
for line in open(manifest):
    name, cat = line.rstrip("\n").split("\t")
    path = os.path.join(dumps, name + ".txt")
    text = open(path, errors="replace").read() if os.path.exists(path) else ""
    glyphs = fills = widgets = 0
    transp = False
    area = 0.0
    page = 1.0
    last_ctm = None
    for l in text.splitlines():
        s = l.strip()
        if s.startswith("page "):
            m = PAGE.search(s)
            if m:
                page = max(1.0, float(m.group(1)) * float(m.group(2)))
        elif s.startswith("gs "):
            m = GS.search(s)
            if m:
                v = [float(x) for x in m.group(1).split()]
                last_ctm = v
                if float(m.group(2)) < 1 or float(m.group(3)) < 1 or m.group(4) != "Normal":
                    transp = True
        elif s.startswith("text "):
            glyphs += s.count("@")
        elif s.startswith(("fill ", "stroke ")):
            fills += 1
        elif s.startswith("image ") and last_ctm and len(last_ctm) >= 4:
            area += abs(last_ctm[0] * last_ctm[3] - last_ctm[1] * last_ctm[2])
        elif s.startswith("begin-annot /Widget"):
            widgets += 1
        elif s.startswith(("begin-group", "begin-softmask")):
            transp = True
    if widgets:
        cls = "forms"
    elif transp:
        cls = "transparency"
    elif area >= 0.2 * page:
        cls = "images"
    elif glyphs >= 400:
        cls = "dense-text"
    elif fills >= 40:
        cls = "vector"
    else:
        cls = "sparse"
    rows.append((name, cls, glyphs, fills, round(area / page, 2)))
with open(out, "w") as f:
    for r in rows:
        f.write("\t".join(map(str, r)) + "\n")
import collections
print(collections.Counter(r[1] for r in rows))
