#!/usr/bin/env python3
"""Writes tests/testdata/svg/illustration.svg: a deterministic, busy vector drawing (a made-up map with roads, blocks, a river,
trees, markers and labels-as-shapes) used to time and check the SVG renderer. Only features the renderer supports are used:
shapes, paths with curves and arcs, strokes with caps, joins and dashes, transforms, groups, group opacity, use of a symbol.

    python3 tools/gen_svg_illustration.py > tests/testdata/svg/illustration.svg
"""
import math
import random

rnd = random.Random(20261022)
out = []
w = out.append


def col():
    return "#%02x%02x%02x" % (rnd.randrange(40, 230), rnd.randrange(40, 230), rnd.randrange(40, 230))


w('<svg xmlns="http://www.w3.org/2000/svg" xmlns:xlink="http://www.w3.org/1999/xlink" viewBox="0 0 1000 800" width="1000" height="800">')
w('<defs><symbol id="tree" viewBox="0 0 10 10"><circle cx="5" cy="4" r="3.5" fill="#2e7d32"/><rect x="4.5" y="6" width="1" height="3.5" fill="#5d4037"/></symbol>')
w('<path id="leaf" d="M0 0 C 4 -6, 10 -6, 12 0 C 10 6, 4 6, 0 0 Z"/></defs>')
w('<rect width="1000" height="800" fill="#f3efe6"/>')

# city blocks
w('<g stroke="#8a8a80" stroke-width="0.6" stroke-linejoin="round">')
for gy in range(0, 20):
    for gx in range(0, 25):
        x, y = gx * 40 + 4, gy * 40 + 4
        pts = []
        n = rnd.choice([4, 5, 6])
        for i in range(n):
            a = 2 * math.pi * i / n + rnd.uniform(-0.2, 0.2)
            r = rnd.uniform(12, 17)
            pts.append("%.1f,%.1f" % (x + 16 + r * math.cos(a), y + 16 + r * math.sin(a)))
        w('<polygon points="%s" fill="%s"/>' % (" ".join(pts), col() if rnd.random() < 0.3 else "#e1dccb"))
w('</g>')

# roads
w('<g fill="none" stroke-linecap="round" stroke-linejoin="round">')
for i in range(60):
    x0, y0 = rnd.uniform(0, 1000), rnd.uniform(0, 800)
    d = "M %.1f %.1f" % (x0, y0)
    for _ in range(rnd.randrange(3, 7)):
        d += " C %.1f %.1f %.1f %.1f %.1f %.1f" % tuple(rnd.uniform(-90, 90) + (x0 if k % 2 == 0 else y0) for k in range(6))
    w('<path d="%s" stroke="#ffffff" stroke-width="%.1f"/>' % (d, rnd.uniform(3, 7)))
    w('<path d="%s" stroke="#c8b89a" stroke-width="%.1f" stroke-dasharray="%.0f %.0f"/>' % (d, rnd.uniform(0.6, 1.2), rnd.uniform(4, 10), rnd.uniform(3, 6)))
w('</g>')

# river
w('<path d="M -10 300 C 200 220 300 420 500 330 S 800 250 1010 360" fill="none" stroke="#7fb3d5" stroke-width="22" stroke-linecap="butt" opacity="0.85"/>')
w('<g opacity="0.8"><path d="M -10 300 C 200 220 300 420 500 330 S 800 250 1010 360" fill="none" stroke="#a9cce3" stroke-width="12"/>')
w('<path d="M 100 100 A 60 40 30 1 1 180 140 A 50 30 0 0 0 120 190 Z" fill="#a9cce3" stroke="#7fb3d5" stroke-width="2"/></g>')

# trees
w('<g>')
for i in range(500):
    x, y, s = rnd.uniform(0, 990), rnd.uniform(0, 790), rnd.uniform(6, 14)
    w('<use xlink:href="#tree" x="%.1f" y="%.1f" width="%.1f" height="%.1f"/>' % (x, y, s, s))
w('</g>')

# leaves and ornaments
w('<g stroke="#1b5e20" stroke-width="0.4">')
for i in range(400):
    x, y = rnd.uniform(0, 1000), rnd.uniform(0, 800)
    w('<use xlink:href="#leaf" transform="translate(%.1f %.1f) rotate(%.0f) scale(%.2f)" fill="%s" fill-opacity="0.8"/>' % (x, y, rnd.uniform(0, 360), rnd.uniform(0.4, 1.2), col()))
w('</g>')

# markers and badges
for i in range(300):
    x, y = rnd.uniform(10, 990), rnd.uniform(10, 790)
    r = rnd.uniform(3, 8)
    w('<g transform="translate(%.1f %.1f)"><circle r="%.1f" fill="%s" stroke="#fff" stroke-width="1.5"/><rect x="%.1f" y="%.1f" width="%.1f" height="%.1f" rx="1" fill="#fff" fill-opacity="0.9"/></g>' % (x, y, r, col(), -r / 3, -r / 3, 2 * r / 3, 2 * r / 3))

# translucent overlay zones
for i in range(12):
    x, y = rnd.uniform(0, 800), rnd.uniform(0, 600)
    w('<g opacity="0.25"><ellipse cx="%.0f" cy="%.0f" rx="%.0f" ry="%.0f" fill="%s"/><rect x="%.0f" y="%.0f" width="%.0f" height="%.0f" fill="%s" stroke="#000" stroke-width="2"/></g>' % (x + 100, y + 80, rnd.uniform(50, 120), rnd.uniform(30, 80), col(), x, y, rnd.uniform(60, 160), rnd.uniform(40, 120), col()))

w('<rect x="2" y="2" width="996" height="796" fill="none" stroke="#333" stroke-width="3"/>')
w('</svg>')
print("\n".join(out))
