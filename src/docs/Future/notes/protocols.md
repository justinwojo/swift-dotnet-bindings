# Protocols and reverse dispatch — reference notes

Searchable findings to consult when working in this area. Nothing here is queued or newly authorized.
[How to use and maintain these notes](README.md). Recorded evidence and counts describe their original investigation; recheck against current source before acting.

<a id="od-w2-1-protocol-forward-view-vs-implementability-public-api-split"></a>

## OD-W2-1 — protocol forward-view vs implementability public-API split

Open wave-2 design decision (wave-2 overview OD-W2-1; wave-1/2 session docs were local-only and have been removed). Whether the public C# surface should split a protocol's *forward view* (call-through-existential) from its *implementability* (implement-from-C#) when capability modelling makes the two diverge. No shipping surface forces the call today. **Trigger:** capability modelling makes call-via-existential vs implement-from-C# diverge on a shipping surface.

<a id="a-narrowed-int-uint-read-through-a-protocol-interface-has-no-native-width-companion"></a>

## A narrowed `Int`/`UInt` read through a protocol interface has no native-width companion

<details>
<summary>Recorded evidence and disposition</summary>

Rider on [the native-width accessor decision](../../Design/decisions.md#native-width-accessors-and-initializer-overloads). Adding a **public interface member** is a pending surface decision because consumer implementations must satisfy it. A protocol requirement typed from Swift `Int`/`UInt` is narrowed to `int`/`uint` the same way a concrete property is (`ProtocolHandler.cs` narrowing gate), and the proxy getter that reads it refuses to truncate — but it throws with `nativeMemberName: null` (`ProtocolProxyEmitter.InterfaceImpl.cs`, the blittable-getter arm), because no `{Name}Native` companion is declared on the interface. So the message tells the caller the Swift declaration is pointer-width and stops there, where the concrete-type read names an accessor to use instead. Adding the companion means adding a member to a generated *interface*, which every C# implementation of that protocol would then have to satisfy — a source-breaking change to the implement-from-C# surface, and the reason this is not a mechanical extension of the concrete-type work. Cheaper half-measures exist (a companion on the proxy class only, reachable by a caller holding the concrete proxy but not through the interface) and are equally a surface call. **Not authorized. Trigger:** a consumer reports an `OverflowException` on a protocol-typed read with no way to get the wide value, or the owner settles the interface-surface question raised by OD-W2-1 (forward view vs implementability), which this is a concrete instance of.

</details>

<a id="family-fold-vs-the-class-lane-a-design-fork"></a>

## Family fold vs. the class lane — a design fork

The protocol lane folds a disambiguated family onto one shared name, which renames a *non-colliding* protocol sibling (`RoomDidFinishWithError`) that the class lane leaves bare, so a Swift class conforming to that protocol would lose the conformance. Not reachable today — the fixture has no conforming class, only an existential harness — and both reviewers warned against generalizing the fold to the other lanes. The fork is retire the fold vs. mirror it in the class lane; either arm moves published names, so it was not taken unilaterally. **Trigger:** a real library declares a class conforming to a protocol whose family the fold renamed, or the owner picks an arm.

<a id="whether-a-reverse-dispatch-inert-interface-should-be-removed-rather-than-marked"></a>

## Whether a reverse-dispatch-inert interface should be removed rather than marked

Option (a) shipped (`a40f87c4`): a protocol proxy whose vtable fills zero slots is no longer registered, and its emitted interface carries a warning-level `[Obsolete(DiagnosticId = "SB0010")]`, so the trap is visible at compile time while the type stays usable in the forward direction. Option (b) — suppressing the interface entirely — was considered and rejected: it removes surface from the manifest and breaks forward-direction code that works today. Recommendation: leave (a) in place; absent an answer, it stands. Evidence: docs-pass breakdown item B2. **Trigger:** the owner decides the marked-but-inert interface is worse than no interface at all, or a consumer implements one and is surprised despite the diagnostic.

<a id="protocol-extension-callbacks-with-optional-closure-parameters-stay-fail-closed"></a>

## Protocol-extension callbacks with `Optional<Closure>` parameters stay fail-closed

2026-09 wave (s1). `ProtocolExtensionEmitter.IsCdeclCompatibleType` used to accept `Optional<Closure>` and would have bound it against a mismatched carrier; the wave closed it fail-closed, so a protocol-extension method taking an optional closure (`sumWithOptionalCallback` in the BindingTests fixture) now emits nothing rather than binding wrongly. Routing it into the protocol-extension closure bridge with an optional-aware carrier is a separate feature: the two-word `Optional<Closure>` read on the bridge's `inout` path (the reabstraction trap recorded in memory) plus a nil arm in the delegate marshaller. **Not authorized** — funding call. **Trigger:** a consumer needs an optional-closure protocol-extension member, or the closure bridge is next reworked.

<a id="array-backed-collection-projection-reads-the-backing-array-not-the-collection-s-index-range"></a>

## Array-backed collection projection reads the backing array, not the collection's index range

The projection prefers a public `[Element]` property over the Collection witness when a conformer publishes both, and then reports that array's `Count` and indexes it directly. That agrees with the collection for every conformer whose index range covers the whole array (all of MusicKit's and ours), and diverges for one that publishes its full buffer while windowing over part of it — `Count` would be the buffer's length and `view[0]` the buffer's first element rather than the window's. Not repaired with the zero-based index fix: nothing static distinguishes the two shapes, so the only faithful fix is to prefer the witness path whenever the witness shape exists, which mints a native shim for every array-backed collection in the Apple supplement and the validation corpus — disproportionate to a shape with no observed instance. **Trigger:** a conformer that publishes both a full backing array and a narrower index range turns up in the corpus or a consumer report.

