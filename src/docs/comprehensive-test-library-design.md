# Comprehensive Swift Test Library Design

**Status**: v1.7 Implemented
**Created**: February 2026
**Last Updated**: February 2026 - v1.7 implemented (56 Swift files, 108 features covered)

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
                    │      Library        │    (56 Swift files, 108 features)
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
│       │   └── Enums.swift              # ✅ Raw, associated values, generic
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
│       │   └── Sendable.swift           # ✅ Sendable type, @Sendable closure
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
│       │   └── ErrorTypes.swift         # ✅ Custom Error types
│       │
│       ├── MemoryManagement/
│       │   └── LibraryEvolution.swift   # ✅ Non-frozen struct/class/enum layout
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
│       │   └── OpaquePointer.swift      # ✅ OpaquePointer, Optional<OpaquePointer>
│       │
│       ├── ObjCInterop/
│       │   ├── NSObjectSubclass.swift   # ✅ NSObject subclass, inheritance
│       │   └── ObjCAttributes.swift     # ✅ @objc, @objcMembers, @objc enum
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
    ├── Swift.SwiftBindingsTestLib.cs     # Generated C# bindings
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
| Weak reference (`weak var`) | **Not Yet** | ✅ v1.5 | `Classes.swift` |
| Unowned reference (`unowned`) | **Not Yet** | ✅ v1.5 | `Classes.swift` |
| Raw value enum | Supported | ✅ v1.0 | `Enums.swift` |
| Associated value enum | Supported | ✅ v1.0 | `Enums.swift` |
| Generic enum | Supported | ✅ v1.0 | `Enums.swift` |
| Nested type in generic | Unknown | ✅ v1.0 | `Structs.swift` |
| Actor | Not Yet | ⬜ Future | `Actors.swift` |

### Protocols

| Feature | Generator Status | Test Coverage | Test File |
|---------|-----------------|---------------|-----------|
| Simple protocol | Supported | ✅ Existing | `BasicProtocols.swift` |
| Protocol with properties | Supported | ✅ v1.0 | `BasicProtocols.swift` |
| Protocol with methods | Supported | ✅ v1.0 | `BasicProtocols.swift` |
| Protocol inheritance | Supported | ✅ v1.0 | `BasicProtocols.swift` |
| Protocol with associated type | Partial | ✅ v1.7 | `PATs.swift` |
| Protocol composition (`A & B`) | Supported | ✅ v1.0 | `Composition.swift` |
| Type conforming to protocol | **Not Emitted** | ✅ v1.0 | `Conformance.swift` |
| Retroactive conformance | Unknown | ✅ v1.0 | `Conformance.swift` |
| Circular protocol refs | Unknown | ✅ v1.0 | `Composition.swift` |
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
| `some Protocol` (opaque) | Not Yet | ⬜ Future | `Existentials.swift` |
| Key paths (`\T.property`) | **Not Yet** | ✅ v1.7 | `KeyPaths.swift` |
| WritableKeyPath | **Not Yet** | ✅ v1.7 | `KeyPaths.swift` |
| Metatypes (`T.Type`) | **Not Yet** | ✅ v1.7 | `Metatypes.swift` |

### Closures

| Feature | Generator Status | Test Coverage | Test File |
|---------|-----------------|---------------|-----------|
| @escaping void closure | Supported | ✅ Existing | `Escaping.swift` |
| @escaping with primitives | Supported | ✅ Existing | `Escaping.swift` |
| @escaping with frozen struct | Supported | ✅ v1.0 | `Escaping.swift` |
| @convention(c) | Supported | ✅ v1.0 | `ConventionC.swift` |
| @autoclosure | **Not Yet** | ✅ v1.7 | `Autoclosures.swift` |
| Method returning closure | Supported | ✅ v1.0 | `ClosureReturns.swift` |
| Async closure | Not Yet | ⬜ Future | `Async.swift` |
| Throwing closure | Not Yet | ⬜ Future | `Escaping.swift` |
| Closure in closure | Not Supported | ⬜ Document | n/a |

### Async/Concurrency

