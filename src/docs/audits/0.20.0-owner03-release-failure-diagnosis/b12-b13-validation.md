# OWNER-03 B12/B13 validation receipt

Date: 2026-09-15
Disposition: **B12 complete; B13 requalification executed and blocked for publication**

This receipt records the root-cause repair, package confirmations, affected matrix
reruns, applicable corpus identities, Pack, Mono full-AOT, and the final fail-closed
qualification decision. It grants no waiver and does not accept any carried-forward
corpus or x20 residual.

## Provenance

| Repository | Isolated worktree | Frozen base |
|---|---|---|
| `swift-bindings` | `/Users/wojo/.codex/worktrees/9e95/swift-bindings` | `2a1a37b51a3389d359dd4179b9aeedbadfa043b3` |
| `swift-dotnet-packages` | `/private/tmp/owner03-b11-swift-dotnet-packages` | `e0edc4b8362f8309fe32634afd490d680d9bb02d` |
| `internal-binding-testing` | `/private/tmp/internal-binding-testing-b07` | `48bf8f00ee983cbc654fa535fa5559b523d53800` |

The internal worktree was restored to its exact base after validation. Generated
artifacts and evidence were retained under
`/private/tmp/owner03-b12-b13-20260914`; the shared dirty checkouts were not modified.

## Root-cause repairs and first-party regressions

The Nuke failure was the remaining D06 forward-witness gap. A Swift-vended protocol
existential could forward only an all-closure, `Void` requirement. Nuke's
`DataLoading.loadData` shape mixes an ObjC-bridgeable `URLRequest`, two retained
`@Sendable` callbacks (one carrying `Error?`), and an existential cancellation-token
return. The repair:

- extends the deliberately narrow forward-witness classifier to already-dispatchable
  value parameters plus a non-optional existential return while retaining fail-closed
  gates for unproved shapes;
- reconstructs handle-backed Foundation values on the Swift side;
- adapts `Optional<any Error>` through the one-word borrowed boxed-error payload
  used by the cdecl callback ABI (without reading a five-word existential container);
- renders the callback as Swift 6 `Swift.Optional<(any Swift.Error)>`, initializes
  temporary optional-error storage, and emits `SBW_Utf8Slice` before any mixed
  `String` callback accessor;
- transfers escaping callback ownership only after the Swift call succeeds; and
- returns an owned existential heap cell and frees that cell after the managed proxy
  takes ownership.

The follow-up also applies the same one-word `any Error` rule to
`MethodClosureBridge`, rejects mixed `inout` requirements until writeback is proven,
and keeps optional existential returns fail-closed. Coordinated unit assertions now
pin the P/Invoke return, cdecl calling convention and entry point, `finally` cleanup,
retained callback copy-out, and owned-return construction together.

First-party coverage uses the exact mixed Nuke shape in
`ProtocolClosureSkipping.swift`, delayed callbacks after a forced GC, data/URL/error
payload checks (including `.some(rejected)`), and cancellation-token dispatch after
the producer is disposed. The allocations are created on a thread that is allowed to
finish before forced GC; producer and token post-dispose behavior is asserted.
Focused simulator and NativeAOT-device runs passed; the full compile-only BindingTests
gate also passed.

CryptoKit's device-only failure was not a bridge ABI defect. It was triggered by the
consumer test's optimized inline exception/catch shape under JIT/NativeAOT. The
downstream regression now uses a non-inlined exception assertion helper and proves a
matching-AAD open before inspecting the resilient `SealedBox`, retaining the negative
wrong-key and mismatched-AAD checks. That exact test passes in the NativeAOT device
cell.

## Package identity and Pack

`./build.sh Pack --version 0.20.0 --apple-version 26.2.10 --output-dir
/private/tmp/owner03-b12-b13-20260914/packages-grok-fix` passed in 50s, including
ApiCompat against Runtime 0.19.4, native-export coverage (49 imports, 478
requirements, ten slice/architecture checks, six slices), negative controls,
no-source-mutation, Windows-path, and version-truth gates.

| Package | SHA-256 |
|---|---|
| `SwiftBindings.Apple.26.2.10.nupkg` | `31d3a8e8ccecb00c6f0d4b710e57105d2d07eddf88549c6d7828348f4004f853` |
| `SwiftBindings.Runtime.0.20.0.nupkg` | `9636bd395a4df0e73facc0466f8dcf09015ff8897a8ae63d775a11e0af5b448e` |
| `SwiftBindings.Sdk.0.20.0.nupkg` | `33e8528df885910dc70a35debdc0618929035a8c166a22b2c85792f7dee2bc6a` |
| `SwiftBindings.Templates.0.20.0.nupkg` | `62de595e429cd1ef93e72b2412224fcdfff7a586fd0652db2575a5500a400473` |

