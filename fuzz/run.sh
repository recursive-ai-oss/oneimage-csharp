#!/usr/bin/env bash
# Build, instrument and run one fuzz target under libFuzzer.
#
#   fuzz/run.sh <target> <seconds> [extra libFuzzer flags]
#   fuzz/run.sh replay <target> <file>...        # run saved inputs once, without libFuzzer (reproduce a crash)
#
# Targets: jpeg inflate lzw icc exif tiff png bmp ico gif webp svg font breaking bidi brotli richtext sysfont svg-xml svg-render   (see fuzz/README.md)
#
# Environment (nothing is baked into the script):
#   DOTNET             the dotnet executable (default: dotnet from PATH)
#   ONEIMAGE_FUZZ_TOOLS  directory holding `libfuzzer-dotnet` (the libFuzzer host) and `sharpfuzz/sharpfuzz`
#                      (the instrumenter); see fuzz/README.md for building them.
#                      LIBFUZZER_DOTNET and SHARPFUZZ override the two paths individually.
#   ONEIMAGE_FUZZ_WORK   where builds, corpora and findings go (default: ./fuzz-work; keep it outside the checkout
#                      or it will show up as untracked files)
#   ONEIMAGE_ARTIFACTS   build output root, as for every other project in the repo
#   FUZZ_TIMEOUT       per-input timeout in seconds (default 10)
#   FUZZ_RSS_MB        memory limit in MB (default 2048)
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
dotnet_bin="${DOTNET:-dotnet}"
work="${ONEIMAGE_FUZZ_WORK:-$PWD/fuzz-work}"
tools="${ONEIMAGE_FUZZ_TOOLS:-}"
host="${LIBFUZZER_DOTNET:-$tools/libfuzzer-dotnet}"
instrument="${SHARPFUZZ:-$tools/sharpfuzz/sharpfuzz}"

mode=fuzz
if [[ "${1:-}" == replay ]]; then
  mode=replay
  shift
fi
target="${1:?usage: run.sh <target> <seconds> | run.sh replay <target> <file>...}"
shift

# seed folder under tests/testdata, a one-byte prefix the target reads as its mode byte (empty for none), file patterns, max input length
case "$target" in
  jpeg)    seeds=jpeg;        prefix='\x00'; patterns='*.jpg';  maxlen=262144 ;;
  inflate) seeds=compression; prefix='\x01'; patterns='valid-*.zlib valid-*.deflate bad-*'; maxlen=65536 ;;
  lzw)     seeds='';          prefix='';     patterns='';      maxlen=65536 ;;
  icc)     seeds=icc;         prefix='';     patterns='*.icc'; maxlen=262144 ;;
  exif)    seeds='';          prefix='';     patterns='';      maxlen=65536 ;;
  png)     seeds=png/pngsuite; prefix='\x00'; patterns='*.png'; maxlen=65536 ;;
  bmp)     seeds=bmp;         prefix='\x00'; patterns='*.bmp'; maxlen=65536 ;;
  ico)     seeds=ico;         prefix='\x00'; patterns='*.ico'; maxlen=65536 ;;
  gif)     seeds=gif;         prefix='\x00'; patterns='*.gif'; maxlen=65536 ;;
  webp)    seeds='';          prefix='\x00'; patterns='';      maxlen=65536 ;;
  svg)     seeds=svgpath;     prefix='';     patterns='*.txt'; maxlen=4096 ;;
  tiff)    seeds=tiff;        prefix='\x00'; patterns='*.tif'; maxlen=262144 ;;
  font)    seeds=fonts;       prefix='';     patterns='*.ttf *.otf *.ttc *.woff *.woff2'; maxlen=262144 ;;
  breaking) seeds='';         prefix='';     patterns='';      maxlen=2050 ;;
  bidi)    seeds='';         prefix='';     patterns='';      maxlen=2050 ;;
  brotli)  seeds=brotli;      prefix='';     patterns='*.compressed'; maxlen=262144 ;;
  richtext) seeds='';         prefix='';     patterns='';      maxlen=2200 ;;
  svg-xml) seeds='';          prefix='';     patterns='';      maxlen=16384 ;;
  svg-render) seeds='';       prefix='';     patterns='';      maxlen=16384 ;;
  sysfont) seeds=fonts;       prefix='';     patterns='*.ttf *.otf *.ttc'; maxlen=65536 ;;
  *) echo "unknown target '$target'" >&2; exit 2 ;;
esac

bin="$work/bin/$target"
mkdir -p "$bin"
# The layout part of the breaking target needs a font with kana and ideographs.
export ONEIMAGE_FUZZ_FONT="$root/tests/testdata/fonts/NotoSansCJK-Subset.otf"
export ONEIMAGE_FUZZ_BIDI_FONT="$root/tests/testdata/fonts/NotoSans-Bidi-Subset.ttf"

# Build once per run: release, then instrument only the library assembly (the harness itself needs no coverage).
"$dotnet_bin" build "$root/fuzz/OneImage.Fuzz/OneImage.Fuzz.csproj" -c Release -v quiet -clp:NoSummary -o "$bin"

if [[ "$mode" == replay ]]; then
  ONEIMAGE_FUZZ_TARGET="$target" "$dotnet_bin" "$bin/OneImage.Fuzz.dll" "$target" "$@"
  exit $?
fi

