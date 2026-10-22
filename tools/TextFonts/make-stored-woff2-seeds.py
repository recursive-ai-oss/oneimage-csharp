#!/usr/bin/env python3
"""Rewrites the test WOFF2 fonts with their table block stored as uncompressed Brotli meta-blocks.

Usage: make-stored-woff2-seeds.py <fontsdir> <outdir>

A fuzzer that flips bits in a Brotli stream mostly produces garbage tables; with the block stored, the same flips land in the
glyph, hmtx and collection data the WOFF2 reader has to validate. The output is meant as extra seeds for `fuzz/run.sh font` and is not committed.
"""
import glob
import os
import struct
import sys

import brotli

src, dst = sys.argv[1:3]
os.makedirs(dst, exist_ok=True)


def stored(data):
    bits = []

    def put(value, n):
        for i in range(n):
            bits.append((value >> i) & 1)

    put(0, 1)                      # window bits 16
    pos = 0
    while pos < len(data):
        chunk = data[pos:pos + 65536]
        pos += len(chunk)
        put(0, 1)                  # not the last meta-block
        put(0, 2)                  # four length nibbles
        put(len(chunk) - 1, 16)
        put(1, 1)                  # uncompressed
        while len(bits) % 8:
            bits.append(0)
        out = bytearray()
        for i in range(0, len(bits), 8):
            out.append(sum(b << k for k, b in enumerate(bits[i:i + 8])))
        out += chunk
        # Continue with a fresh bit list; the stored bytes are byte aligned.
        bits.clear()
        yield bytes(out)
    put(1, 1)                      # last
    put(1, 1)                      # empty
    while len(bits) % 8:
        bits.append(0)
    yield bytes(sum(b << k for k, b in enumerate(bits[i:i + 8])) for i in range(0, len(bits), 8))


for path in sorted(glob.glob(os.path.join(src, "*.woff2"))):
    d = open(path, "rb").read()
    comp = struct.unpack(">I", d[20:24])[0]
    # The block ends the file, possibly followed by up to three bytes of padding.
    for pad in range(4):
        start = len(d) - comp - pad
        try:
            block = brotli.decompress(d[start:start + comp])
            break
        except brotli.error:
            continue
    else:
        raise SystemExit(path + ": no table block found")
    chunks = list(stored(block))
    # The first chunk carries the window bits, which share its first byte with the first meta-block header: rebuild the whole stream at once.
    stream = b"".join(chunks)
    out = bytearray(d[:start]) + stream
    out += bytes((-len(out)) & 3)       # the reference decoder wants the file padded to four bytes
    out[8:12] = struct.pack(">I", len(out))   # file length, padding included
    out[20:24] = struct.pack(">I", len(stream))
    open(os.path.join(dst, "stored-" + os.path.basename(path)), "wb").write(bytes(out))
print("ok")
