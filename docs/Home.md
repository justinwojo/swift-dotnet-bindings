# Swift Bindings for .NET

**Automatically generate C# bindings from compiled Swift libraries.**

Swift Bindings reads a compiled framework (`.xcframework`) and generates idiomatic C# bindings. For Swift libraries, it produces direct P/Invoke code from ABI metadata. For Objective-C libraries, it produces standard `ApiDefinition.cs` + `StructsAndEnums.cs` binding definitions from clang AST parsing. No Objective Sharpie, no manual proxy code.

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
var image = await pipeline.ImageAsync(request);
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

The compile gate passes for all 88 targets across 46 libraries (53 Swift, 34 ObjC, 1 mixed) — including Alamofire, Nuke, Kingfisher, Lottie, CryptoSwift, all Stripe frameworks, GRDB, RxSwift, BlinkID, Realm (ObjC), Stripe3DS2 (ObjC), the full Firebase/Google SDK family (28 ObjC targets), SDWebImage, CocoaLumberjack, and more. See [Supported Features](Supported-Features.md) for details.

This project is a fork of Microsoft's [`dotnet/runtimelab` (feature/swift-bindings branch)](https://github.com/dotnet/runtimelab/tree/feature/swift-bindings), substantially extended with protocols, generics, closures, async, SwiftUI bridging, and more. See [Architecture](Architecture.md) for the full history.

## Next Steps

- **[Getting Started](Getting-Started.md)** — Set up your first binding
- **[Supported Features](Supported-Features.md)** — What Swift features are covered
- **[How Bindings Map](How-Bindings-Map.md)** — Side-by-side Swift → C# examples
- **[Architecture](Architecture.md)** — How the generator works under the hood
