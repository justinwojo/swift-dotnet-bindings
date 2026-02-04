# Swift Bindings - Current Status

**Last Updated**: February 2026 (Phase 52 Complete)
**Unit Tests**: 1216 passed
**Libraries Tested**: Nuke, BlinkID, Lottie

---

## Compilation Status

| Library | Generator Errors | Runtime Validation |
|---------|------------------|-------------------|
| **Nuke** | 0 ✅ | Full runtime validation |
| **BlinkID** | 0 ✅ | Compiles clean (no runtime tests yet) |
| **Lottie** | 0 ✅ | Runtime validated (9/9 tests pass) |

### Binding Coverage

| Library | Types | Type % | Members | Member % |
|---------|-------|--------|---------|----------|
| BlinkID | 116/119 | 97.5% | 559/655 | 85.3% |
| Nuke | 60/68 | 88.2% | 325/490 | 66.3% |
| Lottie | 79/93 | 84.9% | 365/609 | 59.9% |

Member coverage gaps are primarily due to unsupported signatures and existential edge cases. Target is 90%+ for common API patterns.

---

## What Works

### Types
- ✅ Classes (with ARC via SafeHandle)
- ✅ Structs (frozen and non-frozen)
- ✅ Enums (with associated values, raw representable, runtime enum case construction)
- ✅ Protocols (interface + proxy generation + conformance emission)
- ✅ Generics (bound generics, generic enums, generic classes, unbound generic type parameters in properties/methods)
- ✅ Actors (detected via Actor protocol conformance, emitted as classes with actor comment)

