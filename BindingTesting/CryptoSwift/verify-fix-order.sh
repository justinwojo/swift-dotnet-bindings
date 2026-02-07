#!/usr/bin/env bash
set -u

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
CS_OUT="$ROOT_DIR/output-ios/Swift.CryptoSwift.cs"
SWIFT_OUT="$ROOT_DIR/output-ios/Swift.CryptoSwift.swift"
BUGS_DOC="$ROOT_DIR/CODEGEN-BUGS.md"
METHOD_DECL="$ROOT_DIR/../../src/Swift.Bindings/src/Model/TypeDecl/MethodDecl.cs"

PASS_COUNT=0
FAIL_COUNT=0
WARN_COUNT=0

STEP_REQUESTED="${1:-all}"

print_usage() {
    cat <<'EOF'
Usage:
  ./verify-fix-order.sh            # run all step checks
  ./verify-fix-order.sh all        # run all step checks
  ./verify-fix-order.sh <step>     # run one step (1-8)

Notes:
  - Run ./regenerate-bindings.sh before checks.
  - This script is source/output static verification only.
  - Runtime/build checks are still required for full validation.
EOF
}

is_step_requested() {
    local step="$1"
    [[ "$STEP_REQUESTED" == "all" || "$STEP_REQUESTED" == "$step" ]]
}

pass() {
    local step="$1"
    local msg="$2"
    echo "PASS [Step $step] $msg"
    PASS_COUNT=$((PASS_COUNT + 1))
}

fail() {
    local step="$1"
    local msg="$2"
    echo "FAIL [Step $step] $msg"
    FAIL_COUNT=$((FAIL_COUNT + 1))
}

warn() {
    local step="$1"
    local msg="$2"
    echo "WARN [Step $step] $msg"
    WARN_COUNT=$((WARN_COUNT + 1))
}

check_file_exists() {
    local file="$1"
    local label="$2"
    if [[ ! -f "$file" ]]; then
        echo "Missing required file: $label ($file)"
        exit 2
    fi
}

check_absent() {
    local step="$1"
    local file="$2"
    local regex="$3"
    local label="$4"
    local matches
    matches="$(rg -n -e "$regex" "$file" || true)"
    if [[ -n "$matches" ]]; then
        fail "$step" "$label"
        echo "$matches" | head -n 5 | sed 's/^/  -> /'
    else
        pass "$step" "$label"
    fi
}

check_present() {
    local step="$1"
    local file="$2"
    local regex="$3"
    local label="$4"
    local matches
    matches="$(rg -n -e "$regex" "$file" || true)"
    if [[ -n "$matches" ]]; then
        pass "$step" "$label"
    else
        fail "$step" "$label"
    fi
}

check_vtable_references_resolve() {
    local swift_file="$1"
    awk '
    BEGIN {
        err = 0
        in_struct = 0
    }
    {
        if ($0 ~ /^fileprivate struct [A-Za-z0-9_]+_vtable/) {
            line = $0
            sub(/^fileprivate struct /, "", line)
            split(line, parts, " ")
            current_struct = parts[1]
            in_struct = 1
            next
        }
        if (in_struct && $0 ~ /^}/) {
            in_struct = 0
            current_struct = ""
            next
        }
        if (in_struct && $0 ~ /var func_[A-Za-z0-9_]+:/) {
            line = $0
            sub(/^.*var /, "", line)
            split(line, parts, ":")
            field = parts[1]
            gsub(/[[:space:]]/, "", field)
            declared[current_struct "|" field] = 1
            next
        }
        if ($0 ~ /^private var _[A-Za-z0-9_]+_vtable = [A-Za-z0-9_]+_vtable\(\)/) {
            line = $0
            sub(/^private var /, "", line)
            split(line, parts, " = ")
            instance = parts[1]
            struct_name = parts[2]
            sub(/\(\).*/, "", struct_name)
            instance_to_struct[instance] = struct_name
            next
        }

        line = $0
        while (match(line, /_[A-Za-z0-9_]+_vtable\.func_[A-Za-z0-9_]+!/)) {
            token = substr(line, RSTART, RLENGTH)
            split(token, parts, ".")
            instance = parts[1]
            field = parts[2]
            sub(/!$/, "", field)
            struct_name = instance_to_struct[instance]
            if (struct_name == "") {
                printf("Unknown vtable instance %s at line %d\n", instance, NR) > "/dev/stderr"
                err = 1
            } else if (!((struct_name "|" field) in declared)) {
                printf("Missing vtable field %s in %s (line %d)\n", field, struct_name, NR) > "/dev/stderr"
                err = 1
            }
            line = substr(line, RSTART + RLENGTH)
        }
    }
    END {
        exit err
    }' "$swift_file"
}

