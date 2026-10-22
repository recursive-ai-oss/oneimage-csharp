#!/usr/bin/env python3
"""Draws the README hero chart from the published gate reports.

Reads the "Gate summary" tables of bench/results/gate-final.md (AVX2 gate) and
overlays the rows that bench/results/gate-rerun.md measured again after
the final run. Writes docs/img/oneimage-hero-light.svg and -dark.svg.

  python3 tools/make-hero-chart.py            # write the two SVGs
  python3 tools/make-hero-chart.py --table    # print the results-at-a-glance table

Bars show ImageSharp time divided by OneImage time on a log scale: right of
the 1x line is faster, left is slower, grey is a tie (within 5%).
Standard library only.
"""
import math
import os
import re
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
RESULTS = os.path.join(ROOT, "bench", "results")


def summary(path):
    """task -> dict of the gate summary columns."""
    rows = {}
    on = False
    for line in open(path, encoding="utf-8"):
        if line.startswith("## Gate summary"):
            on = True
        elif line.startswith("## ") and on:
            break
        if on and line.startswith("|") and not line.startswith("|---") and not line.startswith("| Task"):
            c = [x.strip() for x in line.strip().strip("|").split("|")]
            rows[c[0]] = c
    return rows


def num(s):
    m = re.match(r"-?\d+(\.\d+)?", s)
    return float(m.group(0)) if m else None


def peak(s):
    m = re.match(r"(\d+(\.\d+)?) \(±(\d+(\.\d+)?)\)", s)
    return (float(m.group(1)), float(m.group(3))) if m else None


def merged(suffix=""):
    final = summary(os.path.join(RESULTS, f"gate-final{suffix}.md"))
    rerun = summary(os.path.join(RESULTS, f"gate-rerun{suffix}.md"))
    out = {}
    for task, c in final.items():
        c = rerun.get(task, c)
        ours, theirs = num(c[1]), num(c[2])
        out[task] = {
            "task": task, "ours": ours, "is": theirs,
            # >1: faster than ImageSharp. The report rounds its ratio column to two decimals,
            # so use the times, except for sub-millisecond rows where the times are the rounded ones.
            "ratio": theirs / ours if ours >= 1 else 1 / num(c[3]),
            "skips_icc": "skips ICC" in c[2],
            "native": c[4], "vs_native": num(c[5]),
            "peak": peak(c[6]), "peak_is": peak(c[7]), "peak_nv": num(c[8]),
            "peak_label": c[9], "peak_ratio_note": c[10],
            "rerun": task in rerun,
        }
    return out


def verdict(r):
    if r["skips_icc"]:
        return "loss"
    if abs(r["ours"] / r["is"] - 1) <= 0.05:
        return "tie"
    return "win" if r["ratio"] > 1 else "loss"


def svg(rows, dark):
    bg, fg, grid, sub = ("#0d1117", "#e6edf3", "#30363d", "#9da7b3") if dark else ("#ffffff", "#1f2328", "#d0d7de", "#59636e")
    col = {"win": "#2da44e" if not dark else "#3fb950", "tie": "#8c959f", "loss": "#d1242f" if not dark else "#f85149"}
    lo, hi = math.log2(0.5), math.log2(32)
    left, right, top, rh = 250, 30, 64, 20
    w = 760
    plot = w - left - right
    h = top + rh * len(rows) + 52

    def x(v):
        return left + (math.log2(v) - lo) / (hi - lo) * plot

    o = [f'<svg xmlns="http://www.w3.org/2000/svg" width="{w}" height="{h}" viewBox="0 0 {w} {h}" '
         f'font-family="-apple-system,Segoe UI,Helvetica,Arial,sans-serif" font-size="12">',
         f'<rect width="{w}" height="{h}" fill="{bg}"/>',
         f'<text x="16" y="24" font-size="15" font-weight="600" fill="{fg}">OneImage vs ImageSharp 3.1.12: how many times faster (log scale, higher is better)</text>',
         f'<text x="16" y="42" fill="{sub}">Bars left of 1x are slower than ImageSharp; grey is a tie (within 5%). Time only; peak memory is on the results page.</text>']
    for t in (0.5, 1, 2, 4, 8, 16, 32):
        gx = x(t)
        o.append(f'<line x1="{gx:.1f}" y1="{top - 6}" x2="{gx:.1f}" y2="{top + rh * len(rows)}" stroke="{fg if t == 1 else grid}" stroke-width="{1.5 if t == 1 else 1}"/>')
        label = f"{t:g}x"
        o.append(f'<text x="{gx:.1f}" y="{top + rh * len(rows) + 16}" text-anchor="middle" fill="{sub}">{label}</text>')
    x1 = x(1)
    for i, r in enumerate(rows):
        y = top + i * rh
        v = verdict(r)
        ratio = max(0.5, min(32, r["ratio"]))
        bx = x(ratio)
        a, b = min(x1, bx), max(x1, bx)
        if v == "tie":
            a, b = x1 - 2, x1 + 2
        o.append(f'<text x="{left - 8}" y="{y + 14}" text-anchor="end" fill="{fg}">{r["label"]}</text>')
        o.append(f'<rect x="{a:.1f}" y="{y + 3}" width="{max(b - a, 2):.1f}" height="{rh - 7}" fill="{col[v]}" rx="2"/>')
        txt = f'{r["ratio"]:.2f}x' if r["ratio"] < 1.5 else f'{r["ratio"]:.1f}x'
        if r["skips_icc"]:
            txt += " (ImageSharp skips ICC)"
        if v == "tie":
            txt += " tie"
        tx, anchor = (bx + 6, "start") if r["ratio"] >= 1 and v != "tie" else (x1 + 8, "start")
        o.append(f'<text x="{tx:.1f}" y="{y + 14}" text-anchor="{anchor}" fill="{sub}">{txt}</text>')
    ly = h - 14
    lx = left
    for name, k in (("faster", "win"), ("tie", "tie"), ("slower", "loss")):
        o.append(f'<rect x="{lx}" y="{ly - 9}" width="10" height="10" fill="{col[k]}" rx="2"/><text x="{lx + 15}" y="{ly}" fill="{sub}">{name}</text>')
        lx += 80
    o.append("</svg>")
    return "\n".join(o) + "\n"


