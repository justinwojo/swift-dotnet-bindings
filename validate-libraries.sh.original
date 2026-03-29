#!/bin/bash
# validate-libraries.sh — Manifest-driven library validation for swift-bindings generator.
#
# Reads validation-libraries.json, generates C# bindings for each library product,
# compiles them, and tracks results against a baseline for regression detection.
#
# Usage:
#   ./validate-libraries.sh                         # All tiers compile gate (default)
#   ./validate-libraries.sh --tier 2                # Tier 2 only
#   ./validate-libraries.sh --tier all              # Both tiers
#   ./validate-libraries.sh --quick                 # Reuse existing /tmp output
#   ./validate-libraries.sh --filter Nuke           # Only matching libraries
#   ./validate-libraries.sh --verbose               # Show errors detail
#   ./validate-libraries.sh --fetch                 # Run fetch script first
#   ./validate-libraries.sh --jobs 4                  # Limit to 4 parallel workers
#   ./validate-libraries.sh --serial                  # Run sequentially (no parallelism)
#   ./validate-libraries.sh --tier 2 --filter SVGView --verbose # Combine flags

set -o pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"

source "$SCRIPT_DIR/scripts/lib.sh"

MANIFEST="$SCRIPT_DIR/validation-libraries.json"
PROJ="$SCRIPT_DIR/src/Swift.Bindings/src/Swift.Bindings.csproj"
# Use branch-specific temp dir so parallel worktrees don't clobber each other
_BRANCH=$(git -C "$SCRIPT_DIR" rev-parse --abbrev-ref HEAD 2>/dev/null | tr '/' '-')
OUTPUT_BASE="/tmp/binding-validation-${_BRANCH:-default}"
BASELINE_FILE="$SCRIPT_DIR/.validation-baseline.json"
LIBRARIES_DIR="$SCRIPT_DIR/.libraries"
RESULTS_DIR=$(mktemp -d)
trap "rm -rf $RESULTS_DIR" EXIT

# --- Flags ---
QUICK=false
FILTER=""
VERBOSE=false
FETCH=false
TIER="all"
JOBS=""

while [[ $# -gt 0 ]]; do
    case $1 in
        --quick) QUICK=true; shift ;;
        --filter) FILTER="$2"; shift 2 ;;
        --verbose) VERBOSE=true; shift ;;
        --fetch) FETCH=true; shift ;;
        --jobs) JOBS="$2"; shift 2 ;;
        --serial) JOBS=1; shift ;;
        --tier)
            case "$2" in
                1|2|all) TIER="$2"; shift 2 ;;
                *) echo "Invalid tier: $2 (must be 1, 2, or all)"; exit 1 ;;
            esac
            ;;
        -h|--help)
            echo "Usage: ./validate-libraries.sh [flags]"
            echo ""
            echo "Flags:"
            echo "  --tier <1|2|all>  Library tier to validate (default: all)"
            echo "                      1 = established libraries (32 targets)"
            echo "                      2 = additional coverage libraries (21 targets)"
            echo "                      all = both tiers"
            echo "  --quick           Recompile from existing cache (branch-specific /tmp/ dir)"
            echo "  --filter <pat>    Only libraries matching pattern (case-insensitive)"
            echo "  --verbose         Show generator warnings and first 10 compile errors"
            echo "  --fetch           Run scripts/fetch-libraries.sh before validating"
            echo "  --jobs <N>        Max parallel workers (default: auto-detected from CPU cores)"
            echo "  --serial          Run sequentially (equivalent to --jobs 1)"
            echo "  -h, --help        Show this help"
            exit 0
            ;;
        *) echo "Unknown flag: $1"; exit 1 ;;
    esac
done

# --- Helper Functions ---

set_result() { echo "$3" > "$RESULTS_DIR/$1.$2"; }
get_result() { cat "$RESULTS_DIR/$1.$2" 2>/dev/null || echo "${3:-}"; }

detect_max_jobs() {
    local cores
    if command -v nproc &>/dev/null; then
        cores=$(nproc)
    elif command -v sysctl &>/dev/null; then
        cores=$(sysctl -n hw.ncpu 2>/dev/null || echo 4)
    else
        cores=4
    fi
    # Leave headroom to avoid excessive contention (dotnet processes are CPU-heavy)
    local jobs=$(( cores > 4 ? cores - 2 : (cores > 1 ? cores : 1) ))
    (( jobs > 16 )) && jobs=16
    echo "$jobs"
}

get_runtime_version() {
    grep -o 'DefaultSwiftRuntimeVersion = "[^"]*"' \
        "$SCRIPT_DIR/src/Swift.Bindings/src/Emitter/BindingProjectEmitter.cs" \
        | grep -o '"[^"]*"' | tr -d '"'
}

# Map platform to TFM and min OS version
platform_to_tfm() {
    case "${1:-ios}" in
        macos) echo "net10.0-macos" ;;
        tvos) echo "net10.0-tvos" ;;
        maccatalyst) echo "net10.0-maccatalyst" ;;
        *) echo "net10.0-ios" ;;
    esac
}

platform_to_min_os() {
    case "${1:-ios}" in
        macos) echo "12.0" ;;
        *) echo "15.0" ;;
    esac
}

platform_to_runtime_tfm() {
    case "${1:-ios}" in
        macos) echo "net10.0-macos" ;;
        tvos) echo "net10.0-tvos" ;;
        maccatalyst) echo "net10.0-maccatalyst" ;;
        *) echo "net10.0-ios" ;;
    esac
}

platform_to_package_suffix() {
    case "${1:-ios}" in
        macos) echo "macOS" ;;
        tvos) echo "tvOS" ;;
        maccatalyst) echo "MacCatalyst" ;;
        *) echo "iOS" ;;
    esac
}

check_swift_wrapper() {
    local outdir="$1"
    # Only count wrapper .swift files — exclude .SwiftUIBridge.swift which the
    # generator emits separately and never compiles as part of the wrapper.
    local swift_file
    swift_file=$(find "$outdir" -maxdepth 1 -name "*.swift" -not -name "*.SwiftUIBridge.swift" -type f 2>/dev/null | head -1)
    if [[ -z "$swift_file" ]]; then
        echo "no_wrapper"
        return
    fi
    local wrapper_binary
    wrapper_binary=$(find "$outdir" -path "*SwiftBindings.framework/*SwiftBindings" -not -name "*.plist" -type f 2>/dev/null | head -1)
    if [[ -n "$wrapper_binary" ]]; then
        echo "ok"
    else
        echo "fail"
    fi
}

