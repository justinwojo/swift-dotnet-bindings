# Demangler Component — Test & Code Review

**Date**: 2026-02-16
**Scope**: `src/Swift.Bindings/src/Demangler/` (19 files, 6,788 LOC)
**Tests**: BasicDemanglingTests (270 LOC) + TbdParserTests (457 LOC) = 727 LOC total
**Test Ratio**: 0.11x — **critically undertested**

---

## Executive Summary

The Demangler is a 6,788 LOC subsystem with only 727 LOC of tests (0.11x ratio). The core demangler (`Swift5Demangler.cs`, 3,274 LOC) is a port of Apple's Swift 5 demangler and is the largest single file in the generator. It has **17 test cases** covering a handful of symbol patterns out of hundreds of possible mangling formats.

The review found **5 confirmed bugs**, **5 dead code instances**, and **massive test coverage gaps**. The TBD parser sub-component is in better shape (457 LOC tests for 926 LOC source), but still has gaps.

| Severity | Count | Description |
|----------|-------|-------------|
| Confirmed Bug | 5 | StringSlice.ToString(), Node.IsContext() unsafe access (×2 — also in Swift5Demangler:193), PunyCode.Decode() missing validation + pos overflow, StringSlice.StartsWith/AdvanceIf EOF crash |
| Dead Code | 6 | 5 unused static methods in Swift5Demangler, 1 unused method in PunyCode |
| Missing Coverage (Critical) | 4 | Entire sub-components with zero tests |
| Missing Coverage (High) | 8 | Major functionality paths without tests |
| Design Issue | 3 | Thread safety, nullable disable, recursion depth |

---

## File-by-File Review

### Core Demangling Engine

#### 1. Swift5Demangler.cs (3,274 LOC) — LARGEST FILE

**Purpose**: Port of Apple's Swift 5 demangler. Converts mangled symbol names (`$s...`) into syntax trees (`Node`). Entry points: `Run()` (returns `IReduction`), `DemangleSymbol()` (returns `Node`).

**Tests**: BasicDemanglingTests — 17 test cases covering ~20 symbol patterns.

**Findings**:

| # | Type | Location | Description |
|---|------|----------|-------------|
| D1 | **Design Issue** | Line 8 | `#nullable disable` — suppresses ALL nullable reference type warnings across 3,274 lines. Hundreds of methods return null to indicate parse failure. No distinction between "failed to parse" and "optional result". |
| D2 | **Design Issue** | Lines 37–48 | **Thread safety**: `Run()` uses `lock(runLock)` but instance fields (`nodeStack`, `substitutions`, `words`, `slice`) are mutable. `DemangleSymbol()` (line 413) is also public but NOT protected by the lock — concurrent calls to `DemangleSymbol()` will corrupt shared state. |
| D3 | **Dead Code** | Lines 311–390 | Five unused static methods: `IsAlias()`, `IsClass()`, `IsEnum()`, `IsProtocol()`, `IsStruct()`. Each creates a new `Swift5Demangler` instance — never called anywhere in the codebase. |
| D4 | **Design Issue** | — | No recursion depth limit. Recursive methods like `DemangleBoundGenericArgs()` (line 1590) and `DemangleType()` (line 456) can recurse indefinitely on malformed input. Stack overflow risk with crafted symbols. |
| D5 | Minor | Line 24 | `SymbolicReferenceResolver` is optional (nullable property). Line 576 returns null silently if not set — caller may not expect null. |
| D5b | **Bug** | Line 193 | **Duplicate `IsContext` bug** — `Swift5Demangler` has its own private `IsContext()` (line 189) with the same `memInfo[0]` unsafe access as `Node.IsContext()`. Same `IndexOutOfRangeException` risk on invalid `NodeKind` values. |
| D6 | Missing Coverage | — | **No tests for**: async function symbols, property getter/setter symbols, subscript symbols, operator symbols, closure symbols, metatype symbols, extension symbols, default parameter initializers, variadic functions, inout parameters, key path getters/setters, reabstraction thunks. |
| D6b | Missing Coverage | — | **No tests for `DemangleSymbol()` path** — only `Run()` is exercised in tests. `DemangleSymbol()` (line 413) is public but has no dedicated tests. |
| D6c | Missing Coverage | — | **No tests for symbolic reference resolver** — `SymbolicReferenceResolver` property (line 24) and its usage path (line 545+) are never tested. |
| D7 | Missing Coverage | — | **No tests for complex generics**: deeply nested bound generics (`Array<Dictionary<String, Optional<Int>>>`), associated type paths, where-clause constraints in mangled names. |
| D8 | Missing Coverage | — | **No edge case tests**: empty strings, very long mangled names (>10K chars), malformed prefixes, truncated symbols, symbols with symbolic references. |

