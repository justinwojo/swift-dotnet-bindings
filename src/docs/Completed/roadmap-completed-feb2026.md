# Roadmap — Completed Items (Archived February 2026)

Items moved from `roadmap.md` when they were fully complete. See git history for implementation details.

---

## P0: Test Pipeline Hardening

**Status**: Complete
**Spec**: `testframework-review.md`

All items implemented (TH-1 through TH-7). TH-8 (semantic verification depth) deferred as ongoing practice.

- **TH-1. Compile gate** — `CompileCheck.csproj` in `build-and-test.sh` Step 2.5
- **TH-2/3/4. Baseline budgets** — `baselines.json` + `check-baselines.sh` (exit code, degraded, compiled-out, unsupported, crash-risk, strip count)
- **TH-5. Allowlist-based crash tolerance** — `run-tests.sh` extracts last test class, allowlist: `EnumMarshallingTests|OwnershipGCStressTests`
- **TH-6. Test profiles** — PR Gate + Nightly documented in `TestFramework/README.md`
- **TH-7. Reduce simulator flake** — default timeout 90s, deterministic device preference

---

## P1: Testing Depth — Completed Portions

### Gap 4: Protocol Witness Dispatch Runtime Tests — DONE (Interface Projection)

`BasicProtocolDispatchTests` with 33 tests (14 Tier 1, 9 Tier 2, 10 Tier 3). Covers
protocol conformance, blittable property/method dispatch through interfaces, string
method dispatch, and enum method/property dispatch. Proxy-based witness dispatch
(existential container path) deferred — requires wrapper library in RuntimeTestsApp.

### Gap 5: Complex Type Composition Tests — DONE

`BasicCompositionTests` with 23 tests (4 Tier 1, 2 Tier 2, 17 Tier 3). Covers class+closure, struct+optional-array, singleton+async, inheritance+protocol patterns.

---

## P3: Testing Infrastructure — Completed Portions

- **PInvokeEmitter unit tests** (Gap 6) — `PInvokeEmitterTests.cs` with 48 tests
- **Generic runtime tests** (Gap 7) — 30 tests total (20 existing + 10 new for unbound generics + generic free functions), Tier 3 pending confirmation
- **Error handling tests** (Gap 8) — `BasicThrowingTests` with 34 tests (24 passing Tier 1-2, 10 Tier 3)

---

## Completed Work Summary

All completed phases are archived in `Completed/`. Key milestones:

| Phase | What |
|-------|------|
| A-G | Core infrastructure through CryptoSwift validation (~1,700 unit + 185 runtime tests) |
| H1-H2 | Unit test gaps + 6 library binding bugs -> all 4 libraries 0 errors |
| I1/I1a/I1b | Mono JIT mitigation: Nuke wrapper path, BitwiseCopyable, ObjC async callbacks |
| K | Swift doc comments -> C# XML doc comments |
| Strategy D+B | MonoJitRiskDetector + Closure Cdecl expansion |
| Tier Promo | Tj dispatch thunks + IsFinal + tier promotions (172->185 runtime) |
| WU1-WU6 | Idiomatic C# binding API |
| DX Steps 1-5 | `--xcframework` mode, auto wrapper compilation, Swift.Runtime NuGet, .csproj/.targets emission, MSBuild SDK + templates |
| Validation 1-4 | 4 passes fixing 440+ binding errors across 25 libraries -> 0 generator errors |
| DX Improvements | C# type aliases, Codable pruning, enum PascalCase |
| Framework Deps | `--framework-dependency` CLI + `<SwiftFrameworkDependency>` MSBuild item |
| Gaps 6-8 | PInvokeEmitter tests (48) + generic runtime tests (10 new) + error handling tests (34) |
| Stripe Binding Fixes | ObjC enum types, URL return marshalling, exit codes |
| ObjC Framework Deps | SwiftModuleNotFoundException, ResolveObjCFramework(), ObjC fallback |
| DllImport Library Name | Replaced 9 hardcoded "SwiftBindings" strings with dynamic library name |

SwiftUI Bridge v2 (Phases 1-3) and TestFramework Phases A-D ran in parallel, adding comprehensive parameter type support, ABI-driven async inference, bridge hints, and ~184 runtime tests.
