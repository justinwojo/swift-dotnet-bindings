# Swift Bindings for .NET

**Automatically generate C# bindings from compiled Swift libraries. No proxy layers. No Objective-C bridging headers. No manual wrapper code.**

Swift Bindings reads the ABI metadata from a compiled Swift framework and produces idiomatic C# that calls directly into the Swift dylib via P/Invoke. The generated bindings handle memory management (ARC), async methods, closures, generics, protocols, and more — so you can consume Swift libraries from .NET the same way you'd consume a NuGet package.

---

## Why This Exists

Apple is moving away from Objective-C. Every year, more frameworks ship as Swift-only with no Objective-C equivalent:

- **StoreKit 2** — Swift-only in-app purchase API
- **SwiftUI** — Swift-only UI framework
- **WeatherKit**, **App Intents**, **Swift Charts** — all Swift-only
- Third-party libraries increasingly drop ObjC support entirely

Without Swift interop, **.NET on iOS becomes progressively less capable with each Xcode release.**

Today's options — Objective Sharpie, Native Library Interop (Slim Bindings), hand-written P/Invoke — all require a human to manually translate between Swift and C#, method by method, type by type. They don't scale, don't support Swift-only APIs, and the tooling hasn't kept pace with modern Xcode.

Swift Bindings automates the entire process:

```
Traditional approach:
  Swift API → manual Swift proxy → @objc headers → Objective Sharpie → C# binding
  (weeks of work, fragile, limited to ObjC-compatible types)

Swift Bindings approach:
  Swift framework (.xcframework) → SwiftBindings tool → C# binding
  (automated, supports Swift-native types, minutes not weeks)
```

### Where This Project Comes From

This project is a fork of Microsoft's [`dotnet/runtimelab` (feature/swift-bindings branch)](https://github.com/dotnet/runtimelab/tree/feature/swift-bindings) — an experimental effort that established the foundational architecture (ABI JSON parsing, Swift symbol demangling, type database, code emitter) but was never intended as a shipping product. Development went inactive with support limited to basic classes, structs, and simple method signatures.

This fork extends the generator substantially. Over 70 phases of development have added protocols, generics, closures, async, SwiftUI bridging, and much more — validated against real-world libraries with zero generator errors and full runtime test suites.

---

## What It Can Do

The generator handles the full breadth of Swift's type system:

- **Classes** with automatic reference counting (ARC) via SafeHandle
- **Structs** (frozen and non-frozen), **enums** with associated values and raw types
- **Protocols** with interface generation, proxy classes, and witness table dispatch
- **Generics** — bound generics, generic enums and classes, unbound type parameters
- **Async methods** via Swift wrapper generation with C# `await` support
- **Closures** (`@convention(c)`, `@escaping`) with automatic delegate marshalling
- **Tuples**, **operators**, **subscripts**, **inout parameters**, **failable initializers**
- **Existential containers** (`any Protocol`) and protocol composition types
- **SwiftUI Views** — automatic UIHostingController bridge generation

Method signatures are automatically converted to idiomatic C# types — `String` → `string`, `Array<T>` → `IReadOnlyList<T>`, `Optional<T>` → `T?`, and more. See the [full type conversion table](docs/Supported-Features.md#type-conversions) and [complete feature reference](docs/Supported-Features.md).

### Real-World Validation

The generator produces **zero compilation errors** across every library tested:

| Library | Purpose | Member Coverage | Runtime Tests |
|---------|---------|-----------------|---------------|
| **Nuke** | Image loading | 94.4% | Validated |
| **BlinkID** | Document scanning SDK | 99.1% | 18/18 passing |
| **Lottie** | Animation framework | 90.4% | 15/15 passing |

### Examples

