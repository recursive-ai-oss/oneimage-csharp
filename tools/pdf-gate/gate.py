#!/usr/bin/env python3
"""The render gate: one verdict per page, and the tables that explain it.

Usage: gate.py <manifest.tsv> <renders-dir> <out.md> [classes.tsv] [verdicts.csv]

Inputs: manifest.tsv (name TAB source category), <renders-dir>/{ours,pdfium,mutool,pdfjs}/<name>.png and ours.tsv (the batch output of
PdfRender: name TAB status ...), and optionally classes.tsv from classify.py (name TAB content class).
SSIM is computed on luminance (transparent pixels over white) with scikit-image's defaults (7 x 7 uniform window). Renders whose size differs
by more than 2 pixels in either direction score as a miss; 1-2 pixels (page size rounding) are cropped away.

The signal. Let `refs` be the references (PDFium, MuPDF, pdf.js) that produced a usable picture of the page, `closest` our best SSIM to
any of them, and `spread` the best SSIM between any two of them. No reference is always right, so the SSIM does not pass or fail a page; it
flags the pages that need a human verdict. A page is flagged when
  * our SSIM is below 0.98 to at least two references AND closest < spread (we are further from every reference than the references are
    from each other), or
  * closest < 0.90 (the gross-error floor), or
  * we did not draw it, or no reference drew it (nothing to compare), or
  * only one reference drew it and our SSIM to it is below 0.98.
Every flagged page needs a line in verdicts.csv (page, verdict, ISO 32000-2 section, reason, deviating renderers); the report lists
flagged pages without one. The tables of strict 0.98 counts (including each renderer measured against the other three) are information.

Outputs: <out.md> (the report), <out>.tsv (one line per page) and <out>.pairs.tsv (cached SSIMs; delete it after re-rendering).
"""
import collections, itertools, os, sys
import numpy as np
from concurrent.futures import ProcessPoolExecutor
from PIL import Image
from skimage.metrics import structural_similarity

R = ["ours", "pdfium", "mutool", "pdfjs"]
REFS = R[1:]
PAIRS = list(itertools.combinations(R, 2))
BAR, FLOOR = 0.98, 0.90


def load_gray(path):
    im = Image.open(path)
    if im.mode in ("RGBA", "LA"):
        bg = Image.new("RGBA", im.size, (255, 255, 255, 255))
        im = Image.alpha_composite(bg, im.convert("RGBA"))
    return np.asarray(im.convert("L"), dtype=np.float64)


def ssim(a, b):
    if a is None or b is None or abs(a.shape[0] - b.shape[0]) > 2 or abs(a.shape[1] - b.shape[1]) > 2:
        return None
    h, w = min(a.shape[0], b.shape[0]), min(a.shape[1], b.shape[1])
    a, b = a[:h, :w], b[:h, :w]
    if h < 7 or w < 7:
        return 1.0 if np.array_equal(a, b) else 0.0
    return float(structural_similarity(a, b, data_range=255.0))


def page_scores(args):
    renders, name = args
    img = {}
    for r in R:
        try:
            img[r] = load_gray(os.path.join(renders, r, name + ".png"))
        except Exception:
            img[r] = None  # a failed render may leave nothing or a partial file: not a picture
    usable = {r: img[r] is not None for r in R}
    return usable, [ssim(img[a], img[b]) for a, b in PAIRS]


