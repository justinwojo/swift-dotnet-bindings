# Known Issues and Workarounds

This document tracks runtime issues, .NET/Swift interop bugs, and their status.

---

## Architecture: @_cdecl Wrapper-First ABI

The generator uses `@_cdecl` Swift wrapper functions as the primary ABI boundary between C# and Swift. These wrappers use `CallingConvention.Cdecl` (C calling convention), avoiding `CallConvSwift` which has known issues on both Mono and NativeAOT runtimes.

**Current coverage**: 78.5% of P/Invokes (13,759 of 17,525) use @_cdecl wrappers. The remaining ~21.5% use direct `CallConvSwift` and receive `[Obsolete(DiagnosticId = "SB0001")]` annotations warning consumers.

**Remaining CallConvSwift categories** (cannot be wrapped):

| Category | Count | Why |
|----------|-------|-----|
| Method-level generics | ~968 | ABI spec not available |
| Frozen struct params | ~200 | Swift compiler restriction ("cannot be represented in Objective-C") |
| Optional\<existential\> | ~199 | Needs protocol proxy conversion in marshalling |
| Combined blockers | ~500 | Multiple unsupported patterns |
| Other (operators, etc.) | ~460 | Diminishing returns |

---

## Mono JIT Assertion Crash (CallConvSwift)

### What It Is

Two related Mono JIT assertions make all Swift type access fatal:

1. **`mini-generic-sharing.c:2759`** — `is_ok(error)` assertion fails during `SwiftObjectHelper<T>.GetTypeMetadata()` for any Swift type `T`. This is triggered by the static field initializer `static nuint _payloadSize = SwiftObjectHelper<T>.GetTypeMetadata().Size;` on first access to any generated Swift type. **This blocks 100% of Swift interop on Mono**, not just CallConvSwift P/Invokes.

2. **`jit-info.c:918`** — `!ji->async` assertion fails during stack unwinding after the first crash.

Both are **process-fatal SIGABRT** — they bypass all managed exception handlers and kill the application immediately. **This affects all Mono JIT** (simulator AND physical device), not just simulator as previously documented. Confirmed on iPhone 13 with Mono debug builds (2026-03-15).

### Impact

**No Swift types can be accessed under Mono.** The crash happens before any @_cdecl wrapper or CallConvSwift P/Invoke is reached — it's in the type metadata initialization path. Pure C# enums (no Swift runtime calls) and apps that don't touch Swift types work fine.

### Mitigation

Two runtime-level workarounds handle the most critical crash paths:

**SwiftString Runtime Wrappers** — Five `@_cdecl` wrapper functions in `libSwiftBindingsRuntime.dylib` handle SwiftString operations (`ToString()`, `Length`, constructor) via `CallingConvention.Cdecl`. The C# runtime uses wrapper-first with fallback to direct CallConvSwift (for non-Mono runtimes).

**Existential Metadata Wrapper** — `@_cdecl` wrapper in `libSwiftBindingsRuntime.dylib` calls `swift_getExistentialTypeMetadata` on the Swift side. Hard-fail without dylib (no fallback — the direct path is process-fatal on Mono and unnecessary on NativeAOT).

> **Runtime dependency**: Both wrappers require `libSwiftBindingsRuntime.dylib` in the application bundle. Without it, SwiftString operations and existential metadata lookups throw `SwiftRuntimeException`.

---

## SB0003: Non-Dispatchable Protocol Members

**Severity**: Low — Informational diagnostic on generated code
**Status**: Resolved (F6, March 2026)

Protocol proxy classes emit `[Obsolete("...", DiagnosticId = "SB0003")]` on members that cannot be dispatched through Swift's witness table. When called on a Swift-backed existential container, these members throw `NotSupportedException`. When called on a C# implementation (proxy created from managed code), they work normally.

| Reason | Example |
|--------|---------|
| `async methods require Swift concurrency runtime` | `async func fetchData()` |
| `parameter 'x' has non-dispatchable type 'Y'` | Method with closure/generic param |
| `return type 'Z' is not dispatchable` | Method returning unsupported type |
| `property type 'T' is not dispatchable via witness table` | Property with unsupported type |
| `subscript dispatch is not yet implemented` | Any subscript member |
| `throwing methods with optional existential return are not supported` | `func find() throws -> (any P)?` |

---

## SB1001: Undisposed ISwiftObject Analyzer Limitations

**Severity**: Low — Informational diagnostic (Warning)
**Status**: By design — lightweight heuristic, not full dataflow

The `SB1001` Roslyn analyzer warns when a local variable implementing `ISwiftObject` is not disposed via `using` or an explicit `Dispose()` call. It uses syntax-level heuristics rather than control-flow analysis.

### What SB1001 recognizes (no warning)

| Pattern | Example |
|---------|---------|
| `using` declaration | `using var x = new FooProxy();` |
| `using` statement | `using (var x = new FooProxy()) { }` |
| Unconditional `Dispose()` in same block | `var x = new FooProxy(); x.Dispose();` |
| `try/finally` Dispose | `try { } finally { x.Dispose(); }` |
| Direct return (ownership transfer) | `return x;` |

