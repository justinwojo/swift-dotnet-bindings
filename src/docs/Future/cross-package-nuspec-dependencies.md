# Cross-Package nuspec Dependencies for Apple Framework Bindings

## Problem

When an Apple framework binding (`SwiftBindings.Apple.<Framework>`) cross-references types from a sibling Apple framework binding, the generated C# emits direct references into the other module's namespace — e.g. `MatterSupport` generates `Matter.MTRSetupPayload`, and `RealityKit` generates `RealityFoundation.Entity`. These references compile locally because MSBuild resolves the sibling assembly through the same `obj/` outputs the test harness produces, but **the produced `.nupkg`'s nuspec does not declare a NuGet dependency on the cross-referenced package**.

That means a downstream consumer who installs only `SwiftBindings.Apple.MatterSupport` (or `SwiftBindings.Apple.RealityKit`) ends up with a binding that won't load at runtime, because `SwiftBindings.Apple.Matter` (or `SwiftBindings.Apple.RealityFoundation`) is not pulled in transitively.

## Confirmed instances (2026-05-18)

| Package | Missing transitive dep | Status |
| --- | --- | --- |
| `SwiftBindings.Apple.MatterSupport` 26.2.3 | `SwiftBindings.Apple.Matter` | Worked around per-package — see "Workaround" below |
| `SwiftBindings.Apple.RealityKit` 26.2.1 (published) | `SwiftBindings.Apple.RealityFoundation` | **Latent** — published nuget today is broken for consumers. Workaround applied in source 2026-05-18; ships on 26.2.3 republish |

