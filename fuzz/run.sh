#!/usr/bin/env bash
# Build, instrument and run one fuzz target under libFuzzer.
#
#   fuzz/run.sh <target> <seconds> [extra libFuzzer flags]
#   fuzz/run.sh replay <target> <file>...        # run saved inputs once, without libFuzzer (reproduce a crash)
#
# Targets: jpeg inflate lzw icc exif tiff   (see fuzz/README.md)
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
  tiff)    seeds=tiff;        prefix='\x00'; patterns='*.tif'; maxlen=262144 ;;
  *) echo "unknown target '$target'" >&2; exit 2 ;;
esac

bin="$work/bin/$target"
mkdir -p "$bin"

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
"$instrument" "$bin/OneImage.dll" >/dev/null

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
# Hand-written seeds for surfaces with no data files: a tiny LZW stream (GIF clear, 'A', end) and a minimal EXIF block.
case "$target" in
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
