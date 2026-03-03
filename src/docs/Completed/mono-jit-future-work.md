# Mono JIT Mitigation — Remaining Future Work

> **Archived March 2026**: Moved to `Completed/`. All current workarounds (Strategies A-D) are deployed. The strategic direction is NativeAOT — if NativeAOT simulator support lands (upstream issue drafted in `Future/upstream-nativeaot-simulator-issue.md`), most of these items become irrelevant. Remaining items are not worth incremental investment given the NativeAOT exit strategy.

**Created**: February 2026
**Source**: Consolidated from `mono-jit-mitigation.md`
**Completed work**: See `Completed/mono-jit-mitigation-strategies.md`

---

## Open Items

### VWT Destroy via CallConvSwift

**Priority**: Medium | **Difficulty**: Medium

`SwiftSafeHandle<T>.ReleaseHandle()` calls `metadata.ValueWitnessTable->Destroy()` through an indirect function pointer with the Swift calling convention. Crashes on Mono when `Dispose()` is called on types with non-trivial fields (e.g., MutableProps with String). Also corrupts Mono's frame tracker non-deterministically during GC stack walks.

`SBW_SwiftString_Destroy` wrapper exists in `SwiftBindingsRuntime.swift` but isn't wired into the generic `SwiftSafeHandle<T>` path — would require type-specific dispatch or per-type generator-emitted destroy wrappers.

**Current mitigation**: MutableProps dispose tests demoted to Tier 3.

### VWT InitializeWithCopy

**Priority**: Low | **Difficulty**: Medium

`metadata.ValueWitnessTable->InitializeWithCopy()` in `MarshalToSwift` uses an indirect CallConvSwift function pointer call. Same frame tracker corruption risk as VWT Destroy. No known test failures from this path yet.

### Non-Primitive Closure Cdecl

**Priority**: Low | **Difficulty**: High

Strategy B (closure Cdecl expansion) only wraps closures with primitive args (Int, Bool, Double, Float). Closures with String, class, or struct arguments stay on the legacy `CallConvSwift` path. Wrapping these requires Swift-side adapters that marshal complex types across the `@convention(c)` boundary.

### N-Protocol Existential Metadata

**Priority**: Low | **Difficulty**: Medium

Strategy C (existential metadata wrapper) handles the zero-protocol case (`Any` / `ExistentialContainer0`). N>0 protocol cases require protocol descriptor pointers passed to `swift_getExistentialTypeMetadata`. The wrapper currently returns nil for `numProtocols > 0`.

**Kill criteria**: If N-protocol cases require per-protocol compile-time registration that makes the wrapper no simpler than existing per-type wrappers.

### Strategy E: NativeAOT Migration

**Priority**: Opportunistic | **Difficulty**: High | **Depends on**: .NET 10 iOS tooling

Eliminates the JIT bug entirely (NativeAOT has no JIT). All three blockers (JIT assertion, non-blittable types, SafeHandle async) verified resolved under NativeAOT. See `nativeaot-investigation.md`.

### Upstream Bug Reports

Three issues drafted in `Future/upstream-bug-reports-draft.md`. Filing deferred until repo goes public.

---

## Code Locations

| File | Remaining Risk |
|------|---------------|
| `SwiftHandle.cs:101-141` | VWT Destroy in `ReleaseHandle()` |
| `TypeMetadata.cs` | N-protocol existential path (throws SwiftRuntimeException) |
| `ClosureEmitter.cs` | Non-primitive closures still use `CallConvSwift` |
| `SwiftBindingsRuntime.swift` | `SBW_SwiftString_Destroy` exported but unwired to generic path |
