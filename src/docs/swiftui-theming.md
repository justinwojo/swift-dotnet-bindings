# SwiftUI Theme Bridge

**Date**: 2026-02-17
**Priority**: P2 | **Effort**: Medium (2 sessions) | **Risk**: Low
**Status**: Session 1 Complete

---

## Problem

Third-party Swift libraries that present UI almost always expose theming — colors, fonts, sizes. The consumer customizes the theme, then presents the view. This is one of the most common integration points for apps consuming UI SDKs.

Currently, all `SwiftUI.Color` and `SwiftUI.Font` properties are skipped as `SwiftUIConstraint` because the generator has no C# projection for these types. This means the entire theming surface of libraries like BlinkIDUX is inaccessible from C#, even though the underlying data is just RGBA values and font name/size/weight tuples.

## Real-World Pattern Survey

Two libraries in the validation corpus use this pattern:

### BlinkIDUX — `BlinkIDTheme`

```
BlinkIDTheme (class, conforms to UXThemeProtocol)
  static let shared: BlinkIDTheme
  var alertTitleColor: SwiftUI.Color          (settable)
  var alertTitleFont: SwiftUI.Font            (settable)
  var alertDescriptionColor: SwiftUI.Color    (settable)
  var alertDescriptionFont: SwiftUI.Font      (settable)
  var alertButtonColor: SwiftUI.Color         (settable)
  var alertButtonFont: SwiftUI.Font           (settable)
  var alertBackgroundColor: SwiftUI.Color     (settable)
  var onboardingSheetTitleColor: SwiftUI.Color
  var onboardingSheetTitleFont: SwiftUI.Font
  var onboardingSheetDescriptionColor: SwiftUI.Color
  var onboardingSheetDescriptionFont: SwiftUI.Font
  var onboardingSheetButtonColor: SwiftUI.Color
  var onboardingSheetButtonFont: SwiftUI.Font
  var onboardingSheetPageIndicatorColor: SwiftUI.Color
  var onboardingSheetBackgroundColor: SwiftUI.Color
  var reticleTooltipFont: SwiftUI.Font
  var helpButtonForegroundColor: SwiftUI.Color
  var helpButtonBackgroundColor: SwiftUI.Color
  var helpButtonTooltipForegroundColor: SwiftUI.Color
  var helpButtonTooltipBackgroundColor: SwiftUI.Color
  var toastBackgroundColor: SwiftUI.Color
```

21 properties: 15 `SwiftUI.Color`, 6 `SwiftUI.Font`. All settable. Accessed via `BlinkIDTheme.shared`.

The theme is not passed through the view init chain — `BlinkIDUXView.init(viewModel:)` takes a `BlinkIDUXModel`, which takes `ScanningUXSettings` (booleans and an enum). The theme is a separate global configuration.

The normal generator already emits `public partial class BlinkIDTheme : ISwiftObject, IDisposable` with a `Shared` property getter. The Color/Font properties are individually skipped as `SwiftUIConstraint`. The theme bridge adds setters for those skipped properties.

### MicroblinkPlatform — `MicroblinkPlatformTheme`

```
MicroblinkPlatformTheme (class)
  static let shared: MicroblinkPlatformTheme
  var primaryColor: UIKit.UIColor             (settable)
  var mainScreenMainImage: UIKit.UIImage      (settable)
  var loadingFont: UIKit.UIFont               (settable)
  var mainScreenTitleFont: UIKit.UIFont       (settable)
  var mainScreenTitleColor: UIKit.UIColor     (settable)
  var mainButtonFont: UIKit.UIFont            (settable)
  var buttonCornerRadius: Swift.Double        (settable)
  var documentScanAlertTitleColor: SwiftUI.Color      (settable)
  var documentScanAlertTitleFont: SwiftUI.Font        (settable)
  var documentScanAlertDescriptionColor: SwiftUI.Color
  var documentScanAlertDescriptionFont: SwiftUI.Font
  var documentScanAlertButtonColor: SwiftUI.Color
  var documentScanAlertButtonFont: SwiftUI.Font
  var documentScanAlertBackgroundColor: SwiftUI.Color
  ... (17 more SwiftUI.Color/Font properties)
```

