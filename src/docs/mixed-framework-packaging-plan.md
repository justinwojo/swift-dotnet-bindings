# Mixed-framework packaging — implementation plan

Companion to `mixed-framework-packaging-gaps.md`. That doc captures the gaps;
this one captures the **decisions and the phased implementation**. Written
2026-06-02 after a source verification pass + an empirical sweep of the fetched
xcframeworks in `.libraries/`, then **revised after paired Codex + Grok review**
(Codex session `019e8b34-97ae-77b3-8c62-9b89a2a8f61d`, Grok session
`019e8b34-ec4e-71e0-a420-ab6568b68032`).

## Review corrections folded in (2026-06-02)

Both reviewers converged on the same load-bearing corrections; all are reflected
in the phases below:

1. **No dual ProjectReference/PackageReference form.** `dotnet pack` already
   auto-emits a nuspec `<dependency>` from a `<ProjectReference>` to a *packable*
   sibling csproj (one with `PackageId` **and** `Version`/`PackageVersion`). Adding
   an explicit `PackageReference` on top is a documented anti-pattern (NU5128 /
   duplicate dep) — see `src/docs/Future/cross-package-nuspec-dependencies.md:20-27,50-54`.
   Mechanically a pack-time prop can't flip the ref form anyway, because ItemGroup
   conditions evaluate at project *load*. **Phase C collapses into B**: version the
   companion → the existing bare `ProjectReference` promotes automatically. No
   `SwiftBindingCompanionAsPackage` conditional.
2. **Gap 2 must gate *packing*, not just the NativeReference, and must cover static
   *dependency* archives.** For a static native the source xcframework is a
   generate/compile-time input only — the wrapper force-loads it. So the linkage
   signal gates (a) wrapper force_load, (b) the consumer NativeReference at every
   site, **and (c) the pack items** (`BindingProjectEmitter` `None` item;
   `Sdk.targets` `@(SwiftFramework)`-driven pack). The same policy applies to static
   `SwiftFrameworkDependency` archives (Firebase/GTMAppAuth graphs), or the
   duplicate-class problem just relocates to a dependency.
3. **One linkage signal in `binding-metadata.props`.** All five SDK sites
   (inject, GetNativeManifest, source pack, dep NativeReference, dep pack) read one
   generator-emitted value — the single source of truth that avoids the
   "exists-now vs will-be-produced" hazard `constraints.md` warns about.
4. **Gap 3 guard unions symbols across *all shipped slices*** (sim + device), not
   the resolved one — a class present in only one slice must not be false-dropped
   or false-kept. It also **drops categories whose base class was dropped**
   (`ApiDefinitionEmitter.cs:265` emits `[BaseType(typeof(<ClassName>))]`), and
   requires **extending `ObjCFrameworkResolution`** to carry the native binary path
   (the record has none today). Reuse `ParseNmSymbols` archive-header skipping.
5. **Companion pack runs once, before nuspec collection** — never inside the
   per-TFM `_ConfigureSwiftBindingPack` (which would pack it repeatedly and race).
6. **The Gap 2 gate asserts `nm -gU` on the produced wrapper** shows representative
   `_OBJC_CLASS_$_*` exports — not only that the app launches.

## Empirical groundwork (verified, not assumed)

- **BlinkID** (`.libraries/BlinkID/BlinkID.xcframework`) ships a **dynamic**
  native on both slices (`ios-arm64`, `ios-arm64_x86_64-simulator`). It is the
  canonical mixed framework, but because its native is dynamic it does **not**
  reproduce Gap 2 (the static-archive double-embed). It is the right gate for
  **Gap 1 (pack→consume)** and **Gap 3 (over-binding)**.
- **Gap 2** is static-archive-specific. Fetched static-archive frameworks that
  are genuinely mixed (Swift module + ObjC headers): `Promises`, `GTMAppAuth`,
  and the Firebase family. `Promises` (FBLPromises ObjC + Promises Swift) is the
  smallest clean static+mixed repro. Use it (or a purpose-built fixture) for the
  Gap 2 single-registration assertion.
- Test infra: ObjC pipeline/emitter unit tests build **in-memory `ObjCModule`s**
  and call static methods directly (`MixedFrameworkDedupTests`,
  `ObjCPipelinePostProcessTests`). `ICommandRunner` has test doubles
  (`MockCommandRunner`, `ScriptedCommandRunner`). `PackGate`
  (`build/Build.PackGate.cs`) is the existing pack→consume gate (packs
  Runtime/SDK/Apple at `0.0.0-packgate`, packs a fixture, asserts nupkg layout,
  runs an end-to-end consumer app). There is **no** mixed pack→consume coverage.

