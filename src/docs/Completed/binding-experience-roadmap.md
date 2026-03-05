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
| 10 | HashValue instead of GetHashCode | Medium | BX3 | Done |
| 2 | Universal IDisposable / disposal anxiety | Medium | BX3 | Done |
| 7 | Types with zero public members | Low | BX3 | Done |
| 5 | Protocol proxy internals visible | Low | BX3 | Done |
| 11 | No inheritance from Apple framework types | High | BX4 | Done |
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

## Session BX3: .NET Idiom Polish — Done

**Feedback items**: #10 (HashValue vs GetHashCode), #2 (disposal anxiety), #7 (zero-member types), #5 (proxy visibility)
**Theme**: Small targeted improvements that make bindings feel more .NET-native
**Status**: Done

### #10: Suppress redundant Hashable/Equatable members — Done

**Problem**: The generator emits `GetHashCode()` overrides for Hashable types AND the raw Swift `hashValue` property and `hash(into:)` method. The raw members are redundant noise.

**Implementation**:
- Added `IsSynthesizedProtocolProperty` and `IsSynthesizedProtocolMethod` to `MemberEmissionValidator` — detects `hashValue` (property) and `hash(into:)` (method) on types conforming to Hashable
- Added `GetConformances(TypeDecl)` helper using pattern matching (ClassDecl/StructDecl/EnumDecl each declare Conformances separately)
- Property filter added before `CanEmitProperty` in 4 handlers: ClassHandler, EnumHandler, NonFrozenStructHandler, FrozenStructHandler
- Method filter added in `IHandler.cs` HandleBaseDecl, before signature dedup
- `EnumHandler.SimpleEnum.IsSynthesizedMethod` now delegates to the public `MemberEmissionValidator.IsSynthesizedProtocolMethod`
- Changed `RecordMemberEmitted` to `RecordMemberSynthesized` in SimpleEnum path for consistency (synthesized members have .NET equivalents but aren't directly emitted)

**Key files**: `MemberEmissionValidator.cs`, `ClassHandler.cs`, `EnumHandler.cs`, `EnumHandler.SimpleEnum.cs`, `NonFrozenStructHandler.cs`, `FrozenStructHandler.cs`, `IHandler.cs`

### #2: XML doc comments for ownership semantics — Done

**Problem**: `ClassHandler` had `EmitDisposalRemarks()` but complex enums and struct handlers did not. Inline `Dispose()` methods lacked doc comments. Cached singleton case properties didn't explain they're disposal-free.

**Implementation**:
- Extracted `TypeAnnotationHelper` static class in `TypeHandlerHelpers.cs` with `EmitDisposalRemarks(csWriter, typeDecl)` — derives Swift kind ("class"/"struct"/"enum") from the TypeDecl subtype, skips if symbol graph already has remarks
- All 4 type handlers call `TypeAnnotationHelper.EmitDisposalRemarks` after `XmlDocCommentEmitter.EmitDocComment`
- Added `/// <summary>Releases the underlying Swift object. Safe to call multiple times.</summary>` before inline `Dispose()` in ClassHandler, EnumHandler, NonFrozenStructHandler, FrozenStructHandler
- Added `/// <remarks>Cached singleton instance — does not require disposal.</remarks>` on cached singleton case properties in both EnumHandler (tag-based) and EnumHandler.RawRepresentable (raw-value-based)

**Key files**: `TypeHandlerHelpers.cs`, `ClassHandler.cs`, `EnumHandler.cs`, `EnumHandler.RawRepresentable.cs`, `NonFrozenStructHandler.cs`, `FrozenStructHandler.cs`

### #7: Opaque type annotations — Done

**Problem**: Types with zero projectable public members appear as empty wrappers with no explanation.

**Implementation**:
- Created `OpaqueSwiftTypeAttribute` in Swift.Runtime (`[AttributeUsage(Class|Struct)]`, constructor takes `skippedMemberCount`)
- Added `CountEmittableMembers(TypeDecl, ITypeDatabase)` to `MemberEmissionValidator` — pre-scans properties (via `CanEmitProperty`), methods (via `ShouldSkipMethodEmission`), constructors; returns `(int emittable, int skipped)`. Filters out accessor methods and module-internal methods to match actual emission paths
- Added `TypeAnnotationHelper.EmitOpaqueTypeAnnotation(csWriter, skippedCount)` — emits `[OpaqueSwiftType]` attribute + opaque handle remarks
- All 4 type handlers: when `emittable == 0 && skipped > 0`, emit opaque annotation; otherwise emit disposal remarks

**Key files**: `OpaqueSwiftTypeAttribute.cs` (new), `MemberEmissionValidator.cs`, `TypeHandlerHelpers.cs`, `ClassHandler.cs`, `EnumHandler.cs`, `NonFrozenStructHandler.cs`, `FrozenStructHandler.cs`

### #5: Protocol proxy sub-namespace — Done

**Problem**: Proxy classes share the main API namespace, cluttering type lists even with `[EditorBrowsable(Never)]`.

**Implementation**:
- Added `DeferredProxyClasses` list to `ModuleEmissionContext` (follows existing pattern of `_protocolExtWrapperLines`)
- Modified `ProtocolHandler.EmitProtocolProxy()` — when `ModuleEmissionContext` is available, buffers proxy output to `StringWriter` + `CSharpWriter` and stores in `DeferredProxyClasses`; falls back to direct emission for unit tests without context
- Modified `ModuleHandler` to emit `using {generatedNamespace}.SwiftInterop;` in the usings block, and flush deferred proxies in a `namespace {generatedNamespace}.SwiftInterop { }` block after the main namespace
- SwiftInterop namespace always emitted (even empty) so the using directive resolves

**Key files**: `ModuleEmissionContext.cs`, `ProtocolHandler.cs`, `ModuleHandler.cs`

### Tests

26 new unit tests in `DotNetIdiomPolishTests.cs`:
- **Item #10** (10 tests): `IsSynthesizedProtocolProperty` positive/negative/static, `IsSynthesizedProtocolMethod` positive/negative/static/constructor, full emission suppression for class/struct/complex enum
- **Item #2** (4 tests): disposal remarks on complex enum/non-frozen struct, Dispose doc comment, cached singleton remarks
- **Item #7** (7 tests): `CountEmittableMembers` all-skipped/some-emittable/accessor-filtering/module-internal-filtering, opaque attribute emission, no-attribute-when-members-exist, no-attribute-when-empty
- **Item #10 metrics** (1 test): `RecordMemberSynthesized` no-throw
- **Item #5** (4 tests): SwiftInterop namespace presence, proxy inside SwiftInterop block not in main namespace, empty SwiftInterop when no protocols, using directive

**Success criteria**: `hashValue`/`hash(into:)` absent from generated output for Hashable types. All types have disposal guidance or opaque annotation. Proxy classes appear under `{Module}.SwiftInterop` namespace. All 53 validation library targets pass.

---

## Session BX4: Apple Framework Type Hierarchy — Done

**Feedback item**: #11 (no inheritance from Apple framework base types)
**Theme**: Swift classes extending UIKit/CoreAnimation types should participate in the Apple type hierarchy from C#
**Status**: Done

### Problem

`LottieAnimationLayer` inherits from `CALayer` in Swift but was projected as a flat type implementing only `ISwiftObject`. You couldn't pass it where a `CALayer` is expected, couldn't use `CALayer` methods, couldn't add it as a sublayer. This was the most impactful limitation for UI-heavy libraries.

### Solution

Option 2 — **ObjC base class + Swift interop interface**. For ObjC-compatible Swift classes, `swift_release == objc_release` (same ARC), so `NSObject.Dispose()` handles lifecycle correctly. The generated class inherits from the MAUI ObjC binding type and implements `ISwiftObject`:

```csharp
// Before
public partial class LottieAnimationLayer : ISwiftObject, IDisposable { ... }

// After
public partial class LottieAnimationLayer : CoreAnimation.CALayer, ISwiftObject { ... }
```

### Implementation

**Model layer**:
- `ClassDecl.HasObjCSuperclass` — direct ObjC base detection via `c:` USR prefix
- `ClassDecl.IsObjCRooted` — set by `ModuleProcessor.ResolveClassHierarchy()` via fixed-point loop: direct ObjC base → transitive through resolved superclass → cross-module TypeRecord flag lookup
- `TypeRecordFlags.ObjCRooted` (1 << 8) — serialized/deserialized in module database XML for cross-module transitivity
- Generic superclass names (containing `<`) guarded against `SwiftTypeName.FromModuleQualifiedName` crash

**Namespace mapping**: Static `SwiftModuleToNetNamespace` dictionary: `ObjectiveC` → `Foundation`, `QuartzCore` → `CoreAnimation`, `Dispatch` → `CoreFoundation`. All others pass through 1:1. `GetObjCBaseTypeName()` maps Swift module-qualified superclass name to .NET namespace-qualified type. Cross-module transitive ObjC-rooted classes with unresolved Swift parents fall back to `Foundation.NSObject`.

**Emission (ClassHandler)**:
- **Interface list**: ObjC boundary classes use mapped ObjC base type (e.g., `CoreAnimation.CALayer`) instead of `IDisposable`. Derived ObjC-rooted classes inherit from their same-module Swift parent (existing logic). Cross-module transitives fall back to `Foundation.NSObject`.
- **No `_payload` / Dispose / finalizer**: All gated by `!isObjCRooted`. NSObject manages lifecycle via ARC.
- **`ISwiftObject.SwiftHandle => Handle`**: NSObject.Handle IS the Swift object pointer.
- **Constructor pattern**: Internal `SwiftHandle` ctor chains `base((ObjCRuntime.NativeHandle)handle.Pointer)` + `DangerousRelease()` to balance ARC (Swift +1, MAUI retain +2, release back to +1). Protected `NativeHandle` ctor for same-module subclass chaining (no release). Public constructors use static `CreateSwiftInstance_{name}(args)` helper — resolves handle BEFORE `base()`, null/error checked before construction.
- **NewFromPayload**: Reads pointer, frees buffer, wraps via `SwiftHandle`.
- **MarshalToSwift**: Uses `Handle` directly, no SafeHandle add/release.

**Self-parameter**:
- `_selfClassObjC` parameter name in `PInvokeEmitter` → resolved to `Handle` in `MethodSignature`
- `WrapperEmitter.Marshalling` skips `EmitSafeHandleAddRef`/`Release` for ObjC-rooted classes
- `MethodMarshalPlanBuilder` adds `SwiftSelfKind.ObjCRootedClass` → `new SwiftSelf((void*)Handle)`

**Type projection**: `ObjCRootedClassProjection` — parameters use call-scoped stackalloc temp buffer (`IntPtr* buf = stackalloc IntPtr[1]; *buf = param.Handle`); returns use same pattern as `ClassProjection` (allocate buffer, `MarshalFromSwift<T>`).

**Async**: `WrapperEmitter.Async` — ObjC-rooted classes use `Handle` directly for `_selfPtr` (no `_payload.DangerousGetHandle()` dereference), still `Arc.Retain` for async safety.

**Key files**: `ClassDecl.cs`, `TypeRecord.cs`, `MarshallingHelpers.cs`, `ModuleProcessor.cs`, `ModuleDatabaseEmitter.cs`, `TypeDatabase.cs`, `ClassHandler.cs`, `WrapperEmitter.cs`/`.Signature.cs`/`.Return.cs`/`.Marshalling.cs`/`.Async.cs`, `PInvokeEmitter.cs`, `MethodSignature.cs`, `MethodMarshalPlanBuilder.cs`, `MethodMarshalPlan.cs`, `TypeProjectionFactory.cs`, `ObjCRootedClassProjection.cs` (new)

### Tests

52 new unit tests in `ClassObjCRootedTests.cs`:
- **Model tests** (7): `HasObjCSuperclass` for ObjC/Swift/no USR, `IsObjCRooted` default/settable
- **Resolution tests** (7): Direct ObjC base, transitive 2-level and 3-level, pure Swift not rooted, reverse declaration order, generic superclass name guard (RxSwift regression)
- **TypeRecord tests** (4): Flag bit value, `IsObjCRooted` helper positive/negative/combined
- **Serialization tests** (2): Round-trip XML persistence, default-false when absent
- **Namespace mapping tests** (6): QuartzCore→CoreAnimation, ObjectiveC→Foundation, Dispatch→CoreFoundation, UIKit passthrough, null for non-ObjC/Swift superclass
- **Emission tests** (15): Declaration with ObjC base, no IDisposable, no _payload, no Dispose/finalizer, SwiftHandle→Handle, constructors (SwiftHandle chain, DangerousRelease, protected NativeHandle), NewFromPayload (free+wrap), MarshalToSwift (Handle, no SafeHandle)
- **Derived class tests** (3): Inherits from Swift parent not ObjC type, no payload, has DangerousRelease
- **Cross-module transitive tests** (3): Falls back to Foundation.NSObject, no payload, ObjC-rooted constructors
- **Projection tests** (5): PublicType, PInvokeType, parameter stackalloc, return MarshalFromSwift, element conversion via Handle

### Success criteria

`LottieAnimationLayer : CoreAnimation.CALayer`, `AnimatedControl : UIKit.UIControl`, `AnimatedButton : AnimatedControl` (derived from same-module Swift parent). No `_payload`, `Dispose()`, or finalizer on ObjC-rooted classes. Self-params use `Handle` directly. All 53 validation library targets pass (no regressions). Line count decreases in Stripe/FSPagerView libraries are expected (removed payload/dispose boilerplate).

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
