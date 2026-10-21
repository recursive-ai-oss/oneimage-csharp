#!/usr/bin/env python3
"""Reads every <name>.tif in a directory with the libtiff bundled in Pillow and compares its scanlines to <name>.raw.

The tests write such a directory when ONEIMAGE_TIFF_DUMP is set. libtiff returns decoded samples in host byte order
(the predictor undone, byte order swapped), which is the layout of the .raw files on a little-endian machine, so this is an
independent check of the encoder for 16-bit, float and CMYK output.

Usage: python3 tools/verify-tiff-with-libtiff.py <dir>
"""
import ctypes
import glob
import os
import sys

import PIL

libs = os.path.abspath(os.path.join(os.path.dirname(PIL.__file__), "..", "pillow.libs"))
if libs not in os.environ.get("LD_LIBRARY_PATH", ""):
    # libtiff's own dependencies sit next to it; the loader only finds them when told.
    os.environ["LD_LIBRARY_PATH"] = libs + ":" + os.environ.get("LD_LIBRARY_PATH", "")
    os.execv(sys.executable, [sys.executable] + sys.argv)
lib_path = glob.glob(os.path.join(libs, "libtiff*.so*"))[0]
lib = ctypes.CDLL(lib_path)
lib.TIFFOpen.restype = ctypes.c_void_p
lib.TIFFOpen.argtypes = [ctypes.c_char_p, ctypes.c_char_p]
lib.TIFFClose.argtypes = [ctypes.c_void_p]
lib.TIFFReadScanline.argtypes = [ctypes.c_void_p, ctypes.c_void_p, ctypes.c_uint32, ctypes.c_uint16]
lib.TIFFScanlineSize.argtypes = [ctypes.c_void_p]
lib.TIFFScanlineSize.restype = ctypes.c_ssize_t
lib.TIFFGetField.argtypes = [ctypes.c_void_p, ctypes.c_uint32, ctypes.c_void_p]
lib.TIFFSetWarningHandler.argtypes = [ctypes.c_void_p]
lib.TIFFSetWarningHandler(None)

d = sys.argv[1]
bad = 0
n = 0
for path in sorted(glob.glob(os.path.join(d, "*.tif"))):
    want = open(path[:-4] + ".raw", "rb").read()
    t = lib.TIFFOpen(path.encode(), b"r")
    if not t:
        print("cannot open", path)
        bad += 1
        continue
    h = ctypes.c_uint32()
    lib.TIFFGetField(t, 257, ctypes.byref(h))
    size = lib.TIFFScanlineSize(t)
    buf = ctypes.create_string_buffer(size)
    got = bytearray()
    ok = True
    for y in range(h.value):
        if lib.TIFFReadScanline(t, buf, y, 0) != 1:
            ok = False
            break
        got += buf.raw
    lib.TIFFClose(t)
    n += 1
    if not ok or bytes(got) != want:
        print("MISMATCH", os.path.basename(path), ok, len(got), len(want))
        bad += 1
print(n, "files checked,", bad, "bad")
sys.exit(1 if bad else 0)
