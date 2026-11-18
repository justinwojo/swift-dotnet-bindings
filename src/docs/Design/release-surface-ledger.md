# Release surface ledger

This ledger accounts for the public surface of the release candidate against the last shipped
release (SDK `sdk-v0.20.2`, Apple supplement `apple-v26.2.9`). It covers four surfaces: the
BindingTests corpus, `SwiftBindings.Runtime`, `SwiftBindings.Apple`, and the Apple framework
bindings RealityFoundation, RealityKit, MusicKit and AppIntents regenerated with both generators.

Every member that was public in the previous release and is now removed, skipped or guarded has a
row with its reason, the engines it affects and one disposition:

| Disposition | Meaning |
|---|---|
| unsafe-before | The member was public but called Swift incorrectly; the fix that withdrew it is cited. Recovery is post-release. |
| refused-pre-native | The member is still public and guarded: it throws `SwiftTransitionUnsupportedException` before native work on the named engines and runs elsewhere. The recovering route is named. |
| withdrawn | Removed by a bridge withdrawal, a pinned-extension rule or the verify-recover loop. |
| intended | A deliberate surface change (rename, reshaped API) with a replacement. |
| regression | An unintended loss. Fixed in this release, or listed as a release blocker. |

Engines are named as in `SwiftTransitionGuard`: NativeAOT, CoreCLR with JIT, Mono (JIT and full
AOT), CoreCLR interpreter arm64, CoreCLR interpreter x64. The previous release supported .NET 10
only, so the .NET 11 CoreCLR interpreter rows describe engines that release never ran on.

## BindingTests corpus

Compared: the API-manifest baseline at `sdk-v0.20.2` with the branch's, the skip-surface markers in
the generated C# at both points, and a metadata listing of every public and protected member of
`CompileCheck.dll` built from each generator (21825 rows at `sdk-v0.20.2`, 24622 on the branch
before the fixture added below).

