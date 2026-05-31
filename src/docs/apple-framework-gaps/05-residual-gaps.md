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
| RealityKit cross-module reads (`CameraTransform`, `Environment.SceneUnderstanding`) (01/03) | ✅ | ✅ | **Landed** |
| CryptoKit incremental `HMAC<H>` via CSM factories (02) | ✅ | ✅ | **Landed** (device system-framework load fixed — see [HMAC on device](#hmac-device)) |
| ProximityReader `GetErrorDescription` @_cdecl parity (04) | ✅ | — | **Landed** |
| MusicKit `Create` array-shims + `MusicItemProxy` (04) | ✅ | — | **Landed** (the 0.11.x ancestor-ordering crash is gone) |
| WorkoutKit `SwiftClosedRange<Bound>` ctors emitted (01) | ✅ | — | **Landed** (callable signatures; usability shim → [06 T2.3](06-remaining-work.md#t23--workoutkit-range-alert-measurement-ctor-shim)) |
| §5b class-superclass read-only proxy fail-clean CALLBACK | ✅ | ✅ | **Landed** |
| §5b Data-return CSM concrete overloads (Ed25519 / context-string sign) | ✅ | — | **Landed** (real-CryptoKit confirm owed → [06 Verification debts](06-remaining-work.md#verification-debts-code-landed-confirmation-owed)) |

**Open work moved out.** Failure A (`Scene.AddAnchor`), the `FamilyActivityPicker` packaging gap,
`Transform(scale,rotation,translation)`, RC‑AOT mesh buffers, the CryptoKit
generic remainders (HPKE construction / Seal/Open, context-string verify), WorkoutKit range-alert
constructibility, the entity-gesture round-trip (Failure B), and the two generator-hardening items
(§5d witness-getter wrap, sibling-marker re-keying) are all tracked in
[`06-remaining-work.md`](06-remaining-work.md) with root cause, fix direction, and a done-criterion
each.

## Validated-working surface (real passes recorded)

The positive half of each partially-landed area, confirmed by real per-package passes:

- **RealityKit / RealityFoundation:** `Scene.Anchors` read traversal and `AnchorEntity`
  construction work (boxing the anchor *into* the scene graph is
  [06 T1.1](06-remaining-work.md#t11--sceneaddanchorihasanchoring-failure-a)). RC‑SIMD `Transform`
  setters + `Transform(Matrix4x4)` round-trip on sim + device.
- **FamilyControls:** `FamilyActivitySelection` construct / read / persist / apply all pass
  (presenting the picker that produces one is
  [06 T1.2](06-remaining-work.md#t12--familyactivitypicker-bridge-packaging)).
- **CryptoKit:** incremental `HMAC<SHA256/384>` is bit-for-bit identical to the one-shot
  `AuthenticationCode` on **sim and device** (`ByteCount` 32/48); the other 40 CryptoKit tests
  (AES.GCM, SHA, key types, …) pass on device.
- **WorkoutKit:** metadata, enums, and the non-`Measurement` ctors (`WorkoutStep`, `IntervalStep`,
  `IntervalBlock`) pass; the four range-alert ctors emit as callable signatures.
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
  RealityKit gesture device round-trip is
  [06 T2.4](06-remaining-work.md#t24--entity-gesture-device-round-trip-failure-b--parser-genericsig).

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
  returns `Bool`, a direct return the indirect-result preflight never blocked — it is
  [06 T2.2](06-remaining-work.md#t22--cryptokit-generic-remainders).) Pinned by
  `Generics/SigningSpecialization.swift` + `SigningSpecializationTests.cs` (single-, two-, and
  three-generic shapes; distinct-seed payload observability) and `ConcreteSpecializationEngineTests`.
  The fixtures model the shape with module-local stand-ins, so the generator mechanism is proven;
  real-CryptoKit end-to-end confirmation is owed (a `nuke validate` sweep —
  [06 Verification debts](06-remaining-work.md#verification-debts-code-landed-confirmation-owed)).
  **Unchanged:** HPKE `Sender`/`Recipient` remain blocked by the separate NestedType conformer
  rejection ([06 T2.2](06-remaining-work.md#t22--cryptokit-generic-remainders)).

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

## Per-package test dispositions applied this pass

The empirical record of how the per-package tests were set during this validation pass. The `Skip`
reasons now point at the [06](06-remaining-work.md) item that will reopen each one.

| Package / test | Was | Now |
|---|---|---|
| RealityKit — `Scene.AddAnchor/RemoveAnchor(IHasAnchoring)` (Failure A) | Fail | **Skip** → [06 T1.1](06-remaining-work.md#t11--sceneaddanchorihasanchoring-failure-a) (traversal + ctor stay real passes) |
| RealityFoundation — `AnchorEntity boxes as IHasAnchoring` (Failure A) | Fail | **Skip** → [06 T1.1](06-remaining-work.md#t11--sceneaddanchorihasanchoring-failure-a) |
| FamilyControls — `FamilyActivityPicker bridge … round-trip` | Fail | **Skip** → [06 T1.2](06-remaining-work.md#t12--familyactivitypicker-bridge-packaging) (selection ctor/property tests stay real passes) |
| CryptoKit — `HMAC<SHA256/384> incremental == one-shot` | Fail (device) | **pass on sim + device** (system-framework load fixed → [HMAC on device](#hmac-device)) |
| RealityFoundation — `Transform(scale,rotation,translation)` | (new) | **Skip** probe → [06 T1.4](06-remaining-work.md#t14--transformscalerotationtranslation-ctor) |
| WorkoutKit — `{HeartRate,Cadence,Power,Speed}RangeAlert` ctors | (new) | **Skip** → [06 T2.3](06-remaining-work.md#t23--workoutkit-range-alert-measurement-ctor-shim) |
| RealityKit — `EntityGestureRecognizer callback` (Failure B) | Skip | **Skip** → [06 T2.4](06-remaining-work.md#t24--entity-gesture-device-round-trip-failure-b--parser-genericsig) |

All seven packages are green on their gated lanes (sim for all; sim+device for CryptoKit,
RealityFoundation, RealityKit). No hard failures remain.
