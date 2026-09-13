# Generics and concrete specialization — reference notes

Searchable findings to consult when working in this area. Nothing here is queued or newly authorized.
[How to use and maintain these notes](README.md). Recorded evidence and counts describe their original investigation; recheck against current source before acting.

<a id="generic-direct-lane-class-allocating-initializers-pass-the-metatype-in-a-normal-register-not-swiftself"></a>

## Generic direct-lane class allocating initializers pass the metatype in a normal register, not `swiftself`

**Revisit when:** a generic class initializer emitted on the direct lane returns a wrong-typed or null instance in BindingTests or a consumer report, or the foreign-outer nested-type gate is opened for another reason.

<details>
<summary>Recorded evidence and disposition</summary>

2026-09 wave (s8). A class allocating `init` is `@convention(method)` over `@thick Self.Type`, so the metatype must arrive in the `swiftself` register (x20); the wave fixed that for non-generic classes (`HandleAllocatingInitMetatypeSelf` in `MethodSignature.cs`), the first members to exercise the path being the direct-lane closure constructors it un-skipped. A *generic* class's allocating initializer on the direct lane still passes its trailing `IntPtr` metatype in a normal argument register, because the predicate that routes the metatype into `swiftself` excludes generic parents outright on the assumption that a generic class's allocating init always reaches a wrapper that threads the metatype itself. **Narrowed by measurement (2026-09-10):** that assumption now holds for all but one shape. The wrapper widening moved the generic-class population onto the `@_cdecl` / generic static-dispatch route, and the corpus retains exactly **one** generic class allocating initializer on the direct `CallConvSwift` lane — the one whose constructor parameter is a type nested inside a *different* generic host. The generic-dispatch wrapper gate refuses that "foreign outer" shape and the handler's fallback keeps the existing direct path, so the declaration is emitted with a bare `IntPtr` metatype. It is already flagged `SB0001` on the public surface and its only runtime exerciser is skipped as a durable regression marker, so nothing green depends on it. Still left deliberately: the fix either widens the metatype-self predicate to generic parents (changing the constructor P/Invoke signature contract) or opens the foreign-outer wrapper gate, and both overlap constructor work in other streams. The wrapper lane is unaffected — the shim takes the metatype explicitly. **Trigger:** a generic class initializer emitted on the direct lane returns a wrong-typed or null instance in BindingTests or a consumer report, or the foreign-outer nested-type gate is opened for another reason.

</details>

<a id="csm-methods-returning-a-non-frozen-supplement-owned-struct-cryptokit-nist-ecdsa"></a>

## CSM methods returning a non-frozen supplement-owned struct (CryptoKit NIST ECDSA)

P256/P384/P521 `signature(for:)` / `isValidSignature(_:for:)` need a closed CSM overload whose return is the supplement-owned non-frozen `ECDSASignature` — requires indirect-result `@_cdecl` return marshalling for supplement-owned non-frozen returns. Today the members are absent and the packages-repo ECDSA test (Test 35) is skipped with the reason encoded in the skip string. **Trigger:** a consumer needs NIST ECDSA sign/verify, or the next CSM return-marshalling session — un-skip the packages test when landed.

<a id="constrained-generic-pwt-plumbing-for-non-accessor-p-invokes"></a>

## Constrained-generic PWT plumbing for non-accessor P/Invokes

`EnumHandler.RawRepresentable.cs:146,254` and `OperatorHandler.cs:453,481` still pass bare `GetMetadataArgumentList()`. Not triggered by any current validation library — leave alone until a repro surfaces.

<a id="parent-baseline-conformance-on-a-non-projectable-protocol-truncates-the-p-invoke-abi-instead-of-skipping-the-m"></a>

## Parent-baseline conformance on a non-projectable protocol truncates the P/Invoke ABI instead of skipping the member

**Revisit when:** a validation library or consumer surfaces a generic type constrained on `Error`/a PAT whose members are reachable — or the next session that touches constrained-generic PWT plumbing, which should take (b) and retire both entries together.

<details>
<summary>Recorded evidence and disposition</summary>

