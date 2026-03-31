# Completed Roadmap: Runtime Test & Validation Sessions (March 2026)

**Archived**: March 30, 2026
**Source**: Moved from `roadmap.md` — 13 sessions fully complete with no remaining action items.

**Baseline at start**: 89/90 CS compile, 55/56 Swift compile. 52 skipped runtime tests in BindingTests.
**Baseline at end**: 89/90 CS compile, 55/56 Swift compile. All targeted skipped tests resolved (60+ runtime tests unskipped, 3 validation sessions completed).

---

## BindingTests Runtime Test Sessions (1-10)

### Session 1: Closure & Callback Fixes

**Result**: 7/7 skipped tests now passing on simulator. Zero validation regressions.

| Fix | Tests | What Changed |
|-----|------:|-------------|
| Frozen struct/enum indirect return | 3 | `ClosureEdgeCaseTests`: TestClosureReturningFrozenPoint, TestClosureReturningEnum, TestClosureReturningFrozenPointWithParam — moved from direct `@convention(c)` return to indirect buffer-based return; replaced `unsafeBitCast` with safe `.rawValue`/pointer-load for enums |
| Throwing closure String return | 1 | `ClosureEdgeCaseTests`: TestThrowingWithParamSuccess — added SwiftString intermediary for callback String returns (System.String has no Swift metadata); made SwiftResult payload lazy to avoid metadata crash on type load |
| MCB complex enum ownership | 3 | `StructClosureBridgeTests`: TestDataTransformerProcess, TestDataTransformerProcessNegativeFactor, TestClassTransformerProcess — removed `defer` deallocation for heap-allocated complex enum args (C# takes ownership via NewFromPayload) |

Additional fixes from code review: ARC leak in buffer return paths (`load(as:)` → `move()`), string-backed enum closure ABI guard, SwiftResult C#-only interop guards.

### Session 1.5: Validation Regression Fix

**Result**: 89/90 CS compile, 55/56 Swift compile restored. All 5 regressions fixed, zero new regressions.

| Fix | Libraries | What Changed |
|-----|----------|-------------|
| Async closure Data ABI type | Nuke (ios/macos/tvos) | `AsyncThrowingClosureState<T>` used projected `byte[]` instead of ABI `Swift.Data`; used `TypeProjectionFactory.PInvokeType` for state type + added `byte[]` → `Swift.Data` conversion wrapper |
| Closure enum `.rawValue` cast | StripePayments | Session 1 changed `unsafeBitCast` to `.rawValue` but `Int`-backed enums return `Swift.Int` not `Int64`; wrapped in explicit scalar cast `Int64(arg.rawValue)` in `ClosureEmitter.SwiftWrapper` + `NestedClosureBridge` |
| DynamicSelf guard depth | StripePayments | `hasDynamicSelfReturn` only checked top-level `IsDynamicSelf`, missing `Optional<Self>`; switched to `TypeSpec.HasDynamicSelf` which covers all nested shapes |
| @autoclosure invocation | SwiftyBeaver | `OptionalPointerWrapperEmitter` passed `@autoclosure () -> Any` closure directly where `Any` expected; added `()` invocation suffix matching `MethodWrapperEmitter` pattern |

### Session 2: Optional & Metadata Fixes (d511fe4)

**Result**: 8/8 skipped tests now passing on simulator. Zero validation regressions. 1204 pass / 0 fail (up from 1201/3).

| Fix | Tests | What Changed |
|-----|------:|-------------|
| SwiftOptional\<T\> metadata for simple enums | 5 | Generator emits @_cdecl metadata wrappers for simple enums, registered via module initializer. Runtime fast paths handle C#↔Swift size mismatch (C# `enum : int` = 4 bytes, Swift enum = 1 byte) |
| Optional Bool extra-inhabitant encoding | 1 | Constructor memcpy fast path for Bool bypasses VWT InitializeWithCopy which corrupts extra-inhabitant encoding on Mono |
| Optional\<T\> return marshalling for value types | 2 | Fixed `ToNullable()` bug: in C# generics, `T?` with unconstrained T is `T` (not `Nullable<T>`). Generator now emits explicit `HasValue`/`Some` check with nullable cast |

### Session 3: Protocol & Existential Wrapper Fixes (fe50a00)

**Result**: 7/9 skipped tests now passing on simulator. 2 remaining tests (RouteEvent/GetDelegateName) moved to `[SkipOnSimulator]` — string callback vtable issue, Session 7 territory. +2 Swift validation improvements (ObjectMapper, Parchment). 1211 pass / 0 fail.

| Fix | Tests | What Changed |
|-----|------:|-------------|
| Protocol closure wrapper stripping | 5 | Added EventDelegate to SwiftSourceStripper PreservedProtocols so witness table isn't cascade-stripped |
| Existential `any` keyword in @_cdecl wrappers | 2 | CdeclParamMapper now emits `any` prefix for protocol existentials in Swift 6 output, with correct `Optional<any Protocol>` handling |
| String vtable callbacks (deferred) | 2 | RouteEvent/GetDelegateName hit Mono JIT async assertion via string callback through vtable — deferred to Session 7 |

### Session 4: SwiftString.Buffer ABI Decomposition (c7670e2)

**Result**: 2/2 skipped tests now passing. Zero validation regressions despite high-risk ABI change. 1213 pass / 0 fail.

| Fix | Tests | What Changed |
|-----|------:|-------------|
| SwiftString.Buffer ABI (4+ string params) | 1 | Decomposed 16-byte Buffer struct into two `nint` P/Invoke parameters (_w0, _w1), matching Swift's two-Int-word layout. Fixes ARM64 AAPCS64 register overflow with 4+ string params |
| String enum raw values | 1 | CSSProperty enum cases now round-trip correctly via string raw value support |

### Session 5: Variadic, ObjC & Cross-Module (af8518f)

**Result**: 8/8 skipped tests now passing. Zero validation regressions. 1221 pass / 0 fail.

| Fix | Tests | What Changed |
|-----|------:|-------------|
| Variadic parameter support | 3 | Removed MemberValidationPipeline gate — `T...` is `Array<T>` at ABI level, CallConvSwift dispatches correctly via `SwiftArray<T>` |
| ObjC Selector type | 3 | Added Selector/ObjCBool to FoundationDatabase.xml (TypeSpecParser rewrites `ObjectiveC.*` → `Foundation.*`) |
| URL protocol bridge | 1 | EveryProtocolEmitter converts URL→AnyObject for vtable params/returns so C# receives NSURL pointer |
| Cross-module closure | 1 | Wrapper already generated correctly, removed stale `[Skip]` |

### Session 6: Opaque Types & Protocol Conformance (pending commit)

**Result**: 2/2 skipped tests now passing on simulator. Zero validation regressions. 1223 pass / 0 fail.

| Fix | Tests | What Changed |
|-----|------:|-------------|
| Opaque return type marshalling | 1 | `WrapperStrippingTests`: TestMixedEmittabilityOpaqueReturn — the @_cdecl wrapper already correctly boxes `some CustomStringConvertible` to `any Protocol` existential container; C# reads `ExistentialContainer1` and returns as `object`. Skip was premature — removed |
| Protocol conformance via generic dispatch | 1 | `LifetimeTrackingTests`: TestOwnableProtocolConformance — `some Ownable` is ABI sugar for `<T: Ownable>`; generated C# generic with `CallConvSwift` + type metadata + protocol witness table dispatch works correctly. Filled in empty test body |

### Session 7: String Callback Fixes (f8f04b7)

**Result**: 6/4 skipped tests now passing (4 target + 2 deferred from Session 3). Zero validation regressions. 1229 pass / 0 fail / 31 skip.

| Fix | Tests | What Changed |
|-----|------:|-------------|
| Optional/Array String closure indirect return | 2 | `SwiftOptional<SwiftString>`/`SwiftArray<SwiftString>` marshalling instead of `TypeMetadata.GetTypeMetadataOrThrow<string?>` which fails (System.String has no Swift metadata) |
| Closure callback String params | 2 | `MarshalFromSwift<SwiftString>().ToString()` instead of `MarshalFromSwift<string>` which fails |
| Protocol receiver String params | 2 | Runtime `SwiftMarshal.MarshalFromSwift` for String params instead of `Unsafe.Read` which can't construct managed types from raw memory (also fixed Session 3 deferred tests) |

### Session 8: Noncopyable Type Wrapper Generation (9fec151)

**Result**: 11/11 skipped tests now passing. Zero validation regressions. 1241 pass / 0 fail.

| Fix | Tests | What Changed |
|-----|------:|-------------|
| Noncopyable inline borrow semantics | 11 | Replaced `.pointee` copy with inline `assumingMemoryBound(to:).pointee` borrow for self, params, properties, subscripts, and returns. Removed noncopyable rejection gates from `WrapperValidation`, `SelfReconstructionEmitter`, `CdeclParamMapper`. Both non-throwing and throwing return paths fixed. |

### Session 9: Async Device Crash Investigation (c37400bb)

**Result**: 9/9 `[SkipOnDevice]` tests unskipped. Confirmed as generator bug, not upstream. Zero validation regressions. 1241 pass / 0 fail (device tests need on-device verification).

| Fix | Tests | What Changed |
|-----|------:|-------------|
| Async optional class return marshalling | 9 | Async wrapper missing conditional retain + null-check for optional class returns. NativeAOT SIGBUS was caused by retaining nil pointer. Not upstream issue #5. |

### Session 10: Generic Edge Cases (095e7fb6)

**Result**: 2/3 skipped tests fixed. 1 confirmed upstream (issue #4). Zero validation regressions.

| Fix | Tests | What Changed |
|-----|------:|-------------|
| Bound generic struct indirect result | 1 | Non-frozen bound generic struct returns need @_cdecl wrapper with resultPtr (indirect result). NativeThunkEmitter now rejects non-frozen bound generic returns, PInvokeEmitter falls through to IndirectResult path |
| Unbound type parameter resolution | 1 | TestMakePairDescriptionSkipped — generator fix for unbound type params |
| Method-level generic (upstream #4) | 1 | TestGetPairSameType confirmed upstream: crashes both Mono+NativeAOT with 2+ type metadata params. Properly categorized as upstream issue #4 |

---

## Validation Coverage Expansion (Sessions 11-13)

These sessions targeted skip reasons across the 90 validation libraries.

### Session 11: Async Properties (00d8587a)

**Result**: Async property getters now emit as Task-returning C# methods (e.g., `GetImageAsync()`). Routes through existing AsyncProjection/MethodHandler pipeline. 8+ validation members unskipped. Generic types gated via CS8895.

### Session 12: ObjC-Bridged Optional Setters (d407d423)

**Result**: `IsOptionalWithReferenceInner` now covers ObjC-bridged structs/enums (excluding NSString typedefs). Enables nullable pointer ABI for Optional property setters via `Unmanaged<AnyObject>` bridge. Removed `objc_bridged_struct_optional_setter` guard.

### Session 13: inout Parameters (25a91e10)

**Result**: Inout parameter support via `UnsafeMutableRawPointer` write-back in @_cdecl wrappers. Type-safe ABI guards for String (2-word), class (Unmanaged), and non-frozen types. 8 new tests.
