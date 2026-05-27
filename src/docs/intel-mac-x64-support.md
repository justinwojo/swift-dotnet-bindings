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
>
> **Spike executed 2026-05-26 (see "Empirical spike results" below).** The
> two biggest unknowns in this doc — "does CoreCLR x86_64 implement
> `CallConvSwift`?" and "is the x86_64 thunk emitter a hand-written-backend
> nightmare?" — were both retired by a working osx-x64 + Rosetta spike.
> Driven by a *second* independent consumer report (StoreKit2 binding,
> Intel MacBook Pro, iOS Simulator) beyond the original #39 reporter — the
> demand threshold this doc set for revisiting.

## Session plan (post-spike, 2026-05-26)

**Decision (2026-05-26): cover the full x86_64 matrix** — `osx-x64` (console
+ macOS-workload GUI), `maccatalyst-x64`, `iossimulator-x64`,
`tvossimulator-x64` — not osx-x64-only. The "recommended scope: osx-x64 only"
section further down is superseded and retained as historical context.

Four logical sessions. The leverage: **Layer 2 (the ABI/thunk work) is
arch-level, so S1 covers x86_64 for *every* Apple platform at once.**
Per-platform work (S2–S4) is then just RID routing (Layer 1) and
`.swiftinterface` probing (Layer 3), both table-driven. Dependency order:
**S1 → S2 → {S3, S4}** (S3 and S4 both need S1+S2 but are independent of each
other; S3 first because it *proves* the Mono-x86_64 runtime that S4's
simulator relies on). Each session is self-contained with an independent
acceptance gate so it runs autonomously.

**Validation locality (important):** `osx-x64` and `maccatalyst-x64` apps are
Mac processes and **run under Rosetta on an Apple Silicon host**, so they are
fully runtime-validatable locally — and Catalyst/macOS workloads run
**Mono-x86_64**, which lets us close the "residual direct-`CallConvSwift` on
Mono-x86_64" caveat locally (S3). The **only** targets with an irreducible
local gap are the iOS/tvOS x86_64 *simulators* (no x86_64 sim on Apple
Silicon) — and even they inherit a proven ABI (S1) + proven Mono-x86_64
runtime (S3), leaving only the sim host/loader unverified until an Intel Mac
or the reporter runs it.

### Session 1 — Layer 2: x86_64 (SysV) thunk backend — the ABI core (all platforms)

> **Status: DONE.** Per-arch target abstraction (`ThunkTargetArch` →
> `Arm64ThunkTarget`/`SysVThunkTarget`) landed; arm64 moved first as a no-op
> refactor. The durable gate is `nuke X64ThunkGate` (`build/Build.X64ThunkGate.cs`
> + committed fixture/driver under `build/x64-thunk-gate/`); it builds the
> fixture for x86_64, runs the generator emit-only, assembles+links the emitted
> thunks, and round-trips all six ABI shapes under `arch -x86_64`. arm64
> BindingTests stay at baseline (2210 pass). **Note for Session 2:** the
> generator's *internal* wrapper/thunk compile is still arm64-only — it globs
> `*.arm64.s` and assembles it against whatever slice arch it's building, so for
> x86_64 it fails (arm64 mnemonics, non-zero exit). The gate sidesteps this with
> `--skip-wrapper-compilation --skip-thunk-compilation` and links the thunks
> itself. Wiring the internal compile to ship both arches' objects in the
> resolved slice is the Layer 1 build-wiring task below.

- **Goal**: the generator emits correct x86_64 SysV AMD64 thunks alongside
  arm64. Arch-level — once done, the ABI is covered for desktop, Catalyst,
  and both simulators.
