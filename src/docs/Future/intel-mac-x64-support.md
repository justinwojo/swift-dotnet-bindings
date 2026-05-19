# Full Intel Mac (x86_64) Support

> Scope-completion follow-up to the SwiftInterfaceParser universal2 fix.
> That fix made the SDK *installable and runnable* on Intel Mac developer
> hosts. This doc tracks the remaining work to make bindings the SDK
> *produces* actually work for x86_64 targets (Intel iOS simulator,
> osx-x64 deployment, Intel tvOS simulator, x86_64 Mac Catalyst).

## What works on Intel hosts today

After the parser binary became universal2, an Intel Mac developer can:

- Install `SwiftBindings.Sdk` and `SwiftBindings.Runtime` from NuGet.
- Run `dotnet new swift-binding` and `dotnet build` against any xcframework.
- Develop and debug .NET-for-iOS apps targeting **physical iPhone/iPad
  (arm64)**. The whole device path is unchanged — `xcrun`, `codesign`,
  and Apple's cross-compile-to-arm64 tooling have always worked from
  Intel hosts, and the generator's output is targeted at the device
  slice the binding ultimately runs on, not the developer machine.

## What still does not work on Intel hosts

Targets that require an x86_64 *runtime* slice:

- iOS Simulator (`iossimulator-x64`) — Intel hosts run the simulator
  natively as x86_64; Apple Silicon hosts run it as arm64.
- macOS app deployment to Intel users (`osx-x64`).
- tvOS Simulator on Intel hosts (`tvossimulator-x64`).
- Mac Catalyst on Intel hosts (`maccatalyst-x64`).

These all fail today because the generator hardcodes arm64 in several
places, and the wrapper-emit path only produces arm64 native thunks.

## The layered problem

There are three independent layers that each have to lift before
x86_64 targets work end-to-end. They're listed in dependency order —
Layer 1 alone is not useful without Layer 2.

### Layer 1 — Slice routing is hardcoded to arm64

**Sdk.targets:34-37** — the `_SwiftBindingNuGetRid` property is set by
platform string only, with arm64 baked in:

```xml
<_SwiftBindingNuGetRid Condition="'$(_SwiftBindingPlatform)' == 'macos'">osx-arm64</_SwiftBindingNuGetRid>
<_SwiftBindingNuGetRid Condition="'$(_SwiftBindingPlatform)' == 'tvos'">tvos-arm64</_SwiftBindingNuGetRid>
<_SwiftBindingNuGetRid Condition="'$(_SwiftBindingPlatform)' == 'maccatalyst'">maccatalyst-arm64</_SwiftBindingNuGetRid>
<_SwiftBindingNuGetRid Condition="'$(_SwiftBindingNuGetRid)' == ''">ios-arm64</_SwiftBindingNuGetRid>
```

This `_SwiftBindingNuGetRid` value drives `--slice-xcframework --rid …`
on the generator command line (Sdk.targets:2194) and the
`runtimes/$(_SwiftBindingNuGetRid)/native/…` layout written into the
final nupkg (Sdk.targets:2272). Whichever RID it picks is the only
slice that gets extracted from the xcframework and packaged into the
consumer's binding output.

**SliceVariant.cs:18-19** — the generator-side slice metadata also has
arm64 baked in as the default:

```csharp
/// <summary>Architecture (always "arm64" for now).</summary>
public string Architecture { get; init; } = "arm64";
```

**Good news, partial credit**: the resolver itself is already
arch-aware. `XCFrameworkResolver.cs:185-187` prefers arm64 if present
but falls back to whatever the slice's first supported arch is, and
`SwiftWrapperCompiler.cs:178-180` threads the resolved architecture
through into the slice's target triple ("Override architecture from
resolution (defense-in-depth: not all xcframeworks have arm64)"). The
plumbing exists; the routing on top always picks arm64.

**What Layer 1 needs**: derive `_SwiftBindingNuGetRid` from the
consumer's `RuntimeIdentifier` / TFM-platform combination instead of
hardcoding arm64. Then propagate the resolved architecture all the way
through the slicer command line and the nupkg `runtimes/<rid>/` layout
so a single binding output can ship both arm64 and x64 native slices,
matching .NET's normal multi-RID packaging convention.

### Layer 2 — Native thunks are arm64-only

The generator emits Swift-side assembly trampolines (`*.arm64.s`) for
calling-convention bridging cases that can't be expressed in Swift
source — register shuffling for existentials, complex generics, certain
protocol-witness paths. Each `.arm64.s` file is hand-emitted with arm64
register and instruction syntax.

**NativeThunkCompiler.cs:131-136** — only collects `.arm64.s`:

```csharp
internal static List<string> CollectAssemblyFiles(string outputDirectory)
{
    return Directory.GetFiles(outputDirectory, "*.arm64.s")
        // …
}
```

Even if Layer 1 lifted, the wrapper built for an x86_64 target would
contain arm64 assembly that the dynamic loader couldn't load into an
x86_64 process. Functions that need a thunk would crash on first call;
simple blittable APIs that bypass the thunk emitter might happen to
work.

**What Layer 2 needs**: a parallel `.x86_64.s` emit path producing
x86_64-native thunks (SysV AMD64 calling convention, different register
file, different prologue/epilogue conventions). Both arches' object
files have to ship in the wrapper binary so the resolved slice picks
up the matching arch.

