# Session 06 — Remaining work (the finish line)

> Status: **the bounded set that ends the campaign.** This is the single source of
> truth for what is still open. Fully-landed work is recorded in
> [`05-residual-gaps.md`](05-residual-gaps.md) (the done ledger). Campaign framing and
> the architectural out-of-scope rationale live in [`00-overview.md`](00-overview.md).

## Definition of done

The campaign is **finishable when every Tier 1 item is closed** — fix landed *and* the
named test green on its gated lane. Tier 1 is the set that blocks real-world use of a
shipping framework from C#.

- **Tier 2** is genuinely fixable but lower-impact. Schedule it after Tier 1; it is *not*
  required to declare the campaign done.
- **Excluded** is consciously parked — architectural or by-design limits, not debts. We do
  not reopen these without a new product decision.
- **Verification debts** are fixes whose *code* already landed but whose real-framework
  confirmation is still owed; closing them is confirmation work, not new code.

This is the whole list. When Tier 1 is green and the verification debts are paid, the
gap-fix campaign is done — Tier 2 and Excluded are tracked here precisely so they do *not*
keep the campaign "open" indefinitely.

## At a glance

| Tier | Item | Framework | Gate |
|---|---|---|---|
| **1** | [T1.1](#t11--sceneaddanchorihasanchoring-failure-a) `Scene.AddAnchor(IHasAnchoring)` | RealityKit / RealityFoundation | sim + device |
| **1** | [T1.2](#t12--familyactivitypicker-bridge-packaging) `FamilyActivityPicker` bridge packaging | FamilyControls | sim |
| ~~1~~ | ~~T1.3 HMAC`<H>` conformance-descriptor load on device~~ — **landed** ([05 done ledger](05-residual-gaps.md#hmac-device)) | CryptoKit | device ✅ |
| **1** | [T1.4](#t14--transformscalerotationtranslation-ctor) `Transform(scale,rotation,translation)` ctor | RealityFoundation | sim + device |
| 2 | [T2.1](#t21--typed-mesh-buffers-on-nativeaot-rc-aot) RC‑AOT typed mesh buffers on NativeAOT | RealityFoundation | device |
| 2 | [T2.2](#t22--cryptokit-generic-remainders) CryptoKit generic remainders (HPKE construction, Seal/Open, context-string verify) | CryptoKit | sim + device |
| 2 | [T2.3](#t23--workoutkit-range-alert-measurement-ctor-shim) WorkoutKit range-alert `Measurement<T>` ctor shim | WorkoutKit | sim |
| 2 | [T2.4](#t24--entity-gesture-device-round-trip-failure-b--parser-genericsig) Entity gesture device round-trip (Failure B) + parser genericSig | RealityKit | device |
| 2 | [T2.5](#t25--witness-getter-entrypointnotfound-to-notsupported-wrap) Witness-getter `EntryPointNotFound`→`NotSupported` wrap | generator | sim + device |
| 2 | [T2.6](#t26--sibling-emission-marker-name-keying-hardening) Sibling emission-marker name-keying hardening | generator | sim + device |

---

# Tier 1 — must close to declare the campaign done

## T1.1 — `Scene.AddAnchor(IHasAnchoring)` (Failure A)
*(was 05 §1 — highest-impact finding)*

**What's blocked.** Core AR anchor placement from C#: `Scene.AddAnchor`, `Scene.RemoveAnchor`,
`AnchorCollection.Append/Remove/ReplaceAll`, and `AnchorCollection`'s indexer setter all box
`IHasAnchoring` via `ExistentialContainerFactory.GetOrCreate<IHasAnchoring>` and throw
`TargetInvocationException` (sim **and** device). Reading `Scene.Anchors` and constructing an
`AnchorEntity` already work — you just can't put the anchor into the scene graph.

**Root cause (two compounding).**
1. **Empty conformance dict.** `ShouldEmitConformanceDictionary`
   (`TypeHandlerHelpers.cs:1484`) returns `false` for `AnchorEntity : HasAnchoring` because the
   gate at `:1495` requires `typeDatabase.TryGetTypeRecord(conformance.Protocol, …)` to succeed,
   and the cross-module/umbrella protocol `HasAnchoring` has **no loaded `TypeRecord`** during
   RealityFoundation generation (comment at `:1493-1494` describes exactly this path). So
   `AnchorEntity._protocolConformanceSymbols` emits empty — the Session 03 predicate split
   changed nothing for the one case it targeted.
2. **`BoxAsExistential1` uses the wrong concrete type.** `AnchorEntity` does not override
   `IExistentialBoxable.BoxAsExistential1<TProtocol>()`; it inherits `Entity`'s, hardcoded to
   `ExistentialContainerFactory.Create<Entity, TProtocol>(this)` (`RealityFoundation.cs:103236`).
   Even with a descriptor, the witness lookup would be `Entity : HasAnchoring` (doesn't exist)
   rather than `AnchorEntity : HasAnchoring`.

**Fix.** Both halves are required:
(a) load the declaring/umbrella module's `TypeRecord` for cross-module protocols so
`ShouldEmitConformanceDictionary` stops silently skipping, **and**
(b) make the generated `BoxAsExistential1<TProtocol>()` dispatch on the *runtime* concrete type
(or override per-subclass so `AnchorEntity` emits `Create<AnchorEntity, TProtocol>`), populating
`AnchorEntity._protocolConformanceSymbols` with the real `IHasAnchoring` descriptor symbol.

**Same family — fix or confirm in the same pass:** RoomPlan `RoomCaptureView.Delegate`
(`RoomPlan.cs:5790`) is the RC‑PROXY secondary site and is presumed to share this shape; the
*session* delegate (`RoomCaptureSessionDelegateProxy`, `RoomPlan.cs:11539`) is the supported path
and is unaffected.

**Done when.** A BindingTests fixture that boxes a **subclass-only** cross-module conformance
(subclass conforms to a protocol the base does not) is green on sim + device, and the
RealityKit `Scene.AddAnchor/RemoveAnchor(IHasAnchoring)` per-package tests flip from `Skip` to
real passes on both lanes. The existing fixtures box on the declaring class itself and would not
have caught this.

## T1.2 — `FamilyActivityPicker` bridge packaging
*(was 05 §2)*

**What's blocked.** You can construct/read/persist/apply a `FamilyActivitySelection`, but you
cannot present the `FamilyActivityPicker` that produces one:
`FamilyActivityPickerSession.Create(…)` throws `DllNotFoundException("FamilyControlsBridge")`
(sim). The display-only `FamilyActivityTitleViewSession`/`IconViewSession` share the same import
and are presumed equally unreachable.

**Root cause.** The generator already emits both halves of the SwiftUI bridge into
`obj/.../swift-binding/` — C# `FamilyControls.SwiftUIBridge.cs`
(`[LibraryImport("FamilyControlsBridge", EntryPoint = "SBW_FamilyControls_FamilyActivityPicker_*")]`)
and Swift `FamilyControls.SwiftUIBridge.swift` (the matching `@_cdecl` trampolines). **But the
native `FamilyControlsBridge` library is never built or bundled.** `nm` across the built `.app`
shows the wrapper framework exports the normal `SBW_FamilyControls_*` symbols but **none** of the
`SBW_FamilyControls_FamilyActivityPicker_*` bridge symbols, and there is no `FamilyControlsBridge`
dylib/framework anywhere in the bundle: the bridge Swift is not fed to the wrapper's `swiftc`
invocation, and no separate `FamilyControlsBridge` build target exists. The fix was validated
only inside swift-bindings' own BindingTests harness (`CodableProfileEditorView`), which compiles
*all* generated sources into one module, so `@rpath`/library naming lines up there but not in the
packaged consumer build.

**Fix.** Teach the SDK build/pack pipeline to compile `*.SwiftUIBridge.swift` into a native
library named exactly `FamilyControlsBridge` (matching the C# `[LibraryImport]` name), bundle it
as a `NativeReference`.

**Done when.** A per-package end-to-end fixture in `swift-dotnet-packages` (UIHostingController +
programmatic selection round-trip) presents the picker and round-trips a selection on sim, so the
packaging path is gated — not just the emitter. The FamilyControls per-package picker test flips
from `Skip` to a real pass.

## T1.3 — HMAC`<H>` conformance-descriptor load on device — ✅ LANDED

Resolved 2026-05-31. The system-framework `@rpath` load now succeeds on device via a
`SwiftFrameworkResolver` bare-name fallback (`/System/Library/Frameworks/Name.framework/Name`
last in the ordered search list) plus a generator change (`ResolveRuntimeLibraryName`) that emits
the bare framework name for system targets so the NativeAOT DllImport resolver is consulted.
CryptoKit `HMAC<SHA256/384> incremental == one-shot` runs and asserts on device (NativeAOT) as
well as sim; the AOT-lane capability skip is removed. Full record:
[05 done ledger → HMAC on device](05-residual-gaps.md#hmac-device).

## T1.4 — `Transform(scale,rotation,translation)` ctor
*(was 05 §5, RC‑SIMD multi-param)*

**What's blocked.** The three-SIMD-param `Transform(scale:rotation:translation:)` ctor.
The indirect/pointer marshalling fix reached the SIMD *setters* and the `Transform(Matrix4x4)`
ctor (both landed, sim + device) but **not** this ctor, which still binds to the real Swift
symbol via `CallConvSwift` with the params passed **by value** (`init_C8B878FF` in the generated
source — no `@_cdecl` wrapper). Runtime: `InvalidProgramException` on the JIT lane.

**Fix.** Route this ctor's SIMD params through the same indirect path the setters use.

**Done when.** The RealityFoundation `Transform(scale,rotation,translation)` test flips from a
`Skip` probe to a real pass on sim + device.

---

# Tier 2 — fixable, lower impact (after Tier 1)

## T2.1 — typed mesh buffers on NativeAOT (RC-AOT)
*(was 05 §5)*

**What's blocked.** `MeshBuffer<T>` / `MeshBuffers.Semantic<T>` / `UnsafeForceEffectBuffer<T>`
generic-specialization metadata resolves on Mono/sim but not on NativeAOT/device — the
constraint-relaxation `T : Vector3` instantiation isn't rooted. Confirmed present on device this
pass (RealityFoundation device 29/0/11 — the 8 buffer entries are among the skips). The
RealityFoundation test capability-gates on `IsDynamicCodeSupported` (fail-if-regressed on Mono,
skip on AOT).

**Fix.** Root the `T : Vector3` generic-specialization metadata on NativeAOT. This is RC‑AOT's
harder case — it needs more than the `SwiftArray` template pattern Session 01 used.

**Done when.** The 8 buffer entries run and assert on device (the AOT-lane skip is removed).

## T2.2 — CryptoKit generic remainders
*(was 05 §5 + 00-overview deferred #1)*

Three related sub-items; the Ed25519/context-string **sign** mechanism already landed (see
[05 §5b](05-residual-gaps.md#data-return-csm)
and Verification debts below).

1. **HPKE construction (nested-conformer CSM).** All 10 `HPKE.Sender`/`HPKE.Recipient`
   initializers (`CryptoKit.swiftinterface:632-637`, `:644-649`) and, transitively,
   `HPKE.Sender.ExportSecret`, are SB0001 generic-only stubs. **Root cause:** every conformer of
   the key-constraining protocols (`Curve25519.KeyAgreement.PublicKey`,
   `XWingMLKEM768X25519.PublicKey`, the P256/P384/P521 KeyAgreement keys, …) has a **3+ component
   `ModuleQualifiedName`**, and the `ClassifyConformerStructurally` NestedType gate
   (`ConcreteProtocolSpecializationEmitter.cs:1858-1860`, a bare
   `ModuleQualifiedName.Split('.').Length > 2` check) rejects all of them. A hint-coverage
   extension does **not** help — this is structural CSM emission. **Fix:** lift the NestedType
   rejection so the CSM engine emits `From{Conformer}` factories for nested-type conformers
   (dedicated `src/docs/Future/csm-nested-conformer.md` track). **Done when:** a BindingTest
   round-trips an HPKE `Sender` once construction is reachable, and a CSM unit test asserts a
   3-part-`ModuleQualifiedName` conformer emits a factory.
2. **HPKE `Seal`/`Open`.** Broken alongside construction; `ExportSecret(byte[]/Data)` and KEM
   `Decapsulate` *do* have working concrete overloads.
3. **Context-string verify.** P256/etc. `IsValidSignature<S,D,C>` (the verify side) returns
   `Bool` — a direct return the [05 §5b](05-residual-gaps.md#data-return-csm) indirect-result
   preflight never gated, so it is a separate item from the sign side that landed in §5b.

## T2.3 — WorkoutKit range-alert Measurement ctor shim
*(was 05 §4)*

**What's blocked.** The `HeartRateRangeAlert` / `CadenceRangeAlert` / `PowerRangeAlert` /
`SpeedRangeAlert` ctors emit as callable signatures (the `SwiftClosedRange<Bound>` fix landed),
but each takes `SwiftClosedRange<Measurement<NSUnit…>>` and the Foundation projection
`Measurement<T>` is **value-only** — `.Value` getter, only `internal Measurement(IntPtr)`
(`Swift.Bindings.Apple/Sources/Foundation/Measurement.cs:136`). So the bounds can't be minted
from C# and the alerts are non-constructible end-to-end. (`Measurement<T>` being value-only is
**by design** — see Excluded; this item is the *targeted shim* that makes the alerts usable
anyway, not a generator change.)

**Fix.** A per-framework `@_cdecl` trampoline that builds `Measurement<Unit>(value:unit:)` from a
double + unit, mirroring the MusicKit array-shim pattern.

**Done when.** The four WorkoutKit range-alert ctors flip from `Skip` to real passes on sim.

## T2.4 — entity gesture device round-trip (Failure B) + parser genericSig
*(was 05 §5 Failure B + 00-overview deferred #3 — the campaign's one "L" item)*

**What's blocked.** `EntityTranslationGestureRecognizer` & friends deliver their target entity
through an `EveryEntityProtocol` existential. The recognizer *type* binds and its metadata
resolves; installing one and receiving a callback does **not** round-trip on real input. The
`EveryEntityProtocol : Entity` carrier is now *emitted* (generator unit tests cover emission),
but it is unreachable on real RealityFoundation input and the device round-trip is unverified.

**Root cause.** The routing gate `HasClassSuperclassRequirement` (`EveryProtocolEmitter.cs`)
reads `ProtocolDecl.InheritedProtocols`, which `SwiftABIParser.cs` `CreateProtocolDecl` populates
**only from `node.Conformances`**. Real Entity-rooted protocols encode the superclass **solely in
the generic signature** (`<Self : RealityKit.Entity>`) — `conformances` lists only
`Escapable`/`Copyable` and `superclassNames` is `None`. So the constraint never reaches
`InheritedProtocols`, the gate returns false, and `IHasAnchoring` & the 8 Entity-rooted protocols
fall through to plain interfaces. (Note: `03-proxy-callback.md`'s Failure B claim that these
"were being skipped via `HasClassSuperclassRequirement`" is **contradicted** by this — they were
never routed; correct that doc when this lands.)

**Fix.** Extend `CreateProtocolDecl` to extract a `genericSig` class-superclass constraint
(`<Self : SomeClass>`) into `InheritedProtocols` (or a dedicated superclass field the routing gate
reads). Then the carrier emits on real input and the round-trip becomes testable.

**Done when.** The Entity-rooted round-trip BindingTest from `03-proxy-callback.md` ("Failure B")
ships as a durable gate and the real RealityKit gesture round-trip passes on a **physical device**
(NativeAOT). Until the parser surfaces the constraint, a hermetic fixture would emit a plain
interface and assert nothing — so this fix cannot ship a green test before the parser change.

## T2.5 — witness-getter EntryPointNotFound to NotSupported wrap
*(was 05 §5d — pre-existing)*

**What's blocked.** A second shape the §5/§5b fail-clean change does **not** cover: the generator
emits the `Get_EveryProtocol_{P}_WitnessTable` accessor optimistically, the Swift wrapper then
fails to compile it (`value of type 'EveryProtocol' does not conform to specified type 'P'`) and
the give-up pass drops that `@_cdecl` from the dylib — but because the getter *was* emitted, the
C# proxy still P/Invokes it, yielding **`EntryPointNotFoundException`** at the CALLBACK boundary
instead of the clean `NotSupportedException`. Reproduced today by `ProtocolExtOptionalClassParam.swift`
(`PExtOptChildProtocol`); every gate stays green because nothing exercises that protocol's
C#-implementation CALLBACK path.

**Fix (needs a red fixture first).** Wrap the getter P/Invoke in `GetWitnessTableFromSwift()` so
`EntryPointNotFoundException` rethrows as `NotSupportedException` with a *generic* message ("the
Swift wrapper exports no witness-table accessor for protocol P …"). **Trade-off:** this also
catches a getter gone missing from an unrelated generator regression, turning a loud "symbol
missing" into a designed-limitation message. The build-time `does not conform` error stays loud,
so the masking risk is bounded but real — decide deliberately, with the `PExtOptChildProtocol`
CALLBACK red fixture in place first.

**Done when.** A red `PExtOptChildProtocol` CALLBACK fixture flips to asserting the clean
`NotSupportedException`, and unit + `binding-tests` (sim) + `--device` stay green.

## T2.6 — sibling emission-marker name-keying hardening
*(detail in [`sibling-marker-name-keying.md`](sibling-marker-name-keying.md))*

**What's blocked / latent.** The witness-table-getter marker was re-keyed to
`SwiftTypeName.ModuleQualifiedName`, but its sibling markers — **SetVtable**, **ObjCBase**,
**EntityBase**, **Conformance** — still key on the simple `.Name`. A local protocol and a
cross-module parent protocol with the same simple name can collide in the shared marker
set/dictionary and mis-gate a cross-module proxy. **Not a reproducing bug today** (no known
same-simple-name collision across the current validation/fixture set; cross-module-parent vtable
wiring uses a separate module-prefixed path), so it is latent.

**Fix.** Re-key the sibling markers to `ModuleQualifiedName`, per the safe-hardening plan in the
detail doc: SetVtable/ObjCBase/EntityBase are low-risk single-site re-keys; **Conformance is the
delicate one** (read at 3 sites incl. a cross-decl ancestor lookup — a naive swap can break
cross-module-parent proxy emission and reintroduce the MusicKit-class crash the witness-getter
work fixed).

**Done when.** A RED fixture (a dependency-module protocol whose simple name collides with a local
protocol, with differing setter/conformance emission) reproduces a dangling P/Invoke or wrong
carrier *before* the change, then goes green; unit + `binding-tests --compile-only` +
`--skip-regen` + `--device` all green.

---

# Verification debts (code landed, confirmation owed)

These are *not* new code — the mechanism shipped — but the campaign isn't honestly done until the
real-framework end-to-end is confirmed.

- **Data-return CSM concrete overloads (Ed25519 / context-string sign).** The generator mechanism
  landed and is pinned by fixtures (`Generics/SigningSpecialization.swift`,
  `SigningSpecializationTests.cs`, `ConcreteSpecializationEngineTests`) — see
  [05 §5b](05-residual-gaps.md#data-return-csm).
  **Owed:** a `nuke validate` sweep to confirm which real-CryptoKit `Signature<D>` /
  context-string `Signature<D,C>` overloads now bind to concrete `byte[]` overloads end-to-end.
- **`EveryEntityProtocol` carrier emission.** Emission is covered by generator unit tests, but the
  real device round-trip is unverified — folded into [T2.4](#t24--entity-gesture-device-round-trip-failure-b--parser-genericsig).

---

# Excluded — won't fix (the finish-line boundary)

Consciously parked. These are architectural or by-design limits, not debts — closing them is a
*different product* or contradicts a framework's own design. Full rationale in
[`00-overview.md`](00-overview.md) ("Explicitly out of scope"); summarized here so the boundary is
explicit in one place.

- **AppIntents `perform()` / authoring, ActivityKit Live Activities** (RC‑STRUCTURAL) — need a
  C#→Swift source-gen + macro-expansion subsystem (different product); both on the
  `swift-dotnet-packages` do-not-ship list.
- **WeatherKit** statistics/summaries and `weather(for:including:)` — 6-way method-own-generic
  `async` tuple return exceeds the CSM cartesian cap. Full-bundle `WeatherAsync` is the workaround.
- **TipKit result-builder DSL** (RC‑AEIC) — entrypoints are shimmable but the authoring
  experience is not restorable from C#.
- **`() -> [T]` result-builder closures + general SwiftUI composition** — the
  `@ViewBuilder`/result-builder wall.
- **RC‑SB0003 reverse witness dispatch** — case-by-case; many are by-design Swift limits; the
  forward (C#-implements) path works and is the supported mechanism.
- **RC‑CLOSURE `@autoclosure`** — no shipping-framework consumer; revisit only if one needs it.
- **RC‑PAT app-defined-conformer cases** (e.g. ProximityReader `requestDocument`) — CSM only works
  for Apple-finite conformer sets; app-defined → source-gen territory.
- **RC‑WILLSET** (RealityKit detached setter trap) — framework `willSet` precondition; no ABI
  route bypasses a Swift property observer. Session 03 landed a best-effort preflight guard + doc
  note; nothing more is generator-fixable.
- **`Measurement<T>` general value-only projection** — Foundation type behavior, not a binding
  defect. (The WorkoutKit-specific *shim* that works around it for range alerts is the actionable
  [T2.3](#t23--workoutkit-range-alert-measurement-ctor-shim), not a change to this projection.)

## References

- [`05-residual-gaps.md`](05-residual-gaps.md) — the done ledger (what landed and was validated).
- [`00-overview.md`](00-overview.md) — campaign framing and out-of-scope rationale.
- [`sibling-marker-name-keying.md`](sibling-marker-name-keying.md) — T2.6 categorical audit + plan.
- `src/docs/apple-framework-binding-gaps.md` — the underlying 2026-05-27 gap analysis (§6 fixability gameplan).
