# Runtime, marshalling and ABI — reference notes

Searchable findings to consult when working in this area. Nothing here is queued or newly authorized.
[How to use and maintain these notes](README.md). Recorded evidence and counts describe their original investigation; recheck against current source before acting.

Consumer-facing capability holes that need infrastructure the generator doesn't have. Consumer-facing phrasing lives in the wiki's Known Limitations; these rows are the internal mechanics.

<a id="swift-foundation-data-ownership-model-dispose-no-op-and-accessor-shape-leak"></a>

## `Swift.Foundation.Data` ownership model — `Dispose` no-op and accessor-shape leak

2026-09 wave (s4, owned-return cleanup). `src/Swift.Bindings.Apple/Sources/Foundation/Data.cs` keeps `Dispose()` a no-op on purpose: `Data` is an inline, freely-copied struct with no retain, so a destructive `Dispose` would fire once per copy and release payloads the value only borrows. The cost is accessor Shape A — `private Data X_Get()` + `public byte[] X => X_Get().ToByteArray()`, ~845 sites across the supplement corpus — which leaks the Swift payload once per read; destroying at that seam is a use-after-free. The wave fixed the CSM/bridge direct owned-`Data` return only. Fixing the rest is a design fork, not a repair: either `Data` becomes a real owning handle (breaking for consumers that treat it as a value) or the accessor shape is regenerated to consume the +1 at the seam for every supplement type at once. **Not authorized** — owner picks the ownership model. **Trigger:** a consumer reports memory growth reading `Data`-valued properties in a loop, or the supplement is regenerated for a new Xcode major (natural moment to change the shape).

<a id="the-class-bound-existential-layout-rule-covers-arity-1-only-a-class-bound-composition-still-marshals-as-opaque"></a>

## The class-bound existential layout rule covers arity 1 only — a class-bound COMPOSITION still marshals as opaque

**Revisit when:** a corpus library or consumer surfaces a parameter, property or requirement typed `any P & Q` where a participant is class-bound (or an explicit `& AnyObject`), or the next edit to the class-bound layout predicate.

<details>
<summary>Recorded evidence and disposition</summary>

`ExistentialHandler.IsClassBoundArity1Existential` returns the compact `[classRef][witnessTable]` carrier only when the composition has exactly one non-marker participant (markers being `Sendable`/`Escapable`/`Copyable`/`SendableMetatype`) whose resolved record is a non-`@objc` protocol flagged class-bound. Anything wider falls through to the opaque container, but Swift does not: a composition is laid out as a *class* existential as soon as any participant is class-bound, so `any P & Q` with a class-bound `P`, and the explicit `any P & AnyObject` spelling, both get the 5-word opaque container where Swift expects the class representation. The `AnyObject` spelling is doubly off — `AnyObject` is not a marker here, so it is counted as a witness-table-bearing participant and pushes the carrier one container wider, though it carries no witness table at all. Latent, not live: no fixture and no corpus library binds a member typed on a class-bound composition, and a composition with a *concrete class* participant is already rejected outright (`CompositionHasNonProtocolParticipant`), so the reachable shapes are narrow. **Fix shape:** a class-existential carrier parameterised on witness-table count, plus excluding `AnyObject` from the count that sizes it — not a widening of the arity-1 predicate, which would silently pick the wrong width. **Trigger:** a corpus library or consumer surfaces a parameter, property or requirement typed `any P & Q` where a participant is class-bound (or an explicit `& AnyObject`), or the next edit to the class-bound layout predicate.

</details>

<a id="mixed-indirect-generic-tuple-returns"></a>

## Mixed-indirect generic tuple returns

Bare-generic shape (`(T, U)`, `(T, U, V)`) is covered by `IsMultiElementGenericTupleIndirectReturn`. Mixed and bound-generic shapes — `(T, Int)` → `(@out T, Int)`, `(Array<T>, T)` → `(Array<T>, @out T)`, `(UnsafePointer<T>, T)` → `(UnsafePointer<T>, @out T)`, `(Optional<T>, T)` → `(@out Optional<T>, @out T)` — fall through to the legacy `SwiftIndirectResult` path with the wrong shape. Real fix: per-element address-only/direct ABI classifier driving a partial-indirect P/Invoke signature. **Trigger:** a validation library or consumer surfaces a mixed or bound-generic tuple return — there is no active repro, so a real shape is what would drive the classifier's design.

<a id="inout-round-trip-cdecl-blittable-frozen-path-fixed-non-cdecl-paths-are-the-residual"></a>

## `inout` round-trip — cdecl blittable-frozen path FIXED; non-cdecl paths are the residual

The `@_cdecl` path for an `inout` blittable *frozen* struct now round-trips end-to-end: the generated C# call site emits `ref T` and writes the struct back after the P/Invoke (post-call `MarshalFromSwift` writeback, `WrapperEmitter.Marshalling.cs`), so Swift-side mutation reaches the caller. Proven by `ParameterTests.TestIncrementPoint` (`incrementPoint(_:inout FrozenPoint)` asserts x/y written back). Residual: any *non-cdecl / projection* path that still marshals the struct into a stack buffer without a readback — un-exercised because the KeyPath foundation fixture (`KeyPathConsumer.writeInt`) sidesteps `inout` by returning a mutated copy. **Trigger to close:** re-enable `inout PointKP` on `KeyPathConsumer` (or another non-cdecl inout shape) and assert the mutation round-trips; grep for a CallConvSwift frozen-inout site that marshals in without a writeback.

<a id="non-primitive-tuple-elements-method-cdecl-buffer-gaps-bufferless-subscript-path"></a>

## Non-primitive tuple elements: method cdecl buffer gaps + bufferless subscript path

<details>
<summary>Recorded evidence and disposition</summary>

Two member-validator tuple gates with deliberately different admit keys. **Methods/ctors** (`IsCdeclBufferMarshallableTuple`): the cdecl tuple-buffer transport carries blittable primitives, `Swift.String`, EC2+ composition existentials, and pure Swift classes per-slot; frozen-blittable non-primitive struct elements (Foundation.Data, custom frozen structs) are rejected — without the gate the wrapper takes the tuple as `UnsafeRawPointer` (CdeclParamMapper fallback) against a C# P/Invoke declaring the `ValueTuple` by value (compiles both sides, ABI-garbage). **Subscripts** (`IsAllPrimitiveTuple` — index params AND tuple returns, get-only included): the subscript path P/Invokes the raw dispatch thunk with no buffer transport and no per-element conversion layer, so ANY non-primitive element breaks — projected elements (String→`string`, Data→`byte[]`) diverge the public indexer from the raw accessor tuple (CS1503), class elements diverge the accessor from its `ValueTuple<IntPtr,…>` P/Invoke (CS1503), get-only returns run wrapper-decomposed-IntPtr conversions against raw element values (CS0030), and any type-consistent survivor still passes a by-value Auto-layout `ValueTuple` under CallConvSwift. Only all-primitive tuples (raw == public == P/Invoke representation) emit. Tuple-typed *properties* need no gate: they already fail closed at MemberEmissionValidator (TryGetTypeRecord returns false for every TupleTypeSpec). The lift for the method-path element kind: write the frozen value into the cdecl buffer at its metadata-derived element offset, borrowed +0 with a keep-alive, like the existing String slot. The lift for subscripts is bigger: route accessors through a buffer-capable transport (or add a real per-element projection layer to the accessor path). The tuple-arm `AppleSupplementReferences.Record` calls are already in place, so supplement-homed elements (Data) go production-live with the lifts. **Reopen trigger:** a real-world validation library or reported binding needs a method/ctor tuple param with a frozen-blittable struct element, or a subscript with any non-primitive tuple index/return.

</details>

<a id="r5-r6-runtime-seam-unaudited-surface-latents"></a>

## R5/R6 runtime-seam + unaudited-surface latents

