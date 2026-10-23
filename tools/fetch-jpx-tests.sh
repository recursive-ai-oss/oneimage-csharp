#!/usr/bin/env bash
# Fetches the JPEG 2000 conformance streams and reference images (ITU-T T.803 Class 0/1 sets) into a cache folder.
#
#   tools/fetch-jpx-tests.sh <out-dir>
#
# The files come from the openjpeg-data repository (input/conformance and baseline/conformance only), pinned to one commit.
# They are test data, never committed here. Run the conformance test with ONEIMAGE_JPX_CORPUS=<out-dir>.
set -euo pipefail

out="${1:?usage: fetch-jpx-tests.sh <out-dir>}"
url="https://github.com/uclouvain/openjpeg-data.git"
commit="39524bd3a601d90ed8e0177559400d23945f96a9"

if [[ ! -d "$out/.git" ]]; then
  mkdir -p "$out"
  git -C "$out" init -q
  git -C "$out" remote add origin "$url"
fi
git -C "$out" config core.sparseCheckout true
printf '/input/conformance/\n/baseline/conformance/\n' >"$out/.git/info/sparse-checkout"
git -C "$out" fetch -q --depth 1 --filter=blob:none origin "$commit"
git -C "$out" checkout -q --force "$commit"
test "$(git -C "$out" rev-parse HEAD)" = "$commit"
echo "JPEG 2000 conformance data at $commit in $out"