- **Scope**:
  - Refactor `ThunkAssemblyEmitter` (`Emitter/ThunkEmitter/`) so register
    names + frame layout sit behind a per-arch target. Do arm64 first as a
    **no-op refactor** (assert generated `.arm64.s` is byte-identical), then
    add the SysV target: params `rdi,rsi,rdx,rcx,r8,r9`/`xmm0-7`, self→`%r13`
    (swiftself), error→`%r12` (swifterror), return `rax,rdx,rcx,r8`/`xmm0-3`,
    struct-return store, metatype-accessor caller-saved save/restore. The
    two templates (`TailCall`, `FullFrame`) and slot model in
    `TypeLowering.cs` carry over unchanged (field-wise lowering confirmed
    identical across arches).
  - Make `NativeThunkCompiler.CollectAssemblyFiles` (the `*.arm64.s` glob)
    and `ModuleEmitter`'s `{ns}.arm64.s` write arch-parametric (`.x86_64.s`).
- **Gate**: a new `osx-x64` + Rosetta CLI test harness (start from the
  reproducible spike in the **Appendix** at the end of this doc — it is the
  durable copy of the working fixture + thunk; do not rely on `/tmp`) that
  builds a fixture lib — instance methods, throwing
  (swifterror), static/ctor (metatype accessor), struct returns >16B
  including mixed int/float — for x86_64, links the generated x86_64 thunks,
  and asserts round-trip correctness under `arch -x86_64`. Existing arm64
  BindingTests stay green (proves the no-op refactor).
- **Validatable fully on Apple Silicon** (Rosetta). No packaging touched.

#### S1 follow-up: small (≤16B) by-value struct-return divergence gate

Making the thunk decision arch-neutral surfaced an ABI trap the original
spike (which used `>16B` mixed returns) never hit. A `≤16B` by-value struct
return is *tail-call*-thunked with **no repacking** — the thunk branches
straight to the Swift symbol and lets swiftcc's return land in the caller's
registers. That is only correct when swiftcc's per-field register assignment
matches the C ABI's aggregate return, and the two C ABIs we target classify
floating-point fields differently:

- **arm64 AAPCS64** returns an aggregate in the FP registers only when it is a
  *homogeneous* floating-point aggregate (HFA — every field the **same**
  fundamental FP type). Any non-HFA — including any int/float mix **and any
  mixed-width float pair** like `{Float, Double}` — goes back in the GPRs
  (x0/x1), while swiftcc returns it field-wise in the FP registers.
- **x86_64 SysV** classifies per eightbyte: a float sharing an eightbyte with
  another field gets packed into one register, while swiftcc explodes the
  fields. So `{Float, Float}` and `{Int32, Float}` diverge, but a field that
  owns a full eightbyte (e.g. each `Double` in `{Double, Double}`) agrees.

The thunk is chosen **once** for both arches, so the safe set is the
*intersection*: a single-slot return, an all-integer return, or a
*homogeneous* all-float return whose fields each own a full eightbyte — which,
once homogeneity is required, leaves only `{Double, Double}`. Everything else
(`{Int64, Double}`, `{Int64, Float}`, `{Float, Float}`, `{Int32, Int32, Float}`,
and the mixed-width float pairs `{Float, Double}` / `{Double, Float}`) is
declined to the `@_cdecl` wrapper, whose C-ABI return is correct by
construction. The rule lives in
`NativeThunkEmitter.SmallStructReturnDivergesFromCAbi`; register placement was
verified by `swiftc`/`clang` asm on both arches. Unit coverage is in
`NativeThunkEmitterTests` (`ShouldEmitThunk_*StructReturn_*`); end-to-end
round-trips for the arm64-divergent shapes (`WidePair` `{Int64, Double}`,
`WideFloatDouble` `{Float, Double}`) are in `MixedRegisterReturnTests` — the
iOS Simulator runs arm64, so they catch the divergence directly.

### Session 2 — Layer 1: multi-arch RID routing + packaging (all four x86_64 RIDs)

- **Goal**: `dotnet build` of a binding produces a nupkg that ships the
  matching `runtimes/<rid>/native` for **every** target, and an `osx-x64`
  consumer app works end-to-end.