(R5-6) `SwiftMarshal.cs` `NewFromPayloadDispatcher.TryCreate` returns `null` for both a cache miss and a registered factory that legitimately returns null → a null-returning factory falls to reflection; no emitted factory returns null today (correct fix = a `bool TryCreate(out …)` sentinel). (R5-3) `EnumHandler.Marshalling.cs` `GetSwiftAbiMetadataType` (8B) vs `GetSwiftRawValueType` (4B) width disagreement for tag-only raw values; needs a `(Tag, Int32)` tuple round-trip probe. (R6) `AbiContractChecker.cs` `IsNonBlittable` defaults every unknown type to blittable behind a 7-name allowlist — a genuinely new non-blittable type escapes CC-001/CC-002. (R6) `ThrowsWalker.swift` (empty-`firstName` `continue`) vs `SignatureFactsWalker.swift` (unconditional append) build the same printedName key by divergent rules — **empirically confirmed non-divergent** (SwiftSyntax guarantees `firstName` is never empty on a real parse; the guard is dead code from the regex→SwiftSyntax migration), safe to leave or delete. **Trigger:** a session is already inside one of these four files, or a real symptom lands on one of the mechanisms — a factory that legitimately returns null, a raw-value width mismatch at runtime, or a CC-001/CC-002 escape naming a type outside the allowlist.

<a id="tuple-return-carrying-an-existential-element-degrades-the-element-to-swift-anytype-wrong-tuple-metadata-cs0029"></a>

## Tuple *return* carrying an existential element degrades the element to `Swift.AnyType` — wrong tuple metadata + CS0029

**Revisit when:** a corpus/validation lib returns a tuple with an existential element, or a session already funding tuple-transport lifts can fold it in.

<details>
<summary>Recorded evidence and disposition</summary>

Found en route during the cross-module-qualification stream (D1, 2026-07-28), evidenced by the explanatory comment in `BindingTests/Sources/SwiftBindingsTestLib/CrossModule/CrossModuleExistentialContainers.swift` (the fixture deliberately omits tuple payloads for this reason). A method whose **return** is a tuple with an existential element (e.g. `(any P, Int)`) projects the existential slot as `Swift.AnyType`, so the runtime tuple metadata is built from `Any`'s layout — element offsets and total size are wrong for the actual `any P` container — and the C# side fails CS0029 assigning the raw slot to the interface type. Two defects in one shape: the compile-catchable CS0029 (verify-recover withdraws the member today, so nothing silently ships) and the underlying metadata-from-`Any` layout error that would bite even if the cast were patched. Distinct from [mixed-indirect generic tuple returns](runtime-abi.md#mixed-indirect-generic-tuple-returns) (that one is generic-element ABI classification; this one is existential-element *projection* feeding tuple metadata). **Fix shape:** its own fundable item — project the existential tuple element through the module-aware existential oracle (as scalar returns now do) and build tuple metadata from the existential container layout, not `Any`; then let the CrossModuleExistentialContainers fixture grow a tuple-payload case as the gate. **Trigger:** a corpus/validation lib returns a tuple with an existential element, or a session already funding tuple-transport lifts can fold it in.

</details>

<a id="wrapper-eligibility-keys-on-the-immediate-parent-a-same-module-nested-internal-parent-member-can-be-granted-an"></a>

## Wrapper eligibility keys on the immediate parent; a same-module nested-internal-parent member can be granted an unspellable wrapper

**Revisit when:** a real library surfaces a member on a public type nested in a same-module internal type (wrapper compile failure/strip, or a faulting call the floor missed).

<details>
<summary>Recorded evidence and disposition</summary>

Pre-existing gap surfaced (not introduced) by the 2026-07-31 SB0009 tombstone work. A member on a public type nested inside a *same-module* internal type passes `WrapperValidation.IsParentTypeModuleInternal` (immediate parent only), so the emitter can grant a `@_cdecl` wrapper that cannot spell its receiver path — and counting as "mitigated" also suppresses the SB0009 floor for it. The naive widening is **proven wrong, don't retry it**: walking enclosing types over `TypeDecl.IsModuleInternal` was attempted and the API-manifest gate caught 4 members (`DependencyService.HostedPayload.Init`/`GetDescribe`, `HostedToken.Init`, `DependencyPoint.HostedTag.Init`) retargeted off `SBW_*` wrappers that compile and work today — a type declared in an extension of a *foreign* module's type has that foreign type as its enclosing decl, and the flag reads internal merely because the name is absent from this module's `PublicTypeNames` while being public and importable where declared. A real fix needs a spellability fact that separates the same-module nested case from the foreign extension receiver; the shared oracle `IsTypeOrEnclosingModuleInternal` has 6 other emission-site consumers, so the blast radius must be proven, not assumed. Rationale inlined at `IsParentTypeModuleInternal`. **Trigger:** a real library surfaces a member on a public type nested in a same-module internal type (wrapper compile failure/strip, or a faulting call the floor missed).

</details>

<a id="sb0009-tombstone-floor-excludes-accessors"></a>

## SB0009 tombstone floor excludes accessors

**Revisit when:** a real library faults through such an accessor, or the accessor SB0001 deferral is reworked. **Partly narrowed 2026-09** (bridged-container ABI floor): the `AnyDirectAbiSlot` walk in `DirectOptionalAbi` now covers accessors too, so a property/subscript accessor whose slot renders an ObjC-bridged container (`[URL]`, `[String: URL]`, `Set<URL>`, optional or not) on the direct arm is tombstoned by `ApplyAbiFloorTombstone` and throws `NotSupportedException`. What accessors still lack is the **SB0009 marker**: `GetNonBlittableCallConvSwiftIssue` returns null for `IsAccessor` on purpose, because the marker's diagnostic is error-severity and a public indexer carrying it would not compile in a consumer that treats SB0009 as an error. Consumers therefore see the throw at run time, not the analyzer diagnostic at compile time. Folding the marker onto accessors needs a warning-severity accessor variant of SB0009 or a member-level attribute the analyzer can read through the accessor — the same SB0001 accessor deferral.

<details>
<summary>Recorded evidence and disposition</summary>

`IsAccessor` short-circuits both the SB0001 advisory and the tombstone floor (`HasUnmitigatedNonBlittableCallConvSwift`), so a non-blittable property/subscript accessor on an unwrappable receiver is still emitted as a live direct call. Deliberate — the accessor path never reaches the marker and matches the documented SB0001 accessor deferral; folding accessors in is that deferral's work, not the floor's. **Trigger:** a real library faults through such an accessor, or the accessor SB0001 deferral is reworked. **Partly narrowed 2026-09** (bridged-container ABI floor): the `AnyDirectAbiSlot` walk in `DirectOptionalAbi` now covers accessors too, so a property/subscript accessor whose slot renders an ObjC-bridged container (`[URL]`, `[String: URL]`, `Set<URL>`, optional or not) on the direct arm is tombstoned by `ApplyAbiFloorTombstone` and throws `NotSupportedException`. What accessors still lack is the **SB0009 marker**: `GetNonBlittableCallConvSwiftIssue` returns null for `IsAccessor` on purpose, because the marker's diagnostic is error-severity and a public indexer carrying it would not compile in a consumer that treats SB0009 as an error. Consumers therefore see the throw at run time, not the analyzer diagnostic at compile time. Folding the marker onto accessors needs a warning-severity accessor variant of SB0009 or a member-level attribute the analyzer can read through the accessor — the same SB0001 accessor deferral.

</details>

<a id="no-first-party-fixture-lane-can-carry-module-internal-swift-shapes"></a>

## No first-party fixture lane can carry module-internal Swift shapes

`CompileModuleSlice` (`build/Build.BindingTests.cs:180`) always passes `-enable-library-evolution`, which strips module-internal members from both the `.swiftinterface` and the `.abi.json` — so BindingTests structurally cannot pin the SB0009 tombstone (or any module-internal-member behavior) end-to-end. Current evidence is a third-party regen: FBAEMKit yields 24 SB0009 tombstones across 11 types and the generated C# compiles. Closing this needs a non-evolution build lane in the BindingTests harness. **Trigger:** extending the tombstone floor or the wrapper-eligibility oracle, where first-party end-to-end proof would be wanted alongside the unit pins.

<a id="no-repo-gate-exercises-the-c-verify-recover-loop-on-the-main-test-library"></a>

## No repo gate exercises the C# verify-recover loop on the main test library

The main BindingTests regeneration passes `--no-verify-csharp` (`build/Build.BindingTests.cs:425`), so the in-generator compile-withdraw-re-emit loop never runs over the primary test corpus. Its only coverage is the partial-success and ingestion kitchens — small, deliberately hostile fixtures rather than the broad surface — so a recovery regression whose shape those two do not happen to contain is caught by nothing in the repo. **Trigger:** any change to the verify-recover loop, or a recovery regression the kitchens miss.