A generic parent constrained on a protocol that fails `MethodValidationGates.IsProtocolAvailableForConstraint` — i.e. a well-known runtime protocol (`Swift.Error`), a stdlib marker, a PAT, or a Self-requirement protocol — emits members whose P/Invoke **omits the required witness-table parameter**, rather than rejecting the member. Swift's ABI for `<T: P>` passes metadata *and* the conformance witness table, so the callee reads the following argument (`metatype`) where it expects the PWT and dereferences it: **SIGSEGV inside `libswiftCore.dylib`**, not a compile error. Mechanism: `PInvokeEmitter.cs:901` sets `admitDynamicPwt = UsesCdeclConstructorOnGenericParent(env)`, which requires `UsesCdeclWrapper`; a direct-CallConvSwift member (e.g. an allocating init, symbol suffix `cfC`) has no `@_cdecl` wrapper, so `admit` is false for such a conformance and the loop `continue`s — **dropping the parameter silently**. The member gate does not compensate: `MemberEmissionValidator.cs:445-451` documents that parent-baseline conformances are "ALWAYS skipped here — supported OR unsupported", delegating to the type-level where clause + CSM, which only covers *projectable* protocols. Verified repro (since removed as out-of-scope scaffolding): `public class ErrorCarrier<Failure: Error>` emits `PInvoke_init_…(SwiftString.Buffer label, IntPtr TFailureMetadata, IntPtr metatype)` — no `TFailureErrorPWT` — while the working constrained generic `BufferModeDescribablePair<K: Describable, V: Describable>` correctly emits `…, IntPtr KMetadata, IntPtr VMetadata, IntPtr KDescribablePWT, IntPtr VDescribablePWT, IntPtr metatype`. Tellingly the *metadata accessor* on the same type DOES thread the PWT (`PInvoke_getMetadata(request, tfailureMetadata, tfailureErrorPWT)` via the descriptor path), so the emitter has the conformance in hand on one path and drops it on the other. Pre-existing and independent of the C# qualification work: `IsProtocolAvailableForConstraint` consults `IsWellKnownRuntimeProtocol`, never `ProtocolDescriptorSymbol`, so `Swift.Error` was inadmissible here both before and after that field was populated. **Symptom:** a runtime SIGSEGV in `libswiftCore.dylib` on first use of a generic type constrained on `Error` (or any PAT/marker/Self-requirement protocol), with a generated P/Invoke that has metadata params but no `…PWT` param. **Fix shape (two options):** (a) *fail closed* — when a required conformance cannot be threaded into the signature, skip the member with `SkipReason.GenericProtocolConstraint` rather than emit a truncated signature; cheapest and matches repo policy, but needs a corpus blast-radius measurement first, since it will drop members that today emit-and-crash; or (b) *thread it* — extend the dynamic-PWT path (`MethodMarshalPlanBuilder.TryGetDynamicPwtCallExpression`, which already resolves exactly this case via `SwiftConformance.GetWitnessTableOrThrow`) to the direct-CallConvSwift member path by relaxing `UsesCdeclConstructorOnGenericParent`. Option (b) is the real fix and subsumes the sibling entry above. **Trigger to revisit:** a validation library or consumer surfaces a generic type constrained on `Error`/a PAT whose members are reachable — or the next session that touches constrained-generic PWT plumbing, which should take (b) and retire both entries together.

</details>

<a id="wrapper-helper-path-dynamic-pwt-resolution"></a>

## Wrapper-helper path dynamic PWT resolution

Swift wrapper side still fail-closed for Self-requirement / associated-type protocols. Not triggered by any current validation library.

<a id="cs0305-generic-argument-shape-mismatch-in-csm-emitted-bound-generics"></a>

## CS0305 generic-argument shape mismatch in CSM-emitted bound generics

Surfaced by Commit C's per-library probe in the Session 6 validation sweep. A small number of CSM-emitted call sites pass the wrong number of type arguments to a closed bound generic (e.g. emit `Foo<A>` against a `Foo<X, Y>` declaration, or the reverse). Distinct from the multi-constraint intersection bug Commit C addresses — the conformer set is correct here, the projection of the closed type onto the C# call site is wrong. Root cause unknown; likely a missing arity check in `BoundGenericsHandler` or a CSM emitter site that flattens a tuple-shape conformer's associated types into the wrong arg list. **Trigger to revisit:** when the remaining post-Commit-D validate reds are inspected.