| Measure | sdk-v0.20.2 | Branch (first measurement) | Candidate (`4600d8f2`) | Change against sdk-v0.20.2 |
|---|---|---|---|---|
| API-manifest members | 6146 | 6983 | 7160 | 1014 added, 0 removed: every (module, signature) entry of `sdk-v0.20.2` is in the candidate's baseline |
| Members whose entry point moved (same C# signature) | | 43 | 43 | 15 direct Swift call → `@_cdecl` wrapper, 27 `@_cdecl` wrapper → native thunk, 1 wrapper → wrapper |
| Skip-surface markers | 348 | 348 | 372 | `SB0001` 23 → 10, `Unsupported` 284 → 305, `UnsupportedSwiftType` 41 → 57 |
| Members carrying a transition guard | none (no guard existed) | 3 | not re-measured | see below |

The candidate column reads the committed API-manifest and skip-surface baselines; the moved entry
points are the API manifest's own entry-point column, unchanged since the first measurement. The
24 markers gained since the first measurement all come from fixtures added after it and are listed
under "Candidate changes since the first measurement" below.

### Members public in sdk-v0.20.2 that are now removed, reshaped, skipped or guarded

| Member | Change | Engines affected | Disposition |
|---|---|---|---|
| `SetterOnlyCallbackHolder.OnConfigChanged` | `{set}` became `{get set}`: the binding now reads the stored callback back. | none | intended (a getter was added; every existing use compiles) |
| `PinnedQuery<T>.Make(int)`, `PinnedQuery<T>.Make(nint)` | At `sdk-v0.20.2` both were `[Obsolete]` tombstones that threw `NotSupportedException` on every engine: the result is a non-frozen generic struct returned indirectly and no wrapper could call it. Now both make a guarded direct Swift-convention call (one shared import, shape `LoweredSignature`); the bridge plan is refused because the lowering predictor does not model a same-type requirement between generic parameters. | Refused before native work on the CoreCLR interpreter x64 (.NET 11 Debug under Rosetta) only. They now run on NativeAOT, CoreCLR with JIT, Mono and the arm64 interpreter, where they used to throw. | refused-pre-native; recovers through a bridge once the predictor models same-type requirements |

No other member present at `sdk-v0.20.2` is removed, reshaped, skipped or guarded: no member gained
a skip marker or an `[Obsolete]` marker, and 18 lost one. The other skip-surface changes are:

- Skipped at both points, now with a marker in the generated C#: `SearchableTypeRegistry.allTypes()`
  (an existential-metatype array; `sdk-v0.20.2` listed it only in `binding-report.json`).
- Fixtures added since `sdk-v0.20.2`, never public before: `GridLayoutRegistry.create/register`,
  `MacAuthenticator.tag`, `TaggedKeyDeriver.deriveKey` (associated-type constraints),
  `ParentContextChoice.Seeded` (generic-enum payload case), `ReturnedClosureVendor.makeApplier`,
  `makeAsyncCounter`, `makeCFunctionApplier` and `storedPairMaker` (closure contracts),
  `WitnessPayload.encode/init` (synthesized `Codable`), and two `Unsupported closure fallback`
  markers.
- Reason text only, skipped at both points: `ZeroWitnessClosureHost.makeAnyDescriber`,
  `makeAnyProducer`, `makePointAnyDescriber`, `makeSendableProducer`, `makeStringProducer` and
  `makeTagAnyDescriber`, and `SearchableTypeRegistry.types`.
- Now bound: `ConventionCDebugParamLoader`, `ConventionCMultiClosureLoader` and
  `ConventionCValidatedLoader` initializers, and 12 members that lost their `SB0001` marker because
  they now reach Swift through a wrapper, thunk or bridge.

### Guarded members that were not public before

| Member | Shapes | Why it has no bridge |
|---|---|---|
| `FoundationRawValueBox<TElement, TFailure>.Flipped(bool)` | `SelfRegister`, `LoweredSignature` | The result `ObjCBool` is declared in another module and is not in the carrier table. This fixture is the exposure report's named positive control: the exposure gate fails unless the final artifact attributes exactly one direct row to it. |

The residual-guard check finds four hazardous imports in `CompileCheck.dll`: `Flipped`, the one
import both `Make` overloads share, and two `StaffRoster.firstEntry` imports that no public member
reaches (the public `FirstEntry` overloads call through other routes). Three guard calls protect
the three public members. `Swift.Runtime.dll` and `SwiftBindings.Apple.dll` have none.

### Fixture added for the regressions fixed below

`Generics/PinnedOverloadScopes.swift` reproduces the two overload-pinning regressions the Apple
framework comparison found: a subclass repeating its superclass's `required init`
(`PinnedInitDerived(value:)`) and an initializer in a constrained extension
(`extension PinnedTransition where Value == PinnedEndpointA`) beside a specialization for another
type. Its members are additions; `PinnedOverloadScopesTests` checks that each default-omitting
member runs the declaration it names, with the default Swift supplies. The
`PinnedEndpointA` specialization meets both initializers, so its short factories stay declined
(listed under `suppressedAmbiguousOverloads` in `binding-emission-report.json`). The constrained
initializer has no open-generic form, so it adds one `Unsupported` skip marker to the corpus.

`PinnedUrlOrSequenceSession` reproduces the third: a `URL` initializer beside a generic one whose
input must be a `Sequence` of samples. `PinnedUrlOrSequenceSession(NSUrl)` and the full form are
additions, and `PinnedOverloadScopesTests` checks that both run the `URL` initializer. C# has no
generic constructors, so the generic initializer adds one `Unsupported` skip marker.

## SwiftBindings.Runtime

`nuke pack --version 0.21.0-dev` diffs the Runtime against `SwiftBindings.Runtime 0.20.2` from
NuGet with ApiCompat; the pack passed. A metadata listing of the public surface of both packages
agrees, identically on all five target frameworks (`net10.0` and the iOS, Mac Catalyst, macOS and
tvOS frameworks): 0 members removed, 38 added.

| Added type | Added members |
|---|---|
| `SwiftTransitionFacts` (flags enum) | `None`, `SelfRegister`, `SpecialBeforeArguments`, `LoweredSignature`, `ReverseCallback`, `FunctionPointerSelfRegister`, `FunctionPointerSpecialBeforeArguments`, `FunctionPointerLoweredSignature` |
| `SwiftTransitionGuard` | `IsSupported`, `EnsureSupported`, `ThrowIfUnsupported`, `UnsupportedFacts` |
| `SwiftTransitionUnsupportedException` | `Facts`, `RefusedFacts`, `Member`, `Runtime` |
| `SwiftLoadContextConflictException` | `Registration`, `OwnerAssembly`, `OwnerLoadContext`, `RefusedAssembly`, `RefusedLoadContext` |
| `NativeRegistrationOwnership` | `ClaimModule` |
| `SwiftProtocolRegistry` | `Register`, `RegisterObjC`, `Retarget<T>` |
| `SwiftReverseContext` | `Create`, `Release` |
| `Marshalling.SwiftBoolMarshaller` | `ConvertToManaged`, `ConvertToUnmanaged` |

`NativeRegistrationOwnership`, `SwiftProtocolRegistry`, `SwiftReverseContext` and
`SwiftBoolMarshaller` are called by generated code; the others are consumer-facing.

## SwiftBindings.Apple

`apple-v26.2.9` from NuGet against the branch's `26.2.10-dev` pack: the public surface is identical
on all four target frameworks (iOS, Mac Catalyst, macOS, tvOS; 123 entries each), 0 removed and
0 added. The packed residual-guard check finds no hazardous import in any copy.

## Apple framework bindings

RealityFoundation, RealityKit, MusicKit and AppIntents were regenerated for iOS on this tree and on
`sdk-v0.20.2` (the same SDK and Xcode for both), and each output's compiled assembly listed member
by member. The candidate is compared in its shipped state: the wrapper is compiled, the module's
lowering receipt collected and the bridges routed, as a package build does. Each assembly was
censused with `swift-transition-gate` and its residual-guard summary. The member counts are the
candidate after the overload-pinning fixes below; before them RealityFoundation lost 19 members and
MusicKit 6. All four modules were regenerated, routed and compiled again on the branch with the
packaging-blocker fixes: no member was removed against the earlier census, RealityFoundation gained
`PhotogrammetrySession(NSUrl)` and the four `FromToByAction` factories, MusicKit the five
`ApplicationMusicPlayer.Queue` factories, and the guarded members are unchanged. The removals are
exactly the rows below that are not marked fixed.

| Module | Public members, sdk-v0.20.2 | Public members, branch | Added | Removed | Guarded, shipped (unrouted) | Skip markers cleared | Markers gained |
|---|---|---|---|---|---|---|---|
| RealityFoundation | 5642 | 5705 | 77 | 14 | 4 (27) | 27 | 0 |
| RealityKit | 294 | 294 | 1 | 1 | 0 (1) | 1 | 0 |
| MusicKit | 2240 | 2245 | 6 | 1 | 0 (1) | 3 | 0 |
| AppIntents | 1798 | 1802 | 7 | 3 | 8 (29) | 32 | 0 |

The additions are mostly specializations the generator now reaches: 57
`Entity.ComponentSet.Set(<Component>)`, `TextureResource` loaders, five MusicKit
`MusicItemCollection` factories and two AppIntents `IntentDonationManager.DonateIntent` forms, plus
generic `!0` properties such as `IntentParameter<T>.WrappedValue`.

### Members public in sdk-v0.20.2 that are now removed or guarded

| Module | Member | Reason | Engines affected | Disposition |
|---|---|---|---|---|
| RealityFoundation | `Entity.ComponentSet.Has(object)`, `Remove(object)` | The parameter is `any Component.Type`, an existential metatype `sdk-v0.20.2` passed as a five-word existential container; Swift expects metadata and a witness table, so every call passed garbage. Now skipped as an unsupported placeholder type. | all | unsafe-before (c0f36040); a metatype projection is post-release |
| RealityFoundation | `SystemDependency.After(…)`, `Before(…)`, `TryGetAfter(…)`, `TryGetBefore(…)` | Enum cases with an `any System.Type` payload, same representation error. | all | unsafe-before (c0f36040) |
| RealityFoundation | `ComponentEvents.{DidActivate, DidAdd, DidChange, WillDeactivate, WillRemove}.ComponentType` (5 getters) | `any Component.Type` result, same representation error. | all | unsafe-before (c0f36040) |
| RealityFoundation | `PhotogrammetrySession.Request.TryGetModelFile(…)` | A `URL` payload in a multi-value case: the extractor handed Objective-C an unbridged Swift storage word. Now skipped because the case's payload bridge does not return a multi-value payload. | all | unsafe-before (137e9592) |
| RealityFoundation | `FromToByAction<Transform>` factories `FromRealityFoundationTransform(Transform)` and `(Transform, Transform)` | The default-omitting call `FromToByAction<Transform>(to:timing:isAdditive:)` is also taken by `init(to:by:mode:timing:isAdditive:)` from `extension FromToByAction where Value == Transform`, so the wrapper could run a different Swift initializer than the one it names. The full forms remain. | all | intended (25909710) |
| RealityFoundation | `FromToByAction<JointTransforms>` factories `FromRealityFoundationJointTransforms(JointTransforms)` and `(JointTransforms, JointTransforms)`; `FromToByAction<BlendShapeWeights>` factories `FromRealityFoundationBlendShapeWeights(…)` ×2 | Declined for the same rival, which exists only where `Value == Transform` and so cannot take a call on these specializations. | all | regression, fixed on this branch: a sibling that pins a type parameter to another concrete type is not a rival of a specialization (`SwiftOverloadCallPinning.PinsAnotherSpecialization`, fixture `PinnedTransition`) |
| RealityFoundation | `PhotogrammetrySession(NSUrl)` | The default-omitting form of `init(input: URL, configuration: = .init())` is declined because `init<S: Sequence>(input: S, configuration:) where S.Element == PhotogrammetrySample` is counted as a rival. `URL` is not such a sequence, so Swift resolves the call to the URL initializer. The full `PhotogrammetrySession(NSUrl, Configuration)` remains. | all | regression, fixed on this branch: a generic sibling whose parameter takes the argument is not a rival when the argument's type provably lacks a protocol the parameter requires. `URL`'s record in `FoundationDatabase.xml` now carries a closed conformance claim, checked against the installed SDKs by `ClosedConformanceSdkAuditTests` (`SwiftOverloadCallPinning.RejectsAsTypeParameter`, fixture `PinnedUrlOrSequenceSession`). Confirmed present in the regenerated RealityFoundation. |
| RealityKit | `ARView.LayerClass` | `class var layerClass: AnyClass`, an existential metatype returned as a five-word container. | all | unsafe-before (c0f36040) |
| MusicKit | `ApplicationMusicPlayer.Queue` factories `FromSwiftSwiftArrayMusicKit{Album,Playlist,Song,Track}…(SwiftArray<…>)` and `FromMusicKitApplicationMusicPlayerQueueEntries(…)` | `ApplicationMusicPlayer.Queue` repeats `MusicPlayer.Queue`'s `required init(for:startingAt:)` and `init(_:startingAt:)` as overrides, but the pinning replay compared their result types (each initializer returns its own class) and kept the superclass initializer as a rival. | all | regression, fixed on this branch: an overriding initializer hides the one it overrides whatever its result type (fixture `PinnedInitDerived`) |
| MusicKit | `MusicCatalogSearchSuggestionsRequest.Create(string)` | Became `new MusicCatalogSearchSuggestionsRequest(string)`: the default-omitting form of `init(term:includingTopResultsOfTypes:)` is now bound as a constructor. The full initializer is skipped (its parameter is `[any MusicCatalogSearchable.Type]`, c0f36040). | all | intended; source-breaking rename, noted in the release notes |
| AppIntents | `IntentDonationMatchingPredicate.IntentType(object, EntityIdentifier)` | `any AppIntent.Type` parameter passed as a five-word container. | all | unsafe-before (c0f36040) |
| AppIntents | `EntityIdentifier.EntityType` | `any AppEntity.Type` result, same error. | all | unsafe-before (c0f36040) |
| AppIntents | `IAppIntentsPackage.IncludedPackages` | A static interface member typed with an existential-metatype array; `sdk-v0.20.2` emitted it as a stub that always threw. | none (it never ran) | intended |
| RealityFoundation | `Entity.PlayAudio(AudioGeneratorConfiguration, Func<…>)`, `PrepareAudio(…)` | Guarded, shape `ReverseCallback`: no native bridge carries a Swift-convention callback yet. Already marked `SB0001` at `sdk-v0.20.2`. Two new overloads without the configuration are guarded the same way. | CoreCLR interpreter x64 | refused-pre-native; recovers with a reverse-callback bridge template |
| AppIntents | `AppShortcutParameterPresentationSummaryString` and `…TitleString`: `.ctor(StringInterpolation)`, `.ctor(string)`, and their `StringInterpolation` `.ctor(int, int)`, `.ctor(nint, nint)` (8) | Guarded, shape `LoweredSignature`: the bridge plan is refused because whether `τ_0_2: IntentParameter<τ_0_1>` takes a witness table is not known. | CoreCLR interpreter x64 | refused-pre-native; recovers with a bridge plan once that generic requirement is modelled |

On iOS the CoreCLR interpreter x64 is a .NET 11 Debug app on an x86_64 simulator; no .NET 10
engine refuses any of these members. No member was withdrawn.

### Members a build that does not route would refuse

The candidate's direct Swift-convention imports that routing replaces (RealityFoundation 23,
RealityKit 1, MusicKit 1, AppIntents 21) made the same call with no guard at `sdk-v0.20.2`:
unsafe-before. Routed, they reach Swift through a bridge on every engine. A binding built without
routing (`SwiftRouteBridges=false`, or no lowering receipt collected) keeps those imports guarded,
and 15 RealityFoundation members and `MultipeerConnectivityService.Owner` (RealityKit), whose shapes
include `SelfRegister`, then throw on Mono. The package pipeline must route.