<a id="optionset-operators-allocate-a-native-instance-per-call-on-a-class-projected-option-set-and-nothing-disposes-i"></a>

## OptionSet operators allocate a native instance per call on a class-projected option set, and nothing disposes it

The generated set operators (`|`, `&`, `^`, and the compound forms) construct a result by allocating a fresh native instance. Where the option set projects to a C# *struct* that is free; where it projects to a **class** the operator hands back an owned native allocation with no disposal path, so an expression chain like `a | b | c` leaves the intermediates to the finalizer. Not a leak in the unrecoverable sense — the payload is a `SwiftSafeHandle`, so a critical finalizer reclaims it — but reclamation is deferred and non-deterministic, and an operator expression is exactly the shape a consumer will not think to wrap in `using`. **Fix shape:** either project option sets as value types uniformly, or have the operator dispose its intermediates, neither of which is a local change to the operator emitter. **Trigger:** a class-projected option set appears on a hot path, or heap growth is reported through option-set arithmetic.

<a id="the-optionset-protocol-match-is-name-only"></a>

## The OptionSet protocol match is name-only

The arm that decides a type is an option set matches the conformance by protocol *name*, without checking the owning module. A third-party protocol also named `OptionSet` would be treated as Swift's, and the type would be projected with set operators and a `rawValue` contract it does not have. Unreached: no corpus library declares a competing `OptionSet`. Same shape as the other name-only matches in the emitter, and the same fix — match on the resolved conformance record rather than the spelling. **Trigger:** a library declares its own `OptionSet` protocol, or a session is already tightening protocol matching to module-qualified records.

<a id="swift-parameter-packs-repeat-each-t"></a>

## Swift parameter packs (`repeat each T`)

Blocked. No pack-projection model in parser/emitter; members using variadic generics are dropped. Blocks e.g. WeatherKit iOS 18+ statistics APIs. **Trigger:** a consumer needs a pack-generic API surface.

<a id="value-buffers-are-allocated-at-max-nuint-alignment-so-an-over-16-byte-aligned-swift-type-would-be-under-aligne"></a>

## Value buffers are allocated at max-`nuint` alignment, so an over-16-byte-aligned Swift type would be under-aligned

Every heap value buffer the runtime and the generated copy-out paths allocate comes from `NativeMemory.Alloc`/`AllocZeroed` sized by `metadata.Size`, and those guarantee only the platform's max-`nuint` (16-byte) alignment. A Swift type whose value-witness **alignment** exceeds 16 — SIMD-heavy, or explicitly over-aligned — would be initialized into an under-aligned destination, which is undefined behaviour rather than a compile or link error. Unexercised rather than known-broken: no type in the validation corpus or the fixtures carries an alignment above 16, so no path has ever produced the misalignment. **Fix shape:** `NativeMemory.AlignedAlloc` keyed on the metadata's alignment (with the matching `AlignedFree` on every release site), applied across the buffer-allocating helpers together — a half-converted set would free an aligned buffer through the plain free. **Trigger:** a corpus library or consumer binds a type whose value-witness alignment is greater than 16, or a crash/corruption report on a SIMD-typed value crossing the boundary.

<a id="protocolconformancedescriptor-tryget-s-nativeaot-last-resort-still-closes-a-generic-reflectively"></a>

## `ProtocolConformanceDescriptor.TryGet`'s NativeAOT last resort still closes a generic reflectively

The NativeAOT arm of `ProtocolConformanceDescriptor.TryGet` falls back to `SwiftObjectReflectionHelper.InvokeGetProtocolConformanceDescriptor`, which does `MakeGenericMethod` on the concrete type's `GetProtocolConformanceDescriptor<TProtocol>` — the same construct that aborts under NativeAOT and was removed from the tuple-metadata path (2026-09). It is reached only when the conformance dispatcher has no registration for the pair, and it sits inside a `try`/`catch`, so today it *degrades* (the lookup fails and the caller takes its own fallback) rather than terminating the process. Not a drop-in fix like the tuple-metadata one: the method it closes lives in **generated binding** code, so there is no by-`Type` sibling on the runtime side to call instead — closing it means the generator emitting a non-generic conformance-descriptor entry point, or every conformance being pre-registered at module init so the last resort is unreachable. **Trigger:** a NativeAOT consumer reports a conformance lookup failing (or the guarded path becoming the only resolver for a shape the dispatcher does not register), or a session already extending the conformance-registration surface.

<a id="result-t-e-parameter-direction"></a>

## Result<T,E> parameter direction

Blocked. Needs native payload synthesis for C#-created instances. **Trigger:** a consumer needs to *pass* a `Result` into Swift — the return direction already works, so only the inbound direction is missing.

<a id="multi-protocol-generic-compositions"></a>

## Multi-protocol generic compositions

Blocked. Needs full existential composition in `@_cdecl` wrapper. **Trigger:** a shipping library's primary surface requires a `T: P & Q` generic parameter.

<a id="value-type-generic-conformers"></a>

## Value-type generic conformers

Blocked. Requires non-AnyObject transport through `@_cdecl` boundary. **Trigger:** a consumer needs a struct or enum to satisfy a generic constraint the binding projects.

<a id="static-protocol-constructors"></a>

## Static protocol constructors

Init witness dispatch needs allocation infrastructure. **Trigger:** a consumer needs to construct a conforming type through a protocol's `init` requirement.

<a id="weak-unowned-references"></a>

## Weak/unowned references

4 test skips. Requires ownership tracking infrastructure. **Trigger:** a consumer reports a retain cycle that only a weak/unowned projection would break, or those 4 skipped tests block a release gate.

<a id="consuming-existential-arguments-exception-path-and-well-known-protocol-residuals"></a>

## Consuming existential arguments: exception-path and well-known-protocol residuals

<details>
<summary>Recorded evidence and disposition</summary>

2026-09 wave (s9, @owned hand-over) minted the +1 for the plan-rendered carriers and stopped there; the existential-proxy arm kept passing the container borrowed. That was a live use-after-free, not a latent: an auto-wrapped C# conformer handed to a consuming initializer had only the proxy's sole construction reference, the callee released it, and the box died under a live proxy — observable as a `ProxyLifetimeTracker` entry vanishing while the proxy was still alive, and fatal as a `FailFastDeadProxyImpl` abort once Swift reverse-dispatched into the collected implementation. The call-argument renderer now threads `HandedOverToCallee` off the same oracle: an EC1-with-proxy argument mints through `CreateOwnedExistential1` (or `CreateOwnedExistential1ConsumerOwned` when the sink is also non-retaining), and a composition (EC2+) — whose only C# implementers are Swift-vended proxies that *borrow* their stored bytes — takes a value-witness copy through `CreateOwnedCompositionExistential`. The class-constrained arm was two defects deep and both are fixed here. Swift lowers `any P` where `P` is `AnyObject`-constrained to a loadable `[classRef][witnessTable]` pair passed in two registers — SIL reads `(@owned any ClassBoundP, @thin Self.Type) -> @out Optional<Self>` and the arm64 callee takes the class reference in `x0` and the witness table in `x1` — while the direct call declared the five-word opaque container, which is passed indirectly, so the callee read the caller's buffer address as the object and the very first witness dispatch segfaulted. The parameter now declares the two-word carrier and the argument is narrowed to it at the boundary; and the owned mint is layout-aware, retaining word 0 rather than running the opaque value witness over words the class layout never fills. Both fixtured by `ClassBoundConsumingInitOwnershipTests` (six tests: the storing arm, repeated hand-overs, an auto-wrapped C# conformer, a Swift-vended one, and the failing-init exit over each of the two conformer kinds), which SIGSEGV'd inside the initializer before the fix. The wrapper arm was never affected — it hands the callee a pointer to the container, whose first two words are already the pair. `ExistentialContainer0` (bare `Any`) is excluded by the shared ownership gate and stays borrowed: it carries no witness table for the runtime to copy through. The resilient (non-frozen) struct in an `@in`/`@owned` slot was the second half and is fixed on the same pass — it travels by pointer, so the hand-over is `OwnedArgument.Retain<T>` (a value-witness `InitializeWithCopy` into the slot) rather than a retain of the wrapper handle. Fixtured by `ConsumingInitOwnershipTests` (eight tests: storing/dropping/failing/throwing exits, a Swift-vended conformer control, both resilient-struct exits, and a GC-pressure churn run), pinned at the emitter by the `HandedOver`/`Borrowed` pairs in `GetCallArgumentStringTests` and `CalleeArgumentOwnershipTests.Direct{Initializer,Method}_ResilientStructArgument_*`. **Residual, unchanged and accepted:** an exception thrown between minting the +1 and issuing the call leaks one retain (leak over underflow — the mint is emitted before the call on purpose). Separately, the non-consuming direct arm's `GetOrCreate` still mints a carrier for a boxable conformer that nothing destroys; that is a pre-existing leak on the borrowing side, out of this row's scope. **Second residual, deliberate:** a *well-known* arity-1 carrier — a boxed `any Error`, say — stays on the borrowed form even under a consuming callee. The arity-generic mint picks its value witness from the container's word count alone, and that carrier does not lay its remaining words out the way the opaque witness reads them; the honest fix is a carrier-specific retain, and until one exists the under-retain is preferred over a copy made through the wrong layout. Trigger: a consumer reports a double-free or a corrupted error after handing `any Error` to a consuming initializer or setter on the direct arm.