| Feature | Generator Status | Test Coverage | Test File |
|---------|-----------------|---------------|-----------|
| Async method | Supported | ✅ v1.0 | `Methods.swift` |
| Async static method | Supported | ✅ v1.0 | `Methods.swift` |
| Async throwing method | Supported | ✅ v1.0 | `AsyncThrowing.swift` |
| Async property | Not Yet | ⬜ Future | `Properties.swift` |
| @MainActor class | **Not Yet** | ✅ v1.6 | `MainActor.swift` |
| @MainActor method | **Not Yet** | ✅ v1.6 | `MainActor.swift` |
| @Sendable closure | **Not Yet** | ✅ v1.6 | `Sendable.swift` |
| Sendable type | **Not Yet** | ✅ v1.6 | `Sendable.swift` |

### Properties

| Feature | Generator Status | Test Coverage | Test File |
|---------|-----------------|---------------|-----------|
| Stored property getter | Supported | ✅ Existing | `Getters.swift` |
| Computed property getter | Supported | ✅ Existing | `Getters.swift` |
| Property setter | **Not Yet** | ✅ v1.0 | `Setters.swift` |
| Static property | Supported | ✅ Existing | `Static.swift` |
| Lazy property | Unknown | ✅ v1.0 | `Getters.swift` |
| @propertyWrapper type | **Not Yet** | ✅ v1.6 | `Wrappers.swift` |
| Wrapped property access | **Not Yet** | ✅ v1.6 | `Wrappers.swift` |
| Projected value (`$prop`) | **Not Yet** | ✅ v1.6 | `Wrappers.swift` |

### Operators

| Feature | Generator Status | Test Coverage | Test File |
|---------|-----------------|---------------|-----------|
| Arithmetic (+, -, *, /, %) | Supported | ✅ v1.0 | `Arithmetic.swift` |
| Comparison (==, !=, <, >) | Supported | ✅ Existing | `Comparison.swift` |
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
| Failable initializer (`init?`) | **Not Yet** | ✅ v1.0 | `Failable.swift` |
| Implicitly unwrapped (`init!`) | **Not Yet** | ✅ v1.0 | `Failable.swift` |
| Throwing initializer | Supported | ✅ v1.0 | `Throwing.swift` |
| Convenience initializer | Unknown | ✅ v1.0 | `BasicInit.swift` |
| Required initializer | Unknown | ✅ v1.0 | `BasicInit.swift` |

### Parameters

| Feature | Generator Status | Test Coverage | Test File |
|---------|-----------------|---------------|-----------|
| Inout parameter (`inout`) | **Not Yet** | ✅ v1.0 | `Inout.swift` |
| Inout with frozen struct | **Not Yet** | ✅ v1.0 | `Inout.swift` |
| Variadic parameter | Unknown | ✅ v1.7 | `Variadic.swift` |
| Default parameter value | **Not Yet** | ✅ v1.0 | `Defaults.swift` |

### Error Handling

| Feature | Generator Status | Test Coverage | Test File |
|---------|-----------------|---------------|-----------|
| Synchronous `throws` method | Supported | ✅ v1.0 | `ThrowingFunctions.swift` |
| Static `throws` method | Supported | ✅ v1.0 | `ThrowingFunctions.swift` |
| Custom `Error` type | Unknown | ✅ v1.0 | `ErrorTypes.swift` |
| Error to Exception mapping | Unknown | ✅ v1.0 | `ErrorTypes.swift` |

### Foundation Interop

| Feature | Generator Status | Test Coverage | Test File |
|---------|-----------------|---------------|-----------|
| Foundation.Data | Supported | ✅ v1.5 | `Foundation/Data.swift` |
| Foundation.URL | Supported | ✅ v1.5 | `Foundation/URL.swift` |
| Foundation.Date | Unknown | ✅ v1.5 | `Foundation/Date.swift` |
| Extension on Foundation type | Unknown | ✅ v1.5 | `Foundation/Extensions.swift` |
| Retroactive conformance | Unknown | ✅ v1.5 | `Foundation/Extensions.swift` |

### Objective-C Interop

| Feature | Generator Status | Test Coverage | Test File |
|---------|-----------------|---------------|-----------|
| NSObject subclass | Unknown | ✅ v1.6 | `NSObjectSubclass.swift` |
| @objc attribute | Unknown | ✅ v1.6 | `ObjCAttributes.swift` |
| @objcMembers | Unknown | ✅ v1.6 | `ObjCAttributes.swift` |
| Selector type | Unknown | ⬜ Add | `Selectors.swift` |

### Unsafe/C-Interop Types

