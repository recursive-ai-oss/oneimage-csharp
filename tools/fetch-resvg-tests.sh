#!/usr/bin/env bash
# Downloads the public resvg test collection (SVG files, reference PNGs, resources and the fonts the text tests use)
# at a pinned commit, verifies its SHA-256 and unpacks it. The SVG conformance tests use the committed subset in
# tests/testdata/resvg; point ONEIMAGE_RESVG_TESTS at the printed folder to use the full collection with fonts.
#
#   tools/fetch-resvg-tests.sh <destination-folder>
set -euo pipefail

dest="${1:?usage: fetch-resvg-tests.sh <destination-folder>}"
commit=5192641d4fd67aef5c0c4d667d570e338da15361
sha256=10cffeb6d93d35b9569f75d4923bd95ecd7088527457791fd7a7cf6becba9c6f
url="https://github.com/linebender/resvg/archive/$commit.tar.gz"

mkdir -p "$dest"
if [[ ! -f "$dest/resvg.tgz" ]]; then
  curl -fsSL -o "$dest/resvg.tgz" "$url"
fi
echo "$sha256  $dest/resvg.tgz" | sha256sum -c - >/dev/null || { echo "checksum mismatch for $dest/resvg.tgz" >&2; exit 1; }
# The collection also carries the upstream Rust test sources; only the data files are used here.
tar -xzf "$dest/resvg.tgz" --exclude='*.rs' --strip-components=1 -C "$dest" "resvg-$commit/crates/resvg/tests"
echo "export ONEIMAGE_RESVG_TESTS=$dest/crates/resvg/tests"