</details>

<a id="wrapperemitter-return-cs-542-bare-generic-class-slot-branch-is-tested-for-c-shape-only"></a>

## `WrapperEmitter.Return.cs:542` bare-generic class-slot branch is tested for C# shape only

2026-09 wave (s4). The branch that reads a bare generic `T` result out of a class slot dereferences a frozen-struct wrapper as if it were a class pointer when `T` is instantiated with a struct-as-class projection. The seam in front of it leaks (copies a borrowed buffer) rather than bad-frees, so the failure is a leak not a crash, and no fixture instantiates the branch with that `T`. **Trigger:** a generic member returning bare `T` is instantiated with a frozen-struct-projected-as-class type in BindingTests or a consumer report.

<a id="over-aligned-mis-sized-trivial-xml-record-fields-inside-frozen-structs-apple-supplement-lane"></a>

## Over-aligned / mis-sized trivial XML-record fields inside frozen structs (Apple supplement lane)

2026-09 wave (s10). For a frozen struct nesting a SIMD or other trivially-copyable record described only by an XML database entry, the C# field lands at the C# type's layout, not Swift's: `simd_float4x4` is 64 bytes at alignment 16 in Swift but `Matrix4x4` aligns at 4, and `simd_float3` is 16 bytes in Swift against `Vector3`'s 12. Pre-existing on `main`, Apple-supplement only; the `DeclaredLayout` lane sizes *reference-bearing* fields and does not touch trivial ones. Fix = a typed-buffer layout model for trivial records (explicit `[StructLayout]` offsets from the XML size/alignment). **Trigger:** a supplement struct with a SIMD field reads a wrong component in a consumer report or a supplement smoke.

<a id="failable-factory-non-frozen-arm-leaks-its-payload-buffer"></a>

## Failable-factory non-frozen arm leaks its payload buffer

2026-09 wave (s5, label-colliding init factories). `WrapperEmitter.EmitFailableFactory`'s non-frozen arm allocates `payloadBuffer`, `InitializeWithCopy`s the result into it, then constructs `new T((SwiftHandle)payloadBuffer)` — which copies again — and neither destroys nor frees the buffer. Pre-existing on the failable lane; the new non-failable factory lane refuses non-frozen structs precisely so it does not inherit the shape. **Trigger:** a consumer reports growth calling a `TryCreate…` factory on a non-frozen struct in a loop; fix = adopt through `PayloadConstructionSemantics` as the wave did for owned returns.

<a id="direct-inout-class-reference-cell-mismatch-refused-sibling-contracts-are-distinct"></a>

## Direct `inout`: class reference-cell mismatch refused; sibling contracts are distinct

**Revisit when:** an actual generated/native mismatch or a consumer need for one of those residual shapes; inspect storage, ownership and writeback independently before extending a refusal or introducing a carrier.

<details>
<summary>Recorded evidence and disposition</summary>

The concrete-class reopen trigger fired: a real compiled class replacement/inspection fixture generated and C#-verified successfully while exposing public `ref` but passing `.Payload`/SafeHandle by value through `CallConvSwift`. Independent Swift compiler IR loads/stores a reference CELL and releases the displaced object; passing the object handle is therefore a compiling-but-wrong ABI contract. `MemberValidationPipeline` now refuses concrete class-inout members with `UnsupportedSignature`, preserving the class and ordinary by-value neighbors. The existing existential and mismatch+large-Optional refusals remain. The bounded sibling inventory found String uses `ref SwiftString.Buffer` into pinned value storage; supported synchronous public `ref string` routes now write back after native return, including Swift errors and managed result-conversion failures. The proven method-wrapper `Optional<String>` route preserves None/Some transitions. Missing entry points preserve the original managed value; resilient and noncopyable values pass their payload STORAGE address, so they must not inherit the class verdict. Scalar/frozen cdecl inout retains its existing writeback tests. This does not qualify all direct inout paths: ObjC-bridgeable values, enums, generic/optional nesting and platform-specific direct value behavior remain unwalked or incompletely runtime-qualified. Separately, the original instance direct consuming-argument fixture (`OwnershipStructConsumer.consumeDirectAndThrow`) was unresolved on Mono when this row was written; it is now **attributed upstream** at register level — see [Mono GC-safe-region cookie clobber](runtime-abi.md#mono-clobbers-the-gc-safe-region-cookie-in-x20-so-any-swiftself-carrying-direct-callconvswift-p-invoke-can-abo) — and the P/Invoke it emitted is ABI-correct. **Trigger:** an actual generated/native mismatch or a consumer need for one of those residual shapes; inspect storage, ownership and writeback independently before extending a refusal or introducing a carrier.

</details>

<a id="mono-clobbers-the-gc-safe-region-cookie-in-x20-so-any-swiftself-carrying-direct-callconvswift-p-invoke-can-abo"></a>

## Mono clobbers the GC-safe-region cookie in `x20`; eligible exposed members route through `@_cdecl`

<details>
<summary>Recorded evidence and disposition</summary>

**OWNER-03 closure (2026-09-15):** a fresh rebuild at `03f793ae3` reclassified the current Mono full-AOT binary before the final route change: 52 managed wrappers carrying `SwiftSelf`, comprising 40 safe, 9 `x20` clobbers, 2 cookie spills and 1 without a transition. The nine clobbers were six buffer-mode generic-parent property getters, `CarrierBox.relayThrough`, and the generic base `append` on `DefaultedHasher` / `DefaultedHasherWithFile`. Their exact generated ABIs and wrapper instruction windows are preserved in `src/docs/audits/0.20.0-owner03-release-failure-diagnosis/x20-cookie-resolution.md`. Buffer-mode metadata reconstruction now uses the real metadata accessor's `(request, argument-buffer)` ABI, bare method-generic returns on existential-opening strategies are initialized inside the opened generic body, and specialization-hint protocol keys prove imported SDK protocol identity for opening. Carrier-backed opening strategies keep bare-generic returns on their prior route because their module-scope scaffolding does not have the opened local type needed to initialize that result. The post-fix binary has 37 `SwiftSelf` wrappers — 35 safe, 1 spill, 1 without a transition, **zero clobbers** — and the Mono full-AOT suite is 4,072 pass / 0 fail / 32 skip / 0 crash (the original 4,067 plus five regressions). No Mono-keyed predicate, refusal, skip, waiver or timeout changed.