<a id="enumerating-a-forward-only-swift-collection-through-the-projection-is-quadratic"></a>

## Enumerating a forward-only Swift `Collection` through the projection is quadratic

The witness-backed indexer reaches element *n* by asking the collection for its `count` and then walking `index(startIndex, offsetBy: n)`. A `RandomAccessCollection` answers both in constant time, so the projected surface costs what it always did; a conformer that declares only `Collection` (just `index(after:)`) pays an O(n) walk per access, and the emitted enumerator — which requests each position independently — is O(n²) over the whole collection. Not repaired here because index arithmetic is the only correct translation available: a Swift `Collection`'s `Int` index space is not required to be contiguous, so `startIndex + offset` is not a substitute. Making enumeration linear means a different native contract — a bulk copy-out, or a cursor the enumerator advances — which replaces the projection's single-call/single-lease design rather than adjusting it. Everything published on the Apple surfaces and in the corpus today is random-access. **Trigger:** a forward-only `Collection` conformer large enough to matter turns up in the corpus or a consumer report.

<a id="sb0010-false-negative-on-all-objc-optional-protocols"></a>

## `SB0010` false negative on all-`@objc optional` protocols

A protocol whose every requirement is `@objc optional` emits an empty local vtable but is still registered, with no `[Obsolete(DiagnosticId = "SB0010")]` on the interface. Implementing it compiles and never fires — the consumer discovers it at runtime. Observed on StripePaymentsUI `ISTPPaymentCardTextFieldDelegate` (12 optional requirements, empty `LocalVTable`, still registered). The hollowness check keys off "all requirements were skipped" rather than "emitted vtable has zero filled slots"; an `@objc optional` requirement counts as present-but-defaulted. Sibling `STPCardFormViewDelegate` (one non-optional) and the Issuing ephemeral-key providers (all skipped) are classified correctly. Re-key hollowness to slot count. **Trigger:** a consumer implements such a delegate and reports that callbacks never fire, or the next reverse-dispatch honesty pass.

<a id="multi-pat-existential-boxing"></a>

## Multi-PAT existential boxing

A type conforming to 2+ PAT protocols cannot box through the `object` fallback because the `typeof(object)` dictionary key is ambiguous. Guarded to fail explicitly (`InvalidCastException`) rather than silently select the wrong witness table. Extremely rare in practice. **Trigger:** the guard's `InvalidCastException` is reported from a real consumer or a corpus library, which is the only evidence that the shape occurs outside fixtures.

<a id="protocol-side-dedup-ignores-argument-labels-instance-methods-fixed-residual-statics"></a>

## Protocol-side dedup ignores argument labels — INSTANCE methods FIXED; residual = statics

<details>
<summary>Recorded evidence and disposition</summary>

`ProtocolSignatureHelper.GetMethodSignatureKey` keys witnesses by name + positional types only. For **instance** requirements this is now resolved: two requirements differing only by argument label (`conversationManager(_:didActivate:)` vs `(_:didDeactivate:)`) are disambiguated into distinct ObjC-selector-style C# members by `ProtocolMethodDisambiguator`, threaded through every interface/proxy/receiver/validator/forward-witness site. Pinned by `ProtocolHandlerOutputTests.Emit_ProtocolWithLabelOnlyOverloads_DisambiguatesWithLabelDerivedNames`, `WitnessDispatchEmitterTests.OverloadDisambiguation_LabelOnlyOverloadPair_SplitsIntoTwoSlots_TrailingMethodShifts`, and the `DuplicateSignatureDisambiguation` BindingTests fixture (reverse-dispatch identity, pair + triple). **Two residuals remain by design:** (1) **static** requirements that differ only by label still collapse (second dropped as `DuplicateSignature`) — deliberately, since statics have no reverse-dispatch/vtable/witness path, any static *method* requirement disables the whole protocol's EveryProtocol conformance (unconditional skip; a lone static *property* skips only in the later no-instance-members gate), and both the surviving interface member and its proxy stub only throw; a selector rename on the interface alone would break the conformance validator's static name-parity gate vs a concrete conformer's numeric-suffix static names, so a faithful fix is a unified static naming-policy change (session 08 territory), not a disambiguator extension. Reasoning inlined at `ProtocolHandler.cs` static branch. (2) Pure **type-erasure** collapses (same labels, distinct Swift types projecting to one C# type) still collapse intentionally. **Trigger to reopen (1):** a real library needing both label-only static requirements AND working static dispatch.

