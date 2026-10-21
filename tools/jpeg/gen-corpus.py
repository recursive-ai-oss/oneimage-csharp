#!/usr/bin/env python3
"""Generates the JPEG decoder test corpus with libjpeg-turbo's command line tools and records what the reference
decoder produces for each file (full size and at 1/2, 1/4, 1/8).

usage: gen-corpus.py <libjpeg-turbo bin dir> <refdec> <mkcmyk> <out dir>

Every image is synthetic (generated here), so the files carry no third-party license. The .ref file next to each
.jpg holds the reference output of the four scales back to back: header-free raw samples (bytes, or 16-bit
little-endian words for 12-bit files). A one-line .txt records width, height, components and precision.
"""
import os, subprocess, sys
import numpy as np

bindir, refdec, mkcmyk, out = sys.argv[1:5]
os.makedirs(out, exist_ok=True)
tmp = os.path.join(out, "_tmp")
os.makedirs(tmp, exist_ok=True)


def photo(w, h, seed, maxval=255):
    """A deterministic image with gradients, hard edges, texture and noise."""
    rng = np.random.default_rng(seed)
    y, x = np.mgrid[0:h, 0:w].astype(np.float64)
    r = 0.5 + 0.5 * np.sin(x / 9.0 + y / 23.0)
    g = 0.5 + 0.5 * np.cos(y / 7.0 - x / 31.0)
    b = (x / max(w - 1, 1)) * 0.6 + (y / max(h - 1, 1)) * 0.4
    img = np.stack([r, g, b], axis=-1)
    img[(x > w * 0.3) & (x < w * 0.55) & (y > h * 0.2) & (y < h * 0.7)] = [0.95, 0.1, 0.1]
    img[((x - w * 0.7) ** 2 + (y - h * 0.5) ** 2) < (min(w, h) * 0.2) ** 2] = [0.1, 0.8, 0.2]
    img[:: max(h // 6, 2), :] = [0.0, 0.0, 0.0]
    img += rng.normal(0, 0.03, img.shape)
    return np.clip(np.rint(img * maxval), 0, maxval)


def write_pnm(path, arr, maxval=255):
    h, w = arr.shape[:2]
    kind = b"P6" if arr.ndim == 3 else b"P5"
    with open(path, "wb") as f:
        f.write(kind + b"\n%d %d\n%d\n" % (w, h, maxval))
        a = arr.astype(">u2" if maxval > 255 else np.uint8)
        f.write(a.tobytes())


def run(args, **kw):
    subprocess.run(args, check=True, **kw)


def reference(name, jpg):
    parts = []
    info = ""
    for d in (1, 2, 4, 8):
        raw = os.path.join(tmp, "ref.raw")
        res = subprocess.run([refdec, jpg, raw, str(d)], check=True, capture_output=True, text=True)
        if d == 1:
            info = res.stdout.strip()
        parts.append(open(raw, "rb").read())
    open(os.path.join(out, name + ".ref"), "wb").write(b"".join(parts))
    open(os.path.join(out, name + ".txt"), "w").write(info + "\n")


def cjpeg(name, src, *flags):
    jpg = os.path.join(out, name + ".jpg")
    run([os.path.join(bindir, "cjpeg"), *flags, "-outfile", jpg, src])
    reference(name, jpg)


W, H = 67, 45
rgb = os.path.join(tmp, "a.ppm")
write_pnm(rgb, photo(W, H, 1))
gray = os.path.join(tmp, "a.pgm")
write_pnm(gray, photo(W, H, 2)[:, :, 0])

for nm, s in [("444", "1x1"), ("422", "2x1"), ("420", "2x2"), ("440", "1x2"), ("411", "4x1")]:
    cjpeg("base-" + nm, rgb, "-sample", s + ",1x1,1x1", "-quality", "85")
cjpeg("base-420-q30", rgb, "-quality", "30")
cjpeg("base-420-q98", rgb, "-quality", "98", "-sample", "2x2")
cjpeg("base-420-optimized", rgb, "-optimize", "-sample", "2x2")
cjpeg("base-420-restart-rows", rgb, "-restart", "1", "-sample", "2x2")
cjpeg("base-444-restart-blocks", rgb, "-restart", "5B", "-sample", "1x1")
cjpeg("base-422-restart-blocks", rgb, "-restart", "3B", "-sample", "2x1")
cjpeg("base-odd-sampling", rgb, "-sample", "2x2,1x2,2x1")
cjpeg("gray", gray, "-grayscale")
cjpeg("gray-restart", gray, "-grayscale", "-restart", "4B")
cjpeg("rgb-colorspace", rgb, "-rgb")
cjpeg("prog-420", rgb, "-progressive", "-sample", "2x2")
cjpeg("prog-444", rgb, "-progressive", "-sample", "1x1")
cjpeg("prog-422-restart", rgb, "-progressive", "-sample", "2x1", "-restart", "2B")
cjpeg("prog-440", rgb, "-progressive", "-sample", "1x2")
cjpeg("prog-gray", gray, "-grayscale", "-progressive")
cjpeg("arith-420", rgb, "-arithmetic", "-sample", "2x2")
cjpeg("arith-444", rgb, "-arithmetic", "-sample", "1x1")
cjpeg("arith-gray", gray, "-grayscale", "-arithmetic")
cjpeg("arith-restart", rgb, "-arithmetic", "-restart", "3B", "-sample", "2x2")
cjpeg("arith-prog-420", rgb, "-arithmetic", "-progressive", "-sample", "2x2")
cjpeg("arith-prog-444", rgb, "-arithmetic", "-progressive", "-sample", "1x1")
cjpeg("arith-prog-gray", gray, "-grayscale", "-arithmetic", "-progressive")

for nm, (w, h) in [("tiny-1x1", (1, 1)), ("tiny-8x8", (8, 8)), ("tiny-9x17", (9, 17)), ("narrow-3x40", (3, 40)), ("wide-70x2", (70, 2))]:
    p = os.path.join(tmp, nm + ".ppm")
    write_pnm(p, photo(w, h, 3))
    cjpeg(nm + "-420", p, "-sample", "2x2")
    cjpeg(nm + "-444", p, "-sample", "1x1")

# 12-bit
rgb12 = os.path.join(tmp, "b.ppm")
write_pnm(rgb12, photo(W, H, 4, 4095), 4095)
gray12 = os.path.join(tmp, "b.pgm")
write_pnm(gray12, photo(W, H, 5, 4095)[:, :, 0], 4095)
cjpeg("p12-420", rgb12, "-precision", "12", "-sample", "2x2")
cjpeg("p12-444", rgb12, "-precision", "12", "-sample", "1x1")
cjpeg("p12-gray", gray12, "-precision", "12", "-grayscale")
cjpeg("p12-prog-420", rgb12, "-precision", "12", "-progressive", "-sample", "2x2")
cjpeg("p12-arith", rgb12, "-precision", "12", "-arithmetic", "-sample", "2x2")

# CMYK / YCCK as Adobe writes them (inverted ink is a convention of the data; the library stores what it is given)
rng = np.random.default_rng(9)
base = photo(W, H, 6)
cm = np.dstack([base, 255 - base[:, :, 1:2] * 0.5]).astype(np.uint8)
raw = os.path.join(tmp, "c.raw")
open(raw, "wb").write(cm.tobytes())
for kind in ("cmyk", "ycck"):
    jpg = os.path.join(out, kind + ".jpg")
    run([mkcmyk, raw, str(W), str(H), kind, "90", jpg])
    reference(kind, jpg)

# Encoder references: what cjpeg writes for the same pixels. The library's encoder is expected to match byte for byte.
write_pnm(os.path.join(out, "enc-src-a.ppm"), photo(67, 45, 1))
write_pnm(os.path.join(out, "enc-src-b.ppm"), photo(131, 77, 7))
write_pnm(os.path.join(out, "enc-src-gray.pgm"), photo(67, 45, 2)[:, :, 0])
cases = [
    # name, source, quality, sampling, optimize, progressive, restart (MCUs; 0 = none)
    ("q75-420", "a", 75, "2x2", 0, 0, 0),
    ("q90-444", "a", 90, "1x1", 0, 0, 0),
    ("q50-422", "a", 50, "2x1", 0, 0, 0),
    ("q20-440", "a", 20, "1x2", 0, 0, 0),
    ("q1-420", "a", 1, "2x2", 0, 0, 0),
    ("q100-444", "a", 100, "1x1", 0, 0, 0),
    ("q75-420-opt", "a", 75, "2x2", 1, 0, 0),
    ("q75-420-prog", "a", 75, "2x2", 0, 1, 0),
    ("q90-444-prog", "a", 90, "1x1", 0, 1, 0),
    ("q75-420-restart", "a", 75, "2x2", 0, 0, 4),
    ("q75-420-prog-restart", "a", 75, "2x2", 0, 1, 5),
    ("q85-420-b", "b", 85, "2x2", 0, 0, 0),
    ("q85-420-b-prog", "b", 85, "2x2", 0, 1, 0),
    ("q85-422-b-opt", "b", 85, "2x1", 1, 0, 0),
    ("gray-q75", "gray", 75, "gray", 0, 0, 0),
    ("gray-q75-prog", "gray", 75, "gray", 0, 1, 0),
    ("gray-q60-opt", "gray", 60, "gray", 1, 0, 0),
]
with open(os.path.join(out, "enc-cases.txt"), "w") as m:
    for name, src, q, samp, opt, prog, rst in cases:
        path = os.path.join(out, "enc-src-%s.%s" % (src, "pgm" if src == "gray" else "ppm"))
        args = ["-quality", str(q), "-dct", "int", "-baseline"]
        if samp == "gray":
            args += ["-grayscale"]
        else:
            args += ["-sample", samp + ",1x1,1x1"]
        if opt:
            args += ["-optimize"]
        if prog:
            args += ["-progressive"]
        if rst:
            args += ["-restart", "%dB" % rst]
        run([os.path.join(bindir, "cjpeg"), *args, "-outfile", os.path.join(out, "enc-ref-%s.jpg" % name), path])
        m.write("%s %s %d %s %d %d %d\n" % (name, os.path.basename(path), q, samp, opt, prog, rst))

for f in os.listdir(tmp):
    os.remove(os.path.join(tmp, f))
os.rmdir(tmp)
