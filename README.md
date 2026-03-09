# Swift + ObjC .NET Bindings

A single tool that generates .NET bindings from Swift, Objective-C, or mixed Apple frameworks.

### Swift Bindings
**Automatically generate C# bindings from compiled Swift libraries. No proxy layers. No Objective-C bridging headers. No manual wrapper code.**

Swift Bindings reads the ABI metadata from a compiled Swift framework and produces idiomatic C# that calls directly into the Swift dylib via P/Invoke. The generated bindings handle memory management (ARC), async methods, closures, generics, protocols, and more, so you can consume Swift libraries from .NET the same way you'd consume a NuGet package.

### Objective-C Bindings
**A full replacement for Objective Sharpie. Ready-to-compile output with no `[Verify]` cleanup required.**

Point the same tool at an Objective-C framework and it parses headers via clang, emits standard `ApiDefinition.cs` + `StructsAndEnums.cs` binding definitions, and generates a ready-to-build `.csproj` — no manual cleanup required. Drop any xcframework and the correct pipeline runs automatically.

### Support This Project

This project takes a massive amount of effort to build and maintain, and the tooling used to develop it isn't cheap. If you find it useful, please consider [sponsoring the project](https://github.com/sponsors/justinwojo).

---

## Why This Exists

Apple is moving away from Objective-C. Every year, more frameworks ship as Swift-only with no Objective-C equivalent:

- **StoreKit 2** — Swift-only in-app purchase API
- **SwiftUI** — Swift-only UI framework
- **WeatherKit**, **App Intents**, **Swift Charts** — all Swift-only
- Third-party libraries increasingly drop ObjC support entirely

Without Swift interop, **.NET on Apple platforms becomes progressively less accessible each year.**

And for the Objective-C frameworks that remain, the de facto binding tool — Objective Sharpie — hasn't been updated for modern Xcode and produces output requiring extensive manual cleanup (`[Verify]` attributes on every binding).

Swift Bindings automates the entire process:

```
Traditional approach (Swift):
  Swift API → manual Swift proxy → @objc headers → Objective Sharpie → C# binding
  (significant work, fragile, limited to ObjC-compatible types)

Traditional approach (Objective-C):
  ObjC framework → Objective Sharpie → C# binding
  (numerous [Verify] attributes to review, manual .csproj scaffolding)

Swift Bindings approach (both):
  Any xcframework → SwiftBindings tool → C# binding
  (automated, ready to compile)
```

### Where This Project Comes From

This project is a fork of Microsoft's [`dotnet/runtimelab` (feature/swift-bindings branch)](https://github.com/dotnet/runtimelab/tree/feature/swift-bindings) — an experimental effort that established the foundational architecture (ABI JSON parsing, Swift symbol demangling, type database, code emitter) but was never intended as a shipping product. Development went inactive with support limited to basic classes, structs, and simple method signatures.

Since forking, this project has grown from a proof-of-concept into a comprehensive binding generator — **550+ commits and ~280K net new lines of code (4.7x the original codebase)**. That breaks down to ~95K lines of production code (generator, runtime, SDK) backed by ~182K lines of tests across 6,500+ test cases. The generator now supports protocols, generics, closures, async, SwiftUI bridging, protocol extensions, existential containers, and much more — validated against 46 real-world libraries (88 framework targets — 53 Swift, 34 ObjC, 1 mixed), with select libraries tested end-to-end on .NET apps running on iOS and macOS.

---

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
- **Existential containers** (`any Protocol`) as parameters and return values, including in collections (`Dictionary<K, any Protocol>`)
- **Type projections** — `String` → `string`, `Array<T>` → `IReadOnlyList<T>`, `Set<T>` → `IReadOnlySet<T>`, `Dictionary<K,V>` → `IReadOnlyDictionary<K,V>`, `Data` → `byte[]`, `Optional<T>` → `T?`
- **Convenience overloads** — `nint`/`nuint` parameters get `int`/`uint` overloads, methods with default parameters get reduced-arity overloads
- **SwiftUI Views** — automatic `UIHostingController` bridge with `@State`/`@Binding` projection, theming support, and async factory patterns
- **XML doc comments** — Swift documentation automatically extracted and converted to C# IntelliSense docs