<a id="cs0234-missing-foundation-binding-referenced-by-csm-extension"></a>

## CS0234 missing Foundation binding referenced by CSM extension

Surfaced by Commit C's per-library probe. CSM extensions in at least one validation library reference `Foundation.<TypeName>` where `<TypeName>` is not present in the Foundation binding's emitted surface. Either Foundation's binding is missing the type (TypeDatabase gap or member-emission suppression) or the CSM extension is over-eager about projecting a Foundation-typed associated type. Distinct from the engine pollution Commit C closes — this is downstream of conformer selection. **Trigger to revisit:** when the remaining post-Commit-D validate reds are inspected.

<a id="cs0246-unresolved-self-projection-on-csm-extension-method"></a>

## CS0246 unresolved `Self` projection on CSM extension method

Surfaced by Commit C's per-library probe. A CSM extension method signature emitted by the engine references the bare identifier `Self` (not `T` or a closed type), which the C# compiler cannot resolve outside an interface/abstract class body. Almost certainly a generator omission: the method's Self-typed return or parameter should have been substituted with the closed conformer type at CSM specialization time. Distinct from the multi-constraint intersection bug. **Trigger to revisit:** when the remaining post-Commit-D validate reds are inspected.

<a id="cs0315-value-type-conformer-fails-iswiftobject-constraint-on-swiftmarshal-getswifttypesize-t"></a>

## CS0315 value-type conformer fails `ISwiftObject` constraint on `SwiftMarshal.GetSwiftTypeSize<T>`

Surfaced by Commit C's per-library probe. CSM-emitted call sites use `SwiftMarshal.GetSwiftTypeSize<T>()` (constrained `where T : ISwiftObject`) with a value-type conformer (frozen struct) that doesn't inherit `ISwiftObject` — only the `ClassWithOpaquePayload` / `Class` projection kinds do per `constraints.md` (Type Marshalling Labels). Likely fix: route value-type conformers through a separate size-discovery path (TypeMetadata-rooted, no `ISwiftObject` requirement) at the CSM emitter call site. Distinct from the engine pollution Commit C closes. **Trigger to revisit:** when the remaining post-Commit-D validate reds are inspected.

<a id="available-floor-propagation-gap-from-csm-parent-into-per-conformer-extensions"></a>

## `@available` floor propagation gap from CSM parent into per-conformer extensions

Surfaced by Commit C's per-library probe. A CSM extension class for a conformer with an iOS-N floor emits without an `[SupportedOSPlatform("iosN.0")]` attribute on the extension class — the per-conformer specialized method then fails to link against the platform-floor-gated symbol on older deployment targets. The `ConcreteConformer.AvailabilityAnnotations` field IS populated by `IndexTypeConformances.CollectAvailability` (walks `ParentDecl` chain), but a propagation step from the conformer through the extension class header is missing. Distinct from the CryptoKit fix (which propagates availability onto the wrapper @available floor, not the C# extension attribute). **Trigger to revisit:** when the remaining post-Commit-D validate reds are inspected.

<a id="csm-emitted-call-site-drops-external-argument-label-emits-name-arg"></a>

## CSM-emitted call site drops external argument label, emits `name(: arg)`

Surfaced by GRDB's Commit C validate run (132 wrapper errors of shape `error: expected argument label before colon`). The CSM emitter for stdlib Sequence/Collection methods (`dropFirst(_:)`, `prefix(_:)`, etc.) on closed bound generics (e.g. `GRDB.RecordCursor<GRDB.ColumnInfo>`) emits `__self.dropFirst(: _value)` with an empty external label before the colon, instead of the bareword call `__self.dropFirst(_value)`. Root cause: parameter-label rendering in the per-conformer Swift wrapper call site treats an unnamed external label (`_`) as "emit empty label + `:`" instead of "omit label and colon entirely". Likely localized to `ConcreteProtocolSpecializationEmitter` Swift call-site emission for stdlib protocol extension methods. Distinct from the multi-constraint intersection bug Commit C addresses — the conformer set is correct here, the Swift call syntax is wrong. **Trigger to revisit:** when the remaining post-Commit-D validate reds are inspected.

<a id="csm-emitted-fastdatabasevaluecursor-t-projects-conformer-types-that-don-t-carry-the-required-interfaces"></a>

