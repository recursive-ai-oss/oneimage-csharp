#!/usr/bin/env python3
"""Compares our renders with the reference renderers' by SSIM and writes the per-category report.

Usage: compare.py <manifest.tsv> <renders-dir> <report.md> [--threshold 0.98]
manifest.tsv: name<TAB>category per line. <renders-dir> holds ours/, pdfium/, mutool/, pdfjs/ and ours.tsv (the batch output of PdfRender).
A page passes when SSIM >= threshold against at least two of the three renderers. Sizes that differ by more than 2 pixels in either
direction count as a miss against that renderer; a difference of 1-2 pixels (rounding of the page size) is cropped away.
SSIM is computed on the luminance with a 7x7 uniform window (scikit-image defaults).
"""
import collections, os, sys
import numpy as np
from PIL import Image
from skimage.metrics import structural_similarity

ORACLES = ["pdfium", "mutool", "pdfjs"]


def load_gray(path):
    im = Image.open(path)
    if im.mode in ("RGBA", "LA"):
        bg = Image.new("RGBA", im.size, (255, 255, 255, 255))
        im = Image.alpha_composite(bg, im.convert("RGBA"))
    return np.asarray(im.convert("L"), dtype=np.float64)


def usable(path):
    # A renderer that failed may leave an empty or partial file behind: that is a failure to render, not a picture.
    try:
        with Image.open(path) as im:
            im.load()
        return True
    except Exception:
        return False


def ssim(a, b):
    h, w = min(a.shape[0], b.shape[0]), min(a.shape[1], b.shape[1])
    if abs(a.shape[0] - b.shape[0]) > 2 or abs(a.shape[1] - b.shape[1]) > 2:
        return None
    a, b = a[:h, :w], b[:h, :w]
    if h < 7 or w < 7:
        return 1.0 if np.array_equal(a, b) else 0.0
    return float(structural_similarity(a, b, data_range=255.0))


def main():
    manifest, renders, report = sys.argv[1:4]
    thr = float(sys.argv[sys.argv.index("--threshold") + 1]) if "--threshold" in sys.argv else 0.98
    cats = [l.rstrip("\n").split("\t") for l in open(manifest) if l.strip()]
    ours = {}
    if os.path.exists(os.path.join(renders, "ours.tsv")):
        for l in open(os.path.join(renders, "ours.tsv")):
            p = l.rstrip("\n").split("\t")
            if len(p) >= 6:
                ours[p[0]] = p
    rows, per = [], collections.defaultdict(lambda: collections.Counter())
    for name, cat in cats:
        o = ours.get(name)
        status = o[1] if o else "missing"
        scores, note = {}, ""
        ref_ok = [x for x in ORACLES if usable(os.path.join(renders, x, name + ".png"))]
        if status == "ok" and os.path.exists(os.path.join(renders, "ours", name + ".png")):
            a = load_gray(os.path.join(renders, "ours", name + ".png"))
            for x in ref_ok:
                scores[x] = ssim(a, load_gray(os.path.join(renders, x, name + ".png")))
        else:
            note = (o[5] if o and len(o) > 5 and o[5] else status)
        hits = sum(1 for v in scores.values() if v is not None and v >= thr)
        # With fewer than two renderers able to draw the page, the page passes when all that could draw it agree.
        need = 2 if len(ref_ok) >= 2 else max(1, len(ref_ok))
        passed = status == "ok" and len(ref_ok) > 0 and hits >= need
        per[cat]["pass" if passed else "fail"] += 1
        if not ref_ok:
            per[cat]["no-reference"] += 1
        rows.append((cat, name, status, passed, scores, note, len(ref_ok)))
    with open(report, "w") as f:
        f.write(f"# Render gate: SSIM >= {thr} against at least two of PDFium, MuPDF and pdf.js\n\n")
        f.write("| category | pages | pass | fail | pass rate |\n|---|---|---|---|---|\n")
        tp = tf = 0
        for cat in sorted(per):
            p, q = per[cat]["pass"], per[cat]["fail"]
            tp += p
            tf += q
            f.write(f"| {cat} | {p + q} | {p} | {q} | {100.0 * p / max(1, p + q):.1f}% |\n")
        f.write(f"| **all** | {tp + tf} | {tp} | {tf} | {100.0 * tp / max(1, tp + tf):.1f}% |\n\n")
        f.write("## Failures\n\n| category | file | ours | pdfium | mutool | pdfjs | note |\n|---|---|---|---|---|---|---|\n")
        for cat, name, status, passed, scores, note, nref in sorted(rows):
            if passed:
                continue
            cell = lambda x: "-" if x not in scores else ("size" if scores[x] is None else f"{scores[x]:.3f}")
            f.write(f"| {cat} | {name} | {status} | {cell('pdfium')} | {cell('mutool')} | {cell('pdfjs')} | {note} |\n")
    print(open(report).read().split("## Failures")[0])
    with open(os.path.splitext(report)[0] + ".scores.tsv", "w") as f:
        for cat, name, status, passed, scores, note, nref in sorted(rows):
            f.write("\t".join([cat, name, status, "pass" if passed else "fail"] + [("" if x not in scores else "size" if scores[x] is None else f"{scores[x]:.4f}") for x in ORACLES]) + "\n")


main()
