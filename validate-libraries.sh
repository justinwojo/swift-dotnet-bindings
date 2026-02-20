#!/bin/bash
# validate-libraries.sh — Manifest-driven library validation for swift-bindings generator.
#
# Reads validation-libraries.json, generates C# bindings for each library product,
# compiles them, and tracks results against a baseline for regression detection.
#
# Usage:
#   ./validate-libraries.sh                         # Full compile gate
#   ./validate-libraries.sh --quick                 # Reuse existing /tmp output
#   ./validate-libraries.sh --filter Nuke           # Only matching libraries
#   ./validate-libraries.sh --verbose               # Show errors detail
#   ./validate-libraries.sh --fetch                 # Run fetch script first
#   ./validate-libraries.sh --filter Nuke --verbose # Combine flags

set -o pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"

source "$SCRIPT_DIR/scripts/lib.sh"

MANIFEST="$SCRIPT_DIR/validation-libraries.json"
PROJ="$SCRIPT_DIR/src/Swift.Bindings/src/Swift.Bindings.csproj"
OUTPUT_BASE="/tmp/binding-validation"
BASELINE_FILE="$SCRIPT_DIR/.validation-baseline.json"
LIBRARIES_DIR="$SCRIPT_DIR/.libraries"
RESULTS_DIR=$(mktemp -d)
trap "rm -rf $RESULTS_DIR" EXIT

# --- Flags ---
QUICK=false
FILTER=""
VERBOSE=false
FETCH=false

while [[ $# -gt 0 ]]; do
    case $1 in
        --quick) QUICK=true; shift ;;
        --filter) FILTER="$2"; shift 2 ;;
        --verbose) VERBOSE=true; shift ;;
        --fetch) FETCH=true; shift ;;
        -h|--help)
            echo "Usage: ./validate-libraries.sh [flags]"
            echo ""
            echo "Flags:"
            echo "  --quick           Recompile from existing /tmp/binding-validation/"
            echo "  --filter <pat>    Only libraries matching pattern (case-insensitive)"
            echo "  --verbose         Show generator warnings and first 10 compile errors"
            echo "  --fetch           Run scripts/fetch-libraries.sh before validating"
            echo "  -h, --help        Show this help"
            exit 0
            ;;
        *) echo "Unknown flag: $1"; exit 1 ;;
    esac
done

# --- Helper Functions ---

set_result() { echo "$3" > "$RESULTS_DIR/$1.$2"; }
get_result() { cat "$RESULTS_DIR/$1.$2" 2>/dev/null || echo "${3:-}"; }

get_runtime_version() {
    grep -o 'DefaultSwiftRuntimeVersion = "[^"]*"' \
        "$SCRIPT_DIR/src/Swift.Bindings/src/Emitter/BindingProjectEmitter.cs" \
        | grep -o '"[^"]*"' | tr -d '"'
}