## CSM-emitted `FastDatabaseValueCursor<T>` projects conformer types that don't carry the required interfaces

Surfaced by GRDB's Commit C validate run (CS0311 errors). The CSM emitter projects `GRDB.DatabaseDateComponents` as a `TValue` type argument to a `where TValue : IStatementColumnConvertible` constrained generic, but `DatabaseDateComponents` is a struct that does not declare `IStatementColumnConvertible` in its emitted interface list. Either the conformer-protocol pairing is incomplete (the conformance exists in Swift but isn't projected onto the C# class header), or the engine should reject this conformer for this generic. Adjacent in shape to the CS0315 `Guid → ISwiftObject` entry above but distinct: that one is a runtime-interface mismatch on `SwiftMarshal.GetSwiftTypeSize<T>`; this one is a protocol-conformance projection gap. **Trigger to revisit:** when the remaining post-Commit-D validate reds are inspected.

<a id="csm-per-method-where-clause-filter-protocol-composition-t-p-q-is-treated-as-a-single-opaque-target"></a>

## CSM per-method where-clause filter: protocol-composition `T : P & Q` is treated as a single opaque target

**Revisit when:** first validation library OR BindingTests fixture where a CSM-eligible method carries `where T : P & Q` and the dropped conformer demonstrably satisfies both. **Remediation shape:** in `ParseMethodLevelConstraints`, after extracting the target, `target.Split('&').Select(s => s.Trim())` and emit one `(Conformance, sub-target)` entry per protocol so each is verified independently.

<details>
<summary>Recorded evidence and disposition</summary>

`ConcreteSpecializationEngine.ParseMethodLevelConstraints` (added by the CSM defensive conformer-constraint filter) splits constraint clauses only on `,` and emits one entry per clause; when Swift's ABI signature serializes a per-method clause as `τ_0_0 : ModuleA.P & ModuleB.Q`, the engine stores `"ModuleA.P & ModuleB.Q"` as a single target string. `parentLevelNames.Contains(target)` then never matches a single declared protocol and `VerifyHintAgainstAbi` returns Disproved against either standalone protocol, so legal pairings where the conformer satisfies BOTH `P` and `Q` get false-rejected (silent undercount; `csmConformerRejections` row will reference the composite string). Existing `GenericSignatureParser.ParseConstraint` has the same single-target behavior, so the engine is internally consistent — but the new filter is the first call site that needs to verify the conformance set, not just store it. **Symptom:** missing CSM extension method for a generic parent whose per-method `where` clause uses `&` composition, with a `csmConformerRejections` JSON row whose `MissingConstraint` field contains `" & "`. **Trigger to revisit:** first validation library OR BindingTests fixture where a CSM-eligible method carries `where T : P & Q` and the dropped conformer demonstrably satisfies both. **Remediation shape:** in `ParseMethodLevelConstraints`, after extracting the target, `target.Split('&').Select(s => s.Trim())` and emit one `(Conformance, sub-target)` entry per protocol so each is verified independently.

</details>

<a id="csm-per-method-sametype-filter-no-sugar-canonicalization-between-data-and-swift-optional-foundation-data"></a>

## CSM per-method SameType filter: no sugar canonicalization between `Data?` and `Swift.Optional<Foundation.Data>`

`ConcreteSpecializationEngine.ParentTupleSatisfiesMethodConstraints` SameType branch compares `entry.Conformer.SwiftQualifiedName` (and `SwiftLiteral` fallback) against the RawGenericSig RHS via `string.Equals(..., StringComparison.Ordinal)`. ABI-sourced conformers always have `SwiftLiteral = null`, so when the RawGenericSig prints sugar (`Data?`, `[Data]`, `[K:V]`) and the conformer prints canonical (`Swift.Optional<Foundation.Data>`, `Swift.Array<Foundation.Data>`, `Swift.Dictionary<…>`) — or vice versa — the SameType clause false-rejects a legal pairing (silent undercount). Validate emit count after C went UP (+188 emitted, -265 skipped) so no current bite on the ~62-library SDK sweep, but the comparison is fragile. **Symptom:** post-release skip-metrics show a drop in `total_emitted_members` AND a `csmConformerRejections` row whose `MissingConstraint` field is `== <sugared form>` for a conformer whose canonical form matches. **Trigger to revisit:** first surfaced false-reject where the conformer demonstrably satisfies the SameType clause but is dropped, OR a measurable emit-count regression attributable to a SameType clause. **Remediation shape:** add a `CanonicalizeTypeRefForComparison(string raw, TypeDatabase db)` helper that normalizes `X?` ↔ `Swift.Optional<X>`, `[X]` ↔ `Swift.Array<X>`, `[K:V]` ↔ `Swift.Dictionary<K,V>` (the three sugar forms Swift prints) and call it on both sides before `string.Equals`.