Confirmed upstream (2026-09-08), root cause pinned at register level; `src/docs/Future/upstream-issue-03-mono-set-insert-done-blocking.md` carries the filing text and the scope correction. **Mechanism:** Mono's managed-to-native wrapper brackets the call with a GC-safe-region transition whose cookie (`MonoThreadInfo*`) is parked in a callee-saved register chosen by the wrapper's register allocator, which does not exclude `x20`/`x21` — the registers `CallConvSwift` reserves for `SwiftSelf`/`SwiftError`. When it picks one that the same call then loads an argument into, the wrapper's own argument setup destroys the cookie before the branch; after the call it reloads the clobbered register and hands it to `mono_threads_exit_gc_safe_region_unbalanced`, which reads a garbage thread record (state `0` = `STARTING`) and SIGABRTs with `Cannot transition thread 0x0 from STARTING with DONE_BLOCKING` — *after* the Swift callee has already returned. **Evidence:** disassembly of the Mono full-AOT device binary's own wrappers; the failing `consumeDirectAndThrow` parks the cookie in `x20` and then overwrites it with `SwiftSelf` (`mov x20, x0` … `mov x20, x2`), while the passing sibling `consumeDirect` parks it in `x22` and leaves it intact. All 181 `SwiftSelf`-carrying wrappers in that binary were classified by whether the cookie register is *written* between the transition entry and the native branch: **14 clobbering, 157 safe, 5 with no GC transition, 5 unparsed**; the safe ones scattered across `x23:69, x24:42, x22:22, x25:20, x19:2, x20:1, x21:1`. Model validated 4/4 against independently-known outcomes (`ThrowingGetterBox.checkedValue` `x23`→pass, `GenericMethodHost.describeWithTag` `x20`→the existing in-tree `[Skip]`, `consumeDirectAndThrow` `x20`→abort, `consumeDirect` `x22`→pass). **Only `x20` clobbers were observed, and the `x21` arm is unconfirmed.** Mono's wrapper *does* write `x21` on every `SwiftError`-carrying call — `ThrowingBytesNamespace.countBytesOrThrow` emits `mov x21, #0x0` between the transition entry and the branch — so a cookie parked there would be destroyed the same way. But no wrapper sampled parks the cookie in `x21`: six `SwiftError`-carrying, `SwiftSelf`-free wrappers were disassembled separately and their cookies landed in `x19`/`x20`/`x22`, none clobbered. (An earlier revision of this row cited `NestedClosureHost` `NCB_F17B0E10` as an `x21`-park specimen; that was wrong — its P/Invoke is `CallConvCdecl`, which reserves no registers, so it is not evidence either way.) File the `x21` arm as *not excluded and destroyable if chosen*, not as reproduced. A later whole-binary measurement makes that concrete rather than sampled: across **14,027** wrappers in one Mono full-AOT device binary, **21** are `SwiftError`-carrying and `SwiftSelf`-free, **none** parks its cookie in `x21`, and **20 of those 21 write `x21`** between the transition entry and the branch. The arm is destroyable in principle and unoccupied in practice on this toolchain — still not reproduced, still not excluded. **Typed `SwiftSelf<T>` is outside the defect entirely:** a frozen-struct self travels in ordinary argument registers, so an `x20` cookie survives — `LeaseProbe.consumeGated` parks in `x20`, is not clobbered, and passes. **Shape-independent:** the non-throwing `consumeDirectWithLater` (no `SwiftError` at all) shows the identical `x20` clobber and is latent only because nothing calls it — so `throws`, `SwiftError`, tuple returns and `@out` are all incidental; `SwiftSelf` alone is sufficient exposure. (`consumeDirectAndThrow` and `consumeDirect` differ in their `SwiftError` setup as well as their cookie register, so that pair alone does not isolate the variable; `consumeDirectWithLater` is what does.) **Why there is no generic Mono gate:** the trigger is Mono's register allocation, which no Swift signature or C# declaration expresses, so there is no shape to predict — a shape-keyed refusal would be an unsound guess and would violate the prediction-gate freeze policy; and one generated assembly runs on Mono *and* NativeAOT, so there is no "Mono lane only" emission mode. Generator remedies are instead shape-proved `@_cdecl` routes, which remove `SwiftSelf` from eligible signatures without trying to predict Mono's register choice. **Disposition — rerouted (2026-09-10):** the answer was neither a shape gate nor waiting for upstream. The exposed members were moved onto `@_cdecl` wrappers, which removes the reserved register from the signature entirely, by narrowing the eligibility guards that were forcing them onto the direct route rather than by adding a Mono-keyed predicate. Route selection, the eligibility guards and the wrapper symbol namespace are described in `src/docs/Design/wrapper-route-selection.md`. **Residual exposure at the 2026-09-10 reroute checkpoint:** the corpus emitted **1,554** deduped direct `CallConvSwift` declarations (a denominator only — two independently written scanners disagreed by a handful on what counted as a duplicate declaration), of which **85 passed an untyped `SwiftSelf`** and were the population still exposed. **Zero** passed a typed `SwiftSelf<T>`. **25 carried `SwiftError`** — 13 as `ref SwiftError` on a Swift member and 12 as `out SwiftError` on a closure-invocation shim that passed `IntPtr __self` rather than a `SwiftSelf` — and 2 of those also carried an untyped `SwiftSelf`. Before that reroute the same three figures read 149 / 9 / 27 (the 27 being 17 `ref` and 10 `out`; an earlier revision of this row recorded 17 because it counted only the member-route `ref` parameters). Those counts are historical declaration-surface measurements, not the current binary classifier result recorded in the OWNER-03 closure above. `SimpleRowAdapter.layoutedAdapter` — one of the 14 wrappers whose cookie register is written before the branch, and the member whose probe aborted verbatim with `Cannot transition thread 0x3 from STARTING with DONE_BLOCKING` inside its own P/Invoke stub — now routes through a wrapper, and the test that reproduced the abort on the Mono full-AOT device lane is in the tree and green. **Reopen trigger:** Mono ships a fix (then re-check whether the `@_cdecl` reroutes for these shapes are still needed), or a consumer/lane reports the `DONE_BLOCKING` abort on a member not already routed through `@_cdecl` — in which case the disposition is the `@_cdecl` reroute, never a shape gate.

</details>

<a id="members-reached-only-through-a-protocol-dispatch-thunk-on-a-usablefrominline-internal-parent-cannot-be-wrapped"></a>

## Members reached only through a protocol dispatch thunk on a `@usableFromInline internal` parent cannot be wrapped — compilation-unit boundary

**Revisit when:** a consumer or corpus library hits the register defect on a member whose parent is module-internal, or the generator gains same-module wrapper emission for another reason.

<details>
<summary>Recorded evidence and disposition</summary>

Three members in the corpus (a property getter, a method and a subscript getter, all reached through `…Tj` conformance thunks) stay on the direct `CallConvSwift` route with an untyped `SwiftSelf` because their parent type is module-internal. **Mechanism:** the generated `@_cdecl` wrapper compiles as a **separate client module**, and a wrapper body would have to name the internal parent to reconstruct `self` — a name that is not visible outside the defining module. This is a compilation-unit boundary, not a predicate: no eligibility-guard change reaches it, and the emitter states the same conclusion from the other direction in its uncallable-internal-direct-dispatch check, which shares the parent-internal predicate with the eligibility gate precisely so the two cannot drift. **The sharp edge** is that the parent's invisibility does not retire the exposure: the conformance is public even though the type is not, so the member stays callable from a client and therefore stays on the exposed route. **Fix shape:** emit the wrapper into the binding target's *own* module rather than a separate wrapper module — an architecture change, not a guard change. **Trigger:** a consumer or corpus library hits the register defect on a member whose parent is module-internal, or the generator gains same-module wrapper emission for another reason.

</details>

<a id="failable-initializers-on-non-frozen-structs-stay-refused-the-refusal-costs-zero-exposed-members"></a>

## Failable initializers on non-frozen structs stay refused — the refusal costs zero exposed members

The constructor wrapper refuses a failable `init` on a resilient struct: the wrapper signals failure by leaving an `Optional<T>` result buffer empty, and initialising an `Optional` of a resilient payload interacts badly with the value-witness-driven path the managed side uses to decide whether a value was produced — the two disagree about who initialises the buffer and when, and for a resilient payload neither can inspect the other's work. **No member in the corpus trips it**, so the refusal costs nothing measurable today and re-deciding it would mean changing a guard with no benefit, which is what the prediction-gate freeze policy exists to prevent. **Trigger:** a corpus library or consumer places a failable initializer on a non-frozen struct behind this guard — and then re-derive the interaction from the code rather than inheriting this paragraph, because the managed creation path has moved since the guard was written and the argument may no longer hold in either direction.

<a id="a-property-literally-named-self-cannot-be-called-from-a-generated-swift-wrapper-the-obstruction-is-above-the-a"></a>

