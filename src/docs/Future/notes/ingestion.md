# Parsing, type databases and ingestion — reference notes

Searchable findings to consult when working in this area. Nothing here is queued or newly authorized.
[How to use and maintain these notes](README.md). Recorded evidence and counts describe their original investigation; recheck against current source before acting.

The ingestion issue ledger + proven-closure quarantine (the DEGRADE plane) makes every dropped/deformed input node a structured ledger entry: a malformed ABI record (a bindable type with an empty `MangledName`, non-ObjC-rooted) is `IsIngestionQuarantined`, withheld from the TypeDatabase, and the proven-closure walk (`IngestionQuarantineClosure.Compute`) either withdraws it + every structurally/signature-reaching dependent (SWIFTBIND046, binding still ships tombstoned + reported) or fails the module closed (SWIFTBIND120) when completeness can't be proven. Landed + gated end-to-end by `BindingTests/Sources/IngestionKitchen/` (leg 1 closed graph, leg 2 SWIFTBIND119, leg 3 single-module quarantine, leg 4 cross-module dependency-protocol quarantine) and `IngestionQuarantineClosureTests`. Cross-module coverage is closed: a protocol quarantined in a **dependency** module now seeds the primary closure's reachability walk (`Compute`'s `dependencyQuarantinedNames`) and is filtered out of the `dependencyProtocols` stash before the cross-module emitters read it, so a primary construct inheriting a malformed dependency protocol withdraws through the ingestion seam instead of emitting against the malformed record by name. The ingestion-quarantine notes retain the residuals a paired Codex+Grok review surfaced; none re-opens the "ships with a live retained emission edge under `ProvenComplete`" failure for the modeled structural/signature surfaces (enum associated-value payloads and operators are now walked + unit-tested). None is queued.

The converter's import-closure gate (`graph_closure.py`) and the generator's dependency-manifest emission carry a few trigger-gated residuals surfaced by the ingestion-hardening program. None is queued; each fails safe (a missed hole or an unresolved-but-restorable reference, never a silent wrong binding).

<a id="corpus-scope-are-server-side-cli-swift-only-idiom-packages-first-class-binding-targets"></a>

## Corpus scope: are server-side / CLI / Swift-only-idiom packages first-class binding targets?

<details>
<summary>Recorded evidence and disposition</summary>

A ratification request, still unratified. The proposal declares ~28–30 of the 120-package corpus reds out of scope because the failing pattern does not generalize to the tool's real use case (an Apple-platform framework consumed from C#) — most sit in `convert_failed` precisely *because* they are not shaped like Apple-platform frameworks, which is the converter behaving correctly. The four classes: **server-side Swift** (swift-nio, swift-nio-ssl, grpc-swift, fluent-kit, jwt-kit, MongoKitten); **CLI/tooling** (swift-argument-parser — stays in the historical denominator either way); **low-level primitives with direct.NET/BCL equivalents** (swift-atomics, -collections, -algorithms, -numerics, -crypto, -certificates, -log, -system, -async-algorithms, -protobuf, combine-schedulers, swift-clocks); **Swift-only architecture patterns / sugar** (ComposableArchitecture, swift-navigation, swift-dependencies, swift-case-paths, swift-identified-collections, SwifterSwift, Factory, CombineCocoa, SwiftUIX, swift-parsing). Effect if ratified: effective green rate on the ~90 realistic targets is ~55% vs 42% raw. **Only headline-cohort membership is at stake — the *mechanisms* stay worth fixing** (Factory and CombineCocoa are in the compile-red cohort; swift-numerics / swift-clocks / swift-identified-collections are in the named-input cohort, and they recur in in-scope libraries). Bucketing is data-backed off the corpus `category`/`stars_approx` tags but is a judgment call. **Decision, not authorized. Trigger:** owner ratifies or rejects; until then quote the raw number, not the effective one.

</details>

<a id="foundation-value-type-sizes-not-stamped-in-the-xml-databases-url-16-indexpath-17-urlrequest-larger"></a>

## Foundation value-type sizes not stamped in the XML databases (`URL` 16, `IndexPath` 17, `URLRequest` larger)

2026-09 wave (s10, frozen-field layout). The parse-time `DeclaredLayout` lane sizes nested frozen fields from the module's own ABI JSON; imported Foundation records reached through `FoundationDatabase.xml` carry no size/alignment, so a frozen struct nesting a `URL`/`IndexPath`/`URLRequest` field still fails closed (`IndeterminateBufferLayout`). Stamping the sizes is a one-line change per record but bakes an Apple-internal layout into the repo that can move between OS majors — the owner decides whether to accept that maintenance contract. **Trigger:** `nuke validate` shows a frozen struct skipped for a Foundation-typed field on a library that matters, or the owner accepts version-pinned Foundation layouts.

<a id="protocol-requirement-public-visibility-heuristic-in-the-public-member-names-scan-membercollectionwalker"></a>

## Protocol-requirement public-visibility heuristic in the public-member-names scan (`MemberCollectionWalker`)

The SwiftSyntax host's public-member-names scan (`MemberCollectionWalker`) only records swiftinterface members whose line carries a `public ` / `open ` keyword, then feeds the surviving names into `PublicMemberNames`. Members of a `public protocol` are implicitly public (Swift never writes the keyword on the requirement line), so protocol property requirements are absent from the set and the negative-space `IsInternalFromPublicMemberNames` flags them `IsModuleInternal = true`. Session 4 KeyPath singleton emission for protocol-bag associated types (e.g. MusicKit `\MusicKit.LibraryAlbumFilter.id`) works around this with a use-site `allowAbstract` gate in `KeyPathBagWalker.WhyPropertyNotEmittable` (the walker KeyPath bag emission calls through) — any other consumer of `IsModuleInternal` on a protocol requirement still misclassifies. Real fix: treat requirement-shape members of a `public`/`open` declaration as implicitly public when their own line lacks an explicit access keyword. **Trigger to revisit:** the next time a downstream emitter wants `IsModuleInternal` to be truthful on protocol requirements, or whenever the use-site workaround needs to grow a second branch.

<a id="macos-conditional-foundation-overlay-typed-remaps"></a>

## macOS-conditional Foundation overlay typed remaps

`Process` (→ `NSTask`, macOS + Catalyst), `Host` (→ `NSHost`, macOS-only), `DistributedNotificationCenter` (→ `NSDistributedNotificationCenter`, macOS + Catalyst) are routed to `NSObject` in `FoundationDatabase.xml` because typed routing would silently break iOS/tvOS-targeted bindings. The Swift overlay names themselves are macOS-side, so consumers don't hit this in practice. Real fix: per-TFM XML supplements (mirroring how `apple-frameworks.json` already partitions modules). **Trigger to revisit:** a macOS-side consumer asks for typed access, or per-TFM XML routing lands for another reason.

<a id="r1-typedatabase-truth-latents-write-cascade-abi-version-root-synthesized-accessor-flag"></a>

## R1 TypeDatabase-truth latents (write-cascade / abi-version root / synthesized-accessor flag)

Three P2 latents from the 2026-06 regression audit, all value-correct today with no emission site. (a) `TypeDatabase.cs` `ApplyEmissionResult` write side keys by exact `_modules`/`_outOfModuleTypes` and doesn't consult the `_moduleAliases`/umbrella cascade the *read* side honors — unreachable (producer key == storage key), foot-gun if an umbrella/alias-qualified decl ever reaches a stamp site. (b) `SwiftABIParser.cs` `GateAbiFormatVersion` reads `json_format_version` only on the root `ABIRoot` node — a future digester stamping it elsewhere → spurious SWIFTBIND033 under `--strict-inputs`. (c) `MethodDecl.cs` `IsSynthesizedAccessor` lost its C# `required` modifier in a migration; every current site sets it correctly, but a future synthesized accessor that forgets it defaults `false` → inadvertently-public helper, no compile error. Cheap hardening (restore `required`, route the write through the read cascade, guard-test) only if already in that code. NOTE: Swift's `required init` is **not tracked anywhere** in the codebase — there is no required-init propagation bug. **Trigger:** a session is already editing one of the three sites — the hardening is only worth its regression risk as a rider, never as its own change.

<a id="r2-eof-strict-typespec-parse-grammar-latents"></a>

## R2 EOF-strict `TypeSpec.Parse` / grammar latents

A P2 family where an un-`try`/`catch`'d EOF-strict `TypeSpecParser.Parse` (or a `SwiftTypeListText` splitter edge) drops a whole decl on input the old lenient parser tolerated — none reachable on Xcode 26.x ABI, mechanism-confirmed. Most concrete lead: the `some`-vs-`any` asymmetry (`TypeSpecParser.cs` special-cases `any` as a modifier but not `some`), so a foreign-type-extension method with a `some P` parameter (`ForeignTypeExtensionEmitter.ParseParameter`, bare `try/catch → return null`) silently drops the whole method at `LogDebug`. Others: sibling `CreateTypeSpec` throw sites, `StripSwiftAttributes` not stripping `some`/`borrowing`/`__owned`, `SwiftTypeListText` arrow-guard / unfloored depth-decrement. **Fix red-first:** a subscript with a `some P` index param via SDK-direct Apple-framework mode. Input-poor, not demonstrated on a real library. **Trigger:** a decl disappears from a binding with a `TypeSpecParser`/`ForeignTypeExtensionEmitter` debug-log drop behind it, or a Swift toolchain bump changes swiftinterface spelling — the family is unreachable only against today's ABI output.

<a id="enum-case-nested-labeled-tuple-payload-loses-associated-value-usrs"></a>

## Enum-case nested-labeled-tuple payload loses associated-value USRs

The associated-value USR threading (`SwiftABIParser.cs:1922-1943`) pairs the flattened `parsedElements` against `assocValuesNode.Children`, which works for the single-nominal and flat-tuple shapes (both tested) but NOT the nested single-labeled-tuple shape `case foo(label: (A, B))`: `TypeSpecParser` hoists the label off the inner tuple and flattens it into two `parsedElements`, while the outer ABI node still has ONE child (the inner-tuple node), so only `A` is visited — paired against the tuple node (no USR) — and `B` is never reached. Consequence: a bridged-NSError associated value in that exact shape isn't USR-marked, so its skip-classification doesn't fire. Not a regression (enum-payload USR threading is new in Part A; before it, no shape was marked) and not corpus-reachable. A targeted fix would descend into the single inner-tuple node's children for the USR loop only (leaving the label-preserving `@_cdecl` flatten untouched); deferred because no real ABI sample of the shape exists to validate the fix + test against, and a blind fix can't be verified. **Trigger:** a corpus/BindingTests lib surfaces an enum `case foo(label: (BridgedNSError, X))` that mis-emits.

<a id="apple-surface-projection-residuals-absent-record-class-constraints-ios-only-index-bare-hit-precedence-pre-exis"></a>

## Apple-surface projection residuals (Absent-record class constraints, iOS-only index, bare-hit precedence, pre-existing-record bypass)

**Revisit when:** a generic method emits an uncompilable `where T : <Apple struct>`, a non-iOS target mis-projects a divergent Apple type, a bare-name Apple collision surfaces, or a SWIFTBIND113 CS0234/CS0721 traces to a pre-existing (non-synthesized) Apple record whose projected type is absent from the installed surface — route the stored-record resolution through the same surface check.

<details>
<summary>Recorded evidence and disposition</summary>

Four edges in the Microsoft.iOS surface-verification path (`TypeDatabaseExtensions.TryProjectViaAppleSurface`), all non-regressing and not corpus-reachable (a wrong constraint or shape would fail the green CS-compile gate). (a) **Absent record still satisfies class-constraint acceptance** — the skip-marked record keeps `Kind=Class | ObjCBridged` (`:710-718`), so `MethodValidationGates.TryGetClassConstraintTarget` (`:282-288`), which checks only those two, could still promote a value/static-constants Apple type to a `where T : …` constraint. Pre-existing: the no-USR path behaved identically before, and the `AbsentAppleProjection` flag is read only by member/enum-case/emitter-ingress validation, not the constraint gate. The generic-constraint absent-Apple case was deliberately left on this safe pre-existing path (phantom → CS0246/CS0721 → verify-recover withdrawal): wiring a new emission-time constraint gate is a compile-catchable failure, so the prediction-gate freeze policy routes it to verify-recover, not a new predictor. (b) **Index is Microsoft.iOS-only** (`AppleTypeSurfaceIndex.cs:217-234`) — macOS/tvOS/Catalyst generation still classifies against iOS shapes; a platform-divergent-shape Apple type could mis-project (workload-absent degrades to synthesis, so only workload-present is exposed). (c) **Bare-USR-name hit wins before the synth name** (`:552-564`) — intentional for cross-namespace enum projection (tested), but has no "prefer qualified synth over a bare-USR homonym" guard for a rare bare Class collision. (d) **Pre-existing / XML-registered records bypass surface verification** — `TryProjectViaAppleSurface` runs ONLY inside `CreateObjCBridgedTypeRecord` (`:474`/`:518`), which fires solely on the on-demand ObjC-bridge synthesis path (`:192`/`:211`) when no stored record exists. An Apple type that resolves to a pre-existing stored `TypeRecord` — parser-ingested, or an XML-registered Apple value type — takes the non-synthesizing lookup and is never surface-checked, so a stored record naming a projected C# type absent-from / wrong-kind-in the installed Microsoft.iOS surface ships without an `AbsentAppleProjection` tombstone. Fails CLOSED: the whole-binding SWIFTBIND113 C# verification build catches the resulting CS0234/CS0721/CS1061 and fail-closes the module (generate-time stop, never a silent bad binding). Curated XML value types are known-present, so no current bite. **Trigger:** a generic method emits an uncompilable `where T : <Apple struct>`, a non-iOS target mis-projects a divergent Apple type, a bare-name Apple collision surfaces, or a SWIFTBIND113 CS0234/CS0721 traces to a pre-existing (non-synthesized) Apple record whose projected type is absent from the installed surface — route the stored-record resolution through the same surface check.

</details>

<a id="appletypesurfaceindex-reflects-one-platform-reference-assembly-and-never-the-sibling-binding-packages"></a>

## `AppleTypeSurfaceIndex` reflects one platform reference assembly and never the sibling binding packages

**Revisit when:** a false-absence report on an already-covered namespace (a member withdrawn for an Apple type that a referenced sibling package really does supply) — reproduce it as the fixture before building the reference-set plumbing.

<details>
<summary>Recorded evidence and disposition</summary>

The Apple-surface absence authority is built by reflecting the installed platform reference assembly (`AppleTypeSurfaceIndex.BuildFromAssembly`), so it knows nothing about types a *sibling binding package* the consuming binding also references contributes. Current mitigation (documented in the `TryProjectViaAppleSurface` remarks): every **absence** verdict is confined to namespaces the index actually declares something in, so a miss in an uncovered namespace is not treated as evidence of absence and no public API is withdrawn on it. That makes the index an authority for the namespaces it reflects — not a complete authority: a sibling package contributing a type to an **already-covered** namespace is still misjudged as absent, and the referencing member is withdrawn. Closing it durably means reflecting the sibling assemblies into the index, which the generator cannot do today because it has no resolved assembly paths at generation time — it needs (a) a generator CLI option that accepts a resolved reference set, (b) SDK plumbing to pass the consuming project's resolved references down to the generator invocation, and (c) a restore/build-ordering guarantee that the sibling bindings exist before the dependent one generates. All three, or the index silently gains a *partial* sibling view, which is worse than the honest namespace-coverage confinement it replaces. **Trigger:** a false-absence report on an already-covered namespace (a member withdrawn for an Apple type that a referenced sibling package really does supply) — reproduce it as the fixture before building the reference-set plumbing.

</details>

<a id="registered-apple-valuetypes-get-no-value-type-record-so-listing-a-type-makes-it-fail-closed"></a>

## Registered Apple `valueTypes` get no value-type record, so listing a type makes it fail closed

**Revisit when:** the next skip-surface recovery program, or a consumer reporting a missing member whose type is a registered Apple `valueTypes` entry.

<details>
<summary>Recorded evidence and disposition</summary>

Ranked #1 recoverable from the 2026-07-27 C2 triage, and the highest-leverage skip-surface recovery currently known. Mechanism at `TypeDatabaseExtensions.cs:833-834`: an auto-bridge Apple type receives a synthetic ObjC class record UNLESS it is listed in `valueTypes` — but nothing supplies a value-type record in its place, so listing a type is what breaks it. The asymmetry is directly observable: unlisted `PKPaymentSummaryItem` binds, listed `PKPaymentButtonType` does not, despite being registered at `apple-frameworks.json:677`. Blast radius beyond the headline row: it also takes `ApplePayConfiguration.init` down (defaulted param at `.swiftinterface:737`) and `StripeCore.additionalEnabledApplePayNetworks` via `[PKPaymentNetwork]`. Corpus-wide the family is **21 rows over 10 registered type names** (3 of them in Stripe). Deliberately NOT taken in the 0.18.0 pre-release pass: it is a surface *recovery*, not a release-blocking regression, and a type-resolution change would have invalidated a green verification matrix mid-run. **2026-09-01 (issue-46 wave, S2):** the mechanism half is now built — `valueTypes` entries with a *described* kind get real records (`kind: "enum"` via `TryCreateRegisteredAppleEnumRecord`, no longer gated on auto-bridge modules, and the new `kind: "stringEnum"` via `TryCreateRegisteredAppleTypedEnumRecord`, which only forms a record when the installed platform surface and a `{Name}Extensions` sibling independently agree); Vision (31 entries) and ImageIO exercise it end-to-end. The PK*/payment family here has NOT been re-triaged — closing it is now mostly a data change (annotate the registered names with their kinds) plus per-type verification. **Trigger:** the next skip-surface recovery program, or a consumer reporting a missing member whose type is a registered Apple `valueTypes` entry.

</details>

<a id="sibling-same-label-overloads-collapse-onto-one-c-parameter-name-interface-facts-parameternames-keyed-label-onl"></a>

## Sibling same-label overloads collapse onto one C# parameter name (interface-facts `ParameterNames` keyed label-only, last-wins)

Found on VisionKit during the issue-46 wave: all four `ImageAnalyzer.analyze` overloads (UIImage/CGImage/CIImage/URL first parameters, same Swift label) emit with the parameter name `pixelBuffer` — the last sibling's name wins. Root cause: `SignatureFactsWalker.swift:389` writes `ParameterNames` facts keyed by label only, last-wins, and `SwiftABIParser.cs:2838-2846` consumes them by the same key. Pre-exists on baseline and is cosmetic in the ABI sense — types and marshalling are correct; only the C# parameter *name* is wrong, which bites named-argument call sites and IntelliSense. Fix shape: a composite key (label + parameter types; `MemberSignatureNormalizer.ComposeKey` is the precedent) on both sides — a `SwiftInterfaceParser` schema change, so it needs the `kSchemaVersion`/`ExpectedSchemaVersion` lockstep bump. Deliberately not folded into the wave: S3 owned the schema surface and was mid-flight when this was found. **Trigger:** a consumer reports wrong parameter names / broken named arguments on overloads, or the next `SwiftInterfaceParser` schema bump (fold it in then rather than paying a handshake bump alone).

<a id="typedecl-records-no-original-swift-name-provenance-for-keyword-mangled-type-names"></a>

## `TypeDecl` records no original-Swift-name provenance for keyword-mangled type names

`MethodDecl`, argument decls and parameter labels capture `OriginalSwiftName` when `ExtractUniqueNameWithOriginal` mangles a C# keyword, but the type-decl factories (`CreateStructDecl`/`CreateClassDecl` and the enum/protocol paths, `SwiftABIParser.cs` ~1870/1938/2363/2585) call the discarding `ExtractUniqueName`, so `GetSwiftName()` on a keyword-named type returns the mangled name. The async-accessor oracle (issue-46 wave, S3) works around this one lookup with a local deterministic `UnsanitizeKeywordName` inverse. The general fix — capturing `OriginalSwiftName` on type decls — also touches every emitter that builds Swift-side call text from type names, so it is a small program, not a patch. **Trigger:** a second consumer of type-level original names appears (someone is about to write a second `UnsanitizeKeywordName`), or a keyword-named Swift type mis-round-trips in wrapper text.

<a id="corevideo-has-no-apple-frameworks-json-entry-and-cvbuffer-is-a-cf-type"></a>

## CoreVideo has no `apple-frameworks.json` entry, and `CVBuffer` is a CF type

The one remaining `UnsupportedSignature` member skip on VisionKit (issue-46 wave): the `ImageAnalyzer.analyze` overload taking a `CVPixelBuffer` resolves through `CoreVideo.CVBuffer`, which has no registry entry at all — and unlike the NS family, CV* types are CoreFoundation types (not `NSObject`-rooted), so bridging them is a real design (CF ownership / toll-free-bridging rules), not a registry data add. **Trigger:** consumer demand for a CV*-typed member (a second report naming one), or an owner decision to take on CF-type bridging.

<a id="three-lower-ranked-c2-skip-surface-recoveries"></a>

## Three lower-ranked C2 skip-surface recoveries

The remainder of the 2026-07-27 C2 ranked list, all smaller than [the Apple value-type record gap](ingestion.md#registered-apple-valuetypes-get-no-value-type-record-so-listing-a-type-makes-it-fail-closed). (a) **DuplicateSignature disambiguation** — 3 rows; would restore `PaymentSheet.init(intentConfiguration:configuration:)`. (b) **Comparison operators on simple enums** — 1 row. (c) **Sync optional-closure properties** — 1 row. Deliberately excluded from the list as real language gaps rather than recoverables: ~12 async-closure-value rows and 6 `@objc`-existential rows. Context for anyone re-reading the raw skip report: rows are NOT members, and `SkipTriage.cs:142-147` subtracts only `ExpectedNonPublic` and `Recovered`, so by-design non-projections count as "lost" — of 53 rows, 22 were verified misclassified (surface actually reachable: 8 `SuppressedProxyMemberDegraded` are emitted public API with consume-direction loss only, 9 `EveryProtocolConformanceSkipped` emit the `I{P}` interface, 5 SwiftUI rows have all 5 bridge classes generated), 23 correct, 5 recoverable, 3 genuine bugs. **Trigger:** the next skip-surface recovery program.

<a id="publicsurfacelost-overstates-lost-surface-it-counts-emitted-but-degraded-members"></a>

## `PublicSurfaceLost` overstates lost surface — it counts emitted-but-degraded members

`SkipTriage.cs:142-147` subtracts only `ExpectedNonPublic` and `Recovered` from the total, so three categories whose surface IS reachable still tally as "lost": `SuppressedProxyMemberDegraded` members (emitted public API, consume-direction loss only), `EveryProtocolConformanceSkipped` rows (the `I{P}` interface is still emitted), and SwiftUI rows (the bridge class is generated). On the Stripe web that inflated the figure by ~42% — the headline "54 public members lost" is really 53 rows of which 22 are misclassified. A `DegradedConsumeCount` field already exists at `SkipTriage.cs:53` and is not subtracted. Not taken now because changing the metric mid-verification would move `build/baselines/skip-surface-baseline.json` and invalidate the green gate matrix; it is also a *reporting* defect, not a binding defect — no consumer surface changes either way. **Trigger:** the next skip-surface recovery program, or any reseed of the skip-surface baseline (fix the metric in the same commit as the reseed so the baseline is honest from the start).

<a id="nine-validation-cells-emit-fewer-lines-than-the-2026-06-28-baseline-largely-explained-one-residue"></a>

## Nine validation cells emit fewer lines than the 2026-06-28 baseline — largely explained, one residue

**Revisit when:** a consumer reports a missing member in one of these five libraries, or the next time the validation baseline is reseeded (compare ratios again — a *second* consecutive decrease in the same cell is real signal).

<details>
<summary>Recorded evidence and disposition</summary>

Surfaced by the 2026-07-27 re-baseline (`52ac336a` → `ff08abd5`). Median cell ratio is 1.043 (emission up), and the huge apparent *increases* (BlinkID 23 → 56,771; FirebaseAuth 117 → 24,304) are just the B5 metric fix — the old `FindMainCsFile` measured one arbitrary file. But nine cells shrank, five materially: AlertToast 0.40×, StripeCore 0.655×, SkeletonView 0.742×, CocoaLumberjackSwift 0.789×, XMLCoder 0.851×. Because the new metric SUMS all files while the old measured one, a smaller new total is a genuine decrease. Root cause identified for the flagged portion: XMLCoder is the only one with `ReviewCount > 0` (14), and all 14 are `MissingWrapperSymbol` on `SharedBox<T>` `Unbox` methods — *"P/Invoke removed: corresponding wrapper symbol was stripped from compiled wrapper."* That is the withdrawal machinery working fail-closed: those members would previously have shipped as dangling P/Invokes throwing `EntryPointNotFoundException`, so the drop is a correctness improvement, not lost working surface. The other four cells report `ReviewCount == 0` with every skip dispositioned. **Residue not chased:** AlertToast's `BridgeSummary` shows 2 SwiftUI views producing 0 generated bridges (1 Template, 1 Skipped, `GeneratedPercent` 0.0), which is dispositioned but not explained, and no gate covers emission *volume* on validation libraries (the skip-surface ratchet measures BindingTests only). Bounded deliberately: attributing nine cells across a 134-commit span is a research program, not a release gate, and all 132 cells compile clean. **Trigger:** a consumer reports a missing member in one of these five libraries, or the next time the validation baseline is reseeded (compare ratios again — a *second* consecutive decrease in the same cell is real signal).

</details>

<a id="28-unregistered-type-names-40-rows-fail-resolution-open-question-whether-the-registry-should-carry-them"></a>

## 28 unregistered type names (40 rows) fail resolution — open question whether the registry should carry them

Distinct from [the Apple value-type record gap](ingestion.md#registered-apple-valuetypes-get-no-value-type-record-so-listing-a-type-makes-it-fail-closed): these type names are **not registered in `apple-frameworks.json` at all** (`Swift.Duration`, `Metal.MTLTextureUsage`, `Dispatch.DispatchQoS`, and 25 more). So this is not a bug in a resolution path — it is an unanswered product question about registry scope: does the Apple registry aim to cover the whole SDK surface a third-party binding can touch, or only the frameworks we explicitly support? Registering them is cheap per-entry but unbounded in aggregate, and each entry is a maintenance commitment across Xcode releases. **Trigger:** an owner decision on registry scope, or a consumer report naming one of these specific types.

<a id="manual-framework-dependency-with-three-distinct-identities-can-stay-falsely-unresolved"></a>

## Manual `--framework-dependency` with three distinct identities can stay falsely unresolved

Raised as a Low by the 2026-07-27 paired review. `DependencyClosureResolver.cs:280` reconciles a pre-resolved dependency by Swift module name and xcframework bundle basename only (`satisfied.Add(Path.GetFileNameWithoutExtension(dep.XCFrameworkPath!))`), but manual resolution accepts any `.xcframework` path and derives the module from its contents — neither identity is required to equal the inner framework / install-name basename. So a primary linking `Core.framework` satisfied by `--framework-dependency Vendor.xcframework` vending module `Payments` leaves `Core` surviving reconciliation, recorded as a degradation and failing `--strict-inputs` spuriously. Not taken: it fails **closed** (a false failure on a hand-passed flag, never a bad binding), the auto-discovery path — the one every real consumer and the SDK take — is covered, and the honest fix is a three-identity reconciliation model (module + bundle + inner framework basename), not a one-line widening. **Trigger:** a real consumer hits a false `--strict-inputs` failure with a manual `--framework-dependency`, or the closure resolver is reworked for any other reason.

<a id="a-tighter-setter-floor-on-a-protocol-requirement-does-not-survive-the-swiftinterface-round-trip"></a>

## A tighter setter floor on a PROTOCOL REQUIREMENT does not survive the `.swiftinterface` round trip

**Revisit when:** a consumer binding an SDK module dump reports a `CA1416` at the wrong version on a protocol member, or the Swift toolchain starts printing accessor-level `@available` into `.swiftinterface` (at which point the protocol fixture stages end-to-end without changes).

<details>
<summary>Recorded evidence and disposition</summary>

A property whose `set` accessor is introduced later than the property (`var x: Int32 { get @available(iOS 17.0, *) set }`) is ingested from the ABI JSON `accessors` array into `PropertyDecl.SetterAvailabilityAnnotations`, and the Swift setter forwarder, C# setter P/Invoke and the `set`-accessor attributes all read that tighter floor (`AvailabilityHelpers.SelectSetterAnnotations`, `SetterAvailabilityIngestionTests`). On a **concrete type** this is proven end to end, not by unit tests alone: the BindingTests fixture's own ABI JSON carries `intro_iOS: 17.4` on the `Set` accessor of `SetterTighterThanGetter.value`, so the compile gate and the runtime lanes exercise the real ingested fact. The gap is narrower and is about **protocol requirements**: `StaggeredAvailabilityDelegate.staggeredValue`'s `Set` accessor comes through with `protocolReq: true` and no `intro_*` at all, because the textual `.swiftinterface` prints the requirement as `{ get set }` with no accessor attributes and the fixture's ABI JSON is produced by recompiling that interface. So a *requirement's* tighter setter floor — the shape that decides which witness-forwarder and proxy-accessor attributes a conformer sees — reaches the parser only from an ABI JSON dumped directly off a compiled module, and in-repo it is covered by unit tests only. Not a generator defect: on the round-tripped path the input genuinely lacks the fact. **Trigger:** a consumer binding an SDK module dump reports a `CA1416` at the wrong version on a protocol member, or the Swift toolchain starts printing accessor-level `@available` into `.swiftinterface` (at which point the protocol fixture stages end-to-end without changes).

</details>

<a id="the-noncopyable-parser-heuristic-misses-a-type-that-suppresses-both-copyable-and-escapable"></a>

## The `NonCopyable` parser heuristic misses a type that suppresses both `Copyable` and `Escapable`

The parser's flag calculation reads the declared conformance list, so `~Copyable, ~Escapable` — which lists neither conformance — is read as copyable. The consequence is bounded rather than open-ended: a type suppressing both is barely constructible across a public API boundary, so few of its members reach a copy witness, and the direction of the error is a missing refusal rather than a wrong emission. It is recorded because it bounds how much the noncopyable refusals actually cover, which is otherwise easy to overstate. **Fix shape:** read suppression explicitly from the dump instead of inferring it from an absent conformance — the same question the `~Copyable` oracle row raises about conformance-free nodes, and best answered once for both. **Trigger:** a corpus library or consumer publishes a `~Copyable, ~Escapable` type whose members bind, or the parser's conformance-free-node handling is revisited.

<a id="transitive-dependency-protocol-quarantine-degrades-outside-the-ledger-soundness-safe-not-a-soundness-hole"></a>

## Transitive dependency-protocol quarantine degrades outside the ledger (soundness-safe, not a soundness hole)

**Revisit when:** a corpus lib surfaces a `Derived: BadBase` dependency chain whose transitive primary dependent mis-emits, or a session that extends the closure seed to model dependency-internal inheritance edges.

<details>
<summary>Recorded evidence and disposition</summary>

The *direct* cross-module case is closed and gated (leg 4): a primary construct that names a quarantined dependency protocol is withdrawn through the ingestion seam. The *transitive* shape is **soundness-safe degradation** — never fail-open, never a truncated-vtable SIGSEGV, but not always "fail-closed" either: a healthy dependency protocol `Derived` that itself inherits a quarantined dependency protocol `BadBase` survives the `dependencyProtocols` stash (Fix B's `!IsIngestionQuarantined` guard filters only `BadBase`, not `Derived`), and a primary construct reaching `Derived` is NOT a closure withdrawal unit — the seed set is only the *directly*-quarantined dependency names (`CollectQuarantinedTypeNames`), so a dependency-internal inheritance edge is invisible to the walk. Why it stays sound: `BadBase` is absent from BOTH by-name sources (withheld from the TypeDatabase at ingestion AND filtered out of `moduleDecl.DependencyProtocols`), so the cross-module-parent ancestor walk (`ProtocolProxyEmitter.CrossModuleParent.EnqueueCrossModuleAncestors`) resolves `BadBase` to null and `continue`s — dropping it from the collected parents, and `BadBase` never reaches `VtableLayoutBuilder`. Per-parent vtable structs are self-contained (each cross-module parent emits its own `_{Parent}_vtable` block, no shared index axis to shift), so omitting `BadBase` does not truncate `Derived`'s slots; and because `BadBase` has zero bound C# surface in its home module, neither forward nor reverse dispatch to it is expressible from C#. Two sound sub-cases follow: (i) if the incomplete transitive conformance makes the primary Swift wrapper (`extension EveryProtocol: BadBase` missing) or the emitted C# uncompilable, it is **compile-catchable** → verify-recover withdraws it (fail-closed); (ii) if the `BadBase` requirements are satisfiable another way (marker/default-witness shapes), the binding **compiles and ships** with `P`'s `BadBase`-portion contract silently dropped and no withdrawal row — reduced, unledgered, but sound (never an emission against the malformed record). The residual is that sub-case (ii) ledger-completeness gap for the transitive edge. **Trigger:** a corpus lib surfaces a `Derived: BadBase` dependency chain whose transitive primary dependent mis-emits, or a session that extends the closure seed to model dependency-internal inheritance edges.

</details>

<a id="constrained-extension-property-accessors-are-outside-the-closure-completeness-proof"></a>

## Constrained-extension property accessors are outside the closure completeness proof

`CollectSignatureWithdrawals` tests a property via `SignatureReachesInternalType(property, …)`, which examines only `property.SwiftTypeSpec` (`InternalTypeReferenceWalker.cs:66`) — it does NOT walk the property's accessor `MethodDecl`s (`PropertyDecl.cs:28`), whose `getter.Method.GenericParameters` the constrained-extension emitter actively reads (`ConstrainedExtensionEmitter.cs:185`). Shape: `Box<T>` with a public extension property under `where T == QuarantinedPayload`. The closure retains the property and reports `ProvenComplete` even though an emitter-visible accessor edge names the withdrawn type. Today it degrades safely — the constrained-extension emitter skips the specialization when the concrete type is absent, so no compile failure — but the loss happens **outside** the withdrawal/ledger, so the completeness claim is not exhaustive for this edge. **Trigger:** a corpus lib surfaces a `where T == <quarantined>` constrained-extension property whose accessor reaches a withdrawn type and mis-emits, or a dedicated session that revisits the closure walk to model accessor-generic edges.

<a id="retained-parser-deformations-bypass-the-ledger"></a>

## Retained parser deformations bypass the ledger

The drop channels are ledgered, but two *retained-but-deformed* parse paths return a malformed spec with no ledger entry: `ParseTypeSpecOrDegrade` logs "degraded to a leading-prefix parse" and returns the malformed prefix (`SwiftABIParser.cs:4319`; documented reachable shapes include a subscript `some P` param and a `sending` closure result at :4288), and an enum-payload parse failure substitutes `new NamedTypeSpec(tupleElement.PrintedName)` (~:2089) without recording the deformation. These are not silent *drops* (the decl still emits, degraded), so they don't hit `DroppedWithError`, but they are undocumented losses of fidelity that the ledger's "no deformation is silent" intent would want captured. **Trigger:** a corpus lib mis-emits from a prefix-degraded spec or a substituted enum payload, or the ingestion-hardening program extends the ledger to retained deformations.

<a id="ambient-threadstatic-ledger-is-not-reset-by-the-public-generatebindings-entry-point"></a>

## Ambient `[ThreadStatic]` ledger is not reset by the public `GenerateBindings` entry point

The collector is `[ThreadStatic]` (`InputResolutionReport.cs:90`) and is reset only in the CLI command path (`BindingsGeneratorCommand.cs`); the public `GenerateBindings` overload delegates directly (`Program.cs:48`) without resetting it, so repeated in-process generations on the same thread share stale ledger (and resolution-decision) rows. Pre-existing for resolution decisions; the new terminal quarantine state makes the contamination more misleading. The CLI (the only shipping caller) resets correctly, so this bites only an embedding host that calls `GenerateBindings` in a loop. **Trigger:** an in-process/embedded consumer drives `GenerateBindings` repeatedly on one thread, or the ledger gains a public accessor.

<a id="proxy-suppression-keys-on-the-leaf-proxy-name-not-qualified-protocol-identity"></a>

## Proxy suppression keys on the leaf proxy name, not qualified protocol identity

When a quarantined protocol's proxy class is suppressed, suppression is recorded and reconciled by the *leaf* proxy name (`{Protocol}Proxy`), not the fully-qualified protocol identity. Two protocols sharing a leaf name in one module — one suppressed, one emitted — would collide: a suppressed-name lookup could mask the emitted twin or vice-versa. Pre-existing architectural latent surfaced by the proxy-suppression completeness fix; the proper fix is a cross-cutting refactor to carry qualified proxy identity through the suppression + reconciliation path. Not reachable today (no corpus module nests two same-leaf protocols across the suppression boundary). **Trigger:** two same-leaf-named nested protocols in one module, one suppressed and one emitted.

<a id="swiftbind122-reconciles-simple-proxy-names-directory-wide-false-negative-window"></a>

## SWIFTBIND122 reconciles simple proxy names directory-wide (false-negative window)

`ProxyReferenceIntegrityGate` reconciles emitted `new {X}Proxy(…)` construction sites against emitted proxy *class* definitions by simple name across the render output directory. Two false-negative windows: an emitted class literally named `{Protocol}Proxy` that is not the intended proxy, or a stale `.cs` left in the output dir, can satisfy the reference check without a real proxy behind it. Backstop-precision limit only — the authoritative fix is the suppression-recording path (every non-`Emit` decision arm now records into `SuppressedProxyClassNames`); the gate is a fail-closed net, not the primary guarantee. Robust fix: reconcile against the render's in-memory emitted-proxy registry rather than a directory name scan. **Trigger:** such a same-named class exists in a real render, or the gate is observed to miss a real dangling proxy reference.

<a id="swiftbind122-interpolated-raw-string-stripping-can-blank-a-live-construction"></a>

## SWIFTBIND122 interpolated/raw-string stripping can blank a live construction

The gate strips string contents before scanning for `new {X}Proxy(…)` construction sites so a proxy name mentioned inside a string literal isn't counted as a live reference. If a generated proxy construction ever moved *into* an interpolated or raw string, the stripping would blank a genuine construction site and the gate would miss it (false negative). Not reachable today — the emitter never constructs proxies inside strings. **Trigger:** generated proxy construction ever moves into interpolated/raw string form.

<a id="swiftbind122-treats-an-empty-absent-output-dir-as-clean"></a>

## SWIFTBIND122 treats an empty/absent output dir as clean

With no emitted source files to scan, the gate reconciles zero references against zero classes and passes. Sound as a *reference-integrity* backstop (nothing emitted ⇒ no dangling ref), but it must never be repurposed as the sole artifact-presence check — an empty render would read clean. **Trigger:** the gate is ever made the sole check that emission produced output.

<a id="graph-closure-depth-1-ownership-boundary"></a>

## Graph-closure depth-1 ownership boundary

The import-closure gate computes module ownership from the depth-1 dependency graph the transitive orchestrator itself is capped at. A required module owned only at graph depth ≥2 is under-flagged (false-negative): the gate can't distinguish it from an OS/SDK module, which is deliberately excluded by gating on graph-ownership rather than an Apple-SDK allowlist. No false positives; the failure mode is a missed hole, not a spurious one. **Reopen trigger:** the orchestrator's depth-1 transitive-product production cap being lifted (at which point ownership can be widened to match without reintroducing an SDK allowlist).

<a id="internal-required-target-siblings-pulse-pulseobjchelpers-shape"></a>

## Internal required-target siblings (Pulse/PulseObjCHelpers shape)

When the missing required module is a graph-owned *target* that is not exposed as a `.library` product (confirmed live on Pulse 5.2.3: `PulseObjCHelpers`, a C/ObjC target `Pulse` depends on and re-exports in its interface), the gate correctly *fails the conversion precisely* rather than building it — the existing orchestrator only drives declared products, and synthesizing library products for internal C/ObjC targets is new capability. **Reopen trigger:** a decision to auto-synthesize sibling products for internal required targets.

<a id="generator-side-pruning-of-spurious-managed-packagereferences"></a>

## Generator-side pruning of spurious managed PackageReferences

The generator emits a managed `PackageReference` for every `--framework-dependency` (all become `manual` effectiveDependencies) plus every binary-linkage dep, without checking whether the primary's emitted C# surface actually references the sibling's types. A dependency that is imported/linked but whose public types never cross into the emitted binding yields a managed reference the consumer must still restore. No correctness gap today — the run-scoped verification feed packs the full managed-ref closure, so nothing goes unresolved. Pruning would require the generator to compute per-sibling managed-reference reachability. **Reopen trigger:** `PublicAbiTypeReference` edges landing — once the generator tracks public-ABI type references between modules it has the reachability signal to drop refs no emitted type uses. Until then, do NOT add heuristic pruning: dropping a reference the emitted C# does need is a silent consumer-side CS0246.

<a id="dependency-closure-fixpoint-does-not-traverse-objc-only-siblings"></a>

## Dependency-closure fixpoint does not traverse ObjC-only siblings

`DependencyClosureResolver.ResolveToFixpoint` admits a resolved dependency to the scan frontier only when it carries a Swift binary (`DylibPath` + `XCFrameworkPath`). `BinaryDependencyAnalyzer` records ObjC-only siblings with `IsObjCOnly = true` and no `DylibPath`, so an ObjC framework's own link edges are never walked: a module reachable *only* through an ObjC sibling stays undiscovered. Both of the fixpoint's scan channels are Swift-shaped (the Swift dylib's link list, the `.swiftinterface` import lines) and the compile-import graph it closes is the Swift one, so this is a boundary of the traversal rather than a missing condition on it — the single-pass analyzer it replaced had the identical blind spot. Closing it means resolving an ObjC framework's Mach-O binary out of its slice search path and walking *that*, which is a new traversal channel. Fails safe: the undiscovered module surfaces as a normal unsatisfied-closure diagnostic at Parse, never as a silently wrong binding. **Reopen trigger:** a corpus library failing closure on a module reachable only via an ObjC-only sibling.

<a id="swiftbind027-auto-detected-dependency-resolver-quirk-under-strict-inputs"></a>

## SWIFTBIND027 auto-detected-dependency resolver quirk under `--strict-inputs`

Pre-existing quirk in the auto-detected-dependency resolver, surfaced when exercising `--strict-inputs` on the ingestion kitchen legs. Not chased down (out of the ingestion-hardening program's scope); benign on the default (non-strict) path the shipping generator uses. **Trigger:** a `--strict-inputs` run mis-resolves an auto-detected dependency, or a session adopts `--strict-inputs` as a default gate.