</details>

<a id="inout-objc-bridgeable-reverse-dispatch-keeps-a-dead-vtable-slot"></a>

## Inout ObjC-bridgeable reverse-dispatch keeps a dead vtable slot

**Revisit when:** the next reverse-dispatch vtable-layout change, or a real protocol requirement with an inout ObjC-bridgeable param.

<details>
<summary>Recorded evidence and disposition</summary>

A reverse-dispatch protocol method with an `inout` ObjC-bridgeable param (`URL`/`URLRequest`/`Decimal`) routes its EveryProtocol witness to a `fatalError` trap stub (`EmitInOutObjCBridgeableMethodStub`) — the dangling-writeback `EmitMethodImplementation` would otherwise emit (`{p}Ref`/`{p}Copy` mismatch) doesn't compile. Unlike the four sibling stub categories (non-dispatchable closure, method-level generic, Self-typed, mixed-generic), it intentionally RETAINS its vtable slot: the trapping witness never reads it and the C# receiver compiles via the ordinary objc-param path (drops the inout), so both the Swift vtable struct and the C# vtable buffer stay in lock-step and the slot is dead-but-harmless. Pinned by `EmitProtocolVtableStruct_InOutObjCBridgeableParam_RetainsVtableSlot`. Making it fully pattern-consistent (skip the slot) is a 3-site lock-step change — the Swift vtable pass (`EveryProtocolEmitter.cs`), `ProtocolVtableMembers.IncludesMethod` (static; needs `TypeDatabase` threaded through its 5 call sites in Vtables.cs/StaticInit.cs/Receivers.cs), and `ProtocolHandler`'s same-module skip set — plus same/cross-module layout tests. Deferred as disproportionate to a cosmetic slot on a near-unreachable signature; no active repro. Follow-ups when revisited: a vtable-index assertion for the trap-stub case, and de-duplicating the four `Emit*Stub` signature-rendering loops (they also diverge on the instance-vs-static parameter-label predicate and lack async/throws renderer coverage). **Trigger to revisit:** the next reverse-dispatch vtable-layout change, or a real protocol requirement with an inout ObjC-bridgeable param.

</details>

<a id="conformance-aware-naming-for-mutability-divergent-witnesses"></a>

## Conformance-aware naming for mutability-divergent witnesses

A protocol requirement and its concrete witness derive their C# method names independently, each from its own `IsMutating`. Swift legally lets a NON-mutating witness satisfy a `mutating` requirement (always so for a class witness; legal-but-unusual for a struct). For a zero-arg, noun-only, value-returning requirement — the shape that gets the mutating-aware `Get` prefix gate — this diverges: the `mutating` requirement projects to bare `FooAsync` while the non-mutating witness projects to `GetFooAsync`. `ProtocolConformanceValidator` predicts the divergence (each side's real `IsMutating`) and **drops the conformance**, so generated C# still compiles (no CS0535) but the type loses that protocol's projection. Graceful, rare, and unobserved in the validation corpus (`nuke validate` swept 130/130 C# compile). Pinned by `ProtocolConformanceValidatorTests.CanFullyImplementProtocol_MutatingAsyncNounGetter_*`. **Proper fix:** conformance-aware naming — when a witness satisfies a requirement, name it from the requirement, not the witness's own `IsMutating`. A naming-SSOT change (threading requirement context into the witness name path), too broad for an emitter-polish pass. **Trigger:** a real consumer surfaces a dropped conformance of this exact shape, or a dedicated naming-SSOT session opens.

<a id="bridgeable-value-subscript-setter-emission-gap-mechanism-b"></a>

## Bridgeable-VALUE subscript setter emission gap (Mechanism B)

A subscript setter over an ObjC-bridgeable **value** type (`URL`/`URLRequest`/`Decimal`/NS_TYPED newtype) is CS0029-broken for **all** such subscripts (optional or not): the subscript-setter receiver is a bespoke path that never wires the whole-value setter conversion, so it's gated out at emission rather than mis-marshalling. Distinct mechanism from the (resolved) reverse-dispatch optional-layout fixes — folding it in would be a partial patch on a different path. No active repro. **Trigger:** a real protocol with a bridgeable-value subscript, or a dedicated subscript-receiver pass.