The exact Pack log is
`grok-followup/upstream-pack.log` (SHA-256
`43d285d1daaa00f6381544d8fdc77d43062af60a56a39481c2628d9f516a5e9b`).
The same package bytes were copied into the isolated downstream `local-packages`
feed and rehash-verified before the follow-up Nuke run. Evidence paths in this
paragraph are relative to `/private/tmp/owner03-b12-b13-20260914`.

## B12 package confirmations

The original five required cells passed against the pre-review `packages-rootfix`
set. The repaired candidate changes the SDK package bytes, so the three affected Nuke
cells were rerun against the package identities above; CryptoKit and Translation are
unchanged confirmations from the earlier package set and are not represented as tests
of the repaired SDK bytes.

| Library | Platform | Result | Duration |
|---|---|---:|---:|
| CryptoKit | iOS device, NativeAOT | PASS | 39.3s |
| Nuke | iOS simulator | PASS | 20.9s (repaired bytes) |
| Nuke | iOS device, NativeAOT | PASS | 21.9s (repaired bytes) |
| Nuke | tvOS simulator | PASS | 31.1s (repaired bytes) |
| Translation | iOS device, NativeAOT | PASS | 19.6s |

Structured receipts:

- `downstream-receipts/20260915-055954.000Z-p11022-e0edc4b/regression-validate.json`
  (Nuke)
- `downstream-receipts/20260915-060400.841Z-p13502-e0edc4b/regression-validate.json`
  (CryptoKit and Translation)
- `grok-followup/downstream-nuke-receipt-original/regression-validate.json`
  (repaired-package Nuke rerun; SHA-256
  `5b8865e3c9d291f21e197222834aaa210c94e8022ab047e46e23d1338952dd59`)

Both paths are relative to `/private/tmp/owner03-b12-b13-20260914`.

## B13 affected downstream matrix

Every one of the fourteen original red downstream cells was attempted. Eleven are
green; three failed closed before product execution because the required external
credential or sandbox authority is absent.

| Library | Platform | Result | Evidence or blocker |
|---|---|---:|---|
| ActivityKit | iOS simulator | PASS | 22.9s |
| ActivityKit | iOS device | BLOCKED | No explicit App ID provisioning profile granting `aps-environment`; `ACTIVITYKIT_PROVISIONING_PROFILE` and the fallback profile variable are unset. |
| CryptoKit | iOS device | PASS | 39.3s, NativeAOT |
| Lottie | iOS simulator | PASS | 39.4s |
| Lottie | iOS device | PASS | 30.7s, NativeAOT |
| Mappedin | iOS simulator | PASS | 105.4s after building the pinned 6.8.0 xcframework input |
| Mappedin | iOS device | PASS | 94.0s, NativeAOT, same pinned input |
| Nuke | iOS simulator | PASS | 20.9s, repaired package bytes |
| Nuke | iOS device | PASS | 21.9s, NativeAOT, repaired package bytes |
| Nuke | tvOS simulator | PASS | 31.1s, repaired package bytes |
| StoreKit2 | iOS simulator | BLOCKED | `STOREKIT_SANDBOX_PRODUCT_ID` and signed-in Sandbox tester authority are absent; preflight stopped before app launch. |
| StoreKit2 | tvOS simulator | BLOCKED | Same B09 external Sandbox authority; no `.storekit` command-line substitution is accepted. |
| TipKit | Mac Catalyst | PASS | 26.7s |
| Translation | iOS device | PASS | 19.6s, NativeAOT |

The ActivityKit/Lottie receipt is
`downstream-receipts/20260915-061013.075Z-p16695-e0edc4b/regression-validate.json`.
The first Mappedin attempt in that receipt correctly exposed the absent xcframework;
the pinned-input rerun is
`downstream-receipts/20260915-061712.298Z-p19911-e0edc4b/regression-validate.json`.
TipKit is recorded in
`downstream-receipts/20260915-062340.391Z-p23386-e0edc4b/regression-validate.json`.
StoreKit's fail-closed exception is preserved in
`downstream-receipts/storekit-preflight.log`. These paths are relative to the same
temporary evidence root.

## B13 internal matrix

The exact `48bf8f00` internal base was requalified with freshly generated simulator
and device wrapper slices from this `swift-bindings` candidate. Both structured
receipts validate against `validate-qualification-receipt.py` with `--require-pass`.

