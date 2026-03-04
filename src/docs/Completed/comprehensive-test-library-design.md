# Comprehensive Swift Test Library Design

**Status**: v2.0 Implemented (memory management, multi-protocol conformance, custom equality)
**Created**: February 2026
**Last Updated**: February 2026 - v2.0: RetainCycles, LeakDetection, multi-protocol conformance, custom equality

---

## Purpose

Create a custom Swift library specifically designed to test the full breadth of Swift language features that the binding generator must handle. This library serves as a **systematic regression suite** that complements the real-world validation provided by third-party bindings (Nuke, BlinkID, Lottie).

---

## Goals

| Goal | Description |
|------|-------------|
| **Systematic coverage** | Deliberately include every Swift construct the generator needs to support |
| **Stability** | Tests don't break due to third-party SDK updates |
| **Debugging clarity** | When failures occur, Swift source is immediately inspectable |
| **Feature-driven development** | Add test cases *before* implementing new features |
| **Minimal size** | Small, focused library without third-party noise |
| **Living documentation** | The library documents what's supported vs. what's not |

---

## Relationship to Existing Tests

### Current Test Pyramid

```
                    ┌─────────────────────┐
                    │   Third-Party SDKs  │  ← Real-world patterns
                    │  (Nuke, BlinkID,    │    (unpredictable, evolving)
                    │   Lottie)           │
                    ├─────────────────────┤
                    │  Integration Tests  │  ← Basic Swift constructs
                    │  (FunctionalTests/) │    (limited coverage)
                    ├─────────────────────┤
                    │    Unit Tests       │  ← Component isolation
                    │  (synthetic data)   │    (no runtime validation)
                    └─────────────────────┘
```

### With Comprehensive Test Library (Implemented)

```
                    ┌─────────────────────┐
                    │   Third-Party SDKs  │  ← "Does it work in the wild?"
                    ├─────────────────────┤
                    │  Comprehensive Test │  ← "Does it handle all Swift features?"
                    │      Library        │    (67 Swift files, 149+ features)
                    ├─────────────────────┤
                    │  Integration Tests  │  ← Keep for quick iteration
                    ├─────────────────────┤
                    │    Unit Tests       │  ← Component isolation
                    └─────────────────────┘
```

### Complementary Roles

| Test Type | Purpose | Stability | Coverage |
|-----------|---------|-----------|----------|
| Unit Tests | Component logic | High | Synthetic patterns |
| Integration Tests | Quick end-to-end | High | Basic constructs |
| **Comprehensive Library** | **All Swift features** | **High** | **Systematic** |
| Third-Party SDKs | Real-world validation | Low (external) | Incidental |

---

## Library Structure

### Directory Layout

```
TestFramework/
├── Package.swift                        # Swift Package Manager manifest
├── build-xcframework.sh                 # Build xcframework for binding generation
├── regenerate-bindings.sh               # Run binding generator against xcframework
├── build-and-test.sh                    # Full pipeline: build + generate bindings
├── generate-coverage-report.sh          # Generate coverage-matrix.json
├── Sources/
│   └── SwiftBindingsTestLib/
│       ├── Types/
│       │   ├── Structs.swift            # ✅ Frozen, non-frozen, nested
│       │   ├── Classes.swift            # ✅ Basic, inheritance, ARC, weak/unowned
│       │   ├── Enums.swift              # ✅ Raw, associated values, generic
│       │   ├── Noncopyable.swift        # ✅ ~Copyable, consuming, borrowing (Swift 6.0+)
│       │   └── InlineArray.swift        # ✅ InlineArray fixed-size type (Swift 6.2+, #if guarded)
│       │
│       ├── Protocols/
│       │   ├── BasicProtocols.swift     # ✅ Simple protocols, properties, methods
│       │   ├── Composition.swift        # ✅ Protocol composition (A & B)
│       │   ├── Conformance.swift        # ✅ Types conforming to protocols
│       │   ├── Conditional.swift        # ✅ Conditional conformance (where clause)
│       │   └── PATs.swift              # ✅ Protocols with associated types
│       │
│       ├── Generics/
│       │   ├── Functions.swift          # ✅ Generic functions
│       │   ├── Types.swift              # ✅ Generic structs/classes, subscripts
│       │   ├── Constraints.swift        # ✅ Where clauses, protocol bounds
│       │   ├── Existentials.swift       # ✅ any Protocol
│       │   ├── KeyPaths.swift           # ✅ KeyPath, WritableKeyPath, key path params
│       │   └── Metatypes.swift          # ✅ T.Type parameter, T.self, metatype return
│       │
│       ├── Closures/
│       │   ├── Escaping.swift           # ✅ @escaping closures
│       │   ├── ConventionC.swift        # ✅ @convention(c) closures
│       │   ├── ClosureReturns.swift     # ✅ Methods returning closures
│       │   └── Autoclosures.swift       # ✅ @autoclosure, @autoclosure @escaping
│       │
│       ├── Async/
│       │   ├── Methods.swift            # ✅ Async methods (instance + static)
│       │   ├── AsyncThrowing.swift      # ✅ Async throwing methods
│       │   ├── MainActor.swift          # ✅ @MainActor class, method
│       │   ├── Sendable.swift           # ✅ Sendable type, @Sendable closure
│       │   ├── Actors.swift             # ✅ Actor type, isolated/nonisolated methods
│       │   ├── AsyncClosures.swift      # ✅ Async closure parameters
│       │   ├── AsyncProperties.swift    # ✅ Async computed properties
│       │   └── IsolationControl.swift   # ✅ nonisolated(unsafe) (Swift 6.1/6.2)
│       │
│       ├── Properties/
│       │   ├── Getters.swift            # ✅ Stored + computed property getters
│       │   ├── Setters.swift            # ✅ Read-write properties
│       │   ├── Static.swift             # ✅ Static properties
│       │   └── Computed.swift           # ✅ Computed properties
│       │
│       ├── Operators/
│       │   ├── Arithmetic.swift         # ✅ +, -, *, /, %
│       │   ├── Comparison.swift         # ✅ ==, !=, <, >, <=, >=
│       │   ├── Bitwise.swift            # ✅ &, |, ^, <<, >>
│       │   └── Unary.swift              # ✅ !, ~, prefix -, prefix +
│       │
│       ├── Tuples/
│       │   ├── BasicTuples.swift        # ✅ 2-7 element tuples
│       │   ├── Named.swift              # ✅ Labeled tuple elements
│       │   └── TupleReturns.swift       # ✅ Methods returning tuples
│       │
│       ├── Initializers/
│       │   ├── BasicInit.swift          # ✅ Standard initializers
│       │   ├── Failable.swift           # ✅ init? and init!
│       │   └── Throwing.swift           # ✅ throws initializers
│       │
│       ├── Parameters/
│       │   ├── Inout.swift              # ✅ inout parameters
│       │   ├── Defaults.swift           # ✅ Default argument values
│       │   └── Variadic.swift           # ✅ Variadic parameters (Int32, String, mixed)
│       │
│       ├── ErrorHandling/
│       │   ├── ThrowingFunctions.swift  # ✅ Synchronous throws methods
│       │   ├── ErrorTypes.swift         # ✅ Custom Error types
│       │   └── TypedThrows.swift        # ✅ throws(SomeError) typed throws (Swift 6.0+)
│       │
│       ├── MemoryManagement/
│       │   ├── LibraryEvolution.swift   # ✅ Non-frozen struct/class/enum layout
│       │   ├── RetainCycles.swift       # ✅ Circular refs, weak/unowned cycle breaking (known-unsupported)
│       │   └── LeakDetection.swift      # ✅ Deinit tracking, struct-with-ref patterns (migrated from FunctionalTests)
│       │
│       ├── Foundation/
│       │   ├── Data.swift               # ✅ Data parameter, return, round-trip
│       │   ├── URL.swift                # ✅ URL parameter, optional return, struct
│       │   ├── Date.swift               # ✅ Date parameter, return, arithmetic
│       │   └── Extensions.swift         # ✅ Extensions on Data/URL, retroactive conformance
│       │
│       ├── UnsafeTypes/
│       │   ├── Pointers.swift           # ✅ UnsafePointer, UnsafeMutablePointer
│       │   ├── RawPointers.swift        # ✅ UnsafeRawPointer, UnsafeMutableRawPointer
│       │   ├── OpaquePointer.swift      # ✅ OpaquePointer, Optional<OpaquePointer>
│       │   └── Span.swift              # ✅ Span<T>, RawSpan (Swift 6.2+, #if guarded)
│       │
│       ├── ObjCInterop/
│       │   ├── NSObjectSubclass.swift   # ✅ NSObject subclass, inheritance
│       │   ├── ObjCAttributes.swift     # ✅ @objc, @objcMembers, @objc enum
│       │   └── Selectors.swift          # ✅ Selector parameter, #selector
│       │
│       ├── PropertyWrappers/
│       │   └── Wrappers.swift           # ✅ @propertyWrapper, wrappedValue, projectedValue
│       │
│       └── EdgeCases/
│           ├── Unicode.swift            # ✅ Unicode identifiers
│           ├── Keywords.swift           # ✅ Reserved word handling
│           ├── Visibility.swift         # ✅ Access levels
│           └── Deprecation.swift        # ✅ @available attributes
│
└── output/                              # Generated binding output
    ├── SwiftBindingsTestLib.cs           # Generated C# bindings
    ├── binding-report.json              # Binding completeness report
    └── coverage-matrix.json             # Feature coverage matrix
```

