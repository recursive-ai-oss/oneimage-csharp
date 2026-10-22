#!/usr/bin/env python3
"""Writes the two paint-server documents used to time the SVG renderer: tests/testdata/svg/gradients.svg (about 600 shapes filled and
stroked with 48 linear and radial gradients: both unit systems, gradientTransform, spread methods, focal points, opacity stops, href
inheritance) and tests/testdata/svg/patterns.svg (about 250 shapes filled with 24 patterns: dots, stripes, checkers, rotated and
nested tilings, bounding-box and user-space units, viewBox tiles).

    python3 tools/gen_svg_paint.py gradients > tests/testdata/svg/gradients.svg
    python3 tools/gen_svg_paint.py patterns  > tests/testdata/svg/patterns.svg
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
    w('<svg xmlns="http://www.w3.org/2000/svg" xmlns:xlink="http://www.w3.org/1999/xlink" viewBox="0 0 1000 800" width="1000" height="800">')


def gradients():
    head()
    w("<defs>")
    spreads = ["pad", "reflect", "repeat"]
    for i in range(48):
        stops = "".join(
            '<stop offset="%.2f" stop-color="%s"%s/>' % (k / 3, col(), ' stop-opacity="%.2f"' % rnd.uniform(0.4, 1) if rnd.random() < 0.3 else "")
            for k in range(4)
        )
        extra = ' spreadMethod="%s"' % spreads[i % 3]
        if i % 4 == 1:
            extra += ' gradientTransform="rotate(%d) skewX(%d)"' % (rnd.randrange(0, 90), rnd.randrange(-20, 20))
        if i % 6 == 2:
            extra += ' gradientUnits="userSpaceOnUse"'
        if i % 2 == 0:
            x2 = rnd.choice(["1", "0.6", "0.5"])
            if "userSpaceOnUse" in extra:
                w('<linearGradient id="g%d" x1="%d" y1="%d" x2="%d" y2="%d"%s>%s</linearGradient>' % (i, rnd.randrange(0, 500), rnd.randrange(0, 400), rnd.randrange(300, 1000), rnd.randrange(300, 800), extra, stops))
            else:
                w('<linearGradient id="g%d" x2="%s" y2="%.1f"%s>%s</linearGradient>' % (i, x2, rnd.uniform(0, 1), extra, stops))
        else:
            if "userSpaceOnUse" in extra:
                w('<radialGradient id="g%d" cx="%d" cy="%d" r="%d"%s>%s</radialGradient>' % (i, rnd.randrange(200, 800), rnd.randrange(200, 600), rnd.randrange(80, 400), extra, stops))
            else:
                w('<radialGradient id="g%d" cx="%.2f" cy="%.2f" r="%.2f" fx="%.2f" fy="%.2f"%s>%s</radialGradient>' % (i, rnd.uniform(0.3, 0.7), rnd.uniform(0.3, 0.7), rnd.uniform(0.3, 0.7), rnd.uniform(0.2, 0.8), rnd.uniform(0.2, 0.8), extra, stops))
    # href inheritance: stops from g0, geometry of its own
    for i in range(8):
        w('<linearGradient id="h%d" xlink:href="#g0" x2="%.2f" gradientTransform="rotate(%d)"/>' % (i, rnd.uniform(0.3, 1), i * 20))
    w("</defs>")
    w('<rect width="1000" height="800" fill="#202428"/>')
    for gy in range(0, 20):
        for gx in range(0, 30):
            x, y = gx * 33 + 4, gy * 40 + 4
            gid = ("g%d" % rnd.randrange(48)) if rnd.random() < 0.85 else ("h%d" % rnd.randrange(8))
            kind = rnd.random()
            if kind < 0.45:
                w('<rect x="%d" y="%d" width="%d" height="%d" rx="5" fill="url(#%s)"/>' % (x, y, rnd.randrange(22, 30), rnd.randrange(26, 36), gid))
            elif kind < 0.8:
                w('<circle cx="%d" cy="%d" r="%d" fill="url(#%s)" stroke="url(#g%d)" stroke-width="3"/>' % (x + 14, y + 16, rnd.randrange(9, 15), gid, rnd.randrange(48)))
            else:
                a = rnd.uniform(0, math.pi)
                w('<path d="M%d %d q 12 %d 26 %d" fill="none" stroke="url(#%s)" stroke-width="%d" stroke-linecap="round"/>' % (x, y + 20, rnd.randrange(-20, 20), rnd.randrange(-10, 10), gid, rnd.randrange(3, 9)))
    w("</svg>")


def patterns():
    head()
    w("<defs>")
    for i in range(24):
        size = rnd.choice([8, 10, 12, 16, 20, 25])
        c1, c2 = col(), col()
        tf = ""
        if i % 3 == 1:
            tf = ' patternTransform="rotate(%d)"' % rnd.randrange(5, 85)
        elif i % 3 == 2:
            tf = ' patternTransform="scale(%.2f) skewX(%d)"' % (rnd.uniform(0.7, 1.6), rnd.randrange(-15, 15))
        units = ' patternUnits="userSpaceOnUse"' if i % 4 != 3 else ""
        sz = ('width="%d" height="%d"' % (size, size)) if i % 4 != 3 else 'width="0.1" height="0.12"'
        kind = i % 5
        if kind == 0:
            body = '<circle cx="%.1f" cy="%.1f" r="%.1f" fill="%s"/>' % (size / 2, size / 2, size / 3, c1)
        elif kind == 1:
            body = '<rect width="%d" height="%.1f" fill="%s"/>' % (size, size / 2, c1)
        elif kind == 2:
            body = '<rect width="%.1f" height="%.1f" fill="%s"/><rect x="%.1f" y="%.1f" width="%.1f" height="%.1f" fill="%s"/>' % (size / 2, size / 2, c1, size / 2, size / 2, size / 2, size / 2, c2)
        elif kind == 3:
            body = '<path d="M0 %d L %d 0" stroke="%s" stroke-width="2"/>' % (size, size, c1)
        else:
            body = '<rect width="%d" height="%d" fill="%s" fill-opacity="0.4"/><circle cx="%.1f" cy="%.1f" r="%.1f" fill="%s"/>' % (size, size, c2, size / 2, size / 2, size / 4, c1)
        extra = ""
        if i % 6 == 5:
            extra = ' viewBox="0 0 10 10"'
        w('<pattern id="p%d" %s%s%s%s>%s</pattern>' % (i, sz, units, tf, extra, body))
    # nested: a pattern whose tile is painted by another pattern
    w('<pattern id="n0" patternUnits="userSpaceOnUse" width="60" height="60"><rect width="60" height="60" fill="url(#p0)"/><circle cx="30" cy="30" r="14" fill="url(#p2)" stroke="#000"/></pattern>')
    w('<pattern id="n1" patternUnits="userSpaceOnUse" width="90" height="70" patternTransform="rotate(15)"><rect width="90" height="70" fill="url(#n0)"/><rect x="10" y="10" width="40" height="30" fill="url(#p4)"/></pattern>')
    w("</defs>")
    w('<rect width="1000" height="800" fill="#f5f2ea"/>')
    for gy in range(0, 12):
        for gx in range(0, 20):
            x, y = gx * 50 + 4, gy * 66 + 4
            pid = "p%d" % rnd.randrange(24) if rnd.random() < 0.9 else "n%d" % rnd.randrange(2)
            kind = rnd.random()
            if kind < 0.5:
                w('<rect x="%d" y="%d" width="%d" height="%d" fill="url(#%s)" stroke="#444"/>' % (x, y, rnd.randrange(30, 44), rnd.randrange(40, 60), pid))
            elif kind < 0.85:
                w('<circle cx="%d" cy="%d" r="%d" fill="url(#%s)" stroke="url(#p%d)" stroke-width="5"/>' % (x + 20, y + 28, rnd.randrange(14, 22), pid, rnd.randrange(24)))
            else:
                w('<polygon points="%d,%d %d,%d %d,%d" fill="url(#%s)"/>' % (x, y + 50, x + 22, y, x + 44, y + 50, pid))
    w("</svg>")


if __name__ == "__main__":
    {"gradients": gradients, "patterns": patterns}[sys.argv[1]]()
    print("\n".join(out))
