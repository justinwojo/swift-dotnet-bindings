# FB-2 outcome — `[any P]` collections: Swift-proto DONE, @objc DEFERRED (scope flag)

**Status (2026-07-03):** FB-2's *Swift-protocol* element case is already complete and durably
covered. FB-2's *@objc-protocol* element case is **deferred as a scope flag** per the session
brief's escape hatch (`02-fb2-any-p-collections.md` lines 81–84: "if the fix is materially larger
than 'lift the gate + wire container element projection' … land the coherent subset that passes
all gates with tests, and document the remainder as a scope flag"). **No generator code changed
this session** — the generator is at the last-green state (`16ca7c6a`). This doc is the turnkey
revival record.

## What is already DONE (no work needed)

- **Swift-protocol `[any P]` — forward.** `[any Describable]` / `[String: any Sendable]` /
  `Result<T, any Error>` project in property-getter, method-return, and parameter position.
  Fixture: `Protocols/ExistentialCollectionProjection.swift` (`DescribableBag.contents`,
  `.allItems()`, `describeAll`) and the `SendableInfoBox` dictionary shapes.
- **Swift-protocol `[any P]` — reverse dispatch.** Witness-dispatched protocol requirements
  typed `[any P]` round-trip through a C# conformer via the EveryProtocol vtable. Fixture:
  `Protocols/RealityKitProtocolBugRepros.swift` — `var items: [any BugReproExistentialItem] { get }`,
  the class-bound `[any Marker]` array round-trip with its reverse driver, `[String: any Marker]`,
  and the nested `[[any Marker]]` / `[String: [String: any Marker]]` grids.
- **Scalar `@objc` `any P` (bare / `Optional<any P>`).** Already supported and runtime-exercised
  (`ObjCInterop/ObjCClassBoundExistential.swift`). The 8-byte object-pointer ABI marshals as a
  bare `IntPtr`.

So the Swift-protocol half of FB-2's "cover both an ObjC and a Swift protocol existential as the
element type" was already shipped before this session. The only net-new capability FB-2 could add
is the **@objc container-element** case.

## OPEN QUESTION a reviver MUST resolve first — is FB's protocol @objc or Swift?

The FB skip evidence (`AppLink.targets`/`.init`/`.appLink`, `AppLinkFactory.createAppLink`,
`AppLinkNavigation.navigationType`, `ShareMediaContent`) all report the string *"Bound generic
contains existential type argument 'any …Protocol'."* That message is emitted by **gate #1**
(`BoundGenericsHandler.IsContainerWithSupportedDirectExistential`), NOT gate #2. Two distinct
root causes produce it, and this session did NOT regenerate against the FB xcframework to
disambiguate (FB is not in BindingTests; the general fixtures are the contract, not FB):

- If `AppLinkTargetProtocol` is an **@objc** protocol → it is the Design-B / @objc-container case
  documented below (materially larger; deferred).