### Packaging blockers: bindings the SDK generates that its own checks refuse

A dry build of the downstream package set (the Apple framework packages and ten third-party
libraries, .NET 10 + Xcode 26.3, iOS and tvOS simulator, build only) against this branch's packages,
before the fixes recorded under Dispositions, built and packed every library, but nine test apps
failed to build and one sibling package failed to pack. In every case the binding carried a valid
transition manifest that the same SDK wrote, and the pack-time check (`SWIFTBIND133`) or the
consumer check (`SWIFTBIND132`) refused it. At
`sdk-v0.20.2` no such check existed and all of them built, so each is a whole-package regression
for its consumers on every Mono app and every .NET 11 app that is not NativeAOT. There are three
causes.

**1. Swift-convention reverse callbacks (R4).** When a member with a closure parameter, or a
closure-typed property setter, cannot take the `@_cdecl` closure wrapper
(`ClosureEmitter.CanConvertToCdecl` declines it: a constructor, a mutating or generic member, a
member of a generic type, a protocol-existential argument, …), the generator keeps a direct
Swift-convention call and hands Swift the closure as an
`[UnmanagedCallersOnly(CallConvs = [CallConvSwift])]` callback. It guards the member with shape
`ReverseCallback`, which `SwiftTransitionGuard` refuses only on the CoreCLR interpreter x64. The
transition rules treated every Swift-convention `UnmanagedCallersOnly` method as an R4 violation
whether or not a guard covered it, and both checks refused an assembly with any R4 violation.

