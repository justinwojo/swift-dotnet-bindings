# Bug: ObjC binding generator emits unconditional `using UIKit;` (and other iOS-only namespaces) into all-platform generated C#

> SDK 0.11.0 generator gap. Discovered 2026-05-18 attempting to multi-TFM
> `SwiftBindings.Apple.Matter` (and `SwiftBindings.Apple.MatterSupport`,
> which transitively pulls Matter).
>
> **Status: resolved.** Fix centralises the ObjC binding using header in
> `ObjCUsingsEmitter` and filters it through
> `AppleFrameworkRegistry.IsModuleAvailableOnPlatform(name, platformInfo.Platform)`,
> so the generator drops `using UIKit;` (and any future iOS-only namespace
> flagged in `apple-frameworks.json`) when the target TFM is macOS. The
> generator already runs once per inner-build TFM and `PlatformInfo`
> already flows into both ObjC emitters via `ObjCPipeline`, so no
> plumbing change was needed beyond the header itself. Unit coverage:
> `ObjCUsingsEmitterTests` (13 tests across ApiDefinition.cs,
> StructsAndEnums.cs, BgenDelegates.cs × iOS / macOS / tvOS / MacCatalyst).

## Repro

In `apple-frameworks/Matter/SwiftBindings.Apple.Matter.csproj`, change
the single-TFM to multi-TFM:

```xml
<TargetFrameworks>net10.0-ios26.2;net10.0-macos26.2;net10.0-maccatalyst26.2</TargetFrameworks>
```

The iOS build succeeds. The macOS build fails with:

```
obj/Debug/net10.0-macos26.2/swift-binding/ApiDefinition.cs(16,7): error CS0246: The type or namespace name 'UIKit' could not be found
obj/Debug/net10.0-macos26.2/swift-binding/StructsAndEnums.cs(10,7): error CS0246: The type or namespace name 'UIKit' could not be found
obj/Debug/net10.0-macos26.2/swift-binding/BgenDelegates.cs(4,7):    error CS0246: The type or namespace name 'UIKit' could not be found
```

`maccatalyst` then cascades-fails because Matter (its `ProjectReference`)
never restored a `maccatalyst` target.

## Root cause

The ObjC binding generator emits a fixed list of `using` directives at
the top of every generated C# file, independent of `$(TargetFramework)`.
For Matter the header is:

```csharp
using System;
using AuthenticationServices;
using AVFoundation;
using BackgroundAssets;
using CoreAnimation;
using CoreFoundation;
using CoreImage;
using CoreLocation;
using CoreMedia;
using Foundation;
using ImageIO;
using MapKit;
using Metal;
using ObjCRuntime;
using CoreGraphics;
using UIKit;            // ← iOS / Mac Catalyst only
using UserNotifications;
using WebKit;
```

Three files include the offending `using UIKit;` (ApiDefinition.cs,
StructsAndEnums.cs, BgenDelegates.cs). The generator does not consult
the target TFM when deciding which usings to emit, and the actual
generated members on those classes don't use UIKit anywhere on macOS
either — the `using` itself is the only thing tying the file to iOS.

## Why this blocks Matter on macOS

Apple ships Matter and MatterSupport on macOS 13.3+, so there is no
*framework-level* reason the binding can't ship to macOS. The
`<SwiftAppleFrameworkTarget>` registry entry for Matter doesn't
restrict to iOS. The only thing keeping us iOS-only is the dead
`using UIKit;` line.

## Suggested fixes

In order of increasing scope:

1. **Strip unused usings from the generated header.** If no member in
   the file references a UIKit type, drop the `using UIKit;`. Same
   for AuthenticationServices, AVFoundation, BackgroundAssets,
   UserNotifications, WebKit — the Matter binding likely doesn't
   touch most of these either. Trims dead imports across the board.
2. **Make the usings list TFM-conditional.** Wrap iOS-only usings
   in `#if IOS || MACCATALYST` / `#if !MACOS`. Less invasive than
   per-platform generation; the surface area is just the file
   header.
3. **Generate per-platform binding sources.** Different `obj/.../net10.0-{tfm}/swift-binding/` outputs per TFM, each with the right
   namespace imports + only the members that exist on that platform.
   Higher complexity but lets us bind frameworks like Matter whose
   surface differs between iOS and macOS.

Option 1 alone may be enough for Matter — if every UIKit reference in
the generated C# is in fact dead, stripping the usings unlocks macOS.
Worth confirming empirically before designing the fancier fix.

## Affected packages today

- `SwiftBindings.Apple.Matter` — verified, this report
- `SwiftBindings.Apple.MatterSupport` — transitively (`ProjectReference`
  to Matter blocks restore on non-iOS TFMs)

Any future ObjC-mode binding for a system framework that Apple ships on
non-iOS but the generator decorates with `using UIKit;` will hit the
same gap.

## Verification

After the fix, the Matter csproj should accept:

```xml
<TargetFrameworks>net10.0-ios26.2;net10.0-macos26.2;net10.0-maccatalyst26.2</TargetFrameworks>
```

and `dotnet build` should succeed for all three TFMs. The README in
both Matter and MatterSupport currently claims iOS-only because of this
gap; bump that claim back to "iOS 16.1+ / macOS 13.3+ / Mac Catalyst
16.4+" once the binding generator stops emitting the dead UIKit
reference.
