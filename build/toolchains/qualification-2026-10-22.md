# .NET 11 / Xcode 27 compatibility qualification — 2026-10-22

This is local preview qualification, not a release support claim. The legacy
.NET 10 / Xcode 26 profile remains the default. No net11 runtime baseline has
been promoted; full net11 runs still require `--allow-unqualified-runtime`.

## Toolchains

- Preview: SDK `11.0.100-rc.2.26504.105`, workload
  `11.0.100-rc.2.26509.2`, Apple workload `27.0.12212-net11-rc.2`, Xcode 27.0
  (`27A266a`). CoreCLR runtime `11.0.0-rc.2.26504.105`.
- Legacy: SDK/workload `10.0.107`, Xcode 26.3 (`17C529`), runtime 10.0.7.
- Physical device: iPhone 13, iOS 26.6 (`23G71`).

## Equality bridge follow-up

`SwiftEquatable.Equals<T>` now crosses a C ABI with four pointer arguments and
an unsigned byte result. Swift reconstructs the concrete Equatable metatype
from metadata and its witness table, opens it, and performs generic dispatch.
The managed public API and operand ownership contract are unchanged. All six
native framework slices were rebuilt with Xcode 26.3; the new entry is present
in all ten architecture/slice pairs and strict signatures verify.

Changing only the former Swift import's bool return to byte was insufficient:
the new end-to-end tests still crashed Mono and interpreted CoreCLR, including
a generic method that remained interpreted in a Release app. The C ABI helper
removes that hidden Swift argument boundary. This is a product fix, not an
additional upstream issue or a runtime skip.

Three permanent BindingTests cover equal/unequal Swift.Int values, high-word
and sign-extension differences, and repeated equality of a large value carrying
a heap String and tracked ARC reference. The latter proves original values
survive, all three native sentinels stay alive until disposal, and all are
released exactly once. The unit carrier also now stores all eight Swift.Int
bytes instead of writing four bytes into an eight-byte metadata contract.

| Gate after the bridge fix | Passed | Skipped | Failed / crashed |
| --- | ---: | ---: | ---: |
| .NET 10 iOS simulator | 4,284 | 31 | 0 / 0 |
| .NET 10 NativeAOT device | 4,287 | 28 | 0 / 0 |
| .NET 10 Mono full-AOT device | 4,284 | 31 | 0 / 0 |
| .NET 11 CoreCLR Release iOS simulator | 4,288 | 27 | 0 / 0 |
| .NET 11 default Debug, focused equality | 3 | 0 | 0 / 0 |
| .NET 11 CoreCLR Release device | 4,288 | 27 | 0 / 0 |
| .NET 11 NativeAOT Release device | 4,287 | 28 | 0 / 0 |
| .NET 11 CoreCLR Release macOS arm64 | 3,376 | 23 | 0 / 0 |
| .NET 11 CoreCLR Release Catalyst arm64 | 3,376 | 23 | 0 / 0 |
| .NET 11 CoreCLR Release tvOS simulator arm64 | 3,484 | 23 | 0 / 0 |
| Final generator unit tests | 19,706 | 36 | 0 |
| Analyzer tests | 79 | 0 | 0 |
| Runtime unit tests | 963 | 1 | 0 |
| Toolchain profile tests | 20 | 0 | 0 |

All three equality regressions pass in every listed full runtime gate. The
final generator gate includes 22 package-lane tests. Analyzer/runtime/profile
checks preceded only the final build-harness version helper and its tests.
The first tvOS deployment attempt targeted a stopped simulator; after booting
that assigned simulator, the same built app passed. Both attempts are retained.

A fresh local Runtime NuGet package also passed all 11 standalone assertions
in both default Debug and Release: a cold app-defined value-type equality
instantiation, inequality, 1,000 repetitions, and String/Array/Optional/Set/
Dictionary operations. These consumers add no selective ReadyToRun override or
application trimming roots. The diagnostic package used `--skip-api-compat`;
this does not replace the shipping API compatibility gate.

Local mixed-package Apple versions now use compact lane codes (`26.2.0-pN` /
`26.2.0-dN`). Long temporary versions exceeded NuGet's path limit once the
supplement included full rebuilt Swift module files, including on net10.
The package contents, SDK/Runtime version strings, and Windows path guards
remain intact.

The rebuilt .NET 10 mixed NuGet consumer and .NET 11 CoreCLR Release mixed
NuGet/SDK-direct consumers pass. Both Apple packages retain all 50 module
files by hash, with longest entry 160 against a 163-character Windows budget.
The verbose net11 diagnostic mixed-fixture and SDK-direct Runtime versions
still exceed the conservative 40-character-user-profile Windows budget
(142/122 and 128/127 respectively); these are local macOS test versions,
not shipping versions. The production per-package Windows guard is unchanged.

## Earlier compatibility batch

Commit `5d3f8acd` established isolated toolchains, runtime-lane identity,
package/template/harness support, scene metadata preservation, and explicit
interface metadata name qualification. Its legacy strict compile gate and
69 withdrawal assertions passed. Earlier package checks passed mixed NuGet
CoreCLR Release consumption on simulator and device, SDK-direct simulator
consumption, all eight Mac/Catalyst framework hygiene legs plus signed IPA,
and the Apple NativeAOT consumer using only package-owned roots. Those broad
archive checks preceded the final metadata-name fix and the equality follow-up;
they are not presented as final-source archive reruns.

## Evidence and boundaries

Full logs, original JSONL, source inventories, commands and toolchain receipts
are retained under ignored `artifacts/qualification/`. The equality follow-up
is `equatable-cdecl-2026-10-22/`; earlier platform, device and package evidence
is in `net11-release-platforms-2026-10-22/`,
`net11-device-packages-2026-10-22/`, and `net10-device-2026-10-22/`.

Runtime/simulator evidence at source fingerprint `9864da7e323d53655c8aafe3f781cb34cbd8247804854856371545e3aefd5a16`
precedes only the subsequent mixed-package helper/tests and this documentation.
Final helper/package/device/desktop/tvOS receipts share source fingerprint
`4d6c9edf1aff15cc3fce7ed030701db0e3383cf4aacf0b3d26f5def34e47d43a`. No canonical
baselines were weakened or promoted.

- CoreCLR Debug remains generally unqualified. BCL-only standalone repros show
  three interpreter ABI defects: SwiftSelf and forward/reverse SwiftError.
  Release controls pass. Upstream issue drafts remain unfiled.
- Selectively precompiling a separate interop assembly passes non-generic and
  reference-type controls but fails for an app-defined value-type generic
  instantiation. Whole-app precompilation passes that control at the cost of
  interpreted user code. No selective-root policy was shipped; debugger
  attachment is untested.
- The Cdecl equality control is independent of those unresolved direct Swift
  call boundaries. Passing its Debug tests does not qualify the whole Debug
  suite.
- Final shipping gates, reviewed net11 baselines and CI qualification remain.
  All-platform export checks do not establish x64 runtime qualification.
- Concurrent same-name simulator apps can confuse the harness's latest `.ips`
  selection; check process PID and simulator path before attributing a crash.

Standalone source/evidence is in the separate local `swift-interop-repro`
repository (`Net11Interpreter`, `LegacyCoreClr`, `DebugPackaging`), outside this
source commit. The DebugPackaging archive includes the corrected package
consumer control; binary NuGet feeds are intentionally excluded.
