# Swift .NET Bindings

**Automatically generate C# bindings from compiled Swift libraries. No proxy layers. No Objective-C bridging headers. No manual wrapper code. No Objective Sharpie tooling.**

Swift Bindings reads the ABI metadata from a compiled Swift framework and produces idiomatic C# that calls directly into the Swift dylib via P/Invoke. The generated bindings handle memory management (ARC), async methods, closures, generics, protocols, and more — so you can consume Swift libraries from .NET the same way you'd consume a NuGet package.

---

## Why This Exists

Apple is moving away from Objective-C. Every year, more frameworks ship as Swift-only with no Objective-C equivalent:

- **StoreKit 2** — Swift-only in-app purchase API
- **SwiftUI** — Swift-only UI framework
- **WeatherKit**, **App Intents**, **Swift Charts** — all Swift-only
- Third-party libraries increasingly drop ObjC support entirely

Without Swift interop, **.NET on iOS becomes progressively less accessible each year.**

Today's options — Objective Sharpie and Native Library Interop (Slim Bindings) — require translating between Swift and C#, method by method, type by type. They don't scale, don't support Swift-only APIs, and the tooling hasn't kept pace with modern Xcode.

Swift Bindings automates the entire process:

```
Traditional approach:
  Swift API → manual Swift proxy → @objc headers → Objective Sharpie → C# binding
  (significant work, fragile, limited to ObjC-compatible types)

Swift Bindings approach:
  Swift framework (.xcframework) → SwiftBindings tool → C# binding
  (automated, supports Swift-native types, minutes not days)
```

### Where This Project Comes From

This project is a fork of Microsoft's [`dotnet/runtimelab` (feature/swift-bindings branch)](https://github.com/dotnet/runtimelab/tree/feature/swift-bindings) — an experimental effort that established the foundational architecture (ABI JSON parsing, Swift symbol demangling, type database, code emitter) but was never intended as a shipping product. Development went inactive with support limited to basic classes, structs, and simple method signatures.

This fork extends the generator substantially — adding protocols, generics, closures, async, SwiftUI bridging, and much more. The generator produces zero compilation errors across 25 real-world libraries, with select libraries validated end-to-end on a .Net for iOS app.

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
- **XML doc comments** — Swift documentation automatically extracted and converted to C# IntelliSense docs

Method signatures are automatically converted to idiomatic C# types — `String` → `string`, `Array<T>` → `IReadOnlyList<T>`, `Optional<T>` → `T?`, and more. See the [full type conversion table](docs/Supported-Features.md#type-conversions) and [complete feature reference](docs/Supported-Features.md).

### Real-World Validation

The generator produces **zero compilation errors** across 25 libraries spanning image loading, payments, animation, networking, document scanning, analytics, and more:

| Category | Libraries |
|----------|-----------|
| **Image & Animation** | Nuke, Lottie |
| **Document Scanning** | BlinkID, MicroblinkPlatform |
| **Cryptography** | CryptoSwift |
| **Networking & Analytics** | Alamofire, Mixpanel |
| **Payments** | Stripe (14 frameworks — StripeCore, StripePaymentSheet, StripePayments, and more) |
| **Hardware & Mapping** | SmartCardIO, BRLMPrinterKit, Mappedin |

Select libraries (Nuke, BlinkID, Lottie, CryptoSwift) have been functionally validated in test apps running on iOS Simulator.

### Examples

