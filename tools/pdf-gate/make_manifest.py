#!/usr/bin/env python3
"""Lists the PDFs of OnePDF's test sets with their category: writes <outdir>/list.txt (path, unique name and password per line) and <outdir>/manifest.tsv (name, category).

Usage: make_manifest.py <onepdf-testdata-dir> <outdir>
Categories: generated-<producer> (chrome, lo, tex, raw, enc), realworld, govdocs, pdf20examples, verapdf.
"""
import glob, json, os, sys

root, out = sys.argv[1:3]
os.makedirs(out, exist_ok=True)
items = []
for p in sorted(glob.glob(root + "/generated/pdf/*.pdf")):
    n = os.path.basename(p)
    items.append((p, "generated-" + n.split("-")[0]))
for sub, cat in (("realworld/pdf", "realworld"), ("govdocs", "govdocs"), ("pdf20examples", "pdf20examples"), ("verapdf", "verapdf")):
    for p in sorted(glob.glob(f"{root}/{sub}/**/*.pdf", recursive=True)):
        items.append((p, cat))
seen, rows = {}, []
for p, cat in items:
    n = os.path.splitext(os.path.basename(p))[0]
    seen[n] = seen.get(n, 0) + 1
    rows.append((p, n if seen[n] == 1 else f"{n}-{seen[n]}", cat))
def password(p):
    t = os.path.join(os.path.dirname(os.path.dirname(p)), "truth", os.path.splitext(os.path.basename(p))[0] + ".json")
    if os.path.exists(t):
        return (json.load(open(t)).get("passwords") or {}).get("user") or ""
    return ""


open(out + "/list.txt", "w").write("".join(f"{p}\t{n}\t{password(p)}\n" for p, n, _ in rows))
open(out + "/manifest.tsv", "w").write("".join(f"{n}\t{c}\n" for _, n, c in rows))
print(len(rows), "files")
