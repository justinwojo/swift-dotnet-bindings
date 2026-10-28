# SwiftBindings 0.20.2

This release fixes apps crashing at launch on an older OS when a binding includes Swift APIs newer than the app's minimum OS version. To pick it up, update the package and rebuild your binding; nothing in the generated C# API changes.

## Highlights

- **No more launch crash on older OS versions** — an app targeting iOS 15 could be killed at startup because the binding's framework required APIs that only exist on iOS 16 or later, even if the app never called them. Those APIs are now linked optionally, so the app launches, and calling one on an OS that lacks it throws a `PlatformNotSupportedException` instead of crashing.
- **Apple framework packages** — if you use one of the `SwiftBindings.Apple.*` framework packages (such as StoreKit2), update to the version built on this SDK once it is published.

## Reported issues fixed

- **[#49](https://github.com/justinwojo/swift-dotnet-bindings/issues/49) — StoreKit2 crashes at startup on iOS 15** — the generated framework required `AppStore.requestReview(in:)` and `Message.display(in:)`, which arrived in iOS 16, so iOS 15 refused to load it. It now loads on iOS 15, where calling either method throws `PlatformNotSupportedException`.

## Packages

| Package                  | Version |
|--------------------------|---------|
| SwiftBindings.Runtime    | 0.20.2  |
| SwiftBindings.Sdk        | 0.20.2  |
| SwiftBindings.Templates  | 0.20.2  |

`SwiftBindings.Apple` is unchanged at `26.2.9`. It declares a floor-only Runtime range, so the published supplement rides forward without a republish.

See the [GitHub wiki](https://github.com/justinwojo/swift-dotnet-bindings/wiki) for installation and usage.
