#!/usr/bin/env python3
"""Puts our render next to the reference renders (and a difference map against the first reference) for a closer look.

Usage: montage.py <renders-dir> <name> <out.png> [scale-down-divisor]
"""
import os, sys
import numpy as np
from PIL import Image

renders, name, out = sys.argv[1:4]
div = int(sys.argv[4]) if len(sys.argv) > 4 else 2
tiles = []
for o in ("ours", "pdfium", "mutool", "pdfjs"):
    p = os.path.join(renders, o, name + ".png")
    if os.path.exists(p):
        tiles.append((o, Image.open(p).convert("RGB")))
base = tiles[0][1]
if len(tiles) > 1:
    a = np.asarray(base, dtype=np.int16)
    b = np.asarray(tiles[1][1].resize(base.size), dtype=np.int16)
    d = np.clip(255 - np.abs(a - b).max(axis=2) * 3, 0, 255).astype(np.uint8)
    tiles.append(("diff", Image.fromarray(d).convert("RGB")))
w = max(t.size[0] for _, t in tiles) // div
h = max(t.size[1] for _, t in tiles) // div
sheet = Image.new("RGB", (w * len(tiles) + 4 * (len(tiles) - 1), h), (200, 0, 200))
for i, (_, t) in enumerate(tiles):
    sheet.paste(t.resize((t.size[0] // div, t.size[1] // div), Image.LANCZOS), (i * (w + 4), 0))
sheet.save(out)
print([n for n, _ in tiles])