| Library | iOS simulator | iOS device NativeAOT |
|---|---:|---:|
| KeychainAccess | PASS, 35/35 | PASS, 35/35 |
| DeviceKit | PASS, 26/26 | PASS, 26/26 |

Receipts and four wrapper-slice SHA-256 values are under
`/private/tmp/owner03-b12-b13-20260914/internal-receipts`. Generation-time restore
checks that requested the historical Runtime 0.8.0 package were treated only as
inconclusive restore-input checks; the actual current-project-reference app builds
and all four live runs passed.

## Applicable corpus identities

The SDK generator/package input changed, so all 84 available corpus-r4 candidates are
applicable:

`ConfettiSwiftUI`, `DynamicColor`, `Euclid`, `Lightbox`, `Macaw`, `Pow`, `rive-ios`,
`SwiftDraw`, `YPImagePicker`, `ChartView`, `DGCharts`, `epoxy-ios`, `HorizonCalendar`,
`MagazineLayout`, `mecid-SwiftUICharts`, `MultiProgressView`, `swiftui-charts`,
`SwiftUICharts`, `Auth0.swift`, `BlueCryptor`, `JWTDecode`, `RNCryptor`, `SwiftOTP`,
`SwiftyRSA`, `Cache`, `CoreStore`, `Disk`, `SQLite.swift`, `SwiftyUserDefaults`,
`ZIPFoundation`, `DDMathParser`, `Localize-Swift`, `Money`, `Siren`, `SwiftDate`,
`SwiftLocation`, `SwiftRichString`, `Time`, `Factory`, `Needle`, `ReactorKit`,
`Resolver`, `ReSwift`, `swift-dependencies`, `swift-identified-collections`,
`CocoaMQTT`, `Get`, `Moya`, `OAuthSwift`, `SocketIO`, `AEXML`, `CSV.swift`, `Kanna`,
`swift-protobuf`, `SwiftSoup`, `SwiftyJSON`, `SWXMLHash`, `Yams`, `Amplitude-Swift`,
`analytics-swift`, `RevenueCat`, `SwiftyStoreKit`, `TelemetryDeck`, `CombineCocoa`,
`CombineExt`, `PromiseKit`, `ReactiveSwift`, `Semaphore`, `swift-clocks`,
`swift-concurrency-extras`, `Eureka`, `FloatingPanel`, `Hero`, `JTAppleCalendar`,
`MessageKit`, `PinLayout`, `Stevia`, `SwiftMessages`, `Files`, `PathKit`,
`swift-argument-parser`, `swift-numerics`, `swift-system`, and `Then`.

The corpus rerun did not proceed. The authority preflight rejected the missing explicit
P9 source/package/toolchain/inventory receipts. The frozen capacity receipt recorded
about 34.2 GiB host free; 20 GiB is the policy reserve, while 58 GiB is the frozen
historical minimum. This is a fail-closed infrastructure result, not a pass or waiver.
The earlier corpus-r4 result therefore remains authoritative: 60/84 available
candidates passed, 24 were nonzero, and 36/120 named inputs were absent. Publication
remains prohibited.

## Mono full-AOT and x20

The candidate was built and run on physical device as Mono full-AOT. The bundle
classifier confirmed managed assemblies and the live app reported
`IsMonoRuntime=True`, `IsNativeAotRuntime=False`, and `IsMonoAot=True`.

- Full run 1 stalled at
  `TupleMarshallingTests.TestDescribeNameableAgeablePair_UnderGCPressure`. Recovery
  reached 4,065 pass / 0 fail / 32 skip / 0 crash, but the sticky timeout kept the
  gate red. The exact class then passed 26/26 on the unchanged binary.
- Full run 2 stalled at
  `SyncErrorCascadeTests.TestClassErrorBoxIsReleasedExactlyOnce`. Its recovery shard
  passed all 252 remaining tests; the aggregate timeout accounting was 4,063 pass / 1
  fail / 32 skip / 0 crash. The recorded failure was
  `RemainingLaneTypedErrorTests.TestAsyncGenericParentErrorBoxIsReleasedExactlyOnce`
  (`Memory leak detected: -1 object(s) not deallocated`); that failed test was excluded
  from the recovery shard and was not rerun. The exact `SyncErrorCascadeTests` class
  then passed 10/10 on the unchanged binary, including 25 allocations / 25
  deallocations for the stalled test, but that does not clear the separate recorded
  `RemainingLaneTypedErrorTests` assertion failure.

Neither run is a qualifying green lane, and run 2 contains a product assertion failure
that was unresolved by that receipt. Both historical full-run timeout receipts and their
two focused-rerun evidence roots remain:

