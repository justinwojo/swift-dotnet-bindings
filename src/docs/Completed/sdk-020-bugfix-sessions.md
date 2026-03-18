# SDK 0.2.0 Bug Fix Sessions

Based on findings from `/Users/wojo/Dev/swift-dotnet-packages/SDK-0.2.0-FINAL-TESTING.md`.

---

## Session 1: MSBuild Target Fixes (4 bugs) — **Status: Complete** (dffc586c)

**Scope**: Fix all 4 documented MSBuild target bugs in `Sdk.targets`.

**Deliverables**:

1. **Bug 1 — GetSwiftFrameworkSearchPaths relative paths**: Add `$(MSBuildProjectDirectory)/` prefix to wrapper xcframework path in `GetSwiftFrameworkSearchPaths` target (line ~393-394).

2. **Bug 2 — _CompileSwiftWrapper missing ContinueOnError**: Add `ContinueOnError="WarnAndContinue"` to the `Exec` task in `_CompileSwiftWrapper` (line ~445-448).

3. **Bug 3 — ObjC ProjectReferences queried for GetSwiftFrameworkSearchPaths**: Filter out `.ObjC.` ProjectReferences before the `MSBuild` task that queries `GetSwiftFrameworkSearchPaths` (line ~421-427).

4. **Bug 4 — Duplicate dependency module error**: Prefer `_ResolvedDepXCFramework` paths and only fall back to `SwiftFrameworkDependency` when no ProjectReference deps resolved (lines ~439-442).

**Key Files**:
- `src/Swift.Bindings.Sdk/Sdk/Sdk.targets`
- `src/Swift.Bindings/tests/UnitTests/SdkTests/SdkPropsTargetsTests.cs`

**Validation**:
- `./run-tests.sh 2>&1 | tee /tmp/session-1-tests.txt`
- `./validate-libraries.sh 2>&1 | tee /tmp/session-1-validate.txt`

---

## Session 2: Data Parameter Wrapper Regression — **Status: Complete** (a03a1d1d)

**Scope**: Investigate and fix the regression where `@_cdecl` wrapper functions that accept `Data`/`byte[]` parameters crash on device with SIGSEGV in SDK 0.2.0 (worked in 0.1.1).

**Affected Tests**:
- Nuke: `DataCache_Roundtrip`, `DataCache_Remove`, `DataCache_RemoveAll`
- Lottie: `Animation_FromData`

**Investigation Steps**:
1. Generate bindings for Nuke and Lottie, compare the wrapper `.swift` files between what 0.1.1 and 0.2.0 produce
2. Look for changes in how `Data`/`[UInt8]` parameters are marshaled in `@_cdecl` functions
3. Check `WrapperEmitter` and `MarshallingHelpers` for recent changes to Data/byte[] handling
4. Check the other Claude's work at `/Users/wojo/Dev/swift-bindings` — commit `103e8fed` "Fix @_cdecl parameter marshalling: UTF-8 strings, collection pointer, nint length" may be relevant

**Key Files**:
- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/WrapperEmitter.*.cs`
- `src/Swift.Bindings/src/Marshaler/MarshallingHelpers.cs`
- Generated wrapper `.swift` files in output directories

**Validation**:
- Generate Nuke/Lottie bindings and verify wrapper compiles
- `./run-tests.sh 2>&1 | tee /tmp/session-2-tests.txt`
- `./validate-libraries.sh 2>&1 | tee /tmp/session-2-validate.txt`

---

## Session 3: LottieAnimationLayer Setter + Stripe Enum Regressions — **Status: Complete** (b77d9e9d)

**Scope**: Fix two additional 0.2.0 regressions:
1. `LottieAnimationLayer.Animation` property setter crashes (CALayer subclass, not UIView)
2. Stripe enum type initializer failures (`CardScanSheetResult`, `FinancialConnectionsSheet.Result`) when wrapper not compiled

**Investigation Steps**:
1. Compare generated wrapper for `LottieAnimationLayer.Animation` setter vs `LottieAnimationView.Animation` setter — why does the CALayer variant crash?
2. For Stripe enums: understand why `SwiftObjectHelper<T>.GetTypeMetadata()` requires the wrapper DLL for enum types when it didn't in 0.1.1
3. Check if there's a fallback path for type metadata when wrapper is unavailable

**Key Files**:
- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/WrapperEmitter.*.cs`
- `src/Swift.Runtime/src/Swift/Runtime/ISwiftObject.cs` (SwiftObjectHelper)
- `src/Swift.Runtime/src/Swift/Runtime/TypeMetadata.cs`