seconds="${1:?usage: run.sh <target> <seconds>}"
shift
[[ -x "$host" ]] || { echo "libFuzzer host not found at '$host' (set ONEIMAGE_FUZZ_TOOLS)" >&2; exit 2; }
[[ -x "$instrument" ]] || { echo "sharpfuzz not found at '$instrument' (set ONEIMAGE_FUZZ_TOOLS)" >&2; exit 2; }

export DOTNET_ROOT="${DOTNET_ROOT:-$(dirname "$(readlink -f "$(command -v "$dotnet_bin")")")}"
# WebP seeds come from the library's own encoder, so make them before instrumenting (instrumented code needs libFuzzer's shared memory).
if [[ "$target" == webp && -z "$(ls -A "$work/webp-seeds" 2>/dev/null)" ]]; then
  ONEIMAGE_FUZZ_TARGET=webp "$dotnet_bin" "$bin/OneImage.Fuzz.dll" seeds-webp "$work/webp-seeds" >/dev/null
fi
if [[ "$target" == bidi && -z "$(ls -A "$work/bidi-seeds" 2>/dev/null)" ]]; then
  ONEIMAGE_FUZZ_TARGET=bidi "$dotnet_bin" "$bin/OneImage.Fuzz.dll" seeds-bidi "$root/tests/testdata/unicode/BidiCharacterTest.txt" "$work/bidi-seeds" >/dev/null
fi
if [[ "$target" == breaking && -z "$(ls -A "$work/breaking-seeds" 2>/dev/null)" ]]; then
  for f in LineBreakTest.txt GraphemeBreakTest.txt; do
    ONEIMAGE_FUZZ_TARGET=breaking "$dotnet_bin" "$bin/OneImage.Fuzz.dll" seeds-breaking "$root/tests/testdata/unicode/$f" "$work/breaking-seeds" >/dev/null
  done
fi
"$instrument" "$bin/OneImage.dll" >/dev/null
[[ "$target" != font && "$target" != breaking && "$target" != bidi && "$target" != brotli && "$target" != richtext && "$target" != sysfont ]] || "$instrument" "$bin/OneImage.Text.dll" >/dev/null
[[ "$target" != svg-xml && "$target" != svg-render ]] || "$instrument" "$bin/OneImage.Svg.dll" >/dev/null

corpus="$work/corpus/$target"
findings="$work/findings/$target"
mkdir -p "$corpus" "$findings"

# Seeds: real files from the checkout's test data, with the target's mode byte in front.
if [[ -n "$seeds" ]]; then
  for pattern in $patterns; do
    for f in "$root/tests/testdata/$seeds"/$pattern; do
      [[ -f "$f" ]] || continue
      [[ $(stat -c %s "$f") -lt $maxlen ]] || continue
      out="$corpus/seed-$(basename "$f")"
      [[ -e "$out" ]] || { printf "$prefix" >"$out"; cat "$f" >>"$out"; }
    done
  done
fi
if [[ "$target" == svg-xml || "$target" == svg-render ]]; then
  # Every committed conformance document that fits, plus the compressed sample.
  while IFS= read -r f; do
    out="$corpus/seed-$(echo "${f#"$root/tests/testdata/resvg/"}" | tr '/' '_')"
    [[ -e "$out" ]] || cp "$f" "$out"
  done < <(find "$root/tests/testdata/resvg" \( -name '*.svg' -o -name '*.svgz' \) -size -16k | sort)
fi
[[ "$target" != breaking ]] || cp -n "$work"/breaking-seeds/* "$corpus"/ 2>/dev/null || true
[[ "$target" != bidi ]] || cp -n "$work"/bidi-seeds/* "$corpus"/ 2>/dev/null || true
# Hand-written seeds for surfaces with no data files: a tiny LZW stream (GIF clear, 'A', end) and a minimal EXIF block.
case "$target" in
  webp)
    if [[ -z "$(ls -A "$corpus")" ]]; then
      for f in "$work/webp-seeds"/*.webp; do { printf '\x00'; cat "$f"; } >"$corpus/$(basename "$f")"; done
    fi ;;
  lzw)
    [[ -e "$corpus/seed-gif" ]] || printf '\x08\x84\x8f\x09\x01' >"$corpus/seed-gif"
    [[ -e "$corpus/seed-tiff" ]] || printf '\x01\x80\x10\x60\x40\x40' >"$corpus/seed-tiff" ;;
  exif)
    [[ -e "$corpus/seed-ii" ]] || printf 'II*\x00\x08\x00\x00\x00\x01\x00\x12\x01\x03\x00\x01\x00\x00\x00\x06\x00\x00\x00\x00\x00\x00\x00' >"$corpus/seed-ii"
    [[ -e "$corpus/seed-mm" ]] || printf 'MM\x00*\x00\x00\x00\x08\x00\x01\x01\x12\x00\x03\x00\x00\x00\x01\x00\x06\x00\x00\x00\x00\x00\x00' >"$corpus/seed-mm" ;;
esac
[[ -n "$(ls -A "$corpus")" ]] || printf '\x00' >"$corpus/seed-empty"

# Run. -max_total_time bounds the run; crashes, hangs and out-of-memory inputs are written to the findings folder.
ONEIMAGE_FUZZ_TARGET="$target" "$host" \
  --target_path="$(command -v "$dotnet_bin")" --target_arg="$bin/OneImage.Fuzz.dll" \
  -max_total_time="$seconds" -timeout="${FUZZ_TIMEOUT:-10}" -rss_limit_mb="${FUZZ_RSS_MB:-2048}" \
  -max_len="$maxlen" -artifact_prefix="$findings/" -print_final_stats=1 "$@" "$corpus"
