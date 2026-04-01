# NativeAOT Skipped Tests — Status & Diagnosis

> **Last updated:** 2026-03-31
> **Device counts:** 1256 pass, 0 fail, 18 skip, 0 crash
> **Simulator counts:** 1265 pass, 0 fail, 9 skip, 0 crash

## Summary of Fixes (2026-03-31)

**21 of 24 previously-skipped tests are now fixed.** The root causes were:

### Fix 1: Bound generic metadata registration (Category F — 1 test)

**Problem:** `Pair<CoordinateRef, LabelRef>` was not registered in the module initializer because the `_requiresIndirectResult` catch-all in `WrapperEmitter.Return.cs` didn't call `RecordBoundGenericSwiftObjectType` for bound generics returned via @_cdecl indirect result.

**Fix:** Added `RecordBoundGenericSwiftObjectType` call in the indirect result catch-all path. Also added `IsRuntimeContainerType` filter to exclude `SwiftArray<T>`, `SwiftDictionary<K,V>`, `SwiftSet<T>` from registration (they have lazy metadata resolution and their element types might not be registered yet).

**Files changed:**
- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/WrapperEmitter.Return.cs` — bound generic registration in indirect result catch-all
- `src/Swift.Bindings/src/Emitter/StringEmitter/ModuleEmissionContext.cs` — `IsRuntimeContainerType` filter

### Fix 2: Witness table pre-registration for all conformances (Categories A, B, C, D, Closure+EA — 20 tests)

**Problem:** On NativeAOT device, `ProtocolWitnessTable.GetOrThrowDirect` → `LoadFromSymbol` → `swift_getWitnessTable` crashed with SIGKILL when called at runtime. The witness table cache (`WitnessTableDispatcher`) was only populated for `ISwiftHashable` conformances, leaving all other protocol conformances to go through the crashing runtime path.

**Root cause:** `LoadFromSymbol` in `ProtocolConformanceDescriptor.cs` does `NativeLibrary.TryLoad` → `NativeLibrary.TryGetExport` → `NativeLibrary.Free`. On NativeAOT device, the library handle lifecycle or `swift_getWitnessTable` P/Invoke has a subtle issue that causes SIGKILL when called after module initialization (but works during initialization). The exact underlying cause in the .NET NativeAOT runtime is unknown.

**Fix:**
1. Emit `RegisterWitnessTable<T, TProtocol>()` for ALL protocol conformances in the module initializer (not just `ISwiftHashable`). This eagerly computes and caches witness tables during init when the path works.
2. Added `WitnessTableDispatcher.TryGet` check at the top of `GetOrThrowDirect` so cached witness tables are returned without going through `LoadFromSymbol`.

**Files changed:**
- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/ModuleHandler.cs` — emit `RegisterWitnessTable` for all conformances
- `src/Swift.Runtime/src/Swift/Runtime/ProtocolWitnessTable.cs` — cache check in `GetOrThrowDirect`

---

## Remaining: Category E — MCB Callback Bridge (3 tests)

**File:** `BindingTests/RuntimeTestsApp/Closures/ClosureEdgeCaseTests.cs`

| Test | Line |
|------|------|
| `TestMCBOverload_DataProcessorProcess` | ~249 |
| `TestMCBOverload_ImageProcessorProcess` | ~267 |
| `TestMCBOverload_DataProcessorProcessWithError` | ~281 |

**What happens:** Methods with closure parameters using `SwiftResult<FetchResult, FetchError>` crash with SIGSEGV (signal 11) on NativeAOT device. 22 non-MCB tests in the same class pass.

**This is NOT a witness table issue** — the fix for Categories A-D doesn't help here. The crash is in the MCB callback mechanism itself, likely in:
1. The `SwiftResult<T, E>` metadata resolution during the callback
2. The `[UnmanagedCallersOnly]` function pointer dispatch
3. Memory layout mismatch for `Result<FetchResult, FetchError>` between Swift and C#

**Next steps:** Add diagnostic logging to the MCB callback path. Check if `SwiftResult<FetchResult, FetchError>` metadata resolves correctly on NativeAOT. Verify the function pointer in `s_MCB_*` static fields is valid.