- `/var/folders/nf/n1g09flj3156vdzd04sm0k6c0000gn/T/swift-bindings-runtime-attempts/02af4d462b7547c480b454a242dc055d`
- `/var/folders/nf/n1g09flj3156vdzd04sm0k6c0000gn/T/swift-bindings-runtime-attempts/66705ed669b34fefad90520c832f6818`
- `/var/folders/nf/n1g09flj3156vdzd04sm0k6c0000gn/T/swift-bindings-runtime-attempts/d06e252b84e44d918ebc57759b2f0dab`
- `/var/folders/nf/n1g09flj3156vdzd04sm0k6c0000gn/T/swift-bindings-runtime-attempts/11d3a3d6b45a46cdae95ba749ba89376`

A separate clean Mono full-AOT log reports 4,065 pass / 0 fail / 32 skip, but it came
from worktree `8ff0` and was not byte-compared with this frozen `9e95` candidate. It is
therefore retained only as non-authoritative context at
`grok-followup/mono-clean-nonauthoritative-8ff0.log` (SHA-256
`f2338b6ac3355aa8232c685e3a3db832fd483e6df37dcafaaad9901cf1c323ca`).

### Mono full-AOT follow-up qualification

The recorded `-1` assertion was a first-party lifetime-oracle defect, not a bridge leak.
`resetAllocationCounters()` cleared the registry and reset both counters, but
`recordTrackedDeallocation(serial:)` still incremented the new window's deallocation
counter when a registry-aware object allocated before the reset was released afterward.
A completed async test state machine can conservatively retain its exception until a
later collection, so the stale serial's removal was a no-op while its deallocation was
incorrectly charged to the next test. This explains the observed 12 allocations / 13
deallocations / `-1` live count with no registry survivor. The first full-run receipt had
already run the same test green, which is consistent with an inter-test reset-window race
rather than an unbalanced error-box transfer.

Generated ABI and ownership were checked before classification. The generated C# import
and Swift `@_cdecl` use the identical symbol and seven parameters: UTF-8 pointer/length,
parent pointer, Cdecl success callback, Cdecl error callback, context, and `Int64`
cancel key. Swift transfers the caught error with `Unmanaged.passRetained(error as
AnyObject)`; C# attaches `SBW_ReleaseError` to the created exception; and that release
calls `Unmanaged<AnyObject>.fromOpaque(error).release()`. No signature, convention, or
retain/release mismatch was found.

The oracle now counts a registry-aware deallocation only when its monotonic serial is
still present in the active window. A deterministic regression forces a live `TrackedRef`
across the native reset boundary and then proves both that the stale deinit is excluded
and that current-window accounting still balances. The ineffective pre-reset async
carrier displacement was removed from the typed-error test; current-window cleanup is
unchanged.

Fresh physical-device Mono full-AOT evidence from this `9e95` worktree is qualifying:

- `nuke binding-tests --device --mono-aot --class-filter LifetimeTrackingTests` regenerated
  all bindings, rebuilt the app, and passed 18 / 18, including the forced cross-reset
  regression. Log: `/private/tmp/owner03-mono-aot-CLCldp/focused-lifetime-mono-aot.log`
  (SHA-256 `8f90b3f9b247ce0d4f8e6916057ebddc836e5af03d4043bcfa399628097407ba`);
  attempt root `d7674f535fc846d5b18c39c56da13270`.
- `nuke binding-tests --device --mono-aot --skip-regen --skip-build --class-filter
  RemainingLaneTypedErrorTests` ran the unchanged built app and passed 9 / 9. The exact
  formerly failing test reported 12 allocations / 12 deallocations. Log:
  `/private/tmp/owner03-mono-aot-CLCldp/focused-remaining-lane-mono-aot.log` (SHA-256
  `7926d529005d25c7da9174bef59b83477fd05e73ddba4fadd08ff4745bda26b1`); attempt root
  `81c5925d839f4007a11d39a1429ce488`.
- `nuke binding-tests --device --mono-aot --skip-regen` rebuilt the device app and passed
  the unfiltered suite on its first run: 4,067 pass / 0 fail / 32 skip / 0 crash,
  `done=True`, in 61.1 seconds, with no recovery. The exact typed-error test again reported
  12 allocations / 12 deallocations. Log:
  `/private/tmp/owner03-mono-aot-CLCldp/full-mono-aot.log` (SHA-256
  `48fd0ba0c997208d9e82638582c1c541ccb9caae0d8dd842a5d790c3d8de0908`); attempt root
  `e46a892befa84f8eb3c2e161cc81cefa`.

