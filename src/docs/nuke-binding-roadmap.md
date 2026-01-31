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
- **0 compilation errors** (down from 95+)
- **1,368 generator tests passing** (605 unit, 691 integration, 72 runtime)
- **100% runtime validation pass rate** (30/30 tests in NukeTestApp, 2 skipped)

**Runtime validated** (Phase 15.4):
- ✅ Async image loading from network URLs
- ✅ UIImage return type marshalling to .NET iOS types
- ✅ Memory management (no retain count leaks detected)
- ✅ Protocol proxy implementations (C# → Swift callbacks)
- ✅ Cache access and population
- ✅ SwiftString and ImageRequest creation/disposal

**Remaining gaps**:
- **~4 methods/constructors** with `AnyType` parameters:
  - `imagePublisher(...)` - Returns Combine `AnyPublisher` (reactive framework out of scope)
  - `ImageRequest` constructor - `() async throws -> Data` closure (async+throws closures not supported due to [UnmanagedCallersOnly] limitations)
- **Fixed in Phase 15.2**:
  - ✅ `didComplete` property - Optional closure properties now correctly emit nullable delegate types (`Action?`, `Func<Task>?`, etc.)
  - Note: Async attribute in closures not preserved from ABI JSON `printedName` (pre-existing parser limitation)
- **Fixed in Phase 15.3**:
  - ✅ Implicit ObjC type conversions - `Swift.URL` and `Swift.Data` now have implicit operators for `Foundation.NSUrl`/`Foundation.NSData`
- **Fixed in Phase 15**:
  - ✅ Throwing closures - Non-async throwing closures now map to `Func<..., SwiftResult<T, SwiftError>>`
  - Note: Async+throwing closures remain unsupported because `[UnmanagedCallersOnly]` callbacks cannot await Tasks
- **Fixed in Phase 14**:
  - ✅ `data(_for:)` - Async method returning tuple `(Data, URLResponse?)` now properly typed
- **Fixed in Phase 13**:
  - ✅ `loadImage(with:queue:progress:completion:)` - Optional closure parameters now use `Action<...>?` syntax
  - ✅ AsyncStream property collision - Properties like `Progress` renamed to `ProgressValue` when colliding with nested types
  - ✅ Module-local types in closures - `ImageResponse?` in progress callback now correctly typed
- **Fixed in Phase 12**:
  - ✅ `ImageProcessors.Resize` constructor - CGSize now resolved via module aliasing
  - ✅ `ImageRequest.ThumbnailOptions` constructor - CGSize now resolved
  - ✅ `loadData` completion callback - Tuple `(Data, URLResponse?)` now properly typed
- **ObjC types use `Swift.*` wrappers instead of existing .NET iOS bindings** (see Phase 2.8 in completed phases) - Major UX issue

---

## Baseline Analysis

Initial binding generation revealed these gap categories:

| Category | Count | Impact |
|----------|-------|--------|
| Protocol conformance descriptors not found | 200 | Warnings only, types still emit |
| Unsupported accessor kinds (set/_modify) | 128 | Properties are read-only |
| Unsupported method signatures | 67 | Methods skipped |
| Unsupported property types | 70 | Properties skipped |
| Generic protocol types unsupported | 8 | Types skipped entirely |
| Unsupported constructor signatures | 22 | Constructors skipped |

**Initial result**: Generated 417KB of C# bindings with 52 types, but many methods/properties skipped.

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
**Status**: Known crash (partially addressed in Phase 16.1)

Properties returning non-frozen struct types that contain existential containers crash at runtime with SIGSEGV in Swift's copy witness.

**Phase 16.1 fixed**: SafeHandle ref counting for class instance methods (e.g., `ImagePipeline.CacheValue` now works).

**Still crashes**:
- `ImagePipeline.ConfigurationValue` - The `Configuration` struct contains existential container fields (protocol types like `DataLoading`)
- Swift's copy witness operation fails when copying structs with existentials

**Workaround**: Avoid accessing properties that return non-frozen structs with existential container fields; use alternative APIs.

### Simple Swift Enum Case Support
**Status**: COMPLETED (Phase 18) - RawRepresentable enums now fully supported

Simple Swift enum cases (without associated values) can now be constructed from C# for both frozen and **non-frozen** RawRepresentable enums.

**Current state (Phase 18)**:
- ✅ Parser extracts `enumRawTypeName` from ABI JSON
- ✅ `EnumDecl.IsRawRepresentable` detection works
- ✅ Frozen RawRepresentable enums emit `FromRawValue()` with direct IntPtr return
- ✅ **Non-frozen RawRepresentable enums** emit `FromRawValue()` with indirect return handling
- ✅ Static case properties (`VeryLow`, `Low`, `Normal`, `High`, `VeryHigh`) work for all RawRepresentable enums

**Working APIs** (Phase 18):
- ✅ `ImageRequest.Priority.*` cases (VeryLow, Low, Normal, High, VeryHigh) - **NOW WORKING**
- ✅ `ImageTask.State.*` cases (Running, Cancelled, Completed) - **NOW WORKING**

**Still limited** (not RawRepresentable):
- `ImagePipeline.Error.*` cases with associated values (requires factory methods)
- Enums without RawRepresentable conformance

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
- [x] **ObjC/Swift type strategy complete** - ObjC classes (UIImage, etc.) map to .NET iOS types; Swift structs (URL, Data) intentionally kept as Swift.* wrappers
- [x] **Implicit ObjC conversions** - `Swift.URL` and `Swift.Data` have implicit operators for `Foundation.NSUrl`/`Foundation.NSData`
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
- ⚠️ Async+throws closures NOT supported - `[UnmanagedCallersOnly]` callbacks cannot await Tasks

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
**Status**: COMPLETED (partial - ref counting fixed, existential struct crash remains)

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

**Remaining issue** (separate root cause):
- `ImagePipeline.ConfigurationValue` still crashes in Swift's copy witness (`$s4Nuke13ImagePipelineC13ConfigurationVWOc`)
- Root cause: The `Configuration` struct contains existential containers (protocol types like `DataLoading`) which cause crashes during Swift's value copy operation
- This is NOT a ref counting issue - it's an existential container marshalling issue in non-frozen struct returns
- Workaround: Skip accessing properties that return non-frozen structs with existential container fields

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
| Category | Count |
|----------|-------|
| Unit tests | 605 |
| Integration tests | 691 |
| Runtime tests | 72 |
| **Total** | **1,368** |

All generator tests passing.

### NukeTestApp Validation (Phase 18)
| Category | Passed | Failed | Warnings |
|----------|--------|--------|----------|
| Basic Binding | 4 | 0 | 1 |
| Async Image Load | 3 | 0 | 0 |
| Cache Operations | 3 | 0 | 0 |
| ImageRequest Options | 4 | 0 | 0 |
| Error Handling | 2 | 0 | 1 |
| Memory Management | 4 | 0 | 0 |
| Performance | 4 | 0 | 0 |
| Protocols | 6 | 0 | 0 |
| **Total** | **30** | **0** | **2** |

**Pass rate**: 100% (30/30 tests, 2 skipped with warnings)

**Core functionality verified**:
- Async image loading from network URLs
- UIImage return type marshalling
- Memory management (no retain count leaks)
- Protocol proxy implementations
- Cache access and population
- Swift async error handling (SwiftException)
- **Priority enum cases** (VeryLow, Low, Normal, High, VeryHigh) - NEW in Phase 18

**Skipped with warnings** (documented limitations):
- ImagePipeline.Error enum cases with associated values (requires factory methods)
- ConfigurationValue property (non-frozen struct with existential containers - Phase 16.1)

**Fixed in Phase 16**:
- ✅ Class SafeHandle ref counting for instance methods (Phase 16.1)
- ✅ Swift async errors now propagate as `SwiftException` (Phase 16.3)
- ✅ SafeHandle finalizer crash during GC (Phase 16.4)
- ✅ ClosureEmitter VWT pointer access syntax (Phase 16.5)

**Fixed in Phase 18**:
- ✅ Non-frozen RawRepresentable enum case construction (Priority, State enums)
