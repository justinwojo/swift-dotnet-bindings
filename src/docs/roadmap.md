# Roadmap to 1.0

**Updated**: April 10, 2026

**Current baseline**: 95/95 CS compile, 61/61 Swift compile. All targets passing.
**Skip metrics**: 10,762 emitted members, 1,956 skipped (15.4% skip rate) across 95 validation targets.

> **Every skipped test is guilty until proven innocent.** 102/102 tests previously blamed on Mono JIT were proven to be generator/runtime bugs in our code. There are exactly 5 confirmed upstream .NET runtime bugs (see `Blocked` section below + memory `feedback_mono_jit_blame.md`). If a crash doesn't match one of these, it's our bug.

**Active release work is in `0.8.0.md`.** This doc covers longer-term themes, blocked items, and post-1.0 ideas.

---

## 1.0 Goal

**Bridge .NET MAUI to Apple's Swift-first platform APIs, plus select third-party Swift SDKs that fill real gaps.** Measured by what shipping packages exist and where they run, not by skip percentages. See `0.8.0.md` for the full thesis.

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

## Theme E: Multi-Platform Hardening *(architecture validated)*

macOS validation audit completed. Generator, SDK, runtime, and build infrastructure all properly support iOS, macOS, Mac Catalyst, and tvOS. Multi-TFM NuGet packaging validated end-to-end with StoreKit2 (4 TFMs in one nupkg).

Remaining items tracked in `0.8.0.md`.

---

## Lower Priority / Post-1.0

| Item | Notes |
|------|-------|
| **Performance benchmarks** | Baseline P/Invoke overhead measurement. [`Future/interop-performance-validation-plan.md`](Future/interop-performance-validation-plan.md) |
| **Bulk retain/release helpers for collections** | Replace per-element `DangerousAddRef`/`DangerousRelease` + P/Invoke loops in `SwiftArray`, `SwiftDictionary`, `SwiftSet` with Swift-side batch helpers. Cuts managed↔native transition cost on large collections. |
| **API snapshot tooling** | Detect API surface drift between versions. [`Future/api-snapshot-tooling.md`](Future/api-snapshot-tooling.md) |
| **SwiftUI beyond current level** | Wait for consumer feedback before investing further |
| **Custom actor types** | Niche — requires async dispatch through actor's serial executor |
| **Property wrappers / KeyPaths** | Low frequency in public API surfaces |
| **Static protocol constructors** | Init witness dispatch needs allocation infrastructure |
| **Weak/unowned references** | 4 test skips. Requires ownership tracking infrastructure |
| **Remap `Swift.CIContext` to `CoreImage.CIContext`** | Last hand-rolled `Swift.*` ObjC wrapper. Cleanup: delete `CIContext.cs`, remove registration, update `CoreImageDatabase.xml`. |
| **Constrained-generic PWT plumbing for non-accessor P/Invokes** | `EnumHandler.RawRepresentable.cs:146,254` and `OperatorHandler.cs:453,481` still pass bare `GetMetadataArgumentList()`. Not triggered by any current validation library — leave alone until a repro surfaces. See `constrained-generic-metadata-witness-tables.md`. |
| **Wrapper-helper path dynamic PWT resolution** | Swift wrapper side still fail-closed for Self-requirement / associated-type protocols. Not triggered by any current validation library. |
| **Self-requirement existential boxing untested** | `GetPublicExistentialType()` lowers `HasSelfRequirement` protocols to `object` at call sites, but no runtime test exercises an `any SelfReqProto`-typed parameter end-to-end. Same-module conformers have `typeof(IFoo<TSelf>)` keyed dictionary entries — whether this round-trips correctly through `GetOrCreate<object>` is unverified. Separate from the PAT fix. |
| **Multi-PAT existential boxing** | A type conforming to 2+ PAT protocols cannot box through the `object` fallback because the `typeof(object)` dictionary key is ambiguous. Guarded to fail explicitly (`InvalidCastException`) rather than silently select the wrong witness table. Extremely rare in practice. |

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