- **Scope** (table-driven — adding four RIDs ≈ the effort of adding one):
  - Generator: `SliceVariant.Architecture` arch-aware (drop the `"arm64"`
    default); `XCFrameworkSlicer.SupportedRids` + `MatchesRid` add
    `osx-x64`, `maccatalyst-x64`, `iossimulator-x64`, `tvossimulator-x64`
    (new `SWIFTBIND050` cases); thread `--rid` through the slicer;
    `SwiftWrapperCompiler` builds the x64 slice (arch override already
    plumbed).
  - SDK `Sdk.targets`: derive `_SwiftBindingNuGetRid` (lines 34-37) from the
    consumer's `RuntimeIdentifier`/TFM instead of hardcoding arm64, and run
    the slice + wrapper-build + pack pipeline **per target RID** so both
    arm64 and x64 `runtimes/<rid>/native/` trees (2287-2293) land in one
    nupkg.
  - **Fail loud** (`SWIFTBIND0xx`) when the input xcframework lacks an
    x86_64 slice for the requested platform — never silently fall back to
    arm64.
- **Gate**: build a binding from a third-party xcframework with an x86_64
  macOS slice, pack it, consume from an `osx-x64` app, run under Rosetta,
  assert correct; arm64 unchanged. The other three RIDs get a build/slice/
  package gate here (their runtime gates are S3/S4).
- **Depends on S1** (needs x64 thunks to exist).

### Session 3 — Desktop GUI + Mac Catalyst: Layer 3 probing + Mono-x86_64 runtime proof

- **Goal**: macOS-workload (`net10.0-macos`) and Mac Catalyst
  (`net10.0-maccatalyst`) x64 bindings work end-to-end — and the
  Mono-x86_64 residual-`CallConvSwift` caveat is closed **locally** by
  running a Catalyst/macOS x64 BindingTests app under Rosetta.
- **Scope**:
  - Layer 3: extend `_SwiftInterfaceSlice` (Sdk.targets:240-245) with the
    x86_64 macOS + macabi triples (`x86_64-apple-macos`,
    `x86_64-apple-ios-macabi`) for Apple-framework bindings.
  - Add `maccatalyst-x64` + `osx-x64` (macOS workload) BindingTests cells
    run under `arch -x86_64` on the Apple Silicon CI host.
- **Gate**: maccatalyst-x64 and macOS-workload-x64 BindingTests pass under
  Rosetta — this is the proof that the narrow direct-`CallConvSwift` surface
  works on **Mono-x86_64**, not just CoreCLR.
- **Depends on S1 + S2.**

### Session 4 — iOS/tvOS x86_64 simulators + the StoreKit2 reporter binding

- **Goal**: the StoreKit2 reporter's binding builds, packages, and loads for
  `iossimulator-x64`; same for `tvossimulator-x64`. (Runtime packs ship
  today — `Microsoft.NETCore.App.Runtime.Mono.iossimulator-x64` etc. — so
  this is gated only by our generator.)
- **Scope**: Layer 3 sim triples (`x86_64-apple-ios-simulator`,
  `x86_64-apple-tvos-simulator`); finish wiring the sim RIDs end-to-end;
  build the reporter's StoreKit2-style Apple-framework binding for the x64
  sim.
- **Gate**: compile + packaging correct; the x64 sim slice loads. **Runtime
  validation boundary**: an x86_64 iOS/tvOS simulator cannot run on an Apple
  Silicon host, so true sim-runtime validation is handed to the reporter or
  an Intel Mac. Confidence is high regardless: S1 proved the ABI, S3 proved
  the Mono-x86_64 runtime — only the simulator host/loader is unverified.
- **Depends on S1 + S2** (and S3 for runtime confidence).

---

