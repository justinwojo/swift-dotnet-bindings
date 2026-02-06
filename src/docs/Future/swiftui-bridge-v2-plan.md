# SwiftUI Bridge v2: Coverage-Driven Expansion

**Date**: February 2026
**Status**: Phases 1-2 complete, Phase 3 next
**Prerequisite**: v1 (Deliverable 2) complete and validated
**Parent**: [SwiftUI Bridge Design](../swiftui-bridge-design.md)

---

## Context

v1 (Deliverable 2) shipped and is validated: View detection, template pipeline, functional bridges for NoInternetView (`() -> Void` closure) and BlinkIDUXView (hard-coded async pattern). 16/16 runtime tests, 52 unit tests.

v2 expanded parameter type support (Phase 1), added ABI-driven async inference (Phase 2A), data-driven emission (Phase 2B), and cross-module type resolution with null-safety (Phase 2C). Current state:

| Library | Views | Bridged | Tests | Notes |
|---------|-------|---------|-------|-------|
| BlinkIDUX | 4 | 2 (50%) | 16/16 | 2 skipped: existential param, generic type param |
| BridgeParamTest | 9 | 9 (100%) | 35/35 | Synthetic views exercising all v2 param types |
| Lottie | 6 | 5 (83%) | 15/15 | 1 skipped: @ViewBuilder generic param |

**Direction** (from Codex review): Build v2 around coverage expansion + bridge hints, not SwiftUI semantics. Track success with a real-library corpus. The product contract is: present SwiftUI `View` as `UIViewController`, callbacks to .NET, lifecycle ownership. NOT: composing ViewBuilder trees from C#.

---

## Completed Phases

**Phase 1 (Parameter Type Expansion)** and **Phase 2 (Generalized Async Factory)** are complete. See [`CompletedPhases/swiftui-bridge-v2-phases1-2.md`](../CompletedPhases/swiftui-bridge-v2-phases1-2.md) for full details.

**Summary**: 1419 unit tests, 35/35 BridgeParamTest, 16/16 BlinkIDUX, 15/15 Lottie.

| Phase | What It Did | Key Result |
|-------|-------------|------------|
| 1A | BoundEnum + Optional\<Primitive\|Enum\> | Enums cross ABI as raw int values |
| 1B | BoundType for classes + Optional\<BoundType\> | Class params via retain/release pointers |
| 1C | TypedClosure | Closures with typed params (max 4) via trampolines |
| 1D | Optional\<BoundType\> | Nullable pointer pattern (shipped with 1B) |
| 2A | ABI-driven async inference | Replaces hard-coded `KnownAsyncPatterns` |
| 2B | Data-driven emission | Chain flattening from inferred patterns |
| 2C | Cross-module types + null-safety | TypeDB resolution + null guards |

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
| BlinkIDUX | 4 | Already tracked | Async chain, existential, generic (16/16 runtime) |
| BridgeParamTest | 9 | Already tracked | Synthetic v2 param type corpus (35/35 runtime) |
| Lottie | 6 | Already tracked | Optional, closures, @ViewBuilder (15/15 runtime) |
| AlertToast | 2 | Easy | Enum params, optional closures |
| ConfettiSwiftUI | 1 | Easy | Simple params |
| SwiftUICharts | 5-10 | Easy-Medium | Data arrays, config structs |
| Kingfisher | 2-3 | Medium | Generic image type, async loading |
| SDWebImageSwiftUI | 3 | Medium | Async image, closures |

### Corpus Reproducibility

Each corpus library entry must include:
- **Pinned version**: Exact release tag or commit hash
- **Artifact hash**: SHA-256 of the xcframework archive
- **Fetch script**: `fetch-corpus.sh` that downloads, extracts, and verifies hashes
- **Manifest**: `bridge-corpus/manifest.json` with per-library version, hash, download URL

### Three-Tier Coverage Metrics

| Tier | Meaning | How Measured |
|------|---------|-------------|
| **Generated** | Bridge code emitted (not just template) | `BridgeStatus == "Generated"` in report |
| **Typechecked** | Generated Swift compiles with `swiftc -typecheck` | Post-generation compilation gate |
| **Runtime-validated** | C# factory consumed in a runtime app, produces correct behavior | iOS Simulator test pass |

Coverage report shows all three:
```
BlinkIDUX:  2/4 generated (50%), 2/4 typechecked (50%), 2/4 runtime-validated (50%)
Lottie:     5/6 generated (83%), 5/6 typechecked (83%), 5/6 runtime-validated (83%)
Aggregate:  7/10 generated (70%), 7/10 typechecked (70%), 7/10 runtime-validated (70%)
```

**Non-generated outcome taxonomy**:

| Dimension | Values |
|-----------|--------|
| **Reason** (why not generated) | `Unsupported` (params can't be bridged), `HintSkipped` (user chose to skip via hints) |
| **Output** (what was emitted) | `Template` (commented-out stub), `None` (no output at all) |

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

- Coverage report shows per-library bridge rates at all 3 tiers
- Baseline established for BlinkIDUX + Lottie
- Coverage decrease in any library at any tier is detectable
- Corpus is reproducible: `fetch-corpus.sh` on a clean machine produces identical xcframeworks
- Report distinguishes Generated vs Template vs HintSkipped, with per-view reason breakdown

---

## Implementation Sequencing

```
Phase 1: Parameter Type Expansion  ✅ (archived)
    ↓
Phase 2: Generalized Async Factory  ✅ (archived)
    ↓
Phase 3: Bridge hints  ← NEXT
    ↓
Phase 4: Corpus + 3-tier metrics (can start in parallel with Phase 3)
```

---

## Verification

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
| Bridge hints schema evolution | Low | Versioned schema; unknown keys ignored with warning |
| Corpus drift | Low | Pinned versions + SHA-256 hashes; fetch script verifies |
| Template shrinkage mistaken as progress | Low | 3-tier metrics (generated/typechecked/runtime-validated) |
