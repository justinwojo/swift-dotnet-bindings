#!/bin/bash
# scripts/fetch-libraries.sh — Fetch and build library xcframeworks for validation.
#
# Reads validation-libraries.json and builds xcframeworks into .libraries/
#
# Usage:
#   scripts/fetch-libraries.sh                    # Build all public libraries
#   scripts/fetch-libraries.sh --filter Nuke      # Build matching libraries only
#   scripts/fetch-libraries.sh --force            # Rebuild even if cached
#   scripts/fetch-libraries.sh --list             # Show library status

set -o pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
ROOT_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"

source "$SCRIPT_DIR/lib.sh"

MANIFEST="$ROOT_DIR/validation-libraries.json"
LIBRARIES_DIR="$ROOT_DIR/.libraries"
FILTER=""
FORCE=false
LIST_ONLY=false

while [[ $# -gt 0 ]]; do
    case $1 in
        --filter) FILTER="$2"; shift 2 ;;
        --force) FORCE=true; shift ;;
        --list) LIST_ONLY=true; shift ;;
        -h|--help)
            echo "Usage: scripts/fetch-libraries.sh [flags]"
            echo ""
            echo "Fetches and builds xcframeworks for library validation."
            echo "Reads from validation-libraries.json, outputs to .libraries/"
            echo ""
            echo "Flags:"
            echo "  --filter <pat>  Only libraries matching pattern (case-insensitive)"
            echo "  --force         Rebuild even if cached"
            echo "  --list          Show library status without building"
            echo "  -h, --help      Show this help"
            exit 0
            ;;
        *) echo "Unknown flag: $1"; exit 1 ;;
    esac
done

[[ -f "$MANIFEST" ]] || die "Manifest not found: $MANIFEST"
mkdir -p "$LIBRARIES_DIR"

# --- Cache Helpers ---

is_cached() {
    local name="$1" version="$2"
    local version_file="$LIBRARIES_DIR/$name/.version"
    if [[ -f "$version_file" ]] && ! $FORCE; then
        local cached
        cached=$(cat "$version_file")
        [[ "$cached" == "$version" ]]
    else
        return 1
    fi
}

write_cache() {
    local name="$1" version="$2"
    echo "$version" > "$LIBRARIES_DIR/$name/.version"
}

# --- Revision Verification ---

verify_revision() {
    local repo="$1" tag="$2" revision="$3"
    if [[ -n "$revision" ]]; then
        echo -e "  ${DIM}Verifying tag $tag...${NC}"
        local remote_sha
        remote_sha=$(git ls-remote "$repo" "refs/tags/$tag" "refs/tags/$tag^{}" 2>/dev/null | tail -1 | awk '{print $1}')
        # Try v-prefixed tag if plain tag not found
        if [[ -z "$remote_sha" && "$tag" != v* ]]; then
            remote_sha=$(git ls-remote "$repo" "refs/tags/v$tag" "refs/tags/v$tag^{}" 2>/dev/null | tail -1 | awk '{print $1}')
        fi
        if [[ -z "$remote_sha" ]]; then
            echo -e "  ${RED}Tag '$tag' not found in $repo${NC}"
            return 1
        fi
        if [[ "$remote_sha" != "$revision" ]]; then
            echo -e "  ${RED}Tag '$tag' resolves to $remote_sha, expected $revision${NC}"
            return 1
        fi
    fi
}

# --- Source Mode ---

