# Version coexistence policy

How the three shipped NuGet packages — `SwiftBindings.Runtime`, `SwiftBindings.Sdk`, and the
`SwiftBindings.Apple` supplement — relate across versions, why a generated binding pins a *bounded*
Runtime range, and the decision (pre-1.0) to keep that bounded-minor window rather than add a
cross-version ApiCompat baseline.

> The **contract of record** lives in code, on the `<remarks>` of
> `BindingsGeneration.RuntimeVersionRange` (`src/Swift.Bindings/src/Emitter/RuntimeVersionRange.cs`).
> This document is the prose half: it does not invent policy, it writes down what that `<remarks>`
> already encodes and records the ratified pre-1.0 decision. If the two ever disagree, the
> `<remarks>` wins and this file is the bug.

## The three packages

| Package | Role | Outbound `SwiftBindings.Runtime` range |
|---|---|---|
| `SwiftBindings.Runtime` | *is* the runtime (native + managed) | — |
| `SwiftBindings.Sdk` + every generated binding | consume the runtime directly | **bounded** `[X.Y.Z, X.(Y+1).0)` (`RuntimeVersionRange.Build`) |
| `SwiftBindings.Apple` supplement | always brokered by the SDK | **floor-only** `[A.B.C,)` (`RuntimeVersionRange.BuildMinimumOnly`) |

A generated binding and the SDK reference the Runtime *directly*, so they carry the bounded form.
The Apple supplement is only ever pulled in transitively through the SDK — whose own bounded range
supplies the ceiling — so the supplement only needs to declare a floor, which lets one shipped
supplement nupkg ride forward across Runtime/SDK minor bumps without a no-op repack.

## Why bounded-minor, not minimum-only

NuGet interprets a plain `Version="X.Y.Z"` as a **minimum-only** float: it will happily resolve a
future `X.(Y+1).0` that is cached locally. Our compatibility promise is **patch is ABI-additive
only** — no struct-layout, P/Invoke-signature, calling-convention, or public-API removal/change —
while **a minor is allowed to break ABI**. So:

- The bounded range `[X.Y.Z, X.(Y+1).0)` floats forward across Runtime **patch** releases (an
  ABI-compatible bug fix reaches every consumer without a matrix republish)…
- …but slams shut at the next **minor**, so a future ABI/struct-layout/P-Invoke break cannot
  silently load under an older binding and crash.

### Consumer-visible consequence (intended)

Two bindings built one Runtime-minor apart are mutually uninstallable in a single project — NuGet
reports `NU1107`. That is a **real fracture boundary**, but it is intended protection: a hard restore
error is strictly better than silently loading an ABI-incompatible runtime and crashing at a
P/Invoke. The practical mitigation is process, not policy: **keep Runtime-minor bumps rare and batch
ABI breaks into them.**

## Enforcement seam

`EnablePackageValidation` on the Runtime and Apple csprojs runs NuGet's offline
compatible-framework / compatible-RID validators at pack time. The range above is therefore no
longer the only thing standing behind the rule.

## Decision (ratified, pre-1.0): keep the window, do not add a baseline

NuGet's cross-version ApiCompat check (`PackageValidationBaselineVersion`) would diff each new pack
against a previously published baseline package. **We deliberately do not enable it before 1.0:**

- Setting a baseline version forces NuGet to `PackageDownload` the baseline at pack time, which
  **breaks offline and `-dryrun` packing** — a hard regression for local builds and the release
  dry-run lane.
- Pre-1.0, the bounded-minor window already delivers the safety the baseline would: an ABI break
  lands in a minor, and the bounded range makes that minor uninstallable alongside older bindings.
  The baseline would add cost (offline breakage) without changing the coexistence guarantee.

**Revisit at 1.0.** Once ABI-stability promises tighten (a 1.x line that pledges no ABI break within
a major), the tradeoff inverts: a baseline ApiCompat gate becomes the right tool to *prove* the
no-break pledge, and the minor window may widen. That is a 1.0 decision, not a pre-1.0 one.

## Related

- `RuntimeVersionRange.cs` `<remarks>` — the code-of-record this doc mirrors.
- Apple-supplement decoupling: the supplement path is floor-only by design; the bounded `Build()`
  form stays minor-up and must not be widened.
