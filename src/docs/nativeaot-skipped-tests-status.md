# NativeAOT Skipped Tests — Status & Diagnosis

> **Last updated:** 2026-03-31
> **Device counts:** 1265 pass, 0 fail, 9 skip, 0 crash
> **Simulator counts:** 1265 pass, 0 fail, 9 skip, 0 crash

## Summary of Fixes

### Fix 1: Bound generic metadata registration (1 test)

**Problem:** `Pair<CoordinateRef, LabelRef>` was not registered in the module initializer because the `_requiresIndirectResult` catch-all in `WrapperEmitter.Return.cs` didn't call `RecordBoundGenericSwiftObjectType` for bound generics returned via @_cdecl indirect result.

**Fix:** Added `RecordBoundGenericSwiftObjectType` call in the indirect result catch-all path. Also added `IsRuntimeContainerType` filter to exclude `SwiftArray<T>`, `SwiftDictionary<K,V>`, `SwiftSet<T>` from registration (they have lazy metadata resolution and their element types might not be registered yet).

### Fix 2: Witness table pre-registration for all conformances (20 tests)

**Problem:** On NativeAOT device, `ProtocolWitnessTable.GetOrThrowDirect` → `LoadFromSymbol` → `swift_getWitnessTable` crashed with SIGKILL at runtime. The witness table cache was only populated for `ISwiftHashable`.

**Fix:** Emit `RegisterWitnessTable<T, TProtocol>()` for ALL protocol conformances in the module initializer. Added `WitnessTableDispatcher.TryGet` cache check in `GetOrThrowDirect`.

### Fix 3: TupleMarshallingTests skip removal (9 tests)

**Problem:** The `_nativeAotCrashClasses` skip was stale — added during early NativeAOT testing before witness table/metadata fixes. All tuple P/Invokes correctly use `CallConvCdecl` with `IntPtr` buffers; `swift_getTupleTypeMetadata` works fine on NativeAOT device.

**Fix:** Removed `TupleMarshallingTests` from `_nativeAotCrashClasses`. All 9 tests pass.

### Fix 4: SwiftResult NativeAOT factory registration

**Problem:** `SwiftResult<T,E>` was missing `TryEagerInitialize()` in its static constructor on NativeAOT, unlike `SwiftArray<T>` which registers its `NewFromPayload` factory eagerly.

**Fix:** Added `TryEagerInitialize()` to `SwiftResult<T,E>` static constructor, same pattern as `SwiftArray<T>`. This is a correctness improvement but did NOT fix the MCB callback crash (see below).

---

## Remaining: MCB Callback Bridge (3 tests)

**File:** `BindingTests/RuntimeTestsApp/Closures/ClosureEdgeCaseTests.cs`

| Test | Skip Attribute |
|------|---------------|
| `TestMCBOverload_DataProcessorProcess` | `[SkipOnDevice]` |
| `TestMCBOverload_ImageProcessorProcess` | `[SkipOnDevice]` |
| `TestMCBOverload_DataProcessorProcessWithError` | `[SkipOnDevice]` |

**Symptom:** SIGTRAP (signal 5) immediately when the test calls the Swift method with a closure that receives `SwiftResult<FetchResult, FetchError>`. The crash kills the process. All 3 tests pass on simulator.

**What's been ruled out:**
- Factory registration: `TryEagerInitialize()` added to `SwiftResult` — didn't help
- Witness tables: All conformances pre-registered — didn't help
- Calling convention: P/Invoke uses `CallConvCdecl` correctly

**Remaining hypotheses:**
1. The `[UnmanagedCallersOnly]` callback function pointer is invalid or has wrong signature on NativeAOT
2. `SwiftMarshal.MarshalFromSwift<SwiftResult<T,E>>` triggers a NativeAOT trimmer/metadata issue during the callback
3. The Swift `Result<FetchResult, FetchError>` memory layout doesn't match what C# expects when passed via `withUnsafePointer`

## Remaining general skips (not NativeAOT-specific)

| Test | Reason |
|------|--------|
| `BasicGenericTests.TestMultiTypeParameterGeneric` | Upstream issue #4: multi-type-parameter generic SIGSEGV |
| `LeakDetectionTests` (4 tests) | weak/unowned references not supported by generator |
| `NonStandardEnumTests.TestPermissionRawValues` | .swiftinterface strips integer raw values |