**Validation**:
- `./run-tests.sh 2>&1 | tee /tmp/session-3-tests.txt`
- `./validate-libraries.sh 2>&1 | tee /tmp/session-3-validate.txt`

---

## Session 4: GCHandle Lifetime Bug (Critical) — **Status: Complete** (9db55a3a)

**Scope**: Fix `GCHandle` freed in `finally` block before escaping closure fires.

**Description**: Generated closure wrappers allocate a `GCHandle` to pin the C# delegate, but free it in a `finally` block at the end of the calling method. For escaping closures (callbacks that fire asynchronously), the `GCHandle` is freed before the callback executes, causing use-after-free. Affects Lottie (22+ callbacks including `Play(completion:)`).

**Key Files**:
- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/` (closure emission code)
- `src/Swift.Bindings/src/Marshaler/` (closure marshaling)

**Validation**:
- `./run-tests.sh 2>&1 | tee /tmp/session-4-tests.txt`
- `./validate-libraries.sh 2>&1 | tee /tmp/session-4-validate.txt`

---

## Session 5: AsyncStream Library Target + Async Singleton Bugs — **Status: Complete** (5d8aae33)

**Scope**: Fix two async-related generator bugs:
1. **AsyncStream Library (Critical)**: Verified already correct — AsyncStream P/Invokes use `AsyncLibraryName` (wrapper library) when set. Added regression tests.
2. **Async Singleton (High)**: Fixed. Async wrappers hardcoded `.shared` instead of using the `self_` parameter. Removed singleton special-casing from PInvokeEmitter and WrapperEmitter.Async — all async instance methods now pass self explicitly.

**Affected**: Nuke (4 async methods on ImagePipeline)

**Key Files**:
- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/WrapperEmitter.Async.cs`
- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/PInvokeEmitter.cs`
- `src/Swift.Bindings/tests/UnitTests/EmitterTests/AsyncSwiftWrapperTests.cs`

**Validation**:
- `./run-tests.sh 2>&1 | tee /tmp/session-5-tests.txt` — 7651 passed, 0 failed
- `./validate-libraries.sh 2>&1 | tee /tmp/session-5-validate.txt` — 90/90 passed

---

## Session 6: Double P/Invoke + SwiftIndirectResult Leak — **Status: Complete** (5c522148)

**Scope**: Fix two memory-related generator bugs:
1. **Double P/Invoke (High)**: Optional ObjC property getters call the P/Invoke twice — once to check if the value is non-nil, then again to get the value. This causes an ARC leak (first call's return value is never released). Affects Nuke (3 properties).
2. **SwiftIndirectResult Leak (High)**: `NativeMemory.Alloc` in `SwiftIndirectResult` getters is never freed. Affects BlinkID (14 properties).

**Key Files**:
- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/` (property emission)
- `src/Swift.Bindings/src/Marshaler/MarshallingHelpers.cs`

**Validation**:
- `./run-tests.sh 2>&1 | tee /tmp/session-6-tests.txt`
- `./validate-libraries.sh 2>&1 | tee /tmp/session-6-validate.txt`

---

## Session 7: OptBuf Param Order + Missing Protocol Proxies

**Scope**: Fix two StripeCore-specific generator bugs:
1. **OptBuf Param Order (Critical)**: Result buffer position differs between the C# signature and the Swift `@_cdecl` wrapper signature. The C# code passes the buffer in one parameter position, but the Swift wrapper expects it in a different position.
2. **Missing Protocol Proxies (High)**: EveryProtocol conformances are missing for 5 of 7 protocols. The generator emits proxy implementations for some protocols but not all. Affects 10 symbols in StripeCore.

**Key Files**:
- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/WrapperEmitter.*.cs`
- `src/Swift.Bindings/src/Marshaler/Projection/MethodMarshalPlanBuilder.cs`
- Protocol emission code

**Validation**:
- `./run-tests.sh 2>&1 | tee /tmp/session-7-tests.txt`
- `./validate-libraries.sh 2>&1 | tee /tmp/session-7-validate.txt`