<a id="associated-type-constraints-with-unavailable-concrete-path-facts"></a>

## Associated-type constraints with unavailable concrete path facts

Dependent-member constraints now preserve the complete path through discovery, parent filters, coupling and final sync/async specialization checks. Known child witnesses are resolved through indexed typealiases/conformances; `Child.Element` never substitutes the root `Element`. Explicit full-path stdlib hints cover selected `SubSequence.Element` shapes. Open generic child substitution and unavailable or ambiguous multi-hop facts remain deferred to compiler verification/recovery, rather than being guessed or predictively refused. The retained concrete-child specialization inventory and native positive/inverse controls qualify the tested paths; they do not establish support for open generic substitution or every third-party generic chain. **Trigger:** a corpus or consumer shape demonstrates a missing concrete path fact with measurable recovery cost or lost retained workflow; obtain its compiler/artifact evidence before extending fact indexing or adding generic substitution.

<a id="cross-module-cross-assembly-conformer-enumeration"></a>

## Cross-module / cross-assembly conformer enumeration

`ConcreteSpecializationEngine.GetConformers` is intra-module + hint-scoped today. The AppEntity keypath-keyed init machinery + `AppShortcutParameterPresentation` higher-kinded `ParameterKeyPath` close conformers only from the bound module; Apple-shipped sibling-framework conformers and consumer-app-defined conformers in separate assemblies are not visible. Acceptable for v1 (consumer ships AppEntity types in the same library as their generated AppIntents bindings). Dedicated session: changes to `ConcreteSpecializationEngine`, TypeDatabase dependency-closure aggregation, `Program.cs` engine construction. **Trigger:** a real consumer asks for a conformer declared in a different module than the protocol — the enumeration is module-local by construction, so cross-module demand is what justifies the closure work.

<a id="appintents-available-unavailable-unavailable-facet-confirmation-against-a-real-appintents-regen"></a>

## AppIntents `@available(*,unavailable)` unavailable-facet confirmation against a real AppIntents regen

Reference-typed `_IntentValue` conformers reach the CSM + `_SBW_CI_` init paths and originally produced 30 wrapper-Swift `swiftc` failures across four facets: `_const` params (8×), `@available(*,unavailable)` (4×), unsatisfied constrained-extension `where` (14+2×), `_SBW_CI_` unconditional-conformance guard (2×). A unified `ConstructorAdmissibility` predicate (cheap-filter + parent-generic extension-constraint check), now consumed by the CSM, `_SBW_CI_`, and the C# `ConstructorHandler.Emit` paths, closes three of the four facets and ships a BindingTests fixture for each. The fourth — `@available(*,unavailable)` — ships only as a unit-tested defense-in-depth `IsModuleInternal` reject; `swiftc` strips unavailable members from a from-source `.abi.json`, so the in-repo fixture cannot reproduce it. Remaining gate to enabling AppIntents in `validation-libraries.json` as a runtime-tested cell (it is already a Tier 1 compile-gate there — `"name": "AppIntents", "tier": 1`): confirm the unavailable facet against a real AppIntents regen. **Trigger:** AppIntents is promoted to a runtime-tested validation cell, or a consumer reports an unavailable-marked member behaving unexpectedly.

<a id="underscoreprotocolsynthesizer-allowlist-expansion-latent"></a>

## `UnderscoreProtocolSynthesizer` allowlist expansion (latent)

