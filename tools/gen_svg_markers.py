#!/usr/bin/env python3
"""Writes the two marker and blend documents used to time the SVG renderer: tests/testdata/svg/markers.svg (about 250 paths with 25
vertices each carrying arrow, dot and square markers with orient=auto, viewBoxes and stroke-width units, plus 60 groups blended with
mix-blend-mode and isolation) and tests/testdata/svg/markers-10k.svg (one polyline of 10,000 vertices with a marker on every vertex).

    python3 tools/gen_svg_markers.py mixed > tests/testdata/svg/markers.svg
    python3 tools/gen_svg_markers.py polyline > tests/testdata/svg/markers-10k.svg
"""
import math
import random
import sys

rnd = random.Random(20261022)
out = []
w = out.append


def col():
    return "#%02x%02x%02x" % (rnd.randrange(30, 240), rnd.randrange(30, 240), rnd.randrange(30, 240))


def head():
    w('<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 1000 800" width="1000" height="800">')


def mixed():
    head()
    w("<defs>")
    w('<marker id="arrow" viewBox="0 0 10 10" refX="5" refY="5" markerWidth="4" markerHeight="4" orient="auto"><path d="M 0 0 L 10 5 L 0 10 z" fill="context-stroke"/></marker>')
    w('<marker id="dot" markerWidth="3" markerHeight="3" refX="1.5" refY="1.5"><circle cx="1.5" cy="1.5" r="1.2" fill="#222"/></marker>')
    w('<marker id="box" markerUnits="userSpaceOnUse" markerWidth="8" markerHeight="8" refX="4" refY="4" orient="auto-start-reverse"><rect width="8" height="8" fill="#d33" opacity="0.8"/></marker>')
    w("</defs>")
    for i in range(250):
        x, y = rnd.uniform(40, 960), rnd.uniform(40, 760)
        pts = []
        a = rnd.uniform(0, 6.28)
        for k in range(25):
            a += rnd.uniform(-0.6, 0.6)
            x = min(max(x + 12 * math.cos(a), 10), 990)
            y = min(max(y + 12 * math.sin(a), 10), 790)
            pts.append("%.1f,%.1f" % (x, y))
        marks = rnd.choice([
            'marker-start="url(#arrow)" marker-end="url(#arrow)" marker-mid="url(#dot)"',
            'marker-mid="url(#box)"',
            'marker-start="url(#box)" marker-end="url(#arrow)"',
        ])
        shape = rnd.choice(["polyline", "polygon"])
        w('<%s points="%s" fill="none" stroke="%s" stroke-width="%d" %s/>' % (shape, " ".join(pts), col(), rnd.randrange(1, 4), marks))
    modes = ["multiply", "screen", "overlay", "darken", "lighten", "difference", "exclusion", "hard-light", "soft-light", "color-dodge", "hue", "luminosity"]
    for i in range(60):
        iso = ' style="isolation:isolate"' if i % 5 == 0 else ""
        w("<g%s>" % iso)
        for k in range(2):
            x, y, r = rnd.uniform(50, 950), rnd.uniform(50, 750), rnd.uniform(20, 80)
            w('<circle cx="%.0f" cy="%.0f" r="%.0f" fill="%s" style="mix-blend-mode:%s" opacity="%.2f"/>' % (x, y, r, col(), rnd.choice(modes), rnd.uniform(0.5, 1)))
        w("</g>")
    w("</svg>")


def polyline():
    head()
    w('<marker id="dot" markerWidth="3" markerHeight="3" refX="1.5" refY="1.5" orient="auto"><rect width="3" height="3" fill="#c22"/></marker>')
    pts = []
    for k in range(10000):
        t = k / 10000.0
        pts.append("%.2f,%.2f" % (500 + 450 * math.cos(t * 62.8) * (0.2 + t * 0.8), 400 + 350 * math.sin(t * 62.8) * (0.2 + t * 0.8)))
    w('<polyline points="%s" fill="none" stroke="#369" stroke-width="1" marker-start="url(#dot)" marker-mid="url(#dot)" marker-end="url(#dot)"/>' % " ".join(pts))
    w("</svg>")


if sys.argv[1:] == ["mixed"]:
    mixed()
elif sys.argv[1:] == ["polyline"]:
    polyline()
else:
    sys.exit(__doc__)
print("\n".join(out))
