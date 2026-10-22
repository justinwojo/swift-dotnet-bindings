# .NET 11 / Xcode 27 compatibility qualification — 2026-10-22

This is local preview qualification, not a release support claim. The legacy
.NET 10 / Xcode 26 profile remains the default. No net11 runtime baseline has
been promoted; full net11 runs still require `--allow-unqualified-runtime`.

## Toolchains and final-source checks

- Preview: SDK `11.0.100-rc.2.26504.105`, workload
  `11.0.100-rc.2.26509.2`, Apple workload `27.0.12212-net11-rc.2`, Xcode 27.0
  (`27A266a`). CoreCLR runtime `11.0.0-rc.2.26504.105`.
- Legacy: SDK/workload `10.0.107`, Xcode 26.3 (`17C529`).
- Physical device: iPhone 13, iOS 26.6 (`23G71`).

| Gate | Passed | Skipped | Failed / crashed |
| --- | ---: | ---: | ---: |
| .NET 11 CoreCLR, device Release | 4,285 | 27 | 0 / 0 |
| .NET 11 NativeAOT, device Release | 4,284 | 28 | 0 / 0 |
| .NET 10, iOS simulator | 4,281 | 31 | 0 / 0 |
| Generator unit tests under net11 profile | 19,700 | 36 | 0 |
| Analyzer tests | 79 | 0 | 0 |
| Runtime unit tests | 959 | 1 | 0 |
| Toolchain profile tests | 20 | 0 | 0 |

The legacy strict compile gate passed, as did 69 withdrawal assertions. Of the
36 generator skips, 34 require generated fixtures absent from that unit run.
The four final runtime/compile source inventories matched the pending source
byte-for-byte before this documentation update, with inventory fingerprint
`2e742bfdee7675487b2f67b3831638b58df29a5274a730aca64f086eb1cf7136`.

Earlier in the same batch, the net11 CoreCLR Release mixed NuGet consumer passed
on simulator and device, SDK-direct passed on simulator, App Store hygiene
passed all eight Mac legs plus signed IPA/package structure, and the Apple
NativeAOT package consumer materialized two `Locale.Language` values using only
package-owned roots. These package checks preceded the final narrow metadata
name qualification fix; they are not presented as exact-final-source reruns.

Full local logs and source inventories are retained under ignored
`artifacts/qualification/`; the compact result is committed here so checkout
history retains the qualification boundary without machine-specific build output.

## Open qualification boundaries

- Newly signed .NET 10 MonoAOT and NativeAOT device runs are in progress.
- CoreCLR Debug is not qualified: standalone BCL-only repros demonstrate three
  interpreter ABI defects involving SwiftSelf, forward SwiftError and reverse
  SwiftError. Release controls pass. Upstream issue drafts are prepared but unfiled.
- Selectively precompiling a separate interop assembly passes all 12 standalone
  assertions. This is an experiment, not a shipped consumer policy or proof of
  debugger attachment. SDK-direct bindings share the application assembly and
  cannot use assembly-level selection while keeping that assembly interpreted.
- Final release gates, reviewed net11 baselines, and CI qualification remain.

Standalone sources and evidence live in the separate local `swift-interop-repro`
repository (`Net11Interpreter`, `LegacyCoreClr`, and `DebugPackaging`). They are
not part of this source commit.
