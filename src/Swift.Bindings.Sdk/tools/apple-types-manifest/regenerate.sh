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
PLATFORMS=(
  "ios|iphonesimulator|arm64-apple-ios18.0-simulator"
  "maccatalyst|macosx|arm64-apple-ios18.0-macabi"
  "tvos|appletvsimulator|arm64-apple-tvos18.0-simulator"
  "macos|macosx|arm64-apple-macos15.0"
)

WORKDIR="${WORKDIR:-/tmp/apple-abi-dump}"
mkdir -p "$WORKDIR"

abi_json_args=()
for module in "${MODULES[@]}"; do
  for triple in "${PLATFORMS[@]}"; do
    IFS='|' read -r platform sdk target <<<"$triple"
    out="$WORKDIR/${module}.${platform}.abi.json"
    sdk_path="$(xcrun --sdk "$sdk" --show-sdk-path 2>/dev/null || true)"
    if [ -z "$sdk_path" ]; then
      echo "[regenerate] Skipping $module ($platform): SDK '$sdk' not installed." >&2
      continue
    fi
    echo "[regenerate] Dumping $module for $platform ($target) ..." >&2
    if ! xcrun swift-api-digester -dump-sdk -module "$module" -target "$target" -sdk "$sdk_path" -o "$out" 2>/dev/null; then
      echo "[regenerate] swift-api-digester failed for $module on $platform; skipping." >&2
      continue
    fi
    abi_json_args+=(--apple-abi-json "$out")
  done
done

if [ "${#abi_json_args[@]}" -eq 0 ]; then
  echo "[regenerate] No ABI JSON dumps produced — aborting." >&2
  exit 1
fi

dotnet run --project "$GENERATOR_PROJECT" -- \
  --emit-apple-types-manifest \
  "${abi_json_args[@]}" \
  --apple-include-types "$MANIFEST_DIR/include-types.json" \
  --apple-sdk-train-major 18 \
  --apple-sdk-train-label "Xcode 16 / iOS 18 / macOS 15 / tvOS 18" \
  --apple-sdk-min-ios 18.0 \
  --apple-sdk-min-maccatalyst 18.0 \
  --apple-sdk-min-tvos 18.0 \
  --apple-sdk-min-macos 15.0 \
  -o "$MANIFEST_DIR/manifest.json"

echo "[regenerate] Wrote $MANIFEST_DIR/manifest.json"