**Assessment**: The demangler "works" because it's a port of Apple's code that has been tested upstream, and it's exercised through integration tests (TBD files from real frameworks). However, the unit test coverage is critically low for a 3,274 LOC file. Any modifications to this code would be risky without better regression tests.

**Priority**: D3 is an easy cleanup. D2 is concerning but mitigated in practice (demangling is single-threaded in the pipeline). D6/D7/D8 represent the core coverage gap.

---

#### 2. Swift5Reducer.cs (1,018 LOC)

**Purpose**: Reduces demangled `Node` syntax trees into higher-level types (`TypeSpec`, `FunctionReduction`, `ProtocolWitnessTableReduction`, etc.). Static `Convert()` entry point.

**Tests**: Tested indirectly through BasicDemanglingTests (which calls `Run()` → `DemangleSymbol()` → `Convert()`). No direct unit tests.

**Findings**:

| # | Type | Location | Description |
|---|------|----------|-------------|
| R1 | Missing Coverage | — | **Zero direct tests**. All coverage is through the full `Run()` pipeline, which couples demangling + reduction. A regression in the reducer would be hard to isolate. |
| R2 | Fragile | Lines 23+ | `BuildMatchRules()` is a ~220-line lambda initializer with 20+ match rules. Rule order matters (first match wins). No documentation of precedence or rule interactions. |
| R3 | Unsafe | — | Multiple reduction functions (e.g., `ConvertNominal()`, `ConvertFunction()`) access `node.Children[i]` without bounds checking. If the demangler produces an unexpected tree shape, these will throw `ArgumentOutOfRangeException`. |
| R4 | Missing Coverage | — | No tests for: function reductions with complex signatures (closures as params, tuple returns), generic function reductions, enum reductions, protocol composition reductions. |

**Priority**: R1 is high — the reducer transforms demangled trees into the type specs that drive the entire rest of the pipeline. A bug here can silently produce wrong type information.

---

#### 3. Node.cs (311 LOC)

**Purpose**: Syntax tree node representation. Stores `NodeKind`, optional text/index payload, and child list.

**Tests**: Tested extensively through tree construction in demangling tests. No direct unit tests.

**Findings**:

| # | Type | Location | Description |
|---|------|----------|-------------|
| ND1 | **Bug** | Lines 158–164 | `IsContext(NodeKind kind)` uses reflection to check for `[Context]` attribute: `memberInfo[0].GetCustomAttributes(...)`. Directly accesses `memberInfo[0]` without checking if `GetMember()` returned an empty array. If an invalid/undefined `NodeKind` value is passed, this throws `IndexOutOfRangeException`. **Note**: The same bug exists in `Swift5Demangler.cs:193` which has its own private `IsContext()` copy — see D5b. |
| ND2 | Performance | Lines 158–164 | `IsContext()` uses reflection on every call — no caching. Called frequently during reduction. Should cache the results in a `HashSet<NodeKind>`. |
| ND3 | Minor | Lines 120–121 | `ReverseChildren(startingAt)` allows `startingAt == Children.Count`, which silently does nothing. Intentional but could mask off-by-one bugs. |

**Priority**: ND1 is medium — unlikely in practice since `NodeKind` values are well-defined, but a defensive check costs nothing. ND2 is a performance optimization opportunity.

---

