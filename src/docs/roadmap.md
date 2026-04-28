# Roadmap

This doc covers longer-term themes, blocked items, and lower-priority ideas. Live baseline counts live in `.validation-baseline.json`; per-library status lives with each package.

> **Every skipped test is guilty until proven innocent.** 102/102 tests previously blamed on Mono JIT were proven to be generator/runtime bugs in our code. There are exactly 7 confirmed upstream .NET runtime bugs (see `Blocked` section below + memory `feedback_mono_jit_blame.md`). If a crash doesn't match one of these, it's our bug.

---

## Theme A: Skip Reduction *(low priority)*

Skip rate is 16.5%. The remaining ~1,519 skips (after "Not Worth Addressing") are overwhelmingly either correct behavior (private API, synthesized Codable) or architecturally blocked. Consumer-impactful patterns (`Result<T,E>`, common generics, protocol conformances) are already covered. Further reduction has diminishing returns.

| Item | Remaining skips | Effort | Why low priority |
|------|----------------:|--------|-----------------|
| **Unsupported signatures** (associated types, bare generics) | ~611 | Very high | Swift patterns with no C# equivalent |
| **AnyTypeFallback** (cross-library types) | ~429 | Very high | Needs full dependency graph resolution — different product scope |
| **UnsupportedClosure** (multi-blocker methods) | ~188 | High | Reduced via setter-only closure properties and the async-closure bridge (throwing 0–3 args with primitive returns plus zero-arg `Foundation.Data` return; non-throwing 0–3 args with primitive returns only). Remaining are generic params, nested closures, and async-closure shapes outside the supported arg/return matrix (e.g., arg-bearing `Data` returns, non-throwing `Data` returns). |
| **UnsatisfiedGenericConstraint** (remaining) | ~76 | High | Fundamental type system constraints, not relaxable gates |
| **Result<T,E> parameter direction** | blocked | Medium | Needs native payload synthesis for C#-created instances |
| **Multi-protocol generic compositions** | blocked | High | Needs full existential composition in @_cdecl wrapper |
| **Value-type generic conformers** | blocked | High | Requires non-AnyObject transport through @_cdecl boundary |

---

## Theme B: Multi-TFM Runtime Coverage

**Exercise non-iOS slices of multi-TFM packages at runtime.** ~35k lines of test code already cover ~39 libraries on iOS sim + device, but every test app targets `net10.0-ios` only. Packages that ship for macOS / Mac Catalyst / tvOS get compile-gated against those TFMs and never run there. The compile gate catches generator/SDK issues; runtime drift (P/Invoke ABI, framework lookup, codesign, lifecycle) goes unnoticed until a consumer reports it.

Multi-TFM packages currently shipping (TFMs untested at runtime in **bold**):

| Package | TFMs | Untested at runtime |
|---------|------|---------------------|
| **CryptoKit, MusicKit, StoreKit2, TipKit, WeatherKit** | iOS + **macOS** + **Catalyst** + **tvOS** | 3 of 4 |
| **LiveCommunicationKit, WorkoutKit** | iOS + **macOS** + **Catalyst** | 2 of 3 |
| **ProximityReader** | iOS + **Catalyst** | 1 of 2 |

8 Apple-framework packages × 1–3 untested platforms each. Third-party libraries currently ship iOS-only — extending them to additional TFMs is a separate, per-library question driven by consumer demand, not part of this theme.

**Approach:**
1. Pick 1–2 packages with the broadest TFM matrix as the pilot (StoreKit2 and TipKit are the natural candidates — both already ship 4 TFMs, both have substantive embedded tests).
2. Multi-target the existing test app so the same C# test surface runs on each TFM the package supports.
3. Wire macOS / Mac Catalyst / tvOS runners into the regression-validation flow next to the existing iOS sim + device legs.
4. Roll the pattern out across the remaining 6 multi-TFM packages.

Once stable, the `nuke binding-tests` flags (`--macos --catalyst --tvos`) that already exist generator-side become first-class regression gates, not just developer probes.

---

## Theme C: Upstream Bug Reports + Repro Repo *(ready to file)*

Repro repo cleaned up with README, .gitignore, clean git history. 8 draft bug reports (Issues 1, 2, 3, 5, 6, 7, 8, 9) polished with correct versions, GitHub URLs, specific repro class references, and filing checklist. Issues 2, 3, 5, 6, 7, 8, 9 re-verified on .NET SDK 10.0.103 + Xcode 26.2 (2026-04-26).

Remaining steps:
1. **Issue 1**: rewrite the minimal repro — the original `swift_getExistentialTypeMetadata` direct P/Invoke now passes; the `!ji->async` assertion still fires but only as a secondary crash during stack unwinding from a separate native SIGSEGV. New repro must trigger via a P/Invoke that itself crashes natively.
2. Publish repro repo to GitHub (`justinwojo/swift-interop-repro` — currently local only, no remote)
3. File the 8 issues on dotnet/runtime

