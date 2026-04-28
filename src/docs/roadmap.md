# Roadmap to 1.0

This doc covers longer-term themes, blocked items, and post-1.0 ideas. Live baseline counts live in `.validation-baseline.json`; per-library status lives with each package.

> **Every skipped test is guilty until proven innocent.** 102/102 tests previously blamed on Mono JIT were proven to be generator/runtime bugs in our code. There are exactly 5 confirmed upstream .NET runtime bugs (see `Blocked` section below + memory `feedback_mono_jit_blame.md`). If a crash doesn't match one of these, it's our bug.

---

## 1.0 Goal

**Bridge .NET MAUI to Apple's Swift-first platform APIs, plus select third-party Swift SDKs that fill real gaps.** Measured by what shipping packages exist and where they run, not by skip percentages.

---

## Theme A: Skip Reduction *(low priority)*

Skip rate is 15.1%. The remaining ~1,923 skips are overwhelmingly either correct behavior (private API, synthesized Codable) or architecturally blocked. Consumer-impactful patterns (`Result<T,E>`, common generics, protocol conformances) are already covered. Further reduction has diminishing returns.

| Item | Remaining skips | Effort | Why low priority |
|------|----------------:|--------|-----------------|
| **Unsupported signatures** (associated types, bare generics) | ~341 | Very high | Swift patterns with no C# equivalent |
| **AnyTypeFallback** (cross-library types) | ~303 | Very high | Needs full dependency graph resolution — different product scope |
| **UnsupportedClosure** (multi-blocker methods) | ~131 | High | Reduced via setter-only closure properties and the async-closure bridge (throwing 0–3 args with primitive returns plus zero-arg `Foundation.Data` return; non-throwing 0–3 args with primitive returns only). Remaining are generic params, nested closures, and async-closure shapes outside the supported arg/return matrix (e.g., arg-bearing `Data` returns, non-throwing `Data` returns). |
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

Generator, SDK, runtime, and build infrastructure all properly support iOS, macOS, Mac Catalyst, and tvOS. Multi-TFM NuGet packaging validated end-to-end with StoreKit2 (4 TFMs in one nupkg). The `nuke binding-tests` target runs each platform via composable flags (`--sim --device --macos --catalyst --tvos`).

---

## Lower Priority / Post-1.0

| Item | Notes |
|------|-------|
| **Performance benchmarks** | Baseline P/Invoke overhead measurement. [`Future/interop-performance-validation-plan.md`](Future/interop-performance-validation-plan.md) |
| **API snapshot tooling** | Detect API surface drift between versions. [`Future/api-snapshot-tooling.md`](Future/api-snapshot-tooling.md) |
| **SwiftUI beyond current level** | Wait for consumer feedback before investing further |
| **Custom-global-actor constructors — runtime executor-aware dispatch** | Status today: (1) the marker-protocol metadata gap is closed, so `Sendable` / `Copyable` / `Escapable` / `SendableMetatype` / `_Concurrency.Actor` / `UnownedSerialExecutor` resolve through the type DB and non-init members on `@<Actor>`-isolated classes survive `MemberEmissionValidator` — methods, stored-property getters, and stored-property setters reach C# normally. (2) Custom-global-actor *constructors* are wholesale-skipped via SWIFTBIND022 regardless of whether the actor `TypeDecl` resolves. An earlier narrowing kept the ctor in the C# binding (with the SB0001 `[Obsolete]` warning) when the actor was reachable, on the assumption a direct `CallConvSwift` call would land on the actor's executor when the caller already was. Device validation showed that assumption is unsound: the implicit metatype/self register layout doesn't survive the NativeAOT thunk, and the call crashes inside Swift's allocating init. There is no Swift-6 synchronous-entry shape we can wrap — `MainActor.assumeIsolated` is a stdlib special case, and `<Actor>.shared.assumeIsolated { _ in init(...) }` enters *instance-actor* isolation, which Swift 6 treats as a different domain than `@<Actor>` *global-actor* isolation (compile error: "call to global actor 'X'-isolated initializer in a synchronous actor-isolated context"). (3) Open follow-up: an async-factory binding pattern, e.g., `static async Task<T> CreateAsync(...)` projected from `Task { @<Actor> in init(...) }`, which would enter the actor's isolation domain through Swift's own concurrency runtime instead of from C#; or upstream Swift evolution exposing a generic synchronous global-actor entry. Either path unblocks construction; until one lands, consumers must construct instances inside Swift (factory functions / framework entry points) and hand the instance back to C# for use. |
| **Property wrappers / KeyPaths** | Low frequency in public API surfaces |
| **Static protocol constructors** | Init witness dispatch needs allocation infrastructure |
| **Weak/unowned references** | 4 test skips. Requires ownership tracking infrastructure |
| **Constrained-generic PWT plumbing for non-accessor P/Invokes** | `EnumHandler.RawRepresentable.cs:146,254` and `OperatorHandler.cs:453,481` still pass bare `GetMetadataArgumentList()`. Not triggered by any current validation library — leave alone until a repro surfaces. See `constrained-generic-metadata-witness-tables.md`. |
| **Wrapper-helper path dynamic PWT resolution** | Swift wrapper side still fail-closed for Self-requirement / associated-type protocols. Not triggered by any current validation library. |
| **Multi-PAT existential boxing** | A type conforming to 2+ PAT protocols cannot box through the `object` fallback because the `typeof(object)` dictionary key is ambiguous. Guarded to fail explicitly (`InvalidCastException`) rather than silently select the wrong witness table. Extremely rare in practice. |
| **tvOS device runner** | Requires provisioning profile + physical Apple TV. Generator, SDK, runtime, and build infra already support tvOS; only the `nuke runtime-tests-tvos-device` Nuke target and deployment mechanism are missing. |

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