def main():
    manifest, renders, out = sys.argv[1:4]
    classes = {}
    if len(sys.argv) > 4:
        for l in open(sys.argv[4]):
            p = l.rstrip("\n").split("\t")
            classes[p[0]] = p[1]
    cats = [l.rstrip("\n").split("\t") for l in open(manifest) if l.strip()]
    cat_of = dict(cats)
    cache = os.path.splitext(out)[0] + ".pairs.tsv"
    data = {}
    if os.path.exists(cache):
        for l in open(cache):
            p = l.rstrip("\n").split("\t")
            data[p[0]] = (dict(zip(R, (x == "1" for x in p[1:5]))), [None if x == "" else float(x) for x in p[5:]])
    todo = [n for n, _ in cats if n not in data]
    if todo:
        with ProcessPoolExecutor(max_workers=4) as ex:
            for n, res in zip(todo, ex.map(page_scores, [(renders, n) for n in todo], chunksize=4)):
                data[n] = res
        with open(cache, "w") as f:
            for n, _ in cats:
                u, s = data[n]
                f.write("\t".join([n] + ["1" if u[r] else "0" for r in R] + ["" if x is None else f"{x:.5f}" for x in s]) + "\n")

    ours_status = {}
    p_ours = os.path.join(renders, "ours.tsv")
    if os.path.exists(p_ours):
        for l in open(p_ours):
            p = l.rstrip("\n").split("\t")
            if len(p) >= 2:
                ours_status[p[0]] = (p[1], p[5] if len(p) > 5 else "")

    def s(n, a, b):
        i = PAIRS.index((a, b) if (a, b) in PAIRS else (b, a))
        v = data[n][1][i]
        return v

    names = [n for n, _ in cats]
    rows = {}
    for n in names:
        usable, _ = data[n]
        refs = [r for r in REFS if usable[r]]
        status = ours_status.get(n, ("missing", ""))
        drawn = status[0] == "ok" and usable["ours"]
        ours = {r: (s(n, "ours", r) or 0.0) for r in refs}
        closest = max(ours.values()) if ours else 0.0
        pair_scores = [(s(n, a, b) or 0.0) for a, b in itertools.combinations(refs, 2)]
        spread = max(pair_scores) if pair_scores else None
        below = sum(1 for v in ours.values() if v < BAR)
        strict_ok = len(refs) >= 2 and below <= len(refs) - 2
        reason = None
        if not drawn:
            reason = "not drawn: " + (status[1] or status[0])
        elif not refs:
            reason = "no reference picture"
        elif closest < FLOOR:
            reason = "floor"
        elif len(refs) == 1:
            if closest < BAR:
                reason = "one reference, below 0.98"
        elif below >= 2 and closest < spread:
            reason = "below the references' spread"
        rows[n] = dict(flag=reason, closest=closest, spread=spread, refs=refs, drawn=drawn, strict=strict_ok)

    verdicts = {}
    if len(sys.argv) > 5 and os.path.exists(sys.argv[5]):
        import csv
        for r in csv.DictReader(open(sys.argv[5], newline="")):
            verdicts[r["page"]] = r

    def strict(n, who):
        # The 0.98 against two rule for one renderer, against the other three renderers (information only).
        others = [r for r in R if r != who]
        return sum(1 for o in others if (s(n, who, o) or 0) >= BAR) >= 2

    flagged = [n for n in names if rows[n]["flag"]]
    L = []
    L.append("# Render gate\n")
    L.append(f"SSIM against PDFium, MuPDF and pdf.js is a signal, not a verdict. A page is flagged when our SSIM is below {BAR} to at least two references and our SSIM to the closest one is below the best SSIM between two references; when the closest is below {FLOOR}; when we did not draw it or no reference did; or when a single reference drew it and we are below {BAR} to it. Each flagged page gets a verdict against ISO 32000-2 in verdicts.csv.\n")
    L.append(f"**{len(flagged)} of {len(names)} pages are flagged.**\n")
    cnt = collections.Counter(verdicts[n]["verdict"] if n in verdicts else "unreviewed" for n in flagged)
    L.append("Verdicts of the flagged pages: " + ", ".join(f"{k} {v}" for k, v in sorted(cnt.items())) + ".\n")
    L.append("## By source\n")
    L.append("| source | pages | flagged | 0.98 to two references |\n|---|---|---|---|")
    for c in sorted(set(cat_of.values())) + ["all"]:
        ns = names if c == "all" else [n for n in names if cat_of[n] == c]
        L.append(f"| {c} | {len(ns)} | {sum(1 for n in ns if rows[n]['flag'])} | {sum(1 for n in ns if rows[n]['strict'])} |")

    L.append("\n## Leave-one-out (information): each renderer under the strict rule (SSIM >= 0.98 to at least two of the other three), pages out of the total\n")
    L.append("| source | pages | " + " | ".join(R) + " |\n|---|---|" + "---|" * len(R))
    for c in sorted(set(cat_of.values())) + ["all"]:
        ns = names if c == "all" else [n for n in names if cat_of[n] == c]
        L.append(f"| {c} | {len(ns)} | " + " | ".join(str(sum(1 for n in ns if strict(n, r))) for r in R) + " |")

    if classes:
        L.append("\n## By content class (information): mean / min SSIM per pair and flagged pages\n")
        L.append("| class | pages | flagged | " + " | ".join(f"{a}-{b}" for a, b in PAIRS) + " |\n|---|---|---|" + "---|" * len(PAIRS))
        byc = collections.defaultdict(list)
        for n in names:
            byc[classes.get(n, "sparse")].append(n)
        for c in sorted(byc) + ["all"]:
            ns = names if c == "all" else byc[c]
            cells = []
            for i in range(len(PAIRS)):
                v = [data[n][1][i] for n in ns if data[n][1][i] is not None]
                cells.append(f"{np.mean(v):.3f} / {min(v):.3f}" if v else "-")
            L.append(f"| {c} | {len(ns)} | {sum(1 for n in ns if rows[n]['flag'])} | " + " | ".join(cells) + " |")

    f3 = lambda x: "-" if x is None else f"{x:.3f}"
    L.append(f"\n## Flagged pages ({len(flagged)})\n")
    L.append("| page | source | class | flag | closest | spread | ours-pdfium | ours-mutool | ours-pdfjs | verdict |\n|---|---|---|---|---|---|---|---|---|---|")
    for n in flagged:
        r = rows[n]
        v = verdicts[n]["verdict"] if n in verdicts else "unreviewed"
        L.append(f"| {n} | {cat_of[n]} | {classes.get(n, '')} | {r['flag']} | {r['closest']:.3f} | {f3(r['spread'])} | {f3(s(n, 'ours', 'pdfium'))} | {f3(s(n, 'ours', 'mutool'))} | {f3(s(n, 'ours', 'pdfjs'))} | {v} |")
    stale = [n for n in verdicts if n not in flagged]
    if stale:
        L.append(f"\nVerdicts for pages that are no longer flagged ({len(stale)}): " + ", ".join(stale) + ".\n")
    L.append(f"\n## Reference disagreement (information)\n\nPages where no two references agree at {BAR} ({sum(1 for n in names if rows[n]['spread'] is not None and rows[n]['spread'] < BAR)} pages): no renderer reaches the strict rule against the others there.\n")
    open(out, "w").write("\n".join(L) + "\n")
    with open(os.path.splitext(out)[0] + ".tsv", "w") as f:
        for n in names:
            r = rows[n]
            f.write("\t".join([n, cat_of[n], classes.get(n, ""), r["flag"] or "", f"{r['closest']:.4f}", "" if r["spread"] is None else f"{r['spread']:.4f}"]) + "\n")
    print("\n".join(L[:12]))
    print("\n".join(L[-(len(flagged) + 8):]) if False else "")


main()