### Build Output

The library builds to an xcframework (iOS Simulator target) that the binding generator consumes:

```
TestFramework/.build/
└── SwiftBindingsTestLib.xcframework/
    └── ios-arm64-simulator/
        └── SwiftBindingsTestLib.framework/
            ├── SwiftBindingsTestLib               # dylib
            └── Modules/SwiftBindingsTestLib.swiftmodule/
                ├── arm64-apple-ios-simulator.abi.json  # ABI metadata
                └── SwiftBindingsTestLib.tbd            # Symbol table
```

### v1.0 Binding Generation Results

```
Source:   38 Swift files, 44 structs, 9 classes, 9 enums, 8 protocols, 64 free functions
Output:   49 C# files (18,377 lines), 1 Swift wrapper file
Types:    71/79 emitted (8 not emitted due to unbound generic parameters)
Members:  313/348 emitted, 12 skipped, 153 synthesized (operator pairs, etc.)
```

### v1.5 Binding Generation Results

```
Source:   45 Swift files, 53 structs, 13 classes, 10 enums, 9 protocols, 91 free functions
Output:   49 C# files, 1 Swift wrapper file
Types:    84/93 emitted (90.3% coverage)
Members:  378/418 emitted, 15 skipped, 186 synthesized
Coverage: 80 features tracked (70 must-pass, 10 known-unsupported)
```

v1.5 adds 7 new Swift files covering Foundation interop (Data, URL, Date, extensions),
unsafe/C-interop types (pointers, raw pointers, OpaquePointer), weak/unowned references,
and non-frozen class/enum types. Also expanded the coverage report with 15 new feature
entries.

### v1.6 Binding Generation Results

```
Source:   51 Swift files, 62 structs, 20 classes, 11 enums, 9 protocols, 105 free functions
Output:   49 C# files, 1 Swift wrapper file
Types:    101/110 emitted (91.8% coverage)
Members:  452/500 emitted, 20 skipped, 239 synthesized
Coverage: 93 features tracked (77 must-pass, 17 known-unsupported — note: some v1.6 features
          like NSObject subclass, @objc, Sendable type land as must-pass since the generator
          handles them without issues)
```

v1.6 adds 6 new Swift files covering Objective-C interop (NSObject subclass, @objc,
@objcMembers, @objc enum), property wrappers (@propertyWrapper, wrappedValue,
projectedValue), concurrency attributes (@MainActor class/method, Sendable type,
@Sendable closure), and conditional conformance (extension with where clause).

### v1.7 Binding Generation Results

```
Source:   56 Swift files, 71 structs, 22 classes, 11 enums, 12 protocols, 131 free functions
Output:   49 C# files, 1 Swift wrapper file
Types:    101/110 emitted (91.8% coverage)
Members:  452/500 emitted, 20 skipped, 239 synthesized
Coverage: 108 features tracked (77 must-pass, 31 known-unsupported)
```

v1.7 adds 5 new Swift files covering key paths (KeyPath, WritableKeyPath, key path
as parameter), metatypes (T.Type parameter, T.self, metatype return), protocols with
associated types (associatedtype, PAT conformance, PAT as constraint), variadic
parameters (Int32, String, mixed with other params), and @autoclosure (@autoclosure
parameter, @autoclosure with @escaping).

### v1.8 Binding Generation Results

```
Source:   60 Swift files, 74 structs, 26 classes, 12 enums, 12 protocols, 149 free functions
Output:   49 C# files, 1 Swift wrapper file
Types:    123/135 emitted (91.1% coverage)
Members:  544/629 emitted, 46 skipped, 265 synthesized
Coverage: 123 features tracked (87 must-pass, 36 known-unsupported)
         79 must-pass passing, 8 degraded (skipped binding members)
```

