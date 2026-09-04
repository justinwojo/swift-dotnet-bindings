# SwiftBindings 0.19.4

This release addresses the crash chain behind issue #46 — a C# delegate assigned to a Swift `weak` property was collected before the first callback arrived — together with four neighbouring defects the same investigation surfaced while binding VisionKit and LiveCommunicationKit.

## Highlights

- **A C# implementation assigned to a `weak` or `unowned` Swift property stays reachable.** Nearly every Apple delegate property is declared `weak`, which means the Swift side takes no reference on what you assign. The binding's own wrapper around your implementation had nothing else holding it, so the next collection could take it away: the Swift slot read back as `nil` and callbacks never arrived. That wrapper's lifetime now follows your implementation object instead. Hold your own reference to the implementation for as long as you want callbacks; dropping it lets the Swift slot clear, which is what the `weak` declaration says should happen. Properties on this path also carry a `<remarks>` note on the generated surface saying so.
- **Protocols declared `: AnyObject` in an Apple framework now use the compact existential layout.** An ABI dump can spell the `AnyObject` constraint in either of two ways, and the layout rule recognised only the spelling a module compiled from source produces. A framework dump uses the other, so such a protocol was laid out as the wider opaque container while Swift read the compact class one — the callee then dispatched through an empty witness table.
- **Values handed to a C# implementation through a protocol conformance are read with the right ownership.** Enums with associated values, wrapper-backed structs, key paths and optionals were read bitwise in some shapes, which reinterprets Swift payload bytes as a managed reference; a C# `enum : int` over a narrower Swift discriminator also pulled in bytes from the neighbouring value. Those reads now go through a value-witness copy out of the borrowed slot.
- **A protocol requirement introduced after its protocol now reaches your implementation.** The Swift forwarder for such a requirement did not carry the requirement's own availability, so the call bound to the protocol's extension default and a conforming C# implementation was never invoked — with no diagnostic, because nothing failed.
- **`Foundation.UUID` crosses the wrapper boundary correctly.** A `UUID` parameter was passed by value where Swift expects an `NSUUID` pointer.
- Two smaller fixes ride along: registering a `Hashable` enum's conformance no longer races a caller already in flight, and a thunked setter's ownership hand-over is now balanced on teardown.

## Behaviour changes you may notice

One of these fixes changes what already-compiling code does.

- **A protocol-proxy member whose availability floor is above its protocol's now throws `PlatformNotSupportedException` below that floor** rather than calling through. `[SupportedOSPlatform]` is a compile-time hint, so a call that suppresses CA1416 previously went ahead; it now refuses at the boundary. This mirrors Swift's own rule that a requirement introduced after its protocol is callable only above its own floor, and it covers the case where the Swift implementation behind the requirement uses APIs from that same OS version. Two remedies: guard the call with an OS version check (`OperatingSystem.IsIOSVersionAtLeast`, say), or raise the app's minimum OS version. Code that already respects the `[SupportedOSPlatform]` attribute on the member sees no change, and the guard is emitted only where the member's floor is above the proxy type's own — across the Apple framework bindings we regenerated, one member picks it up.

## Consumer-owned delegates: what happens when the implementation goes away

<!-- W1: fill in lane-B degraded-callback behaviour -->

## Reported issues fixed

- **[#46](https://github.com/justinwojo/swift-dotnet-bindings/issues/46): a delegate assigned to a VisionKit `weak` property never received callbacks.** Assigning a C# implementation to `DataScannerViewController.delegate` left the Swift slot `nil` shortly afterwards. The delegate lifetime fix above is the primary change; the existential-layout, receiver-read, availability-forwarder and `UUID` fixes were all found on the same path and ship with it.

## Packages

| Package                  | Version |
|--------------------------|---------|
| SwiftBindings.Runtime    | 0.19.4  |
| SwiftBindings.Sdk        | 0.19.4  |
| SwiftBindings.Templates  | 0.19.4  |

`SwiftBindings.Apple` is unchanged at `26.2.8`. It declares a floor-only Runtime range, so the published supplement rides forward without a republish.

See the [GitHub wiki](https://github.com/justinwojo/swift-dotnet-bindings/wiki) for installation and usage.
