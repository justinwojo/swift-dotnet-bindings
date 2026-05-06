# Bug: `IEnumerable<TStruct>` packed as raw `IntPtr` handles + dispose-before-await lifetime bug

> SDK 0.10.0 generator correctness bug (combined ABI confusion + lifetime).
> Discovered 2026-05-05 during the WeatherKit + MusicKit cross-package
> consumer-experience audit (Round 5). See
> [`audit-weatherkit-musickit-2026-05-05.md`](../../swift-dotnet-packages/audit-weatherkit-musickit-2026-05-05.md)
> finding **O-3**.
>
> **Status:**
> - **Defect A (wrong serialization shape) — RESOLVED in Bundle 04** via
>   `NonFrozenStructProjection.SwiftContainerGenericType` returning the typed
>   wrapper and a mirrored skip-conversion rule in
>   `ProtocolProxyEmitter.Receivers.cs`. Validated by `nuke validate`
>   (Kingfisher, RealityFoundation, RealityKit, StripePayments + 3 cascade
>   libs IMPROVED), `nuke binding-tests --sim --device` (sim 1832→1835,
>   device 1845→1848 IMPROVEMENT — `TestSumPointMagnitudesEmpty`,
>   `TestSumPointMagnitudesPayloadByValue`, `TestScalePointsRoundTrip` pass on
>   both Mono JIT and NativeAOT).
> - **Defect B (async `using var` lifetime) — CARVED OUT to Bundle 10** under
>   the closure-lifetime infrastructure umbrella. Tracked separately because
>   it shares the holder/cleanup machinery with Bundle 10's
>   `DeferDeallocate`/captured-closure work. The structural fix follows the
>   `_asyncCallHolder` + `DeferredSafeHandleRelease` precedent already in
>   `WrapperEmitter.Async.cs` / `AsyncHarnessEmitter.cs`.

## Summary

For Swift async methods accepting an `Array<TStruct>` where `TStruct` is
an `ISwiftStruct`-projected Swift value type, the generator emits a
serialization path that packs the **`SafeHandle.DangerousGetHandle()` of
each element** into a `SwiftArray<IntPtr>`. The wrapper expects the array
to hold `Array<TStruct>` (i.e., the Swift array storage holds the
`ISwiftStruct` payload buffer of each element, not a pointer-to-payload).
Two distinct bugs manifest on the same call site:

1. **ABI type confusion** — `SwiftArray<IntPtr>` and `Array<TStruct>` have
   different in-memory layouts. The wrapper interprets the IntPtr-array
   bytes as Swift struct payloads, reading uninitialized / wrong memory.
2. **Lifetime — `using var` disposes the buffer before async completes.**
   The serialization buffer is bound to a `using var` whose scope is the
   foreground method body. The async task continues running *after* the
   method returns, dereferencing the now-freed buffer.

Both bugs fire on every call to the affected overloads. Symptoms range
from "wrong songs enqueued" (best case) to crashes inside Swift async
runtime (typical case).

## Environment

- SwiftBindings.Sdk 0.10.0 / Runtime 0.10.0 / Apple 26.2.1
- .NET SDK 10.0.107, workload 10.0.107
- Xcode 26.3 / Swift 6.2.x / iPhoneOS26.2.sdk
- macOS 26.x, arm64

## Repro

```bash
sed -n '38662,38720p' apple-frameworks/MusicKit/obj/Debug/net10.0-ios26.2/swift-binding/MusicKit.cs
```

```csharp
// MusicKit.cs:38678
public Task InsertAsync(
    IEnumerable<Album> entry,
    EntryInsertionPosition position,
    CancellationToken cancellationToken = default)
{
    // [1] Pack the IEnumerable<Album> as a SwiftArray<IntPtr> by extracting
    //     each Album's payload SafeHandle.
    var entryContainers = entry.Select(e => e.Payload.DangerousGetHandle());
    var entrySwiftDirect = SwiftArray<IntPtr>.FromEnumerable(entryContainers);
    using var entrySwift = entrySwiftDirect;            // [2] using-var lifetime
    IntPtr entryBuffer = entrySwift.Payload.DangerousGetHandle();

    // [3] Async PInvoke — the wrapper expects Array<Album>, not Array<IntPtr>.
    //     Type confusion at the ABI boundary.
    PInvoke_insert_<HASH>(
        s_insertCallback_<HASH>,
        taskHolder,
        taskIndex,
        entryBuffer,                                    // ← raw IntPtr-array bytes
        position,
        ...);

    return tcs.Task;
    // [2′] entrySwift goes out of scope HERE → buffer freed.
    //      Async task continues running for >> 0 ms after this point;
    //      Swift task wakes up and dereferences a freed buffer.
}
```