Mixed UIKit + SwiftUI types on the same theme object. Same singleton access pattern. Also includes `UIImage` (out of scope for phase 1) and primitive types like `Double`.

### Common Pattern

Both libraries share:
1. **Concrete class** with a `static let shared` singleton
2. **Settable properties** — the consumer mutates them before presenting views
3. **No init-chain injection** — theme is global state, not a constructor parameter
4. **Pure data** — colors are RGBA, fonts are name+size+weight, no complex behavior

This is the dominant pattern in the iOS SDK ecosystem. Libraries like Stripe, Plaid, and most document scanning SDKs follow it.

## Design

### Approach: `@_cdecl` Setter Wrappers

This is NOT an extension of the protocol proxy system. Protocol proxies replicate the full Swift witness table at the ABI level — that's heavyweight machinery for what is fundamentally "set an RGBA tuple." Instead, this is a new bridge emitter that:

1. Detects theme-bridgeable types from ABI metadata
2. Emits `@_cdecl` setter functions that accept primitives and construct SwiftUI types
3. Emits C# static setter methods that call the setters via P/Invoke

The Swift wrapper handles all SwiftUI type construction internally. C# never sees `SwiftUI.Color` — it sees `SwiftColor(r, g, b, a)`.

### C# Runtime Types (`Swift.Runtime`)

Two new value types, reusable across all libraries.

**Validation contract**: Pass-through semantics. Values are forwarded to Swift as-is without clamping or rejection. `SwiftUI.Color` and `UIColor` both accept out-of-range RGBA values (they clamp internally during rendering). Negative font sizes and empty font names produce the same behavior as calling the Swift API directly. This keeps the bridge transparent — no hidden normalization layer between the C# call and the Swift result.

```csharp
/// <summary>
/// Represents a color for SwiftUI/UIKit theme bridging.
/// Passed to Swift as four Double values (RGBA). Values are not clamped —
/// SwiftUI.Color and UIColor handle out-of-range values at render time.
/// </summary>
public readonly record struct SwiftColor(double R, double G, double B, double A = 1.0)
{
    // Convenience constructors
    public static SwiftColor FromHex(uint hex) =>
        new((hex >> 16 & 0xFF) / 255.0, (hex >> 8 & 0xFF) / 255.0, (hex & 0xFF) / 255.0);

    public static SwiftColor FromHex(uint hex, double alpha) =>
        new((hex >> 16 & 0xFF) / 255.0, (hex >> 8 & 0xFF) / 255.0, (hex & 0xFF) / 255.0, alpha);

    // Named colors
    public static SwiftColor White => new(1, 1, 1);
    public static SwiftColor Black => new(0, 0, 0);
    public static SwiftColor Clear => new(0, 0, 0, 0);
    public static SwiftColor Red => new(1, 0, 0);
    public static SwiftColor Green => new(0, 1, 0);
    public static SwiftColor Blue => new(0, 0, 1);
}
```

```csharp
/// <summary>
/// Represents a font for SwiftUI/UIKit theme bridging.
/// Passed to Swift as: font name (UTF-8), size, weight enum, design enum, isSystem flag.
/// Values are not validated — they produce the same result as calling the Swift API directly.
/// </summary>
public readonly struct SwiftFont
{
    public string? FontName { get; }
    public double Size { get; }
    public SwiftFontWeight Weight { get; }
    public SwiftFontDesign Design { get; }
    public bool IsSystem => FontName == null;

    private SwiftFont(string? fontName, double size, SwiftFontWeight weight, SwiftFontDesign design)
    {
        FontName = fontName;
        Size = size;
        Weight = weight;
        Design = design;
    }

    // Construction modes
    public static SwiftFont Custom(string name, double size) =>
        new(name, size, SwiftFontWeight.Regular, SwiftFontDesign.Default);

    public static SwiftFont System(double size, SwiftFontWeight weight = SwiftFontWeight.Regular,
        SwiftFontDesign design = SwiftFontDesign.Default) =>
        new(null, size, weight, design);

    // Semantic presets (match SwiftUI.Font static properties)
    public static SwiftFont LargeTitle => System(34, SwiftFontWeight.Regular);
    public static SwiftFont Title => System(28, SwiftFontWeight.Regular);
    public static SwiftFont Title2 => System(22, SwiftFontWeight.Regular);
    public static SwiftFont Title3 => System(20, SwiftFontWeight.Regular);
    public static SwiftFont Headline => System(17, SwiftFontWeight.Semibold);
    public static SwiftFont Body => System(17, SwiftFontWeight.Regular);
    public static SwiftFont Callout => System(16, SwiftFontWeight.Regular);
    public static SwiftFont Subheadline => System(15, SwiftFontWeight.Regular);
    public static SwiftFont Footnote => System(13, SwiftFontWeight.Regular);
    public static SwiftFont Caption => System(12, SwiftFontWeight.Regular);
    public static SwiftFont Caption2 => System(11, SwiftFontWeight.Regular);
}

public enum SwiftFontWeight : int
{
    UltraLight = 0, Thin, Light, Regular, Medium, Semibold, Bold, Heavy, Black
}

public enum SwiftFontDesign : int
{
    Default = 0, Rounded, Monospaced, Serif
}
```