## A property literally named `self` cannot be called from a generated Swift wrapper — the obstruction is above the ABI

**Revisit when:** Swift gains a spelling that reaches a shadowed `self` member, or a consumer reports this member returning the wrong type.

<details>
<summary>Recorded evidence and disposition</summary>

A backtick-escaped `` `self` `` is a legal property name, and the corpus has one: a getter on a nested type returning the *outer* type. The wrapper stays refused and the member keeps the direct route with an untyped `SwiftSelf`. **Mechanism:** a wrapper body would have to write `obj.self` to reach it, and in Swift `x.self` is the *identity expression* — it evaluates to `x`, at `x`'s own type. The declared accessor is shadowed by the language and unreachable through ordinary member-access syntax, so the shim would compile and silently return the wrong type. The obstruction is at the Swift source level, not the ABI: the signature is expressible in C, the call is not expressible in Swift. The direct P/Invoke escapes it by naming the accessor's mangled symbol, which the shadowing rule does not touch. **Recorded independently of the verdict:** this member has **no runtime test on any lane**, so the direct path here is unobserved everywhere — a no-crash read test is cheap and worth adding whenever this area is next touched. **Trigger:** Swift gains a spelling that reaches a shadowed `self` member, or a consumer reports this member returning the wrong type.

</details>

<a id="an-inout-parameter-of-a-resilient-struct-declines-the-wrapper-and-the-direct-route-is-the-sound-answer"></a>

## An `inout` parameter of a resilient struct declines the wrapper, and the direct route is the sound answer

One corpus member takes a non-frozen struct `inout`; the wrapper declines and the member keeps the direct `CallConvSwift` route with an untyped `SwiftSelf`. **Mechanism:** the wrapper's write-back is a fixed-shape pointer store, but a resilient struct's size and field offsets are known only through the value-witness table at run time, so there is no C type the shim can declare for the buffer and no store it can emit that stays correct across library versions. Swift passes an `inout` resilient value as a pointer under its own convention and performs the write-back itself with full layout knowledge — so this is a **route-selection floor**, not a coverage gap: the route the wrapper declines to is the correct one. Closing the register exposure here would mean a shim that carries the value-witness table alongside the pointer and does witness-driven copies — a different mechanism, not a widened guard. **Trigger:** a consumer hits the register defect on a resilient-`inout` member, or a witness-carrying shim design is funded for another reason.

<a id="stored-properties-on-a-globally-isolated-type-cannot-be-read-from-a-generated-wrapper-cross-module-isolation-b"></a>

## Stored properties on a globally-isolated type cannot be read from a generated wrapper — cross-module isolation boundary

**Revisit when:** the generator gains same-module wrapper emission, or Swift changes when a stored property on a globally-isolated type is implicitly nonisolated across module boundaries — then re-run the negative control and expect it to go red.

<details>
<summary>Recorded evidence and disposition</summary>

Every stored-property accessor on a type annotated with a global actor stays on the direct `CallConvSwift` route with an untyped `SwiftSelf`; eleven declarations in the corpus. **Mechanism:** Swift makes a stored `let` of a `Sendable` type implicitly nonisolated only *inside the module that declares it*. The generated wrapper compiles as a separate module that imports the binding target, so a nonisolated synchronous read of **any** stored property on a globally-isolated type — `let` included — is refused there, at every strict-concurrency level and in both language modes. Immutability and the property type's `Sendability` do not move that line, which is why this is a boundary rather than a guard that could be narrowed: it is the same compilation-unit obstruction as a module-internal parent, arriving through isolation instead of visibility. **Held in place by a negative control**, deliberately: a fixture class carries the immutable, mutable, computed and getter-only-from-outside stored shapes and asserts that none of them grows a wrapper. The failure mode of getting this wrong is not a wrapper that misbehaves — it is a member that *disappears*, because a wrapper that cannot compile is withdrawn along with the accessor group it belongs to. **Trigger:** the generator gains same-module wrapper emission, or Swift changes when a stored property on a globally-isolated type is implicitly nonisolated across module boundaries — then re-run the negative control and expect it to go red.

</details>

<a id="the-untyped-swiftself-residue-that-is-a-coverage-gap-rather-than-a-bar-roughly-57-declarations-across-six-guar"></a>

## The untyped-`SwiftSelf` residue that is a coverage gap rather than a bar — roughly 57 declarations across six guards

**Revisit when:** a consumer or corpus library reports the register defect on one of these shapes, or a stream is funded to open a specific guard — per shape, behind its own test, never as a class.

<details>
<summary>Recorded evidence and disposition</summary>

