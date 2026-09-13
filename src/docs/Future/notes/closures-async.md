# Closures and async — reference notes

Searchable findings to consult when working in this area. Nothing here is queued or newly authorized.
[How to use and maintain these notes](README.md). Recorded evidence and counts describe their original investigation; recheck against current source before acting.

<a id="async-closure-start-thunk-bridge-is-gated-to-async-cdecl-wrapped-carriers"></a>

## Async-closure start-thunk bridge is gated to async, cdecl-wrapped carriers

<details>
<summary>Recorded evidence and disposition</summary>

A baseline async closure parameter reaches the start-thunk bridge only when the *carrying member* is itself async and `@_cdecl`-wrapped (`PInvokeEmitter`'s two async-closure arms require `UsesCdeclMethodWrapper && IsAsync`, plus `Throws` for the throwing baseline). Every other carrier — a **sync** method taking an `async` closure, or an async member that never got a cdecl wrapper — degrades the parameter to the `AnyType` placeholder, marks the signature unbridgeable, and the member is refused/tombstoned rather than bound. A compile probe established that this boundary is **implementation, not ABI**: the start-thunk calling convention itself does not require the outer member to be async, so the restriction is about which wrapper shapes the emitter knows how to write, not about what the Swift runtime will accept. Widening it is therefore a real feature (a sync outer `@_cdecl` wrapper that starts the task and blocks or hands back a handle), with its own ownership/lifetime story for the closure context and its own device-leg proof — not a predicate tweak. **Not authorized. Trigger:** consumer demand for async closure members on sync (or otherwise non-cdecl) methods; reproduce the exact carrier shape as a fixture before designing the wrapper.

</details>

<a id="struct-enum-constructor-exclusion-from-the-unsupported-closure-tombstone"></a>

## Struct/enum-constructor exclusion from the unsupported-closure tombstone

`ClosureParamTombstoneEmitter.IsEligible` covers class constructors but deliberately excludes **struct/enum** constructors, with the scope rationale "definite-assignment rules require all fields be assigned" — so a struct initializer carrying an unsupported closure parameter vanishes from the surface entirely instead of surviving as a visible, `SB0005`-marked throwing member the way its class sibling does. That rationale is void at the language level we emit for: since C# 11 a struct constructor no longer has to definitely-assign every field (unassigned fields are auto-defaulted), and generated bindings target net10. So widening the tombstone to struct/enum ctors would compile — what remains is a **product surface** call: more visible-but-uninvokable members (a consumer sees "right API, not bridgeable yet") versus fewer, silently absent ones. **Not authorized. Trigger:** a skip-surface review flags struct-ctor tombstones as recoverable, or a consumer reports a missing struct initializer whose only obstacle is a closure parameter.

<a id="imported-objc-apple-no-payload-enums-are-over-skipped-on-the-direct-closure-lane"></a>

## Imported ObjC/Apple no-payload enums are over-skipped on the direct closure lane

2026-09 wave (s8, by-value direct bridge). `DirectClosureArgAbi` refuses every record flagged `SimpleEnum` as a closure argument, including ObjC-umbrella imports, `ExternalAppleEnum` records and XML-database entries (`UIKitDatabase.xml` alone carries 51). IR shows those are raw-value laid out at full width and would be `PassThrough`-correct, so the refusal is stricter than the ABI requires for them. Not narrowed because the four `SimpleEnum` assignment sites do not separate ObjC-imported from native-Swift enums in the Apple buckets, and a wrong narrowing readmits a silent ABI mismatch rather than a compile error. Measured cost on the BindingTests corpus is zero (API manifest: 88 added, 0 removed). The fix is a real ObjC-provenance marker on the record. **Trigger:** `nuke validate` shows API removals attributable to this arm.

<a id="remaining-closure-and-async-findings-from-the-binding-quality-wave"></a>

## Remaining closure and async findings from the binding-quality wave

The wave's numbered defects are resolved or disproved; the remaining work is narrower:

- The two completion-handler-to-Task error arms and the typed-throws async mismatch arm still lack the retained payload needed for dynamic typed-error classification. See the error-path entry in this file.
- Reference-cell and Optional closure `inout` contracts remain refused.
- A closure-bearing existential vtable slot remains deferred even though the concrete closure-only initializer now binds. Revisit when a consumer or corpus library needs the member through the existential.
- The recorded Lottie package-route recheck requires a runtime at or after `82ef2b438`; check for a later receipt before repeating it.

**Current decision:** hold these remaining shapes until their consumer signal or a scoped decision makes them relevant. The resolved items do not constitute a new wave proposal.

<a id="async-and-completion-handler-error-paths-surface-the-untyped-swiftexception-never-swiftexception-t"></a>

## Async and completion-handler error paths surface the untyped `SwiftException`, never `SwiftException<T>`

**Revisit when:** a consumer needs `catch (SwiftException<MyError>)` to work on an `async throws` member or a completion-handler-bridged member, or the typed-throws mismatch arm is observed losing error identity a consumer depended on.

<details>
<summary>Recorded evidence and disposition</summary>

Added 2026-09-11 (reroute-wave audit). The module error registry's synchronous classifier (`ErrorRegistryHelperEmitter.GetSyncDispatchHelperReference` -> `CreateSyncException`) is wired into all four synchronous throw sites, and the asynchronous cascade dispatcher (`CreateException`) is wired into the async plain-`throws` arm. Three error paths remain untyped, and per-site assessment shows none of them can adopt the sync classifier as-is, because the value they hold is not a retained Swift error box: the two completion-handler-to-`Task` bridge arms throw on an *already-materialized* `Foundation.AnyError` that the closure marshaller has fully projected (there is no box, no description pointer and no release callback in scope), and the typed-throws async arm reached on a declared-type mismatch runs only when the Swift catch block has already decided not to hand over a payload pointer, so there is nothing to classify. Two further candidate sites turn out to need nothing: the async no-registered-error-types arms are reached under exactly the condition the sync helper itself returns null for, and the legacy async projection template is invoked only by unit tests. **Fix shape:** retain and forward the underlying Swift error from the completion-handler closure thunk, and route a typed-throws mismatch through the cascade dispatcher as a second-chance classification — a change to the Swift wrapper's wire shape and to the closure thunk, not a swap of the C# throw expression, which is why it is not folded into the sync pass. **Trigger to revisit:** a consumer needs `catch (SwiftException<MyError>)` to work on an `async throws` member or a completion-handler-bridged member, or the typed-throws mismatch arm is observed losing error identity a consumer depended on.

</details>

<a id="one-unsupported-closure-shape-can-poison-every-free-function-in-the-module-through-the-static-constructor"></a>

## One unsupported closure shape can poison every free function in the module through the static constructor

**Revisit when:** a corpus library or a consumer report surfaces a throwing closure with a tuple (or other non-blittable) return, or any `TypeInitializationException` is reported against a generated free-function container.

<details>
<summary>Recorded evidence and disposition</summary>

Added 2026-09-11 (reroute-wave audit), found by a probe fixture rather than by a consumer. A throwing closure parameter whose return type the wrapper route declines — observed with a tuple return — falls back to a direct `delegate* unmanaged[Swift]` callback whose return type is not blittable under that calling convention. The callback is a **static field initializer** on the module's free-function container, so the runtime's rejection of that one signature is thrown from the container's type initializer and every free function in the module becomes unreachable behind a `TypeInitializationException`, not just the member that carried the closure. Measured blast radius on the probe: one member took 1082 test invocations down. The shape is **not in the corpus today** — the probe fixture was removed once it had answered its question, so nothing currently regresses. Note for whoever funds this: the emission-time prediction-gate freeze policy would *license* a gate here, because the failure it prevents compiles cleanly and only fails when the runtime validates the signature. **Fix shape:** make the closure gate's layer-1 admission agree with what the direct fallback route can actually declare, so a shape the wrapper declines is refused outright rather than emitted onto a route that cannot carry it; failing that, move the callback off the shared static container so the blast radius is the member rather than the module. **Trigger to revisit:** a corpus library or a consumer report surfaces a throwing closure with a tuple (or other non-blittable) return, or any `TypeInitializationException` is reported against a generated free-function container.

</details>

<a id="disposable-failure-carrier-for-throwing-closure-errors"></a>

## Disposable failure carrier for throwing-closure errors

`ClosureEmitter.InvokeThunk.cs:360-368` failure path emits `SwiftResult.FromFailure(new SwiftError((void*)_err))`; the +1 retain that `Unmanaged.passRetained` took in the `@_cdecl` thunk stays with the `SwiftError` for its lifetime and never releases — `SwiftError` has no `Dispose`, matching the established `SwiftErrorException.Error` / `ClosureEmitter.Throwing.cs` round-trip convention. Per-error leak; only matters when a Swift→C# throwing closure throws frequently. Real fix: introduce an `IDisposable` failure carrier (Dispose-able wrapper around `SwiftError` for the closure-result failure slot) so consumers release the +1 when done with the error, without changing the broader "managed code never releases bare `SwiftError` pointers" convention used by exception forwarding. **Trigger to revisit:** a real consumer surfaces measurable error-path throughput in a throwing closure callback.

<a id="optional-objc-bridgeable-value-type-closure-argument-is-unsupported-e-g-url-void"></a>

## `Optional<ObjC-bridgeable value type>` closure argument is unsupported (e.g. `(URL?) -> Void`)

**Revisit when:** a real consumer (or the next closure-marshalling session) needs an `Optional<ObjC-bridgeable value>` closure argument. **Remediation shape (red-first per `feedback_tdd_for_regression_fixes`):** this needs a dedicated Swift bridging thunk that materialises the value inner into an object pointer before the C# callback runs (the same mechanism general value-type closure arguments require); reproduce with a maximum-case `(URL?) -> Void` callback-arg fixture first, then add the thunk so the closure-argument predicate can safely widen to value inners.

<details>
<summary>Recorded evidence and disposition</summary>

A closure parameter whose single argument is `Optional<ObjC-bridgeable VALUE type>` (`URL`/`URLRequest`/`Decimal`/…) is **not** cdecl-compatible, so the method emits its callback through the `CallConvSwift` closure path (`delegate* unmanaged[Swift]<…>` against the direct `Tj` thunk) rather than the `@_cdecl` arm. A closure slot carries such an inner by its Swift VALUE representation — there is no Swift-side `as AnyObject` bridge on the reverse-closure path — so any C# read that treats the slot as an object pointer (`GetNSObject<NSUrl>(arg0)`) aborts at runtime (`_objc_fatal`). **This is ours, not upstream** — the read doesn't match what Swift passes. The per-argument lowering runs identically for the `@_cdecl` and `CallConvSwift` closure shapes, so the closure-argument predicate must stay narrow (`ClosureHandler.IsOptionalReferenceArg` = `Optional<T>` with a *true-reference* inner only — class / ObjC-rooted / ObjC-bridged). It is deliberately distinct from the WIDER producer-position oracle `WrapperValidation.IsOptionalWithReferenceInner`, which classifies bridgeable value types as nullable-pointer ABI because a witness-getter / `@_cdecl` *return* does materialise the pointer via `as AnyObject`; feeding that wider oracle into a closure slot is what made a `(URL?) -> Void` arbiter abort (the divergence is pinned by `OptionalReferenceClassifierTests`). The exploratory value-type arbiter fixtures have been removed — they exercised this never-worked capability, not the in-scope ownership convergence. **Trigger to revisit:** a real consumer (or the next closure-marshalling session) needs an `Optional<ObjC-bridgeable value>` closure argument. **Remediation shape (red-first per `feedback_tdd_for_regression_fixes`):** this needs a dedicated Swift bridging thunk that materialises the value inner into an object pointer before the C# callback runs (the same mechanism general value-type closure arguments require); reproduce with a maximum-case `(URL?) -> Void` callback-arg fixture first, then add the thunk so the closure-argument predicate can safely widen to value inners.

</details>

<a id="legacy-async-fault-arm-cancels-with-a-token-less-trysetcanceled"></a>

## Legacy async fault arm cancels with a token-less `TrySetCanceled()`

The legacy (non-token-threaded) async projection emits `tcs.TrySetCanceled()` with no token on the Swift-reported-cancellation arm (`AsyncProjection.cs:231`), so `OperationCanceledException.CancellationToken` on that path never identifies the requesting token; the modern path captures it via `AsyncCallState.CaptureCancellationToken`. The cancellation itself is correct — only token correlation is missing. **Trigger:** a consumer needs to correlate a legacy-path cancellation to a specific token.

<a id="closure-nested-in-a-non-collection-generic-container-still-emits-a-delegate-type-argument-cs0306"></a>

## Closure nested in a non-collection generic container still emits a `delegate*` type argument (CS0306)

`ClosureHandler.ContainsClosureNestedInCollection` gates members whose closure sits under `Array`/`Set`/`Dictionary` (the Moya `[Endpoint: [(Result) -> Void]]` shape), because the projected element type is a `delegate* unmanaged[Swift]<…>` used as a generic type argument — illegal C# (CS0306) — and independently runtime-unsound (no FROM-Swift delegate marshal arm, no `Function` metadata for element stride/destroy). Other generic carriers can compose the same broken shape — e.g. `Result<() -> Void, E>` or a user generic `Box<(Int) -> Void>` — and are NOT covered by the collection-only predicate. Tuples-with-closures are already gated separately (`ContainsUnsupportedTupleElement` + the cdecl tuple-buffer gate). **Trigger:** a corpus or consumer red showing CS0306 from a closure under a non-collection generic container. Fix shape: widen the predicate to any bound-generic carrier whose projection passes the closure through as a generic type argument, keeping bare and `Optional<Closure>` (both supported) excluded.

<a id="nested-closure-constructor-recovery-is-bounded-most-closure-bearing-initializers-are-still-refused"></a>

## Nested-closure constructor recovery is bounded — most closure-bearing initializers are still refused

The closure-bearing-initializer recovery (`927f41a4`) widened `NestedClosureBridge` to admit root, non-failable, non-generic, non-ObjC-rooted, non-isolated classes with no defaulted parameters and *sync* nested closures, recovering 2 initializers plus a downstream type. Failable, throwing, async, struct and enum nested-closure constructors, and every `MethodClosureBridge` constructor, remain refused — this is not “constructors with closures work now”. Throwing inner closures are refused deliberately: admitting them force-casts to a non-throwing Swift type that compiles and then traps on the first callback. **Trigger:** a shipped library is blocked on one of the still-refused constructor shapes, or the owner funds widening the bridge.

<a id="legacy-direct-callconvswift-async-closure-path-non-xcframework-mode-is-unverified-at-runtime"></a>

## Legacy direct-CallConvSwift async-closure path (non-xcframework mode) is unverified at runtime

2026-09 wave (s1). The async escaping-closure carrier now rides the same cdecl pointer pair the sync path uses whenever the member is `@_cdecl`-wrapped, which is every BindingTests and validation lane. The pre-existing direct-CallConvSwift arm for non-xcframework (dylib-only) generation was left intact and is exercised by unit tests only — no runtime gate builds in that mode. **Trigger:** a dylib-only consumer reports an async closure crash, or a runtime lane for non-xcframework generation is added.

<a id="objc-bridgeable-value-types-on-the-direct-closure-path-are-handed-to-swift-as-raw-layouts"></a>

## ObjC-bridgeable VALUE types on the direct closure path are handed to Swift as raw layouts

2026-09 wave (s7/s8). `ClosureEmitter.cs` (`FormatObjCBridgeCall`, ~line 969) bridges an ObjC-bridgeable closure argument by passing the C# projection's buffer where the Swift closure expects the Swift value. It works for `URL` only because `URL`'s layout happens to be one reference word; `URLRequest`, `IndexPath`, `Decimal`, `Calendar`, `Locale`, `CharacterSet` have wider or non-reference layouts and would be read wrong. No fixture exercises those today, and the by-value direct lane (s8) fails closed on the shapes it does not model, so the reachable set is `URL`-shaped only. Fix = `as AnyObject` bridging in `GetSwiftArgConversion` with a per-type layout check. **Trigger:** a fixture or consumer closure takes any ObjC-bridgeable value type other than `URL` on the direct lane.

<a id="direct-lane-by-value-closure-arguments-shapes-deliberately-failed-closed"></a>

## Direct-lane by-value closure arguments: shapes deliberately failed closed

2026-09 wave (s8, `DirectClosureArgAbi`). Each of these is refused with a unit-tested skip reason rather than modelled: `@frozen` struct and `Optional<@frozen struct>`, `ArraySlice<T>` and its Optional, `@frozen` payload-carrying enum, every no-payload enum (bare, as a tuple element, as an `Optional` payload, as a `Result` success payload), `Optional<Foundation.Data>`, `Optional<Result<loadable T, any Error>>`, `Optional<Int32>`/`Optional<Bool>`, `Int??`. Each is a real register schema that needs more layout machinery (exploded-word counts per nested field, spare-bit optional discriminators) than the lane models; none is a wrong model. Members refused here still bind through the wrapper lane where one exists. **Trigger:** `nuke validate` or a consumer shows a member lost to one of these skips with no wrapper fallback.

<a id="direct-lane-property-exemption-restates-the-wrapper-lane-s-refusals-wrapperlanerefusesregardlessofclosure-unde"></a>

## Direct-lane property exemption restates the wrapper lane's refusals; `WrapperLaneRefusesRegardlessOfClosure` under-estimates for methods

2026-09 wave (s8). `MemberValidationPipeline`'s property exemption encodes the wrapper's refusal arms (`IsModuleInternal`, `IsSpiProtected`, parent `IsModuleInternal`, `IsActorIsolatedMember`) directly because `GetMemberRejectionReason` needs a `MethodEnvironment` validation cannot build; every arm is a pure decl flag or the wrapper's own public predicate, so drift is loud, but a *new* decl-visible refusal added to `WrapperValidation` must be mirrored. `WrapperLaneRefusesRegardlessOfClosure` answers only for constructors and returns false for every other member kind (matching `main`); under-estimating refusal reproduces today's behaviour, over-estimating would newly over-skip working members. A variadic method (`Int...`) therefore reaches the wrapper and fails as a `swiftc` compile error inside the verify-recover loop — by the prediction-gate freeze policy that is the intended catch. **Trigger:** a new decl-visible refusal lands in `WrapperValidation`, or validation grows a `MethodEnvironment`.

<a id="a-closure-carried-raw-value-enum-whose-raw-values-the-swiftinterface-stripped-marshals-a-raw-value-against-a-c"></a>

## A closure-carried raw-value enum whose raw values the `.swiftinterface` stripped marshals a raw value against a C# ordinal

**Revisit when:** a corpus library or consumer passes a non-`@objc` integral raw-value enum with non-ordinal raw values through a closure parameter or return, or any `init(rawValue:)` force-unwrap trap is reported from a generated Swift wrapper.

<details>
<summary>Recorded evidence and disposition</summary>

Added 2026-09-11 (reroute-wave audit), found by a fixture written for the `inout` enum cell. The C# enum member carries `EnumDecl.GetCaseMarshalScalar` — the Swift source raw value when the per-case raw values are recoverable, and the declaration-order tag when they are not. The closure lane derives its scalar independently: `ClosureEmitter.SwiftWrapper.cs` `GetSwiftArgConversion`/`GetSwiftReturnConversion` emit `.rawValue` and `init(rawValue:)` whenever `GetSimpleEnumInfo().hasRawValue`, which keys only off the raw value *type name* (`ClosureHandler.cs:2491`). Swift preserves explicit enum raw values in the textual interface only for `@objc` enums, so a plain `enum: Int32 { case queued = 4100 }` reaches the closure boundary with the C# member numbered `0` and the Swift side marshalling `4100`; the C#-to-Swift direction then force-unwraps `BuilderStage(rawValue: 0)!` and traps. Same divergence class as the cdecl return sentinel: a parallel derivation instead of the mapping that chose the emitted value. Not reachable for `@objc` enums (values recovered, both sides agree) nor for any enum whose raw values equal their ordinals, which is every such enum in the corpus today. **Fix shape:** carry "explicit raw values were recovered" as a `TypeRecordFlags` bit set in `ModuleProcessor.CalculateEnumFlags`, serialized through `ModuleDatabaseEmitter`/`TypeDatabase` XML and set by the ObjC-bridged and Apple-registry record factories, and require it in `GetSimpleEnumInfo().hasRawValue` so an unrecoverable enum takes the tag path the C# member was numbered from. Parser + type-database + closure-lane change with cross-module serialization fidelity, which is why it is not folded into the audit's fix pass. **Trigger to revisit:** a corpus library or consumer passes a non-`@objc` integral raw-value enum with non-ordinal raw values through a closure parameter or return, or any `init(rawValue:)` force-unwrap trap is reported from a generated Swift wrapper.

</details>

<a id="the-generated-closure-context-helper-can-bind-a-wrong-nonzero-factory-from-a-competing-image"></a>

## The generated closure-context helper can bind a wrong-nonzero factory from a competing image

`ClosureContextHelperEmitter.cs:86-95` emits a helper that branches only on nil when it resolves `SwiftBindings_NewClosureContext`, so a second image in the same process exporting that symbol can supply the factory: the `GCHandle` is then never freed, or destruction routes to a registration authority that did not create the context. Reaching it needs two runtime images in one process, which no consumer configuration in the tree produces — and the detection that makes it observable at all was added by the pass that found this. **Fix shape:** an emitter-side suppression signal plus an isolated fixture that deliberately exports a competing symbol, done together; a one-line nil-check change does not address which factory is right. **Trigger:** a consumer ships two runtime images in one process, or a packaging change makes duplicating the symbol possible.
