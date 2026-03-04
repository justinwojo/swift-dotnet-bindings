# Binding Experience Roadmap

**Date**: March 4, 2026
**Source**: `swift-dotnet-packages/binding-feedback.md` — developer experience feedback on generated bindings from the perspective of a C#/MAUI developer.
**Goal**: Close the ergonomic gap between Swift-generated bindings and traditional Xamarin ObjC binding libraries. Every item below addresses real developer confusion or friction observed when consuming Lottie and BlinkIDUX bindings.

---

## Status Key

| Status | Meaning |
|--------|---------|
| Pending | Not started |
| Done | Completed and verified |

---

## Feedback Item Index

Quick reference mapping feedback items to sessions.

| # | Feedback Item | Severity | Session | Status |
|---|---------------|----------|---------|--------|
| 4 | AnyType for unresolved dependency types | High | BX1 | Done |
| 3 | SwiftOptional leaking to public API | High | BX1 | Done |
| 9 | Inconsistent optional projection | High | BX1 | Done |
| 8 | Double-wrapped collections | Medium | BX1 | Done |
| 1 | Enums as classes (simple cases) | Medium | BX2 | Done |
| 10 | HashValue instead of GetHashCode | Medium | BX3 | Pending |
| 2 | Universal IDisposable / disposal anxiety | Medium | BX3 | Pending |
| 7 | Types with zero public members | Low | BX3 | Pending |
| 5 | Protocol proxy internals visible | Low | BX3 | Pending |
| 11 | No inheritance from Apple framework types | High | BX4 | Pending |
| 6 | Structs projected as classes | Medium | Deferred | — |

---

## Session BX1: Projection Completeness

**Feedback items**: #4 (AnyType for dependency types), #3 (SwiftOptional leaking), #9 (inconsistent optional projection), #8 (double-wrapped collections)
**Theme**: Types from dependency frameworks resolving to AnyType, and Swift wrapper types leaking into public API surfaces
**Status**: Done

### #4: Framework dependency type resolution — Done

**Problem**: `--framework-dependency` only added `-F` search paths and `import` statements. It did NOT parse the dependency's ABI JSON, so any dependency type appearing in the primary library's API fell back to `AnyType`.

**Implementation**:
- Added `AbiJsonPath`, `TbdPath`, `IsAutoDetected` properties to `FrameworkDependencyInfo`
- `ResolveFrameworkDependencies()` now captures ABI JSON and TBD paths from `XCFrameworkResolver`
- `BinaryDependencyAnalyzer` stores the same paths for auto-detected dependencies (with `IsAutoDetected = true`)
- New dependency ABI parsing block in `GenerateBindings()` (between module database loading and primary ABI parsing): for each dependency with an ABI JSON, parses via `SwiftABIParser` → `ModuleProcessor` → `typeDatabase.AddModuleDatabase()`
- Error handling: explicit `--framework-dependency` fails hard (SWIFTBIND073); auto-detected deps warn and continue
- Self-reference guard: `PeekModuleNameFromAbiJson` hoisted once for both `--module-database` and `--framework-dependency` blocks
- Skip guards: ObjC-only deps, missing ABI JSON/TBD, already-loaded modules

**Key files**: `FrameworkDependencyInfo.cs`, `Program.cs`, `BinaryDependencyAnalyzer.cs`

### #3 / #9: SwiftOptional fallback for Apple framework ObjC types — Done

**Problem**: `Optional<T>` where T is an unknown Apple framework ObjC class (e.g., `CoreBluetooth.CBCentralManager`) returned null, skipping the entire method.

**Implementation**:
- Added ObjC class fallback in `TypeProjectionFactory` Optional projection path
- Dual safety guard: `IsKnownAppleModule(module)` AND `HasObjCClassPrefix(name)` — both must pass
- `KnownAppleModules`: 44-entry `HashSet<string>` of Apple framework module names
- `HasObjCClassPrefix`: checks for 2-3 letter ObjC naming convention (UI/MK/CB/WK/etc.) followed by uppercase letter
- The prefix guard prevents misprojecting Swift value types (e.g., `StoreKit.Transaction`, `Vision.RecognizedText`) that live in the same Apple modules but are NOT ObjC classes
- Additional guards: non-generic, not stdlib container, not pointer type, module-qualified
- Non-allowlisted modules (third-party, user) always return null — existing safe behavior preserved

**Key files**: `TypeProjectionFactory.cs`

### #8: Collection projection in async contexts — Done

**Problem**: Async methods returning collections used `MarshalFromSwift<IReadOnlyList<T>>` (public type) instead of `MarshalFromSwift<SwiftArray<T>>` (runtime container type), producing invalid code.

