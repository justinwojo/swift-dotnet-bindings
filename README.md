# Swift + ObjC .NET Bindings

A single tool that generates .NET bindings from Swift, Objective-C, or mixed Apple frameworks.

### Swift Bindings
**Automatically generate C# bindings from compiled Swift libraries. No proxy layers. No Objective-C bridging headers. No manual wrapper code.**

Swift Bindings reads the ABI metadata from a compiled Swift framework and produces idiomatic C# that calls directly into the Swift dylib via P/Invoke. The generated bindings handle memory management (ARC), async methods, closures, generics, protocols, and more, so you can consume Swift libraries from .NET the same way you'd consume a NuGet package.

### Objective-C Bindings
Also handles pure Objective-C frameworks — a full replacement for Objective Sharpie with ready-to-compile output. See [below](#objective-c-support).

For the story behind this project — why I created it, the process of building it, and lessons learned along the way — check out the [full write-up on my blog](https://wojosoftware.com/blog/swift-dotnet-bindings/).

## Why This Exists

Apple is moving away from Objective-C. Every year, more frameworks ship as Swift-only with no Objective-C equivalent:

- **StoreKit 2** — Swift-only in-app purchase API
- **SwiftUI** — Swift-only UI framework
- **WeatherKit**, **App Intents**, **Swift Charts** — all Swift-only
- Third-party libraries increasingly drop ObjC support entirely

Without Swift interop, **.NET on Apple platforms becomes progressively less accessible each year.**

And for the Objective-C frameworks that remain, the de facto binding tool — Objective Sharpie — is no longer maintained and produces output requiring extensive manual cleanup (`[Verify]` attributes on every binding).

Swift Bindings automates the entire process.

**Traditional approach:**

```
Swift:  Swift API → manual proxy → @objc headers → Objective Sharpie → C# binding
        (fragile, limited to ObjC-compatible types)

ObjC:   ObjC framework → Objective Sharpie → C# binding
        (dozens of [Verify] attributes, manual .csproj scaffolding)
```

**With Swift Bindings:**

```
Any xcframework → SwiftBindings tool → C# binding
(automated, ready to compile)
```

### Where This Project Comes From

This project is a fork of Microsoft's [`dotnet/runtimelab` (feature/swift-bindings branch)](https://github.com/dotnet/runtimelab/tree/feature/swift-bindings) — an experimental effort that established the foundational architecture (ABI JSON parsing, Swift symbol demangling, type database, code emitter) but was never intended as a shipping product. Development went inactive with support limited to basic classes, structs, and simple method signatures.

Since forking, the project has grown from that proof-of-concept into a comprehensive binding generator — **800+ commits, ~140K lines of production code and ~150K lines of tests**.

## What It Can Do

The generator handles the full breadth of Swift's type system:

- **Classes** with automatic reference counting (ARC) via SafeHandle, including inheritance hierarchies
- **Structs** (frozen and non-frozen), **enums** with associated values, raw types, case construction, and pattern-matched inspection
- **Protocols** with interface generation, proxy classes, witness table dispatch, and protocol conformance on concrete types
- **Protocol extensions** — methods from protocol extensions injected onto conforming types, including generic extensions and extensions on foreign types (UIKit, etc.)
- **Generics** — bound generics, generic enums and classes, unbound type parameters, generic closures
- **Async methods** via Swift wrapper generation with C# `Task<T>` / `await` support, `CancellationToken` threading, and actor isolation (`@MainActor`)
- **Closures** (`@convention(c)`, `@escaping`) with automatic delegate marshalling, including closures with bound generic arguments and error propagation
- **Tuples**, **operators**, **subscripts**, **inout parameters**, **failable initializers**
- **Existential containers** (`any Protocol`) as parameters and return values, with concrete-type boxing via `IExistentialBoxable`
- **Type projections** — `String` → `string`, `Array<T>` → `IReadOnlyList<T>`, `Set<T>` → `IReadOnlySet<T>`, `Dictionary<K,V>` → `IReadOnlyDictionary<K,V>`, `Data` → `byte[]`, `Decimal` → `decimal`, `Optional<T>` → `T?`
- **Convenience overloads** — `nint`/`nuint` parameters get `int`/`uint` overloads, methods with default parameters get reduced-arity overloads
- **SwiftUI Views** — automatic `UIHostingController` bridge with two-way state updates, view modifier chains, and async factory patterns
- **XML doc comments** — Swift documentation automatically extracted and converted to C# IntelliSense docs

See the [full type conversion table](https://github.com/justinwojo/swift-dotnet-bindings/wiki/Supported-Features#type-conversions) and [complete feature reference](https://github.com/justinwojo/swift-dotnet-bindings/wiki/Supported-Features).

### Objective-C Support

The generator is also a **full replacement for Objective Sharpie** for pure Objective-C frameworks. Point it at any xcframework with ObjC headers:

- Parses all public headers via `clang -ast-dump=json` (uses Xcode's built-in clang)
- Emits standard `ApiDefinition.cs` + `StructsAndEnums.cs` binding definitions
- Generates a ready-to-build `.csproj` — produces a NuGet package compatible with .NET MAUI
- **No `[Verify]` attributes** — output compiles without manual review (Sharpie typically requires dozens per library)

Supports protocol patterns (`[Protocol, Model]`, `WeakDelegate`/`Wrap`), idiomatic C# enums with prefix stripping, availability attributes, foreign-type categories, designated initializer detection, pointer/out-param mapping, and rich XML doc comments.

**Mixed frameworks** (Swift + ObjC) are also supported with automatic type-level dedup. Framework type is auto-detected — no flags needed, same `dotnet build` workflow.

### Examples

**Async image loading with [Nuke](https://github.com/kean/Nuke):**

```csharp
// Load an image asynchronously
var pipeline = ImagePipeline.Shared;
var request = new ImageRequest("https://example.com/photo.jpg");
UIImage image = await pipeline.ImageAsync(request);

// Check the cache first
ImageContainer? cached = pipeline.CacheValue.CachedImage(request);
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

All generated C# — no manual wrapper code.

## Getting Started

**Requires**: macOS, [Xcode 26](https://developer.apple.com/xcode/) or later, and [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) with the platform workload for your target (e.g., `dotnet workload install ios`).

**Supported platforms**: iOS, macOS, Mac Catalyst, tvOS. The generator, MSBuild SDK, and runtime all support multi-platform targeting — generate bindings for any Apple platform with a single `--platform` flag or by changing the TFM.

```bash
# 1. Install the project template and MSBuild SDK
dotnet new install SwiftBindings.Templates

# 2. Create a binding project (use --platform for macos, tvos, or maccatalyst)
dotnet new swift-binding -n MyLibrary.Swift.iOS

# 3. Copy your xcframework into the project directory
cp -r /path/to/MyLibrary.xcframework MyLibrary.Swift.iOS/

# 4. Build from the project directory — generates bindings and compiles
dotnet build

# 5. Optionally, pack into a NuGet package for distribution
dotnet pack
```

For prerequisites, CLI usage, and a full walkthrough, see the [Getting Started guide](https://github.com/justinwojo/swift-dotnet-bindings/wiki/Getting-Started). Already installed? See the [Upgrading guide](https://github.com/justinwojo/swift-dotnet-bindings/wiki/Upgrading).

### Working with Swift Package Manager Libraries

Swift Bindings requires a compiled `.xcframework` as input. If you're working with a library distributed through Swift Package Manager (SPM), you'll need to build it into an xcframework first.

This is intentionally out of scope for Swift Bindings — SPM dependency resolution, multi-platform builds, and ABI-stable framework generation involve a significant amount of complexity that's orthogonal to binding generation. Keeping the concerns separate means each tool can evolve independently.

I maintain a standalone script for this: **[spm-to-xcframework](https://github.com/justinwojo/spm-to-xcframework)**

It handles the full conversion — resolving the package, building for device and simulator, enabling `BUILD_LIBRARY_FOR_DISTRIBUTION` for ABI stability, and producing a ready-to-use xcframework:

```bash
# Clone the tool
git clone https://github.com/justinwojo/spm-to-xcframework.git

# Build an xcframework from any SPM package
./spm-to-xcframework/spm-to-xcframework https://github.com/kean/Nuke --version 12.0.0

# Then generate bindings as usual
dotnet new swift-binding -n Nuke.Swift.iOS
cp -r Nuke.xcframework Nuke.Swift.iOS/
cd Nuke.Swift.iOS && dotnet build
```

### AI-Assisted Binding Creation

A [Claude Code skill](https://github.com/justinwojo/claude-skills) is available that walks you through the entire binding process — from SPM package or xcframework to a validated NuGet package. It checks prerequisites, builds xcframeworks, diagnoses errors using the latest docs, and optionally reviews the generated binding for completeness.

**Claude Code:**

```bash
# Add the skills marketplace
/plugin marketplace add justinwojo/claude-skills

# Install the skill
/plugin install swift-binding-assistant@justinwojo-claude-skills
```

Then just ask Claude: *"I want to create a Swift binding for [library name]"*

**Codex / other AI tools:** The skill definition is a standalone markdown file ([`swift-binding-assistant/SKILL.md`](https://github.com/justinwojo/claude-skills/blob/main/swift-binding-assistant/SKILL.md)). Copy its contents into your tool's custom instructions or system prompt to get the same guided workflow.

## SwiftUI Interop

SwiftUI Views can't be bound through conventional interop — they rely on opaque return types, property wrappers, and a declarative rendering pipeline with no C# equivalent.

Swift Bindings generates a bridge layer that wraps SwiftUI Views in `UIHostingController`, exposing them as `UIViewController` instances that .NET can embed in any UIKit-based layout (including .NET MAUI). This bridge generation is fully automatic — the generator analyzes View initializer parameters and produces the correct interop code for primitives, strings, closures, enums, class references, and async factory patterns. The bridge also supports two-way state updates via `Update{Param}()` methods, view modifier chains, and multi-level async view hierarchies with cycle detection.

For customization options (bridge hints, constructor selection, import overrides), see the [SwiftUI Interop docs](https://github.com/justinwojo/swift-dotnet-bindings/wiki/SwiftUI-Interop).

## Real-World Validation

Validated against **51 libraries (95 framework targets)** spanning image loading, payments, animation, networking, document scanning, analytics, and more:

| Category | Libraries |
|----------|-----------|
| **Image & Animation** | Nuke, Lottie, Kingfisher, SwiftyGif, FSPagerView |
| **Networking & Data** | Alamofire, Starscream, GRDB, KeychainAccess, ObjectMapper |
| **Payments** | Stripe (14 frameworks) |
| **UI Components** | SnapKit, SkeletonView, Parchment, SVGView, BonMot, CodeScanner, AlertToast |
| **Utilities** | CryptoSwift, DeviceKit, PhoneNumberKit, Swinject, RxSwift, XMLCoder |
| **ObjC Frameworks** | Realm, SDWebImage, CocoaLumberjack, MBProgressHUD, Firebase (28 targets) |

Select libraries (Nuke, Lottie, BlinkID, BlinkID UX, Stripe) have dedicated test apps with end-to-end runtime validation on iOS Simulator in the [swift-dotnet-packages](https://github.com/justinwojo/swift-dotnet-packages) repo. The generator also validates Nuke across macOS and tvOS targets.

## Documentation

Full documentation is available on the **[project wiki](https://github.com/justinwojo/swift-dotnet-bindings/wiki)**.

| Page | Description |
|------|-------------|
| [Getting Started](https://github.com/justinwojo/swift-dotnet-bindings/wiki/Getting-Started) | Prerequisites, installation, first binding walkthrough |
| [Supported Features](https://github.com/justinwojo/swift-dotnet-bindings/wiki/Supported-Features) | Full feature reference with type conversion tables |
| [SwiftUI Interop](https://github.com/justinwojo/swift-dotnet-bindings/wiki/SwiftUI-Interop) | SwiftUI bridge usage, bridge hints, async views |
| [Customization](https://github.com/justinwojo/swift-dotnet-bindings/wiki/Customization) | CLI options, MSBuild properties, namespace control |
| [Troubleshooting](https://github.com/justinwojo/swift-dotnet-bindings/wiki/Troubleshooting) | Error codes, common issues, binding report analysis |
| [Known Limitations](https://github.com/justinwojo/swift-dotnet-bindings/wiki/Known-Limitations) | Platform requirements, Mono JIT workarounds, unsupported patterns |
| [Architecture](https://github.com/justinwojo/swift-dotnet-bindings/wiki/Architecture) | Generator pipeline, type mapping, memory management |

## Known Limitations

Swift Bindings targets .NET 10 on Apple platforms. The vast majority of generated P/Invokes (94-98% for representative libraries) use standard C calling conventions and work identically everywhere. A small number of methods use `CallConvSwift`, which may encounter Mono JIT limitations on iOS/tvOS Simulator. **Device builds (NativeAOT) and macOS are unaffected.**

For full details, see [Known Limitations](https://github.com/justinwojo/swift-dotnet-bindings/wiki/Known-Limitations).

## Project Status

Swift Bindings is under active development. The core generator, MSBuild SDK, and NuGet packaging are all functional — backed by **9,000+ unit tests** and **1,285 runtime tests** passing on both iOS Simulator (Mono JIT) and physical device (NativeAOT).

## Contributing

The best way to contribute right now is through [issue reports](https://github.com/justinwojo/swift-dotnet-bindings/issues) — binding errors, feature requests, and bug reports help prioritize the most impactful work. Pull requests are welcome too, but please open an issue first to discuss the change.

See [CONTRIBUTING.md](.github/CONTRIBUTING.md) for development setup, PR guidelines, and details on the AI-assisted workflow used to build this project.

## Support This Project

This project takes a massive amount of effort to build and maintain, and the tooling used to develop it isn't cheap. If you find it useful, please consider [sponsoring the project](https://github.com/sponsors/justinwojo).

## License

Licensed under the [MIT License](https://github.com/justinwojo/swift-dotnet-bindings/blob/main/LICENSE).

Originally developed by Microsoft Corporation as part of [`dotnet/runtimelab`](https://github.com/dotnet/runtimelab/tree/feature/swift-bindings). Now maintained and actively developed by Justin Wojciechowski.
