# Remaining Runtime Test Fixes

**Created**: March 19, 2026
**Baseline**: 638 passed, 0 failed, 56 skipped (simulator). Unit tests: 7921.
**Previous work**: Sessions 1-7 in `nativeaot-callconvswift-sessions.md` (historical reference only).

---

## Current State

56 skip annotations remain (43 `[Skip]` + 13 `[SkipOnSimulator]`). Of these:
- **26 are unfixable** without external action (upstream bugs, missing data sources, future roadmap)
- **30 are fixable** generator/runtime bugs, grouped into 14 categories below

## Unfixable Skips (26 annotations — leave as-is)

| Category | Count | Why Unfixable |
|----------|------:|---------------|
| Returned thick closures `[SkipOnSimulator]` | 5 | Upstream Mono bug: CallConvSwift 16-byte struct return ABI. Confirmed with standalone repro. |
| String enum raw values | 7 | ABI JSON lacks raw values — generator emits sequential ordinals. No fix without new data source. |
| ~Copyable noncopyable types (UniqueResource) | 11 | `@_cdecl` wrapper uses `.pointee` which copies — illegal for `~Copyable`. Needs move semantics support. |
| Non-blittable closures (SwiftString) | 2 | Documented CallConvSwift limitation — SafeHandle/SwiftString can't cross CallConvSwift boundary. |
| ValueTuple StructLayout.Auto | 1 | MarshalDirectiveException on Mono, SIGSEGV on NativeAOT. Documented upstream limitation. |

## Fixable Skips (30 annotations — 14 categories)

### Priority 1: High-impact, clear fix approach

#### 1. Generic static factory on Mono `[SkipOnSimulator]` — 8 tests

**Tests**: TestWrapperCreation, TestWrapperUnwrap, TestGenericPairCreation, TestGenericPairMixedTypes, TestGenericClassCreation, TestGenericClassGetMethod, TestGenericClassValueSetter, TestGenericNamedBoxCreation (BasicGenericTests.cs)

**Root cause**: Session 7C's protocol-based static factory pattern compiles correctly but crashes Mono's JIT at `jit-info.c:918` when the metatype dispatch P/Invoke is compiled. The wrappers work on NativeAOT device.

**Fix approach**: Investigate what specific aspect of the protocol metatype dispatch triggers the Mono JIT assertion. The pattern uses `unsafeBitCast(_metadata0, to: Any.Type.self) as! any Protocol.Type` — the `as!` existential cast may be the trigger. Alternative: use a function pointer table instead of protocol metatype dispatch for Mono compatibility.

**Key files**: `ConstructorWrapperEmitter.cs` (lines 1316-1550), `MethodWrapperEmitter.cs` (lines 526-800), `PropertyWrapperEmitter.cs` (lines 766-1010)

#### 2. IntContainer array marshalling — 3 tests

**Tests**: TestIntContainerCreation, TestIntContainerElementAt, TestIntContainerEmpty (BasicGenericTests.cs)

**Root cause**: Constructor wrapper passes the array buffer contents (element pointer) instead of the full Swift Array struct (which includes count + capacity + storage pointer). `Count` returns 0 because the count field is garbage.

**Fix approach**: Fix constructor wrapper array parameter marshalling to pass the full Array struct, not just the element buffer. Look at how `SwiftArray` is passed in non-constructor contexts (method params work fine).

**Key files**: `ConstructorWrapperEmitter.cs`, `WrapperEmitter.Marshalling.cs`

#### 3. SHA2Variant ABI size mismatch — 1 test

**Tests**: TestCreateHashAlgorithm (NestedEnumTests.cs)

**Root cause**: `SHA2Variant` is backed by Swift `Int` (8 bytes on 64-bit) but the C# enum uses `int` (4 bytes). When passed through CallConvSwift, the ABI size mismatch causes SIGSEGV.

**Fix approach**: For enums with `Int`/`UInt` raw value types, use `nint`/`nuint` as the C# backing type instead of `int`/`uint`. This affects enum declaration emission, not the wrapper.

