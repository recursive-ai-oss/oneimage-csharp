#!/usr/bin/env python3
"""Regenerates tests/testdata/compression: small valid and malformed zlib / raw DEFLATE streams.

Files named valid-* must decode (and match the platform inflater); files named bad-* must be
rejected with a format error. The extension says whether the stream is zlib (.zlib) or raw (.deflate).
"""
import os
import random
import struct
import zlib

out = os.path.join(os.path.dirname(__file__), "..", "tests", "testdata", "compression")
os.makedirs(out, exist_ok=True)
rnd = random.Random(7)


def raw(data, level):
    c = zlib.compressobj(level, zlib.DEFLATED, -15)
    return c.compress(data) + c.flush()


def put(name, data):
    with open(os.path.join(out, name), "wb") as f:
        f.write(data)


text = b"the quick brown fox jumps over the lazy dog. " * 200
noise = bytes(rnd.randrange(256) for _ in range(3000))
rows = b"".join(bytes([i % 5]) + bytes((x * 3 + i) & 255 for x in range(300)) for i in range(40))

for name, data in (("empty", b""), ("one", b"x"), ("text", text), ("noise", noise), ("rows", rows)):
    for level in (0, 1, 6, 9):
        put(f"valid-{name}-l{level}.zlib", zlib.compress(data, level))
        put(f"valid-{name}-l{level}.deflate", raw(data, level))

good = zlib.compress(rows, 6)
put("bad-truncated-half.zlib", good[: len(good) // 2])
put("bad-truncated-header.zlib", good[:1])
put("bad-no-adler.zlib", good[:-4])
flipped = bytearray(good)
flipped[-1] ^= 0x01
put("bad-adler.zlib", bytes(flipped))
put("bad-header-check.zlib", b"\x78\x00" + good[2:])
put("bad-method.zlib", b"\x79\x9c" + good[2:])
put("bad-blocktype3.deflate", b"\x07")
put("bad-stored-nlen.deflate", struct.pack("<BHH", 1, 5, 5) + b"abcde")
put("bad-stored-short.deflate", struct.pack("<BHH", 1, 9, 0xFFFF ^ 9) + b"abc")