Drafts: [`Future/upstream-bug-reports-draft.md`](Future/upstream-bug-reports-draft.md)

---

## Lower Priority

| Item | Notes |
|------|-------|
| **Performance benchmarks** | Baseline P/Invoke overhead measurement. [`Future/interop-performance-validation-plan.md`](Future/interop-performance-validation-plan.md) |
| **API snapshot tooling** | Detect API surface drift between versions. [`Future/api-snapshot-tooling.md`](Future/api-snapshot-tooling.md) |
| **SwiftUI beyond current level** | Wait for consumer feedback before investing further |
| **Property wrappers / KeyPaths** | Low frequency in public API surfaces |
| **Static protocol constructors** | Init witness dispatch needs allocation infrastructure |
| **Weak/unowned references** | 4 test skips. Requires ownership tracking infrastructure |
| **Constrained-generic PWT plumbing for non-accessor P/Invokes** | `EnumHandler.RawRepresentable.cs:146,254` and `OperatorHandler.cs:453,481` still pass bare `GetMetadataArgumentList()`. Not triggered by any current validation library — leave alone until a repro surfaces. |
| **Wrapper-helper path dynamic PWT resolution** | Swift wrapper side still fail-closed for Self-requirement / associated-type protocols. Not triggered by any current validation library. |
| **Multi-PAT existential boxing** | A type conforming to 2+ PAT protocols cannot box through the `object` fallback because the `typeof(object)` dictionary key is ambiguous. Guarded to fail explicitly (`InvalidCastException`) rather than silently select the wrong witness table. Extremely rare in practice. |
| **tvOS device runner** | Requires provisioning profile + physical Apple TV. Generator, SDK, runtime, and build infra already support tvOS; only the `nuke runtime-tests-tvos-device` Nuke target and deployment mechanism are missing. |

---

## Blocked (Confirmed Upstream Only)

These are the **only** confirmed upstream issues. There are exactly 7 (reproduced in standalone repro at `/Users/wojo/Dev/swift-interop-repro/`). If a crash doesn't match one of these, it's our bug. See `feedback_mono_jit_blame.md` for the full investigation checklist.

| # | Issue | Blocked By |
|---|-------|-----------|
| 1 | **Mono: JIT assertion `!ji->async` on CallConvSwift P/Invoke** | Fatal `jit-info.c:918` during stack unwinding when calling Swift runtime functions. Workaround: `@_silgen_name` Swift wrappers |
| 2 | **Non-blittable type rejection with CallConvSwift** | .NET runtime design limitation. Workaround: @_cdecl wrappers (already covers 78.5% of P/Invokes) |
| 3 | **Mono: SafeHandle async lifetime** | GC may collect SafeHandle during async suspension. Workaround: manual ARC retain/release or singleton pattern |
| 4 | **NativeAOT: custom struct float/double in GPR instead of FPR** | NativeAOT JIT `SwiftPhysicalLowering.cs` register allocation bug. System types (CGRect) work; custom structs fail |
| 5 | **NativeAOT: custom integer struct >16B SIGSEGV** | Struct passed by pointer instead of in registers. NativeAOT (≥24B), Mono (≥32B) |
| 6 | **NativeAOT: multi-type-parameter generic SIGSEGV** | 1 type param works, 2+ type params SIGSEGV |
| 7 | **Mono: `Cannot transition thread from STARTING with DONE_BLOCKING` on `(Bool, @out via x0)` tuple-return CallConvSwift** | Specific to `Set<T>.insert` ABI shape. `Set.contains` (no `@out`) passes. Workaround: `@_cdecl` Swift wrapper |

| Other | Status |
|-------|--------|
| **Non-Int32 enum raw values** | Blocked on Swift compiler: `.swiftinterface` strips integer raw values. No workaround. 1 skipped test. |

---

## Not Worth Addressing

| Skip Reason | Count | Why Not |
|-------------|------:|---------|
| @_spi / internal members | ~750 | Correct behavior — private API should not be bound |
| Synthesized Codable | ~730 | .NET consumers use own serialization (`System.Text.Json`, etc.) |
| Generic protocol constraints / PATs | ~453 | Architecturally blocked by associated type erasure |
| SwiftUI/Combine dependencies | ~181 | Framework boundary — consumers use SwiftUI bridge instead (`SwiftUIConstraint` + `SwiftUIView`) |
| Unsupported existential (opaque generics) | ~90 | Fundamental limitation of Swift's type system vs C# generics |

---

## Explicitly Out of Scope

| Item | Reason |
|------|--------|
| Full Swift type graph infrastructure | Over-engineered for current needs |
| Deep generic signature / associated type constraint emission | C# generics can't express Swift's full type system |
| Result builder (`@resultBuilder`) projection | Compile-time Swift feature, no ABI JSON representation |
| `@dynamicMemberLookup` / KeyPath projection | Affects <5 types across 53 validation libraries |
| Composing SwiftUI view trees from C# | Result builders are a compiler feature |
| Structs projected as C# value types | Only safe for frozen+blittable subset; marginal benefit |

