# Supported Features

## Type System

| Swift Feature | C# Projection | Notes |
|---------------|---------------|-------|
| **Classes** | C# class with `IDisposable` | ARC via SafeHandle. Constructors, properties, methods all projected. |
| **Structs (frozen)** | C# struct or class | Blittable frozen structs → C# struct. Non-blittable (contains ref types) → C# class with buffer. |
| **Structs (non-frozen)** | C# class with SafeHandle | Opaque payload — size not known at compile time. |
| **Enums** | C# class | Associated values, raw representable, `init?(rawValue:)` as `TryFromRawValue()`. |
| **Protocols** | C# interface + proxy class | Interface for calling Swift, proxy class for implementing from C#. |
| **Generics** | Bound generics | `Array<Int>`, `Optional<String>`, generic classes/enums. Unbound type parameters in properties/methods/constructors. |
| **Actors** | C# class | Detected via Actor conformance. Isolation enforcement not yet projected. |
| **Existential containers** | `any Protocol` → interface | Protocol composition types supported. |

## Members

| Swift Feature | C# Projection | Notes |
|---------------|---------------|-------|
| **Methods** | Instance and static methods | Naming follows C# conventions (verb prefix for actions). |
| **Properties** | C# properties | Getters and setters. Witness dispatch for protocol properties. |
| **Constructors** | C# constructors | Including failable `init?` → `TryCreate()` factory method. |
| **Async methods** | `Task<T>` / `Task` | Via Swift wrapper generation with `@_cdecl` callback. |
| **Operators** | C# operator overloads | Arithmetic (`+` `-` `*` `/` `%`), comparison (`==` `!=` `<` `>` `<=` `>=`), bitwise (`&` `^` `<<` `>>`), unary (`!` `~`). Automatic pair synthesis for `==`/`!=`, `<`/`>`, `<=`/`>=`. |
| **Subscripts** | C# indexers (`this[key]`) | |
| **Inout parameters** | `ref` parameters | |
| **Closures** | `Action<T>` / `Func<T>` | `@escaping`, `@convention(c)`, async, and throwing closures. Primitive args via Cdecl expansion. |
| **Tuples** | `ValueTuple` | 1–7 elements. Named elements preserved. |

## Type Conversions

Method signatures are automatically converted to idiomatic C# types:

| Swift Type | C# Type (in methods) | Notes |
|------------|----------------------|-------|
| `String` | `string` | Automatic `SwiftString` ↔ `string` marshalling |
| `Array<T>` | `IReadOnlyList<T>` (return) / `IEnumerable<T>` (param) | .NET collection interfaces |
| `Optional<T>` | `T?` | C# nullable syntax |
| `Int` / `Int32` / `Int64` | `nint` / `int` / `long` | .NET numeric types |
| `Bool` | `bool` | Direct mapping |
| `Float` / `Double` | `float` / `double` | Direct mapping |
| `URL` | `NSUrl` | Foundation type |
| `Date` | `DateTimeOffset` | .NET date type |
| `UUID` | `Guid` | .NET GUID |
| `UnsafePointer<T>`, `OpaquePointer` | `IntPtr` | All Swift pointer types |

> **Properties** retain Swift wrapper types (`SwiftString`, `SwiftArray<T>`) for getter/setter consistency. **Methods** use the idiomatic conversions above.

## Protocol Support

Protocols are projected as C# interfaces. You can:

1. **Call Swift protocol members** through the generated interface
2. **Implement Swift protocols from C#** using the generated proxy class

```csharp
// Implement a Swift protocol in C#
public class MyProcessor : IImageProcessing
{
    public SwiftString Identifier => new SwiftString("my-processor");
    public UIImage? Process(UIImage image) => image;
}

// Pass it to Swift code that expects the protocol
var proxy = new ImageProcessingProxy(new MyProcessor());
```

### Witness Table Dispatch

Protocol properties and methods are dispatched through Swift's witness table mechanism:
- **Blittable types** (Int, Bool, Double, etc.) — fully supported for getters and setters
- **String types** — fully supported via UTF-8 slice marshalling
- **Mutating methods, throws, async** — not yet supported through witness dispatch

## Async Support

Swift async methods are projected as C# `Task<T>` / `Task`:

```csharp
// Swift: func loadImage(_ url: URL) async -> UIImage
var image = await pipeline.LoadImage(url);
```

The generator creates a Swift wrapper function that bridges the async boundary using a `@_cdecl` callback. The C# side uses `TaskCompletionSource<T>` to convert the callback into a standard .NET `Task`.

Supported async return types: `void`, primitives, `String`, `Array<T>`, classes, enums, structs.

## Closures

Closures are projected as `Action<T>` (void return) or `Func<T, R>` (with return):

```csharp
// Swift: func onComplete(_ handler: @escaping (Int, Bool) -> Void)
obj.OnComplete((count, success) => {
    Console.WriteLine($"Done: {count}, success: {success}");
});
```

- **`@convention(c)` closures** — direct Cdecl mapping
- **`@escaping` closures with primitive args** — Cdecl expansion (Mono JIT safe)
- **Async and throwing closures** — callback-based bridging
- **Closures with String/class/struct args** — legacy CallConvSwift path

## Enum Support

Swift enums are projected as C# classes with static factory methods:

```csharp
// Swift: enum LogLevel: String { case debug = "[DEBUG]"; case error = "[ERROR]" }
var level = LogLevel.Debug;
string raw = level.RawValue; // "[DEBUG]"

// Swift: enum Result<T> { case success(T); case failure(Error) }
var result = Result.Success(42);
```

- **Simple enums** — static readonly instances
- **Raw representable** — `RawValue` property + `TryFromRawValue()` factory
- **Associated values** — factory methods with parameters

## Real-World Coverage

The generator produces 0 errors across **25 production Swift libraries** including Nuke, Alamofire, CryptoSwift, Lottie, BlinkID, all Stripe frameworks, Mappedin, Mixpanel, and more.

Four libraries have full test apps with runtime validation on iOS Simulator:

| Library | Types Bound | Member Coverage | Runtime Validated |
|---------|-------------|-----------------|-------------------|
| **Nuke** | 60/60 (100%) | 295/352 (83.8%) | Yes (9 tests) |
| **BlinkID** | 116/116 (100%) | 609/619 (98.4%) | Yes (6 tests) |
| **Lottie** | 77/80 (96%) | 345/475 (72.6%) | Yes (9 tests) |
| **CryptoSwift** | 103/103 (100%) | 429/501 (85.6%) | Yes (10 tests) |

Skipped members are primarily exotic patterns: existential arguments in bound generics, Combine publishers, unsatisfied generic constraints, and compound assignment operators with no C# equivalent.

---

## Next Steps

- **[SwiftUI Interop](SwiftUI-Interop)** — How SwiftUI views are bridged to .NET
- **[Known Limitations](Known-Limitations)** — What's not supported yet
- **[Architecture](Architecture)** — How the generator handles all of this internally
