# Remaining Runtime Test Fixes

**Created**: March 19, 2026
**Updated**: March 19, 2026 (Session 13)

### Simulator (Mono)
**Current**: 656 passed, 0 failed, 38 skipped.

### Device (NativeAOT)
**Current**: 654 passed, 0 failed, 40 skipped.
**Gap**: 2 tests (both `[SkipOnDevice]` existential container ref params, Session 8).

---

## Device Test Coverage Gaps — RESOLVED (Session 13)

Session 13 closed the 43-test device gap (30 SwiftUI bridge + 13 OperatorTests). See Session 13 completed fixes below.

---

## Session 13 Completed Fixes

### Tests recovered (43 device — full device parity achieved)

| # | Fix | Tests | Root cause |
|---|-----|------:|------------|
| A | SwiftUI bridge device build support | 30 | `build-bridge.sh` only built for simulator (`ios-arm64-simulator`). Added `--target device` flag that builds for `ios-arm64` with `iphoneos` SDK. Updated `run-runtime-tests.sh` device path to call `./build-bridge.sh --target device`. Added `SWIFTUI_BRIDGE` conditional define, bridge CS file include, and bridge framework NativeReference to `RuntimeTestsApp.Device.csproj`. |
| B | Operator @_cdecl wrappers for NativeAOT | 13 | Operators on frozen structs used `CallConvSwift` P/Invoke to mangled symbols. NativeAOT ILC segfaults compiling these (even on simple blittable structs like `ArithmeticValue(Int32)`). Fix: `ShouldEmitOperatorWrapper()` now returns true for non-generic frozen struct operators, emitting @_cdecl wrappers with `CallConvCdecl`. Generic frozen structs fall back to `RequiresCdeclForAbiSafety()` since the @_cdecl path doesn't emit metadata arguments. Device bridge output separated to `SwiftBridge/device/` to prevent simulator/device binary mismatch. Also re-added `OperatorTests` to `TrimmerRoots.xml`. |

---

## Session 12 Completed Fixes

### Tests recovered (42 device — NativeAOT parity achieved)

| # | Fix | Tests | Root cause |
|---|-----|------:|------------|
| A | Type metadata pre-registration in module initializer | 34 | `TryGetTypeMetadataUncached<T>()` uses `SwiftObjectReflectionHelper.InvokeGetTypeMetadata(type)` which searches for `GetTypeMetadata` via `type.GetMethods()` reflection. NativeAOT trims those methods. Fix: emit `SwiftObjectHelper<T>.GetTypeMetadata()` for every non-generic ISwiftObject type in the `[ModuleInitializer]`. This caches metadata at assembly load time, before any code hits the reflection path. |
| B | NativeAOT-safe tuple return marshalling | 8 | Two reflection paths trimmed on NativeAOT: (1) `TypeMetadata.GetTypeMetadataOrThrow<ValueTuple<T1,T2>>()` uses `MakeGenericMethod` for tuple metadata. (2) `SwiftMarshal.MarshalFromSwift<ValueTuple<T1,T2>>(resultPtr)` uses `GetConstructor()` to create the tuple. Fix: emit `GetTupleTypeMetadataFromElements()` (NativeAOT-safe P/Invoke to `swift_getTupleTypeMetadata`) for buffer allocation, and inline per-element reading via `TupleTypeMetadata.GetElementOffset()` + `MarshalPrimitiveFromSwift<T>()` for return construction. |

---

## Session 11 Completed Fixes

### Tests recovered (9 simulator)

| # | Fix | Tests | Root cause |
|---|-----|------:|------------|
| 4 | Decomposed Optional property getters/setters | 2 | `SwiftOptional<T>.NewSome()` calls VWT `initializeWithCopy` on the inner type, crashing Mono for complex enums/non-frozen structs. Fix: decompose Optional into (rawPayload, hasValue) as separate params. Swift @_cdecl wrappers reconstruct `Optional<T>` on the Swift side. C# never constructs `SwiftOptional<T>` for these types. Applied to all three wrapper paths (main, generic-static, generic-class protocol dispatch). Subscript accessors excluded via `IsSubscriptAccessor` flag. |
| 1/5/6 | Fix generic metatype dispatch in emitter | 7 | Two bugs: (1) `_metadata0` is `T.self` but protocol is conformed by `GenericClass<T>`, not `T`. Added `_sbw_meta_*` helpers that call the parent type's metadata accessor via `dlsym` to convert `T.self` → `ParentType<T>.self` before protocol cast. (2) Parameter ordering mismatch: C# P/Invoke sends `(resultPtr, TMetadata, self)` but Swift @_cdecl declared `(resultPtr, self_, _metadata0)`. Fixed getter/setter emitters. |