> **⚠️ Everything below this line is pre-spike background and original
> analysis (pre-2026-05-26), retained for context.** Where it conflicts with
> the **Session plan** or **Empirical spike results** above, *those win.*
> Specifically superseded:
> - "If picked up: recommended scope — `osx-x64` desktop only / do not
>   attempt the full matrix" → we deliberately cover the **full x86_64
>   matrix** (see Session plan).
> - "Why deferred" / "unless demand surfaces" → demand surfaced (2nd
>   reporter); we are proceeding.
> - "De-risking sequence… the C-shim spike; then decide" and the Open
>   Questions about a spike → the **spike is DONE**; the assembly-path
>   approach is validated, the C-shim alternative is moot.
> - "Layer 2… the largest piece, each thunk-emitting site produces two
>   variants" → it is **one centralized file** (`ThunkAssemblyEmitter.cs`,
>   two templates, one entry point), not per-site work.
>
> A fresh session implementing S1–S4 should work from the Session plan +
> spike results + Appendix; the sections below are reference detail on the
> arm64 hardcode points and the platform/runtime landscape.

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

## Empirical spike results (2026-05-26)

A one-session spike on the current tree (Apple Silicon, Xcode 26.3 /
Swift 6.2.4, .NET SDK 10.0.107) executed the doc's recommended "cheapest
high-fidelity ABI gate": `osx-x64` self-contained .NET app, run under
Rosetta, calling an x86_64 Swift dylib three ways. **All three passed**
(`ProcessArchitecture=X64` confirmed genuine x86_64 execution, not arm64):

| Path tested | Mechanism | Result |
|---|---|---|
| cdecl P/Invoke into `@_cdecl` func | CoreCLR osx-x64 native | ✅ correct |
| **Our Layer-2 thunk**: cdecl → hand-written x86_64 SysV thunk (self→`%r13`) → swiftcc instance method | the thing we'd generate | ✅ correct |
| **Direct `CallConvSwift`** P/Invoke into a swiftcc instance method (`SwiftSelf`) | the doc's "unfixable here" wildcard | ✅ correct |

What this changes about the three-layer estimate:

1. **The `CallConvSwift`-on-x86_64 wildcard is dead.** CoreCLR's x86_64
   JIT lowers `SwiftSelf` to the correct register (`%r13`) and the call
   returns correctly. The residual direct-`CallConvSwift` surface
   (standalone closure wrappers, `PInvokeHelperEmitter.cs:678`) is
   therefore *not* blocked on x86_64 desktop. (Mono-x86_64 on the
   simulator is a separate runtime and still unproven for the residual
   surface — but the cdecl-thunk path below carries most of the load and
   is runtime-agnostic.)

2. **Layer 2 is centralized, not scattered.** The whole thunk emitter is
   **one file, two templates** (`ThunkAssemblyEmitter.cs`: `TailCall` +
   `FullFrame`), reached through a single `NativeThunkEmitter.EmitThunk`
   entry from 4 call sites. The hand-written x86_64 SysV thunk that bridged
   self into `%r13` worked first try. This is the doc's low-end
   ("C-shim/templated covers it") scenario, not the 10+-session
   hand-written-backend scenario.

3. **The lowering layer is mostly arch-portable.** `TypeLowering.cs`
   assigns each struct field to the next integer/float register slot
   (`i`→GPR, `f`→FPR) with a 4-slot / 16-byte direct rule. Swift's swiftcc
   explodes aggregates field-wise on **both** arm64 and x86_64 (it does
   *not* use the SysV C "eightbyte merge"), so the slot model carries over;
   what's arm64-specific is purely the physical register *naming* in the
   emitter (`x{i}`/`d{i}` → the x86_64 param sequence `rdi,rsi,…`/`xmm` and
   the return sequence `rax,rdx,rcx,r8`/`xmm0-3`). **The eightbyte question
   is RESOLVED:** LLVM IR for a mixed `{i32, float, i64, double}` struct
   returns `swiftcc { i32, float, i64, double }` *identically* on x86_64 and
   arm64 — Swift explodes field-wise and does **not** apply the SysV C
   eightbyte merge. The return-bridge port is mechanical; the slot model
   needs no x86_64-specific classifier.