write_fallback_csproj() {
    local outdir="$1"
    local platform="${2:-ios}"
    local cs_file
    cs_file=$(ls "$outdir"/*.cs 2>/dev/null | grep -v '\.Wrappers\.cs' | grep -v '\.SwiftUIBridge\.cs' | head -1)
    [[ -z "$cs_file" ]] && return 1
    local cs_basename
    cs_basename=$(basename "$cs_file")

    local tfm
    tfm=$(platform_to_tfm "$platform")
    local min_os
    min_os=$(platform_to_min_os "$platform")
    local runtime_tfm
    runtime_tfm=$(platform_to_runtime_tfm "$platform")
    local runtime_dll="$SCRIPT_DIR/src/Swift.Runtime/src/bin/Debug/$runtime_tfm/Swift.Runtime.dll"
    cat > "$outdir/Test.csproj" <<CSPROJ
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Library</OutputType>
    <TargetFramework>$tfm</TargetFramework>
    <SupportedOSPlatformVersion>$min_os</SupportedOSPlatformVersion>
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
    <Nullable>enable</Nullable>
    <NoWarn>0169;CA1420</NoWarn>
  </PropertyGroup>
  <ItemGroup>
    <AssemblyAttribute Include="System.Runtime.CompilerServices.DisableRuntimeMarshallingAttribute" />
  </ItemGroup>
  <ItemGroup>
    <Reference Include="Swift.Runtime">
      <HintPath>$runtime_dll</HintPath>
    </Reference>
  </ItemGroup>
  <ItemGroup>
    <Compile Include="$cs_basename" />
  </ItemGroup>
</Project>
CSPROJ
}

# --- Resolve parallel job count ---

if [[ -n "$JOBS" ]]; then
    if ! [[ "$JOBS" =~ ^[1-9][0-9]*$ ]]; then
        echo "Invalid --jobs value: $JOBS (must be a positive integer)"
        exit 1
    fi
    MAX_JOBS=$JOBS
else
    MAX_JOBS=$(detect_max_jobs)
fi

# --- Phase 1: Prerequisites ---

echo -e "${BOLD}=== Library Validation ===${NC}"
echo ""

if [[ ! -f "$MANIFEST" ]]; then
    echo -e "${RED}ERROR: Manifest not found: $MANIFEST${NC}"
    exit 1
fi

if [[ ! -f "$PROJ" ]]; then
    echo -e "${RED}ERROR: Generator project not found: $PROJ${NC}"
    exit 1
fi

RUNTIME_VERSION=$(get_runtime_version)
if [[ -z "$RUNTIME_VERSION" ]]; then
    echo -e "${RED}ERROR: Could not extract DefaultSwiftRuntimeVersion${NC}"
    exit 1
fi
echo -e "${DIM}Runtime version: $RUNTIME_VERSION${NC}"
echo -e "${DIM}Git SHA: $(git -C "$SCRIPT_DIR" rev-parse --short HEAD)${NC}"
echo -e "${DIM}Tier: $TIER${NC}"
echo -e "${DIM}Workers: $MAX_JOBS${NC}"

# --- Fetch if requested ---

if $FETCH; then
    echo ""
    FETCH_ARGS=""
    [[ -n "$FILTER" ]] && FETCH_ARGS="--filter $FILTER"
    if ! "$SCRIPT_DIR/scripts/fetch-libraries.sh" $FETCH_ARGS; then
        echo -e "${RED}Fetch failed — aborting validation${NC}"
        exit 1
    fi
    echo ""
fi

# --- Check .libraries/ exists (skip for --quick which uses cached /tmp output) ---

if ! $QUICK && [[ ! -d "$LIBRARIES_DIR" ]]; then
    echo -e "${RED}ERROR: .libraries/ not found. Run first:${NC}"
    echo "  scripts/fetch-libraries.sh"
    exit 1
fi

# --- Compute which frameworks have declared dependencies ---

HAS_DEPS_SET="|$(python3 -c "
import json
libs = json.load(open('$MANIFEST'))['libraries']
for lib in libs:
    for prod in lib['products']:
        if prod.get('dependencies'):
            print(prod['framework'])
" 2>/dev/null | tr '\n' '|')"

# --- Expand manifest to validation targets ---

TARGETS=()
MANUAL_TARGETS=()

while IFS='|' read -r fw lib_name xcfw_path mode known_errors tier platform; do
    # Filter uses base framework name (strip @platform suffix for matching)
    local_fw_base="${fw%%@*}"
    if ! matches_filter "$local_fw_base"; then
        continue
    fi
    if ! matches_tier "$tier"; then
        continue
    fi
    has_deps=0
    [[ "$HAS_DEPS_SET" == *"|${local_fw_base}|"* ]] && has_deps=1
    if $QUICK; then
        # In --quick mode, don't require xcframeworks — we use cached /tmp output
        TARGETS+=("$fw|$lib_name|$xcfw_path|$mode|$known_errors|$has_deps|$platform")
    elif [[ "$mode" == "manual" ]]; then
        if [[ -d "$xcfw_path" ]]; then
            TARGETS+=("$fw|$lib_name|$xcfw_path|$mode|$known_errors|$has_deps|$platform")
        else
            MANUAL_TARGETS+=("$fw")
        fi
    else
        if [[ -d "$xcfw_path" ]]; then
            TARGETS+=("$fw|$lib_name|$xcfw_path|$mode|$known_errors|$has_deps|$platform")
        else
            echo -e "  ${YELLOW}$fw: xcframework not found — skipping${NC}"
        fi
    fi
done < <(manifest_expand_targets "$LIBRARIES_DIR")

if [[ ${#TARGETS[@]} -eq 0 ]]; then
    if [[ ${#MANUAL_TARGETS[@]} -gt 0 ]]; then
        echo -e "${YELLOW}All matching targets are manual and missing xcframeworks:${NC}"
        echo -e "${DIM}  ${MANUAL_TARGETS[*]}${NC}"
    elif [[ -n "$FILTER" ]]; then
        echo -e "${YELLOW}No libraries match filter: $FILTER${NC}"
    else
        echo -e "${RED}No libraries available. Run: scripts/fetch-libraries.sh${NC}"
    fi
    exit 0
fi

TOTAL_TARGETS=${#TARGETS[@]}
MANUAL_MISSING=${#MANUAL_TARGETS[@]}
echo -e "${DIM}Targets: $TOTAL_TARGETS${NC}"
if [[ $MANUAL_MISSING -gt 0 ]]; then
    echo -e "${DIM}Manual (missing): $MANUAL_MISSING (${MANUAL_TARGETS[*]})${NC}"
fi
echo ""

# --- Phase 2: Build Generator ---

GENERATOR_DLL="$SCRIPT_DIR/src/Swift.Bindings/src/bin/Debug/net10.0/Swift.Bindings.dll"
BUILD_STAMP="$OUTPUT_BASE/.build-stamp"

if ! $QUICK; then
    # Fingerprint generator + runtime sources to skip build when unchanged
    BUILD_FINGERPRINT=$(find "$SCRIPT_DIR/src/Swift.Bindings/src" "$SCRIPT_DIR/src/Swift.Runtime/src" \
        \( -name '*.cs' -o -name '*.csproj' -o -name '*.props' -o -name '*.targets' \) \
        -not -path '*/bin/*' -not -path '*/obj/*' | sort | xargs cat 2>/dev/null | shasum -a 256 | cut -d' ' -f1)

    if [[ -f "$BUILD_STAMP" ]] && [[ "$(cat "$BUILD_STAMP" 2>/dev/null)" == "$BUILD_FINGERPRINT" ]] && [[ -f "$GENERATOR_DLL" ]]; then
        echo -e "${DIM}Generator unchanged — skipping build${NC}"
    else
        echo -e "${BOLD}--- Building generator + runtime ---${NC}"
        if dotnet build "$SCRIPT_DIR/src/Swift.Bindings/src/Swift.Bindings.csproj" -v quiet 2>&1 | tail -3 \
           && dotnet build "$SCRIPT_DIR/src/Swift.Runtime/src/Swift.Runtime.csproj" -v quiet 2>&1 | tail -3; then
            echo -e "${GREEN}Generator built${NC}"
            mkdir -p "$OUTPUT_BASE"
            echo "$BUILD_FINGERPRINT" > "$BUILD_STAMP"
        else
            echo -e "${RED}Generator build failed${NC}"
            exit 1
        fi
    fi
    echo ""
fi

# --- Phase 3: Generate, Compile Wrappers, and Compile C# ---

echo -e "${BOLD}--- Binding Pipeline ---${NC}"

if $QUICK; then
    if [[ -f "$BASELINE_FILE" ]]; then
        BASELINE_SHA=$(python3 -c "import json; d=json.load(open('$BASELINE_FILE')); print(d.get('git_sha',''))" 2>/dev/null)
        CURRENT_SHA=$(git -C "$SCRIPT_DIR" rev-parse --short HEAD)
        if [[ "$BASELINE_SHA" != "$CURRENT_SHA" ]]; then
            echo -e "${YELLOW}WARNING: Generator changed since last run ($BASELINE_SHA -> $CURRENT_SHA) — results may be stale${NC}"
        fi
    fi
    if [[ ! -d "$OUTPUT_BASE" ]]; then
        echo -e "${RED}No existing output at $OUTPUT_BASE — run without --quick first${NC}"
        exit 1
    fi
fi

# --- Build framework-to-library-name mapping for dependency resolution ---
# Maps framework names to their parent library directory names (for xcframework paths)
FW_TO_LIB=$(python3 -c "
import json
libs = json.load(open('$MANIFEST'))['libraries']
for lib in libs:
    for prod in lib['products']:
        print(f\"{prod['framework']}|{lib['name']}\")
" 2>/dev/null)

# Build framework-to-dependencies mapping (C# compile gate)
FW_DEPS=$(python3 -c "
import json
libs = json.load(open('$MANIFEST'))['libraries']
for lib in libs:
    for prod in lib['products']:
        deps = prod.get('dependencies', [])
        if deps:
            print(f\"{prod['framework']}|{','.join(deps)}\")
" 2>/dev/null)

# Build framework-to-wrapper-deps mapping (includes both dependencies + wrapper_deps for swift compilation)
WRAPPER_DEPS=$(python3 -c "
import json
libs = json.load(open('$MANIFEST'))['libraries']
for lib in libs:
    for prod in lib['products']:
        deps = list(prod.get('dependencies', [])) + list(prod.get('wrapper_deps', []))
        if deps:
            print(f\"{prod['framework']}|{','.join(deps)}\")
" 2>/dev/null)

# --- Phase 3a: Generate All Bindings (parallel) ---

generate_target() {
    local entry="$1"
    IFS='|' read -r name lib_name xcfw_path mode known_errors has_deps platform <<< "$entry"
    local outdir="$OUTPUT_BASE/$name"
    local output_file="$RESULTS_DIR/$name.gen_output"
    local GEN_VERBOSE=""
    platform="${platform:-ios}"

    if ! $QUICK; then
        rm -rf "$outdir"
        mkdir -p "$outdir"
        local GEN_START=$SECONDS
        local GEN_OUTPUT GEN_EXIT
        local GEN_VERBOSITY=0
        $VERBOSE && GEN_VERBOSITY=1
        GEN_OUTPUT=$(dotnet "$GENERATOR_DLL" --skip-wrapper-compilation --xcframework "$xcfw_path" -o "$outdir" --platform "$platform" -v $GEN_VERBOSITY 2>&1)
        GEN_EXIT=$?
        if [[ $GEN_EXIT -eq 0 ]] && ls "$outdir"/*.cs 2>/dev/null | grep -qv '\.Wrappers\.cs\|\.SwiftUIBridge\.cs'; then
            set_result "$name" gen "ok"
        else
            set_result "$name" gen "fail"
            if $VERBOSE; then
                GEN_VERBOSE=$(echo "$GEN_OUTPUT" | tail -5)
            fi
        fi
        set_result "$name" seconds $(( SECONDS - GEN_START ))
    else
        if [[ -d "$outdir" ]]; then
            set_result "$name" gen "cached"
            set_result "$name" seconds 0
        else
            set_result "$name" gen "missing"
            set_result "$name" compile "skip"
            set_result "$name" errors 0
            set_result "$name" lines 0
            set_result "$name" swift_compile "unknown"
            echo -e "  ${YELLOW}$name: no cached output${NC}" > "$output_file"
            return
        fi
    fi

    # Count generated lines
    local CS_FILE LINES
    CS_FILE=$(ls "$outdir"/*.cs 2>/dev/null | grep -v '\.Wrappers\.cs' | grep -v '\.SwiftUIBridge\.cs' | head -1)
    LINES=0
    [[ -n "$CS_FILE" ]] && LINES=$(wc -l < "$CS_FILE" | tr -d ' ')
    set_result "$name" lines "$LINES"

    local GEN_SECS
    GEN_SECS=$(get_result "$name" seconds 0)

    # Format generation result
    {
        if [[ -n "$GEN_VERBOSE" ]]; then
            echo "$GEN_VERBOSE" | while IFS= read -r line; do
                echo -e "    ${DIM}$line${NC}"
            done
        fi
        local gen_status
        gen_status=$(get_result "$name" gen "unknown")
        if [[ "$gen_status" == "ok" || "$gen_status" == "cached" ]]; then
            echo -e "  ${GREEN}$name: generated${NC} ${DIM}(${LINES} lines, ${GEN_SECS}s)${NC}"
        else
            echo -e "  ${RED}$name: gen failed${NC} ${DIM}(${GEN_SECS}s)${NC}"
        fi
    } > "$output_file"
}

# --- Phase 3b: Compile Swift Wrappers (parallel) ---

compile_wrapper() {
    local entry="$1"
    IFS='|' read -r name lib_name xcfw_path mode known_errors has_deps platform <<< "$entry"
    local outdir="$OUTPUT_BASE/$name"
    local output_file="$RESULTS_DIR/$name.swift_output"
    platform="${platform:-ios}"

    # Skip if generation failed or missing
    local gen_status
    gen_status=$(get_result "$name" gen "unknown")
    if [[ "$gen_status" != "ok" && "$gen_status" != "cached" ]]; then
        set_result "$name" swift_compile "unknown"
        return
    fi

    if ! $QUICK; then
        # Build the --compile-wrapper-only command with dependency framework paths
        local CMD_ARGS=("--compile-wrapper-only" "--xcframework" "$xcfw_path" "-o" "$outdir" "--platform" "$platform")
        local GEN_VERBOSITY=0
        $VERBOSE && GEN_VERBOSITY=1
        CMD_ARGS+=("-v" "$GEN_VERBOSITY")

        # Look up framework name (strip @platform suffix for mapping)
        local fw_base="${name%%@*}"

        # Add --framework-dependency flags for each declared dependency, then add
        # all sibling xcframeworks from the same library directory to resolve transitive
        # imports (e.g., Stripe modules importing Stripe3DS2/StripeUICore transitively,
        # CocoaLumberjackSwift importing CocoaLumberjack). Duplicates are tracked to
        # avoid passing the same xcframework twice (which would error in the generator).
        local added_deps="|"
        local dep_list=""
        while IFS='|' read -r dep_fw dep_fws; do
            [[ "$dep_fw" == "$fw_base" ]] && dep_list="$dep_fws"
        done <<< "$WRAPPER_DEPS"

        if [[ -n "$dep_list" ]]; then
            IFS=',' read -ra DEP_NAMES <<< "$dep_list"
            for dep_fw_name in "${DEP_NAMES[@]}"; do
                # Look up library name for this dependency framework
                local dep_lib_name=""
                while IFS='|' read -r map_fw map_lib; do
                    [[ "$map_fw" == "$dep_fw_name" ]] && dep_lib_name="$map_lib"
                done <<< "$FW_TO_LIB"
                if [[ -n "$dep_lib_name" ]]; then
                    local dep_xcfw="$LIBRARIES_DIR/$dep_lib_name/$dep_fw_name.xcframework"
                    if [[ -d "$dep_xcfw" ]]; then
                        CMD_ARGS+=("--framework-dependency" "$dep_xcfw")
                        added_deps="${added_deps}$(basename "$dep_xcfw")|"
                    fi
                fi
            done
        fi

        # Add sibling xcframeworks (same library directory) for transitive deps
        local lib_dir
        lib_dir=$(dirname "$xcfw_path")
        local self_xcfw
        self_xcfw=$(basename "$xcfw_path")
        if [[ -d "$lib_dir" ]]; then
            for sibling_xcfw in "$lib_dir"/*.xcframework; do
                [[ ! -d "$sibling_xcfw" ]] && continue
                local sibling_base
                sibling_base=$(basename "$sibling_xcfw")
                [[ "$sibling_base" == "$self_xcfw" ]] && continue
                [[ "$added_deps" == *"|${sibling_base}|"* ]] && continue
                CMD_ARGS+=("--framework-dependency" "$sibling_xcfw")
                added_deps="${added_deps}${sibling_base}|"
            done
        fi

        local WRAPPER_OUTPUT WRAPPER_EXIT
        WRAPPER_OUTPUT=$(dotnet "$GENERATOR_DLL" "${CMD_ARGS[@]}" 2>&1)
        WRAPPER_EXIT=$?

        # Check swift wrapper status
        local SWIFT_STATUS
        SWIFT_STATUS=$(check_swift_wrapper "$outdir")
        set_result "$name" swift_compile "$SWIFT_STATUS"

        # Capture Swift error lines when verbose + fail
        local SWIFT_ERRORS=""
        if $VERBOSE && [[ "$SWIFT_STATUS" == "fail" ]]; then
            SWIFT_ERRORS=$(echo "$WRAPPER_OUTPUT" | grep -E '\.swift:[0-9]+:[0-9]+: error:' | head -5)
        fi

        # Format swift wrapper result
        {
            if [[ -n "$SWIFT_ERRORS" ]]; then
                echo "$SWIFT_ERRORS" | while IFS= read -r line; do
                    echo -e "    ${RED}$line${NC}"
                done
            fi
            case "$SWIFT_STATUS" in
                ok) echo -e "  ${GREEN}$name: [swift:ok]${NC}" ;;
                fail) echo -e "  ${RED}$name: [swift:fail]${NC}" ;;
                no_wrapper) echo -e "  ${DIM}$name: [no wrapper]${NC}" ;;
            esac
        } > "$output_file"
    else
        # --quick mode: check cached wrapper status
        local SWIFT_STATUS
        SWIFT_STATUS=$(check_swift_wrapper "$outdir")
        set_result "$name" swift_compile "$SWIFT_STATUS"
    fi
}

# --- Phase 3c: C# Compile (parallel for non-dep, cascading for dep) ---

compile_target() {
    local entry="$1"
    IFS='|' read -r name lib_name xcfw_path mode known_errors has_deps platform <<< "$entry"
    local outdir="$OUTPUT_BASE/$name"
    local output_file="$RESULTS_DIR/$name.compile_output"
    platform="${platform:-ios}"

    # Skip if generation failed or missing
    local gen_status
    gen_status=$(get_result "$name" gen "unknown")
    if [[ "$gen_status" != "ok" && "$gen_status" != "cached" ]]; then
        set_result "$name" compile "skip"
        set_result "$name" errors 0
        return
    fi

    # Find .csproj to compile — prefer platform-specific .Swift.{Platform}.csproj for mixed frameworks
    local CSPROJ_FILE=""
    local pkg_suffix
    pkg_suffix=$(platform_to_package_suffix "$platform")
    if ls "$outdir"/*.Swift.${pkg_suffix}.csproj >/dev/null 2>&1; then
        CSPROJ_FILE=$(ls "$outdir"/*.Swift.${pkg_suffix}.csproj | head -1)
    elif ls "$outdir"/*.csproj >/dev/null 2>&1; then
        CSPROJ_FILE=$(ls "$outdir"/*.csproj | grep -v 'Test.csproj\|_dep_test.csproj' | head -1)
    fi

    # Fallback .csproj when wrapper compilation fails
    if [[ -z "$CSPROJ_FILE" ]] && ls "$outdir"/*.cs 2>/dev/null | grep -qv '\.Wrappers\.cs\|\.SwiftUIBridge\.cs'; then
        write_fallback_csproj "$outdir" "$platform"
        CSPROJ_FILE="$outdir/Test.csproj"
    fi

    if [[ -z "$CSPROJ_FILE" ]]; then
        set_result "$name" compile "no_csproj"
        set_result "$name" errors 0
        echo -e "  ${YELLOW}$name: no .csproj generated${NC}" > "$output_file"
        return
    fi

    # Patch .csproj to use local Swift.Runtime DLL
    local RUNTIME_TFM
    RUNTIME_TFM=$(platform_to_runtime_tfm "$platform")
    local RUNTIME_DLL="$SCRIPT_DIR/src/Swift.Runtime/src/bin/Debug/$RUNTIME_TFM/Swift.Runtime.dll"
    if grep -q 'PackageReference.*SwiftBindings\.Runtime' "$CSPROJ_FILE" 2>/dev/null; then
        sed -i '' 's|<PackageReference Include="SwiftBindings.Runtime"[^/]*/>|<Reference Include="Swift.Runtime"><HintPath>'"$RUNTIME_DLL"'</HintPath></Reference>|' "$CSPROJ_FILE"
    elif grep -q 'PackageReference.*Swift\.Runtime' "$CSPROJ_FILE" 2>/dev/null; then
        sed -i '' 's|<PackageReference Include="Swift.Runtime"[^/]*/>|<Reference Include="Swift.Runtime"><HintPath>'"$RUNTIME_DLL"'</HintPath></Reference>|' "$CSPROJ_FILE"
    fi

    # Restore if no assets file (fallback csproj needs this)
    if [[ ! -f "$outdir/obj/project.assets.json" ]]; then
        dotnet restore "$CSPROJ_FILE" -v quiet 2>/dev/null
    fi

    # Compile
    local BUILD_OUTPUT BUILD_EXIT ERRORS
    BUILD_OUTPUT=$(dotnet build "$CSPROJ_FILE" -p:EnableDefaultCompileItems=false --no-restore -v quiet 2>&1)
    BUILD_EXIT=$?
    ERRORS=$(echo "$BUILD_OUTPUT" | grep "error CS" | sort -u | wc -l | tr -d ' ')

    # Detect non-CS build failures (e.g., NETSDK1004, MSB errors)
    if [[ $BUILD_EXIT -ne 0 && $ERRORS -eq 0 ]]; then
        local INFRA_ERRORS
        INFRA_ERRORS=$(echo "$BUILD_OUTPUT" | grep -i "error " | grep -v "error CS" | head -1)
        if [[ -n "$INFRA_ERRORS" ]]; then
            set_result "$name" compile "infra_fail"
            set_result "$name" errors 0
            {
                echo -e "  ${RED}$name: build infrastructure failure${NC}"
                echo -e "    ${DIM}$INFRA_ERRORS${NC}"
            } > "$output_file"
            return
        fi
    fi
    set_result "$name" errors "$ERRORS"

    local GEN_SECS EXPECTED_ERRORS LINES
    GEN_SECS=$(get_result "$name" seconds 0)
    EXPECTED_ERRORS=$known_errors
    LINES=$(get_result "$name" lines 0)

    # Swift wrapper status marker
    local swift_marker=""
    local sw_status
    sw_status=$(get_result "$name" swift_compile "unknown")
    case "$sw_status" in
        ok) swift_marker=" ${GREEN}[swift:ok]${NC}" ;;
        fail) swift_marker=" ${RED}[swift:fail]${NC}" ;;
        no_wrapper) swift_marker="" ;;
    esac

    # Format compile result output (buffered to file for ordered display)
    {
        if [[ $ERRORS -eq 0 ]]; then
            set_result "$name" compile "ok"
            echo -e "  ${GREEN}$name: OK${NC}${swift_marker} ${DIM}(${LINES} lines, ${GEN_SECS}s)${NC}"
        elif [[ $EXPECTED_ERRORS -gt 0 && $ERRORS -le $EXPECTED_ERRORS ]]; then
            set_result "$name" compile "known_errors"
            echo -e "  ${YELLOW}$name: $ERRORS errors (known, expected $EXPECTED_ERRORS)${NC}${swift_marker} ${DIM}(${LINES} lines, ${GEN_SECS}s)${NC}"
        elif [[ $EXPECTED_ERRORS -gt 0 && $ERRORS -gt $EXPECTED_ERRORS ]]; then
            set_result "$name" compile "regressed"
            echo -e "  ${RED}$name: $ERRORS errors (expected $EXPECTED_ERRORS — REGRESSED)${NC}${swift_marker} ${DIM}(${LINES} lines)${NC}"
            if $VERBOSE; then
                echo "$BUILD_OUTPUT" | grep "error CS" | head -10 | while IFS= read -r line; do
                    echo -e "    ${DIM}$line${NC}"
                done
                [[ $ERRORS -gt 10 ]] && echo -e "    ${DIM}... and $((ERRORS - 10)) more${NC}"
            fi
        else
            set_result "$name" compile "fail"
            echo -e "  ${RED}$name: $ERRORS errors${NC}${swift_marker} ${DIM}(${LINES} lines)${NC}"
            if $VERBOSE; then
                echo "$BUILD_OUTPUT" | grep "error CS" | head -10 | while IFS= read -r line; do
                    echo -e "    ${DIM}$line${NC}"
                done
                [[ $ERRORS -gt 10 ]] && echo -e "    ${DIM}... and $((ERRORS - 10)) more${NC}"
            fi
        fi
    } > "$output_file"
}