Categorical audit across the iOS 26.2 simulator SDK lists ~108 underscored public protocols (re-run: `grep -lE 'public.*protocol _' <interfaces>` then per-file extraction). Of those, AppIntents declares 10 (`_IntentValue`, `_IntentValueRepresentable`, `_SequenceIntentValue`, `_SystemIntentValue`, `_ParameterSummarySwitchCase`, `_SupportsAppDependencies`, `_AppShortcutsContentMarker`, `_AppShortcutsContentEmitterMarker`, `_LimitedAvailabilityAppShortcutsContentMarker`, `_ViewBridgeLoader`); 2 are allowlisted today. Foundation declares 6 (`_BridgedNSError`, `_BridgedStoredNSError`, `_ObjectiveCBridgeableError`, `_ErrorCodeProtocol`, `_KeyValueCodingAndObserving`, `_FormatSpecifiable`) — the first three are load-bearing for error bridging if an emitter use case surfaces. The rest (Swift stdlib builtin-literal infrastructure, SwiftUI/SwiftUICore view-tree internals, CoreMedia init trampolines, RealityFoundation private services) are not load-bearing for current bindings. Trigger to expand the allowlist: an `IndeterminatePwtShape` referencing one of the unallowlisted names lands during a real consumer regen.

<a id="musiclibrarysectionedrequest-sectiontype-musicitemtype-multi-generic-parent-csm-route-c"></a>

## `MusicLibrarySectionedRequest<SectionType, MusicItemType>` multi-generic-parent CSM + Route C

0/17 surface members emit. Generator emits 64 empty cartesian `MusicLibrarySectionedRequest{Section}{Item}CsmExtensions` classes plus 17 tombstone comments (`filterItems` ×8, `sortItems` ×1, `filterSections` ×7, `sortSections` ×1, `response` ×1) reasoned "protocol with associated types used as constraint". Root cause: per-method `where SectionType : MusicLibraryRequestable` clauses on a two-PAT-generic-parent type aren't handled by current CSM filter machinery, and Route C gates on single-generic-parent at `RouteCSortShapeEligibility.cs:72`. Sibling type `MusicLibraryRequest<T>` (single PAT-generic-parent) emits all 11 surface members. **Trigger to revisit:** a consumer asks for sectioned-request access. The single-generic-parent path covers the vast majority of MusicKit workflows.

<a id="csm-methodgenericbridgeemitter-result-pointer-alloc-free-antipattern"></a>

## CSM `MethodGenericBridgeEmitter` result-pointer alloc+free antipattern

`MethodGenericBridgeEmitter.cs` lines 731/741/834-836/861 still emit `Marshal.AllocHGlobal` + `try/finally { Marshal.FreeHGlobal(resultPtr) }` around `SwiftMarshal.MarshalFromSwift<T>(resultPtr)` — the same shape that was a double-free for non-frozen-struct conformers in `ConcreteProtocolSpecializationEmitter` until the per-`NewFromPayload`-contract discriminator landed (direct-wrap / copy-out / pure value). No current method-generic-bridge test exercises a wrap-and-dispose round trip, so the latent double-free is unreached. **Fix shape:** mirror the `ConcreteProtocolSpecializationEmitter.cs:1402-1440` discrimination at the bridge emission site. **Trigger to revisit:** the next session that touches CSM result-pointer marshalling, or the first method-generic-bridge fixture that exercises non-frozen-struct return wrapping.

<a id="csm-class-conformer-returnsgenericparam-carrier-wrapping"></a>

## CSM class-conformer `returnsGenericParam` carrier-wrapping

Class-conformer `returnsGenericParam` returns route through `MarshalFromSwift<Class>(resultPtr)`: Swift writes the class pointer into `resultPtr` via `initializeMemory`, but the C# `NewFromPayload(handle)` wraps the *carrier* address rather than the loaded class pointer. Pre-existing, unchanged by the 0.11.0 CSM cleanup pass; no current BindingTests fixture exercises a class-conformer `returnsGenericParam` round-trip. **Fix shape:** load the pointee class-pointer from the carrier before invoking `NewFromPayload`, gated to class-conformer rows on the CSM result branch. **Trigger to revisit:** first class-conformer `returnsGenericParam` BindingTests fixture or a consumer surfacing wrong handle wrapping.

<a id="value-type-availability-floor-not-merged-in-appentity-keypath-singleton-factory-emitters"></a>

## Value-type availability floor not merged in AppEntity KeyPath singleton + factory emitters