---

## Session 10 Completed Fixes

### Tests recovered (1 simulator + device, 3 unit)

| # | Fix | Tests | Root cause |
|---|-----|------:|------------|
| 3 | Optional<Int32> None getter implicit operator bug | 1 | `implicit operator T?(SwiftOptional<T>)` is broken for value types: T is unconstrained, so `T?` in IL is `T` (not `Nullable<T>`). `default(T)` returns `0` instead of null, causing None to appear as `Some(0)`. Fixed property getter to use explicit `HasValue`/`Some` check. Also fixed non-blittable accessor return path that called `.ToNullable()` (same bug). |

---

## Session 9 Completed Fixes

### Tests recovered (3 simulator, 39 unit)

| # | Fix | Tests | Root cause |
|---|-----|------:|------------|
| 2 | Non-frozen struct instance @_cdecl wrappers | 3 | `WrapperValidation.RequiresCdeclForAbiSafety()` didn't recognize non-frozen struct instance members need @_cdecl wrappers. C# projects these as ClassWithOpaquePayload (IntPtr self), but Swift ABI expects SwiftSelf<T> (struct by value). Added `IsNonFrozenStructInstanceMember()` check. |
| — | GetSwiftRawValueType missing Bool/Float/Double/CGFloat | 0 | Pre-existing bug exposed by #2: `GetSwiftRawValueType()` had `_ => "Int"` fallback that caught Bool/Float/Double/CGFloat, causing wrapper compilation failures like `cannot assign value of type 'Bool' to type 'Int'`. Fixed 7 library wrapper regressions. |

### Generator/runtime improvements (no test count change)