def label(task):
    m = re.match(r"(.*) \((?:Content|Case|Lossless)=(.*)\)", task)
    if m:
        return f"{m.group(1)} ({m.group(2).replace('True', 'lossless').replace('False', 'lossy')})"
    return task


def fmt_peak(p):
    return "-" if p is None else f"{p[0]:.1f} (±{p[1]:.1f})"


def table(rows):
    print("| Task | OneImage ms | ImageSharp ms | Time vs ImageSharp | Fastest native wrapper | OneImage time ÷ fastest native | Peak MiB: OneImage | Peak: ImageSharp | Peak: NetVips | Peak vs ImageSharp |")
    print("|---|--:|--:|---|---|--:|--:|--:|--:|---|")
    for r in rows:
        v = verdict(r)
        if r["skips_icc"]:
            t = f'{1 / r["ratio"]:.2f}× slower (ImageSharp skips the ICC conversion)'
        elif v == "tie":
            t = "tie"
        elif v == "win":
            t = f'{r["ratio"]:.2f}× faster'
        else:
            t = f'{1 / r["ratio"]:.2f}× slower'
        lab = r["peak_label"]
        if r["peak"] is None:
            pv = "-"
        elif r["task"] == "jpeg-encode":
            pv = "tie (within the run-to-run spread)"
        elif r["task"] == "thumb-jpeg":
            pv = "**higher** (exception)"
        elif "tie" in lab:
            pv = "tie"
        elif "win" in lab:
            pv = "lower"
        else:
            pv = "higher" + (" (ImageSharp skips the ICC conversion)" if r["skips_icc"] else "")
        nat = r["native"]
        nv = "-" if r["peak_nv"] is None else f'{r["peak_nv"]:.1f}'
        print(f'| {r["label"]} | {r["ours"]} | {r["is"]} | {t} | {nat} | {r["vs_native"]:.2f}× | {fmt_peak(r["peak"])} | {fmt_peak(r["peak_is"])} | {nv} | {pv} |')


def table_avx512():
    a2 = merged()
    print("| Task | AVX2 gate: OneImage ms | AVX-512: OneImage ms | AVX-512: ImageSharp ms | AVX-512 time vs ImageSharp |")
    print("|---|--:|--:|--:|---|")
    for t, r in merged("-avx512").items():
        r["label"] = label(t)
        v = verdict(r)
        s = "tie" if v == "tie" else (f'{r["ratio"]:.2f}× faster' if v == "win" else f'{1 / r["ratio"]:.2f}× slower' + (" (ImageSharp skips the ICC conversion)" if r["skips_icc"] else ""))
        print(f'| {r["label"]} | {a2[t]["ours"]} | {r["ours"]} | {r["is"]} | {s} |')


def main():
    if "--table-avx512" in sys.argv:
        table_avx512()
        return
    data = merged()
    rows = []
    for t, r in data.items():
        r["label"] = label(t)
        rows.append(r)
    if "--table" in sys.argv:
        table(rows)
        return
    img = os.path.join(ROOT, "docs", "img")
    os.makedirs(img, exist_ok=True)
    for dark in (False, True):
        with open(os.path.join(img, f"oneimage-hero-{'dark' if dark else 'light'}.svg"), "w", encoding="utf-8", newline="\n") as f:
            f.write(svg(rows, dark))


if __name__ == "__main__":
    main()