The bug surfaces on every concrete-element overload at:

- `MusicKit.cs:38662-38719` — `InsertAsync(IEnumerable<Album>, …)`
- `MusicKit.cs:38830` — `InsertAsync(IEnumerable<Playlist>, …)`
- `MusicKit.cs:38998` — `InsertAsync(IEnumerable<Song>, …)`
- `MusicKit.cs:39166` — `InsertAsync(IEnumerable<Track>, …)`

(The fifth, generic-S overload at `:38266` fails earlier on PInvoke
resolution — see **O-1**
[`bug-0.10.0-generic-async-wrapper-symbol-missing.md`](bug-0.10.0-generic-async-wrapper-symbol-missing.md).
With O-1 fixed, the generic overload would land here and hit the same
bug.)

## Hypothesis

Two cooperating defects:

### Defect A — wrong serialization shape

For `Array<T>` parameters where `T` is an `ISwiftStruct`, the wrapper
emit picks `Array<T>` directly (a Swift array whose storage is the
contiguous TStruct payload, e.g. layout-compatible with C struct
`Album { ... }[count]`). The C# binding emit picks `SwiftArray<IntPtr>`
instead — likely because the emitter's "marshal a sequence of Swift
structs" path falls back to "treat the Swift struct as opaque, pack the
SafeHandle pointer." The two sides disagree on the buffer's interior
type.

The fix is to materialize `SwiftArray<TStruct>` (where `TStruct` is the
ISwiftStruct), extract each element's payload bytes by value-copy
(via the type's `MarshalToSwift` / VWT initializeBufferWithCopy
operation), and pack those into a contiguous Swift array buffer. Same
shape the synchronous overload would use.

### Defect B — `using var` lifetime mismatch

Even with serialization fixed, the buffer is currently scoped to the
synchronous method body. Async path needs the buffer to live until the
Swift continuation completes. Two candidate fixes:

- **Hoist into the callback closure** — capture the buffer in the C#
  callback and dispose it from the callback's success/failure branches.
- **Hand off ownership via `DeferredSafeHandleRelease`** — wrap the
  serialization payload in a Swift-side-released holder. Same pattern
  used for SafeHandle async retention, but applied to the buffer.

Either approach extends the buffer's lifetime past `tcs.Task`'s
completion.

## Impact

Every audited `Queue.InsertAsync(IEnumerable<T>, …)` overload is
broken. Combined with **O-1** (the generic overload's
`EntryPointNotFoundException`), the entire MusicKit
**`MusicPlayer.Queue.InsertAsync`** family is unusable.

Workaround for consumers: call the single-item `InsertAsync(Album,
EntryInsertionPosition, CancellationToken)` overload N times in a loop.
This works (single-item overload doesn't go through the SwiftArray
path) but trades one batch round-trip for N round-trips — a 5–10×
wall-clock regression for typical 50-track playlist enqueue, plus
SafeHandle refcount churn proportional to N.

## Severity

**High.** Surface-load-bearing API. Even with O-1 fixed, every C#
consumer of bulk queue insertion hits this on first call, with both
"wrong songs enqueued" and crash modes plausible depending on which
the underlying buffer interpretation lands on.

## Fix gate

`MusicKit.cs:38662-38719` (and the three sibling typed overloads)
should:

- Pack the `IEnumerable<Album>` as `SwiftArray<Album>` (matching the
  wrapper's `Array<Album>` parameter), not `SwiftArray<IntPtr>`.
- Extend the buffer's lifetime past async completion (callback
  capture or DeferredSafeHandleRelease handoff).

A test that calls
`MusicPlayer.Shared.Queue.InsertAsync(new[] { album1, album2 },
EntryInsertionPosition.Tail)` and awaits the task on a real
authorized + subscribed device would catch the bug structurally.

Same structural issue likely affects every `Array<TStruct>` async
parameter the SDK emits — worth a generator-side audit pass once a
fix lands.
