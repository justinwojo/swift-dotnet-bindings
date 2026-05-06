# Bug: Callback parameter projection diverges from async-return projection

> SDK 0.10.0 generator correctness/consistency bug. Discovered 2026-05-05
> during a consumer-experience audit of
> [SwiftBindings.Nuke](https://github.com/justinwojo/swift-dotnet-packages)
> 13.0.5 generated bindings.

## Summary

The same Swift parameter type is projected to two different C# types
depending on whether it appears as an *async return tuple element* or
as a *callback closure argument*. The async path applies the full set
of standard projection rules — `Foundation.Data` → `byte[]`,
`Foundation.URLResponse?` → `Foundation.NSUrlResponse?`. The callback
path leaves the Swift type names raw —
`Swift.Foundation.Data`, `Swift.SwiftOptional<IntPtr>`.

Net result: a consumer who picks the callback overload (or whose use
case requires it) writes Swift-runtime-shaped C# code instead of
ordinary `byte[]` / nullable-NSObject code.

## Environment

- SwiftBindings.Sdk 0.10.0 / Runtime 0.10.0 / Apple 26.2.2
- .NET SDK 10.0.107, workload 10.0.107
- Xcode 26.3 / Swift 6.2.x
- macOS 26.x, arm64
- Source library: Nuke 13.0.5

## Repro — same Swift type, two C# projections

```bash
grep -n "DataAsync\|public unsafe Nuke.ImageTask LoadData" \
   libraries/Nuke/obj/Debug/net10.0-ios/swift-binding/Nuke.cs | head
```

Async path (correct projection):

```csharp
// Nuke.cs:15087
public Task<(byte[], Foundation.NSUrlResponse?)> DataAsync(
    Nuke.ImageRequest url, CancellationToken cancellationToken = default)
{ … }
```

Callback path (raw Swift types):

```csharp
// Nuke.cs:15326
public unsafe Nuke.ImageTask LoadData(
    Nuke.ImageRequest url,
    Action<Swift.SwiftResult<
        (Swift.Foundation.Data data, Swift.SwiftOptional<IntPtr> response),
        Nuke.ImagePipeline.Error>> completion)
{ … }

// Nuke.cs:15378  (3-arg overload — same projection problem)
public unsafe Nuke.ImageTask LoadData(
    Nuke.ImageRequest request,
    Action<long, long>? progressHandler,
    Action<Swift.SwiftResult<
        (Swift.Foundation.Data data, Swift.SwiftOptional<IntPtr> response),
        Nuke.ImagePipeline.Error>> completion)
{ … }
```

## Native ground truth

```text
swiftinterface (line 788):
  final public func data(for request: Nuke.ImageRequest) async throws
      -> (Foundation.Data, Foundation.URLResponse?)

swiftinterface (line 559):
  final public func loadData(with request: Foundation.URLRequest,
      didReceiveData: @escaping (Foundation.Data, Foundation.URLResponse) -> Swift.Void,
      completion: @escaping ((any Swift.Error)?) -> Swift.Void)
      -> any Nuke.Cancellable
```

Same `Foundation.Data` and `Foundation.URLResponse?` Swift types in both
positions. The async return-tuple projector handles them; the closure
arg-tuple projector doesn't.

## Hypothesis

Two different C# emission paths for "type appears in a tuple" share no
projection logic. The async-return-tuple path runs Swift→C# projection
rules (likely the same set used for top-level method parameters and
returns). The closure-arg-tuple path leaves the type as the raw Swift
runtime representation.

A second piece of evidence: `Foundation.URLResponse?` projects to
`Foundation.NSUrlResponse?` in the async path. In the callback path it
becomes `Swift.SwiftOptional<IntPtr>` — the **inner** type isn't even
`NSUrlResponse`, it's `IntPtr`. The closure-arg path appears to drop
*both* the optional projection (`SwiftOptional<T>` → `T?`) **and** the
NSObject lookup (`URLResponse` → `NSUrlResponse`). Two missed rules in
one type.

Plausible fix site: the closure-arg-type projector in the generator
should call into the same `ProjectSwiftType(...)` (or equivalent)
that the parameter/return projector uses, instead of stringifying the
Swift type directly.

## Why the consumer pays for this

Concretely, a consumer who needs the callback overload (e.g. because
they need the synchronous `ImageTask` return value alongside async
data) writes:

```csharp
// What you write today
pipeline.LoadData(request, completion: result =>
{
    if (result.IsSuccess)
    {
        var (data, response) = result.SuccessValue;
        // data is Swift.Foundation.Data — must call .ToByteArray() or similar
        // response is Swift.SwiftOptional<IntPtr> — must unbox via SwiftMarshal
    }
});

// What you should be able to write (matches DataAsync)
pipeline.LoadData(request, completion: result =>
{
    if (result.IsSuccess)
    {
        var (data, response) = result.SuccessValue;
        // data : byte[]
        // response : Foundation.NSUrlResponse?
    }
});
```

The mismatch also defeats refactoring: code that switches between
`LoadData` (callback) and `DataAsync` (async) — common when retrofitting
sync-style APIs onto an async pipeline or vice versa — has to change
the result-handling shape too, even though the underlying Swift API
returns identical values.

## Impact

- **Consumer-experience.** Callback wrappers across the SDK are
  effectively second-class. They expose Swift-runtime types that
  consumers shouldn't have to know about.
- **Library scope.** Anywhere the generator emits a callback closure
  whose arg type is `Foundation.Data`, `Foundation.URLResponse?`, an
  optional NSObject, or any other type that has a known Swift→C#
  projection. Likely affects every callback-style API across the 14
  third-party libraries — needs a cross-library scan after the fix.
- **Adjacent to the GCHandle leak in C2** ([bug-0.10.0-callback-trampoline-gchandle-leak.md](./bug-0.10.0-callback-trampoline-gchandle-leak.md)) —
  same callback-marshalling subsystem. Both bugs make the callback
  overloads worse than the async overloads in different ways. Worth
  fixing together.

## Workaround

Consumer side: read the raw Swift types and unmarshal manually
(`SwiftMarshal.MarshalFromSwift<…>(handle)`). Tedious and requires
runtime knowledge that ordinary C# consumers don't have.

Proper fix in
[swift-dotnet-bindings](https://github.com/justinwojo/swift-dotnet-bindings):
unify closure-arg-type projection with parameter/return type projection.

## Severity

**Correctness — Medium.** Compiles and runs correctly — the consumer
just gets ugly types they have to manually unwrap. No memory corruption
or leak. But the symmetry between async and callback overloads is part
of the SDK's "feel" promise, and right now it's broken.