4. **Microsoft already ships the x86_64 simulator runtime pack.** The
   stock .NET 10 iOS workload includes `Microsoft.NETCore.App.Runtime.Mono.iossimulator-x64`,
   `Microsoft.iOS.Runtime.iossimulator-x64.net10.0_26.2`, and the
   `…AOT.osx-arm64.Cross.iossimulator-x64` cross-compiler. The CoreCLR
   section's worry #2 ("Microsoft may simply not ship the pack") is false
   for the .NET 10 Mono era — the simulator runtime is present today, so
   the simulator path is gated **only by our generator**, not by Microsoft.

**Net:** the spike converted the doc's "5–7 or 10+ sessions, gated on an
unverified runtime wildcard" into "the wildcard works; Layer 2 is one
templated file." See revised sizing below.

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
   - On (a): **checked 2026-05-26 — the pack ships today.** The stock
     .NET 10 iOS workload includes `Microsoft.NETCore.App.Runtime.Mono.iossimulator-x64`
     and `Microsoft.iOS.Runtime.iossimulator-x64.net10.0_26.2`. For the
     .NET 10 Mono era there *is* a runtime to host the binding, so the
     simulator is gated only by our generator. The CoreCLR-pack question
     re-opens only for .NET 11+, when the sim moves off Mono — worth
     re-checking then, but no longer a precondition for .NET 10 work.
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

**Sizing (Claude-session terms) — post-spike, no longer bimodal:**

The 2026-05-26 spike resolved the fork. The hand-written x86_64 SysV thunk
worked, so the "10+ session hand-written backend" branch is retired — Layer 2
is a parallel emit path in one file, not a from-scratch ABI backend.

- **Spike: DONE.** ✅ All three calling paths verified on x86_64 under
  Rosetta (cdecl, our thunk, direct `CallConvSwift`). See "Empirical spike
  results."
- Layer 1 (`osx-x64`) + `osx-x64` BindingTests cell: ~1 session, mechanical
  (4 arm64 hardcode points: `SliceVariant.Architecture`, the `*.arm64.s`
  glob, the `{ns}.arm64.s` write, `XCFrameworkSlicer.SupportedRids`).
- Layer 2 (x86_64 thunk emit): parametrize `ThunkAssemblyEmitter` over a
  register-naming + frame-layout target (arm64 → no-op refactor, then add
  the SysV target). Two templates, four sub-flags. **~2–4 sessions**
  including per-category BindingTests, with the struct-return bridge
  (eightbyte question, item 3 above) as the one piece to validate first.
- **Full `osx-x64` desktop ≈ 3–5 sessions** (was 5–7 / 10+). The simulator
  matrix adds Layer 1 RID branches + packaging, not more ABI work.
- **Wildcard, formerly "unfixable here": resolved green.** CoreCLR x86_64
  `CallConvSwift` works (spike path 3). The remaining runtime caveat is
  narrower than the doc feared: only the Mono-x86_64-on-*simulator* residual
  `CallConvSwift` surface is unproven, and the cdecl-thunk path (proven)
  carries most of it.

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

## Appendix — reproducible spike (2026-05-26)

This is the durable copy of the spike that retired the two big unknowns.
It compiles a Swift dylib for x86_64, hand-writes the cdecl→swiftcc thunk
(the thing S1 will generate), and calls it three ways from a `osx-x64`
.NET app under Rosetta on an Apple Silicon host. **S1's gate harness should
grow from this.** All four files + the commands ran green on Xcode 26.3 /
Swift 6.2.4 / .NET SDK 10.0.107.

**`spike.swift`** — fixture (single file ⇒ Swift module name is `main`):

```swift
import Foundation

// (1) cdecl baseline — plain C calling convention.
@_cdecl("spike_add")
public func spike_add(_ a: Int64, _ b: Int64) -> Int64 { return a &+ b }

// (2)/(3) instance method whose self travels in the swiftself register.
public final class Box {
    private var v: Int64
    public init(_ start: Int64) { v = start }
    public func addAndGet(_ delta: Int64) -> Int64 { v = v &+ delta; return v }
}

// Stable swiftcc symbol to make a Box without guessing init mangling.
@_silgen_name("spike_box_make")
public func spike_box_make(_ start: Int64) -> Box { return Box(start) }
```

