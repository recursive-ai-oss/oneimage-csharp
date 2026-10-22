#!/usr/bin/env bash
# Runs the test suite as linux-arm64 code under user-mode qemu on an x64 host.
# QEMU proves correctness only; its timings mean nothing.
#
#   tests/arm64-qemu.sh [sysroot] [extra test-runner arguments...]
#
# Environment:
#   ARM64_SYSROOT   arm64 root with libc6, libgcc-s1, libstdc++6 and libssl3 extracted
#                   (dpkg-deb -x from the Ubuntu arm64 packages); or pass it as $1.
#   QEMU            qemu-aarch64 binary (default: qemu-aarch64 from PATH).
#   WORK            scratch directory for the publish output (default: a mktemp dir).
#   SMOKE=1         also try a NativeAOT linux-arm64 publish of the smoke app and run it
#                   (needs the arm64 cross toolchain; skipped with a note if it cannot publish).
#
# Excluded tests, because they cannot run under emulation:
#   * Comparisons against libvips through NetVips: the NetVips native package here is x64 only.
#     The same comparisons run on x64 and on a native arm64 runner.
#   * The timing-bound ICC mutation test, which asserts a 90 s wall-clock budget that
#     emulation exceeds (it completes all cases without a wrong exception type).
set -euo pipefail

SYSROOT="${ARM64_SYSROOT:-}"
if [[ -n "${1:-}" && "$1" != -* ]]; then SYSROOT="$1"; shift; fi
[[ -d "$SYSROOT" ]] || { echo "usage: $0 <arm64-sysroot> [runner args]  (or set ARM64_SYSROOT)" >&2; exit 2; }
QEMU="${QEMU:-qemu-aarch64}"
command -v "$QEMU" >/dev/null || { echo "qemu-aarch64 not found (set QEMU)" >&2; exit 2; }

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
WORK="${WORK:-$(mktemp -d)}"
OUT="$WORK/arm64-tests"
mkdir -p "$OUT"

dotnet publish "$ROOT/tests/OneImage.Tests" -c Release -r linux-arm64 --self-contained \
  -p:InvariantGlobalization=true -o "$OUT" -v quiet -clp:NoSummary

EXCLUDE=(
  OneImage.Tests.Filter.FilterVipsComparisonTests.GaussianBlurIsCloseToLibvips
  OneImage.Tests.Filter.FilterVipsComparisonTests.MedianEqualsLibvipsInTheInterior
  OneImage.Tests.Filter.FilterVipsComparisonTests.SobelAndSharpenAreReportedAgainstLibvips
  OneImage.Tests.Geometry.VipsComparisonTests.ExactOpsEqualLibvips
  OneImage.Tests.Geometry.VipsComparisonTests.Rotate17IsCloseToLibvips
  OneImage.Tests.Png.ApngTests.EncodedAnimationRoundTripsAndLibpngSeesTheDefaultImage
  OneImage.Tests.Png.PngEncodeTests.EveryFilterAndLevelRoundTrips
  OneImage.Tests.Png.PngEncodeTests.IndexedOutputForFewColorsIsLosslessAndSmallestDepth
  OneImage.Tests.Png.PngEncodeTests.IndexedWithAlphaWritesTrnsAndKeepsAlpha
  OneImage.Tests.Png.PngEncodeTests.MetadataBlobsRoundTrip
  OneImage.Tests.Png.PngEncodeTests.RoundTripsBitExactThroughOurDecoderAndLibpng
  OneImage.Tests.Png.PngSuiteTests.DecodesBitExactAgainstLibpng
  OneImage.Tests.Quantize.PngIndexedPhotoTests.AlphaSurvivesAsTrnsEntries
  OneImage.Tests.Quantize.PngIndexedPhotoTests.PhotoSavedIndexedHasAtMost256ColorsAndMatchesTheQuantizer
  OneImage.Tests.Tone.ToneOracleTests.BrightnessContrastAgreeWithTheEquivalentLinearMaps
  OneImage.Tests.Tone.ToneOracleTests.GammaAgreesWithLibvips
  OneImage.Tests.Tone.ToneOracleTests.GrayscaleAgreesWhereTheMathIsTheSame
  OneImage.Tests.Tone.ToneOracleTests.InvertIsExactlyTheSame
  OneImage.Tests.Tone.ToneOracleTests.LevelsAgreeWithLibvipsLinearAndClip
  OneImage.Tests.MetadataRobustnessTests.MutatedAndTruncatedIccGivesOnlyTypedErrors
)
ARGS=()
for m in "${EXCLUDE[@]}"; do ARGS+=(-method- "$m"); done

echo "excluded: ${#EXCLUDE[@]} test methods (see header)"
cd "$OUT"
# Tests run one at a time: emulated threads are slow and the timing-sensitive tests need headroom.
"$QEMU" -L "$SYSROOT" ./OneImage.Tests -parallel none "${ARGS[@]}" "$@" && status=0 || status=$?

if [[ "${SMOKE:-0}" == 1 ]]; then
  if dotnet publish "$ROOT/tests/OneImage.AotSmoke" -c Release -r linux-arm64 -o "$WORK/smoke" -warnaserror; then
    "$QEMU" -L "$SYSROOT" "$WORK/smoke/OneImage.AotSmoke"
  else
    echo "NativeAOT linux-arm64 publish is not available on this host; smoke app skipped" >&2
  fi
fi
exit $status