write_fallback_csproj() {
    local outdir="$1"
    local cs_file
    cs_file=$(ls "$outdir"/Swift.*.cs 2>/dev/null | head -1)
    [[ -z "$cs_file" ]] && return 1
    local cs_basename
    cs_basename=$(basename "$cs_file")

    local runtime_dll="$SCRIPT_DIR/src/Swift.Runtime/src/bin/Debug/net10.0-ios/Swift.Runtime.dll"
    cat > "$outdir/Test.csproj" <<CSPROJ
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Library</OutputType>
    <TargetFramework>net10.0-ios</TargetFramework>
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

# --- Expand manifest to validation targets ---

TARGETS=()
MANUAL_TARGETS=()

while IFS='|' read -r fw lib_name xcfw_path mode known_errors; do
    if ! matches_filter "$fw"; then
        continue
    fi
    if $QUICK; then
        # In --quick mode, don't require xcframeworks — we use cached /tmp output
        TARGETS+=("$fw|$lib_name|$xcfw_path|$mode|$known_errors")
    elif [[ "$mode" == "manual" ]]; then
        if [[ -d "$xcfw_path" ]]; then
            TARGETS+=("$fw|$lib_name|$xcfw_path|$mode|$known_errors")
        else
            MANUAL_TARGETS+=("$fw")
        fi
    else
        if [[ -d "$xcfw_path" ]]; then
            TARGETS+=("$fw|$lib_name|$xcfw_path|$mode|$known_errors")
        else
            echo -e "  ${YELLOW}$fw: xcframework not found — skipping${NC}"
        fi
    fi
done < <(manifest_expand_targets "$LIBRARIES_DIR")

if [[ ${#TARGETS[@]} -eq 0 ]]; then
    if [[ -n "$FILTER" ]]; then
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

if ! $QUICK; then
    echo -e "${BOLD}--- Building generator ---${NC}"
    if dotnet build "$SCRIPT_DIR/SwiftBindings.sln" -v quiet 2>&1 | tail -3; then
        echo -e "${GREEN}Generator built${NC}"
    else
        echo -e "${RED}Generator build failed${NC}"
        exit 1
    fi
    echo ""
fi

# --- Phase 3: Compile Gate ---

echo -e "${BOLD}--- Compile Gate ---${NC}"

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

COMPILE_PASSED=0
COMPILE_FAILED=0
COMPILE_NO_OUTPUT=0

for entry in "${TARGETS[@]}"; do
    IFS='|' read -r name lib_name xcfw_path mode known_errors <<< "$entry"
    outdir="$OUTPUT_BASE/$name"

    # Generate
    if ! $QUICK; then
        rm -rf "$outdir"
        mkdir -p "$outdir"
        GEN_START=$SECONDS

        GEN_OUTPUT=$(dotnet run --no-build --project "$PROJ" -- --xcframework "$xcfw_path" -o "$outdir" -v 0 2>&1)
        GEN_EXIT=$?
        if [[ $GEN_EXIT -eq 0 ]] && ls "$outdir"/Swift.*.cs >/dev/null 2>&1; then
            set_result "$name" gen "ok"
        else
            set_result "$name" gen "fail"
            if $VERBOSE; then
                echo "$GEN_OUTPUT" | tail -5 | while read -r line; do
                    echo -e "    ${DIM}$line${NC}"
                done
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
            COMPILE_NO_OUTPUT=$((COMPILE_NO_OUTPUT + 1))
            echo -e "  ${YELLOW}$name: no cached output${NC}"
            continue
        fi
    fi

    # Find .csproj to compile
    CSPROJ_FILE=""
    if ls "$outdir"/*.csproj >/dev/null 2>&1; then
        CSPROJ_FILE=$(ls "$outdir"/*.csproj | grep -v Test.csproj | head -1)
    fi

    # Fallback .csproj when wrapper compilation fails
    if [[ -z "$CSPROJ_FILE" ]] && ls "$outdir"/Swift.*.cs >/dev/null 2>&1; then
        write_fallback_csproj "$outdir"
        CSPROJ_FILE="$outdir/Test.csproj"
    fi

    if [[ -z "$CSPROJ_FILE" ]]; then
        set_result "$name" compile "no_csproj"
        set_result "$name" errors 0
        set_result "$name" lines 0
        COMPILE_NO_OUTPUT=$((COMPILE_NO_OUTPUT + 1))
        echo -e "  ${YELLOW}$name: no .csproj generated${NC}"
        continue
    fi

    # Patch .csproj to use local Swift.Runtime DLL
    RUNTIME_DLL="$SCRIPT_DIR/src/Swift.Runtime/src/bin/Debug/net10.0-ios/Swift.Runtime.dll"
    if grep -q 'PackageReference.*Swift\.Runtime' "$CSPROJ_FILE" 2>/dev/null; then
        sed -i '' 's|<PackageReference Include="Swift.Runtime"[^/]*/>|<Reference Include="Swift.Runtime"><HintPath>'"$RUNTIME_DLL"'</HintPath></Reference>|' "$CSPROJ_FILE"
    fi

    # Count lines
    CS_FILE=$(ls "$outdir"/Swift.*.cs 2>/dev/null | head -1)
    if [[ -n "$CS_FILE" ]]; then
        LINES=$(wc -l < "$CS_FILE" | tr -d ' ')
    else
        LINES=0
    fi
    set_result "$name" lines "$LINES"

    # Compile
    BUILD_OUTPUT=$(dotnet build "$CSPROJ_FILE" -p:EnableDefaultCompileItems=false -v quiet 2>&1)
    ERRORS=$(echo "$BUILD_OUTPUT" | grep "error CS" | sort -u | wc -l | tr -d ' ')
    set_result "$name" errors "$ERRORS"

    GEN_SECS=$(get_result "$name" seconds 0)
    EXPECTED_ERRORS=$known_errors

    if [[ $ERRORS -eq 0 ]]; then
        set_result "$name" compile "ok"
        COMPILE_PASSED=$((COMPILE_PASSED + 1))
        echo -e "  ${GREEN}$name: OK${NC} ${DIM}(${LINES} lines, ${GEN_SECS}s)${NC}"
    elif [[ $EXPECTED_ERRORS -gt 0 && $ERRORS -le $EXPECTED_ERRORS ]]; then
        set_result "$name" compile "known_errors"
        COMPILE_PASSED=$((COMPILE_PASSED + 1))
        echo -e "  ${YELLOW}$name: $ERRORS errors (known, expected $EXPECTED_ERRORS)${NC} ${DIM}(${LINES} lines, ${GEN_SECS}s)${NC}"
    elif [[ $EXPECTED_ERRORS -gt 0 && $ERRORS -gt $EXPECTED_ERRORS ]]; then
        set_result "$name" compile "regressed"
        COMPILE_FAILED=$((COMPILE_FAILED + 1))
        echo -e "  ${RED}$name: $ERRORS errors (expected $EXPECTED_ERRORS — REGRESSED)${NC} ${DIM}(${LINES} lines)${NC}"
        if $VERBOSE; then
            echo "$BUILD_OUTPUT" | grep "error CS" | head -10 | while read -r line; do
                echo -e "    ${DIM}$line${NC}"
            done
            if [[ $ERRORS -gt 10 ]]; then
                echo -e "    ${DIM}... and $((ERRORS - 10)) more${NC}"
            fi
        fi
    else
        set_result "$name" compile "fail"
        COMPILE_FAILED=$((COMPILE_FAILED + 1))
        echo -e "  ${RED}$name: $ERRORS errors${NC} ${DIM}(${LINES} lines)${NC}"
        if $VERBOSE; then
            echo "$BUILD_OUTPUT" | grep "error CS" | head -10 | while read -r line; do
                echo -e "    ${DIM}$line${NC}"
            done
            if [[ $ERRORS -gt 10 ]]; then
                echo -e "    ${DIM}... and $((ERRORS - 10)) more${NC}"
            fi
        fi
    fi
done

echo ""
TOTAL=$((COMPILE_PASSED + COMPILE_FAILED + COMPILE_NO_OUTPUT))
if [[ $COMPILE_FAILED -eq 0 && $COMPILE_NO_OUTPUT -eq 0 ]]; then
    echo -e "${GREEN}Compile gate: $COMPILE_PASSED/$TOTAL passed${NC}"
elif [[ $COMPILE_FAILED -eq 0 ]]; then
    echo -e "${GREEN}Compile gate: $COMPILE_PASSED/$TOTAL passed${NC} ${DIM}($COMPILE_NO_OUTPUT no output)${NC}"
else
    echo -e "${RED}Compile gate: $COMPILE_PASSED/$TOTAL passed, $COMPILE_FAILED failed${NC}${COMPILE_NO_OUTPUT:+ ${DIM}($COMPILE_NO_OUTPUT no output)${NC}}"
fi
echo ""

# --- Phase 4: Baseline & Regression Detection ---

GIT_SHA=$(git -C "$SCRIPT_DIR" rev-parse --short HEAD)

# Determine if this is a full run (no filter) — only full runs update the baseline
IS_FULL_RUN=true
[[ -n "$FILTER" ]] && IS_FULL_RUN=false

# Load previous baseline for regression comparison
PREV_BASELINE=""
if [[ -f "$BASELINE_FILE" ]]; then
    PREV_BASELINE=$(cat "$BASELINE_FILE")
fi

# Build compile gate JSON for current run
COMPILE_JSON=""
for entry in "${TARGETS[@]}"; do
    IFS='|' read -r name lib_name xcfw_path mode known_errors <<< "$entry"
    gen=$(get_result "$name" gen "unknown")
    comp=$(get_result "$name" compile "unknown")
    errs=$(get_result "$name" errors 0)
    lines=$(get_result "$name" lines 0)
    secs=$(get_result "$name" seconds 0)
    if [[ -n "$COMPILE_JSON" ]]; then COMPILE_JSON+=","; fi
    COMPILE_JSON+="\"$name\":{\"generate\":\"$gen\",\"compile\":\"$comp\",\"errors\":$errs,\"lines\":$lines,\"gen_seconds\":$secs}"
done

# Only write baseline on full (unfiltered) runs to prevent partial corruption
if $IS_FULL_RUN; then
    python3 -c "
import json, sys
from datetime import datetime, timezone

baseline = {
    'timestamp': datetime.now(timezone.utc).strftime('%Y-%m-%dT%H:%M:%SZ'),
    'git_sha': '$GIT_SHA',
    'runtime_version': '$RUNTIME_VERSION'
}

compile_json = '''${COMPILE_JSON:-}'''
if compile_json:
    libs = json.loads('{' + compile_json + '}')
    passed = sum(1 for v in libs.values() if v['compile'] in ('ok', 'known_errors'))
    failed = sum(1 for v in libs.values() if v['compile'] in ('fail', 'regressed'))
    baseline['compile_gate'] = {
        'total': len(libs),
        'passed': passed,
        'failed': failed,
        'libraries': libs
    }

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
from datetime import datetime, timezone
baseline = {
    'timestamp': datetime.now(timezone.utc).strftime('%Y-%m-%dT%H:%M:%SZ'),
    'git_sha': '$GIT_SHA',
    'runtime_version': '$RUNTIME_VERSION'
}
compile_json = '''${COMPILE_JSON:-}'''
if compile_json:
    libs = json.loads('{' + compile_json + '}')
    baseline['compile_gate'] = {'total': len(libs), 'libraries': libs}
import json as j
with open('$CURR_TMPFILE', 'w') as f:
    j.dump(baseline, f)
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

    # Status regressions: ok/known_errors -> fail/no_csproj/regressed
    prev_ok = prev_status in ('ok', 'known_errors')
    curr_ok = curr_status in ('ok', 'known_errors')

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

# Count public vs manual targets
PROFILE_INFO=$(python3 -c "
import json
libs = json.load(open('$MANIFEST'))['libraries']
public_targets = sum(len(lib['products']) for lib in libs if lib['mode'] != 'manual')
manual_targets = sum(len(lib['products']) for lib in libs if lib['mode'] == 'manual')
print(f'{public_targets}|{manual_targets}')
")
PUBLIC_TARGET_COUNT="${PROFILE_INFO%%|*}"
MANUAL_TARGET_COUNT="${PROFILE_INFO#*|}"

echo -e "${BOLD}=== Summary ===${NC}"
if [[ ${COMPILE_FAILED:-0} -eq 0 && ${COMPILE_NO_OUTPUT:-0} -eq 0 ]]; then
    echo -e "  Compile: ${GREEN}${COMPILE_PASSED:-0}/$TOTAL passed${NC}"
elif [[ ${COMPILE_FAILED:-0} -eq 0 ]]; then
    echo -e "  Compile: ${GREEN}${COMPILE_PASSED:-0}/$TOTAL passed${NC}, ${DIM}${COMPILE_NO_OUTPUT} no output${NC}"
else
    echo -e "  Compile: ${RED}${COMPILE_PASSED:-0}/$TOTAL passed, $COMPILE_FAILED failed${NC}${COMPILE_NO_OUTPUT:+, ${DIM}${COMPILE_NO_OUTPUT} no output${NC}}"
fi

# Determine which profile was achieved
TOTAL_TARGETS_IN_RUN=${#TARGETS[@]}
if [[ $TOTAL_TARGETS_IN_RUN -ge $((PUBLIC_TARGET_COUNT + MANUAL_TARGET_COUNT)) ]]; then
    echo -e "  Profile: ${GREEN}full${NC} ($PUBLIC_TARGET_COUNT public + $MANUAL_TARGET_COUNT manual)"
elif [[ $TOTAL_TARGETS_IN_RUN -ge $PUBLIC_TARGET_COUNT ]]; then
    echo -e "  Profile: ${GREEN}public${NC} ($PUBLIC_TARGET_COUNT targets)"
elif [[ -n "$FILTER" ]]; then
    echo -e "  Profile: ${DIM}filtered${NC} ($TOTAL_TARGETS_IN_RUN targets matching '$FILTER')"
else
    echo -e "  Profile: ${YELLOW}partial${NC} ($TOTAL_TARGETS_IN_RUN of $PUBLIC_TARGET_COUNT public targets)"
fi

if $IS_FULL_RUN; then
    echo -e "  Baseline: ${DIM}$BASELINE_FILE (updated)${NC}"
else
    echo -e "  Baseline: ${DIM}$BASELINE_FILE (not updated — filtered run)${NC}"
fi
echo ""

# Exit with failure if any target failed or produced no output
if [[ ${COMPILE_FAILED:-0} -gt 0 || ${COMPILE_NO_OUTPUT:-0} -gt 0 ]]; then
    exit 1
fi
exit 0
