# Bug: Swift `Error` enum lowered as flat `int` enum; async wrappers stringify error via `String(describing:)` instead of bridging to a typed exception

> SDK 0.10.0 generator correctness bug + feature gap. Discovered 2026-05-05
> during the WeatherKit + MusicKit cross-package consumer-experience audit
> (Round 5). See
> [`audit-weatherkit-musickit-2026-05-05.md`](../../swift-dotnet-packages/audit-weatherkit-musickit-2026-05-05.md)
> finding **O-8**.

## Summary

WeatherKit's `WeatherError` is a Swift enum conforming to
`Foundation.LocalizedError` / `Swift.Error`:

```swift
public enum WeatherError : Foundation.LocalizedError {
    case permissionDenied, locationNotFound, unknown(any Error)
    ...
}
```

The C# binding lowers it as a flat `enum WeatherError : int { ... }`
with no `[SwiftError]` attribute, no exception-bridge implementation,
and no typed-catch path. The async wrappers stringify thrown errors
via `String(describing: error)` and feed the string into a
generic `NSError` userInfo dictionary; consumers see a generic
`Exception("permissionDenied")` whose `.Message` is the stringified
case name.

Consumers cannot write:

```csharp
try { await WeatherService.Shared.WeatherAsync(loc); }
catch (WeatherException ex) when (ex.Error == WeatherError.PermissionDenied)
{ ... }
```

— because there is no `WeatherException` typed bridge and the enum
case isn't exposed on the generic `Exception`.

## Environment

- SwiftBindings.Sdk 0.10.0 / Runtime 0.10.0 / Apple 26.2.1
- .NET SDK 10.0.107
- Xcode 26.3 / Swift 6.2.x / iPhoneOS26.2.sdk
- macOS 26.x, arm64

## Repro — generated C#

```bash
sed -n '6934,6973p' apple-frameworks/WeatherKit/obj/Debug/net10.0-ios26.2/swift-binding/WeatherKit.cs
```

```csharp
// WeatherKit.cs:6934
[SwiftFrozenEnum]
public enum WeatherError : int
{
    PermissionDenied = 0,
    LocationNotFound = 1,
    Unknown = 2,
    ...
}
```

Plain int-backed enum. No `[SwiftError]` attribute. No matching
`WeatherException : Exception` class.

## Repro — Swift wrapper stringifies error

```bash
sed -n '4800,4810p' apple-frameworks/WeatherKit/obj/Debug/net10.0-ios26.2/swift-binding/WeatherKit.Wrapper.swift
```

```swift
// WeatherKit.Wrapper.swift:4806  (inside WeatherService.weather async wrapper)
do {
    let result = try await service.weather(for: location)
    callback(taskHolder, taskIndex, resultBuf, nil, /* errorPtr */ nil)
} catch {
    let nserror = NSError(
        domain: "WeatherKit.weather",
        code: 1,
        userInfo: [NSLocalizedDescriptionKey: String(describing: error)])
    callback(taskHolder, taskIndex, nil, /* resultBuf */ nil, withUnsafePointer(to: nserror) { $0 })
}
```

The Swift error is stringified at the wrapper boundary. The C#
callback receives an NSError whose `localizedDescription` is the
case name (e.g. `"permissionDenied"`), wraps it as a generic
`NSException` / managed `Exception`, and there's no typed dispatch
path on the consumer side.

## Hypothesis

Two cooperating gaps in the SDK's error-bridging support:

### Gap A — No `[SwiftError]` attribute / typed exception emission

The generator has no facility for emitting an
`{ErrorEnumName}Exception : Exception` paired type that carries the
Swift case as a strongly-typed property. Swift `Error` conformance is
ignored during enum lowering — the enum is treated as a normal
data-bearing type.

### Gap B — Wrapper stringifies the error rather than passing the case payload

Even if the C# side had a typed exception, the Swift wrapper
boundary throws away the case and associated payload during
stringification. The wrapper would need to:
- Identify the error as a known `Error`-conforming enum,
- Encode the case index and any `RawRepresentable`-backed payload
  into a known-shape struct,
- Pass that struct (rather than `String(describing:)`) to the
  callback,
- C# side reconstructs the enum case + payload on receipt and
  raises a typed `WeatherException`.

The two halves move together: Gap B is unblocked once the SDK
defines a stable wire format for thrown enums; Gap A is the C#-side
bridging that consumes that format.

## Affected sites

WeatherKit:

- `WeatherKit.cs:6934-6973` — `WeatherError` lowered as flat int enum
- `WeatherKit.Wrapper.swift:4806` — async wrappers stringify error
- Every `WeatherService.*Async` method — every async data-fetch goes
  through the stringified-error path

Cross-cutting risk: any Swift framework with `async throws` and a
domain-specific `Error`-conforming enum has the same shape. Examples:

- `MusicKit.MusicAuthorization.RequestError`
- `MusicKit.MusicLibraryError`
- `StoreKit2.StoreKitError`
- All `Foundation.URLError` subtypes
- Any third-party framework with a custom error enum

A generator-wide audit of `swiftinterface` declarations for
`enum X : Error` should enumerate the affected types.

## Impact

Consumers cannot write idiomatic typed-catch code for Apple framework
async APIs. The workaround is to parse `ex.Message` for the case
name string, which:

- Breaks on Swift compiler version changes that reword
  `String(describing:)` output.
- Breaks on internationalization (when Swift starts localizing the
  description string).
- Breaks for cases with associated values (the description includes
  the payload, mangled).

WeatherKit specifically: every consumer hitting a permission failure,
a no-data-for-location failure, or a network failure receives the
same generic `Exception` and must string-match. This is a real
ergonomic regression from Swift's typed-throws idiom.

## Severity

**High** for any consumer relying on case-based error handling.
Cross-cutting infrastructure work — affects every Apple framework
binding with `async throws`.

## Fix gate

After fix:

```csharp
try { await WeatherService.Shared.WeatherAsync(loc); }
catch (WeatherException ex) when (ex.Error == WeatherError.PermissionDenied)
{
    // typed dispatch on Swift case
}
```

…should compile and dispatch correctly. The generator should:

- Emit a `WeatherException : Exception` paired with the
  `WeatherError` enum, exposing `Error` and any associated-value
  accessors.
- Modify the Swift wrapper to encode the error case (and
  `RawRepresentable` payload, if any) on the wire instead of
  stringifying.
- Modify the C# async-callback handler to reconstruct and raise the
  typed exception.

Pair with **O-7** in the next SDK iteration's "type-system fidelity"
priority slot.