#### 4. StringSlice.cs (227 LOC)

**Purpose**: Position-tracking string wrapper for efficient parsing during demangling.

**Tests**: Zero direct tests. Tested indirectly through demangling.

**Findings**:

| # | Type | Location | Description |
|---|------|----------|-------------|
| SS1 | **Bug** | Line 95 | `ToString()` implementation: `return Position == 0 ? slice : slice.Substring(Current);`. `Current` is a `char` property (the character at the current position), but `Substring()` expects an `int` position index. C# implicitly converts `char` to `int` (its Unicode code point), so e.g., if position=5 and the char is `'S'` (code point 83), it calls `Substring(83)` instead of `Substring(5)`. Should be `slice.Substring(Position)`. |
| SS2 | Missing Coverage | — | No direct unit tests for any method. Edge cases (empty string, single char, advance past end, rewind at start) are untested. |
| SS3 | Doc mismatch | Line ~146 | `Rewind()` documentation says "Rewind to beginning" but implementation only moves back 1 character. |
| SS5 | **Robustness Bug** | Lines 30, 123 | `StartsWith(char c)` (line 30) dereferences `Current` without guarding `IsAtEnd` — throws `ArgumentException` at end of input instead of returning false. Same issue in `AdvanceIf()` (line 123) which evaluates its predicate (often accessing `Current`) without an EOF guard. This is a contract/defensive-behavior bug (unexpected throw at EOF) rather than a currently observed production crash — callers in `Swift5Demangler` typically guard with `!IsAtEnd` before calling, but the methods themselves have an unsafe contract. |

**Priority**: SS1 is a confirmed bug. SS5 is a robustness bug — `StartsWith` should return false at end-of-input, not throw. In practice, `ToString()` may not be called in production paths (likely only debugging), but both should be fixed. SS2 would catch bugs like SS1/SS5 if tests existed.

> **Correction**: An earlier draft listed SS4 claiming `Advance(int n)` lacks `n >= 0` validation. This is **false** — line 136 already checks `if (n < 0 || ...) throw`. Removed.

---

#### 5. PunyCode.cs (119 LOC)

**Purpose**: Decodes Swift's variant of Punycode for internationalized identifiers in mangled names.

**Tests**: **Zero tests.** Not a single test case exists for this component.

**Findings**:

| # | Type | Location | Description |
|---|------|----------|-------------|
| PC1 | **Bug** | Line 88 | `Decode()` accesses `decodeTable[input[pos++]]` without validation. The `decodeTable` only contains 36 characters (`a-z`, `A-J`). If the input contains ANY other character (e.g., `K-Z`, digits, symbols), this throws `KeyNotFoundException`. No error handling around this lookup. |
| PC1b | **Bug** | Line 88 | Same line: `pos++` in the inner `for` loop can overflow past `inputLength` on malformed tails, causing `IndexOutOfRangeException`. The outer `while (pos < inputLength)` only guards the loop entry, not the inner `for` which increments `pos` without re-checking bounds. |
| PC2 | **Dead Code** | Lines 109–116 | `digit_index()` method is defined but never called. Appears to be an alternative decoder that was abandoned. Should be removed. |
| PC3 | Missing Coverage | — | **Zero tests**. No tests for: valid punycode decoding, invalid characters, empty input, delimiter-only input, non-ASCII output, edge cases in the algorithm. |

**Priority**: PC1/PC1b are **malformed-input robustness** issues. Valid Swift punycode payloads stay within the expected alphabet (`a-z`, `A-J`), so normal Unicode identifiers should not trigger these — the risk is malformed or corrupted mangled names, not typical international identifiers. PC2 is trivial cleanup. PC3 is high — this is a self-contained algorithm that is trivially testable.

---

#### 6. Enums.cs (408 LOC)

**Purpose**: All enum types used in demangling — `NodeKind` (280+ values), `PayloadKind`, `GenericTypeKind`, `Directness`, `ValueWitnessKind`, etc.

**Tests**: Implicitly tested through node creation. No direct validation tests.

**Findings**:

| # | Type | Location | Description |
|---|------|----------|-------------|
| EN1 | Missing Coverage | — | No verification that `NodeKind` values match the Swift ABI spec. If values drift (e.g., new Swift version adds/reorders), there's no test to catch it. |
| EN2 | Unclear | Lines ~344–352 | `FunctionEntityArgs` enum — no documented usage. May be dead code. |

**Priority**: Low — enums are stable data. EN1 would be nice for correctness assurance against Swift version updates.

---

#### 7. IReduction.cs (158 LOC)

**Purpose**: Result types from the reduction pipeline — `TypeSpecReduction`, `FunctionReduction`, `ProtocolWitnessTableReduction`, `ProtocolConformanceDescriptorReduction`, `ProvenanceReduction`, `ReductionError`.

**Tests**: Tested indirectly. No direct tests.

**Findings**:

| # | Type | Location | Description |
|---|------|----------|-------------|
| IR1 | Minor | Line ~34 | `ReductionError.Severity` defaults to `Low` — may hide errors if not explicitly set. |
| IR2 | Minor | — | No null validation in any factory methods or constructors. |

**Priority**: Very low — data record types.

---

#### 8. MatchRule.cs (152 LOC)

**Purpose**: Pattern matching rules for node reduction. `Matches()` checks if a rule applies to a node.

**Tests**: Zero direct tests. Tested through Swift5Reducer.

**Findings**:

| # | Type | Location | Description |
|---|------|----------|-------------|
| MR1 | Missing Coverage | — | No direct tests for matching logic. Edge cases: empty child rules, mismatched content types, NodeKindList with multiple entries. |
| MR2 | Minor | Lines 34–48 | `NodeKind` convenience accessor throws if `NodeKindList.Count != 1` — correct but not tested. |

**Priority**: Low — simple matching logic, but would benefit from unit tests if the reducer rules change.

---

#### 9. RuleRunner.cs (37 LOC)

**Purpose**: Executes pattern matching rules on nodes. Simple iterator.

**Tests**: Zero direct tests.

**Findings**: No issues — trivial code (iterate rules, return first match or error).

---

#### 10. DemanglingResults.cs (145 LOC)

**Purpose**: Container for demangling results from a TBD file. Groups reductions by type (metadata accessors, dispatch thunks, witness tables, etc.).

**Tests**: Tested indirectly through TbdParserTests.

**Findings**:

| # | Type | Location | Description |
|---|------|----------|-------------|
| DR1 | Design | Lines 112–122 | `GetMetadataAccessor()` throws generic `Exception` (not a custom type) when accessor not found. Same at line 133. Makes it hard for callers to distinguish "not found" from unexpected errors. |
| DR2 | Silent Error | Line ~97 | `FromTbd()` catches `Exception` during individual symbol demangling and logs a warning, then continues. Caller has no way to know how many symbols failed. Could mask systemic issues (e.g., new mangling format not recognized). |
| DR3 | Missing Coverage | Line 73 | No tests for batch demangling failure aggregation behavior in `FromTbd()`. When multiple symbols fail, the error count/behavior is untested. |

**Priority**: DR1 is low (works fine in practice). DR2/DR3 are medium — a batch of silent failures could indicate a real problem.

---

### TBD Parser Sub-Component (926 LOC total)

#### 11. TbdParser.cs (91 LOC)

**Purpose**: Top-level dispatcher — reads TBD file, tries YAML parser then JSON parser.

**Tests**: TbdParserTests covers this through the `ParseFile()` path.

**Findings**:

| # | Type | Location | Description |
|---|------|----------|-------------|
| TP1 | Minor | Lines 32–36 | Parser registration order matters (YAML checked before JSON). No documentation of precedence. |
| TP2 | Minor | Line ~84 | Generic `Exception` catch wraps non-`ParsingException` errors — loses original exception type information. |

---

#### 12. YamlLikeTbdFormatParser.cs (414 LOC)

**Purpose**: Parser for YAML-like TBD format (versions 1–4, used by older Xcode).