### Generated Swift Wrappers

Per settable Color property:

```swift
@_cdecl("SBW_BlinkIDTheme_set_alertTitleColor")
func SBW_BlinkIDTheme_set_alertTitleColor(
    _ r: Double, _ g: Double, _ b: Double, _ a: Double
) {
    SBW_onMainThread {
        BlinkIDTheme.shared.alertTitleColor = Color(red: r, green: g, blue: b, opacity: a)
    }
}
```

Per settable Font property (with defensive checks instead of force unwraps):

```swift
@_cdecl("SBW_BlinkIDTheme_set_alertTitleFont")
func SBW_BlinkIDTheme_set_alertTitleFont(
    _ namePtr: UnsafePointer<UInt8>?, _ nameLen: Int,
    _ size: Double, _ weight: Int32, _ design: Int32, _ isSystem: Int32
) {
    SBW_onMainThread {
        let font: Font
        if isSystem != 0 {
            font = .system(size: CGFloat(size),
                           weight: SBW_fontWeight(weight),
                           design: SBW_fontDesign(design))
        } else if let namePtr = namePtr, nameLen > 0,
                  let name = String(bytes: UnsafeBufferPointer(start: namePtr, count: nameLen),
                                    encoding: .utf8) {
            font = .custom(name, size: CGFloat(size))
        } else {
            // Fallback: invalid custom font name → system font at requested size
            font = .system(size: CGFloat(size))
        }
        BlinkIDTheme.shared.alertTitleFont = font
    }
}

// Shared helpers (emitted once per module)
func SBW_fontWeight(_ raw: Int32) -> Font.Weight {
    switch raw {
    case 0: return .ultraLight
    case 1: return .thin
    case 2: return .light
    case 3: return .regular
    case 4: return .medium
    case 5: return .semibold
    case 6: return .bold
    case 7: return .heavy
    case 8: return .black
    default: return .regular
    }
}

func SBW_fontDesign(_ raw: Int32) -> Font.Design {
    switch raw {
    case 0: return .default
    case 1: return .rounded
    case 2: return .monospaced
    case 3: return .serif
    default: return .default
    }
}
```

### Generated C# Wrappers

The normal generator already emits `public partial class BlinkIDTheme : ISwiftObject, IDisposable` with the `Shared` property and non-SwiftUI members. The theme bridge emits **static methods in a separate `partial class` block** that merges with the existing type, avoiding any naming collision.

**ABI note**: Swift `Int` is 64-bit on Apple ARM64 targets. All `Int`-typed parameters in the P/Invoke signatures use `nint` (platform-native integer) to match. `Int32` parameters (weight, design, isSystem flags) remain `int` since they are explicitly 32-bit on both sides.

