# SwiftUI Bridge v2: Coverage-Driven Expansion

**Date**: February 2026
**Status**: Planning
**Prerequisite**: v1 (Deliverable 2) complete and validated
**Parent**: [SwiftUI Bridge Design](../swiftui-bridge-design.md)

---

## Context

v1 (Deliverable 2) shipped and is validated: View detection, template pipeline, functional bridges for NoInternetView (`() -> Void` closure) and BlinkIDUXView (hard-coded async pattern). 16/16 runtime tests, 52 unit tests.

**The problem**: v1 only auto-generates functional bridges for views whose init parameters are primitives, `String`, or `() -> Void` closures. Everything else falls back to commented-out templates requiring manual bridge writing. Real-world coverage is poor:

| Library | Views | v1 Bridged | Template/Skipped |
|---------|-------|-----------|-----------------|
| BlinkIDUX | 4 | 2 (50%) | 2 — existential param, generic type param |
| Lottie | 3 | 0 (0%) | 3 — optional types, async closures, @ViewBuilder |

**The bottleneck**: `InitAnalyzer.MapParameterType()` is a simple switch that rejects any type not in its hardcoded list. Meanwhile, the normal binding pipeline (ClosureHandler, TupleHandler, ExistentialHandler) already supports far richer types. The bridge doesn't leverage any of it.

**Direction** (from Codex review): Build v2 around coverage expansion + bridge hints, not SwiftUI semantics. Track success with a real-library corpus. The product contract is: present SwiftUI `View` as `UIViewController`, callbacks to .NET, lifecycle ownership. NOT: composing ViewBuilder trees from C#.

---

## Non-Goals (v2)

