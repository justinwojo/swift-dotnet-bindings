# Gap: `DuplicateSignature` skip drops both overloads silently

> SDK 0.10.0 generator feature gap. Discovered 2026-05-05 during a
> consumer-experience audit of
> [SwiftBindings.Nuke](https://github.com/justinwojo/swift-dotnet-packages)
> 13.0.5; same shape exists in other libraries.

## Summary

When two Swift members lower to the same C# signature (after type
projection — e.g. `Foundation.URL` → `Foundation.NSUrl`,
`[Element]` → `Swift.SwiftArray<Element>`), the generator declines to
emit *both* and tags both as
`Reason: "DuplicateSignature"` in `binding-report.json`. The
`RecommendedWorkaround` it suggests — *"Rename one member via a Swift
extension to disambiguate"* — requires a code change in the **upstream
Swift library**, which the SwiftBindings consumer doesn't own. So in
practice the two methods are simply unavailable from C#.

The gap is twofold:

1. **The generator drops both overloads** instead of emitting one and
   skipping the other. There is no consumer-side mechanism to choose
   which one survives.
2. **The "rename via extension" workaround is non-actionable** for any
   consumer who isn't also the upstream library author. SwiftBindings is
   bound to vendor-shipped `swiftinterface` files; there's no place for
   a consumer-authored extension.

## Environment

- SwiftBindings.Sdk 0.10.0 / Runtime 0.10.0 / Apple 26.2.2
- .NET SDK 10.0.107, workload 10.0.107
- Xcode 26.3 / Swift 6.2.x
- macOS 26.x, arm64

## Concrete cases (Nuke 13.0.5)

```bash
jq '.SkippedMembers[] | select(.Reason == "DuplicateSignature")' \
   libraries/Nuke/obj/Debug/net10.0-ios/swift-binding/binding-report.json
```

```text
ImagePrefetcher.startPrefetching(with: [ImageRequest])  → SwiftArray collision with URL overload
ImagePrefetcher.stopPrefetching(with:  [ImageRequest])  → same
ImageProcessing.process(...)                            → protocol method signature dup
```

For `ImagePrefetcher.startPrefetching`, only the
`_startPrefetching(...)` private mangled emission survives — there's no
public `StartPrefetching(IEnumerable<ImageRequest>)` that consumers can
call. Net consumer impact: prefetching real `ImageRequest`s
(with headers, processors, priority, cache options, userInfo) is
effectively unsupported from C#; consumers can only prefetch by URL,
losing all per-request state.

## Why "use a Swift extension" doesn't apply here

Swift's `extension` mechanism lets the *original* author of a type rename
or replace one of its overloads, e.g.

```swift
extension Nuke.ImagePrefetcher {
  func startPrefetching(requests: [ImageRequest]) { … }
}
```

But SwiftBindings binds to a vendor-shipped `xcframework` /
`.swiftinterface`. The consumer cannot inject Swift source into the
binding pipeline; they can only consume what the vendor shipped. So the
"workaround" message in the binding report doesn't describe an action
any swift-dotnet-packages user can take.

(In principle the SwiftBindings build could compile a small Swift
"shim" module that adds disambiguating extensions on the consumer's
behalf, but that's a sizeable feature and not what the existing message
implies.)

## Hypothesis

The C# overload-resolution mapper computes the signature key after type
projection (so `data(for url: URL)` and `data(for url: NSURL)` collide
because both project to `Foundation.NSUrl`). When two source members
collide, the emitter currently treats this as "ambiguous, skip both."

Better strategy candidates, in increasing complexity:

1. **Rename collision** — emit both, with the second one getting a
   numeric or type-derived suffix (e.g. `StartPrefetching` and
   `StartPrefetchingFromUrls`). Consumers gain access; the suffix
   strategy needs to be predictable (always alphabetical-by-source-line?
   always shortest-name-first?).
2. **Choose-one heuristic** — emit the overload with the richer parameter
   types (`ImageRequest > URL`), report the other as skipped. Loses
   functionality for the URL-based overload but at least keeps the more
   capable one.
3. **Vendor-rename annotation** — accept a `library.json`-side "rename
   map" so the consumer can pick disambiguators. Most labor; most
   control.

For most cases, (1) is probably the right default: emit both with a
deterministic suffix on the second one. (2) is a reasonable fallback for
cases where the two signatures differ only in optionality / nullability
and one is strictly more general.

## Adjacent issue — protocol-method duplicates

The `ImageProcessing.process(...)` collision is on a *protocol* method
rather than a concrete type method. Same root cause but the C# emission
target is different (interface member vs. class method). Whatever
strategy lands for class methods needs to apply uniformly to interface
members.

## Impact

