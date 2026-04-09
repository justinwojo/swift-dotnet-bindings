# Roadmap to 1.0

**Updated**: April 4, 2026

**Current baseline**: 95/95 CS compile, 61/61 Swift compile. All targets passing.
**Skip metrics**: 10,762 emitted members, 1,956 skipped (15.4% skip rate) across 95 validation targets.
**Runtime tests**: 1,301 passed, 17 skipped on iOS Device (NativeAOT). 1,668 passed, 2 skipped across 8 real-world library test suites (swift-dotnet-packages).
**Downstream validation**: 2,107/2,107 sim tests passing across 23 libraries (swift-dotnet-packages + sim-validation). Zero regressions on 0.6.0 packages.

> **Every skipped test is guilty until proven innocent.** 102/102 tests previously blamed on Mono JIT were proven to be generator/runtime bugs in our code. There are exactly 5 confirmed upstream .NET runtime bugs (see `Blocked` section below + memory `feedback_mono_jit_blame.md`). If a crash doesn't match one of these, it's our bug.

---

## 1.0 Goal

**Any xcframework to full-featured NuGet package.** Cover as much of the Swift API surface as possible, validate it works at runtime, and ensure the end-to-end experience is solid.

---

## Theme A: Skip Reduction *(low priority)*

Skip rate is 15.4%. The remaining ~1,956 skips are overwhelmingly either correct behavior (private API, synthesized Codable) or architecturally blocked. Consumer-impactful patterns (`Result<T,E>`, common generics, protocol conformances) are already covered. Further reduction has diminishing returns.

| Item | Remaining skips | Effort | Why low priority |
|------|----------------:|--------|-----------------|
| **Unsupported signatures** (associated types, bare generics) | ~351 | Very high | Swift patterns with no C# equivalent |
| **AnyTypeFallback** (cross-library types) | ~307 | Very high | Needs full dependency graph resolution — different product scope |
| **UnsupportedClosure** (multi-blocker methods) | ~131 | High | Reduced from 153 via setter-only closure properties. Remaining are generic params, async closures, nested closures. |
| **UnsatisfiedGenericConstraint** (remaining) | ~92 | High | Fundamental type system constraints, not relaxable gates |
| **Result<T,E> parameter direction** | blocked | Medium | Needs native payload synthesis for C#-created instances |
| **Multi-protocol generic compositions** | blocked | High | Needs full existential composition in @_cdecl wrapper |
| **Value-type generic conformers** | blocked | High | Requires non-AnyObject transport through @_cdecl boundary |

---

## Theme B: Runtime Depth

**Deep-dive 3 additional flagship libraries.** Nuke and Lottie already have full coverage (sim + device, sample apps, extensive tests). Next targets should exercise different binding patterns and represent what users are most likely to try.

Planning doc: `runtime-depth-sessions.md` (not yet written)

Candidates (not yet committed — finalize when planning):

| Library | Why | Exercises |
|---------|-----|-----------|
| **Alamofire** | Most popular Swift networking lib | Async/await, `Result` returns, request chains, generics |
| **Kingfisher** | Popular image loading | Closure-heavy pipeline, caching, options/enums |
| **GRDB** | Most complex API surface in validation set | Protocols, associated types, generics, database patterns |

These complement Nuke (image processing) and Lottie (animation) to give coverage across networking, images, and database.

---

## Theme C: Developer Experience Hardening *(mostly complete)*

Significant investment already made. First hardening pass completed: template post-creation instructions, actionable binding report paths, CLI help completeness, verbosity error messages, multi-platform comment accuracy. Golden path validated end-to-end.

Remaining:
- Documentation gaps in wiki

---

## Theme D: Upstream Bug Reports + Repro Repo *(ready to file)*

Repro repo cleaned up with README, .gitignore, clean git history. All 7 draft bug reports polished with correct versions, GitHub URLs, specific repro class references, and filing checklist. Ready for publication and filing.