See the [full type conversion table](docs/Supported-Features.md#type-conversions) and [complete feature reference](docs/Supported-Features.md).

### Objective-C Support

The generator is also a **full replacement for Objective Sharpie** for pure Objective-C frameworks. Point it at any xcframework with ObjC headers:

- Parses all public headers via `clang -ast-dump=json` (uses Xcode's built-in clang)
- Emits standard `ApiDefinition.cs` + `StructsAndEnums.cs` binding definitions
- Generates a ready-to-build `.csproj` — produces a NuGet package compatible with .NET MAUI
- **No `[Verify]` attributes** — output compiles without manual review (Sharpie typically requires dozens per library)

Supports protocol patterns (`[Protocol, Model]`, `WeakDelegate`/`Wrap`), idiomatic C# enums with prefix stripping, availability attributes, foreign-type categories, designated initializer detection, pointer/out-param mapping, and rich XML doc comments.

**Mixed frameworks** (Swift + ObjC) are also supported with automatic type-level dedup. Framework type is auto-detected — no flags needed, same `dotnet build` workflow.

Validated against **34 ObjC framework targets** including Realm, Stripe3DS2, SDWebImage, CocoaLumberjack, MBProgressHUD, and the full Firebase/Google SDK family (28 targets).

### Real-World Validation

Validated against **46 libraries (88 framework targets — 53 Swift, 34 Objective-C, 1 mixed)** spanning image loading, payments, animation, networking, document scanning, analytics, and more:

| Category | Libraries |
|----------|-----------|
| **Image & Animation** | Nuke, Lottie, SwiftyGif, FSPagerView |
| **Networking & Data** | Alamofire, Starscream, GRDB, KeychainAccess, ObjectMapper |
| **Payments** | Stripe (14 frameworks) |
| **UI Components** | SnapKit, SkeletonView, Parchment, SVGView, BonMot |
| **Utilities** | CryptoSwift, DeviceKit, PhoneNumberKit, Swinject, RxSwift |
| **ObjC Frameworks** | Realm, SDWebImage, CocoaLumberjack, MBProgressHUD, Firebase (28 targets) |

Select libraries (Nuke, BlinkID, Lottie, CryptoSwift) have been functionally validated in test apps running on iOS Simulator. The generator also validates Nuke across macOS and tvOS targets.

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

All generated C# — no proxy libraries, no bridging headers, no manual wrapper code.

---

## SwiftUI Interop

SwiftUI Views can't be bound through conventional interop — they rely on opaque return types, property wrappers, and a declarative rendering pipeline with no C# equivalent.

Swift Bindings generates a bridge layer that wraps SwiftUI Views in `UIHostingController`, exposing them as `UIViewController` instances that .NET can embed in any UIKit-based layout (including .NET MAUI). This bridge generation is fully automatic — the generator analyzes View initializer parameters and produces the correct interop code for primitives, strings, closures, enums, class references, and async factory patterns. The bridge also supports `@State`/`@Binding` property projection, theme configuration (colors, fonts, sizes), and multi-level async view hierarchies with cycle detection.

For customization options (bridge hints, constructor selection, import overrides), see the [SwiftUI Interop docs](docs/SwiftUI-Interop.md).

---

## Getting Started

**Requires**: macOS, [Xcode 26](https://developer.apple.com/xcode/) or later, and [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) with the platform workload for your target (e.g., `dotnet workload install ios`).

**Supported platforms**: iOS, macOS, Mac Catalyst, tvOS. The generator, MSBuild SDK, and runtime all support multi-platform targeting — generate bindings for any Apple platform with a single `--platform` flag or by changing the TFM.

```bash
# 1. Install the project template and MSBuild SDK
dotnet new install SwiftBindings.Templates

# 2. Create a binding project (default: iOS; use --platform for others)
dotnet new swift-binding -n MyLibrary.Bindings
# dotnet new swift-binding -n MyLibrary.Bindings --platform macos

# 3. Copy your xcframework into the project directory
cp -r /path/to/MyLibrary.xcframework MyLibrary.Bindings/

# 4. Build — generates bindings and compiles the project
cd MyLibrary.Bindings && dotnet build

# 5. Pack into a NuGet package, then consume in any .NET Apple platform app
dotnet pack
dotnet add package MyLibrary.Bindings
```

For prerequisites, CLI usage, and a full walkthrough, see the [Getting Started guide](docs/Getting-Started.md).

## AI-Assisted Binding Creation

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

---

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
dotnet new swift-binding -n Nuke.Bindings
cp -r Nuke.xcframework Nuke.Bindings/
cd Nuke.Bindings && dotnet build
```

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

Swift Bindings targets .NET 10 on Apple platforms, which currently uses the Mono runtime. Mono's JIT compiler has a known defect that causes crashes with `CallConvSwift` in certain P/Invoke frame types. **This only affects Mono JIT builds (iOS/tvOS Simulator)** — device builds use NativeAOT and macOS native builds are unaffected. Four transparent workarounds are built into the generator and runtime — generated bindings work correctly without manual intervention.

For full details, see [Known Limitations](docs/Known-Limitations.md).

---

## Project Status

Swift Bindings is under active development. The core generator, MSBuild SDK, and NuGet packaging are all functional — validated against 46 libraries (88 framework targets — 53 Swift, 34 ObjC, 1 mixed) and tested with **6,400+ unit tests**, **700+ integration tests**, and **240+ end-to-end runtime tests** on iOS Simulator and macOS. Multi-platform support (iOS, macOS, Mac Catalyst, tvOS) is built into the generator, SDK, and runtime.

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