**Implementation**:
- Added `TryGetCollectionAsyncInfo()` in `WrapperEmitter.Async.cs` — uses existing `s_projectionFactory` to detect Array/Dictionary/Set projections and extract runtime container type + conversion expression
- Added `EmitAsyncWrapperForCollection()` — same OpaquePointer pattern as complex types but marshals via runtime container type
- Inserted collection check between existing `isArrayStringReturn` and `isComplexTypeReturn` paths
- `SetProjection.GetReturnContainerConversion()` null handled with identity fallback (`_collection`)
- Swift-allocated memory freed via `SBW_Free(resultPtr)` in finally block

**Key files**: `WrapperEmitter.Async.cs`

### Tests

21 new unit tests in `ProjectionCompletenessTests.cs`:
- `FrameworkDependencyAbiPathTests` (4 tests): AbiJsonPath/TbdPath storage, IsAutoDetected defaults
- `OptionalAppleFallbackTests` (12 tests): ObjC fallback positive/negative, module allowlist, prefix detection, Swift value type rejection
- `AsyncCollectionProjectionTests` (5 tests): Array/Dictionary/Set async marshalling, SBW_Free, Swift OpaquePointer

**Success criteria**: `SwiftOptional<T>` never appears in a public method signature or property type. `SwiftArray<T>` and `SwiftDictionary<K,V>` never appear as public return types. Dependency types resolve to their actual names, not AnyType.

---

## Session BX2: Simple Enum Expansion — Done

**Feedback item**: #1 (enums as classes)
**Theme**: No-payload Swift enums should feel like C# enums
**Status**: Done

### Problem

`CanSafelyEmitAsSimpleEnum()` was too conservative — it rejected enums with static methods, computed properties, or incompatible instance method signatures, pushing them to the heavyweight class path (SafeHandle + ISwiftObject + IDisposable). Real-world enums like `RenderingEngineOption`, `DocumentSide`, and `BlinkIDScanningAlertType` have `CustomStringConvertible` conformance or static members, yet carry no payload data.

### Implementation

**Paradigm shift**: From "if any member can't be emitted, fall to class path" → "emit as C# enum value type, emit compatible members as extensions, skip incompatible members with ReportCollector tracking."

**Gate relaxation** (`CanSafelyEmitAsSimpleEnum`): Removed 3 of 5 gates (properties, static methods, incompatible instance methods). Only nested types and non-equality operators still block the simple path. Simplified `IsStringRawValueSimpleEnum` to match.