`AppEntityKeyPathSingletonEmitter` and `ConformerKeyPathInitFactoryEmitter` merge the conformer + dep-class availability floors but **not** the property's *value*-type floor. A property whose value type is introduced on a later OS than both the conformer and the dep class would emit an under-annotated (and silently stripped) Swift trampoline + C# factory. Not triggered by current fixtures (all value types are `String` / `Int`). **Fix shape:** coordinated availability-floor merge across both emitters plus a gated-value-type BindingTests fixture. **Trigger to revisit:** first AppEntity property whose value type carries a later platform floor than its owning conformer.

<a id="withdrawn-type-skip-oracle-umbrella-remap-is-single-hop-the-csm-string-parse-fallback-misses-m-outer-t-inner"></a>

## Withdrawn-type skip oracle: umbrella remap is single-hop; the CSM string-parse fallback misses `M.Outer<T>.Inner`

**Revisit when:** a second `compileImportModule` umbrella relationship (multi-source or chained) is added to `apple-frameworks.json`, **or** a CSM hint spells a conformer as `M.Outer<T>.Inner` whose inner type is withdrawn (i.e. a SWIFTBIND113 CS0234 traced to a string-only-named nested-generic conformer).

<details>
<summary>Recorded evidence and disposition</summary>

The withdrawal-consistency oracle that stops an emitter from naming a skipped type it can't declare (`ValidationRuleSet.IsTypeSkippedWithUmbrellaRemap` + `ConcreteProtocolSpecializationEmitter.ConformerReferencesWithdrawnType`) has two residual gaps, both pure latents. (1) **Single-hop umbrella remap:** `IsTypeSkippedWithUmbrellaRemap` derives the umbrella module from the first-dot prefix and re-attaches source modules via `AppleFrameworkRegistry.GetCompileImportSourceModules` **once** — it resolves `RealityKit.X` ↔ withdrawn `RealityFoundation.X` (the one shipped `compileImportModule` relationship) but not a *chained* re-export (umbrella A → B → C) nor a multi-source umbrella whose skip was recorded under a non-first source module. There is exactly one such relationship in `apple-frameworks.json` today, so no bite. (2) **CSM `>.` string-parse fallback:** `ConformerReferencesWithdrawnType`'s TypeSpec path now walks the full `InnerType` chain and recurses generics at every level (the r2 InnerType fix), so a withdrawn nested-after-generic-outer inner IS caught when the conformer carries a `SwiftType`. The string-only fallback (`SwiftType == null`, parsed from `SwiftQualifiedName`) does not split a `M.Outer<T>.Inner` spelling (generic outer, then `>.`-nested inner) into the `M.Outer.Inner` skip-key form, so a conformer named purely by that string whose inner is withdrawn would slip. No shipped hint spells `>.`, so unreached. **Both fail safe today** — the whole-binding SWIFTBIND113 verification catches any resulting CS0234 and fail-closes the module (a generate-time stop, never a silent bad binding). **Fix shape:** (1) iterate `GetCompileImportSourceModules` transitively / over all sources; (2) canonicalize the `>.`-generic-nested string into the generics-stripped skip-key before the fallback probe. **Trigger to revisit:** a second `compileImportModule` umbrella relationship (multi-source or chained) is added to `apple-frameworks.json`, **or** a CSM hint spells a conformer as `M.Outer<T>.Inner` whose inner type is withdrawn (i.e. a SWIFTBIND113 CS0234 traced to a string-only-named nested-generic conformer).

</details>

<a id="generic-callback-holder-properties-are-dropped-for-a-where-t-iswiftobject-constraint-the-emitter-never-emitted"></a>

## Generic callback-holder properties are dropped for a `where T : ISwiftObject` constraint the emitter never emitted

**Revisit when:** a consumer reports a second dropped-property shape whose emitted generic carries no `where` clause on the offending parameter, **or** the next session that takes on `Void`/tuple type-argument projection.

<details>
<summary>Recorded evidence and disposition</summary>

