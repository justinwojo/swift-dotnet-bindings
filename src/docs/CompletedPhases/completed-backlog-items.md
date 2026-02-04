# Completed Backlog Items

Archived from `remaining-work.md`. These items were completed during Phases 45-52.

**Starting Point**: Phase 48, 1107 unit tests, TestFramework v2.0 (67 files, 145 features)
**End Point**: Phase 52, 1216 unit tests, 93/93 must-pass features

---

## 1. Generic Tuple Return Marshalling (Phase 48)

Generic tuple returns (e.g., `pair<T, U>() -> (T, U)`) now emit correctly. Generator changes:
1. `MarshallingHelpers.MethodRequiresIndirectResult` returns `true` for tuples with generic elements
2. `CSSignatureBuilder.HandleReturnType` accepts generic-element tuples via `GenericContext`
3. `PInvokeSignatureBuilder.HandleReturnType` lets generic-element tuples fall through to indirect result handling
4. Both signature builders reject generic-element tuples on async methods (async skips indirect result, so the required sret path is unavailable)

The runtime already supported tuple metadata (`TryGetTupleTypeMetadata`) and per-element extraction (`MarshalTupleFromSwift`) — only the generator needed changes. TestFramework: `generic_function` moved from degraded to passing (92/93 must-pass).

---

## 2. OpaquePointer in Method Signatures (Phase 45)

Added `IntPtrType` static TypeRecord and `IsPointerType()` helper in `TypeDatabaseExtensions.cs`. Early-return checks in all 5 resolution methods cover `OpaquePointer`, `UnsafePointer`, `UnsafeMutablePointer`, `UnsafeRawPointer`, `UnsafeMutableRawPointer`, and `Builtin.RawPointer`. Optional<OpaquePointer> resolves automatically through the existing Optional handler. 46 unit tests added.

---

## 3. NSObject Subclass as Method Parameter (Phase 45)

Added synthetic ObjCBridged TypeRecord generation for known ObjC root classes (NSObject, NSProxy) in `TypeDatabaseExtensions.cs`. Handles TypeSpecParser's `ObjectiveC.X -> Foundation.X` remapping with a narrow predicate (`IsKnownObjCRootClass`). DB-first precedence preserved — explicit type database entries override synthetic records. Non-class ObjectiveC module types (Selector, ObjCBool) correctly excluded via `IsObjCRootClassSwiftType()`. 8 unit tests added.

---

## 4. Existential Type Argument in Bound Generic (Phase 50)

Two-part fix in `BoundGenericsHandler.cs` and `MethodHandler.cs`:

1. **Type translation**: `TranslateTypeSpecToCSharp()` now resolves supported existentials (0-8 protocols) to `ExistentialContainer{N}` instead of blanket `AnyType` fallback. Uses `ExistentialHandler.ToProtocolListTypeSpec()` + `IsSupportedExistential()` + `GetCSharpExistentialType()`. Unsupported existentials (9+ protocols) still fall back to `AnyType`.
2. **Method guard**: Added `TryGetFirstUnsupportedExistentialTypeArgument()` — same recursive structure as `TryGetFirstExistentialTypeArgument` but only returns `true` for unsupported existentials. `MethodHandler.Emit` calls this narrower check, allowing methods like `describeAll([any Describable])` to emit as `SwiftArray<ExistentialContainer1>`.

Constructor and property guards intentionally left on the broader check to limit blast radius.

---

## 5. Formalize Async Concurrency Hook (Phase 49)

Extracted the inline concurrency hook from `AsyncTests.swift` into a proper shared library:

1. `src/Swift.Runtime/swift/SwiftBindingsRuntime.swift` — GCDExecutor + `dlsym`-based hook, exported as `SwiftBindings_InitializeConcurrency` and `SwiftBindings_IsConcurrencyInitialized` via `@_cdecl`
2. `src/Swift.Runtime/swift/build-runtime.sh` — Builds `libSwiftBindingsRuntime.dylib` for macOS, iOS device, and iOS Simulator targets
3. `src/Swift.Runtime/src/Swift/Runtime/SwiftConcurrency.cs` — Thread-safe `SwiftConcurrency.Initialize()` with double-checked locking, `IsInitialized` property, XML documentation of limitations
4. `Swift.Runtime.csproj` updated to include native library per platform target
5. `AsyncTests.cs` updated to use `SwiftConcurrency.Initialize()` instead of test-specific P/Invoke; inline hook removed from `AsyncTests.swift`

---

## 7. Lottie 9/9 -- LottieConfiguration.Shared (Phase 48)

Root cause: Generated `==` and `!=` operators on reference types accessed `.Payload` without null checks. When C# code did `config != null`, it invoked the overloaded `!=` operator which delegated to `==`, passing `null` as the second argument. `arg1.Payload` threw `NullReferenceException`.

Fix: `OperatorHandler.cs` now emits null guards (`if (arg0 is null) return arg1 is null;`) for equality/inequality operators when the containing type is a C# reference type (ClassDecl, EnumDecl, non-frozen StructDecl, frozen-struct-projected-as-class). Guards are emitted for both explicit operators and synthesized paired operators. Lottie: 9/9 runtime tests pass.

---

## 9. Add Finalizer Safety Net to SwiftSafeHandle (Phase 45)

Added `GC.SuppressFinalize(this)` to `Dispose()` in `SwiftHandle.cs` — standard .NET SafeHandle pattern. Added `Debug.WriteLine` diagnostic warning when a SwiftSafeHandle is finalized without explicit Dispose, alerting developers to the ARC leak. Note: Swift `Destroy` is deliberately skipped during finalization to avoid SIGSEGV from the Swift runtime during .NET shutdown — the buffer is still freed, but Swift ARC is not decremented. This is documented as a known tradeoff.

---

## 10. Protocol Runtime Completion (Phase 47)

Replaced all 7 `NotImplementedException` stubs in `ProtocolProxyEmitter.cs` with descriptive `NotSupportedException` throws. The Swift existential code path (when `_csharpImpl == null`) now throws `NotSupportedException` with messages identifying the specific member and explaining the limitation. The conformance descriptor stub similarly throws `NotSupportedException` explaining that proxy types use EveryProtocol's witness table.

All `TODO` comments removed. XML documentation on the existential container constructor documents the limitation. 8 new tests verify the degradation behavior.

---

## 11. Wrapper Automation for Known-Problematic Patterns (Phase 51)

First-cut automation for existential-in-bound-generic constructors via `ExistentialBypassEmitter`. When a struct constructor has bound generic params containing existential type arguments and all such params have `HasDefaultArg == true`, the generator auto-emits:

1. **Swift wrapper**: `@_silgen_name` function that omits existential params (Swift fills defaults), heap-allocates the result, and returns `UnsafeMutableRawPointer`. Companion free function for cleanup.
2. **C# factory**: Static `Create_{hash}` method with try/finally cleanup, P/Invoke declarations (inline or via `PInvokeHelperContext` for generic types), frozen/non-frozen copy strategies.
3. **Binding report**: `WrappedItems` list with `WrapperKind`, `MangledName` (for overload disambiguation), and details.

**Safety gates** (return false -> falls back to skip):
- Parent must be a StructDecl; failable/throwing constructors rejected
- All existential params must have `HasDefaultArg == true`
- Passthrough params with `IsGeneric == true` rejected (no GenericTypeMapping for reduced method)
- Reduced signature must have no placeholders
- Wrapper and P/Invoke parameter signatures must match exactly (rejects types needing marshalling setup: SafeHandle, idiomatic conversions, indirect results)

**Scope limitation**: Handles constructors only, existential-in-bound-generic pattern only. Async SafeHandle and non-blittable CallConvSwift patterns are deferred.

---

## 12 Phase A. Emitter Decomposition -- MethodHandler File Split (Phase 50)

