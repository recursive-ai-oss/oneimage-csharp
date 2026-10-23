#!/usr/bin/env python3
"""Times PDFium (through pypdfium2) drawing page 1 of each named PDF at 150 dpi from the file's bytes: open, render to a bitmap, copy to a
NumPy array (the same work as our bytes-to-Rgba32 benchmark). Prints the mean and minimum over the runs.

Usage: bench_pdfium.py <runs> <file.pdf>...
"""
import sys, time
import numpy as np
import pypdfium2 as pdfium

runs = int(sys.argv[1])
for path in sys.argv[2:]:
    data = open(path, "rb").read()
    times = []
    for i in range(runs + 2):
        t = time.perf_counter()
        pdf = pdfium.PdfDocument(data)
        bmp = pdf[0].render(scale=150 / 72, may_draw_forms=False)
        arr = bmp.to_numpy()
        times.append(time.perf_counter() - t)
        pdf.close()
    times = times[2:]
    print(f"{path.split('/')[-1]}: {arr.shape[1]}x{arr.shape[0]} mean {1000 * np.mean(times):.1f} ms min {1000 * min(times):.1f} ms")
