# SwiftBindings 0.19.4

This release fixes the crash chain behind a reported VisionKit failure: a C# delegate assigned to a Swift `weak` property was collected before the first callback arrived, so the Swift slot read back as `nil` and callbacks never came. The neighbouring defects the same investigation surfaced ship with it.

## Highlights

- **A C# implementation assigned to a `weak` or `unowned` Swift property stays reachable** — nearly every Apple delegate property is declared `weak`, so Swift takes no reference on what you assign and nothing held the binding's wrapper around your object. That wrapper's lifetime now follows your implementation; keep your own reference for as long as you want callbacks.
- **Protocols declared `: AnyObject` in an Apple framework now use the compact existential layout** — an ABI dump can spell that constraint in either of two ways and only one was recognised, so such a protocol was laid out as the wider opaque container while Swift read the compact class one, and the callee dispatched through an empty witness table.
- **Values handed to your implementation through a protocol conformance are read with the right ownership** — enums with associated values, wrapper-backed structs, key paths and optionals were read bitwise in some shapes, and an optional `Data` or `Date` that Swift sent as `nil` arrived as a present-but-empty value.
- **A protocol requirement introduced after its protocol now reaches your implementation** — its Swift forwarder did not carry the requirement's own availability, so the call bound to the protocol's extension default and a conforming C# implementation was never invoked, with no diagnostic.
- **On device (NativeAOT), a delegate receiving a tuple parameter with a non-primitive element no longer terminates the process** — resolving the element's metadata went through reflection NativeAOT cannot close at run time. The same call already worked on the simulator, so this was a device-only stop.

## Bug fixes

- `Foundation.UUID` was passed by value across the wrapper boundary where Swift expects an `NSUUID` pointer.
- A protocol requirement taking a tuple with a payload-less enum element threw out of the receiver before your implementation ran, because the element's zero-size payload was read as though it had one.
- A key path parameter whose type argument is `String` failed metadata resolution at the boundary rather than reaching your implementation.
- Reading a case of an enum with associated values retained the payload without a matching release, so repeatedly reading such an enum grew native memory.
- Registering a `Hashable` enum's conformance no longer races a caller already in flight, and a thunked setter's ownership hand-over is now balanced on teardown.

## Behaviour changes

- **A callback arriving on an implementation that was already collected degrades instead of stopping the process.** It now returns what Swift returns for a `nil` weak delegate — `Void` dropped, optionals `nil`, numbers `0`, collections empty, `async throws` throwing — and the first one on a conformance raises `Swift.Runtime.ProxyDegradation.ImplCollected`. A few return types cannot be synthesized and still stop. The rules, and how to keep the callbacks arriving instead, are in [Ownership](https://github.com/justinwojo/swift-dotnet-bindings/wiki/Ownership#delegates-in-weak-swift-properties).
- **A protocol-proxy member whose availability floor is above its protocol's now throws `PlatformNotSupportedException` below that floor** rather than calling through, mirroring Swift's own rule. `[SupportedOSPlatform]` is a compile-time hint, so a call that suppresses CA1416 previously went ahead. Guard it with an OS version check or raise the app's minimum OS version; across the Apple framework bindings we regenerated, one member picks this up.

## Reported issues fixed

- **[#46](https://github.com/justinwojo/swift-dotnet-bindings/issues/46): a delegate assigned to a VisionKit `weak` property never received callbacks.** Assigning a C# implementation to `DataScannerViewController.delegate` left the Swift slot `nil` shortly afterwards. The delegate lifetime fix above is the primary change; the existential-layout, receiver-read, availability-forwarder and `UUID` fixes were all found on the same path and ship with it.

## Coverage

The end-to-end suite now also runs on a physical device under Mono full-AOT — the runtime a .NET for iOS or MAUI app builds against unless it opts into NativeAOT — alongside the existing NativeAOT device pass.

## Packages

| Package                  | Version |
|--------------------------|---------|
| SwiftBindings.Runtime    | 0.19.4  |
| SwiftBindings.Sdk        | 0.19.4  |
| SwiftBindings.Templates  | 0.19.4  |

`SwiftBindings.Apple` is unchanged at `26.2.8`. It declares a floor-only Runtime range, so the published supplement rides forward without a republish.

See the [GitHub wiki](https://github.com/justinwojo/swift-dotnet-bindings/wiki) for installation and usage.
