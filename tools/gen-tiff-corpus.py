#!/usr/bin/env python3
"""Generates the TIFF decoder corpus in tests/testdata/tiff.

Every file is written by libtiff (through Pillow), read back by libtiff, and checked against the pixels it was built from, so
the expected-pixel files next to it are libtiff's own view of the data. Content is deterministic (fixed seeds).

Usage: python3 tools/gen-tiff-corpus.py [output-dir]

Each case produces <name>.tif and <name>.raw (the expected pixels in the library's layout: little-endian samples,
interleaved, rows top to bottom, no padding) and one line in manifest.txt: name|width|height|PixelFormat property name[|name of the .raw file when shared].
"""
import os
import sys

import numpy as np
from PIL import Image

OUT = sys.argv[1] if len(sys.argv) > 1 else os.path.join(os.path.dirname(__file__), "..", "tests", "testdata", "tiff")
os.makedirs(OUT, exist_ok=True)
W, H = 53, 37
manifest = []


def smooth(shape, seed, dtype=np.uint8, hi=256):
    """A gradient with a little noise: compresses like a photo-ish image, and exercises predictors."""
    rng = np.random.default_rng(seed)
    h, w = shape[:2]
    base = np.add.outer(np.arange(h) * 5, np.arange(w) * 3)
    if len(shape) == 3:
        planes = [(base + 40 * c) % hi for c in range(shape[2])]
        base = np.stack(planes, axis=2)
    noise = rng.integers(0, 4, size=shape)
    return ((base + noise) % hi).astype(dtype)


def emit(name, img, fmt, raw, check=None, **save):
    path = os.path.join(OUT, name + ".tif")
    img.save(path, **save)
    back = Image.open(path)
    back.load()
    got = np.asarray(back)
    want = raw if check is None else check
    assert got.shape == want.shape and np.array_equal(got, want), name
    with open(os.path.join(OUT, name + ".raw"), "wb") as f:
        f.write(np.ascontiguousarray(raw).astype(raw.dtype.newbyteorder("<")).tobytes())
    manifest.append("%s|%d|%d|%s" % (name, img.width, img.height, fmt))


COMPS = [
    ("none", dict(compression="raw")),
    ("lzw", dict(compression="tiff_lzw")),
    ("lzwpred", dict(compression="tiff_lzw", tiffinfo={317: 2})),
    ("deflate", dict(compression="tiff_adobe_deflate")),
    ("deflatepred", dict(compression="tiff_adobe_deflate", tiffinfo={317: 2})),
    ("packbits", dict(compression="packbits")),
]

# 8-bit integer modes
gray = smooth((H, W), 1)
rgb = smooth((H, W, 3), 2)
rgba = smooth((H, W, 4), 3)
cmyk = smooth((H, W, 4), 4)
la = smooth((H, W, 2), 5)
for tag, kw in COMPS:
    emit("gray8_" + tag, Image.fromarray(gray, "L"), "Gray8", gray, **kw)
    emit("rgb8_" + tag, Image.fromarray(rgb, "RGB"), "Rgb8", rgb, **kw)
    emit("rgba8_" + tag, Image.fromarray(rgba, "RGBA"), "Rgba8", rgba, **kw)
    emit("cmyk8_" + tag, Image.fromarray(cmyk, "CMYK"), "Cmyk8", cmyk, **kw)
    emit("grayalpha8_" + tag, Image.fromarray(la, "LA"), "GrayAlpha8", la, **kw)

# palette
pal_idx = (smooth((H, W), 6) % 200).astype(np.uint8)
pim = Image.fromarray(pal_idx, "P")
palette = []
for i in range(256):
    palette += [(i * 7) % 256, (i * 13 + 5) % 256, (255 - i)]
pim.putpalette(palette)
pal_rgb = np.asarray(pim.convert("RGB"))
for tag, kw in COMPS:
    emit("palette8_" + tag, pim, "Rgb8", pal_rgb, check=pal_idx, **kw)

# 16-bit gray and 32-bit float
g16 = smooth((H, W), 7, np.uint16, 65536) * 251
g16 = g16.astype(np.uint16)
f32 = (np.random.default_rng(8).random((H, W)) * 4 - 1).astype(np.float32)
f32 += np.add.outer(np.arange(H), np.arange(W)).astype(np.float32) / 50
for tag, kw in COMPS[:5]:
    emit("gray16_" + tag, Image.fromarray(g16), "Gray16", g16, **kw)
emit("grayf32_none", Image.fromarray(f32, "F"), "GrayF32", f32, compression="raw")
emit("grayf32_lzw", Image.fromarray(f32, "F"), "GrayF32", f32, compression="tiff_lzw")
emit("grayf32_lzwfloatpred", Image.fromarray(f32, "F"), "GrayF32", f32, compression="tiff_lzw", tiffinfo={317: 3})
emit("grayf32_deflatefloatpred", Image.fromarray(f32, "F"), "GrayF32", f32, compression="tiff_adobe_deflate", tiffinfo={317: 3})