Remaining steps:
1. Re-verify all repros on current .NET SDK
2. Publish repro repo to GitHub (`justinwojo/swift-interop-repro`)
3. File the 7 issues on dotnet/runtime

Drafts: [`Future/upstream-bug-reports-draft.md`](Future/upstream-bug-reports-draft.md)

---

## Theme E: Multi-Platform Hardening *(audit complete, runtime testing remaining)*

macOS validation audit completed. Generator, SDK, runtime, and build infrastructure all properly support iOS, macOS, Mac Catalyst, and tvOS — architecture is solid. Verified end-to-end by running the generator against Nuke.xcframework with `--platform macos`. Fixed iOS-specific help text and comments.

Remaining:
- Runtime tests on non-iOS platforms (at minimum macOS — most accessible)
- Validate SDK/NuGet packaging for macOS / Mac Catalyst / tvOS TFMs with real consumers

---

## Lower Priority / Post-1.0

| Item | Notes |
|------|-------|
| **Performance benchmarks** | Baseline P/Invoke overhead measurement. [`Future/interop-performance-validation-plan.md`](Future/interop-performance-validation-plan.md) |
| **Bulk retain/release helpers for collections** | Replace per-element `DangerousAddRef`/`DangerousRelease` + P/Invoke loops in `SwiftArray`, `SwiftDictionary`, `SwiftSet` with Swift-side batch helpers (e.g. `SBW_RetainMany`/`SBW_ReleaseMany`) in the SwiftBindingsRuntime library. Cuts managed↔native transition cost on large collections. Low-medium effort, high perf impact. |
| **API snapshot tooling** | Detect API surface drift between versions. [`Future/api-snapshot-tooling.md`](Future/api-snapshot-tooling.md) |
| **SwiftUI beyond current level** | Wait for consumer feedback before investing further |
| **Custom actor types** | Niche — requires async dispatch through actor's serial executor |
| **Property wrappers / KeyPaths** | Low frequency in public API surfaces |
| **Static protocol constructors** | Init witness dispatch needs allocation infrastructure |
| **Weak/unowned references** | 4 test skips. Requires ownership tracking infrastructure |
| **Remap `Swift.CIContext` to `CoreImage.CIContext`** | Last hand-rolled `Swift.*` ObjC wrapper. Imports `$sSo9CIContextCABycfC` from CoreImage which doesn't exist as a Swift dispatch thunk (CIContext is an ObjC class — `init` dispatches via `+[CIContext new]`/`objc_msgSend`, not via Swift). Same root cause as the 5 wrappers deleted in 2026-04. Cleanup needs: (1) delete `src/Swift.Runtime/src/Swift/CIContext.cs`, (2) remove `Swift.CIContext` registration in `SwiftFrameworkResolver.cs`, (3) update `CoreImageDatabase.xml` to remap `CIContext` → `CoreImage.CIContext` with `objcBridged="true"`, (4) verify validation gates remain green (no test currently exercises CIContext, so blast radius should be zero). |
| **Constrained-generic PWT plumbing for non-accessor P/Invokes** | The 0.7.0 fix threads parent-type PWTs through the type-metadata-accessor P/Invoke (`PInvoke_getMetadata`). Four sibling call sites still pass only the bare `GetMetadataArgumentList()` and would undercount PWTs for a constrained-generic parent: `EnumHandler.RawRepresentable.cs:146,254` (`PInvoke_InitWithRawValue` for generic raw-representable enums) and `OperatorHandler.cs:453,481` (operator P/Invokes on constrained-generic parents). `PInvokeEmitter.HandleProtocolConformance` only adds PWTs for method-level generic params, not parent-type params, so these paths have the same class of latent ABI mismatch. Not triggered by any current validation library — leave alone until a repro surfaces, then widen `GetTypeMetadataAccessorArgumentList()` (or a renamed variant) to cover these sites. Same end-state goal as the accessor fix: the emitted C# signature must match what Swift's ABI actually expects. See `constrained-generic-metadata-witness-tables.md` for the type-accessor precedent. |
| **Wrapper-helper path dynamic PWT resolution** | `MetatypeHelperEmitter.GetResolvablePwtParameterCount` silently excludes Self-requirement / associated-type protocols on the Swift wrapper side. The 0.7.0 fix added a `swift_conformsToProtocol` runtime fallback on the **C#** P/Invoke side; the Swift wrapper helper path is still fail-closed via `HasUnresolvableTypeConformances` and `WouldExceedRegisterArgumentThreshold` gates in `GenericDispatchEmitter.CanEmitGenericDispatch`. Teaching the Swift wrapper to dynamically resolve descriptors via `dlsym` + `swift_conformsToProtocol` from Swift code would unblock methods/properties/constructors on these parents. Not triggered by any current validation library. |
| **Foreign value-type metadata registration gap** | TypeDB maps .NET value types to ObjC-bridged Swift types at generator time (e.g. `System.Guid ↔ Foundation.UUID` in `FoundationDatabase.xml`, `System.DateTime ↔ Foundation.Date`, `System.Decimal ↔ Foundation.Decimal`), but there are no corresponding runtime `RegisterMetadata` calls. Consequence: any `SwiftOptional<Guid>`/`SwiftOptional<DateTime>` cctor chains through `TypeMetadataCache.GetTypeMetadataOrThrow<T>()` and throws at first use. Discovered while picking a StoreKit 2 smoke-test accessor (Session 5): `AppStore.deviceVerificationID` returns `SwiftOptional<Guid>` and is unusable because of this, forcing the pivot to `AppStore.canMakePayments` (bool return). Likely surfaces again in Session 6 against `Transaction` / `VerificationResult<Transaction>` which almost certainly expose UUIDs / Dates through Optional. Fix: wire a runtime `RegisterMetadata` call for each generator-level foreign value-type mapping so the metadata accessor resolves to the ObjC-bridged layout at runtime. Generalizes across all Apple frameworks, not just StoreKit. |

