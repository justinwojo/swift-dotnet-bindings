# Bug: `IEquatable<T>` emitted unconditionally on Swift generics whose Equatable conformance is conditional

> SDK 0.10.0 generator correctness bug. Discovered 2026-05-05 during the
> WeatherKit + MusicKit cross-package consumer-experience audit (Round 5).
> See
> [`audit-weatherkit-musickit-2026-05-05.md`](../../swift-dotnet-packages/audit-weatherkit-musickit-2026-05-05.md)
> finding **O-6**.

## Summary

Swift extends some generic types (e.g. `MusicItemCollection<MusicItemType>`)
with `Equatable` / `Hashable` conformances **only when the type
parameter itself satisfies the conformance**:

```swift
extension MusicItemCollection : Equatable
    where MusicItemType : Equatable
extension MusicItemCollection : Hashable
    where MusicItemType : Hashable
```

The C# binding emits `IEquatable<MusicItemCollection<TMusicItemType>>`,
the operator overloads, and the `GetHashCode` override **without** any
constraint on `TMusicItemType`. Consumer code that compiles today
against `MusicItemCollection<NonEquatableT>.Equals(...)` dispatches at
runtime to a Swift specialization that may not exist.

Distinct from Family B (Equatable not lowered) — B is *missing*
emission. This is *over-broad* emission that the Swift type system
would have refused.

## Environment

- SwiftBindings.Sdk 0.10.0 / Runtime 0.10.0 / Apple 26.2.1
- .NET SDK 10.0.107
- Xcode 26.3 / Swift 6.2.x / iPhoneOS26.2.sdk
- macOS 26.x, arm64

## Repro

```bash
sed -n '11050,11060p' apple-frameworks/MusicKit/obj/Debug/net10.0-ios26.2/swift-binding/MusicKit.cs
sed -n '11288,11320p' apple-frameworks/MusicKit/obj/Debug/net10.0-ios26.2/swift-binding/MusicKit.cs
```

```csharp
// MusicKit.cs:11057
public partial class MusicItemCollection<TMusicItemType>
    : ISwiftObject, ISwiftStruct, IDisposable,
      IEquatable<MusicItemCollection<TMusicItemType>>     // [1] unconditional
    where TMusicItemType : ISwiftObject, IMusicItem
{ ... }

// MusicKit.cs:11291
public bool Equals(MusicItemCollection<TMusicItemType>? other)
    => other != null && PInvoke_eq(this.Payload.DangerousGetHandle(),
                                   other.Payload.DangerousGetHandle());
public override int GetHashCode() => SwiftHashable.GetHashCode(this);
public static bool operator ==(MusicItemCollection<TMusicItemType>? l,
                               MusicItemCollection<TMusicItemType>? r) => …;
public static bool operator !=(...) => …;
```

C# generic constraint at `:11057` is `where TMusicItemType :
ISwiftObject, IMusicItem`. Swift's gating constraint is `where
MusicItemType : Equatable`. The set of valid C# instantiations is a
strict superset of the set of valid Swift instantiations.

The PInvoke `eq` resolves to a Swift specialization
keyed on the `TMusicItemType`'s `Equatable` witness table. For a type
that doesn't conform, the witness table doesn't exist — Swift's
`_specialize`/`metadata-fetch` pipeline either returns a stub that
traps or returns nil and the call dereferences a null witness.

## Affected sites

Same shape across MusicKit's generic-collection family:

- `MusicKit.cs:11057, 11291-11314` — `MusicItemCollection<T>`
- `MusicKit.cs` — `MusicLibraryResponse<T>`
- `MusicKit.cs` — `MusicLibrarySection<S, T>`
- `MusicKit.cs` — `MusicLibrarySectionedResponse<S, T>`

Cross-cutting risk: any Swift `extension Foo : Equatable where T :
Equatable` declaration is at risk of the same defect.

## Hypothesis

The emit pipeline's "extension lowering" picks up the
`extension … : Equatable` declaration and adds the C# `IEquatable<T>`
interface to the type, but loses the `where T : Equatable` constraint
during the conversion. C# *could* express that constraint as
`where TMusicItemType : ISwiftObject, IMusicItem,
IEquatable<TMusicItemType>` — but only if the generator threads the
Swift conformance constraint through the conversion.

The cleanest fix: when lowering a conditional Swift extension, append
the conformance constraint to the C# generic parameter list. This is
the same shape as *Default Interface Members* would solve in C# 8+,
but at the type-parameter level.

If the SDK chooses not to express the constraint, the conservative
alternative is to drop the `IEquatable<T>` conformance entirely on
generic types whose Swift conformance is conditional — paired with
documentation that consumers must use `Equals(object?)` and accept
the per-call boxing.

## Impact

A consumer can write:

```csharp
var col = new MusicItemCollection<NonEquatableMusicItem>(...);
if (col.Equals(otherCol)) { ... }      // ← clean compile
```

…and crash inside the Swift runtime at the `eq` dispatch. The crash
manifests as either:

- Null witness table dereference (clean SIGSEGV)
- Stack-trap from Swift's `_witness_unavailable` runtime helper

There's no analyzer / compile-time signal of the mismatch. Consumers
discover this only on first call.

In practice, MusicKit's `MusicItem`-conforming types do all conform
to `Hashable`/`Equatable`, so this defect doesn't immediately fire on
typical consumer workloads. But it's a latent footgun: any consumer
that introduces a custom `MusicItem`-conforming type that *isn't*
also `IEquatable<T>`-implementing falls into the trap.

## Severity

**High.** Latent footgun on a primary collection type. Should be
fixed in the same SDK pass that addresses Family B.

## Fix gate

`MusicKit.cs:11057` should declare `where TMusicItemType :
ISwiftObject, IMusicItem, IEquatable<TMusicItemType>` (and parallel
constraint for `IHashable<>` if the SDK adds that), or drop the
`IEquatable<MusicItemCollection<...>>` conformance declaration
entirely.

Generator audit gate: every `IEquatable<T>` / `GetHashCode` override
declared on a generic type should have its corresponding Swift
extension's `where ... : Equatable` constraint mirrored to the C#
generic parameter list.
