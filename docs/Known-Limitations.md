# Known Limitations

## Platform Requirements

- **macOS only** — the generator requires Apple platform tools (`swift-frontend`, `swiftc`, `xcodebuild`)
- **Xcode required** — for both binding authors and app builders (this is a .NET iOS requirement, not specific to Swift Bindings)
- **.NET 10.0** — targets `net10.0-ios` (and `net10.0-macos`, `net10.0-maccatalyst`)
- **Dynamic xcframeworks only** — static libraries (`.a` archives) are not supported
- **Apple platforms only** — no Windows/Linux support (these are Apple-specific frameworks)

## Mono JIT Runtime Bug

.NET on iOS currently uses the Mono runtime, which has a known JIT compiler defect (`jit-info.c:918`). This causes process-fatal crashes when certain P/Invoke frame types use `CallConvSwift`.

Four workarounds (A through D) are built into the generator and runtime. These are transparent — generated bindings work correctly without any manual intervention. However, they introduce a runtime dependency on `libSwiftBindingsRuntime.dylib`.

### What's Covered by Workarounds

| Category | Workaround | Status |
|----------|-----------|--------|
| SwiftString operations | A: `@_cdecl` wrapper in runtime dylib | Working |
| Closure callbacks | B: Cdecl expansion (primitive args) | Working |
| Existential metadata | C: `@_cdecl` wrapper in runtime dylib | Working |
| Signature risk detection | D: Static analysis flags risky methods | Working |

### What's Still Affected

- **Explicit `Dispose()` on structs with reference-type fields** — `ValueWitnessTable->Destroy()` uses an indirect `CallConvSwift` function pointer. Affects a small number of struct types.
- **Closures with non-primitive arguments** — String, class, and struct closure arguments stay on the legacy `CallConvSwift` path.
- **N-protocol existentials** — Only zero-protocol existentials (`Any`) are covered by the wrapper.

These will be resolved when the upstream Mono fix lands in `dotnet/runtime`.

## Unsupported Swift Patterns

| Pattern | Why | Workaround |
|---------|-----|------------|
| **Protocols with Associated Types (PATs)** | Exponential type complexity | Use concrete type wrappers |
| **Actors (deep isolation)** | Actor isolation model doesn't map to .NET threading | Actor methods are callable but without isolation enforcement |
| **`@MainActor` constraints** | Thread affinity doesn't map cleanly | Dispatch manually |
| **Combine / `@Published`** | Reactive paradigm mismatch | Use async/await patterns instead |
| **8+ element tuples** | C# `ValueTuple` nesting complexity | Restructure the API |
| **Closures within closures** | ABI complexity | Flatten the callback structure |
| **`async throws` closures at runtime** | Binding generation works but runtime blocked by Mono JIT | Wait for upstream fix |
| **Deep SwiftUI state** | `@State`/`@Binding`/`@Environment` don't map to C# | Use the bridge for presentation only |

## Non-Blittable Type Limitation

.NET's `CallConvSwift` requires all P/Invoke parameters to be blittable (directly memory-mappable). Types like `SwiftOptional<T>`, `SafeHandle`, and managed strings are not blittable.

The generator works around this with Swift wrapper functions and `IntPtr` + manual marshalling. This is transparent but means some APIs route through an extra layer.

## SafeHandle in Async P/Invoke

The .NET runtime doesn't preserve `SafeHandle` references across async continuations. The generator works around this with singleton detection and `IntPtr` conversion, but edge cases with certain class hierarchies may exist.

## Coverage Gaps

The generator achieves 88–99% member coverage across tested libraries. The remaining gaps are:

- **Unsupported operators** — compound assignment (`+=`, `-=`) and overflow shift (`&<<`, `&>>`) have no C# equivalent
- **Generic protocol closure signatures** — e.g., `worker()` on block cipher modes
- **AnyType property fallbacks** — types not in the type database fall back to `object`
- **Internal methods** — correctly excluded; wrappers can't access `@usableFromInline internal` members
- **Existential arguments in bound generics** — e.g., `Array<any Protocol>` (26 skips across Nuke/Lottie)

The binding report (`binding-report.json`) documents exactly which members are skipped and why.

## Swift Wrapper Escape Hatch

For APIs that can't be directly bound (due to .NET runtime limitations or Swift complexity), the generator supports a Swift wrapper fallback:

```
Normal API:      C# → P/Invoke → Swift dylib
Edge case API:   C# → P/Invoke → Swift wrapper → Swift dylib
```

The Swift compiler is the only entity guaranteed to understand the Swift ABI perfectly. When .NET's JIT or marshalling fails, delegating to Swift-side code is the architecturally correct approach.

---

## Next Steps

- **[Troubleshooting](Troubleshooting)** — Solutions for specific errors
- **[Supported Features](Supported-Features)** — What does work
- **[Architecture](Architecture)** — How the generator handles these constraints
