# Roadmap

**Created**: February 2026
**Status**: Active — forward-looking work items only

For completed work, see `Completed/` (notably `phases-a-through-g.md`, `phases-h-through-wu.md`, and `developer-experience.md`).
For detailed gap descriptions and contract matrix, see `testing-gaps.md`.
For test pipeline hardening specs, see `testframework-review.md`.
For deferred/aspirational work, see `Future/`.

---

## Current Baselines

| Metric | Value |
|--------|-------|
| Unit tests | 2,395 passing |
| Integration tests | 699 passing (11 skipped, pre-existing) |
| Runtime tests | 185 passing at Tier 2 safe-only (28 pre-existing failures) |
| TestFramework must-pass | 94/94 passing, 0 degraded |
| Libraries validated | 25 clean (0 generator errors) + 5 environmental-only |

---

## P0: Test Pipeline Hardening

**Status**: Not Started
**Spec**: `testframework-review.md`

Quick wins from the TestFramework risk-first review. Low-effort, high-value gates.

1. **TH-1. Compile gate** — `CompileCheck.csproj` as hard-fail gate in `build-and-test.sh`
2. **TH-2. Baseline budgets** — skip/compiled-out/wrapper-stripped counts in baseline JSON; fail if counts increase
3. **TH-3. Generator exit code gate** — expected exit code in baseline; `build-and-test.sh` compares
4. **TH-4. Ratchet wrapper stripping** — stripped-block count baseline + tolerance
5. **TH-5. Allowlist-based crash tolerance** — replace pattern matching with explicit allowlist
6. **TH-6. Test profiles** — define explicit profiles by intent (fast/full/nightly)
7. **TH-7. Reduce simulator flake** — timing stabilization in runtime tests
8. **TH-8. Semantic verification depth** — beyond string-shape checks

**Done when**: Invalid generated C# fails in under 2 minutes; skip/stripped counts can't drift.

---

## P1: Testing Depth

**Status**: Not Started
**Spec**: `testing-gaps.md` Gaps 3-5

### Gap 3: Async Runtime Tests

Move core async Swift sources out of `.disabled/`, implement `AsyncStringTests.cs` and `AsyncComplexTypeTests.cs`. Contract matrix cells `Async x {String, Array, Class, Enum}` move from `R?` to `R✓`.

### Gap 4: Protocol Witness Dispatch Runtime Tests

Enable protocol Swift sources, implement `WitnessDispatchTests.cs` — property getter/setter dispatch, method dispatch for blittable + String types.

### Gap 5: Complex Type Composition Tests

Add `RealWorldCompositions.swift` with class+closure, struct+optional-array, singleton+async patterns. `CompositionTests.cs` validates round-trips.

---

## P2: Binding API Quality

**Status**: Not Started
**Spec**: `Future/binding-api-future-work.md`

- Callback type projection (typed `Action<T>` / `Func<T,R>` instead of raw delegates)
- Non-frozen enum projection improvements
- Async wrappers for callback-based APIs
- `CancellationToken` support for async methods
- Exception mapping from Swift errors

---

## P2: DX — Multi-Framework Auto-Detection

**Status**: Not Started — manual `--framework-dependency` / `<SwiftFrameworkDependency>` already works
**Spec**: `Future/dx-multi-framework-auto-detection.md`

- Binary linkage analysis (`otool -L`) for automatic detection
- `dependency-manifest.json` generation
- Topological sort for multi-package build ordering

---

## P2: Library Validation Expansion

**Status**: Not Started

- Runtime test apps for validated libraries beyond TestFramework (Nuke, Lottie, BlinkID, CryptoSwift already have them)
- Stripe end-to-end with `--framework-dependency` chain
- Add libraries to `BindingTesting/` with build/validate scripts

---

## P3: Generator Known Bugs

**Status**: Tracked — workarounds exist

