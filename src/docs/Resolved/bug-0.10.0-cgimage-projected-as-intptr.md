# Bug: `CoreGraphics.CGImage?` returns project as raw `System.IntPtr?` instead of the canonical `CGImage` ref type

> SDK 0.10.0 generator typedb gap. Discovered 2026-05-05 during a
> consumer-experience audit of
> [SwiftBindings.Lottie](https://github.com/justinwojo/swift-dotnet-packages)
> (Lottie 4.x).

## Summary

Swift methods that return `CoreGraphics.CGImage?` (or take/return any
Quartz/CoreGraphics reference type) lower to `System.IntPtr?` in the
generated C# instead of the canonical `CoreGraphics.CGImage` type that
.NET's `xamarin-macios` / `dotnet/macios` workload already provides. The
consumer receives a raw pointer with no managed wrapper, no compile-time
type safety, and no automatic CFRetain/CFRelease management.

## Environment

- SwiftBindings.Sdk 0.10.0 / Runtime 0.10.0 / Apple 26.2.2
- .NET SDK 10.0.107, workload 10.0.107
- Xcode 26.3 / Swift 6.2.x
- macOS 26.x, arm64
- Source library: lottie-spm 4.x

## Repro

```bash
sed -n '22376,22420p' libraries/Lottie/obj/Debug/net10.0-ios/swift-binding/Lottie.cs
sed -n '24555,24580p' libraries/Lottie/obj/Debug/net10.0-ios/swift-binding/Lottie.cs
```

```csharp
// Lottie.cs:22378
public System.IntPtr? ImageForAsset(Lottie.ImageAsset asset)
{
    ...
    var __result = PInvoke_imageForAsset_…(this.Payload.DangerousGetHandle(),
        asset.Payload.DangerousGetHandle());
    return __result == IntPtr.Zero ? null : __result;
}
```

Same shape on `FilepathImageProvider.ImageForAsset` (Lottie.cs:24557) and
on the `IAnimationImageProvider` interface declaration (parity with both).

## Native ground truth

```text
swiftinterface (Lottie.framework, line 1718):
  open class BundleImageProvider : Lottie.AnimationImageProvider {
    final public func imageForAsset(asset: Lottie.ImageAsset) -> CoreGraphics.CGImage?
  }
```

`CoreGraphics.CGImage` is a CFType — it has CFRetain/CFRelease semantics
that need to be honored. The .NET binding for it (`CoreGraphics.CGImage`
in the macios workload) is the canonical wrapper.

## Hypothesis

The typedb is missing an entry for `CoreGraphics.CGImage` (and likely the
broader `CoreGraphics.CG*` family — `CGPath`, `CGColor`, `CGFont`,
`CGGradient`, etc.). When the emitter encounters a return type it can't
resolve to a known C# type, it falls back to `IntPtr?`.

Stripe and other libraries that touch these types do so through
`Foundation.NSData`-flavored APIs (image bytes via `NSData`), sidestepping
the gap. Lottie's `CGImage?` return is the first uncovered direct
surface.

Likely fix: extend the typedb with `CoreGraphics.CGImage` ↔
`CoreGraphics.CGImage` (and related CFTypes), and ensure the emitter
respects CFRetain/CFRelease semantics at the boundary.

## Impact

- **No compile-time type safety.** Consumers must manually verify the
  pointer represents a CGImage.
- **Manual ARC.** Consumers must call CFRelease on the returned pointer
  if they want to release it; forgetting leaks; double-releasing crashes.
- **Cannot mock for testing.** `IAnimationImageProvider.ImageForAsset`
  returning `IntPtr?` makes test doubles impossible without unsafe
  pointer manipulation.
- **Affects every binding that returns Quartz CFTypes directly.** Today
  observed only in Lottie; will surface in any future binding that
  exposes Core Graphics primitives.

## Workaround

Consumer side: cast manually:

```csharp
IntPtr? raw = provider.ImageForAsset(asset);
CoreGraphics.CGImage? img = raw is { } ptr
    ? new CoreGraphics.CGImage(ptr, owns: false)
    : null;
```

The `owns: false` is correct for a Swift-returned non-+1 reference; if
Swift is `passRetained`-ing the result the consumer must use `owns:
true`. Without inspecting the wrapper code it's not obvious which.

## Severity

**Correctness — Medium.** Type-safety hole + ARC ambiguity. Easy to
mishandle; hard to detect at compile time.

Cross-reference in
[SDK-0.10.0-BLOCKERS.md](../../swift-dotnet-packages/SDK-0.10.0-BLOCKERS.md)
under Round 4 / M-7.

## Round 5 — `CGColor` extension (2026-05-05)

The cross-package audit of `SwiftBindings.Apple.MusicKit` confirms
the same defect generalizes from `CGImage` to `CGColor`. The
generator does not bridge CoreGraphics types into the `CoreGraphics.*`
namespace, dropping them all to `IntPtr` / `IntPtr?`.

| Site | Property | Native return type | C# return type |
|------|----------|--------------------|------------------|
| MusicKit.cs:136 | `Artwork.BackgroundColor` | `CGColor?` | `System.IntPtr?` |
| MusicKit.cs:189 | `Artwork.PrimaryTextColor` | `CGColor?` | `System.IntPtr?` |
| MusicKit.cs:242 | `Artwork.SecondaryTextColor` | `CGColor?` | `System.IntPtr?` |
| MusicKit.cs:295 | `Artwork.TertiaryTextColor` | `CGColor?` | `System.IntPtr?` |
| MusicKit.cs:348 | `Artwork.QuaternaryTextColor` | `CGColor?` | `System.IntPtr?` |

Same fix unit applies — extend the typedb mapping to the full
CoreGraphics module. Doc title and scope should generalize from
"CGImage" to "CoreGraphics types" once a fix lands.

Cross-reference in
[SDK-0.10.0-BLOCKERS.md](../../swift-dotnet-packages/SDK-0.10.0-BLOCKERS.md)
under Round 5 / M-7.
