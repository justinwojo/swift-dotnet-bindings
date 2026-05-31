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

## Release targeting — 0.12.0 vs 0.13.0

Tier 1 closing makes 0.12.0 shippable. The 0.12.0 release wave additionally pulls in the
Tier-2 items + verification debts that cover core consumer flows people would actually trip
on. The remainder defer to 0.13.0 and get a wiki *Known Limitations* note in the 0.12.0
release announcement.

**Targeting 0.12.0** (core-flow gaps + cheap verifications)

- ~~**T2.3** WorkoutKit range-alert ctor shim~~ — **LANDED (sim)** via the runtime-level
  `Measurement<T>(value, unit)` ctor + existing `SwiftClosedRange<Bound>(lower, upper)`;
  no per-framework shim was needed once the Foundation Measurement projection grew its
  public ctor. See [05 → WorkoutKit range-alert ctors](05-residual-gaps.md#workoutkit-range-alert-ctors).
- **T2.4** Entity gesture device verification — emitter routing already shipped; this is the
  device round-trip on real RealityKit gesture input, not new code.
- ~~Context-string verify per-package test~~ — **LANDED (sim)**; MLDSA65 round-trip
  (sign → verify-positive → verify-wrong-context) exercises the 3-PAT byte[]/Data
  cartesian on real CryptoKit. See
  [05 → MLDSA context-string verify](05-residual-gaps.md#mldsa-context-string-verify-sim).
- ~~Data-return CSM `nuke validate` sweep~~ — **DONE**; filtered `nuke validate --filter
  CryptoKit` regen confirms `MLDSA65.PrivateKey.Signature(data[, context])` binds to all
  byte[]/Data cartesian overloads (4 sign-side + 8 verify-side), and Ed25519 signing
  binds to `byte[]`. Recorded in
  [05 → Data-return CSM](05-residual-gaps.md#data-return-csm).

**Deferred to 0.13.0** (advanced / niche / latent / polish)

- **T2.1** RC-AOT typed mesh buffers on device — advanced mesh introspection only; sim works.
- **T2.2** HPKE construction — niche modern primitive; rest of CryptoKit binds correctly.
- **T2.5** Witness-getter `EntryPointNotFound`→`NotSupported` wrap (second shape) —
  error-quality polish; no shipping framework hits the second shape today.
- **T2.6** Sibling emission-marker name-keying hardening — latent, not reproducing.

## Status legend

- **LANDED** — code shipped, validated end-to-end on its named gated lane.
- **LANDED (sim) · device owed** — code shipped and sim passes; physical-device confirmation pending.
- **PARTIAL** — some of the required code shipped; specific sub-pieces still needed.
- **OPEN** — no code shipped yet; this is the next thing to start on.
- **VERIFICATION OWED** — code shipped + hermetic fixtures pin it; a real-framework
  end-to-end pass is still owed.

## At a glance

| Tier | Item | Framework | Status | Target |
|---|---|---|---|---|
| ~~1~~ | ~~T1.1 `Scene.AddAnchor(IHasAnchoring)`~~ | RealityKit / RealityFoundation | **LANDED** ([ledger](05-residual-gaps.md#class-bound-existential-device)) | 0.12.0 |
| ~~1~~ | ~~T1.2 `FamilyActivityPicker` bridge packaging~~ | FamilyControls | **LANDED** ([ledger](05-residual-gaps.md#familyactivitypicker-bridge)) | 0.12.0 |
| ~~1~~ | ~~T1.3 HMAC`<H>` conformance-descriptor load on device~~ | CryptoKit | **LANDED** ([ledger](05-residual-gaps.md#hmac-device)) | 0.12.0 |
| ~~1~~ | ~~T1.4 `Transform(scale,rotation,translation)` ctor~~ | RealityFoundation | **LANDED** ([ledger](05-residual-gaps.md#transform-three-simd-device)) | 0.12.0 |
| 2 | [T2.1](#t21--typed-mesh-buffers-on-nativeaot-rc-aot) RC‑AOT typed mesh buffers on NativeAOT | RealityFoundation | **OPEN** | 0.13.0 |
| 2 | [T2.2](#t22--cryptokit-generic-remainders) CryptoKit generic remainders (HPKE construction + Seal/Open reach) | CryptoKit | **PARTIAL** — instance-method side landed; HPKE init still blocked | 0.13.0 |
| ~~2~~ | ~~T2.3 WorkoutKit range-alert `Measurement<T>` ctor shim~~ | WorkoutKit | **LANDED (sim)** ([ledger](05-residual-gaps.md#workoutkit-range-alert-ctors)) | 0.12.0 |
| 2 | [T2.4](#t24--entity-gesture-device-round-trip-failure-b) Entity gesture device round-trip (Failure B) | RealityKit | **PARTIAL** — emitter routing + carrier emission landed; device round-trip unverified | **0.12.0** |
| 2 | [T2.5](#t25--witness-getter-entrypointnotfound-to-notsupported-wrap-second-shape) Witness-getter `EntryPointNotFound`→`NotSupported` wrap (second shape) | generator | **OPEN** | 0.13.0 |
| 2 | [T2.6](#t26--sibling-emission-marker-name-keying-hardening) Sibling emission-marker name-keying hardening | generator | **OPEN** | 0.13.0 |

---

# Tier 1 — closed

All four Tier-1 items are LANDED on their gated lanes (sim + device). The gap-fix campaign
reaches the doc's definition of "finishable" — Tier 2 and the verification debts remain, but
per the [overview](#definition-of-done) those do not keep the campaign open. Full records in
the [done ledger](05-residual-gaps.md); pointers below.

- **T1.1 — `Scene.AddAnchor(IHasAnchoring)` (Failure A).** Class-bound existential boxing for
  cross-module subclass conformers landed in `8099d434`. Sim and device (iPhone 13 NativeAOT,
  2026-05-31) pass `Scene.AddAnchor/RemoveAnchor(IHasAnchoring) round-trip`,
  `Scene.Anchors traversal + AnchorEntity construction`, and
  `AnchorEntity boxes as IHasAnchoring existential`. The durable BindingTests fixture
  `TestSubclassOnlyConformanceBoxesAsDerivedType` is green on both lanes. Full record:
  [05 → class-bound existential](05-residual-gaps.md#class-bound-existential-device). The
  RoomPlan `RoomCaptureView.Delegate` (`RoomPlan.cs:5790`) RC-PROXY secondary site has no
  per-package test today; the read-only-proxy CALLBACK fail-clean shape is gated by the
  portable `EntityRootedExistential` fixture and the *session* delegate (the supported path)
  is unaffected.
- **T1.2 — `FamilyActivityPicker` bridge packaging.** The SDK build/pack pipeline already
  detects `*.SwiftUIBridge.swift`, compiles it into `{Module}Bridge.xcframework` via
  `_CompileSwiftUIBridge`, injects it as a `NativeReference` (locally via
  `_ResolveSwiftNativeReferences`, in packed consumers via the synthesized
  `{PackageId}.targets`), and bundles it under `runtimes/{rid}/native/` at pack time
  (`3d62df46`). The FamilyControls per-package
  `FamilyActivityPicker bridge create + selection JSON round-trip` test passes on sim and
  device (iPhone 13 NativeAOT, 2026-05-31). Full record:
  [05 → FamilyActivityPicker bridge](05-residual-gaps.md#familyactivitypicker-bridge).
- **T1.3 — HMAC`<H>` conformance-descriptor load on device.** Resolved 2026-05-31
  (`ede9a029`) via `SwiftFrameworkResolver` bare-name fallback +
  `ResolveRuntimeLibraryName` system-target reduction. Full record:
  [05 → HMAC on device](05-residual-gaps.md#hmac-device).
- **T1.4 — `Transform(scale,rotation,translation)` ctor.** Parser `@inlinable` access-control
  fix landed. Sim and device (iPhone 13 NativeAOT, 2026-05-31) pass the RealityFoundation
  `Transform(scale,rotation,translation) constructor` test. Full record:
  [05 → Transform three-SIMD on device](05-residual-gaps.md#transform-three-simd-device).

---

# Tier 2 — fixable, lower impact (after Tier 1)

## T2.1 — typed mesh buffers on NativeAOT (RC-AOT) — OPEN

**Status:** OPEN. No code has landed for this item.

**Release target:** **deferred to 0.13.0.** Advanced mesh-buffer introspection only; sim path
already works. Ships in 0.12.0 with a wiki *Known Limitations* entry.

**What's blocked.** `MeshBuffer<T>` / `MeshBuffers.Semantic<T>` / `UnsafeForceEffectBuffer<T>`
generic-specialization metadata resolves on Mono/sim but not on NativeAOT/device — the
constraint-relaxation `T : Vector3` instantiation isn't rooted. Confirmed present on device this
pass (RealityFoundation device 29/0/11 — the 8 buffer entries are among the skips). The
RealityFoundation test capability-gates on `IsDynamicCodeSupported` (fail-if-regressed on Mono,
skip on AOT).

**Fix to land.** Root the `T : Vector3` generic-specialization metadata on NativeAOT. This
is RC‑AOT's harder case — it needs more than the `SwiftArray` template pattern Session 01
used.

**Done when.** The 8 buffer entries run and assert on device (the AOT-lane skip is removed).

## T2.2 — CryptoKit generic remainders — PARTIAL

**Status:** PARTIAL. The structural CSM gates (NestedType, indirect-`Data` return) have all
landed and the instance-method side now binds to concrete overloads end-to-end. **HPKE
construction is the one remaining real blocker** — and its root cause has changed since the
0.12.0 retrospective. What follows reflects the regen'd `CryptoKit.cs` as of `ff2bafbb`.

**Release target:** **deferred to 0.13.0.** HPKE is a niche modern primitive; the rest of
CryptoKit (signatures, HMAC, AEAD, KDFs, hashing, key exchange) binds correctly today. Ships
in 0.12.0 with a wiki *Known Limitations* entry.

### What shipped (don't redo)

- **NestedType structural gate LIFTED** (`3cd6d0f4`). The
  `ConcreteProtocolSpecializationEmitter.ClassifyConformerStructurally` reject for 3+ segment
  `ModuleQualifiedName`s now passes when the type record resolves. Proven by the regen:
  `FromCryptoKit_Insecure_SHA1` and `FromCryptoKit_Insecure_MD5` factories (3-segment
  `ModuleQualifiedName`) emit in `CryptoKit.cs`.
- **HPKE `Seal` / `Open` / `ExportSecret` instance-method specialization LANDED** (`3cd6d0f4` +
  `44a6002b`). HPKE.Sender now exposes concrete `Seal(byte[]/Foundation.Data, byte[]/Foundation.Data)`
  and `ExportSecret(byte[]/Foundation.Data, nint)` overloads (regen'd
  `CryptoKit.cs:22878-23371`). HPKE.Recipient analogous (`23372-`). These are **unreachable in
  practice today** because HPKE construction is still blocked — see Open below.
- **Data-return CSM concrete overloads LANDED** (`44a6002b`). Ed25519 signing and the sign-side
  of context-string `Signature<D,C>` now bind to concrete `byte[]` via the `InlineSwiftStruct`
  preflight admit (see [05 §5b](05-residual-gaps.md#data-return-csm)).
- **Context-string verify (`Bool` return) LANDED + GRADUATED.** The 8 concrete 3-PAT cartesian
  `IsValidSignature(signature, data, context) → bool` overloads emit on
  **`MLDSA65.PublicKey`** and `MLDSA87.PublicKey` (not the ECDSA P256/P384/P521 surfaces;
  Apple's API does not expose a context parameter on ECDSA verify). End-to-end per-package
  test ships in CryptoKit/tests/Tests.cs (`MLDSA65 context-string verify round-trip` —
  sign + verify-matching-context + verify-wrong-context, sim PASS). See
  [05 → MLDSA context-string verify](05-residual-gaps.md#mldsa-context-string-verify-sim).

### What's still open

**HPKE construction (T2.2 #1) — OPEN, with a NEW root cause.**

All 10 `HPKE.Sender` / `HPKE.Recipient` initializers (`CryptoKit.swiftinterface:632-637`,
`:644-649`) and, transitively, the user-reachable use of `HPKE.Sender.ExportSecret` and
`Seal`/`Open`, are still SB0001 / dropped stubs. The regen'd `CryptoKit.cs` shows each init
dropped with:

```
// Unsupported: method 'init' — parameter or return type not yet supported
//   (C# does not support generic constructors with method-own type parameters.)
```

**Old root cause (now stale):** the `ClassifyConformerStructurally` NestedType gate
(`ConcreteProtocolSpecializationEmitter.cs:1858-1860`). **This is no longer the blocker.**

**New root cause:** the CSM specialization path runs for instance methods (`Seal`, `Open`,
`ExportSecret` — concrete overloads emit on HPKE.Sender/Recipient), but it does **not**
run for initializers carrying method-own generic type parameters. Inits fall through the
"C# does not support generic constructors with method-own type parameters" arm and drop.
The structural NestedType lift was necessary but not sufficient — what's needed is an
init-specialization path that emits a non-generic `From{Conformer}` static factory per
conformer (the same shape `Seal`/`ExportSecret` now use, applied at the constructor site).

**Fix to land.** Extend the CSM specialization engine to emit
`public static Sender From{Conformer}(...)` factories for method-own-generic inits — per
conformer of the key-constraining protocols
(`Curve25519.KeyAgreement.PublicKey`, `XWingMLKEM768X25519.PublicKey`, the P256/P384/P521
KeyAgreement keys, …). The conformer set is the same one already exercised for HPKE.Sender's
instance-method specialization, so this is reusing existing conformer enumeration in a new
context (init), not discovering a new one.

**Done when.** A BindingTest round-trips an HPKE `Sender` end-to-end (construct → Seal →
Open via a Recipient), and a CSM unit test asserts a 3+-segment-`ModuleQualifiedName`
conformer emits a *constructor* factory (not just an instance-method factory). At that
point HPKE.Sender's `Seal` / `ExportSecret` concrete overloads — already emitted — become
reachable in practice.

<a id="t23--workoutkit-range-alert-measurement-ctor-shim--landed-sim"></a>

## T2.3 — WorkoutKit range-alert Measurement ctor shim — LANDED (sim)

**Status:** LANDED on sim. Full record in
[05 → WorkoutKit range-alert ctors](05-residual-gaps.md#workoutkit-range-alert-ctors).

The premise of this item — that `Measurement<T>` is value-only in the Foundation
projection and needs a per-framework `@_cdecl` shim — became stale once the runtime-level
`Measurement<T>(double value, T unit)` ctor + `SBW_Measurement_InitFromValueUnit` shipped
in `Swift.Bindings.Apple/Sources/Foundation/Measurement.cs`. Combined with the existing
`SwiftClosedRange<Bound>(lower, upper)` ctor, the four WorkoutKit range-alert types
(`HeartRateRangeAlert`, `CadenceRangeAlert`, `PowerRangeAlert`, `SpeedRangeAlert`) are
constructible end-to-end from C# with no per-framework Swift trampoline. The four
per-package tests flipped from `Skip` to real passes on sim
(`apple-frameworks/WorkoutKit/tests/Tests.cs`). Device round-trip still owed.

<a id="t24--entity-gesture-device-round-trip-failure-b"></a>

## T2.4 — entity gesture device round-trip (Failure B) — PARTIAL

**Status:** PARTIAL. The emitter routing + carrier emission landed; the device round-trip
on real RealityKit input is still unverified.

**Release target:** **0.12.0.** Entity gestures are how RealityKit apps wire user
interaction (drag-to-move, pinch-to-scale, tap-to-place). The remaining work is verification,
not new code — install a recognizer on a real RealityKit scene on physical device and
confirm the callback fires through the `EveryEntityProtocol` existential.

### What shipped (don't redo)

- **Routing-gate fix LANDED via the emitter side, not the parser** (`8099d434`).
  `EveryProtocolEmitter.HasClassSuperclassRequirement` (lines 5001-5018) now reads the
  protocol's `GenericSignature` directly and matches `IsRealityFoundationEntityName`, so
  `<Self : RealityKit.Entity>` constraints route through the class-bound path even though
  `node.Conformances` lists only `Escapable`/`Copyable`. (The doc previously proposed
  extending `SwiftABIParser.CreateProtocolDecl` to populate `InheritedProtocols`; the
  implementation took the equivalent route of reading `GenericSignature` at the gate.)
- **`EveryEntityProtocol : Entity` carrier emission covered by generator unit tests.** The
  carrier emits on real Entity-rooted RealityFoundation input.
- **Read-only proxy CALLBACK fail-clean LANDED** (`44a6002b`, see
  [05 §5b](05-residual-gaps.md)). The C#-implements-protocol direction for any class-superclass
  read-only proxy now throws clean `NotSupportedException` instead of crashing.

### What's still open

**Device round-trip verification.** Installing an `EntityTranslationGestureRecognizer` &
friends on a real RealityKit scene and receiving a callback through the
`EveryEntityProtocol` existential — on a **physical device (NativeAOT)** — has not been
exercised. Hermetic fixtures (`EntityRootedExistential.swift` /
`EntityRootedExistentialTests.cs`) pin the carrier shape but use a pure-Swift `Entity`
stand-in; real RealityKit gesture input has not been tested.

**Fix to land.** None expected if the emitter/routing shipped correctly. If the device
round-trip fails, the failure mode is the work: surface it, root-cause it, and pin it with
a fixture.

**Done when.** The Entity-rooted round-trip BindingTest from `03-proxy-callback.md`
("Failure B") ships as a durable gate **and** the real RealityKit gesture round-trip passes
on a **physical device (NativeAOT)**.

## T2.5 — witness-getter `EntryPointNotFound` to `NotSupported` wrap (second shape) — OPEN

**Status:** OPEN for the *second* shape (see below). The *first* shape (class-superclass,
generator decides upfront not to emit the getter) shipped in `44a6002b` and is recorded in
[05 §5b](05-residual-gaps.md) — confirm you're not redoing that work.

**Release target:** **deferred to 0.13.0.** Error-quality polish for a fixture-only repro
(`ProtocolExtOptionalClassParam.swift`); no shipping Apple framework hits the second shape
today. The masking trade-off (catching unrelated regressions as designed-limitation
messages) warrants a deliberate pass with a RED fixture, not a pre-release sprint.

**What's still open — the second shape.** The §5/§5b fail-clean change covers the case
where the generator decides upfront NOT to emit `Get_EveryProtocol_{P}_WitnessTable`. It
does **not** cover a different, pre-existing failure mode: the generator emits the
witness-getter optimistically, the Swift wrapper then fails to compile it (`value of type
'EveryProtocol' does not conform to specified type 'P'`), and the wrapper give-up pass
drops that one `@_cdecl` from the dylib — but because the getter *was* emitted by the
generator, its emission marker is set, so the C# proxy still emits the
`[LibraryImport(... "Get_EveryProtocol_P_WitnessTable")]` and P/Invokes it at runtime,
yielding **`EntryPointNotFoundException`** at the CALLBACK boundary instead of the clean
`NotSupportedException`.

**Reproduces today** with `ProtocolExtOptionalClassParam.swift` (`PExtOptChildProtocol`);
every gate stays green because nothing exercises that protocol's C#-implementation
CALLBACK path.

**Fix to land (needs a red fixture first).** Wrap the getter P/Invoke in
`GetWitnessTableFromSwift()` so `EntryPointNotFoundException` rethrows as
`NotSupportedException` with a *generic* message ("the Swift wrapper exports no
witness-table accessor for protocol P …"). **Trade-off:** this also catches a getter gone
missing from an unrelated generator regression, turning a loud "symbol missing" into a
designed-limitation message. The build-time `does not conform` error stays loud, so the
masking risk is bounded but real — decide deliberately, with the `PExtOptChildProtocol`
CALLBACK red fixture in place first.

**Done when.** A red `PExtOptChildProtocol` CALLBACK fixture flips to asserting the clean
`NotSupportedException`, and unit + `binding-tests` (sim) + `--device` stay green.

## T2.6 — sibling emission-marker name-keying hardening — OPEN

**Status:** OPEN. The witness-table-getter marker was re-keyed to `ModuleQualifiedName` in
`44a6002b` (see [`sibling-marker-name-keying.md`](sibling-marker-name-keying.md)), but its
sibling markers — **SetVtable**, **ObjCBase**, **EntityBase**, **Conformance** — still key
on the simple `.Name`. A local protocol and a cross-module parent protocol with the same
simple name can collide in the shared marker set/dictionary and mis-gate a cross-module
proxy. **Not a reproducing bug today** (no known same-simple-name collision across the
current validation/fixture set; cross-module-parent vtable wiring uses a separate
module-prefixed path), so it is latent.

**Release target:** **deferred to 0.13.0.** Latent hazard with no reproducer; pure
categorical-hardening pass.

**Fix to land.** Re-key the sibling markers to `ModuleQualifiedName`, per the
safe-hardening plan in the detail doc: SetVtable/ObjCBase/EntityBase are low-risk
single-site re-keys; **Conformance is the delicate one** (read at 3 sites incl. a
cross-decl ancestor lookup — a naive swap can break cross-module-parent proxy emission and
reintroduce the MusicKit-class crash the witness-getter work fixed).

**Done when.** A RED fixture (a dependency-module protocol whose simple name collides with
a local protocol, with differing setter/conformance emission) reproduces a dangling
P/Invoke or wrong carrier *before* the change, then goes green; unit +
`binding-tests --compile-only` + `--skip-regen` + `--device` all green.

---

# Verification debts (code landed, confirmation owed)

These are *not* new code — the mechanism shipped — but the campaign isn't honestly done
until the real-framework end-to-end is confirmed.

- **`EveryEntityProtocol` carrier on real input.** *(Targeting 0.12.0 — folded into
  T2.4.)* Emission is covered by generator unit tests and the routing-gate fix landed; the
  real device round-trip is unverified — see
  [T2.4](#t24--entity-gesture-device-round-trip-failure-b).

*(Data-return CSM sweep + context-string verify per-package test were paid in this pass
and graduated to [05 → Data-return CSM](05-residual-gaps.md#data-return-csm) /
[05 → MLDSA context-string verify](05-residual-gaps.md#mldsa-context-string-verify-sim).)*

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
  defect. (The runtime-level `Measurement<T>(double, T)` ctor that makes WorkoutKit range alerts
  constructible from C# is a *targeted* surface, not a general round-trippable
  `Measurement<T>` — see [T2.3](#t23--workoutkit-range-alert-measurement-ctor-shim--landed-sim).)

## References

- [`05-residual-gaps.md`](05-residual-gaps.md) — the done ledger (what landed and was validated).
- [`00-overview.md`](00-overview.md) — campaign framing and out-of-scope rationale.
- [`sibling-marker-name-keying.md`](sibling-marker-name-keying.md) — T2.6 categorical audit + plan.
- `src/docs/apple-framework-binding-gaps.md` — the underlying 2026-05-27 gap analysis (§6 fixability gameplan).