v1.8 adds 4 new Swift files and updates 2 existing files covering actors (actor type,
isolated/nonisolated methods), opaque return types (`some Protocol`, opaque computed
property), throwing closures (@escaping closures that throw), async closures (async
closure parameters), Selector type (Selector parameter, #selector, responds(to:)),
and async properties (computed properties with async getter).

### v1.9 Binding Generation Results

```
Source:   65 Swift files, 77 structs, 27 classes, 14 enums, 12 protocols, 159 free functions
Output:   49 C# files, 1 Swift wrapper file
Types:    129/141 emitted (91.5% coverage)
Members:  570/655 emitted, 46 skipped, ~265 synthesized
Coverage: 136 features tracked (87 must-pass, 49 known-unsupported)
         79 must-pass passing, 8 degraded, 0 missing
         44 known-unsupported with tests, 5 compiled out (InlineArray/Span)
```

v1.9 adds 5 new Swift files covering Swift 6.0–6.2 language features: typed throws
(`throws(SomeError)` with specific error type, async typed throws, struct with typed
throwing method), noncopyable types (`~Copyable` struct, `consuming`/`borrowing`
ownership modifiers, deinit), isolation control (`nonisolated(unsafe)` property),
InlineArray (fixed-size inline type, `#if swift(>=6.2)` guarded), and Span (safe
buffer view, `#if swift(>=6.2)` guarded). Also updated `Package.swift` from
swift-tools-version 5.9 to 6.0 with `swiftLanguageMode(.v5)` to avoid breaking
existing code with strict concurrency.

The coverage report now detects `compiled_out` features — files that exist on disk but
whose declarations are absent from ABI JSON (due to `#if` guards or library-evolution
limitations). These are reported separately from `missing` (no source) and `implemented`
(source + ABI visible).

### v2.0 Binding Generation Results

```
Source:   67 Swift files, 85 structs, 36 classes, 14 enums, 17 protocols, 170 free functions
Output:   49 C# files, 1 Swift wrapper file
Types:    151/168 emitted (89.9% coverage)
Members:  654/747 emitted, 47 skipped, 334 synthesized
Coverage: 145 features tracked (93 must-pass, 52 known-unsupported)
         85 must-pass passing, 8 degraded, 0 missing
         47 known-unsupported with tests, 5 compiled out (InlineArray/Span)
```

v2.0 adds 2 new Swift files and extends 2 existing files: RetainCycles.swift (circular
strong references, weak cycle breaking, unowned cycle breaking — all known-unsupported),
LeakDetection.swift (migrated from FunctionalTests/MemoryTests — deinit tracking,
struct-with-ref-at-offset, frozen-struct-with-ref, embedded-ref-at-nonzero-offset — all
must-pass), multi-protocol conformance in Composition.swift (4+ protocol composition
constraints), and custom equality logic in Comparison.swift (approximate equality with
tolerance).

### Phase 43 Generator Improvements (applied to v1.8 output)

Phase 43 implemented several generator features that affect how v1.8 test cases are handled:

- **Protocol conformance emission**: Types now emit C# interfaces for same-module protocol conformances (e.g., `SimpleItem : ISwiftObject, ISwiftDescribable, ISwiftTestIdentifiable`)
- **Opaque return types** (`some Protocol`): Swift wrappers generated to box concrete returns into existential containers via `_opaque` suffixed symbols
- **Async property detection**: Async getters detected via TBD `Tu` suffix; properly skipped with `SkipReason.AsyncProperty`
- **Actor type support**: Actors detected via `$sScA` mangled conformance; `unownedExecutor` filtered; emitted as classes with actor annotation
- **Bug fixes**: NullRef in MethodHandler for top-level async functions, CacluateFlags crash for unknown generic types

As a result of Phase 43, the following features were promoted from known-unsupported to
must-pass: actors (3 features), opaque returns (3 features), throwing closures (2 features),
and async closures (2 features).

### Phase 44 Generator Improvements (applied to v1.8 output)

Phase 44 added two new feature implementations and applied several correctness fixes identified via Codex review:

- **Inout parameter support**: Swift `inout` parameters emit as C# `ref` parameters; ABI JSON `paramValueOwnership: "InOut"` detected in parser
- **Failable initializer support** (`init?`): Failable constructors emit `TryCreate()` static factory methods returning nullable types; handles frozen (direct value extraction) and non-frozen (InitializeWithCopy) types
- **`PayloadBuffer<T>.BufferRef`**: Added ref-returning property to `PayloadBuffer<T>` for inout frozen-with-memory-management types — `Buffer` returns by value (CS1510 if used with `ref`), `BufferRef` returns a ref into native memory
- **Generic inout writeback ordering**: `EmitGenericInoutWriteback` now runs before `EmitSwiftError` so mutations to `ref` generic parameters are preserved even when the Swift call throws
- **Failable factory generic/closure setup**: `EmitFailableFactory` now calls `EmitDeclarationsForAllocations`, `EmitGenericArguments`, `EmitProtocolWitnessTables`, `EmitGenericInoutWriteback`, and `EmitSafeHandleRelease` for proper generic and closure-heavy failable constructors
- **P/Invoke dedup scoping**: `PInvokeHelperContext.AddDeclaration` deduplicates by method name; inline dedup set moved from static `ConstructorHandler` field to instance field on `ConstructorHandlerFactory` (scoped to one generation run)

Generator fixes required for v1.0 (all resolved):
- Existential arguments (`any Protocol`) crashed `EmitSafeHandleAddRef` — added filter
- Existential and tuple return types crashed `EmitReturnMethod` — added early return handlers
- Existential and tuple returns crashed `MethodRequiresIndirectResult` — added guards
- Null `MetadataPtr` crashed `ValueWitnessTable` access in `FrozenStructHandler` — added null check

---

## Feature Coverage Matrix

This matrix tracks which Swift features are covered by the test library. Features marked "Supported" should have corresponding test cases; features marked "Not Yet" are gaps to fill as we implement them.

### Types

| Feature | Generator Status | Test Coverage | Test File |
|---------|-----------------|---------------|-----------|
| Frozen struct | Supported | ✅ Existing | `Structs.swift` |
| Non-frozen struct | Supported | ✅ Existing | `Structs.swift` |
| Nested struct | Supported | ✅ Existing | `Structs.swift` |
| Struct with ref field | Supported | ✅ v1.0 | `Structs.swift` |
| Basic class | Supported | ✅ v1.0 | `Classes.swift` |
| Class inheritance | Supported | ✅ v1.0 | `Classes.swift` |
| Final class | Supported | ✅ v1.0 | `Classes.swift` |
| Weak reference (`weak var`) | Supported | ✅ v1.5 | `Classes.swift` |
| Unowned reference (`unowned`) | Supported | ✅ v1.5 | `Classes.swift` |
| Raw value enum | Supported | ✅ v1.0 | `Enums.swift` |
| Associated value enum | Supported | ✅ v1.0 | `Enums.swift` |
| Generic enum | Supported | ✅ v1.0 | `Enums.swift` |
| Nested type in generic | Partial | ✅ v1.0 | `Structs.swift` |
| Actor | Supported | ✅ v1.8 | `Actors.swift` |
| Noncopyable struct (`~Copyable`) | **Not Yet** | ✅ v1.9 | `Noncopyable.swift` |
| `consuming` parameter | **Not Yet** | ✅ v1.9 | `Noncopyable.swift` |
| `borrowing` parameter | **Not Yet** | ✅ v1.9 | `Noncopyable.swift` |
| Noncopyable `deinit` | **Not Yet** | ✅ v1.9 | `Noncopyable.swift` |
| InlineArray (fixed-size) | **Not Yet** | ✅ v1.9 | `InlineArray.swift` (compiled out) |

### Protocols

| Feature | Generator Status | Test Coverage | Test File |
|---------|-----------------|---------------|-----------|
| Simple protocol | Supported | ✅ Existing | `BasicProtocols.swift` |
| Protocol with properties | Supported | ✅ v1.0 | `BasicProtocols.swift` |
| Protocol with methods | Supported | ✅ v1.0 | `BasicProtocols.swift` |
| Protocol inheritance | Supported | ✅ v1.0 | `BasicProtocols.swift` |
| Protocol with associated type | Partial | ✅ v1.7 | `PATs.swift` |
| Protocol composition (`A & B`) | Supported | ✅ v1.0 | `Composition.swift` |
| Multi-protocol conformance (4+) | Supported | ✅ v2.0 | `Composition.swift` |
| Type conforming to protocol | Supported | ✅ v1.0 | `Conformance.swift` |
| Retroactive conformance | Partial | ✅ v1.0 | `Conformance.swift` |
| Circular protocol refs | Supported | ✅ v1.0 | `Composition.swift` |
| Conditional conformance | **Not Yet** | ✅ v1.6 | `Conditional.swift` |

### Generics

| Feature | Generator Status | Test Coverage | Test File |
|---------|-----------------|---------------|-----------|
| Generic function | Supported | ✅ Existing | `Functions.swift` |
| Generic function with constraint | Supported | ✅ Existing | `Functions.swift` |
| Generic struct | Partial | ✅ v1.0 | `Types.swift` |
| Generic class | Partial | ✅ v1.0 | `Types.swift` |
| Bound generic type | Supported | ✅ v1.0 | `Types.swift` |
| Generic subscript | Partial | ✅ v1.0 | `Types.swift` |
| Where clause | Supported | ✅ Existing | `Constraints.swift` |
| `any Protocol` (existential) | Supported | ✅ v1.0 | `Existentials.swift` |
| `some Protocol` (opaque) | Supported | ✅ v1.8 | `Existentials.swift` |
| Key paths (`\T.property`) | Partial | ✅ v1.7 | `KeyPaths.swift` |
| WritableKeyPath | Partial | ✅ v1.7 | `KeyPaths.swift` |
| Metatypes (`T.Type`) | Partial | ✅ v1.7 | `Metatypes.swift` |

### Closures

| Feature | Generator Status | Test Coverage | Test File |
|---------|-----------------|---------------|-----------|
| @escaping void closure | Supported | ✅ Existing | `Escaping.swift` |
| @escaping with primitives | Supported | ✅ Existing | `Escaping.swift` |
| @escaping with frozen struct | Supported | ✅ v1.0 | `Escaping.swift` |
| @convention(c) | Supported | ✅ v1.0 | `ConventionC.swift` |
| @autoclosure | Supported | ✅ v1.7 | `Autoclosures.swift` |
| Method returning closure | Supported | ✅ v1.0 | `ClosureReturns.swift` |
| Async closure | Supported | ✅ v1.8 | `AsyncClosures.swift` |
| Throwing closure | Supported | ✅ v1.8 | `Escaping.swift` |
| Closure in closure | Not Supported | ⬜ Document | n/a |

### Async/Concurrency

| Feature | Generator Status | Test Coverage | Test File |
|---------|-----------------|---------------|-----------|
| Async method | Supported | ✅ v1.0 | `Methods.swift` |
| Async static method | Supported | ✅ v1.0 | `Methods.swift` |
| Async throwing method | Supported | ✅ v1.0 | `AsyncThrowing.swift` |
| Async property | Detected & Skipped | ✅ v1.8 | `AsyncProperties.swift` |
| @MainActor class | Supported | ✅ v1.6 | `MainActor.swift` |
| @MainActor method | Supported | ✅ v1.6 | `MainActor.swift` |
| @Sendable closure | Supported | ✅ v1.6 | `Sendable.swift` |
| Sendable type | Supported | ✅ v1.6 | `Sendable.swift` |
| `nonisolated(unsafe)` | **Not Yet** | ✅ v1.9 | `IsolationControl.swift` |

### Properties

| Feature | Generator Status | Test Coverage | Test File |
|---------|-----------------|---------------|-----------|
| Stored property getter | Supported | ✅ Existing | `Getters.swift` |
| Computed property getter | Supported | ✅ Existing | `Getters.swift` |
| Property setter | Supported | ✅ v1.0 | `Setters.swift` |
| Static property | Supported | ✅ Existing | `Static.swift` |
| Lazy property | Supported | ✅ v1.0 | `Getters.swift` |
| @propertyWrapper type | Supported | ✅ v1.6 | `Wrappers.swift` |
| Wrapped property access | Supported | ✅ v1.6 | `Wrappers.swift` |
| Projected value (`$prop`) | Supported | ✅ v1.6 | `Wrappers.swift` |

### Operators

| Feature | Generator Status | Test Coverage | Test File |
|---------|-----------------|---------------|-----------|
| Arithmetic (+, -, *, /, %) | Supported | ✅ v1.0 | `Arithmetic.swift` |
| Comparison (==, !=, <, >) | Supported | ✅ Existing | `Comparison.swift` |
| Custom equality logic | Supported | ✅ v2.0 | `Comparison.swift` |
| Bitwise (&, \|, ^, <<, >>) | Supported | ✅ v1.0 | `Bitwise.swift` |
| Unary (!, ~) | Supported | ✅ v1.0 | `Unary.swift` |

### Tuples

| Feature | Generator Status | Test Coverage | Test File |
|---------|-----------------|---------------|-----------|
| 2-element tuple | Supported | ✅ v1.0 | `BasicTuples.swift` |
| 7-element tuple | Supported | ✅ v1.0 | `BasicTuples.swift` |
| Named tuple elements | Supported | ✅ v1.0 | `Named.swift` |
| Method returning tuple | Supported | ✅ v1.0 | `TupleReturns.swift` |
| 8+ element tuple | Not Supported | ⬜ Document | n/a |

### Initializers

| Feature | Generator Status | Test Coverage | Test File |
|---------|-----------------|---------------|-----------|
| Standard initializer | Supported | ✅ v1.0 | `BasicInit.swift` |
| Failable initializer (`init?`) | Supported | ✅ v1.0 | `Failable.swift` |
| Implicitly unwrapped (`init!`) | Supported | ✅ v1.0 | `Failable.swift` |
| Throwing initializer | Supported | ✅ v1.0 | `Throwing.swift` |
| Convenience initializer | Supported | ✅ v1.0 | `BasicInit.swift` |
| Required initializer | Supported | ✅ v1.0 | `BasicInit.swift` |

### Parameters

| Feature | Generator Status | Test Coverage | Test File |
|---------|-----------------|---------------|-----------|
| Inout parameter (`inout`) | Supported | ✅ v1.0 | `Inout.swift` |
| Inout with frozen struct | Supported | ✅ v1.0 | `Inout.swift` |
| Variadic parameter | Supported | ✅ v1.7 | `Variadic.swift` |
| Default parameter value | Partial | ✅ v1.0 | `Defaults.swift` |

### Error Handling

| Feature | Generator Status | Test Coverage | Test File |
|---------|-----------------|---------------|-----------|
| Synchronous `throws` method | Supported | ✅ v1.0 | `ThrowingFunctions.swift` |
| Static `throws` method | Supported | ✅ v1.0 | `ThrowingFunctions.swift` |
| Custom `Error` type | Supported | ✅ v1.0 | `ErrorTypes.swift` |
| Error to Exception mapping | Supported | ✅ v1.0 | `ErrorTypes.swift` |
| Typed throws (`throws(E)`) | **Not Yet** | ✅ v1.9 | `TypedThrows.swift` |
| Typed async throws | **Not Yet** | ✅ v1.9 | `TypedThrows.swift` |

### Foundation Interop

| Feature | Generator Status | Test Coverage | Test File |
|---------|-----------------|---------------|-----------|
| Foundation.Data | Supported | ✅ v1.5 | `Foundation/Data.swift` |
| Foundation.URL | Supported | ✅ v1.5 | `Foundation/URL.swift` |
| Foundation.Date | Supported | ✅ v1.5 | `Foundation/Date.swift` |
| Extension on Foundation type | Partial | ✅ v1.5 | `Foundation/Extensions.swift` |
| Retroactive conformance | Partial | ✅ v1.5 | `Foundation/Extensions.swift` |

### Objective-C Interop

| Feature | Generator Status | Test Coverage | Test File |
|---------|-----------------|---------------|-----------|
| NSObject subclass | Partial | ✅ v1.6 | `NSObjectSubclass.swift` |
| @objc attribute | Supported | ✅ v1.6 | `ObjCAttributes.swift` |
| @objcMembers | Supported | ✅ v1.6 | `ObjCAttributes.swift` |
| Selector type | Partial | ✅ v1.8 | `Selectors.swift` |

### Unsafe/C-Interop Types

| Feature | Generator Status | Test Coverage | Test File |
|---------|-----------------|---------------|-----------|
| UnsafePointer<T> | Supported | ✅ v1.5 | `UnsafeTypes/Pointers.swift` |
| UnsafeMutablePointer<T> | Supported | ✅ v1.5 | `UnsafeTypes/Pointers.swift` |
| UnsafeRawPointer | Supported | ✅ v1.5 | `UnsafeTypes/RawPointers.swift` |
| UnsafeMutableRawPointer | Supported | ✅ v1.5 | `UnsafeTypes/RawPointers.swift` |
| OpaquePointer | Partial | ✅ v1.5 | `UnsafeTypes/OpaquePointer.swift` |
| Span<T> | **Not Yet** | ✅ v1.9 | `UnsafeTypes/Span.swift` (compiled out) |
| RawSpan | **Not Yet** | ✅ v1.9 | `UnsafeTypes/Span.swift` (compiled out) |

### Memory Management (Stability Tests)

| Feature | Generator Status | Test Coverage | Test File |
|---------|-----------------|---------------|-----------|
| Circular strong reference | **Not Yet** | ✅ v2.0 | `RetainCycles.swift` |
| Weak cycle breaking | **Not Yet** | ✅ v2.0 | `RetainCycles.swift` |
| Unowned cycle breaking | **Not Yet** | ✅ v2.0 | `RetainCycles.swift` |
| Non-frozen layout change | **Critical** | ✅ v1.0 | `LibraryEvolution.swift` |
| Non-frozen class | Supported | ✅ v1.5 | `LibraryEvolution.swift` |
| Non-frozen enum | Supported | ✅ v1.5 | `LibraryEvolution.swift` |
| Evolving optional fields | Supported | ✅ v1.5 | `LibraryEvolution.swift` |
| Deinit tracking | Supported | ✅ v2.0 | `LeakDetection.swift` |
| Struct with ref at offset | Supported | ✅ v2.0 | `LeakDetection.swift` |
| Frozen struct with ref | Supported | ✅ v2.0 | `LeakDetection.swift` |
| Embedded ref at nonzero offset | Supported | ✅ v2.0 | `LeakDetection.swift` |

### Out of Scope (Compile-Time Only)

These Swift features don't appear in ABI JSON and thus don't need test coverage:

| Feature | Reason |
|---------|--------|
| Macros (`@Observable`, etc.) | Expanded at compile time; bindings see expanded code |
| Parameter packs | Compile-time variadic generics; monomorphized in ABI |
| Result builders (`@ViewBuilder`) | DSL syntax sugar; desugared before ABI |
| Property observers (`willSet`/`didSet`) | Internal implementation; not in public ABI |
| Data-race safety mode | Compiler flag for strict concurrency checking; no ABI impact |
| Trailing comma syntax | Syntax convenience (Swift 6.1); no ABI representation |
| Package traits | Swift Package Manager feature (Swift 6.1); no ABI impact |
| Task naming | Debugging/instrumentation only (Swift 6.2); not in public ABI |
| Raw identifiers (`#`) | Source-level syntax; mangled names unchanged (mostly Swift Testing) |
| Simplified TaskGroup syntax | Compiler sugar (Swift 6.1); no ABI change |
| Java interoperability | Separate language bridge (Swift 6.2); not relevant to C#/.NET bindings |

---

## Implementation Strategy (Codex Recommendations)

### v1 Scope: Start Small

**v1 should include only Tier 1 + Tier 2 features.** Resist the temptation to build everything at once.

| Version | Tiers | Features |
|---------|-------|----------|
| **v1.0** ✅ | Tier 1 + 2 | Property setters, protocol conformance, throws, inout, failable init, default params |
| **v1.5** ✅ | + Tier 3 | Weak/unowned, Foundation interop, unsafe types, non-frozen class/enum |
| **v1.6** ✅ | + Tier 4 partial | ObjC interop, property wrappers, @MainActor, @Sendable, conditional conformance |
| **v1.7** ✅ | **+ Key paths, metatypes, PATs, variadic, autoclosures** | **56 files** |
| **v1.8** ✅ | **+ Actors, opaque returns, throwing/async closures, selectors, async properties** | **60 files** |
| 2.0 | + Full coverage | All remaining gaps |

### Test Bucketing: Must-Pass vs Known-Unsupported

Tests are split into two categories via the `KNOWN_UNSUPPORTED_FEATURES` set in
`generate-coverage-report.sh` (conceptual split, not directory-based):

| Category | Purpose | CI Behavior |
|----------|---------|-------------|
| **must-pass** | Features that work today | `run-tests.sh` warns on degradation |
| **known-unsupported** | Features we're tracking | Reported but does not warn |

All Swift sources live in a single target (`Sources/SwiftBindingsTestLib/`). The coverage
report script classifies features by cross-referencing the binding report's skipped items
against per-feature declaration ownership:

- **passing**: Test file exists and no binding members were skipped
- **degraded**: Test file exists but some binding members were skipped
- **compiled_out**: Test file exists but declarations are absent from ABI (e.g., `#if swift(>=6.2)` guard)
- **missing**: No test file for this feature

When a feature is implemented, remove it from `KNOWN_UNSUPPORTED_FEATURES` to promote it
to must-pass. The coverage report will then flag it as degraded if any bindings are still
skipped.

### Machine-Readable Coverage Report

Auto-generate a `coverage-matrix.json` by cross-referencing ABI JSON, binding report,
and Swift source files:

```json
{
  "generated": "2026-02-03T00:00:00Z",
  "generator_exit_code": 0,
  "summary": {
    "must_pass": { "total": 87, "passing": 79, "degraded": 8, "compiled_out": 0, "missing": 0 },
    "known_unsupported": { "total": 49, "with_test": 44, "compiled_out": 5, "without_test": 0 }
  },
  "features": [
    { "name": "frozen_struct", "status": "must_pass", "test_status": "passing", ... },
    { "name": "generic_struct", "status": "must_pass", "test_status": "degraded",
      "binding_skips": [{"name": "wrapped", "kind": "Property", "reason": "AnyTypeFallback", ...}] },
    { "name": "actor_type", "status": "known_unsupported", "test_status": "implemented", ... },
    { "name": "inline_array_parameter", "status": "known_unsupported", "test_status": "compiled_out", ... }
  ]
}
```

Benefits:
- Cross-references binding report to detect degraded features (skipped binding members)
- Declaration-level attribution avoids false positives when features share source files
- Detects `compiled_out` features (source exists but not in ABI due to `#if` guards)
- Status can't drift from reality
- Can diff against previous runs to detect regressions
- Powers dashboard/reporting if desired

### Early ABI Evolution Tests

Add 1-2 non-frozen struct layout tests in v1.0, not v1.5. This is a high-risk area that's easy to get wrong silently.

```swift
// v1 of library
public struct EvolvingConfig {
    public var featureA: Bool
    public var timeout: Int
}

// v2 of library (simulated by building two versions)
public struct EvolvingConfig {
    public var featureA: Bool
    public var featureB: Bool  // NEW - shifts layout!
    public var timeout: Int
}
```

Test verifies that C# bindings using getter/setter functions (not memory offsets) work across both versions.

---

## Recommended Prioritization

Based on analysis of real-world library patterns and current blockers:

### Tier 1: Functional Blockers (Implement First)
| Feature | Rationale |
|---------|-----------|
| **Property Setters** | Without setters, bindings are read-only "viewers" not usable APIs |
| **Protocol Conformance Emission** | Required for delegates, callbacks, UI patterns - #1 blocker for UI libs |
| **Synchronous throws** | Standard Swift error handling; `Error` → `Exception` mapping essential |

### Tier 2: High-Impact Features
| Feature | Rationale |
|---------|-----------|
| **Inout Parameters** | Required for mutation-heavy libs (animation, math, buffers) |
| **Failable Initializers** | Factory patterns everywhere; `init?` → `TryCreate()` |
| **Default Parameter Values** | Without these, C# users must specify every argument (poor DX) |

### Tier 3: Completeness & Stability
| Feature | Rationale |
|---------|-----------|
| **Weak/Unowned References** | Prevents memory leak cycles between C# and Swift |
| **Foundation Interop** | Data, URL, Date are ubiquitous in real libraries |
| **Non-frozen Layout Testing** | Prevents silent crashes when Swift lib updates internally |

### Tier 4: Advanced Features
| Feature | Rationale |
|---------|-----------|
| **Property Wrappers** | SwiftUI/Combine compatibility |
| **@MainActor/@Sendable** | Swift concurrency model |
| **Key Paths / Metatypes** | Advanced generic patterns |
| **Actors** | Complete concurrency support |

### Tier 5: Swift 6 Language Features
| Feature | Rationale |
|---------|-----------|
| **Typed Throws** | Error type in ABI signature; enables specific catch types in C# |
| **Noncopyable Types** | Different ABI (no copy witness); requires move-only marshalling |
| **`consuming`/`borrowing`** | Ownership modifiers in ABI like `inout`; affects parameter passing |
| **InlineArray** | New fixed-size value type in stdlib; may appear in Apple framework APIs |
| **Span** | Safe buffer replacement; natural C# `Span<T>` mapping |
| **`@concurrent`/isolation** | New Swift 6.2 concurrency attributes; may affect function signatures |

---

## Known C# Interop Challenges

These are anticipated challenges when implementing certain features:

### Non-Frozen Struct Layout Trap
> **Warning**: Non-frozen structs do NOT have fixed memory layout. Using `[StructLayout(LayoutKind.Sequential)]` with P/Invoke will cause crashes when the Swift library changes internal field order.

**Solution**: Use getter/setter function calls instead of direct memory offsets for non-frozen types.

### Protocol Diamond Problem
Swift allows a type to conform to two protocols with same-named methods but different semantics. C# requires Explicit Interface Implementation to handle this.

```csharp
// C# must detect and emit:
public class MyType : IProtocolA, IProtocolB
{
    void IProtocolA.DoThing() { /* A's impl */ }
    void IProtocolB.DoThing() { /* B's impl */ }
}
```

### Generic Constraint Mapping
Swift constraints like `where T: Numeric` or `where T: AnyObject` don't always map to C#. May require runtime validation in constructors.

### Swift Task vs .NET Task
Swift's async uses a different executor than .NET ThreadPool. A `SwiftTaskAwaiter` bridge is needed to prevent UI thread deadlocks.

### String Marshalling
Swift strings are UTF-8 and can contain null bytes. Standard P/Invoke `CharSet.Ansi`/`Unicode` causes data loss. Custom `SwiftString` marshaller required (already implemented).

### Circular Protocol References
Protocol A returning Protocol B (and vice versa) can cause stack overflow in proxy generation. Requires lazy initialization or forward declaration pattern.

### Debug Symbols
The `build-xcframework.sh` script should generate **dSYMs** (debug symbols). Without these, debugging the C#→Swift boundary is nearly impossible.

---

## Example Swift Code for Complex Features

### Inout Parameters
```swift
// C# expected: public static void IncrementX(ref MutablePoint point)
public func incrementX(_ point: inout MutablePoint) {
    point.x += 1.0
}
```

### Failable Initializers
```swift
// C# expected: public static bool TryCreate(..., out SafeDiv? result)
public struct SafeDiv {
    public let result: Double
    public init?(numerator: Double, denominator: Double) {
        guard denominator != 0 else { return nil }
        self.result = numerator / denominator
    }
}
```

### Property Wrappers
```swift
// C# expected: Properties for wrappedValue, projectedValue ($count)
@propertyWrapper
public struct Clamped<T: Comparable> {
    public var wrappedValue: T
    public var projectedValue: T { wrappedValue }
    public init(wrappedValue: T) { self.wrappedValue = wrappedValue }
}

public struct Counter {
    @Clamped public var count: Int = 0
}
```

### Weak/Unowned References
```swift
// C# expected: WeakRef<Node> or nullable SafeHandle with weak semantics
public class Node {
    public weak var parent: Node?
    public unowned var owner: Owner
}
```

### Key Paths
```swift
// C# expected: KeyPath<Point, double> or delegate-based equivalent
public func getValue<T, V>(_ obj: T, at keyPath: KeyPath<T, V>) -> V {
    return obj[keyPath: keyPath]
}
```

### @MainActor
```swift
// C# expected: [MainActor] attribute or dispatch wrapper
@MainActor
public class ViewModel {
    public var title: String = ""
    public func refresh() async { }
}
```

### Conditional Conformance
```swift
// C# expected: Box<T> : IDescribable only when T : ICustomStringConvertible
public struct Box<T> { public var value: T }
extension Box: CustomStringConvertible where T: CustomStringConvertible {
    public var description: String { value.description }
}
```

### Autoclosures
```swift
// C# expected: Func<bool> (no parameters, lazy evaluation)
public func logIfTrue(_ condition: @autoclosure () -> Bool, message: String) {
    if condition() { print(message) }
}
```

### Default Parameter Values
```swift
// C# expected: Multiple overloads or optional parameters
// ABI generates separate "default argument generator" function
public func search(query: String, limit: Int = 10, offset: Int = 0) -> [Result] {
    // ...
}
```

### Extension on Foundation Type
```swift
// C# expected: Static extension methods on SwiftData
extension Data {
    public var hexString: String {
        return map { String(format: "%02x", $0) }.joined()
    }
}
```

### Nested Type in Generic
```swift
// Complex mangled name in ABI - tests name resolution
public struct Container<T> {
    public enum Status {
        case empty
        case loaded(T)
    }

    public var status: Status = .empty
}
```

### Circular Protocol References
```swift
// Tests proxy generation doesn't stack overflow
public protocol NodeProtocol {
    var parent: (any TreeProtocol)? { get }
}

public protocol TreeProtocol {
    var root: any NodeProtocol { get }
}
```

### Synchronous Throws with Custom Error
```swift
// C# expected: throws SwiftException wrapping the error
public enum ValidationError: Error {
    case empty
    case tooLong(maxLength: Int)
}

public func validate(_ input: String) throws -> String {
    guard !input.isEmpty else { throw ValidationError.empty }
    guard input.count <= 100 else { throw ValidationError.tooLong(maxLength: 100) }
    return input
}
```

### Typed Throws (Swift 6.0+)
```swift
// C# expected: throws specific error type, not generic Error
// ABI: error type appears in function signature (unlike untyped throws)
public enum ParseError: Error {
    case invalidInput
    case overflow(value: Int)
}

public func parseNumber(_ input: String) throws(ParseError) -> Int {
    guard let value = Int(input) else { throw .invalidInput }
    guard value <= Int32.max else { throw .overflow(value: value) }
    return value
}

// Async typed throws
public func asyncParse(_ input: String) async throws(ParseError) -> Int {
    return try parseNumber(input)
}
```

### Noncopyable Types (Swift 6.0+)
```swift
// C# expected: Different marshalling — no copy semantics, unique ownership
// ABI: ~Copyable inverse conformance, consuming/borrowing ownership modifiers
public struct UniqueResource: ~Copyable {
    public let id: Int32

    public init(id: Int32) {
        self.id = id
    }

    // consuming — takes ownership, caller can't use value after
    consuming public func consume() -> Int32 {
        return id
    }

    // borrowing — read-only borrow, no ownership transfer
    borrowing public func inspect() -> Int32 {
        return id
    }

    deinit {
        // Cleanup when ownership ends
    }
}

// Free function with ownership modifiers
public func transferOwnership(_ resource: consuming UniqueResource) -> Int32 {
    return resource.id
}

public func borrowResource(_ resource: borrowing UniqueResource) -> Int32 {
    return resource.id
}
```

### Swift 6.2 Isolation Control
```swift
// C# expected: @concurrent is an attribute; nonisolated(unsafe) similar to nonisolated
// ABI: @concurrent appears as function attribute

@concurrent
public func concurrentWork(input: Int32) async -> Int32 {
    return input * 2
}

public class SharedState {
    nonisolated(unsafe) public var unsafeCounter: Int32 = 0

    public init() {}
}
```

### InlineArray (Swift 6.2+)
```swift
// C# expected: Fixed-size value type, likely mapped to fixed-size buffer or tuple
// ABI: InlineArray<N, Element> with compile-time count
public func sumInlineArray(_ values: InlineArray<4, Int32>) -> Int32 {
    var total: Int32 = 0
    for v in values { total += v }
    return total
}
```

### Span (Swift 6.2+)
```swift
// C# expected: Safe buffer view, likely mapped to Span<T> or ReadOnlySpan<T> in C#
// ABI: Span<T> replaces UnsafeBufferPointer in safe APIs
public func sumSpan(_ values: Span<Int32>) -> Int32 {
    var total: Int32 = 0
    for v in values { total += v }
    return total
}
```

### Non-Frozen Struct (Library Evolution Test)
```swift
// WARNING: Field order may change between library versions
// C# bindings MUST use getter/setter functions, not memory offsets
public struct EvolvingConfig {
    public var featureA: Bool
    public var featureB: Bool
    // Future version might add featureC here, shifting layout
    public var timeout: Int

    public init(featureA: Bool, featureB: Bool, timeout: Int) {
        self.featureA = featureA
        self.featureB = featureB
        self.timeout = timeout
    }
}
```

---

## Integration with Test Suite

### Binding Generation Test

```csharp
[Fact]
public async Task ComprehensiveLibrary_GeneratesWithoutErrors()
{
    // Generate bindings for the comprehensive test library
    var result = await GenerateBindings("SwiftBindingsTestLib.xcframework");

    Assert.Empty(result.Errors);
    Assert.True(result.TypeCount > 100, "Expected comprehensive type coverage");
}
```

### Compilation Test

```csharp
[Fact]
public async Task ComprehensiveLibrary_CompilesCleanly()
{
    var generatedCode = await GenerateBindings("SwiftBindingsTestLib.xcframework");
    var compilation = CSharpCompilation.Create("Test", generatedCode.SyntaxTrees);

    var diagnostics = compilation.GetDiagnostics()
        .Where(d => d.Severity == DiagnosticSeverity.Error);

    Assert.Empty(diagnostics);
}
```

### Runtime Validation Test (iOS Simulator)

```csharp
[Fact]
public async Task ComprehensiveLibrary_RuntimeValidation()
{
    // Build and run test app on simulator
    // Verify key operations work at runtime
}
```

---

## Usage Workflow

### Adding a New Feature

When implementing a new Swift feature (e.g., property setters):

1. **Add test case to library**:
   ```swift
   // In Properties/Setters.swift
   public struct SettableProperties {
       public var value: Int32

       public init(value: Int32) {
           self.value = value
       }
   }
   ```

2. **Regenerate bindings** (expect failure or placeholder):
   ```bash
   ./TestFramework/regenerate-bindings.sh
   ```

3. **Implement the feature** in the generator

4. **Verify bindings compile** and run correctly

5. **Update coverage matrix** in this document

### Running the Full Suite

```bash
# Build the test library
cd TestFramework && ./build-xcframework.sh

# Generate bindings
./regenerate-bindings.sh

# Run compilation test
dotnet test --filter "Category=ComprehensiveLibrary"

# Run on simulator (if runtime tests exist)
./validate-sim.sh
```

---

## Maintenance Guidelines

### When to Add Test Cases

- Before implementing any new Swift feature
- When a third-party SDK reveals an unhandled pattern
- When a bug is found (add regression test)
- When expanding to new Swift language versions

### Versioning

The test library should be versioned to track Swift language feature additions:

| Library Version | Swift Features | Notes |
|----------------|----------------|-------|
| **1.0** ✅ | **All Tier 1-2 + extras** | **38 files: types, closures, async, operators, tuples, protocols, generics, existentials, error handling, edge cases** |
| **1.5** ✅ | **+ Tier 3** | **45 files: + Foundation interop (Data, URL, Date, extensions), unsafe/C-interop types (pointers, raw pointers, OpaquePointer), weak/unowned refs, non-frozen class/enum** |
| **1.6** ✅ | **+ Tier 4 partial** | **51 files: + ObjC interop (NSObject, @objc, @objcMembers), property wrappers, @MainActor, @Sendable, conditional conformance** |
| **1.7** ✅ | **+ Key paths, metatypes, PATs, variadic, autoclosures** | **56 files: + key paths (KeyPath, WritableKeyPath), metatypes (T.Type, T.self), protocols with associated types, variadic parameters, @autoclosure** |
| **1.8** ✅ | **+ Actors, opaque returns, throwing/async closures, selectors, async properties** | **60 files: + actor type, some Protocol opaque returns, throwing closures, async closures, Selector parameter, async computed properties** |
| **1.9** ✅ | **+ Swift 6 language features** | **65 files: + typed throws, noncopyable types, ownership modifiers, isolation control, InlineArray, Span** |
| **2.0** ✅ | **+ Memory management, multi-protocol, custom equality** | **67 files: + RetainCycles, LeakDetection (migrated from FunctionalTests), multi-protocol conformance, custom equality** |

### Documentation

Each Swift file should include comments explaining:
- What feature is being tested
- Expected C# output (for complex cases)
- Any known limitations

```swift
// MARK: - Escaping Closures with Frozen Struct Parameters
// Tests: @escaping closures where parameters are frozen structs
// Expected C#: Action<FrozenPoint> / Func<FrozenPoint, FrozenPoint>
// Limitation: Non-frozen struct parameters not supported

@frozen
public struct FrozenPoint {
    public var x: Double
    public var y: Double
}

public func transformPoint(
    _ point: FrozenPoint,
    using transform: @escaping (FrozenPoint) -> FrozenPoint
) -> FrozenPoint {
    return transform(point)
}
```

---

## Success Criteria

The comprehensive test library is successful when:

1. **Coverage**: Every supported Swift feature has at least one test case
2. **Regression detection**: New changes that break existing features are caught via binding report cross-referencing (degraded features flagged automatically)
3. **Feature roadmap**: Unsupported features are documented with placeholder tests
4. **CI integration**: The library is built and tested via `run-tests.sh` (macOS only)
5. **Developer confidence**: Engineers can implement features knowing tests will verify correctness
6. **Honest reporting**: Coverage report cannot show false-green — skipped binding members are attributed to specific features via declaration-level mapping

---

## Next Steps

### Phase 1: v1.0 Foundation
1. [x] Create `TestFramework/` directory structure (MustPass/KnownUnsupported split is conceptual via `KNOWN_UNSUPPORTED_FEATURES` in coverage report, not directory-based)
2. [x] Write `Package.swift` manifest
3. [x] Implement Tier 1 tests (property setters, protocol conformance, throws)
4. [x] Implement Tier 2 tests (inout, failable init, default params)
5. [x] Add 1-2 ABI evolution tests (non-frozen struct layout)
6. [x] Create `build-xcframework.sh` script
7. [x] Create `generate-coverage-report.sh` that outputs `coverage-matrix.json`

### Phase 2: CI Integration
8. [x] Wire TestFramework into `run-tests.sh` (macOS-only guard, warns on degraded features)
9. [ ] Configure must-pass tests to gate PRs (currently warns, does not fail)
10. [x] Known-unsupported tests are informational (reported in coverage matrix, no gate)
11. [x] Document in CLAUDE.md

### Phase 3: Expansion (v1.5+)
12. [x] Migrate relevant cases from existing `FunctionalTests/` (MemoryTests patterns → LeakDetection.swift)
13. [x] Add Tier 3 coverage (weak/unowned, Foundation interop, unsafe types)
14. [x] Expand ABI evolution test suite (non-frozen class, enum, optional fields)

### Phase 4: Tier 4 Features (v1.6)
15. [x] Add Objective-C interop tests (NSObject subclass, @objc, @objcMembers)
16. [x] Add property wrapper tests (@propertyWrapper, wrappedValue, projectedValue)
17. [x] Add concurrency attribute tests (@MainActor class/method, Sendable, @Sendable closure)
18. [x] Add conditional conformance test (extension with where clause)

### Phase 5: Advanced Features (v1.7)
19. [x] Add key path tests (KeyPath, WritableKeyPath, key path as parameter)
20. [x] Add metatype tests (T.Type parameter, T.self, metatype return)
21. [x] Add protocol with associated type tests (associatedtype, PAT conformance, PAT as constraint)
22. [x] Add variadic parameter tests (Int32, String, mixed with other params)
23. [x] Add @autoclosure tests (@autoclosure parameter, @autoclosure with @escaping)

### Phase 6: Concurrency & Remaining Gaps (v1.8)
24. [x] Add actor tests (actor type, isolated methods, nonisolated methods)
25. [x] Add opaque return type tests (`some Protocol`, opaque computed property)
26. [x] Add throwing closure tests (@escaping closures that throw)
27. [x] Add async closure tests (async closure parameters)
28. [x] Add Selector tests (Selector parameter, #selector, responds(to:))
29. [x] Add async property tests (computed property with async getter)

### Phase 7: Swift 6 Language Features (v1.9)
30. [x] Update `Package.swift` swift-tools-version from 5.9 to 6.0 (required for typed throws, noncopyable types)
31. [x] Add typed throws tests (`throws(SomeError)` with specific error type, async typed throws, struct with typed throwing method)
32. [x] Add noncopyable type tests (`~Copyable` struct, `consuming` parameter, `borrowing` parameter, deinit on noncopyable)
33. [x] Add Swift 6.2 isolation control tests (`nonisolated(unsafe)` property)
34. [x] Add InlineArray tests (InlineArray parameter, InlineArray return, InlineArray property; guarded with `#if swift(>=6.2)`)
35. [x] Add Span tests (Span<T> parameter, RawSpan parameter; guarded with `#if swift(>=6.2)`)
36. [x] Update `generate-coverage-report.sh` FEATURE_MAP and KNOWN_UNSUPPORTED_FEATURES for new files/features

---

## See Also

- `CURRENT-STATUS.md` - What currently works
- `binding-gaps-consolidated.md` - Known gaps
- `north-star.md` - Project vision
- `emitter-redesign-proposal.md` - Architecture direction

---

## Research Notes

**Coverage matrix expanded February 2026** based on multi-model analysis:

### Round 1: Grok Analysis
- Swift 5.9+ language features (concurrency attributes, existentials)
- Common patterns in popular Swift libraries (Nuke, Lottie, Alamofire)
- Apple SDK API patterns (SwiftUI property wrappers, StoreKit)
- Interop edge cases (inout, weak refs, key paths)

**Key insight**: Macros and parameter packs don't need coverage as they're compile-time features that don't persist in ABI JSON. The binding generator sees the expanded/monomorphized code.

### Round 2: Gemini Analysis
- Foundation type bridging (Data, URL, Date)
- Objective-C interop patterns (NSObject, @objc, Selector)
- C-interop unsafe types (UnsafePointer, OpaquePointer)
- Memory management edge cases (non-frozen layout, circular refs)
- Default parameter values (separate ABI function)
- Extension on external types (retroactive conformance)

**Key insight**: Non-frozen struct layout is a critical trap - using `StructLayout.Sequential` for P/Invoke will cause silent crashes when Swift library internals change. Must use getter/setter functions.

### Prioritization Consensus
Both models agreed that **Property Setters** and **Protocol Conformance Emission** are the highest-priority gaps, as they're functional blockers preventing the generator from producing usable (non-read-only) bindings.

### Round 3: Codex Analysis
- Start small: v1 should be Tier 1 + Tier 2 only
- Split tests into must-pass (gates PRs) vs known-unsupported (tracks progress)
- Auto-generate machine-readable coverage report to prevent status drift
- Add ABI evolution tests early (non-frozen layout is high-risk)

**Key insight**: The test library should be a *living compatibility suite*, not a comprehensive catalog of every Swift feature. Start with what blocks real usage, expand incrementally.

### Round 4: Grok + Codex Swift 6.0–6.2 Analysis
- Grok catalogued all Swift 6.0, 6.1, and 6.2 language features
- Codex cross-reviewed for binding-generator relevance and accuracy

**ABI-impactful features identified (added as Phase 7 / v1.9):**
1. **Typed throws** (`throws(SomeError)`) — error type is part of the ABI function signature; current tests only use untyped `throws`
2. **Noncopyable types** (`~Copyable`) — inverse conformance requirement in ABI; different value witness tables (no copy function); `consuming` and `borrowing` ownership modifiers appear as parameter annotations similar to `inout`
3. **InlineArray** (Swift 6.2) — new fixed-size inline type `InlineArray<N, Element>` that appears in function signatures; potential C# mapping to fixed-size buffer or ValueTuple
4. **Span** (Swift 6.2) — safe non-owning buffer view replacing `UnsafeBufferPointer`; natural C# mapping to `Span<T>`/`ReadOnlySpan<T>`
5. **`@concurrent`** (Swift 6.2) — new function attribute replacing some `nonisolated` usage; appears in ABI JSON
6. **`nonisolated(unsafe)`** (Swift 6.1) — isolation modifier that may appear in ABI

**Correctly excluded (not ABI-visible):**
- Data-race safety mode (compiler flag)
- Trailing commas (syntax sugar, Swift 6.1)
- Package traits (SPM, Swift 6.1)
- Simplified TaskGroup syntax (compiler sugar)
- Child task return type inference (compiler)
- Task naming (debugging/instrumentation)
- Raw identifiers (source syntax; Codex correctly noted this is mainly Swift Testing display names, not a broad language feature)
- Java interoperability (separate language bridge, not relevant to C#/.NET)

**Codex corrections on Grok's report:**
- Grok listed "Raw identifiers" as broad new identifier syntax — actually tied to Swift Testing display names
- Grok mixed tooling items (faster macro builds, WebAssembly support) with language features
- InlineArray/Span were presented as ecosystem context by Codex, but they are actually ABI-visible types that the generator will encounter in Apple framework APIs

**Key insight**: Swift 6's ownership model (`~Copyable`, `consuming`, `borrowing`) is the most architecturally significant addition for binding generation. These types cannot be retained/released like normal Swift types, requiring fundamentally different marshalling strategies. The generator will need to detect inverse conformance requirements in ABI JSON and emit move semantics rather than copy/ARC patterns. `Package.swift` must be updated from swift-tools-version 5.9 to 6.0+ for typed throws and noncopyable features to compile.
