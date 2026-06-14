# Version coexistence policy

Status: **active contract** for the patch lane; the minor-window end-state is an
**open owner decision** (see *Deferred decision* below).

This document is the written half of the fix for the version-skew finding: the
patch-additivity rule that the code relies on used to live only as a `<remarks>`
comment on `RuntimeVersionRange.Build` with nothing enforcing it. It records what
the version ranges mean, what each lane promises, and what is intentionally left
to a future owner decision.

## The three packages and how their versions relate

| Package | Versioned by | Outbound Runtime dependency range |
|---|---|---|
| `SwiftBindings.Runtime` | SDK lane (`sdk-X.Y.Z`) | — (it *is* the runtime) |
| `SwiftBindings.Sdk` | SDK lane (`sdk-X.Y.Z`) | bounded `[X.Y.Z, X.(Y+1).0)` via `Sdk.props` `SwiftRuntimePackageVersionRange` |
| `SwiftBindings.Apple` | Apple lane (`apple-A.B.C`) | floor-only `[A.B.C,)` via `RuntimeVersionRange.BuildMinimumOnly` |
| *generated binding* | the consuming author | bounded `[X.Y.Z, X.(Y+1).0)` (emitted by the generator from the SDK it was built with) |

`RuntimeVersionRange` is the single source of truth for both bounded ranges
(`Build`) and the supplement's floor-only range (`BuildMinimumOnly`); it is
link-compiled into the Nuke build so the generator-emitted range and the
SDK-stamped range cannot drift.

## What each range promises

- **Patch (`X.Y.Z` → `X.Y.(Z+1)`)** is **ABI-additive only**: no struct-layout,
  P/Invoke-signature, calling-convention, or public-API removal/change. The
  bounded range floats forward across patches so an ABI-compatible bug fix reaches
  every already-shipped binding's consumer **without a matrix republish**.
- **Minor (`X.Y.*` → `X.(Y+1).0`)** is allowed to break ABI. The bounded range
  *slams shut* at the next minor precisely so a future-incompatible minor cannot
  be silently resolved into an old binding's consumer (NuGet would otherwise
  treat a plain `Version="X.Y.Z"` as a minimum-only float and happily resolve a
  locally-cached `X.(Y+1).0`).

### The consequence the consumer feels

Because each generated binding pins a bounded `[X.Y.Z, X.(Y+1).0)` Runtime range,
**two bindings built against SDKs one Runtime-minor apart are mutually
uninstallable in the same project** — NuGet reports `NU1107` (version conflict),
which the consumer cannot resolve themselves. This is *intended* protection (it is
strictly better than silently loading an ABI-incompatible runtime and crashing),
but it is a real fracture boundary and it is why the minor cadence is a deliberate,
infrequent event, not a routine bump. Keep Runtime-minor bumps rare and batch ABI
breaks into them.

### Why the Apple supplement is floor-only

`SwiftBindings.Apple` declares only a floor (`[A.B.C,)`) on Runtime. That is safe
**only because the supplement is always brokered by `SwiftBindings.Sdk`**, whose
own bounded Runtime `PackageReference` supplies the actual compatibility ceiling.
A floor-only supplement consumed *without* the SDK has no ceiling and could float
onto an incompatible Runtime minor — an unsupported configuration. The supplement
is additionally **cross-major additive-only** (its own surface only grows), so one
shipped supplement nupkg can ride forward across Runtime/SDK minor bumps without a
no-op repack.

## Enforcement

- **`EnablePackageValidation=true`** on `SwiftBindings.Runtime` and
  `SwiftBindings.Apple` runs NuGet's offline compatible-framework / compatible-RID
  validators over each produced nupkg at pack time, catching a TFM/RID asset that
  would leave a consumer without a compatible compile or runtime asset. This needs
  no network and is safe for offline and `release/**-dryrun.N` packs.
- **The packed-consumer-topology PackGate** asserts the delivery layout that makes
  the ranges meaningful (descriptors land in `buildTransitive/`, the consumer
  `.targets` roots its own sibling, assembly-name/descriptor agreement).

## Deferred decision (project owner)

Two coupled choices are intentionally **not** made in code yet:

1. **`PackageValidationBaselineVersion`** — the cross-version ApiCompat check that
   would fail a *non-additive patch* against the last shipped version. It is not
   set because (a) a baseline forces NuGet to `PackageDownload` the baseline nupkg,
   which breaks offline and dry-run packs, and (b) pinning the baseline commits to
   a specific "what may change across versions" stance.
2. **The minor-window scheme end-state** — whether to keep the current
   bounded-minor-window scheme (with its `NU1107` fracture boundaries) or move to a
   wider/looser coexistence model.

These are coupled: choosing the window scheme determines what the validation
baseline should assert. They are reserved for an explicit owner decision. When that
decision lands, set `PackageValidationBaselineVersion` to the agreed baseline — the
`EnablePackageValidation` seam is already in place for it to plug into — and update
this document with the chosen scheme.