The original `MethodHandler.cs` (3,827 LOC) was split into 7 files with no behavioral changes:

| File | Lines | Contents |
|------|-------|----------|
| `MethodHandler.cs` | 369 | Handler factories + handlers (public API) |
| `MethodSignature.cs` | 517 | Parameter, Signature, SignatureBuilderBase, WrapperSignatureBuilder, SignatureHandler |
| `PInvokeEmitter.cs` | 535 | PInvokeSignatureBuilder, PInvokeEmitter |
| `WrapperEmitter.cs` | 692 | Core orchestration, constructor emission, structural helpers |
| `WrapperEmitter.Async.cs` | 779 | Async emission pipeline |
| `WrapperEmitter.Marshalling.cs` | 546 | Argument marshalling, closures, SafeHandle, generics |
| `WrapperEmitter.Return.cs` | 444 | Return handling, tuple element helpers |

All files <=800 LOC. All 1107 unit tests pass. TestFramework coverage unchanged.

---

## 13. Improve Binding Report with Workaround Recommendations (Phase 51)

Added `RecommendedWorkaround` field to `SkippedItem` in the binding report data model. `WorkaroundRecommendations.GetRecommendation(SkipReason)` maps all 14 skip reasons to actionable guidance text. Wired into both `RecordTypeSkipped` and `RecordMemberSkipped` in `ReportCollector`. JSON serialization picks it up automatically.

**Files**:
- `BindingReport.cs` — added `RecommendedWorkaround` property to `SkippedItem`
- `WorkaroundRecommendations.cs` — static mapping for all 14 `SkipReason` values
- `ReportCollector.cs` — populates workaround on skip recording
- `ReportEmitter.cs` — wrapper count in console summary

---

## 19 Phase A. Protocol Witness Table Dispatch -- Blittable Read-Only (Phase 52)

For protocol members whose types are all blittable primitives, the generator now emits Swift `@_silgen_name` accessor functions and C# P/Invoke calls instead of `NotSupportedException`.

**New files**:
- `WitnessDispatchEmitter.cs` — Generates Swift accessor functions (`SBW_{Protocol}_{kind}_{name}_{index}`) that reconstruct existentials via `containerPtr.load(as: (any Protocol).self)` and dispatch through the witness table. Heap-allocates return values; companion free functions handle cleanup.
- `SwiftTypeNameHelper.cs` — Shared Swift type name rendering extracted from `EveryProtocolEmitter`, used by both emitters.
- `WitnessDispatchEmitterTests.cs` — 47 tests covering accessor generation, marshalability checks, naming conventions, overload disambiguation.

**Modified files**:
- `ProtocolProxyEmitter.cs` — Property getters and methods with blittable signatures now emit `fixed` + P/Invoke dispatch path instead of `NotSupportedException`. P/Invoke declarations added to NativeMethods class. Three-layer projected-type gate ensures dispatch is only enabled when the C#-side projected type is also a blittable primitive (prevents type mismatches when TypeDatabase is incomplete).
- `EveryProtocolEmitter.cs` — Delegates to `SwiftTypeNameHelper` for type name rendering.
- `ModuleHandler.cs` — Creates `WitnessDispatchEmitter` and calls `EmitWitnessDispatchFunctions` for each suitable protocol.

**Safety gates** (all three must pass for dispatch):
1. **Swift-side blittability**: `IsPropertyGetterDispatchable` / `IsMethodDispatchable` — checks Swift type names against known blittable primitives, rejects throws/async methods
2. **Projected-type blittability**: C#-side projected type (from TypeDatabase) must also be a blittable primitive (`IsBlittablePrimitive`). Prevents type mismatches when projected type diverges (e.g., `Swift.AnyType` from incomplete TypeDatabase).
3. **Return type canonicalization**: `MarshalFromSwift<T>` uses canonical blittable type from `GetBlittableCSharpType`, not the interface-projected type.

Non-dispatchable members gracefully degrade to `NotSupportedException`.
