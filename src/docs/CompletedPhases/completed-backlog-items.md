# Completed Backlog Items

Archived from `remaining-work.md`. These items were completed during Phases 45-53.

**Starting Point**: Phase 48, 1107 unit tests, TestFramework v2.0 (67 files, 145 features)
**End Point**: Phase 54, 1238 unit tests, 93/93 must-pass features

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

---

## 14. .NET Convenience Methods on Swift Runtime Types (Phase 52)

Added `ToArray()`, `ToList()`, and `ToString()` to `SwiftArray<T>`. Fixed native memory leak in indexer getter — `NativeMemory.Alloc` buffer was never freed after `MarshalFromSwift` copied data out; added try/finally with `NativeMemory.Free`. `SwiftString` already had all needed methods (`ToString()`, implicit conversions). 9 new tests added.

---

## 16. Upstream .NET Runtime Bug Reports (Phase 52)

Drafts complete for three Mono JIT issues with minimal repros:
1. `!ji->async` JIT assertion crash when calling `swift_getExistentialTypeMetadata`
2. Non-blittable type support with `CallConvSwift` (feature request)
3. SafeHandle/SwiftSelf lifetime across async P/Invoke (tracking comment)

Filing deferred until repo goes public. Drafts in `src/docs/upstream-bug-reports-draft.md`.

---

## 19B. Protocol Witness Table Dispatch -- Phase B (Phase 53)

String marshalling and property setters through witness dispatch.

**`SBW_Utf8Slice` bridge struct** — `@frozen` Swift struct + `[StructLayout(Sequential)]` C# struct for ABI-stable UTF-8 transfer across the P/Invoke boundary.

**String property getters** — Swift accessor encodes `String` to `Array(result.utf8)`, allocates `SBW_Utf8Slice`, returns pointer. C# decodes via `Encoding.UTF8.GetString`. Free function deallocates both the buffer and the slice.

**String method returns/params** — Same `SBW_Utf8Slice` bridge. Parameters use `GCHandle.Alloc` pinning with exception-safe cleanup.

**Property setters** — Blittable setters use typed pointee assignment. String setters encode to UTF-8 via `fixed` block and pass `Utf8Slice` pointer. `_swiftContainer` field changed from `readonly` to mutable for write-back.

**Projected-type validation gates** — `IsSwiftStringProjectedType()` validates properties project to `Swift.SwiftString`. `IsIdiomaticStringType()` validates method params/returns project to `string`. Prevents dispatch when TypeDatabase is incomplete.

22 updated/new tests in `WitnessDispatchEmitterTests.cs`, 14 in `ProtocolProxyEmitterTests.cs`.

---

## 20. AnyTypeFallback Investigation (Phase 53)

Investigated and resolved — binding reports were stale (generated before Phases 49-52).

**BlinkID (10 → 0 AnyTypeFallback)**: All 10 skips were generic type parameter properties on `VehicleClassInfo<T>`, `DateResult<T>`, and `DriverLicenseDetailedInfo<T>`. Already fixed by Phase 50 generic improvements.

**Lottie (4 → 1 AnyTypeFallback)**: `Keyframe.value` fixed. `LottieButton.body`/`LottieSwitch.body` (`some View`) now handled by UnsupportedSignature path. `LottieAnimationLayer.animationLayer` remains — `Optional<QuartzCore.CALayer>` requires broader ObjC framework module support.

---

## 21. UnsupportedSignature Triage (Phase 53)

Investigation complete. All 30 skips categorized:

| Category | Count | Root Cause |
|----------|-------|------------|
| UIKit touch/event methods | 8 | UITouch, UIEvent, CGPoint references |
| Foundation.Bundle params | 8 | Bundle type not in TypeDatabase |
| Placeholder types | 5 | Unresolved generic/internal type params |
| Logger autoclosures | 4 | `@autoclosure () -> String`, `StaticString` |
| CALayer content gravity | 2 | QuartzCore type alias |
| UIControl.State / ClosedRange | 2 | Range types + UIKit |
| Other | 1 | Unresolved config type |

No low-cost fixable cluster — largest are UIKit (10) and Foundation.Bundle (8), both requiring structural TypeDatabase extensions.

---

## 8. BlinkID Runtime Validation (Phase 53)

15/18 tests pass on iOS Simulator. Test suites: Type Metadata (3/3), Enum Cases (3/4), Enum Raw Values (2/4), Enum FromRawValue (2/2), Static Properties (1/1), Extended Metadata (4/4).

3 failures are all non-blittable types in P/Invoke with Swift calling convention (SwiftString parameter/return on enum raw values). Known Mono JIT limitation. Addressable via `SBW_Utf8Slice` pattern from Phase B.

---

## 12B. Emitter Decomposition -- Phase B (Phase 53)

Partial class split completed:

| File | Before | After | Partial Files |
|------|--------|-------|---------------|
| `ClosureEmitter.cs` | ~1,220 LOC | 395 LOC | 5 files |
| `EnumHandler.cs` | ~1,680 LOC | 299 LOC | 5 files + extracted helper |
| `ProtocolProxyEmitter.cs` | 1,964 LOC | 106 LOC | 7 files |

Full handler extraction per `emitter-redesign-proposal.md` evaluated and deferred — current imperative architecture is functional and well-tested.

---

## 22. Static Protocol Member Fix (Phase 54)

Fixed protocol conformance compile errors (CS0736) where types emitted static properties but the interface required instance members.

**Root cause**: Swift protocols can have `static var` requirements, but C# interfaces cannot have static members. The generator was emitting static properties as instance members in interfaces while correctly emitting them as static on conforming types, causing a static/instance mismatch.

**Solution**: Skip static properties/subscripts when emitting protocol interfaces and all related proxy code, ensuring vtable layout consistency between C# and Swift sides.

**Files modified**:
- `ProtocolHandler.cs` — Skip static properties/subscripts in interface emission
- `ProtocolProxyEmitter.Receivers.cs` — Skip static members in receiver emission
- `ProtocolProxyEmitter.InterfaceImpl.cs` — Skip static members in interface implementation
- `ProtocolProxyEmitter.Vtables.cs` — Skip static members in vtable struct emission
- `ProtocolProxyEmitter.StaticInit.cs` — Skip static members in vtable initialization
- `WitnessDispatchEmitter.cs` — Skip static property P/Invoke emission
- `EveryProtocolEmitter.cs` — Skip static members in Swift vtable, updated `hasImplementableMembers` check
- `ProtocolProxyEmitter.cs` — Updated `hasImplementableMembers` to exclude static-only protocols
- `BindingReport.cs` — Added `StaticProtocolMember` skip reason

**Impact**:
- BlinkID: 18 → 0 compile errors ✅
- TestFramework: 93/93 must-pass features (no regression)
- Static member skips now properly recorded in binding report

**Note**: Nuke (~42 errors) and Lottie (~39 errors) have a different category of errors (CS0535 missing implementations, CS0738 return type mismatches, CS0111 duplicate definitions) that are unrelated to this fix — tracked separately as P1 in remaining-work.md.
