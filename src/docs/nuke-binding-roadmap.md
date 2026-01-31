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
- **593 unit tests, 691 integration tests, 72 runtime tests passing (1,356 total)**

**Remaining gaps**:
- **1 property** with unsupported types (skipped):
  - `didComplete` - @MainActor async closure with Optional wrapping; closure infrastructure ready but accessor signature unsupported
- **~4 methods/constructors** with `AnyType` parameters:
  - `imagePublisher(...)` - Returns Combine `AnyPublisher` (reactive framework out of scope)
  - `ImageRequest` constructor - `() async throws -> Data` closure (throwing closures not supported)
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
- [x] **Existential parameters** - Methods with `any Protocol` parameters now generate valid `ExistentialContainer{N}` types
- [x] **Closure bound generic parameters** - Closures accepting `Result<T,E>`, `Array<T>`, `Optional<T>`, etc. now generate valid C# code
- [x] **Closure bound generic returns** - Closures returning `SwiftOptional<T>`, `SwiftResult<S,E>`, etc. now use indirect return marshalling
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

### 15.1 Throwing Closures
**Priority**: Medium

Closures with `throws` attribute are excluded from support.

**Affected APIs**:
- `ImageRequest` init with `() async throws -> Data` parameter

**Requirements**:
- Implement error marshalling for closure returns
- Handle Swift Error to C# Exception conversion

### 15.2 didComplete Property Accessor
**Priority**: Medium

The `didComplete` property has @MainActor async closure infrastructure ready, but the accessor method signature is unsupported.

```swift
var didComplete: (@MainActor @Sendable () async -> Void)?
```

**Requirements**:
- Analyze why accessor method signature fails
- May need special handling for Optional closure property accessors
- MainActor dispatch on C# side

### 15.3 ObjC Type Remapping Enhancement
**Priority**: Medium

ObjC types currently use `Swift.*` wrappers (e.g., `Swift.URL`, `Swift.Data`) instead of existing .NET iOS bindings. This creates friction for .NET developers.

**Current state**: ObjC classes (UIImage, URLResponse) correctly map to .NET iOS types. Swift structs bridged from ObjC (URL, Data) intentionally use wrappers.

**Options**:
1. Keep current design (Swift wrappers for safety)
2. Add implicit conversions between Swift.* and Foundation types
3. Full remapping (breaking change)

### 15.4 Complete API Coverage Validation
**Priority**: Medium

Validate that all emitted APIs actually work at runtime, not just compile.

**Test plan**:
- Create integration tests for AsyncStream properties
- Test closure property invocation
- Verify non-frozen struct closure parameter marshalling

### 15.5 Closure Return Type Marshalling Fix
**Priority**: Low

ClosureEmitter generates invalid invocation code for closures with:
- Non-frozen struct parameters (uses `ISwiftObject.Payload` which doesn't exist)
- Existential return types (incorrect return type conversion)

This is a pre-existing bug exposed when fixing other issues. Currently affects closure properties like `makeImageDecoder`.

### Note: Async Instance Method Workaround

The singleton pattern detection (Phase 8) handles most async instance methods. For non-singleton classes, `unsafeBitCast` is used which may have edge cases. A proper fix would require .NET runtime changes to support SwiftSelf register with async Task closure capture.

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

| Category | Count |
|----------|-------|
| Unit tests | 593 |
| Integration tests | 691 |
| Runtime tests | 72 |
| **Total** | **1,356** |

All tests passing. iOS Simulator validation successful.