**`thunk.x86_64.s`** — the proven cdecl→swiftcc self-bridge (arm64 moves
self into `x20`; x86_64 SysV uses `%r13`). The branch target is the mangled
symbol of `Box.addAndGet`; get it from `nm -gU libspike.x86_64.dylib`
(`_$s4main3BoxC9addAndGetys5Int64VAFF` for module `main`):

```asm
.text
.globl _thunk_addAndGet
.p2align 4
_thunk_addAndGet:                                  // cdecl args: delta=%rdi, self=%rsi
    pushq   %r13                                   // preserve callee-saved r13 for the .NET caller
    movq    %rsi, %r13                             // self -> swiftself
    callq   _$s4main3BoxC9addAndGetys5Int64VAFF    // delta already in %rdi; returns in %rax
    popq    %r13
    ret
```

**`app/app.csproj`** + **`app/Program.cs`** — the three calling paths:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <RuntimeIdentifier>osx-x64</RuntimeIdentifier>
    <SelfContained>true</SelfContained>
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
  </PropertyGroup>
</Project>
```

```csharp
using System;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices.Swift;

unsafe class P {
    const string LIB = "spike"; // -> libspike.dylib next to the exe

    [DllImport(LIB, EntryPoint = "spike_add", CallingConvention = CallingConvention.Cdecl)]
    static extern long Add(long a, long b);

    // OUR Layer-2 mechanism: cdecl P/Invoke into a native x86_64 thunk.
    [DllImport(LIB, EntryPoint = "thunk_addAndGet", CallingConvention = CallingConvention.Cdecl)]
    static extern long AddAndGet_ViaThunk(long delta, IntPtr self);

    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvSwift) })]
    [DllImport(LIB, EntryPoint = "spike_box_make")]
    static extern IntPtr BoxMake(long start);

    // THE WILDCARD: direct CallConvSwift into an instance method (self -> r13).
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvSwift) })]
    [DllImport(LIB, EntryPoint = "$s4main3BoxC9addAndGetys5Int64VAFF")]
    static extern long AddAndGet_Direct(long delta, SwiftSelf self);

    static int Main() {
        Console.WriteLine($"ProcessArchitecture={RuntimeInformation.ProcessArchitecture}");
        Console.WriteLine($"[1 cdecl]         = {Add(40, 2)}            (expect 42)");
        IntPtr box = BoxMake(100);
        Console.WriteLine($"[2 thunk]         = {AddAndGet_ViaThunk(5, box)}  (expect 105)");
        Console.WriteLine($"[3 CallConvSwift] = {AddAndGet_Direct(5, new SwiftSelf((void*)box))}  (expect 110)");
        return 0;
    }
}
```

**Build + run** (from the dir holding `spike.swift`, `thunk.x86_64.s`, `app/`):

```bash
xcrun clang -c thunk.x86_64.s -o thunk.x86_64.o -target x86_64-apple-macos13.0
swiftc -emit-library -target x86_64-apple-macos13.0 -o libspike.x86_64.dylib spike.swift thunk.x86_64.o
cd app && dotnet publish -r osx-x64 -c Release -o out
cp ../libspike.x86_64.dylib out/libspike.dylib
./out/app        # Rosetta translates the x86_64 apphost automatically
```

**Expected output** (genuine x86_64 execution — note `X64`):

```
ProcessArchitecture=X64
[1 cdecl]         = 42            (expect 42)
[2 thunk]         = 105  (expect 105)
[3 CallConvSwift] = 110  (expect 110)
```

**Eightbyte check** (settles whether the return-bridge slot model is
portable — it is): `swiftc -emit-ir -target x86_64-apple-macos13.0 mixed.swift`
on a `struct Mixed { var a: Int32; var b: Float; var c: Int64; var d: Double }`
+ a func returning it shows `define swiftcc { i32, float, i64, double }` —
**identical** to the arm64 target. Swift explodes field-wise; no SysV
eightbyte merge.