# --- Sort targets longest-first for optimal scheduling ---
# Start slow targets first so fast targets fill in around them.
# Uses baseline lines (stable proxy for gen time) when available, falls back to 0.
# DISPLAY_TARGETS preserves manifest order for output.

DISPLAY_TARGETS=("${TARGETS[@]}")

if [[ -f "$BASELINE_FILE" ]] && (( MAX_JOBS > 1 )); then
    SORT_TMPFILE=$(mktemp)
    printf '%s\n' "${TARGETS[@]}" > "$SORT_TMPFILE"

    SORTED_TARGETS=()
    while IFS= read -r entry; do
        SORTED_TARGETS+=("$entry")
    done < <(python3 -c "
import json
with open('$BASELINE_FILE') as f:
    bl = json.load(f)
libs = bl.get('compile_gate', {}).get('libraries', {})
with open('$SORT_TMPFILE') as f:
    entries = [line.rstrip('\n') for line in f if line.strip()]
entries.sort(key=lambda e: libs.get(e.split('|')[0], {}).get('lines', 0), reverse=True)
for entry in entries:
    print(entry)
" 2>/dev/null)
    rm -f "$SORT_TMPFILE"

    if [[ ${#SORTED_TARGETS[@]} -gt 0 ]]; then
        TARGETS=("${SORTED_TARGETS[@]}")
    fi
fi

# --- Phase 3a: Parallel dispatch — Generate All Bindings ---

echo -e "${DIM}Phase 3a: Generating $TOTAL_TARGETS targets with $MAX_JOBS parallel workers...${NC}"
PHASE3A_START=$SECONDS

for entry in "${TARGETS[@]}"; do
    generate_target "$entry" &
    while (( $(jobs -rp | wc -l) >= MAX_JOBS )); do
        sleep 0.1
    done
done
wait

echo -e "${DIM}Phase 3a completed in $((SECONDS - PHASE3A_START))s${NC}"

# Display Phase 3a results
for entry in "${DISPLAY_TARGETS[@]}"; do
    IFS='|' read -r name _ <<< "$entry"
    [[ -f "$RESULTS_DIR/$name.gen_output" ]] && cat "$RESULTS_DIR/$name.gen_output"
done

# --- Phase 3b: Parallel dispatch — Compile Swift Wrappers ---

echo ""
echo -e "${DIM}Phase 3b: Compiling Swift wrappers with $MAX_JOBS parallel workers...${NC}"
PHASE3B_START=$SECONDS

for entry in "${TARGETS[@]}"; do
    compile_wrapper "$entry" &
    while (( $(jobs -rp | wc -l) >= MAX_JOBS )); do
        sleep 0.1
    done
done
wait

echo -e "${DIM}Phase 3b completed in $((SECONDS - PHASE3B_START))s${NC}"

# Display Phase 3b results and compute swift counters
SWIFT_PASSED=0
SWIFT_FAILED=0
SWIFT_NO_WRAPPER=0

for entry in "${DISPLAY_TARGETS[@]}"; do
    IFS='|' read -r name _ <<< "$entry"
    [[ -f "$RESULTS_DIR/$name.swift_output" ]] && cat "$RESULTS_DIR/$name.swift_output"
    swift_status=$(get_result "$name" swift_compile "unknown")
    case "$swift_status" in
        ok) SWIFT_PASSED=$((SWIFT_PASSED + 1)) ;;
        fail) SWIFT_FAILED=$((SWIFT_FAILED + 1)) ;;
        no_wrapper) SWIFT_NO_WRAPPER=$((SWIFT_NO_WRAPPER + 1)) ;;
    esac
done

# Swift wrapper compilation summary
SWIFT_TESTED=$((SWIFT_PASSED + SWIFT_FAILED))
if [[ $SWIFT_TESTED -gt 0 ]]; then
    SWIFT_NOWRAP_NOTE=""
    (( SWIFT_NO_WRAPPER > 0 )) && SWIFT_NOWRAP_NOTE=" ${DIM}($SWIFT_NO_WRAPPER ObjC/no wrapper)${NC}"
    if [[ $SWIFT_FAILED -eq 0 ]]; then
        echo -e "${GREEN}Swift wrapper: $SWIFT_PASSED/$SWIFT_TESTED passed${NC}${SWIFT_NOWRAP_NOTE}"
    else
        echo -e "${RED}Swift wrapper: $SWIFT_PASSED/$SWIFT_TESTED passed, $SWIFT_FAILED failed${NC}${SWIFT_NOWRAP_NOTE}"
    fi
fi

# --- Phase 3c: C# Compile Gate ---
# Non-dep libraries: parallel standalone compile
# Dep libraries: cascading rounds with assembly references

echo ""
echo -e "${BOLD}--- Compile Gate ---${NC}"

# Split targets into non-dep and dep sets
NON_DEP_TARGETS=()
DEP_TARGET_ENTRIES=()
for entry in "${TARGETS[@]}"; do
    IFS='|' read -r name lib_name xcfw_path mode known_errors has_deps platform <<< "$entry"
    if [[ "$has_deps" == "1" ]]; then
        DEP_TARGET_ENTRIES+=("$entry")
    else
        NON_DEP_TARGETS+=("$entry")
    fi
done

# Phase 3c-standalone: Compile non-dep targets in parallel
if [[ ${#NON_DEP_TARGETS[@]} -gt 0 ]]; then
    echo -e "${DIM}Phase 3c: Compiling ${#NON_DEP_TARGETS[@]} standalone targets with $MAX_JOBS parallel workers...${NC}"
    PHASE3C_START=$SECONDS

    for entry in "${NON_DEP_TARGETS[@]}"; do
        compile_target "$entry" &
        while (( $(jobs -rp | wc -l) >= MAX_JOBS )); do
            sleep 0.1
        done
    done
    wait

    echo -e "${DIM}Phase 3c standalone completed in $((SECONDS - PHASE3C_START))s${NC}"
fi

# Display standalone compile results and compute counters
COMPILE_PASSED=0
COMPILE_FAILED=0
COMPILE_NO_OUTPUT=0

for entry in "${DISPLAY_TARGETS[@]}"; do
    IFS='|' read -r name _ _ _ _ has_deps _ <<< "$entry"
    # Only show non-dep results here; dep results shown in dep gate below
    [[ "$has_deps" == "1" ]] && continue
    [[ -f "$RESULTS_DIR/$name.compile_output" ]] && cat "$RESULTS_DIR/$name.compile_output"
    comp_status=$(get_result "$name" compile "unknown")
    case "$comp_status" in
        ok|known_errors) COMPILE_PASSED=$((COMPILE_PASSED + 1)) ;;
        fail|regressed|infra_fail) COMPILE_FAILED=$((COMPILE_FAILED + 1)) ;;
        *) COMPILE_NO_OUTPUT=$((COMPILE_NO_OUTPUT + 1)) ;;
    esac
done

COMPILE_TESTED=$((COMPILE_PASSED + COMPILE_FAILED + COMPILE_NO_OUTPUT))
if [[ $COMPILE_TESTED -gt 0 ]]; then
    echo ""
    if [[ $COMPILE_FAILED -eq 0 && $COMPILE_NO_OUTPUT -eq 0 ]]; then
        echo -e "${GREEN}Compile gate (standalone): $COMPILE_PASSED/$COMPILE_TESTED passed${NC}"
    elif [[ $COMPILE_FAILED -eq 0 ]]; then
        echo -e "${GREEN}Compile gate (standalone): $COMPILE_PASSED/$COMPILE_TESTED passed${NC} ${DIM}($COMPILE_NO_OUTPUT no output)${NC}"
    else
        echo -e "${RED}Compile gate (standalone): $COMPILE_PASSED/$COMPILE_TESTED passed, $COMPILE_FAILED failed${NC}${COMPILE_NO_OUTPUT:+ ${DIM}($COMPILE_NO_OUTPUT no output)${NC}}"
    fi
fi

# --- Phase 3c Dependency Gate (Cascading) ---

# Resolves cross-module type references by compiling libraries with assembly references
# to their dependencies. Computes transitive dependency closure so indirect deps are
# included. Runs in cascading rounds: each round's successful compilations produce DLLs
# that unlock the next round's compilations (e.g., StripeCore -> StripePayments -> Stripe).

DEP_PASSED=0
DEP_FAILED=0
DEP_SKIPPED=0
DEP_TOTAL=0

# Compute transitive dependency closures from manifest
DEP_CLOSURES=$(python3 -c "
import json
libs = json.load(open('$MANIFEST'))['libraries']
dep_map = {}
for lib in libs:
    platforms = lib.get('platforms', ['ios'])
    for prod in lib['products']:
        deps = prod.get('dependencies', [])
        if deps:
            for plat in platforms:
                target = prod['framework'] if plat == 'ios' else f\"{prod['framework']}@{plat}\"
                dep_targets = [d if plat == 'ios' else f'{d}@{plat}' for d in deps]
                dep_map[target] = dep_targets
def closure(fw, seen=None):
    if seen is None:
        seen = set()
    for dep in dep_map.get(fw, []):
        if dep not in seen:
            seen.add(dep)
            closure(dep, seen)
    return seen
for fw in dep_map:
    all_deps = closure(fw)
    if all_deps:
        print(fw + '|' + ','.join(sorted(all_deps)))
" 2>/dev/null)

# Build lookup set of target framework names actually in this run (pipe-delimited string; bash 3.2 compatible)
RUN_TARGETS="|"
for entry in "${TARGETS[@]}"; do
    IFS='|' read -r _fw _ <<< "$entry"
    RUN_TARGETS="${RUN_TARGETS}${_fw}|"
done

if [[ -n "$DEP_CLOSURES" ]]; then
    echo ""
    echo -e "${BOLD}--- Dependency Gate ---${NC}"

    # Collect libraries needing dep gate processing (newline-separated, bash 3.2 compatible)
    DEP_PENDING=""
    while IFS='|' read -r dep_fw dep_list; do
        if [[ "$RUN_TARGETS" != *"|${dep_fw}|"* ]]; then
            continue
        fi
        DEP_TOTAL=$((DEP_TOTAL + 1))
        if [[ -n "$DEP_PENDING" ]]; then DEP_PENDING+=$'\n'; fi
        DEP_PENDING+="$dep_fw|$dep_list"
    done <<< "$DEP_CLOSURES"

    # Cascading resolution: compile when all deps have DLLs, repeat until no progress
    while [[ -n "$DEP_PENDING" ]]; do
        DEP_PROGRESS=false
        NEXT_PENDING=""

        while IFS='|' read -r dep_fw dep_list; do
            [[ -z "$dep_fw" ]] && continue
            dep_outdir="$OUTPUT_BASE/$dep_fw"

            # Find main C# source file
            local_cs=$(ls "$dep_outdir"/*.cs 2>/dev/null | grep -v '\.Wrappers\.cs' | grep -v '\.SwiftUIBridge\.cs' | head -1)
            if [[ -z "$local_cs" ]]; then
                DEP_SKIPPED=$((DEP_SKIPPED + 1))
                echo -e "  ${YELLOW}$dep_fw: no C# source${NC}"
                continue
            fi

            # Locate all transitive dependency DLLs (prioritize specific names over generic Test.dll)
            MISSING_DEPS=()
            FOUND_REFS=""
            IFS=',' read -ra DEPS <<< "$dep_list"
            for dep in "${DEPS[@]}"; do
                dep_dll=""
                dep_base="${dep%%@*}"
                dep_plat="ios"
                [[ "$dep" == *"@"* ]] && dep_plat="${dep##*@}"
                dep_pkg_suffix=$(platform_to_package_suffix "$dep_plat")
                for dll_name in "${dep_base}.dll" "${dep_base}.Swift.${dep_pkg_suffix}.dll" "Test.dll"; do
                    dep_dll=$(find "$OUTPUT_BASE/$dep/bin" -name "$dll_name" 2>/dev/null | grep -v 'Swift.Runtime.dll' | head -1)
                    [[ -n "$dep_dll" && -f "$dep_dll" ]] && break
                    dep_dll=""
                done
                if [[ -n "$dep_dll" && -f "$dep_dll" ]]; then
                    FOUND_REFS="$FOUND_REFS
    <Reference Include=\"$dep\"><HintPath>$dep_dll</HintPath></Reference>"
                else
                    MISSING_DEPS+=("$dep")
                fi
            done

            # If any dependency DLL is missing, defer to next round
            if [[ ${#MISSING_DEPS[@]} -gt 0 ]]; then
                if [[ -n "$NEXT_PENDING" ]]; then NEXT_PENDING+=$'\n'; fi
                NEXT_PENDING+="$dep_fw|$dep_list"
                continue
            fi

            # Resolve platform-specific settings for this dep target
            dep_platform="ios"
            dep_fw_base="$dep_fw"
            if [[ "$dep_fw" == *"@"* ]]; then
                dep_platform="${dep_fw##*@}"
                dep_fw_base="${dep_fw%%@*}"
            fi
            dep_tfm=$(platform_to_tfm "$dep_platform")
            dep_min_os=$(platform_to_min_os "$dep_platform")
            dep_runtime_tfm=$(platform_to_runtime_tfm "$dep_platform")
            DEP_RUNTIME_DLL="$SCRIPT_DIR/src/Swift.Runtime/src/bin/Debug/$dep_runtime_tfm/Swift.Runtime.dll"

            # Create dep test csproj with AssemblyName so DLL is findable by later rounds
            local_cs_basename=$(basename "$local_cs")
            DEP_CSPROJ="$dep_outdir/_dep_test.csproj"
            cat > "$DEP_CSPROJ" <<DEP_EOF
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Library</OutputType>
    <TargetFramework>$dep_tfm</TargetFramework>
    <SupportedOSPlatformVersion>$dep_min_os</SupportedOSPlatformVersion>
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
    <Nullable>enable</Nullable>
    <NoWarn>0169;CA1420</NoWarn>
    <AssemblyName>$dep_fw_base</AssemblyName>
  </PropertyGroup>
  <ItemGroup>
    <AssemblyAttribute Include="System.Runtime.CompilerServices.DisableRuntimeMarshallingAttribute" />
  </ItemGroup>
  <ItemGroup>
    <Reference Include="Swift.Runtime">
      <HintPath>$DEP_RUNTIME_DLL</HintPath>
    </Reference>$FOUND_REFS
  </ItemGroup>
  <ItemGroup>
    <Compile Include="$local_cs_basename" />
  </ItemGroup>
</Project>
DEP_EOF

            # Restore + build
            dotnet restore "$DEP_CSPROJ" -v quiet 2>/dev/null
            DEP_BUILD_OUTPUT=$(dotnet build "$DEP_CSPROJ" -p:EnableDefaultCompileItems=false --no-restore -v quiet 2>&1)
            DEP_BUILD_EXIT=$?
            DEP_ERRORS=$(echo "$DEP_BUILD_OUTPUT" | grep "error CS" | sort -u | wc -l | tr -d ' ')

            dep_display_deps=$(echo "$dep_list" | tr ',' ' ')

            # Swift wrapper status marker for dep gate display
            dep_swift_marker=""
            dep_sw_status=$(get_result "$dep_fw" swift_compile "unknown")
            case "$dep_sw_status" in
                ok) dep_swift_marker=" ${GREEN}[swift:ok]${NC}" ;;
                fail) dep_swift_marker=" ${RED}[swift:fail]${NC}" ;;
                no_wrapper) dep_swift_marker="" ;;
            esac

            if [[ $DEP_BUILD_EXIT -eq 0 && $DEP_ERRORS -eq 0 ]]; then
                DEP_PASSED=$((DEP_PASSED + 1))
                DEP_PROGRESS=true
                echo -e "  ${GREEN}$dep_fw + [${dep_display_deps}]: OK${NC}${dep_swift_marker}"
                set_result "$dep_fw" compile "ok"
                set_result "$dep_fw" dep_compile "ok"
                set_result "$dep_fw" errors 0
                set_result "$dep_fw" dep_errors 0
            elif [[ $DEP_BUILD_EXIT -ne 0 && $DEP_ERRORS -eq 0 ]]; then
                INFRA_ERR=$(echo "$DEP_BUILD_OUTPUT" | grep -i "error " | grep -v "error CS" | head -1)
                DEP_FAILED=$((DEP_FAILED + 1))
                set_result "$dep_fw" compile "infra_fail"
                set_result "$dep_fw" errors 0
                echo -e "  ${RED}$dep_fw + [${dep_display_deps}]: build failure${NC}${dep_swift_marker}"
                if $VERBOSE && [[ -n "$INFRA_ERR" ]]; then
                    echo -e "    ${DIM}$INFRA_ERR${NC}"
                fi
            else
                DEP_FAILED=$((DEP_FAILED + 1))
                set_result "$dep_fw" compile "fail"
                set_result "$dep_fw" errors "$DEP_ERRORS"
                echo -e "  ${RED}$dep_fw + [${dep_display_deps}]: $DEP_ERRORS errors${NC}${dep_swift_marker}"
                if $VERBOSE; then
                    echo "$DEP_BUILD_OUTPUT" | grep "error CS" | head -5 | while IFS= read -r line; do
                        echo -e "    ${DIM}$line${NC}"
                    done
                fi
            fi
        done <<< "$DEP_PENDING"

        DEP_PENDING="$NEXT_PENDING"
        # Stop if no progress was made this round
        $DEP_PROGRESS || break
    done

    # Report libraries that couldn't be resolved after all rounds
    if [[ -n "$DEP_PENDING" ]]; then
        while IFS='|' read -r dep_fw dep_list; do
            [[ -z "$dep_fw" ]] && continue
            DEP_SKIPPED=$((DEP_SKIPPED + 1))
            set_result "$dep_fw" compile "skip"
            set_result "$dep_fw" errors 0
            dep_display_deps=$(echo "$dep_list" | tr ',' ' ')
            echo -e "  ${YELLOW}$dep_fw + [${dep_display_deps}]: skipped (dependencies not resolved)${NC}"
        done <<< "$DEP_PENDING"
    fi

    echo ""
    if [[ $DEP_TOTAL -gt 0 ]]; then
        DEP_TESTED=$((DEP_PASSED + DEP_FAILED))
        if [[ $DEP_TESTED -eq 0 ]]; then
            echo -e "${YELLOW}Dependency gate: $DEP_TOTAL targets, all skipped (dependencies not compiled)${NC}"
        elif [[ $DEP_FAILED -eq 0 && $DEP_SKIPPED -eq 0 ]]; then
            echo -e "${GREEN}Dependency gate: $DEP_PASSED/$DEP_TOTAL passed${NC}"
        elif [[ $DEP_FAILED -eq 0 ]]; then
            echo -e "${GREEN}Dependency gate: $DEP_PASSED/$DEP_TESTED tested, passed${NC} ${DIM}($DEP_SKIPPED skipped — dependencies not compiled)${NC}"
        else
            echo -e "${RED}Dependency gate: $DEP_PASSED/$DEP_TESTED tested, $DEP_FAILED failed${NC}${DEP_SKIPPED:+ ${DIM}($DEP_SKIPPED skipped)${NC}}"
        fi
    fi
fi

echo ""

# --- Phase 4: Baseline & Regression Detection ---

GIT_SHA=$(git -C "$SCRIPT_DIR" rev-parse --short HEAD)

# Determine if this is a full run (tier 1, no filter) — only full runs update the baseline
IS_FULL_RUN=true
[[ -n "$FILTER" ]] && IS_FULL_RUN=false
[[ "$TIER" != "all" ]] && IS_FULL_RUN=false

# Load previous baseline for regression comparison
PREV_BASELINE=""
if [[ -f "$BASELINE_FILE" ]]; then
    PREV_BASELINE=$(cat "$BASELINE_FILE")
fi

# Build compile gate JSON for current run
COMPILE_JSON=""
for entry in "${DISPLAY_TARGETS[@]}"; do
    IFS='|' read -r name lib_name xcfw_path mode known_errors has_deps platform <<< "$entry"
    comp=$(get_result "$name" compile "unknown")
    errs=$(get_result "$name" errors 0)
    lines=$(get_result "$name" lines 0)
    dep_comp=$(get_result "$name" dep_compile "none")
    sw_comp=$(get_result "$name" swift_compile "unknown")
    if [[ -n "$COMPILE_JSON" ]]; then COMPILE_JSON+=","; fi
    COMPILE_JSON+="\"$name\":{\"compile\":\"$comp\",\"errors\":$errs,\"lines\":$lines,\"dep_compile\":\"$dep_comp\",\"swift_compile\":\"$sw_comp\"}"
done

# Only write baseline on full (unfiltered) runs to prevent partial corruption
if $IS_FULL_RUN; then
    python3 -c "
import json

baseline = {'git_sha': '$GIT_SHA'}

compile_json = '''${COMPILE_JSON:-}'''
if compile_json:
    libs = json.loads('{' + compile_json + '}')
    baseline['compile_gate'] = {'libraries': libs}

with open('$BASELINE_FILE', 'w') as f:
    json.dump(baseline, f, indent=2)
" 2>/dev/null
else
    echo -e "${DIM}Filtered run — baseline not updated${NC}"
fi

# --- Phase 5: Regression Detection ---

# For filtered runs, compare against saved baseline.
# For full runs, compare against the previous baseline (before we overwrote it).
if [[ -n "$PREV_BASELINE" ]]; then
    echo -e "${BOLD}--- Regression Check ---${NC}"

    PREV_TMPFILE=$(mktemp)
    echo "$PREV_BASELINE" > "$PREV_TMPFILE"

    # For filtered runs, write current results to a temp file for comparison
    CURR_TMPFILE=$(mktemp)
    if $IS_FULL_RUN; then
        cp "$BASELINE_FILE" "$CURR_TMPFILE"
    else
        python3 -c "
import json
baseline = {'git_sha': '$GIT_SHA'}
compile_json = '''${COMPILE_JSON:-}'''
if compile_json:
    libs = json.loads('{' + compile_json + '}')
    baseline['compile_gate'] = {'libraries': libs}
with open('$CURR_TMPFILE', 'w') as f:
    json.dump(baseline, f)
" 2>/dev/null
    fi

    python3 - "$PREV_TMPFILE" "$CURR_TMPFILE" "$IS_FULL_RUN" <<'PYEOF'
import json, sys

with open(sys.argv[1]) as f:
    prev = json.load(f)
with open(sys.argv[2]) as f:
    curr = json.load(f)
is_full_run = sys.argv[3] == 'true' if len(sys.argv) > 3 else False

if 'compile_gate' not in prev or 'compile_gate' not in curr:
    sys.exit(0)

prev_libs = prev['compile_gate'].get('libraries', {})
curr_libs = curr['compile_gate'].get('libraries', {})

regressions = []
improvements = []
drift = []

# Check current targets against previous baseline
for name, curr_data in curr_libs.items():
    prev_data = prev_libs.get(name)
    if not prev_data:
        continue

    prev_status = prev_data.get('compile', 'unknown')
    curr_status = curr_data.get('compile', 'unknown')
    prev_errs = prev_data.get('errors', 0)
    curr_errs = curr_data.get('errors', 0)

    # A library passes if it compiles standalone OR with its dependencies.
    # dep_compile is 'ok' when the library passes the dependency gate, 'none' otherwise.
    prev_dep = prev_data.get('dep_compile', 'none')
    curr_dep = curr_data.get('dep_compile', 'none')
    prev_ok = prev_status in ('ok', 'known_errors') or prev_dep == 'ok'
    curr_ok = curr_status in ('ok', 'known_errors') or curr_dep == 'ok'

    if prev_ok and not curr_ok:
        regressions.append((name, f'{prev_status}({prev_errs})', f'{curr_status}({curr_errs})'))
    elif not prev_ok and curr_ok:
        improvements.append((name, f'{prev_status}({prev_errs})', f'{curr_status}({curr_errs})'))
    elif prev_ok and curr_ok and prev_errs == 0 and curr_errs > 0:
        # Was clean, now has (known) errors
        regressions.append((name, f'ok(0)', f'{curr_status}({curr_errs})'))
    elif prev_ok and curr_ok and prev_errs > 0 and curr_errs == 0:
        improvements.append((name, f'{prev_status}({prev_errs})', f'ok(0)'))

    prev_lines = prev_data.get('lines', 0)
    curr_lines = curr_data.get('lines', 0)
    if prev_lines > 0:
        pct = abs(curr_lines - prev_lines) / prev_lines * 100
        if pct > 10:
            drift.append((name, prev_lines, curr_lines, pct))

    # Swift wrapper compilation regression detection
    prev_swift = prev_data.get('swift_compile', 'unknown')
    curr_swift = curr_data.get('swift_compile', 'unknown')
    if prev_swift == 'ok' and curr_swift == 'fail':
        regressions.append((name, f'swift:ok', f'swift:fail'))
    elif prev_swift == 'fail' and curr_swift == 'ok':
        improvements.append((name, f'swift:fail', f'swift:ok'))

# Detect targets that existed in baseline but disappeared from current full run
# (only meaningful for full runs — filtered runs are expected to have subsets)
if is_full_run:
    for name, prev_data in prev_libs.items():
        if name not in curr_libs:
            prev_status = prev_data.get('compile', 'unknown')
            regressions.append((name, f'{prev_status}(present)', 'MISSING'))

if regressions:
    for name, prev_e, curr_e in regressions:
        print(f'\033[0;31mREGRESSION: {name} {prev_e} -> {curr_e}\033[0m')
        print(f'  -> Diagnose:  ./validate-libraries.sh --filter {name} --verbose')
        print(f'  -> After fix: add unit/integration test reproducing the pattern')
        print(f'  -> Verify:    ./validate-libraries.sh')
        print()

if improvements:
    for name, prev_e, curr_e in improvements:
        print(f'\033[0;32mIMPROVED: {name} {prev_e} -> {curr_e}\033[0m')

if drift:
    for name, prev_l, curr_l, pct in drift:
        direction = "+" if curr_l > prev_l else ""
        print(f'\033[0;33mLINE DRIFT: {name} {prev_l} -> {curr_l} ({direction}{curr_l - prev_l}, {pct:.0f}%) -- investigate\033[0m')

if not regressions and not improvements and not drift:
    print('\033[0;32mNo regressions detected\033[0m')
PYEOF

    rm -f "$PREV_TMPFILE" "$CURR_TMPFILE"
    echo ""
fi

# --- Profile Summary ---

# Count targets by tier
TIER_INFO=$(python3 -c "
import json
libs = json.load(open('$MANIFEST'))['libraries']
t1 = sum(len(lib['products']) for lib in libs if lib.get('tier', 1) == 1)
t2 = sum(len(lib['products']) for lib in libs if lib.get('tier', 1) == 2)
manual = sum(len(lib['products']) for lib in libs if lib['mode'] == 'manual')
print(f'{t1}|{t2}|{manual}')
")
TIER1_TARGET_COUNT="${TIER_INFO%%|*}"
TIER2_TARGET_COUNT="$(echo "$TIER_INFO" | cut -d'|' -f2)"
MANUAL_TARGET_COUNT="${TIER_INFO##*|}"

echo -e "${BOLD}=== Summary ===${NC}"

# Overall = standalone passes + dep-gate passes
OVERALL_PASSED=$((COMPILE_PASSED + DEP_PASSED))
OVERALL_FAILED=$((TOTAL_TARGETS - OVERALL_PASSED - COMPILE_NO_OUTPUT))
if [[ $OVERALL_FAILED -le 0 && ${COMPILE_NO_OUTPUT:-0} -eq 0 ]]; then
    echo -e "  Overall: ${GREEN}${OVERALL_PASSED}/$TOTAL_TARGETS passed${NC}"
else
    echo -e "  Overall: ${RED}${OVERALL_PASSED}/$TOTAL_TARGETS passed, $OVERALL_FAILED failed${NC}${COMPILE_NO_OUTPUT:+, ${DIM}${COMPILE_NO_OUTPUT} no output${NC}}"
fi

if [[ ${COMPILE_FAILED:-0} -eq 0 && ${COMPILE_NO_OUTPUT:-0} -eq 0 ]]; then
    echo -e "  Compile (standalone): ${GREEN}${COMPILE_PASSED:-0}/$COMPILE_TESTED passed${NC}"
elif [[ ${COMPILE_FAILED:-0} -eq 0 ]]; then
    echo -e "  Compile (standalone): ${GREEN}${COMPILE_PASSED:-0}/$COMPILE_TESTED passed${NC}, ${DIM}${COMPILE_NO_OUTPUT} no output${NC}"
else
    echo -e "  Compile (standalone): ${RED}${COMPILE_PASSED:-0}/$COMPILE_TESTED passed, $COMPILE_FAILED failed${NC}${COMPILE_NO_OUTPUT:+ ${DIM}($COMPILE_NO_OUTPUT no output)${NC}}"
fi
if [[ ${DEP_TOTAL:-0} -gt 0 ]]; then
    if [[ ${DEP_TESTED:-0} -eq 0 ]]; then
        echo -e "  Dependencies: ${DIM}$DEP_TOTAL targets, all skipped${NC}"
    elif [[ ${DEP_FAILED:-0} -eq 0 ]]; then
        echo -e "  Dependencies: ${GREEN}${DEP_PASSED:-0}/${DEP_TESTED:-0} tested, passed${NC}${DEP_SKIPPED:+ ${DIM}($DEP_SKIPPED skipped)${NC}}"
    else
        echo -e "  Dependencies: ${RED}${DEP_PASSED:-0}/${DEP_TESTED:-0} tested, $DEP_FAILED failed${NC}${DEP_SKIPPED:+ ${DIM}($DEP_SKIPPED skipped)${NC}}"
    fi
fi
if [[ ${SWIFT_TESTED:-0} -gt 0 ]]; then
    SWIFT_SUMMARY_NOWRAP=""
    (( SWIFT_NO_WRAPPER > 0 )) && SWIFT_SUMMARY_NOWRAP=" ${DIM}($SWIFT_NO_WRAPPER ObjC/no wrapper)${NC}"
    if [[ ${SWIFT_FAILED:-0} -eq 0 ]]; then
        echo -e "  Swift wrapper: ${GREEN}${SWIFT_PASSED:-0}/$SWIFT_TESTED passed${NC}${SWIFT_SUMMARY_NOWRAP}"
    else
        echo -e "  Swift wrapper: ${RED}${SWIFT_PASSED:-0}/$SWIFT_TESTED passed, $SWIFT_FAILED failed${NC}${SWIFT_SUMMARY_NOWRAP}"
    fi
fi

# Show tier and profile info
TOTAL_TARGETS_IN_RUN=${#TARGETS[@]}
if [[ -n "$FILTER" ]]; then
    echo -e "  Tier: ${DIM}$TIER${NC} (filtered: $TOTAL_TARGETS_IN_RUN targets matching '$FILTER')"
elif [[ "$TIER" == "all" ]]; then
    echo -e "  Tier: ${GREEN}all${NC} ($TIER1_TARGET_COUNT tier-1 + $TIER2_TARGET_COUNT tier-2)"
elif [[ "$TIER" == "2" ]]; then
    echo -e "  Tier: ${CYAN}2${NC} ($TIER2_TARGET_COUNT targets)"
else
    echo -e "  Tier: ${GREEN}1${NC} ($TIER1_TARGET_COUNT targets)"
fi

if $IS_FULL_RUN; then
    echo -e "  Baseline: ${DIM}$BASELINE_FILE (updated)${NC}"
else
    echo -e "  Baseline: ${DIM}$BASELINE_FILE (not updated — filtered/tier-2 run)${NC}"
fi
echo ""

# Exit with failure if any target failed or produced no output
if [[ ${COMPILE_FAILED:-0} -gt 0 || ${COMPILE_NO_OUTPUT:-0} -gt 0 || ${DEP_FAILED:-0} -gt 0 ]]; then
    exit 1
fi
exit 0