<a id="same-signature-closure-async-method-fan-out-gap"></a>

## Same-signature closure/async method fan-out gap

The owner/sibling vtable fan-out (`EveryProtocolEmitter.cs:1426`) threads `methodPlan` only into `EmitMethodImplementation` (`:1520`); the closure-param / closure-return / async emitters (`:1468/:1470/:1472`) don't receive it, and the C# receiver fallback is gated off (`ProtocolProxyEmitter.Receivers.cs:976` `!method.IsAsync && !hasDispatchableClosureParamForFallback`). So if two emittable protocols share a closure-shaped/async method signature and a C# type implements ONLY the non-owner protocol, dispatch routes into the owner body, which force-unwraps its own nil vtable field (`:4022`) → loud Swift nil-unwrap trap (not silent corruption). No validation-set library produces it. **Trigger:** two emittable protocols sharing a closure/async method full signature + a non-owner-only impl. Fix: thread owner-box + sibling-vtable fan-out into the three closure/async emitters; extend the receiver fallback past the guard; `--device` + fixtures for closure-param/closure-return/async collisions.

<a id="owned-existential-collection-element-carrier-fall-through"></a>

## Owned existential collection-element carrier fall-through

Two fall-throughs in `ExistentialProjection.GetArrayElementCarrierConversion` (`:162-186`) route an `__owned` existential collection-element param/write through the non-owning alias instead of minting an owned carrier (same over-release shape the arity-1 case already fixed): (a) null-`_proxyClassName` EC1, (b) composition existential `any P & Q` (arity ≥ 2). Empirically unreachable (`Select(.*GetExistentialContainer())` = 0, `FromEnumerable<…ExistentialContainer[23]>` = 0; all 23 emitted owned-carrier conversions take the minting path). **Trigger:** a composition or null-proxy existential collection param/write. Fix: arity-/proxy-agnostic owned-carrier dispatch (`CreateOwnedExistentialN` mirroring EC1's `ownsContainer`), composition array/dict `LifetimeTracker` probe as the gate.

<a id="protocol-emitter-narrow-latents-cs0111-cs0108-null-fn-pointer"></a>

## Protocol-emitter narrow latents (CS0111 / CS0108 / null-fn-pointer)

**Revisit when:** a fixture or corpus library produces one of the three named compile errors from a protocol emitter — all three are compile-catchable, so a red build is the signal, not an audit.

<details>
<summary>Recorded evidence and disposition</summary>

Three zero-fixture mechanisms beyond the `propertyNames` constraint contract; batch if a protocol-emitter session opens. **(F7 — RESOLVED, AF05 ruling b)** the three former projected-key builders are now one core, `ProtocolSignatureHelper.BuildProjectedMethodKey`, which appends a trailing `CancellationToken` for every async method on ALL paths including protocol (`:292–295`) — so an async `fetch()` + sync `fetch(cancellationToken:)` pair no longer collides, and the old CS0111 can't occur. Fixtured by `KeyBuilderAsyncOverloadProtocol`; the legacy-blocking-receiver companion is `KeyBuilderAsyncBlockingOverloadProtocol`. **(F8)** vtable-slot **fillability** residual. The LAYOUT walks now render slots from `VtableLayoutBuilder.Build(...).IncludedSlots` (`ProtocolProxyEmitter.Vtables.cs:45–46`, `:87–88`), decoupled from every skip set — the struct deliberately KEEPS a slot for an interface-skipped / AnyType-unprojectable member so the C# `[StructLayout]` and the Swift `_vtable` stay field-aligned (gating layout on `_skippedMethodKeys` or the projected key would shrink the C# struct below Swift's and shift every later field — the Finding-8 corruption). The residual is downstream: the fillability walks (receivers, and the `StaticInit.cs` cctor assignment loops) legitimately leave a kept-but-unfillable slot's managed delegate null, so if the Swift side ever reverse-dispatches that skipped requirement it hits a null function pointer. Latent — the assignment loops fill every slot a conformance actually exercises today. **(F9)** duplicate-subscript interface skip (`ProtocolHandler.cs:276`) increments the index but doesn't record `skippedSubscriptIndices`; proxy (`InterfaceImpl.cs:44`) emits both → CS0111 indexers. **(F10)** `SubscriptDecl` has no `WasEmitted`/`IsOverride`, no `HasSubscriptInResolvedAncestors` reader, `SubscriptHandler` never emits `override` → derived-class subscript override → CS0108. (Narrow sibling: `ProtocolProxyEmitter.Receivers.cs:491` `EmitDispatchableClosureReturningMethodReceiver` uses `propertyNames: null` → a `() -> Void` method that PascalCase-collides with a property emits a CS1061 receiver.) **Trigger:** a fixture or corpus library produces one of the three named compile errors from a protocol emitter — all three are compile-catchable, so a red build is the signal, not an audit.

