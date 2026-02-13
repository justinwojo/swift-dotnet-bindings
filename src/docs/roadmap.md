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
| Runtime tests | 185 passing at Tier 2 (28 pre-existing failures, allowlist-based crash tolerance) |
| TestFramework must-pass | 94/94 passing, 0 degraded |
| Libraries validated | 25 clean (0 generator errors) + 5 environmental-only |

---

## P0: Test Pipeline Hardening — DONE

**Status**: Complete
**Spec**: `testframework-review.md`

All items implemented (TH-1 through TH-7). TH-8 (semantic verification depth) deferred as ongoing practice.

- **TH-1. Compile gate** — `CompileCheck.csproj` in `build-and-test.sh` Step 2.5
- **TH-2/3/4. Baseline budgets** — `baselines.json` + `check-baselines.sh` (exit code, degraded, compiled-out, unsupported, crash-risk, strip count)
- **TH-5. Allowlist-based crash tolerance** — `run-tests.sh` extracts last test class, allowlist: `EnumMarshallingTests|OwnershipGCStressTests`
- **TH-6. Test profiles** — PR Gate + Nightly documented in `TestFramework/README.md`
- **TH-7. Reduce simulator flake** — default timeout 90s, deterministic device preference

---

## P1: Testing Depth

**Status**: Tests Written (Gap 3 runtime-blocked at Tier 3 upstream; Gaps 4-5 done)
**Spec**: `testing-gaps.md` Gaps 3-5

### Gap 3: Async Runtime Tests — Tests Implemented (Blocked at Tier 3)

32 async runtime tests across 3 classes (`AsyncStringTests`, `AsyncComplexTypeTests`, `AsyncMethodTests`). All Tier 3 — blocked by Mono JIT assertion on `CallConvSwift` in async P/Invoke. Ready for when the upstream blocker is resolved.

### Gap 4: Protocol Witness Dispatch Runtime Tests — DONE (Interface Projection)

`BasicProtocolDispatchTests` with 33 tests (14 Tier 1, 9 Tier 2, 10 Tier 3). Covers
protocol conformance, blittable property/method dispatch through interfaces, string
method dispatch, and enum method/property dispatch. Proxy-based witness dispatch
(existential container path) deferred — requires wrapper library in RuntimeTestsApp.

### Gap 5: Complex Type Composition Tests — DONE

`BasicCompositionTests` with 23 tests (4 Tier 1, 2 Tier 2, 17 Tier 3). Covers class+closure, struct+optional-array, singleton+async, inheritance+protocol patterns.

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

**Status**: Partially Complete (Gap 8 done)
**Spec**: `testing-gaps.md` Gaps 6-10

- **PInvokeEmitter unit tests** (Gap 6) — dedicated tests for P/Invoke generation
- **Generic runtime tests** (Gap 7) — `Container<T>`, generic methods, bound type params
- ~~**Error handling tests** (Gap 8)~~ — **DONE**: `BasicThrowingTests` with 34 tests (24 passing Tier 1-2, 10 Tier 3)
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
