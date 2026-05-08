# Gap: `[UnsupportedSwiftType]` and "Unsupported" comments decorate members that ARE bound and work

> SDK 0.10.0 generator ergonomics gap. Discovered 2026-05-05 during a
> consumer-experience audit of
> [SwiftBindings.Lottie](https://github.com/justinwojo/swift-dotnet-packages)
> (Lottie 4.x).
>
> **Status: RESOLVED — Modes 1, 2, and 3 all shipped.**
>
> - **Mode 1** (`[UnsupportedSwiftType("Existential type fallback", ...)]`
>   on existential-projected members that DO work): RESOLVED. Existential
>   fallback is gated on marker / ObjC-bridged / PAT / Self-requirement
>   so plain protocol existentials no longer carry the misleading
>   attribute.
> - **Mode 2** (constructor "Unsupported:" comment placed above
>   emitted-and-working ctor): RESOLVED. Comment removed.
> - **Mode 3** (SB0001 `[Obsolete]` over-broadcast — diagnostic stamped
>   on members whose body actually calls a real Swift symbol via
>   `CallConvSwift`): RESOLVED. SB0001 is now narrowed by a
>   runtime-safety classifier so members whose body dispatches through
>   a real `CallConvSwift` PInvoke no longer carry the diagnostic;
>   the attribute fires only on shapes that genuinely lack a safe
>   call-shape (no @_cdecl wrapper AND the direct Swift PInvoke would
>   not be ABI-correct).

## Summary

Several emitted Lottie members carry decorations that imply the binding is
non-functional, when in fact the underlying call shape is correct and the
member works:

1. `[UnsupportedSwiftType("Existential type fallback", "any
   Lottie.DotLottieCacheProvider")]` on a `DotLottieFile.NamedAsync`
   overload that *does* dispatch through the existential proxy correctly.
2. `[UnsupportedSwiftType(...)]` on `AnimatedControl.SetValueProvider`,
   which works through the `IAnyValueProvider` proxy.
3. `// Unsupported: method 'init' — C# signature collides with another
   member` directly above an emitted-and-working `AnimationKeypath
   (IEnumerable<string> keys)` constructor.
4. `[Obsolete("No @_cdecl wrapper or native thunk available. P/Invoke
   calling convention may not match Swift ABI.", DiagnosticId = "SB0001")]`
   on `AnimatedControl.SetLayer(string, UIControlState)`, whose body
   actually calls a real Swift symbol via `CallConvSwift`.

The combined effect is build-noise — and worse, real `[Obsolete]`
deprecations get ignored when consumers learn to filter SB0001 out of
their warning streams.

## Environment

- SwiftBindings.Sdk 0.10.0 / Runtime 0.10.0 / Apple 26.2.2
- .NET SDK 10.0.107, workload 10.0.107
- Xcode 26.3 / Swift 6.2.x
- macOS 26.x, arm64
- Source library: lottie-spm 4.x

## Repro

```bash
sed -n '13820,13840p' libraries/Lottie/obj/Debug/net10.0-ios/swift-binding/Lottie.cs
sed -n '11140,11180p' libraries/Lottie/obj/Debug/net10.0-ios/swift-binding/Lottie.cs
sed -n '16320,16345p' libraries/Lottie/obj/Debug/net10.0-ios/swift-binding/Lottie.cs
```

Site 1 (Lottie.cs:13827):

```csharp
[UnsupportedSwiftType("Existential type fallback", "any Lottie.DotLottieCacheProvider")]
public Task<...> NamedAsync(string name, NSBundle bundle, ...,
    IDotLottieCacheProvider? dotLottieCache, CancellationToken ct = default)
{ ... } // body works correctly via existential proxy
```

Site 2 (Lottie.cs:11176):

```csharp
[UnsupportedSwiftType(...)]
public void SetValueProvider(IAnyValueProvider provider, AnimationKeypath keypath)
{ ... } // works correctly via existential proxy
```

Site 3 (Lottie.cs:16329):

```csharp
// Unsupported: method 'init' — C# signature collides with another member
public AnimationKeypath(IEnumerable<string> keys) { ... }   // works fine
```

Site 4 (Lottie.cs:11151):

```csharp
[Obsolete("No @_cdecl wrapper or native thunk available. P/Invoke calling " +
          "convention may not match Swift ABI.", DiagnosticId = "SB0001")]
public void SetLayer(string layerName, UIKit.UIControlState state)
{
    // actually calls a real Swift symbol with [UnmanagedCallConv(CallConvs =
    // new[] { typeof(CallConvSwift) })] — works correctly
    PInvoke_setLayer_…(...);
}
```

## Hypothesis

Three failure modes feed into the same symptom:

1. **`[UnsupportedSwiftType]` is over-emitted.** The attribute is
   applied to any member whose signature contains an existential type,
   regardless of whether the existential proxy was successfully emitted.
   Likely fix: only emit when the proxy itself is missing or throws
   `NotSupportedException`.
2. **"Unsupported" comments are emitted as residue from skip
   resolution.** The `binding-report.json` skip log gets pasted into the
   generated source as a comment block adjacent to the emitted-anyway
   member. Likely fix: only emit the comment when the member is *actually
   skipped*, not when it ships under a different signature.
3. **`SB0001` is over-broadcast.** Emitted on any wrapper-skipped member
   even if the direct-CallConvSwift PInvoke is known-safe. Combined with
   `bug-0.10.0-direct-callconvswift-pinvoke-for-skipped-wrapper.md`
   (Round 4 / M-6), it's not always over-broadcast — sometimes it really
   IS unsafe. The diagnostic should distinguish.

## Impact

- **Build noise.** Consumers learn to suppress SB0001 / skim
  `[UnsupportedSwiftType]` warnings — and miss the real ones.
- **Documentation drift.** The "Unsupported: method 'init'" comment
  directly above a working constructor leads developers reading source
  to assume that constructor is broken.

## Workaround

Consumer side: ignore SB0001 warnings on members whose body contains a
real `PInvoke_*` call. Verify by inspection, not by attribute.

## Severity

**Ergonomic — Low.** Build noise; obscures real obsoletes.

Cross-reference in
[SDK-0.10.0-BLOCKERS.md](../../swift-dotnet-packages/SDK-0.10.0-BLOCKERS.md)
under Round 4 / M-11.
