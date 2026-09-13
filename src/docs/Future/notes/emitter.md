# Emission, naming and consumer surface — reference notes

Searchable findings to consult when working in this area. Nothing here is queued or newly authorized.
[How to use and maintain these notes](README.md). Recorded evidence and counts describe their original investigation; recheck against current source before acting.

Deferred residuals from the 2026-07 usability-probe pass (Kingfisher/Nuke/Lottie/Stripe consumer scenarios). The shippable ergonomics of that pass already landed — additive `string`→`NSUrl` convenience overloads, the placeholder-default recovery constructor (which recovered Stripe `ApplePayConfiguration`'s suppressed public init), the hand-written CoreGraphics implicit conversions (now runtime-test-covered), the enum-case `Int` forwarder, and the auto-generated `{Module}.api-surface.md`. A follow-up review pass then landed two api-surface correctness fixes: recovered placeholder-default overloads now record an api-manifest entry (so a member that survives *only* as a truncated overload — `ApplePayConfiguration` — is listed in `api-surface.md` instead of silently absent), and a zero-member build now deletes a stale surface doc rather than leaving it behind. What remains here is design-gated, by-design, or an owner surface decision. Nothing below is queued.

<a id="a-generated-type-name-that-shadows-a-platform-type-in-a-commonly-imported-namespace"></a>

## A generated type name that shadows a platform type in a commonly-imported namespace

Faithful Swift-to-C# naming produced a type whose name shadows a platform type, so a consumer file importing both needs one alias line. Renaming silently would trade a compile-time inconvenience for an unpredictable name, and a warn-only build note is the only shape that clears the prediction-gate freeze policy (the failure it would prevent *compiles*). Recommendation: accept and document; absent an answer, that is what stands — no generator change. Evidence: docs-pass breakdown item A4. **Trigger:** the owner asks for the warn-only note, or a second library reports the same shadowing.

<a id="case-only-collision-naming-keep-the-numeric-scheme-or-derive-one"></a>

## Case-only collision naming: keep the numeric scheme or derive one

The case-only collision pass resolves two members whose distinct Swift names project onto the same C# identifier by appending a digit (`Url` / `Url2`). This is deliberate and is *not* the overload ladder — the members are not overloads, so there is no argument label or parameter type to derive a name from, which is why the "no numeric suffixes" statement carves this producer out. The fork is keep the digit and document the carve-out, or give this pass a derived scheme consistent with `NameCollisionPolicy`'s side-indicating vocabulary. Recommendation: keep the digit — inventing a token for a pair with no distinguishing content would be less predictable than the digit. Absent an answer, that stands. Evidence: docs-pass breakdown item A1. **Trigger:** a consumer reports the numeric name is unreadable, or a second producer wants to mint numeric names.

<a id="deprecated-identical-pair-collapse-was-never-implemented"></a>

## Deprecated / identical-pair collapse was never implemented

The overload-naming direction included a second ask that was not built: where two emissions share the same C# signature and one is upstream-deprecated (`HandleNextAction` / `HandleNextAction2`), collapse to a single member carrying the deprecation rather than disambiguating them. Only the naming half shipped. Today the survivor of an identical-signature pair is whichever comes **first in declaration order** — nothing prefers the non-deprecated member, and nothing prefers the instance member in the analogous static/instance shadow case, so the outcome is decided by ABI JSON ordering. Recommendation: implement the collapse with an explicit preference (non-deprecated wins, declaration order breaks ties). Absent an answer, declaration order decides. Evidence: docs-pass breakdown item A1. **Trigger:** a consumer gets the deprecated member of such a pair, or an upstream reordering silently swaps which one survives.

<a id="which-member-loses-when-the-cs0121-guard-fires"></a>

## Which member loses when the CS0121 guard fires

`OverloadAmbiguityGuard` prevents an ambiguous-call compile error by declining one of two candidates; *which* one is a policy the generator applies on the consumer's behalf. Declines are reported rather than hidden. Recommendation: leave the rule and keep reporting; absent an answer, it stands. Evidence: docs-pass breakdown item B10. **Trigger:** a consumer reports that the surviving overload is the wrong one.

<a id="eight-generated-cdecl-wrappers-force-cast-a-caller-supplied-metatype-to-a-protocol-existential-metatype-with-n"></a>

## Eight generated `@_cdecl` wrappers force-cast a caller-supplied metatype to a protocol existential metatype with no checked arm

**Revisit when:** a crash report or corpus failure traces to one of these casts, or the wrapper family gains an error out-parameter for another reason, at which point the checked arm rides along for free.

<details>
<summary>Recorded evidence and disposition</summary>

Added 2026-09-11 (reroute-wave audit). Every generic-parent dispatch wrapper that needs `ParentType<T>.self` recovers it by bitcasting a `_metadata{i}` / `parentMetaPtr` `@_cdecl` parameter to `Any.Type` and then `as! any {Protocol}.Type`. All eight sites were classified and **all eight are caller-supplied** — none derives the metatype from the receiver — so the cast is sound by *managed-side* construction (C# obtains the pointer from the type-metadata accessor for a `T` the generic constraint already requires to conform) rather than by anything the Swift side checks. There is no `guard let ... as?` arm, no conformance assertion, and no comment claiming soundness at any of them; a mismatched pointer traps in the Swift runtime. Converting to a checked shape is **not contained**: the sites live in 7 distinct emitter methods (`ConstructorWrapperEmitter` x2, `MethodWrapperEmitter`, `PropertyWrapperEmitter` getter and setter, `MethodClosureBridge`, `CollectionProjectionEmitter` count and subscript), and five of the eight have **no refusal channel at all** — the property getter/setter and closure-bridge trampolines carry no error out-parameter, and the two collection wrappers' `Int` return is already committed to the count / bounds sentinel. The other three carry `errorOut` only when the Swift member itself `throws`. So reporting a refusal means adding a new `@_cdecl` parameter, which is an ABI change that the P/Invoke signature builder, every generated `DllImport` and every call site must move with. Half-converting the three conditionally-throwing sites would leave the checked and unchecked shapes interleaved across one wrapper family. **Trigger to revisit:** a crash report or corpus failure traces to one of these casts, or the wrapper family gains an error out-parameter for another reason, at which point the checked arm rides along for free.

</details>

<a id="sixteen-more-emitter-sites-render-a-swift-type-spec-into-a-position-that-rejects-escaping"></a>

## Sixteen more emitter sites render a Swift type spec into a position that rejects `@escaping`

**Revisit when:** a wrapper-compile failure or `SWIFTBIND108` names a Swift block containing `@escaping` in a return clause or metatype position, or a session already reworking one of these emitters folds its own site in.

<details>
<summary>Recorded evidence and disposition</summary>

Added 2026-09-11 (reroute-wave audit), from a full sweep of the family after two sites were fixed. `ExistentialBypassEmitter.RenderModuleQualifiedSwiftTypeSpec` is written for **parameter** position and prepends `@escaping ` to a function type. `@escaping` is illegal Swift in two other positions the emitters also render into: a function's own **return clause**, and **metatype** position (`X.self`, `MemoryLayout<X>`). Sixteen sites still call the parameter-position renderer (or decide `useCdecl` from the parameter shape alone) where one of those two positions is what gets written: metatype shape at `ClosureEmitter.SwiftWrapper.cs:1316` and `:1352`, `OptionalPointerWrapperEmitter.cs:445` and `:482`, `Handler/ArraySliceNormalizationEmitter.cs:838`, and `WrapperValidation.cs:3385` via `MethodWrapperEmitter.cs:1231`'s `returnReferencesT` branch; return-clause shape at `ClosureEmitter.SwiftWrapper.cs:1143`, `OptionalPointerWrapperEmitter.cs:78`, `Handler/ArraySliceNormalizationEmitter.cs:589`, `Handler/ProtocolExtensionEmitter.cs:1701` and `:1958`, `Handler/MetatypeArrayBridgeEmitter.cs:254`, `ObjCOverridePropertyWrapperEmitter.cs:105`, plus `MethodWrapperEmitter.cs:1857` and `:1979` (variadic-cast and overload-disambiguation signatures) and `ProtocolExtensionEmitter.cs:1524`. All sixteen are **latent** at tip: the generated Swift under `BindingTests/output/` has zero `-> @escaping`, zero `as: @escaping` and zero `MemoryLayout<@escaping` occurrences — though only three wrapper `.swift` files exist, so that is narrow coverage rather than disproof. The failure mode when one is reached is the wave's silent-withdrawal shape: the wrapper block fails to compile, the post-processor strips it and the member drops off the wrapper route (or, post-fix, fails loudly as `SWIFTBIND108`). **Fix shape:** the two fixed sites used purpose-built renderers (`RenderSwiftTypeSpecForReturnType`, `RenderModuleQualifiedSwiftTypeSpecForReturnType`, `MethodWrapperEmitter.RenderIndirectResultMetatype`); converting sixteen more is a broad, position-by-position emitter sweep, each site needing its own fixture to prove it was reachable at all, which is why the audit fixed only the two with demonstrated reds. Per-site classification is preserved with the audit receipts (`receipts/escaping-position-sweep.md` in the wave's audit directory). **Trigger to revisit:** a wrapper-compile failure or `SWIFTBIND108` names a Swift block containing `@escaping` in a return clause or metatype position, or a session already reworking one of these emitters folds its own site in.

</details>

<a id="a-resilient-non-frozen-struct-returned-from-a-property-on-a-class-receiver-has-no-wrapper-route-only-methods-w"></a>

## A resilient (non-frozen) struct returned from a *property* on a class receiver has no wrapper route — only methods were bound

Added 2026-09-11 (reroute-wave audit), carrying forward an orphaned `Future-work:` line from the wave that named no receiving session. The 2026-09 reroute wave bound the class-receiver resilient-struct **return** for methods; the property accessor form was left out because the accessor needs its own trampoline rather than reusing the method wrapper's body — the getter has no parameter list to hang the indirect-result pointer off, and `self` reconstruction differs for an accessor on a class parent. Affected members keep their existing route (or stay skipped) — nothing regressed; this is an unclaimed capability, not a defect. **Fix shape:** a property-accessor sibling of the method trampoline, threading the same result-buffer convention the method arm already uses. **Trigger to revisit:** a real-world library in the validation corpus whose public surface is blocked on a resilient-struct property of a class, or a consumer report naming one.

<a id="a-swift-parameter-whose-real-external-label-is-literally-arg0-arg1-is-indistinguishable-from-the-parser-s-plac"></a>

## A Swift parameter whose real external label is literally `arg0`/`arg1`/… is indistinguishable from the parser's placeholder

**Revisit when:** a library surfaces a parameter whose external label matches `arg\d+`, i.e. a wrapper-compile failure of shape `expected argument label` or a missing conformance whose requirement's label is `argN`.

<details>
<summary>Recorded evidence and disposition</summary>

The parser renders a Swift wildcard label (`_`) as the synthesized C# identifier `arg{i}`, and eleven emitter sites recover "this is unlabeled" by pattern-matching that name via `SwiftBuilder.IsAutoGeneratedArgName` — including the wrapper's own Swift call-label emission (`MethodWrapperEmitter`, `CdeclParamMapper`, `ConstructorWrapperEmitter`, …) and the protocol-extension-defaults key builder. A Swift declaration that genuinely spells `func apply(arg0: Int)` would be rendered as `apply(_:)` everywhere, emitting a wrapper that calls `apply(_: x)` against a declaration expecting `apply(arg0: x)`. The subscript path already learned this lesson and carries a real parser-set fact instead (`ArgumentDecl.IsUnlabeledSubscriptIndex`, whose doc comment names the same `index0` collision). Unreached: no validation library or fixture declares such a label. **Fix shape:** extend the subscript precedent to ordinary parameters — set a parser-owned `IsUnlabeled` fact at the `arg{i}` injection point in `SwiftABIParser.ExtractParameterNames` and migrate all `IsAutoGeneratedArgName` call sites to read it, in ONE pass so the key builders and the wrapper call-label emitters cannot disagree. **Trigger to revisit:** a library surfaces a parameter whose external label matches `arg\d+`, i.e. a wrapper-compile failure of shape `expected argument label` or a missing conformance whose requirement's label is `argN`.

</details>

<a id="a-property-declared-both-statically-and-per-instance-loses-the-instance-member-silently-and-with-it-every-conf"></a>

## A property declared both statically and per-instance loses the instance member silently, and with it every conformance that needed it

**Revisit when:** a consumer reports a missing instance property that plainly exists in Swift, or a conformance refused for the static-shadow reason on a type whose static sibling is incidental.

<details>
<summary>Recorded evidence and disposition</summary>

Swift lets one type carry `static let keySize` and `let keySize`; C# does not, so `ClassHandler`, `FrozenStructHandler` and `NonFrozenStructHandler` each keep whichever the ABI JSON lists first and drop the other — declaration order decides, nothing prefers the instance member. Two consequences. (a) The drop is invisible: `ClassHandler` does call `ReportCollector.RecordMemberSkipped(… DuplicateSignature …)`, but `MemberDiagnosticIdentity` deliberately excludes the static/instance `Discriminator` from equality, so the loser's identity equals the already-emitted winner's and `RecordMemberSkippedInternal` returns early — no row reaches `binding-report.json`. (b) When the survivor is the static one, `ProtocolConformanceValidator` now refuses the whole conformance, because a static member cannot implement an instance interface requirement and no default interface member changes that. That refusal is correct but coarse: a real-world cipher type lost `: ICipher` outright over a `keySize` it does declare per-instance. **Fix shape:** make the collision a resolution rather than a drop — let the protocol-witnessing member keep the C# name and rename the loser (the collision-suffix machinery methods already use), which needs the three handlers to share one collision oracle instead of three loop-local `HashSet`s, plus a `Discriminator`-aware report identity so the outcome is at least visible. **Trigger to revisit:** a consumer reports a missing instance property that plainly exists in Swift, or a conformance refused for the static-shadow reason on a type whose static sibling is incidental.

</details>

<a id="a-scalar-nsstring-backed-apple-typed-enum-associated-value-exposes-the-foundation-nsstring-carrier-not-the-c-e"></a>

## A scalar NSString-backed Apple typed-enum associated value exposes the `Foundation.NSString` carrier, not the C# enum

Consequence of the issue-46 wave's `AppleTypedEnum` records (before them the type had no record at all, so nothing emitted). Bound-generic payloads (e.g. `[VNBarcodeSymbology]`) project correctly through `EnumHandler.CaseConstruction.cs:1088`; only a *scalar* associated value falls through to the `NativeTypeName` arm (`EnumHandler.CaseConstruction.cs:1112`, similarly `SwiftUIBridgeEmitter.InitAnalyzer.cs:608`) and surfaces as `NSString`. It compiles and marshals correctly — the value genuinely is an `NSString`, and construction and `TryGet` agree — so it is un-idiomatic, not unsound. Fixing it means changing public-type resolution for every `NativeTypeName`-bearing type (`NSUrl`, `NSUrlRequest`, …), which legitimately want the native type — so it needs a discriminator (project the enum only for `AppleTypedEnum`-flagged records), decided deliberately. **Trigger:** a consumer reports the NSString-typed associated value as friction, or the next typed-enum follow-up session.

<a id="a-swift-backed-conformer-passed-into-a-scalar-any-classboundprotocol-parameter-sigsegvs-the-boxable-path-write"></a>

## A Swift-backed conformer passed into a scalar `any ClassBoundProtocol` parameter SIGSEGVs — the boxable path writes the opaque layout into a class-existential slot

**Revisit when:** any fixture or consumer passes a Swift-backed conformer (not a C# proxy) into a class-bound existential parameter — or the next session that takes on existential-parameter marshalling.

<details>
<summary>Recorded evidence and disposition</summary>

For `protocol P: AnyObject`, `any P` is the two-word class existential `{classRef, witnessTable}`; the emitter marshals every scalar existential parameter as the five-word `ExistentialContainer1` and the `@_cdecl` wrapper does `ptr.load(as: (any P).self)`, so Swift reads the container's first two words. The **proxy** path survives that by accident of layout — it puts the witness in `Payload1`, i.e. word 1, which is exactly where a class existential wants it — which is why the existing `readSinkCountA` / `readSinkCountB` reverse-dispatch tests pass. The **boxable** path (`IExistentialBoxable.BoxAsExistential1` → `ExistentialContainerFactory.Create`, taken for a concrete Swift class conformer) instead writes the class ref to `Payload0`, leaves `Payload1` zero and puts the witness in the dedicated witness word, so Swift reads a null witness table and crashes inside the callee. Reproduced with a `protocol KeyedCipher: AnyObject` fixture whose Swift-backed conformer was passed to `func cipherKeySize(_ cipher: any KeyedCipher)`: deterministic SIGSEGV in `$s…13cipherKeySizeys5Int32VAA11KeyedCipher_pF`, one frame below the `@_cdecl` wrapper. Nothing in the corpus reaches it today — every existing class-bound existential parameter is fed a proxy. **Fix shape:** at the existential-parameter emission site, when the protocol is class-bound, hand the callee a `ClassExistentialContainer1` — `ExistentialContainerFactory.FromExistentialContainer1` already normalises both source layouts by picking the non-zero witness word — and keep the borrowed (non-consuming) ownership the parameter position implies, unlike the array-element carrier which must mint an owned +1. Needs a device (NativeAOT) leg as well as the simulator, being a layout change on a P/Invoke boundary. **Trigger to revisit:** any fixture or consumer passes a Swift-backed conformer (not a C# proxy) into a class-bound existential parameter — or the next session that takes on existential-parameter marshalling.

</details>

<a id="generated-local-names-insulated-temporaries-and-lifetime-test-residuals"></a>

## Generated-local names: insulated temporaries and lifetime-test residuals

**Revisit when:** a real collision surfaces — a compile error in generated C# where a projected parameter shadows one of those local names.

<details>
<summary>Recorded evidence and disposition</summary>

**RESOLVED.** Every generated local now goes through a per-emitted-body name scope (`SyntheticNameScope` / `SyntheticLocalNames` in `NameProvider.cs`) seeded with the identifiers the member's own emitted parameters occupy, across the wrapper, enum-case, operator, protocol-proxy, closure-bridge, existential and keypath lanes, and the cross-module / foreign-type extension emitters (whose return-marshalling bodies had been written as raw-string literals — `result`, `metadata`, `buffer`, `indirectResult` — that a `WriteLine`-shaped sweep could not see); a collision moves the generated local aside and the public parameter keeps its Swift name. Fixture family under `BindingTests/.../Collisions/`. The extension fixtures also exposed a use-after-free on the foreign-type path — a Swift class was handed back to the managed peer at +0 while the peer adopts +1 — fixed in the same pass (`ForeignTypeExtensionEmitter.FormatOpaqueClassReturn`, arm-specific: Swift class +1, ObjC-imported class stays +0); the runtime test for it went deterministically red pre-fix on the simulator but reads an inline integer, so it observes the fix rather than guaranteeing it — a `deinit`-counted fixture would make it a real detector. Two corrections to the record below: the pre-fix failure was **not** always fail-closed — three lanes emitted a wrong binding that only a type error happened to catch, and the operator lane silently withdrew the member; and the `_`-prefixed insulated temporaries (`_cdeclBuf`, `_tcs`, `_asyncCallHolder`, `_cancelRegistration`, `tupleResult{i}Ptr`, …) are deliberately outside the registry — reopen if a real library spells a public parameter in that vocabulary. Original entry kept for the trigger history: Codex round-2 follow-up: many emitted locals are still hardcoded names (`tag`, `optionalMetadata`, `resultPtr`, `resultBuffer`, `swiftResult`, `_swiftResult`, `existentialResult`, `returnMetadata`, `swiftIndirectResult`). A Swift method whose parameter is projected to any of those names would shadow the generated local. No real-world repro — punted from the `result` collision fix. The same gap covers the *parameter-derived* locals (2026-09 wave, Codex r4): every projection derives its scratch names from the parameter name with a fixed suffix — `{p}Buffer`, `{p}Swift`, `{p}Handle`, and the ObjC-bridged container owners `{p}NSArray` / `{p}NSDict` / `{p}Pairs` / `{p}NSSet` (`ArrayProjection.cs`, `DictionaryProjection.cs`, `SetProjection.cs`, now minted through `ObjCContainerBridgeOwner.Declare`) — and `NameProvider`'s parameter dedup only compares parameters against each other, so a Swift signature like `func f(items: [URL]?, itemsNSArray: Int32)` redeclares a sibling parameter and fails the generated C# compile (CS0128, fail-closed in verify-recover; never a wrong binding). Pre-existing on every one of those suffixes; the owner refactor kept the names it found. Real fix: a `LocalNameRegistry` consulted by `MethodMarshalPlanBuilder`, `FailableFactory`, and every projection emitter so all generated locals are guaranteed unique against projected parameter names. **Trigger:** a real collision surfaces — a compile error in generated C# where a projected parameter shadows one of those local names.

</details>

<a id="resilient-extension-returns-struct-receivers-and-class-properties"></a>

## Resilient extension returns: struct receivers and class properties

**Revisit when:** a consumer reports a missing extension member whose return is a resilient struct on a struct receiver or on a class-receiver *property*, or a skip-surface review finds a declared P/Invoke with no caller. The original analysis follows. `CrossModuleExtensionEmitter` (the class-receiver arm, both method and property sites — re-grep for the `NonFrozenStruct` decline) refuses a resilient (non-frozen) struct return, but the refusal leaves a `PInvoke_…` declaration in the emitted `_PInvoke` class with no public member calling it and writes **no** row to `binding-report.json`. The member simply vanishes from the surface; the skip-surface and API-manifest gates see nothing, because there is no marker and nothing was ever declared. Observed with a probe fixture `configTagged(metadata:buffer:indirectResult:) -> DependencyConfig` during the registry pass (removed, not shipped). ~~The sibling struct-receiver and foreign-type arms bind the shape, so a consumer would find the same member present on one receiver kind and silently absent on another.~~ (Half-wrong, corrected above: the struct-receiver arm declines it too, and the foreign arm never carried a `SwiftSelf`.) **Fix shape:** either route the decline through `ReportCollector.RecordMemberSkipped` with a reason and suppress the orphan declaration, or lift the decline by giving the class-receiver arm the indirect-result lane its siblings have (the `indirectResult` argument is already minted there, so the arm is correct if the decline is lifted).

<details>
<summary>Recorded evidence and disposition</summary>

**Status — fixed (2026-09).** The class-receiver arm no longer declines: it binds the shape through the `@_cdecl` trampoline with a leading result pointer that the wrapper initialises, and the orphan declaration is gone from both emitters that produced it. The direct route's decline is retained deliberately and now carries its evidence — the direct arm is empirically memory-unsafe for an indirect result plus a class self: a probe watched the receiver's isa word move from a heap address to a stack address with the refcount unchanged, and the next dispatch through it segfaulted. Red-before-green was the member absent from the surface with an uncalled P/Invoke left behind; both green after. **Residue, deliberate:** the row's original premise that the sibling arms already bind this shape is half-wrong — the struct-receiver arm still declines a non-frozen struct return at two sites, and the foreign-type arm reaches the member with a plain `IntPtr` self rather than a `SwiftSelf`, so it was never on this defect's route. The class-receiver **property** arm did not get the lane either: no property trampoline exists, and the emitter it would need is not handed the Swift writer or the emission context. Reverting that half cost zero corpus members. **Trigger (kept):** a consumer reports a missing extension member whose return is a resilient struct on a struct receiver or on a class-receiver *property*, or a skip-surface review finds a declared P/Invoke with no caller. The original analysis follows. `CrossModuleExtensionEmitter` (the class-receiver arm, both method and property sites — re-grep for the `NonFrozenStruct` decline) refuses a resilient (non-frozen) struct return, but the refusal leaves a `PInvoke_…` declaration in the emitted `_PInvoke` class with no public member calling it and writes **no** row to `binding-report.json`. The member simply vanishes from the surface; the skip-surface and API-manifest gates see nothing, because there is no marker and nothing was ever declared. Observed with a probe fixture `configTagged(metadata:buffer:indirectResult:) -> DependencyConfig` during the registry pass (removed, not shipped). ~~The sibling struct-receiver and foreign-type arms bind the shape, so a consumer would find the same member present on one receiver kind and silently absent on another.~~ (Half-wrong, corrected above: the struct-receiver arm declines it too, and the foreign arm never carried a `SwiftSelf`.) **Fix shape:** either route the decline through `ReportCollector.RecordMemberSkipped` with a reason and suppress the orphan declaration, or lift the decline by giving the class-receiver arm the indirect-result lane its siblings have (the `indirectResult` argument is already minted there, so the arm is correct if the decline is lifted).

</details>

<a id="low-yield-behavior-neutral-parity-robustness-latents"></a>

## Low-yield / behavior-neutral parity + robustness latents

**Revisit when:** each sub-item is a rider, not a task — take one only when a session is already editing that file, or when the data condition each names actually appears (a `kind="class"` `ObjCBridgeable` XML entry, a value-generic declaration, a property-renamed CSM method with trailing defaults).

<details>
<summary>Recorded evidence and disposition</summary>

Log-only, very narrow. **§2.1** `ClosureProjection` escaping-param branch (`:167-242`) is unguarded dead code — `CallbackDeclarations` has 0 production readers; inert until an emitter wires it live. **F-typed-closure** `BoundType`+ObjC-bridgeable **class** arg routes through `MarshalFromSwift` not `GetNSObject` (`SwiftUIBridgeEmitter.cs:3686`) — data-unreachable (`ObjCBridgeable` flag has 0 `kind="class"` XML entries); add a guard test only if curated XML grows one. **F11** demangler handles only `Ya/Yb/YK` (`Swift5Demangler.cs:552`); `Yt` (`_const`) hits 4 current symbols → demangle fails, `IsAsync`/variadic falls to heuristic (benign); treat unknown `Y?` as ignorable annotation. **F14** CSM trim-variant pool keys with null `siblingPropertyNames` (`ConcreteProtocolSpecializationEmitter.cs:498` / `.Async.cs:446`) → cross-pool collision on a property-renamed method with trailing defaults. **F15** `IsOptionalObjCBridged` (`MarshallingHelpers.cs:172`) is missing 3 of `TypeProjectionFactory`'s 9 guards (`!ContainsGenericParameters`/`!IsStdlibContainer`/`!IsPointerType`) — behavior-neutral today, worth a parity test. **F17** value generics `<let N : Int>` have no gate (parameter packs have `each` at `GenericTypeEmitter.cs:483`) — would hit the malformed-identifier cascade; add a gate opportunistically when touching `GenericTypeEmitter`. **`DefaultIndicies` typo** (`Swift5Demangler.cs:740`, demangled name discarded — 1-char opportunistic fix). **`FrozenWithMemory` closure-arg** SwiftUI-bridge emission is code-correct but has no BindingTests runtime coverage — add a fixture when convenient. **Trigger:** each sub-item is a rider, not a task — take one only when a session is already editing that file, or when the data condition each names actually appears (a `kind="class"` `ObjCBridgeable` XML entry, a value-generic declaration, a property-renamed CSM method with trailing defaults).

</details>

<a id="cdeclparammapper-sibling-escape-removes-by-transformed-name-not-parameter-identity"></a>

## `CdeclParamMapper` sibling-escape removes by transformed name, not parameter identity

**Revisit when:** a corpus/validation lib emits a duplicate-`@_cdecl`-binding wrapper of this shape, or a session already reworking `CdeclParamMapper` name-escaping can fold it in.

<details>
<summary>Recorded evidence and disposition</summary>

Codex flagged this in the doc-06 wrapper-emission review (r1 + r2) as a High; out of the 8-bug scope. `CdeclParamMapper.CollectSiblingBindingNames` + the `ExcludeSelf` self-removal in the escape helpers key the sibling set by the *transformed* binding name, not the parameter's identity. When two params keyword-rename or sanitize to the SAME name — e.g. `func f(extension: Int, extensionParam: Int)` both become `extensionParam` — the sole HashSet entry is removed by each param's own self-exclusion, so neither escapes against the other and the emitted `@_cdecl` wrapper declares two `_ extensionParam:` bindings → Swift wrapper compile error (the compile gate strips the block; the member silently degrades). **Exotic:** needs a Swift function whose keyword-named param collides post-transform with a sibling; zero corpus evidence. **Fix shape:** make the sibling set identity-keyed (parameter index / `ArgumentDecl` reference) across the ~15 escape call-sites so `ExcludeSelf` removes the current param's OWN entry rather than a name a sibling also produced — an order-aware rework, not a one-liner. **Trigger to revisit:** a corpus/validation lib emits a duplicate-`@_cdecl`-binding wrapper of this shape, or a session already reworking `CdeclParamMapper` name-escaping can fold it in.

</details>

<a id="optionalpointerwrapperemitter-has-no-inout-awareness-protected-only-by-methodwrapper-claiming-abi-safe-inout-f"></a>

## `OptionalPointerWrapperEmitter` has no inout awareness — protected only by MethodWrapper claiming ABI-safe inout first

**Revisit when:** a corpus/validation lib produces an OptionalPointer wrapper that drops an inout, or a session touching the wrapper-routing decision can add the guard + a fixture cheaply.

<details>
<summary>Recorded evidence and disposition</summary>

Both reviewers raised this in the doc-06 wrapper-emission review as a false-positive "High" on `stepAndLabel` (which actually works — `MethodWrapperEmitter` claims it and forwards the inout via `CdeclParamMapper.MapInout`), but the underlying observation is real. `OptionalPointerWrapperEmitter`'s param loop special-cases only the large-Optional arg it exists for; every other param (including an `inout`) is declared by its plain type with no `IsInOut` branch, so an `inout` that reaches it is silently dropped (no `&`, no deferred write-back). Today this is unreachable for the two demonstrated shapes: an ABI-safe `inout` + large-Optional is claimed by `MethodWrapperEmitter` BEFORE MethodHandler's OptionalPointer block (gated on `!UsesWrapperLibrary`), and an ABI-mismatch `inout` is excluded by the routing's `!HasInoutWithAbiMismatch` guard (and now skipped by MemberValidationPipeline Gate 5c). **Undemonstrated corner:** a shape MethodWrapper declines for a NON-inout reason (e.g. an unsupported closure param) while still carrying an ABI-safe `inout` + a large-Optional could reach OptionalPointer and drop the inout; no fixture or corpus lib exercises it. **Fix shape (fail-closed, cheap):** have `OptionalPointerWrapperEmitter.CanConvertToCdecl` (or MethodHandler's routing) refuse when any non-large arg `IsInOut`, so the method falls to the raw CallConvSwift path (which forwards ABI-safe inout via `ref`) instead of silently dropping it. **Trigger to revisit:** a corpus/validation lib produces an OptionalPointer wrapper that drops an inout, or a session touching the wrapper-routing decision can add the guard + a fixture cheaply.

</details>

<a id="enum-named-tag-discriminator-collision-multi-property-and-static-residuals"></a>

## Enum-named-`Tag` discriminator collision — multi-property and static residuals

**Revisit when:** a real library declares an enum literally named `Tag` carrying either two discriminator-colliding properties or a static discriminator-colliding property, or a session already reworking the enum-discriminator naming path.

<details>
<summary>Recorded evidence and disposition</summary>

A Swift enum that lowers to a C# class emits a synthetic `public CaseTag Tag` discriminator; when the enum is itself named `Tag` the discriminator dodges to `TagValue`/`Tag2` (`EnumHandler.GetTagPropertyName`, which reserves the type name, `CaseTag`, case names, `TryGet` helpers, and nested-type leaves), and a user *instance* property whose C# name collides with the discriminator is recovered away via a `{name}Value` suffix (the `EnumHandler.cs` property-recovery loop). Two residuals persist, both requiring the pathological `enum Tag { … }` shape. (1) **Multi-property co-collision:** the recovery channel `enumPropertyRenames` is keyed by the *projected* colliding name, so two distinct instance properties projecting to the same name (e.g. `var tag` and `var tagValue`, both → `TagValue` on an enum named `Tag`) share one key — the second overwrites the first and both emit the same recovered name → `CS0102` between the two properties. This is net-neutral versus the pre-recovery behavior (both properties already collided with the discriminator → `CS0102`); the recovery fixes the common single-property case and leaves this compound case no worse, and the same projected-name-keyed structure pre-exists for case-constructor recovery. (2) **Static-property gap:** the recovery loop skips `IsStatic` properties, so a `static var tag` colliding with the discriminator on an enum named `Tag` is neither recovered nor fail-closed → `CS0102`. A faithful fix is a rename channel keyed by property *identity* (not projected name) plus symmetric static handling — a naming-SSOT change disproportionate to these near-unreachable shapes. **Trigger:** a real library declares an enum literally named `Tag` carrying either two discriminator-colliding properties or a static discriminator-colliding property, or a session already reworking the enum-discriminator naming path.

</details>

<a id="native-int-method-convenience-forwarder-does-not-mirror-sb0006-poison-subscript-sibling-does"></a>

## Native-int method convenience forwarder does not mirror SB0006 poison (subscript sibling does)

**Revisit when:** a method-body SB0006 poison recorder is introduced (i.e. some emitter starts poisoning a method body for a suppressed-proxy read), **or** a corpus/validation lib produces a SWIFTBIND113 CS0619 traced to a native-int method convenience forwarder.

<details>
<summary>Recorded evidence and disposition</summary>

`NativeIntOverloadEmitter.TryEmitOverload` emits an `int`/`uint` convenience method as an expression-bodied forwarder to the primary native-int method (`public T Foo(int i) => Foo((nint)i);`). Its subscript sibling `TryEmitIndexerOverload` was hardened this program to *mirror* SB0006 poison — when the primary indexer getter was suppressed-proxy-poisoned (`emissionContext.WasSubscriptGetterProduceThrow(subscriptDecl)`), the convenience overload emits its own throwing, `[Obsolete(error:true)]` getter instead of forwarding into the poisoned one (which would be CS0619). The method path has **no** equivalent mirror — and cannot today, because there is no method-*body* SB0006 poison recorder in `ModuleEmissionContext`: the poison recorders are `_produceThrowAccessors` / `_produceThrowGetters` / `_produceThrowSubscriptGetters` only, never a method body. So a native-int method whose primary body were SB0006-poisoned (a suppressed-proxy read that can only throw) would be forwarded-into → CS0619. **Unreachable today:** no code path poisons a method body, so the forwarder never targets one. **Fails CLOSED if it ever arises:** the in-generator C# verification build (SWIFTBIND113) catches the CS0619 and fail-closes the module — a hard generate-time stop, never a silent bad binding. **Fix shape (when triggered):** add a method-body poison recorder mirroring the subscript one (`RecordMethodBodyProduceThrow`/`WasMethodBodyProduceThrow`) and have `TryEmitOverload` mirror the poison the way `TryEmitIndexerOverload` now does, rather than forwarding. **Trigger to revisit:** a method-body SB0006 poison recorder is introduced (i.e. some emitter starts poisoning a method body for a suppressed-proxy read), **or** a corpus/validation lib produces a SWIFTBIND113 CS0619 traced to a native-int method convenience forwarder.

</details>

<a id="apple-supplement-recording-in-tuplehandler-is-keyed-on-the-swift-foundation-namespace"></a>

## Apple-supplement recording in `TupleHandler` is keyed on the `Swift.Foundation.*` namespace

`TupleHandler.RecordAppleSupplementReference` (`TupleHandler.cs:643-647`) decides "this managed type is homed in the SwiftBindings.Apple supplement" by testing whether the resolved C# type's fully-qualified name starts with `Swift.Foundation.` — correct today (Swift.Runtime declares no `Swift.Foundation` namespace, and every supplement-homed managed type lives there), but it is a namespace *convention*, not a package-membership fact. The first supplement-homed type projected outside `Swift.Foundation.*` would silently skip recording on the tuple arm, and the binding would miss its supplement reference. Fails closed, not silent: the generated C# hits CS0234 on the unreferenced supplement namespace and the in-generator verification surfaces it as SWIFTBIND114 — the module fail-closes rather than shipping. **Trigger:** the first supplement-homed managed type outside the `Swift.Foundation.*` namespace (make supplement membership a queryable TypeDatabase/registry fact then, not a name prefix).

<a id="hardcoded-name-gated-applesupplementreferences-record-arms-lack-per-arm-collector-asserts"></a>

## Hardcoded name-gated `AppleSupplementReferences.Record` arms lack per-arm collector asserts

Supplement-reference recording has two mechanisms: projection-keyed arms record structurally via `TypeProjectionFactory`, while a set of hardcoded emission arms that bypass the factory (`ClosureEmitter` sync + async, `ConcreteProtocolSpecializationEmitter`, the `EnumHandler` case-construction/case-inspection/marshalling arms, `MethodClosureBridge`, `WrapperEmitter.Return`, `TypeConversionHandler`, `TupleHandler`) each carry their own name-gated `AppleSupplementReferences.Record(...)` call at the point they emit a supplement type name. No unit test asserts per-arm that the collector actually received the record when the arm fires, so a future edit could drop one call without a test going red. Not hardened now because a missed record fails closed: the generated binding's compile hits CS0234 on the missing supplement namespace and the in-generator verification reports SWIFTBIND114 — a loud generate-time stop, never a silent bad binding. The per-arm asserts are cheap insurance, not a correctness gap. **Trigger:** adding a new supplement-referencing emission arm — add the per-arm collector assert then, alongside the new arm's own test.

<a id="synthesized-paired-operator-residuals-heterogeneous-relational-body-multi-overload-keying-generic-remap"></a>

## Synthesized paired-operator residuals — heterogeneous relational body, multi-overload keying, generic remap

**Revisit when:** a corpus/BindingTests lib emits a heterogeneous relational operator, a multi-overload same-symbol operator set, or a generic operator whose synthesized partner mis-projects — reproduce that operator as the fixture before hardening the synthesizer beyond the equality path.

<details>
<summary>Recorded evidence and disposition</summary>

`OperatorHandler.ValidateAndEmitPairs` synthesizes a missing partner operator from its defined mate. The equality partners (`!=` from `==`, `==` from `!=`) are body-safe for any operand pair (they negate `left`/`right` in place), and heterogeneous **equality** is now correct — `ResolveSynthesizedOperandTypes` derives the partner's operand types from the source operator's projected wrapper signature so the synthesized `!=` matches the source `==` (the leg-4-adjacent defect this fixed; unit-covered). Three residuals remain, all compile-catchable → owned by verify-recover, none warranting a new prediction gate (freeze policy): (1) **heterogeneous relational** — the `<`/`>`/`<=`/`>=` synthesis emits a *swap* body (`return right < left;`), which for a heterogeneous `<(A, B)` needs a nonexistent `<(B, A)` → `CS0019`; the source relational operator is withdrawn by verify-recover (same end-state as declining to synthesize; homogeneous relational, the common case, is correct + tested). (2) **multi-overload keying** — `definedSymbols` is a `HashSet<string>` keyed by operator *symbol*, so a type with two `<` overloads of different operand types drives one presence check; a mismatched/duplicate synthesis fails the compile and verify-recover withdraws it. (3) **generic remap parity** — `ResolveSynthesizedOperandTypes` applies only the bare-parent→`typeNameWithGenerics` fix, not `EmitOperatorWrapper`'s full `ApplyGenericRemap`, so a generic-operator operand naming a remapped method-generic could diverge; compile-catchable. **Trigger:** a corpus/BindingTests lib emits a heterogeneous relational operator, a multi-overload same-symbol operator set, or a generic operator whose synthesized partner mis-projects — reproduce that operator as the fixture before hardening the synthesizer beyond the equality path.

</details>

<a id="internal-rawrepresentable-enum-surface-is-tombstoned-wholesale-the-tag-injection-path-could-recover-the-case-a"></a>

## Internal RawRepresentable enum surface is tombstoned wholesale; the tag-injection path could recover the case accessors

**Revisit when:** a consumer reports missing internal / `@usableFromInline` enum surface — reproduce that enum as a fixture, prove the metadata accessor resolves for it, then route its simple cases through the tag-injection path and keep `FromRawValue` tombstoned.

<details>
<summary>Recorded evidence and disposition</summary>

When an enum is module-internal (or nested in an internal type) its Swift wrapper plane is discarded, so the `SBW_` `@_cdecl` symbols every route out of `EmitRawRepresentableSupport` calls are never defined. The emitter therefore tombstones the entire RawRepresentable surface — `FromRawValue` plus one static accessor per simple case — as `SkipReason.ModuleInternal` with a greppable `// Unsupported:` marker, instead of emitting P/Invokes against symbols that don't exist. Honest and fail-closed, but broader than it strictly has to be: the sibling case-emission path `EnumHandler.EmitSimpleCaseFromTag` constructs a case with **no wrapper symbol at all** — it allocates a buffer and writes the discriminator through the value-witness table (`DestructiveInjectEnumTag`) — so the per-case static accessors are candidates to be **recovered** rather than tombstoned, leaving only `FromRawValue` dropped (that one genuinely needs raw values the ABI JSON does not carry). Not done, because the recovery is not free: the tag path still resolves `SwiftObjectHelper<T>.GetTypeMetadata()`, and an internal decl is exactly the case where the metadata accessor may not be exported — un-tombstoning on the assumption that it is would trade an honest skip for a runtime failure, the one outcome the tombstone exists to prevent. **Trigger:** a consumer reports missing internal / `@usableFromInline` enum surface — reproduce that enum as a fixture, prove the metadata accessor resolves for it, then route its simple cases through the tag-injection path and keep `FromRawValue` tombstoned.

</details>

<a id="cdeclparammapper-optionset-arm-assumes-a-non-failable-init-rawvalue-unlike-its-enum-sibling"></a>

## `CdeclParamMapper` OptionSet arm assumes a non-failable `init(rawValue:)`, unlike its enum sibling

0.18.0 whole-range review (Opus L3). The OptionSet branch (`CdeclParamMapper.cs:~542-548`) emits `let {label}Val = {swiftType}(rawValue: {label})` — non-optional, never consults the `ExternalAppleEnum` record — while the enum branch just below uses the failability-agnostic `({swiftType}(rawValue: {label}) as {swiftType}?)`. Unreachable at tip: every registered OptionSet has a non-failable init. The trap is the next `"kind": "enum"` registry entry that Microsoft.iOS projects as `[Flags]` with a failable initializer — the emitted `@_cdecl` wrapper then fails to compile (Swift optional-conversion error), surfacing as a wrapper build break rather than at registry-edit time. Compile-catchable → verify-recover territory, no new prediction gate (freeze policy). **Trigger:** a registry `valueTypes` enum entry whose platform projection is `[Flags]` with a failable `init(rawValue:)` — align the OptionSet arm with the enum arm's optional-cast shape then, with that entry as the fixture.

<a id="emitting-module-derivation-order-diverges-from-resolveemittingmodule-in-three-sites"></a>

## Emitting-module derivation order diverges from `ResolveEmittingModule` in three sites

0.18.0 whole-range review (Opus L4). `AsyncSequenceEmitter.cs:38`/`:66` and `TypeHandlerHelpers.cs:1270` derive the emitting module as `typeDecl.SwiftTypeName?.Module ?? typeDecl.ModuleDecl?.Name` (SwiftTypeName first), while the qualification pass's `ClosureParamTombstoneEmitter.ResolveEmittingModule` deliberately prefers `ModuleDecl.Name` first. The orders disagree only for the cross-module-extension case documented at `Parser/ModuleProcessor.cs:370-380`; an `AsyncSequence`-conforming type reached through a cross-module extension would qualify its existential against the wrong module. No fixture or corpus lib reaches it at tip. **Trigger:** a cross-module extension on an `AsyncSequence`-conforming (or `TypeHandlerHelpers`-routed) type appears in a fixture/corpus red — build the fixture first (TDD), then unify all derivation sites on the `ResolveEmittingModule` order in one pass.

<a id="protocol-overload-keys-are-module-unqualified-while-emission-is-module-qualified"></a>

## Protocol overload keys are module-unqualified while emission is module-qualified

0.18.0 whole-range review (Opus L5). `ProtocolSignatureHelper` overload-key path (`:245`, `:262-266`; mirrored `:445`, `:471-473`) builds its `ProjectionContext` without `CurrentModuleName`, while the existential/bound-generic emission fallbacks in the same file thread `currentModuleName`. Two protocol requirements whose parameter existentials differ only by owning module can project to the same unqualified key, so the helper sees a collision emission would not produce and takes the conservative de-overload path. Direction is safe — over-conservative de-overloading, never a duplicate-signature compile error — and unreachable with current fixtures. **Trigger:** a protocol requirement pair differing only by existential owning module shows up de-overloaded in a fixture/corpus lib — thread `CurrentModuleName` into the key path then, keyed off that repro (note the key/emission agreement constraint: key and emitted signature must move together).

<a id="nested-types-under-non-prepass-suppressions-are-unaccounted-as-types-in-the-skip-report"></a>

## Nested *types* under non-prepass suppressions are unaccounted *as types* in the skip report

The skip-accounting hardening (B14, 2026-07-28) closed the member-level gap: members of types suppressed outside the prepass (SPI, underscore-prefixed, SwiftUI-only, Apple-supplement-homed — all recorded at `HandleBaseDecl`) are now counted in the skip report. The residual seam is one level up: a *nested type* declared inside such a suppressed parent is neither emitted nor recorded as a skipped **type** — its row simply doesn't exist in the report, so a consumer diffing "types in the Swift module" against "types in the report" sees an unexplained absence rather than an honest skip line. Members are accounted; the nested type's own identity row is not. Fail-silent only at the reporting layer — no wrong code is emitted, the surface is correctly absent. **Fix shape:** when `HandleBaseDecl` records a non-prepass suppression, walk the decl's nested types and record each as a skipped type with the parent's reason, mirroring what the prepass already does for its own suppressions. **Trigger:** the next skip-report fidelity audit, or a consumer question the report can't answer about a missing nested type.

<a id="occupancy-policy-diverges-between-the-class-and-protocol-naming-lanes"></a>

## Occupancy policy diverges between the class and protocol naming lanes

The class lane applies `RungFits` family-wide; the protocol lane accepts per-member. They diverge on `configure(mode:)`/`configure(other:)` beside a natural `configureMode`. Both lanes are dual-pinned by fixtures today, so the divergence is observable and stable rather than silent drift — but converging them moves published names. **Trigger:** a real library hits the divergent shape and the two lanes disagree on a name a consumer sees, or [family-fold design decision](protocols.md#family-fold-vs-the-class-lane-a-design-fork) is settled — they are the same design question.

<a id="wrappedmembercount-is-a-floor-not-a-count"></a>

## `WrappedMemberCount` is a floor, not a count

`RecordMemberWrapped` is never called on the mainline `@_cdecl` path, so the recorded number under-reports. Closing it changes `WrappedItems` semantics repo-wide, which is why the wrapper-recovery pass left it — the number was phrased from a complete entry-point count rather than corrected. Anything reading the value as exact will be wrong low; reading it as a trend is fine. **Trigger:** a gate or report starts making a decision on the exact value rather than on its direction.

<a id="cross-producer-source-order-dependence-in-overload-naming"></a>

## Cross-producer source-order dependence in overload naming

When two producers contribute candidates, the order they arrive in can change which name each ends up with. Accepted as a documented design residual when the overload lattice landed — the inversion is *reported*, not silent, and making emission transactional was out of scope. The same pass fixed a cross-key reservation leak, an unshaped failable-factory key, cap arithmetic spending a slot on a doomed candidate, an over-strict unlabeled rule and an unseeded manifest ladder; this is the one item left standing. **Related residual, never closed:** `OverloadAmbiguityGuard` primaries are not re-checked after later candidates are assigned, so a name that was unambiguous when it was granted can become ambiguous once a sibling lands — same source-order root, different site. **Trigger:** a reported inversion turns out to be consumer-visible on a shipped library, or transactional emission is funded for another reason.

<a id="ambiguity-guard-drops-typed-constructors-against-an-unrelated-cgrect-overload"></a>

## Ambiguity guard drops typed constructors against an unrelated `CGRect` overload

`OverloadAmbiguityGuard` suppressed `LottieAnimationView` constructors taking `LottieAnimation` / `string` / `NSUrl` / `DotLottieFile` as ambiguous with `ctor(CGRect)` — types C# would never consider ambiguous. Result: `new LottieAnimationView(animation)` is missing while the sibling `LottieAnimationLayer(animation)` survives. Same pass also suppressed several `Play` overloads against `Play(Action<bool>)`. Suspected: comparing arity or a normalized all-defaults shape rather than the actual parameter type list. Policy should be: decline the 0-arity form, keep the typed form; do not loosen `AreAmbiguous`. **Trigger:** a Lottie (or other) consumer needs a typed constructor the guard dropped, or a fixture `init(Animation)` vs `init(CGRect)` is added.

<a id="282-of-4-385-api-manifest-entries-record-a-base-symbol-no-p-invoke-binds"></a>

## 282 of 4,385 api-manifest entries record a base symbol no P/Invoke binds

Members emitted by specialized bridges (method-generic, multi-callback closure, AsyncStream property) mint entry-point names outside `ComputeEntryPoint`, so the manifest records the base symbol rather than the one actually bound. Routing them through `ComputeEntryPoint` over-suffixes them (`_XM_XC` instead of `_XM`), so the accessor-symbol fix was scoped to accessors and the gap is documented on `ModuleEmissionContext.GetMethodEntryPointSymbol`. Closing it needs each bridge to stamp the entry point it wrote. **Trigger:** the manifest is used to prove symbol-level ABI compatibility rather than surface shape, or a bridge is reworked and can stamp its symbol cheaply.

<a id="the-property-rename-ledger-classifies-into-two-buckets-and-the-fallback-suffix-is-applied-without-recording-wh"></a>

## The property-rename ledger classifies into two buckets, and the fallback suffix is applied without recording which bucket it came from

The rename ledger sorts a colliding property into one of two buckets and, when neither yields a name, falls back to a `PropertyValueSuffix`. The fallback is applied at the point of collision without a record of *why* the derived arms declined, so a reader of the emitted name (or of the report) cannot tell a deliberate suffix from an exhausted derivation. Direction is safe — the suffix always produces a compilable, unique name — but it makes the ledger's own output non-self-explaining, which is the property the overload ladder deliberately has (it records the rung each name came from). **Fix shape:** record the declining arm alongside the assigned name, the way `OverloadNameDisambiguator` records its rung. **Trigger:** a rename needs to be explained to a consumer and the ledger cannot say where it came from, or a session is already reworking the property-rename path.

<a id="the-overload-name-gate-reads-final-shaped-names-so-a-synthetic-name-that-already-looks-shaped-evades-it"></a>

## The overload-name gate reads final shaped names, so a synthetic name that already looks shaped evades it

The `--compile-only` overload-name gate reads the resolver's own assignment records and rejects a bare numeric suffix — which is what makes it immune to an author-written `Process2`. The converse is not covered: a *synthetic* name that arrives already carrying a shaped form (a `Configure2Async`-style name minted by a producer downstream of the resolver) satisfies the check without the resolver ever having assigned it, so the gate sees nothing to reject. Scope is narrow — only producers that mint names outside the resolver can reach it, which is the same population as the case-only collision pass and the specialized bridges. **Fix shape:** have every name-minting producer record its assignment the way the resolver does, so the gate reads one complete ledger rather than one producer's. **Trigger:** a numeric-looking name reaches a published surface without a resolver record behind it, or a new name-minting producer is added.

<a id="the-overload-ladder-s-return-type-only-refusal-set-was-never-enumerated"></a>

## The overload ladder's return-type-only refusal set was never enumerated

The last rung of the disambiguation ladder is refusal, and one thing it necessarily refuses is a Swift overload pair differing only in return type — legal in Swift, unprojectable in C#. Which members that actually costs was never measured, so there is no list of refused members and no sense of whether the population is one fixture shape or a recurring corpus pattern. Nothing is wrong today: refusal is the correct outcome and it is reported per member. What is missing is the aggregate. **Fix shape:** count the return-type-only refusals across the corpus from the existing per-member records — a report query, not a generator change. **Trigger:** a consumer reports a missing member that turns out to be a return-type-only refusal, or a skip-surface program wants the number.

<a id="corpus-wide-closure-skip-population-was-never-quantified"></a>

## Corpus-wide closure-skip population was never quantified

The closure-bearing-initializer recovery landed against a stated corpus figure of roughly 69 closure-related skips out of 219 — a figure that was never actually measured, then or since. So the recovery's coverage is known relative to its fixtures and unknown relative to the corpus: how many of the remaining refusals are failable, throwing, async, struct/enum or `MethodClosureBridge` shapes is unmeasured, and any claim about "most closure skips" is currently unsupported. **Fix shape:** tally the closure-family skip reasons from the existing corpus binding reports before funding any further closure recovery, so the next widening is aimed at the largest real bucket. **Trigger:** further closure-recovery work is proposed — measure first, or the scope is a guess.

<a id="propertydecl-emission-signals-are-unreliable-in-both-directions-on-the-property-paths"></a>

## `PropertyDecl` emission signals are unreliable in both directions on the property paths

2026-08 audit (verified against code). Two decoupled flags, each stamped at the wrong moment on one path. (a) `EmitAsyncPropertyAsMethods` (`Handler/PropertyHandler.cs:1539`, called at `:419`) marks `WasEmitted` (`:1639`) while emitting `Get{Name}`/`Set{Name}` *methods* — never a property — so the flag asserts occupancy of a property name that is actually unoccupied; and that path never stamps `EmittedCSharpName` at all. (b) The sync path stamps `MarkEmittedCSharpName` at `:436`, *before* six accessor-level skip returns (`:527/:538/:553/:565/:574/:582`) can still abandon the property — so a skipped property can carry a stamped emitted name. `PropertyRenameLedger.cs:63` defends itself (`requiresEmissionFlag && !property.WasEmitted`), but any other reader keying on either flag inherits a phantom occupancy or a phantom name. **Fix shape:** stamp both signals at a single success point after the accessor skip gates, and give the async-lowered path its own marker distinct from property-name occupancy. **Trigger:** a second consumer of these flags appears beyond the rename ledger, or a rename/collision traced to a skipped or async-lowered property.

<a id="overloadambiguityguard-recordreservation-failable-factory-arm-records-a-shape-under-a-key-it-did-not-claim"></a>

## `OverloadAmbiguityGuard.RecordReservation` failable-factory arm records a shape under a key it did not claim

2026-08 audit (verified against code). `RecordReservation` (`:158`) is a plain dictionary overwrite — deliberate, per the adjacent comment, because recording "can only ever make the check MORE permissive". The failable-factory arm in the recovery branch violates the same site's claimed-keys-only contract: it records its shape under a projectedKey owned by the earlier failable *winner*, overwriting the winner's recorded shape. Direction stays safe (more permissive ⇒ over-conservative naming at worst, never a duplicate signature), but the ledger now misattributes which member claimed the key. Call sites: `MethodHandler.cs:2078`, `ModuleHandler.cs:358`, `DefaultParameterOverloadEmitter.cs:520`. **Fix shape:** have the failable-factory arm record under a key it actually claimed (or not at all when the key belongs to another member), so the adjacent comment stays true. **Trigger:** an ambiguity decision or report row traced to the overwritten shape, or a session already reworking the reservation path.

<a id="declemissionstatesnapshot-captures-no-subscriptdecl-state"></a>

## `DeclEmissionStateSnapshot` captures no `SubscriptDecl` state

2026-08 audit (verified against code). `Recovery/DeclEmissionStateSnapshot.cs:19-36` snapshots/restores mutable emission state for methods, properties, classes, and arguments — subscripts are absent. Benign today only because `SubscriptDecl` carries no mutable emission flag at all (no `WasEmitted`/`EmittedCSharpName` — itself [protocol-emitter F10 subscript latent](protocols.md#protocol-emitter-narrow-latents-cs0111-cs0108-null-fn-pointer)): there is nothing to leak across a recovery re-emit. The moment a SubscriptDecl-level flag is introduced, a module re-emit after a failed pass would read stale-true state from the first attempt. **Fix shape:** add the subscript arm to the snapshot in the same commit that adds the first mutable `SubscriptDecl` emission field — never separately. **Trigger:** the first mutable emission-state field on `SubscriptDecl`.

<a id="classhandler-seeds-method-vs-property-collision-renames-from-all-properties-not-emitted-ones"></a>

## `ClassHandler` seeds method-vs-property collision renames from ALL properties, not emitted ones

2026-08 audit (Grok r2 note, verified against code). `ClassHandler.cs:412-414` builds its `propertyNames` HashSet from every `classDecl.Properties` entry, so a method colliding with a property that never surfaces in the binding (skipped, or async-lowered to `Get{Name}` methods) is still renamed `Foo` → `FooMethod`. `NonFrozenStructHandler` already seeds from `actuallyEmittedPropertyNames`. Over-conservative only — never a compile error, just an unnecessary rename on the published surface. Blocked in practice on [property emission-signal issue](emitter.md#propertydecl-emission-signals-are-unreliable-in-both-directions-on-the-property-paths): filtering on `WasEmitted` is only correct once that flag is stamped truthfully. **Fix shape:** mirror the struct handler's emitted-only seeding, sequenced after the emission-signal fix. **Trigger:** a consumer-visible `FooMethod` rename whose colliding property does not exist in the binding, or [property emission-signal fix](emitter.md#propertydecl-emission-signals-are-unreliable-in-both-directions-on-the-property-paths) landing.

<a id="buildoverloaddeclcore-clones-drop-originalswiftname-swiftdefaultexpression"></a>

## `BuildOverloadDeclCore` clones drop `OriginalSwiftName`/`SwiftDefaultExpression`

2026-08 audit (verified against code). The default-parameter overload builder's trailing-trim and all-defaults forms reconstruct the kept `ArgumentDecl`s without copying `OriginalSwiftName` or `SwiftDefaultExpression`. Compile-benign at tip: `EmitSwiftWrapper` renders call labels via `BuildSwiftCallArgLabel`, and keyword-mangled labels round-trip through `StripParserKeywordPrefix`, so no current label needs the dropped fields. The latent is any future label whose faithful Swift spelling depends solely on `OriginalSwiftName` (a spelling the keyword-strip cannot reconstruct) — the overload's wrapper would then call with the wrong label, a wrapper-compile `expected argument label` error. **Fix shape:** carry both fields through the clone (copy the source `ArgumentDecl` wholesale rather than reconstructing from a field list). **Trigger:** a wrapper-compile label error on a default-parameter overload, or a new reader of `OriginalSwiftName` on the overload path.

<a id="subscript-requirements-still-do-not-reverse-dispatch-through-a-swift-backed-existential"></a>

## Subscript requirements still do not reverse-dispatch through a Swift-backed existential

2026-09 wave (s6, BQ-13). Conformance is restored — a conforming type keeps its interface and a consumer holding it *as* the interface reads the indexer — but `ProtocolProxyEmitter.InterfaceImpl.cs:1435-1443` unconditionally records `SkipReason.ProtocolWitnessNotDispatchable` for a subscript on a value that arrived from Swift as `any P`, emitting an `[Obsolete(… SB0003)]` throwing member. `SubscriptRequirementConformanceTests.TestCatalogHost_SwiftBackedExistentialIndexerIsDocumentedGap` pins the boundary. A residual CS0535 risk also remains: `CanEmitSubscript` covers only the statically-decidable half of `TryPlanIndexer`'s refusals, so a subscript the planner rejects late can still drop out of a class that declares the interface. **Trigger:** a fixture or consumer reads a subscript through a Swift-returned existential, or a CS0535 appears in the verify-recover loop naming an indexer.

<a id="resolvesubscripttypename-falls-back-to-typespec-tostring-not-anytype"></a>

## `ResolveSubscriptTypeName` falls back to `typeSpec.ToString()`, not `AnyType`

2026-09 wave (s6). A subscript element type that clears the `AnyType` gates but has no projection reaches emission as a bare Swift spelling; every other resolver falls back to the `AnyType` projection. It is the one place where "clears the gate" does not imply "resolves to a real C# type". Pre-existing; not observed on any corpus. **Trigger:** a generated indexer fails to compile with an unresolved Swift type name.

<a id="a-public-swift-property-named-payload-produces-uncompilable-c-cs0102"></a>

## A public Swift property named `payload` produces uncompilable C# (CS0102)

2026-09 wave (s4/s9). The emitted struct wrapper declares a `Payload` accessor for its backing buffer; a Swift stored property `payload` projects to the same member name and the generated file fails to compile. Worked around in the BindingTests fixture by renaming to `carried`. Pre-existing; fix = reserve the emitted infrastructure names in the member-name disambiguator. **Trigger:** `nuke validate` or a consumer library carries a public `payload` stored property.

<a id="protocolconformancevalidator-label-colliding-inits-recovered-as-static-factories-unexplored"></a>

## `ProtocolConformanceValidator` × label-colliding inits recovered as static factories — unexplored

2026-09 wave (s5). When a Swift protocol `init` requirement is satisfied by an initializer that the collision recovery turned into a `CreateWith…` factory, the conformance validator may key off "has a constructor" and report the type non-conforming. No fixture exercises it and none was built. **Trigger:** a validation library or consumer shows a conformance dropped on a type whose only matching init became a factory.

<a id="subscript-refusals-carry-no-inline-unsupported-marker-so-the-whole-class-is-invisible-to-the-skip-surface-tren"></a>

## Subscript refusals carry no inline `// Unsupported:` marker, so the whole class is invisible to the skip-surface trend gate

2026-09 wave. A refused subscript is recorded in `binding-report.json` like any other skip, but the subscript emission path writes no inline `// Unsupported:` comment into the generated `.cs`, unlike the method and property paths. `--skip-surface` parses those inline markers straight out of the generated files under `BindingTests/output/` — deliberately, so what it ratchets is exactly what a consumer reading the binding sees — so **no** subscript refusal, old or new, moves a baseline count, and a subscript that starts being refused shows up in neither direction of the trend. Pre-existing and not specific to any one refusal; noted because a receiver-carrier refusal landed on the subscript path with no baseline row and the absence read as a reseed bug until it was traced here. The fix is to emit the marker on the subscript path (one baseline reseed, then the class becomes visible), **not** to teach the gate a second source of truth. **Trigger:** a subscript refusal regresses unnoticed, or the skip-surface gate is next reworked.

<a id="overloadnamedisambiguator-memoizes-process-globally-so-rename-rows-may-not-re-record-on-an-emission-retry"></a>

## `OverloadNameDisambiguator` memoizes process-globally, so rename rows may not re-record on an emission retry

The disambiguator memoizes its decisions in a process-global `ConditionalWeakTable`. A first emission pass records the class-body rename rows into the report; a retry inside the same process gets the memoized answer and short-circuits the path that records them, so the retry can publish a report missing rename rows the first pass carried. The names themselves stay stable — this is a reporting gap, not a naming one — and no fixture reproduces it, because the retry paths that would show it do not re-enter the disambiguator in the tree today. **Fix shape:** record the row where the name is *used* rather than where it is *computed*, so a memoized answer still produces one. **Trigger:** a rename row is observed missing from a `binding-report.json` after an emission retry, or the overload-name gate reds on an empty rename ledger where a rename plainly happened.

<a id="sixteen-emitter-sites-spell-init-rawvalue-without-consulting-the-two-typerecordflags-that-change-its-correct-s"></a>

## Sixteen emitter sites spell `init(rawValue:)` without consulting the two `TypeRecordFlags` that change its correct spelling

**Revisit when:** a consumer or corpus library passes an Apple-framework enum or an imported `OptionSet` through a closure, an extension member or a SwiftUI bridge parameter, or any `init(rawValue:)` compile error or force-unwrap trap is reported against a generated Swift wrapper for an imported enum.

<details>
<summary>Recorded evidence and disposition</summary>

Added 2026-09-11 (reroute-wave audit), raised by a reviewer against two sites and enumerated to sixteen before dispositioning. An imported Apple enum carries `TypeRecordFlags.ExternalAppleEnum` (its cdecl integer type is *inferred* from the managed enum's storage and can disagree in signedness or width with the real Swift `RawValue` — `CGImagePropertyOrientation` binds as managed `int` and is `UInt32`-backed) and, when it is a flags enum, `OptionSet` (whose `init(rawValue:)` is **non-failable**, so both `guard let` and `!` are Swift compile errors). Only `CdeclParamMapper` and `CdeclReturnRenderer` honour them, via `.init(truncatingIfNeeded:)` on the raw argument, a direct non-failable bind for `OptionSet`, and `as T?` coercion where failability is unknown. Sixteen other sites across eight files re-derive the spelling blind: the closure lane (`ClosureEmitter.SwiftWrapper.cs:700,:765`, `NestedClosureBridge.cs:1795,:1839`, `MethodClosureBridge.cs:765`, `ClosureEmitter.InvokeThunk.cs:271`), the extension emitters (`ForeignTypeExtensionEmitter.cs:835`, `CrossModuleExtensionEmitter.Struct.cs:633,:774`) and the SwiftUI bridge (`SwiftUIBridgeEmitter.cs:981,:1488,:1546,:1749,:1802,:2252`, `SwiftUIBridgeEmitter.AsyncPattern.cs:875`). A flagged record reaching any of them emits a Swift compile error (wrapper withdrawn, member silently lost) or an exact conversion that traps on a high-bit value. **The structural cause, and why no subset is a fix:** `ClosureHandler.GetSimpleEnumInfo` returns `(csUnderlying, swiftScalar, hasRawValue, swiftRawType)` and discards both flags, so none of its ~25 callers *can* honour them, while `ExtensionMarshallingHelper.TryGetSimpleEnumLowering` and the SwiftUI bridge's `InitAnalyzer` repeat the same blind lookup independently. Neither flag is ever set for a record parsed from the bound module's own Swift source — both are synthesized only behind the Apple-framework registry gate or the ObjC `NS_OPTIONS` bridge — so the reachable set is Apple-framework enums, which is most real consumer bindings and nothing in this repo's fixtures. `OptionSet` is serialized (`optionSet="true"`); `ExternalAppleEnum` is not, so a cross-module round-trip loses it. **Fix shape:** widen the shared accessor to carry both flags, then update all sixteen consumers in one pass, and decide separately whether `ExternalAppleEnum` needs serializing; a fixture putting an imported Apple enum and an imported `OptionSet` through a closure parameter and return has to come first, since none exists. **Trigger to revisit:** a consumer or corpus library passes an Apple-framework enum or an imported `OptionSet` through a closure, an extension member or a SwiftUI bridge parameter, or any `init(rawValue:)` compile error or force-unwrap trap is reported against a generated Swift wrapper for an imported enum.

</details>

<a id="a-caseonlyrenames-row-can-outlive-the-declaration-it-names"></a>

## A `CaseOnlyRenames` row can outlive the declaration it names

Case-only rename rows — two sibling Swift names differing only by case, which carry no labels or parameter types to name a member by — are published during the pre-pass, before the emission decision is made. A declaration skipped later takes its member out of the emitted surface and leaves the row behind, so `EmittedName` can name something no consumer will find. The overload-name gate reports this lane rather than failing on it, so the effect is a misleading report row rather than a red, and no fixture reproduces it today. **Fix shape:** publish at the emission decision, or reconcile at `ReportCollector.Complete()` — the latter needs a `DeclId` on `CaseOnlyRenameItem` so a row can be matched back to its declaration. **Trigger:** a report reader or consumer hits a `CaseOnlyRenames` row naming a member that is not in the binding, or the rename ledger is revisited for another reason.

<a id="long-tail-bare-system-rooted-references-in-emitted-code-536-generated-lines"></a>

## Long-tail bare `System.`-rooted references in emitted code (~536 generated lines)

**Revisit when:** a real module exporting a type named `System` (or any other root BCL namespace identifier) appears in a corpus/validation sweep, **or** a session already refactoring emitted-name construction can fold the helper in cheaply. **Verification command:** `grep -rh '[^:a-zA-Z._]System\.[A-Z]' BindingTests/output | grep -v 'global::System' | grep -v 'using System' | grep -v '///' | wc -l` (536 at time of writing; emitted `using System…;` directives MUST stay bare — `global::` is illegal there).

<details>
<summary>Recorded evidence and disposition</summary>

The qualification pass established the convention "every namespace-or-BCL reference emitted is `global::`-qualified" and swept every *collidable-in-practice* surface (bare `Type`, the `DllImport`/`UnmanagedCallConv`/`UnmanagedCallersOnly`/`ModuleInitializer`/`MethodImpl` attribute families, `Encoding.UTF8`, the conformance dictionaries, `GC.KeepAlive`, `ArgumentNullException`, `Marshal.PtrToStructure`, `Unsafe.SizeOf`, the metadata-fallback `catch` clauses, `Buffer.MemoryCopy`) — regenerated `BindingTests/output` went from 2,552 bare lines to **536**, against **37,602+** `global::`-qualified ones. The residue is a diffuse tail (no shape >10 occurrences), dominated by two families: (a) `System.Numerics.{Vector2,Vector3,Vector4,Quaternion,Matrix4x4}` emitted as projected C# type names sourced from a type-mapping table, and (b) `System.Collections.Generic.List<string>` / `System.Exception` inside `AsyncHarnessEmitter` + `ErrorRegistryHelperEmitter` raw-string bodies. **Why not swept:** (1) it is unreachable without a bound Swift module exporting a public type literally named `System` — unlike `Type`, which SwiftyJSON really has, no corpus library does, so there is no observed failure; (2) family (a) flows through mapping tables where the same string can serve **comparison** and **emission** (the `InlineSwiftStructAllowlist` trap — a `"Foundation.UUID"` key must stay bare while its `global::System.Guid` value must not), so a blind sweep risks silently breaking type resolution for zero corpus benefit; (3) ~100 generator files mix emitted text with the generator's *own* `System.` code (`Process`, `Program.cs`, parsers, models), and separating them per-site is a project, not a patch. **Fix shape:** not 500 call-site patches — a central emission helper (the session doc's own preferred shape) that every emitter routes BCL type names through, so qualification is structural rather than per-literal; that also fixes families (a) and (b) at once and prevents regrowth. **Trigger to revisit:** a real module exporting a type named `System` (or any other root BCL namespace identifier) appears in a corpus/validation sweep, **or** a session already refactoring emitted-name construction can fold the helper in cheaply. **Verification command:** `grep -rh '[^:a-zA-Z._]System\.[A-Z]' BindingTests/output | grep -v 'global::System' | grep -v 'using System' | grep -v '///' | wc -l` (536 at time of writing; emitted `using System…;` directives MUST stay bare — `global::` is illegal there).

</details>

<a id="emitted-against-a-non-nullable-apple-value-type-cs0023-secpadding"></a>

## `?.` emitted against a non-nullable Apple value type → CS0023 (`SecPadding`)

**Revisit when:** the session that takes the SwiftyRSA corpus lib to `ok`, or the next CS0023 of this shape in a corpus/validation sweep. Not in `corpus-sweep/SYNTHESIS.md` — it post-dates that sweep.

<details>
<summary>Recorded evidence and disposition</summary>

Repro: `python3 scripts/run_library.py SwiftyRSA --skip-convert` in `internal-binding-testing/corpus-sweep` → 4× `error CS0023: Operator '?' cannot be applied to operand of type 'SecPadding'` at `SwiftyRSA.Types.ClearMessage.cs:266`, `SwiftyRSA.Types.EncryptedMessage.cs:262`, `SwiftyRSA.Types._objc_ClearMessage.cs:344`, `SwiftyRSA.Types._objc_EncryptedMessage.cs:294` (all column 47). The emitter applies a null-conditional to a value of type `Security.SecPadding`, which is a non-nullable struct, so the `?` operator is illegal. Previously **masked**: SwiftyRSA's compile stopped at 18× CS0426 (`SwiftyRSA` module-vs-class name collision) before reaching these; the qualification fix cleared the CS0426 family (now 0) and exposed this one. Not a qualification defect — the reference resolves fine; the nullability *shape* of the projected Apple value type is wrong. Likely an `IsOptionalObjCBridged` / `TypeProjectionFactory` parity gap where a `SecPadding`-typed member is projected as a reference-ish/optional shape on the accessor path but emitted as a bare struct in the C# signature (see the `IsOptionalObjCBridged` parity constraint in `.claude/rules/constraints.md`, and `AppleFrameworkRegistry.ValueTypes`). **Symptom:** CS0023 on `?.`/`?` against any `AppleFrameworkRegistry` value type. **Fix shape:** decide the member's nullability once from the projected type and gate the null-conditional on it (or register `SecPadding` correctly as a known Apple value type so the optional projection isn't chosen); then re-run the SwiftyRSA corpus lib as the gate. **Trigger to revisit:** the session that takes the SwiftyRSA corpus lib to `ok`, or the next CS0023 of this shape in a corpus/validation sweep. Not in `corpus-sweep/SYNTHESIS.md` — it post-dates that sweep.

</details>

<a id="pulse-enum-accessor-cdecl-wrapper-swiftbind108-distinct-from-the-enclosing-internal-metadata-gate"></a>

## Pulse enum-accessor `@_cdecl` wrapper SWIFTBIND108 (distinct from the enclosing-internal metadata gate)

Repro: `python3 scripts/run_library.py Pulse --skip-convert` in `internal-binding-testing/corpus-sweep`. The C# emits `[DllImport]` EntryPoints for synthesized enum accessors — `SBW_..._eq_` (Equatable `==`), `..._CaseByIndex` (CaseIterable), `..._InitWithRawValue` (RawRepresentable) — whose matching `@_cdecl` the wrapper never defines, so SWIFTBIND108 fail-closes the module. Several of the enums are String-raw-value enums nested in **public** structs (`ConsoleFilters`, `ConsoleListOptions`), so this is **not** the F-033 foreign-nested/enclosing-internal metadata pattern fixed this session (that path is the simple/ISwiftObject *metadata* wrapper on an internal-enclosing enum) — it is a separate **enum operator/RawRepresentable/CaseIterable accessor** family where the synthesized-member `@_cdecl` emission and the C# DllImport disagree on whether the wrapper symbol exists. **Fix shape:** make the enum-accessor/operator wrapper emitters (`EnumHandler.RawRepresentable.cs`, `OperatorHandler.cs`, CaseIterable accessor site) gate their C# DllImport on the same "will the `@_cdecl` actually be emitted?" signal the Swift side uses — the parallel of F-033 but for synthesized accessors, not metadata. Confirm root cause per-symbol first (which side drops the `@_cdecl`). **Trigger to revisit:** a session takes Pulse/PulseUI to `ok`, or the next SWIFTBIND108 of this enum-accessor shape in a corpus/validation sweep.

<a id="2026-07-corpus-re-sweep-residual-generator-families-session-11-measurement-close-out"></a>

## 2026-07 corpus re-sweep residual generator families (Session 11 measurement close-out)

The 120-library corpus re-sweep landed 39/120 green (+26 from Sessions 1–9's family fixes; ZIPFoundation recovered via this session's F-033 metadata fix). The residual failures in OUR stages are **pre-existing, not regressions**, and split into two genuine generator families worth a future fix arc: (a) **"Swift wrapper compilation failed"** ~13 libs — the emitted wrapper has an uncompilable construct (CSV.swift, CocoaMQTT, CoreStore, Macaw, MessageKit, PromiseKit, Pulse, ReSwift, ReactorKit, RevenueCat, YPImagePicker, adyen-ios, swift-protobuf, swiftui-charts); (b) **generated-C# CS-family compile errors** ~11 libs — CS0114/CS0108/CS0508/CS0216/CS0234/CS0246 (FloatingPanel, JTAppleCalendar, OAuthSwift, SwiftMessages, DGCharts, Factory, Resolver, SwiftDate, SwiftUICharts, rive-ios). Plus SWIFTBIND109 ×5 (CombineCocoa, FlexLayout, TelemetryDeck, Yams, swift-system) and SWIFTBIND058 ×1 (SwiftUIX), gate-specific. The remaining ~13 "no public types / internal-only API" generate_failed are **by-design fail-closed** (no bindable public surface) and the 15 NU1101 are **harness/environmental** (sibling multi-module bindings not built into the local feed) — neither is a defect. Full per-lib evidence: `internal-binding-testing/corpus-sweep/SYNTHESIS.md` + findings.md (scratch, temporary) and memory `project_corpus_sweep_2026_07`. **Trigger to revisit:** a dedicated per-family fix session, or one of the (a)/(b) libs is promoted into `validation-libraries.json`.

<a id="cross-module-name-shadowing-must-be-fixed-at-the-render-layer-not-per-emission-site"></a>

## Cross-module name shadowing must be fixed at the render layer, not per emission site

**Revisit when:** a cross-module name-shadowing compile error in generated code — capture that module pair as the fixture, then generalize the pass rather than patching the site that happened to emit the shadowed name.

<details>
<summary>Recorded evidence and disposition</summary>

When a bound module's namespace (or a type it declares) shadows a name another module's emitted reference resolves through, the reference has to be globally rooted. The obvious approach — prefix `global::` at each emission site — is **unimplementable as a general rule**: emitter strings mix globally-rooted and namespace-relative paths at the same call sites, so blanket prefixing produces `global::` in front of a namespace-relative path and the compile fails with CS0400 (name not found in the global namespace); telling the two apart per literal is exactly the information the emission site does not have. The implementable shape is the **render layer**: `ModuleEmitter.QualifyNamespaceReferences` already rewrites bare `{Namespace}.Type` into `global::{Namespace}.Type` in one whole-text pass over the finished file (with lookbehinds for `namespace ` / an existing `global::`, a nested-type exclusion set so `Namespace.Nested` is left alone, and a `TextEditJournal` so offsets measured on the pre-rewrite text carry exactly onto the result). It is invoked today only for the single case it was written for — the module-namespace-vs-type-name collision — driven by one hardcoded namespace. Generalizing it means running the same pass over the TypeDatabase's set of namespace roots, each with its own nested-type exclusion set, so qualification becomes structural rather than per-literal. Deferred because that widens a regex rewrite across all generated text for a shape with no observed red, and the exclusion sets must be right per root or it introduces the CS0400 it exists to prevent. Related in kind (and the same preferred end-state — one central helper rather than N call-site patches) is the bare-`System.`-rooted long tail above. **Trigger:** a cross-module name-shadowing compile error in generated code — capture that module pair as the fixture, then generalize the pass rather than patching the site that happened to emit the shadowed name.

</details>

<a id="extensions-declared-on-external-objc-owned-types-u-002"></a>

## Extensions declared on external / ObjC-owned types (U-002)

Design-note only (mandated; no implementation attempted). The generator binds a Swift module's *own* public declarations; a module that declares an extension on a type it does **not** own — the Kingfisher `.kf` namespace pattern `UIImageView.kf.setImage(with:)`, extensions on `UIImage`/`URL`/`String`, ObjC-owned or Foundation-owned receivers — has those members surface under the foreign type, which the target module's ABI does not re-export, so they are not emitted. This is not a suppression bug: the extension members are not part of the bound module's exported type surface. A real fix is a cross-module extension-emission feature — attribute the extension's members onto the (separately bound, or Apple-supplement) foreign type — with all the ownership/duplicate-registration questions that implies. **Trigger:** repeated consumer reports naming missing extension APIs on external types, **or** a session explicitly scoping cross-module extension emission.

<a id="coregraphics-namespace-unification-swift-cg-coregraphics-cg-u-001"></a>

## CoreGraphics namespace unification — `Swift.CG*` → `CoreGraphics.CG*` (U-001)

Owner surface decision, **not authorized**. Swift-ABI-bound geometry surfaces as the portable `Swift.CGPoint`/`CGSize`/`CGRect` structs while UIKit/AppKit APIs want `CoreGraphics.*`; the shipped mitigation is the hand-written, Apple-TFM-conditional implicit conversions in `Swift.Runtime` (`CGRect.cs`/`CGPoint.cs`/`CGSize.cs`), now covered by `CoreGraphicsImplicitConversionTests`. *Unifying* so bound APIs surface `CoreGraphics.CGSize` directly means flipping `managedNameSpace="Swift"`→`"CoreGraphics"` on the three entries in `CoreGraphicsDatabase.xml`. That is **source-breaking** for every consumer already typed against `Swift.CGSize`, **and** layout-unverified: the emitted P/Invoke raw-type path currently reads/writes the `Swift.CG*` frozen structs, so the flip needs a full `nuke binding-tests` + `nuke validate` ABI re-verification, not just a rename. Corpus scale of the ambiguity: Kingfisher 68 `Swift.CGSize` refs, Lottie 60 `CGPoint`/48 `CGRect`/27 `CGSize`, Nuke 4 `CGSize`. **Trigger:** owner authorizes the source-breaking rename together with the ABI re-verification sweep.

<a id="nested-type-rename-type-vs-property-choice-has-no-consumer-knob-u-003"></a>

## Nested-type rename: type-vs-property choice has no consumer knob (U-003)

**Revisit when:** a consumer names a concrete case where the type-side rename is the wrong call, with the shape that makes it wrong — not a general preference.

<details>
<summary>Recorded evidence and disposition</summary>

**Verified already-correct — no defect.** The `*Type` suffix the probe reported (`ConfigurationType`/`ModeType`/`AddressType`/`ColorsType`) reflects an *older published* package; tip-of-tree `ApplyNestedTypeRenames` (`NameProvider.cs`) renames a nested type **only** on a true CS0102 collision — a same-named property whose type IS that nested type — and then with a kind-aware suffix (struct/class→`Info`, enum→`Kind`). A Stripe regen against HEAD emits `ConfigurationInfo`/`ModeKind`/`AddressInfo`/`ColorsInfo` and zero `*Type`-suffixed nested classes; a non-colliding nested type keeps its natural name. Pinned by `NestedTypeRenameTests` and `NameProviderRenameTests` (`PrecomputeNestedTypeRenames_AppliedToDepModuleWithoutCollision_LeavesTypeRecordUnchanged`, `..._StructParentWithTypeSuffixChild_UsesInfoSuffixWithoutNumericBump`). Residual (design, not defect): the policy fixes on renaming the **type** (the property is the consumer-facing member); a consumer who would rather keep the natural type name and rename the property has no knob. **Revisited by the naming-SSOT session (2026-08-02) and re-affirmed — CLOSED as a decision, not merely deferred.** `NameCollisionPolicy` (`src/Swift.Bindings/src/Marshaler/NameCollisionPolicy.cs`) now states the type-vs-member choice as an explicit, documented precedence with two disjoint token vocabularies: a type-side token (`Info`/`Kind`) means the TYPE moved, a member-side token (`Value`/`Method`/`With`/`Get`/`Swift`) means the MEMBER moved, so a reader can tell which side was renamed from the name alone. A per-consumer knob would break exactly that readability guarantee (the same token would no longer imply the same side) and would make the emitted surface configuration-dependent, which the api-manifest gate treats as a contract change. **Trigger:** a consumer names a concrete case where the type-side rename is the wrong call, with the shape that makes it wrong — not a general preference.

</details>

<a id="case-only-collisions-the-a3-pass-deliberately-leaves-alone"></a>

## Case-only collisions the A3 pass deliberately leaves alone

**Revisit when:** a case-folding consumer toolchain (a case-insensitive filesystem step, a code generator, an IDE refactor) demonstrably breaks on one of these pairs — capture that pair as the fixture.

<details>
<summary>Recorded evidence and disposition</summary>

The 2026-08-02 pass resolves two shapes: a type whose emitted leaf case-folds onto a sibling *namespace facade*, and two sibling properties whose distinct Swift names project onto the **same** C# identifier (a hard CS0102 that previously cost the later property its binding). Three neighbouring shapes are out of scope **by decision, not oversight**: **(a)** two members whose projections differ ordinally but match case-insensitively (`DocumentURL` vs `DocumentUrl`) — C# keeps them distinct and legal, so renaming one is churn against a surface that already compiles and would be an api-manifest removal for no defect; **(b)** two top-level *types* that case-fold onto each other where **neither** is a namespace facade — same argument, plus neither side is the obviously-movable one (the facade arm only has a clear answer because a facade is a namespace segment every nested type and cross-module reference is spelled under); **(c)** methods and enum cases, which reach their own case-collision handling through `NameProvider.ComputeCaseNameMap` and the overload-collision resolver. Widening (a)/(b) is a *readability* preference, and the cost is real: every widened rename is a public-surface change. **Trigger:** a case-folding consumer toolchain (a case-insensitive filesystem step, a code generator, an IDE refactor) demonstrably breaks on one of these pairs — capture that pair as the fixture.

</details>

<a id="case-only-requirement-adoption-stops-at-the-direct-conformance-edge"></a>

## Case-only requirement adoption stops at the direct conformance edge

**Revisit when:** a real library (or a consumer report) shows a refined/inherited requirement pair whose C# names diverge between the interface and its implementation — capture it as the fixture and widen the adoption walk to the refinement graph.

<details>
<summary>Recorded evidence and disposition</summary>

The A3 pass decides case-only member renames on protocols first and has conforming types *adopt* the requirement's settled name, so a conformer that declares the two colliding requirements in the opposite order still binds each C# member to the storage the protocol named (C# matches an implicit interface implementation by name, so a divergent winner compiles and reads the wrong field). Adoption walks the direct `Conformances` edge of a `ClassDecl`/`StructDecl`/`EnumDecl` only. Two neighbouring edges are **out of scope by decision**: **(a)** a protocol that *refines* another (`protocol B: A`, i.e. `ProtocolDecl.InheritedProtocols`) — `B` re-decides any case-only pair it redeclares without consulting `A`, and `moduleDecl.Protocols` is not topologically ordered, so closing this means a refinement-graph traversal with a cycle guard, not a lookup; **(b)** a conformance reached only *transitively* (the conformer lists `B`, and the colliding requirement is inherited from `A`). Both are latent rather than live: they need a protocol hierarchy where the SAME case-only pair is redeclared at two levels AND the levels disagree on declaration order — no corpus library exhibits it, and the direct edge covers every observed shape. **Trigger:** a real library (or a consumer report) shows a refined/inherited requirement pair whose C# names diverge between the interface and its implementation — capture it as the fixture and widen the adoption walk to the refinement graph.

</details>

<a id="implementation-types-in-public-signatures-existential-result-projection-u-005"></a>

## Implementation types in public signatures — existential-result projection (U-005)

Two shapes. **(a) design-gated:** a completion whose Swift result carries an existential surfaces as `SwiftResult<T, ExistentialContainer1>` — Stripe `FlowController.Create(...)` (regen: `Action<Swift.SwiftResult<…FlowController, Swift.Runtime.ExistentialContainer1>> completion`). Projecting it to an idiomatic `(T?, Error?)` / `Task<T>` requires runtime ARC ownership handling and a device leak-validation leg (the async fault-path carrier-leak family — see `[[project_suppressed_proxy_async_carrier_leak]]`), so it is not a pure emitter change. **(b) by-design, not reopenable:** `ModeType.Payment`'s amount surfaces as `System.IntPtr` because it is a Swift `Int` on a method return/parameter — method return types are **not** narrowed (`.claude/rules/constraints.md`: C# overload resolution would prefer an `int` overload and silently 64-bit-truncate). **Trigger (a only):** a dedicated existential-result projection session that includes device ARC/leak validation.

<a id="convenience-overload-scope-beyond-scalar-url-u-007-residual"></a>

## Convenience-overload scope beyond scalar URL (U-007 residual)

**Revisit when:** a consumer names one of these shapes, or a session generalizing convenience overloads past the scalar-URL case.

<details>
<summary>Recorded evidence and disposition</summary>

The scalar `URL`→`string` sugar shipped and fires in the corpus (Kingfisher `Cancel(string)`, Nuke `ImageTask(string)` + `DataCache.Init(string,…)`). Shapes that stay out of scope by design: **(a) collections** — `IEnumerable<NSUrl>`/`IReadOnlySet<NSUrl>` params (Nuke `StartPrefetching`) would need an element-wise-constructing `IEnumerable<string>` overload; **(b) URL-conforms-to-protocol** — a `URL` surfaced as the protocol it conforms to (Kingfisher `IResource`), where a `string`/`NSUrl` overload must construct the *conforming* type, not a bare `NSUrl`; **(c) non-composition with sibling axes** — the sugar rewrites only its own `URL` axis from the primary signature, so a method like `load(url: URL, count: Int)` gets `Load(NSUrl,nint)`/`Load(NSUrl,int)`/`Load(string,nint)` but no `Load(string,int)` cross-product (each post-processor is independent by design; a combinatorial expansion is not attempted). All are additive if pursued, but each needs its own construction path and dedup story. **Related call-site sharp edge (by-design):** because the sugar emits *both* `Foo(NSUrl)` and `Foo(string)` (two unrelated reference types), a call written as `Foo(null)` with an untyped `null` literal is CS0121-ambiguous — the caller must cast (`Foo((string?)null)`) or pass a typed value; a real argument never trips it. This is the same BCL-idiomatic string/typed-value overload shape (`Path.Combine`, `XmlWriter.WriteAttributeString`) and is not "fixed" by dropping an overload. **Trigger:** a consumer names one of these shapes, or a session generalizing convenience overloads past the scalar-URL case.

</details>

<a id="placeholder-null-nullable-literal-overload-ambiguity-u-008-residual"></a>

## `Placeholder(null)` nullable-literal overload ambiguity (U-008 residual)

The placeholder-default *recovery* overload shipped (additive; also the U-004 `ApplePayConfiguration` fix). Separately, calling an overloaded member with a bare `null` literal — where the argument is ambiguous between e.g. an `IPlaceholder?` overload and a `UIImage?` overload — fails C# overload resolution at the call site. Resolving it means emitting a disambiguating overload or renaming a member: a **consumer-visible surface change**, not additive. **Trigger:** a consumer hits the ambiguity, or a session reworking optional-overload emission.

<a id="api-surface-md-additive-convenience-overloads-u-009-residual"></a>

## `api-surface.md` additive convenience overloads (U-009 residual)

The auto-generated `{Module}.api-surface.md` shipped, and residuals (a) and (b) are now closed: the manifest — and so the doc — records **properties and subscripts** alongside methods and free functions, and the doc is packed as the binding package's `PackageReadmeFile`, so the shipped README *is* the machine-derived member list. A load-time reconciliation check now fails the generator if any manifest entry names a member the emitted C# does not contain, which makes a *phantom* entry impossible by construction. What remains is the opposite direction: additive **convenience** overloads emitted into the C# but not recorded — the `string`→`NSUrl` URL sugar and the enum-case `Int` forwarder. (The `nint` indexer convenience overload is now recorded, so subscripts are complete.) The reconciler cannot catch these: an unrecorded emitted member is not a lie, only an omission, so the doc lists the primary member (`DescribeURL(NSUrl)`) but not its sugar sibling (`DescribeURL(string)`). The underlying member stays documented, so this is a completeness refinement rather than a member gap; closing it means a manifest write at each convenience-emission site. **Trigger:** a session itemizes every additive convenience overload.

<a id="recovered-placeholder-default-member-is-reported-as-skipped-u-004-u-008-residual"></a>

## Recovered placeholder-default member is reported as skipped (U-004/U-008 residual)

When a constructor/method's full signature is rejected for a trailing-defaulted `AnyType` placeholder and a truncated overload is recovered, the completeness/skip report still records the member under `SkipReason.UnsupportedSignature` (the skip is recorded before recovery runs; only the loud drop *comment* is suppressed on success). So the report counts a member that ships a usable truncated surface as fully unsupported. The emitted C# and — after the api-surface fix — `api-surface.md` are correct; only the internal completeness tally under-counts. A faithful "recovered/degraded" member status is a broader `ReportCollector` change (a third state between emitted and skipped) rather than a flag flip. **Trigger:** a completeness-report accuracy pass, or a consumer relies on the skip tally to gauge coverage.

<a id="a-returned-dictionary-cannot-be-handed-straight-to-a-dictionary-parameter-documented-not-fixed"></a>

## A returned dictionary cannot be handed straight to a dictionary parameter (documented, not fixed)

**Revisit when:** an owner-authorized dictionary-surface change that settles the mutation semantics, or a consumer report where the copy is not an acceptable workaround (e.g. a hot path where the copy cost is measured, not assumed).

<details>
<summary>Recorded evidence and disposition</summary>

A dictionary **return** projects to `IReadOnlyDictionary<K,V>` while a dictionary **parameter** accepts `IDictionary<K,V>` (`DictionaryProjection.cs:40` for the parameter type), so `b.Take(a.Get())` does not compile and the consumer must copy: `new Dictionary<K,V>(a.Get())`. **Checked empirically first, because a comment at `DictionaryProjection.cs:384-387` implies the runtime object might already satisfy both interfaces — it does not.** `GetReturnPlan` (`:231-247`) always appends an `.AsProjected(...)` call, and `BuildAsProjected` (`:293-306`) falls through to the identity `".AsProjected(v => v)"` rather than returning the container bare, so *every* top-level dictionary return is wrapped. The wrappers are `SwiftDictionaryValueProjection` and `SwiftDictionaryProjection` (`src/Swift.Runtime/src/Swift/SwiftDictionaryProjection.cs:19`, `:76`) — both `internal sealed` and both implementing `IReadOnlyDictionary<,>` **only**, so neither the cast nor a consumer-side subclass is available. Pinned at runtime by `DictionaryAnyTests.TestReturnedDictionaryIsNotAnIDictionary` (asserts the cast is unavailable) and `…CopyFeedsBackIntoSwift` (asserts the copy composes), so a future change that makes the direct hand-off work will turn the first test red rather than passing silently. Closing it for real means a **public-signature change** — widening returns to a mutable interface, or having the projections implement `IDictionary<,>` — which is an api-manifest contract change and needs its own mutation-semantics decision (writes to a projected view have nowhere to go: the Swift dictionary was copied out). Not attempted here. **Trigger:** an owner-authorized dictionary-surface change that settles the mutation semantics, or a consumer report where the copy is not an acceptable workaround (e.g. a hot path where the copy cost is measured, not assumed).

</details>

<a id="a-borrowing-parameter-reaching-a-generic-slot-is-handed-over-as-a-move-so-the-c-value-is-consumed"></a>

## A `borrowing` parameter reaching a generic slot is handed over as a move, so the C# value is consumed

Swift's `borrowing` says the callee does not take ownership, but a parameter arriving through a generic slot has no borrowed representation to be handed in, so the value is moved out of C# and the caller's variable is consumed even though the Swift signature borrows. This follows from how generic slots transfer values rather than from an oversight, but it is consumer-visible: code written against the Swift declaration will expect its value to survive the call and find it consumed. **This belongs on the consumer-facing limitations page as much as here** — the wiki's Known Limitations, which lives in a separate repo the owner updates. **Fix shape:** a borrowed representation for generic slots, which is a design question rather than a patch. **Trigger:** a consumer reports a value unexpectedly consumed by a `borrowing` parameter, or generic-slot transfer is redesigned for another reason.
