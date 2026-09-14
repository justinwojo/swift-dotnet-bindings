# Version compatibility

Current behavior checked 2026-09-13. Three independent checks protect consumers: NuGet dependency
ranges at restore, the generated-binding handshake at load, and managed API compatibility at pack.
This document explains those checks; it does not change the versioning policy.

## Restore: bounded Runtime dependencies

[RuntimeVersionRange](../../Swift.Bindings/src/Emitter/RuntimeVersionRange.cs) computes the ranges
used by the generator and the SDK's stamped properties:

| Consumer | Runtime range | Purpose |
|---|---|---|
| SDK-driven projects and generated bindings | `[X.Y.Z, X.(Y+1).0)` | Allow compatible patches, reject the next Runtime minor |
| Apple supplement | `[A.B.C,)` | Declare a floor; the SDK-driven consumer supplies the bounded ceiling |

Patch releases must remain ABI-additive; a minor boundary permits deliberate compatibility breaks.
A plain package version is a minimum constraint, so it would not prevent NuGet from choosing a
future incompatible minor. The explicit ceiling provides that protection. Two bindings requiring
different Runtime minors can therefore produce `NU1107` in one project. Regenerate/rebuild/repack
bindings against a common Runtime minor rather than widening their dependency ranges to hide the
conflict. Keep minor breaks deliberate and grouped.

The Apple supplement has its own release version and can span Runtime/SDK minor releases without a
no-op repack. Its floor-only dependency relies on the bounded range supplied by the supported
SDK-driven consumption path; it is not permission to widen direct generated-binding dependencies.

## Load: the runtime contract epoch

[RuntimeContract](../../Swift.Runtime/src/Swift/Runtime/RuntimeContract.cs) checks the epoch embedded
in a generated binding's module initializer. The epoch derives from `major * 1000 + minor`. Normal
packaged bindings are accepted only within `[MinimumSupportedGeneratedVersion, Version]`; epoch 0
is the development/in-tree bypass and is not packaged compatibility evidence.

The current floor is **16**. Raise it only for a real module-initializer/runtime dispatch-contract
break; additive work leaves it unchanged. The floor is a release decision, not automatically raised
on every minor. The load check is narrower than the NuGet policy: manually bypassing a dependency
range may allow a binding to load while retaining old generated calls. Passing this handshake does
not prove every native ABI interaction is safe.

The 0.20.0 release pack was checked with epoch **20**, floor **16**, generated/SDK Runtime range
`[0.20.0,0.21.0)`, and Apple-supplement Runtime floor `[0.20.0,)`. A 0.19.x binding must be
regenerated, rebuilt, and repacked for 0.20.0; manually bypassing its old range can leave pre-0.20
generated routes in place even when the load-time floor accepts the assembly.

## Pack: public API compatibility

Since `82ef2b438`, [nuke pack](../../../build/Build.Pack.cs) enables a cross-version ApiCompat check
for **SwiftBindings.Runtime** by passing `PackageValidationBaselineVersion`. The
[baseline selector](../../../build/Build.ApiCompat.cs) uses the highest stable `sdk-v*` tag below the
requested version, or an explicit `--api-compat-baseline`. Missing baseline selection fails the pack;
stale validation stamps are removed so a previous pack cannot suppress the check.

Removed or reshaped public members fail, including adding an optional parameter that replaces a
method's binary signature. A minor version label alone does not bypass the check. An intended
breaking release needs an explicit compatibility decision and baseline treatment.

Baseline acquisition can require downloading the previous nupkg. `--skip-api-compat` is the explicit,
warned escape hatch for local packs that ship nowhere; it is not release validation. Dry-run release
packing does not inherently skip the check.

Both Runtime and Apple also enable compatible-framework/RID package validation. The current Nuke
pack supplies a cross-version baseline to Runtime, **not Apple**. ApiCompat checks managed public
API compatibility; it does not prove native calling conventions, layout or ownership. Those still
need the applicable runtime and packaging gates.

## Future policy decisions

The bounded-minor policy remains in force. A looser coexistence model and a future 1.x compatibility
promise remain owner decisions; the existing Runtime ApiCompat gate does not settle either one.
See the [signed 1.0 decision record](../1.0-decision-record.md) and the
[remaining coexistence question](../Future/notes/tooling.md#version-coexistence-end-state-apicompat-baseline-minor-window-scheme).

Older prose deferred ApiCompat together with the range decision. That description is superseded:
the Runtime pack check shipped while the bounded range stayed unchanged. Keep descriptions aligned
with the range functions, runtime handshake and pack implementation rather than treating any one
comment as authority for all three mechanisms.