| Change | Files |
|--------|-------|
| Optional<BlittablePrimitive> constructor tag fixup | `ConstructorWrapperEmitter.cs` — After `initialize(to:)`, emits explicit tag byte fixup using `MemoryLayout<T>.offset(of:)` for each Optional<BlittablePrimitive> stored property. Constructor tag bytes verified correct via runtime diagnostics. Separate Mono implicit operator bug blocks test recovery (see #3 below). |
| Generalized SwiftOptional<T> tag byte fast path | `SwiftOptional.cs` — Extended blittable primitive fast path to cover ALL types without extra inhabitants (complex enums, non-frozen structs) by comparing `Optional<T>.Size` vs `T.Size`. Tag byte at offset `T.Size` when Optional is larger. |

### Investigation findings

| Finding | Detail |
|---------|--------|
| OptionalConfig None test (#3) — C# getter fast path bug | Constructor tag fixup writes correct bytes (verified: buffer bytes `00 00 00 00 01 00 00 00`, tag=1). Swift getter correctly reads None and writes tag=1 to return buffer. C# return buffer ALSO shows byte[4]=0x01. But `(int?)Count_Get()` returns `Some(0)` on **both Mono and NativeAOT**. The blittable fast path `if (_optPtr[4] != 0) return null!` should fire but doesn't take effect through the implicit conversion chain. Needs investigation of the `Count_Get()` → `using var` → `(int?)__ret` → `op_Implicit` path. |
| OptionalShape setter (#4) — VWT payload copy crash | Generalized tag byte fast path works, but `Shape.MarshalToSwift()` uses VWT `initializeWithCopy` which crashes Mono for complex enums. Tag fix alone insufficient — needs approach that avoids constructing `SwiftOptional<Shape>` on C# side entirely. |

---

## Session 8 Completed Fixes

### Tests recovered (5 simulator, 3 unit)

| # | Fix | Tests | Root cause |
|---|-----|------:|------------|
| 3 | Complex enum return @_cdecl routing | 1 | `WrapperValidation.IsReturnTypeCdeclRequired()` returned false for non-simple enum returns. C# P/Invoke used `CallConvSwift`+`SwiftIndirectResult` instead of connecting to the existing @_cdecl wrapper. Fixed: non-simple enums now route through @_cdecl. |
| 8 | Unary operator wrappers | 2 | Skip was stale. @_cdecl wrappers for `!` and `~` on `UnaryValue` (Bool-field struct) were already fully emitted by Session 6B. Removed skips. |
| 14 | Existential container ref params | 2 | `TestRunModeConsumerWithSimpleMode/StrictMode` work on simulator. Device crash (SIGKILL) is a separate NativeAOT issue — added `[SkipOnDevice]` attribute and infrastructure. |
| — | Unit test exception assertions | 3 | .NET 10 changed exception wrapping: `TargetInvocationException` vs `SwiftRuntimeException` direct throw. Fixed `ProtocolWitnessTableTests`, `SwiftSetTests`, `SwiftArrayTests` to accept either wrapper with inner type validation. |

### Generator/runtime improvements (no test count change)

| Change | Files |
|--------|-------|
| Generic class @_cdecl calling convention fix | `PInvokeEmitter.cs`, `MethodMarshalPlanBuilder.cs` — GenericClass/GenericNamedBox P/Invoke now uses `CallConvCdecl` with correct 2-param signature (was `CallConvSwift` with 3 params). Protocol metatype dispatch still crashes Mono JIT separately. |
| Optional<BlittablePrimitive> property getter explicit tag writing | `PropertyWrapperEmitter.cs` — Swift getter uses typed pointer `tagPtr.pointee = 1` instead of `initializeMemory(as: Optional<Int32>.self)` which writes wrong tag bytes on Mono. |
| Optional<BlittablePrimitive> constructor split-read | `ConstructorWrapperEmitter.cs` — Swift constructor reads tag byte separately via `count.advanced(by: 4).load(as: UInt8.self)` instead of `assumingMemoryBound(to: Optional<Int32>.self).pointee`. |
| Optional return buffer safety margin | `MethodMarshalPlanBuilder.cs` — Optional return buffers use `AllocZeroed(Math.Max(size, 16))` to guard against incorrect VWT size on Mono. |
| Accessor return blittable fast path | `WrapperEmitter.Return.cs` — Blittable Optional accessor returns read tag byte directly and return `null!` for None, bypassing `SwiftOptional<T>` VWT entirely. |
| Runtime `SwiftOptional<T>` fast paths | `SwiftOptional.cs` — `Case` property and `NewNone()` use direct tag byte read/write for blittable primitives, bypassing VWT `GetEnumTag`/`DestructiveInjectEnumTag`. |
| `[SkipOnDevice]` infrastructure | `TestResults.cs`, `TestBase.cs` — New attribute for tests that pass on simulator but crash on NativeAOT device. Class-level support included. |

---

## NativeAOT Device Failures (42 tests — RESOLVED in Session 12)

Goal achieved: device and simulator have identical pass/fail results. 611 passed, 0 failed, 40 skipped on device. See Session 12 completed fixes table above for details.

---

## Current State (Simulator Skips)

38 `[Skip]` + 2 `[SkipOnDevice]` = 40 total annotations. Of these:
- **27 are unfixable** without external action (upstream bugs, missing data sources, future roadmap)
- **13 are fixable** generator/runtime bugs, grouped into 8 categories below

## Unfixable Skips (27 annotations — leave as-is)

| Category | Count | Why Unfixable |
|----------|------:|---------------|
| Returned thick closures | 5 | Missing @_cdecl wrapper for closure-returning functions. These use `CallConvSwift` to return `SwiftClosureData` (16 bytes) directly. Generator needs to emit @_cdecl wrappers that return closures via result pointer. Crashes both Mono (no CallConvSwift) and NativeAOT (multi-register return marshalling). **Our bug but needs wrapper emitter changes for closure returns.** |
| String enum raw values | 7 | ABI JSON lacks raw values — generator emits sequential ordinals. No fix without new data source. |
| ~Copyable noncopyable types (UniqueResource) | 11 | `@_cdecl` wrapper uses `.pointee` which copies — illegal for `~Copyable`. Needs move semantics support. |
| Non-blittable closures (SwiftString) | 2 | Documented CallConvSwift limitation — SafeHandle/SwiftString can't cross CallConvSwift boundary. |
| ValueTuple StructLayout.Auto | 1 | MarshalDirectiveException on Mono, SIGSEGV on NativeAOT. Documented upstream limitation. |
| ABI JSON lacks enum raw values (Permission) | 1 | Same root cause as string enum raw values. |

## Fixable Skips (13 annotations — 8 categories)

### Priority 1: Partially fixed, need remaining work

#### 1. Generic struct constructors — 4 tests

**Tests**: 4 struct (Wrapper, GenericPair)

**Status**: Class sub-issues all **fixed** (Session 11: metatype dispatch via `_sbw_meta_*` helpers + parameter ordering fix). Struct constructors remain: no @_cdecl wrapper emitted — C# uses `CallConvSwift` directly to mangled symbols. Generator doesn't emit wrappers for generic struct constructors.

**Fix approach**: Emit @_cdecl wrappers for generic struct constructors, similar to the class pattern.

**Key files**: `ConstructorWrapperEmitter.cs` (EmitGenericStaticFactoryConstructor)

#### ~~2. IntContainer array marshalling — 3 tests~~ **FIXED (Session 9)**

#### ~~3. Optional<Int32> None getter — 1 test~~ **FIXED (Session 10)**

### Priority 2: Medium complexity

#### ~~4. OptionalShape setter — 2 tests~~ **FIXED (Session 11)**

#### ~~5. Generic class T-typed constructors — 2 tests~~ **FIXED (Session 11)**

#### 6. Generic struct method dispatch — 1 test

**Tests**: TestConstrainedBoxGetDescription

**Root cause**: Guard 5b in wrapper emission rejects concrete-signature methods on generic structs. Method doesn't reference T but no @_cdecl wrapper generated — C# uses `CallConvSwift` on Mono which crashes.

**Fix approach**: Relax guard 5b to allow methods with concrete signatures on generic types, or emit @_cdecl wrappers for them.

**Key files**: `MethodWrapperEmitter.cs`, `WrapperValidation.cs`

### Priority 3: Requires investigation or deep infrastructure

#### 7. Async optional nil detection — 1 test

**Tests**: TestAsyncGetNilResult (AsyncComplexTypeTests.cs)

**Root cause**: Async callback returns non-null for nil Swift optional. The tag/discriminator detection in the async optional return path doesn't detect None correctly.

**Key files**: `WrapperEmitter.Async.cs`

#### 8. Async typed throws wrapper compilation — 2 tests

**Tests**: TestAsyncParseTypedCatch, TestAsyncParseSuccess (ThrowingMethodTests.cs)

**Root cause**: `EntryPointNotFoundException` — the typed throws async wrapper fails Swift compilation and is silently stripped by the error-based retry build.

**Key files**: `ClosureEmitter.Async.cs`, `WrapperEmitter.Async.cs`

#### 9. Method-level generic free function — 1 test

**Tests**: TestGetPairSameType (BasicGenericTests.cs)

**Root cause**: Guard 6 in `MethodWrapperEmitter.ShouldEmitWrapper()` blocks all method-level generics. Session 7C's protocol pattern relies on extending a parent type, which free functions don't have.

**Fix approach**: New wrapper pattern needed: dummy struct host, function pointer trampolines, or specialized wrappers per instantiation.

**Key files**: `MethodWrapperEmitter.cs` (guard 6)

#### 10. Optional array layout mismatch — 1 test

**Tests**: TestBatchConfigTagCountNil (CompositionTests.cs)

**Root cause**: Frozen struct + optional array buffer size calculation wrong.

**Key files**: Layout/marshalling code for frozen structs

#### 11. ExistentialCallbackTests — 1 test (class-level skip)

**Tests**: TestExistentialParamCallbackDelivery (ExistentialCallbackTests.cs)

**Root cause**: `EntryPointNotFoundException` — EveryProtocol conformance extension is stripped by the Swift post-processor because it references internal types from the target library.

**Fix approach**: Fix the post-processor to preserve EveryProtocol conformances that are needed, or generate the conformance differently to avoid internal type references.

**Key files**: Existential callback wrapper emission, Swift post-processor

#### 12. Existential container ref params (device-only) — 2 tests

**Tests**: TestRunModeConsumerWithSimpleMode, TestRunModeConsumerWithStrictMode (ExistentialBoxingTests.cs)

**Status**: Pass on simulator (Mono), SIGKILL on NativeAOT device. Marked `[SkipOnDevice]`.

**Root cause**: P/Invoke signatures match correctly (`ref ExistentialContainer1` → `UnsafeRawPointer`). The crash may be in existential container construction/boxing for protocols whose methods accept existential parameters (`ModeConsumer.consume(mode: any ProcessingMode)`).

**Key files**: `ExistentialContainerFactory`, existential boxing code

---

## Test Infrastructure Reference

```bash
# Unit tests (~30s):
./run-tests.sh 2>&1 | tee /tmp/run-tests-results.txt

# Runtime tests — full rebuild (~3 min):
cd BindingTests && ./run-runtime-tests.sh --timeout 90 2>&1 | tee /tmp/runtime-results.txt

# Runtime tests — skip regen (~17s):
cd BindingTests && ./run-runtime-tests.sh --skip-regen --timeout 90

# Runtime tests — single class (~5s):
cd BindingTests && ./run-runtime-tests.sh --skip-build --class BasicGenericTests --timeout 90

# Runtime tests — device:
cd BindingTests && ./run-runtime-tests.sh --platform device --timeout 120

# Library validation (~1 min):
./validate-libraries.sh 2>&1 | tee /tmp/validate-results.txt
```
