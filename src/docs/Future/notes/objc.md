# ObjC and mixed bindings — reference notes

Searchable findings to consult when working in this area. Nothing here is queued or newly authorized.
[How to use and maintain these notes](README.md). Recorded evidence and counts describe their original investigation; recheck against current source before acting.

<a id="q3-mixed-objc-rebind-as-swift-only-mode"></a>

## Q3 — mixed-ObjC "rebind as Swift-only" mode

Deep-audit §3.6 owner decision, **not authorized**. The mixed-binding abort (`BindingsGeneratorCommand.cs:844–852`) is correct; the safe alternative is an explicit "rebind as an honestly-labelled Swift-only artifact" mode — the metadata must NOT claim Mixed or it bypasses SWIFTBIND039. A real feature with real cost; build on demand. **Trigger:** owner authorizes it after a consumer actually needs the Swift half of a framework the ObjC pipeline can't take.

<a id="where-the-enum-case-strip-tag-comes-from-a-source-stability-hazard"></a>

## Where the enum-case strip tag comes from — a source-stability hazard

The module tag used by the enum-case strip is derived at generation time as the longest common prefix of the module's exported `extern` constants, and nothing persists it. A vendor adding one upstream constant that does not carry the tag collapses the prefix, the tag arm stops matching, and **every** enum case in that module reverts to its unstripped name — a silent whole-module public-surface change triggered by an unrelated upstream edit. FBSDKCoreKit already demonstrates the collapse. Recommendation: persist the tag at first generation of a module and reuse the persisted value, so a published name can only move deliberately. Absent an answer, recompute-every-time stands and the hazard is live. **Trigger:** a corpus regeneration shows a module's enum cases changing with no generator change, or the owner authorizes persisting the tag.

<a id="foreign-owned-objc-types-are-omitted-not-resolved-against-the-sibling-assembly"></a>

## Foreign-owned ObjC types are omitted, not resolved against the sibling assembly

**Revisit when:** the Facebook kits head for publication with those internal seams wanted, or any multi-module ObjC vendor needs cross-assembly type resolution.

<details>
<summary>Recorded evidence and disposition</summary>

Residual after the 2026-07-31 duplicate-registration fix. **What was fixed:** `ClangAstParser.IsForwardDeclaration` no longer treats an attribute-only `inner` array or a `super: {"id":"0x0"}` as evidence of a definition — clang propagates a definition's attributes onto every later *re*-declaration, so `@protocol FBSDKDataPersisting;` in FBSDKCoreKit's headers arrived as `inner: [SwiftNameAttr]`, parsed as a member-less declaration, and was emitted as an empty `[Protocol]` shell colliding in the static registrar with `FBSDKCoreKit_Basics`' real binding. **Empirical impact — the mixed-lane generator path is re-emitting declarations already owned by a pure-ObjC dependency, and it was a hard block, not a wart:** the intersection of the two ApiDefinitions is **13 managed re-declarations** — 1 class (`FBSDKCrashHandler`) plus 6 protocols each contributing two decls (the `[Protocol]` model type *and* its `I…` interface) — of which only the model types carry ObjC registration, so the **registrar sees 7 colliding native names** (exactly what the captured `MT4118` output lists). By registrar flavour: **static / partial-static / managed-static** — the default for non-simulator iOS and forced under NativeAOT — **fails the build** (`MT4118` → `MT4116` → `MT2431`/`MT2362` → `IL7000` → `NETSDK1144`), while the simulator's **dynamic registrar builds, then silently aborts registration of the entire `FBSDKCoreKit_Basics` managed assembly at startup**. It is transitive through `SwiftBindings.Facebook.Core`'s `ProjectReference`, so it blocked *every* consumer of that package and could not be dodged from a consumer csproj (tried). It had been invisible only because `SwiftBindings.Facebook.CoreBasics.csproj` was missing `IsBindingProject` / `SwiftFrameworkType`, so that assembly shipped zero types and there was nothing to collide with — fixing the csproj is what surfaced the collision. Scale: CoreBasics declares 25 interfaces, Core re-declared just over half of them (Core declares 308 of its own). A body-less node is now reclassified as a re-declaration when — and only when — another node in the same TU declares the same `{kind}|{name}` **with** a body, so the 7 Basics-owned names (`FBSDKCrashHandler`, `FBSDKCrashObserving`, `FBSDKDataPersisting`, `FBSDKFileDataExtracting`, `FBSDKFileManaging`, `FBSDKInfoDictionaryProviding`, `FBSDKNotificationDelivering`) plus their `I…` interfaces no longer appear in FBSDKCoreKit's ApiDefinition. **The residual:** FBSDKCoreKit members *typed by* those names now fail closed through the existing `ObjCUnresolvableType` channel (14 items: 11 `configureWith…`/`init…` DI seams and the `DataStore` / `FileManager` / `NotificationCenter` properties — every one of them a Facebook `INTERNAL - DO NOT USE` surface, so no public API is lost) rather than binding to `FBSDKCoreKit_Basics`' real type. Resolving them needs the importing binding to emit `FBSDKCoreKit_Basics.IFBSDKDataPersisting` (qualified, or via a `using`) and to know the sibling *assembly* name for the owning module — the `TypeOwnershipManifestEmitter` direction. Newly feasible, because the consumer repo's `SwiftBindings.Facebook.Core.csproj` now carries `ProjectReference`s to the CoreBasics and AEM binding projects. **Trigger:** the Facebook kits head for publication with those internal seams wanted, or any multi-module ObjC vendor needs cross-assembly type resolution.