- `SwiftBindings.Apple.RealityFoundation` does not pack: `Entity.PlayAudio(AudioGeneratorConfiguration, Func<…>)`,
  `PrepareAudio(…)` and their two new overloads, 4 callbacks. RealityKit's app fails through it.
- Five of the ten third-party libraries fail the consumer check for the same reason (2 to 24
  callbacks each), on closure parameters of methods and on closure-typed property setters of
  nested configuration structs.

The BindingTests corpus had no member of this shape (its compile-check assembly scanned R4 = 0),
so neither the compile gate nor the transition gate met it.

**2. Lowering predicted differently from what swiftc compiles (one library).** The SDK routed two
members through bridges whose plans disagree with the collected lowering receipt: an initializer
whose swiftc lowering is `swiftcc ptr (ptr noalias, ptr noalias, ptr, i64, ptr, i64, ptr, ptr swiftself)`,
and a getter the plan lowers through an indirect (sret) result while swiftc returns `ptr`. The
consumer check refuses each as "planned through `SBSR_…`, but its lowering evidence does not make it
executable".

**3. Evidence collection cannot name some types (one library, four modules).** The receipt
collector's probe fails on types the module surfaces under another name or through Objective-C
(`no type named '…' in module '…'`, `swiftc shows no $s…`), yet the plans still route those members:
42, 89, 61 and 9 refused checks across the four modules.

