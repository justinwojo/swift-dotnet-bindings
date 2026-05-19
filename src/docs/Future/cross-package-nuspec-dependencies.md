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

## Proper fix (SDK level)

The SDK's Apple-framework pack/inject path should detect cross-module references in generated C# (the same behavior-derived detection `InjectProjectRefs` already does for third-party multi-product libraries) and emit a `<PackageReference Include="SwiftBindings.Apple.<Framework>" Version="<thisAppleVersion>" />` for every cross-module reference detected. Apple framework versions are uniform within a release wave, so the version is the package's own `<Version>`.

Third-party multi-product libraries need no SDK change — the existing `<ProjectReference>` auto-emit already handles them.

This removes the manual-bookkeeping burden and prevents the latent ship-broken state we hit on RealityKit 26.2.1.

## Action items

- **Republish `SwiftBindings.Apple.RealityKit` 26.2.3** with the explicit `PackageReference Include="SwiftBindings.Apple.RealityFoundation"` (workaround already in source as of 2026-05-18; ships once the RealityFoundation 26.2.3 publish completes and RealityKit gets re-tagged).
- **Audit every Apple framework binding** under `apple-frameworks/` for cross-module references in generated C# and add explicit `PackageReference` items where needed. Candidates: anything whose generated `obj/.../swift-binding/*.cs` mentions another `SwiftBindings.Apple.*` module namespace in its public API. (Same detection logic `InjectProjectRefs` already runs.)
- **SDK fix**: extend the Apple-framework pack/inject path so cross-module references propagate to nuspec automatically. Likely a sibling of `InjectProjectRefs` that emits `PackageReference` items keyed on detected module references, scoped to the Apple-framework SDK target.