### Known limitations

- Field assignment, method ownership transfer, cross-method disposal, and conditional-but-always-reachable disposal cause false positives
- Dead code and variable reassignment cause false negatives
- Consumers should use `using` declarations consistently for reliable leak prevention

---

## Non-Blittable Types with Swift Calling Convention

**Severity**: Medium — Affects specific API patterns
**Status**: Documented limitation (requires upstream .NET changes)

### Symptoms

```
System.InvalidProgramException: Cannot use non-blittable types with Swift calling convention
```

.NET's `CallConvSwift` requires all parameters and return types to be blittable. Types like `SwiftOptional<T>`, `SafeHandle` derivatives, and managed strings are not blittable. The @_cdecl wrapper architecture avoids this for 78.5% of P/Invokes by marshalling through C-compatible types.

---

## SafeHandle in Async P/Invoke

**Severity**: Medium — Requires workaround for async instance methods
**Status**: Workaround implemented (singleton pattern, IntPtr conversion)

The .NET runtime does not properly preserve `SafeHandle` across async P/Invoke with Swift calling convention. The generator works around this via:

- **Singleton Pattern Detection**: Classes with `shared` static property use `ClassName.shared.method()` in Swift wrappers.
- **IntPtr Conversion** (non-singletons): Swift wrappers use `unsafeBitCast(_self, to: ClassName.self)`.

---

## Upstream Bug Report Status

**Draft location**: `Future/upstream-bug-reports-draft.md`

| # | Issue | Filing Strategy | Status |
|---|-------|-----------------|--------|
| 1 | `jit-info.c:918` JIT assertion crash with `CallConvSwift` | Bug report — clear-cut JIT defect | Draft ready |
| 2 | Non-blittable types rejected with `CallConvSwift` | Feature request — scope limitation | Draft ready |
| 3 | SafeHandle not preserved across async P/Invoke | Comment on tracking issue | Draft ready |

**Related dotnet/runtime tracking issues**:
- [#93631](https://github.com/dotnet/runtime/issues/93631) — Runtime support for Swift Interop in .NET 9
- [#108662](https://github.com/dotnet/runtime/issues/108662) — Runtime support for Swift Interop in .NET 10
- [#64215](https://github.com/dotnet/runtime/issues/64215) — Introduce `CallConvSwift`

---

## NativeAOT on Device (Required for Runtime Testing)

All Mono JIT issues — the `mini-generic-sharing.c:2759` crash, `jit-info.c:918` assertion, non-blittable type rejections, SafeHandle async limitations — are **eliminated** by using NativeAOT.

**NativeAOT on iOS simulator is NOT supported** — the .NET iOS SDK requires a device architecture for `dotnet publish` (`iossimulator-arm64` is rejected). NativeAOT requires a physical device with `ios-arm64`.

### How to Enable

```xml
<PropertyGroup>
  <TargetFramework>net10.0-ios</TargetFramework>
  <RuntimeIdentifier>ios-arm64</RuntimeIdentifier>
  <PublishAot>true</PublishAot>
  <PublishAotUsingRuntimePack>true</PublishAotUsingRuntimePack>
  <!-- Code signing required for device -->
  <CodesignKey>Apple Development: Justin Wojciechowski (KBKS29A36Q)</CodesignKey>
  <CodesignProvision>Wildcard Dev</CodesignProvision>
  <TeamIdentifierPrefix>TL2K6QUQEH</TeamIdentifierPrefix>
</PropertyGroup>
```

```bash
dotnet publish -c Release
xcrun devicectl device install app --device $UDID path/to/App.app
xcrun devicectl device process launch --device $UDID --console $BUNDLE_ID
```

### Trade-offs

| Aspect | Mono JIT (default) | NativeAOT (device) |
|--------|-------------------|-----------|
| Swift interop | **Broken** (SIGABRT on all type access) | Works (90.6% pass rate across 15 libraries) |
| Build time | Fast (~5s) | Slow (~30-60s) |
| Target | Simulator or device | Device only |
| Incremental builds | Supported | Full rebuild required |
| Debugging | Full managed debugger | Limited (native debugger) |

### Validated Results (2026-03-15)

See `src/docs/sim-validation-findings.md` for full device test results across 15 real-world libraries (309/341 tests pass). Test infrastructure at `/Users/wojo/Dev/sim-validation/`.

---

## Primitive/ObjC Types Cannot Satisfy ISwiftObject Constraint

**Severity**: Low — Affects ~5 methods across all validated libraries
**Status**: Documented limitation (by design)

Methods where a bound generic type parameter is instantiated with a primitive (`Int`, `Bool`) or ObjC-bridged type (`String`, `URL`) cannot be bound because C# generic constraints require `where T : ISwiftObject`, which .NET primitives cannot implement.

No workaround — use the equivalent non-generic Swift API or write a Swift wrapper.