## Decisions on the open questions

1. **TFM pinning (Q1):** keep the version-pinned `PackTfm` default. The pin is
   load-bearing for the buildTransitive pack layout (NU1012) and for libraries
   not floating TPV. The consumer-Xcode friction is a **docs** fix (consumer app
   uses versionless `net10.0-ios`; the package floor is compatible with any iOS ≥
   pin). No emitter change. Document in the wiki Known-Limitations + a comment.
2. **Companion versioning (Q2):** **lockstep** with the Swift binding. Both are
   generated from one xcframework in one run; independent cadence buys nothing
   and couples worse. Derive the companion `PackageVersion` from the same
   `XCFrameworkMetadata.PackageVersion` the Swift binding uses.
3. **Publish order (Q3):** companion first, then Swift binding (the Swift binding
   declares a dependency on the companion). The automated pack produces both;
   the publish step pushes companion before Swift. Documented, not enforced in
   code beyond producing both nupkgs.
4. **Companion native at consume (Q4):** the companion is **managed-only**. Its
   ObjC class symbols resolve from the **Swift package's** native (source
   xcframework + wrapper), loaded once. The companion nupkg carries no native;
   its local-build `NativeReference` is `Pack="false"`/local-only. This is the
   single-load wiring and is the same decision as Gap 2.
5. **Device/AOT double-embed (Q5):** verify after Gap 2 on device. Needs
   hardware; flagged as a hardware-gated validation step, not a code item.
6. **Apple dep range (Q6):** `[26.0.0,)` open-upper is intended per the
   `feedback_apple_supplement_decoupling` memo (cross-major additive-only). No
   change; confirm only.
7. **bgen resources (Q7):** the companion's `*.resources.zip` must pack and flow
   transitively. Covered by the automated companion pack + the pack→consume gate.
8. **Single-reference UX (Q8):** the acceptance bar; verified by the Phase F
   gate (one `PackageReference` to the Swift binding → ObjC types usable).

## Architecture decisions

### Gap 3 — native-symbol existence guard (ObjC over-binding)

Add a guard step in `ObjCPipeline.Run`, mirroring the existing
`FilterPlatformTypeStubs`/`FilterToForeignCategories` filter pattern:

- Gather the set of **defined ObjC class symbols** from the native binary via
  `nm -gU <slice-binary>` (the same evidence
  `RequiresTbdSynthesis`/`SynthesizeTbdFromStaticArchive` already gather),
  extracting `_OBJC_CLASS_$_<Name>` entries. Reuse `ParseNmSymbols`'
  archive-header (`name.o:`) skipping. Run through the injected `ICommandRunner`.
- **Union across all shipped slices.** A class present only on the device slice
  but absent from the sim slice (or vice-versa) must not be false-dropped — the
  generated ApiDefinition is shared across slices and ships in one nupkg. Gather
  symbols from every slice the binding will link/ship and union them before
  filtering. `ObjCFrameworkResolution` carries only `FrameworkSearchPath`/
  `ModuleName`/`FrameworkDirectoryName`/`IsSimulatorSlice` today — **extend it
  with the resolved native binary path** (mirrors the Swift-side `DylibPath`) and
  surface the sibling slice path(s) so the guard can union.
- Pure filter `FilterToNativeSymbolBackedClasses(ObjCModule, ISet<string>
  presentClassNames, ILogger, ObjCBindingDiagnostics)` drops any class whose
  exact name has no `_OBJC_CLASS_$_<Name>` symbol. **It also drops any category
  whose `ClassName` was just dropped** (`ApiDefinitionEmitter.cs:265` emits
  `[Category][BaseType(typeof(<ClassName>))]`; a surviving category on a dropped
  class still fails to compile/link). Record each drop in diagnostics and emit a
  single `SWIFTBIND054` **warning** listing skipped classes/categories.
- **Scope to classes (+ their categories) only.** The demonstrated failure is
  `_OBJC_CLASS_$_OMIDAdSession` undefined. ObjC `[Protocol]` bindings use
  `_OBJC_PROTOCOL_$_*`/section metadata, not `_OBJC_CLASS_$`, and bgen tolerates
  protocol-only declarations; guarding protocols risks dropping legitimately
  referenced protocols. Classes are the link-break surface.
- **Fail-open on missing evidence.** If `nm` fails or no slice binary can be read
  (e.g. a header-only slice), do not filter — log and keep current behavior. The
  guard only ever *removes* a class when we have positive proof the symbol is
  absent across **every** slice we could read. A static archive lists
  `_OBJC_CLASS_$_*` from `nm -gU`; a dynamic Mach-O does too.
