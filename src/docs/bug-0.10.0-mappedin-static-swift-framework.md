# Bug: Static Swift framework misclassified as ObjC-only, then ObjC fallback fails on missing system frameworks

> SDK 0.10.0 generator regression (or longstanding gap surfaced now). Discovered
> 2026-05-05 attempting to switch
> [SwiftBindings.Mappedin](https://github.com/justinwojo/swift-dotnet-packages)
> from a hand-provisioned 1.0 dynamic xcframework to Mappedin's publicly-shipped
> 6.2.0 static xcframework
> ([release](https://github.com/MappedIn/ios/releases/tag/6.2.0)).

## Summary

When a vendor xcframework's binary is a **static** archive (`ar archive`) but a
complete `.swiftmodule` is present alongside it, the SDK detects "static
library" and routes to its **ObjC framework fallback** path, ignoring the Swift
module that's right there. The fallback then hits a *second* bug: its `clang
-ast-dump` invocation doesn't pre-load Apple system frameworks the vendor
header references, so clang can't resolve `WKWebView`,
`CLLocationManagerDelegate`, etc., and bails.

Result: a perfectly valid Swift framework with a full `swiftinterface` cannot
be bound, even though it has everything the Swift binding path needs.

## Environment

- SwiftBindings.Sdk 0.10.0 / Runtime 0.10.0 / Apple 26.2.2
- .NET SDK 10.0.107, workload 10.0.107
- Xcode 26.2 / Swift 6.2.x
- macOS 26.x, arm64
- Mappedin iOS SDK 6.2.0 (public release zip)

## Repro

```bash
# In swift-dotnet-packages
$ cat libraries/Mappedin/library.json
{
  "repository": "https://github.com/MappedIn/ios.git",
  "version": "6.2.0",
  "mode": "zip",
  "minIOS": "15.0",
  "zipUrl": "https://github.com/MappedIn/ios/releases/download/{version}/Mappedin.xcframework.zip",
  "products": [ { "framework": "Mappedin" } ]
}

# csproj on SwiftBindings.Sdk/0.10.0
$ dotnet nuke BuildLibrary --library Mappedin
```

Zip download + extract succeeds. `dotnet build` then fails inside the binding
generator. The MSBuild output only shows `error MSB3073: command exited with
code 1`; the generator's real output is only visible by running it directly:

```bash
$ dotnet exec "$HOME/.nuget/packages/swiftbindings.sdk/0.10.0/Sdk/../tools/net10.0/any/Swift.Bindings.dll" \
    --xcframework /path/to/libraries/Mappedin/Mappedin.xcframework \
    -o obj/Debug/net10.0-ios/swift-binding/ \
    --platform ios --platform-target simulator \
    --wrapper-architectures all --sdk-mode -v 1 \
    --package-id SwiftBindings.Mappedin \
    --apple-version 26.2.2 \
    --skip-wrapper-compilation
info: BindingsGeneration.BindingsGenerator[0]
      Static library — attempting ObjC framework detection...
info: BindingsGeneration.BindingsGenerator[0]
      Creating combined header from 1 explicit modulemap entries
info: BindingsGeneration.BindingsGenerator[0]
      Invoking clang AST dump: xcrun clang -x objective-c -Xclang -ast-dump=json \
        -isysroot ".../iPhoneSimulator26.2.sdk" \
        -F ".../Mappedin.xcframework/ios-arm64_x86_64-simulator" \
        -fsyntax-only ".../Mappedin_combined.h"
fail: BindingsGeneration.BindingsGenerator[0]
      Clang AST dump failed (exit 1):
      Mappedin-Swift.h:344:49: error: cannot find protocol declaration for 'CLLocationManagerDelegate'
        @interface BlueDot (SWIFT_EXTENSION(Mappedin)) <CLLocationManagerDelegate>
      Mappedin-Swift.h:433:43: error: unknown class name 'WKScriptMessageHandler'; did you mean 'WKScriptMessage'?
        @interface MapViewController : WKWebView <WKScriptMessageHandler>
      Mappedin-Swift.h:433:32: error: cannot find interface declaration for 'WKWebView'
      Mappedin-Swift.h:441:59: error: cannot find protocol declaration for 'WKNavigationDelegate'
      ...
      6 errors generated.
```

## Xcframework structure

Mappedin 6.2.0 publishes a single `Mappedin.xcframework.zip` with two slices:

```
Mappedin.xcframework/
├── Info.plist
├── ios-arm64/
│   └── Mappedin.framework/
│       ├── Mappedin                          # static — `ar archive`
│       ├── Headers/
│       │   ├── Mappedin.h
│       │   └── Mappedin-Swift.h              # Swift→ObjC interop header
│       ├── Modules/
│       │   ├── module.modulemap
│       │   └── Mappedin.swiftmodule/
│       │       ├── arm64-apple-ios.swiftinterface
│       │       ├── arm64-apple-ios.private.swiftinterface
│       │       ├── arm64-apple-ios.package.swiftinterface
│       │       ├── arm64-apple-ios.swiftdoc
│       │       └── arm64-apple-ios.abi.json
│       └── Info.plist
└── ios-arm64_x86_64-simulator/  (same layout)
```

`file Mappedin` reports `current ar archive random library` for both arm64 and
x86_64 — i.e. a true static framework. Both slices have a complete
`Mappedin.swiftmodule` with `.swiftinterface` files.

## Two stacked bugs

### Bug A (load-bearing): static binary → ObjC fallback ignores swiftmodule

The detection logic appears to gate the binding path on the binary's load
command type. If the framework binary is a Mach-O dylib, the Swift binding flow
runs (parses `.swiftinterface`, generates C# from the Swift surface). If the
binary is a static archive, the SDK assumes "ObjC-only static library" and
falls through to the ObjC clang AST dump path — even when a full
`.swiftmodule` directory sits in `Modules/` next to the binary.

A static **Swift** framework is a normal vendor distribution shape (it lets
consumers static-link to avoid the dynamic-framework startup cost) and the
swiftinterface contains exactly the same information whether the framework is
static or dynamic. The detection should be:

```
if Modules/<Module>.swiftmodule exists with .swiftinterface →
    Swift binding path
else if static binary →
    ObjC fallback
```

not:

```
if static binary →
    ObjC fallback
```

This is the only bug that matters for Mappedin specifically — fix this, and
the swiftinterface path produces correct bindings (the previous Mappedin 1.0
dynamic build under SDK 0.9.0 successfully generated a 49k-line `Mappedin.cs`
that bound `MPIMapView : WebKit.WKWebView`, all `WKNavigationDelegate`
methods, etc.).

### Bug B (latent): ObjC AST dump skips system framework imports

Independent of A, the ObjC fallback path's `clang -ast-dump` invocation only
passes `-isysroot` and the xcframework's slice as `-F`. It does not pass
`-framework WebKit -framework CoreLocation` etc., and the vendor's
`Mappedin-Swift.h` doesn't `@import WebKit;` itself — it relies on consumers
having already imported the system frameworks before including it. So clang
sees `WKWebView` / `CLLocationManagerDelegate` and can't resolve them.

Even if the SDK guesses the right system frameworks from the headers it sees,
Apple frameworks are reachable via `@import` once `-fmodules` is on. Probably
the right fix is `-fmodules` on the AST dump, plus a whitelist of common Apple
umbrellas to pre-import (UIKit, Foundation, WebKit, CoreLocation,
AVFoundation, …) when an `*-Swift.h` style header is detected.

This bug only matters if Bug A isn't fixed. With Bug A fixed, the ObjC
fallback path doesn't run for Mappedin at all. But it's worth fixing
separately for any genuinely ObjC-only static library that bridges to system
frameworks.

## Hypothesis

Bug A is most likely a fallback ordering bug in the SDK's framework
detection — possibly a recent change in 0.10.0 (since 0.9.0 handled Mappedin
1.0 dynamic correctly, though I can't verify whether 0.9.0 also misrouted
static-Swift). Worth checking the detection logic for an explicit
swiftmodule-presence check before the binary-kind check.

## Impact

- Blocks the public-distribution path for Mappedin in
  [swift-dotnet-packages](https://github.com/justinwojo/swift-dotnet-packages).
  Without Bug A fixed, we can either (a) keep the in-tree dynamic 1.0
  xcframework that's hand-built out-of-band, or (b) skip Mappedin from the
  release wave entirely.
- Likely affects any other vendor that ships a Swift-based **static**
  xcframework — a common shape for SDKs that want to minimize app launch time.
  Worth searching for `mode: "manual"` libraries in our portfolio that might
  share this distribution shape.

## Workaround

None on the consumer side. Possible local hacks for unblocking development:

- Repackage the static framework as a dynamic framework before binding (lots of
  toolchain plumbing, breaks the use case of static linking).
- Fork Mappedin's distribution and re-export with `@import WebKit;` /
  `@import CoreLocation;` pre-injected into `Mappedin-Swift.h` (only addresses
  Bug B; Bug A still routes incorrectly).

The real fix has to land in
[swift-dotnet-bindings](https://github.com/justinwojo/swift-dotnet-bindings).
Bug A is the high-leverage one — fixing it unblocks Mappedin without needing
to touch the ObjC fallback path at all.

Until then, Mappedin stays on the hand-provisioned 1.0 dynamic xcframework
(`mode: "manual"`).