run_step_1() {
    echo
    echo "== Step 1: PInvoke enum handling (Bug #24) =="
    check_absent "1" "$CS_OUT" "private static extern .+\\(.+Swift\\.CryptoSwift\\.(SHA2|SHA3|HMAC)\\.Variant\\s+[A-Za-z_][A-Za-z0-9_]*\\)" \
        "P/Invoke signatures do not use managed enum wrapper types"
    check_present "1" "$CS_OUT" "variant\\.Payload\\.DangerousGetHandle\\(\\)" \
        "Wrapper calls pass enum payload pointers"
}

run_step_2() {
    echo
    echo "== Step 2: Non-frozen class constructor projection (Bug #20) =="
    check_present "2" "$CS_OUT" "public\\s+.*SHA2\\(\\s*Swift\\.CryptoSwift\\.SHA2\\.Variant" \
        "SHA2 has a constructor that takes Variant"
    check_absent "2" "$CS_OUT" "public\\s+unsafe\\s+Swift\\.CryptoSwift\\.SHA2\\s+Init\\(" \
        "SHA2 no longer exposes instance Init(...) constructor shim"
}

run_step_3() {
    echo
    echo "== Step 3: Operator return ABI + generic operator fixes (Bugs #1, #4, #10) =="
    check_absent "3" "$CS_OUT" "Generator bug: missing swiftIndirectResult allocation" \
        "No operator stubs for missing SwiftIndirectResult allocation"
    check_absent "3" "$CS_OUT" "operator\\s*(>>|<<)\\s*\\([^\\)]*\\bT0\\b" \
        "Shift operators do not emit unresolved T0 parameters"
    check_absent "3" "$CS_OUT" "operator\\s*(==|!=|\\+|\\-|\\*|/|%|\\||&|\\^)\\s*\\([^\\)]*\\bT1\\b" \
        "Operators do not emit wrong generic parameter T1"
}

run_step_4() {
    echo
    echo "== Step 4: Tuple return marshalling + pointer safety (Bugs #2, #6) =="
    check_absent "4" "$CS_OUT" "Generator bug: tuple return not marshalled" \
        "No tuple-return marshalling stubs remain"
    check_absent "4" "$CS_OUT" "ValueTuple<void\\*" \
        "No ValueTuple<void*> signatures are emitted"
}

run_step_5() {
    echo
    echo "== Step 5: EveryProtocol vtable/index integrity (Bug #21) =="
    if check_vtable_references_resolve "$SWIFT_OUT"; then
        pass "5" "All EveryProtocol vtable field references resolve to declared fields"
    else
        fail "5" "At least one EveryProtocol vtable field reference is missing (index drift)"
    fi
}

run_step_6() {
    echo
    echo "== Step 6: EveryProtocol signature correctness (Bugs #22a, #22b, #23, #13) =="
    check_present "6" "$SWIFT_OUT" "public func makeEncryptor\\(\\) throws ->" \
        "Throwing protocol methods emit throws in Swift conformance"
    check_absent "6" "$SWIFT_OUT" "assumingMemoryBound\\(to: \\([^\\(][^\\n]*->[^\n]*\\.self\\)" \
        "Function-type metatype uses parenthesized form before .self"

    if rg -n "enum\\s+ThrowsKind|public\\s+required\\s+ThrowsKind\\s+ThrowsKind|ThrowsKind\\s*:" "$METHOD_DECL" >/dev/null 2>&1; then
        pass "6" "Method model supports ThrowsKind (None/Throws/Rethrows)"
    else
        warn "6" "Method model still uses bool Throws; Bug #22b remains open"
    fi
}