**Tests**: TbdParserTests — tested with Foundation.tbd (v4) and a mock v4 file.

**Findings**:

| # | Type | Location | Description |
|---|------|----------|-------------|
| YP1 | Missing Coverage | — | Only v4 format tested. Versions 1–3 have different indentation and section names. If a library ships with v2/v3 TBD files, parsing may silently fail or produce wrong results. |
| YP2 | Fragile | Line ~216 | Malformed array detection is incomplete. Assumes strict formatting; non-standard YAML could fail silently. |
| YP3 | Known Limitation | Line ~325 | Weak symbols explicitly skipped (comment: "not needed for bindings"). Should verify this assumption. |

---

#### 13. JsonTbdFormatParser.cs (181 LOC)

**Purpose**: Parser for JSON TBD format (version 5+, used by modern Xcode).

**Tests**: TbdParserTests — tested with mock v5 files, ObjC classes, optional fields, text segments.

**Findings**:

| # | Type | Location | Description |
|---|------|----------|-------------|
| JP1 | Silent Data Loss | Line ~153 | `GetFirstStringFromArray()` returns empty string if array is missing or empty. Caller can't distinguish "not present" from "empty value". |
| JP2 | Missing Coverage | — | No tests with real-world large TBD files in JSON format (only synthetic mocks). |

---

#### 14. Models (Symbol.cs, TbdFile.cs, ParsingException.cs) — 176 LOC total

**Tests**: Covered in TbdParserTests.

**Findings**:

| # | Type | Location | Description |
|---|------|----------|-------------|
| SM1 | Minor | Symbol.cs:54 | Symbol classification: `_` prefix (but not `_$`) → ObjectiveC. Overly broad — C++ mangled symbols (`__ZN...`) would be misclassified as ObjC. Not a practical issue since we only care about Swift symbols. |

---

## Summary: Coverage Gaps by Priority

### CRITICAL — Components with zero test coverage

| ID | File | LOC | Gap |
|----|------|-----|-----|
| PC3 | PunyCode.cs | 119 | **Zero tests.** Self-contained algorithm, trivially testable. Has a confirmed bug (PC1). |
| SS2 | StringSlice.cs | 227 | **Zero tests.** Core parsing primitive used by the entire demangler. Has a confirmed bug (SS1). |
| R1 | Swift5Reducer.cs | 1,018 | **Zero direct tests.** Only tested through full pipeline. Transforms node trees into type specs — critical for correctness. |

### HIGH — Major functionality gaps

| ID | File | Gap |
|----|------|-----|
| D6 | Swift5Demangler.cs | No tests for: properties, subscripts, operators, closures, extensions, async functions, metatypes, default params, variadic, inout, key paths, reabstraction thunks |
| D7 | Swift5Demangler.cs | No tests for complex generics (nested bound generics, associated types, where clauses) |
| D8 | Swift5Demangler.cs | No edge case tests (empty input, very long symbols, truncated symbols, malformed prefixes) |
| R4 | Swift5Reducer.cs | No tests for function reductions with closures/tuples, generic functions, enums, protocol compositions |

### MEDIUM — Confirmed bugs to fix

| ID | File | Bug |
|----|------|-----|
| SS1 | StringSlice.cs:95 | `ToString()` passes `Current` (char) to `Substring()` instead of `Position` (int) |
| SS5 | StringSlice.cs:30,123 | `StartsWith(char)` and `AdvanceIf()` dereference `Current` without EOF guard — throw instead of returning false at end of input |
| PC1 | PunyCode.cs:88 | `Decode()` throws `KeyNotFoundException` on characters outside `a-z`/`A-J` |
| PC1b | PunyCode.cs:88 | `Decode()` inner loop `pos++` can overflow past input length on malformed tails |
| ND1 | Node.cs:162 | `IsContext()` accesses `memberInfo[0]` without bounds check |
| D5b | Swift5Demangler.cs:193 | Duplicate `IsContext()` with same `memInfo[0]` unsafe access |

### LOW — Cleanup items

