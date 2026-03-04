# SwiftUI Interop

## How It Works

SwiftUI Views can't be bound through conventional interop — they rely on opaque return types, property wrappers (`@State`, `@Binding`), and a declarative rendering pipeline with no C# equivalent.

Swift Bindings takes a different approach: **automatic bridge generation**. When the generator encounters a SwiftUI View, it generates a bridge layer that wraps the View in a `UIHostingController` and exposes it as a `UIViewController` that .NET can embed in any UIKit-based layout (including .NET MAUI).

This is interop bridging, not SwiftUI binding. You don't compose SwiftUI view hierarchies from C# — you configure and present SwiftUI components from Swift libraries.

## What Gets Generated

For each detected SwiftUI View, the generator produces:

**Swift bridge code** — wraps the View in a `UIHostingController` with a C-callable API:
- `SBW_{Module}_{View}_Create(...)` — create session + hosting controller
- `SBW_{Module}_{View}_GetViewController(handle)` — get the `UIViewController` pointer
- `SBW_{Module}_{View}_Free(handle)` — release the session

**C# bridge class** — wraps the P/Invoke calls with `IDisposable` lifecycle:

```csharp
using var session = new MyViewSession(onTap: () => Console.WriteLine("Tapped!"));
var viewController = session.ViewController;
// Present viewController in your UIKit/MAUI layout
```

## Supported Parameter Types

View initializer parameters are automatically bridged:

| Parameter Type | Example | Support |
|----------------|---------|---------|
| Primitives | `Int`, `Bool`, `Double`, `Float` | Fully supported |
| String | `String` | Fully supported |
| Closures | `() -> Void`, `(Int, String) -> Void` | Up to 4 params; primitive, String, and class args |
| Enums | Enum types with raw values | Via raw value conversion |
| Classes | Reference types | Via opaque pointer |
| Structs | Non-frozen and frozen-with-memory structs | Via opaque pointer with `.pointee` reconstruction |
| Optionals | `Optional<T>` for all above types | Fully supported |

## Generic Views

Generic SwiftUI Views with View-constrained type parameters (e.g., `AnimatedImage<Placeholder: View>`) are automatically bridged. The generator analyzes each generic parameter and substitutes `EmptyView` as the default placeholder, including for `@ViewBuilder` closure parameters.

Views with non-View generic constraints (e.g., `<T: Identifiable>`) currently fall back to template generation.

You can control placeholder behavior via [bridge hints](#bridge-hints):

```json
{
  "views": {
    "AnimatedImage": {
      "placeholder": "empty"
    }
  }
}
```

## Two-Way State Binding

Views with updatable parameters (primitives, strings, enums, classes, structs) get `Update{Param}()` methods on their session class. Updates flow through SwiftUI's `ObservableObject`/`@Published` reactivity system, so the view re-renders immediately:

```csharp
var session = CounterViewSession.Create(count: 0, label: "Score");
session.UpdateCount(42);        // SwiftUI re-renders
session.UpdateLabel("Points");  // String update via UTF-8 encoding
```

Closure parameters are set-once at creation (not updatable). Views with only closures or no parameters skip the state pattern entirely.

## View Modifier Chains

Self-returning modifier methods (e.g., `.playing()`, `.animationSpeed(2.0)`, `.looping(.loop)`) are detected and bridged as methods on the session class:

```csharp
var session = LottieViewSession.Create(animation: anim);
session.AnimationSpeed(2.0);    // Double modifier
session.Looping(LottieLoopMode.Loop);  // Enum modifier
session.Playing();              // Parameterless toggle
session.AnimationSpeed(null);   // Reset modifier (nil = not applied)
```

Supported modifier parameter types: primitives, `Bool`, `String`, and enums. Multi-param, closure-param, and generic-param modifiers are not yet supported.

## Async View Factories

Many Swift libraries use async factory patterns for views that require initialization (loading data, SDK setup, etc.):

```swift
// Swift side — async constructor chain
struct ScannerView: View {
    let analyzer: Analyzer  // async init
    init() async throws { ... }
}
```

The generator detects these patterns through ABI analysis and generates the appropriate async bridge with `Task` + callback:

```csharp
// C# side — async creation
var session = await ScannerViewSession.CreateAsync(
    onResult: result => HandleScanResult(result),
    onError: error => HandleError(error)
);
var viewController = session.ViewController;
```

The async inference analyzes constructor chains up to 3 levels deep with cycle detection, including cross-module type resolution.

## Bridge Hints

When auto-detection needs adjustment, you can provide a `bridge-hints.json` file instead of writing manual bridge code:

```json
{
  "$schema": "bridge-hints-v1",
  "views": {
    "CameraPreview": {
      "skip": true,
      "reason": "Requires live camera — not bridgeable"
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

### Hint Options

| Hint | Effect |
|------|--------|
| `skip` | Don't generate a bridge for this view |
| `forceTemplate` | Use template (placeholder) bridge instead of full bridge |
| `preferredInit` | Select which constructor to use (by index) |
| `asyncPattern` | Force async classification and re-run ABI inference |
| `extraSwiftImports` | Additional Swift `import` statements for the bridge file |

### Discovery

The hints file is discovered automatically in this order:
1. CLI flag: `--bridge-hints path/to/hints.json`
2. Module-specific: `{module}.bridge-hints.json` in the output directory
3. Generic: `bridge-hints.json` in the output directory

First match wins.

## Embedding in .NET MAUI

The bridge produces a standard `UIViewController`, which .NET MAUI can embed via a custom handler or `UIViewControllerRepresentable` pattern:

```csharp
// In a MAUI page
var session = new MySwiftViewSession(onAction: HandleAction);

// Get the native UIViewController
var nativeVC = Runtime.GetNSObject<UIViewController>(session.ViewController);

// Present it however your app needs
PresentViewController(nativeVC, animated: true, completionHandler: null);
```

## What's Not Supported

- **Composing SwiftUI views from C#** — no `VStack`, `HStack`, etc.
- **Implementing the `View` protocol from C#** — no C# types conforming to `SwiftUI.View`
- **`@Environment`** — environment values stay in Swift
- **`@ViewBuilder` closures** — no SwiftUI view-building closures from C# (generic View-constrained params are substituted with `EmptyView`)
- **Reactive bindings** — no Combine ↔ `INotifyPropertyChanged` bridge
- **Frozen blittable struct params** — C# value types needing pinning (e.g., `CGPoint`)
- **Closure non-primitive returns** — closures returning String or class types across the bridge
- **Generic views with non-View constraints** — `<T: Identifiable>`, `<T: Hashable>` (template fallback)

The bridge handles configuration, dynamic state updates, and presentation. The SwiftUI rendering pipeline stays entirely in Swift.

---

## Next Steps

- **[Customization](Customization.md)** — Other ways to control the generator output
- **[Supported Features](Supported-Features.md)** — Full feature reference
- **[Known Limitations](Known-Limitations.md)** — Platform and runtime constraints
