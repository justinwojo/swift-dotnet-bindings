# Packaging & Project-Reference Gaps Worth Closing

Scope: the NuGet packaging and consumer-wiring surface — how a generated binding
is packed into a `.nupkg`, how cross-package dependencies flow, and how consumers
reference it (`PackageReference`, SDK-direct, `ProjectReference`). This is the
forward-looking gap list for that theme; it deliberately excludes generator/proxy
correctness work tracked in `roadmap.md` and `apple-framework-deferred-work.md`.

## Already closed (context)

The mixed (ObjC+Swift) packaging story is now complete and gated:

- **Single-package contract** — one mixed xcframework → one `.nupkg` with the ObjC
  companion assembly embedded in `lib/<tfm>/`, never a separate package or nuspec
  `<dependency>`. Gated structurally by the `mixed/static`, `mixed/dynamic`, and
  `mixed/multi-tfm` PackGate legs (`build/Build.PackGate.MixedFixture.cs`).
- **Multi-TFM mixed bindings** — one csproj with `<TargetFrameworks>` (the
  `SwiftBindings.Nuke` shape) packs each platform's companion under its own
  `lib/<tfm>/` slice. Confirmed working and locked in by `mixed/multi-tfm`.
- **Three consumption paths fail closed** — `SWIFTBIND040/041/042` guards
  (`Sdk.targets:2442`, `ConsumerTargetsEmitter.cs:364`) make a missing companion or
  native carrier a loud build error on all three paths instead of a silent CS0246 /
  runtime `DllNotFound`.
- **Runtime gates** — `--mixed-pack` (path a: iOS Simulator + physical device) and
  `--mixed-direct` (path b: iOS Simulator) prove the binding links, round-trips the
  ObjC type, and registers the class exactly once.

The gaps below are what remains.

---

## Tier 1 — real consumer impact

### 1. Cross-package nuspec dependencies don't auto-propagate (Apple-framework path)

A consumer installing a single Apple-framework binding (e.g.
`SwiftBindings.Apple.MatterSupport` or `...RealityKit`) can get a package that fails
to load at runtime because a sibling `SwiftBindings.Apple.*` package it depends on is
not pulled transitively — the Apple-framework pack path doesn't emit those references
as NuGet `<dependency>` items.

- **Where:** `src/docs/Future/cross-package-nuspec-dependencies.md:66-68` (three open
  action items: republish RealityKit 26.2.3, audit all `apple-frameworks/` bindings
  for cross-module refs, and the SDK fix).
- **Partial mitigation today:** `SwiftFrameworkDependency`-declared deps are guarded at
  pack time (`Sdk.targets:2615`), but auto-detected cross-module references in
  generated C# are not.
- **Worth closing because:** it's the only open item here that produces a *runtime
  load failure in a shipped package*, and it scales with the Apple-framework catalog.
- **Shape of fix:** a sibling of `InjectProjectRefs` that detects `SwiftBindings.Apple.*`
  module references in the generated public API and emits matching `PackageReference`
  items into the nuspec, scoped to the Apple-framework SDK target.

---

## Tier 2 — coverage gaps (mechanism works, but unproven on a platform/path)

### 2. No mixed-framework runtime coverage on tvOS or Mac Catalyst

`--mixed-pack` is iOS-only and `--mixed-direct` is iOS-sim-only; both explicitly
ignore `--tvos`/`--catalyst` (`build/Build.BindingTests.cs:861,879`). The
`mixed/multi-tfm` PackGate leg covers `net10.0-ios;net10.0-macos` only. So a mixed
binding is never exercised on tvOS or Mac Catalyst — neither structurally nor at
runtime.

- **Worth closing because:** the duplicate-ObjC-class-registration hazard that mixed
  packaging defends against is linkage-keyed; Catalyst (UIKit-on-mac) and tvOS have
  distinct linkers/runtimes from the proven iOS/macOS pair.
- **Caveats:** Mac Catalyst x64 has separate upstream Mono-JIT instability
  (`Future/upstream-issue-04-mono-catalyst-x64-instability.md`); the tvOS device runner
  itself is an open item (`roadmap.md` low-priority — no `nuke runtime-tests-tvos-device`
  target yet). Catalyst-arm64 + tvOS-Simulator are the reachable first targets.
- **Shape of fix:** extend the `mixed/multi-tfm` PackGate `<TargetFrameworks>` to add
  `net10.0-maccatalyst`/`net10.0-tvos` slices (structural, cheap), then a Catalyst/tvOS
  runtime leg if a real consumer needs it.

### 3. Local `ProjectReference` (path c) has no dedicated iOS runtime leg

Path c is covered by unit tests only (`ConsumerTargetsEmitterTests` assert the emitted
`.ProjectReference.targets` injects the companion `<Reference>` and the `SWIFTBIND042`
guard). It shares path b's `_BuildMixedObjCCompanion` build path and the same
plain-`<Reference>` surfacing mechanism, so it is covered *by proxy* via
`--mixed-direct`.

- **Where:** described in `CLAUDE.md` ("Coverage by path … c → unit tests only").
- **Status:** acknowledged design decision, defensible — not a known bug.
- **Worth closing only if:** an independent iOS-runtime exercise of path c is ever
  deemed necessary (e.g. the build path diverges from path b). Low priority while the
  mechanisms remain shared.

### 4. PackGate doesn't exercise per-RID native asset selection

PackGate asserts the on-disk slice *layout* a consumer would resolve from, but a
no-RID `dotnet build` doesn't drive `_ExpandNativeReferences` per RID — true coverage
needs an app consumer published with each `RuntimeIdentifier`.

- **Where:** `build/Build.PackGate.cs:362-363` and `:725` (both marked "deferred as a
  followup").
- **Shape of fix:** add an iOS app consumer leg published with `-r ios-arm64`
  (and an osx RID) that asserts the correct slice is resolved, not just present.

---

## Tier 3 — deferred until a triggering case

### 5. SwiftPackage / SPM-direct integration is a hard-fail stub

`<SwiftPackage>` items error out: *"SwiftPackage items are not yet supported. Use
SwiftFramework items with a pre-built xcframework."*

- **Where:** `src/Swift.Bindings.Sdk/Sdk/Sdk.targets:76,82-84` (labeled "v2 stub").
- **Workaround today:** `spm-to-xcframework` produces the xcframework consumers feed in
  via `<SwiftFramework>`. Closing this would fold that step into the SDK.

### 6. Private / bundled framework dependencies (`BundleInPackage`)

Bundling a vendored internal framework into the package rather than requiring a
separate `PackageReference`.

- **Where:** `src/docs/Future/private-framework-dependencies-plan.md` (full plan,
  "status: deferred 2026-05-02").
- **Trigger to revisit:** a second/third vendor with the same internal-framework graph
  (Firebase, Facebook SDK, etc.).

### 7. Multi-framework library first-build ordering

When binding a multi-module library, wrapper compilation can fail on the first build if
dependency projects haven't been built yet. Handled today with a `ContinueOnError`
warning + wiki hint, not a structural ordering fix (`Sdk.targets`, multi-framework
build hint).

- **Worth closing because:** it's a first-run papercut for multi-framework consumers; a
  proper inter-project build-ordering dependency would remove the retry.

---

## Out of scope here (tracked elsewhere)

Generator/proxy correctness items that surface *through* packaging but aren't packaging
fixes: witness-getter `EntryPointNotFound`→`NotSupported` second shape (T2.5) and
sibling emission-marker name-keying hardening (T2.6) in `apple-framework-deferred-work.md`;
cross-module/cross-assembly conformer enumeration in `roadmap.md`.
