# .NET 10 / Xcode 26 and 27 maintenance qualification

Qualification started October 22, 2026. This records the local maintenance
candidate, including the ObjC fixes merged from `e358e96b3`, the C ABI generic
equality fix in `361048edc`, and compatibility foundations in `5d3f8acd`.
No packages have been published by this qualification task.

## Decision

Retain the .NET 10/Xcode 26 release lane and its existing public API/runtime
contract. The candidate has substantial Xcode 27 coverage, but full .NET 10/Xcode
27 NativeAOT qualification is blocked by a proven Microsoft Apple workload
regression. Do not advertise unrestricted NativeAOT support or suppress its
trimming diagnostics. A future fixed workload must pass the blocked gates.

The tested legacy pairing is Xcode **26.3**, not every 26.x patch. Existing
26.x support intent and the packages' 26.0 reference-assembly floor are preserved;
other Xcode patches still require a matching Microsoft workload. No x64 runtime
qualification is claimed by these arm64 runtime runs.

## Toolchains and results

| Profile | SDK / workload set | Xcode | Managed runtime |
| --- | --- | --- | --- |
| `net10-xcode26` | 10.0.107 / 10.0.107 | 26.3 (17C529) | 10.0.7 |
| `net10-xcode27` | 10.0.401 / 10.0.401.1 | 27.0 (27A266a) | 10.0.12 |

The profiles isolate SDKs, caches, build trees and Xcode selection. Physical
runs use an iPhone 13 on iOS 26.6. Per-run toolchain receipts record the exact
Apple reference packs: the standalone legacy minimal repro resolves a different
installed Apple pack from the main test suite, so its own receipt is authoritative.

| Gate | Xcode 26.3 | Xcode 27.0 |
| --- | --- | --- |
| Generator unit tests | 19,734 passed / 36 skipped | Same |
| Analyzer tests | 79 passed | Same |
| Runtime unit tests | 963 passed / 1 skipped | Same |
| Strict compile, API manifest, skip surface | Passed | Passed |
| Full iOS simulator | 4,289 passed / 31 skipped | Same |
| Full iOS NativeAOT | 4,291 passed / 28 skipped (before test-only lifetime refinement) | Blocked during publish by upstream IL2037 |
| Full iOS Mono AOT | 4,289 passed / 31 skipped | Same |
| macOS CoreCLR arm64 | 3,377 passed / 23 skipped | Same |
| Mac Catalyst Mono arm64 | 3,373 passed / 27 skipped | Same |
| tvOS simulator Mono arm64 | 3,481 passed / 27 skipped | Same |
| Partial-success kitchen | Passed, frozen baseline unchanged | Same |
| Mixed NuGet simulator / SDK-direct simulator | Both passed | Both passed |
| Mixed NuGet NativeAOT device | Passed | Passed |
| Rebuilt Apple NativeAOT package consumer | Passed | Passed |
| App Store hygiene | Signed IPA and all eight Mac/Catalyst legs passed | Same |
| Blast-radius exact goldens | Passed | Passed |
| Nuke 13.0.4 validation, PackGate, BehaviorTier | Passed | Passed |
| Toolchain isolation regression tests | 21 passed | 21 passed |

All listed passing runtime gates have zero failures/crashes. Existing skips are
retained. Core equality regressions and the ObjC initialization/protocol fixes
are included in these suites. IronSource 9.7.0 compiles with the candidate SDK
on both toolchains; no ad-network initialization or service interaction is claimed.

## Product/package compatibility

The shipping candidate was packed using Xcode 26.3, with ApiCompat enabled
against Runtime 0.20.0. Runtime, SDK and Templates are locally versioned 0.20.1;
the already-published Apple 26.2.9 package is retained, not repacked under the
same version. These retained qualification archives precede the final README
limitation text; the release workflow must pack the final source before publishing. Native export validation checked 488 requirements across ten
architecture/slice pairs, six framework slices and negative controls. Shipping
Windows package-path budgets passed.

Independent fresh apps consume the **actual published** Apple 26.2.9 plus the
candidate Runtime 0.20.1. Both toolchains pass simulator execution on iOS 26.3.1
and signed NativeAOT execution on the physical phone, materializing two
`Locale.Language` values without app-authored trimming roots. This narrowly
proves package upgrade compatibility; it does not exercise NSObject explicit
properties and therefore does not override the full-suite NativeAOT blocker.

| Package | SHA-256 |
| --- | --- |
| Runtime 0.20.1 | `f98ec34ba842480da4a28fe6d88c765d5b13a4414c17ff8d8fd7956be6f723ce` |
| SDK 0.20.1 | `7355ebf730f515363d4e390e5220a6d35ae95335b58702a8deabd65b9ca4bb0b` |
| Templates 0.20.1 | `c58445b4bd251b30b884a1e1323f882bc3ae6865437616bb4c83142917347511` |
| Published Apple 26.2.9 | `3f7b98dde372ac2b8e47c0789ec326355b001645481cc64df9b0fd7f2091f65a` |

## NativeAOT upstream regression

The Apple assembly preparer emits `DynamicDependency` member names for
NSObject-subclass explicit interface properties without documentation-signature
escaping. ILLink cannot resolve these names. The full suite reports IL2037 for
`ISwiftObject.SwiftHandle` and `ISwiftObject.PayloadConstructionSemantics`; the
compiled explicit property metadata itself is valid.

A standalone repro with **no SwiftBindings dependency** establishes:

| Source / toolchain | NativeAOT publish |
| --- | --- |
| Explicit properties, .NET 10/Xcode 27 | Both expected IL2037 errors |
| Public-property control, same toolchain | Passed, native code and IPA |
| Same explicit properties, .NET 10/Xcode 26.3 | Passed, native code and IPA |
| Same explicit properties, .NET 11 RC2/Xcode 27 | Passed, native code and IPA |

These controls establish publishing only; they were unsigned and not launched.
[Existing issue #26969](https://github.com/dotnet/macios/issues/26969) matches
exactly. The [fix on main](https://github.com/dotnet/macios/pull/26529) was
backported to .NET 11 and the [.NET 10 servicing branch](https://github.com/dotnet/macios/pull/26987).
The .NET 10 backport merged October 22; no released fixed .NET 10 workload was
verified during this pass. The published Xcode 27.1 workload also has the defect,
so installing 27.1 is not a demonstrated remedy.

Source, four-case logs/binlogs, pins, metadata, upstream records and an unsubmitted
follow-up draft are saved in the separate `swift-interop-repro` repository under
`NativeAotExplicitProperties/`. No issue or comment was submitted. No generated
public property/API workaround was introduced: legal consumer type/member names
can collide with such properties, and suppressing warnings leaves invalid roots.

## Qualification infrastructure changes

- Installed NuGet packages live outside the owner checkout, within each profile's
  owned external temporary directory. Otherwise a packaged generator can discover
  the owner's source Runtime and older `global.json` by walking its ancestors,
  causing nested MSBuild verification to use the wrong workload. This caused the
  retained TipKit PackGate MSB4014 failure; the fixture's SDK27 APIs were not the
  cause. Existing package caches were moved without copying or overwriting;
  their stale owned Nuke resolver entries were preserved and regenerated.
  The focused profile regression checks prevent this cross-profile ancestry.
- CI/release SDK selection pins 10.0.107 to match the legacy workload and
  `global.json`, instead of floating into an incompatible SDK feature band.
- Optional .NET 11 probes have ordering edges in the strict Nuke graph; they do
  not become default dependencies.
- The synchronous error lifetime tests run allocation churn on a thread that has
  exited before collection, removing conservative stack roots. Both retain exact
  25-allocation/25-release assertions. A new positive control deliberately roots
  an exception, proves its named native payload remains alive after payload
  disposal, then releases the root and requires exactly one final deallocation.
  Initial retained failures were test-lifetime sensitivity; runtime ownership
  code was unchanged. The revised focused suite passes 11/11 on both toolchains.
- PackGate fixtures use the shared 26.0 library reference floor; the 27 workload
  does not carry the intermediate 26.2 target-platform version. Blast-radius
  fixtures and all five matching PackGate/BehaviorTier/x64 consumer app writers
  use workload-supported macOS 14 as their app minimum. The blast-radius builds avoid
  leaking a global macOS RID into unrelated multi-target restore legs. Product
  deployment floors are unchanged.
- The new macOS host adds managed assembly filename strings. The exact new27
  string golden is selected only for that profile. Native linkage/symbol goldens
  and the old26 string golden remain unchanged.
- Withdrawal policies support exact alternative toolchain profiles. Only three
  zero-exception Nuke policies were added for Xcode 27; all 132 original entries,
  112 original policies and original counts are preserved. Unknown, malformed or
  duplicate profiles fail closed. All 81 withdrawal assertions pass.

One new27 simulator consumer initially retained the old toolchain's
`System.Reflection.Metadata.dll` during incremental install. Captured built and
installed hashes proved the mismatch. A clean reinstall of the **same app bits**
matched all checked binaries and passed. This diagnostic failure is retained;
it was not repaired by changing product code or rebuilding the app.

## Evidence and remaining boundary

Full evidence is ignored local data under
`artifacts/qualification/net10-maintenance-2026-10-22/`: `xcode26/`, `xcode27/`,
`published-package-consumers/` and `final-harness/`. Each gate retains its own
source/toolchain receipt. Final runtime runs include the lifetime refinement;
legacy NativeAOT preceded that test-only refinement. Later changes affect only
qualification tooling/docs, so earlier unchanged product/package checks are
retained rather than falsely relabeled as final-source reruns.

The harness fingerprint before the final cache-isolation correction/documentation is
`692ded8b46bf58fdba93c2a42b384c6141fb347d5d7829a88e256d6c3a7000a2`.
The complete final packaging runs on both profiles used source fingerprint
`d6de68fafbff2d1e98f1a5f008a9122c40e01c5042a762952bfa80a4c9eeb381`.
The final documentation update followed those runs. Their restored Nuke inputs
and source receipts match through fetch and execution; source slicing and
auto-discovery actually ran (five slices/three RIDs), alongside static/dynamic
mixed consumers and the filtered Foundation behavior fixture. The temporary
unfiltered BehaviorTier attempt's missing Alamofire prerequisite and earlier
runs without source-slicing inputs are retained as partial attempts, not final
acceptance. This is Nuke-focused real-library validation, not a full library-corpus
sweep. The changed x64 fixture minimum does not establish x64 runtime qualification.
Original baselines were not seeded or promoted. The parent-created disposable
package-consumer simulator was deleted; worker simulators are restored to their
initial stopped state. No unrelated project evidence was removed.

.NET 11 remains experimental pending GA qualification. This maintenance pass
adds the standalone .NET 11 upstream-defect control; it does not replace the
separate historical [preview qualification](qualification-2026-10-22.md).