- If it is a plain **Swift** protocol → the general Swift-proto `[any P]` container capability is
  already covered (fixtures below prove it), so an FB skip would mean gate #1 rejects *this
  specific protocol* for a SEPARATE reason (most likely a missing/partial `TypeRecord` so the
  element isn't recognized as a supported existential). That is a NARROWER, likely-smaller bug
  than Design B — and it, not the @objc rewrite, would be the actual consumer-facing FB win.

**Revive by first regenerating the FB binding and reading which gate/branch rejects the member**
(is the element `@objc`? does its `TypeRecord` resolve?). Do not assume it is the @objc case.

## Why the @objc case is DEFERRED (materially larger than "lift the gate")

The committed, intentional contract is that `@objc` protocol existentials in a
container/tuple/closure position are **out of scope and must be dropped, fail-closed**. This is a
*durable regression gate*: `ObjCInterop/ObjCExistentialOutOfScopeGate.swift`
(`outOfScopeArrayObjC`, `outOfScopeDictionaryObjC`, `arrayProp`, …) exists so that a regression
which lets any of these emit again surfaces as a **compile-gate failure**, not a runtime crash in
a consumer. Enabling forward `@objc [any objcP]` therefore is not "lift a gate" — it is:

1. **Rewriting a durable fail-closed regression gate** (move the Array + array-property shapes in
   `ObjCExistentialOutOfScopeGate.swift` from "must drop" to "must emit + runtime-test", while
   keeping Dictionary / tuple / closure / async dropped).
2. **Two novel `@_cdecl` wrapper shapes** (`.map`-laundering, distinct from the reinterpret-only
   Swift-proto path — see Design B below). New marshalling code that, per the brief, **requires a
   physical-device (NativeAOT) run** to validate — existential/container marshalling is exactly
   where Mono and NativeAOT diverge. A single headless turn cannot soundly land + device-validate
   novel dual-direction @objc container marshalling.
3. **Entanglement with a pre-existing reverse-path hole** (below): the projection arms are shared
   between forward projection and the reverse receiver, so any @objc-array carrier change silently
   alters reverse behavior.

Codex + Grok were consulted in the design phase and both converged on **Design B** as the soundest
forward carrier. The forward design is understood; the blocker is scope size + the mandatory device
gate, not design uncertainty.

## Key finding — the reverse/interface path is existential-BLIND (latent hole)

`VtableLayoutBuilder.ClassifyMethod/ClassifyProperty/ClassifySubscript`
(`Emitter/StringEmitter/VtableLayout.cs:224/250/278`) and `MemberGateEvaluator.Evaluate*`
(`Emitter/StringEmitter/MemberGateEvaluator.cs`) consult **neither** gate:

- Gate #1 `BoundGenericsHandler.IsContainerWithSupportedDirectExistential` — admits
  `Array<any objcP>` for a *custom* @objc protocol (because `IsObjCExistentialBridgedProtocol`
  keys on Apple-framework `objcPrefixes`, so a non-Apple @objc protocol is not filtered out).
- Gate #2 `ExistentialHandler.HasUnsupportedObjCProtocolExistentialPosition` — the @objc-specific
  blocker. Enforced ONLY on the concrete-witness path (`MemberEmissionValidator.CanEmitMethod:661`,
  `CanEmitProperty:266`, `ShouldSkipMethodEmission:1138`, `MemberValidationPipeline` :622) and
  `ProtocolConformanceValidator`'s `:` conformance-keeping re-check.

Consequence: a Swift/ObjC protocol whose requirement is typed `[any objcP]` already (a) is declared
in the C# protocol interface and (b) receives an `Included` reverse-dispatch vtable slot, entirely
without gate #2 — its reverse receiver (`ProtocolProxyEmitter.Receivers.cs:2047/2122`) is emitted
today with the 40-byte `ExistentialContainer1` carrier against an 8-byte @objc element stride (a
buffer over-read). No current fixture exercises this shape, so it is latent, but it is a real
defect. (This is why the WIP `ExistentialProjection` @objc arms were reverted: they would have
flipped this reverse path from a loud over-read to a **silent leak** — trading one crash for
another, exactly what the brief warns against.)

**Good news for a future forward-only lift:** because the vtable membership never depended on
gate #2, lifting gate #2 behind a *forward-only opt-in* at the concrete sites does **not** cause a
vtable size-desync (the layout oracle is unaffected). The residual constraint is: keep
`ProtocolConformanceValidator`'s calls on the strict (non-opted-in) form so `:` conformance-keeping
stays strict.

## Design B (forward `@objc [any objcP]`) — turnkey revival spec

**Carrier:** C# `SwiftArray<IntPtr>` (trivial 8-byte element metadata), each element a +1-minted
object pointer. The `@_cdecl` wrapper reinterprets as `Array<UnsafeRawPointer>` (an exact metadata
match for trivial storage) then per-element `.map`-launders into a genuine `[any objcP]`.

Projection arms (were prototyped, reverted; re-apply to `Marshaler/Projection/ExistentialProjection.cs`):

- `ArrayElementCarrierType` → `"IntPtr"` when `_isObjCExistential` (checked BEFORE the class-bound arm).
- `GetArrayElementCarrierConversion` (forward PARAM): when `_isObjCExistential && _proxyClassName != null`,
  `ExistentialContainerFactory.CreateOwnedClassCarrier<{_publicType}>({elem}[, static __v => new {proxy}(__v)]).ClassRef`
  (mint +1; suppressed-proxy drops the wrap lambda).
- `GetOwnedReturnElementConversion` (forward RETURN): when `_isObjCExistential && _proxyClassName != null && !_proxyIsSuppressed`,
  `({_publicType})new {proxy}(new ExistentialContainer1 { Payload0 = {elem} }, ownsContainer: true)` (adopt +1).

Wrapper param branch — `Emitter/StringEmitter/CdeclParamMapper.cs` (insert BEFORE the generic-container
branch at ~:357): reinterpret + launder
`{label}.assumingMemoryBound(to: Swift.Array<UnsafeRawPointer>.self).pointee.map { Unmanaged<AnyObject>.fromOpaque($0).takeRetainedValue() as! (any {Module}.{objcP}) }`
(consumes the C#-minted +1 into a real `[any objcP]`; that array's destroy releases it → balanced).

Wrapper return branch — `Emitter/StringEmitter/MethodWrapperEmitter.cs` (the `needsResultPtr` arm at
~:714 AND the second return site ~:1385 — factor a shared helper):
`let __rawResult = result.map { Unmanaged.passRetained($0 as AnyObject).toOpaque() }; resultPtr.initializeMemory(as: Swift.Array<UnsafeMutableRawPointer>.self, repeating: __rawResult, count: 1)`
(each +1 survives the trivial `SwiftArray<IntPtr>` C# read into the adopting proxy → balanced).

Gate lift — add `ExistentialHandler.IsSupportedForwardObjCExistentialArray(TypeSpec, ITypeDatabase)`
(true ONLY for a top-level `Array<any objcP>` with a DIRECT element; NOT Dictionary/tuple/closure/nested).
Thread an opt-in `bool allowForwardObjCContainer = false` through the SHARED validators and pass
`true` ONLY from concrete-emission callers, keeping the default (strict) everywhere else:

- `ShouldSkipMethodEmission:1138` — exempt via the opt-in threaded from `ValidateMethodEmission`
  (concrete callers `ModuleHandler`/`IHandler` pass true; `CanEmitMethod:1410` and
  `ProtocolConformanceValidator` pass false → protocol methods stay rejected at `CanEmitMethod:661`).
- `CanEmitProperty:266` — opt-in; concrete handlers (`ClassHandler:300`, `FrozenStructHandler:354`,
  `NonFrozenStructHandler:230`, `EnumHandler:463/636`, `CollectionProjectionEmitter:527`) pass true;
  `ProtocolConformanceValidator:315/389` + `ProtocolExtensionDefaultsIndex:330` keep default false.
- `MemberValidationPipeline.ValidatePropertyEmission:622` — opt-in; `PropertyHandler:105` passes true,
  `ProtocolConformanceValidator:337/396` keeps default false.
- `CanEmitMethod:661` — UNCHANGED (the protocol/reverse gate; keeps reverse @objc closed).

## What still needs NEW plumbing (beyond forward)

- **Reverse `@objc [any objcP]`** is unsound with any current carrier: the type-generic reverse
  thunk hands `SwiftArray<IntPtr>` across as `Array<any objcP>` with no `.map` to launder → the
  trivial storage-destroy never releases the +1s (leak); the reverse setter reads +0 through a
  trivial subscript then adopts +1 (over-release/UAF). There is no sound 8-byte *retainable* @objc
  carrier today (`ClassExistentialContainer1` is 16-byte with a witness word → stride desync).
  Needs new runtime existential-container plumbing (a genuinely retainable single-word carrier, or
  an interface/vtable-path drop of @objc-array requirements added to BOTH `MemberGateEvaluator` and
  `VtableLayoutBuilder.Classify*` in lockstep to avoid the constraints.md vtable-size-desync SIGSEGV).
- **`@objc [String: any objcP]` (Dictionary), tuple/closure/async @objc** — stay out of scope
  (`ObjCExistentialOutOfScopeGate.swift` continues to enforce them dropped).

## Revival checklist

1. Re-apply the three `ExistentialProjection` arms + `CdeclParamMapper` + `MethodWrapperEmitter`
   branches + the `IsSupportedForwardObjCExistentialArray` gate + opt-in threading above.
2. Move the Array + array-property shapes in `ObjCExistentialOutOfScopeGate.swift` to a new
   supported+runtime-tested fixture (heterogeneous ≥2 conformers, forward round-trip + LifetimeTracker
   no-leak); keep Dictionary/tuple/closure/async in the out-of-scope gate.
3. Decide the reverse @objc story: either close the interface/vtable hole (drop @objc-array protocol
   requirements, lockstep MemberGateEvaluator + VtableLayoutBuilder) OR build a sound retainable carrier.
4. Gates: `nuke test`, `nuke binding-tests` (sim), **and `nuke binding-tests --device`** — the device
   run is mandatory for @objc container marshalling.