### Members
- ✅ Methods (instance, static, async)
- ✅ Properties (getters and setters)
- ✅ Operators (+, -, ==, !=, <, >, etc. with automatic pair synthesis, null-safe equality on reference types)
- ✅ Constructors (including failable `init?` as `TryCreate()` factory methods)
- ✅ Inout parameters (emitted as `ref` in C#)
- ✅ Subscripts (as C# indexers)

### Special Types
- ✅ SwiftString, SwiftArray<T>, SwiftSet<T>, SwiftOptional<T>
- ✅ Closures (@convention(c), @escaping with frozen types, throwing closures)
- ✅ Tuples (1-7 elements)
- ✅ Existential containers (protocol composition)
- ✅ Opaque return types (`some Protocol` → existential container via Swift wrapper)
- ✅ CoreGraphics opaque types (CGImage, CGColor, CGContext → IntPtr)
- ✅ Swift pointer types (OpaquePointer, UnsafePointer, UnsafeMutablePointer → IntPtr)
- ✅ NSObject subclass parameters (ObjC bridged marshalling pipeline)

### DX Features
- ✅ Binding completeness report (`binding-report.json`)
- ✅ `[UnsupportedSwiftType]` attribute on degraded members
- ✅ Skip reasons in report (UnsupportedSignature, AnyTypeFallback, AsyncProperty, etc.)
- ✅ Configurable namespace mapping
- ✅ Async property detection via TBD symbol analysis

---

## What Doesn't Work

### Architectural Gaps
- ⚠️ **Protocol witness table dispatch** - Phase A (blittable read-only) complete: property getters and non-mutating methods with primitive types dispatch through witness table. Setters, mutating methods, and String marshalling not yet supported.
- ❌ **Actor isolation enforcement** - Actor methods callable without async/await from C# (Swift runtime handles isolation internally)

### Framework Limitations
- ❌ **SwiftUI** - Types with SwiftUI constraints skipped
- ❌ **Combine** - Reactive framework out of scope

### Edge Cases
- ❌ **8+ element tuples** - Would require ValueTuple nesting
- ❌ **Closures within closures** - Not supported
- ❌ **Generic associated types** - PATs limited
- ❌ **Async+throwing closures at runtime** - Binding generation works but runtime blocked by existential metadata Mono JIT bug

### Known Runtime Issues
- **Mono JIT**: `swift_getExistentialTypeMetadata` crash when creating `SwiftArray<ExistentialContainer>` (workaround: Swift wrapper functions)
- **SafeHandle in async**: .NET runtime doesn't preserve SafeHandle through async P/Invoke (workaround: singleton pattern + IntPtr conversion)
- See `known-issues-workarounds.md` for full details

---

## Recent Completions (Phase 52)

### Protocol Witness Table Dispatch — Phase A (Blittable Read-Only)
- Swift-backed existential proxies can now access protocol members with blittable primitive types
- `WitnessDispatchEmitter` generates Swift `@_silgen_name` accessor functions that reconstruct existentials via `containerPtr.load(as: (any Protocol).self)` and dispatch through the witness table
- `ProtocolProxyEmitter` replaces `NotSupportedException` with P/Invoke dispatch for dispatchable members
- Three-layer safety gate: Swift-side blittability + projected-type blittability + return-type canonicalization
- Non-dispatchable members (String, throws, async, setters, mutating) gracefully degrade to `NotSupportedException`
- `SwiftTypeNameHelper` extracted as shared utility for Swift type name rendering
- 65 new unit tests; 1216 total passing

---

## Previous Completions (Phase 49)

### Async Concurrency Hook Shared Library
- Extracted inline `swift_task_enqueueGlobal_hook` from `AsyncTests.swift` into `libSwiftBindingsRuntime.dylib`
- `SwiftBindingsRuntime.swift`: GCDExecutor + `dlsym`-based hook, exported via `@_cdecl`
- `build-runtime.sh`: Builds universal binaries (arm64 + x86_64) for macOS, iOS device, and iOS Simulator
- `SwiftConcurrency.cs`: Thread-safe `SwiftConcurrency.Initialize()` with double-checked locking; verifies hook was installed via native callback; throws `InvalidOperationException` on failure
- `Swift.Runtime.csproj`: Includes platform-specific native dylib via `RuntimeIdentifier`-based conditions
- `AsyncTests.cs` updated to use `SwiftConcurrency.Initialize()` instead of test-specific P/Invoke
- Any .NET app can now initialize Swift async interop with: `SwiftConcurrency.Initialize()`

---

## Previous Completions (Phase 48)

### Generic Tuple Return Marshalling
- Generic tuple returns (e.g., `pair<T, U>() -> (T, U)`) now emit with correct indirect result marshalling
- `MarshallingHelpers.MethodRequiresIndirectResult` returns `true` for tuples with generic type parameter elements
- CSSignatureBuilder and PInvokeSignatureBuilder updated to accept generic-element tuples via GenericContext
- P/Invoke uses `SwiftIndirectResult` + `void` return; wrapper extracts elements via `SwiftMarshal.MarshalFromSwift<ValueTuple<T0, T1>>`
- TestFramework: `generic_function` moved from degraded to passing (92/93 must-pass, 1 degraded)

### Null-Safe Equality Operators on Reference Types
- Generated `==` and `!=` operators on C# reference types (classes, non-frozen structs, enums) now include null guards
- Without guards, `obj == null` or `obj != null` would call `.Payload` on null and throw `NullReferenceException`
- Guards emitted for both explicit operators (P/Invoke-backed) and synthesized paired operators
- Lottie: `LottieConfiguration.Shared` now works correctly — 9/9 runtime tests pass (up from 8/9)

---

## Previous Completions (Phase 47)

### Protocol Runtime Completion
- All 7 `NotImplementedException` stubs in `ProtocolProxyEmitter.cs` replaced with descriptive `NotSupportedException` throws
- Swift-backed existential proxies now degrade gracefully: property get/set, method calls, subscript access, and conformance descriptor all throw `NotSupportedException` with messages identifying the specific member
- All `TODO` comments removed from the emitter
- XML documentation on existential container constructor documents the limitation
- 8 new unit tests verify the degradation behavior
- Unit tests: 1107 (up from 1099)

---

## Previous Completions (Phase 46)

### Unbound Generic Type Parameter Support
- Generic type parameters (`τ_0_0`, `τ_0_1`) in properties, methods, and constructors of generic types now resolve correctly
- `GenericContext` helper merges type-level + method-level generic mappings with offset C# names to avoid collisions
- Parser propagates parent type's generic parameters to property accessor methods (getter/setter)
- `PropertyHandler` resolves generic type param properties (e.g., `Wrapper<T>.wrapped` → `T0`)
- `BoundGenericsHandler` accepts explicit `GenericContext` for resolving args like `Optional<τ_0_0>` → `SwiftOptional<T0>`
- `TupleHandler` supports generic type parameter elements when context available
- All signature builders thread `GenericContext` through bound generic and tuple translation
- TestFramework: 91/93 must-pass features (up from 88/93), 672/747 members emitted (up from 658/747)
- 3 features fixed: `generic_struct`, `generic_class`, `where_clause`
- `generic_function`: `pair<T,U>()` correctly skipped — generic tuple return marshalling deferred to Phase 48
- Remaining degraded (Phase 46): `generic_function` (fixed in Phase 48), `any_protocol_existential` (requires `SwiftArray<ExistentialContainer>`)

### Code Review Fixes
- Fixed `FromMethodInType` duplicate param handling: parser copies type params to accessor methods, so method params that duplicate type-level params are now skipped (prevents τ_0_0 → T0 being overwritten with T1)
- Fixed `EmitSignatureMethod` to not emit `<T0>` generic param declarations on accessor methods
- Fixed `BuildWhereClause` to skip accessor methods (accessors inherit constraints from parent type)
- Generic tuple returns (`pair<T,U>() -> (T, U)`) explicitly skipped: wrapper would need per-element marshalling from `ValueTuple<IntPtr, IntPtr>` → `(T0, T1)` which requires indirect result + element extraction (deferred)
- Narrowed `IsGenericTypeParameter` to only match `τ_X_Y` notation, single-letter params, and numbered params (T0, T1) — removed overly broad named matches ("Element", "Key", "Value", "Result", etc.) that could misclassify concrete types
- Added null safety in `PropertyHandler` for `typeRecord` dereference when type resolution fails
- Fixed `EmitSignatureMethod` and `EmitSignatureConstructor` to only declare method-own generic params, not type-inherited ones (methods inside generic types were redundantly redeclaring `<T0, T1>`)
- Made `HasGenericTypeParameterElements` recursive: now catches nested generic params like `(Optional<τ_0_0>, Int)`, not just direct generic param elements
- Added 21 regression tests for `GenericContext`, `TupleHandler`, and `BoundGenericsHandler` generic behavior

---

## Previous Completions (Phase 45)

### Swift Pointer Type Support
- `OpaquePointer`, `UnsafePointer`, `UnsafeMutablePointer`, `UnsafeRawPointer`, `UnsafeMutableRawPointer`, `Builtin.RawPointer` → `System.IntPtr`
- Early-return checks in TypeDatabaseExtensions for all 5 resolution methods
- `Optional<OpaquePointer>` resolves automatically through existing Optional handler

### NSObject Subclass as Method Parameter
- Free functions taking NSObject parameters (e.g., `describeNSObject(obj: NSObject)`) now emit correctly
- Synthetic ObjCBridged TypeRecord created for known ObjC root classes (NSObject, NSProxy)
- Handles TypeSpecParser's `ObjectiveC.X → Foundation.X` remapping with narrow predicate
- DB-first precedence preserved: explicit type database entries override synthetic records
- Non-class ObjectiveC module types (Selector, ObjCBool) correctly excluded

### Finalizer Safety Net for SwiftSafeHandle
- `GC.SuppressFinalize(this)` added to `Dispose()` — standard .NET SafeHandle pattern
- `Debug.WriteLine` warning emitted when SwiftSafeHandle is finalized without explicit Dispose
- Documents the deliberate tradeoff: Swift `Destroy` is skipped during finalization to avoid SIGSEGV

### Test Coverage
- Unit tests: 1078 (up from 1032)
- TestFramework: 88/93 must-pass features passing (up from 85/93)
- 3 features fixed: `opaque_pointer`, `optional_opaque_pointer`, `nsobject_as_parameter`

---

## Previous Completions (Phase 44)

### Inout Parameter Support
- Swift `inout` parameters now emit as C# `ref` parameters
- ABI JSON `paramValueOwnership: "InOut"` detected in parser
- `ref` modifier added to P/Invoke and wrapper signatures for direct-pass types
- `PayloadBuffer<T>.BufferRef` added for inout frozen-with-memory-management types (ref-returning property into native memory; avoids CS1510 from by-value `Buffer` property)

### Failable Initializer Support (`init?`)
- Failable constructors emit `TryCreate()` static factory methods returning nullable types
- Correctly handles frozen structs (direct value extraction) and non-frozen types (InitializeWithCopy)
- Uses `SwiftOptional` metadata accessor to check Some/None tag on indirect result
- String and other type-converted parameters marshalled correctly
- Generic and closure-heavy failable constructors supported (TypeMetadata, payload, GCHandle, protocol witness table setup)

### Codex Review Fixes
- Generic inout writeback now runs before error check, so `ref` generic parameter mutations survive exceptions on throwing paths
- P/Invoke dedup for `SwiftOptional` metadata accessor scoped to generation run (moved from static `ConstructorHandler` set to instance field on `ConstructorHandlerFactory`); prevents suppressed emission across multiple runs in the same process
- `PInvokeHelperContext.AddDeclaration` deduplicates by method name to prevent duplicate P/Invoke declarations in generic type helper classes

---

## Previous Completions (Phase 43)

### Protocol Conformance Emission
- Types now emit C# interfaces for same-module protocol conformances
- `SimpleItem : ISwiftObject, ISwiftDescribable, ISwiftTestIdentifiable`
- Works across classes, structs, and enums

### Opaque Return Types (`some Protocol`)
- `OpaqueTypeArchetype` parsed from ABI JSON → `ProtocolListTypeSpec { IsOpaque = true }`
- Swift wrappers generated to box concrete returns into existential containers
- Property getters and methods both supported

### Async Property Detection
- Async getters detected via TBD symbol `mangledName + "Tu"` suffix
- Properly skipped with `SkipReason.AsyncProperty` (previously emitted as synchronous)

### Actor Type Support
- Actors detected via `Actor` protocol conformance in ABI JSON
- `IsActor` flag on `ClassDecl`, `unownedExecutor` property filtered
- Generated with `// Swift actor type` comment

### Bug Fixes
- Fixed NullReferenceException in MethodHandler for top-level async functions
- Fixed `CacluateFlags` crash for unknown generic types (e.g., `Swift.KeyPath`)
- Fixed test reflection for `DemanglingResults` constructor with `AllSymbols` parameter

### Previous Phase (42) Highlights
- Lottie: 8/9 runtime tests pass on iOS Simulator
- Enum case construction fix (DestructiveInjectEnumTag)
- CoreGraphics type stubs (CGImage, CGColor, etc.)

### Test Coverage (Phase 44)
- Unit tests: 1032
- Integration tests: 678 passed
- Runtime tests: 108 passed

---

## Active Documentation

| File | Purpose |
|------|---------|
| `north-star.md` | Project vision and roadmap |
| `remaining-work.md` | Consolidated backlog (generator gaps, runtime, validation) |
| `comprehensive-architecture-review.md` | Strategic direction and priorities |
| `emitter-redesign-proposal.md` | Future architecture improvement plan |
| `known-issues-workarounds.md` | Runtime issues and workarounds |
| `CompletedPhases/swift-concurrency-interop-plan.md` | Async concurrency design (fully implemented, remaining work in `remaining-work.md` item 6 for callback marshalling) |

---

## Development History

49 phases of improvements tracked in git history. Key milestones:
- Phase 1-15: Core infrastructure and Nuke validation
- Phase 16-29: Type system and runtime fixes
- Phase 30-33: Generic type improvements
- Phase 34-39: Codex task completion (operators, enums, reporting)
- Phase 40: Protocol conformance infrastructure, namespace mapping
- Phase 41: Generic type fixes, 0 generator errors achieved
- Phase 42: Lottie runtime validation, enum case construction, CoreGraphics stubs
- Phase 43: Protocol conformance emission, opaque returns, async properties, actors
- Phase 44: Inout parameters, failable initializers
- Phase 45: Pointer types, NSObject parameters, finalizer safety net
- Phase 46: Unbound generic type parameters, code review fixes
- Phase 47: Protocol runtime completion (NotImplementedException → NotSupportedException)
- Phase 48: Generic tuple return marshalling, null-safe equality operators, Lottie 9/9
- Phase 49: Async concurrency hook shared library (SwiftConcurrency.Initialize)
