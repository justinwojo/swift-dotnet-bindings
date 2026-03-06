# Known Limitations

This is preview-quality software. The generator has been validated against 42 real-world libraries (Swift and ObjC) and achieves 88–99% member coverage. The sections below document what doesn't work yet and why.

## Platform Requirements

- **macOS only** — the generator requires Apple platform tools (`swift-frontend`, `swiftc`, `xcodebuild`)
- **Xcode required** — for both binding authors and app builders (this is a .NET iOS requirement, not specific to Swift Bindings)
- **.NET 10.0** — targets `net10.0-ios` (and `net10.0-macos`, `net10.0-maccatalyst`)
- **Dynamic xcframeworks only** — static libraries (`.a` archives) are not supported
- **Apple platforms only** — no Windows/Linux support (these are Apple-specific frameworks)

## Build Requirements for Swift Libraries

The xcframework you're binding must be built with **`BUILD_LIBRARY_FOR_DISTRIBUTION=YES`**. This enables the stable Swift ABI and generates the `.swiftinterface` files the generator needs.

If your library wasn't built this way, the generator will either produce no types or crash with an empty module name. This is the most common cause of "my library generates 0 types." If you control the library, rebuild it with this flag. If you're consuming a pre-built xcframework, check with the library author.

## Mono JIT Runtime Bug

.NET on iOS currently uses the Mono runtime, which has a known JIT compiler defect (`jit-info.c:918`). This causes process-fatal crashes when certain P/Invoke frame types use `CallConvSwift`.

**These issues only affect the Mono JIT (iOS Simulator).** Production device builds using NativeAOT (`dotnet publish -r ios-arm64`) use a completely different codegen (RyuJIT AOT) where `CallConvSwift` works correctly. The workarounds below are necessary for simulator-based development but do not affect shipped App Store apps.

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

These Swift language features cannot currently be represented in the generated bindings:

| Pattern | Why | Workaround |
|---------|-----|------------|
| **Protocols with Associated Types (PATs)** | Associated types create open-ended generic requirements that don't map to C# interfaces | Use concrete types directly instead of the protocol |
| **Protocols with Self requirements** | `Self` in protocol signatures requires the conforming type to be known at compile time | Use the concrete conforming type |
| **Typed throws (`throws(ErrorType)`)** | The error type resolution requires runtime metadata not yet available at generation time | Untyped `throws` methods work; typed throws methods are skipped |
| **Actors (deep isolation)** | Actor isolation model doesn't map to .NET threading | Actor methods are callable but without isolation enforcement |
| **`@MainActor` constraints** | Thread affinity doesn't map cleanly | Dispatch manually to the main thread |
| **Combine / `@Published`** | Reactive paradigm mismatch | Use async/await patterns instead |
| **8+ element tuples** | C# `ValueTuple` nesting complexity | Restructure the API |
| **`async throws` closures at runtime** | Binding generation works but runtime blocked by Mono JIT | Wait for upstream fix |
| **Deep SwiftUI state** | `@State`/`@Binding`/`@Environment` don't map to C# | Use the bridge for presentation only |

## Non-Blittable Type Limitation

.NET's `CallConvSwift` requires all P/Invoke parameters to be blittable (directly memory-mappable). Types like `SwiftOptional<T>`, `SafeHandle`, and managed strings are not blittable.

The generator works around this with Swift wrapper functions and `IntPtr` + manual marshalling. This is transparent but means some APIs route through an extra layer.

## SafeHandle in Async P/Invoke

The .NET runtime doesn't preserve `SafeHandle` references across async continuations. The generator works around this with singleton detection and `IntPtr` conversion, but edge cases with certain class hierarchies may exist.

## Generator Limitations

These are specific gaps in what the generator can represent, even for otherwise supported types:

### String enum raw values

