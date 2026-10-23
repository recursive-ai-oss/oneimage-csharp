#!/usr/bin/env python3
"""Generates the larger JPEG 2000 files the decode benchmark uses (not committed): lossy and lossless, tiled and untiled.

Usage: python3 tools/gen-jpx-bench.py <output directory>      (needs numpy and glymur)
Run the benchmark with ONEIMAGE_JPX_BENCH=<output directory>. The same script times the independent decoder
(OpenJPEG through glymur) on the same files: python3 tools/gen-jpx-bench.py <dir> --time
"""
import os, sys, time, warnings
import numpy as np
import glymur

warnings.filterwarnings('ignore')
out = sys.argv[1]
os.makedirs(out, exist_ok=True)


def photo(w, h, c, bits, seed=7):
    rng = np.random.default_rng(seed)
    yy, xx = np.mgrid[0:h, 0:w].astype(np.float64)
    planes = []
    for k in range(c):
        base = 0.5 + 0.25 * np.sin(xx / (31.0 + 3 * k) + k) * np.cos(yy / (23.0 + 5 * k))
        base += 0.15 * np.sin((xx + yy) / (97.0 + k))
        base += 0.06 * ((xx.astype(int) // 40 + yy.astype(int) // 56 + k) % 2)
        base += 0.03 * rng.standard_normal((h, w))
        planes.append(base)
    a = np.stack(planes, axis=-1).clip(0, 1)
    if c == 1:
        a = a[:, :, 0]
    return (a * ((1 << bits) - 1)).round().astype(np.uint8 if bits <= 8 else np.uint16)


files = {
    'lossy-1tile.jp2': (photo(2048, 1536, 3, 8), dict(irreversible=True, cratios=[20], numres=6)),
    'lossy-tiles256.jp2': (photo(2048, 1536, 3, 8), dict(irreversible=True, cratios=[20], numres=5, tilesize=(256, 256))),
    'lossless-gray.j2k': (photo(3000, 2000, 1, 8), dict(numres=6)),
}
if '--time' not in sys.argv:
    for name, (img, kw) in files.items():
        glymur.Jp2k(os.path.join(out, name), data=img, **kw)
        print(name, os.path.getsize(os.path.join(out, name)))
else:
    names = list(files)
    extra = os.environ.get('ONEIMAGE_JPX_CORPUS')
    paths = [os.path.join(out, n) for n in names]
    if extra:
        paths.append(os.path.join(extra, 'input', 'conformance', 'p0_07.j2k'))
    for p in paths:
        j = glymur.Jp2k(p)
        j[:]
        best = 1e9
        for _ in range(5):
            t = time.perf_counter(); j[:]; best = min(best, time.perf_counter() - t)
        print(os.path.basename(p), f'{best * 1000:.1f} ms (best of 5, libopenjp2 via glymur, includes numpy copy)')