</details>

<a id="classes-forward-declared-but-never-defined-in-the-tu-still-emit-as-empty-shells"></a>

## Classes forward-declared but never defined in the TU still emit as empty shells

Sibling of the entry above, deliberately left alone by the 2026-07-31 fix because it is a *different* defect. When a name is `@class`-forward-declared and has no definition **anywhere** in the translation unit, there is no evidence it belongs to another module, so the historical answer is preserved and an empty `[BaseType(typeof(NSObject))] partial interface X { }` is still emitted. Live examples in FBSDKCoreKit 18.1.0: `FBSDKProfile` and `FBSDKAppLink` — both are Swift-defined classes whose real `@interface` lives in `FBSDKCoreKit-Swift.h`, which the framework's modulemap puts in a **separate submodule** (`module FBSDKCoreKit.Swift`) outside the umbrella header, so the clang dump never sees the definition. The shells are memberless and therefore near-useless, and the class-vs-protocol clash they create is what forces `@protocol FBSDKAppLink` to be renamed `FBSDKAppLinkProtocol`. Two candidate fixes, neither small: parse the modulemap's sibling submodules into the same TU, or drop a class whose `_OBJC_CLASS_$` symbol resolves but which contributes zero members *and* was never defined locally. **Trigger:** a Swift-heavy ObjC-shipped framework binds visibly short of its surface, or a consumer reports an empty class binding.

<a id="objc-mode-async-task-overloads-for-completionhandler-methods"></a>

## ObjC-mode `[Async]`/`Task` overloads for `completionHandler:` methods

Deferred ergonomics (2026-06 BindingAudit: Matter, Stripe3DS2). ObjC-imported completion-handler methods emit the callback shape only; a `Task`-returning overload is bgen-style sugar, not Swift-generator work. **Trigger:** consumer demand for await-able ObjC completion-handler APIs.

<a id="realitykit-arkit-typedb-dependency-gaps"></a>

## RealityKit ARKit TypeDB dependency gaps

`ARRaycastQuery` / `ARRaycastResult` / `ARTrackedRaycast` and accessibility-rotor types are absent from the TypeDatabase, so RealityKit members touching them stay dropped. Dependency-gated (needs ARKit/UIKit type coverage), not generator logic. **Trigger:** ARKit/UIKit types ship as Apple bindings.

<a id="objc-protocol-category-bridging-mixed-binding-phase-2-fb-2-any-objcp-collections"></a>

## ObjC protocol/category bridging (mixed-binding Phase 2) + FB-2 `[any objcP]` collections

**Revisit when:** a consumer needs `[any objcProtocol]` collections, or FB App Links deep-linking demand appears.

<details>
<summary>Recorded evidence and disposition</summary>

