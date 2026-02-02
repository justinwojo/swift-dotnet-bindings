# Nuke Swift Binding Roadmap

This document tracks the effort to make the [Nuke](https://github.com/kean/Nuke) Swift image loading library fully consumable from .NET for iOS. Nuke serves as a real-world test case for validating the binding generator against a production Swift library.

## Why Nuke?

Nuke is an ideal test case because it:
- Is a popular, actively maintained Swift library (MIT licensed)
- Uses modern Swift features (async/await, protocols, generics)
- Has a non-trivial API surface (~30 classes, 8 protocols)
- Exercises many code paths in the binding generator

---

## Completed Phases

The following phases have been completed. See individual documents for details:

| Phase | Description | Document |
|-------|-------------|----------|
| Phase 1 | Infrastructure (Required to Run Anything) | [phase-1-infrastructure.md](CompletedPhases/phase-1-infrastructure.md) |
| Phase 2 | Type System Gaps | [phase-2-type-system-gaps.md](CompletedPhases/phase-2-type-system-gaps.md) |
| Phase 3 | Method Signature Gaps | [phase-3-method-signature-gaps.md](CompletedPhases/phase-3-method-signature-gaps.md) |
| Phase 4 | Runtime Infrastructure | [phase-4-runtime-infrastructure.md](CompletedPhases/phase-4-runtime-infrastructure.md) |
| Phase 5 | Testing & Validation | [phase-5-testing-validation.md](CompletedPhases/phase-5-testing-validation.md) |
| Phase 6 | Protocol Interface Completeness | [phase-6-protocol-interface-completeness.md](CompletedPhases/phase-6-protocol-interface-completeness.md) |
| Phase 7 | Protocol Proxy Emitter | [phase-7-protocol-proxy-emitter.md](CompletedPhases/phase-7-protocol-proxy-emitter.md) |
| Phase 8 | Remaining Validation & Bug Fixes | [phase-8-validation-bug-fixes.md](CompletedPhases/phase-8-validation-bug-fixes.md) |
| Phase 9 | Binding Gap Reduction | [phase-9-binding-gap-reduction.md](CompletedPhases/phase-9-binding-gap-reduction.md) |
| Phase 10 | Remaining Binding Gap Fixes | [phase-10-binding-gap-fixes.md](CompletedPhases/phase-10-binding-gap-fixes.md) |
| Phase 11 | Advanced Binding Gap Fixes | [phase-11-binding-gap-fixes.md](CompletedPhases/phase-11-binding-gap-fixes.md) |
| Phase 12 | CoreFoundation/TupleHandler Fixes | [phase-12-corefoundation-tuple-fixes.md](CompletedPhases/phase-12-corefoundation-tuple-fixes.md) |
| Phase 13 | Optional Closures & AsyncStream Fixes | [phase-13-optional-closures.md](CompletedPhases/phase-13-optional-closures.md) |
| Phase 14 | Async Tuple Return Support | [phase-14-async-tuple-returns.md](CompletedPhases/phase-14-async-tuple-returns.md) |
| Phase 15 | Throwing Closures Support | [phase-15-throwing-closures.md](CompletedPhases/phase-15-throwing-closures.md) |
| Phase 16 | Bug Fixes & Stability | See Phase 16+ section below |
| Phase 17 | RawRepresentable Enum Support (Partial) | See Phase 17 section below |
| Phase 18 | Full Non-Frozen RawRepresentable Enum Support | See Phase 18 section below |
| Phase 19 | Enum Associated Values Support | See Phase 19 section below |
| Phase 20 | Enum Associated Value Extraction | See Phase 20 section below |
| Phase 21 | Generic Type Parameter & Swift Wrapper Fixes | [phase-21-generic-param-fixes.md](CompletedPhases/phase-21-generic-param-fixes.md) |
| Phase 22 | Generic Protocol Proxy Compilation Fix | [phase-22-generic-protocol-proxy-fix.md](CompletedPhases/phase-22-generic-protocol-proxy-fix.md) |
| Phase 23 | Integration Test Restoration | [phase-23-integration-test-restoration.md](CompletedPhases/phase-23-integration-test-restoration.md) |
| Phase 24 | Tuple Associated Values in Enum TryGet | See Phase 24 section below |
| Phase 25 | Async DllImport Fix | See Phase 25 section below |
| Phase 26 | Native Type Remapping (URL → NSUrl, Data → NSData) | See Phase 26 section below |
| Phase 27 | Swift Class Return Value Marshalling Fix | See Phase 27 section below |
| Phase 28 | Async+Throwing Closures Support | [phase-28-async-throwing-closures.md](phase-28-async-throwing-closures.md) |
| Phase 29 | ExistentialContainer Workaround (ImageRequestFactory) | See Phase 29 section below |
| Phase 30 | Generic Operator & Member Collision Fixes | See Phase 30 section below |
| Phase 31 | Generic DllImport, Protocol Proxy & AnyType Fixes | See Phase 31 section below |
| Phase 32 | Optional-Wrapped Existential Property Accessor Fix | See Phase 32 section below |

---

## Current State

**Generated**: ~18,400+ lines of C# code
- 30+ classes implementing `ISwiftObject`
- 8 protocol interfaces (all fully typed - no AnyType fallbacks)
- 8 protocol proxy classes with witness table export
- Protocol subscripts emitted as C# indexers
- Property getters and setters
- AsyncStream properties with `IAsyncEnumerable<T>` return types
- Closure properties with frozen and non-frozen struct parameters
- P/Invoke declarations with Swift calling convention
- Operators on generic types with correct type parameters (Phase 30)
- Member name collision detection (Phase 30)
- Generic types with DllImport via helper classes (Phase 31)
- Protocol proxy generation with full framework imports (Phase 31)
- Generic type parameter handling in TypeDatabase (Phase 31)
- Optional-wrapped existential properties with accessor methods (Phase 32)
- **0 compilation errors** (down from 95+)
- **935 unit tests passing**
- **Integration tests**: 678 passed, 13 skipped (known limitations)
- **100% runtime validation pass rate** (34/34 tests in NukeTestApp)
- **Cross-library validated**: BlinkID (0 errors), Lottie (0 errors)

**Runtime validated** (Phase 15.4):
- ✅ Async image loading from network URLs
- ✅ UIImage return type marshalling to .NET iOS types
- ✅ Memory management (no retain count leaks detected)
- ✅ Protocol proxy implementations (C# → Swift callbacks)
- ✅ Cache access and population
- ✅ SwiftString and ImageRequest creation/disposal

**Remaining gaps** (in priority order):

1. **Combine Framework** - `imagePublisher(...)` returns `AnyPublisher` (reactive framework out of scope for this project).

2. **Generic Protocol Proxies** - Protocols with associated types (PATs) skip proxy generation. Can consume Swift implementations but can't implement from C#.

3. **SwiftArray<ExistentialContainer> Metadata** - Creating `SwiftArray<ExistentialContainer1>` crashes due to Mono JIT bug. **Workaround available**: Use `ImageRequestFactory` (see Phase 29). Full direct API access blocked until Mono fixes the underlying JIT issue. See `/src/docs/known-issues-workarounds.md` for details.

---

## Baseline Analysis

Initial binding generation (before any phases) revealed these gap categories:

| Category | Initial Count | Current Status |
|----------|---------------|----------------|
| Protocol conformance descriptors not found | 200 | ✅ Warnings only, types still emit |
| Unsupported accessor kinds (set/_modify) | 128 | ✅ **FIXED** - Property setters implemented |
| Unsupported method signatures | 67 | ✅ **FIXED** - Only 2 remain (Combine framework) |
| Unsupported property types | 70 | ✅ **FIXED** - Existentials, closures, generics supported |
| Generic protocol types unsupported | 8 | ⚠️ Partial - Can consume but not implement from C# |
| Unsupported constructor signatures | 22 | ✅ **FIXED** - All constructors now supported including async+throwing closures with Data return |

**Initial result**: Generated 417KB of C# bindings with 52 types, but many methods/properties skipped.

**Current result** (after 31 phases): ~18,400+ lines of C# code, 30+ classes, 8 protocols. Only 2 items skipped:
- 2 methods returning Combine `AnyPublisher` (out of scope - reactive framework)

---

## Known Limitations

### Async P/Invoke with SafeHandle
**Status**: WORKAROUND APPLIED (see Phase 8)

The .NET runtime does not support passing non-blittable types (like `SafeHandle`) through P/Invoke with Swift calling convention. A workaround using `IntPtr` and proper Swift copy semantics has been implemented.

For singleton classes like `ImagePipeline`, the generator automatically detects the singleton pattern (static `shared` property returning Self) and uses `ClassName.shared.method()` instead of passing `self` in async contexts. This is implemented in `TypeDecl.HasSingletonPattern` and `MethodHandler.cs`.

For non-singleton classes, async instance methods use `unsafeBitCast(_self, to: ClassName.self)` to convert the IntPtr back to the class instance. This approach may have limitations with certain Swift class hierarchies.

### URL.FromString Non-Blittable Issue
**Status**: Known issue

The `Swift.URL.FromString()` method fails with the same non-blittable type error.

**Workaround**: Use `ImageRequest(SwiftString)` constructor directly.

### Protocol Conformance Descriptors
**Status**: Warnings only

200 warnings about missing protocol conformance descriptors for built-in Swift 5.9+ protocols:
- `Swift.Copyable`
- `Swift.Escapable`
- `Swift.Sendable`

Bindings still generate, but conformance information is incomplete.

### Non-Frozen Struct with Existential Containers
**Status**: FIXED (Phase 27)

Properties returning non-frozen struct types that contain existential containers previously crashed at runtime with SIGSEGV in Swift's copy witness.

**Phase 16.1 fixed**: SafeHandle ref counting for class instance methods (e.g., `ImagePipeline.CacheValue` now works).

**Phase 27 fixed**: Swift class return value marshalling. The root cause was that Swift classes return pointers directly in registers (x0 on ARM64), not via `SwiftIndirectResult` buffers. The generator was incorrectly treating class types like non-frozen structs.

**Now working**:
- `ImagePipeline.Shared` - Returns valid class pointer
- `ImagePipeline.ConfigurationValue` - The `Configuration` struct with existential container fields now works correctly

### Generic Protocol Proxy Classes
**Status**: Graceful degradation (Phase 22)

Protocols with associated types (PATs) generate generic proxy classes like `ContainerProxy<TElement>`. These classes would contain `[UnmanagedCallersOnly]` callback methods for Swift-to-C# callbacks, but C# doesn't allow `[UnmanagedCallersOnly]` methods inside generic types.

**Original errors** (now resolved):
```
error CS8895: Methods attributed with 'UnmanagedCallersOnly' cannot have generic type parameters
error CS7042: The DllImport attribute cannot be applied to a method that is generic or contained in a generic type
```

**Solution**: Phase 22 implemented graceful degradation - the generator now:
1. Marks protocols with associated types via `TypeRecordFlags.HasAssociatedTypes`
2. Skips proxy class generation for these protocols with a warning
3. Skips using them as generic constraints (can't use `ISwiftContainer<TElement>` without type arguments)
4. Skips them in protocol conformance dictionaries

**Affected**: Protocols with associated types cannot be implemented from C# (no proxy generation). They can still be consumed as interface types when Swift provides the implementation.

**Future work**: More sophisticated support could use runtime code generation or specialized proxy classes per type argument.

### SwiftArray<ExistentialContainer> Metadata Crash
**Status**: WORKAROUND IMPLEMENTED (Phase 29) - Swift wrapper functions bypass the crash

Creating a `SwiftArray<ExistentialContainer1>` fails at runtime due to a Mono JIT bug with `swift_getExistentialTypeMetadata`.

**Symptoms**:
```
* Assertion at mono/metadata/jit-info.c:918, condition `!ji->async' not met

Managed Stacktrace:
  at Swift.Runtime.TypeMetadata:swift_getExistentialTypeMetadata
  at Swift.Runtime.TypeMetadata:GetExistentialTypeMetadata
  ...
```

**Root cause**: The Swift runtime function `swift_getExistentialTypeMetadata` triggers a Mono JIT assertion about async state. This is a Mono runtime bug, not a binding generator issue. See `/src/docs/known-issues-workarounds.md` for full technical details.

**Mitigations applied** (Phase 28.1):
1. **Lazy initialization** in `SwiftArray<T>` - Element metadata lookup deferred from static constructor to first use
2. **Graceful error** - `TypeMetadata.TryGetTypeMetadataUncached` throws a descriptive `SwiftRuntimeException` instead of crashing
3. **P/Invoke fix** - Updated `swift_getExistentialTypeMetadata` to use Swift calling convention (`CallConvSwift`)

**Workaround implemented** (Phase 29):
- **Swift wrapper functions** in `SwiftBindings.swift` that create `ImageRequest` on the Swift side
- **C# factory class** `ImageRequestFactory` with `FromUrlString()`, `FromUrl()`, `FromUrlRequest()` methods
- Completely avoids the Mono JIT crash because Swift handles the existential array creation

**Impact**: The 3 blocked `ImageRequest` constructors can now be accessed via `ImageRequestFactory`:
```csharp
// Instead of: new ImageRequest(url, processors: [])
var request = ImageRequestFactory.FromUrlString("https://example.com/image.jpg");
```

**Long-term fix**: Report bug to dotnet/runtime - the Mono JIT incorrectly marks `swift_getExistentialTypeMetadata` as async

### Simple Swift Enum Case Support
**Status**: COMPLETED (Phase 18 + Phase 19 + Phase 20)

Simple Swift enum cases can now be constructed from C# via two mechanisms:

**RawRepresentable enums (Phase 18)**:
- ✅ Parser extracts `enumRawTypeName` from ABI JSON
- ✅ `EnumDecl.IsRawRepresentable` detection works
- ✅ Frozen RawRepresentable enums emit `FromRawValue()` with direct IntPtr return
- ✅ **Non-frozen RawRepresentable enums** emit `FromRawValue()` with indirect return handling
- ✅ Static case properties (`VeryLow`, `Low`, `Normal`, `High`, `VeryHigh`) work for all RawRepresentable enums

**Non-RawRepresentable enums (Phase 19)**:
- ✅ Simple cases emit via direct P/Invoke to case constructor
- ✅ Uses indirect return pattern (SwiftIndirectResult)
- ✅ Works for any enum, regardless of RawRepresentable conformance

**Enum value extraction (Phase 20)**:
- ✅ `CaseTag` nested enum for type-safe case discrimination
- ✅ `Tag` property using `ValueWitnessTable->GetEnumTag()`
- ✅ `TryGet` methods for non-destructive associated value extraction

**Working APIs**:
- ✅ `ImageRequest.Priority.*` cases (VeryLow, Low, Normal, High, VeryHigh)
- ✅ `ImageTask.State.*` cases (Running, Cancelled, Completed)
- ✅ Simple cases on non-RawRepresentable enums (e.g., `ImagePipeline.Error.DataIsEmpty`)
- ✅ `error.Tag` and `error.TryGetDataLoadingFailed(out var value)` extraction

**Still limited**:
- Enum cases with 8+ element tuples (requires ValueTuple nesting)
- Enum cases with nested tuples as associated values
- Enum cases with associated values containing closures or nested enums

**Technical solution**: Phase 18 implemented indirect return handling for non-frozen failable initializers:
1. Allocate buffer for `SwiftOptional<EnumType>`
2. Pass buffer via `SwiftIndirectResult` parameter
3. Call P/Invoke (returns void, writes to buffer)
4. Use `GetEnumTag()` to check Some (tag 0) vs None (tag 1)
5. Extract payload with `InitializeWithCopy()` when Some

Note: `ImageRequest.Options.*` (OptionSet) cases also work correctly using getter symbols (`vgZ` suffix).

### Swift Wrapper Error Propagation
**Status**: FIXED (Phase 16.3)

Swift async method errors are now properly caught and propagated to C# as `SwiftException`.

**Implementation**:
- Swift wrappers now use `do { try await ... } catch { ... }` instead of `try!`
- Error callback parameter added to all async wrapper functions
- `SwiftException` class created in `Swift.Runtime` for Swift errors
- Errors are marshalled via `String(describing: error)` and `TrySetException()`

**Usage**:
```csharp
try
{
    var image = await pipeline.Image(request);
}
catch (SwiftException ex)
{
    // ex.Message contains the Swift error description
    Console.WriteLine($"Swift error: {ex.Message}");
}
```

---

## Success Criteria

The binding is "complete" when we can:

```csharp
// Create a pipeline
var pipeline = ImagePipeline.Shared;

// Create a request
var url = new URL("https://example.com/image.jpg");
var request = new ImageRequest(url);

// Load an image (async)
var response = await pipeline.Image(request);
var image = response.Image; // UIImage
```

**Checklist**:
- [x] Basic type generation
- [x] Property getters/setters
- [x] Library path configuration (`-l` flag)
- [x] iOS build infrastructure
- [x] Fix code generation bugs (Phase 1.4, 1.5)
- [x] Optional<T> handling
- [x] Foundation type wrappers
- [x] Fix naming collision bugs
- [x] Fix SwiftOptional<T> PayloadBuffer
- [x] URL type support
- [x] NSImage/UIImage support
- [x] Fix enum type registration
- [x] Proper ISwiftObject implementation
- [x] Property setters
- [x] URLRequest/URLResponse support
- [x] Enum case constructors (simple cases work, cases with associated values emit static methods)
- [x] **Async method support** - FIXED: Uses IntPtr + proper Swift copy semantics
- [x] **ObjC type remapping** - ObjC classes (UIImage, URLResponse, OperationQueue) map to .NET iOS types for both params and returns
- [x] **Existential type handling** - Generator handles `any Protocol` without crashing
- [x] **Swift wrapper imports** - Generator now emits imports automatically
- [x] **Async non-frozen parameter cleanup** - FIXED: Cleanup runs after callback, not in defer
- [x] **ObjC/Swift type strategy complete** - ObjC classes (UIImage, etc.) map to .NET iOS types; Swift structs (URL, Data) now also use native types (NSUrl, NSData) in public signatures
- [x] **Native type remapping** - `Swift.URL` → `Foundation.NSUrl` and `Swift.Data` → `Foundation.NSData` in public method signatures (Phase 26)
- [x] **Existential parameters** - Methods with `any Protocol` parameters now generate valid `ExistentialContainer{N}` types
- [x] **Closure bound generic parameters** - Closures accepting `Result<T,E>`, `Array<T>`, `Optional<T>`, etc. now generate valid C# code
- [x] **Closure bound generic returns** - Closures returning `SwiftOptional<T>`, `SwiftResult<S,E>`, etc. now use indirect return marshalling
- [x] **Throwing closures** - Non-async throwing closures map to `Func<..., SwiftResult<T, SwiftError>>`
- [x] **Optional closure properties** - Properties with optional closure types now emit with nullable delegate types (`Action?`, `Func<...>?`)
- [x] **Existential properties** - Properties with `any Protocol` types now generate `ExistentialContainer{N}` types
- [x] **Generic constructors** - Constructors with generic parameters now work using same infrastructure as methods
- [x] **Hasher type support** - `hash(into:)` methods now generate correctly
- [x] **UIColor type support** - ObjC-bridged to `UIKit.UIColor`
- [x] **URLSession types support** - URLSession, URLSessionConfiguration, URLCache ObjC-bridged
- [x] Existentials in closures - ClosureHandler now supports `any Protocol` parameters/returns
- [x] Existentials in tuples - TupleHandler now supports `any Protocol` elements
- [x] SwiftResult value extraction - `Success`, `Failure`, `TryGetSuccess`, `TryGetFailure`, `Match`
- [x] Nested type handling in generics - `SwiftTypeName.FromTypeSpec` traverses `InnerType` chain
- [x] Integration tests fixed - Updated naming conventions (PascalCase), 684 tests pass
- [x] Runtime testing of async methods on iOS simulator - VERIFIED
- [x] **Unbound generic methods** - GenericTypeParam parsing, where clause emission, unknown protocol handling
- [x] **Dictionary with existential values** - `Dictionary<K, Any>` now generates `SwiftDictionary<K, ExistentialContainer0>`
- [x] **Closure callback signature fix** - Function pointer and callback signatures now consistently use blittable types
- [x] **ExistentialContainerFactory** - Helper methods for creating existential containers from C# objects
- [x] **Unbound generic type definitions** - Generic types like `Box<T>` now emit as `Box<T0> where T0 : ISwiftObject`
- [x] **Protocol associated types (PATs)** - AssociatedTypeReferenceSpec for `Self.Element`, `DependentMember` parsing
- [x] **Protocol subscript support** - Protocol interfaces now emit C# indexers for Swift subscripts
- [x] **Closure tuple parameters in protocols** - `(Data, URLResponse) -> Void` now emits as `Action<(Data, URLResponse)>`
- [x] **Consistent existential handling** - Both `ProtocolListTypeSpec` and `NamedTypeSpec.IsAny` handled uniformly
- [x] **Protocol Proxy Emitter** - Full C# proxy class generation for Swift protocol implementation
- [x] **EveryProtocol pattern** - Swift side conformance generation with vtable callbacks
- [x] **SwiftObjectRegistry** - Container-to-proxy mapping for Swift callbacks
- [x] **Async instance method singleton detection** - Types with `shared` property automatically use `ClassName.shared.method()` pattern

---

## Future Work (Phase 15+)

### Phase 12 Completed
- ✅ CGSize module aliasing (CoreFoundation → CoreGraphics)
- ✅ TupleHandler support for bound generics (Optional<T>, Array<T>)
- ✅ Async tuple return exclusion (prevents crash, falls back to AnyType)

### Phase 13 Completed
- ✅ Optional closure parameters use `Action<...>?` / `Func<...>?` syntax
- ✅ Module-local types in closures (ImageResponse? now correctly typed)
- ✅ BoundGenericsHandler excludes optional closures (handled by ClosureHandler)
- ✅ AsyncStream property collision detection (renames to `PropertyValue` when colliding with nested types)
- ✅ TypeConversionHandler defers Optional<Closure> to ClosureHandler

### Phase 14 Completed
- ✅ Async tuple return support - Methods like `data(_for:)` now return `Task<(T1, T2)>` instead of `Task<AnyType>`
- ✅ Tuple elements flattened for `@convention(c)` callback compatibility
- ✅ Element-wise marshalling for ObjC types (`GetNSObject`) and Swift types
- ✅ TupleHandler P/Invoke type mapping enhanced (ObjC → IntPtr, non-frozen → Buffer)

### Phase 15 Completed
- ✅ Throwing closures (non-async) - Map to `Func<..., SwiftResult<T, SwiftError>>`
- ✅ `SwiftVoid` unit type for void-returning throwing closures
- ✅ `SwiftResult.FromSuccess()` / `FromFailure()` factory methods
- ✅ `EmitThrowingClosureCallback()` with SwiftError* out parameter handling
- ✅ `EmitThrowingClosureReturnMarshalling()` for receiving throwing closures from Swift
- ✅ Async+throws closures supported (Phase 28) - Uses Swift continuation wrapper pattern with `[UnmanagedCallersOnly]` start callback

### Phase 15.2 Completed
- ✅ Optional closure property emission - PropertyHandler now checks `IsOptionalClosure` and uses `GetCSharpOptionalDelegateType()`
- ✅ `didComplete` property now emits as `Action?` with getter and setter
- Note: The async attribute isn't preserved from ABI JSON `printedName` field - this is a pre-existing parser limitation

### Phase 15.3 Completed
- ✅ Implicit conversion operators added to `Swift.URL` for `Foundation.NSUrl`
- ✅ Implicit conversion operators added to `Swift.Data` for `Foundation.NSData`
- ✅ Bidirectional conversions enable seamless interop: `URL swiftUrl = nsUrl;` and `NSUrl ns = swiftUrl;`
- ✅ Guarded by `#if IOS || MACCATALYST || MACOS` for platform compatibility

### Phase 15.4 Completed - Runtime Validation
**Status**: COMPLETED (2026-01-31)

Comprehensive runtime validation suite created and executed.

**Initial results**: 28 passed, 2 failed, 2 warnings (93% pass rate)
**After Phase 16 fixes**: 29 passed, 0 failed, 3 warnings (100% pass rate)

**Test categories validated**:
| Category | Tests | Result |
|----------|-------|--------|
| Basic Binding | 4/5 | Type metadata, Shared singleton, SwiftString, ImageRequest |
| Async Image Load | 3/3 | Valid URL, sequential loads, UIImage validity |
| Cache Operations | 3/3 | Cache access, cache population, Options types |
| ImageRequest Options | 3/5 | Options access, property access, metadata |
| Error Handling | 2/2 | Invalid URL format handled, SwiftException caught |
| Memory Management | 4/4 | No retain count leaks, allocation cycles, async stability |
| Performance | 4/4 | All operations < 50ms average |
| Protocols | 6/6 | Cancellable, ImageProcessing proxies, registry |

**Core use case verified working**:
```csharp
var pipeline = ImagePipeline.Shared;
var request = new ImageRequest("https://picsum.photos/200/200");
UIImage image = await pipeline.Image(request);  // Works!
```

**Issues discovered and resolved** (see Phase 16):
- ✅ Swift wrapper try! crash → Fixed with error callback (Phase 16.3)
- ✅ SafeHandle finalizer crash → Fixed with explicit dispose tracking (Phase 16.4)
- ✅ ClosureEmitter VWT syntax → Fixed pointer access (Phase 16.5)
- ✅ Class SafeHandle ref counting → Fixed class instance method ref counting (Phase 16.1)

**Documented as limitations** (requires future work):
- Non-frozen struct with existentials crash (Phase 16.1) - Configuration struct still crashes due to existential container fields
- Simple enum cases (Phase 16.2) - requires RawRepresentable support

### 15.5 Closure Return Type Marshalling Fix
**Priority**: Low
**Status**: COMPLETED (see Phase 16.5)

Fixed ClosureEmitter issues:
- Non-frozen struct parameters now use correct VWT pointer access (`->`)
- Existential generic return types marked as unsupported (cannot marshal `void*` to bound generic)

### Note: Async Instance Method Workaround

The singleton pattern detection (Phase 8) handles most async instance methods. For non-singleton classes, `unsafeBitCast` is used which may have edge cases. A proper fix would require .NET runtime changes to support SwiftSelf register with async Task closure capture.

---

## Phase 16: Bug Fixes & Stability

Issues discovered during Phase 15.4 runtime validation testing. Most have been resolved.

**Summary**:
- 16.1 Class SafeHandle ref counting → COMPLETED (non-frozen struct with existentials still crashes)
- 16.2 Simple enum case support → DOCUMENTED AS LIMITATION (requires RawRepresentable)
- 16.3 Swift async error handling → COMPLETED
- 16.4 SafeHandle finalizer crash → COMPLETED
- 16.5 ClosureEmitter VWT syntax → COMPLETED

---

### 16.1 Class SafeHandle Ref Counting Fix
**Priority**: High
**Status**: COMPLETED (fully fixed in Phase 27)

**Problem**: Properties on Swift **classes** that return non-frozen struct types were crashing because `EmitSafeHandleAddRef` and `EmitSafeHandleRelease` only handled `StructDecl`, not `ClassDecl`. Without ref counting, the GC could finalize the SafeHandle during the P/Invoke call, causing SIGSEGV.

**Solution implemented**:
- Added `ClassDecl` handling to `EmitSafeHandleAddRef()` - emits `DangerousAddRef` for class instance methods
- Added `ClassDecl` handling to `EmitSafeHandleRelease()` - emits `DangerousRelease` for class instance methods
- Swift classes always need ref counting since they use `_payload` SafeHandle

**Files modified**:
- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/MethodHandler.cs` - Added ClassDecl cases at lines ~1834 and ~1884

**Verified working**:
- `ImagePipeline.CacheValue` property access now works (previously would crash)
- All 1,363 generator tests pass
- NukeTestApp validation passes (29/29 tests)

**Remaining issue** (fixed in Phase 27):
- `ImagePipeline.ConfigurationValue` was still crashing - root cause was Swift class return value marshalling, not ref counting
- See Phase 27 for the complete fix

### 16.2 Simple Swift Enum Case Support
**Priority**: Medium
**Status**: DOCUMENTED AS LIMITATION

**Problem**: Simple Swift enum cases (without associated values) don't have exported constructor functions.

**Root cause analysis**:
- ABI JSON provides base symbol (e.g., `$s...yA2EmF`)
- TBD file exports with `WC` suffix (e.g., `$s...yA2EmFWC`)
- But `WC` symbols are DATA (`S`), not FUNCTION (`T`) - they are witness table entries
- Swift doesn't export constructor functions for simple enum cases

**Affected APIs**:
- `ImageRequest.Priority.*` cases (VeryLow, Low, Normal, High, VeryHigh)
- `ImagePipeline.Error.DataIsEmpty`

**Solution implemented**:
- Generator now skips simple enum cases with a warning instead of emitting broken P/Invoke
- Test app skips Priority and Error enum case tests with warnings

**Future work**: Implement RawRepresentable protocol to construct enum values from raw integers.

**Files modified**:
- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/TypeHandler.cs` - Skip simple enum cases with warning

### 16.3 Swift Wrapper Error Handling (try! crash)
**Priority**: Medium
**Status**: COMPLETED

**Problem**: The generated Swift wrapper uses `try!` for async method calls, which causes a fatal error when the underlying operation fails instead of propagating the error to C#.

**Solution implemented**:
1. Added `errorCallback` parameter to all Swift async wrapper functions
2. Changed `try!` to `do { try await ... } catch { ... }` in all Swift wrappers
3. Error message marshalled via `String(describing: error).withCString { errorCallback($0, task) }`
4. Created `SwiftException` class in `Swift.Runtime` namespace
5. C# error callback calls `TaskCompletionSource.TrySetException(new SwiftException(message))`

**Files modified**:
- `src/Swift.Bindings/src/Marshaler/NameProvider.cs` - Added error callback naming methods
- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/MethodHandler.cs` - Updated Swift wrapper generation
- `src/Swift.Runtime/src/Swift/Runtime/SwiftException.cs` - New exception class

**Verified working**: Error handling test passes - `SwiftException` caught with Swift error message

### 16.4 SafeHandle Finalizer Crash During GC
**Priority**: Low
**Status**: COMPLETED

**Problem**: After test completion, during GC finalization, `SwiftSafeHandle.ReleaseHandle` crashed with SIGSEGV when trying to release Swift objects.

**Root cause**: During finalization (when `Dispose()` wasn't explicitly called), calling Swift's `Destroy` could crash because the Swift runtime may be shutting down or the object was already released. C# try-catch cannot catch native SIGSEGV.

**Solution implemented**:
- Added `_explicitDispose` flag to track if `Dispose()` was called explicitly
- `ReleaseHandle()` only calls Swift's `Destroy` during explicit disposal
- During GC finalization, only frees the .NET-allocated buffer (skips Swift Destroy)
- This is safe because: if `Dispose()` wasn't called, Swift ARC still owns the object

**Files modified**:
- `src/Swift.Runtime/src/Swift/Runtime/SwiftHandle.cs` - Added explicit dispose tracking

**Verification**: NukeTestApp now completes without SIGSEGV crash after "TEST SUCCESS"

### 16.5 ClosureEmitter Non-Frozen Struct Parameter Bug
**Priority**: Low
**Status**: COMPLETED

**Problem**: Closure getters with non-frozen struct parameters generated invalid code:
- Used `((ISwiftObject)_arg0).Payload` but `ISwiftObject` doesn't define `Payload`
- Called `ValueWitnessTable.InitializeWithCopy()` / `Destroy()` with wrong syntax

**Solution implemented**:
- Fixed `InitializeWithCopy` to use `->` pointer access and proper parameter marshalling
- Fixed `Destroy` to use `->` and include required metadata parameter
- Marked closure return types with existential generic parameters as unsupported

**Files modified**:
- `src/Swift.Bindings/src/Emitter/StringEmitter/ClosureEmitter.cs` - Fixed VWT pointer access
- `src/Swift.Bindings/src/Marshaler/ClosureHandler.cs` - Exclude existential generic returns

**Remaining limitation**: Closure return types with existential containers in generic parameters (e.g., `SwiftOptional<ExistentialContainer1>`) are marked unsupported because the emitter cannot marshal `void*` back to the bound generic type.

---

## Phase 17: RawRepresentable Enum Support (Partial)

Infrastructure for RawRepresentable enum support - parsing raw value types from ABI JSON.

**Summary**:
- 17.1 Parser Enhancement → COMPLETED (parse `enumRawTypeName` from ABI JSON)
- 17.2 Model Update → COMPLETED (add `RawValueTypeName` and `IsRawRepresentable` to EnumDecl)
- 17.3 Emitter Support → PARTIAL (frozen enums only - non-frozen requires indirect return handling)

---

### 17.1 Parser Enhancement
**Status**: COMPLETED

**Changes**:
- Added `EnumRawTypeName` field to `Node` record in `SwiftABIParser.cs`
- Parser now extracts `enumRawTypeName` from ABI JSON nodes

**Files modified**:
- `src/Swift.Bindings/src/Parser/SwiftABIParser.cs` - Added EnumRawTypeName to Node, pass to EnumDecl

### 17.2 Model Update
**Status**: COMPLETED

**Changes**:
- Added `RawValueTypeName` property to `EnumDecl` - stores Swift raw type (e.g., "Int", "String")
- Added `IsRawRepresentable` computed property - true when RawValueTypeName is set

**Files modified**:
- `src/Swift.Bindings/src/Model/TypeDecl/EnumDecl.cs` - Added RawValueTypeName and IsRawRepresentable
- `src/Swift.Bindings/tests/UnitTests/ParserTests/EnumParserTests.cs` - Added 5 unit tests

### 17.3 Emitter Support for RawRepresentable
**Status**: PARTIAL - Frozen enums only

**Problem discovered**: Swift failable initializers (`init?(rawValue:)`) for non-frozen enums use indirect return semantics. The return type `Optional<Self>` requires:
1. Pre-allocating space for the optional result
2. Passing `SwiftIndirectResult` parameter
3. Marshaling the optional to check for nil

This is different from regular allocating initializers (`tcfC`) that can return pointers directly.

**Current behavior**:
- **Frozen RawRepresentable enums**: Would emit `FromRawValue()` and static case properties (no Nuke enums are frozen)
- **Non-frozen RawRepresentable enums**: Skip with warning about requiring indirect return handling

**Files modified**:
- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/TypeHandler.cs`:
  - Added `EmitRawRepresentableSupport()` method
  - Detects RawRepresentable conformance
  - Skips non-frozen enums with descriptive warning
  - Maps Swift raw types to C# types (Int→long, String→string, etc.)

**Example warning**:
```
Enum 'Priority' is RawRepresentable but non-frozen. Failable initializers (init?(rawValue:))
for non-frozen enums require indirect return handling that isn't yet implemented.
```

---

## Phase 18: Full Non-Frozen RawRepresentable Enum Support

**Status**: COMPLETED (2026-01-31)

Full support for non-frozen RawRepresentable enums using indirect return handling for failable initializers.

### Summary

- 18.1 Indirect Return Handling → COMPLETED (allocate buffer, pass via SwiftIndirectResult, check enum tag)
- 18.2 FromRawValue Implementation → COMPLETED (both frozen and non-frozen code paths)
- 18.3 Runtime Validation → COMPLETED (Priority enum cases work in NukeTestApp)

### Implementation Details

**Problem**: Swift's `init?(rawValue:)` for non-frozen enums returns `Optional<Self>` via indirect return semantics.

**Solution implemented**:
1. Get metadata for enum type and `SwiftOptional<EnumType>`
2. Allocate buffer for optional result using `NativeMemory.AllocZeroed()`
3. Call P/Invoke with `SwiftIndirectResult` parameter (void return)
4. Check enum tag: `GetEnumTag()` returns 0 for Some, 1 for None
5. If Some, extract payload with `InitializeWithCopy()` and create enum instance
6. Proper cleanup with `Destroy()` and `NativeMemory.Free()`

**Files modified**:
- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/TypeHandler.cs` - Refactored `EmitRawRepresentableSupport()` with two code paths:
  - Frozen enums: P/Invoke returns `IntPtr` directly
  - Non-frozen enums: P/Invoke uses `SwiftIndirectResult`, checks `GetEnumTag()` for Some/None

**P/Invoke signatures**:
```csharp
// Frozen enum (existing)
private static extern IntPtr PInvoke_InitWithRawValue(long rawValue);

// Non-frozen enum (new)
private static extern void PInvoke_InitWithRawValue(SwiftIndirectResult result, long rawValue);
```

### Verification

- All 1,368 generator tests pass (605 unit + 691 integration + 72 runtime)
- NukeTestApp validation: 30 passed, 0 failed, 2 warnings
- `ImageRequest.Priority.High` can now be constructed from C#
- Invalid raw values correctly return `null`

### Now Working

| Enum | Cases |
|------|-------|
| `ImageRequest.Priority` | VeryLow, Low, Normal, High, VeryHigh |
| `ImageTask.State` | Running, Cancelled, Completed |

---

## Phase 19: Enum Associated Values Support

**Status**: COMPLETED (2026-01-31)

Support for enum cases with associated values, including existential types like `any Swift.Error`.

### Summary

- 19.1 Existential Type Handling → COMPLETED (map `any Protocol` to `ExistentialContainer1`)
- 19.2 Simple Case Direct P/Invoke → COMPLETED (emit without RawRepresentable requirement)
- 19.3 TypeSpecParser Fix → COMPLETED (parse labeled existential types correctly)
- 19.4 Unit Tests → COMPLETED (2 new tests for existential parsing)

### Problem

Enum cases with associated values in `ImagePipeline.Error` were broken or skipped:

1. **Simple cases skipped** - Cases like `dataMissingInCache`, `dataIsEmpty` weren't emitted because `ImagePipeline.Error` is NOT RawRepresentable
2. **Existential types → AnyType** - Cases like `dataLoadingFailed(error: Swift.Error)` generated broken `Swift.AnyType` parameters instead of `ExistentialContainer1`
3. **TypeSpec parsing issue** - Labeled existentials like `(error: any Swift.Error)` weren't parsed correctly

### Implementation Details

#### 19.1 Existential Type Handling in Enum Associated Values

**Problem**: `any Swift.Error` was mapping to `Swift.AnyType` instead of `ExistentialContainer1`.

**Solution**:
- Modified `GetCSharpTypeNameForEnumCase()` to detect existential types using `ExistentialHandler`
- Returns `Swift.Runtime.ExistentialContainer1` for single-protocol existentials
- Updated `GetPInvokeType()` and `GetPInvokeArgument()` to handle existentials correctly

**Files modified**:
- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/TypeHandler.cs`

#### 19.2 Simple Case Direct P/Invoke

**Problem**: Simple enum cases were skipped with a warning if the enum wasn't RawRepresentable.

**Solution**:
- Added `EmitSimpleCaseDirectPInvoke()` method that generates P/Invoke calls directly to Swift case constructors
- Uses indirect return pattern: allocate buffer, pass via `SwiftIndirectResult`, create enum instance from result

**Code pattern**:
```csharp
public static EnumType CaseName
{
    get
    {
        var metadata = PInvoke_getMetadata();
        IntPtr buffer = (IntPtr)NativeMemory.Alloc(metadata.Size);
        var indirectResult = new SwiftIndirectResult((void*)buffer);
        PInvoke_CaseName(indirectResult);
        return new EnumType(buffer, metadata);
    }
}
```

**Files modified**:
- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/TypeHandler.cs`

#### 19.3 TypeSpecParser Fix for Labeled Existentials

**Problem**: `(error: any Swift.Error)` was being misparsed because the parser checked for `any` keyword BEFORE `TypeLabel`.

**Root cause**: In the input `error: any Swift.Error`, the tokenizer produces:
- `TypeLabel`: "error"
- `TypeName`: "any"
- `TypeName`: "Swift.Error"

The parser was checking for `any` first, consuming "error" as a type name instead of a label.

**Solution**:
1. Reordered prefix checks: TypeLabel checked BEFORE `any` keyword
2. Fixed tuple unwrapping to preserve inner type's `IsAny` and `TypeLabel` properties:
   ```csharp
   typeLabel = type.TypeLabel ?? typeLabel;
   isAny = type.IsAny || isAny;
   inout = type.IsInOut || inout;
   type.TypeLabel = null;
   ```

**Files modified**:
- `src/Swift.Bindings/src/Model/TypeSpecParsing/TypeSpecParser.cs`

#### 19.4 Unit Tests

Added two tests for existential type parsing:

```csharp
[Fact]
public static void TestAnySwiftError()
{
    var ts = TypeSpecParser.Parse("any Swift.Error");
    var ns = ts as NamedTypeSpec;
    Assert.NotNull(ns);
    Assert.Equal("Swift.Error", ns.Name);
    Assert.True(ns.IsAny);
}

[Fact]
public static void TestLabeledTupleWithExistential()
{
    var ts = TypeSpecParser.Parse("(error: any Swift.Error)");
    var ns = ts as NamedTypeSpec;
    Assert.NotNull(ns);
    Assert.Equal("Swift.Error", ns.Name);
    Assert.True(ns.IsAny);
    Assert.Equal("error", ns.TypeLabel);
}
```

**Files modified**:
- `src/Swift.Bindings/tests/UnitTests/TypeSpecTests/TypeSpecParserTests.cs`

### Verification

- All 1,370 generator tests pass (607 unit + 691 integration + 72 runtime)
- Nuke bindings regenerate without `Swift.AnyType` in enum case signatures
- Simple cases like `DataIsEmpty` now appear in generated code

### Generated Code Example

Before (broken):
```csharp
public static Error DataLoadingFailed((Swift.AnyType error, Swift.AnyType) value0)
```

After (fixed):
```csharp
public static Error DataLoadingFailed(Swift.Runtime.ExistentialContainer1 error)
```

---

## Phase 20: Enum Associated Value Extraction

**Status**: COMPLETED (2026-01-31)

Support for extracting associated values from existing Swift enum instances. This complements Phase 19 which added support for *creating* enum instances with associated values.

### Summary

- 20.1 Model Enhancement → COMPLETED (GetCaseTag(), PayloadCases, NoPayloadCases helpers)
- 20.2 CaseTag Enum Generation → COMPLETED (nested enum for type-safe case discrimination)
- 20.3 Tag Property Generation → COMPLETED (ValueWitnessTable->GetEnumTag())
- 20.4 TryGet Method Generation → COMPLETED (non-destructive payload extraction)
- 20.5 Unit Tests → COMPLETED (12 new tests for tag calculation)

### Problem

After creating enum instances with associated values (Phase 19), there was no way to:
1. Determine which case an enum instance represents
2. Extract the associated values from the enum

### Implementation Details

#### 20.1 Model Enhancement

Added helper methods to `EnumDecl` for Swift's tag calculation based on the Swift ABI layout rules (payload cases first, then no-payload cases):

```csharp
public IEnumerable<EnumCaseDecl> PayloadCases => Cases.Where(c => c.HasAssociatedValues);
public IEnumerable<EnumCaseDecl> NoPayloadCases => Cases.Where(c => !c.HasAssociatedValues);

public int GetCaseTag(EnumCaseDecl caseDecl)
{
    var payloadList = PayloadCases.ToList();
    int payloadIndex = payloadList.IndexOf(caseDecl);
    if (payloadIndex >= 0) return payloadIndex;

    var noPayloadList = NoPayloadCases.ToList();
    int noPayloadIndex = noPayloadList.IndexOf(caseDecl);
    return payloadList.Count + noPayloadIndex;
}
```

**Files modified**:
- `src/Swift.Bindings/src/Model/TypeDecl/EnumDecl.cs`

#### 20.2 CaseTag Enum Generation

Generates a nested `CaseTag` enum for type-safe case discrimination:

```csharp
public enum CaseTag : uint
{
    DataLoadingFailed = 0,    // payload case
    DecoderNotRegistered = 1, // payload case
    DataMissingInCache = 2,   // no-payload case
    DataIsEmpty = 3,          // no-payload case
}
```

Tags are ordered according to Swift's ABI: payload cases first (in declaration order), then no-payload cases (in declaration order).

**Files modified**:
- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/TypeHandler.cs` - Added `EmitCaseTagEnum()`

#### 20.3 Tag Property Generation

Generates a `Tag` property using `ValueWitnessTable->GetEnumTag()`:

```csharp
public unsafe CaseTag Tag
{
    get
    {
        var metadata = PInvoke_getMetadata();
        byte* payload = (byte*)_payload.DangerousGetHandle();
        return (CaseTag)metadata.ValueWitnessTable->GetEnumTag(payload, metadata);
    }
}
```

**Files modified**:
- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/TypeHandler.cs` - Added `EmitTagProperty()`

#### 20.4 TryGet Method Generation

Generates `TryGet` methods for each case with associated values, using non-destructive extraction:

```csharp
public unsafe bool TryGetDataLoadingFailed([MaybeNullWhen(false)] out ExistentialContainer1 error)
{
    if (Tag != CaseTag.DataLoadingFailed)
    {
        error = default;
        return false;
    }

    var metadata = PInvoke_getMetadata();

    // Non-destructive: copy enum first
    byte* enumCopy = stackalloc byte[(int)metadata.Size];
    metadata.ValueWitnessTable->InitializeWithCopy(enumCopy, (void*)_payload.DangerousGetHandle(), metadata);

    // Strip tag, leaving payload
    metadata.ValueWitnessTable->DestructiveProjectEnumData(enumCopy, metadata);

    // Marshal payload to C# type
    error = SwiftMarshal.MarshalFromSwift<ExistentialContainer1>(new IntPtr(enumCopy));
    return true;
}
```

**Key design decisions**:
- **Non-destructive**: Copies enum before stripping tag to preserve original value
- **Type-safe out parameter**: Uses `[MaybeNullWhen(false)]` for proper nullability analysis
- **Bound generic handling**: Properly resolves types like `SwiftResult<T, E>` to full C# names

**Files modified**:
- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/TypeHandler.cs` - Added `EmitTryGetMethod()`, `EmitPayloadMarshal()`
- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/ModuleHandler.cs` - Added `System.Diagnostics.CodeAnalysis` using directive

#### 20.5 Unit Tests

Added 12 tests for tag calculation and helper properties:

```csharp
[Fact]
public void GetCaseTag_PayloadCaseFirst_ReturnsZero()

[Fact]
public void GetCaseTag_NoPayloadCaseAfterPayload_ReturnsPayloadCount()

[Fact]
public void GetCaseTag_MultiplePayloadCases_ReturnsDeclarationOrder()

[Fact]
public void TagOrdering_MatchesSwiftConvention()
// etc.
```

**Files created**:
- `src/Swift.Bindings/tests/UnitTests/EmitterTests/EnumExtractionTests.cs`

### Limitations

- **8+ element tuples**: Tuples with more than 7 elements require ValueTuple nesting (not yet implemented).
- **Nested tuples**: Tuples containing tuples as elements are not yet supported.
- **Nested closures/enums**: Complex associated value types are not yet supported.

### Verification

- All 1,382 generator tests pass (619 unit + 691 integration + 72 runtime)
- NukeTestApp validation: 30 passed, 0 failed, 2 warnings
- Generated bindings include `CaseTag` enum, `Tag` property, and `TryGet` methods

### Generated Code Example

```csharp
// CaseTag enum
public enum CaseTag : uint
{
    DataLoadingFailed = 0,
    DecoderNotRegistered = 1,
    DataMissingInCache = 2,
    DataIsEmpty = 3,
}

// Tag property
public unsafe CaseTag Tag { get { ... } }

// TryGet method
public unsafe bool TryGetDataLoadingFailed([MaybeNullWhen(false)] out ExistentialContainer1 error) { ... }
```

### Usage Example

```csharp
var error = ImagePipeline.Error.DataLoadingFailed(someExistentialError);

// Check which case
if (error.Tag == ImagePipeline.Error.CaseTag.DataLoadingFailed)
{
    // Extract value
    if (error.TryGetDataLoadingFailed(out var extractedError))
    {
        // Use extractedError
    }
}
```

---

## Related Issues

- [#2875 - Existential Containers](https://github.com/dotnet/runtimelab/issues/2875) - Parameters, properties, bound generic arguments, ExistentialContainerFactory implemented
- [#2996 - Async Properties](https://github.com/dotnet/runtimelab/issues/2996)
- [#2873 - Tuple Support](https://github.com/dotnet/runtimelab/issues/2873) - Implemented
- [#2874 - Closure Support](https://github.com/dotnet/runtimelab/issues/2874) - Implemented; Bound generic params & returns added
- [#2890 - Generic Constructors](https://github.com/dotnet/runtimelab/issues/2890) - Implemented
- Generic Methods - Implemented: GenericTypeParam parsing, where clause emission
- Generic Type Definitions - Implemented: Unbound generic types like `Box<T>` now emit correctly
- Protocol Associated Types (PATs) - Implemented: AssociatedTypeReferenceSpec, DependentMember parsing, generic proxy classes, typealias emission
- Protocol Interface Emission - Implemented: Subscripts, closures with tuples, existential consistency
- Protocol Conformance from C# - Implemented: EveryProtocol pattern, ProtocolProxyEmitter, SwiftObjectRegistry, witness table export

---

## Commands Reference

```bash
# Install iOS workload
sudo dotnet workload install ios

# Generate iOS bindings (works on macOS host)
dotnet run --project src/Swift.Bindings/src -c Release -- \
  -a BindingTesting/Nuke/output/Nuke-sim.abi.json \
  -d BindingTesting/Nuke/Nuke.xcframework/ios-arm64_x86_64-simulator/Nuke.framework/Nuke \
  -t BindingTesting/Nuke/output/Nuke-sim.tbd \
  -l "Nuke" \
  -o BindingTesting/Nuke/output-ios/

# Build and run test app
dotnet build BindingTesting/Nuke/NukeTestApp -c Debug -t:Run

# Run all tests (unit, integration, runtime)
./run-tests.sh
```

For detailed testing workflows and environment setup, see [Phase 5: Testing & Validation](CompletedPhases/phase-5-testing-validation.md).

---

## Test Results Summary

### Generator Tests
| Category | Count | Status |
|----------|-------|--------|
| Unit tests | 935 | All passing |
| Integration tests | 691 | 678 passed, 13 skipped, 0 failed |
| Runtime tests | 94 | 93 passed, 1 skipped |

**Unit tests**: All 935 passing (includes 32 new TypeSpecHelpers tests in Phase 31).

**Integration tests**: Phase 23 restored all tests to a passing or appropriately-skipped state. The 15 skipped tests are due to known limitations:
- 3 primitive generic tests (primitives don't implement ISwiftObject)
- 6 protocol conformance tests (C# structs don't implement protocol interfaces)
- 6 async tests (Swift concurrency executor doesn't run from C#)

### NukeTestApp Validation (Phase 29)
| Category | Passed | Failed | Warnings |
|----------|--------|--------|----------|
| Basic Binding | 5 | 0 | 0 |
| Async Image Load | 3 | 0 | 0 |
| Cache Operations | 3 | 0 | 0 |
| ImageRequest Options | 4 | 0 | 0 |
| Error Handling | 2 | 0 | 1 |
| Memory Management | 4 | 0 | 0 |
| Performance | 4 | 0 | 0 |
| Protocols | 6 | 0 | 0 |
| Async Closures | 2 | 0 | 2 |
| ImageRequest Wrappers | 1 | 0 | 0 |
| **Total** | **34** | **0** | **3** |

**Pass rate**: 100% (34/34 tests passing)

**Core functionality verified**:
- Async image loading from network URLs
- UIImage return type marshalling
- Memory management (no retain count leaks)
- Protocol proxy implementations
- Cache access and population
- Swift async error handling (SwiftException)
- **Priority enum cases** (VeryLow, Low, Normal, High, VeryHigh) - NEW in Phase 18
- **Async+throwing closure binding** - Constructor exists, `Func<Task<Swift.Data>>` delegate works - NEW in Phase 28

**Skipped with warnings** (documented limitations):
- Enum cases with associated values containing closures or nested enums
- Full `ImageRequest(data:)` invocation blocked by `SwiftArray<ExistentialContainer1>` metadata crash

**Fixed in Phase 16**:
- ✅ Class SafeHandle ref counting for instance methods (Phase 16.1)
- ✅ Swift async errors now propagate as `SwiftException` (Phase 16.3)
- ✅ SafeHandle finalizer crash during GC (Phase 16.4)
- ✅ ClosureEmitter VWT pointer access syntax (Phase 16.5)

**Fixed in Phase 18**:
- ✅ Non-frozen RawRepresentable enum case construction (Priority, State enums)

**Fixed in Phase 19**:
- ✅ Simple enum cases on non-RawRepresentable enums (direct P/Invoke)
- ✅ Existential types in enum associated values (`any Swift.Error` → `ExistentialContainer1`)
- ✅ TypeSpecParser parsing of labeled existentials (`error: any Swift.Error`)

**Fixed in Phase 20**:
- ✅ Enum case discrimination via `CaseTag` enum and `Tag` property
- ✅ Associated value extraction via `TryGet` methods (non-destructive)
- ✅ Proper bound generic type resolution in payload marshalling

**Fixed in Phase 21**:
- ✅ TypeConversionHandler crash with generic type parameters (`τ_0_0`)
- ✅ EveryProtocolEmitter closure type rendering in Swift wrapper
- ✅ EveryProtocolEmitter existential metatype syntax (`(any Protocol).self`)

**Fixed in Phase 22**:
- ✅ Generic protocol proxy compilation errors (CS8895, CS7042, CS0305)
- ✅ Protocols with associated types now skip proxy generation gracefully
- ✅ Generic constraints skip protocols with associated types (can't use without type arguments)
- ✅ Protocol conformance dictionaries skip generic protocol interfaces

**Fixed in Phase 24**:
- ✅ Tuple associated values in enum TryGet methods (multi-element extraction)
- ✅ TupleTypeMetadata struct for accessing Swift tuple element offsets at runtime
- ✅ Multiple out parameters generated for each tuple element

**Fixed in Phase 25**:
- ✅ Async DllImport regression fixed via configurable `--async-library` option
- ✅ All async image loading tests now pass (was 6 failures in Phase 24)

**Fixed in Phase 26**:
- ✅ Native type remapping for Foundation.URL → Foundation.NSUrl in public method signatures
- ✅ Native type remapping for Foundation.Data → Foundation.NSData in public method signatures
- ✅ Automatic conversion at marshalling layer (no developer effort required)

**Fixed in Phase 27**:
- ✅ Swift class return value marshalling (classes return pointers in registers, not via indirect result)
- ✅ `ImagePipeline.Shared` now returns valid class pointer
- ✅ `ImagePipeline.ConfigurationValue` now works (non-frozen struct with existential containers)

**Fixed in Phase 28**:
- ✅ Async+throwing closure infrastructure added via Swift continuation wrapper pattern
- ✅ `GetCSharpDelegateType` returns `Task<T>` for async+throwing (error handling via callback, not return type)
- ✅ `AsyncThrowingClosureState<T>` runtime class for holding async delegate state
- ✅ Helper method pattern to work around C# async lambda unsafe context restriction
- ✅ Async+throwing closures with `Foundation.Data` return type now fully supported
- ✅ `ImageRequest.init(data:)` constructor binding generated (emits `Func<Task<Swift.Data>>`)
- ⚠️ Runtime invocation blocked by `SwiftArray<ExistentialContainer1>` metadata crash (workaround in Phase 29)
- ✅ NukeTestApp "Async Closures" test section added (2 passed, 1 warning)

**Fixed in Phase 29**:
- ✅ `ImageRequestFactory` class provides workaround for `SwiftArray<ExistentialContainer1>` crash
- ✅ `FromUrlString()` successfully creates `ImageRequest` and loads images
- ✅ Swift wrapper functions handle existential array creation natively
- ⚠️ `FromUrl(URL)` has known limitation with `URL.AbsoluteString` non-blittable P/Invoke

---

## Phase 25: Async DllImport Fix

**Status**: COMPLETED (2026-02-01)

Fixed async method regression caused by .NET's DllImport validation for async P/Invoke calls.

### Summary

- 25.1 Root Cause Analysis → COMPLETED (DllImport async library mismatch)
- 25.2 Configurable --async-library Option → COMPLETED (CLI flag for async library name)
- 25.3 Runtime Validation → COMPLETED (100% pass rate restored)

### Problem

Phase 24's NukeTestApp validation showed 6 failures, all related to `ImagePipeline.image(for:)` async methods. The issue was that .NET's async P/Invoke requires a matching `[DllImport]` library name, but the generated Swift wrapper library had a different name than what was being specified.

### Solution

Added configurable `--async-library` (`-al`) command-line option to the binding generator. This allows specifying the correct library name for async wrapper P/Invoke declarations, which may differ from the main Swift library.

**Usage**:
```bash
SwiftBindings -a abi.json -d lib.dylib -t lib.tbd -l "MainLib" -al "MainLibAsyncWrapper" -o output/
```

### Files Modified

- `src/Swift.Bindings/src/Program.cs` - Added `--async-library` CLI option
- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/MethodHandler.cs` - Use async library for wrapper P/Invoke

### Verification

- All 30 NukeTestApp tests pass (100% pass rate)
- Async image loading from network URLs works correctly
- No regressions in other test categories

---

## Phase 24: Tuple Associated Values in Enum TryGet

**Status**: COMPLETED (2026-02-01)

Support for extracting tuple associated values from Swift enum cases. Enum cases like `case decodingFailed(decoder: any ImageDecoding, context: ImageDecodingContext, error: any Error)` now generate TryGet methods with multiple out parameters.

### Summary

- 24.1 Runtime TupleTypeMetadata → COMPLETED (struct for accessing tuple element offsets)
- 24.2 EmitTryGetMethodForTuple → COMPLETED (generates multiple out parameters)
- 24.3 Tuple Metadata Accessor → COMPLETED (cached accessor per enum case)
- 24.4 Unit Tests → COMPLETED (10 new tests for tuple associated values)

### Problem

Phase 20 added `TryGet` methods for enum cases with associated values, but skipped cases where the associated value was a tuple (multi-element). Swift represents `case example(a: Int, b: String)` as a single `TupleTypeSpec` containing multiple elements.

**Before (skipped with warning)**:
```
Enum case 'Error.decodingFailed' has tuple associated value with 3 elements. TryGet for tuple extraction not yet supported.
```

### Implementation Details

#### 24.1 Runtime TupleTypeMetadata

Added `TupleTypeMetadata` struct to `TypeMetadata.cs` that provides access to Swift's tuple metadata layout:

```csharp
public unsafe struct TupleTypeMetadata
{
    public nuint Kind;
    public nuint NumElements;
    public IntPtr Labels;

    public nuint GetElementOffset(int index) { ... }
    public IntPtr GetElementTypeHandle(int index) { ... }
    public ValueWitnessTable* ValueWitnessTable { get; }
    public nuint Size { get; }
}
```

Also added helper methods to `TypeMetadata`:
- `AsTupleMetadata()` - Converts TypeMetadata to TupleTypeMetadata pointer
- `GetTupleTypeMetadataFromElements()` - Creates tuple metadata from element types

**Files modified**:
- `src/Swift.Runtime/src/Swift/Runtime/TypeMetadata.cs`

#### 24.2 EmitTryGetMethodForTuple

Replaced the skip logic in `EnumHandler.EmitTryGetMethod()` with `EmitTryGetMethodForTuple()` that:

1. Validates tuple element count (max 7 per C# ValueTuple limit)
2. Generates method with multiple `[MaybeNullWhen(false)] out T` parameters
3. Uses `TupleTypeMetadata` to get element offsets at runtime
4. Marshals each element from its computed offset

**Generated code pattern**:
```csharp
public unsafe bool TryGetDecodingFailed(
    [MaybeNullWhen(false)] out ExistentialContainer1 decoder,
    [MaybeNullWhen(false)] out ImageDecodingContext context,
    [MaybeNullWhen(false)] out ExistentialContainer1 error)
{
    if (Tag != CaseTag.DecodingFailed) { ... return false; }

    // Copy enum, strip tag
    var tupleMetadata = GetTupleMetadata_DecodingFailed();

    var offset0 = tupleMetadata->GetElementOffset(0);
    decoder = SwiftMarshal.MarshalFromSwift<ExistentialContainer1>(new IntPtr(enumCopy + (int)offset0));

    var offset1 = tupleMetadata->GetElementOffset(1);
    context = SwiftMarshal.MarshalFromSwift<ImageDecodingContext>(new IntPtr(enumCopy + (int)offset1));

    var offset2 = tupleMetadata->GetElementOffset(2);
    error = SwiftMarshal.MarshalFromSwift<ExistentialContainer1>(new IntPtr(enumCopy + (int)offset2));

    return true;
}
```

**Files modified**:
- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/EnumHandler.cs`

#### 24.3 Tuple Metadata Accessor

Each enum case with tuple associated values gets a cached tuple metadata accessor:

```csharp
private static TupleTypeMetadata* _tupleMetadata_DecodingFailed;

private static unsafe TupleTypeMetadata* GetTupleMetadata_DecodingFailed()
{
    if (_tupleMetadata_DecodingFailed != null)
        return _tupleMetadata_DecodingFailed;

    var elementMetadataArray = new TypeMetadata[3];
    elementMetadataArray[0] = TypeMetadata.GetExistentialTypeMetadata(1);
    elementMetadataArray[1] = SwiftObjectHelper<ImageDecodingContext>.GetTypeMetadata();
    elementMetadataArray[2] = TypeMetadata.GetExistentialTypeMetadata(1);

    var tupleMetadata = TypeMetadata.GetTupleTypeMetadataFromElements(elementMetadataArray);
    _tupleMetadata_DecodingFailed = tupleMetadata.AsTupleMetadata();
    return _tupleMetadata_DecodingFailed;
}
```

#### 24.4 Unit Tests

Added 10 tests to `EnumHandlerTests.cs`:

- `EnumDecl_CaseWithTupleAssociatedValue_HasOneTupleTypeSpec`
- `EnumDecl_CaseWithTupleAssociatedValue_PreservesLabels`
- `EnumDecl_CaseWithTupleNoLabels_HasNullLabels`
- `EnumDecl_CaseWith7ElementTuple_IsSupported`
- `EnumDecl_CaseWith8ElementTuple_ExceedsMaxSupported`
- `EnumDecl_CaseWithMixedTypes_CollectsAllElementTypes`
- `EnumDecl_MultipleCasesWithDifferentTuples_EachHasOwnStructure`
- `TupleTypeSpec_IsNotEmptyTuple_WhenHasElements`
- `TupleTypeSpec_IsEmptyTuple_WhenNoElements`

**Files modified**:
- `src/Swift.Bindings/tests/UnitTests/EmitterTests/EnumHandlerTests.cs`

### Constraints

- Maximum 7 tuple elements (C# ValueTuple limit without nesting)
- Nested tuples as tuple elements not supported
- Closures as tuple elements not supported

### Verification

- All 1,677 generator tests pass (892 unit + 691 integration + 94 runtime)
- Nuke bindings regenerate with TryGet methods for tuple cases:
  - `TryGetDecodingFailed` (3 out params)
  - `TryGetProcessingFailed` (3 out params)
  - `TryGetFailedToCreateFilter` (2 out params)

### Now Working

| Enum Case | Tuple Elements |
|-----------|----------------|
| `Error.decodingFailed` | `(decoder, context, error)` |
| `Error.processingFailed` | `(processor, context, error)` |
| `CoreImageError.failedToCreateFilter` | `(name, parameters)` |

---

## Phase 26: Native Type Remapping (URL → NSUrl, Data → NSData)

**Status**: COMPLETED (2026-02-01)

Public method signatures now use familiar .NET iOS types (`Foundation.NSUrl`, `Foundation.NSData`) instead of Swift wrapper types (`Swift.URL`, `Swift.Data`). Automatic conversion happens at the marshalling layer.

### Summary

- 26.1 TypeRecord Enhancement → COMPLETED (NativeTypeName property for configurable remapping)
- 26.2 TypeDatabase XML Parsing → COMPLETED (nativeType attribute support)
- 26.3 TypeConversionHandler → COMPLETED (detection and conversion methods)
- 26.4 MethodHandler Integration → COMPLETED (public signatures use native types)
- 26.5 Async Parameter Handling → COMPLETED (proper copy buffer management)

### Problem

Swift types like `Foundation.URL` and `Foundation.Data` are structs that bridge to ObjC classes (`NSUrl`, `NSData`). The binding generator was using Swift wrapper types (`Swift.URL`, `Swift.Data`) in public method signatures, requiring developers to manually convert from .NET iOS types.

**Before**:
```csharp
// Developer must convert NSUrl → Swift.URL
public Task<UIImage> Image(Swift.URL url);
```

**After**:
```csharp
// Developer can pass NSUrl directly
public Task<UIImage> Image(Foundation.NSUrl url);
```

### Implementation Details

#### 26.1 TypeRecord Enhancement

Added `NativeTypeName` property to `TypeRecord` for configurable type remapping:

```csharp
/// <summary>
/// Optional native type name to use in public method signatures.
/// When set, the public API exposes this type (e.g., Foundation.NSUrl) instead of the
/// internal Swift wrapper type (e.g., Swift.URL). Conversion happens at the marshalling layer.
/// </summary>
public CSharpTypeName? NativeTypeName { get; init; }
```

**Files modified**:
- `src/Swift.Bindings/src/TypeDatabase/TypeRecord.cs`

#### 26.2 TypeDatabase XML Parsing

Added `nativeType` attribute parsing in `TypeDatabase.ReadVersion1_0()`:

```xml
<entity managedNameSpace="Swift" managedTypeName="URL">
    <typedeclaration kind="struct" name="URL" module="Foundation"
        mangledName="$s10Foundation3URLV" frozen="false"
        requiresMemoryManagement="true" nativeType="Foundation.NSUrl" />
</entity>
```

**Files modified**:
- `src/Swift.Bindings/src/TypeDatabase/TypeDatabase.cs`
- `src/Swift.Runtime/src/Swift/FoundationDatabase.xml` (configured URL and Data mappings)

#### 26.3 TypeConversionHandler Methods

Added comprehensive native type handling:

```csharp
public bool IsFoundationURL(TypeSpec? typeSpec);
public bool IsFoundationData(TypeSpec? typeSpec);
public bool HasNativeTypeRemapping(TypeSpec? typeSpec);
public string? GetNativeTypeName(TypeSpec? typeSpec);
public string? GetNativeParameterConversion(string paramName, TypeSpec? typeSpec);
public string? GetNativeReturnConversion(string resultVar, TypeSpec? typeSpec);
public string? GetSwiftWrapperTypeForNative(TypeSpec? typeSpec);
```

**Files modified**:
- `src/Swift.Bindings/src/Marshaler/TypeConversionHandler.cs`

#### 26.4 MethodHandler Integration

Updated `WrapperSignatureBuilder` to use native types in public signatures:

- `HandleReturnType()` - Returns `Foundation.NSUrl` / `Foundation.NSData` instead of Swift wrappers
- `HandleArguments()` - Parameters use native types with automatic conversion
- `EmitTypeConversions()` - Generates conversion code: `var urlSwift = Swift.URL.FromNSUrl(url);`
- `EmitReturnMethod()` - Converts Swift result to native type: `return result.ToNSUrl();`

**Files modified**:
- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/MethodHandler.cs`

#### 26.5 Async Parameter Handling

Special handling for async methods with native-remapped parameters:

```csharp
// For async methods, convert native type and copy to buffer for Swift
var urlMetadata = SwiftObjectHelper<Swift.URL>.GetTypeMetadata();
IntPtr urlCopyBuffer = (IntPtr)NativeMemory.Alloc(urlMetadata.Size);
using var urlSwiftTemp = Swift.URL.FromNSUrl(url);
urlMetadata.ValueWitnessTable->InitializeWithCopy(
    (void*)urlCopyBuffer,
    (void*)urlSwiftTemp.Payload.DangerousGetHandle(),
    urlMetadata);
```

### Verification

- All 892 unit tests pass
- All 678 integration tests pass (13 skipped - known limitations)
- All 93 runtime tests pass (1 skipped)
- 30/30 NukeTestApp validation tests pass

### Generated Code Example

**Before**:
```csharp
public static Task<ImageResponse> Image(Swift.URL url)
{
    // ...
}
```

**After**:
```csharp
public static Task<ImageResponse> Image(Foundation.NSUrl url)
{
    var urlSwift = Swift.URL.FromNSUrl(url);
    // ... internal implementation uses Swift.URL ...
}
```

---

## Phase 27: Swift Class Return Value Marshalling Fix

**Status**: COMPLETED (2026-02-01)

Fixed Swift class return value marshalling. Swift classes return pointers directly in registers (x0 on ARM64), not via `SwiftIndirectResult` buffers.

### Summary

- 27.1 Root Cause Analysis → COMPLETED (class types incorrectly using indirect result)
- 27.2 MarshallingHelpers Fix → COMPLETED (return false for class types)
- 27.3 MethodHandler Enhancements → COMPLETED (class return handling)
- 27.4 Runtime Validation → COMPLETED (31/31 tests pass)

### Problem

`ImagePipeline.Shared` was returning a null class pointer, and subsequently accessing `ConfigurationValue` crashed with SIGSEGV in Swift's copy witness.

**Diagnostic output before fix**:
```
[INFO] ImagePipeline.Shared class pointer: 0x0
[WARN] Class pointer is null!
```

**Root cause**: Swift classes return pointers directly in registers, but the generator was treating them like non-frozen structs - using `SwiftIndirectResult` to pass a buffer for the return value. This resulted in the return value never being captured.

### Implementation Details

#### 27.1 MarshallingHelpers Fix

Added check for `TypeRecordKind.Class` in `MethodRequiresIndirectResult()`:

```csharp
TypeRecord typeRecord = env.TypeDatabase.GetTypeRecordOrThrow(returnType.SwiftTypeSpec);

// Swift classes return pointers directly in registers, not via indirect result
if (typeRecord.Kind == TypeRecordKind.Class)
    return false;

if (!IsTypeFrozen(typeRecord)) return true;
```

**Files modified**:
- `src/Swift.Bindings/src/Marshaler/MarshallingHelpers.cs`

#### 27.2 MethodHandler Enhancements

Already in place from earlier work:
- P/Invoke return type set to `IntPtr` for class types
- Wrapper method allocates buffer, stores returned pointer, creates object via `SwiftMarshal.MarshalFromSwift`
- Class self pointer dereferenced correctly for instance methods

**Files modified**:
- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/MethodHandler.cs`

### Generated Code

**Before (broken)**:
```csharp
[DllImport("Nuke", EntryPoint = "$s4Nuke13ImagePipelineC6sharedACvgZ")]
private static extern void PInvoke_shared_Get(SwiftIndirectResult swiftIndirectResult);

private static ImagePipeline Shared_Get()
{
    var payload = NativeMemory.Alloc((nuint)returnMetadata.Size);
    var swiftIndirectResult = new SwiftIndirectResult(payload);
    PInvoke_shared_Get(swiftIndirectResult);  // Return value lost!
    return SwiftMarshal.MarshalFromSwift<ImagePipeline>(new IntPtr(swiftIndirectResult.Value));
}
```

**After (fixed)**:
```csharp
[DllImport("Nuke", EntryPoint = "$s4Nuke13ImagePipelineC6sharedACvgZ")]
private static extern IntPtr PInvoke_shared_Get();  // Returns pointer directly

private static ImagePipeline Shared_Get()
{
    var result = PInvoke_shared_Get();  // Capture return value
    var classPayload = NativeMemory.Alloc((nuint)sizeof(IntPtr));
    *(IntPtr*)classPayload = result;
    return (ImagePipeline)SwiftMarshal.MarshalFromSwift<ImagePipeline>(new IntPtr(classPayload));
}
```

### Verification

**Diagnostic output after fix**:
```
[INFO] ImagePipeline.Shared class pointer: 0x1080403A0
[MEM] ImagePipeline.Shared retain count (correct): 2
[PASS] ImagePipeline.Shared access
[INFO] ConfigurationValue access succeeded!
[PASS] Configuration property access
```

- All 892 unit tests pass
- All 678 integration tests pass (13 skipped - known limitations)
- All 93 runtime tests pass (1 skipped)
- All 31 NukeTestApp validation tests pass (was 30, ConfigurationValue now included)

### Impact

This fix resolves a fundamental issue with Swift class interop:
- All Swift class static properties now return valid pointers
- Instance methods on Swift classes work correctly
- Properties returning non-frozen structs (like `Configuration`) work even when containing existential containers

---

## Phase 29: ExistentialContainer Workaround (ImageRequestFactory)

**Status**: COMPLETED (2026-02-01)

Implemented Swift wrapper functions and C# factory class to bypass the Mono JIT bug with `swift_getExistentialTypeMetadata`. This unblocks 3 of 4 `ImageRequest` constructors that were previously inaccessible.

### Summary

- 29.1 P/Invoke Workaround Research → COMPLETED (consulted Grok/Gemini, tried alternative approaches)
- 29.2 Swift Wrapper Functions → COMPLETED (`SwiftBindings.swift` with `@_silgen_name`)
- 29.3 C# Factory Class → COMPLETED (`ImageRequestFactory` in `Swift.Nuke.Wrappers.cs`)
- 29.4 Runtime Validation → COMPLETED (34 tests pass, 3 warnings)

### Problem

Creating `SwiftArray<ExistentialContainer1>` crashes at runtime with:
```
* Assertion at mono/metadata/jit-info.c:918, condition `!ji->async' not met
```

This blocked the `ImageRequest` constructors that require a `processors` parameter (type `IEnumerable<ExistentialContainer1>`).

### Research Findings

Consulted external AI models (Grok, Gemini) on this issue. Key findings:

1. **Not easily fixable** - This is a known category of Mono JIT bug with Swift Calling Convention
2. **P/Invoke workarounds attempted**:
   - `[SuppressGCTransition]` - Did not resolve
   - `CallingConvention.Cdecl` instead of `CallConvSwift` - Did not resolve
   - `nint` return type - Did not resolve
3. **Swift wrappers are the best approach** because:
   - Avoids Mono JIT entirely for metadata fetch
   - Works on iOS FullAOT (no JIT)
   - Future-proof (NativeAOT ignores JIT bugs)
   - Swift compiler understands existential container layout perfectly

### Implementation Details

#### 29.1 P/Invoke Workarounds (in TypeMetadata.cs)

Added alternative P/Invoke declarations for future experimentation:

```csharp
// Try 1: [SuppressGCTransition]
[SuppressGCTransition]
[UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
[DllImport(KnownLibraries.SwiftCore, EntryPoint = "swift_getExistentialTypeMetadata")]
private static extern TypeMetadata swift_getExistentialTypeMetadata_SuppressGC(...);

// Try 2: Use Cdecl calling convention
[DllImport(KnownLibraries.SwiftCore, EntryPoint = "swift_getExistentialTypeMetadata",
           CallingConvention = CallingConvention.Cdecl)]
private static extern nint swift_getExistentialTypeMetadata_Cdecl(...);

// Try 3: nint return type
[UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
[DllImport(KnownLibraries.SwiftCore, EntryPoint = "swift_getExistentialTypeMetadata")]
private static extern nint swift_getExistentialTypeMetadata_nint(...);
```

None of these workarounds resolved the Mono JIT assertion.

#### 29.2 Swift Wrapper Functions

**File**: `BindingTesting/Nuke/output-ios/SwiftBindings.swift`

```swift
// MARK: - ImageRequest Wrappers (Bypass ExistentialContainer JIT Bug)

/// Creates an ImageRequest from a URL string with default options and no processors.
@_silgen_name("ImageRequest_initWithURLString_simple")
public func imageRequest_initWithURLString_simple(_ urlString: UnsafePointer<CChar>) -> UnsafeMutableRawPointer {
    let urlStr = String(cString: urlString)
    let request = ImageRequest(url: URL(string: urlStr))

    // Allocate and copy the ImageRequest to heap
    let ptr = UnsafeMutablePointer<ImageRequest>.allocate(capacity: 1)
    ptr.initialize(to: request)
    return UnsafeMutableRawPointer(ptr)
}

/// Frees an ImageRequest that was allocated by a wrapper function.
@_silgen_name("ImageRequest_free")
public func imageRequest_free(_ ptr: UnsafeMutableRawPointer) {
    let typedPtr = ptr.assumingMemoryBound(to: ImageRequest.self)
    typedPtr.deinitialize(count: 1)
    typedPtr.deallocate()
}
```

#### 29.3 C# Factory Class

**File**: `BindingTesting/Nuke/output-ios/Swift.Nuke.Wrappers.cs`

```csharp
public static unsafe class ImageRequestFactory
{
    public static ImageRequest FromUrlString(string urlString)
    {
        var utf8Bytes = System.Text.Encoding.UTF8.GetBytes(urlString + "\0");
        fixed (byte* ptr = utf8Bytes)
        {
            var swiftPtr = PInvoke_ImageRequest_initWithURLString_simple((IntPtr)ptr);

            // Allocate C# buffer and copy from Swift-allocated memory
            var metadata = SwiftObjectHelper<ImageRequest>.GetTypeMetadata();
            var csharpBuffer = (IntPtr)NativeMemory.Alloc((nuint)metadata.Size);

            // Use value witness table to initialize with copy (handles ARC correctly)
            metadata.ValueWitnessTable->InitializeWithCopy((void*)csharpBuffer, (void*)swiftPtr, metadata);

            // Create the ImageRequest with our copied buffer
            var result = SwiftMarshal.MarshalFromSwift<ImageRequest>(csharpBuffer);

            // Free the Swift-allocated memory
            PInvoke_ImageRequest_free(swiftPtr);

            return result;
        }
    }

    public static ImageRequest FromUrl(Swift.URL url) => FromUrlString(url.AbsoluteString);

    public static ImageRequest FromUrlRequest(Swift.URLRequest urlRequest)
    {
        var url = urlRequest.URL ?? throw new ArgumentException("URLRequest must have a valid URL");
        return FromUrlString(url.AbsoluteString);
    }

    [DllImport("SwiftBindings", EntryPoint = "ImageRequest_initWithURLString_simple")]
    private static extern IntPtr PInvoke_ImageRequest_initWithURLString_simple(IntPtr urlString);

    [DllImport("SwiftBindings", EntryPoint = "ImageRequest_free")]
    private static extern void PInvoke_ImageRequest_free(IntPtr ptr);
}
```

### Files Modified

| File | Changes |
|------|---------|
| `src/Swift.Runtime/src/Swift/Runtime/TypeMetadata.cs` | Added alternative P/Invoke declarations for experimentation |
| `BindingTesting/Nuke/output-ios/SwiftBindings.swift` | Added Swift wrapper functions |
| `BindingTesting/Nuke/output-ios/Swift.Nuke.Wrappers.cs` | Created new file with `ImageRequestFactory` class |
| `BindingTesting/Nuke/NukeTestApp/NukeTestApp.csproj` | Added reference to `Swift.Nuke.Wrappers.cs` |
| `BindingTesting/Nuke/NukeTestApp/Program.cs` | Added tests for `ImageRequestFactory` |
| `BindingTesting/Nuke/build-swift-wrapper.sh` | Updated to compile both Swift files |

### Verification

- **34 tests passed, 0 failed, 3 warnings**
- `TEST SUCCESS` marker detected
- `=== ALL VALIDATION PASSED ===`

**Working**:
```csharp
var request = ImageRequestFactory.FromUrlString("https://picsum.photos/200/200");
var image = await ImagePipeline.Shared.Image(request);  // Successfully loads image!
```

### Known Limitations

- `FromUrl(Swift.URL)` convenience method has a known issue with `URL.AbsoluteString` using non-blittable P/Invoke - documented as warning
- Custom image processors still cannot be passed (would need Swift wrappers that accept processor configurations)

### Documentation

See `/src/docs/known-issues-workarounds.md` for full technical details on this and other major issues/workarounds

---

## Phase 30: Generic Operator & Member Collision Fixes

**Status**: COMPLETED (2026-02-01)

Fixed two cross-library issues identified from BlinkID and Lottie testing that caused compilation errors on generic types.

### Summary

- 30.1 Generic Operator Type Parameters → COMPLETED (48 errors fixed in BlinkID, multiple in Lottie)
- 30.2 Member Name Collision Detection → COMPLETED (CS0542 edge cases)
- 30.3 Cross-Library Validation → COMPLETED (0 errors in BlinkID, Lottie, Nuke)

### Problem 1: Operators on Generic Types (CS0563, CS0305)

Operators on generic types like `DateResult<T0>` were emitting non-generic type names:

```csharp
// Before (broken):
public static bool operator ==(DateResult left, DateResult right)  // CS0305!

// After (fixed):
public static bool operator ==(DateResult<T0> left, DateResult<T0> right)  // ✅
```

### Problem 2: Member Name Collisions (CS0542)

Swift allows properties to have the same name as their containing type or nested types, but C# does not.

### Solution

#### 30.1 Operator Handler Updates

**Files Modified:**
- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/OperatorHandler.cs`
  - Updated `EmitOperator()` to compute `typeNameWithGenerics` via `GenericTypeEmitter.GetTypeNameWithGenerics()`
  - Updated `EmitOperatorWrapper()` to accept both `typeName` and `typeNameWithGenerics`
  - Added `FixGenericTypeName()` helper to replace base type with generic version

- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/TypeHandlerHelpers.cs` (`EqualityMethodsWriter`)
  - Added `_typeNameWithGenerics` field
  - Updated `WriteSwiftEquatableImplementationWithSwiftEquals()` and `WriteDefaultEquatableImplementation()` to use generic type names

- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/ClassHandler.cs` (`ClassEqualityMethodsWriter`)
  - Same pattern as `EqualityMethodsWriter`

- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/NonFrozenStructHandler.cs`
- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/FrozenStructHandler.cs`
- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/ClassHandler.cs`
  - Updated `ValidateAndEmitPairs()` calls to pass `typeNameWithGenerics`

#### 30.2 Member Collision Detection

**Files Modified:**
- `src/Swift.Bindings/src/Marshaler/NameProvider.cs`
  - Added `containingTypeName` parameter to `GetPropertyName()`
  - Properties matching containing type name get `Value` suffix

- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/PropertyHandler.cs`
- `src/Swift.Bindings/src/Emitter/StringEmitter/AsyncStreamEmitter.cs`
- Type handlers (`NonFrozenStructHandler`, `FrozenStructHandler`, `ClassHandler`, `EnumHandler`)
  - Updated to pass containing type name for collision detection

### Verification

**Test Results:**
- 902 unit tests passing
- 678 integration tests passing (13 skipped - known limitations)
- 93 runtime tests passing

**Cross-Library Compilation:**
| Library | Before | After |
|---------|--------|-------|
| BlinkID | 66 errors | **0 errors** |
| Lottie | 41 errors | **0 errors** |
| Nuke | 0 errors | **0 errors** |

### Documentation

Updated:
- `/src/docs/binding-gaps-consolidated.md` - Marked Issues 2 & 3 as FIXED
- `/BindingTesting/BlinkId/BINDING_GAPS.md` - Updated status and statistics

---

## Phase 31: Generic DllImport, Protocol Proxy & AnyType Fixes

**Status**: COMPLETED (2026-02-01)

Cross-library validation identified three issues affecting BlinkID and Lottie bindings. All three have been fixed.

### Summary

- 31.1 Generic Type DllImport (P3) → COMPLETED (CS7042 warnings fixed via PInvokeHelperContext)
- 31.2 Protocol Proxy Generation (P4) → COMPLETED (missing imports, invalid syntax, empty return types)
- 31.3 AnyType in Generic Arguments (P5) → COMPLETED (generic type parameters now handled correctly)

### 31.1 Generic Type DllImport (CS7042)

**Problem**: C# doesn't allow `[DllImport]` inside generic types:
```csharp
public class Keyframe<T0>
{
    [DllImport(...)]  // CS7042!
    private static extern void PInvoke_...();
}
```

**Solution**: Created `PInvokeHelperContext` system that factors P/Invoke declarations into non-generic helper classes (`{TypeName}_PInvoke`):
```csharp
internal static class Keyframe_PInvoke
{
    [DllImport("Lottie", EntryPoint = "...")]
    internal static extern void PInvoke_value_Get(...);
}

public class Keyframe<T0>
{
    public T0 Value => Keyframe_PInvoke.PInvoke_value_Get(...);
}
```

**Files created/modified**:
- **NEW** `src/Swift.Bindings/src/Emitter/StringEmitter/PInvokeHelperEmitter.cs`
- `src/Swift.Bindings/src/Marshaler/Conductor.cs`
- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/MethodHandler.cs`
- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/NonFrozenStructHandler.cs`
- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/FrozenStructHandler.cs`
- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/ClassHandler.cs`
- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/OperatorHandler.cs`

**Impact**: ~18 CS7042 warnings in BlinkID eliminated

### 31.2 Protocol Proxy Generation (P4)

**Problem**: Generated Swift code for EveryProtocol had multiple issues:
- Missing imports (CoreGraphics, CoreText, QuartzCore)
- Invalid metatype syntax (`any Any.Type` vs `Any.Type`)
- Empty return types from unresolved generics
- Unresolved generic parameters (`τ_0_0`) in signatures

**Solution**:
- Fixed `GetSwiftTypeName()` in `EveryProtocolEmitter.cs` to handle `ProtocolListTypeSpec`
- Added generic type parameter detection (`τ_0_0`, `T`, `Element` → `Any`)
- Fixed metatype syntax handling
- Expanded `AppleFrameworks` set in `ModuleHandler.cs` (40+ frameworks)
- Added `ScanProtocolsForFrameworkImports()` for recursive type scanning

**Files modified**:
- `src/Swift.Bindings/src/Emitter/StringEmitter/EveryProtocolEmitter.cs`
- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/ModuleHandler.cs`

**Impact**: ~8 protocols in Lottie now generate valid Swift code

### 31.3 AnyType in Generic Arguments (P5)

**Problem**: Generic type parameters (`τ_0_0`, `T`, `Element`, etc.) were incorrectly classified as existential types, causing properties to be skipped with `AnyType` fallback or crash with "Invalid module-qualified name".

**Solution**: Created shared `TypeSpecHelpers.IsGenericTypeParameter()` utility and updated `TypeDatabaseExtensions` to not misclassify generic parameters:
```csharp
public static bool IsGenericTypeParameter(string typeName)
{
    if (typeName.StartsWith("τ_")) return true;
    if (typeName is "T" or "U" or "Element" or "Key" or "Value" ...) return true;
    // ...
}
```

**Files created/modified**:
- **NEW** `src/Swift.Bindings/src/Model/TypeSpec/TypeSpecHelpers.cs`
- **NEW** `src/Swift.Bindings/tests/UnitTests/TypeSpecTests/TypeSpecHelpersTests.cs` (32 tests)
- `src/Swift.Bindings/src/TypeDatabase/TypeDatabaseExtensions.cs`
- `src/Swift.Bindings/src/Marshaler/ClosureHandler.cs`
- `src/Swift.Bindings/src/Emitter/StringEmitter/EveryProtocolEmitter.cs`

**Impact**: ~20 skipped properties in BlinkID now handled gracefully

### Verification

- All 935 unit tests pass
- All 678 integration tests pass (13 skipped - known limitations)
- All 93 runtime tests pass (1 skipped)

**Cross-Library Compilation Status**:
| Library | Before Phase 31 | After Phase 31 |
|---------|-----------------|----------------|
| BlinkID | 18 CS7042 warnings | **0 warnings** |
| Lottie | Protocol proxy errors | **0 errors** |
| Nuke | 0 errors | **0 errors** |

---

## Phase 32: Optional-Wrapped Existential Property Accessor Fix

**Status**: COMPLETED (2026-02-01)

### Problem

Properties with Optional-wrapped existential types like `(any DataCaching)?` were generating property wrappers but not their accessor methods (`DataCache_Get()`, `DataCache_Set()`), causing compilation errors:

```
error CS0103: The name 'DataCache_Get' does not exist in the current context
error CS0103: The name 'DataCache_Set' does not exist in the current context
```

This affected 5 properties in Nuke:
- `DataCache`, `ImageCache` in `ImagePipeline.Configuration`
- `Delegate` in `DataLoader`
- `DataLoadingError` in `ImagePipeline.Error`

### Root Cause

The `TypeSpecParser` was incorrectly propagating the `IsAny` flag from the inner existential type to the outer `Swift.Optional` wrapper when parsing types like `(any DataCaching)?`. This caused the generator to treat `Optional<Existential>` as if it were an existential type itself, leading to incorrect type handling in signature builders.

### Solution

1. **TypeSpecParser.cs**: Fixed to apply `IsAny` to the inner type before wrapping with Optional, then reset `IsAny` for the wrapper:
   ```csharp
   while (tokenizer.Peek().Kind == TypeTokenKind.QuestionMark)
   {
       type.IsAny = isAny;  // Apply to inner type first
       isAny = false;        // Reset for Optional wrapper
       type = WrapAsBoundGeneric(type, "Swift.Optional");
   }
   ```

2. **ExistentialHandler.cs**: Added methods to detect and handle Optional-wrapped existentials:
   - `IsOptionalExistential(TypeSpec)` - detects `Optional<Existential>` types
   - `UnwrapOptionalExistential(TypeSpec)` - extracts the inner existential
   - `GetCSharpOptionalExistentialType()` - returns nullable type (e.g., `ExistentialContainer1?`)

3. **PropertyHandler.cs**: Added handling for Optional-existential properties

4. **MethodHandler.cs**: Updated `WrapperSignatureBuilder` and `PInvokeSignatureBuilder` to handle Optional-existential return types and parameters

5. **AnyType.cs**: Added `ToNullable()` method to handle generated code that calls this method

### Verification

- All 935 unit tests pass
- All 678 integration tests pass (13 skipped)
- All 93 runtime tests pass (1 skipped)
- Nuke: 0 compilation errors
- NukeTestApp: 34/34 validation tests pass

---

## Technical Debt Reference

This section summarizes code markers and test coverage for future contributors.

### TODO/FIXME Summary

| Location | Count | Notes |
|----------|-------|-------|
| Swift.Bindings/src | ~24 | Excludes intentional generated code markers |
| Swift.Runtime/src | ~5 | Known limitations documented |
| Tests | 1 | ClosuresTests.swift @convention(c) |

**Known Limitation TODOs** (documented, not actionable without major work):
- `PropertyHandler.cs:162` - Async properties not supported
- `SwiftResult.cs:129` - Protocol conformance for Result incomplete
- `TypeMetadata.cs:327,150` - Tuple types and built-in type metadata
- `SwiftMarshal.cs:49,192` - Runtime tuple marshalling
- `TbdParser.cs:34` - JSON TBD format (only YAML-like supported)
- `ProtocolProxyEmitter.cs:44` - Generic protocol proxy sophistication

**Namespace Mapping** (`ModuleProcessor.cs:273,354,385,443`): Uses temporary `Swift.{Module}` pattern. Tracked for future proper namespace mapping solution.

**Generated Code Markers** (intentional): `ProtocolProxyEmitter.cs` and `EnumHandler.cs` emit TODO comments in proxy stubs that throw `NotImplementedException`. These are expected in generated Nuke bindings (~40 instances).

### Test Coverage Summary

**Well-Tested Components** (dedicated test files):
| Component | Tests | Coverage |
|-----------|-------|----------|
| ClosureHandler | 38+ | Comprehensive |
| ExistentialHandler | 42 | Comprehensive |
| OperatorHandler | 68 | Comprehensive |
| TupleHandler | 24 | Good |
| BoundGenericsHandler | ~20 | Good |
| TypeConversionHandler | ~15 | Good |
| EnumHandler | ~22 | Good (includes tag calculation, tuple extraction) |

**Coverage via Integration/Runtime Tests**:
- MethodHandler (2,905 lines) - Covered by 691 integration tests
- TypeHandler (3,090 lines) - Covered by integration tests + NukeTestApp
- SwiftABIParser (1,160 lines) - Covered by integration tests

**Test Totals**: 902 unit + 691 integration + 94 runtime = 1,687 tests