**Async image loading with [Nuke](https://github.com/kean/Nuke):**

```csharp
// Load an image asynchronously
var pipeline = ImagePipeline.Shared;
var request = new ImageRequest("https://example.com/photo.jpg");
UIImage image = await pipeline.GetImageAsync(request);

// Check the cache first
ImageContainer? cached = pipeline.Cache.GetCachedImage(request);
```

**Animation playback with [Lottie](https://github.com/airbnb/lottie-ios):**

```csharp
// Play with a completion callback and nullable loop mode
var animationView = new LottieAnimationView();
animationView.Animation = myAnimation;
animationView.Play(
    fromProgress: 0.0, toProgress: 1.0,
    loopMode: LottieLoopMode.Loop,
    completion: finished => Console.WriteLine($"Done: {finished}")
);

// Playback control
animationView.Pause();
bool playing = animationView.IsAnimationPlaying;
double progress = animationView.CurrentProgress;
```

All generated C# — no proxy libraries, no bridging headers, no manual wrapper code.

---

## SwiftUI Interop

SwiftUI Views can't be bound through conventional interop — they rely on opaque return types, property wrappers, and a declarative rendering pipeline with no C# equivalent.

Swift Bindings generates a bridge layer that wraps SwiftUI Views in `UIHostingController`, exposing them as `UIViewController` instances that .NET can embed in any UIKit-based layout (including .NET MAUI). This bridge generation is fully automatic — the generator analyzes View initializer parameters and produces the correct interop code for primitives, strings, closures, enums, class references, and async factory patterns.

For customization options (bridge hints, constructor selection, import overrides), see the [SwiftUI Interop docs](docs/SwiftUI-Interop.md).

---

## Getting Started

```bash
# 1. Install the project template and MSBuild SDK
dotnet new install Swift.Bindings.Templates

# 2. Create a binding project
dotnet new swift-binding -n MyLibrary.Bindings

# 3. Copy your xcframework into the project directory
cp -r /path/to/MyLibrary.xcframework MyLibrary.Bindings/

# 4. Build — generates bindings and produces a NuGet package
cd MyLibrary.Bindings && dotnet build

# 5. Consume in any .NET iOS/MAUI app
dotnet add package MyLibrary.Bindings
```

For prerequisites, CLI usage, and a full walkthrough, see the [Getting Started guide](docs/Getting-Started.md).

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

Swift Bindings is under active development. The core generator is functional and validated against real-world libraries.

| Milestone | Status |
|-----------|--------|
| Core type system (classes, structs, enums, generics) | Complete |
| Protocols and witness dispatch | Complete |
| Async method support | Complete |
| Closures, tuples, operators | Complete |
| SwiftUI bridge generation | Complete |
| Real-world library validation | 25 libraries, 0 errors |
| MSBuild SDK and project templates | Complete |
| NuGet packaging automation | Complete |
| AI agent skills (Claude Code, Codex) | Planned |
| Swift Package Manager integration | Planned |

Extensively tested with **2,400+ unit tests**, **700+ integration tests**, and **200+ end-to-end runtime tests** validated on iOS Simulator — in addition to the 25-library validation above.

---

## Contributing

Swift Bindings is in its early stages and evolving rapidly. The best way to contribute right now is through **issue reports**:

- **Binding errors** — if the generator produces C# that doesn't compile for your library, [open an issue](../../issues) with the details below
- **Feature requests** — if there's a Swift pattern or workflow the generator doesn't handle, let us know
- **Bug reports** — unexpected crashes, incorrect generated code, or runtime failures

This helps prioritize the most impactful work across the many libraries and Swift patterns in the wild.

**Pull requests** are welcome, but please open an issue first to discuss the change — especially for anything beyond a trivial fix. The generator internals are changing frequently, and coordinating upfront avoids wasted effort on both sides. Once the project reaches a more stable state, we'll formalize a more open contribution workflow.

### Reporting Issues

When filing an issue, please include:

1. **Generator logs** — run with `-v 2` for verbose output
2. **The binding report** — `binding-report.json` from the output directory
3. **The xcframework** (if possible) — or at minimum the ABI JSON and TBD file

See [Troubleshooting](docs/Troubleshooting.md) for common issues and solutions.

---

## License

Licensed under the [MIT License](LICENSE.TXT).

Originally developed by Microsoft Corporation as part of [`dotnet/runtimelab`](https://github.com/dotnet/runtimelab/tree/feature/swift-bindings). Now maintained and actively developed by Justin Wojciechowski.
