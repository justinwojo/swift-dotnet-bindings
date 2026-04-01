# Roadmap

**Updated**: April 1, 2026

**Current baseline**: 95/95 CS compile, 58/61 Swift compile (GRDB + RichTextKit + YouTubePlayerKit: known non-binding failures).
**Skip metrics**: 10,718 emitted members, 2,038 skipped (16% skip rate) across 95 validation targets.
**Downstream validation**: 630/630 sim tests passing across 20 libraries (swift-dotnet-packages + sim-validation). Zero regressions on 0.5.0-dev packages.

> **Every skipped test is guilty until proven innocent.** 102/102 tests previously blamed on Mono JIT were proven to be generator/runtime bugs in our code. There are exactly 5 confirmed upstream .NET runtime bugs (see `Blocked` section below + memory `feedback_mono_jit_blame.md`). If a crash doesn't match one of these, it's our bug. Investigate generated C#/Swift wrapper signatures before ever labeling a failure as upstream.

**Completed work**: Runtime test sessions 1-10, validation coverage sessions 11-13, and pre-release sessions 1-5 are archived in `Completed/`.

---

## Remaining Small Items

No remaining small items.

---

## Hard / Deferred

High skip counts but architecturally difficult. Not scheduled unless a specific consumer need drives them.

| Item | Skips | Libraries | Why Deferred |
|------|------:|----------:|-------------|
| **Unsupported signatures** (associated type refs, placeholder types) | ~353 | ~37 | Requires associated type resolution through conformance graph |
| **Generic type contexts** (generic parent leaks into wrapper) | ~349 | ~14 | Needs type-erased dispatch for non-final generic class members |
| **Method-level generics** (`func foo<T>(...)`) | ~179 | ~13 | Requires specialization or type-erased wrappers; C# has no generic constructors |
| **Protocol extension associated type context** | — | GRDB | 666 errors contained by gate. Needs full generic constraint context. EC-17 |
| **Architectural generic closures** | ~26 | RxSwift, Alamofire | Generic type parameters in closures (`(τ_0_0) -> τ_1_0`). GenericClosureBridge handles narrow pattern (sync, method-generic, noescape, identity-forwarding). Broader coverage needs monomorphization. |
| **Existential params in closures** | ~27 | Alamofire, RxSwift | `Result<T, any Error>`, union types with existential args. Can't fit existential container in C function pointer ABI. Would need delegate-based wrapper. |
| **ArraySlice<T> closures** | ~9 | CryptoSwift | `ArraySlice<UInt8>` cipher operations. No ArraySlice-to-Array conversion in closure context. |
| **Existential/protocol return closures** | ~4 | Various | Abstract return type, no ABI for constructing existential from void*. |
| **Unsupported generic containers** | ~71 | ~20 | `Result<T,E>`, `Optional<existential>` |
| **Custom actor types** (`actor Counter`) | — | 5+ | Requires async dispatch through actor's serial executor |
| **Static protocol constructors** (`Create()` factory) | — | — | Init witness dispatch needs allocation infrastructure |
| **Weak/unowned references** | 4 tests | — | Requires ownership tracking infrastructure (LeakDetectionTests) |
| **SwiftUI WritableKeyPath bridging** | — | RichTextKit (2 views) | Picker/Toggle use `WritableKeyPath<Binding<T>, T>` for two-way property access. No direct C# equivalent. |
| **SwiftUI existential protocol params** | — | RichTextKit (1 view), WhatsNewKit (1 view) | `any Protocol` and protocol compositions in view init params or closure args. Needs witness table bridging. |
| **SwiftUI generic type params on Views** | — | RichTextKit (1 view) | View itself has generic type parameter. Needs monomorphization or type erasure at ABI boundary. |
| **SwiftUI Result<T,E> in closures** | — | CodeScanner (1 view) | `(Result<ScanResult, ScanError>) -> Void`. Needs result decomposition into success/error branches. |
| **SwiftUI Optional<ExternalClass>** | — | CodeScanner (1 view) | `Optional<AVCaptureDevice>`. Needs optional handling for TypeDB-resolved BoundType params. |

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
| **Upstream bug reports** (7 drafts) | Blocked on repo going public. Drafts: `Future/upstream-bug-reports-draft.md` |

---

## Future Vision

| Item | Effort | Notes |
|------|--------|-------|
| **Performance benchmarks** | Medium | `Future/interop-performance-validation-plan.md` |
| **API snapshot tooling** (detect API surface drift) | Medium | `Future/api-snapshot-tooling.md` |

---

## Not Worth Addressing

| Skip Reason | Count | Why Not |
|-------------|------:|---------|
| @_spi / internal members | ~795 | Correct behavior — private API should not be bound |
| Synthesized Codable | ~155 | .NET consumers use own serialization (`System.Text.Json`, etc.) |
| SwiftUI/Combine dependencies | ~60 | Framework boundary — consumers use SwiftUI bridge instead |
| Generic protocol constraints / PATs | ~68 | Architecturally blocked by associated type erasure |
| Unsatisfied ISwiftObject | ~104 | Fundamental type system constraint — generic args must be projectable |

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