```csharp
// Emitted into the bridge .cs file as a separate partial block.
// Merges with the existing BlinkIDTheme class from the main generator.
public partial class BlinkIDTheme
{
    /// <summary>
    /// Theme bridge setters for SwiftUI.Color and SwiftUI.Font properties.
    /// Set these before creating a view session. Changes take effect on next render.
    /// </summary>
    public static void SetAlertTitleColor(SwiftColor value)
    {
        ThemeBridgeNativeMethods.SBW_BlinkIDTheme_set_alertTitleColor(
            value.R, value.G, value.B, value.A);
    }

    public static unsafe void SetAlertTitleFont(SwiftFont value)
    {
        var nameBytes = value.FontName != null
            ? System.Text.Encoding.UTF8.GetBytes(value.FontName) : null;
        fixed (byte* namePtr = nameBytes)
        {
            ThemeBridgeNativeMethods.SBW_BlinkIDTheme_set_alertTitleFont(
                namePtr, nameBytes?.Length ?? 0,
                value.Size, (int)value.Weight, (int)value.Design,
                value.IsSystem ? 1 : 0);
        }
    }

    private static partial class ThemeBridgeNativeMethods
    {
        [LibraryImport("BlinkIDUXSwiftBindings",
            EntryPoint = "SBW_BlinkIDTheme_set_alertTitleColor")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
        internal static partial void SBW_BlinkIDTheme_set_alertTitleColor(
            double r, double g, double b, double a);

        [LibraryImport("BlinkIDUXSwiftBindings",
            EntryPoint = "SBW_BlinkIDTheme_set_alertTitleFont")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
        internal static unsafe partial void SBW_BlinkIDTheme_set_alertTitleFont(
            byte* namePtr, nint nameLen,
            double size, int weight, int design, int isSystem);
    }
}
```

### Detection Criteria

The emitter detects theme-bridgeable types from ABI metadata:

1. **Concrete class** (not protocol, not struct)
2. **Has a static singleton property** — name is `shared`, `default`, `current`, `sharedInstance`, or `instance`; type is `Self`; has getter
3. **Has settable properties with bridgeable UI types** — at least one property whose type is:
   - `SwiftUI.Color` or `SwiftUI.Font` (Phase 1)
   - `UIKit.UIColor` or `UIKit.UIFont` (Phase 2)
4. **Properties are not static** — instance properties accessed through the singleton

Properties with non-bridgeable types (like `UIKit.UIImage`) on the same class are silently skipped. Primitive properties (`Double`, `Bool`, `Int`, `String`) that already have working bindings through the normal generator are excluded from theme bridge emission to avoid duplication.

### Why Not Protocol Proxy

The protocol approach (`UXThemeProtocol`) would require:
- Full witness table dispatch for 21 properties
- C# -> Swift callback trampolines for each getter
- `EveryProtocol` conformance in the Swift wrapper
- Handling of `SwiftUI.Color`/`Font` as return types across the ABI boundary (not just primitives going in)
- Actor isolation (`@MainActor`) on the conformance

The theme bridge avoids all of this. The C# side only sends primitives *into* Swift. Swift constructs the SwiftUI types internally. No witness tables, no trampolines, no return-type marshalling.

### Thread Safety: `SBW_onMainThread` Semantics

All theme setters are wrapped in `SBW_onMainThread`, which has **synchronous** semantics:

```swift
func SBW_onMainThread<T>(_ block: () -> T) -> T {
    if Thread.isMainThread { return block() }
    return DispatchQueue.main.sync { block() }
}
```

This means:
- If called from the main thread: executes inline, returns immediately
- If called from a background thread: blocks until main thread executes and returns

**Consequence**: When the C# setter returns, the Swift singleton is guaranteed to be updated. There is no race between "set theme" and "create session" — the set completes synchronously before execution continues.

This is the same helper used by all existing bridge session `Create`/`GetViewController`/`Free` functions. No new threading mechanism needed.

### Integration with Bridge Sessions

Theme configuration is called before creating a view session:

```csharp
// 1. Configure theme (one-time or per-session)
BlinkIDTheme.SetAlertTitleColor(SwiftColor.FromHex(0x1A73E8));
BlinkIDTheme.SetAlertButtonFont(SwiftFont.System(16, SwiftFontWeight.Semibold));
BlinkIDTheme.SetAlertBackgroundColor(new SwiftColor(0.95, 0.95, 0.97));

// 2. Create and present the view (existing bridge session)
using var session = BlinkIDUXViewSession.Create(
    licenseKey: licenseKey,
    showIntroductionAlert: true,
    onResult: result => ProcessResult(result));

var viewController = session.GetViewController();
PresentViewController(viewController);
```