- **Mangled-symbol caution.** Match the exact `_OBJC_CLASS_$_<Name>` form against
  the post-mixed-dedup class name. The guard runs *after* mixed/platform filters
  so Swift `@objc` classes already deduped by Swift name are gone; the residual
  classes are pure-ObjC surface whose symbol is the unmangled `_OBJC_CLASS_$_<Name>`.
- Pure function is unit-tested with in-memory modules + canned symbol sets
  (matches `MixedFrameworkDedupTests`), including a category-on-dropped-class case
  and a slice-union case. Integration: regenerate BlinkID, confirm no over-bound
  classes and a clean warning list.

### Gap 1 — pack-wiring the mixed dependency

Four interlocking pieces:

**(B) Companion versioning + obj isolation + managed-only.** In
`ObjCBindingProjectEmitter`:
- Add `PackageVersion` to `ObjCBindingProjectOptions`, threaded from the Swift
  binding's `XCFrameworkMetadata.PackageVersion` (lockstep). Emit
  `<PackageVersion>`.
- Set a **distinct `<BaseIntermediateOutputPath>`** (e.g. `obj.objc/`) so the
  companion no longer shares `obj/project.assets.json` with the Swift binding
  (Gap 1.5 obj-stomp). This is the robust fix independent of reference form.
- Keep the local-build `<NativeReference>` but mark it **not packed** and emit
  no native pack item — the companion is managed-only (Q4). Add
  `IncludeBuildOutput`/pack items only for the managed DLL + bgen
  `*.resources.zip`.

**(C) Packable Swift→companion dependency — no code change beyond (B).** Once the
companion is a packable csproj (`PackageId` + `PackageVersion`, both set by (B)),
the **existing bare** `<ProjectReference Include="Foo.ObjC.iOS.csproj" />` is
auto-promoted by `dotnet pack` into a real nuspec `<dependency id="Foo.ObjC.iOS"
version="X" />`. This is the verified Stripe/BlinkIDUX behavior
(`cross-package-nuspec-dependencies.md:20-27`). **Do not** add a conditional
`PackageReference` — both forms together is the NU5128 anti-pattern (`:50-54`),
and a pack-time prop can't retroactively flip the ref form because ItemGroup
conditions evaluate at project load. The only requirement is that the companion
`ProjectReference` survives into the pack graph (it does — `Exists()`-gated for
local; the companion csproj exists at pack time because the generator emitted it).

