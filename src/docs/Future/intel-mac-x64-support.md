# Full Intel Mac (x86_64) Support

> Scope-completion follow-up to the SwiftInterfaceParser universal2 fix.
> That fix made the SDK *installable and runnable* on Intel Mac developer
> hosts. This doc tracks the remaining work to make bindings the SDK
> *produces* actually work for x86_64 targets (Intel iOS simulator,
> osx-x64 deployment, Intel tvOS simulator, x86_64 Mac Catalyst).
>
> **Research snapshot (mid-2026, Xcode 26.3)**: Empirical checks on
> current Apple Silicon + latest Xcode SDKs are incorporated below.
> See "Layer 3 status" and "Development environment reality".

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

**Implementation status**: As of the current tree, Layers 1–3 remain
completely unaddressed. The arm64 hardcoding described below is still
present in `PlatformInfoFactory`, `SliceVariant`, `NativeThunkCompiler`,
`ThunkAssemblyEmitter`, `ModuleEmitter`, `XCFrameworkSlicer`, `Sdk.targets`,
and related emission/compilation paths.

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

**Status as of Xcode 26.3 (macOS 26.2 SDK)**: Apple's own frameworks
still ship x86_64 simulator slices. The text-based stub (`.tbd`) for
`Foundation.framework` (and its re-exports) explicitly lists both:

```
targets: [ x86_64-ios-simulator, arm64-ios-simulator ]
```

Cross-compilation from Apple Silicon also works cleanly:

```bash
xcrun --sdk iphonesimulator clang -target x86_64-apple-ios26.0-simulator ...
xcrun --sdk iphonesimulator swiftc   -target x86_64-apple-ios26.0-simulator ...
```

Equivalent x86_64 targets succeed for `macosx`, `appletvsimulator`, and
`macabi`. The supply-side risk for *Apple* frameworks is currently lower
than the original concern, but third-party xcframeworks (especially
SPM-derived ones) remain the dominant practical constraint.

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
  2. Run the x86_64 slice under Rosetta on the arm64 runner. Contrary to
     the intuition that this is a low-fidelity gate, Rosetta 2 translates
     genuine x86_64 machine code and faithfully reproduces the SysV AMD64
     ABI — argument-register assignment, struct INTEGER/SSE/MEMORY
     classification, register-vs-indirect passing. The exact bug class
     this generator fights (calling-convention / register-layout /
     `@frozen`-vs-resilient struct marshalling) therefore *does* reproduce
     under Rosetta. What Rosetta does not faithfully reproduce is a short
     list irrelevant to Swift interop: x87 80-bit `long double` precision,
     AVX-512, and native timing/performance. For ABI-correctness purposes,
     Rosetta is close to a native x86_64 host — the "I have no Intel Mac"
     constraint is much weaker than it first appears.

     **Caveat — the validation mechanism is itself sunsetting.** Apple
     announced at WWDC25 that Rosetta 2 is retained through macOS 26/27 in
     reduced scope and then removed. So the gate decays on roughly the
     same timeline as the Intel install base it would validate; do not
     build a *permanent* CI dependency on it.
- **Cheapest high-fidelity ABI gate: don't start with the iOS simulator.**
  SysV AMD64 is SysV AMD64 regardless of Apple platform, so an `osx-x64`
  BindingTests-equivalent harness run as a plain `arch -x86_64 ./runner`
  command-line process under Rosetta validates the entire Layer 2 thunk +
  P/Invoke marshalling story with no simulator involved. Decouple "is the
  x86_64 ABI correct" (cheap, `osx-x64` + Rosetta CLI) from "does the full
  `iossimulator-x64` packaging path work end to end" (more involved, and
  the lowest-value target). Empirically confirmed on the current tree
  (Apple Silicon, Xcode 26.3 / SDK 26.2): `swiftc -target
  x86_64-apple-ios26.0-simulator` and `-target x86_64-apple-macos` both
  cross-compile cleanly, and Rosetta executes x86_64 host binaries.
- `nuke validate` would need an `iossimulator-x64` cell added once
  Layer 1+2 generate the needed output.

## Runtime transition: CoreCLR replaces Mono (.NET 11, Nov 2026)

In .NET 11 (GA November 2026), .NET MAUI / mobile workloads move to
**CoreCLR by default** on iOS, Mac Catalyst, tvOS, and Android, retiring
Mono as the default mobile runtime (opt-out via `UseMonoRuntime` through
.NET 11 servicing). This reshapes the Intel-x64 question in three ways,
and is arguably more decisive than any of the three generator layers
above:

1. **The simulator inner loop changes runtime regardless of x64.** Our
   everyday gate today is Mono JIT on the simulator (`nuke binding-tests
   --sim`) and NativeAOT on device. When the simulator path becomes
   CoreCLR JIT, the *bug profile* shifts (different JIT, different
   marshalling stubs). We absorb this transition anyway — but note it
   lands on precisely the simulator surface the x64 work targets, so x64
   work should not start until our CoreCLR-on-simulator baseline is green.

2. **The decisive dependency is now Microsoft's, not ours.** Whether an
   Intel iOS-simulator binding can run at all depends on (a) Microsoft
   shipping a CoreCLR **`iossimulator-x64` runtime pack**, and (b)
   `CallConvSwift` being implemented and tested on **Apple x86_64**.
   - On (a): given Apple's own Intel sunset, Microsoft may simply not
     ship CoreCLR runtime packs for the Apple x86_64 simulator RIDs. If
     they don't, Layers 1–3 are moot for the simulator — there is no
     runtime to host the binding. This must be checked first.
   - On (b): the .NET Swift-interop design (`dotnet/runtime#93631`,
     `#64215`) defines `CallConvSwift` register usage for *both* x64 and
     arm64 (e.g. RCX/R8 for extra integer returns, XMM2/XMM3 for FP), so
     x64 is in scope by design — **but** development and testing have
     centered on arm64 (the only Apple architecture that ships on device
     and on Apple Silicon hosts). x86_64 Apple-platform maturity is
     unverified and is a runtime-layer gap we cannot fix in the generator.

