# Gap: Swift `_:` argument labels leak as parameter name `_`, `value0`, or projected typedef name

> SDK 0.10.0 generator ergonomics gap. Discovered 2026-05-05 during a
> consumer-experience audit of
> [SwiftBindings.Lottie](https://github.com/justinwojo/swift-dotnet-packages)
> (Lottie 4.x).

## Summary

Swift functions/initializers/case-payloads with `_:` (no external label)
parameters lose argument-label information at the swiftinterface
boundary. The C# emitter falls back to one of three strategies — none
ergonomic:

1. **Project the typedef name** — Lottie's `AnimationProgressTime`
   typealias for `CGFloat` becomes `cGFloat` as the parameter name.
2. **Positional placeholder** — `value0`, `value1`, etc.
3. **Literal underscore** — parameter name is just `_`. Legal C#, but
   collides with the discard-pattern symbol in C# 7.0+ and trips static
   analyzers.

The Swift compiler resolves these the same way at the call site (positional
only); the C# binding doesn't have positional-only support, so consumers
who want named-argument calls get nonsense names.

## Environment

- SwiftBindings.Sdk 0.10.0 / Runtime 0.10.0 / Apple 26.2.2
- .NET SDK 10.0.107, workload 10.0.107
- Xcode 26.3 / Swift 6.2.x
- macOS 26.x, arm64
- Source library: lottie-spm 4.x (broad pattern; not Lottie-specific)

## Repro

Lottie sites (broadest concentration):

```bash
sed -n '6249,6450p' libraries/Lottie/obj/Debug/net10.0-ios/swift-binding/Lottie.cs
```

```csharp
// Lottie.cs:6249 — Swift: case progress(_: AnimationProgressTime)
public static LottiePlaybackMode Progress(double cGFloat) { ... }

// Lottie.cs:6266 — Swift: case frame(_: AnimationFrameTime)
public static LottiePlaybackMode Frame(double cGFloat) { ... }

// Lottie.cs:6283 — Swift: case time(_: TimeInterval)
public static LottiePlaybackMode Time(double value0) { ... }

// Lottie.cs:6300 — Swift: case fromProgress(_: AnimationProgressTime?,
//                                            toProgress: AnimationProgressTime, ...)
public static LottiePlaybackMode FromProgress(
    double? cGFloat, double toProgress, ...) { ... }

// Lottie.cs:6371 — Swift: case marker(_: String, ...)
public static LottiePlaybackMode Marker(string value0, ...) { ... }

// Lottie.cs:22416 — Swift: func contentsGravity(for _: Lottie.ImageAsset)
public string ContentsGravity(Lottie.ImageAsset _) { ... }
```

## Native ground truth

```text
swiftinterface (Lottie.framework, lines 619-644):
  case progress(_: Lottie.AnimationProgressTime)
  case frame(_: Lottie.AnimationFrameTime)
  case time(_: Foundation.TimeInterval)
  case fromProgress(_: Lottie.AnimationProgressTime?,
                    toProgress: Lottie.AnimationProgressTime, ...)
  case marker(_: Swift.String, ...)
  ...
```

The `_:` is Swift's "no external label" syntax. At Swift call sites the
caller writes `LottiePlaybackMode.progress(0.5)` (positional). C# call
sites must do the same: `LottiePlaybackMode.Progress(0.5)`.

## Hypothesis

Three failure modes in the same emitter pass:

1. When the swiftinterface lists `(_: AnimationProgressTime)`, the emitter
   has no argument label and projects from the type. `AnimationProgressTime`
   is a typealias for `CGFloat` → `cGFloat`. Lowercasing the typedef name
   to a parameter name is the fallback when no label is present.
2. When the typealias depth is too high (e.g. `Foundation.TimeInterval` →
   `Double` is 2 hops away from a printable type), the projector gives up
   and uses positional `value0`.
3. For some declarations the emitter lifts the literal `_` straight from
   the swiftinterface as the parameter name. Worst form.

Likely fix: when the source argument label is `_`, infer a parameter name
from the parameter's *role* in the surrounding declaration. For an enum
`case foo(_: Bar)`, the natural name is `value` (first payload) or
`<barCamelCase>` (the lowercased payload-type name without typealias
stripping). For a function `func contentsGravity(for _: ImageAsset)`, the
external label is missing in Swift but the *internal* label (`for`) is
visible inside the function body; C# could lift that.

## Impact

- **API ergonomics.** Calling `LottiePlaybackMode.FromMarker(value0: null,
  toMarker: "end", playEndMarker: true, ...)` is unreadable. The point of
  named-argument support evaporates.
- **Static-analyzer noise.** `_` parameter names trip the
  IDE0060/IDE0058 family. Some teams have those analyzer rules on
  warning-as-error.
- **Pattern leakage to other libraries.** Every binding that surfaces
  Swift `case foo(_:)` will hit this — Lottie has the heaviest
  concentration but the emitter is shared.

## Workaround

Consumer side: only call the affected APIs positionally. Avoid named
arguments for the relevant `_:`-derived parameters.

## Severity

**Ergonomic — Low.** No correctness impact; just a visible blemish on
the consumer-facing API.

Cross-reference in
[SDK-0.10.0-BLOCKERS.md](../../swift-dotnet-packages/SDK-0.10.0-BLOCKERS.md)
under Round 4 / M-8.