run_step_7() {
    echo
    echo "== Step 7: Protocol proxy/interface alignment (Bugs #3, #11, #12) =="
    check_absent "7" "$CS_OUT" "Generator bug: closure type mismatch" \
        "No closure-type mismatch proxy stubs remain"
    check_absent "7" "$CS_OUT" "Generator bug: Batched\\(\\) not on ISwiftCollection interface" \
        "No Batched()/ISwiftCollection proxy mismatch stubs remain"
    check_absent "7" "$CS_OUT" "BatchedCollection<AnyType>" \
        "No AnyType generic constraint violations in BatchedCollection projection"
}

run_step_8() {
    echo
    echo "== Step 8: Wrapper extension filtering + cleanup (Bugs #14-17, #7, #8, #9) =="
    check_absent "8" "$SWIFT_OUT" "^extension CryptoSwift\\.(Collection|FixedWidthInteger|BatchedCollection|BlockEncryptor|StreamEncryptor|StreamDecryptor)\\b" \
        "No wrapper extensions on non-module/internal types"
    check_absent "8" "$SWIFT_OUT" "\\bSHA2\\.process(32|64)\\(" \
        "No internal SHA2 process* calls in generated Swift wrappers"
    check_absent "8" "$SWIFT_OUT" "\\bSHA3\\.process\\(" \
        "No internal SHA3 process calls in generated Swift wrappers"
    check_absent "8" "$CS_OUT" "Generator bug: generic constructor missing type arg on SwiftSafeHandle" \
        "No generic constructor stubs for missing SwiftSafeHandle type args"
    check_absent "8" "$CS_OUT" "Generator bug: non-frozen generic parameter marshalling" \
        "No non-frozen generic marshalling stubs remain"
    check_absent "8" "$CS_OUT" "Generator bug: duplicate KeySize property" \
        "No duplicate static/instance property emission stubs remain"
}

run_final_meta_checks() {
    echo
    echo "== Global Meta Checks =="
    local bug_stub_count
    bug_stub_count="$(rg -n "Generator bug:" "$CS_OUT" | wc -l | tr -d " ")"
    if [[ "$bug_stub_count" == "0" ]]; then
        pass "meta" "No remaining 'Generator bug:' stubs in generated C# output"
    else
        fail "meta" "Remaining 'Generator bug:' stubs in generated C# output: $bug_stub_count"
    fi

    if [[ -f "$ROOT_DIR/output-ios/binding-report.json" ]]; then
        pass "meta" "binding-report.json exists (capability-gap tracking available)"
    else
        fail "meta" "binding-report.json missing"
    fi
}

main() {
    if [[ "$STEP_REQUESTED" == "-h" || "$STEP_REQUESTED" == "--help" ]]; then
        print_usage
        exit 0
    fi

    if [[ "$STEP_REQUESTED" != "all" && ! "$STEP_REQUESTED" =~ ^[1-8]$ ]]; then
        echo "Invalid step: $STEP_REQUESTED"
        print_usage
        exit 2
    fi

    check_file_exists "$CS_OUT" "generated C# output"
    check_file_exists "$SWIFT_OUT" "generated Swift wrapper output"
    check_file_exists "$BUGS_DOC" "CODEGEN-BUGS.md"
    check_file_exists "$METHOD_DECL" "MethodDecl model"

    echo "CryptoSwift fix-order verifier"
    echo "Root: $ROOT_DIR"
    echo "Mode: $STEP_REQUESTED"

    is_step_requested "1" && run_step_1
    is_step_requested "2" && run_step_2
    is_step_requested "3" && run_step_3
    is_step_requested "4" && run_step_4
    is_step_requested "5" && run_step_5
    is_step_requested "6" && run_step_6
    is_step_requested "7" && run_step_7
    is_step_requested "8" && run_step_8

    if [[ "$STEP_REQUESTED" == "all" ]]; then
        run_final_meta_checks
    fi

    echo
    echo "Summary: PASS=$PASS_COUNT FAIL=$FAIL_COUNT WARN=$WARN_COUNT"
    if [[ "$FAIL_COUNT" -gt 0 ]]; then
        exit 1
    fi
    exit 0
}

main "$@"
