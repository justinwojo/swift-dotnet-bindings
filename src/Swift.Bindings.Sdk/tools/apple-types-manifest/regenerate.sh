#!/usr/bin/env bash
# Regenerate src/Swift.Bindings.Sdk/tools/apple-types-manifest/manifest.json by dumping
# Apple Xcode SDK ABI JSON via swift-api-digester for the modules in include-types.json
# and feeding them into `Swift.Bindings --emit-apple-types-manifest`.
#
# This is a small driver — the pipeline's authoritative entry point is the generator CLI.
# Invoked manually during Phase 2 Session 2 and from Session 7's bootstrap.
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")"/../../../.. && pwd)"
MANIFEST_DIR="$REPO_ROOT/src/Swift.Bindings.Sdk/tools/apple-types-manifest"
GENERATOR_PROJECT="$REPO_ROOT/src/Swift.Bindings/src"

# Modules owning the seed types in include-types.json. When Session 7 broadens coverage,
# add its target-framework modules here too.
MODULES=(Foundation ManagedSettings CryptoKit)

# Platform targets driving swift-api-digester. Each dump contributes its platform's
# intro_* availability fields; the manifest builder unions them per swift_identity.
# Targets track the Apple SDK train the supplement ships against. Override the default
# (Xcode 26) via SDK_TRAIN_MAJOR=NN to regenerate against a newer train without
# editing the script.
SDK_TRAIN_MAJOR="${SDK_TRAIN_MAJOR:-26}"
SDK_TRAIN_VERSION="${SDK_TRAIN_MAJOR}.0"
PLATFORMS=(
  "ios|iphonesimulator|arm64-apple-ios${SDK_TRAIN_VERSION}-simulator"
  "maccatalyst|macosx|arm64-apple-ios${SDK_TRAIN_VERSION}-macabi"
  "tvos|appletvsimulator|arm64-apple-tvos${SDK_TRAIN_VERSION}-simulator"
  "macos|macosx|arm64-apple-macos${SDK_TRAIN_VERSION}"
)

WORKDIR="${WORKDIR:-/tmp/apple-abi-dump}"
mkdir -p "$WORKDIR"

# Opt-in partial mode for dev workflows where one platform's SDK may be absent.
# Default is fail-closed: every (module, platform) combination must yield an ABI
# dump. A ship manifest assembled from partial dumps silently loses platform
# availability coverage for types that are only present in the missing slice.
ALLOW_PARTIAL="${ALLOW_PARTIAL:-0}"
partial_forward=()
if [ "$ALLOW_PARTIAL" = "1" ]; then
  partial_forward=(--allow-partial-apple-types-manifest)
  echo "[regenerate] ALLOW_PARTIAL=1 — missing SDKs / digester failures will NOT fail the build." >&2
fi

abi_json_args=()
failures=()
for module in "${MODULES[@]}"; do
  for triple in "${PLATFORMS[@]}"; do
    IFS='|' read -r platform sdk target <<<"$triple"
    out="$WORKDIR/${module}.${platform}.abi.json"
    sdk_path="$(xcrun --sdk "$sdk" --show-sdk-path 2>/dev/null || true)"
    if [ -z "$sdk_path" ]; then
      msg="SDK '$sdk' not installed (module=$module platform=$platform)"
      if [ "$ALLOW_PARTIAL" = "1" ]; then
        echo "[regenerate] Skipping: $msg" >&2
        continue
      fi
      failures+=("$msg")
      continue
    fi
    echo "[regenerate] Dumping $module for $platform ($target) ..." >&2
    if ! xcrun swift-api-digester -dump-sdk -module "$module" -target "$target" -sdk "$sdk_path" -o "$out" 2>/dev/null; then
      msg="swift-api-digester failed (module=$module platform=$platform target=$target)"
      if [ "$ALLOW_PARTIAL" = "1" ]; then
        echo "[regenerate] Skipping: $msg" >&2
        continue
      fi
      failures+=("$msg")
      continue
    fi
    abi_json_args+=(--apple-abi-json "$out")
  done
done

if [ "${#failures[@]}" -gt 0 ]; then
  echo "[regenerate] ABI dump failures (fatal; pass ALLOW_PARTIAL=1 to downgrade):" >&2
  for f in "${failures[@]}"; do
    echo "  - $f" >&2
  done
  exit 1
fi

if [ "${#abi_json_args[@]}" -eq 0 ]; then
  echo "[regenerate] No ABI JSON dumps produced — aborting." >&2
  exit 1
fi

dotnet run --project "$GENERATOR_PROJECT" -- \
  --emit-apple-types-manifest \
  "${abi_json_args[@]}" \
  --apple-include-types "$MANIFEST_DIR/include-types.json" \
  --apple-version "${SDK_TRAIN_VERSION}.0" \
  --apple-sdk-train-label "Xcode ${SDK_TRAIN_MAJOR} / iOS ${SDK_TRAIN_MAJOR} / macOS ${SDK_TRAIN_MAJOR} / tvOS ${SDK_TRAIN_MAJOR}" \
  --apple-sdk-min-ios "$SDK_TRAIN_VERSION" \
  --apple-sdk-min-maccatalyst "$SDK_TRAIN_VERSION" \
  --apple-sdk-min-tvos "$SDK_TRAIN_VERSION" \
  --apple-sdk-min-macos "$SDK_TRAIN_VERSION" \
  ${partial_forward[@]+"${partial_forward[@]}"} \
  -o "$MANIFEST_DIR/manifest.json"

echo "[regenerate] Wrote $MANIFEST_DIR/manifest.json"