#### Dispositions

| Cause | Disposition | Commits |
|---|---|---|
| 1. Swift-convention reverse callbacks (R4) | **Guarded acceptance**, one check for the transition gate, SWIFTBIND133 and SWIFTBIND132 (all three run `ResidualGuardCheck.AcceptedReverseCallbacks`). A private Swift-convention callback whose address stays in a private static field and reaches Swift only through members guarded for every shape of its signature passes R4. A public, exported, escaping, unguarded or under-guarded callback, or one whose signature carries `SwiftSelf<T>`, still fails R4 in all three. **Root fixes** for values that crossed wrongly: result completions with metadata-backed payloads take the `@_cdecl` adapter; Swift-lane address-only results are written through the indirect-result register; `Optional` value-wrapper arguments and results go through `Optional<T>`; class results return at +1; results Swift explodes across return registers are refused at generation. **Engine refusals:** `ReverseCallback` on the CoreCLR interpreter x64. A callback that takes both an error and an indirect result also carries `SpecialBeforeArguments`, which CoreCLR without a JIT refuses on arm64: the interpreter, and ReadyToRun iOS and tvOS apps, which share its mask. | `bdd363fe`, `bced45db`, `9ac60e18`, `b5ebaa13`, `5bd79fcb` |
| 2. Lowering predicted differently from swiftc | **Predictor fixed**: a class-bound generic value, and its Optional, is passed and returned as one reference, including Apple protocols that are class-bound only through their own refinement. A plan the receipt contradicts stays fatal rather than falling back: the guarded direct import would make the same wrong call. | `0b43aed1` |
| 3. Evidence collection cannot name some types | A forward plan to a target the public module interface cannot name (internal, `@usableFromInline`, SPI) is refused as `NotPublicFromInterface` and stays a guarded direct import, instead of an evidence gap that refuses the package. | `28a9b4bf` |