---

## Blocked (Confirmed Upstream Only)

These are the **only** confirmed upstream issues. There are exactly 5 (reproduced in standalone repro at `/Users/wojo/Dev/swift-interop-repro/`). If a crash doesn't match one of these, it's our bug. See `feedback_mono_jit_blame.md` for the full investigation checklist.

| # | Issue | Blocked By |
|---|-------|-----------|
| 1 | **Non-blittable type rejection with CallConvSwift** | .NET runtime design limitation. Workaround: @_cdecl wrappers (already covers 78.5% of P/Invokes) |
| 2 | **NativeAOT: custom struct float/double in GPR instead of FPR** | NativeAOT JIT `SwiftPhysicalLowering.cs` register allocation bug. System types (CGRect) work; custom structs fail |
| 3 | **NativeAOT: custom integer struct >16B SIGSEGV** | Struct passed by pointer instead of in registers. NativeAOT (>24B), Mono (>32B) |
| 4 | **NativeAOT: multi-type-parameter generic SIGSEGV** | 1 type param works, 2+ type params SIGSEGV |
| 5 | **Mono: SafeHandle async lifetime** (inferred, not independently reproduced) | GC may collect SafeHandle during async suspension |

| Other | Status |
|-------|--------|
| **Non-Int32 enum raw values** | Blocked on Swift compiler: `.swiftinterface` strips integer raw values. No workaround. 1 skipped test. |

---

## Not Worth Addressing

| Skip Reason | Count | Why Not |
|-------------|------:|---------|
| @_spi / internal members | ~724 | Correct behavior — private API should not be bound |
| Synthesized Codable | ~178 | .NET consumers use own serialization (`System.Text.Json`, etc.) |
| SwiftUI/Combine dependencies | ~118 | Framework boundary — consumers use SwiftUI bridge instead |
| Generic protocol constraints / PATs | ~67 | Architecturally blocked by associated type erasure |
| Unsupported existential (opaque generics) | ~44 | Fundamental limitation of Swift's type system vs C# generics |

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

