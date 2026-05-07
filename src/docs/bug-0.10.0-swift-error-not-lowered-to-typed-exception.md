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
catch (SwiftException<WeatherError> ex) when (ex.Error == WeatherError.PermissionDenied)
{
    // typed dispatch on Swift case
}
```

…should compile and dispatch correctly. The generator should:

- Emit `SwiftException<WeatherError>` for plain-throws async methods
  whose dynamic error type matches a known Error-conforming enum,
  exposing `Error` and any associated-value accessors.
- Modify the Swift wrapper to encode the error case (and
  `RawRepresentable` payload, if any) on the wire instead of
  stringifying.
- Modify the C# async-callback handler to reconstruct and raise the
  typed exception.

Pair with **O-7** in the next SDK iteration's "type-system fidelity"
priority slot.

## Implementation design (D2 investigation, 2026-05-07)

### What's already shipped (typed-throws path)

The generator already supports Swift 6's typed throws (`throws(T)` syntax) end-to-end:

- `MethodDecl.HasTypedThrows` is true for `func foo() async throws(T)`.
- `WrapperEmitter.cs:114` resolves the typed error type via
  `TypeDatabase.TryGetTypeRecord(ThrownErrorType)` and sets
  `useTypedErrorCallback = true`.
- `WrapperEmitter.Async.cs:2087-2191` emits a 5-param callback
  `(errorPtr, errorSize, errorMessagePtr, isCancellation, task)` instead of
  the 3-param untyped callback.
- The C# error path uses `SwiftMarshal.MarshalFromSwift<TError>(errorPtr)` to
  reconstruct the error and throws `SwiftException<TError>`.
- `Swift.Runtime/SwiftException.cs` defines `SwiftException<TError>` with
  the typed `Error` property.
- `BindingTests/Sources/SwiftBindingsTestLib/ErrorHandling/TypedThrows.swift`
  exercises the path with `ParseError`, `RangeError`, and
  `TypedThrowingParser.asyncParse(...) async throws(ParseError)`.

### What's missing (this bug — plain-throws bridging)

WeatherKit/MusicKit/StoreKit2 declare `func f() async throws -> T` (plain
throws), not `throws(WeatherError)`. The Swift compiler doesn't statically
encode the error type. So `MethodDecl.HasTypedThrows` is false and the
generator falls through to the stringification path.

To bridge plain-throws methods to typed exceptions, we need:

1. **Module-scope error-enum enumeration**. At module-emission time, walk
   every `EnumDecl` (and `StructDecl` / `ClassDecl`) that declares
   `Error` / `LocalizedError` / `Foundation.LocalizedError` conformance and
   build a per-module registry: `errorTypeId → SwiftType`. Output the
   registry to the C# side as a static `Dictionary<int, Type>` plus a
   matching Swift-side switch.

2. **Wire format extension**. The 5-param error callback grows a
   discriminator: `(errorPtr, errorSize, errorMessagePtr, isCancellation, task,
   errorTypeId)`. `errorTypeId == 0` means untyped (existing fallback);
   any other value indexes into the registry.

3. **Swift wrapper conditional encoding**. The catch block performs an
   ordered cascade of `as?` casts against each known error type:

   ```swift
   } catch {
       if let typed = error as? WeatherError {
           let buf = UnsafeMutableRawPointer.allocate(byteCount: MemoryLayout<WeatherError>.stride, alignment: ...)
           buf.initializeMemory(as: WeatherError.self, repeating: typed, count: 1)
           callback(taskHolder, taskIndex, nil, buf, MemoryLayout<WeatherError>.stride,
                    String(describing: error).utf8CStringPtr,
                    /* isCancellation */ 0, /* errorTypeId */ 1, /* task */ task)
       } else if let typed = error as? OtherError {
           ...
       } else {
           // existing stringification fallback
       }
   }
   ```

   The cascade lives in a per-module helper `_dispatchSwiftError(_:_:...)`
   shared across all wrappers in the module so the body emits once instead
   of per-method.

4. **C# callback dispatch on errorTypeId**. The 6-param C# callback
   inspects `errorTypeId`, looks up the C# type via the registry, and uses
   `MarshalFromSwift<TError>` to reconstruct the typed error. Throws
   `SwiftException<TError>` (typed) when the id is non-zero, falls back to
   `SwiftException` (untyped) when the id is zero.

5. **Reuse Session B `_SBClosureCtx` owner-token**. The Swift-allocated
   error buffer follows the same Swift-ARC-owned-box ownership shape
   established for closures: the wrapper allocates the box, hands it to
   the callback, and the C# side calls `SBW_Free` after MarshalFromSwift
   takes ownership (or in `finally` for value-copy errors). This is
   identical to the current typed-throws path's
   `typedErrorTransfersOwnershipAsync` shape — the new path inherits it
   without modification.

### Why this is multi-session work

Layer 1 is mechanical (one new emitter pass + module-scope registry build).
Layer 2 is a wire-format change that ripples through five emission sites
(`WrapperEmitter.Async.cs` 5-param callback, `AsyncHarnessEmitter.cs`
helper-extracted twin, plus the runtime-side `SBW_*` symbol surface for
the cascade helper). Layer 3 is the new emit pass for the Swift-side
cascade helper plus per-method conditional. Layer 4 is the C# dispatcher
update. Layer 5 is verifying the existing transfer-ownership logic still
holds for plain-throws errors that may carry payload buffers (associated
values).

The mechanical changes are straightforward but the testing surface is
broad: every existing typed-throws test has to keep passing, every
plain-throws test has to keep passing, and the new path's catch-cascade
ordering has to be deterministic across re-runs (alphabetical by error
type name is the obvious ordering).

A careful single-session implementation would need ~6 emit-site edits,
~4 runtime additions, fixture coverage for at least 3 module shapes
(simple enum, enum with associated values, struct error) and runtime
tests on both Mono JIT and NativeAOT to catch the wire-format change
breaking either runtime's marshalling.

D2 investigation summary: groundwork for layers 1-5 is identified and the
existing typed-throws scaffolding (5-param callback, `SwiftException<T>`,
`MarshalFromSwift<T>`, transfer-ownership tracking) is the right base.
The bug stays open as a definite-scope follow-up; estimated landing in
the next dedicated error-bridging session (Session D3 or E pre-release).