| ID | File | Item |
|----|------|------|
| D3 | Swift5Demangler.cs:311–390 | 5 dead static methods (`IsAlias`, `IsClass`, `IsEnum`, `IsProtocol`, `IsStruct`) |
| PC2 | PunyCode.cs:109–116 | Dead `digit_index()` method |
| SS3 | StringSlice.cs:~146 | `Rewind()` doc says "rewind to beginning" but only goes back 1 char |

---

## Test Quality Assessment

### BasicDemanglingTests (270 LOC, 17 tests)

**What's tested**:
- Protocol witness tables (3 tests)
- Protocol conformance descriptors (2 tests)
- Functions with tuples, dispatch thunks, no args, labeled/unlabeled params (6 tests)
- Metadata accessors — regular and generic (2 tests)
- Error case — garbage input (1 test)
- Unknown entries — default argument initializer (1 test)
- Throwing functions (1 test)
- Generic with associated type (1 test)

**What's NOT tested** (sampling of missing categories):
- Async function symbols
- Property getter/setter/modify symbols
- Subscript symbols
- Operator symbols (prefix, infix, postfix)
- Closure/anonymous function symbols
- Metatype access symbols
- Extension method symbols
- Default parameter initializers (tested but only as "unknown entry")
- Variadic function symbols
- `inout` parameter symbols
- Key path getter/setter symbols
- Reabstraction thunk symbols
- Witness method symbols
- Value witness table symbols
- Type metadata symbols (beyond accessor)
- Resilient enum symbols
- Multi-module qualified symbols
- Retroactive conformance symbols

**Test quality**: The 17 tests that exist are **well-written** — they use real mangled names from actual Swift libraries and verify the full reduction output (function name, module, argument labels, types). They are not fake or trivially-passing. The problem is purely **quantity** — 17 tests for 4,292 LOC of demangler + reducer is insufficient.

### TbdParserTests (457 LOC, 14 tests)

**What's tested**:
- YAML v4 format (Foundation.tbd + mock)
- JSON v5 format (multiple variations)
- Error cases (missing file, invalid format, malformed JSON)
- ObjC classes, optional fields, text segments
- End-to-end through TbdParser dispatcher

**What's NOT tested**:
- YAML v1–v3 formats
- Very large TBD files (stress tests)
- Concurrent parsing
- Weak symbols handling

**Test quality**: Good — uses both real-world files and synthetic mocks. Covers happy and error paths. Better ratio than BasicDemanglingTests.

---

## Bugs Found — Detail

### Bug 1: StringSlice.ToString() (SS1)

**File**: `StringSlice.cs:95`
**Severity**: Medium (likely only affects debugging/logging, not production demangling)

```csharp
public override string ToString()
{
    if (IsAtEnd)
        return "";
    return Position == 0 ? slice : slice.Substring(Current);
    //                                              ^^^^^^^ BUG
    //                                    Should be: Position
}
```

`Current` returns a `char` (the character at the current position). `Substring(int)` expects a start index. C# implicitly converts `char` to `int` via its Unicode code point. So if position is 5 and the character at position 5 is `'S'` (code point 83), this calls `Substring(83)` — returning from position 83 instead of position 5, or throwing if the string is shorter than 83 characters.

**Fix**: Change `Current` to `Position`.

---

### Bug 2: PunyCode.Decode() missing validation (PC1 + PC1b)

**File**: `PunyCode.cs:88`
**Severity**: Medium (malformed-input robustness — valid Swift punycode stays within expected alphabet)

```csharp
int digit = decodeTable[input[pos++]];
//          ^^^^^^^^^^^^^^^^^^^^^^^^^ Two issues:
//  1. No TryGetValue — chars outside a-z/A-J throw KeyNotFoundException
//  2. pos++ can overflow past inputLength in the inner for loop
```

`decodeTable` contains only 36 characters: `a-z` and `A-J`. Characters outside this set cause `KeyNotFoundException`. Additionally, the inner `for` loop increments `pos` without re-checking bounds against `inputLength`, so a malformed tail can cause `IndexOutOfRangeException`.

