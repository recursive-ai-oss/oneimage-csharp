#!/usr/bin/env python3
"""Generates the small JPEG 2000 files under testdata/jpx/gen and their expected samples.

The files are produced by an independent encoder (OpenJPEG through the glymur wrapper, used as a black box) from synthetic
images made here, so the repository carries no third-party image data. manifest.json records for each file the geometry and the
SHA-256 of the samples the independent decoder produces: little-endian 16-bit, interleaved, row-major. Lossy files also
get a .exp file holding those samples, because other decoders are only expected to agree with them within a tolerance.

Usage: python3 tools/gen-jpx-corpus.py <output directory>      (needs numpy and glymur)
"""
import hashlib, json, os, sys, warnings
import numpy as np
import glymur

warnings.filterwarnings('ignore')
out = sys.argv[1]
os.makedirs(out, exist_ok=True)


def image(w, h, c, bits, seed):
    rng = np.random.default_rng(seed)
    yy, xx = np.mgrid[0:h, 0:w]
    planes = []
    for k in range(c):
        base = 0.5 + 0.3 * np.sin(xx / (4.0 + k) + k) * np.cos(yy / (5.0 + 2 * k))
        base += 0.15 * ((xx // 16 + yy // 16 + k) % 2)  # blocky edges
        base += 0.04 * rng.standard_normal((h, w))
        planes.append(base)
    a = np.stack(planes, axis=-1).clip(0, 1)
    if c == 1:
        a = a[:, :, 0]
    return (a * ((1 << bits) - 1)).round().astype(np.uint8 if bits <= 8 else np.uint16)


cases = []


def add(name, w, h, c, bits, lossless=True, **kw):
    cases.append((name, w, h, c, bits, lossless, kw))


add('rgb-default', 97, 61, 3, 8)
add('gray-1res', 64, 48, 1, 8, numres=1)
add('gray-3res', 64, 48, 1, 8, numres=3)
for p in ['LRCP', 'RLCP', 'RPCL', 'PCRL', 'CPRL']:
    add('prog-' + p.lower(), 97, 61, 3, 8, prog=p, numres=4, psizes=[(32, 32)] * 3 + [(64, 64)], cbsize=(16, 16), cratios=[40, 12, 1])
add('tiles-pcrl', 97, 61, 3, 8, prog='PCRL', tilesize=(40, 30), numres=3)
add('tiles-rpcl-prec', 97, 61, 3, 8, prog='RPCL', tilesize=(48, 48), psizes=[(16, 16)] * 3, cbsize=(8, 8), numres=3)
add('tiles-cprl', 97, 61, 3, 8, prog='CPRL', tilesize=(33, 29), numres=3, cratios=[20, 1])
add('cblk-16x16', 97, 61, 1, 8, cbsize=(16, 16))
add('cblk-64x8', 97, 61, 1, 8, cbsize=(64, 8))
add('cblk-4x4', 40, 36, 1, 8, cbsize=(4, 4), numres=3)
add('cblk-1024x4', 130, 20, 1, 8, cbsize=(1024, 4), numres=3)
for name, ms in [('bypass', 1), ('reset', 2), ('termall', 4), ('causal', 8), ('pterm', 16), ('segsym', 32),
                 ('bypass-termall', 5), ('bypass-causal-reset', 11), ('all-modes', 63)]:
    add('mode-' + name, 97, 61, 3, 8, modesw=ms, cbsize=(32, 32), cratios=[8, 1])
add('sop-eph', 97, 61, 3, 8, sop=True, eph=True, cratios=[6, 1])
add('plt-tlm', 97, 61, 3, 8, plt=True, tlm=True, tilesize=(50, 40))
add('gray16', 75, 50, 1, 16)
add('rgb16', 61, 40, 3, 16, cratios=[1])
add('offset', 70, 50, 3, 8, grid_offset=(3, 5), tilesize=(32, 32))
add('odd-1x1', 1, 1, 1, 8, numres=1)
add('lossy-ict', 97, 61, 3, 8, False, irreversible=True, cratios=[10], mct=True)
add('lossy-gray', 97, 61, 1, 8, False, irreversible=True, cratios=[8, 4])
add('lossy-layers-tiles', 97, 61, 3, 8, False, irreversible=True, cratios=[40, 15, 5], tilesize=(48, 40), prog='RLCP')
add('lossy-16bit', 61, 40, 1, 16, False, irreversible=True, cratios=[6])
add('lossy-modes', 97, 61, 3, 8, False, irreversible=True, cratios=[10], modesw=1 | 4 | 32)

manifest = []
for name, w, h, c, bits, lossless, kw in cases:
    path = os.path.join(out, name + '.j2k')
    img = image(w, h, c, bits, sum(map(ord, name)))
    kw = dict(kw)
    try:
        glymur.Jp2k(path, data=img, **kw)
    except Exception as e:
        print('skip', name, str(e)[:100])
        continue
    dec = glymur.Jp2k(path)[:]
    if dec.ndim == 2:
        dec = dec[:, :, None]
    raw = dec.astype('<u2').tobytes()
    manifest.append({'file': name + '.j2k', 'width': int(dec.shape[1]), 'height': int(dec.shape[0]), 'channels': int(dec.shape[2]),
                     'bits': bits, 'lossless': lossless, 'sha256': hashlib.sha256(raw).hexdigest(),
                     'bytes': os.path.getsize(path)})
    if not lossless:
        with open(os.path.join(out, name + '.exp'), 'wb') as f:
            f.write(raw)
    if lossless:
        assert np.array_equal(dec.reshape(img.shape), img), name
    np.save(os.path.join(os.environ.get('TMPDIR', '/tmp'), name + '.npy'), dec) if os.environ.get('JPX_KEEP') else None

with open(os.path.join(out, 'manifest.json'), 'w') as f:
    json.dump(manifest, f, indent=1)
print(len(manifest), 'files', sum(m['bytes'] for m in manifest), 'bytes')
