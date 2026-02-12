# Swift Bindings for .NET

**Automatically generate C# bindings from compiled Swift libraries.**

Swift Bindings reads the ABI metadata from a compiled Swift framework (`.xcframework`) and generates idiomatic C# that calls directly into the Swift dylib via P/Invoke. No Objective-C bridging headers, no manual proxy code, no Objective Sharpie.

```
Swift framework (.xcframework)  →  SwiftBindings tool  →  C# bindings + NuGet package
```

## Why This Exists

Apple is moving away from Objective-C. More frameworks ship as Swift-only every year — StoreKit 2, SwiftUI, WeatherKit, App Intents, Swift Charts — and third-party libraries increasingly drop ObjC support entirely.

Without Swift interop, .NET on iOS becomes progressively less capable with each Xcode release.

The existing approaches all require a human to manually translate between Swift and C#, method by method:

| Approach | Pain Point |
|----------|------------|
| Objective Sharpie | Requires `@objc` proxy + bridging headers. Fails on Swift-only APIs. Hasn't kept pace with modern Xcode. |
| Native Library Interop | Requires hand-written C wrappers + C# declarations. Weeks of work for large libraries. |
| Raw P/Invoke | Requires deep Swift ABI knowledge. Not realistic beyond a handful of functions. |

Swift Bindings automates the entire process. You point it at a compiled framework and get back a working C# binding.

## Quick Example

```csharp
// Nuke (popular Swift image library), fully generated binding
var pipeline = ImagePipeline.Shared;
var request = new ImageRequest("https://picsum.photos/200/200");
var image = await pipeline.Image(request);
Console.WriteLine($"Image loaded: {image.Size.Width}x{image.Size.Height}");
```

No manual wrappers. This is generated C# calling Swift directly.

## What It Handles

The generator covers the full breadth of Swift's type system:

- Classes with ARC, structs (frozen and non-frozen), enums with associated values
- Protocols with interface generation, proxy classes, and witness table dispatch
- Generics, async/await, closures, tuples, operators, subscripts
- SwiftUI Views via automatic UIHostingController bridge generation
- Idiomatic type conversions (`String` → `string`, `Array<T>` → `IReadOnlyList<T>`, `Optional<T>` → `T?`)

## Validated Against Real Libraries

| Library | Errors | Member Coverage |
|---------|--------|-----------------|
| Nuke (image loading) | 0 | 94.4% |
| BlinkID (document scanning) | 0 | 99.1% |
| Lottie (animation) | 0 | 90.4% |
| CryptoSwift (cryptography) | 0 | 88.0% |

## Where This Comes From

This project is a fork of Microsoft's [`dotnet/runtimelab` (feature/swift-bindings branch)](https://github.com/dotnet/runtimelab/tree/feature/swift-bindings) — an experimental effort that established the foundational architecture. That experiment went inactive in an early state, handling only basic classes, structs, and simple method signatures.

This fork extends the generator substantially: 70+ phases of development have added protocols, generics, closures, async, SwiftUI bridging, and much more — validated against real-world libraries with zero generator errors.

## Next Steps

- **[Getting Started](Getting-Started)** — Set up your first binding
- **[Supported Features](Supported-Features)** — What Swift features are covered
- **[Architecture](Architecture)** — How the generator works under the hood