**Key files**: Enum type emission in the generator (look for where `RawValueTypeName` maps to C# types)

#### 4. Optional<Int32> None in constructor params — 1 test

**Tests**: TestOptionalConfigConstructorWithoutLabel (OptionalMarshallingTests.cs)

**Root cause**: Session 7E's blittable primitive fast path fixes Optional<Int32> for return values but not constructor parameter marshalling. None still reads as Some in the constructor param path.

**Fix approach**: Extend the 7E-1 blittable fast path from `OptionalProjection.GetReturnPlan()` to also cover parameter marshalling in constructor wrappers.

**Key files**: `OptionalProjection.cs`, `ConstructorWrapperEmitter.cs`

### Priority 2: Medium complexity, proven patterns available

#### 5. Generic class T-typed constructors — 2 tests

**Tests**: TestConstrainedBoxCreation, TestTypedEntityCreation (BasicGenericTests.cs)

**Root cause**: `CanEmitGenericClassConstructorWrapper()` rejects constructors where parameters reference the parent's generic type parameter. The existing protocol factory pattern (Session 7C) handles generic struct constructors and generic class constructors where the class provides `AnyObject` constraint, but these have T-typed params that need UnsafeRawPointer erasure.

**Fix approach**: Extend 7C's `EmitGenericStaticFactoryConstructor` to handle class constructors with T-typed params. The pattern is the same (protocol + extension + metatype dispatch) but needs `AnyObject` constraint on the protocol or a different dispatch mechanism.

**Key files**: `ConstructorWrapperEmitter.cs` (`CanEmitGenericStaticFactoryWrapper`, `EmitGenericStaticFactoryConstructor`)

#### 6. Generic class property/method dispatch — 2 tests

**Tests**: TestGenericNamedBoxName, TestConstrainedBoxGetDescription (BasicGenericTests.cs)

**Root cause**: Two sub-issues:
- `TestGenericNamedBoxName`: `GenericNamedBox<T>.Name` property getter needs protocol-based static dispatch (same as 7C pattern but for non-final class property getters)
- `TestConstrainedBoxGetDescription`: Method doesn't reference T but no @_cdecl wrapper generated — unexplained gap in wrapper emission logic

**Fix approach**: For `Name`: extend 7C's `EmitGenericStaticDispatchPropertyGetter` to handle generic class properties. For `getDescription`: investigate why `ShouldEmitWrapper() && RequiresCdeclForAbiSafety()` doesn't produce a wrapper — likely a guard rejection.

**Key files**: `PropertyWrapperEmitter.cs`, `MethodWrapperEmitter.cs`, `WrapperValidation.cs`

#### 7. OptionalShape setter — 2 tests

**Tests**: TestEnumPropertyHolder_SetOptionalShape, TestEnumPropertyHolder_ClearOptionalShape (EnumMarshallingTests.cs)

**Root cause**: `SwiftOptional<Shape>` generic metadata crashes CallConvSwift on both runtimes. The property setter needs the optional enum to be marshalled through a @_cdecl wrapper, but the current setter emission doesn't handle `Optional<ComplexEnum>` correctly.

**Fix approach**: Fix property setter emission for `Optional<ComplexEnum>` types. May need to pass the optional through UnsafeRawPointer in the @_cdecl wrapper.

**Key files**: `PropertyWrapperEmitter.cs`, `WrapperEmitter.Marshalling.cs`

#### 8. UnaryValue Bool operator — 2 tests

**Tests**: TestUnaryNot, TestUnaryBitwiseNot (OperatorTests.cs)

**Root cause**: `UnaryValue` has a Bool field making the struct non-blittable for CallConvSwift. The @_cdecl operator wrapper was partially fixed in Session 6B-1 for arithmetic/comparison operators but `!` and `~` unary operators on Bool-field structs still need work.

**Fix approach**: Extend Session 6B-1's operator wrapper fix (`OperatorHandler.cs`) to handle unary operators on structs with Bool fields. The wrapper needs UnsafeRawPointer params with `.load(as:)` reconstruction.

**Key files**: `OperatorHandler.cs`

### Priority 3: Requires investigation or deep infrastructure

#### 9. Async optional nil detection — 1 test

**Tests**: TestAsyncGetNilResult (AsyncComplexTypeTests.cs)

**Root cause**: Async callback returns non-null for nil Swift optional. Session 7D fixed the async callback for non-nil optional values, but the nil case still fails — the tag/discriminator detection in the async optional return path doesn't detect None correctly.

**Fix approach**: Investigate the async optional return marshalling in `WrapperEmitter.Async.cs`. Compare with the working sync optional return path.

**Key files**: `WrapperEmitter.Async.cs`

#### 10. Async typed throws wrapper compilation — 2 tests

**Tests**: TestAsyncParseTypedCatch, TestAsyncParseSuccess (ThrowingMethodTests.cs)

**Root cause**: `EntryPointNotFoundException` for `SBW_..._asyncParse_..._async` — the typed throws async wrapper is stripped during Swift compilation. The wrapper function fails to compile and the build script's error-based retry silently removes it.

**Fix approach**: Look at the Swift compilation errors for the typed throws async wrapper. May need fixes to `ClosureEmitter.Async.cs` or `WrapperEmitter.Async.cs` for the typed throws path.

**Key files**: `ClosureEmitter.Async.cs`, `WrapperEmitter.Async.cs`

#### 11. Method-level generic free function — 1 test

**Tests**: TestGetPairSameType (BasicGenericTests.cs)

**Root cause**: `pair<T,U>()` is a method-level generic free function. Guard 6 in `MethodWrapperEmitter.ShouldEmitWrapper()` (`env.MethodDecl.IsGeneric`) blocks all method-level generics. Session 7C's protocol pattern relies on extending a parent type, which free functions don't have.

**Fix approach**: New wrapper pattern needed. Options: (a) generate a dummy struct to serve as the protocol conformance host, (b) use function pointer trampolines, (c) generate specialized wrappers per instantiation.

**Key files**: `MethodWrapperEmitter.cs` (guard 6)

#### 12. Optional array layout mismatch — 1 test

**Tests**: TestBatchConfigTagCountNil (CompositionTests.cs)

**Root cause**: Frozen struct + optional array buffer size calculation wrong. The layout engine doesn't compute the correct size for `Optional<[SomeType]>` fields in frozen structs.

**Fix approach**: Investigate the buffer size calculation for optional array fields in frozen struct layouts.

**Key files**: Layout/marshalling code for frozen structs

#### 13. ExistentialCallbackTests — 1+ tests (class-level skip)

**Tests**: All tests in ExistentialCallbackTests (class-level skip)

**Root cause**: `EntryPointNotFoundException` for existential callback wrapper — the Swift wrapper either isn't emitted or fails to compile.

**Fix approach**: Investigate whether the wrapper is emitted and if so, why it fails to compile. Check the Swift compilation errors.

**Key files**: Existential callback wrapper emission

#### 14. Existential container ref params — 2 tests

**Tests**: TestRunModeConsumerWithSimpleMode, TestRunModeConsumerWithStrictMode (ExistentialBoxingTests.cs)

**Root cause**: SIGKILL on NativeAOT device when existential container is passed by reference. Works on Mono simulator.

**Fix approach**: May need a different parameter passing strategy — pass existential container by value or through UnsafeRawPointer in the @_cdecl wrapper.

**Key files**: Existential container marshalling, `WrapperEmitter.Marshalling.cs`

---

## Recommended Execution Order

1. **Quick wins** (#3 SHA2Variant, #4 Optional constructor): 2 tests, likely < 1 hour each
2. **Array marshalling** (#2 IntContainer): 3 tests, clear root cause
3. **Generic class extensions** (#5, #6): 4 tests, extends proven 7C pattern
4. **Operator/optional fixes** (#7 OptionalShape, #8 UnaryValue): 4 tests
5. **Async fixes** (#9 nil detection, #10 wrapper compilation): 3 tests
6. **Mono JIT investigation** (#1): 8 SkipOnSimulator tests, may be upstream
7. **Deep infrastructure** (#11, #12, #13, #14): 5+ tests, needs investigation

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

# Library validation (~1 min):
./validate-libraries.sh 2>&1 | tee /tmp/validate-results.txt
```
