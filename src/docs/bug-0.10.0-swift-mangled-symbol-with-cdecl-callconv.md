# Bug: PInvoke targets a Swift-mangled (`$s…`) symbol with `CallConvCdecl` instead of `CallConvSwift`

> SDK 0.10.0 generator correctness bug. Discovered 2026-05-05 during the
> WeatherKit + MusicKit cross-package consumer-experience audit (Round 5).
> See
> [`audit-weatherkit-musickit-2026-05-05.md`](../../swift-dotnet-packages/audit-weatherkit-musickit-2026-05-05.md)
> finding **O-5**.

## Summary

The generator emits a `[LibraryImport(EntryPoint = "$s8MusicKit…")]`
attribute (a Swift-mangled symbol — Swift calling convention) and
pairs it with `[UnmanagedCallConv(CallConvs = new[] {
typeof(CallConvCdecl) })]`. The two are incompatible — Swift mangled
symbols use Swift CC (register-passing convention with implicit
self / metadata / witness-table arguments), and reading them with
cdecl ABI rules yields garbage register state.

Inverse direction of **M-6**
([`bug-0.10.0-direct-callconvswift-pinvoke-for-skipped-wrapper.md`](bug-0.10.0-direct-callconvswift-pinvoke-for-skipped-wrapper.md)),
which catches CallConvSwift on a missing-wrapper symbol. Here the
wrapper *does* exist — the generator just selected the wrong symbol
when both wrapped and unwrapped forms were available.

## Environment

- SwiftBindings.Sdk 0.10.0 / Runtime 0.10.0 / Apple 26.2.1
- .NET SDK 10.0.107
- Xcode 26.3 / Swift 6.2.x / iPhoneOS26.2.sdk
- macOS 26.x, arm64

## Repro

```bash
sed -n '12183,12230p' apple-frameworks/MusicKit/obj/Debug/net10.0-ios26.2/swift-binding/MusicKit.cs
```

```csharp
// MusicKit.cs:12200  (AnyMusicProperty.PInvoke_op_eq)
[LibraryImport(
    "@rpath/MusicKitSwiftBindings.framework/MusicKitSwiftBindings",
    EntryPoint = "$s8MusicKit03AnyA8PropertyC2eeoiySbAC_ACtFZ")]
[UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
internal static partial bool PInvoke_op_eq(IntPtr lhs, IntPtr rhs);
```

The `EntryPoint` mangled name decodes as `MusicKit.AnyMusicProperty
.==(_:_:)` — a Swift `static func ==` operator. Swift mangled symbols
use Swift's own CC, which:

- passes `self` (or class metadata) implicitly via a dedicated register
  (`x20` on arm64),
- passes generic metadata + witness tables in dedicated metadata
  registers,
- uses Swift's error-handling register convention.

`CallConvCdecl` ignores all of those — and the `bool` return is read
from the cdecl-return register rather than Swift's. End result: the
PInvoke either reads garbage from registers that Swift didn't
populate, or corrupts state because cdecl callee-saved-vs-caller-saved
register conventions don't match Swift's.

The wrapper binary *does* export a cdecl trampoline:

```bash
nm -gU MusicKitSwiftBindings | grep AnyMusicProperty | grep eeoi
SBW_MusicKit_AnyMusicProperty_eeoi_<HASH>
```

The generator's emit picked the Swift mangled name when the cdecl
wrapper was the correct target. One symbol-selection bug.

## Hypothesis

The emit pipeline has a fallback chain when picking the
`EntryPoint` for an operator-equality method:

1. Try the cdecl wrapper symbol if it exists → use `CallConvCdecl`.
2. Otherwise try the Swift mangled symbol → use `CallConvSwift`.

For this site, step 1 succeeded (cdecl wrapper exists) but the
emitter wrote the *Swift mangled name* into the `EntryPoint`
attribute — the mangled-name selection in step 1 was wrong, but the
calling-convention picker didn't notice because it had already been
flipped to `CallConvCdecl` based on "cdecl wrapper existence."

The two decisions are normally tied together; this site has them
desynchronized. Likely a code path where the mangled name was
computed once, the wrapper-existence check was a separate code path,
and the result of the wrapper check fed the calling-convention
attribute but not the mangled-name override.

The fix is to keep the entry-point name and the calling convention
inseparable — pick them from the same decision point.

## Impact

Calling `lhs.Equals(rhs)` (which dispatches through this PInvoke) on
two `AnyMusicProperty` values returns garbage `bool` and may corrupt
state under specific argument shapes. The bug surfaces inconsistently —
sometimes the read garbage happens to be 0 / 1, sometimes the call
appears to succeed but `lhs == rhs` for two equal values returns
`false`, sometimes the call crashes mid-stack-walk.

Cross-cutting risk: any other `[LibraryImport(EntryPoint = "$s…")]`
across other libraries with a paired `CallConvCdecl` would have the
same shape. Worth a generator-side audit pass: every `EntryPoint`
starting with `$s…` should pair with `CallConvSwift`, not
`CallConvCdecl`.

## Severity

**High.** Equality-operator on a public type — used in every
collection-bucket lookup, every `==` consumer comparison, every
`Distinct` LINQ query. Visible-but-inconsistent failure mode.

## Fix gate

`MusicKit.cs:12200-12230` should declare an `EntryPoint` matching the
cdecl wrapper symbol (e.g. `SBW_MusicKit_AnyMusicProperty_eeoi_<HASH>`)
paired with `CallConvCdecl`. A 2-line test
(`new AnyMusicProperty(...).Equals(otherInstance)`) calling it twice
should return consistent `bool`.

Generator-wide audit: grep all generated `*.cs` for
`[LibraryImport(...EntryPoint = "$s...")]` paired with
`CallConvCdecl` should return zero matches.
