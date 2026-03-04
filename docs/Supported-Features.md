# Supported Features

## Type System

| Swift Feature | C# Projection | Notes |
|---------------|---------------|-------|
| **Classes** | C# class with `IDisposable` | ARC via SafeHandle. Constructors, properties, methods all projected. |
| **Structs (frozen)** | C# struct or class | Blittable frozen structs → C# struct. Non-blittable (contains ref types) → C# class with buffer. |
| **Structs (non-frozen)** | C# class with SafeHandle | Opaque payload — size not known at compile time. |
| **Enums** | C# class | Associated values, raw representable, `init?(rawValue:)` as `FromRawValue()`. |
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

| Swift Type | C# Type | Notes |
|------------|---------|-------|
| `String` | `string` | Automatic marshalling at the boundary |
| `Array<T>` | `IReadOnlyList<T>` (return) / `IEnumerable<T>` (param) | .NET collection interfaces |
| `Optional<T>` | `T?` | C# nullable syntax |
| `Int` / `Int32` / `Int64` | `nint` / `int` / `long` | .NET numeric types |
| `Bool` | `bool` | Direct mapping |
| `Float` / `Double` | `float` / `double` | Direct mapping |
| `URL` | `NSUrl` | Foundation type |
| `Date` | `DateTimeOffset` | .NET date type |
| `UUID` | `Guid` | .NET GUID |
| `UnsafePointer<T>`, `OpaquePointer` | `IntPtr` | All Swift pointer types |

> Both properties and methods use idiomatic C# types. Marshalling between Swift and C# types (`SwiftString` ↔ `string`, `SwiftArray<T>` ↔ `IReadOnlyList<T>`) is handled automatically in the getter/setter/method bodies.

## Protocol Support

Protocols are projected as C# interfaces. You can:

1. **Call Swift protocol members** through the generated interface
2. **Implement Swift protocols from C#** using the generated proxy class

```csharp
// Implement a Swift protocol in C#
public class MyProcessor : IImageProcessing
{
    public string Identifier => "my-processor";
    public UIImage? Process(UIImage image) => image;
}

// Pass it to Swift code that expects the protocol
var proxy = new ImageProcessingProxy(new MyProcessor());
```

### Witness Table Dispatch

Protocol properties and methods are dispatched through Swift's witness table mechanism:
- **Blittable types** (Int, Bool, Double, etc.) — fully supported for getters and setters
- **String types** — fully supported via UTF-8 slice marshalling
- **Collection returns** (`Array<T>`, `Dictionary<K,V>`, `Set<T>`) — fully supported via heap-allocated pointer dispatch
- **Optional existential returns** (`Optional<any Protocol>`) — fully supported via `if let` pattern dispatch
- **Mutating methods, async** — not yet supported through witness dispatch

## Async Support

Swift async methods are projected as C# `Task<T>` / `Task`:

```csharp
// Swift: func loadImage(_ url: URL) async -> UIImage
var image = await pipeline.LoadImageAsync(url);
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
- **Raw representable** — `RawValue` property + `FromRawValue()` factory
- **Associated values** — factory methods with parameters

## Real-World Coverage

The generator produces 0 errors across **40 production Swift libraries** (53 validation targets) including Nuke, Alamofire, Kingfisher, CryptoSwift, Lottie, BlinkID, GRDB, RxSwift, all Stripe frameworks, Mappedin, Mixpanel, and more. All 53 targets compile successfully.

Four libraries have full test apps with runtime validation on iOS Simulator:

| Library | Runtime Tests |
|---------|---------------|
| **Nuke** (image loading) | 9 tests |
| **BlinkID** (document scanning) | 6 tests |
| **Lottie** (animation) | 9 tests |
| **CryptoSwift** (cryptography) | 10 tests |

Skipped members are primarily exotic patterns: existential arguments in bound generics, Combine publishers, unsatisfied generic constraints, and compound assignment operators with no C# equivalent. The binding report (`binding-report.json`) documents exactly which members are skipped and why.

---

## Next Steps

- **[SwiftUI Interop](SwiftUI-Interop.md)** — How SwiftUI views are bridged to .NET
- **[Known Limitations](Known-Limitations.md)** — What's not supported yet
- **[Architecture](Architecture.md)** — How the generator handles all of this internally
