# Swift Bindings - Current Status

**Last Updated**: February 2026 (Phase 61 + SwiftUI Bridge v2 Phase 2B)
**Unit Tests**: 1,413 passed
**Libraries Tested**: Nuke, BlinkID, BlinkIDUX, BridgeParamTest, Lottie

---

## Compilation Status

| Library | Generator Errors | Runtime Validation |
|---------|------------------|-------------------|
| **Nuke** | 0 | Full runtime validation |
| **BlinkID** | 0 | Full runtime validation (18/18 tests) |
| **BlinkIDUX** | 0 | SwiftUI bridge validation (16/16 tests) |
| **BridgeParamTest** | 0 | v2 param type validation (26/26 tests) |
| **Lottie** | 0 | Full runtime validation (9/9 tests) |

### Binding Coverage

Assessed after Phase 60 (async complex type marshalling).

| Library | Types | Type % | Members | Member % |
|---------|-------|--------|---------|----------|
| BlinkID | 116/119 | 97.5% | 567/572 | 99.1% |
| BlinkIDUX | 36/45 | 80.0% | 128/172 | 74.4% |
| Nuke | 60/68 | 88.2% | 323/342 | 94.4% |
| Lottie | 79/93 | 84.9% | 387/428 | 90.4% |

Remaining member gaps are primarily unsupported existential type arguments in bound generics (26 skips across Nuke/Lottie) and UIKit/Foundation types not in TypeDatabase. See `remaining-work.md` for details.

### TestFramework Coverage

| Metric | Value |
|--------|-------|
| Must-pass features | 93 |
| Passing | 92 (98.9%) |
| Degraded | 1 |
| Known-unsupported | 52 |
| Types emitted | 151/168 (89.9%) |
| Members emitted | 673/747 (90.1%) |

Remaining degraded feature: `any_protocol_existential` — 1 skip (UnsupportedExistential: `describeAll([any Describable])` requires `SwiftArray<ExistentialContainer>` runtime support).

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

---

## What Doesn't Work

### Architectural Gaps
- **Full protocol witness dispatch** — Blittable and String types dispatch through witness table. Setters work for blittable + String. Mutating methods, throws, and async not yet supported.
- **Actor isolation enforcement** — Actor methods callable without async/await from C# (Swift runtime handles isolation internally)

### Framework Limitations
- **SwiftUI Views** — Skipped by generator; auto-generated interop bridge via UIHostingController validated for simple and async views. v2 Phase 1 supports primitives, String, Bool, closures, BoundEnum, BoundType, Optional variants (26/26 BridgeParamTest). Phase 2A adds ABI-driven async inference (constructor chain resolution, same-module only). Phase 2B adds data-driven emission from inferred chains (mixed chain + leaf params, async detection fallback).
- **Combine** — `@Published` properties and reactive streams not bridged

### Edge Cases
- **8+ element tuples** — Would require ValueTuple nesting
- **Closures within closures** — Not supported
- **Generic associated types** — PATs limited
- **Async+throwing closures at runtime** — Binding generation works but runtime blocked by existential metadata Mono JIT bug

### Known Runtime Issues
- **Mono JIT**: `swift_getExistentialTypeMetadata` crash when creating `SwiftArray<ExistentialContainer>` (workaround: Swift wrapper functions)
- **Non-blittable CallConvSwift**: Mono JIT rejects non-blittable types with Swift calling convention (workaround: `IntPtr` + manual marshalling)
- **SafeHandle in async**: .NET runtime doesn't preserve SafeHandle through async P/Invoke (workaround: singleton pattern + IntPtr conversion)
- See `known-issues-workarounds.md` for full details and `Future/upstream-bug-reports-draft.md` for draft .NET runtime bug reports

---

## Development History

61 phases of improvements tracked in git history. Key milestones:

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

SwiftUI Bridge v2 phases ran in parallel with core generator improvements:

| Phase | Highlights |
|-------|------------|
| v2 Phase 1A | BoundEnum + Optional\<Primitive\|Enum> parameter support |
| v2 Phase 1B | BoundType class parameter + Optional\<BoundType> support |
| v2 Phase 1C | TypedClosure `(T...) -> R` parameter support |
| v2 Phase 1 | Runtime validation: 26/26 BridgeParamTest tests on iOS Simulator |
| v2 Phase 2A | ABI-driven async inference (constructor chain resolution, cycle detection, depth limiting) |
| v2 Phase 2B | Data-driven emission from inferred chains (mixed chain + leaf params, async mangled-name fallback) |

TestFramework Phases A-D ran in parallel, adding ~184 runtime tests across string/enum/class/blittable marshalling, ownership lifecycle, negative paths, stress tests, arrays, optionals, tuples, pointers, operators, and closures.

---

## Active Documentation

| File | Purpose |
|------|---------|
| `swiftui-bridge-design.md` | SwiftUI View bridge pattern via UIHostingController (pre-release blocker) |
| `remaining-work.md` | Consolidated backlog (2 open items: Roslyn analyzer, NativeAOT validation) |
| `known-issues-workarounds.md` | Three active Mono runtime blockers and workarounds |
| `testing-gaps.md` | Known testing gaps across all layers |
| `Future/emitter-redesign-proposal.md` | Architectural north star for emitter refactoring |
| `Future/nativeaot-investigation.md` | NativeAOT desk research (hands-on validation pending) |
| `Future/upstream-bug-reports-draft.md` | Draft .NET runtime bug reports (waiting for repo to go public) |
| `Future/interop-performance-validation-plan.md` | Performance benchmarking plan |
| `CompletedPhases/` | Archived phase completion records |
