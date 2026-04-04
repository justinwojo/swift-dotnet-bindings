# Roadmap to 1.0

**Updated**: April 4, 2026

**Current baseline**: 95/95 CS compile, 61/61 Swift compile. All targets passing.
**Skip metrics**: 10,757 emitted members, 1,971 skipped (15.5% skip rate) across 95 validation targets.
**Runtime tests**: 1,292 passed, 20 skipped on iOS Device (NativeAOT). 1,668 passed, 2 skipped across 8 real-world library test suites (swift-dotnet-packages).
**Downstream validation**: 2,107/2,107 sim tests passing across 23 libraries (swift-dotnet-packages + sim-validation). Zero regressions on 0.6.0 packages.

> **Every skipped test is guilty until proven innocent.** 102/102 tests previously blamed on Mono JIT were proven to be generator/runtime bugs in our code. There are exactly 5 confirmed upstream .NET runtime bugs (see `Blocked` section below + memory `feedback_mono_jit_blame.md`). If a crash doesn't match one of these, it's our bug.

---

## 1.0 Goal

**Any xcframework to full-featured NuGet package.** Cover as much of the Swift API surface as possible, validate it works at runtime, and ensure the end-to-end experience is solid.

---

## Theme A: Skip Reduction *(low priority)*

Skip rate is 15.5%. The remaining ~1,971 skips are overwhelmingly either correct behavior (private API, synthesized Codable) or architecturally blocked. Consumer-impactful patterns (`Result<T,E>`, common generics, protocol conformances) are already covered. Further reduction has diminishing returns.

| Item | Remaining skips | Effort | Why low priority |
|------|----------------:|--------|-----------------|
| **Unsupported signatures** (associated types, bare generics) | ~346 | Very high | Swift patterns with no C# equivalent |
| **AnyTypeFallback** (cross-library types) | ~307 | Very high | Needs full dependency graph resolution — different product scope |
| **UnsupportedClosure** (multi-blocker methods) | ~153 | High | Each has 2-3 overlapping blockers |
| **UnsatisfiedGenericConstraint** (remaining) | ~92 | High | Fundamental type system constraints, not relaxable gates |
| **Result<T,E> parameter direction** | blocked | Medium | Needs native payload synthesis for C#-created instances |
| **Multi-protocol generic compositions** | blocked | High | Needs full existential composition in @_cdecl wrapper |
| **Value-type generic conformers** | blocked | High | Requires non-AnyObject transport through @_cdecl boundary |
| **NativeAOT MCB callback SIGSEGV** | 3 tests | Low | Only runtime skips that might be our bugs. Resolve or classify before 1.0 |
| **NativeAOT ResultReturnTests crash** | 6 tests | Medium | Device-only: app crashes at class init before any test runs. Blind-skipped by harness. Needs investigation — may be upstream NativeAOT or a generator bug in result-return marshalling. |

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

## Theme C: Developer Experience Hardening

**1-2 sessions.** Significant investment already made. Focus on validation and rough edges, not new features.

Planning doc: `dx-hardening-sessions.md` (not yet written)

Areas to cover:
- End-to-end golden path validation (clean machine perspective)
- Error messages and diagnostics review (consumer-facing, not internal)
- Template and SDK polish
- Documentation gaps in wiki

---

## Theme D: Upstream Bug Reports + Repro Repo

**1 session.** Clean up the repro repo at `/Users/wojo/Dev/swift-interop-repro/`, get it on GitHub, file the 7 draft bug reports with reproduction steps.

Planning doc: `upstream-bug-reports-session.md` (not yet written)

Drafts: [`Future/upstream-bug-reports-draft.md`](Future/upstream-bug-reports-draft.md)

---

## Theme E: Multi-Platform Hardening

**1-2 sessions.** We claim support for macOS, Mac Catalyst, and tvOS but have done very little testing beyond iOS. Need to validate before 1.0.

Planning doc: `multi-platform-hardening-sessions.md` (not yet written)

Areas to cover:
- Validate xcframework slicing and platform detection for macOS / Mac Catalyst / tvOS
- Runtime tests on non-iOS platforms (at minimum macOS — most accessible)
- SDK/NuGet packaging: verify platform-specific TFMs and runtime identifiers resolve correctly
- Identify and fix any iOS-only assumptions in the generator or runtime

---

## Lower Priority / Post-1.0

| Item | Notes |
|------|-------|
| **Performance benchmarks** | Baseline P/Invoke overhead measurement. [`Future/interop-performance-validation-plan.md`](Future/interop-performance-validation-plan.md) |
| **API snapshot tooling** | Detect API surface drift between versions. [`Future/api-snapshot-tooling.md`](Future/api-snapshot-tooling.md) |
| **SwiftUI beyond current level** | Wait for consumer feedback before investing further |
| **Custom actor types** | Niche — requires async dispatch through actor's serial executor |
| **Property wrappers / KeyPaths** | Low frequency in public API surfaces |
| **Static protocol constructors** | Init witness dispatch needs allocation infrastructure |
| **Weak/unowned references** | 4 test skips. Requires ownership tracking infrastructure |

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
| **Upstream bug reports** (7 drafts) | Blocked on repro repo cleanup. See Theme D. |

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