# bilevel: 1-bit data compressed every way libtiff offers
bil = (smooth((H, W), 9) > 128)
bim = Image.fromarray(bil.astype(np.uint8) * 255, "L").convert("1")
bexp = np.where(bil, 255, 0).astype(np.uint8)
for tag, kw in [("none", dict(compression="raw")), ("lzw", dict(compression="tiff_lzw")), ("packbits", dict(compression="packbits")),
                ("deflate", dict(compression="tiff_adobe_deflate")), ("g3", dict(compression="group3")),
                ("g3_2d", dict(compression="group3", tiffinfo={292: 1})), ("g3_fill", dict(compression="group3", tiffinfo={292: 4})),
                ("g3_2dfill", dict(compression="group3", tiffinfo={292: 5})), ("g3_2d_fillorder2", dict(compression="group3", tiffinfo={292: 1, 266: 2})),
                ("g4_fillorder2", dict(compression="group4", tiffinfo={266: 2})), ("g4", dict(compression="group4")), ("ccittrle", dict(compression="tiff_ccitt"))]:
    path = os.path.join(OUT, "bilevel_" + tag + ".tif")
    bim.save(path, **kw)
    back = np.asarray(Image.open(path).convert("L"))
    assert np.array_equal(back, bexp), tag
    open(os.path.join(OUT, "bilevel_" + tag + ".raw"), "wb").write(bexp.tobytes())
    manifest.append("bilevel_%s|%d|%d|Gray8" % (tag, W, H))

# every CCITT run length: one row per run length k (white k, then black), wide enough for the extended make-up codes
RW = 2700
runs = list(range(0, 70)) + list(range(64, 2688, 64)) + [1727, 1728, 1729, 1791, 1792, 1793, 2559, 2560, 2561, 2623, 2624, 2650]
rows = np.zeros((len(runs) * 2, RW), np.uint8)
for i, k in enumerate(runs):
    rows[2 * i, k:] = 255            # white run of k pixels first... then black (0) after: invert below
    rows[2 * i + 1, :k] = 255        # black first, white after
rows = 255 - rows                    # 255 = white pixels; row 2i: k black then white; row 2i+1: k white then black
rim = Image.fromarray(rows, "L").convert("1")
rexp = np.where(rows > 0, 255, 0).astype(np.uint8)
for tag, kw in [("g4", dict(compression="group4")), ("g3", dict(compression="group3")), ("ccittrle", dict(compression="tiff_ccitt")),
                ("g3_2d", dict(compression="group3", tiffinfo={292: 1})), ("g3_2dfill", dict(compression="group3", tiffinfo={292: 5}))]:
    path = os.path.join(OUT, "runs_" + tag + ".tif")
    rim.save(path, **kw)
    back = np.asarray(Image.open(path).convert("L"))
    assert np.array_equal(back, rexp), "runs " + tag
    open(os.path.join(OUT, "runs.raw"), "wb").write(rexp.tobytes())
    manifest.append("runs_%s|%d|%d|Gray8|runs" % (tag, RW, rows.shape[0]))

# premultiplied (associated) alpha, when Pillow can write it
try:
    ra = Image.fromarray(rgba, "RGBA").convert("RGBa")
    path = os.path.join(OUT, "rgba8_associated_lzw.tif")
    ra.save(path, compression="tiff_lzw")
    back = Image.open(path)
    if back.tag_v2.get(338) == (1,):
        open(os.path.join(OUT, "rgba8_associated_lzw.raw"), "wb").write(np.asarray(ra).tobytes())
        manifest.append("rgba8_associated_lzw|%d|%d|Rgba8Premultiplied" % (W, H))
    else:
        os.remove(path)
except Exception as e:  # pragma: no cover - depends on the Pillow build
    print("no associated alpha:", e)

# JPEG-in-TIFF files for when the JPEG codec is connected: expected pixels are libjpeg's output (compare within 1 level)
jdir = os.path.join(OUT, "jpeg")
os.makedirs(jdir, exist_ok=True)
jm = []
for nm, im in [("gray", Image.fromarray(gray, "L")), ("rgb", Image.fromarray(rgb, "RGB")), ("cmyk", Image.fromarray(cmyk, "CMYK"))]:
    p = os.path.join(jdir, "jpeg_" + nm + ".tif")
    im.save(p, compression="jpeg", quality=90)
    back = np.asarray(Image.open(p))
    open(os.path.join(jdir, "jpeg_" + nm + ".raw"), "wb").write(back.tobytes())
    jm.append("jpeg_%s|%d|%d|%s" % (nm, W, H, {"gray": "Gray8", "rgb": "Rgb8", "cmyk": "Cmyk8"}[nm]))
open(os.path.join(jdir, "manifest.txt"), "w").write("\n".join(jm) + "\n")

open(os.path.join(OUT, "manifest.txt"), "w").write("\n".join(manifest) + "\n")
print(len(manifest), "cases")
