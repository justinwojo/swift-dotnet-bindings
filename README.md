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

This is a growing problem for .NET developers on Apple platforms. Without Swift interop, **.NET on iOS becomes progressively less capable with each Xcode release.**

### The Current State of .NET + Swift

Today, if you need to call a Swift API from .NET, your options are painful:

**Objective Sharpie / Classic Bindings** — The traditional Xamarin approach. Write a Swift "proxy" library, annotate it with `@objc`, generate an Objective-C bridging header, then run Objective Sharpie to produce a C# binding. This doubles your maintenance burden, doesn't work for Swift-only APIs that can't be exposed via `@objc`, and the tooling has not kept pace with modern Xcode versions. Many developers report Objective Sharpie failing outright on recent SDKs.

**Native Library Interop (formerly Slim Bindings)** — A newer approach that uses `[LibraryImport]` to call C-compatible functions. While simpler, it requires you to manually write a C-compatible Swift wrapper for every API you want to expose, then manually write the corresponding C# declarations. For a library with hundreds of methods, this is weeks of tedious, error-prone work — and you lose type safety, async support, and any API that can't be flattened to C types.

**Hand-rolled P/Invoke** — Some developers skip the tooling entirely and write raw interop code. This requires deep knowledge of the Swift ABI, calling conventions, name mangling, and memory management. It's not realistic for anything beyond a handful of functions.

**All of these approaches share the same fundamental problem: they require a human to manually translate between Swift and C#, method by method, type by type.**

### What Swift Bindings Does Differently

Swift Bindings reads the compiled ABI metadata that Swift already produces and generates the entire C# binding automatically. There's no intermediate Objective-C layer, no manual proxy code, and no reliance on Objective Sharpie or bridging headers.

```
Traditional approach:
  Swift API → manual Swift proxy → @objc headers → Objective Sharpie → C# binding
  (weeks of work, fragile, limited to ObjC-compatible types)

Swift Bindings approach:
  Swift framework (.xcframework) → SwiftBindings tool → C# binding
  (automated, supports Swift-native types, minutes not weeks)
```

### Where This Project Comes From

This project is a fork of Microsoft's [`dotnet/runtimelab` (feature/swift-bindings branch)](https://github.com/dotnet/runtimelab/tree/feature/swift-bindings) — an experimental effort to explore Swift/.NET interoperability. That experiment established the foundational architecture: ABI JSON parsing, Swift symbol demangling, a type database, and the beginnings of a code emitter.

However, the runtimelab branch was never intended as a shipping product. Development slowed and eventually stopped, leaving the project in an early experimental state. At the time it went inactive, the generator could handle a narrow set of Swift types — basic classes and structs, simple method signatures, and a subset of Foundation types. It could not handle generics, protocols, closures, tuples, operators, async methods, enums with associated values, or most of the Swift type system features that real-world libraries actually use. There was no runtime validation against real Swift libraries and no mechanism to assess binding completeness.

This fork picks up where runtimelab left off and extends the generator substantially. Since forking, over 60 phases of development have added support for the full range of Swift types, protocols with witness dispatch, async methods, closures, generics, SwiftUI bridge generation, and much more — validated against real-world libraries (Nuke, BlinkID, Lottie) with zero generator errors and full runtime test suites. The core architecture from Microsoft's original work remains, but the generator's capabilities and real-world readiness are fundamentally different from the experimental state it was left in.

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
- **SwiftUI Views** — automatic UIHostingController bridge generation (see below)

### Idiomatic Type Conversions

Generated bindings don't just expose raw Swift types — method signatures are automatically converted to idiomatic C#/.NET types so the API feels native:

| Swift Type | C# Type (in methods) | Notes |
|------------|----------------------|-------|
| `String` | `string` | Automatic `SwiftString` ↔ `string` conversion |
| `Array<T>` | `IReadOnlyList<T>` (return) / `IEnumerable<T>` (param) | Standard .NET collection interfaces |
| `Optional<T>` | `T?` | C# nullable syntax |
| `Int` / `Int32` / `Int64` | `nint` / `int` / `long` | .NET numeric types |
| `Bool` | `bool` | Direct mapping |
| `Float` / `Double` | `float` / `double` | Direct mapping |
| `URL` | `NSUrl` | Familiar Foundation type |
| `Date` | `DateTimeOffset` | Standard .NET date type |
| `UUID` | `Guid` | Standard .NET GUID |
| `UnsafePointer<T>`, `OpaquePointer`, etc. | `IntPtr` | All Swift pointer types |

For example, a Swift method `func fetchName() -> String` becomes `string FetchName()` in C# — not `SwiftString FetchName()`. You work with regular .NET types throughout your code.

Properties retain their Swift wrapper types (`SwiftString`, `SwiftArray<T>`) for consistency with getter/setter patterns, while methods use the idiomatic conversions above.

