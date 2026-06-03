# Mixed-framework (ObjC + Swift) packaging gaps

**Status:** Gap 2 (static-native double-embed), Gap 3 (ObjC over-binding), and
the correctness facets of Gap 1 (companion versioning 1.2, packable wiring 1.3,
obj/ isolation 1.5; native carrier 1.4 resolved by Gap 2) are **implemented and
reviewed** (paired Codex + Grok). Still open: Gap 1 facet 1.1 (no automated path
produces the companion nupkg) plus the pack→consume acceptance gate — gated on
open question #3 (companion publish ownership/order, a release-policy decision) —
and D5 (static *transitive* dependency force_load, no failing repro yet). See
`mixed-framework-packaging-plan.md` for the phased implementation and decisions.

Captured 2026-06-02 from a real reporter-bundle build (Kidoz SDK, issue #40) and
a follow-up source sweep. This doc aims to capture *everything* — confirmed gaps
to fix, smaller structural issues, and open questions to investigate — so a
future session starts from facts, not a re-derivation.

## Why this matters

A "mixed framework" is a third-party xcframework that has **both** a Swift API
and an Objective-C surface, so the generator emits two binding projects:

- `Foo.Swift.iOS.csproj` — the Swift binding (the main package)
- `Foo.ObjC.iOS.csproj` — a companion binding for the ObjC classes, with a
  tool-generated `ApiDefinition.cs` / `StructsAndEnums.cs` (Kidoz: the OMID
  viewability classes)

We want this to be **publishable to NuGet, and consumable with a single
`PackageReference`, with zero manual steps**. Today it is not: producing a
working nupkg pair for Kidoz required hand-editing several things the tooling
should have emitted, and uncovered a binding-content bug. The desired end state:
a consumer references `Foo.Swift.iOS` and transitively gets the ObjC types, the
runtime, the Apple deps, and a native library that loads **once**.

Nothing here is documented elsewhere. `src/docs/Future/cross-package-nuspec-dependencies.md`
covers the related-but-distinct Apple inter-package dependency case and assumes
third-party multi-product packages already work via ProjectReference promotion.

The canonical real-world mixed frameworks already in the repo are **BlinkID**
(`validation-libraries.json:204-217`, tier-1 binary mode) and **BRLMPrinterKit**
(`:232-240`, tier-1 manual) — but both are covered for *generation* only, not for
*pack → consume* (see "Test coverage" below). Either could seed a real gate.

---

## Gap 1 — the mixed dependency isn't pack-wired (publishing is manual)

The single-Swift-binding pack flow works. The **mixed** dimension does not: there
is no automated path that produces a publishable, correctly-wired package pair.
Several facets, all confirmed in source:

1. **The companion is never auto-packed.** `nuke pack` (`build/Build.Pack.cs:24-170`)
   packs only the four infrastructure packages (`Runtime`, `Sdk`, `Templates`,
   `Apple`) — it has no per-binding logic at all. Per-binding packing happens
   through the SDK's `_ConfigureSwiftBindingPack` (`Sdk.targets:2652-2750`), which
   runs in the **Swift** binding's context and packs only it. The SDK injects the
   companion as a build-time `ProjectReference` (`_InjectMixedObjCProjectReference`,
   `Sdk.targets:1751-1772`), but a ProjectReference does not cause the referenced
   project to be packed. So nothing — not Nuke, not the SDK — produces the
   companion nupkg. A developer must `dotnet pack` it by hand.

2. **The companion has no version**, so even when packed it can't be promoted to a
   nuspec `<dependency>`. `ObjCBindingProjectEmitter.cs:48` emits `IsPackable=true`
   + a `PackageId` but no `<Version>` / `<PackageVersion>`. ← root cause of the
   "ProjectReference won't auto-promote" symptom.

3. **The Swift binding references the companion as a ProjectReference, not a
   package.** `BindingProjectEmitter.cs:347-357` emits
   `<ProjectReference Include="{Foo}.ObjC.iOS.csproj" />` with no PackageReference
   / pack-aware variant and no pack-mode conditional. The `buildTransitive`
   `$(PackageId).targets` (`ConsumerTargetsEmitter.cs`) never mentions the
   companion at all, and the `SwiftFrameworkDependency` → nuspec-dependency
   mechanism (`_ValidateSwiftDependencyMetadata`, `Sdk.targets:~2400-2413`) is
   Swift-to-Swift only — neither covers the ObjC companion.

4. **The companion's native is unreachable from a nupkg.** Its `<NativeReference>`
   uses an absolute on-disk path to the source xcframework
   (`ObjCBindingProjectEmitter.cs:73-75`, no `Pack`/`PackagePath`). The native is
   packed **only** into the *Swift* binding nupkg
   (`Sdk.targets:2721-2723` source xcfw → `runtimes/<rid>/native/`; `:2724-2726`
   wrapper; `:2730-2732` bridge). So a published companion package would carry no
   native and would have to obtain the ObjC classes' symbols from the Swift
   package transitively — a wiring that doesn't exist today and interacts with
   Gap 2 (where does the native actually load from?).

5. **obj/ stomping (structural smell).** All binding csprojs are generated into one
   directory and share one `obj/project.assets.json` (keyed by directory, not
   project). A ProjectReference pulls the companion's restore graph in, both
   projects write the same assets file, the companion (no Runtime ref) wins, and
   the Swift compile fails CS0246 on every `Swift.Runtime` type. The
   PackageReference form sidesteps it, but the "one obj/ for N projects" layout is
   a latent hazard for any multi-project scenario.

### Manual workaround used for the Kidoz bundle

1. Added `<PackageVersion>` to the companion csproj; packed it as its own nupkg.
2. Replaced the Swift binding's ProjectReference with
   `<PackageReference Include="Foo.ObjC.iOS" Version="X" />` (real nuspec
   `<dependency>` + clean restore graph, sidesteps the obj/ stomp).

### Fix direction (to design)

Give the companion a real `PackageId` + `PackageVersion` (derived from the Swift
binding), emit the dependency in **packable** form (PackageReference or a
SwiftFrameworkDependency-style mechanism extended to the companion) so `dotnet
pack` + `buildTransitive` carry it automatically, add an automated path that
actually produces the companion nupkg, and decide where the companion's native
comes from at consume time (see Gap 2).

---

## Gap 2 — the static native is double-embedded → duplicate ObjC class registration

### Observed (ground truth)

The packaged Kidoz consumer produced **82** `objc[…]: Class <X> is implemented in
both …/FooSwiftBindings.framework/… and …/<App> (the main executable)` warnings
(e.g. `KidozOMIDSessionManager`, `KidozOMIDService`, `KPBid`). The native's ObjC
classes are present in **both** the wrapper framework binary **and** the app
executable. Benign in every run we validated (33/0 device + 9/0 sim), but the
runtime warns it "may cause spurious casting failures and mysterious crashes."

### Mechanism

- The wrapper is always built `swiftc -emit-library` (dynamic)
  (`SwiftWrapperCompiler.cs:1501`), linking the native via `-F <slice>
  -Xlinker -framework -Xlinker <Native>`. `-framework` reads like dynamic
  linkage, **but** the vendor ships its native slice as a **static `ar archive`**
  (`file KidozSDK.framework/KidozSDK` → `current ar archive`). A static archive is
  *statically embedded* into every image that links it — so the native's ObjC
  classes are baked into the wrapper dylib.
- The consumer also links the **same** source xcframework directly:
  `BindingProjectEmitter.cs:439-443` (standalone csproj) and
  `ConsumerTargetsEmitter.cs:157-160` (packed `buildTransitive` targets) both
  register it as a `NativeReference`. So the app executable embeds the native too.
- Net: native ObjC classes embedded twice. A vendor SDK shipping a *dynamic*
  native wouldn't hit this (one load command, one copy) — the bug is specific to
  **static-archive** natives, which are common.

### Existing machinery to reuse

The codebase already distinguishes static vs dynamic natives, just not for wrapper
linking: `RequiresTbdSynthesis` runs `file` + falls back to `nm -gU`
(`XCFrameworkResolver.cs:186-192`, `:1114-1133`); `IsLinkableFrameworkBinary`
accepts both Mach-O and `ar` archives (`SwiftWrapperCompiler.cs:1600-1641`);
static bare-slices are rejected with `SWIFTBIND101` (`XCFrameworkResolver.cs:199-204`).
But the wrapper's `-emit-library` invocation never branches on static vs dynamic,
and nothing adds `-force_load` / `-undefined dynamic_lookup` / two-level-namespace
handling.

### Fix direction (to design)

Ensure the native is embedded into the consumer **exactly once**. Options: (a)
build the wrapper to leave native symbols undefined and resolve from the app's
copy (don't embed the static archive into the wrapper); (b) make the wrapper the
sole carrier and stop registering the source xcframework as a consumer
NativeReference; (c) detect static-archive natives (machinery above) and pick a
strategy per linkage type. First step: verify per-slice (device vs simulator)
static/dynamic linkage.

---

## Gap 3 — the ObjC emitter over-binds classes that have no native symbol

### Confirmed (this is ours)

The ObjC companion's `ApiDefinition.cs` is **tool-generated**, not hand-authored:
`ObjCPipeline.Run` (`ObjC/Pipeline/ObjCPipeline.cs:22-163`) runs `xcrun clang …
-ast-dump=json` over the umbrella header (`ClangAstInvoker.cs:51-92`) and
`ApiDefinitionEmitter.Emit` (`ObjC/Emitter/ApiDefinitionEmitter.cs:82-87`, `:181`,
`:108`, `:266`) emits a `[BaseType(typeof(NSObject))] interface` for **every**
class found in the headers.

There is **no native-symbol existence guard**. The only filters are name-based:
`FilterForMixedFramework` drops names in the Swift type set
(`ObjCPipeline.cs:193-225`) and `FilterPlatformTypeStubs` drops Apple-SDK types
(`:232-280`); an empty-module check skips emission entirely (`:91-97`). None
consult the TBD / `nm` / native binary for whether the class actually exists.

So a class **declared in a header but absent from the binary** gets a binding with
no backing symbol → the consumer fails to link. This is exactly the
`OMIDAdSession` / `OMIDSDK` break I hit: Kidoz statically links the OMID SDK under
a vendor class prefix (`OMIDKidoznet*`), but ships headers that still declare the
un-prefixed `OMIDAdSession` / `OMIDSDK`. The emitter bound the header-declared
names; the linker found no `_OBJC_CLASS_$_OMIDAdSession` / `…OMIDSDK`. I removed
the two spurious interfaces by hand to make the bundle link.

### Fix direction (to design)

Before emitting an ObjC class/protocol interface, confirm a matching native
symbol exists (TBD or `nm -gU` over the slice — the same evidence
`RequiresTbdSynthesis` already gathers). Drop (or comment out with a diagnostic)
header-only declarations. Emit a build warning listing what was skipped so a
human can audit, rather than producing code that silently fails to link.

---

## Open questions to investigate

These need a decision or a dig before they become fix items:

1. **TFM pinning vs. consumer Xcode.** The generated package TFM is always
   version-pinned (`net10.0-ios26.0`): `BindingProjectEmitter.cs:392` /
   `ObjCBindingProjectEmitter.cs:44` use `pi.PackTfm` = `Tfm + PlatformVersion`
   (`PlatformInfo.cs:55`, default `"26.0"` at `:28`), and a comment at
   `BindingProjectEmitter.cs:385-391` says versionless `net10.0-ios` is
   *intentionally* never emitted. But a pinned TFM forces the consumer onto an
   exact iOS SDK pack → *"requires Xcode 26.0"* on a newer Xcode; I had to tell
   Carl to use versionless `net10.0-ios` in his app. **Q: is pinning the right
   default for *published* packages, given the assets are compatible with any iOS
   ≥ the pinned version?** Re-read the rationale in that comment before changing
   anything; the fix may be consumer docs, a lower floor, or a packable-vs-build
   distinction rather than removing the pin.

2. **Companion versioning relationship.** If the companion gets a version (Gap 1),
   should it be lockstep with the Swift binding, or independent? How is it
   derived? Lockstep is simplest but couples release cadence.

3. **Publishing / CI for the pair.** Even after Gap 1, who publishes the companion
   to NuGet.org, and in what order (companion before Swift binding, since the
   Swift binding depends on it)? `nuke pack` doesn't build it today — does the
   release pipeline need a new step?

4. **Companion native at consume time.** The native is only in the Swift nupkg
   (Gap 1.4). When a consumer pulls the companion transitively, where do its ObjC
   classes' symbols resolve from — the Swift package's native, loaded once?
   Confirm the intended single-load wiring end to end (ties directly to Gap 2).

5. **Device / NativeAOT behavior of the double-embed.** The 33/0 device
   (NativeAOT) run also showed the duplicate-class warnings and passed. Does the
   static linker / AOT toolchain dead-strip or merge differently than the
   simulator's dynamic loader, and is "benign on sim" the same as "benign on
   device"? Verify both after a Gap 2 fix.

6. **`SwiftBindings.Apple` dependency range.** The bundle's Swift binding depends
   on `SwiftBindings.Apple [26.0.0,)` (open upper bound). Confirm that's the
   intended range policy for published bindings (cf. the decoupling note in
   memory `feedback_apple_supplement_decoupling`).

7. **bgen resource flow.** The companion produces a `*.resources.zip` (bgen
   binding resources). Confirm it packs and flows transitively to consumers
   correctly — untested in the bundle path.

8. **Single-reference UX.** Verify the end goal empirically: a consumer adds one
   `PackageReference` to `Foo.Swift.iOS` and can use the ObjC types without a
   second explicit reference. That's the "super easy to use" bar.

---

## Test coverage (none today for pack → consume)

Existing mixed-framework coverage is generation-only:

- Unit: `MixedFrameworkDedupTests.cs`, `MixedFrameworkDetectionTests.cs`,
  `ObjCPipelinePostProcessTests.cs`, `ObjCPipelineIntegrationTests.cs` — cover
  dedup + detection logic, not packing.
- Validation: BlinkID + BRLMPrinterKit exercise generation/build, not pack.
- BindingTests: no project consumes a pre-packed mixed companion nupkg.

So the mixed **pack → consume** round-trip has **zero** automated coverage; the
acceptance gate below is genuinely new work.

---

## Acceptance criteria for the fix session

- `dotnet pack` (or `nuke pack`) on a generated mixed-framework Swift binding
  produces **both** nupkgs, and the Swift nuspec carries a real `<dependency>` on
  the versioned companion, with **no** hand-edits to either csproj.
- The ObjC emitter does not bind classes absent from the native binary (Gap 3),
  and warns about what it skipped.
- A clean nupkg-only consumer (no source/ProjectReference) with a single
  `PackageReference` to the Swift binding restores, compiles, links, and runs —
  ObjC types usable, and the native's ObjC classes register **once** (no
  `implemented in both` warnings) on **both** simulator and device.
- A BindingTests-level gate packs a mixed framework (BlinkID is the natural
  candidate) and consumes it as nupkgs, so none of the above can silently regress.