| Feature | Generator Status | Test Coverage | Test File |
|---------|-----------------|---------------|-----------|
| UnsafePointer<T> | Supported | ✅ v1.5 | `UnsafeTypes/Pointers.swift` |
| UnsafeMutablePointer<T> | Supported | ✅ v1.5 | `UnsafeTypes/Pointers.swift` |
| UnsafeRawPointer | Unknown | ✅ v1.5 | `UnsafeTypes/RawPointers.swift` |
| UnsafeMutableRawPointer | Unknown | ✅ v1.5 | `UnsafeTypes/RawPointers.swift` |
| OpaquePointer | Unknown | ✅ v1.5 | `UnsafeTypes/OpaquePointer.swift` |

### Memory Management (Stability Tests)

| Feature | Generator Status | Test Coverage | Test File |
|---------|-----------------|---------------|-----------|
| Circular C#↔Swift refs | Unknown | ⬜ Add | `RetainCycles.swift` |
| Non-frozen layout change | **Critical** | ✅ v1.0 | `LibraryEvolution.swift` |
| Non-frozen class | Supported | ✅ v1.5 | `LibraryEvolution.swift` |
| Non-frozen enum | Supported | ✅ v1.5 | `LibraryEvolution.swift` |
| Evolving optional fields | Unknown | ✅ v1.5 | `LibraryEvolution.swift` |
| Leak detection harness | n/a | ⬜ Add | `LeakDetection.swift` |

### Out of Scope (Compile-Time Only)

These Swift features don't appear in ABI JSON and thus don't need test coverage:

| Feature | Reason |
|---------|--------|
| Macros (`@Observable`, etc.) | Expanded at compile time; bindings see expanded code |
| Parameter packs | Compile-time variadic generics; monomorphized in ABI |
| Result builders (`@ViewBuilder`) | DSL syntax sugar; desugared before ABI |
| Property observers (`willSet`/`didSet`) | Internal implementation; not in public ABI |

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
| v1.8+ | + Remaining | Actors, selectors, other gaps |

### Test Bucketing: Must-Pass vs Known-Unsupported

Split tests into two categories from day one:

| Category | Purpose | CI Behavior |
|----------|---------|-------------|
| **must-pass** | Features that work today | Gates PRs - failures block merge |
| **known-unsupported** | Features we're tracking | No gate - tracks progress over time |

```
Tests/
├── MustPass/           # Gates PRs
│   ├── Structs/
│   ├── Classes/
│   └── ...
└── KnownUnsupported/   # Progress tracking
    ├── Actors/
    ├── PATs/
    └── ...
```

When a feature is implemented, move its tests from `KnownUnsupported/` to `MustPass/`.

### Machine-Readable Coverage Report

Auto-generate a `coverage-matrix.json` from the test results:

```json
{
  "generated": "2026-02-03T00:00:00Z",
  "summary": {
    "must_pass": { "total": 42, "passing": 42, "failing": 0 },
    "known_unsupported": { "total": 18, "passing": 3, "failing": 15 }
  },
  "features": [
    { "name": "frozen_struct", "status": "supported", "tests": 5, "passing": 5 },
    { "name": "actors", "status": "unsupported", "tests": 2, "passing": 0 }
  ]
}
```

Benefits:
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
| 1.8 | + Actors | Complete concurrency model |
| 2.0 | + Full coverage | All remaining gaps |

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
2. **Regression detection**: New changes that break existing features are caught
3. **Feature roadmap**: Unsupported features are documented with placeholder tests
4. **CI integration**: The library is built and tested in CI pipeline
5. **Developer confidence**: Engineers can implement features knowing tests will verify correctness

---

## Next Steps

### Phase 1: v1.0 Foundation
1. [x] Create `TestFramework/` directory structure with `MustPass/` and `KnownUnsupported/` split
2. [x] Write `Package.swift` manifest
3. [x] Implement Tier 1 tests (property setters, protocol conformance, throws)
4. [x] Implement Tier 2 tests (inout, failable init, default params)
5. [x] Add 1-2 ABI evolution tests (non-frozen struct layout)
6. [x] Create `build-xcframework.sh` script
7. [x] Create `generate-coverage-report.sh` that outputs `coverage-matrix.json`

### Phase 2: CI Integration
8. [ ] Add CI job: build library, generate bindings, run must-pass tests
9. [ ] Configure must-pass tests to gate PRs
10. [ ] Configure known-unsupported tests as informational (no gate)
11. [x] Document in CLAUDE.md

### Phase 3: Expansion (v1.5+)
12. [ ] Migrate relevant cases from existing `FunctionalTests/`
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