- **Library scope.** Anywhere two Swift overloads collide after type
  projection. Usually surfaces when:
  - Foundation types project to NSObject equivalents that erase a
    distinction (`URL`/`NSURL`).
  - Swift collection types (`[T]`, `Set<T>`) all project to
    `SwiftArray<T>` regardless of source-Swift type.
  - Generic constraints differ but the erased C# signature doesn't carry
    them.
- **Functional gap.** Consumer can't reach one or both overloads from C#.
  In Nuke's case, the higher-fidelity `ImageRequest` form of
  `startPrefetching` is unreachable; consumers fall back to the
  URL-based overload and lose per-request configuration.

## Round 4 — Lottie audit (2026-05-05)

The Lottie audit confirmed one more recurrence: `LottiePlaybackMode.paused`
collides on signature with another nullary playback-mode factory and
both are dropped.

```text
binding-report.json:
  Lottie.LottiePlaybackMode::paused [DuplicateSignature]
```

Consumer-visible result: `LottiePlaybackMode.Paused()` is unreachable
from C#; consumers must construct the paused playback mode via the
generic `.Mode(...)` API path or fall back to `LottiePlaybackMode
.Pause()`. The latter, however, is itself flagged with the spurious
deprecated message that needs the per-overload `[Obsolete]` fix
described in
[bug-0.10.0-spurious-obsolete-on-recommended-overload.md](./bug-0.10.0-spurious-obsolete-on-recommended-overload.md)
Round 4 / F-3.

The pair of these is consumer-toxic: the paused playback mode is
reachable only via a method the C# emitter advertises as deprecated, and
the actually-non-deprecated `paused` accessor is dropped to
`DuplicateSignature`.

Cross-reference in
[SDK-0.10.0-BLOCKERS.md](../../swift-dotnet-packages/SDK-0.10.0-BLOCKERS.md)
under Round 4 / M-13-adjacent.

## Workaround

Consumer side: depending on the case, sometimes the "lost" functionality
is reachable via a different API path (e.g. `pipeline.LoadImage(request,
…)` instead of prefetching with a request). Otherwise, none.

Proper fix in
[swift-dotnet-bindings](https://github.com/justinwojo/swift-dotnet-bindings):
deterministic disambiguation strategy, ideally (1) above as the default
with optional consumer-controlled override.

## Severity

**Feature gap — Medium.** No correctness impact, no runtime risk. But
silently drops APIs from the C# surface, with a `binding-report.json`
"workaround" message that consumers cannot act on. Tracked as P5 in
[SDK-0.10.0-BLOCKERS.md](../../swift-dotnet-packages/SDK-0.10.0-BLOCKERS.md).

## Resolution

The non-constructor case shipped earlier in 0.10.0:
`ModuleHandler.cs` now disambiguates colliding non-constructor methods
(class methods, static methods, free functions) by emitting all
overloads with deterministic numeric suffixes (`Process`, `Process2`,
…). Both `MethodWrapperEmitter` and `DefaultParameterOverloadEmitter`
participate in a shared dedup key so the surface stays consistent across
trim overloads, generic static-dispatch extensions, and constrained-
extension methods. Coverage: a regression suite landed under
`Marshalling/Collisions/ClosureOverloadCollisionTests.{cs,swift}` plus
`MarshalerTests/CollisionSuffixTests.cs`.

Verified at consumer scope: this fix unblocked Stripe's
`handleNextAction` and `confirmSetupIntent` (the documented Nuke /
Lottie / Stripe collision shapes), and added rescued methods across
fifteen-plus validation libraries.

### Constructor case — documented C# language limitation

Constructors **cannot** be disambiguated by suffix because constructors
in C# have no name distinct from the type itself; you can only
distinguish them by parameter list, and the projected (post-type-
erasure) parameter lists are by definition identical when the conflict
fires. Renaming one constructor to a static factory method would change
the public API in a way that breaks symmetric round-tripping with the
other overload (consumers would have to know which constructor maps to
which factory). There is no mechanical disambiguation that preserves
constructor semantics under signature equivalence.

The current behavior — drop both constructors with a `DuplicateSignature`
skip record in `binding-report.json` — is therefore retained as a
documented language-limitation case. Consumer-side mitigation:

- For value-type constructors, expose a Swift-side `static func make…`
  factory upstream that takes the disambiguating parameter shape, or
  hand-roll a Swift `@_cdecl` shim and PInvoke it from C#.
- For class constructors with collision-on-projection, the Swift author
  can rename one constructor to a factory method (not actionable for
  vendor-shipped frameworks; same caveat as the original "rename via
  extension" workaround).

This case will not be fixed in the SwiftBindings generator without a
broader rename-strategy mechanism (see hypothesis (3) in this doc — a
`library.json` consumer-controlled rename map). That mechanism is out
of scope for 0.10.0 and tracked separately on the roadmap.

**Status: closed for 0.10.0** — non-ctor case shipped; ctor case
documented as a C# language limitation.