3. **`osx-x64` is the least affected, most stable target.** CoreCLR has
   always been a first-class x86_64 desktop/server runtime; `osx-x64` is
   its native habitat and is well-exercised. This reinforces the
   recommendation below: if any Intel slice is ever worth building, it is
   `osx-x64` desktop deployment — which is also the cheapest to validate
   (Rosetta CLI) and the only target with a tail extending past Apple's
   developer-hardware sunset (Intel Mac *end-users* persist a few years
   beyond Intel Mac *developers*).

Net effect: CoreCLR does not make the generator work easier or harder,
but it moves the real go/no-go decision **upstream of this repo**. Before
investing in Layers 1–2 for the simulator, confirm Microsoft ships a
CoreCLR `iossimulator-x64` runtime pack with working `CallConvSwift`. If
that pack never materializes, the simulator path is permanently blocked
regardless of our work, and only `osx-x64` remains viable.

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

**Development environment reality (2026)**: An Apple Silicon Mac running
current Xcode + Rosetta 2 is currently a *better* environment for
authoring and partially validating the x86_64 paths against the latest
SDKs and Swift ABI than a 2018–2020 Intel Mac stuck on its final
supported macOS/Xcode. All generator changes, dual-architecture thunk
emission, cross-compilation of wrappers, and Rosetta execution of the
resulting x86_64 thunks and test binaries can be performed without
native Intel hardware. The only thing that cannot be done is a true
native-x86_64 execution fidelity gate for exotic silicon-specific bugs.

The cost-to-benefit balance argues for *not* investing the substantial
generator/thunk-emitter rewrite this requires unless real consumer
demand surfaces. If demand does surface (e.g. multiple GitHub issues
beyond the original #39 reporter), revisit; otherwise let this doc sit
until the Intel install base is small enough that the question
naturally closes.

## If picked up: recommended scope and sizing

**Recommended scope — `osx-x64` desktop only.** Do not attempt the full
sim + Catalyst + tvOS + desktop matrix on the first pass. Desktop alone
is the clean target for three reasons:

- **It rides mainline CoreCLR *today* — no .NET 11 preview dependency.**
  The Mono→CoreCLR transition is a *mobile* (iOS/tvOS/Catalyst) story;
  `osx-x64` / `osx-arm64` have been GA on stable CoreCLR for releases. So
  the simulator path is the one entangled with the .NET 11 preview and the
  "does Microsoft ship an `iossimulator-x64` runtime pack" gamble — desktop
  is not. This is distinct from (and more favorable than) the CoreCLR
  section above, which is about the *simulator* risk.
- **Layer 1 shrinks to a single branch** (`osx-arm64` → `osx-x64`): no
  simulator/device split on macOS, no Catalyst `macabi` triple.
- **Layer 3 drops out entirely for third-party xcframeworks** (it is the
  Apple system-framework `.swiftinterface` probing path). It only returns
  if you also want CryptoKit-style Apple-framework bindings on desktop.

What does *not* shrink: **Layer 2 (the x86_64 thunk emitter) is identical
work at any scope**, because SysV AMD64 is the same convention across all
Apple x86_64 targets. L2 is ~80% of the cost regardless of scope.

**MVP fallback — Layer 1 only, thunk-free bindings.** Ship the RID routing
fix and gate the generator to **fail loudly when a thunk is required**.
Plain functions, value types, and basic classes (which bypass the thunk
emitter) would then work on `osx-x64`; existentials, generics, and
protocol-witness paths would not. Leaky and partial — only worth it if a
specific consumer arrives with a specific simple library — but it is a
~1–2 session change instead of the full L2 rewrite.

**Sizing (Claude-session terms) — bimodal, gated on one spike:**

- Layer 1 (`osx-x64`) + `osx-x64` BindingTests cell: ~1 session, mechanical.
- **Spike first** (see Open Questions): one session to write a
  representative thunk (existential register-shuffle, a generic, a
  protocol-witness case) as C, confirm it lowers correctly for both arm64
  and x86_64, and run it under Rosetta. This collapses almost all the L2
  uncertainty *and* smoke-tests the `CallConvSwift`-on-x86_64 runtime
  maturity for one session of cost — so it must precede any commitment.
- L2 then forks on the spike result:
  - *C-shim covers the thunks* → ~3–5 more sessions (move every
    thunk-emitting site onto the C mechanism, arm64-first as a no-op
    refactor, plus per-category BindingTests). **Full desktop ≈ 5–7
    sessions.**
  - *C cannot express the residue* (register-passthrough cases) → a
    hand-written SysV AMD64 backend parallel to the arm64 one: **10+
    sessions** of genuinely hard ABI work.
- **Wildcard, unfixable here:** if CoreCLR's x86_64 `CallConvSwift` leg has
  bugs, they surface as crashes that *look* like ours but are Microsoft's.
  The spike finds this cheaply (Rosetta CLI process) — if the spike's
  thunks crash in ways that trace to the runtime, that is the signal to
  stop before sinking sessions.

**De-risking sequence before committing:** (1) an Explore pass to
enumerate every thunk-emitting site (turns the L2 estimate from "3–5 or
10+" into a real number, and avoids a session cascade as sites are
discovered piecemeal); (2) the C-shim spike; (3) only then decide.

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