In a mixed ObjC+Swift binding, `ObjCBridgeRecordFactory` synthesizes type-DB records for ObjC classes / NS_ENUM / NS_OPTIONS / NS_TYPED but **not protocols** (`src/Swift.Bindings/src/ObjC/Pipeline/ObjCBridgeRecordFactory.cs:54`, "ObjC protocols remain out of scope"). A Swift-half member typed `any P` / `[any P]` where `P` is an ObjC protocol therefore fail-closes (member dropped with a reported reason, or degrades to `object`) — no crash, no mis-marshal. Impact: ~15 value-type/container members across the 3 FBSDK mixed kits, all **off the primary surface** (everyday delegate protocols already bind via the bgen companion; MapLibre is pure-ObjC and unaffected). Blocking gaps for a fix: `ObjCProtocolDecl`/`ObjCCategoryDecl` (`ObjC/Model/ObjCDeclarations.cs:107/:221`) have no `SwiftName` field (add it + extend `ClangAstParser.ExtractSwiftName` + the rekeyer's `SwiftABIParser.ObjCImportedTypeNames` ABI-harvest map to cover protocol import names), and `ObjCProtocol`-flag marshalling semantics vs the companion C# interface are unproven. **Category bridging is a separable non-issue** (categories extend already-parsed classes, no new named type). **Pair with FB-2:** most of those 15 fail on the `@objc`-existential-**in-container** limitation (`CanEmitMethod` / `HasUnsupportedObjCProtocolExistentialPosition`), a distinct container-marshalling gap — protocol-record synthesis alone won't recover them, so do both together or not at all. Owner already classified the dominant sub-case (FB App Links) as a documented limitation for the shipping libs. **Trigger:** a consumer needs `[any objcProtocol]` collections, or FB App Links deep-linking demand appears.

</details>

<a id="appintents-indexingkey-objc-import-projection"></a>

## AppIntents `indexingKey:` ObjC-import projection

A typed-singleton container for `PartialKeyPath<CSSearchableItemAttributeSet>` would need `CSSearchableItemAttributeSet` (and its ~80 storage properties) addressable from generated C#. CoreSpotlight is overwhelmingly ObjC; the Swift binding generator emits 0 lines for it (`declKind: "Import"` re-exports only). The `wrapperImportable: true` flip for CoreSpotlight shipped as a harmless precursor (`apple-frameworks.json:229-234`) and the `EntityProperty.init(indexingKey:…)` specialization is gated off in `ConcreteProtocolSpecializationEmitter.IsKeyPathGenericArgResolvable` so no broken C# emits. Unblock paths: (1) extend the binding pipeline to enumerate ObjC class declarations from clang module metadata + emit ObjC-aware trampolines (significant scope); (2) hand-curated `CoreSpotlightSupplement.xml` listing each public storage property as a synthetic property declaration (lower scope, SDK-maintenance-coupled). **Trigger:** a consumer needs Spotlight-indexed AppIntents entities — the specialization is already gated off, so nothing breaks until someone wants the feature.

<a id="objc-imported-api-availability-recovered-via-source-offset-finding-22-option-a2-nested-wrapper-macros-degrade"></a>

## ObjC-imported API availability: recovered via source-offset (Finding 22 option a2); nested wrapper macros degrade

**Revisit when:** a consumer needs availability bound through a custom wrapper macro (would require recursive macro expansion — `libclang`'s `clang_getCursorAvailability`, or a pre-pass expanding user macros before the offset scan), or a real SDK header surfaces a deprecation-sentinel/exotic-macro gap that matters for CA1416 completeness.

<details>
<summary>Recorded evidence and disposition</summary>

The ObjC binding path (clang-AST → ApiDefinition/StructsAndEnums emitters) now recovers `@available`/deprecation/`API_UNAVAILABLE` information and emits the same `[SupportedOSPlatform]`/`[ObsoletedOSPlatform]`/`[UnsupportedOSPlatform]` shape as the Swift `.swiftinterface` path, so CA1416 analyzer enforcement reaches ObjC-imported APIs too. The OLD vertical that read a JSON `platform` key was correctly deleted in S20 — clang's `-ast-dump=json` `AvailabilityAttr` nodes carry only `{id, kind, range}`, never `platform`/`introduced`/`deprecated`. The S20 (a2) replacement recovers the annotation from the header **source** at the attribute's `range.begin` byte offset (preferring `expansionLoc.offset`, the user-site macro token — NOT `spellingLoc`, which lands in Apple's `AvailabilityInternal.h`), then runs a parenthesis-aware macro-arg scan (`ObjCAvailabilityParser`) → `ObjCAvailability` model → `ObjCAvailabilityEmitter`. Covers the direct spellings `API_AVAILABLE`/`API_DEPRECATED`/`API_DEPRECATED_WITH_REPLACEMENT`/`API_UNAVAILABLE`, the `__attribute__((availability(...)))` keyword form, the `NS_AVAILABLE_*`/`NS_DEPRECATED_*` suffix-version forms, and the combined positional `NS_AVAILABLE`/`NS_CLASS_AVAILABLE`/`NS_DEPRECATED`/`NS_CLASS_DEPRECATED` (macOS-first, iOS-second) forms. **Known limitations (graceful degrade, not a crash — all yield NO attribute rather than a wrong one):** (1) nested user/framework **wrapper** macros (e.g. `MYLIB_AVAILABLE = API_AVAILABLE(ios(15.0))`) anchor `expansionLoc` at the wrapper token, so the arg scan finds no recognizable platform clause and emits nothing for that decl; (2) the `API_TO_BE_DEPRECATED` sentinel (a non-numeric "will be deprecated in some future OS" marker) is dropped from the deprecation slot — the **introduced** version is still recovered — because.NET has no attribute for an unknown-future obsoletion (`[ObsoletedOSPlatform]` requires a concrete version, so there is nothing correct to emit); (3) exotic / non-standard deprecation-macro spellings whose platform token doesn't map (e.g. a hypothetical `NS_DEPRECATED_WITH_REPLACEMENT_<PLAT>`) fall through `ParseNamedPlatformMacro`'s `MapPlatform` and contribute nothing. **Trigger to revisit:** a consumer needs availability bound through a custom wrapper macro (would require recursive macro expansion — `libclang`'s `clang_getCursorAvailability`, or a pre-pass expanding user macros before the offset scan), or a real SDK header surfaces a deprecation-sentinel/exotic-macro gap that matters for CA1416 completeness.

</details>

<a id="mixed-binding-bridged-type-container-position-test-coverage"></a>

## Mixed-binding bridged-type container-position test coverage

The Phase-1 ObjC→Swift type bridge (classes / NS_ENUM / NS_OPTIONS / NS_TYPED, shipped `5614e296`) is proven end-to-end for **scalar** and **Optional** positions in `build/Build.PackGate.MixedFixture.cs`, but no runtime test exercises `Set<T>` / `[T]` **of a bridged consumer-companion type** specifically (existing container tests cover Apple's pre-existing `NSUrl` bridge). `ObjCBridgedProjection` supplies per-element conversions so containers *should* ride that path, but it's unasserted for the new records. **Fix:** add a Swift member taking/returning `Set<BridgedClass>` / `[BridgedClass]` (and `[AuthType]` for NS_TYPED) to the mixed fixture, asserting elements round-trip rather than degrading to `object`. **Trigger:** the Phase-2 protocol-bridging work (Medium Priority), or a consumer report of a dropped bridged-type collection.

<a id="two-minor-fb-mixed-binding-drops-attribution-cross-module-typed-enum"></a>

## Two minor FB mixed-binding drops (attribution + cross-module typed-enum)

Surfaced by the pre-release ObjC skip sweep; both ship documented, neither blocks. (1) **CAPIReporter attribution:** `ICAPIReporter.Configure(factory, settings)` takes `any GraphRequestFactoryProtocol` → `object` fallback, so the EveryProtocol reverse-dispatch proxy is correctly un-emittable, but the decline reason is never recorded → it lands in the Review tier as "no decision recorded" instead of `ExpectedStructural`. You can *consume* `ICAPIReporter`, just not *implement* one in C# (rare). Fix = record the decline cause so it classifies. (2) **Cross-module NS_TYPED_EXTENSIBLE_ENUM:** `FBSendButton.ImpressionTrackingEventName` / `FBShareButton.ImpressionTrackingEventName` drop because `FBSDKCoreKit.AppEvents.Name` (a NS_TYPED_EXTENSIBLE_ENUM) doesn't resolve across the module boundary — the cross-module wrinkle of the shipped NS_TYPED bridge family. **Trigger:** either surfaces on a consumer report, or the next NS_TYPED bridge / EveryProtocol attribution pass.

<a id="mixed-framework-ast-dump-uses-the-platform-default-arch-in-target-not-the-selected-slice-s"></a>

## Mixed-framework AST dump uses the platform-default arch in `-target`, not the selected slice's

The clang AST-dump `-target` triple is built from a `SliceVariant` whose architecture defaults to `arm64` (`ObjCPipeline.cs:129`, `ClangAstInvoker.cs:86`); `XCFrameworkResolver.ResolveObjCFramework` selects the actual slice (`XCFrameworkResolver.cs:451`) but its result record carries no selected-architecture field (`:422`), so the choice never reaches the triple. An **x86_64-only simulator** mixed xcframework is therefore parsed with an arm64 `-target`, which can take the wrong `__x86_64__`/`__arm64__` header branch or fail on an arch-specific declaration. Not a regression — the `-target` flag is new in Part B and the prior host-default was already arm64 — and not corpus-reachable (modern libs ship arm64 sim slices). Real fix: thread the selected slice's arch through the resolver result → `SliceVariant` → triple. **Trigger:** an x86_64-only-sim mixed framework surfaces an AST-dump failure that classifies on arch grounds.

<a id="objc-apidefinitionemitter-dedup-key-would-emit-check-omit-delegateprotocolnames-grok-m2"></a>

## ObjC `ApiDefinitionEmitter` dedup key + would-emit check omit `delegateProtocolNames` (Grok M2)

**Revisit when:** first case where a delegate-protocol and a same-named regular protocol are both same-selector method params on one type, or any other `ApiDefinitionEmitter` dedup work.

<details>
<summary>Recorded evidence and disposition</summary>

`EmitMethod` renders emitted parameter types **with** `delegateProtocolNames` (via `EmitParameters` → `ObjCTypeMapper.MapType`, `ApiDefinitionEmitter.cs:698`), so a delegate-protocol param emits as `Foo`; but the dedup signature in `ResolveMethodNameWithDedup` (`:747`) and the resolvability check in `WouldEmitMethod` (`:784/:789`) call `MapType` **without** it, rendering the same param as `IFoo`. The dedup set is still internally consistent — every writer (`ResolveMethodNameWithDedup`, plus inherited-signature seeding `SeedInheritedProtocolSignatures`/`ComputeProtocolEmissionSet`, which route through those same two helpers) omits `delegateProtocolNames`, and `MapType` is deterministic — so two methods that EMIT with identical signatures also dedup-key identically: **no missed collision, no CS0111/CS0102 reachable**. The only reachable effect is a cosmetic **false-positive** over-rename: a delegate-protocol param (emits `Foo`) and a same-named regular-protocol param (emits `IFoo`) collapse to one `IFoo` dedup key and trigger a spurious suffix/`SelectorToFullMethodName` rename. No validation-library or fixture repro. **Deferred (real, not cheap):** the fix is a lock-step change across `ResolveMethodNameWithDedup` + `WouldEmitMethod` (both feed the seeding path) to thread the delegate-aware `MapType` view, plus a full ObjC re-validation since it perturbs dedup for every ObjC binding. **Trigger:** first case where a delegate-protocol and a same-named regular protocol are both same-selector method params on one type, or any other `ApiDefinitionEmitter` dedup work.

</details>

<a id="objc-enum-case-collision-disambiguation-diverges-from-the-topascalcase-reference-site-naming"></a>

## ObjC enum-case collision disambiguation diverges from the `ToPascalCase` reference-site naming

**Revisit when:** a corpus/consumer library surfaces a default-value reference to a disambiguated ObjC enum case, or a session already reworking ObjC enum-case naming.

<details>
<summary>Recorded evidence and disposition</summary>

When two ObjC `NS_ENUM` cases project to the same C# identifier, or a case PascalCases to the enum's own type name, `StructsAndEnumsEmitter.EmitEnum` deterministically suffixes the later member (`{name}_2`, `_3`, …) via the `emittedNames` set (seeded with the enum type name) and records a `DuplicateSignature` skip — emitting a compilable enum instead of an invalid `CS0102`/`CS0542` one. But the reference sites that name an enum case in a Swift default-value position (`SwiftDefaultValueMapper.MapEnumCase`) compute the bare `NameProvider.ToPascalCase(caseName)` with no knowledge of the suffix, so a Swift member whose default value points at the *renamed* case emits `EnumName.Mode` while the declaration now carries `EnumName.Mode_2` → `CS0117` for that member. Reachable only in the doubly-rare combination of (a) an ObjC enum whose cases collide under PascalCase AND (b) a Swift-side default value referencing the colliding case; the disambiguation is strictly preferable to emitting an invalid enum, and this reference-side mismatch is its residual tradeoff. **Fix shape:** thread the emitted-case rename map out of `EmitEnum` and have `MapEnumCase` consult it. **Trigger:** a corpus/consumer library surfaces a default-value reference to a disambiguated ObjC enum case, or a session already reworking ObjC enum-case naming.

</details>

<a id="anonymous-objc-enums-are-dropped-with-no-diagnostic"></a>

## Anonymous ObjC enums are dropped with no diagnostic

**Revisit when:** a consumer or corpus library binds an ObjC framework that declares public constants through an untagged enum, or a session already reworking `ParseEnumDecl`. **Trigger evidence (2026-07-31, not picked up):** MapLibre's `MLNPluginLayer.h` declares `typedef enum { … } MLNPluginLayerPropertyType;` — the enum is dropped and the two `MLNPluginLayerProperty` members typed by it skip as unresolvable (recorded in the binding report, so the member half is diagnosed even though the enum half is silent). This is the plugin-layer extension surface, not the mainstream map API; whether to fund the typedef-case fix is an owner call.

<details>
<summary>Recorded evidence and disposition</summary>

`ClangAstParser.ParseEnumDecl` bails on the first line — `var name = GetName(element); if (name == null) return null;` (`src/Swift.Bindings/src/ObjC/Parser/ClangAstParser.cs:603-604`) — so an untagged `typedef enum { … } Foo;` (clang emits the `EnumDecl` with no `name`, and a separate `TypedefDecl` carrying the name) loses every constant silently: no `SkipReason`, no `SWIFTBIND` diagnostic, no count in the skip report. Not corpus-reachable in practice: `NS_ENUM`/`NS_OPTIONS` always name the tag, and every ObjC framework bound so far (including the 2026-07 Intercom investigation that mapped this pipeline) uses them exclusively. Two independent shapes hide behind the same `return null`: a genuinely anonymous enum used only for its constants (would want the constants hoisted somewhere, which has no obvious home in the ApiDefinition/StructsAndEnums split), and a typedef-named enum (would want the `TypedefDecl` name back-propagated onto the `EnumDecl`). The second is the tractable one; the first is a design question, not a bug fix. **Fix shape (typedef case only):** correlate the anonymous `EnumDecl` with the sibling `TypedefDecl` whose underlying type points at it, and adopt that name. **Trigger:** a consumer or corpus library binds an ObjC framework that declares public constants through an untagged enum, or a session already reworking `ParseEnumDecl`. **Trigger evidence (2026-07-31, not picked up):** MapLibre's `MLNPluginLayer.h` declares `typedef enum { … } MLNPluginLayerPropertyType;` — the enum is dropped and the two `MLNPluginLayerProperty` members typed by it skip as unresolvable (recorded in the binding report, so the member half is diagnosed even though the enum half is silent). This is the plugin-layer extension surface, not the mainstream map API; whether to fund the typedef-case fix is an owner call.

</details>

<a id="umbrella-header-convention-short-circuits-the-modulemap"></a>

## Umbrella-header convention short-circuits the modulemap

`ClangAstInvoker.FindUmbrellaHeader` checks the `Headers/{moduleName}.h` convention **before** the modulemap is read (`src/Swift.Bindings/src/ObjC/Parser/ClangAstInvoker.cs:238-244`) and returns immediately on a hit, so strategies 2–4 (`umbrella header "X.h"`, directory `umbrella "Headers"`, explicit `header` lines) never run for such a framework. For a framework whose modulemap names a *different* umbrella than the convention file — or whose convention file is a stub that imports only part of the public surface — everything reachable only from the modulemap-named umbrella is invisible, with a zero exit code. Not observed: every framework parsed to date has a convention file that agrees with its modulemap (Intercom's `Intercom.h` is both), and inverting the order is not obviously safer — a modulemap can name an umbrella that is itself less complete than the convention header. The honest fix is not a reorder but a *reconciliation*: when both exist and disagree, parse the union (the combined-header machinery added for the directory-umbrella strategy already gives us the mechanism). **Trigger:** a framework binds near-empty or short of its public surface and its modulemap names an umbrella other than `Headers/{moduleName}.h`.

<a id="combined-umbrella-header-globs-h-only"></a>

## Combined umbrella header globs `*.h` only

The directory-umbrella and explicit-`header`-lines strategies synthesize a combined header by globbing `*.h` under the umbrella directory (`ClangAstInvoker.CreateCombinedHeaderFile`). A framework shipping public declarations in a `.hh`/`.hpp` header therefore loses them silently. This is deliberate, not an oversight: those extensions signal C++, and a single C++ header pulled into the ObjC translation unit hard-fails the *entire* clang dump — trading one framework's partial surface for that framework's total loss. The extension filter is the cheapest available proxy for "is this header parseable as ObjC", since the alternative (attempt, detect the C++ failure, retry without it) needs per-header bisection of the dump. **Fix shape:** if it ever matters, bisect — dump with all headers, and on a C++-flavored failure drop the offending header and re-dump, rather than widening the glob blindly. **Trigger:** a framework with a directory umbrella (or explicit `header` lines) binds short of its public surface and ships `.hh`/`.hpp` headers under `Headers/`.

<a id="compatibility-alias-is-an-unhandled-top-level-ast-node-kind"></a>

## `@compatibility_alias` is an unhandled top-level AST node kind

Sentry's clang dump raised `SWIFTBIND029` for an `ObjCCompatibleAliasDecl` — the node kind is outside `ClangAstParser`'s top-level vocabulary, so an ObjC `@compatibility_alias NewName OldName` contributes nothing to the model and any public member typed by the alias resolves against a name the binding never declares. Noticed in passing during the 2026-07 Intercom investigation and not investigated: Sentry independently fails its own `SWIFTBIND113` compile gate (1 error, 22 skips) from a separate pre-existing cause, so the alias gap has never been isolated as the thing blocking a binding. The diagnostic is doing its job — the kind is *reported*, not silently dropped — which is why this is a vocabulary gap rather than a defect. **Fix shape:** parse the alias into a name→name mapping and resolve aliased references to the target class at type-mapping time. **Trigger:** a consumer or corpus framework binds short with `SWIFTBIND029` naming `ObjCCompatibleAliasDecl`, or someone takes on Sentry's `SWIFTBIND113` failure.

<a id="third-party-types-that-squat-on-the-ns-prefix-keep-the-net-acronym-rename"></a>

## Third-party types that squat on the `NS` prefix keep the.NET acronym rename

**Revisit when:** a bound framework declares an `NS`-prefixed type containing one of those acronyms and the renamed managed name is reported as confusing or breaks a consumer's expectations.

<details>
<summary>Recorded evidence and disposition</summary>

`ApplyDotNetAcronymConvention` rewrites `URL`/`HTTP`/`HTTPS`/`JSON`/`HTML`/`XPC` to.NET casing for any `NS`-prefixed name, because that is how Microsoft.iOS spells Apple's own types (`NSURLSession` -> `NSUrlSession`). It cannot tell an Apple type from a third-party one that squats on Apple's reserved prefix, so a vendor class or protocol named `NSURLThing` is *also* renamed. As of the 2026-07 declaration/reference alignment this is now at least self-consistent — the declaration is emitted under the renamed spelling with `Name = "<raw>"` preserving native registration, so every reference resolves — but the managed name a consumer sees still differs from the ObjC one for no reason that applies to their type. The principled fix is to apply the convention only to names NOT declared in the binding, which means threading a locally-declared-names set through `MapType`'s full recursion (the same shape as `localProtocolNames`) plus `MapClassName`'s call sites. Not done because the reachable population is "third-party frameworks that both NS-prefix their types and use one of six acronyms" — zero instances across the 120-library corpus. **Trigger:** a bound framework declares an `NS`-prefixed type containing one of those acronyms and the renamed managed name is reported as confusing or breaks a consumer's expectations.

</details>

<a id="maptype-drops-pointer-indirection-for-known-value-type-names-in-non-parameter-positions"></a>

## `MapType` drops pointer indirection for known value-type names in non-parameter positions

**Revisit when:** a consumer reports wrong data through a value-type pointer in a field/return/block-param position, or the next ObjC type-mapping pass.

<details>
<summary>Recorded evidence and disposition</summary>

The bare-name arms that claim `objcValueTypes` / system-struct / system-enum spellings ignore `typeRef.IsPointer`, so `MapType(NSRange *)` returns `NSRange` — a pointer bound by value — for struct **fields, return types, and block parameters**. Parameters are safe: they route through `MapValueTypePointerParameterType`, which strips the indirection before re-entering `MapType`. Pre-existing shape — the `objcValueTypes` seeding behaved this way before the 2026-07-31 vocabulary extension; that work widened the affected *name set*, not the mechanism (measured during the system-enum-pointer fix: for an enum alias the change renamed the defect — `CGBlendMode` vs `int`, both 4 bytes, both drop the pointer — with no drop→emit flip). One deliberate neighbor: a struct alias that itself carries indirection (`typedef NSRange *TLRangeRef`) stays **fail-closed** — it resolves to the unclaimed record tag `_NSRange` and the member drops at the resolvability gate — because stopping the shared typedef walk on struct names was proven (Codex High, probe-confirmed) to flip that drop into a by-value bind through the by-name readers. Fixing the root moves members from "emitted by value" to "dropped as unresolvable", which the API-manifest gate treats as a removal — it wants its own pass with baseline reseeds, not a drive-by. **Trigger:** a consumer reports wrong data through a value-type pointer in a field/return/block-param position, or the next ObjC type-mapping pass.

</details>

<a id="c-functions-are-blind-to-the-mutable-element-pointer-array-shape"></a>

## C functions are blind to the mutable element-pointer array shape

The 2026-07-31 array projection correlates an element pointer with a `count:` **selector keyword**; a C function has no selector, so `void f(CGPoint *pts, size_t n)` still projects `out CGPoint` (the zeroed-input wrong-data shape item 2 fixed for methods). Only the `const` variant drops with a recorded skip (`StructsAndEnumsEmitter` C-function const drop). Using the parameter *name* as the correlation signal was considered and rejected — it is exactly the guess the design refuses. **Trigger:** a bound framework exposes a public C function taking element-pointer + count, or a sound signal appears (e.g. `__counted_by` annotations in the SDK headers).

<a id="class-re-declaration-map-ignores-protocol-flattened-base-properties"></a>

## Class re-declaration map ignores protocol-flattened base properties

`BuildInheritedClassPropertyMap` walks only `ancestor.Properties`; a property the base class gains via a **protocol conformance** (bgen flattens protocol members onto the generated base) never enters the map, so a subclass restating it emits without `[New]`/defer and the CS0108 shadow survives for that one shape. Guessing is lossy in both directions — a wrong defer silently deletes API, a spurious `[New]` is noise — so it was left outside the 2026-07-31 superclass-chain fix on purpose (Grok Medium, both rounds, dispositioned). `BuildInheritedProtocolAccessorSelectors` already does the analogous walk for methods — the shape to copy. **Trigger:** a real library hits CS0108 through a property declared only on a protocol its base conforms to.

<a id="bgen-drops-argumentsemantic-from-a-method-export"></a>

## bgen drops `ArgumentSemantic` from a method `[Export]`

The category instance-property projection now states the property's declared semantic on both accessor exports (`[Export("tint", ArgumentSemantic.Copy)]` / `[Export("setTint:", ArgumentSemantic.Copy)]`), so the api-definition contract no longer describes a weaker property than the one it replaces. What that buys is contract fidelity, not behavior: bgen re-emits the semantic for a **property** export and not for a **method** one, which the umbrella fixture shows directly — `[Export("ou_spanRank", ArgumentSemantic.Assign)]` in the ApiDefinition comes back as a bare `[Export ("ou_spanRank")]` in the generated `NSValue_OUBoxing.g.cs`. So a projected accessor cannot express a memory semantic bgen will act on, and a category property whose setter genuinely needs `copy` gets whatever the native setter does. Not worth chasing: the accessor is a direct `objc_msgSend` to the property's own setter selector, which is the same call the property form would make, so there is no managed-side storage for a semantic to govern. **Trigger:** a retain/copy defect observed through a projected category setter, or a bgen version that starts honoring the semantic on method exports (at which point the declaration is already in place).

<a id="unqualified-objc-read-write-pointers"></a>

## Unqualified ObjC read/write pointers

Compiler-expanded explicit `in`/`inout` scalar pointers now preserve incoming values through `ref`, while explicit `out` and unqualified pointers retain the established `out` projection. A native unqualified read/write control demonstrates that the default can lose input; mutability and selector names are not authoritative direction facts. **Trigger:** a consumer needs an unqualified read/write selector; add explicit direction metadata or an override feeding the existing declaration fact, with a deliberate managed source-compatibility decision and native writeback proof.

<a id="objc-owned-properties-and-consumed-inputs-without-proven-bgen-carriers"></a>

## ObjC owned properties and consumed inputs without proven bgen carriers

Clang explicit/implicit method return ownership is emitted through the native-validated `[return: Release]` carrier. Retained properties are detected but refused as complete properties: bgen ignores the getter return attribute, and a property-level attribute does not compile. Consumed parameters/ordinary receivers and explicit input/inout NSError also receive named member refusal; `[Retain]` and `GC.KeepAlive` do not transfer a native +1. **Trigger:** a supported bgen carrier or concrete consumer requirement justifies a native-proven shim/forwarder, including caller liveness, incoming/outgoing ownership and exact deallocation.

<a id="the-delegate-receiver-acronym-strip-mis-handles-a-multi-acronym-class-name"></a>

## The delegate-receiver acronym strip mis-handles a multi-acronym class name

`ApiDefinitionEmitter.StripLeadingAcronym` (`ApiDefinitionEmitter.cs:2326`, reached from `DelegateReceiverCandidates`) removes the whole leading uppercase run rather than just the framework prefix, so `NSURLSessionDelegate` yields the candidate `Session` instead of `URLSession`. No candidate matches the selector's first part, and the delegate method keeps today's name. Conservative — an un-peeled name is correct, just less Apple-like. This is the selector-naming path, not `ObjCSwiftImportNameRewriter`; the two were deferred together but are separate mechanisms. **Trigger:** a shipped library's delegate surface is materially harder to read because of it, or a second multi-acronym class hits the same shape.

<a id="companion-re-emit-under-the-ismoduleprocessed-skip-cannot-see-the-vetted-rename-map"></a>

## Companion re-emit under the `IsModuleProcessed` skip cannot see the vetted rename map

On the path where a module is re-emitted as an ObjC companion after already being processed, the rewriter's vetted map is not in scope — that path generates no Swift half at all, so the Swift-import renames do not apply there. **Trigger:** a mixed binding is observed emitting un-renamed ObjC type names through the companion path.

<a id="the-objc-namespace-collision-guard-is-computed-before-the-swift-import-rename-so-it-guards-the-pre-rename-name"></a>

## The ObjC namespace-collision guard is computed before the Swift-import rename, so it guards the pre-rename name

The guard in `ObjCPipeline.Parse` compares declared type names against the module namespace using the names as parsed, while `ObjCSwiftImportNameRewriter` moves some of those names afterwards. A rename that *creates* a collision with the module namespace (or clears one the guard already fired on) is therefore judged against the wrong name. Latent, not observed: the rewriter's accept-list is vetted per name and none of the current 15 renames lands on a module namespace. **Fix shape:** run the guard on the post-rename names, or re-run it after the rewriter, so both stages see the same vocabulary. **Trigger:** a vetted rename is added whose result equals a module namespace, or a consumer reports a namespace/type-name clash in an ObjC binding.

<a id="struct-valued-objc-field-constants-have-no-dlfcn-reader"></a>

## Struct-valued ObjC `[Field]` constants have no Dlfcn reader

Scalar and NSString-typed `extern` constants bind; a struct-valued one does not. MapLibre's `MLNCoordinateSpanZero` skips as `ObjCUnsupportedConstruct` (`[Field] has no Dlfcn reader for 'MLNCoordinateSpan'`). Workaround `default(MLNCoordinateSpan)` / object initializer only works because that struct has public fields and the constant is zero-like; a non-trivial struct constant would have no workaround. **Trigger:** a consumer needs a struct-valued extern constant, or the next ObjC constants pass.