This is the largest piece. Each existing thunk-emitting site has to
produce two assembly variants, and the emitter has to know enough about
SysV AMD64 to express the same register shuffling the arm64 thunks do.
A clean separation would split per-arch instruction emission behind an
abstraction, with the arm64 path moving to the new abstraction first
as a no-op refactor, then x86_64 added.

### Layer 3 — Apple framework `.swiftinterface` probing is arm64-only

**Sdk.targets:243-251** — the Apple-framework mode picks system
`.swiftinterface` files by hardcoded arm64/arm64e triple per platform:

```xml
<_SwiftInterfaceSlice Condition="'$(_SwiftBindingPlatform)' == 'ios' AND '$(SwiftPlatformTarget)' == 'simulator'">arm64-apple-ios-simulator</_SwiftInterfaceSlice>
<_SwiftInterfaceSlice Condition="'$(_SwiftBindingPlatform)' == 'ios' AND '$(SwiftPlatformTarget)' != 'simulator'">arm64e-apple-ios</_SwiftInterfaceSlice>
<!-- …same shape for tvos, macos, maccatalyst -->
```

This drives where the Apple-framework binding generator looks for the
system framework's text-format swift interface inside the macOS SDK.
For *parsing* the interface, the arch of the chosen `.swiftinterface`
doesn't change the bindings produced (Swift's textual interface is
arch-portable). But for actually building and running the wrapper
xcframework for an x86_64 target, the consumed framework binary has to
be the matching x86_64 slice.

**What Layer 3 needs**: probe the x86_64 simulator slice (e.g.
`x86_64-apple-ios-simulator`) when targeting Intel iOS sim, and the
x86_64 macOS slice when targeting osx-x64. The probe logic itself is
mechanical; the hard part is verifying the system framework
distribution still contains an x86_64 slice for the framework in
question (some Apple frameworks have already dropped Intel sim slices
in recent SDKs).

### Bonus — `regenerate.sh` dev script

**`src/Swift.Bindings.Sdk/tools/apple-types-manifest/regenerate.sh:26-29`**
hardcodes arm64 target triples for the `swift-api-digester` invocation
that regenerates the Apple-types manifest. This is a dev-only script
(not shipped in any nupkg), so an Intel-host contributor who needs to
regenerate the manifest would currently have to edit the triples
manually. Easy add-on if and when the rest of Intel-host work lands.

## Hard constraint: input xcframework has to contain x86_64

Even with all three layers fixed, the SDK can only produce an x86_64
binding for a library whose xcframework *contains* an x86_64 slice for
the relevant platform. Apple's own first-party frameworks still
generally ship Intel simulator slices, but many community xcframeworks
distributed via SPM-derived archives or `xcframework`-from-SPM tooling
have already gone arm64-only (especially anything built after 2024).

This means Intel x64 support is fundamentally best-effort dependent on
upstream library distribution decisions. The SDK should fail loudly
and clearly when asked to produce an x86_64 binding for an xcframework
that lacks the slice, not silently fall back to arm64.

## Validation plan

When picked up:

- BindingTests gains an x86_64-sim runtime gate. Two implementation
  paths:
  1. Add an Intel CI runner. Apple no longer offers Intel runners on
     GitHub-hosted macOS, so this means a self-hosted Intel Mac on the
     supported macOS line (currently up to macOS 26 Tahoe on the small
     set of Intel models that still ship). Has a sunset horizon.
  2. Run the x86_64 simulator slice under Rosetta on the arm64 runner.
     This proves the binding *loads and executes*, but doesn't catch
     register-layout bugs the way a native x86_64 host would.
- `nuke validate` would need an `iossimulator-x64` cell added once
  Layer 1+2 generate the needed output.

## Why deferred

The parser-binary fix unblocks the immediate complaint ("I can't even
install this SDK"). After it ships, Intel-host iPhone-on-device
development works the way it always has — most of the actual iOS
development workflow that Intel Mac owners care about. The remaining
layers benefit a shrinking population:

- macOS 26 (Tahoe) is the last macOS release that supports any Intel
  hardware, and only a narrow set of 2019–2020 Intel models (Mac Pro
  2019, iMac 2020 27", MacBook Pro 16" 2019, MacBook Pro 13" 2020
  4-TB3). Earlier Intel Macs are already on prior macOS versions that
  can't run Xcode 26.
- macOS 27 is widely expected to drop Intel entirely.
- Apple itself is winding down x86_64 simulator slices in its own
  framework distributions, so the supply side is shrinking too.

The cost-to-benefit balance argues for *not* investing the substantial
generator/thunk-emitter rewrite this requires unless real consumer
demand surfaces. If demand does surface (e.g. multiple GitHub issues
beyond the original #39 reporter), revisit; otherwise let this doc sit
until the Intel install base is small enough that the question
naturally closes.

## Open questions

- Is there a cleaner way to introduce x86_64 thunk emission than a
  per-instruction abstraction? An alternative would be emitting C
  source that lowers to either arch via the system C compiler — slower
  at runtime (function-call overhead vs. inlined assembly) but
  drastically simpler to maintain. Worth a spike before committing to
  the assembly path.
- Should we ship the `_SwiftBindingNuGetRid` derivation as an explicit
  consumer opt-in (`<SwiftBindingHostArch>`) rather than auto-deriving
  from `RuntimeIdentifier`? Auto-derivation is friendlier but riskier
  if the consumer's RID and the target slice need to disagree (e.g.
  arm64 host building osx-x64 deployment binary).