- Composing SwiftUI views from C# (no View protocol, no @ViewBuilder)
- Combine/reactive bridging (@Published stays inside session)
- SwiftUI.Color/Font property mapping
- Auto-compiling bridge (generator produces source; build is user's step)
- Closures with >4 parameters
- Async/throwing closures as init parameters
- Dependency chain flattening deeper than 3 levels
- BoundType for non-frozen structs (deferred to v2.1 — value witness copy semantics are high-risk)

---

## Phase 1: Parameter Type Expansion

**Objective**: Expand `InitAnalyzer.MapParameterType()` to support enums, `Optional<T>` (for primitives and enums), typed closures, and already-bound class parameters. Split into safe increments to isolate risk.

### Prerequisite: Thread ITypeDatabase into Bridge Emitter

The bridge emitter currently has no access to the TypeDatabase. Without it, we can't look up whether a parameter type is an already-bound enum, struct, or class.

| File | Change |
|------|--------|
| `src/Swift.Bindings/src/Emitter/StringEmitter/ModuleEmitter.cs` | Pass `_typeDatabase` to `SwiftUIBridgeEmitter.EmitBridgeFiles()` |
| `src/Swift.Bindings/src/Emitter/StringEmitter/SwiftUIBridgeEmitter.cs` | Accept `ITypeDatabase`, thread through as `BridgeContext` record to analysis methods |

### Phase 1A: BoundEnum + Optional<Primitive|Enum>

Safest increment. Enums cross the ABI as raw integer values. Optionals of primitives/enums use a hasValue flag + raw value.

**New BridgeParameterKind values:**
```
BoundEnum        — enum from TypeDatabase (pass raw Int value across ABI)
OptionalWrapped  — Optional<T> where T is Primitive or BoundEnum only (v2.0)
```

**C ABI Mapping:**

| Swift Type | Kind | Swift @_cdecl param | C# P/Invoke | C# Factory Type |
|------------|------|-------------------|-------------|-----------------|
| `MyEnum` (Int raw) | BoundEnum | `Int32` | `int` | `MyEnum` |
| `Optional<Int>` | OptionalWrapped | `Int32` (hasValue) + `nint` (value) | `int, nint` | `nint?` |
| `Optional<MyEnum>` | OptionalWrapped | `Int32` (hasValue) + `Int32` (rawValue) | `int, int` | `MyEnum?` |

**Not included in 1A** (deferred): `Optional<T>` where T is a reference type (class/struct). This avoids the nullable-pointer vs flag ambiguity for now.

### Phase 1B: BoundType for Classes

Class parameters cross the ABI as `UnsafeMutableRawPointer`. The session retains the pointer in Create and releases in Free.

| Swift Type | Kind | Swift @_cdecl param | C# P/Invoke | C# Factory Type |
|------------|------|-------------------|-------------|-----------------|
| `MyClass` | BoundType | `UnsafeMutableRawPointer` | `IntPtr` | `MyClass` |

**Retain/release contract**: `swift_retain` in session init, `swift_release` in Free. C# factory extracts handle via `obj.Payload.DangerousGetHandle()`.

**Not included in 1B**: Non-frozen structs. Struct value copy via value witness table is significantly more complex (needs VWT lookup, `initializeWithCopy`, `destroy`). Deferred to v2.1 after class support is proven stable.

### Phase 1C: TypedClosure

Closures with typed parameters and/or return values. Max 4 closure parameters.

| Swift Type | Kind | Swift @_cdecl param | C# P/Invoke | C# Factory Type |
|------------|------|-------------------|-------------|-----------------|
| `(T) -> Void` | TypedClosure | `@convention(c) (T_abi, UnsafeMutableRawPointer?) -> Void` | `IntPtr, IntPtr` | `Action<T>` |
| `(T) -> R` | TypedClosure | `@convention(c) (T_abi, UnsafeMutableRawPointer?) -> R_abi` | `IntPtr, IntPtr` | `Func<T, R>` |
| `(T, U) -> Void` | TypedClosure | `@convention(c) (T_abi, U_abi, UnsafeMutableRawPointer?) -> Void` | `IntPtr, IntPtr` | `Action<T, U>` |

Each typed closure generates a C# `[UnmanagedCallersOnly]` trampoline that unpacks `GCHandle → delegate`, converts args, calls delegate. Async and throwing closures remain unsupported (template fallback).

### Phase 1D: Optional<BoundType> for Reference Types

After class BoundType is stable, extend Optional to reference types using nullable pointers.

| Swift Type | Kind | Swift @_cdecl param | C# P/Invoke | C# Factory Type |
|------------|------|-------------------|-------------|-----------------|
| `Optional<MyClass>` | OptionalWrapped | `UnsafeMutableRawPointer?` | `IntPtr` | `MyClass?` |

No flag needed — null pointer = nil.

### MapParameterType Expansion Logic (Final)

```
1. ClosureTypeSpec:
   a. () -> Void → VoidClosure (existing)
   b. !async && !throws && ≤4 params:
      - Recursively map each arg and return type
      - All mappable → TypedClosure
      - Else → null (template)
   c. async/throwing → null

2. NamedTypeSpec:
   a. Existing primitives (Swift.Int, Swift.Bool, etc.) → Primitive (existing)
   b. Swift.String → String (existing)
   c. Swift.Optional with 1 generic param:
      - Recursively map inner type
      - Inner is Primitive or BoundEnum → OptionalWrapped (Phase 1A)
      - Inner is BoundType (class only) → OptionalWrapped (Phase 1D)
      - Else → null
   d. TypeDatabase lookup:
      - Enum → BoundEnum (Phase 1A)
      - Class → BoundType (Phase 1B)
      - Struct (non-frozen) → null (deferred to v2.1)
      - Else → null

3. Everything else → null (template fallback)
```

### Files Modified

| File | Change |
|------|--------|
| `ModuleEmitter.cs` | Pass `_typeDatabase` to bridge emitter |
| `SwiftUIBridgeEmitter.cs` | Accept `ITypeDatabase`, add `BridgeContext`, new Swift/C# emission for each new Kind |
| `SwiftUIBridgeEmitter.InitAnalyzer.cs` | Expand `MapParameterType`, new `BridgeParameterKind` values, new `BridgeParameter` fields, TypeDatabase lookup |
| `SwiftUIBridgeEmitterTests.cs` | Tests for each new parameter kind (~25 tests) |

### Acceptance Criteria

**Per-subphase, each new parameter kind must pass all 3 tiers:**

1. **Generated**: Unit test verifying correct Swift + C# output
2. **Typechecked**: Generated Swift passes `swiftc -typecheck`
3. **Runtime-validated**: At least one new kind consumed by a C# factory in a runtime test app (builds, runs, produces correct behavior)

**Specific acceptance:**
- 1A: A View with `init(style: MyEnum)` → functional bridge, runtime-validated
- 1B: A View with `init(animation: LottieAnimation)` → functional bridge, runtime-validated
- 1C: A View with `init(callback: (Int) -> Bool)` → functional bridge, runtime-validated
- 1D: A View with `init(animation: LottieAnimation?)` → functional bridge, runtime-validated
- All existing tests pass; BlinkIDUX 16/16 runtime tests unchanged
- Unsupported params (non-frozen structs, async closures) still fall back cleanly to template

---

## Phase 2: Generalized Async Factory

**Objective**: Replace hard-coded `KnownAsyncPatterns` dictionary with ABI-driven inference. Any View whose init depends on a type with `async throws` init can be auto-bridged, not just BlinkIDUXView.

### Constructor/Factory Selection Rules

When a dependency type has multiple constructors or factory methods, select deterministically:

1. **Hints override** (Phase 3): If bridge hints specify a factory, use it unconditionally.
2. **Hard-coded pattern** (existing): If `KnownAsyncPatterns` has an entry, use it.
3. **Inferred selection** (ranked by preference):
   a. Prefer constructors over static factory methods
   b. Among constructors, prefer the one with the **smallest supported parameter surface** (fewest parameters where all are bridgeable)
   c. Among ties, prefer the **shallowest async depth** (fewer levels of async dependency)
   d. If multiple constructors tie on all criteria, use the first in ABI declaration order (deterministic)
   e. If no constructor has all-supported parameters, fall back to template

### Dependency Chain Flattening

The BlinkIDUXView pattern flattens a multi-object construction chain into a single `@_cdecl Create`. The algorithm:
1. Start from View's init. For each non-primitive param, look up its type's init (using selection rules above).
2. If the type's init has only supported params (primitives/strings/closures/enums), flatten those into Create params.
3. If a param is another non-primitive, recurse (max depth 3).
4. If any leaf param is unsupported or depth >3, fall back to template.
5. For `async throws` inits, wrap in `Task { @MainActor in ... }` with onReady/onError callbacks.

### CreateAsync Failure and Timeout Policy

Generated `CreateAsync` factories must handle failure scenarios:

1. **Timeout**: `CreateAsync` accepts an optional `CancellationToken` parameter (default `CancellationToken.None`). The C# factory registers the token with the `TaskCompletionSource`. If cancelled, `TrySetCanceled()` is called and the GCHandle is freed.
2. **Default timeout**: No implicit timeout. Callers opt in via `CancellationTokenSource` with timeout. This matches standard .NET async patterns — the framework doesn't impose policy.
3. **Swift-side failure**: `onError` callback fires → `TrySetException()` with the error message. GCHandle freed in error trampoline (idempotent, already implemented in v1).
4. **Callback never arrives**: The `TaskCompletionSource` remains pending. The caller's `CancellationToken` is the only protection. Document this explicitly in the generated factory's XML doc comment.
5. **Multiple callbacks**: `TrySetResult`/`TrySetException` are idempotent — second call is a no-op (already implemented in v1).
6. **Cancellation/callback race**: When cancellation and a Swift callback race, the single completion authority is the `TrySet*` family — whichever wins the race completes the task, the loser is a no-op. GCHandle cleanup uses an `int _completed` field with `Interlocked.CompareExchange(ref _completed, 1, 0)` as the single free-path guard. Whichever path (cancellation registration, onReady, or onError) wins the CAS is responsible for freeing the GCHandle. This prevents double-free without locks.

### Precedence Order (Definitive)

When determining how to bridge a View, the system checks in this exact order:

1. **Bridge hints `skip`/`forceTemplate`** → If present, stops immediately (skip or template)
2. **Bridge hints `asyncPattern`** → If present, builds async pattern from hints (overrides everything below)
3. **`KnownAsyncPatterns` dictionary** → Hard-coded patterns (backward compat; entries can be removed over time)
4. **ABI-driven inference** → Analyzes dependency chain from type declarations
5. **Simple classification** → Falls through to `InitAnalyzer.AnalyzeInitParameters()` for non-async views
6. **Template fallback** → If all above fail or produce unsupported parameters

This is documented in code via a comment block at the top of `AnalyzeView()`.

### Files Modified

| File | Change |
|------|--------|
| `SwiftUIBridgeEmitter.cs` | Accept `ModuleDecl`, new `InferAsyncPattern()`, precedence comment block |
| `SwiftUIBridgeEmitter.AsyncPattern.cs` | Add `InferAsyncViewPattern()` alongside existing dictionary; constructor ranking; auto-build `AsyncViewPattern` from ABI; `CancellationToken` support in C# factory |
| `SwiftUIBridgeEmitter.InitAnalyzer.cs` | Detect async dependency by examining parameter types' inits |
| `ModuleEmitter.cs` | Pass `moduleDecl` to bridge emitter |
| `SwiftUIBridgeEmitterTests.cs` | Tests for auto-inferred async patterns (~15 tests) |

### Acceptance Criteria

- `KnownAsyncPatterns` entries take precedence over inference (backward compat)
- A View with `init(model: MyModel)` where `MyModel.init(key: String)` is `async throws` → auto-generates async factory with `CancellationToken` support
- 3-level chain with primitive leaves generates correctly
- 4-level chain falls to template
- Chains with unsupported leaf types fall to template
- Constructor selection is deterministic: same ABI always produces same bridge
- BlinkIDUXView still works (kept in dictionary; stretch goal: removing it and running inference produces equivalent output)
- **Runtime-validated**: At least one auto-inferred async bridge consumed in a runtime test app
- **Golden-output stability**: Check in a golden-output snapshot of the inferred async bridge's `@_cdecl` function signatures (function names, parameter types, return types). Unit tests diff against this snapshot. Any signature drift fails the test explicitly, catching subtle ABI changes that `swiftc -typecheck` alone wouldn't flag. Snapshot lives at `tests/UnitTests/EmitterTests/GoldenOutput/AsyncBridge_{ViewName}.txt`.

### Risk

Inference generating incorrect Swift for unfamiliar patterns is the highest risk. Mitigation: `swiftc -typecheck` compilation gate; bridge hints (Phase 3) as escape hatch; constructor ranking rules ensure determinism.

---

## Phase 3: Bridge Hints File

**Objective**: JSON sidecar file allowing users to annotate views that auto-detection handles incorrectly or incompletely. This is the "minimal manual effort" escape hatch — users write a few lines of JSON instead of a full manual bridge.

### Discovery

1. `--bridge-hints <path>` CLI argument (highest priority)
2. `{module}.bridge-hints.json` in output directory
3. `bridge-hints.json` in output directory
4. No file = pure auto-detection

If multiple files match, only the first (highest priority) is loaded. Warn if both CLI and file-discovery find hints.

### Schema

```json
{
  "$schema": "bridge-hints-v1",
  "views": {
    "BlinkIDUXView": {
      "preferredInit": 0,
      "asyncPattern": {
        "dependencyChain": [
          { "type": "BlinkIDSdk", "factory": "createBlinkIDSdk", "params": { "licenseKey": "flattened" } },
          { "type": "BlinkIDAnalyzer", "factory": "init", "params": { "sdk": "chain" } },
          { "type": "BlinkIDUXModel", "factory": "init", "params": { "analyzer": "chain", "uxSettings": "flatten" } }
        ],
        "resultMonitor": { "field": "analyzer", "method": "result" }
      }
    },
    "CameraPreview": {
      "skip": true,
      "reason": "Requires live camera preview source (existential)"
    },
    "MyView": {
      "parameterOverrides": {
        "config": { "kind": "flatten", "fields": ["name", "size"] },
        "callback": { "kind": "typedClosure", "signature": "(Int) -> Void" }
      }
    }
  },
  "globalSettings": {
    "maxAsyncChainDepth": 3,
    "maxClosureParams": 4,
    "extraSwiftImports": ["BlinkID"]
  }
}
```

### Precedence and Conflict Behavior

| Scenario | Behavior |
|----------|----------|
| Hints say `skip`, inference says `Generated` | **Hints win.** View is skipped entirely. |
| Hints say `forceTemplate`, inference says `Generated` | **Hints win.** View gets template. |
| Hints specify `asyncPattern`, `KnownAsyncPatterns` also has entry | **Hints win.** Hints override hard-coded patterns. |
| Hints specify `preferredInit`, auto-detection picks different init | **Hints win.** Specified init is used. |
| Hints specify `parameterOverrides` for a param, auto-detection classifies it differently | **Hints win** for that parameter; other params use auto-detection. |
| Hints file has unknown keys | **Ignored with warning.** Forward-compatible. |
| Hints file is malformed JSON | **Warning, skip hints entirely.** Fall back to pure auto-detection. |
| No hints file | **Pure auto-detection.** No change from unhinted behavior. |

**Rule**: Hints are always additive overrides. They never cause a view to be bridged that wouldn't otherwise be detected (detection happens first via `SwiftUIViewDetector`). They can only change the classification or skip a detected view.

### Key Hint Types

| Hint | Purpose |
|------|---------|
| `preferredInit` | Select which constructor to bridge (index into ABI constructor list) |
| `asyncPattern` | Explicitly define dependency chain + result monitor |
| `parameterOverrides` | Force a param to specific kind, or flatten a struct |
| `skip` | No output for this view (not even template) |
| `forceTemplate` | Always template, never functional |
| `extraSwiftImports` | Additional imports for bridge file |

### Files Modified/Created

| File | Change |
|------|--------|
| NEW: `src/Swift.Bindings/src/Emitter/StringEmitter/BridgeHints.cs` | Deserialization model + loader + validation |
| `SwiftUIBridgeEmitter.cs` | Load hints at start of `EmitBridgeFiles`, apply during analysis per precedence rules |
| `SwiftUIBridgeEmitter.InitAnalyzer.cs` | Check hints before auto-detection |
| `SwiftUIBridgeEmitter.AsyncPattern.cs` | Build AsyncViewPattern from hints when present |
| `src/Swift.Bindings/src/Program.cs` | `--bridge-hints` CLI argument |
| `SwiftUIBridgeEmitterTests.cs` | Tests for hint loading, precedence, conflict behavior (~12 tests) |

### Acceptance Criteria

- No hints file → identical behavior to Phase 1+2
- `skip: true` → no output for that view
- `forceTemplate` → always template
- `preferredInit` → correct constructor selected
- `asyncPattern` from hints produces same bridge as `KnownAsyncPatterns` hard-code
- Malformed hints → warning + graceful fallback to auto-detection
- Unknown keys → ignored with warning (forward compat)

---

## Phase 4: Corpus + Metrics

**Objective**: Track bridge coverage across real libraries to measure progress and prevent regressions. Metrics must distinguish generation quality tiers, not just quantity.

### Corpus (start with what we have, grow to 10+)

| Library | Views | Tier | Key Challenges |
|---------|-------|------|----------------|
| BlinkIDUX | 4 | Already tracked | Async chain, existential, generic |
| Lottie | 3 | Already tracked | Optional types, async closures, @ViewBuilder |
| AlertToast | 2 | Easy | Enum params, optional closures |
| ConfettiSwiftUI | 1 | Easy | Simple params |
| SwiftUICharts | 5-10 | Easy-Medium | Data arrays, config structs |
| Kingfisher | 2-3 | Medium | Generic image type, async loading |
| SDWebImageSwiftUI | 3 | Medium | Async image, closures |

### Corpus Reproducibility

Each corpus library entry must include:
- **Pinned version**: Exact release tag or commit hash (e.g., `lottie-ios@4.4.1`)
- **Artifact hash**: SHA-256 of the xcframework archive
- **Fetch script**: `fetch-corpus.sh` that downloads, extracts, and verifies hashes. No binaries checked into the repo.
- **Manifest**: `bridge-corpus/manifest.json` with per-library version, hash, download URL

If a hash doesn't match, the fetch script fails with a clear error. Manual `--update-corpus` flag re-downloads and updates hashes.

### Three-Tier Coverage Metrics

Each view is tracked at three quality levels:

| Tier | Meaning | How Measured |
|------|---------|-------------|
| **Generated** | Bridge code emitted (not just template) | `BridgeStatus == "Generated"` in report |
| **Typechecked** | Generated Swift compiles with `swiftc -typecheck` | Post-generation compilation gate |
| **Runtime-validated** | C# factory consumed in a runtime app, produces correct behavior | iOS Simulator test pass |

Coverage report shows all three:
```
BlinkIDUX:  2/4 generated (50%), 2/4 typechecked (50%), 2/4 runtime-validated (50%)
Lottie:     1/3 generated (33%), 1/3 typechecked (33%), 0/3 runtime-validated (0%)
Aggregate:  3/7 generated (43%), 3/7 typechecked (43%), 2/7 runtime-validated (29%)
```

This prevents "template shrinkage" (moving views from template to generated but broken) from being mistaken as real progress.

**Non-generated outcome taxonomy**: Two orthogonal dimensions — *reason* (why) and *output* (what was emitted):

| Dimension | Values |
|-----------|--------|
| **Reason** (why not generated) | `Unsupported` (params can't be bridged), `HintSkipped` (user chose to skip via hints) |
| **Output** (what was emitted) | `Template` (commented-out stub), `None` (no output at all) |

Mapping: `Unsupported` → always emits `Template` output. `HintSkipped` → emits `None` (no file output for this view).

The `BridgeSummary` uses output-level counts. Reason breakdown is available per-view in `BridgedViews[]`:

```json
{
  "BridgedViews": [
    { "ViewName": "CameraView", "BridgeStatus": "Template", "Reason": "Unsupported", "..." : "..." },
    { "ViewName": "CameraPreview", "BridgeStatus": "HintSkipped", "Reason": "HintSkipped", "..." : "..." }
  ]
}
```

Views not detected as SwiftUI Views (e.g., `ScanningUXSettings`) never appear in bridge metrics.

### Coverage Report Extension

Add `BridgeSummary` to `binding-report.json`:
```json
{
  "BridgeSummary": {
    "TotalViews": 7,
    "Generated": 4,
    "Typechecked": 4,
    "RuntimeValidated": 2,
    "Template": 2,
    "HintSkipped": 1,
    "GeneratedPercent": 57.1,
    "RuntimeValidatedPercent": 28.6
  }
}
```

### Automation

New script: `generate-bridge-coverage.sh`
- `fetch-corpus.sh` — download + verify corpus xcframeworks
- For each corpus library: run generator, collect bridge report, run `swiftc -typecheck` on generated Swift
- Aggregate into `bridge-coverage-matrix.json`
- Print per-library and aggregate coverage at all 3 tiers
- Compare against baseline, flag regressions at any tier

### Files Modified/Created

| File | Change |
|------|--------|
| `src/Swift.Bindings/src/Reporting/BindingReport.cs` | Add `BridgeSummary` section with 3-tier metrics |
| `src/Swift.Bindings/src/Reporting/ReportCollector.cs` | Expand `RecordBridgedView` with parameter details |
| NEW: `generate-bridge-coverage.sh` | Corpus automation |
| NEW: `bridge-corpus/manifest.json` | Pinned versions + hashes |
| NEW: `bridge-corpus/fetch-corpus.sh` | Download + verify xcframeworks |

### Acceptance Criteria

- Coverage report shows per-library bridge rates at all 3 tiers (generated, typechecked, runtime-validated)
- Baseline established for BlinkIDUX + Lottie
- Coverage decrease in any library at any tier is detectable
- Corpus is reproducible: `fetch-corpus.sh` on a clean machine produces identical xcframeworks (verified by hash)
- Report distinguishes Generated vs Template vs HintSkipped, with per-view reason breakdown (Unsupported, HintSkipped)

---

## Implementation Sequencing

```
Phase 1A: Thread ITypeDatabase + BoundEnum + Optional<Primitive|Enum>
    ↓
Phase 1B: BoundType for classes (retain/release)
    ↓
  Validate: runtime test for BoundEnum + BoundType
    ↓
Phase 1C: TypedClosure support
    ↓
  Validate: runtime test for TypedClosure
    ↓
Phase 1D: Optional<BoundType> for reference types
    ↓
  Validate: Lottie LottieSwitch/LottieButton bridgeable + runtime test
    ↓
Phase 2: Generalized async inference + CancellationToken + constructor ranking
    ↓
  Validate: auto-inferred pattern matches KnownAsyncPatterns output; runtime test
    ↓
Phase 3: Bridge hints (can overlap with Phase 2)
    ↓
  Validate: BlinkIDUXView works via hints override
    ↓
Phase 4: Corpus + 3-tier metrics (can start in parallel with Phase 2-3)
```

---

## Verification Per Phase

### Phase 1 (per subphase)
```bash
./run-tests.sh                                          # All tests green
cd BindingTesting/BlinkId && ./regenerate-ux-bindings.sh # Existing bridges unchanged
cd BindingTesting/BlinkId && ./build-all-bridge.sh && ./validate-bridge.sh  # 16/16
# Per-subphase: runtime test app consumes at least one new param kind
```

### Phase 2
```bash
./run-tests.sh
# Verify auto-inferred async pattern produces same @_cdecl signatures as v1
# Verify CancellationToken wired in generated CreateAsync
cd BindingTesting/BlinkId && ./build-all-bridge.sh && ./validate-bridge.sh  # Still 16/16
# Runtime test: auto-inferred async bridge consumed in test app
```

### Phase 3
```bash
./run-tests.sh
# Verify hints file overrides auto-detection per precedence rules
# Verify malformed hints → warning + graceful fallback
# Verify no hints file = same behavior as Phase 2
```

### Phase 4
```bash
./bridge-corpus/fetch-corpus.sh   # Download + verify corpus
./generate-bridge-coverage.sh      # Aggregate 3-tier report
# Verify baseline established and no regressions at any tier
```

---

## Deferred to v2.1

| Feature | Reason |
|---------|--------|
| BoundType for non-frozen structs | Value witness copy semantics are high-risk; need VWT lookup, `initializeWithCopy`, `destroy`. Prove class BoundType stable first. |
| Optional<T> for non-frozen structs | Depends on struct BoundType |
| Async/throwing closures as init params | Complex; Task+callback wrapping per closure, not just per view |
| Tuple init parameters | Rare in SwiftUI view inits; low impact |
| >4 closure parameters | Trampoline complexity; rare in practice |

---

## Risk Register

| Risk | Impact | Mitigation |
|------|--------|------------|
| BoundType retain/release bugs | High | Test with runtime validation per subphase; classes first (simpler), structs deferred |
| TypedClosure trampoline mismatch | Medium | Generate Swift typealias and C# delegate* from same BridgeParameter; runtime test |
| Async inference generates wrong Swift | High | `swiftc -typecheck` gate; deterministic constructor ranking; bridge hints escape hatch |
| TypeDatabase missing cross-module types | Medium | Fall back to template when type not found |
| Optional value-type ABI explosion | Medium | v2.0 restricts to Optional<Primitive\|Enum> only; ref types use nullable pointer |
| Constructor ranking instability | Medium | Explicit ranking rules documented; deterministic tie-breaking by ABI order |
| CreateAsync hangs (callback never arrives) | Medium | CancellationToken support; documented behavior; no implicit timeout |
| Bridge hints schema evolution | Low | Versioned schema; unknown keys ignored with warning |
| Corpus drift | Low | Pinned versions + SHA-256 hashes; fetch script verifies |
| Template shrinkage mistaken as progress | Low | 3-tier metrics (generated/typechecked/runtime-validated) |