**Async image loading with [Nuke](https://github.com/kean/Nuke):**

```csharp
var pipeline = ImagePipeline.Shared;
var request = new ImageRequest("https://picsum.photos/200/200");
var image = await pipeline.Image(request);
Console.WriteLine($"Image loaded: {image.Size.Width}x{image.Size.Height}");
```

**Implementing a Swift protocol from C#:**

```csharp
public class MyImageProcessor : ISwiftImageProcessing
{
    public SwiftString Identifier => new SwiftString("my-processor");
    public UIImage? Process(UIImage image) => image;
}

var proxy = new ImageProcessingProxy(new MyImageProcessor());
```

No proxy libraries. No bridging headers. Generated C# calling Swift directly — validated on iOS Simulator.

---

## SwiftUI Interop

SwiftUI Views can't be bound through conventional interop — they rely on opaque return types, property wrappers, and a declarative rendering pipeline with no C# equivalent.

Swift Bindings generates a bridge layer that wraps SwiftUI Views in `UIHostingController`, exposing them as `UIViewController` instances that .NET can embed in any UIKit-based layout (including .NET MAUI). This bridge generation is fully automatic — the generator analyzes View initializer parameters and produces the correct interop code for primitives, strings, closures, enums, class references, and async factory patterns.

For customization options (bridge hints, constructor selection, import overrides), see the [SwiftUI Interop docs](docs/SwiftUI-Interop.md).

---

## Getting Started

```bash
# 1. Create a binding project
dotnet new swift-binding -n MyLibrary.Bindings

# 2. Add your xcframework to the project

# 3. Build — generates bindings and produces a NuGet package
dotnet build

# 4. Consume in any .NET iOS/MAUI app
dotnet add package MyLibrary.Bindings
```

For prerequisites, CLI usage, and a full walkthrough, see the [Getting Started guide](docs/Getting-Started.md).

---

## Let AI Create Your Binding

The repository includes structured scripts and diagnostic reports designed so AI coding assistants (Claude Code, Codex, etc.) can generate bindings, resolve issues, build test apps, and validate on a simulator — automatically.

The vision: **point an AI agent at your Swift framework, and get back a working, tested NuGet package.**

---

## Documentation

| Page | Description |
|------|-------------|
| [Getting Started](docs/Getting-Started.md) | Prerequisites, installation, first binding walkthrough |
| [Supported Features](docs/Supported-Features.md) | Full feature reference with type conversion tables |
| [SwiftUI Interop](docs/SwiftUI-Interop.md) | SwiftUI bridge usage, bridge hints, async views |
| [Customization](docs/Customization.md) | CLI options, MSBuild properties, namespace control |
| [Troubleshooting](docs/Troubleshooting.md) | Error codes, common issues, binding report analysis |
| [Known Limitations](docs/Known-Limitations.md) | Platform requirements, Mono JIT workarounds, unsupported patterns |
| [Architecture](docs/Architecture.md) | Generator pipeline, type mapping, memory management |

---

## Known Limitations

Swift Bindings targets .NET 10 on Apple platforms, which currently uses the Mono runtime. Mono's JIT compiler has a known defect that causes crashes with `CallConvSwift` in certain P/Invoke frame types. Four transparent workarounds are built into the generator and runtime — generated bindings work correctly without manual intervention.

For full details, see [Known Limitations](docs/Known-Limitations.md).

---

## Project Status

Swift Bindings is under active development. The core generator is functional and validated against real-world libraries. See [`north-star.md`](north-star.md) for the full technical roadmap.

| Milestone | Status |
|-----------|--------|
| Core type system (classes, structs, enums, generics) | Complete |
| Protocols and witness dispatch | Complete |
| Async method support | Complete |
| Closures, tuples, operators | Complete |
| SwiftUI bridge generation | Complete |
| Real-world library validation | 5 libraries, 0 errors |
| MSBuild SDK and project templates | In progress |
| NuGet packaging automation | In progress |
| Swift Package Manager integration | Planned |

---

## Reporting Issues

Please [open an issue](../../issues) with:

1. **Generator logs** — run with `-v 2` for verbose output
2. **The binding report** — `binding-report.json` from the output directory
3. **The xcframework** (if possible) — or at minimum the ABI JSON and TBD file

See [Troubleshooting](docs/Troubleshooting.md) for common issues and solutions.

---

## License

Licensed under the [MIT License](LICENSE.TXT).

Originally developed by Microsoft Corporation as part of [`dotnet/runtimelab`](https://github.com/dotnet/runtimelab/tree/feature/swift-bindings). Now maintained and actively developed by Justin Wojciechowski.
