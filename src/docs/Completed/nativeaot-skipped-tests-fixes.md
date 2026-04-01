# NativeAOT Skipped Tests — Fixes

**Date**: March–April 2026
**Final device counts**: 1265 pass, 0 fail, 9 skip, 0 crash

## Fix 1: Bound generic metadata registration (1 test)

`Pair<CoordinateRef, LabelRef>` was not registered in the module initializer because the `_requiresIndirectResult` catch-all in `WrapperEmitter.Return.cs` didn't call `RecordBoundGenericSwiftObjectType` for bound generics returned via @_cdecl indirect result.

**Fix**: Added `RecordBoundGenericSwiftObjectType` call in the indirect result catch-all path. Also added `IsRuntimeContainerType` filter to exclude `SwiftArray<T>`, `SwiftDictionary<K,V>`, `SwiftSet<T>` from registration.

## Fix 2: Witness table pre-registration for all conformances (20 tests)

On NativeAOT device, `ProtocolWitnessTable.GetOrThrowDirect` → `LoadFromSymbol` → `swift_getWitnessTable` crashed with SIGKILL. The witness table cache was only populated for `ISwiftHashable`.

**Fix**: Emit `RegisterWitnessTable<T, TProtocol>()` for ALL protocol conformances in the module initializer. Added `WitnessTableDispatcher.TryGet` cache check in `GetOrThrowDirect`.

## Fix 3: TupleMarshallingTests skip removal (9 tests)

The `_nativeAotCrashClasses` skip was stale — added during early NativeAOT testing before witness table/metadata fixes. All tuple P/Invokes correctly use `CallConvCdecl` with `IntPtr` buffers.

**Fix**: Removed `TupleMarshallingTests` from `_nativeAotCrashClasses`. All 9 tests pass.

## Fix 4: SwiftResult NativeAOT factory registration

`SwiftResult<T,E>` was missing `TryEagerInitialize()` in its static constructor on NativeAOT, unlike `SwiftArray<T>`.

**Fix**: Added `TryEagerInitialize()` to `SwiftResult<T,E>` static constructor. Correctness improvement but did NOT fix the MCB callback crash (tracked in roadmap Hard/Deferred).