Swift enums with `String` or `Int` raw values (e.g., `enum ErrorCode: Int`) generate correctly as C# enums, but the **raw values use case names instead of the actual values**. The compiled ABI JSON and `.swiftinterface` files don't include per-case raw values — they're compiled away. This means `ErrorCode.notFound` might map to `0` in C# instead of its actual Swift raw value.

**Impact**: Affects libraries like GRDB (`ResultCode`) and CryptoSwift. Enum cases are usable for pattern matching but raw value comparisons may be incorrect.

### `UnsafePointer<T>` parameters

Swift methods that take `UnsafePointer<T>`, `UnsafeMutablePointer<T>`, or similar unsafe pointer types fall back to an opaque `object` type in C#. The generator can't project concrete pointer element types.

**Impact**: Methods with pointer parameters are skipped. This is uncommon in public library APIs but appears in performance-oriented code (e.g., buffer manipulation).

### Optional protocol types in closures

Closures that take or return `Optional<any Protocol>` are skipped. The runtime marshalling layer can't currently unwrap optional existential containers inside closure callbacks.

**Impact**: Affects a small number of callback-heavy APIs. Non-optional protocol parameters in closures work.

### Existential arguments in generic containers

Methods taking parameters like `Array<any Protocol>` or `Dictionary<String, any Protocol>` — a protocol existential nested inside a generic container — are skipped. Each existential in a container needs per-element boxing that the marshalling layer doesn't yet support.

**Impact**: ~26 methods across libraries like Nuke and Lottie. Non-generic existential parameters (`any Protocol` directly) work.

### Primitive types in generic constraints

When a Swift generic is instantiated with `Int`, `Bool`, `Double`, `String`, or `URL`, the C# binding fails because these map to .NET primitives (`long`, `bool`, `double`) or Foundation types (`NSUrl`) that can't implement the required `ISwiftObject` interface.

**Impact**: ~5 methods across tested libraries. Rare in practice.

## Coverage Gaps

The generator achieves 88–99% member coverage across tested libraries. Beyond the generator limitations above, methods are also skipped for:

- **Unsupported operators** — compound assignment (`+=`, `-=`) and overflow shift (`&<<`, `&>>`) have no C# equivalent
- **Generic protocol closure signatures** — closures parameterized by a protocol's associated types
- **Unresolved type references** — types the generator can't resolve (e.g., types from modules not included in the generation pass and not covered by cross-module extension handling) fall back to `object`
- **Internal methods** — correctly excluded; Swift's `@usableFromInline internal` members aren't accessible from the wrapper

The binding report (`binding-report.json`) in the output directory documents exactly which members were skipped and why. The generator summary also prints skip counts grouped by reason.

## Swift Wrapper Escape Hatch

For APIs that can't be directly bound (due to .NET runtime limitations or Swift complexity), the generator supports a Swift wrapper fallback:

```
Normal API:      C# → P/Invoke → Swift dylib
Edge case API:   C# → P/Invoke → Swift wrapper → Swift dylib
```

The Swift compiler is the only entity guaranteed to understand the Swift ABI perfectly. When .NET's JIT or marshalling fails, delegating to Swift-side code is the architecturally correct approach.

---

## Non-Goals

These are explicitly out of scope to maintain focus:

| Non-Goal | Rationale |
|----------|-----------|
| **C# → Swift bindings** | Reverse direction (calling C# from Swift) is a separate problem |
| **Windows/Linux support** | Apple platforms only — these are Apple-specific frameworks |
| **Objective-C bridging** | Existing tools (Objective Sharpie, Slim Bindings) handle ObjC |
| **Deep SwiftUI state management** | `@State`/`@Binding`/`@Environment` semantics don't map to C#; the auto-generated bridge covers View instantiation |

---

## Next Steps

- **[Troubleshooting](Troubleshooting.md)** — Solutions for specific errors
- **[Supported Features](Supported-Features.md)** — What does work
- **[Architecture](Architecture.md)** — How the generator handles these constraints