build_source() {
    local lib_idx=$1
    local name repo version min_ios revision
    name=$(manifest_lib_field "$lib_idx" name)
    repo=$(manifest_lib_field "$lib_idx" repository)
    version=$(manifest_lib_field "$lib_idx" version)
    min_ios=$(manifest_lib_field "$lib_idx" minIOS "15.0")
    revision=$(manifest_lib_field "$lib_idx" revision "")

    local lib_dir="$LIBRARIES_DIR/$name"
    local build_dir="$lib_dir/.build-workspace"
    local archives_dir="$build_dir/archives"
    local derived_data="$build_dir/DerivedData"

    # Read build settings
    local build_settings=()
    while IFS= read -r line; do
        [[ -n "$line" ]] && build_settings+=("$line")
    done < <(manifest_build_settings "$lib_idx")

    # Verify revision if provided
    if [[ -n "$revision" ]]; then
        verify_revision "$repo" "$version" "$revision" || return 1
    fi

    # Clean and prepare
    rm -rf "$build_dir"
    rm -rf "$lib_dir"/*.xcframework
    mkdir -p "$build_dir"

    echo -e "  ${CYAN}Cloning $repo @ $version${NC}"
    if ! git clone --depth 1 --branch "$version" "$repo" "$build_dir/source" 2>&1 | tail -1; then
        echo -e "  ${RED}Clone failed${NC}"
        rm -rf "$build_dir"
        return 1
    fi

    # Build each product
    local prod_count
    prod_count=$(manifest_product_count "$lib_idx")

    for ((j=0; j<prod_count; j++)); do
        local scheme framework
        scheme=$(manifest_product_field "$lib_idx" "$j" scheme "")
        framework=$(manifest_product_field "$lib_idx" "$j" framework)

        if [[ -z "$scheme" ]]; then
            echo -e "  ${RED}Product $framework missing 'scheme' (required for source mode)${NC}"
            rm -rf "$build_dir"
            return 1
        fi

        echo -e "  ${CYAN}Building $framework (scheme: $scheme) — device${NC}"
        if ! (cd "$build_dir/source" && xcodebuild archive \
            -scheme "$scheme" \
            -destination "generic/platform=iOS" \
            -archivePath "$archives_dir/${framework}-ios-arm64" \
            -derivedDataPath "$derived_data/device" \
            BUILD_LIBRARY_FOR_DISTRIBUTION=YES \
            SKIP_INSTALL=NO \
            MACH_O_TYPE=mh_dylib \
            IPHONEOS_DEPLOYMENT_TARGET="$min_ios" \
            ${build_settings[@]+"${build_settings[@]}"} \
            -quiet 2>&1 | tail -3); then
            echo -e "  ${RED}Device build failed for $framework${NC}"
            rm -rf "$build_dir"
            return 1
        fi

        echo -e "  ${CYAN}Building $framework (scheme: $scheme) — simulator${NC}"
        if ! (cd "$build_dir/source" && xcodebuild archive \
            -scheme "$scheme" \
            -destination "generic/platform=iOS Simulator" \
            -archivePath "$archives_dir/${framework}-ios-simulator" \
            -derivedDataPath "$derived_data/simulator" \
            BUILD_LIBRARY_FOR_DISTRIBUTION=YES \
            SKIP_INSTALL=NO \
            MACH_O_TYPE=mh_dylib \
            IPHONEOS_DEPLOYMENT_TARGET="$min_ios" \
            ${build_settings[@]+"${build_settings[@]}"} \
            -quiet 2>&1 | tail -3); then
            echo -e "  ${RED}Simulator build failed for $framework${NC}"
            rm -rf "$build_dir"
            return 1
        fi

        # Find framework in archives
        local device_fw simulator_fw
        device_fw=$(find "$archives_dir/${framework}-ios-arm64.xcarchive/Products" -name "${framework}.framework" -type d 2>/dev/null | head -1)
        simulator_fw=$(find "$archives_dir/${framework}-ios-simulator.xcarchive/Products" -name "${framework}.framework" -type d 2>/dev/null | head -1)

        if [[ -z "$device_fw" ]]; then
            echo -e "  ${RED}${framework}.framework not found in device archive${NC}"
            rm -rf "$build_dir"
            return 1
        fi
        if [[ -z "$simulator_fw" ]]; then
            echo -e "  ${RED}${framework}.framework not found in simulator archive${NC}"
            rm -rf "$build_dir"
            return 1
        fi

        # Inject Swift module interfaces if missing (SPM dynamic libraries)
        local dd_variant
        for fw_path in "$device_fw" "$simulator_fw"; do
            if [[ "$fw_path" == "$device_fw" ]]; then dd_variant="device"; else dd_variant="simulator"; fi
            if [[ ! -d "$fw_path/Modules/${framework}.swiftmodule" ]]; then
                local swiftmod
                swiftmod=$(find "$derived_data/$dd_variant" -path "*/ArchiveIntermediates/${scheme}/BuildProductsPath/*/${framework}.swiftmodule" -type d 2>/dev/null | head -1)
                if [[ -n "$swiftmod" ]]; then
                    echo -e "  ${DIM}Injecting Swift module interfaces${NC}"
                    mkdir -p "$fw_path/Modules"
                    cp -R "$swiftmod" "$fw_path/Modules/"
                fi
            fi
        done

        # Create xcframework
        if ! xcodebuild -create-xcframework \
            -framework "$device_fw" \
            -framework "$simulator_fw" \
            -output "$lib_dir/${framework}.xcframework" 2>&1 | tail -1; then
            echo -e "  ${RED}Failed to create ${framework}.xcframework${NC}"
            rm -rf "$build_dir"
            return 1
        fi

        echo -e "  ${GREEN}${framework}.xcframework built${NC}"
    done

    # Cleanup
    rm -rf "$build_dir"
    write_cache "$name" "$version"
}

# --- Binary Mode ---

build_binary() {
    local lib_idx=$1
    local name repo version min_ios revision
    name=$(manifest_lib_field "$lib_idx" name)
    repo=$(manifest_lib_field "$lib_idx" repository)
    version=$(manifest_lib_field "$lib_idx" version)
    min_ios=$(manifest_lib_field "$lib_idx" minIOS "15.0")
    revision=$(manifest_lib_field "$lib_idx" revision "")

    local lib_dir="$LIBRARIES_DIR/$name"
    local build_dir="$lib_dir/.build-workspace"

    # Verify revision if provided
    if [[ -n "$revision" ]]; then
        verify_revision "$repo" "$version" "$revision" || return 1
    fi

    # Clean previous
    rm -rf "$build_dir"
    rm -rf "$lib_dir"/*.xcframework
    mkdir -p "$build_dir/Sources"

    # SPM platform version
    local spm_ios_ver
    spm_ios_ver=$(python3 -c "print(f'.v{\"$min_ios\".split(\".\")[0]}')")

    # Create minimal Package.swift
    cat > "$build_dir/Package.swift" <<SWIFT
// swift-tools-version:5.9
import PackageDescription
let package = Package(
    name: "Resolver",
    platforms: [.iOS($spm_ios_ver)],
    dependencies: [
        .package(url: "$repo", exact: "$version")
    ],
    targets: [.target(name: "Resolver", path: "Sources")]
)
SWIFT
    echo "// placeholder" > "$build_dir/Sources/Resolver.swift"

    echo -e "  ${CYAN}Resolving SPM dependencies${NC}"
    if ! (cd "$build_dir" && swift package resolve 2>&1 | tail -3); then
        echo -e "  ${RED}SPM resolve failed${NC}"
        rm -rf "$build_dir"
        return 1
    fi

    local artifacts_dir="$build_dir/.build/artifacts"
    local prod_count
    prod_count=$(manifest_product_count "$lib_idx")

    for ((j=0; j<prod_count; j++)); do
        local framework
        framework=$(manifest_product_field "$lib_idx" "$j" framework)

        # Find xcframework in artifacts (exclude __MACOSX resource forks)
        local found
        found=$(find "$artifacts_dir" -name "__MACOSX" -prune -o -name "${framework}.xcframework" -type d -print 2>/dev/null | head -1)

        if [[ -z "$found" ]]; then
            echo -e "  ${RED}${framework}.xcframework not found in SPM artifacts${NC}"
            echo -e "  ${DIM}Available:${NC}"
            find "$artifacts_dir" -name "*.xcframework" -type d 2>/dev/null | sed 's/^/    /' >&2
            rm -rf "$build_dir"
            return 1
        fi

        cp -R "$found" "$lib_dir/${framework}.xcframework"
        echo -e "  ${GREEN}${framework}.xcframework resolved${NC}"
    done

    rm -rf "$build_dir"
    write_cache "$name" "$version"
}

# --- Manual Mode ---

check_manual() {
    local lib_idx=$1
    local name note
    name=$(manifest_lib_field "$lib_idx" name)
    note=$(manifest_lib_field "$lib_idx" note "Place xcframework in .libraries/$name/")

    local lib_dir="$LIBRARIES_DIR/$name"
    local prod_count all_present=true
    prod_count=$(manifest_product_count "$lib_idx")

    for ((j=0; j<prod_count; j++)); do
        local framework
        framework=$(manifest_product_field "$lib_idx" "$j" framework)
        if [[ ! -d "$lib_dir/${framework}.xcframework" ]]; then
            all_present=false
            echo -e "  ${YELLOW}$framework: missing${NC}"
            echo -e "  ${DIM}$note${NC}"
        else
            echo -e "  ${GREEN}$framework: present${NC}"
        fi
    done

    if $all_present; then
        write_cache "$name" "manual"
    fi
}

# --- Main ---

echo -e "${BOLD}=== Library Fetch ===${NC}"
echo ""

LIB_COUNT=$(manifest_lib_count)
PUBLIC_COUNT=0
MANUAL_COUNT=0
FETCHED=0
CACHED=0
FAILED=0
SKIPPED=0

for ((i=0; i<LIB_COUNT; i++)); do
    NAME=$(manifest_lib_field "$i" name)
    MODE=$(manifest_lib_field "$i" mode)
    VERSION=$(manifest_lib_field "$i" version "manual")

    if ! matches_filter "$NAME"; then
        continue
    fi

    if [[ "$MODE" == "manual" ]]; then
        MANUAL_COUNT=$((MANUAL_COUNT + 1))
    else
        PUBLIC_COUNT=$((PUBLIC_COUNT + 1))
    fi

    # --- List mode ---
    if $LIST_ONLY; then
        if is_cached "$NAME" "$VERSION"; then
            echo -e "  ${GREEN}$NAME: cached ($VERSION)${NC}"
        elif [[ "$MODE" == "manual" ]]; then
            local_lib_dir="$LIBRARIES_DIR/$NAME"
            if [[ -d "$local_lib_dir" ]] && ls "$local_lib_dir"/*.xcframework >/dev/null 2>&1; then
                echo -e "  ${GREEN}$NAME: present (manual)${NC}"
            else
                echo -e "  ${YELLOW}$NAME: missing (manual)${NC}"
            fi
        else
            echo -e "  ${DIM}$NAME: not fetched ($MODE, $VERSION)${NC}"
        fi
        continue
    fi

    # --- Check cache ---
    if is_cached "$NAME" "$VERSION"; then
        echo -e "  ${DIM}$NAME: cached${NC}"
        CACHED=$((CACHED + 1))
        continue
    fi

    echo -e "${BOLD}$NAME${NC} ($MODE, $VERSION)"
    mkdir -p "$LIBRARIES_DIR/$NAME"

    case "$MODE" in
        source)
            if build_source "$i"; then
                FETCHED=$((FETCHED + 1))
            else
                FAILED=$((FAILED + 1))
                echo -e "  ${RED}Failed to build $NAME${NC}"
            fi
            ;;
        binary)
            if build_binary "$i"; then
                FETCHED=$((FETCHED + 1))
            else
                FAILED=$((FAILED + 1))
                echo -e "  ${RED}Failed to resolve $NAME${NC}"
            fi
            ;;
        manual)
            check_manual "$i"
            SKIPPED=$((SKIPPED + 1))
            ;;
    esac

    echo ""
done

echo -e "${BOLD}=== Summary ===${NC}"
if $LIST_ONLY; then
    echo -e "  Public: $PUBLIC_COUNT libraries"
    echo -e "  Manual: $MANUAL_COUNT libraries"
else
    [[ $FETCHED -gt 0 ]] && echo -e "  ${GREEN}Fetched: $FETCHED${NC}"
    [[ $CACHED -gt 0 ]] && echo -e "  ${DIM}Cached: $CACHED${NC}"
    [[ $SKIPPED -gt 0 ]] && echo -e "  ${DIM}Skipped (manual): $SKIPPED${NC}"
    [[ $FAILED -gt 0 ]] && echo -e "  ${RED}Failed: $FAILED${NC}"
fi
echo ""

[[ $FAILED -eq 0 ]] || exit 1