Any other binding that takes a cross-module type as a public input/output will hit this. The SDK currently produces these references freely (it's expected for cross-module Apple framework interop — see the `wrapperImportable` design for Matter/MatterSupport), but the Apple-framework pack path doesn't propagate them as NuGet deps.

## Scope clarification: this is Apple-framework-specific

**Third-party multi-product packages (Stripe, BlinkIDUX, etc.) already work correctly.** Verified empirically 2026-05-18 against live nuget.org:

- `libraries/Stripe/StripePaymentSheet/SwiftBindings.Stripe.PaymentSheet.csproj` has only `<ProjectReference>` entries to `SwiftBindings.Stripe.Core` and `SwiftBindings.Stripe.Payments` (no `<PackageReference>`).
- The shipped `SwiftBindings.Stripe.PaymentSheet 25.11.0` nuspec on nuget.org contains the matching `<dependency id="SwiftBindings.Stripe.Core" version="25.11.0" .../>` and `<dependency id="SwiftBindings.Stripe.Payments" version="25.11.0" .../>` entries.

`dotnet pack` **does** auto-emit `<dependency>` entries from `<ProjectReference>` items as long as the target is a packable csproj (both `<PackageId>` and `<Version>` set). The `BuildLibrary` flow's `InjectProjectRefs` already populates `<ProjectReference>` for behavior-derived cross-module deps in third-party multi-product libraries — and because the targets are packable, the nuspec captures them automatically. No `<PackageReference>` is needed there. The BlinkID → BlinkIDUX case works the same way.

The Apple-framework path is different because the package's primary "thing" is a `<SwiftAppleFrameworkTarget>` item, not a `<ProjectReference>`. The pack output has no project reference for `dotnet pack` to auto-emit from. Confirmed empirically: `SwiftBindings.Apple.Matter 26.2.3` nuspec on nuget.org ships with empty `<dependencies>` groups even though the binding's wrapperImportable path needs it for cross-module use. The earlier framing of "ProjectReference does not propagate" was wrong — the correct framing is "the Apple-framework pack path produces no ProjectReference for pack to propagate from."

## Why local builds hide it

The Nuke harness builds and sim-tests every package against the local `bin/` outputs of every other package. Cross-module assembly resolution happens through MSBuild's project graph and the `InjectFrameworkDeps` / `InjectProjectRefs` injection pass, not through the nuspec. So local + CI sim validation all pass — the gap only surfaces when a consumer pulls one package from nuget.org in isolation.

## Workaround applied for 0.11.0 / Apple 26.2.3 wave

Hand-add an explicit `<PackageReference>` to the cross-referenced package alongside the `SwiftAppleFrameworkTarget` item:

```xml
<ItemGroup>
  <PackageReference Include="SwiftBindings.Apple.Matter" Version="26.2.3" />
</ItemGroup>
```

This gets captured in the nuspec `<dependencies>` per TFM at pack time and propagates correctly to consumers.

Applied 2026-05-18 to:

- `apple-frameworks/MatterSupport/SwiftBindings.Apple.MatterSupport.csproj` → `SwiftBindings.Apple.Matter` 26.2.3
- `apple-frameworks/RealityKit/SwiftBindings.Apple.RealityKit.csproj` → `SwiftBindings.Apple.RealityFoundation` 26.2.3

### Anti-pattern: don't add both `<PackageReference>` and `<ProjectReference>` to the same sibling

For packages that already have a `<ProjectReference>` to a packable sibling csproj (third-party multi-product libraries), adding an explicit `<PackageReference>` on top would emit a **duplicate** `<dependency>` entry in the nuspec — either an NU5128 error or a silent dedup, neither of which is desirable. The auto-emitted dependency from the `ProjectReference` already does the job. This is why the BlinkIDUX → BlinkID PackageReference was reverted in commit `438e992` after being briefly applied in `ae9aaee`.

The rule: workaround applies **only** to packages whose primary build mechanism is `<SwiftAppleFrameworkTarget>` (i.e. the Apple-framework pack path) and which have a cross-module reference in their generated C#. For everything else, ProjectReference alone is correct.

## Proper fix (SDK level) — LANDED

The SDK now auto-detects cross-module references and propagates them to the nuspec, so
the manual `<PackageReference>` workaround is no longer required for new builds.

- **Detector:** `AppleFrameworkImportDetector.Detect()` parses the framework's
  `.swiftinterface` imports; the generator exposes it via the
  `--detect-apple-cross-module-deps <interface> --apple-version <v>` subcommand, which
  prints `MODULE|PACKAGE_ID|VERSION_RANGE` lines.
- **Injection:** `_DetectAppleFrameworkCrossModuleDeps`
  (`Sdk.targets`, `BeforeTargets="ResolveProjectReferences;CollectPackageReferences"`)
  shells that subcommand and injects a bounded **`PackageReference`** for each detected
  module — *always* a PackageReference, never a ProjectReference, regardless of whether the
  conventional in-tree sibling csproj exists. This is a real distinction from the authored
  third-party case above: an **authored** `<ProjectReference>` (present in the csproj at restore
  time, like Stripe's) lands in `project.assets.json` and so *does* propagate to the nuspec; a
  **build-time-injected** `<ProjectReference>` (added by this target, which runs *after* restore)
  never enters the restored assets graph that `dotnet pack` reads
  (`_GetProjectReferenceVersions` → `GetProjectReferencesFromAssetsFileTask`), so it would
  produce an *empty* nuspec `<dependencies>` group and a `DllNotFound` at the consumer.
  `CollectPackageReferences` *is* part of restore, so an injected PackageReference flows into the
  graph and materializes as a per-TFM `<dependency>`. The mono-repo pre-packs sibling Apple
  frameworks into its local feed before building dependents, so the injected PackageReference
  restores in-tree too; a genuinely unavailable sibling fails **loud** (NU1101) instead of
  silently shipping a broken package. (A bounded version *range* is also something a
  ProjectReference cannot express in the first place.)
- **Version:** Apple framework versions are uniform within a release wave, so the injected
  version range is bounded by the package's own Apple supplement version
  (e.g. `[26.2.1,26.3.0)`).

Third-party multi-product libraries need no SDK change — the existing `<ProjectReference>`
auto-emit already handles them.

If a user instead *authors* a `<ProjectReference>` from one Apple-framework binding to a sibling
Apple binding, the injection dedups against it (no double-declare) but that authored
ProjectReference packs as an *unbounded minimum* (`version="26.2.1"`), not the bounded train
range — a latent cross-train hazard. The SDK emits **`SWIFTBIND044`** (warning, not error) pointing
the maintainer at a bounded `<PackageReference>`; it is skipped when the sibling is *also* a
PackageReference (the range is pinned there). This is the one case where an Apple-sibling
ProjectReference is tolerated-but-discouraged rather than auto-corrected — a ProjectReference
cannot carry a version *range* at all.

This removes the manual-bookkeeping burden and prevents the latent ship-broken state we hit
on RealityKit 26.2.1.

### Test coverage

- `AppleFrameworkCrossModuleDepsInjectionTests.cs` — `InjectedCrossModuleDep_MaterializesAsNuspecDependency`
  packs a RealityKit-shaped Apple-framework fixture through the real `Sdk.targets` and asserts
  the injected `<dependency id="SwiftBindings.Apple.RealityFoundation" version="[26.2.1,26.3.0)">`
  actually appears in the produced nuspec (Tier-1 closure parity with the Apple supplement's
  `AssertSupplementBoundsRuntimeRange` PackGate check).
- `InTreeSiblingPresent_StillInjectsBoundedPackageReferenceDependency` is the regression guard
  for the always-PackageReference crux: it plants the conventional in-tree sibling csproj *and* a
  restorable stub package, packs the fixture, and asserts the bounded `<dependency>` still
  materializes — proving the presence of an in-tree csproj no longer diverts injection to a
  non-propagating ProjectReference (which would silently empty the dependency group).
- `RestoreOnly_InjectedCrossModuleDep_EntersRestoreGraph` proves the load-bearing precondition
  directly: a plain `dotnet restore` (no build, no pack) writes the bounded dep into
  `project.assets.json`. The nuspec test above proves materialization transitively through a full
  pack; this proves restore-phase visibility on its own, closing the "restore-then-`pack
  --no-restore` ships an empty deps group" hazard.
- `AuthoredProjectReferenceSibling_SuppressesInjection_AndWarnsSWIFTBIND044` plants an authored
  sibling ProjectReference and asserts the bounded auto-PackageReference is suppressed *and*
  `SWIFTBIND044` fires; `AuthoredProjectReferenceSibling_AlsoPinnedAsPackageReference_NoWarning`
  asserts the warning is skipped when a PackageReference already pins the range.
- `NoDepsDetected_NoInjection` guards the negative case.

## Remaining (packages-repo, owner-driven — not SDK work)

The SDK fix above makes these automatic for any *re-pack*. The two already-shipped packages
still carry the manual `<PackageReference>` workaround until their next republish, which is a
`swift-dotnet-packages` operation (not tracked here):

- Republish `SwiftBindings.Apple.RealityKit` (the manual workaround in source since 2026-05-18
  still ships correctly; the SDK fix makes the manual line redundant but harmless once re-packed
  against an SDK with the detector).
- One-time audit of `apple-frameworks/` for any cross-module reference that predates the
  detector. New builds need no audit — the detector covers them.
