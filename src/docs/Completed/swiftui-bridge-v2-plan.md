# SwiftUI Bridge v2: Remaining Future Work

**Date**: February 2026
**Status**: Phases 1-3 complete, Phase 4 next
**Completed work**: See `Completed/swiftui-bridge-v2-phases1-2.md` (Phases 1-2) and `Completed/swiftui-bridge-v2-phase3.md` (Phase 3)

---

## Current State

| Library | Views | Bridged | Tests | Notes |
|---------|-------|---------|-------|-------|
| BlinkIDUX | 4 | 2 (50%) | 16/16 | 2 skipped: existential param, generic type param |
| BridgeParamTest | 9 | 9 (100%) | 35/35 | Synthetic views exercising all v2 param types |
| Lottie | 6 | 5 (83%) | 15/15 | 1 skipped: @ViewBuilder generic param |

**Direction** (from Codex review): Build v2 around coverage expansion + bridge hints, not SwiftUI semantics. Track success with a real-library corpus. The product contract is: present SwiftUI `View` as `UIViewController`, callbacks to .NET, lifecycle ownership. NOT: composing ViewBuilder trees from C#.

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

## Deferred to v2.1

| Feature | Reason |
|---------|--------|
| BoundType for non-frozen structs | Value witness copy semantics are high-risk; need VWT lookup, `initializeWithCopy`, `destroy`. Prove class BoundType stable first. |
| Optional<T> for non-frozen structs | Depends on struct BoundType |
| Async/throwing closures as init params | Complex; Task+callback wrapping per closure, not just per view |
| Tuple init parameters | Rare in SwiftUI view inits; low impact |
| >4 closure parameters | Trampoline complexity; rare in practice |

---

## Verification

```bash
./bridge-corpus/fetch-corpus.sh   # Download + verify corpus
./generate-bridge-coverage.sh      # Aggregate 3-tier report
# Verify baseline established and no regressions at any tier
```

---

## Risk Register

| Risk | Impact | Mitigation |
|------|--------|------------|
| Bridge hints schema evolution | Low | Versioned schema; unknown keys ignored with warning |
| Corpus drift | Low | Pinned versions + SHA-256 hashes; fetch script verifies |
| Template shrinkage mistaken as progress | Low | 3-tier metrics (generated/typechecked/runtime-validated) |
