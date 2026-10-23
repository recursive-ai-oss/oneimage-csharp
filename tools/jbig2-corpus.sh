#!/usr/bin/env bash
# Builds a JBIG2 conformance corpus from PDF files you already have.
#
#   tools/jbig2-corpus.sh <out-dir> <pdf>...
#
# For every image with /Filter /JBIG2Decode in the given PDFs it writes
#   NAME-NNN.jb2e      the embedded JBIG2 stream
#   NAME-NNN.jb2g      its /JBIG2Globals stream, when there is one
#   NAME-ref-NNN.pbm   the same image as decoded by poppler's `pdfimages` (a black box reference)
# where NAME is the PDF's file name without extension and NNN the image index. The reference applies
# /Decode [1 0] and /ImageMask, so it is the inverse of our decoder's output (1 = black) for those images; the
# corpus test accepts either polarity. Needs poppler-utils (pdfimages). Nothing is downloaded and the output is
# never committed. Run the test with ONEIMAGE_JBIG2_CORPUS=<out-dir>.
set -euo pipefail

out="${1:?usage: jbig2-corpus.sh <out-dir> <pdf>...}"
shift
mkdir -p "$out"
work="$(mktemp -d "$out/.work.XXXXXX")"
trap 'rm -rf "$work"' EXIT

for pdf in "$@"; do
  name="$(basename "${pdf%.pdf}")"
  rm -rf "${work:?}"/*
  # -all keeps JBIG2 streams as .jb2e (and their globals as .jb2g); the plain run gives the reference bitmaps.
  pdfimages -all "$pdf" "$work/s" 2>/dev/null || continue
  ls "$work"/s-*.jb2e >/dev/null 2>&1 || continue
  pdfimages "$pdf" "$work/r" 2>/dev/null || continue
  for e in "$work"/s-*.jb2e; do
    idx="${e##*/s-}"; idx="${idx%.jb2e}"
    # pdfimages writes .pbm for 1-bit images; skip a stream whose reference is missing.
    ref="$work/r-$idx.pbm"
    [ -f "$ref" ] || continue
    cp "$e" "$out/$name-$idx.jb2e"
    [ -f "$work/s-$idx.jb2g" ] && cp "$work/s-$idx.jb2g" "$out/$name-$idx.jb2g"
    cp "$ref" "$out/$name-ref-$idx.pbm"
  done
done
