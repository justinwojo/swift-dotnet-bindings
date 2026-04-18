#!/usr/bin/env bash
# Session 5 / M9 — framework-linkage blast-radius smoke test.
#
# Publishes BlastRadius.Baseline + BlastRadius.Consumer and captures
# otool -L / nm -gU / strings diffs so we can quantify what the Apple
# supplement drags into a consumer binary.
#
# Run from the repo root:
#   bash BindingTests/BlastRadius.Baseline/measure-blast-radius.sh
#
# Output lands in BindingTests/BlastRadius.Baseline/measurements/. The .diff
# files are the load-bearing artifacts — commit them alongside the csproj
# changes so future refactors can see regressions at a glance.

set -euo pipefail

# We cross-build for osx-arm64 below. On an Intel host the Mach-O output encodes
# a different cpusubtype + different Swift stdlib path, so the diff would diverge
# for reasons unrelated to framework linkage. Fail loud instead of producing a
# misleading "clean" measurement.
HOST_ARCH="$(uname -m)"
if [ "$HOST_ARCH" != "arm64" ]; then
  echo "ERROR: measure-blast-radius.sh requires an arm64 host; got '$HOST_ARCH'." >&2
  echo "       The script builds -r osx-arm64 and compares the resulting Mach-Os;" >&2
  echo "       an Intel cross-compile introduces differences unrelated to Swift linkage." >&2
  exit 2
fi

REPO_ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
OUT_DIR="$REPO_ROOT/BindingTests/BlastRadius.Baseline/measurements"
mkdir -p "$OUT_DIR"

BASELINE_PROJ="$REPO_ROOT/BindingTests/BlastRadius.Baseline/BlastRadius.Baseline.csproj"
CONSUMER_PROJ="$REPO_ROOT/BindingTests/BlastRadius.Consumer/BlastRadius.Consumer.csproj"

publish() {
  local proj="$1"
  local label="$2"
  echo ">>> Building $label"
  # NOTE: We use `dotnet build` instead of `dotnet publish`:
  #   - PublishAot fails because Swift.Analyzers (a Roslyn analyzer project pulled
  #     in transitively) is not AOT-compatible.
  #   - Plain `publish --self-contained` fails to locate
  #     `Microsoft.NETCore.App.Runtime.Mono.osx-arm64` 10.0.3 on nuget.org.
  # For the linkage question we only need the .app bundle (Xamarin.Shared.Sdk
  # produces it during Build), so we keep the flow simple and deterministic.
  dotnet build "$proj" \
    -c Release \
    -r osx-arm64 \
    2>&1 | tail -10
}

inspect() {
  local bin="$1"
  local label="$2"
  echo ">>> Inspecting $label"
  otool -L "$bin" > "$OUT_DIR/$label.otool-L.txt"
  nm -gU "$bin"  > "$OUT_DIR/$label.nm.txt" 2>/dev/null || true
  # `$s` is the Swift 5 mangling prefix (with `$S` as its Swift 4 predecessor and
  # `_$s` as the same symbol seen through a leading-underscore ABI). Add them so
  # stripped symbols still show up in the diff — `_swift`/`swift` alone misses
  # every type-metadata accessor we care about ($s...Ma).
  strings "$bin" | grep -E '^(_?swift|_?_swift|SB_|SBW_|SwiftBindings|Swift\.|_?\$[sS])' \
    | sort -u > "$OUT_DIR/$label.strings-swift.txt" || true
  wc -c "$bin" | awk '{print $1}' > "$OUT_DIR/$label.size-bytes.txt"
}

inspect_bundle() {
  local app="$1"
  local label="$2"
  echo ">>> Listing bundle contents for $label"
  find "$app" -type f \( -name '*.dylib' -o -name '*.framework' -o -name '*.dll' \) \
    | sed "s|$app/||" | sort > "$OUT_DIR/$label.bundle-contents.txt" || true
  du -sk "$app" | awk '{print $1 * 1024}' > "$OUT_DIR/$label.bundle-size-bytes.txt"
}

publish "$BASELINE_PROJ" baseline
publish "$CONSUMER_PROJ" consumer

BASELINE_APP=$(find "$REPO_ROOT/BindingTests/BlastRadius.Baseline/bin" -name 'BlastRadius.Baseline.app' -type d | head -1 || true)
CONSUMER_APP=$(find "$REPO_ROOT/BindingTests/BlastRadius.Consumer/bin" -name 'BlastRadius.Consumer.app' -type d | head -1 || true)

BASELINE_BIN=$(find "${BASELINE_APP:-/nonexistent}" -type f -perm +111 -name BlastRadius.Baseline | head -1 || true)
CONSUMER_BIN=$(find "${CONSUMER_APP:-/nonexistent}" -type f -perm +111 -name BlastRadius.Consumer | head -1 || true)

if [[ -z "$BASELINE_BIN" || -z "$CONSUMER_BIN" ]]; then
  echo "ERROR: could not locate published binaries — inspect publish output above." >&2
  exit 2
fi

echo "Baseline binary: $BASELINE_BIN"
echo "Consumer binary: $CONSUMER_BIN"

inspect "$BASELINE_BIN" baseline
inspect "$CONSUMER_BIN" consumer

inspect_bundle "$BASELINE_APP" baseline
inspect_bundle "$CONSUMER_APP" consumer

echo ">>> Diffing"
diff -u "$OUT_DIR/baseline.otool-L.txt"        "$OUT_DIR/consumer.otool-L.txt"        > "$OUT_DIR/otool-L.diff" || true
diff -u "$OUT_DIR/baseline.nm.txt"             "$OUT_DIR/consumer.nm.txt"             > "$OUT_DIR/nm.diff" || true
diff -u "$OUT_DIR/baseline.strings-swift.txt"  "$OUT_DIR/consumer.strings-swift.txt"  > "$OUT_DIR/strings-swift.diff" || true
diff -u "$OUT_DIR/baseline.bundle-contents.txt" "$OUT_DIR/consumer.bundle-contents.txt" > "$OUT_DIR/bundle-contents.diff" || true

baseline_size=$(cat "$OUT_DIR/baseline.size-bytes.txt")
consumer_size=$(cat "$OUT_DIR/consumer.size-bytes.txt")
delta=$((consumer_size - baseline_size))
pct=$(awk -v b="$baseline_size" -v d="$delta" 'BEGIN { if (b==0) print "n/a"; else printf "%.2f%%\n", (d/b)*100 }')

baseline_bundle=$(cat "$OUT_DIR/baseline.bundle-size-bytes.txt")
consumer_bundle=$(cat "$OUT_DIR/consumer.bundle-size-bytes.txt")
bundle_delta=$((consumer_bundle - baseline_bundle))
bundle_pct=$(awk -v b="$baseline_bundle" -v d="$bundle_delta" 'BEGIN { if (b==0) print "n/a"; else printf "%.2f%%\n", (d/b)*100 }')

{
  echo "=== Mach-O executable ==="
  echo "Baseline size: $baseline_size bytes"
  echo "Consumer size: $consumer_size bytes"
  echo "Delta:         $delta bytes ($pct)"
  echo
  echo "=== .app bundle (includes dylibs + managed assemblies) ==="
  echo "Baseline bundle: $baseline_bundle bytes"
  echo "Consumer bundle: $consumer_bundle bytes"
  echo "Delta:           $bundle_delta bytes ($bundle_pct)"
} > "$OUT_DIR/size-summary.txt"
cat "$OUT_DIR/size-summary.txt"

echo ">>> Done. Artifacts under: $OUT_DIR"