A fluent-builder library's `Delegate<Input, Output>` callback properties (`onSuccess`/`onFailure`/`onProgress`) all drop with `UnsatisfiedGenericConstraint`, leaving the marquee builder API with no way to observe an outcome. **Triage result: neither of the two arms this was originally attributed to is reached.** The drop happens earlier, in `MemberGateEvaluator` (property arm `:529`, method arm `:451`) via `BoundGenericsHandler.HasNonSwiftObjectGenericArg` — `BoundGenericsHandler.TryGetFirstUnsatisfiedConstraint`'s parent-param arm and its fail-closed scopeless arm never run for this shape. Repro (scratch module, four-way classification): given `public class Notifier<Input: AnyObject, Output>`, the properties typed `Notifier<Value, Void>` on a parent that declares `Value: AnyObject`, on a conditional extension `where Value: AnyObject`, on a nested type closing over the outer parameter, **and on a fully concrete holder typed `Notifier<Token, Void>` where `Token` is a plain final class**, are ALL dropped — while the same concrete holder typed `Notifier<Token, Token>` emits fine. The axis is therefore `Void` (the empty tuple) in type-argument position, not any generic-parameter-scope question. The gate's stated premise is that "all other emitted generics have `where T : ISwiftObject`, making ValueTuple args a CS0311 error" — but the emitted class for this shape is `public partial class Notifier<TInput, TOutput> : ISwiftObject, IDisposable` **with no `where` clause at all**, so the CS0311 the gate is preventing cannot occur and the member is dropped for a compile error that would never happen. **Genuinely over-broad axis:** `HasNonSwiftObjectGenericArg` is a type-shape predicate that never consults whether the *specific* emitted generic actually carries an `ISwiftObject` constraint on the position being checked. **Why it is not a quick narrowing:** admitting the member means projecting `Void` as a real C# type argument, which requires `TypeMetadata` resolution for Swift's empty tuple on the `SwiftClassHandle<Notifier<Token, ValueTuple>>` path and a `Void`-aware arm on every `IProjectionVisitor` implementation (the full parity ritual in `constraints.md` § Projection parity pattern) — L-size, and the runtime marshalling question is genuinely open, not just the gate. **Fix shape:** thread the emitted generic's actual per-parameter constraint set (the same data `GenericTypeEmitter.GetWhereClause` computes) into `HasNonSwiftObjectGenericArg` so the check asks "does THIS parameter carry `ISwiftObject`?" instead of assuming it always does; then, separately, decide whether a `Void` type argument is marshallable. **Trigger to revisit:** a consumer reports a second dropped-property shape whose emitted generic carries no `where` clause on the offending parameter, **or** the next session that takes on `Void`/tuple type-argument projection.

</details>

<a id="adopt-vs-copy-decisions-outside-the-shared-payload-seam"></a>

## Adopt-vs-copy decisions outside the shared payload seam

Concrete specialization, operator, method-generic bridge and async result planning retain distinct emission decisions; their ownership contracts are not qualified by the collection repair. The witness-backed collection result is an owned initialized slot, not a borrowed buffer: it now uses `SwiftMarshal.MarshalMovedValueFromSlot`, tracks raw/initialized/consumed state, destroys only a live non-POD source on failure, and frees raw allocation after transfer. Class elements use the object-pointer reader; POD Adopt results obtain independent storage. The shared helper also serves its existing callers. Focused native witness-collection controls exercise returned-value lifetime and initialized-slot failure cleanup. They do not qualify the separate bare-ISwiftObject Adopt route in SwiftArray or every generic/operator/async carrier. **Trigger:** one of those residual sites changes ownership, or a retained native specimen demonstrates a leak, premature release or invalid slot interpretation.

<a id="swiftdisposescope-tryregister-can-throw-after-a-swiftsafehandle-is-constructed-finally-frees-a-buffer-the-hand"></a>

## `SwiftDisposeScope.TryRegister` can throw after a `SwiftSafeHandle` is constructed → `finally` frees a buffer the handle also owns

2026-09 wave (s3b, CSM pre-native leak). `SwiftDisposeScope.cs:38-44` returns early with no active scope and can only throw on list-growth OOM, so the double-free is unreachable in practice; the alternative (drop the `finally`) leaks on an always-reachable path. The complete fix is an ownership-consuming marshal entry point so the buffer has exactly one owner at every statement — recorded in the helper's doc comment. **Trigger:** `TryRegister` gains a reachable throw, or the marshal entry points are next restructured.
