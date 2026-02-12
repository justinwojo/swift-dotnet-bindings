# Swift Bindings - Current Status

**Last Updated**: February 2026 (Binding API WU1-WU6 + Codex review fixes + Mono JIT mitigation + Tier promotion + Doc comments)
**Unit Tests**: 1,929 passed
**Runtime Tests**: 185 passing on iOS Simulator at Tier 2 safe-only (28 pre-existing failures)
**Integration Tests**: 699 passing (11 skipped, pre-existing)
**Libraries Tested**: Nuke, BlinkID, BlinkIDUX, BridgeParamTest, Lottie, CryptoSwift

---

## Compilation Status

| Library | Generator Errors | Runtime Validation |
|---------|------------------|-------------------|
| **Nuke** | 0 | Full runtime validation |
| **BlinkID** | 0 | Full runtime validation (18/18 tests) |
| **BlinkIDUX** | 0 | SwiftUI bridge validation (16/16 tests) |
| **BridgeParamTest** | 0 | v2 param type validation (35/35 tests) |
| **Lottie** | 0 | Full runtime + SwiftUI bridge validation (15/15 tests) |
| **CryptoSwift** | 0 | Binding coverage validation (89.2% members, no runtime tests) |

### Binding Coverage

Assessed after Phase 62 (ArraySlice normalization).

| Library | Types | Type % | Members | Member % |
|---------|-------|--------|---------|----------|
| BlinkID | 116/119 | 97.5% | 567/572 | 99.1% |
| BlinkIDUX | 36/45 | 80.0% | 128/172 | 74.4% |
| Nuke | 60/68 | 88.2% | 323/342 | 94.4% |
| Lottie | 79/93 | 84.9% | 387/428 | 90.4% |
| CryptoSwift | 103/103 | 100% | 441/501 | 88.0% |

Remaining member gaps are primarily unsupported existential type arguments in bound generics (26 skips across Nuke/Lottie), UIKit/Foundation types not in TypeDatabase, and ArraySlice in unsupported contexts (closures, inout). CryptoSwift's 60 skipped members break down as: 24 unsupported operators (20 compound assignment `+=`/`-=`/etc. + 4 overflow shift `&<<`/`&>>`/etc. — no C# equivalent), 14 generic protocol closure signatures (`worker()` on block cipher modes), 6 AnyType property fallbacks, 4 static protocol members, and 12 internal methods (correctly excluded — wrappers can't access `@usableFromInline internal` or `@inlinable internal` methods). See `Future/unsupported-existential-analysis.md` for details.

### TestFramework Coverage

| Metric | Value |
|--------|-------|
| Must-pass features | 94/94 passing |
| Degraded | 0 |
| Compiled-out | See disabled dirs |
| Known-unsupported | See testing-gaps.md |
| Runtime tests | 185 passing at Tier 2 safe-only |

Tier promotion pass added runtime tests across string/enum/class/closure/composition/blittable marshalling. Coverage includes error handling, SwiftUI bridge, operators, optionals, tuples, pointers, arrays, closures, and protocol proxies.

---

## What Works

### Types
- Classes (with ARC via SafeHandle)
- Structs (frozen and non-frozen)
- Enums (with associated values, raw representable including String raw values, runtime enum case construction)
- Protocols (interface generation, proxy generation, conformance emission, witness table dispatch for blittable + String types)
- Generics (bound generics, generic enums, generic classes, unbound generic type parameters in properties/methods/constructors)
- Actors (detected via Actor protocol conformance, emitted as classes)
- Existential containers (protocol composition, existential type arguments in bound generics)