</details>

<a id="collapsed-existential-overloads-emitted-signature-collision-residual"></a>

## Collapsed existential overloads — emitted-signature-collision residual

When protocol-requirement overloads collapse to one C# method (existential-param overloads), the surplus vtable slots are dropped from the C# fillability walk and their Swift witnesses emit a branded `fatalError` trap instead of a dispatch body (`EveryProtocolEmitter.ComputeCollapsedUnfilledMethodSlotKeys`). One residual is deliberately not covered (see that method's `<remarks>`): a member whose projected key is unique but whose fully **rendered C# signature** collides needs ProtocolHandler's private emitted-signature builder to detect — vanishingly narrow, and it fails closed exactly as before (nil-slot force-unwrap trap at dispatch). The root fix for the whole family is distinct signature keys so existential overloads never collapse into one slot — also the root fix for the same-signature fan-out gap above. **Trigger:** a real library trips the branded trap or the residual force-unwrap.

<a id="static-requirement-self-leniency-is-signature-wide-not-slot-scoped"></a>

## Static-requirement Self-leniency is signature-wide, not slot-scoped

The static-method conformance check (`ProtocolConformanceValidator.cs:658-670`) computes `staticMismatchIsUnspellableSelf` once from "ANY Self-bearing slot exists anywhere in the signature" and applies that one flag to BOTH the return-type (CS0738) check and the all-params check. A static requirement that mixes a Self-typed slot with an *independently* mismatched non-Self slot is therefore kept when a slot-scoped check would drop it. Deliberately not tightened: statics project to `static virtual` interface members with throwing default bodies, so a diverged static signature is normally compile-benign (the concrete member just leaves the default in place — same end-state as member-absent), and any wrong keep that DOES produce a compile error is caught by the in-generator C# verification build (SWIFTBIND113) — a hard generate-time stop, never a silent bad binding. Per the prediction-gate freeze policy, a compile-catchable shape belongs to the verify loop, not a sharper predictor. **Trigger:** a real corpus/validate red with a CS0738-family error traced to a static requirement mixing a Self-bearing slot with an independently mismatched slot.

<a id="conformance-dictionary-gate-is-broader-than-the-interface-gate-a-factory-can-register-for-an-interface-the-cla"></a>

## Conformance-dictionary gate is broader than the interface gate — a factory can register for an interface the class never declares

**Revisit when:** consumer-visible confusion, or a runtime fault traced to a factory registered for an interface the conformer never declares — then decide whether the factory registration should adopt the interface gate or the interface list should adopt the dictionary gate, rather than patching one call site.

<details>
<summary>Recorded evidence and disposition</summary>

`ShouldEmitConformanceDictionary` is deliberately the looser of the two conformance gates: `ShouldEmitConformanceInterface` layers a cross-module-with-members skip on top of it, because a cross-module protocol with emittable members cannot be declared as C# inheritance (no stubs → CS0535), while the descriptor/`IExistentialBoxable` path only needs the conformance-descriptor symbol. The consequence is that `GetConformanceProtocolNames` — which pairs with the dictionary entries so a descriptor entry gets a matching NativeAOT factory pre-registration — keys on the *dictionary* gate, so a conformance dropped from the inheritance list still emits `RegisterConformanceFactory<Conformer, Interface>` naming an interface the emitted class does not implement. This **compiles** (the registration is by type argument, not by declared implementation) and is pre-existing; the asymmetry is intentional, since the dictionary/boxing path genuinely works without member stubs. The residual is legibility — a reader of the generated module init sees a factory that looks like a conformance the class doesn't have — plus the unproven question of what a runtime lookup keyed on that interface does when the class can't satisfy it. **Trigger:** consumer-visible confusion, or a runtime fault traced to a factory registered for an interface the conformer never declares — then decide whether the factory registration should adopt the interface gate or the interface list should adopt the dictionary gate, rather than patching one call site.

</details>

<a id="suppressed-proxy-consume-marker-sb0008-does-not-reach-property-subscript-accessors-by-design-for-the-observed"></a>

## Suppressed-proxy CONSUME marker (SB0008) does not reach property/subscript accessors — by design for the observed shapes, with one uncovered residual

**Revisit when:** a fixture or corpus library with a suppressed-proxy existential in a subscript *index* (or any accessor whose consume-degrade is not accompanied by a produce-degrade on the same member) — then add the checkpoint/injection to the accessor emission site rather than widening the method-path helper.

<details>
<summary>Recorded evidence and disposition</summary>

