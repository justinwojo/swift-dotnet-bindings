#!/bin/bash
# Copyright (c) 2026 Justin Wojciechowski.
# Licensed under the MIT License.
#
# Builds and packs all three SwiftBindings NuGet packages in dependency order:
#   1. SwiftBindings.Runtime
#   2. SwiftBindings.Sdk (publishes generator + packs)
#   3. SwiftBindings.Templates
#
# Usage: ./pack-all.sh --version <semver> [--output <dir>]

set -euo pipefail

REPO_ROOT="$(cd "$(dirname "$0")" && pwd)"

# --- Parse arguments ---
VERSION=""
OUTPUT_DIR="/tmp/swift-nuget/"

while [[ $# -gt 0 ]]; do
    case "$1" in
        --version)
            VERSION="$2"
            shift 2
            ;;
        --output)
            OUTPUT_DIR="$2"
            shift 2
            ;;
        -h|--help)
            echo "Usage: $0 --version <semver> [--output <dir>]"
            echo ""
            echo "Options:"
            echo "  --version <semver>  Required. Version for all packages (e.g., 0.1.0, 0.1.0-preview.1)"
            echo "  --output <dir>      Output directory for .nupkg files (default: /tmp/swift-nuget/)"
            exit 0
            ;;
        *)
            echo "Error: Unknown argument '$1'"
            echo "Usage: $0 --version <semver> [--output <dir>]"
            exit 1
            ;;
    esac
done

if [[ -z "$VERSION" ]]; then
    echo "Error: --version is required"
    echo "Usage: $0 --version <semver> [--output <dir>]"
    exit 1
fi

# --- Version file locations ---
RUNTIME_CSPROJ="$REPO_ROOT/src/Swift.Runtime/src/Swift.Runtime.csproj"
SDK_CSPROJ="$REPO_ROOT/src/Swift.Bindings.Sdk/Swift.Bindings.Sdk.csproj"
TEMPLATES_CSPROJ="$REPO_ROOT/src/Swift.Bindings.Templates/Swift.Bindings.Templates.csproj"
SDK_PROPS="$REPO_ROOT/src/Swift.Bindings.Sdk/Sdk/Sdk.props"
TEMPLATE_PROJECT="$REPO_ROOT/src/Swift.Bindings.Templates/content/swift-binding/ProjectName.csproj"

VERSION_FILES=(
    "$RUNTIME_CSPROJ"
    "$SDK_CSPROJ"
    "$TEMPLATES_CSPROJ"
    "$SDK_PROPS"
    "$TEMPLATE_PROJECT"
)

# --- Backup originals ---
echo "=== Packing SwiftBindings v${VERSION} ==="
echo ""

for f in "${VERSION_FILES[@]}"; do
    if [[ ! -f "$f" ]]; then
        echo "Error: Version file not found: $f"
        exit 1
    fi
    cp "$f" "${f}.pack-bak"
done

# --- Restore function (called on exit, even on failure) ---
restore_versions() {
    for f in "${VERSION_FILES[@]}"; do
        if [[ -f "${f}.pack-bak" ]]; then
            mv "${f}.pack-bak" "$f"
        fi
    done
}
trap restore_versions EXIT

# --- Update versions ---
echo "Updating version to ${VERSION} in 6 locations..."

# 1. Runtime csproj: <PackageVersion>0.0.0-dev</PackageVersion>
sed -i '' "s|<PackageVersion>[^<]*</PackageVersion>|<PackageVersion>${VERSION}</PackageVersion>|" "$RUNTIME_CSPROJ"

# 2. SDK csproj: <PackageVersion>0.0.0-dev</PackageVersion>
sed -i '' "s|<PackageVersion>[^<]*</PackageVersion>|<PackageVersion>${VERSION}</PackageVersion>|" "$SDK_CSPROJ"

# 3. Templates csproj: <PackageVersion>0.0.0-dev</PackageVersion>
sed -i '' "s|<PackageVersion>[^<]*</PackageVersion>|<PackageVersion>${VERSION}</PackageVersion>|" "$TEMPLATES_CSPROJ"

# 4. Sdk.props: <_SwiftBindingSdkVersion> and <SwiftRuntimeVersion>
sed -i '' "s|<_SwiftBindingSdkVersion>[^<]*</_SwiftBindingSdkVersion>|<_SwiftBindingSdkVersion>${VERSION}</_SwiftBindingSdkVersion>|" "$SDK_PROPS"
sed -i '' "s|<SwiftRuntimeVersion Condition=\"'\$(SwiftRuntimeVersion)' == ''\">[^<]*</SwiftRuntimeVersion>|<SwiftRuntimeVersion Condition=\"'\$(SwiftRuntimeVersion)' == ''\">${VERSION}</SwiftRuntimeVersion>|" "$SDK_PROPS"

# 5. Template project: Sdk="SwiftBindings.Sdk/..."
sed -i '' "s|Sdk=\"SwiftBindings.Sdk/[^\"]*\"|Sdk=\"SwiftBindings.Sdk/${VERSION}\"|" "$TEMPLATE_PROJECT"

echo "Versions updated."
echo ""

# --- Build packages in dependency order ---
mkdir -p "$OUTPUT_DIR"

# 1. Runtime
echo "=== [1/3] Packing SwiftBindings.Runtime ==="
dotnet pack "$RUNTIME_CSPROJ" \
    -c Release \
    -o "$OUTPUT_DIR" \
    --nologo -v quiet
echo ""

# 2. SDK (publish generator + pack)
echo "=== [2/3] Packing SwiftBindings.Sdk ==="
echo "  Publishing generator..."
dotnet publish "$REPO_ROOT/src/Swift.Bindings/src/Swift.Bindings.csproj" \
    -c Release \
    -o "$REPO_ROOT/src/Swift.Bindings.Sdk/tools/net10.0/any/" \
    --nologo -v quiet

echo "  Packing SDK..."
dotnet pack "$SDK_CSPROJ" \
    -c Release \
    -o "$OUTPUT_DIR" \
    --nologo -v quiet
echo ""

# 3. Templates
echo "=== [3/3] Packing SwiftBindings.Templates ==="
dotnet pack "$TEMPLATES_CSPROJ" \
    -c Release \
    -o "$OUTPUT_DIR" \
    --nologo -v quiet
echo ""

# --- Summary (versions restored by EXIT trap) ---
echo "=== All packages built ==="
echo "Output: $OUTPUT_DIR"
echo ""
ls -1 "$OUTPUT_DIR"/*.nupkg 2>/dev/null | while read -r pkg; do
    echo "  $(basename "$pkg")"
done

NUPKG_COUNT=$(ls -1 "$OUTPUT_DIR"/*.nupkg 2>/dev/null | wc -l | tr -d ' ')
echo ""
echo "${NUPKG_COUNT} package(s) created."