**Note**: Valid Swift punycode payloads use only the expected alphabet, so this primarily affects malformed or corrupted mangled names, not normal international identifiers.

**Fix**: Use `TryGetValue`, add bounds check on `pos` inside the inner loop, and throw a descriptive exception on invalid input.

---

### Bug 3: Node.IsContext() unsafe access (ND1)

**File**: `Node.cs:162`
**Severity**: Low (NodeKind values are well-defined, but defensive coding prevents crashes)

```csharp
var memberInfo = type.GetMember(kind.ToString());
var attrs = memberInfo[0].GetCustomAttributes(...);
//          ^^^^^^^^^^^^^^ No bounds check
```

If `GetMember()` returns an empty array (e.g., cast an arbitrary int to NodeKind), this throws `IndexOutOfRangeException`.

**Fix**: Add `if (memberInfo.Length == 0) return false;`.

---

## Design Issues

### 1. `#nullable disable` across 3,274 LOC (D1)

The entire `Swift5Demangler.cs` disables nullable warnings. This means:
- Hundreds of `return null` statements with no caller validation
- No compiler assistance for null safety
- Any future modifications risk null dereference bugs

**Recommendation**: This is a large lift to fix. For now, document that the demangler uses null-as-error-signal pattern and ensure callers always null-check results.

### 2. Thread safety (D2)

`Run()` is protected by a lock, but `DemangleSymbol()` is public and unprotected. Instance fields are mutable. In practice, the generator uses a single demangler instance per TBD file processing, so this isn't a production issue, but it's a trap for future callers.

**Recommendation**: Either make `DemangleSymbol()` internal, or document that `Swift5Demangler` instances are not thread-safe.

### 3. No recursion depth limit (D4)

Recursive demangling methods have no depth limit. Crafted malicious mangled names could cause stack overflow. Not a security concern (inputs are from compiled Swift libraries), but a robustness issue.

**Recommendation**: Add a depth parameter with a reasonable limit (e.g., 256).

---

## Recommended Test Plan

### Phase 1: Fix bugs, add tests for zero-coverage components

1. **Fix SS1** (StringSlice.ToString) + **Fix SS5** (StartsWith/AdvanceIf EOF guard) + add StringSlice unit tests
2. **Fix PC1+PC1b** (PunyCode.Decode validation + bounds check) + add PunyCode unit tests
3. **Fix ND1 + D5b** (Node.IsContext + Swift5Demangler.IsContext bounds checks)
4. **Remove dead code**: D3 (5 unused static methods), PC2 (`digit_index()`)

### Phase 2: Add reducer tests

5. Add direct `Swift5Reducer.Convert()` tests with pre-built `Node` trees
6. Test all reduction paths: nominal types, functions, dispatch thunks, protocol witness tables, protocol conformance descriptors, metadata accessors

### Phase 3: Expand demangler symbol coverage

7. Add tests for `DemangleSymbol()` public path (currently only `Run()` is exercised)
8. Add tests for `SymbolicReferenceResolver` behavior
9. Add tests for `FromTbd()` batch failure aggregation
10. Add demangler tests for each major symbol category:
   - Property getter/setter (`$s...ig` / `$s...is`)
   - Subscript (`$s...ip`)
   - Operator (`$s...oi` / `$s...op` / `$s...oP`)
   - Closure (`$s...fU`)
   - Extension methods
   - Async functions
   - Complex generics (nested, associated types, constraints)

### Phase 4: Edge cases and robustness

11. Empty/null input handling
12. Truncated symbols
13. Very long symbols
14. Symbols with symbolic references
15. PunyCode with malformed input (invalid chars, truncated tails)

---

## Session Plan

**2 sessions.** Phase 1+2 fit together naturally; Phase 3+4 are a separate research-heavy effort.

### Session 1 — Bug fixes, dead code, utility tests, reducer tests (Phase 1 + Phase 2)

**Items**: Steps 1–6 above.