What is left on the direct `CallConvSwift` route after the reroute, excluding the separately documented [internal-parent boundary](runtime-abi.md#members-reached-only-through-a-protocol-dispatch-thunk-on-a-usablefrominline-internal-parent-cannot-be-wrapped), [resilient failable initializers](runtime-abi.md#failable-initializers-on-non-frozen-structs-stay-refused-the-refusal-costs-zero-exposed-members), [shadowed self property](runtime-abi.md#a-property-literally-named-self-cannot-be-called-from-a-generated-swift-wrapper-the-obstruction-is-above-the-a), [resilient inout](runtime-abi.md#an-inout-parameter-of-a-resilient-struct-declines-the-wrapper-and-the-direct-route-is-the-sound-answer), [global isolation](runtime-abi.md#stored-properties-on-a-globally-isolated-type-cannot-be-read-from-a-generated-wrapper-cross-module-isolation-b). In descending size: members on a **generic parent** the generic static-dispatch route did not reach (the opened subset covers one wrapper per generic declaration with metadata as a parameter; class bounds, associated-type and same-type clauses, parameterized protocols, generic returns and `inout`/composite/non-copyable payloads were not opened); members with their **own generic parameters** outside the opened subset, for the same reasons; members taking a **closure parameter**, and property **setters whose value is a closure**, where the shim would have to reconstruct the closure's context on the Swift side; members inheriting a generic context from an enclosing declaration; and one property whose return is a generic container the wrapper cannot bridge. **These are fundable, not blocked** — each is a wrapper the emitter could learn to build — which is exactly why none of them was opened speculatively: the failure mode of an unsound wrapper is a runtime trap on a device lane, not a compile error, so each newly opened shape has to arrive with its own end-to-end fixture. **Trigger:** a consumer or corpus library reports the register defect on one of these shapes, or a stream is funded to open a specific guard — per shape, behind its own test, never as a class.

</details>

<a id="twelve-generic-parent-members-stay-behind-fail-closed-metadata-gates-dynamic-witness-table-threading-and-buffe"></a>

## Dynamic witness-table threading stays behind a fail-closed metadata gate; buffer-mode unpacking is resolved

**Revisit when:** a consumer needs a remaining dynamic-witness shape, or a device fixture can prove its exact witness construction — open that gate per shape behind its own test, never as a class.

<details>
<summary>Recorded evidence and disposition</summary>

**Partial resolution (2026-09-15):** descriptor-resolvable generic-parent properties that cross the four-direct-slot metadata-accessor threshold now reconstruct metadata with the accessor's buffer ABI and use generic static dispatch. Physical-device coverage exercises four metadata entries and two metadata plus two protocol-witness entries; the six previously clobbering property getters are no longer directly exposed. Dynamic witness-table construction remains closed and is still the trigger for revisiting this row.

The generic-parent reroute opened generic static dispatch with metadata passed as a parameter, one wrapper per generic *declaration* rather than per specialization. At that checkpoint twelve members did not follow: six needed a dynamic protocol-witness-table threaded into the metadata accessor, and six needed buffer-mode unpacking. Both gates were initially fail-closed and each member kept the direct `CallConvSwift` route with an untyped `SwiftSelf`. **Why they were not opened then:** the failure mode is an arm64e pointer-authentication trap from an arity disagreement between the C# P/Invoke, the metadata helper and the real metadata accessor — a runtime trap on a physical device, not a compile error — so each newly opened shape has to ship its own end-to-end test on the metadata path and be verified on device before its gate is relaxed. The buffer-mode half now satisfies that requirement; the dynamic-witness half does not. Three further generic-parent members were left closed for the same reason: an Optional-ABI tag probe, a bridged-URL-collection getter and a static member behind an availability gate. **Trigger:** a consumer needs a remaining closed shape, or a fixture exists for the dynamic-witness path on device — open the gate per shape behind its own test, never as a class.

</details>

<a id="a-frozen-copyable-struct-with-no-reference-bearing-field-is-refused-not-projected"></a>

## A `@frozen ~Copyable` struct with no reference-bearing field is refused, not projected

**Revisit when:** a corpus library or consumer needs a plain (no reference field) `~Copyable` struct bound, or Swift's move-only surface reaches enough of the Apple SDKs that refusing the shape costs real API.

<details>
<summary>Recorded evidence and disposition</summary>

The frozen/reference split decides the projection: a frozen struct carrying a reference becomes a Buffer-backed C# class with a real `Payload`, and its move-only semantics are already handled there (consumed arguments mark the payload, `Dispose` releases it) — that flavor stays supported and is the boundary this refusal must not cross. A frozen struct with only trivial fields becomes a **plain by-value C# struct with no payload at all**, and the move-only contract has nowhere to live: C# assignment copies a value Swift permits exactly one owner of, `Dispose()` is a no-op even when the Swift type declares a `deinit`, the value-witness copy the emitted paths use is `InitializeWithCopy` on a type that has no copy, and the consumed-ownership preflight has no `Payload` to read. A compiled probe confirmed the shape end-to-end: every member of such a type is already withdrawn by the C# verify-recover loop (nothing callable survives), but the *type projection itself* compiles clean and silently offers copy semantics — a failure the compilers cannot see, which is what puts it on the refusal side of the prediction-gate freeze policy rather than leaving it to verify-recover. Widening the class projection to cover it was tried against the same probe and **recovered zero members** while trading the copy for an `InitializeWithCopy` through the value witness, so it is not the fix. `TypeSkipConditions.NonCopyableValueProjection` now refuses the type explicitly and the dependency closure prunes every member that referenced it. **Fix shape:** a genuine move-only projection — a C# `ref struct`-shaped carrier (or an ownership-tracked wrapper) that has no copy constructor and can enforce a consumed value's single ownership by value — not a widening of either existing projection. **Trigger:** a corpus library or consumer needs a plain (no reference field) `~Copyable` struct bound, or Swift's move-only surface reaches enough of the Apple SDKs that refusing the shape costs real API.

</details>

<a id="noncopyable-payloads-frozen-async-parameter-and-over-refusal-residuals"></a>

## Noncopyable payloads: frozen async-parameter and over-refusal residuals

**Revisit when:** any of these shapes appears — a closure parameter or return of a `~Copyable` type, an `async` member taking or returning one, a failable or existential-bypass `init` on one — in a corpus library, a consumer report, or a new fixture; or a `SwiftOptional<T>` is instantiated over a non-copyable wrapper; or a consumer reports a trap from instantiating a generated generic bridge over a `~Copyable` carrier.

<details>
<summary>Recorded evidence and disposition</summary>

**Status — closed (2026-09).** All nine emitter sites and both runtime seams were dispositioned: eight lanes **refuse** at emission with a `binding-report.json` row and a skip marker — the five closure lanes (argument, escaping, throwing, two-argument, and the closure *result* lane that carries no ownership specifier in its Swift spelling), the two async-parameter staging lanes, and the failable-`Optional` constructor — while the async **return** carrier **takes** rather than copies, because the harness owns the allocation outright and the managed side is its only consumer. On the runtime side the copyability guard now precedes the POD fast path in the borrowed payload extractor, in its tuple-element sibling and in `SwiftOptional`, which is the case a hand-written or generic-bridge consumer can reach with its own type argument: a move-only type with trivial fields is POD and non-copyable at once, so the fast path was the hole. Zero copyable members changed route. Two carve-outs stay open and are what the trigger below now watches: a frozen carrier in the async-parameter position, and [noncopyable oracle over-refusal](runtime-abi.md#the-copyable-oracle-over-refuses-two-shapes-swift-actually-allows). The historical, pre-fix analysis that produced those dispositions follows; its present-tense defect descriptions describe the state before the fixes above. The move-construction pass fixed the lanes it set out to fix (the synchronous return / `MarshalToSwift` path now takes with `InitializeWithTake`, and the consumed-argument hand-over refuses a non-copyable record), then audited every remaining `InitializeWithCopy` on a payload path. Most are unreachable by construction — an `Array`/`Set`/`Dictionary`/`ClosedRange`/`Result` element, a tuple element, an existential slot and an unconstrained Swift generic parameter all carry an implicit `Copyable` bound, and a class-pointer "copy" is an ARC retain. Nine are genuinely reachable and stay unguarded, in three groups. **Closure argument lane** — `ClosureEmitter.InvokeThunk.cs:437`, `ClosureEmitter.StructParams.cs:262`, `ClosureEmitter.Throwing.cs:330` — all three take the `closureHandler.IsNonFrozenStruct(arg)` branch, and `IsNonFrozenStruct` tests only `Kind == Struct && !Frozen`, which a non-frozen `~Copyable` struct answers true. The root cause is one detection gap rather than three missing branches: `WrapperValidation.IsNonCopyableType` opens with `if (typeSpec is not NamedTypeSpec) return false;`, so the member-level oracle never descends into a `ClosureTypeSpec`'s argument types. **Async lane** — `WrapperEmitter.Async.cs:167` and `:186` (parameter copy buffers) and the return carriers at `AsyncHarnessEmitter.cs:1020` and `AsyncMethodGenericBridgeEmitter.cs:949`; none of those three files contains the substring `NonCopyable`. A directly-named `~Copyable` parameter passes the member gate by design (`CdeclParamMapper.LowersNonCopyableDirectly`), and the synchronous lane is sound only because it *adopts* rather than copies, while the async lane inserts an extra witness copy of the carrier before `MarshalFromSwift`. **Constructor-result lane** — `WrapperEmitter.FailableFactory.cs:270` and `ExistentialBypassEmitter.cs:1266`, whose non-frozen `else` arms copy the constructed `Self` out of the result buffer; both files guard `~Copyable` *arguments* and neither consults a copyability predicate for the parent. Two runtime lines sit behind the public API rather than any emitter: `SwiftOptional.cs:207` and `:292` place no copyability constraint on `T`, and `Optional<Wrapped: ~Copyable>` is legal Swift, so a hand-written interop consumer can reach them even though generated bindings never construct one. One further site is **not** latent: `SwiftMarshal.MarshalExtractedPayloadValue` gates its copy on `sem != PayloadConstructionSemantics.Inline`, which admits `Move` — the `~Copyable` semantics — and the tuple-element extraction path beside it does the same. A paired review established that this is consumer-reachable today through the generated generic closure bridge: `GenericClosureBridgeEmitter` emits `where T : ISwiftObject`, a constraint that cannot express Swift's implicit `T: Copyable`, so a C# consumer may instantiate the bridge over a `~Copyable` carrier (which is an `ISwiftObject` like any other) and reach the borrowed extraction helper. Nothing in the generator chooses that type argument — the consumer does — so no emission-time refusal can see it. The obvious one-line correction (`sem is Adopt or Copy`) is **wrong**: it would route a non-copyable into the bitwise-memcpy `else` arm, producing two owners of one reference-bearing value and a double `deinit`. Nor can the semantics enum answer the question as it stands: `SwiftString` declares `Move` while being perfectly copyable, because its `Move` describes *the wire buffer was moved from*, not *this type has no copy* — two orthogonal facts the one enum currently conflates. That site copies out of a *borrowed* source, which a non-copyable value cannot do at all, so the honest treatment is a diagnosable refusal keyed on a copyability signal the enum does not yet carry, not a different copy. **Fix shape:** a runtime-visible copyability signal, then one shared refusal rather than nine patches. Swift's `TargetValueWitnessFlags` carries a non-copyable bit that `ValueWitnessFlags` does not yet model, which would let the marshal seam fail with a describable exception instead of trapping in `__swift_cannot_copy_noncopyable_type`; the bit's value must be confirmed against a real metadata probe before it is encoded, not taken from memory. On the emitter side the closure group wants `IsNonCopyableType` to descend into `ClosureTypeSpec`, and the async and constructor-result groups want the parent/carrier consulted by the same corrected predicate. That reading — nine latent emitter sites plus two consumer-reachable runtime sites — is what the pass above acted on: the runtime seams were guarded first because a consumer's own type argument reaches them, and each emitter lane was then given the disposition its ownership story supports rather than a uniform patch. **Trigger:** any of these shapes appears — a closure parameter or return of a `~Copyable` type, an `async` member taking or returning one, a failable or existential-bypass `init` on one — in a corpus library, a consumer report, or a new fixture; or a `SwiftOptional<T>` is instantiated over a non-copyable wrapper; or a consumer reports a trap from instantiating a generated generic bridge over a `~Copyable` carrier.

</details>

<a id="the-copyable-oracle-over-refuses-two-shapes-swift-actually-allows"></a>

## The `~Copyable` oracle over-refuses two shapes Swift actually allows

**Revisit when:** a corpus library or consumer reports a member refused with `NonCopyableThroughGenericSlot` whose Swift signature copies fine, or a producer emitting conformance-free type nodes enters the supported set.

<details>
<summary>Recorded evidence and disposition</summary>

`WrapperValidation.IsNonCopyableType` recurses into ALL generic arguments and, for a nested spelling, resolves the OUTER segment first. Both were deliberate fail-closed choices, and a paired review compiler-verified that both over-refuse. Swift permits a declaration to remain explicitly `Copyable` while admitting a non-copyable type argument it never stores or copies (`struct Phantom<T: ~Copyable>: Copyable { var n: Int }` — `requireCopyable(Phantom<Token>(n: 1))` compiles), and it permits a `Copyable` nested type inside a `~Copyable` outer type. The oracle classifies both as non-copyable, so `MemberValidationPipeline` / `MemberGateEvaluator` refuse members whose copy witness is perfectly valid. The direction of the error is safe — a refused member is a missing binding, never a trap — which is why it was not fixed in the pass that introduced the recursion: the correct predicate has to ask the *outer declaration's own* conformances (does it declare `Copyable`?) before recursing into its arguments, and that answer is not reliably present in every ABI dump the parser accepts. A related asymmetry sits on the other side: a dump node carrying NEITHER `Copyable` nor `Escapable` is read as copyable, so an older producer that predates explicit `Copyable` conformances yields a false negative rather than a false positive. **Fix shape:** make the oracle consult the enclosing declaration's declared conformances and recurse only when the enclosure does not assert `Copyable`, and decide explicitly what a conformance-free node means per producer version — not a per-caller carve-out. **Trigger:** a corpus library or consumer reports a member refused with `NonCopyableThroughGenericSlot` whose Swift signature copies fine, or a producer emitting conformance-free type nodes enters the supported set.

</details>

<a id="a-net-static-class-reference-is-still-reported-as-a-swiftui-constraint"></a>

## A .NET static-class reference is still reported as a SwiftUI constraint

**Revisit when:** the next skip-reporting pass, or a consumer report citing a SwiftUI constraint on a member whose signature names no SwiftUI type.

<details>
<summary>Recorded evidence and disposition</summary>

The skip-reason honesty pass split "signature names a type this binding refuses to emit" out of the `SwiftUIConstraint` bucket as `SkipReason.SkippedTypeReference`. One inhabitant of that bucket was left behind: `ValidationRuleSet.ClassifyUnsupportedReference` classifies a type that projects to a .NET **static class** (unusable as a variable, parameter, return or type argument — CS0718/CS0723) as `OtherUnsupported`, which `ToSkipReason` maps to `SwiftUIConstraint` and `DescribeUnsupportedReference` renders as "unsupported module (SwiftUI/Combine)". A consumer reading that row is told to look at a SwiftUI boundary that has nothing to do with the cause. The cause is real and the refusal is right; only the label is wrong. It was left out of the honesty pass because the honest fix is a new `SkipReason` member plus its attribution, disposition and workaround map entries and a new skip-surface reason key — the same shape as the split that was done, but a second one, landing after that pass had already been reviewed. **Fix shape:** one more `UnsupportedReferenceKind` → `SkipReason` split at the same three classifier seams, with the prose naming the static class; no emission logic moves. **Trigger:** the next skip-reporting pass, or a consumer report citing a SwiftUI constraint on a member whose signature names no SwiftUI type.

</details>

<a id="a-success-followed-by-a-throw-before-the-move-strands-one-1-rather-than-risking-a-double-deinit"></a>

## A success followed by a throw before the move strands one `+1` rather than risking a double `deinit`

A deliberate trade, documented in the remarks on `MethodMarshalPlanBuilder.BuildCopyOutWireCleanup`. When a call succeeds and then throws before its value is moved out, the cleanup declines to destroy the wire buffer, so the `deinit` never runs and one `+1` is stranded. Destroying on that path instead would risk destroying a value the callee has already taken — a double `deinit` rather than a leak — so the leak is the safe direction. It is not fixable at the site: the wire protocol carries no signal for whether the value is still live at the throw. **Fix shape:** a wire-level liveness signal, then one shared cleanup decision — not a per-site change. **Trigger:** the wire protocol gains such a signal, or a consumer reports growth traceable to a throw on a successful call.

<a id="one-hand-written-runtime-p-invoke-still-carries-an-untyped-swiftself-swiftequatable-equals"></a>

## One hand-written runtime P/Invoke still carries an untyped `SwiftSelf` — `SwiftEquatable.Equals`

**Revisit when:** a consumer or corpus run reports a wrong or crashing `SwiftEquatable.Equals` under Mono (JIT or full-AOT), or the runtime's native xcframework is being rebuilt for another reason.

<details>
<summary>Recorded evidence and disposition</summary>

The cdecl-reroute wave moved `Hashable.hashValue` and the six shape-A collection ops onto C cdecl shims and wrote the coverage rule into `SwiftCollectionCdeclWrappers.cs` ("every hand-written `Swift.Runtime` P/Invoke carrying an untyped `SwiftSelf` is wrapped, plus the six shape-A ops"). `SwiftEquatable.cs:17-24` was not moved: `PInvoke_SwiftEquals` is still `CallConvSwift` with an untyped `SwiftSelf typeMetadataInSwiftSelf` parameter, which is exactly the shape-B `x20` cookie-clobber class the wave set out to close. The **population is empirically closed at one** — a repo-wide search of `src/Swift.Runtime/src` for an untyped `SwiftSelf` on a `DllImport` hits only this declaration; every other occurrence is prose. It is not queued because the honest fix is larger than the site: a C shim plus a Cdecl P/Invoke plus a call-site change plus a rebuild of the **shipped** native runtime xcframework, and `$sSQ2eeoiySbx_xtFZTj` is a *static* witness requirement, so the metatype rides `swift_context` rather than an ordinary argument. Asserting that register contract without a disassembly is a guess whose failure mode is a garbage branch target rather than a wrong value, and the only lane that can verify it is a device Mono-full-AOT run. **Fix shape:** the byte-adjacent template already exists — `SBW_Hashable_HashValue` in `SwiftBindingsRuntimeCollections.c` (an `extern` `SBW_SWIFTCALL` declaration bearing the `__asm` mangled symbol with `SBW_SWIFT_CONTEXT selfPtr`, plus a one-line forwarding Cdecl entry point) — so the work is confirming the `==` witness's register layout against a real SIL/asm dump, then following that template and re-verifying on `--device --mono-aot`. **Trigger:** a consumer or corpus run reports a wrong or crashing `SwiftEquatable.Equals` under Mono (JIT or full-AOT), or the runtime's native xcframework is being rebuilt for another reason.

</details>

<a id="the-thunk-route-still-reads-the-ownership-specifier-for-copyable-address-only-parameters"></a>

## The thunk route still reads the ownership specifier for COPYABLE address-only parameters

The noncopyable hand-over pass made the consumed hand-over a property of the emitted route and refused the shapes with no move-capable route, but it deliberately left one site alone: `NativeThunkEmitter.cs:657` still reads the declared ownership specifier for **copyable** address-only parameters, and whether that reading is correct there was never investigated. It was left because widening the refusal at that site would rebind many stable signatures — a large, ABI-visible change resting on an untested hypothesis. **Fix shape:** first establish, with a compiled specimen rather than a reading of the emitter, whether a copyable address-only parameter's specifier changes what the callee expects on that route; decide afterwards. Do not widen the refusal first. **Trigger:** a consumer or corpus library reports a copyable address-only parameter mis-handed on the thunk route, or a session is already changing that site for another reason.
