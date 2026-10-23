#!/usr/bin/env bash
# Downloads a public collection of Photoshop files (PSD and PSB, produced by Photoshop and other editors, MIT-licensed test data of the
# psd-tools project: its files only) at a pinned commit, verifies the archive's SHA-256 and unpacks the files. The PSD tests use these when
# ONEIMAGE_PSD_TESTS points at the printed folder, and skip the corpus checks otherwise. Nothing from this collection is committed.
#
#   tools/fetch-psd-tests.sh <destination-folder>
set -euo pipefail

dest="${1:?usage: fetch-psd-tests.sh <destination-folder>}"
commit=5e101e98016c31f5aba61e53ee0dc36ec72c3dbf
sha256=d6cdf1951450f3f875573cf8fd104331491bbf6feb20ecbd6f57553b61aaca6e
url="https://github.com/psd-tools/psd-tools/archive/$commit.tar.gz"

mkdir -p "$dest"
if [[ ! -f "$dest/psdtools.tgz" ]]; then
  curl -fsSL -o "$dest/psdtools.tgz" "$url"
fi
echo "$sha256  $dest/psdtools.tgz" | sha256sum -c - >/dev/null || { echo "checksum mismatch for $dest/psdtools.tgz" >&2; exit 1; }
tar -xzf "$dest/psdtools.tgz" --strip-components=1 -C "$dest" --wildcards '*/tests/psd_files'
echo "export ONEIMAGE_PSD_TESTS=$dest/tests/psd_files"