No changes to the bridge session system are needed. The theme setters mutate the singleton synchronously, and the SwiftUI views read from it when they render.

## UIKit Type Support (Phase 2)

MicroblinkPlatformTheme mixes `UIKit.UIColor`/`UIKit.UIFont` and `SwiftUI.Color`/`SwiftUI.Font` on the same class. The ABI crossing is nearly identical:

| Swift Type | C ABI | SwiftUI Construction | UIKit Construction |
|-----------|-------|---------------------|-------------------|
| Color / UIColor | `(Double, Double, Double, Double)` | `Color(red:green:blue:opacity:)` | `UIColor(red:green:blue:alpha:)` |
| Font / UIFont | `(UnsafePointer<UInt8>?, Int, Double, Int32, Int32, Int32)` | `.system(size:weight:design:)` / `.custom(_:size:)` | `.systemFont(ofSize:weight:)` / `UIFont(name:size:)` |

The C# types (`SwiftColor`, `SwiftFont`) work for both — only the Swift construction code differs. The emitter checks the property's module (`SwiftUI` vs `UIKit`) and emits the appropriate constructor.

## File Changes

### New files
- `src/Swift.Runtime/src/Swift/SwiftColor.cs` — `SwiftColor` value type
- `src/Swift.Runtime/src/Swift/SwiftFont.cs` — `SwiftFont`, `SwiftFontWeight`, `SwiftFontDesign`

### Modified files
- `src/Swift.Bindings/src/Emitter/StringEmitter/SwiftUIBridgeEmitter.cs` — or new `ThemeBridgeEmitter.cs`
  - Detect theme-bridgeable types during emission
  - Emit Swift setter wrappers into the bridge `.swift` file
  - Emit C# theme `partial class` block into the bridge `.cs` file
- `src/Swift.Bindings/src/Emitter/StringEmitter/SwiftUIBridgeEmitter.InitAnalyzer.cs` — Add `SwiftUI.Color`/`Font` to `MapSwiftTypeToAbi` as bridgeable (optional, for init-chain support)
- Report integration — `RecordThemeBridged` or equivalent

### Test files
- `src/Swift.Runtime/tests/SwiftColorTests.cs` — construction, hex, named colors, out-of-range pass-through
- `src/Swift.Runtime/tests/SwiftFontTests.cs` — custom, system, semantic presets, empty name handling
- Unit tests for detection logic and emission
- BlinkIDUX validation: theme bridge compiles, all 21 properties accessible
- MicroblinkPlatform validation: mixed UIKit/SwiftUI theme bridge

## Open Questions

1. **Getter support**: Should the theme bridge also emit getters (read current values back to C#)? This requires marshalling `SwiftUI.Color` -> RGBA on the Swift side, which is possible via `UIColor(color).getRed(&r, &g, &b, &a)`. Adds complexity. Probably Phase 2.

2. **Non-singleton themes**: Some libraries might use `init(theme:)` injection instead of singletons. The bridge session's async constructor chain could flatten theme properties into the `Create` function parameters. This is a natural extension but significantly more complex. Track separately.

3. **`UIImage` bridging**: MicroblinkPlatform has `UIImage` properties on its theme. Image bridging (by asset name or data blob) is a separate, larger feature. Skip for now.

## Session Plan

### Session 1: Runtime types + SwiftUI Color/Font bridge
- `SwiftColor` in `Swift.Runtime` with tests
- `SwiftFont`, `SwiftFontWeight`, `SwiftFontDesign` in `Swift.Runtime` with tests
- Theme detection logic (singleton + bridgeable properties)
- Swift Color setter emission (RGBA doubles)
- Swift Font setter emission + shared helpers (with defensive null/encoding checks)
- C# `partial class` emission (merges with existing generated type)
- P/Invoke signatures with correct ABI widths (`nint` for Swift `Int`)
- Unit tests for detection and emission
- BlinkIDUX validation: 21 properties accessible from C#

### Session 2: UIKit types + getters + polish
- UIKit.UIColor/UIFont support (parallel construction paths)
- MicroblinkPlatform validation (mixed UIKit/SwiftUI)
- Getter support (optional, if straightforward)
- Report integration
- Documentation update