| Work Item | Scope | Est. Effort |
|-----------|-------|-------------|
| Bug fixes (SS1, SS5, PC1, PC1b, ND1, D5b) | 5 bugs across 4 files, all surgical single-line or few-line fixes with exact locations documented | Light |
| Dead code removal (D3, PC2) | 6 methods total — delete and verify no references | Trivial |
| StringSlice unit tests (SS2) | 227 LOC source, self-contained parsing primitive. Test: construction, advance, rewind, StartsWith, AdvanceIf, ToString, edge cases (empty, single char, EOF) | Light |
| PunyCode unit tests (PC3) | 119 LOC source, self-contained algorithm. Test: valid decoding, invalid chars, empty input, delimiter-only, truncated tails | Light |
| Swift5Reducer direct tests (R1, R4) | 1,018 LOC source. Build `Node` trees manually, call `Convert()`, verify all reduction paths (nominals, functions, dispatch thunks, witness tables, conformance descriptors, metadata accessors) | Medium |

**Why these fit in one session**: The bug fixes and dead code are warmup that builds familiarity with the files. StringSlice/PunyCode are small isolated units. The reducer tests reuse knowledge gained during the bug fixes — all files are in the same `Demangler/` directory and share the same `Node`/`NodeKind` types.

**Verification**: Run `./run-tests.sh | tail -20` at the end. Expect baseline + new tests passing, zero regressions.

### Session 2 — Demangler symbol coverage + edge cases (Phase 3 + Phase 4)

**Items**: Steps 7–15 above.

| Work Item | Scope | Est. Effort |
|-----------|-------|-------------|
| `DemangleSymbol()` path tests (D6b) | Public API with no dedicated tests — exercise separately from `Run()` | Light |
| `SymbolicReferenceResolver` tests (D6c) | Resolver property + usage path (line 545+) never tested | Light |
| `FromTbd()` batch failure tests (DR3) | Test error aggregation when multiple symbols fail demangling | Light |
| Symbol category tests (D6) | ~15 categories: properties, subscripts, operators, closures, extensions, async, metatypes, default params, variadic, inout, key paths, reabstraction thunks, witness methods, value witness tables, resilient enums | Heavy |
| Complex generics tests (D7) | Nested bound generics, associated types, where-clause constraints | Medium |
| Edge case tests (D8 / Phase 4) | Empty/null input, truncated symbols, very long symbols, symbolic references, malformed PunyCode | Medium |

**Why this is a separate session**: Symbol category tests (D6) require sourcing real mangled symbols — either extracting from `.tbd` files in the repo or generating with the Swift compiler. This is research-heavy: each category needs a valid mangled name and expected demangled output. ~30+ new test cases total.

**Verification**: Run `./run-tests.sh | tail -20` at the end. Expect significant increase in demangler test count with zero regressions.

---

## Review Corrections (from Codex cross-review)

The initial review was cross-reviewed by Codex. Corrections applied:

**Round 1**:
1. **Added D5b**: Duplicate `IsContext` unsafe access in `Swift5Demangler.cs:193` (missed in initial review)
2. **Added SS5**: `StartsWith(char)` and `AdvanceIf()` EOF crash paths (missed in initial review)
3. **Added PC1b**: `pos++` overflow in PunyCode inner loop (missed in initial review)
4. **Added D6b/D6c**: Missing coverage for `DemangleSymbol()` path and symbolic reference resolver (missed in initial review)
5. **Added DR3**: Missing coverage for `FromTbd()` batch failure aggregation (missed in initial review)
6. **Removed SS4**: False positive — `Advance(int n)` already validates `n < 0` at line 136
7. **Corrected PC1 severity**: PunyCode crash is malformed-input robustness, not normal Unicode identifier handling (valid Swift punycode stays within expected alphabet)

**Round 2**:
8. **Fixed D4**: Removed `PopNode()` from recursive examples — it is non-recursive. `DemangleBoundGenericArgs()` and `DemangleType()` are the correct examples.
9. **Fixed dead code count**: 5 → 6 (5 in Swift5Demangler + 1 in PunyCode)
10. **Clarified SS5**: Reworded as contract/defensive-behavior bug, not a currently observed production crash path
