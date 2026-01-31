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
- **1,363 generator tests passing** (600 unit, 691 integration, 72 runtime)
- **93% runtime validation pass rate** (28/30 tests in NukeTestApp)

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

### Non-Frozen Struct Property Access
**Status**: Known crash (Phase 16.1)

Properties returning non-frozen struct types crash at runtime with SIGSEGV. Affects:
- `ImagePipeline.ConfigurationValue`
- Potentially other complex struct returns

**Workaround**: Avoid accessing these properties; use alternative APIs.

### Swift Enum Case Symbol Resolution
**Status**: Known issue (Phase 16.2)

Some Swift enum case accessor symbols fail with `EntryPointNotFoundException`:
- `ImageRequest.Priority.*` cases (VeryLow, Low, Normal, High, VeryHigh)
- `ImagePipeline.Error.DataIsEmpty`

Note: `ImageRequest.Options.*` (OptionSet) cases work correctly.

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

Comprehensive runtime validation suite created and executed. Results: **28 passed, 2 failed, 2 warnings (93% pass rate)**.

**Test categories validated**:
| Category | Tests | Result |
|----------|-------|--------|
| Basic Binding | 4/5 | Type metadata, Shared singleton, SwiftString, ImageRequest |
| Async Image Load | 3/3 | Valid URL, sequential loads, UIImage validity |
| Cache Operations | 3/3 | Cache access, cache population, Options types |
| ImageRequest Options | 3/5 | Options access, property access, metadata |
| Error Handling | 1/1 | Invalid URL format handled |
| Memory Management | 4/4 | No retain count leaks, allocation cycles, async stability |
| Performance | 4/4 | All operations < 50ms average |
| Protocols | 6/6 | Cancellable, ImageProcessing proxies, registry |

**Core use case verified working**:
```csharp
var pipeline = ImagePipeline.Shared;
var request = new ImageRequest("https://picsum.photos/200/200");
UIImage image = await pipeline.Image(request);  // Works!
```

**Issues discovered** (see Phase 16+ for details):
- Non-frozen struct property crash (Configuration, Cache)
- Swift enum case P/Invoke symbol resolution failures
- Swift wrapper try! causes fatal crash on errors
- SafeHandle finalizer crash during post-test GC

### 15.5 Closure Return Type Marshalling Fix
**Priority**: Low