All three runs reported `IsMonoRuntime=True`, `IsNativeAotRuntime=False`,
`IsMonoAot=True`, and `Rid=ios-arm64`. The green unfiltered run refreshed the
Device/MonoAOT identity baseline from 4,064 to 4,067. The historical timeout receipts
above remain preserved; the Mono full-AOT lane is now qualified by the fresh clean run.

The nine carried-forward x20 clobber identities remain unaccepted and unwaived:
`BufferModeDescribablePair.first`, `BufferModeDescribablePair.second`,
`BufferModeQuad.first`, `BufferModeQuad.second`, `BufferModeQuad.third`,
`BufferModeQuad.fourth`, `CarrierBox.relayThrough`, `DefaultedHasher.append`, and
`DefaultedHasherWithFile.append`.

## Grok round-1 adjudication and repair evidence

The requested Grok review is preserved at
`/private/tmp/owner03-b12-b13-grok-review/grok-r1/response.json` (SHA-256
`34af0445a4d9908f1fa22657ec2c72c546bf3723f9d7d3060f3d1c11fa0588dd`;
session `01a0a3e6-b9c9-77e1-a74a-16ffc991845b`). Its accepted findings were
repaired as one affected-category pass: one-word `any Error` callback payloads in
both closure bridges; Swift 6 optional-error rendering and initialized storage;
mixed-String helper emission; fail-closed mixed `inout`; `.some(Error)` runtime
coverage; finished-thread GC; producer/token independent ownership and post-dispose
behavior; coordinated proxy/witness assertions; the Mono receipt correction; and the
corpus-capacity wording correction.

Current first-party evidence, relative to
`/private/tmp/owner03-b12-b13-20260914/grok-followup`, is:

- `focused-emitter.trx` — 674 pass / 0 fail / 0 skip, SHA-256
  `f7c510174fa51f4fd7432d764f576a31714b4e14d9a64e098438c3f79a4a72d2`;
- `upstream-bindingtests-compile-only.log` — SHA-256
  `4e0ae6f824cd5abf80008ec2faa7a8de7ad836ff8ffc1dc2446e5dea9d9a9944`;
- `upstream-unit-tests.log` — SHA-256
  `3a053a2b6e11857aff746c48d96636787a8bb409ed2a45a3eb5e7d29fe019def`;
- `upstream-protocol-closure-simulator.log` — SHA-256
  `dc35591731a803256df8e8638a21b5079d23b0d2b08b3509e82f07ac38c06989`;
- `upstream-protocol-closure-device-nativeaot.log` — SHA-256
  `77ee104acc2d8a43d59e99557eb520cec5ce7d766d0dfc8968f8266dadc3afab`;
- `downstream-nuke-regression.log` — SHA-256
  `222bdb4d7c1263bbf2eab22c8ceabab2c893ae898165b8bc9734833aec06e5ea`.

## First-party and integrated validation

| Gate | Result |
|---|---:|
| Focused closure/witness/proxy emitter unit tests | PASS, 674/674 (covered again by the full gate) |
| Full `UnitTests` | PASS, 19,074 passed / 0 failed / 2 known skips; floor auto-raised 19,071 → 19,074 |
| Full BindingTests compile-only | PASS, 1,707 C# and three Swift wrapper files; C#/Swift compilation, parity, resilience, and ingestion green |
| Mixed-closure first-party simulator class | PASS, 50 / 0 / 1 known skip |
| Mixed-closure first-party NativeAOT device class | PASS, 50 / 0 / 1 known skip |
| Pack | PASS, exact log and four package hashes recorded above |
| B12 required cells | PASS, 5/5 original confirmations; affected Nuke 3/3 rerun against repaired bytes |
| B13 original-red downstream cells | BLOCKED, 11 pass / 3 external-authority blockers |
| B13 internal cells | PASS, 4/4 |
| Corpus-r4 | BLOCKED by authority and capacity; prior 24 nonzero / 36 absent retained |
| Mono full-AOT | PASS, 4,067 / 0 / 32 / 0 on a clean first run; live Mono full-AOT identity confirmed |

## Final qualification decision

**BLOCKED FOR PUBLICATION.** B12 is complete. B13 has an honest final receipt, but it
cannot be declared green while any of the following remain:

1. ActivityKit device lacks an explicit APNs-capable provisioning profile.
2. StoreKit2 iOS/tvOS lack the B09 Sandbox product and signed-in tester authority.
3. Corpus-r4 lacks current explicit authority receipts and sufficient disk capacity;
   its 24 nonzero candidates and 36 absent named inputs remain unresolved.
4. The nine x20 clobber identities remain unaccepted.

No merge, push, publication, limitation acceptance, or waiver is authorized by this
receipt.
