# Session 05 — What landed and was validated in the 0.12.0 pass (done ledger)

This doc is the **done ledger** for the Apple-framework gap-fix campaign: the fixes that landed
and were validated end-to-end against the real consumer packages in `swift-dotnet-packages` (not
the generator's own BindingTests harness), during the pre-ship double-check after SDK 0.12.0 /
Apple 26.2.4 shipped. **Everything still open lives in
[`06-remaining-work.md`](06-remaining-work.md)** (the finish line); this doc records only what is
finished.

**Validation method.** For each fix, the relevant per-package test was un-skipped / added and run
on **simulator (Mono JIT)** and, where Sessions 01–03 gated it, on a **physical device
(NativeAOT)** — iPhone 13, 2026-05-29. A test that merely exercises a type's metadata is not
enough; these tests drive the actual fixed API end-to-end and assert a correct result.

## What landed

| Fix (session) | Sim | Device | Verdict |
|---|---|---|---|
| RC‑SIMD Transform setters + `Transform(Matrix4x4)` (01) | ✅ | ✅ | **Landed** |
| Class-bound existential boxing — real `AnchorEntity : HasAnchoring` (`Scene.AddAnchor/RemoveAnchor`, `AnchorEntity boxes as IHasAnchoring`) | ✅ | ✅ | **Landed** (device confirmed — see [class-bound existential on device](#class-bound-existential-device)) |
| RC‑SIMD `Transform(scale,rotation,translation)` ctor (3-SIMD indirect marshal) | ✅ | ✅ | **Landed** (device confirmed — see [Transform three-SIMD on device](#transform-three-simd-device)) |
| RealityKit cross-module reads (`CameraTransform`, `Environment.SceneUnderstanding`) (01/03) | ✅ | ✅ | **Landed** |
| CryptoKit incremental `HMAC<H>` via CSM factories (02) | ✅ | ✅ | **Landed** (device system-framework load fixed — see [HMAC on device](#hmac-device)) |
| FamilyActivityPicker SwiftUI bridge packaging | ✅ | ✅ | **Landed** (SDK bridge xcframework pipeline — see [FamilyActivityPicker bridge](#familyactivitypicker-bridge)) |
| ProximityReader `GetErrorDescription` @_cdecl parity (04) | ✅ | — | **Landed** |
| MusicKit `Create` array-shims + `MusicItemProxy` (04) | ✅ | — | **Landed** (the 0.11.x ancestor-ordering crash is gone) |
| WorkoutKit `SwiftClosedRange<Bound>` ctors emitted (01) | ✅ | — | **Landed** (callable signatures) |
| WorkoutKit `{HeartRate,Cadence,Power,Speed}RangeAlert` ctors via runtime `Measurement<T>(value, unit)` | ✅ | — | **Landed** (was 06 T2.3 — see [WorkoutKit range-alert ctors](#workoutkit-range-alert-ctors)) |
| §5b class-superclass read-only proxy fail-clean CALLBACK | ✅ | ✅ | **Landed** |
| §5b Data-return CSM concrete overloads (Ed25519 / context-string sign) | ✅ | — | **Landed** (real-CryptoKit confirm paid this pass — see [Data-return CSM](#data-return-csm)) |
| MLDSA65/MLDSA87 context-string `IsValidSignature(sig, data, context) → bool` overloads on sim | ✅ | — | **Landed** (was 06 T2.2 verification debt — see [MLDSA context-string verify](#mldsa-context-string-verify-sim)) |
| RealityKit `EntityTranslationGestureRecognizer.Entity` carrier identity round-trip (Failure B) | ✅ | ✅ | **Landed** (was 06 T2.4 — see [entity-gesture device round-trip](#entity-gesture-device-round-trip)) |

**Tier 1 closed.** With the FamilyActivityPicker bridge packaging confirmed end-to-end and the
batched device pass green for the existential-boxing and 3-SIMD ctor fixes, every Tier-1 item in
[`06-remaining-work.md`](06-remaining-work.md) is LANDED on its gated lanes (sim + device, iPhone
13 NativeAOT, 2026-05-31). The gap-fix campaign reaches the doc's stated "finishable" criterion.
WorkoutKit range-alert constructibility (was 06 T2.3) and the entity-gesture real-RealityKit
identity round-trip on device (was 06 T2.4, Failure B) both graduated to this ledger — see
[WorkoutKit range-alert ctors](#workoutkit-range-alert-ctors) and
[entity-gesture device round-trip](#entity-gesture-device-round-trip). **All 0.12.0
verification debts are paid; the campaign's 0.12.0 work is closed.** RC‑AOT mesh buffers,
CryptoKit HPKE construction, and the two generator-hardening items (T2.5 witness-getter wrap,
T2.6 sibling-marker re-keying) remain in [`06`](06-remaining-work.md) as Tier 2 deferred to
0.13.0.

## Validated-working surface (real passes recorded)

The positive half of each partially-landed area, confirmed by real per-package passes:

- **RealityKit / RealityFoundation:** `Scene.Anchors` read traversal, `AnchorEntity` construction,
  **and boxing the anchor *into* the scene graph** pass on **sim + device** —
  `Scene.AddAnchor/RemoveAnchor(IHasAnchoring)` round-trip and `AnchorEntity boxes as IHasAnchoring
  existential` (see [class-bound existential on device](#class-bound-existential-device)). RC‑SIMD
  `Transform` setters + `Transform(Matrix4x4)` round-trip on sim + device;
  `Transform(scale,rotation,translation)` round-trips on **sim + device** (see
  [Transform three-SIMD on device](#transform-three-simd-device)). Real-RealityKit
  `EntityTranslationGestureRecognizer.Entity` carrier identity round-trip passes on
  **sim + device** (see [entity-gesture device round-trip](#entity-gesture-device-round-trip)).
- **FamilyControls:** `FamilyActivitySelection` construct / read / persist / apply all pass on
  sim and device; the SwiftUI bridge `FamilyActivityPicker bridge create + selection JSON
  round-trip` test passes on both lanes
  (see [FamilyActivityPicker bridge](#familyactivitypicker-bridge)).
- **CryptoKit:** incremental `HMAC<SHA256/384>` is bit-for-bit identical to the one-shot
  `AuthenticationCode` on **sim and device** (`ByteCount` 32/48); the other 40 CryptoKit tests
  (AES.GCM, SHA, key types, …) pass on device. **MLDSA65 context-string verify round-trip**
  (sign → verify-matching-context true → verify-wrong-context false) passes on **sim**
  ([MLDSA context-string verify](#mldsa-context-string-verify-sim)).
- **WorkoutKit:** metadata, enums, and the non-`Measurement` ctors (`WorkoutStep`, `IntervalStep`,
  `IntervalBlock`) pass; the four range-alert ctors (`HeartRateRangeAlert`,
  `CadenceRangeAlert`, `PowerRangeAlert`, `SpeedRangeAlert`) construct end-to-end on **sim**
  via the runtime-level `Measurement<T>(double value, T unit)` ctor +
  `SwiftClosedRange<Bound>(lower, upper)` — see
  [WorkoutKit range-alert ctors](#workoutkit-range-alert-ctors).
- **MusicKit:** 40/0/0 on sim, incl. the `Create` shims.

<a id="data-return-csm"></a>

## §5b — Follow-on pass: class-superclass proxy hardening + Data-return CSM

Work after the 0.12.0 retrospective. Two generator gaps are addressed and two portable
BindingTests fixtures pin the behaviour permanently.

- **Class-superclass read-only proxy — fail-clean CALLBACK (Failure B-adjacent).** Any protocol
  whose `Self` is constrained to a Swift class superclass that the synthesized `EveryProtocol`
  helper cannot subclass is routed through the read-only proxy path, which emits no
  `Get_EveryProtocol_{P}_WitnessTable` getter. Previously the C# proxy still declared and called
  that P/Invoke, so the unsupported C#-implements-protocol (CALLBACK) direction crashed with
  `EntryPointNotFoundException` on the missing symbol. The witness-getter marker is now recorded at
  the Swift emission site and read by the proxy emitter — both keyed on the conformer's
  *module-qualified* name — so the proxy suppresses the getter P/Invoke and
  `GetWitnessTableFromSwift()` throws `NotSupportedException` at the C#→Swift boundary instead.
  RETURN and ACCEPT (Swift-vended existentials, which dispatch through their own witness table) are
  unaffected. Pinned by `Protocols/EntityRootedExistential.swift` + `EntityRootedExistentialTests.cs`
  (RETURN/ACCEPT pass; CALLBACK asserts the clean `NotSupportedException`), using a pure-Swift
  `Entity` stand-in so the gate stays portable across every cell (incl. tvOS, which has no
  RealityKit). Note: this is the *read-only* path; the real RealityFoundation
  `EveryEntityProtocol : Entity` carrier emission is generator-unit-test-covered, and the real
  RealityKit gesture device round-trip is now landed — see
  [entity-gesture device round-trip](#entity-gesture-device-round-trip).

- **Data-return CSM concrete overloads (Ed25519 / context-string sign).** The concrete-specialization
  engine's indirect-result preflight rejected any non-ISwiftObject return, which dropped
  method-level-generic methods returning `Foundation.Data` (it projects to the C# `byte[]` value
  type) to generic-only SB0001 stubs — so C# could verify but not *produce* an Ed25519 signature.
  The preflight now consults the `InlineSwiftStruct` allowlist: a `Foundation.Data` indirect return
  is admitted (sized/marshaled on the ISwiftObject `Swift.Foundation.Data`) and projected to
  `byte[]` on the public surface, a drop-in for the generic stub it shadows. This is the generator
  mechanism behind the **Ed25519 signing** stub (Ed25519 signatures are raw `Foundation.Data`, so
  `signature(for:)` now binds to a concrete `byte[]` overload) and behind any **context-string
  sign** whose concrete return is likewise `Foundation.Data`. (The context-string *verify* path
  returns `Bool`, a direct return the indirect-result preflight never blocked — and now ships
  end-to-end on sim, see [MLDSA context-string verify](#mldsa-context-string-verify-sim).)
  Pinned by `Generics/SigningSpecialization.swift` + `SigningSpecializationTests.cs` (single-,
  two-, and three-generic shapes; distinct-seed payload observability) and
  `ConcreteSpecializationEngineTests`. **Real-CryptoKit confirmation paid this pass:** filtered
  `nuke validate --filter CryptoKit` regen survey confirms the cartesian binds end-to-end —
  Ed25519 signing emits `byte[]` Sign overloads, and `MLDSA65.PrivateKey.Signature(data[,
  context])` emits 4 sign-side cartesian overloads (`CryptoKit.cs:25307-25417`); the verify
  side (`MLDSA65.PublicKey.IsValidSignature(sig, data, context) → bool`) emits 8 cartesian
  overloads (`CryptoKit.cs:24792-24903`). MLDSA87 mirrors. **Unchanged:** HPKE
  `Sender`/`Recipient` remain blocked by the separate NestedType conformer rejection
  ([06 T2.2](06-remaining-work.md#t22--cryptokit-generic-remainders)).

- **Adjacent latent hazard.** A sibling family of emission markers (`SetVtable`, `ObjCBase`,
  `EntityBase`, `Conformance`) still keys on the simple type name rather than the module-qualified
  name. It is **not a reproducing bug** today — categorical audit and the TDD-first hardening plan
  moved to [06 T2.6](06-remaining-work.md#t26--sibling-emission-marker-name-keying-hardening)
  (detail in [`sibling-marker-name-keying.md`](sibling-marker-name-keying.md)).

<a id="hmac-device"></a>

## HMAC`<H>` on device — system-framework load (was 06 T1.3)

The CryptoKit `HMAC<H>` CSM factories were correct on sim but failed to initialize on **device
(NativeAOT)** with `TypeInitializationException -> SwiftRuntimeException: Unable to load library:
@rpath/CryptoKit.framework/CryptoKit`. The CSM static initializer resolves the hash conformer's
protocol-conformance descriptor by `dlopen`-ing the framework via `@rpath/...`; on a physical
device that path does not resolve, because CryptoKit is an Apple **system** framework at
`/System/Library/Frameworks`, not bundled in the app's `@rpath`. (On NativeAOT the per-assembly
DllImport resolver is consulted only for *bare* names, never for dyld-style path names — the
`@rpath/...` string went straight to dyld and threw.)

**Fix (two halves).**
- **Runtime resolver** (`SwiftFrameworkResolver`): a dyld-style `@rpath/Name.framework/Name` load
  that fails is retried by reconstructing candidates from the bare framework name, with
  `/System/Library/Frameworks/Name.framework/Name` last in the ordered search list. A correctly
  app-bundled framework still resolves via its verbatim `@rpath` first, so user frameworks are
  unaffected. This fixes the `ProtocolConformanceDescriptor`/`ProtocolDescriptor` `LoadFromSymbol`
  path (the HMAC crash).
- **Generator** (`BindingsGeneratorCommand.ResolveRuntimeLibraryName`): for system-framework
  targets the embedded library name is reduced to the bare framework name (e.g. `CryptoKit`)
  before it is baked into every emitted `[LibraryImport]`/`LoadFromSymbol` string, so the
  per-assembly DllImport resolver is consulted on NativeAOT (closing the broader
  metadata-accessor P/Invoke path).

**Validated.** CryptoKit `HMAC<SHA256/384> incremental == one-shot` now runs and asserts on
**device** (NativeAOT, iPhone) as well as sim — RegressionValidate CryptoKit `ios-sim PASS,
ios-device PASS` (2026-05-31), the AOT-lane capability skip removed. Unit coverage:
`ResolveRuntimeLibraryNameTests` (generator) + `SwiftFrameworkResolverTests` (runtime).

<a id="class-bound-existential-device"></a>

## Class-bound existential boxing on device — was 06 T1.1

The cross-module/umbrella-protocol `HasAnchoring` `TypeRecord` resolution and the per-subclass
`IExistentialBoxable.BoxAsExistential1<TProtocol>()` re-implementation (`8099d434`) were proven on
sim during the 0.12.0 retrospective. The batched device pass on iPhone 13 (NativeAOT,
2026-05-31) confirms the same paths under AOT: RealityFoundation
`AnchorEntity boxes as IHasAnchoring existential` and RealityKit
`Scene.AddAnchor/RemoveAnchor(IHasAnchoring) round-trip` +
`Scene.Anchors traversal + AnchorEntity construction` all pass. The portable BindingTests
fixture `TestSubclassOnlyConformanceBoxesAsDerivedType` remains the durable hermetic gate.

RoomPlan `RoomCaptureView.Delegate` (`RoomPlan.cs:5790`), called out as a presumed RC-PROXY
secondary site, has no per-package test today; the read-only-proxy CALLBACK fail-clean shape is
gated by the portable `EntityRootedExistential` fixture (§5b) and the *session* delegate (the
supported path) is unaffected. No new code emerged from the device pass.

<a id="familyactivitypicker-bridge"></a>

## FamilyActivityPicker SwiftUI bridge packaging — was 06 T1.2

The SDK build/pack pipeline added in `3d62df46` ("SwiftUI bridge SDK integration: auto-compile
and package bridge framework") already covers what the 06 doc's stale prose called the missing
piece. The generator emits `*.SwiftUIBridge.swift` + `*.SwiftUIBridge.cs` into
`obj/.../swift-binding/`; `_CompileSwiftUIBridge` (Sdk.targets) calls the generator's
`--compile-bridge-only` to produce `{Module}Bridge.xcframework`;
`_ResolveSwiftNativeReferences` injects it as a `<NativeReference Kind="Framework">` for the
local build, the synthesized `{PackageId}.targets` injects it the same way for packed
consumers, and `_ConfigureSwiftBindingPack` bundles it under `runtimes/{rid}/native/` at pack
time. For FamilyControls the generator emits functional `@_cdecl` bodies (not template stubs)
and the resulting `FamilyControlsBridge.framework` exports the full
`SBW_FamilyControls_FamilyActivityPicker_*` symbol set that the C#
`[LibraryImport("FamilyControlsBridge", …)]` declarations resolve at runtime.

**Validated.** The FamilyControls per-package test
`FamilyActivityPicker bridge create + selection JSON round-trip` passes on sim (Mono JIT,
16/0/0) and device (iPhone 13 NativeAOT, 16/0/0) — 2026-05-31. The test exercises the
end-to-end packaging path: `FamilyActivityPickerSession.Create(selection)` resolves the
native library (no `DllNotFoundException`), the underlying `UIHostingController` is non-null,
and `ReadSelection()` round-trips the selection through the Swift bridge's
`JSONEncoder`/`JSONDecoder`. No SDK or generator change was needed in this pass — the
implementation was already in place; what was owed was empirical confirmation in a packaged
consumer.

<a id="transform-three-simd-device"></a>

## Transform three-SIMD ctor on device — was 06 T1.4

The parser `@inlinable` access-control fix that lets the three-SIMD-param
`Transform(scale:rotation:translation:)` ctor emit a `@_cdecl` wrapper marshaling the SIMD
params indirectly (`CallConvCdecl`, buffer-pointer ABI) was proven on sim during the 0.12.0
retrospective. The batched device pass on iPhone 13 (NativeAOT, 2026-05-31) confirms the
RealityFoundation `Transform(scale,rotation,translation) constructor` test passes under AOT as
well. No new code emerged from the device pass.

<a id="workoutkit-range-alert-ctors"></a>

## WorkoutKit range-alert ctors on sim — was 06 T2.3

The four range-alert types (`HeartRateRangeAlert`, `CadenceRangeAlert`, `PowerRangeAlert`,
`SpeedRangeAlert`) take `SwiftClosedRange<Measurement<NSUnit…>>` bounds. Previously the
Foundation `Measurement<T>` projection was value-only (read-only — `.Value` getter + only
`internal Measurement(IntPtr)`), so the bounds could not be minted from C# and the alerts
were non-constructible end-to-end. The 06 doc tracked this as a per-framework `@_cdecl`
trampoline.

What actually shipped is a **runtime-level** `public unsafe Measurement(double value, T unit)
where T : class` ctor on the Foundation `Measurement<T>` projection
(`src/Swift.Bindings.Apple/Sources/Foundation/Measurement.cs:193`), routing through
`MeasurementInterop.InitFromValueUnit` → the `SBW_Measurement_InitFromValueUnit` shim
in the Apple supplement xcframework. Combined with the existing
`SwiftClosedRange<Bound>(Bound lower, Bound upper)` ctor
(`src/Swift.Runtime/src/Swift/SwiftClosedRange.cs:194`) — which copies the bounds via the
value-witness-table InitializeWithCopy, so the source `Measurement<T>` values can be safely
disposed after range construction — the four range-alert types are constructible from C# with
**no per-framework Swift trampoline**. The Measurement projection's static cctor auto-registers
the `ISwiftComparable` conformance, satisfying the `SwiftClosedRange<Bound>` Comparable
requirement.

**Validated.** WorkoutKit per-package sim run (Mono JIT, 2026-05-31): 29/0/0. Each of the four
range-alert ctor tests flipped from `Skip()` to a real pass — minting two
`Measurement<NSUnit{Frequency,Power,Speed}>` bounds, wrapping them in
`SwiftClosedRange<Measurement<…>>`, and constructing the alert (the `SpeedRangeAlert` ctor
additionally takes a `WorkoutAlertMetric`; `.Current` is used). Test source:
`apple-frameworks/WorkoutKit/tests/Tests.cs`. Device round-trip is not gated for this surface.

<a id="mldsa-context-string-verify-sim"></a>

## MLDSA context-string verify on sim — was 06 T2.2 verification debt

The `Bool`-return context-string verify overload — `IsValidSignature(signature, data, context)
→ bool` — is a direct return that the indirect-result preflight described in
[§5b](#data-return-csm) never blocked. The 0.12.0 retrospective initially tracked the
emitted overloads under T2.2 as a verification debt awaiting a real-CryptoKit per-package
round-trip. This pass pays that debt.

Apple's ECDSA surfaces (`P256/P384/P521.Signing.PublicKey`) expose **no** context parameter
on `IsValidSignature`; the 3-PAT cartesian context-string verify ships on the **ML-DSA
post-quantum signing** surfaces — `MLDSA65.PublicKey` and `MLDSA87.PublicKey` (FIPS 204,
iOS 26+). The 8 concrete cartesian overloads
(`(byte[]|Foundation.Data)³ → bool`) emit at `CryptoKit.cs:24792-24903`. MLDSA65 CSM uses
direct cdecl entries (`SBW_CSM_..._signature_...`, `SBW_CSM_..._isValidSignature_...`),
not the `ConcreteSpecializationEngine` static-cctor `@rpath/CryptoKit.framework/CryptoKit`
load path, so it is unaffected by the historical HMAC`<H>` device-load gap and exercises
cleanly on Mono/sim. (Device runtime is not gated for this overload set in 0.12.0; the
generator mechanism is the same that ships HMAC`<H>` on device via the §5b/HMAC fixes,
so the device-lane risk is bounded to a follow-on confirmation, not new code.)

**Validated.** New CryptoKit per-package sim test (`apple-frameworks/CryptoKit/tests/Tests.cs`,
`MLDSA65 context-string verify round-trip`): mint `MLDSA65.PrivateKey()` → derive
`PublicKey` → `Signature(msg, context)` → `IsValidSignature(sig, msg, context)` asserts
true → `IsValidSignature(sig, msg, wrongContext)` asserts false. Sim pass, CryptoKit
per-package run 43/0/0 (Mono JIT, 2026-05-31).

<a id="entity-gesture-device-round-trip"></a>

## Entity gesture device round-trip on sim + device — was 06 T2.4

[Failure B](03-proxy-callback.md) is the class-rooted existential carrier — the boundary a
real RealityKit gesture callback crosses when it reads back the hit-tested entity. The
0.12.0 retrospective shipped the carrier emission (`EveryEntityProtocol`,
`HasCollisionProxy`), routing-gate fix, and a hermetic in-repo fixture under BindingTests
(`Protocols/EntityRootedExistential*`). The remaining debt was a real-RealityKit
end-to-end identity round-trip on a physical iPhone running NativeAOT — the lane where
calling-convention or marshalling regressions in the existential carrier would surface
that the hermetic fixture (running on Mono/sim) could miss.

**Validated.** New RealityKit per-package test
(`apple-frameworks/RealityKit/tests/Tests.cs`, `EntityTranslationGestureRecognizer.Entity
carrier identity round-trip (A → B → null)`): construct two distinct `ModelEntity`
instances, instantiate `EntityTranslationGestureRecognizer(target: null, action: null)`,
assign `recognizer.Entity = entityA` (boxes through `HasCollisionProxy` via the
`EveryHasCollisionProtocol` existential carrier), read the property back, cast to
`Swift.Runtime.ISwiftObject`, and assert `SwiftHandle` equality against the source
entity's handle. Repeat with `entityB` and a `null` clear. RealityKit per-package run
20/0/0 on iPhone 13 NativeAOT (device, 2026-05-31) and 20/0/0 on iOS Simulator (Mono JIT,
2026-05-31). With both lanes green on real RealityKit, the Failure-B carrier is
end-to-end validated and graduates out of the verification-debt list. The hermetic
EntityRootedExistential fixture stays in BindingTests as the durable in-repo gate.

## Per-package test dispositions applied this pass

The empirical record of how the per-package tests were set during this validation pass.

| Package / test | Was | Now |
|---|---|---|
| RealityKit — `Scene.AddAnchor/RemoveAnchor(IHasAnchoring)` (Failure A) | Fail → fixed | **real pass on sim + device** (see [class-bound existential on device](#class-bound-existential-device)) |
| RealityFoundation — `AnchorEntity boxes as IHasAnchoring` (Failure A) | Fail → fixed | **real pass on sim + device** (see [class-bound existential on device](#class-bound-existential-device)) |
| FamilyControls — `FamilyActivityPicker bridge … round-trip` | Skip | **real pass on sim + device** (see [FamilyActivityPicker bridge](#familyactivitypicker-bridge)) |
| CryptoKit — `HMAC<SHA256/384> incremental == one-shot` | Fail (device) | **pass on sim + device** (system-framework load fixed → [HMAC on device](#hmac-device)) |
| RealityFoundation — `Transform(scale,rotation,translation)` | Fixed | **real pass on sim + device** (see [Transform three-SIMD on device](#transform-three-simd-device)) |
| WorkoutKit — `{HeartRate,Cadence,Power,Speed}RangeAlert` ctors | (new) | **real pass on sim** (see [WorkoutKit range-alert ctors](#workoutkit-range-alert-ctors)) |
| CryptoKit — `MLDSA65 context-string verify round-trip` | (new) | **real pass on sim** (see [MLDSA context-string verify](#mldsa-context-string-verify-sim)) |
| RealityKit — `EntityTranslationGestureRecognizer.Entity carrier identity round-trip` (Failure B) | Skip → fixed | **real pass on sim + device** (see [entity-gesture device round-trip](#entity-gesture-device-round-trip)) |

All Tier-1 gates are green on sim + device. The remaining skips track Tier-2 items in
[`06-remaining-work.md`](06-remaining-work.md). No hard failures.
