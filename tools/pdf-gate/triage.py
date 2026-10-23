#!/usr/bin/env python3
"""Measures how far the reference renderers agree with each other, so the gate's bar can be judged against what a renderer can reach.

Usage: triage.py <manifest.tsv> <renders-dir> <out.md> [classes.tsv]
For every page it computes SSIM for each pair of renderers (ours, pdfium, mutool, pdfjs) and writes:
  * per category: the mean SSIM of every pair and how many pages reach 0.98 in each pair;
  * the gate's rule (>= 0.98 against at least two of the other three renderers) applied to every renderer, so the bar is shown
    next to what each of the references itself reaches;
  * failing pages of ours that no renderer pair could pass (no two references agree at 0.98), which no renderer can pass even by
    copying one of the references.
The pair scores are cached next to <out.md> as <out>.pairs.tsv and reused when present (delete it after re-rendering).
With classes.tsv (classify.py) the by-content tables are written too: mean and min SSIM of every pair and the gate pass count per class.
"""
import collections, itertools, os, sys
import numpy as np
from concurrent.futures import ProcessPoolExecutor
from PIL import Image
from skimage.metrics import structural_similarity

R = ["ours", "pdfium", "mutool", "pdfjs"]
PAIRS = list(itertools.combinations(R, 2))
THR = 0.98


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


def page(args):
    renders, name = args
    img = {}
    for r in R:
        p = os.path.join(renders, r, name + ".png")
        try:
            img[r] = load_gray(p)
        except Exception:
            img[r] = None
    return [ssim(img[a], img[b]) for a, b in PAIRS]


def main():
    manifest, renders, out = sys.argv[1:4]
    cache = os.path.splitext(out)[0] + ".pairs.tsv"
    classes = {}
    if len(sys.argv) > 4:
        for l in open(sys.argv[4]):
            p = l.split("\t")
            classes[p[0]] = p[1]
    cats = [l.rstrip("\n").split("\t") for l in open(manifest) if l.strip()]
    data = {}
    if os.path.exists(cache):
        for l in open(cache):
            p = l.rstrip("\n").split("\t")
            data[p[0]] = [None if x == "" else float(x) for x in p[2:]]
    todo = [n for n, _ in cats if n not in data]
    if todo:
        with ProcessPoolExecutor(max_workers=4) as ex:
            for n, res in zip(todo, ex.map(page, [(renders, n) for n in todo], chunksize=4)):
                data[n] = res
        with open(cache, "w") as f:
            for n, c in cats:
                f.write("\t".join([n, c] + ["" if x is None else f"{x:.5f}" for x in data[n]]) + "\n")

    def s(n, a, b):
        i = PAIRS.index((a, b) if (a, b) in PAIRS else (b, a))
        return data[n][i]

    bycat = collections.defaultdict(list)
    for n, c in cats:
        bycat[c].append(n)
    lines = ["# Renderer agreement\n"]
    lines.append("## Mean SSIM per renderer pair (pages reaching 0.98 in brackets)\n")
    lines.append("| category | pages | " + " | ".join(f"{a}-{b}" for a, b in PAIRS) + " |")
    lines.append("|---|---|" + "---|" * len(PAIRS))
    allp = [n for n, _ in cats]
    for c in sorted(bycat) + ["all"]:
        names = allp if c == "all" else bycat[c]
        cells = []
        for i in range(len(PAIRS)):
            v = [data[n][i] for n in names if data[n][i] is not None]
            hit = sum(1 for x in v if x >= THR)
            cells.append(f"{np.mean(v):.3f} ({hit})" if v else "-")
        lines.append(f"| {c} | {len(names)} | " + " | ".join(cells) + " |")

    lines.append("\n## The gate rule applied to each renderer in turn (>= 0.98 against at least two of the other three)\n")
    lines.append("| category | pages | " + " | ".join(R) + " |")
    lines.append("|---|---|" + "---|" * len(R))

    def passes(n, who):
        others = [r for r in R if r != who]
        hits = sum(1 for o in others if (s(n, who, o) or 0) >= THR)
        return hits >= 2

    for c in sorted(bycat) + ["all"]:
        names = allp if c == "all" else bycat[c]
        lines.append(f"| {c} | {len(names)} | " + " | ".join(str(sum(1 for n in names if passes(n, r))) for r in R) + " |")


    if classes:
        lines.append("\n## By content class: mean / min SSIM per pair and pages passing the gate rule\n")
        lines.append("| class | pages | pass | " + " | ".join(f"{a}-{b}" for a, b in PAIRS) + " |")
        lines.append("|---|---|---|" + "---|" * len(PAIRS))
        byc = collections.defaultdict(list)
        for n, _ in cats:
            byc[classes.get(n, "sparse")].append(n)
        for c in sorted(byc) + ["all"]:
            names = allp if c == "all" else byc[c]
            cells = []
            for i in range(len(PAIRS)):
                v = [data[n][i] for n in names if data[n][i] is not None]
                cells.append(f"{np.mean(v):.3f} / {min(v):.3f}" if v else "-")
            npass = sum(1 for n in names if passes(n, "ours"))
            lines.append(f"| {c} | {len(names)} | {npass} | " + " | ".join(cells) + " |")

    # The gate's own rule judges ours against the three references. A page cannot be passed by any renderer when fewer than two
    # references reach 0.98 with a common third: a copy of pdfium would score 1.0 against pdfium but needs one more reference at 0.98.
    lines.append("\n## Pages ours fails\n")
    fails = [n for n in allp if not passes(n, "ours")]
    refs = [r for r in R if r != "ours"]
    unreach, near, far = [], [], []
    for n in fails:
        best_copy = max(sum(1 for o in refs if o != r and (s(n, r, o) or 0) >= THR) for r in refs)
        best_ours = max((s(n, "ours", o) or 0) for o in refs)
        if best_copy < 1:
            unreach.append(n)
        elif best_ours >= 0.95:
            near.append(n)
        else:
            far.append(n)
    lines.append(f"- failing pages: {len(fails)}")
    lines.append(f"- no reference reaches 0.98 with any other reference (a copy of one reference would fail too): {len(unreach)}")
    lines.append(f"- references agree (so the bar is reachable) and ours is within 0.95..0.98 of its best reference: {len(near)}")
    lines.append(f"- references agree and ours is below 0.95 against all: {len(far)}")
    cat_of = dict(cats)
    lines.append("\n| page | category | class | ours-pdfium | ours-mutool | ours-pdfjs | pdfium-mutool | pdfium-pdfjs | mutool-pdfjs |")
    lines.append("|---|---|---|---|---|---|---|---|---|")
    f3 = lambda x: "-" if x is None else f"{x:.3f}"
    for cls, group in (("unreachable", unreach), ("near", near), ("far", far)):
        for n in group:
            lines.append(f"| {n} | {cat_of[n]} | {cls} | " + " | ".join(f3(s(n, a, b)) for a, b in [("ours", "pdfium"), ("ours", "mutool"), ("ours", "pdfjs"), ("pdfium", "mutool"), ("pdfium", "pdfjs"), ("mutool", "pdfjs")]) + " |")
    open(out, "w").write("\n".join(lines) + "\n")
    print("\n".join(lines[:40]))


main()