The SB0008 warning marker is emitted from the wrapper-emitter member paths (method, constructor, ObjC-rooted constructor, failable factory) and explicitly skips accessors (`WrapperEmitter.InjectConsumeDegradedMarker`'s `IsAccessor` arm). For every shape the fixtures exercise this is **correct, not a gap**: an existential-typed property or subscript *element* has a getter on the same member, that getter's PRODUCE path already carries the error-level SB0006 marker for the same suppressed type, and `[Obsolete]` is `AllowMultiple=false` — so the member is never silent and physically cannot carry both. Evidence: `BindingTests/output/SwiftBindingsTestLib.Types.BoxableSinkImpl.cs` and `…BoxableSubscriptSinkImpl.cs` — SB0006 on the getter, whose text says "A setter, where the member has one, still works." **The residual is narrower than "setters":** a subscript whose *index* is the suppressed existential while its element type is not — `subscript(box: any Boxable) -> Int32 { get set }` — produces nothing suppressed, so neither accessor earns SB0006, and both silently CONSUME a C#-authored conformer that will never fire. No fixture in `SuppressedProxyChannels.swift` has that shape (every existential there is in the element/return position), and no corpus library has surfaced it. Closing it is not the bounded read-back fix the member paths got: accessors emit their public surface from `PropertyHandler`/`SubscriptHandler`, a different emission site with no checkpoint around the accessor's own signature, so it needs new plumbing there rather than a call to the shared injector. **Trigger:** a fixture or corpus library with a suppressed-proxy existential in a subscript *index* (or any accessor whose consume-degrade is not accompanied by a produce-degrade on the same member) — then add the checkpoint/injection to the accessor emission site rather than widening the method-path helper.

</details>

<a id="emitcrossmoduleparentvtableinit-dual-walk-dedups-on-different-key-domains-the-swift-facing-table-can-copy-a-ne"></a>

## `EmitCrossModuleParentVtableInit` dual walk dedups on different key domains — the Swift-facing table can copy a never-assigned local slot

**Revisit when:** a cross-module parent protocol carrying an existential-overload pair, or the next edit to either walk of this dual-walk site.

<details>
<summary>Recorded evidence and disposition</summary>

2026-08 audit (r2 review note, verified against code). The cross-module parent vtable init (`ProtocolProxyEmitter.StaticInit.cs:510`) walks the parent's methods twice: the local-vtable assignment loop (~`:586-590`) dedups on `ProtocolMethodDisambiguator.EffectiveRawKey` **and** `EffectiveProjectedKey`, while the Swift-facing table loop (~`:634-641`) clears only `methodIndices`/`emittedCSharpKeys` and re-applies projected dedup — the EffectiveRawKey collapse is never re-applied. A method pair collapsing to one EffectiveRawKey while carrying distinct slot keys and projected keys (the existential-overload collapse shape) would get one local assignment but two Swift-facing `func_* = (IntPtr)local.Func_*` copies, the second reading a local field that was never assigned — a null function pointer planted where the collapse intended a re-point at the survivor. Same family as the F8 fillability residual: fails as a nil-dispatch trap, not silent corruption, and no fixture or corpus library produces the shape on a *cross-module parent* today. **Fix shape:** re-apply the same EffectiveRawKey dedup (re-pointing collapsed slots at the survivor) in the Swift-facing loop — an incomplete twin-site application of the local loop's dedup, not a new mechanism. **Trigger:** a cross-module parent protocol carrying an existential-overload pair, or the next edit to either walk of this dual-walk site.

</details>

<a id="conformance-validator-matcher-residuals-label-blind-static-matching-lane-inconsistent-static-name-derivation-u"></a>

## Conformance-validator matcher residuals — label-blind static matching, lane-inconsistent static name derivation, unreachable subscript rung

**Revisit when:** that static naming unification opens (session-08 territory), or a static name-parity red traced to one of these lanes.

<details>
<summary>Recorded evidence and disposition</summary>

2026-08 audit (verified against code); three residuals in `ProtocolConformanceValidator`, all compile-benign today and owned by the same future unification. (1) `FindMatchingStaticMethod` (`:1140`) matches label-blind — but the static-requirement loop itself already dedups label-blind (second label-only static dropped as `DuplicateSignature`, see [static protocol dedup residual](protocols.md#protocol-side-dedup-ignores-argument-labels-instance-methods-fixed-residual-statics)), so a label-aware tie-break would have nothing left to disambiguate; inert until static dedup moves onto the `Effective*` keys. (2) The static path derives `concreteEmittedName` from `PublicMethodNameContext.ForMethod(...)` *without* folding `OverloadNameDisambiguator.ForMethod`, and `interfaceMethodName` from raw `protoMethod.Name` rather than the `EffectiveNameInput` lane — two inconsistencies that mutually cancel (both sides skip the same shaping), so the static name-parity check holds today; changing either lane alone would red the parity gate. (3) `FindMatchingSubscript` (`:1056`) is also label-blind, but unreachable as a defect: the subscript naming path has no label rung, so no label-only subscript pair survives to the matcher as distinct members. **Fix shape:** one pass moving static dedup, static matching, and static name derivation onto the same `Effective*` key/name lanes the instance path uses — the "unified static naming-policy change" already named in the statics row. **Trigger:** that static naming unification opens (session-08 territory), or a static name-parity red traced to one of these lanes.

</details>

<a id="a-protocol-requirement-with-a-foundation-uuid-parameter-gets-no-witness-table-slot"></a>

## A protocol requirement with a `Foundation.UUID` parameter gets no witness-table slot

**Revisit when:** a consumer delegate protocol carries a `UUID` parameter (LiveCommunicationKit's conversation-manager delegate is the likely first), or any fixture protocol with a `UUID` requirement lands in BindingTests.

<details>
<summary>Recorded evidence and disposition</summary>

The forward direction was fixed with the UUID `@_cdecl` lowering (`CdeclParamMapper.IsObjCBridgedValueStruct`): an ObjC-bridgeable frozen value struct is lowered by Swift to a single `NSUUID` pointer, so the wrapper now takes the 16 bytes through the indirect `UnsafeRawPointer` path. The reverse direction was not extended. `WitnessDispatchEmitter.IsTypeDispatchable` admits blittable primitives, `Swift.String`, Swift classes and indirect structs (non-frozen, or frozen with ref fields); `Foundation.UUID` is frozen with no ref fields and remaps to `System.Guid`, which is not in the blittable-primitive set, so a requirement such as `func didSelect(id: UUID)` is judged non-dispatchable. It consumes no dispatch index, the emitted interface satisfies it with a default, and a C# conformer's implementation is silently never called — the same silent-no-op class the availability-forwarder fix closed. Fix shape: a bridged-value-struct arm mirroring the forward wedge, applied identically in all three walks that compute the dispatch index (Swift accessor emission, C# P/Invoke walk, C# caller walk), with the accessor taking a pointer to the 16 bytes and the receiver reading a `Guid` from it. `UuidParameterAbi.swift` has no protocol shape yet; the fixture comes first. **Trigger:** a consumer delegate protocol carries a `UUID` parameter (LiveCommunicationKit's conversation-manager delegate is the likely first), or any fixture protocol with a `UUID` requirement lands in BindingTests.

</details>

<a id="an-optional-container-of-class-bound-existentials-in-a-receiver-parameter-is-sized-with-the-opaque-carrier"></a>

## An OPTIONAL container of class-bound existentials in a receiver parameter is sized with the opaque carrier

**Revisit when:** a delegate protocol requirement takes an optional array/set/dictionary of a class-bound existential, or the next edit to either receiver container arm.

<details>
<summary>Recorded evidence and disposition</summary>

The class-bound existential layout work gives a `[any P]` element (where `P` is declared `: AnyObject`) the compact 16-byte `ClassExistentialContainer1` stride: the bare `Array`/`Set`/`Dictionary` existential receiver arms in `ProtocolProxyEmitter.Receivers.cs` are consulted *before* the generic projection walk and read `ExistentialProjection.ArrayElementCarrierType`, which resolves to the class-bound width. `Optional<[any P]>` does not take that path. The optional receiver-getter arm falls through to `BuildOptionalContainerGetterConversion` → `GetReceiverArrayGetterConversion` (and its `Set`/`Dictionary` siblings), which size elements by `SwiftContainerGenericType` — the 40-byte opaque `ExistentialContainer1`. So a requirement declaring `[any ClassBoundP]?` would have its elements deposited at a stride Swift does not expect: not a compile error, a wrong-address read on the callee side. Latent rather than live — the two carrier widths only diverge for a *class-bound arity-1* existential with a proxy, and no fixture or corpus library puts one inside an optional container in a protocol requirement. **Fix shape:** give the optional container arms the same carrier-type source the bare arms already use, rather than teaching the projection a second stride rule. **Trigger:** a delegate protocol requirement takes an optional array/set/dictionary of a class-bound existential, or the next edit to either receiver container arm.

</details>

<a id="a-parameter-that-swift-later-stores-into-weak-unowned-storage-still-takes-the-retaining-lane"></a>

## A parameter that Swift later stores into `weak`/`unowned` storage still takes the retaining lane

**Revisit when:** a consumer reports the collected-delegate symptom on a type whose delegate is only settable through an initializer, or the interface-facts producer starts carrying stored-property ownership.

<details>
<summary>Recorded evidence and disposition</summary>

The consumer-owned carrier lane — the one that keeps a C# implementation reachable after it is assigned into a non-retaining Swift slot — is keyed on the *setter*: `NonRetainingSinkLane.ConsumerOwnsCarrier` reads the accessor's recorded `SinkReferenceOwnership` and is scoped to the setter's value argument. An initializer or method parameter carries no such fact; the ABI says nothing about the storage the callee will move the value into. So `init(delegate: any P)` on a type that stores `delegate` into a `weak var` marshals through the default lane, the wrapper's construction `+1` is the box's only reference, and the implementation can be collected while the consumer still expects callbacks — the shape the setter path was fixed for, reached through a different door. Consumer-visible symptom: callbacks stop, the Swift slot reads `nil`, and nothing was assigned in between. The generated `<remarks>` on non-retaining properties already tells a consumer to assign through the property once after construction, which is the working remedy; it is emitted only for a property that has a `set` accessor, so an `unowned let` (get-only) property carries no note at all. Closing it properly means an ownership fact about the *callee's storage*, which the ABI does not expose — the honest alternatives are a whole-module SwiftSyntax pass over stored-property declarations or a consumer-facing opt-in. **Trigger:** a consumer reports the collected-delegate symptom on a type whose delegate is only settable through an initializer, or the interface-facts producer starts carrying stored-property ownership.

</details>

<a id="a-synchronous-throws-requirement-has-no-error-channel-on-either-side-of-the-reverse-dispatch-boundary"></a>

## A synchronous `throws` requirement has no error channel on either side of the reverse-dispatch boundary

**Revisit when:** a consumer needs a real Swift error to propagate out of a synchronous `throws` protocol requirement implemented in C#, or a session already reshaping the reverse-dispatch thunk signature.

<details>
<summary>Recorded evidence and disposition</summary>

The lane-B degradation work (2026-09) surfaced this while giving every unsatisfiable reverse-dispatch return a defined terminal. An `async throws` requirement carries its error through the async continuation, but a **synchronous** `throws` requirement's thunk has no error out-slot: the Swift side declares no `@error` parameter and the C# receiver has nowhere to put an exception. So a live C# implementation that throws reaches `FailFastUnhandledClosureException` (the process terminates rather than the Swift caller catching), and a collected implementation degrades to the identity value for the return type instead of surfacing a Swift error — the honest terminal, but one a Swift `try` cannot observe. Neither shape can be fixed inside the receiver: the missing channel is in the thunk signature. **Fix shape:** an error out-parameter threaded through the `@_cdecl` thunk and the EveryProtocol conformance, so the receiver can write a Swift error the caller's `try` observes — a two-sided ABI change, not a receiver patch. **Trigger:** a consumer needs a real Swift error to propagate out of a synchronous `throws` protocol requirement implemented in C#, or a session already reshaping the reverse-dispatch thunk signature.

</details>

<a id="generated-receivers-leave-the-per-array-swiftarray-t-wrapper-to-finalization"></a>

## Generated receivers leave the per-array `SwiftArray<T>` wrapper to finalization

An array-typed reverse-dispatch parameter is presented to the C# implementation as a `SwiftArray<T>` wrapper, which the generated receiver never disposes — the wrapper's native release happens whenever the finalizer runs. Not an ARC leak (the finalizer does run, and `TestRepeatedDelegateCallbacksDoNotLeak` drains), but the native releases are deferred unpredictably rather than at the receiver's exit, so a callback-heavy delegate holds Swift buffers longer than the call that produced them. **Fix shape:** bind the wrapper to a `using` in the receiver body, which means the receiver emission has to know the wrapper is receiver-owned rather than handed on to the implementation — a lifetime fact the parameter conversion does not carry today. **Trigger:** a consumer reports native memory growth under a high-frequency array-parameter delegate, or a session already inside the receiver container arms.

<a id="one-implementation-in-both-a-weak-and-a-strong-swift-slot-gets-two-carriers-so-swift-between-them-is-false"></a>

## One implementation in both a weak and a strong Swift slot gets two carriers, so Swift `===` between them is false

The auto-wrap memo is deliberately two tables: the default lane memoises the carrier weakly, the consumer-owned lane memoises it in a separate table whose entry holds the proxy strongly, because the two lanes need opposite rooting. A consumer who assigns the *same* C# implementation to a `weak` property and to a strong one therefore gets two distinct conformer boxes, and Swift code comparing the two slots with `===` sees them as different objects. By design, not a defect the lanes can merge away: one table has to keep its proxy alive and the other must not, so a single shared memo would either leak every auto-wrapped implementation or re-open the collected-delegate crash. It is invisible unless the Swift side compares identity across two differently-owned slots. **Trigger:** a consumer reports a Swift-side `===` (or `ObjectIdentifier`) comparison between two delegate slots failing for an implementation they assigned once — at which point the answer is a documented note, not a merged memo, unless the report shows a shape where identity is load-bearing.
