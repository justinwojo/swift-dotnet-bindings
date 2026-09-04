# SwiftBindings 0.19.4

This release addresses the crash chain behind issue #46 — a C# delegate assigned to a Swift `weak` property was collected before the first callback arrived — together with four neighbouring defects the same investigation surfaced while binding VisionKit and LiveCommunicationKit.

## Highlights

- **A C# implementation assigned to a `weak` or `unowned` Swift property stays reachable.** Nearly every Apple delegate property is declared `weak`, which means the Swift side takes no reference on what you assign. The binding's own wrapper around your implementation had nothing else holding it, so the next collection could take it away: the Swift slot read back as `nil` and callbacks never arrived. That wrapper's lifetime now follows your implementation object instead. Hold your own reference to the implementation for as long as you want callbacks; dropping it lets the Swift slot clear, which is what the `weak` declaration says should happen. Properties on this path also carry a `<remarks>` note on the generated surface saying so.
- **Protocols declared `: AnyObject` in an Apple framework now use the compact existential layout.** An ABI dump can spell the `AnyObject` constraint in either of two ways, and the layout rule recognised only the spelling a module compiled from source produces. A framework dump uses the other, so such a protocol was laid out as the wider opaque container while Swift read the compact class one — the callee then dispatched through an empty witness table.
- **Values handed to a C# implementation through a protocol conformance are read with the right ownership.** Enums with associated values, wrapper-backed structs, key paths and optionals were read bitwise in some shapes, which reinterprets Swift payload bytes as a managed reference; a C# `enum : int` over a narrower Swift discriminator also pulled in bytes from the neighbouring value. Those reads now go through a value-witness copy out of the borrowed slot.
- **A protocol requirement introduced after its protocol now reaches your implementation.** The Swift forwarder for such a requirement did not carry the requirement's own availability, so the call bound to the protocol's extension default and a conforming C# implementation was never invoked — with no diagnostic, because nothing failed.
- **`Foundation.UUID` crosses the wrapper boundary correctly.** A `UUID` parameter was passed by value where Swift expects an `NSUUID` pointer.
- **A tuple parameter carrying a payload-less enum element arrives instead of throwing.** A protocol requirement taking, say, `(MyCase, Int)` threw out of the receiver before your implementation ran, because the element's zero-size payload was read as though it had one.
- **An optional `Data` or `Date` that Swift sent as `nil` now reaches your implementation as `null`.** It previously arrived as a present-but-empty value — a zero-length `byte[]`, or a date at the Swift epoch — which an implementation had no way to tell apart from a real one.
- **A key path parameter whose type argument is `String` resolves.** Such a requirement previously failed metadata resolution at the boundary rather than reaching your implementation.
- **Reading a case of an enum with associated values no longer holds on to the payload.** Every case accessor retained its payload once without a matching release, so repeatedly reading such an enum grew native memory.
- **On device (NativeAOT), a delegate receiving a tuple parameter with a non-primitive element no longer terminates the process.** Resolving the element's metadata went through reflection over a generic method, which NativeAOT cannot close at run time; that path is gone. The same call already worked on the simulator, so this was a device-only stop.
- Two smaller fixes ride along: registering a `Hashable` enum's conformance no longer races a caller already in flight, and a thunked setter's ownership hand-over is now balanced on teardown.

## Behaviour changes you may notice

One of these fixes changes what already-compiling code does.

- **A protocol-proxy member whose availability floor is above its protocol's now throws `PlatformNotSupportedException` below that floor** rather than calling through. `[SupportedOSPlatform]` is a compile-time hint, so a call that suppresses CA1416 previously went ahead; it now refuses at the boundary. This mirrors Swift's own rule that a requirement introduced after its protocol is callable only above its own floor, and it covers the case where the Swift implementation behind the requirement uses APIs from that same OS version. Two remedies: guard the call with an OS version check (`OperatingSystem.IsIOSVersionAtLeast`, say), or raise the app's minimum OS version. Code that already respects the `[SupportedOSPlatform]` attribute on the member sees no change, and the guard is emitted only where the member's floor is above the proxy type's own — across the Apple framework bindings we regenerated, one member picks it up.

## Consumer-owned delegates: what happens when the implementation goes away

When you assign a C# object into a Swift `weak`, `unowned` or `unowned(unsafe)` property, nothing on
the Swift side keeps it alive — that is what the annotation asks for. Your application is the only
owner, and if you stop referencing the object, the garbage collector may collect it.

Swift, meanwhile, may still call. A framework that takes a delegate through a `weak` property often
also holds it somewhere else for the duration of a piece of work: an internal observer array, a
closure captured by a queued block, an operation already in flight. Through that other reference the
Swift object keeps calling the conformance long after your own reference is gone. A callback can
also simply race a drop on another thread.

Previously a callback arriving in that state stopped the process. It now degrades instead, following
what Swift itself does with a `nil` weak delegate:

| The requirement returns | The degraded call gives Swift |
|---|---|
| `Void` | nothing — the call is dropped |
| An optional | `nil` |
| `Bool` | `false` |
| An integer or floating-point type | `0` |
| `String` | `""` |
| `Array`, `Set`, `Dictionary` | the empty collection |
| A frozen value type with no reference fields | its zeroed value |
| A closure | `nil` |
| `async throws` | a thrown error your `await` observes |

Delegates assigned into ordinary **strong** Swift properties are unaffected. Swift holds those for as
long as it holds the conformance, so a missing implementation there still indicates something has
gone wrong internally and is still reported loudly.

### Finding out that it happened

A degraded callback is quiet by design, which makes "my delegate stopped firing" hard to explain. So
the first time a given conformance degrades, the runtime writes a line through `System.Diagnostics.Trace`
and raises `ProxyDegradation.ImplCollected`. Later callbacks on the same conformance stay silent, so a
per-frame delegate will not fill a log.

```csharp
Swift.Runtime.ProxyDegradation.ImplCollected += (_, e) =>
    Console.WriteLine($"Swift called {e.Member} on a collected implementation (0x{e.Handle:X})");
```

`ProxyDegradation.ReportCount` gives the number of conformances that have reported so far, which is
convenient to assert on in a test.

### Two shapes that still stop

**A synchronous `throws` requirement cannot report the error.** Its reverse-dispatch entry point
returns a value and carries no error slot, so a degraded synchronous `throws` call comes back as the
identity value for its return type, exactly like a non-throwing one. Only `async throws` requirements,
which resume a continuation, have somewhere to put a failure.

**Some return types have no value to synthesize.** A non-optional class or existential has no null to
give; an enumeration's zeroed form is a real case, but a specific one your code never chose, and
silently returning it would be worse than saying so; a resilient (non-frozen) struct such as
`Foundation.URL` gives no assurance that zeroed bytes are a valid instance. For those, a degraded
callback still stops the process, with a message naming the member, the conformance, and the fix:

> Swift called '…' on a C# implementation that was already collected, and the requirement returns
> '…' — a type with no value this binding can synthesize on the caller's behalf. … Keep a reference
> to the implementation for as long as the Swift side may call it. This is a lifetime mistake in
> application code, not a binding defect.

The element type of a collection is not part of this: an empty `[any P]` needs no element, so it is
synthesized like any other empty array.

### The remedy

Degradation keeps a lifetime mistake from taking the process down; it does not make the callbacks
arrive. If you want them to keep arriving, hold a reference to the implementation for as long as the
Swift side may call it — a field on the owning view controller, view model, or service is usually the
right place. Assigning into a `weak` Swift property and keeping no reference of your own means asking
for the object to be collected.

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
