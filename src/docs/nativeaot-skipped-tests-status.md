# NativeAOT Skipped Tests — Status & Diagnosis

> **Last updated:** 2026-04-01
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

**Symptom:** SIGSEGV (signal 11) during P/Invoke to the Swift MCB wrapper. The C# `[UnmanagedCallersOnly]` callback is **never entered** — crash occurs on the Swift side. All 3 tests pass on simulator.

**Diagnostic findings (2026-04-01):**
- SwiftResult metadata resolution works fine outside callbacks (FetchResult, FetchError, SwiftResult all resolve)
- Function pointer `s_MCB_10992C96` is valid (non-null, reasonable address 0x10219A000)
- The MCB callback C# function is never reached (no console output from `[UnmanagedCallersOnly]` method)
- Symbol `_SBW_MCB_10992C96_process` exists in device framework (`nm -g` confirmed)
- `SwiftResult<int, ExistentialContainer1>` MCB callbacks **work** on device (same MCB pattern, different type params)
- The crash is specific to `Result<FetchResult, FetchError>` where both params are class/enum types

**What's been ruled out:**
- Factory registration (`TryEagerInitialize` added — didn't help, crash is before C# is entered)
- Witness tables (all pre-registered — unrelated)
- Invalid function pointer (verified non-null, valid address 0x10219A000)
- Stripped Swift wrapper (symbol confirmed in framework via `nm -g`)
- Calling convention (P/Invoke uses `CallConvCdecl`, same as working MCB callbacks)
- Metadata resolution (all types resolve correctly outside callback context)
- Heap allocation vs stack pointer (`UnsafeMutableRawPointer.allocate` + `initializeMemory` doesn't fix it — same SIGSEGV)
- Emitter code path difference (Codex verified: working and crashing wrappers are structurally identical)

**Key discriminator:** `Result<Int32, any Error>` (primitive + existential) works. `Result<FetchResult, FetchError>` (class + concrete Error enum) crashes. Same MCB pattern, same calling convention, same wrapper structure. The ONLY difference is the Swift Result type parameters.

**Root cause:** Unknown. The crash is SIGSEGV in the compiled Swift `cdecl(...)` call — the instruction that transfers control from the Swift closure to the NativeAOT `[UnmanagedCallersOnly]` entry point. C# managed code is never entered. Both `withUnsafePointer` (stack) and heap allocation crash identically.

**Next steps for future investigation:**
1. Attach Xcode debugger to device and catch the SIGSEGV in-flight — get the exact faulting instruction and register state
2. Compare ARM64 disassembly of the working wrapper (`SBW_MCB_2C2A0217_processWithResult`) vs crashing wrapper (`SBW_MCB_10992C96_process`) in the compiled framework
3. Check if Swift's `unsafeBitCast` of the function pointer produces different code for closures captured inside different `Result<>` type contexts
4. Test with a simpler `Result<String, FetchError>` (class success, concrete error) to narrow whether the crash is related to class success type, concrete Error enum type, or the combination

## Remaining general skips (not NativeAOT-specific)

| Test | Reason |
|------|--------|
| `BasicGenericTests.TestMultiTypeParameterGeneric` | Upstream issue #4: multi-type-parameter generic SIGSEGV |
| `LeakDetectionTests` (4 tests) | weak/unowned references not supported by generator |
| `NonStandardEnumTests.TestPermissionRawValues` | .swiftinterface strips integer raw values |