**(E) Automated companion pack.** The SDK is what runs for third-party
consumers, so the SDK pack flow must produce the companion nupkg. Add an SDK
target that, when `_SwiftBindingFrameworkType == Mixed`, invokes the companion
csproj's `Pack` target via `<MSBuild Projects="…ObjC.iOS.csproj" Targets="Pack">`,
staging the resulting `.nupkg` next to the Swift binding's. **Placement matters
(review #5):** run it *once*, before the Swift pack's nuspec/content collection —
**not** inside `_ConfigureSwiftBindingPack` (which is registered as per-TFM
content and would pack the companion repeatedly and race-stomp its output). The
nested Pack inherits the outer `$(PackageOutputPath)`; the companion's distinct
`<BaseIntermediateOutputPath>` (from (B)) keeps its `obj`/restore isolated. For
the repo's own release, `nuke pack` packs only infra packages today; per-binding
mixed packing lives in the SDK path + the gate. Document the order
(companion → Swift).

**(Gap 1.4) Companion native** — resolved by (B)+Gap 2: managed-only companion,
native loaded once from the Swift package.

### Gap 2 — wrapper as sole carrier for static-archive natives

Branch the wrapper link + the consumer native-reference **+ the pack items** on
the bound native's linkage type. The static native becomes a generate/
compile-time input only; the wrapper is the sole runtime carrier.

- **Detect** static-archive linkage of the bound native binary (reuse the
  `file`-based check: not `"dynamically linked shared library"` ⇒ static, with
  the `ar` `!<arch>` magic confirmation `IsLinkableFrameworkBinary` already
  uses). Add a small linkage probe (shared helper) usable from the wrapper
  compiler, the resolver, and metadata emission.
- **Static archive:** link the native into the wrapper with
  `-Xlinker -force_load -Xlinker <native-binary>` (the concrete slice binary
  path, *in addition to* the existing name-based `-framework` thunk link at
  `SwiftWrapperCompiler.cs:1414`, which only pulls *referenced* symbols — the
  force_load pulls the unreferenced ObjC classes the companion needs). The `-F`
  search path still applies. The wrapper dylib then *defines and exports* every
  `_OBJC_CLASS_$_*` (verified empirically: force-loading `FBLPromises.a` into a
  throwaway dylib exported its class symbols). Then **drop the source xcframework
  from the consumer at all sites** so the app executable embeds no second copy:
  - csproj `NativeReference`: `BindingProjectEmitter:439-443`
  - csproj source **pack** `None` item: `BindingProjectEmitter:455-459`
    (review #2 — packing must gate too, else the `.a` ships redundantly)
  - consumer buildTransitive + ProjectReference targets: `ConsumerTargetsEmitter:157-160`, PR-targets
  - SDK inject: `Sdk.targets:2261`
  - SDK `GetNativeManifest`: `Sdk.targets:2365`
  - SDK source pack: `Sdk.targets:2721`
  Net: one copy, in the wrapper; classes register once.
- **Static *dependency* archives (review #2 / Codex High 2):** apply the same
  policy to `SwiftFrameworkDependency` static natives. The wrapper already links
  transitive deps (`SwiftWrapperCompiler.cs:1431/1477`) — force_load the static
  ones, and drop the dep NativeReference/pack at `Sdk.targets:2280/2389`.
  Otherwise Firebase/GTMAppAuth graphs relocate the duplicate-class problem to a
  dependency instead of fixing it.
- **Dynamic native (and dynamic deps):** unchanged — one load command, one copy
  already; the source dylib must still ship + be referenced.
- **One signal, emitted to `binding-metadata.props`** (`_SwiftBindingSourceNativeLinkage`
  = `Static`|`Dynamic`, plus a per-dependency variant). The consumer-targets
  emitter, the csproj emitter, and every SDK site read this one value — single
  source of truth, avoiding the "exists-now vs will-be-produced" hazard. Thread
  it through `BindingProjectEmitterOptions` / `ConsumerTargetsEmitterOptions` and
  `BindingsGeneratorCommand`.
- **Verify** on simulator: regenerate a static mixed framework, build a consumer,
  confirm zero `implemented in both` warnings, that classes resolve, **and that
  `nm -gU` on the produced wrapper lists representative `_OBJC_CLASS_$_*`
  exports** (review #6). Device (NativeAOT) verification is hardware-gated (Q5).

  Risk note: `-force_load` of a large static archive bloats the wrapper with the
  full native. Correctness-first; it is exactly what linking the static
  framework into the app would cost anyway, just relocated to one image.
  Considered and rejected `-undefined dynamic_lookup` (heavy-handed, masks real
  link errors, App Store friction).

## Phase sequence (dependency order)

- **A — Gap 3 guard.** Self-contained, unit-testable, ships first. No ABI risk.
  Includes the `ObjCFrameworkResolution` binary-path extension + slice union +
  category-drop.
- **B — Companion versioning + obj isolation + managed-only.** Emitter-level.
  Makes the companion a packable csproj; the existing bare `ProjectReference`
  then auto-promotes to a nuspec `<dependency>` (former Phase C — no separate
  dual-form work).
- **D — Gap 2 static-native wrapper sole-carrier.** Wrapper force_load + the
  `_SwiftBindingSourceNativeLinkage` metadata signal gating all consumer ref
  **and pack** sites, for the primary source native *and* static deps.
- **E — Automated companion pack.** SDK pack target, run once before nuspec
  collection.
- **F — Mixed pack→consume gate.** Extend `PackGate`: BlinkID (Gap 1+3) and a
  static mixed framework (Gap 2 single-registration). Asserts: both nupkgs
  produced, Swift nuspec has a real companion `<dependency>`, no static `.a`
  packed into the Swift nupkg, `nm -gU` on the wrapper shows the ObjC class
  exports, single `PackageReference` consumer restores/compiles/links/runs (sim),
  ObjC types usable, no `implemented in both`.

Each phase ships with tests at the right layer (`nuke test` unit for
emitter/pipeline logic; `nuke binding-tests`/PackGate for end-to-end). Gap 2's
device run and Q5 are explicitly hardware-gated and called out, not silently
skipped.

## Layering / risk notes

- Generator → emitter → SDK targets → gate. Changes to `BindingProjectEmitter`
  and `ConsumerTargetsEmitter` are covered by their existing unit suites
  (71 + 45 facts) — keep them green and add cases.
- The native-linkage signal must thread the same value to (a) the wrapper
  compile, (b) the standalone csproj source NativeReference, and (c) the
  buildTransitive + ProjectReference targets. Single source of truth to avoid the
  "exists now vs will-be-produced" class of bug already noted in constraints.
- `EnableDefaultCompileItems=false` must stay scoped to the project (never on the
  command line) — applies to the companion csproj too.