Downstream dry build (.NET 10 + Xcode 26.3, iOS and tvOS simulator, build only, local feed of this
branch's packages):

| | Library builds | App builds | Sibling packs |
|---|---|---|---|
| Before | all pass | 9 fail SWIFTBIND132 | RealityFoundation fails SWIFTBIND133 |
| After | 27 / 27 | 32 / 32 (26 iOS, 6 tvOS) | 2 / 2 |

The packaging blockers are closed. Each cause has a first-party BindingTests fixture or unit
fixture of its shape; the downstream patches and logs are outside this repository.

## Totals

| Surface | Rows (removed, reshaped, skipped or guarded at any point on the branch) | unsafe-before | refused-pre-native | withdrawn | intended | regression fixed | regression open |
|---|---|---|---|---|---|---|---|
| BindingTests corpus | 3 | 0 | 2 | 0 | 1 | 0 | 0 |
| SwiftBindings.Runtime | 0 | | | | | | |
| SwiftBindings.Apple | 0 | | | | | | |
| RealityFoundation | 21 | 12 | 2 | 0 | 2 | 5 | 0 |
| RealityKit | 1 | 1 | 0 | 0 | 0 | 0 | 0 |
| MusicKit | 6 | 0 | 0 | 0 | 1 | 5 | 0 |
| AppIntents | 11 | 2 | 8 | 0 | 1 | 0 | 0 |
| **All** | **42** | **15** | **12** | **0** | **5** | **10** | **0** |

The 10 fixed rows are public again; 32 members remain removed, reshaped or guarded. The three
packaging causes above are closed: every downstream library packs and every test app builds.

## Candidate changes since the first measurement

The candidate (`4600d8f2`) adds `defect-closure-3`, the packaging-blocker fixes and four
generation batches to the tree the sections above first measured. Against that measurement the API
manifest gains 177 members and loses none, and the skip surface moves by 24 markers, each from a
fixture added after it:

| Change | Markers | Source |
|---|---|---|
| `Existential type fallback` (`UnsupportedSwiftType`) | 21 → 35 | `Lifetime/NoProxyExistentialReturns.swift` (`500f011b`): members of the new fixture whose existential has no proxy. They are fixture members, never public before. |
| `SB0001` "a closure parameter shape the C wrapper bridge cannot marshal" | 1 → 0 | `500f011b`: the member now takes the closure wrapper. |
| `Unsupported`: generic constructors with method-own type parameters | +3 | `PinnedUrlOrSequenceSession.init` (`b1c56c11`), `FlooredAdopter.init`, `FlooredHost.init` (`15a4b4fe`) |
| `Unsupported`: constrained-extension properties on a single specialization | +8 | `PinBox.Op.Options.first`, `.second` (2 each), `.rawValue`, `PinBox.Op.label`, `PinBox.Op.tag`, `PinBox.Unit.rawValue` (`15a4b4fe`, `Generics/ConstrainedExtensionNestedTypes.swift`) |

No member present at `sdk-v0.20.2` gained a marker. The reverse-callback disposition (cause 1) is
delivered as recorded under Dispositions: the packed SDK's `SwiftBindings.Tasks.dll` carries
`ResidualGuardCheck.AcceptedReverseCallbacks`, and the release checkpoint below exercised it.

## Release checkpoint on the candidate

One package set was packed from `4600d8f2` on the `net10-xcode26` profile
(`nuke pack --version 0.21.0-ck.1 --apple-version 26.2.10-ck.1`; ApiCompat against
`SwiftBindings.Runtime 0.20.2`, transition evidence and the packed residual-guard check passed) and
consumed unchanged on all three supported pairings, each with its own feed copy and package cache.

| Package | sha256 |
|---|---|
| `SwiftBindings.Runtime 0.21.0-ck.1` | `3d6c794738340432ebd944641033cd0099757c360f3d75cebb32b62705d4f1d3` |
| `SwiftBindings.Sdk 0.21.0-ck.1` | `6d3ab76fab24e951a3e4a72697eead9024b0a3b8417a41d3d5bd5d59afc4d165` |
| `SwiftBindings.Templates 0.21.0-ck.1` | `3a857e41f13d73d09a95172bbe876d524853a86d98b8fe09010c00c3fa3a5846` |
| `SwiftBindings.Apple 26.2.10-ck.1` | `5d5d3be011dfc2b244c5f8e71b89ee6c11bf29378793da3e0db2e3a66ad0420a` |

The packed `SwiftBindings.Tasks.dll` carries `ResidualGuardCheck.AcceptedReverseCallbacks`.

### Consumption on the three pairings

Each pairing restored from an empty private package cache and its own copy of the feed, in its own
copy of the downstream checkout with no `bin/` or `obj/`, and built Debug: the package, then its
iOS (and, where it has one, tvOS) simulator test app. The checkpoint's time box covered the
packages below; the third-party libraries, the internal binding-testing apps and the remaining
Apple framework packages were not reached and stay to be run.

| Package | `net10-xcode26` | `net10-xcode27` | `net11-xcode27` | Test apps |
|---|---|---|---|---|
| RealityFoundation sibling pack | pass | pass | pass | |
| Matter sibling pack | pass | pass | pass | |
| RealityFoundation | pass | pass | pass | iOS: pass ×3 |
| RealityKit (consumes the RealityFoundation sibling) | pass | pass | pass | iOS: pass ×3 |
| AppIntents | pass | pass | pass | none in the package |
| LiveCommunicationKit | pass | pass | pass | iOS: pass ×3 |
| Translation | pass | pass | pass | iOS: pass ×3 |
| MusicKit | pass | pass | pass | iOS, tvOS: pass ×3 |

No restore, build or consumer transition check failed.

The RealityFoundation sibling package was packed first on each pairing and RealityKit restored that
package from the pairing's local feed: the nupkg in each private cache hashes to the one packed
(`0bcd282b…` on `net10-xcode26`, `35d40d5b…` on `net10-xcode27`, `34e11d3d…` on `net11-xcode27`; the
three differ because each pairing generates its own binding). The packed sibling assembly declares
`PhotogrammetrySession(NSUrl)` on all three. RealityFoundation on both .NET 10 pairings and
RealityKit on `net11-xcode27` were built a second time in the same checkout, nothing cleaned: each
rebuilt incrementally and passed.

**Negative control.** A first-party reverse-callback fixture bound through the packed SDK
(`net10-xcode26`), in two copies: as generated, and with one added Swift-convention
`[UnmanagedCallersOnly]` callback handed to an import that no transition guard covers. Each was
packed and consumed through a `ProjectReference` by a simulator app. The doctored copy's pack fails
with `SWIFTBIND133` (and the package is deleted) and its app build with `SWIFTBIND132`, both naming
the doctored callback. The generated copy packs with its guarded generated callbacks accepted, and
the app's transition check records it `Verified`.

### Surface on the candidate

Member-level diff of each binding's compiled iOS assembly: `sdk-v0.20.2` against the candidate on
Xcode 26.3, and the candidate on Xcode 26.3 against Xcode 27.2 (`net10-xcode27`; `net11-xcode27`
generates the same binding). The silent-drop checker compares each module's public ABI against
the emitted members and the skip report; a row is real only if the member is public, unrenamed and
absent from both.

| Module | Members: sdk-v0.20.2 → candidate, Xcode 26.3 | sdk-v0.20.2 → candidate: removed, guarded | Candidate, Xcode 26.3 → 27.2 | Lost on 27.2 | Checker rows new on the candidate |
|---|---|---|---|---|---|
| RealityFoundation | 5642 → 5705 | exactly the rows above | 5705 → 6946 | 29: 27 moved (Apple), 2 declined (regression) | 0 real (18 on 27.2, all renamed types, skip-reported or existing channels) |
| RealityKit | 294 → 294 | `ARView.LayerClass` (above) | identical | 0 | 0 |
| AppIntents | 1798 → 1802 | exactly the rows above | 1802 → 2047 | 35 types (regression), 1 reshaped and 1 obsoleted (Apple) | 0 real (63 on 27.2, all API new in 27.2 in existing channels or renamed) |
| LiveCommunicationKit | 286 → 286 | none | 286 → 341 | 0 | 0 real (1 on 27.2, a requirement of a new protocol) |
| Translation | 74 → 74 | none | 74 → 96 | 0 | 0 (none on any run) |
| MusicKit | 2240 → 2245 | the rows above, and `MusicLibrarySectionedRequest<,>()` guarded because the build does not route (see the defects) | 2245 → 2264 | 0 | 0 |

Against `sdk-v0.20.2` the candidate on Xcode 26.3 removes or guards no member beyond the rows already
recorded above, and no member any checker run flags is newly dropped. Every row the checker reports
on the candidate also exists at `sdk-v0.20.2` (the shapes recorded in the binding plan: protocol
requirements and protocol-extension members, raw-value initializers and Codable initializers of
enums projected as C# enums, caseless namespace enums) or is a false positive (a nested type
emitted under an `…Info` name beside a same-named property, a member the skip report names).

The Xcode 27.2 losses:

- **RealityFoundation `Audio.Directivity`, `Audio.DistanceAttenuation`** and their `CaseTag`s (27
  members): Apple added static members to `public enum Audio` in the 27.2 SDK
  (`Audio.prepareAudio(_:)`, `Audio.playAudio(_:)`, `playAudio(_:at:)`), so `Audio` is no longer a
  caseless namespace enum and is emitted as a class. Both types reappear nested in it member for
  member; the C# spelling `Audio.Directivity` is unchanged, the binary identity is not.
- **RealityFoundation `AudioFileResource.CreateAsync(NSUrl, string, …)` and
  `CreateAsync(string, NSBundle, …)`**: the 27.2 SDK adds `init(from: Data, configuration:)`, and
  the default-parameter overload emitter treats any same-named constructor of equal arity as a
  collision without comparing parameter types, so the forms that omit only `configuration` are
  declined. Recorded in the emission report's `suppressedAmbiguousOverloads`; the full forms remain.
  Regression, open.
- **AppIntents, 35 `…CsmExtensions` types** (`EmptyResolverSpecification`, `EntityProperty`,
  `IntentItem`, `IntentItemCollection`, `IntentItemSection`, `IntentParameter`,
  `IntentParameterContext` × `EntityIdentifier`, `IntentCurrencyAmount`, `IntentFile`,
  `IntentPaymentMethod`, `IntentPerson`): the 27.2 interface spells the conformances with module
  selectors (`extension AppIntents::EntityIdentifier : AppIntents::_IntentValue`), which
  `UnderscoreProtocolSynthesizer`'s extension-header pattern does not match, so the stripped
  `_IntentValue` conformances are never re-attached and no conformer reaches the specialization
  emitter. Neither emitted nor in the skip report. Regression, open: it does not affect the
  supplement generated on Xcode 26.3, but a consumer generating AppIntents on Xcode 27.2 loses these
  types silently.
- **AppIntents `IntentFile.DefaultResolverSpecification`** becomes `object`: the 27.2 SDK declares
  its `Specification` as an opaque result type. **`AppIntentError.Unrecoverable.Unknown`** is
  `[Obsolete]`: Apple deprecated it in 27.2.

### Defects found

| Defect | Pairings | Effect | Owning component | Status |
|---|---|---|---|---|
| A closure whose parameters include `UnsafePointer<T>` gets a wrapper adapter that passes the typed pointer where the `@convention(c)` thunk takes a raw pointer; the wrapper does not compile and the SDK path, which has no member-level recovery, fails the whole binding with `SWIFTBIND051`. | all (seen on `net10-xcode26`) | whole package | generator closure-adapter emission; SDK wrapper-compile recovery | open |
| The Xcode 27.2 interfaces' module selectors (`AppIntents::T`) defeat `UnderscoreProtocolSynthesizer`'s extension-header pattern: 35 AppIntents types silently lost. | Xcode 27.2 | 35 types, silent | parser | open |
| The default-parameter overload emitter compares constructors by arity only: 2 RealityFoundation overloads declined on Xcode 27.2. | Xcode 27.2 | 2 members, recorded | emitter | open |
| The wrapper xcframework compiled in the lowering-receipt path names its fat simulator slice `ios-arm64-simulator` (Info.plist lists arm64 and x86_64), while validate's PackGate expects `ios-arm64_x86_64-simulator`: PackGate is red. No consumer build failed. | all (seen on `net11-xcode27`; the same layout is in packages built on `net10-xcode26`) | gate | SDK/generator slice naming or PackGate's slice table | open |
| validate pins Apple-framework targets to platform version 26.2, whose reference packs the Xcode 27.2 profiles do not install: every Apple-framework target fails generation there, and an unfiltered validate on those profiles cannot pass. | Xcode 27.2 | gate | validate target selection × profile packs | open |
| The downstream MusicKit build collects no lowering receipt and routes nothing, so `MusicLibrarySectionedRequest<,>()` ships guarded (shape `LoweredSignature`) instead of routed. Cause not found. | all | 1 member, CoreCLR interpreter x64 only | SDK receipt collection and routing | open |