| Bug | Impact |
|-----|--------|
| String enum raw values use case names | ABI JSON lacks individual case raw values |
| `UnsafePointer<T>` → AnyType | No concrete projection for immutable pointers |
| Named tuples with String elements | `(SwiftString.Buffer, ...)` → `(SwiftString, ...)` CS0029 |
| Throwing closure thunks | `SwiftString` return emitted as `void*` |
| `async throws(ErrorType)` free functions | Emit `_payload`/`this` in static context (guarded) |
| ExistentialContainer0 in tuple element | Lottie edge case, not reached by current guards |

---

## P3: Testing Infrastructure

**Status**: Not Started
**Spec**: `testing-gaps.md` Gaps 6-10

- **PInvokeEmitter unit tests** (Gap 6) — dedicated tests for P/Invoke generation
- **Generic runtime tests** (Gap 7) — `Container<T>`, generic methods, bound type params
- **Error handling tests** (Gap 8) — enable `ThrowingFunctions.swift`, test throw→exception mapping
- **Golden API snapshot tooling** (Gap 9) — detect API surface drift
- **CI integration** (Gap 10) — GitHub Actions with tiered test profiles

---

## Deferred / Blocked on Upstream

- **Upstream bug reports** — 3 Mono runtime issues documented, waiting for public repo (`Future/upstream-bug-reports-draft.md`)
- **I2. Non-primitive closure wrappers** — Strategy B covers primitive-arg closures; String/class/struct closure args remain on legacy `CallConvSwift` path
- **I3. Instance method wrappers** — `CallConvSwift` for `self` parameter triggers Mono JIT assertion; would unblock CryptoSwift instance API
- **VWT Destroy crash** — `SwiftSafeHandle<T>.ReleaseHandle()` uses indirect CallConvSwift function pointer → Mono JIT assertion

---

## Future Explorations

Each has a design doc in `Future/`:

- NativeAOT validation (`Future/nativeaot-investigation.md`)
- Emitter architecture redesign (`Future/emitter-redesign-proposal.md`)
- Roslyn analyzer for unsafe pattern detection (`Future/roslyn-analyzer-plan.md`)
- Performance benchmarks (`Future/interop-performance-validation-plan.md`)
- Unsupported existential analysis (`Future/unsupported-existential-analysis.md`)

---

## Completed Work Summary

All completed phases are archived in `Completed/`. Key milestones:

| Phase | What |
|-------|------|
| A-G | Core infrastructure through CryptoSwift validation (~1,700 unit + 185 runtime tests) |
| H1-H2 | Unit test gaps + 6 library binding bugs → all 4 libraries 0 errors |
| I1/I1a/I1b | Mono JIT mitigation: Nuke wrapper path, BitwiseCopyable, ObjC async callbacks |
| K | Swift doc comments → C# XML doc comments |
| Strategy D+B | MonoJitRiskDetector + Closure Cdecl expansion |
| Tier Promo | Tj dispatch thunks + IsFinal + tier promotions (172→185 runtime) |
| WU1-WU6 | Idiomatic C# binding API |
| DX Steps 1-5 | `--xcframework` mode, auto wrapper compilation, Swift.Runtime NuGet, .csproj/.targets emission, MSBuild SDK + templates |
| Validation 1-4 | 4 passes fixing 440+ binding errors across 25 libraries → 0 generator errors |
| DX Improvements | C# type aliases, Codable pruning, enum PascalCase |
| Framework Deps | `--framework-dependency` CLI + `<SwiftFrameworkDependency>` MSBuild item |

---

## Known Runtime Blockers (Upstream)

- **Mono JIT assertion (jit-info.c:918)**: Kills process on closure P/Invoke + SwiftString via CallConvSwift
- **SafeHandle in async P/Invoke**: Not preserved through async continuation
- **Non-blittable CallConvSwift**: Mono rejects non-blittable types with Swift calling convention
- See `known-issues-workarounds.md` for details