**Member emission on the simple path**:
- **Instance methods**: Emitted as `public static {ReturnType} {Name}(this EnumType self)` extension methods. Swift wrapper converts scalar tag → enum, calls method, converts result back.
- **Instance properties**: Emitted as `public static {ReturnType} Get{Name}(this EnumType self)` extension getters. Setters skipped (C# extension methods receive a copy — mutations can't propagate).
- **Static methods**: Emitted as static methods on `{EnumName}Extensions` class. Enum-typed params cast to underlying scalar at P/Invoke boundary.
- **Static properties**: Emitted as static properties on `{EnumName}Extensions` class.
- **`CustomStringConvertible.description`**: Emitted as `GetDescription(this EnumType self)` using Utf8Slice string marshalling (same pattern as WitnessDispatch and RawRepresentable).
- **`CaseIterable.allCases`**: Pure C# — `Enum.GetValues<EnumType>()` wrapped in `Array.AsReadOnly`. No Swift P/Invoke needed.

**String marshalling**: String-returning members use `SBW_Utf8Slice` struct + `SBW_Free` deallocation, same as existing WitnessDispatch and RawRepresentable paths. `Utf8SliceEmitter` dedup via `ModuleEmissionContext` prevents duplicate struct declarations.

**Swift wrapper visibility**: Wrappers use `@_cdecl` (not `@_silgen_name`) with `public func` to ensure exported symbol visibility for `dlsym`-based `LibraryImport` resolution.

**Non-RawRepresentable enum param conversion**: Enum-typed method parameters use tag-to-case switch conversion (inline closure) for enums without `rawValue` initializer, and `rawValue:` init for RawRepresentable enums.

**Member-loss policy**: Any member emittable on the class path but not on the simple path is recorded via `ReportCollector.RecordMemberSkipped` with a specific `SkipReason`. This explains why CryptoSwift and StripeFinancialConnections show `generate: fail` (skipped members logged as warnings) — both still compile with 0 errors.

**Key files**: `EnumHandler.SimpleEnum.cs` (gate relaxation, 4 new emission methods, string marshalling, CaseIterable), `EnumDecl.cs` (simplified `IsStringRawValueSimpleEnum`), `EnumHandler.cs` (updated decision point comment)

### Tests

47 new unit tests across `EnumHandlerOutputTests.cs`:
- Gate tests: relaxed gates return true for properties, static methods, instance properties
- Instance method/property emission: extension methods with P/Invoke, Utf8Slice for strings, enum-returning casts
- Static method/property emission: static methods on extensions class, enum param casting, factory patterns
- CaseIterable: pure C# `AllCases` with `Enum.GetValues`
- ABI correctness: instance enum params use scalar in Swift wrapper, non-RawRepresentable uses tag switch, bool params get `[MarshalAs(UnmanagedType.U1)]`, string-return paths handle enum/bool params correctly
- Mixed compatibility: compatible members emitted, incompatible skipped, enum stays simple

### Success criteria

No-payload enums with `CustomStringConvertible`, `CaseIterable`, static members, or computed properties project as C# enums. All 53 validation library targets pass (no regressions). Enums with ANY associated value case remain on the class path.

---

## Session BX3: .NET Idiom Polish

**Feedback items**: #10 (HashValue vs GetHashCode), #2 (disposal anxiety), #7 (zero-member types), #5 (proxy visibility)
**Theme**: Small targeted improvements that make bindings feel more .NET-native

### #10: Suppress redundant Hashable/Equatable members

**Current state**: The generator ALREADY emits `GetHashCode()` overrides for Hashable types (delegates to `SwiftHashable.GetHashCode(this)`) and `Equals()`/`operator ==`/`operator !=` for Equatable types. However, it ALSO emits the raw Swift protocol members — `HashValue` property and `Hash(Hasher)` method — as regular projected members. These are redundant noise.

**Fix**: Suppress emission of `hashValue` (property) and `hash(into:)` (method) when the type conforms to Hashable, since `GetHashCode()` already covers this. Same pattern as `EnumHandler.SimpleEnum.cs` line 707-708 which already filters these for simple enums — extend to complex enums and classes.

**Key files**: `EnumHandler.SimpleEnum.cs` (existing filter), `MethodHandler.cs`, `PropertyHandler.cs` (add filter)

### #2: XML doc comments for ownership semantics

**Fix**: Emit XML doc comments on generated types and key patterns:
- On `ISwiftObject`-implementing types: `/// <summary>Wraps a Swift {struct/class/enum}. Call Dispose() when done, or use 'using' statements.</summary>`
- On enum case singleton properties: `/// <summary>Cached singleton — does not require disposal.</summary>`
- On property getters that return owned references: `/// <remarks>Returns an owned reference. Dispose when no longer needed.</remarks>`
- On `Dispose()`: `/// <summary>Releases the underlying Swift object. Safe to call multiple times.</summary>`

This is low-effort, high-signal guidance that eliminates disposal anxiety without changing any behavior.

**Key files**: `ClassHandler.cs`, `EnumHandler.cs`, `PropertyHandler.cs` (emission points for doc comments)

### #7: Opaque type annotations

**Fix**: When a generated type has zero public members (no properties, methods, or constructors beyond ISwiftObject/IDisposable infrastructure), emit a type-level XML doc comment and attribute:

```csharp
/// <summary>
/// Opaque Swift type. No public API members could be projected.
/// 3 members skipped: 2 use unsupported types, 1 uses unsupported generics.
/// </summary>
[OpaqueSwiftType(SkippedMembers = 3)]
public partial class BlinkIDResultState : ISwiftObject, IDisposable { ... }
```

Count the skipped members from `[UnsupportedSwiftType]` attributes that were suppressed. This tells the developer the type isn't empty by design — the generator couldn't project its members.

**Key files**: `ClassHandler.cs`, `TypeHandlerHelpers.cs` (post-emission member counting)

### #5: Protocol proxy visibility polish

**Current state**: Proxy classes are `public` with `[EditorBrowsable(Never)]`. They must be public for cross-assembly protocol conformance scenarios.

**Fix**: Move proxy classes into a `{Namespace}.SwiftInterop` sub-namespace. This keeps them public (required) but separates them from the primary API namespace. IntelliSense won't show them unless the developer explicitly imports the interop namespace.

**Key files**: `ProtocolProxyEmitter.cs`, namespace emission logic

**Success criteria**: A developer browsing the generated namespace sees only user-facing types. `SwiftInterop` sub-namespace contains proxy classes, witness tables, and other interop plumbing.

---

## Session BX4: Apple Framework Type Hierarchy

**Feedback item**: #11 (no inheritance from Apple framework base types)
**Theme**: Swift classes extending UIKit/CoreAnimation types should participate in the Apple type hierarchy from C#

### Problem

`LottieAnimationLayer` inherits from `CALayer` in Swift but is projected as a flat type implementing only `ISwiftObject`. You can't pass it where a `CALayer` is expected, can't use `CALayer` methods, can't add it as a sublayer. This is the most impactful limitation for UI-heavy libraries.

### Why this is hard

Apple framework types (`UIView`, `CALayer`, `NSObject`) live in the ObjC binding world (Xamarin/MAUI's `ObjCRuntime`). Swift bindings live in the Swift interop world (`SwiftSafeHandle`, `ISwiftObject`). Bridging them requires one of:

1. **Dual inheritance** (not possible in C#) — can't inherit from both `CALayer` and have `SwiftSafeHandle`
2. **ObjC base class + Swift interop interface** — the generated class inherits from the ObjC binding's `CALayer` and implements `ISwiftObject` via composition. Swift method dispatch goes through the Swift handle; inherited ObjC methods go through the ObjC handle. Both handles point to the same underlying object (Swift classes that extend ObjC classes are ObjC-compatible).
3. **Implicit conversion operators** — lightweight approach where `LottieAnimationLayer` has an implicit conversion to `CALayer` via handle unwrapping. Less seamless but much simpler.

### Approach

This session starts with a design spike to evaluate options 2 and 3 against real library patterns (Lottie's `LottieAnimationLayer : CALayer`, SnapKit's constraint types, etc.). Then implement the chosen approach.

**Design considerations**:
- Swift classes inheriting ObjC classes share the same object pointer — `Unmanaged.toOpaque()` on the Swift side yields the ObjC object pointer
- MAUI's ObjC binding runtime can wrap an existing `IntPtr` as a managed `CALayer` via `Runtime.GetNSObject<CALayer>(ptr)`
- The generator already knows the superclass chain from ABI JSON (`Superclass` field on `ClassDecl`)
- Scope to ObjC-rooted class hierarchies only (pure Swift class inheritance already works via skip-to-ancestor dispatch)

**Key files**: `ClassHandler.cs`, `TypeHandlerHelpers.cs`, `SwiftABIParser.cs` (superclass chain), runtime interop layer (new)

**Success criteria**: `LottieAnimationLayer` can be passed to any API expecting `CALayer`. `layer.Sublayers` returns it in the collection. Basic `CALayer` properties (frame, bounds, opacity) are accessible on the generated type.

---

## Deferred Items

### #6: Structs projected as classes

**Verdict**: Not worth pursuing as a general solution. Likely not worth pursuing even for the limited safe subset, unless a real user hits measurable perf issues.

**Why Swift structs can't simply be C# structs**: Swift structs aren't like C structs. They can have non-trivial copy constructors (value witnesses), internally reference-counted fields (e.g., a struct containing a `String` or `Array`), and opaque layout that changes between Swift versions (non-frozen). The only safe subset for C# struct projection is **frozen + fully blittable** — no strings, no reference fields, no generics. That's a very small slice of real-world Swift structs.

**Why the benefit is marginal even for the safe subset**: A `LottieColor` class with `.R`, `.G`, `.B`, `.A` properties is nearly as usable as a C# struct. What you lose with class projection:
- Stack allocation (minor perf in most scenarios)
- Value semantics (copy-on-assign instead of reference sharing)
- Pattern matching with `is` / `switch`
- Direct field access without P/Invoke

None of these affect API discoverability or correctness — which is what the rest of this roadmap targets. The bigger pain point cited in the feedback ("no way to read R, G, B, A back out") is a member projection issue (#7), not a struct-vs-class issue. Fixing member visibility solves the practical problem without changing the type's projection model.

**The one scenario where it matters**: Hot-path performance — creating thousands of value-type instances per frame, where heap allocation + disposal overhead adds up. This is niche, and the workaround (pre-allocate, reuse) is well-understood in .NET.

**If we ever revisit**: Scope to frozen + fully blittable structs only. Requires layout computation from Swift type metadata at generation time, plus a parallel projection path through the entire pipeline (marshalling, P/Invoke, wrappers). High effort, narrow applicability. Only pursue if a real user reports measurable overhead from struct-as-class in a profiling scenario.

---

## Session Dependency Graph

```
BX1 (Projection Completeness)     BX2 (Simple Enum Expansion)
         │                                  │
         └──────────┬───────────────────────┘
                    │
             BX3 (.NET Idiom Polish)
                    │
             BX4 (Type Hierarchy)
```

BX1 and BX2 are independent and can run in either order. BX3 should follow both (it includes verification of projection and enum changes). BX4 is independent but benefits from BX3's polish being in place first.