### Members
- Methods (instance, static, async via Swift wrapper generation)
- Properties (getters and setters, including witness dispatch through protocol proxies)
- Operators (+, -, ==, !=, <, >, etc. with automatic pair synthesis, null-safe equality on reference types)
- Constructors (including failable `init?` as `TryCreate()` factory methods)
- Inout parameters (emitted as `ref` in C#)
- Subscripts (as C# indexers)

### Special Types
- SwiftString, SwiftArray\<T>, SwiftSet\<T>, SwiftOptional\<T>
- ArraySlice\<T> parameters (normalized to Array\<T> via Swift wrapper with `ArraySlice()` conversion at call site)
- Closures (@convention(c), @escaping with frozen types, throwing closures)
- Tuples (1-7 elements, named elements preserved)
- Opaque return types (`some Protocol` → existential container via Swift wrapper)
- CoreGraphics opaque types (CGImage, CGColor, CGContext → IntPtr)
- Swift pointer types (OpaquePointer, UnsafePointer, UnsafeMutablePointer → IntPtr)
- NSObject subclass parameters (ObjC bridged marshalling pipeline)
- Async return marshalling for String, Array\<String>, classes, enums, and structs (via `@convention(c)` callbacks)

### DX Features
- Binding completeness report (`binding-report.json`) with workaround recommendations
- `[UnsupportedSwiftType]` attribute on degraded members
- Skip reasons in report (UnsupportedSignature, AnyTypeFallback, AsyncProperty, etc.)
- Configurable namespace mapping
- Async property detection via TBD symbol analysis
- Idiomatic C# API surface: verb-prefixed methods, `string` properties, `T?` optionals, `nint` integers, `IDisposable`, real constructors, clean interface names
- XML doc comments from Swift symbol graphs (automatic in xcframework mode, opt-out via `--no-docs`)
- Mono JIT crash mitigation: SwiftString wrappers, closure Cdecl expansion, existential metadata wrappers, signature risk detection

---

## What Doesn't Work

### Architectural Gaps
- **Full protocol witness dispatch** — Blittable and String types dispatch through witness table. Setters work for blittable + String. Mutating methods, throws, and async not yet supported.
- **Actor isolation enforcement** — Actor methods callable without async/await from C# (Swift runtime handles isolation internally)

### Framework Limitations
- **SwiftUI Views** — Skipped by generator; auto-generated interop bridge via UIHostingController validated for simple and async views. v2 supports primitives, String, Bool, closures, BoundEnum, BoundType, Optional variants, ABI-driven async inference, data-driven emission, cross-module type resolution with null-pointer safety, and bridge hints (`bridge-hints.json`) for user overrides (skip, forceTemplate, preferredInit, asyncPattern, extraSwiftImports). 13 SwiftUI bridge features tracked in TestFramework coverage matrix (enum/class/closure/optional/async Views). Runtime tests at Tier 2. BridgeTest (`BindingTesting/BridgeTest/`) retained as shadow validation.
- **Combine** — `@Published` properties and reactive streams not bridged

### Edge Cases
- **8+ element tuples** — Would require ValueTuple nesting
- **Closures within closures** — Not supported
- **Generic associated types** — PATs limited
- **Async+throwing closures at runtime** — Binding generation works but runtime blocked by existential metadata Mono JIT bug

### Known Runtime Issues
- **Mono JIT assertion (jit-info.c:918)**: `condition '!ji->async' not met` — kills process when any closure is passed through P/Invoke (both `@convention(c)` and `@escaping`) or when `SwiftString.PInvoke_GetLength` is called via `CallConvSwift`. Affects all closure tests and SwiftString property access at runtime. Workaround: these tests are deferred to Tier 3 (nightly). Bridge tests use `@_cdecl` functions which are unaffected.
- **Mono JIT**: `swift_getExistentialTypeMetadata` crash when creating `SwiftArray<ExistentialContainer>` (workaround: Swift wrapper functions)
- **Non-blittable CallConvSwift**: Mono JIT rejects non-blittable types with Swift calling convention (workaround: `IntPtr` + manual marshalling)
- **SafeHandle in async**: .NET runtime doesn't preserve SafeHandle through async P/Invoke (workaround: singleton pattern + IntPtr conversion)
- See `known-issues-workarounds.md` for full details and `Future/upstream-bug-reports-draft.md` for draft .NET runtime bug reports

---

## Development History

70+ phases of improvements tracked in git history. Key milestones:

| Phase | Highlights |
|-------|------------|
| 1-15 | Core infrastructure, Nuke validation |
| 16-29 | Type system and runtime fixes |
| 30-33 | Generic type improvements |
| 34-39 | Codex task completion (operators, enums, reporting) |
| 40-42 | Protocol conformance infrastructure, namespace mapping, Lottie runtime (8/9) |
| 43 | Protocol conformance emission, opaque returns, async properties, actors |
| 44 | Inout parameters, failable initializers |
| 45 | Pointer types, NSObject parameters, finalizer safety net |
| 46 | Unbound generic type parameters |
| 47 | Protocol runtime completion (NotImplementedException → NotSupportedException) |
| 48 | Generic tuple return marshalling, null-safe equality operators, Lottie 9/9 |
| 49 | Async concurrency hook shared library (SwiftConcurrency.Initialize) |
| 50 | Existential type arguments in bound generics, MethodHandler decomposition (3.8K → 7 files) |
| 51 | Binding report workarounds, existential bypass wrapper automation |
| 52 | Protocol witness dispatch Phase A (blittable read-only), SwiftArray convenience methods |
| 53 | Witness dispatch Phase B (String marshalling, property setters), BlinkID runtime (15/18) |
| 54 | Static protocol member fix (BlinkID 0 compile errors) |
| 55 | String enum raw value UTF-8 marshalling |
| 56 | Protocol conformance validation (Nuke/Lottie 0 compile errors) |
| 57 | Protocol runtime tests (20 tests) |
| 58 | Async String callback marshalling |
| 59 | Async Array\<String> callback marshalling |
| 60 | Async complex type callback marshalling (BlinkID 18/18) |
| 61 | Fix IntPtr\<T> generic emission bug (integration tests 0 compile errors) |
| 62 | ArraySlice parameter normalization via Swift wrappers, CryptoSwift validation (91.6%), `IsMutating` + `UsesWrapperLibrary` model additions |
| H1-H2 | Unit test gaps + 6 library binding bugs → all 4 libraries 0 errors |
| I1/I1a/I1b | Mono JIT mitigation: Nuke wrapper path, BitwiseCopyable fix, ObjC async callback marshalling |
| K | Swift doc comments → C# XML doc comments (30 unit tests) |
| Strategy D | MonoJitRiskDetector static analysis (34 unit tests) |
| Strategy B | Closure Cdecl expansion for Mono JIT crash mitigation (38 unit tests) |
| Tier Promo | Tj dispatch thunks + IsFinal + closure/composition/string tier promotions (185 runtime tests) |
| WU1-WU6 | Idiomatic C# binding API: verb prefixes, async stripping, array element conversion, subscript conversion, parameter normalization, unsafe removal |
| Codex Fixes | Protocol proxy type asymmetry, Optional<Array<String>> marshalling, GetSwiftWrapperType raw elements, async-void Get prefix |

SwiftUI Bridge v2 phases ran in parallel with core generator improvements:

| Phase | Highlights |
|-------|------------|
| v2 Phase 1A | BoundEnum + Optional\<Primitive\|Enum> parameter support |
| v2 Phase 1B | BoundType class parameter + Optional\<BoundType> support |
| v2 Phase 1C | TypedClosure `(T...) -> R` parameter support |
| v2 Phase 1 | Runtime validation: 26/26 BridgeParamTest tests on iOS Simulator |
| v2 Phase 2A | ABI-driven async inference (constructor chain resolution, cycle detection, depth limiting) |
| v2 Phase 2B | Data-driven emission from inferred chains (mixed chain + leaf params, async mangled-name fallback) |
| v2 Phase 2C | Cross-module async inference via TypeDatabase (BoundType/BoundEnum), null-pointer safety, Lottie SwiftUI bridge (15/15) |
| v2 Phase 3 | Bridge hints JSON sidecar (skip, forceTemplate, preferredInit, asyncPattern, extraSwiftImports) with discovery, validation, and safe stale cleanup |

TestFramework Phases A-D ran in parallel, adding ~184 runtime tests across string/enum/class/blittable marshalling, ownership lifecycle, negative paths, stress tests, arrays, optionals, tuples, pointers, operators, and closures.

CryptoSwift fix steps (9 total) addressed 24 generator bugs spanning P/Invoke enum handling, constructor projection, operators, tuples, EveryProtocol vtable/index integrity, protocol proxy alignment, wrapper extension filtering, protocol composition return types, and swiftinterface-based internal member detection. All 9 steps verified via `verify-fix-order.sh all` (25 PASS, 0 FAIL). Generated `Swift.CryptoSwift.swift` typechecks with 0 errors.

---

## Active Documentation

| File | Purpose |
|------|---------|
| `swiftui-bridge-design.md` | SwiftUI View bridge pattern via UIHostingController, including bridge hints (pre-release blocker) |
| `roadmap.md` | Active work queue (single source of truth) |
| `known-issues-workarounds.md` | Three active Mono runtime blockers and workarounds |
| `testing-gaps.md` | Known testing gaps across all layers |
| `Future/binding-api-future-work.md` | Remaining binding API improvements (ExistentialContainer, AnyType, exception mapping, etc.) |
| `Future/mono-jit-future-work.md` | Remaining Mono JIT edge cases (VWT Destroy, non-primitive closures, N-protocol existentials) |
| `Future/emitter-redesign-proposal.md` | Architectural north star for emitter refactoring |
| `Future/nativeaot-investigation.md` | NativeAOT desk research (hands-on validation pending) |
| `Future/upstream-bug-reports-draft.md` | Draft .NET runtime bug reports (waiting for repo to go public) |
| `Future/interop-performance-validation-plan.md` | Performance benchmarking plan |
| `Completed/` | Archived phase completion records |
