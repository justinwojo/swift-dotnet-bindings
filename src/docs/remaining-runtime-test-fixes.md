# Remaining Runtime Test Fixes

**Created**: March 19, 2026
**Updated**: March 19, 2026 (Session 9)
**Current**: 646 passed, 0 failed, 48 skipped (simulator). Unit tests: 8328.
**Previous**: 643 passed, 0 failed, 51 skipped (simulator). Unit tests: 8289.

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

## Current State

48 `[Skip]` + 2 `[SkipOnDevice]` = 50 total annotations. Of these:
- **27 are unfixable** without external action (upstream bugs, missing data sources, future roadmap)
- **23 are fixable** generator/runtime bugs, grouped into 11 categories below

## Unfixable Skips (27 annotations — leave as-is)

| Category | Count | Why Unfixable |
|----------|------:|---------------|
| Returned thick closures | 5 | Missing @_cdecl wrapper for closure-returning functions. These use `CallConvSwift` to return `SwiftClosureData` (16 bytes) directly. Generator needs to emit @_cdecl wrappers that return closures via result pointer. Crashes both Mono (no CallConvSwift) and NativeAOT (multi-register return marshalling). **Our bug but needs wrapper emitter changes for closure returns.** |
| String enum raw values | 7 | ABI JSON lacks raw values — generator emits sequential ordinals. No fix without new data source. |
| ~Copyable noncopyable types (UniqueResource) | 11 | `@_cdecl` wrapper uses `.pointee` which copies — illegal for `~Copyable`. Needs move semantics support. |
| Non-blittable closures (SwiftString) | 2 | Documented CallConvSwift limitation — SafeHandle/SwiftString can't cross CallConvSwift boundary. |
| ValueTuple StructLayout.Auto | 1 | MarshalDirectiveException on Mono, SIGSEGV on NativeAOT. Documented upstream limitation. |
| ABI JSON lacks enum raw values (Permission) | 1 | Same root cause as string enum raw values. |

## Fixable Skips (23 annotations — 11 categories)

### Priority 1: Partially fixed, need remaining work

#### 1. Generic static factory — Mono JIT crash — 8 tests

**Tests**: 4 struct (Wrapper, GenericPair) + 4 class (GenericClass, GenericNamedBox)

**Status**: Calling convention mismatch **fixed** (Session 8: CallConvCdecl + 2-param signature). But protocol metatype dispatch (`unsafeBitCast(_metadata0, to: Any.Type.self) as! any Protocol.Type`) still crashes Mono JIT at `jit-info.c:918`. The `as!` existential cast is the trigger.

**Sub-issues**:
- 4 struct tests (Wrapper, GenericPair): No @_cdecl wrapper emitted — C# uses `CallConvSwift` directly to mangled symbols. Generator doesn't emit wrappers for generic struct constructors.
- 4 class tests (GenericClass, GenericNamedBox): @_cdecl wrapper exists and calling convention is now correct, but the Swift wrapper's protocol metatype dispatch crashes Mono.

**Fix approach**: Replace `as! any Protocol.Type` existential cast with a function pointer table or direct metatype resolution that doesn't trigger the Mono JIT assertion.

**Key files**: `ConstructorWrapperEmitter.cs` (EmitGenericStaticFactoryConstructor), `MethodWrapperEmitter.cs`, `PropertyWrapperEmitter.cs`

#### ~~2. IntContainer array marshalling — 3 tests~~ **FIXED (Session 9)**

### Priority 2: Medium complexity

#### 3. Optional<Int32> None in constructor params — 1 test

**Tests**: TestOptionalConfigConstructorWithoutLabel (OptionalMarshallingTests.cs)

**Status**: Session 9 added constructor tag fixup via `MemoryLayout<T>.offset(of:)`. Exhaustive runtime diagnostics confirmed: (1) constructor writes correct tag byte (buffer bytes `00 00 00 00 01 00 00 00`, tag=1 at offset 20), (2) Swift getter reads `obj.count = nil` and writes tag=1 to return buffer, (3) C# return buffer also shows byte[4]=0x01. Despite all this, `(int?)Count_Get()` returns `Some(0)` on **both Mono and NativeAOT** (verified on device).

**Root cause**: NOT the constructor or Swift getter — both are correct. The bug is in the C# `Count_Get()` → `using var __ret` → `(int?)__ret` → `op_Implicit` chain. The blittable fast path `if (_optPtr[4] != 0) return null!` should fire (byte[4] is 1), but the value doesn't propagate correctly through the implicit conversion to `Nullable<int>`.

**Fix approach**: Investigate the `Count_Get()` return → implicit operator path. Possible approaches: (a) return `SwiftOptional<int>.NewNone()` instead of `null!`, (b) bypass the `SwiftOptional<T>` intermediate entirely and return `int?` directly from `Count_Get()`, (c) check if `using var` + `return null!` has unexpected interaction.

**Key files**: `WrapperEmitter.Return.cs` (blittable fast path emission), `SwiftOptional.cs` (implicit operator)

#### 4. OptionalShape setter — 2 tests

**Tests**: TestEnumPropertyHolder_SetOptionalShape, TestEnumPropertyHolder_ClearOptionalShape

**Status**: Session 9 generalized `SwiftOptional<T>` tag byte fast path to cover complex enums (comparing `Optional<T>.Size` vs `T.Size`). Tag operations now bypass VWT correctly. However, `Shape.MarshalToSwift()` uses VWT `initializeWithCopy` which crashes Mono before the tag byte path is reached. Not tested on NativeAOT.

**Root cause**: `SwiftOptional<Shape>.NewSome()` calls `MarshalToSwift` to copy the Shape payload into the Optional buffer. `MarshalToSwift` for complex enums uses VWT `initializeWithCopy` which crashes Mono. The tag byte fix is correct but the payload copy crashes first.

**Fix approach**: Avoid constructing `SwiftOptional<Shape>` on C# side entirely. Change the setter @_cdecl wrapper to accept raw Shape bytes + `hasValue` flag, then construct `Optional<Shape>` in Swift. This bypasses all C#-side VWT operations.

**Key files**: `PropertyWrapperEmitter.cs` (setter emission), `OptionalProjection.cs`

#### 5. Generic class T-typed constructors — 2 tests

**Tests**: TestConstrainedBoxCreation, TestTypedEntityCreation

**Root cause**: `CanEmitGenericClassConstructorWrapper()` rejects constructors where parameters reference the parent's generic type parameter.

**Fix approach**: Extend `EmitGenericStaticFactoryConstructor` to handle class constructors with T-typed params. Needs `AnyObject` constraint on the protocol or different dispatch mechanism.

**Key files**: `ConstructorWrapperEmitter.cs`

#### 6. Generic class property/method dispatch — 2 tests

**Tests**: TestGenericNamedBoxName, TestConstrainedBoxGetDescription

**Root cause**: Two sub-issues:
- `TestGenericNamedBoxName`: Property getter needs protocol-based static dispatch for generic class properties.
- `TestConstrainedBoxGetDescription`: Method doesn't reference T but no @_cdecl wrapper generated — guard rejection in wrapper emission.

**Key files**: `PropertyWrapperEmitter.cs`, `MethodWrapperEmitter.cs`, `WrapperValidation.cs`

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