### Real-World Validation

The generator produces **zero compilation errors** across every library tested:

| Library | Purpose | Member Coverage | Runtime Tests |
|---------|---------|-----------------|---------------|
| **Nuke** | Image loading | 94.4% | Validated |
| **BlinkID** | Document scanning SDK | 99.1% | 18/18 passing |
| **Lottie** | Animation framework | 90.4% | 15/15 passing |

The remaining coverage gaps are primarily exotic type patterns (existential arguments in bound generics, Combine publishers) that have clear diagnostics and documented workarounds.

### Examples

**Async image loading with [Nuke](https://github.com/kean/Nuke)** — a popular Swift image library, bound and running from C#:

```csharp
// Load an image asynchronously using Nuke's pipeline
var pipeline = ImagePipeline.Shared;
var request = new ImageRequest("https://picsum.photos/200/200");
var image = await pipeline.Image(request);
Console.WriteLine($"Image loaded: {image.Size.Width}x{image.Size.Height}");
```

**Animation loading with [Lottie](https://github.com/airbnb/lottie-ios)** — parsing a Lottie JSON animation file:

```csharp
// Load a Lottie animation from a bundled JSON file
using var data = NSData.FromFile("animation.json");
var animation = LottieAnimation.From(data, DecodingStrategy.DictionaryBased);
Console.WriteLine($"Animation: {animation.Duration}s at {animation.Framerate}fps");
```

**Implementing a Swift protocol from C#** — passing a C# object back to Swift:

```csharp
// C# class that implements a Swift protocol
public class MyImageProcessor : ISwiftImageProcessing
{
    public SwiftString Identifier => new SwiftString("my-processor");

    public UIImage? Process(UIImage image)
    {
        // Apply custom processing
        return image;
    }
}

// Create a proxy and pass it to Swift code that expects the protocol
var proxy = new ImageProcessingProxy(new MyImageProcessor());
```

No proxy libraries. No bridging headers. These are generated C# bindings calling Swift directly — validated on iOS Simulator.

---

## SwiftUI Interop

SwiftUI Views can't be bound through conventional interop — they rely on opaque return types, property wrappers (`@State`, `@Binding`), and a declarative rendering pipeline that has no C# equivalent.

Swift Bindings takes a different approach: **automatic bridge generation**. When the generator encounters a SwiftUI View, it emits a Swift bridge layer that wraps the View in a `UIHostingController`, exposing it as a `UIViewController` that .NET can embed in any UIKit-based layout (including .NET MAUI).

This bridge generation is fully automatic. The generator analyzes View initializer parameters — primitives, strings, closures, enums, class references — and produces the correct interop code for each. For Views with async factory patterns (common in SDK-style frameworks), the generator infers the construction chain from the ABI and emits data-driven bridge code.

The result: SwiftUI Views are usable from .NET without writing any Swift wrapper code by hand.

### Bridge Hints

When auto-detection needs adjustment — for example, to skip a View that requires an unsupported parameter type, or to select a specific initializer — you can provide a `bridge-hints.json` sidecar file instead of writing a manual bridge:

```json
{
  "$schema": "bridge-hints-v1",
  "views": {
    "CameraPreview": {
      "skip": true,
      "reason": "Requires live camera preview source"
    },
    "CustomView": {
      "preferredInit": 1,
      "extraSwiftImports": ["SomeFramework"]
    }
  },
  "globalSettings": {
    "extraSwiftImports": ["SharedLib"]
  }
}
```

Supported per-view hints: `skip`, `forceTemplate`, `preferredInit` (constructor index), `asyncPattern` (force async classification), `extraSwiftImports`.

The file is discovered automatically as `{module}.bridge-hints.json` or `bridge-hints.json` in the output directory, or specified explicitly with `--bridge-hints path/to/hints.json`.

---

## Purpose-Built Test Framework

Real-world Swift libraries exercise the full complexity of the language — generics nested inside protocols, closures returning tuples, async methods with existential parameters. Isolated unit tests can't catch the interactions between these features.

Swift Bindings includes a **comprehensive test library** of 93 must-pass feature scenarios drawn from patterns encountered in real bindings (Nuke, BlinkID, Lottie, and others). This test framework:

- Contains 67 Swift source files across 18 categories (types, closures, generics, protocols, async, operators, tuples, pointers, and more)
- Produces a coverage matrix showing exactly which features pass, which are degraded, and why
- Runs after every generator change to catch regressions immediately
- Includes 184+ runtime tests on iOS Simulator validating actual cross-language calls

Current status: **92 of 93 must-pass features passing (98.9%)**. The single degraded feature requires runtime support for `SwiftArray<ExistentialContainer>`, which is tracked.

This test framework is what gives confidence that changes to the generator don't break real-world bindings. It's not a toy test suite — it simulates the actual complexity you'll encounter binding production Swift libraries.

---

## Getting Started

> **This section is under construction.** Detailed usage instructions, project templates, and walkthrough guides are coming as the tooling matures toward its v1.0 release.

The end-state workflow will be:

```bash
# 1. Create a binding project
dotnet new swift-binding -n MyLibrary.Bindings

# 2. Add your xcframework to the project

# 3. Build — generates bindings and produces a NuGet package
dotnet build

# 4. Consume in any .NET iOS/MAUI app
dotnet add package MyLibrary.Bindings
```

Currently, the generator works with pre-compiled xcframeworks. **Swift Package Manager integration** is on the roadmap — the goal is to accept a Package.swift URL or dependency declaration and have the tooling resolve, build, and bind the package automatically. This would cover the large number of Swift libraries distributed exclusively through SPM.

For now, see the `CLAUDE.md` file for build instructions and the helper scripts in `BindingTesting/` for working examples of the full pipeline.

---

## Let AI Create Your Binding

One of the goals of this project is to make binding creation so well-structured that an AI agent can do it for you.

The repository includes an AI instruction file that guides tools like Claude Code, Codex, or other AI coding assistants through the full binding workflow: generating bindings from your xcframework, resolving any issues, building a test app, and validating it on a simulator — all automatically.

The vision: **point an AI agent at your Swift framework, and get back a working, tested NuGet package.** The structured scripts, diagnostic reports, and validation tooling in this repo are designed to make that loop tight enough for an AI to close without human intervention.

This is aspirational but grounded — the test infrastructure and binding reports already provide the feedback signals an AI agent needs to iterate toward a working binding.

---

## Reporting Issues

If you run into a problem generating bindings for a Swift library, please [open an issue](../../issues) and include:

1. **Generator logs** — Run with `-v 2` for verbose output and attach the full log
2. **The binding report** — The `binding-report.json` file from the output directory shows exactly which members were skipped and why
3. **The xcframework** (if possible) — Having the actual framework lets us reproduce the issue and validate the fix. If the framework is proprietary, include at minimum the ABI JSON (`-a` output) and TBD file

The more context you provide, the faster we can diagnose and fix the issue. The binding report alone often contains enough information to identify the root cause.

---

## Known Limitations

Swift Bindings targets .NET 10 on Apple platforms, which currently uses the Mono runtime for iOS deployment. Mono's JIT compiler has a known defect (`jit-info.c:918`) that causes process-fatal crashes when it encounters `CallConvSwift` in certain P/Invoke frame types. This affects three categories of Swift interop: string operations, closure callbacks, and existential type metadata.

Four workarounds (A through D) have been implemented in this project to route around the crash. These workarounds are transparent to the end user — generated bindings work correctly on Mono without any manual intervention. However, they introduce a runtime dependency on `libSwiftBindingsRuntime.dylib`, which must be included in the application bundle.

For full details on each workaround, affected files, and a revert checklist for when the upstream fix lands, see [`src/docs/known-issues-workarounds.md`](src/docs/known-issues-workarounds.md).

Other documented limitations:
- **Non-blittable types**: .NET's `CallConvSwift` requires all P/Invoke parameters to be blittable. Types like `SwiftOptional<T>` and `SafeHandle` require wrapper-based marshalling.
- **SafeHandle in async P/Invoke**: The .NET runtime doesn't preserve `SafeHandle` references across async continuations. Singleton and IntPtr-based workarounds are implemented.
- **VWT Destroy on Mono**: Explicit `Dispose()` on structs with reference-type fields (e.g., a struct containing a `String`) can trigger the JIT crash through `ValueWitnessTable->Destroy()`. This remains an open issue for a small number of types.

---

## Project Status

Swift Bindings is under active development. The core generator is functional and validated against real-world libraries. Work is ongoing toward the v1.0 developer experience (MSBuild SDK, project templates, NuGet packaging automation).

See [`north-star.md`](north-star.md) for the full technical roadmap.

| Milestone | Status |
|-----------|--------|
| Core type system (classes, structs, enums, generics) | Complete |
| Protocols and witness dispatch | Complete |
| Async method support | Complete |
| Closures, tuples, operators | Complete |
| SwiftUI bridge generation | Complete |
| Real-world library validation | 5 libraries, 0 errors |
| MSBuild SDK and project templates | Planned |
| NuGet packaging automation | Planned |
| Swift Package Manager integration | Planned |
| Additional library validation (StoreKit, HealthKit, etc.) | Planned |
| Public documentation and guides | In progress |

---

## License

Licensed under the [MIT License](LICENSE.TXT).

Originally developed by Microsoft Corporation as part of [`dotnet/runtimelab`](https://github.com/dotnet/runtimelab/tree/feature/swift-bindings). Now maintained and actively developed by Justin Wojciechowski.
