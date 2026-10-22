#!/usr/bin/env bash
# Release gate: faster than ImageSharp 3.1.12 on every task with lower peak memory, and the distance to the native wrappers.
#
#   bench/gate.sh [--avx512] [--out FILE] [--work DIR] [--note TEXT] [--skip-timing] [--skip-peak]
#
# 1. builds the benchmark project in Release;
# 2. runs the BenchmarkDotNet rows tagged [BenchmarkCategory("Gate")] (standard MemoryDiagnoser output, JSON kept in DIR/bdn);
# 3. measures peak resident memory per task and library, one process each ("gate-peak");
# 4. writes one markdown report (default bench/results/gate-<date>.md).
#
# The x64 gate runs with AVX-512 switched off (DOTNET_EnableAVX512=0) so the AVX2 path is what is measured;
# --avx512 leaves it on for information. Inputs are generated deterministically; nothing is downloaded.
# Each library is run through its .NET package: SixLabors.ImageSharp 3.1.12, SkiaSharp, NetVips, Magick.NET.
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJ="$HERE/OneImage.Benchmarks"
AVX512=0
OUT=""
WORK="${GATE_WORK:-${TMPDIR:-$HERE/../.gate-work}/gate-work}"
NOTE=""
TIMING=1
PEAK=1
while [ $# -gt 0 ]; do
  case "$1" in
    --avx512) AVX512=1 ;;
    --out) OUT="$2"; shift ;;
    --work) WORK="$2"; shift ;;
    --note) NOTE="$2"; shift ;;
    --skip-timing) TIMING=0 ;;
    --skip-peak) PEAK=0 ;;
    *) echo "unknown option $1" >&2; exit 2 ;;
  esac
  shift
done
[ -n "$OUT" ] || OUT="$HERE/results/gate-$(date +%Y-%m-%d)$([ $AVX512 = 1 ] && echo -avx512).md"
mkdir -p "$WORK" "$(dirname "$OUT")"
if [ $AVX512 = 0 ]; then export DOTNET_EnableAVX512=0; fi
export DOTNET_TieredPGO=${DOTNET_TieredPGO:-1}

DN=dotnet
RUN=("$DN" run --no-build -c Release --project "$PROJ" --)
"$DN" build -c Release "$PROJ" -v quiet -clp:NoSummary

LOADLOG="$WORK/load.log"
: > "$LOADLOG"
( while true; do echo "$(date +%H:%M:%S) $(uptime | sed 's/.*load average/load average/')" >> "$LOADLOG"; sleep 60; done ) &
SAMPLER=$!
trap 'kill $SAMPLER 2>/dev/null || true' EXIT

UP_BEFORE="$(uptime)"
START="$(date '+%Y-%m-%d %H:%M:%S %Z')"

if [ $TIMING = 1 ]; then
  rm -rf "$WORK/bdn"
  "${RUN[@]}" --anyCategories Gate --exporters json --artifacts "$WORK/bdn" 2>&1 | tee "$WORK/bdn.log" | tail -n 5
fi

TASKS="thumb-jpeg thumb-webp png-to-webp jpeg-decode jpeg-encode png-decode png-encode webp-decode webp-encode gif-decode gif-encode gif-resize tiff-multipage-decode tiff-encode bmp-decode bmp-encode upscale-2x watermark cmyk-jpeg-to-srgb"
LIBS="oneimage imagesharp skia netvips magick"
PEAKTSV="$WORK/peak.tsv"
if [ $PEAK = 1 ]; then
  : > "$PEAKTSV"
  for t in $TASKS; do
    for l in $LIBS; do
      timeout 900 "${RUN[@]}" gate-peak "$t" "$l" 2>>"$WORK/peak.err" | grep -E '^(PEAK|SKIP)' >> "$PEAKTSV" || echo "FAILED $t $l" | tee -a "$WORK/peak.err" >&2
    done
  done
fi

# Colour accuracy of the CMYK task (CIEDE2000 against an ICC-correct reference; one process, all libraries).
DELTAE="$WORK/deltae.tsv"
: > "$DELTAE"
if [ $PEAK = 1 ]; then
  timeout 1800 "${RUN[@]}" gate-deltae 2>>"$WORK/peak.err" | grep -E '^DELTAE' >> "$DELTAE" || echo "FAILED gate-deltae" | tee -a "$WORK/peak.err" >&2
fi

UP_AFTER="$(uptime)"
END="$(date '+%Y-%m-%d %H:%M:%S %Z')"
kill $SAMPLER 2>/dev/null || true

INFO="$("${RUN[@]}" gate-peak info)"
HEADER="$WORK/header.md"
{
  echo "# Release gate report"
  echo
  [ -z "$NOTE" ] || { echo "> $NOTE"; echo; }
  echo "## Host"
  echo
  echo "- Started $START, finished $END"
  echo "- CPU: $(grep -m1 'model name' /proc/cpuinfo | sed 's/.*: //'), $(nproc) logical CPUs, $(awk '/MemTotal/ {printf "%.0f GiB", $2/1048576}' /proc/meminfo) RAM"
  echo "- OS: $(. /etc/os-release && echo "$PRETTY_NAME"), kernel $(uname -r)"
  echo "- .NET SDK $("$DN" --version); $INFO"
  echo "- AVX-512: $([ $AVX512 = 0 ] && echo 'disabled (DOTNET_EnableAVX512=0), the AVX2 gate configuration' || echo 'enabled, native run, information only')"
  echo "- Load before: \`$UP_BEFORE\`"
  echo "- Load during (sampled each minute):"
  sed 's/^/  - /' "$LOADLOG"
  echo "- Load after: \`$UP_AFTER\`"
  echo "- Libraries: ImageSharp 3.1.12, SkiaSharp, NetVips, Magick.NET (Q8), through their .NET packages; this library built from the same checkout."
  echo
  echo "## Method"
  echo
  echo "- Time: BenchmarkDotNet, \`MemoryDiagnoser\`, Throughput strategy with 2-3 warmup and 8-12 measured iterations per row (the job each benchmark class declares); Mean shown. Inputs are generated deterministically (\`bench/OneImage.Benchmarks\`); every task runs from and to memory."
  echo "- Peak memory: one process per task and library. After the inputs are built the process runs the operation once to warm up (one-time code generation, thread-pool start-up and native library loading are excluded for every library alike), collects garbage, resets the kernel's resident-set high-water mark, runs the operation 4 times (collecting garbage after each run, outside the timed part, so a result left on the managed heap by one run does not add to the next), and reports (peak − resident before) in MiB, i.e. the working memory of the operation (differences under 2 MiB count as a tie), with the process total in brackets. Native allocations are included; they are what NetVips, SkiaSharp and Magick.NET use."
  echo "- Rows within a task pick each library's idiomatic one-call path to the same output (stated per row). Where output types differ (Rgb24 vs Rgba32) both rows exist."
  echo "- A library that cannot do a task has no row: SkiaSharp has no BMP, TIFF or animated GIF encoder and no TIFF decoder here; NetVips's native build has no BMP loader or saver."
  echo
} > "$HEADER"

"${RUN[@]}" gate-report "$WORK/bdn/results" "$PEAKTSV" "$HEADER" "$OUT" "$DELTAE"
echo "report: $OUT"
