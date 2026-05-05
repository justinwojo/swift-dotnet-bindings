# Gap: Swift `Sendable` conformance silently dropped — no C# signal of thread-safe-by-design types

> SDK 0.10.0 generator documentation gap. Discovered 2026-05-05 during
> the WeatherKit + MusicKit cross-package consumer-experience audit
> (Round 5). See
> [`audit-weatherkit-musickit-2026-05-05.md`](../../swift-dotnet-packages/audit-weatherkit-musickit-2026-05-05.md)
> finding **O-11**.

## Summary

Swift `Sendable` conformance — the marker that says "instances of this
type are safe to share across actor / concurrency boundaries" — is not
reflected in the C# binding in any form. There's no `[Sendable]`
attribute, no XML doc note, no marker interface. Consumers who would
otherwise gate a thread-safety decision on the Swift type's Sendable
declaration get no signal.

The defect is "silent loss of API-surface metadata," not a
correctness bug. Marking it explicitly so the next SDK ship can
decide what (if anything) to project.

## Environment

- SwiftBindings.Sdk 0.10.0 / Runtime 0.10.0 / Apple 26.2.1
- .NET SDK 10.0.107
- Xcode 26.3 / Swift 6.2.x / iPhoneOS26.2.sdk
- macOS 26.x, arm64

## Repro

```text
swiftinterface (MusicKit, line 591):
  extension MusicItemCollection : Swift.Sendable
    where MusicItemType : Swift.Sendable

swiftinterface (WeatherKit, line 393):
  public struct WeatherCondition : Swift.Hashable, Swift.Sendable, ...
```

Generated C# emits `WeatherCondition` and `MusicItemCollection<T>` with
no Sendable marker — neither the conformance nor a doc note about it.

## Hypothesis

There is no clean C# equivalent for `Sendable`:

- `[ThreadSafe]` is not a framework attribute (no
  `System.ThreadSafeAttribute`).
- `record` types in C# 9+ have no concurrency-safety semantics
  attached.
- The closest existing C# concept is "immutable value type" (a
  `readonly struct` or a `record`); only some Sendable Swift types
  meet that bar.

So the generator's options are:

- **Project as XML doc.** Add `<remarks>This type is Sendable in
  Swift; instances may be shared across .NET threads.</remarks>` to
  the generated type's XML doc. Survives across IDEs without a
  framework attribute.
- **Project as a marker attribute.** Define
  `[SwiftSendable]` in `SwiftBindings.Runtime` and apply it.
  Consumer-discoverable but requires consumers to know the attribute
  exists.
- **Drop entirely** (current behavior).

Whichever the SDK picks, the goal is to surface "this type is safe to
share across threads in Swift" without forcing the consumer to read
the swiftinterface to find out.

## Affected sites

- Every Swift `Sendable`-conforming type across MusicKit (~80% of
  public types) and WeatherKit (most value types).
- Cross-cutting: every Apple framework binding and most third-party
  libraries declare Sendable broadly.

## Impact

- Consumers cannot tell whether a Swift-bound type can be passed
  across `Task.Run` boundaries without reading the swiftinterface.
- C# consumers may either:
  - Apply over-cautious locking around Sendable types (perf cost, no
    correctness benefit).
  - Apply under-cautious sharing of non-Sendable types (latent bug if
    a future Swift API breaks the assumption).
- No analyzer signal; no IDE quick-info for thread-safety.

## Severity

**Low.** Documentation gap, not a correctness bug. Listed for
completeness so the SDK maintainer can decide on a projection
strategy. Suggested approach: XML doc note (cheapest, most portable);
escalate to attribute if consumer demand grows.

## Fix gate

When fixed, every Swift type whose `swiftinterface` declares
`Sendable` conformance should have a corresponding C# signal — XML
doc, marker attribute, or interface — that lets a consumer know
without reading the Swift source.

Current state: 0% projected.
