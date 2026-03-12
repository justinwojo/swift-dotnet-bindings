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

Mono's JIT compiler incorrectly marks `CallConvSwift` P/Invoke frames as "async", then hits the assertion `!ji->async` at `jit-info.c:918` during stack unwinding. **This is a process-fatal abort** — it bypasses all managed exception handlers and kills the application immediately.

### Impact

With @_cdecl wrappers covering 78.5% of P/Invokes, most generated bindings work correctly on Mono. The remaining ~21.5% CallConvSwift P/Invokes may trigger this crash.

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

## NativeAOT on iOS Simulator (Recommended for Development)

All Mono JIT issues — the `jit-info.c:918` crash, non-blittable type rejections, SafeHandle async limitations — are **eliminated** by using NativeAOT on the iOS Simulator.

### How to Enable

```xml
<PropertyGroup>
  <TargetFramework>net10.0-ios</TargetFramework>
  <RuntimeIdentifier>iossimulator-arm64</RuntimeIdentifier>
  <PublishAot>true</PublishAot>
  <PublishAotUsingRuntimePack>true</PublishAotUsingRuntimePack>
</PropertyGroup>
```

```bash
dotnet publish -c Release
```

### Trade-offs

| Aspect | Mono JIT (default) | NativeAOT |
|--------|-------------------|-----------|
| Build time | Fast (~5s) | Slow (~30-60s) |
| `CallConvSwift` | Crashes (workarounds needed) | Works correctly |
| Incremental builds | Supported | Full rebuild required |
| Debugging | Full managed debugger | Limited (native debugger) |

---

## Primitive/ObjC Types Cannot Satisfy ISwiftObject Constraint

**Severity**: Low — Affects ~5 methods across all validated libraries
**Status**: Documented limitation (by design)

Methods where a bound generic type parameter is instantiated with a primitive (`Int`, `Bool`) or ObjC-bridged type (`String`, `URL`) cannot be bound because C# generic constraints require `where T : ISwiftObject`, which .NET primitives cannot implement.

No workaround — use the equivalent non-generic Swift API or write a Swift wrapper.
