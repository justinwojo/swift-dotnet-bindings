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
| Closures | `() -> Void`, `(Int, Bool) -> Void` | Up to 4 params, primitive args |
| Enums | Enum types with raw values | Via raw value conversion |
| Classes | Reference types | Via opaque pointer |
| Optionals | `Optional<T>` for all above types | Fully supported |

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
- **`@State` / `@Binding` / `@Environment`** — deep state management stays in Swift
- **`@ViewBuilder` closures** — no SwiftUI view-building closures from C#
- **Reactive bindings** — no Combine ↔ `INotifyPropertyChanged` bridge
- **Closures with String/class arguments** — only primitive closure arguments in bridge

The bridge handles configuration and presentation. The SwiftUI rendering pipeline stays entirely in Swift.

---

## Next Steps

- **[Customization](Customization)** — Other ways to control the generator output
- **[Supported Features](Supported-Features)** — Full feature reference
- **[Known Limitations](Known-Limitations)** — Platform and runtime constraints