ClosureEmitter generates invalid invocation code for closures with:
- Non-frozen struct parameters (uses `ISwiftObject.Payload` which doesn't exist)
- Existential return types (incorrect return type conversion)

This is a pre-existing bug exposed when fixing other issues. Currently affects closure properties like `makeImageDecoder`.

### Note: Async Instance Method Workaround

The singleton pattern detection (Phase 8) handles most async instance methods. For non-singleton classes, `unsafeBitCast` is used which may have edge cases. A proper fix would require .NET runtime changes to support SwiftSelf register with async Task closure capture.

---

## Phase 16+: Issues Discovered During Validation

The following issues were discovered during Phase 15.4 runtime validation testing. They are documented here for future work.

### 16.1 Non-Frozen Struct Property Marshalling Crash
**Priority**: High
**Status**: Not started

**Problem**: Accessing properties that return non-frozen struct types causes a native crash (SIGSEGV) in the Swift runtime.

**Affected APIs**:
- `ImagePipeline.ConfigurationValue` → `ImagePipeline.Configuration` (non-frozen struct)
- Potentially other properties returning complex non-frozen structs

**Crash location**: `$s4Nuke13ImagePipelineC13ConfigurationVWOc` (witness table copy)

**Root cause**: The generated P/Invoke for non-frozen struct return types doesn't correctly handle Swift's indirect return mechanism or value witness table operations.

**Workaround**: Skip accessing these properties; use alternative APIs where available.

**Files to investigate**:
- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/PropertyHandler.cs`
- `src/Swift.Bindings/src/Marshaler/MarshalingHelpers.cs`

### 16.2 Swift Enum Case P/Invoke Symbol Resolution
**Priority**: Medium
**Status**: Not started

**Problem**: Swift enum case accessor functions fail with `EntryPointNotFoundException`. The mangled symbols are not being found in the dylib.

**Affected APIs**:
- `ImageRequest.Priority.VeryLow` → symbol `$s4Nuke12ImageRequestV8PriorityO7veryLowyA2EmF` not found
- `ImageRequest.Priority.Low`, `.Normal`, `.High`, `.VeryHigh` - all fail
- `ImagePipeline.Error.DataIsEmpty` → symbol `$s4Nuke13ImagePipelineC5ErrorO11dataIsEmptyyA2EmF` not found

**Observed behavior**: All Priority enum cases fail; Options enum cases (DisableMemoryCache, etc.) work fine.

**Possible causes**:
1. Enum case symbols not exported in TBD file
2. Symbol mangling mismatch between generator and actual symbols
3. Different treatment of enum cases vs OptionSet static properties

**Files to investigate**:
- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/EnumHandler.cs`
- `src/Swift.Bindings/src/Parser/SwiftABIParser.cs` (enum parsing)
- Generated TBD file for symbol presence

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
**Status**: Not started

**Problem**: After test completion, during GC finalization, `SwiftSafeHandle.ReleaseHandle` crashes with SIGSEGV when trying to release Swift objects.

**Crash location**:
```
Swift_Runtime_SwiftSafeHandle_1_T_REF_ReleaseHandle
→ swift_release (Swift runtime)
```

**Observed behavior**: Tests complete successfully, then crash occurs during GC cleanup. This doesn't affect functionality but causes test runner to report failure.

**Possible causes**:
1. Double-release of Swift objects
2. Release of objects already deallocated by Swift runtime
3. Incorrect order of SafeHandle disposal vs Swift object lifecycle
4. Race condition between finalizer thread and Swift ARC

**Workaround**: Explicitly dispose SafeHandles before allowing GC (already done in tests).

**Files to investigate**:
- `src/Swift.Runtime/src/Swift/Runtime/SwiftSafeHandle.cs`
- `src/Swift.Runtime/src/Swift/Runtime/Arc.cs`

### 16.5 ClosureEmitter Non-Frozen Struct Parameter Bug
**Priority**: Low
**Status**: Documented in 15.5

This is the same issue documented in 15.5. Closure getters with non-frozen struct parameters generate invalid code:
- Uses `((ISwiftObject)_arg0).Payload` but `ISwiftObject` doesn't define `Payload`
- Calls `ValueWitnessTable.InitializeWithCopy()` / `Destroy()` which don't exist

**Affected APIs**:
- `ImagePipeline.Configuration.MakeImageDecoder` getter (commented out as workaround)
- `ImagePipeline.Configuration.MakeImageEncoder` getter (commented out as workaround)

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
| Unit tests | 600 |
| Integration tests | 691 |
| Runtime tests | 72 |
| **Total** | **1,363** |

All generator tests passing.

### NukeTestApp Validation (Phase 15.4)
| Category | Passed | Failed | Warnings |
|----------|--------|--------|----------|
| Basic Binding | 4 | 0 | 1 |
| Async Image Load | 3 | 0 | 0 |
| Cache Operations | 3 | 0 | 0 |
| ImageRequest Options | 3 | 1 | 0 |
| Error Handling | 1 | 0 | 1 |
| Memory Management | 4 | 0 | 0 |
| Performance | 4 | 0 | 0 |
| Protocols | 6 | 0 | 0 |
| **Total** | **28** | **2** | **2** |

**Pass rate**: 93% (28/30 core tests)

**Core functionality verified**:
- Async image loading from network URLs
- UIImage return type marshalling
- Memory management (no retain count leaks)
- Protocol proxy implementations
- Cache access and population

**Known failures** (non-blocking):
- Priority enum case symbols not found (P/Invoke issue)
- ImagePipeline.Error enum case symbols not found (P/Invoke issue)

**Skipped tests** (would cause crash):
- ConfigurationValue property (non-frozen struct marshalling crash)
- Error path testing (Swift wrapper uses try! → fatal error)
