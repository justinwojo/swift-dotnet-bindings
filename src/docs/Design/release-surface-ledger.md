# Release surface ledger

This ledger accounts for the public surface of the release candidate against the last shipped
release (SDK `sdk-v0.20.2`, Apple supplement `apple-v26.2.9`). It covers four surfaces: the
BindingTests corpus, `SwiftBindings.Runtime`, `SwiftBindings.Apple`, and the Apple framework
bindings regenerated with both generators: RealityFoundation, RealityKit, MusicKit and AppIntents
first, then, in the release checkpoint at the end of this file, every shipped Apple framework
package and the downstream third-party bindings.

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

| Measure | sdk-v0.20.2 | Branch (first measurement) | Candidate (`4600d8f2`) | Candidate (`5ab97b23`) | Change against sdk-v0.20.2 |
|---|---|---|---|---|---|
| API-manifest members | 6146 | 6983 | 7160 | 7262 | 1116 added, 0 removed: every (module, signature) entry of `sdk-v0.20.2` is in the candidate's baseline |
| Members whose entry point moved (same C# signature) | | 43 | 43 | 43 | 15 direct Swift call → `@_cdecl` wrapper, 27 `@_cdecl` wrapper → native thunk, 1 wrapper → wrapper |
| Skip-surface markers | 348 | 348 | 372 | 372 | `SB0001` 23 → 10, `Unsupported` 284 → 305, `UnsupportedSwiftType` 41 → 57 |
| Members carrying a transition guard | none (no guard existed) | 3 | not re-measured | not re-measured | see below |

The candidate columns read the committed API-manifest and skip-surface baselines; the moved entry
points are the API manifest's own entry-point column, unchanged since the first measurement. The
24 markers gained since the first measurement all come from fixtures added after it and are listed
under "Candidate changes since the first measurement" below. Between `4600d8f2` and `5ab97b23` the
manifest gains 102 entries and loses or moves none (baseline `git_sha` `11a9e29c`), and the
skip-surface baseline is unchanged.

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

The release checkpoint's census of every other shipped Apple framework package (below) adds these
rows:

| Module | Member | Reason | Engines affected | Disposition |
|---|---|---|---|---|
| ProximityReader | `PaymentCardTransactionRequest.TransactionAmountDescription.TryGetInstallment(PaymentCycle&, NSDecimalNumber&, nint&)` | A `Foundation.Decimal` in a multi-value case payload: `sdk-v0.20.2` read the Swift value as an Objective-C object pointer. Now skipped ("stored as Swift values, not as the Objective-C objects"), the `TryGetModelFile` cause. | all | unsafe-before (137e9592) |
| TipKit | `TipGroup.CurrentTipUpdates` | `some AsyncSequence<any Tip, Never>` was projected as `object`: an existential container copied as raw bytes and never destroyed, so each read leaked one owned value and the result could not be iterated. Now skipped as an async stream with an unsupported element. | all | unsafe-before (4dd967ff) |
| TipKit | `TipUIView.FromTipKitAnyTip(AnyTip)` | The specialization of the `@_disfavoredOverload` generic `init(_ tip: some Tip, …)`; its wrapper's call resolved to the favored `init(_ tip: any Tip, …)`, so it never ran the declaration it named. Refused by call pinning and recorded in the emission report's `suppressedAmbiguousOverloads`. The same initializer is `new TipUIView(ITip)`, which `AnyTip` implements, with the same availability. | all | intended (fec82d1b) |
| TipKit | `Tips.OptionsBuilder.BuildFinalResult(IgnoresDisplayFrequency)`, `(MaxDisplayCount)`, `(MaxDisplayDuration)` | Specializations of `buildFinalResult(_: some TipOption)`, whose generic wrapper the verify-recover loop withdraws at both points. Each specialization is now emitted inside its generic method's recovery unit, so withdrawing the method takes them with it; at `sdk-v0.20.2` they were sound (sole overload, no defaults, correct marshalling) and stayed. The skip report names only the generic method. Result-builder plumbing Apple hides from its documentation. | all | regression, open (262ad429): a withdrawn generic method withdraws its sound specializations, without a report row for them |

On iOS the CoreCLR interpreter x64 is a .NET 11 Debug app on an x86_64 simulator; no .NET 10
engine refuses any of these members. No member was withdrawn, apart from the three TipKit
specializations above.

### Parameter names changed since sdk-v0.20.2

c2cd4428 gives each overload of a specialized or default-omitting family its own parameter names;
before it, a member could borrow a sibling's names. Every rename below keeps the C# signature, so
positional callers compile unchanged and only named arguments break. Xcode 26.3, `sdk-v0.20.2`
against the candidate:

| Module | Renamed | Members (old → new) |
|---|---|---|
| RealityFoundation | 16 | `AnchorEntity(Vector3)` transform → position; `BoundingBox.Union`, `Contains`, `FormUnion(Vector3)` other/boundingBox → point; `ShaderGraphMaterial.CreateAsync(string, byte[])` url → data; `ShapeResource.GenerateConvex(MeshResource)`, `GenerateConvexAsync(MeshResource)` points → mesh; `MeshResource.Generate`/`GenerateAsync`/`CreateAsync` forms; `Entity.ChildCollection.Append` children → array/sequence; `Entity.ComponentSet.Set<T>` components → component; `Scene.AnchorCollection.Append` sequence → array; `CustomMaterial.CustomMaterialTexture(TextureResource)` texture → resource |
| AppIntents | 4 | `EnumURLRepresentation(string)` and two specialization factories urlRepresentations → value; `NegativeAppShortcutPhrase.StringInterpolation.AppendInterpolation(AppShortcutPhraseToken)` subject → token |
| MusicKit | 6 | `MusicPlayer.Queue.InsertAsync(Album \| Playlist \| RecentlyPlayedMusicItem \| Song \| Station \| Track, …)` entry → playableItem |
| VisionKit | 3 | `ImageAnalyzer.AnalyzeAsync(CGImage \| CIImage \| UIImage, …)` pixelBuffer → cgImage / ciImage / image |
| TipKit | 2 | `Tips.Rule(CompoundOperation, IEnumerable<Rule>)` parameter, parameterBuilder → operation, rules; `Tips.ActionBuilder.BuildExpression(Action)` component → element |
| RoomPlan | 1 | `CapturedRoom.ModelProvider.ModelFileURL(Object)` category → object |

No other shipped Apple framework package renames a parameter.

### Members a build that does not route would refuse

The candidate's direct Swift-convention imports that routing replaces (RealityFoundation 23,
RealityKit 1, MusicKit 1, AppIntents 21) made the same call with no guard at `sdk-v0.20.2`:
unsafe-before. Routed, they reach Swift through a bridge on every engine. A binding built without
routing (`SwiftRouteBridges=false`, or no lowering receipt collected) keeps those imports guarded,
and 15 RealityFoundation members and `MultipeerConnectivityService.Owner` (RealityKit), whose shapes
include `SelfRegister`, then throw on Mono. The package pipeline must route. Routing cannot reach a
member whose plan is refused as `NotPublicFromInterface` (packaging-blocker cause 3 below): such a
member stays guarded in every build, and where its shapes include `SelfRegister` it throws on Mono
for good. The release checkpoint met this in one third-party library family: 159 guarded
transitions refused this way, most of the 165 members that family loses on Mono (its defects
table).

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
| ProximityReader | 1 | 1 | 0 | 0 | 0 | 0 | 0 |
| TipKit | 5 | 1 | 0 | 0 | 1 | 0 | 3 |
| Other shipped Apple framework packages (ActivityKit, CryptoKit, FamilyControls, LiveCommunicationKit, Matter, MatterSupport, RoomPlan, StoreKit2, Translation, VisionKit, WeatherKit, WorkoutKit) | 0 | | | | | | |
| **All** | **48** | **17** | **12** | **0** | **6** | **10** | **3** |

The 10 fixed rows are public again; 38 members remain removed, reshaped or guarded, three of them
an open regression (the TipKit specializations). The three packaging causes above are closed: every
downstream library packs and every test app builds. Parameter renames are counted in their own
table above, not here.

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

The second checkpoint packed one package set from `5ab97b23` on the `net10-xcode26` profile
(`nuke pack --version 0.21.0-ck.2 --apple-version 26.2.10-ck.2`) and consumed it unchanged on all
three supported pairings. It supersedes the first checkpoint (`4600d8f2`, `0.21.0-ck.1`), whose
results are in this file's history. ApiCompat against `SwiftBindings.Runtime 0.20.2` passed, and so
did the transition evidence: the Runtime is `PresentValid` on its five frameworks (4 bridges, 4
twins, 28 imports) and the Apple supplement on two manifests (20 imports). The packed
residual-guard check found no hazardous import.

| Package | sha256 |
|---|---|
| `SwiftBindings.Runtime 0.21.0-ck.2` | `c22753bdafd258a4744345950145bd247cf6908f36e5c9862ddbb7744bebddf0` |
| `SwiftBindings.Sdk 0.21.0-ck.2` | `25f29329f47b9c3b4cb03470cca2c4f8843babebcb5fadab89ca93691315d5c0` |
| `SwiftBindings.Templates 0.21.0-ck.2` | `33b7a7fb8d5f3bc9d7fce67a261a266fe3db8241382dd6af7e2a252cffca5c60` |
| `SwiftBindings.Apple 26.2.10-ck.2` | `7f161b4efef9225e186621a5bbd82d06621b9b0ac06407949b0105c4eaa0ffe8` |

### Consumption on the three pairings

Each pairing restored from an empty private package cache and its own copy of the feed, in its own
copy of the downstream checkout with no `bin/` or `obj/`, and built every package (its library,
then its test apps). Every resolved `SwiftBindings.Runtime` and `SwiftBindings.Apple` in every
pairing's cache hashes to the packed nupkg above. Test apps ran on the iOS and tvOS simulators:
.NET 10 apps are Debug on Mono; .NET 11 apps run twice, Debug and Release. A .NET 11 Debug app runs
its own assembly and every binding it builds on the CoreCLR interpreter (the ReadyToRun composite
holds the framework and package assemblies only); Release adds the app to the composite.

| Package | Library: 26.3 / 27.2 / net11 | `net10-xcode26` | `net10-xcode27` | `net11-xcode27` Debug | `net11-xcode27` Release |
|---|---|---|---|---|---|
| Matter | pass / pass / pass | iOS pass 19/0/1 | iOS pass 19/0/1 | iOS pass 19/0/1 | iOS pass 19/0/1 |
| RealityFoundation | pass / pass / pass | iOS pass 42/0/3 | iOS pass 42/0/3 | iOS pass 42/0/3 | iOS pass 42/0/3 |
| MusicKit | pass / pass / pass | iOS pass 45/0/1, tvOS pass 44/0/2 | iOS pass 45/0/1, tvOS pass 44/0/2 | iOS crash, tvOS crash | iOS crash, tvOS crash |
| Translation | pass / pass / pass | iOS pass 15/0/1 | iOS pass 15/0/1 | iOS pass 15/0/1 | iOS pass 15/0/1 |
| LiveCommunicationKit | pass / pass / pass | iOS pass 23/0/1 | iOS pass 23/0/1 | iOS pass 23/0/1 (second run; the first timed out with no failure) | iOS pass 23/0/1 |
| RealityKit | pass / pass / pass | iOS pass 20/0/0 | iOS pass 20/0/0 | iOS pass 20/0/0 | iOS pass 20/0/0 |
| AppIntents | pass / pass / pass | — | — | — | — |
| MusicUnderstanding | — / pass / pass | — | iOS pass 39/0/0 | iOS pass 39/0/0 | iOS pass 39/0/0 |
| ActivityKit | pass / pass / pass | iOS pass 27/0/0 | iOS pass 24/0/3 | iOS pass 27/0/0 | iOS pass 27/0/0 |
| CryptoKit | pass / pass / pass | iOS pass 50/0/1, tvOS pass 50/0/1 | iOS pass 50/0/1, tvOS not run (the simulator install timed out twice, 240 s each) | iOS pass 50/0/1, tvOS pass 50/0/1 | iOS pass 50/0/1, tvOS pass 50/0/1 |
| FamilyControls | pass / pass / pass | iOS pass 16/0/0 | iOS pass 16/0/0 | iOS pass 16/0/0 | iOS pass 16/0/0 |
| MatterSupport | pass / pass / pass | iOS pass 10/0/1 | iOS pass 10/0/1 | iOS pass 10/0/1 | iOS pass 10/0/1 |
| ProximityReader | pass / pass / pass | iOS pass 11/0/0 | iOS pass 11/0/0 | iOS pass 11/0/0 | iOS pass 11/0/0 |
| RoomPlan | pass / pass / pass | iOS pass 34/0/2 | iOS pass 34/0/2 | iOS pass 34/0/2 | iOS pass 34/0/2 |
| StoreKit2 | pass / pass / pass | iOS unqualified 44/0/0, tvOS unqualified 39/0/0 | iOS unqualified 44/0/0, tvOS unqualified 39/0/0 | iOS unqualified 44/0/0, tvOS unqualified 39/0/0 | iOS unqualified 44/0/0, tvOS unqualified 39/0/0 |
| TipKit | pass / pass / pass | iOS pass 21/0/1, tvOS pass 21/0/1 | iOS pass 21/0/1, tvOS pass 21/0/1 | iOS pass 21/0/1, tvOS pass 21/0/1 | iOS pass 21/0/1, tvOS pass 21/0/1 |
| VisionKit | pass / pass / pass | iOS pass 42/0/0 | iOS pass 42/0/0 | iOS fail 40/1/1, then pass 42/0/0 on a second run | iOS fail 40/1/1 (not run again) |
| WeatherKit | pass / pass / pass | iOS pass 27/0/1, tvOS pass 27/0/1 | iOS pass 27/0/1, tvOS pass 27/0/1 | iOS pass 27/0/1, tvOS pass 27/0/1 | iOS pass 27/0/1, tvOS pass 27/0/1 |
| WorkoutKit | pass / pass / pass | iOS pass 29/0/0 | iOS pass 29/0/0 | iOS pass 29/0/0 | iOS pass 29/0/0 |
| Third-party bindings (9 libraries) | 9 / 9 / 9 built | 8 of 8 run pass; one library's apps built, not run | 9 of 9 pass | 8 of 9 pass: one library's app fails 160/1/0 (below) | 8 of 8 run pass; one library's Release app built, not run |
| Internal binding-testing apps (15 libraries) | generator CLI | 14 of 15 pass: one fails 32/1/0 (below) | 14 of 15 pass: the same one fails 32/1/0 | 14 of 15 pass: the same one fails 32/1/0 | 14 of 15 pass: the same one fails 32/1/0 |

Cells are passed/failed/skipped tests. AppIntents has no test app. StoreKit2 reports `UNQUALIFIED` on every lane: its harness passes but withholds qualification because the live App Store sandbox checks cannot run on a simulator. The ActivityKit app skips three tests on the iOS 27.2 simulator under .NET 10 that run on .NET 11 there. The CryptoKit tvOS app built on `net10-xcode27`, but the tvOS 27.2 simulator never finished installing it; the same simulator installed and ran both .NET 11 CryptoKit tvOS apps. One third-party library's apps were built on `net10-xcode26`, and its Release app on `net11-xcode27`, but not run: the shared simulator was held by another job when the checkpoint closed. The MusicUnderstanding row is the smoke project, built against the `net10-xcode27` and `net11-xcode27` libraries.

**Incremental rebuild.** LiveCommunicationKit was built a second time in the same checkout on
`net10-xcode26` and `net11-xcode27`, nothing cleaned: the library rebuilt in 8 s and 6 s (54 s and
75 s clean) and the app in 4 s (54 s and 17 s), and both passed.

### Documentation claims

| Claim | Result | Evidence |
|---|---|---|
| A binding app publishes with NativeAOT on .NET 10 + Xcode 27.2 | true (build only; no device in this checkpoint) | A third-party library's test app published for `ios-arm64` with `PublishAot=true` on `net10-xcode27`: exit 0, an arm64 executable and no managed assemblies in the bundle. The only AOT warning (IL3050) is in the shared test harness. |
| A .NET 11 app consumes packages built with .NET 10 | true | A .NET 11 iOS app (`net11-xcode27`) referencing a third-party binding packed on .NET 10 with the Runtime and Apple packages: the transition check records all three `Verified`, and the app passes Debug (interpreter) and Release (ReadyToRun), 85 passed, 0 failed, 3 skipped each. |
| A package packed with Xcode 27.2 is consumed with Xcode 26.3 | true | The same binding packed on `net10-xcode27` (`021d8406…`), restored and built on `net10-xcode26`: the transition check records it `Verified`. Its test app passes on the iOS 26.3 simulator on Mono: 85 passed, 0 failed, 3 skipped. |
| A versionless `net10.0-ios` binding packs as `net10.0-ios26.0` on both .NET 10 pairings | true | The reverse-callback fixture packs `lib/net10.0-ios26.0/` on Xcode 26.3 and on Xcode 27.2, and the third-party binding packs `lib/net10.0-{ios,macos,tvos}26.0/` on Xcode 27.2. |

### Upgrade from the published packages

The published MusicKit package (`26.2.16`, built on `sdk-v0.20.2`, `8f6117fb…`), consumed with the
candidate's Runtime and Apple packages on each pairing:

- **Restore** succeeds with the `0.21.0-ck.2` labels, which sort below `0.21.0` and so satisfy the
  published package's `SwiftBindings.Runtime [0.20.2, 0.21.0)`. Relabelled as the final `0.21.0`
  (`net10-xcode26`), a consumer that references the Runtime directly restores with `NU1608`; one
  that references only the Apple and MusicKit packages fails with `NU1107`, because the Apple
  package requires Runtime `>= 0.21.0` and MusicKit `< 0.21.0`.
- **Build** fails on every pairing, Debug and Release, with `SWIFTBIND132`: the published assembly
  carries no transition manifest and two of its Swift transitions are not primitive-only (the
  `MusicCatalogSearchSuggestionsRequest` and `MusicLibrarySectionedRequest` initializers, both direct
  Swift-convention imports). No `SWIFTBIND133` applies (nothing is packed).
- **With `<SwiftBindingsTransitionSuppress Include="SwiftBindings.Apple.MusicKit" />`** it builds on
  every pairing (the verdict stays `Refused`, suppressed). The app loads the binding through the new
  Runtime and its calls run: the `RuntimeContract` check accepts the published binding's epoch.
  The suppressed MusicKit test app passes on Mono on both .NET 10 pairings (45 passed, 0 failed, 1
  skipped) and crashes on .NET 11, Debug and Release, after about 40 passing tests, at the search
  request initializer: the shim defect below, which the published package carries too.

### Negative control

The first-party reverse-callback fixture, bound through the packed SDK on `net10-xcode26` in two
copies: as generated, and with one Swift-convention `[UnmanagedCallersOnly]` callback added and
handed to an import no transition guard covers. The doctored copy's pack fails with `SWIFTBIND133`
naming `R4Probe.Doctored.Factory::Body_Callback` (and the package is deleted), and an app consuming
it through a `ProjectReference` fails with `SWIFTBIND132`. The generated copy packs and its app's
transition check records it `Verified`.

### MusicKit on the CoreCLR interpreter x64

`MusicLibrarySectionedRequest<,>()` now routes: on every pairing and every slice family (iOS, Mac
Catalyst, tvOS) the build collects a lowering receipt and its routing sidecar rewrites the
initializer's import to its bridge, and the member's guard is inert. A .NET 11 Debug Mac Catalyst
app for `maccatalyst-x64` (the MusicKit binding outside the ReadyToRun composite, so on the
interpreter) built with the binding `Verified` and, run under Rosetta, ran the routed initializer and passed it (`MusicLibrarySectionedRequest<Artist, Album>()`); the app then stopped with a bus error in a later test, consistent with the MusicKit search-shim defect below (not separately verified on x64).

### Surface on the candidate

Member-level diff of each binding's compiled iOS assembly, listed type by type and member by member:
`sdk-v0.20.2` on Xcode 26.3 against the candidate on Xcode 26.3 (`net10-xcode26`), and the candidate
on Xcode 26.3 against Xcode 27.2 (`net10-xcode27`; `net11-xcode27` generates the same assembly,
member for member). Rows count types and members. Guarded counts are members a transition guard
refuses on some engine; each guard's engines are its own record. The silent-drop checker compares
each module's public Swift ABI with the emitted members and the skip report.

| Package | Rows: sdk-v0.20.2 → 26.3 → 27.2 | sdk-v0.20.2 → 26.3: added, removed, guarded | Parameter renames | Xcode 26.3 → 27.2: lost | Checker rows new on the candidate |
|---|---|---|---|---|---|
| RealityFoundation | 6303 → 6368 → 7735 | 79, 14, 2 (rows above) | 16 | 27 moved by Apple (below) | 0 |
| RealityKit | 324 → 324 → 324 | 1, 1, 0 (rows above) | 0 | 0 | 0 |
| MusicKit | 2700 → 2705 → 2725 | 6, 1, 0 (rows above) | 6 | 0 | 0 |
| AppIntents | 2189 → 2197 → 2584 | 11, 3, 8 (rows above) | 4 | 1 reshaped, 1 obsoleted (Apple, below) | 0 |
| LiveCommunicationKit | 322 → 322 → 402 | 0, 0, 0 | 0 | 0 | 0 |
| Translation | 83 → 83 → 106 | 0, 0, 0 | 0 | 0 | 0 |
| ActivityKit | 150 → 152 → 152 | 2, 0, 0 | 0 | 0 | 0 |
| CryptoKit | 1542 → 1632 → 1661 | 90, 0, 0 | 0 | 0 | 0 |
| FamilyControls | 91 → 91 → 105 | 0, 0, 0 | 0 | 0 | 0 |
| Matter | 34406 → 34406 → 37958 | 0, 0, 0 | 0 | 131 members, 10 types removed by Apple (below) | not run (Objective-C module) |
| MatterSupport | 180 → 180 → 180 | 0, 0, 0 | 0 | 0 | 0 |
| ProximityReader | 1297 → 1296 → 1555 | 0, 1, 0 (row above) | 0 | 3 obsoleted (Apple, below) | 0 |
| RoomPlan | 548 → 549 → 549 | 1, 0, 0 | 1 | 0 | 0 |
| StoreKit2 | 1013 → 1015 → 1213 | 2, 0, 0 (one added member, `VerificationResult<T>.PayloadValue`, is guarded on its own: never public before) | 0 | 0 | 0 on 26.3; 4 on 27.2, all API the 27.2 SDK adds |
| TipKit | 355 → 351 → 353 | 1, 5, 0 (rows above) | 2 | 0 | 0 |
| VisionKit | 273 → 273 → 273 | 0, 0, 0 | 3 | 0 | 0 |
| WeatherKit | 1015 → 1015 → 1015 | 0, 0, 0 | 0 | 0 | 0 |
| WorkoutKit | 363 → 364 → 364 | 1, 0, 0 | 0 | 0 | 0 |
| MusicUnderstanding | new in Xcode 27.2: 27.2 only | — | — | — | — |
| Third-party bindings (9 libraries) | 26365 → 26413 → no loss | 66, 18, 226 (below) | 11 | 0 | 0 (not run on two libraries) |

The rows in "removed" are exactly the member rows recorded earlier in this file; there is no other
removal against `sdk-v0.20.2` in any shipped Apple framework package. The checker's rows on the
candidate are all present at `sdk-v0.20.2` too, in the channels the binding plan records (protocol
requirements and protocol-extension members, raw-value and Codable initializers of enums projected
as C# enums, caseless namespace enums, nested types emitted under an `…Info` name), so no member is
newly dropped without a skip-report entry or an emitted counterpart. The candidate no longer emits
2300 `watchOS` availability attributes across 11 packages (`397bf12d`); they are counted, not listed.

**Third-party bindings.** The downstream third-party libraries were bound from the same inputs with
`sdk-v0.20.2` and with the candidate:

- **Removed, 18 members in four libraries, all unsafe-before:** a `CMTime` timestamp property and
  the two constructors that took it, all three passed as `IntPtr` (`c972ed9c`); a run-loop-mode
  property typed `NSRunLoopMode`, now `NSString` (`133609f3`); five enum-payload accessors whose
  payload is a Swift `URL` (`137e9592`); a Swift-metatype `ValueType` property (`any Any.Type`)
  on five value-provider types, their protocol and its proxy (`c0f36040`); and a `UILayoutPriority`
  parameter and a `UIFont.Weight` property whose carriers had the wrong size (`Int32` for a `Float`
  and a `Double`), now `float` and `double` (`133609f3`).
- **Newly guarded, 226 members in six libraries, all refused-pre-native.** 59 are refused only on
  the CoreCLR interpreter on x64 (reverse callbacks, and lowered signatures whose types the module
  does not know). The other 167 also refuse on Mono and on the CoreCLR interpreter and ReadyToRun
  on arm64 (shapes `SelfRegister` and `LoweredSignature`), so an app on the default iOS runtime
  loses them: two members of one library whose `UIControlEvent` parameter is not in the carrier
  table, and 165 members of a mixed Objective-C and Swift library whose build routes no bridge at
  all (defects below).
- Parameter renames: 11 in four libraries, each a parameter now named after its Swift argument label (the
  overload-naming change, `c2cd4428`).
- No library loses a member between Xcode 26.3 and 27.2, and the checker finds nothing new on any of
  them.

**The Xcode 27.2 losses**, each a change in Apple's 27.2 SDK:

- **RealityFoundation `Audio.Directivity`, `Audio.DistanceAttenuation`** and their `CaseTag`s (27
  rows): Apple added static members to `public enum Audio`, so `Audio` is no longer a caseless
  namespace enum and is emitted as a class; both types reappear nested in it member for member (and
  four `SpatialAudioComponent` members change type identity with them). The C# spelling is
  unchanged, the binary identity is not. The two `AudioFileResource.CreateAsync` overloads the first
  checkpoint saw declined on 27.2 are emitted (`d520dbd9`).
- **Matter**, 131 members of 10 types: the 27.2 SDK's Objective-C headers no longer declare them.
- **AppIntents `IntentFile.DefaultResolverSpecification`** becomes `object` (the 27.2 SDK declares
  its `Specification` as an opaque result type), and `AppIntentError.Unrecoverable.Unknown` is
  `[Obsolete]` (deprecated by Apple). The 35 `…CsmExtensions` types the first checkpoint lost on
  27.2 are emitted (`e69a1f84`).
- **ProximityReader**, three `NameComponents` members are `[Obsolete]`, deprecated by Apple.

No other package loses a member on Xcode 27.2.

### Defects

The first checkpoint's defects, re-measured on `5ab97b23`:

| Defect | Status | Receipt |
|---|---|---|
| A closure with an `UnsafePointer<T>` parameter got a wrapper adapter that did not compile, failing the whole SDK binding with `SWIFTBIND051`. | closed | `643c6b00`. Every package built on every pairing, and the negative-control fixture packs with no `SWIFTBIND051`. |
| Module selectors in the Xcode 27.2 interfaces cost AppIntents 35 types. | closed | `e69a1f84`. AppIntents on Xcode 27.2 removes no type against 26.3 (table above). |
| The default-parameter overload emitter declined 2 RealityFoundation constructors on Xcode 27.2. | closed | `d520dbd9`. RealityFoundation on Xcode 27.2 loses only the 27 moved `Audio` rows. |
| The lowering-receipt wrapper's fat simulator slice was named `ios-arm64-simulator`. | closed in the packed layout | `8f54da9f`. Every package built here consumed its wrapper; validate's PackGate was not run in this checkpoint. |
| validate pins Apple-framework targets to platform 26.2 on the Xcode 27.2 profiles. | open, not re-measured | The fix (`20b7a7f8`, `9d3f34c8`) is on the candidate; validate was not run in this checkpoint. |
| The downstream MusicKit build collected no lowering receipt, so `MusicLibrarySectionedRequest<,>()` shipped guarded. | closed | Routed on every pairing and slice family (above). |

Found in this checkpoint:

| Defect | Pairings | Effect | Owning component | Status |
|---|---|---|---|---|
| The downstream MusicKit package's hand-written search shims declare the UTF-8 length `int` in C# (`CatalogSearchRequestInit`, `LibrarySearchRequestInit`, `CatalogSearchSuggestionsRequestInit`, `[LibraryImport]`, cdecl) while the Swift `@_cdecl` functions take `Int`. The arm64 calling convention leaves the upper 32 bits of a 32-bit argument unspecified: the Mono apps pass, while under the CoreCLR interpreter and ReadyToRun Swift reads a garbage length (`SIGBUS` in `String.init(decoding:as:)` under `SBW_MusicKitShims_CatalogSearchRequest_Init`). Repro: `MusicCatalogSearchRequest.Create("rush", MusicCatalogSearchTypes.Album \| MusicCatalogSearchTypes.Artist)` in any .NET 11 iOS or tvOS simulator app, Debug or Release. The published `26.2.16` package carries the same shim. | `net11-xcode27`, Debug and Release, iOS and tvOS | the MusicKit test app crashes; every search request initializer is unusable on .NET 11 | downstream MusicKit package (hand-written shim) | open, release stop for MusicKit on .NET 11 |
| `Tips.OptionsBuilder.BuildFinalResult` (3 specializations) is withdrawn with its unsound base (`262ad429`): neither emitted nor in the skip report. | all | 3 members, silent | `ConcreteProtocolSpecializationEmitter` | open (row above) |
| Packages built on `sdk-v0.20.2` carry no transition manifest; any whose Swift transitions are not all primitive fails a consumer build on the new Runtime with `SWIFTBIND132` until it is suppressed or rebuilt. Relabelled as the final `0.21.0`, the published MusicKit package's `SwiftBindings.Runtime [0.20.2, 0.21.0)` also conflicts with the Apple package's `>= 0.21.0` (`NU1107`). | all | every published Apple framework package with a non-primitive Swift transition | consumer transition check (by design) × downstream package release order | open: the downstream packages need a rebuild on the candidate before or with the release |
| Six members of a third-party library's error factory (public Swift API returning `any Error`, through the `_optbuf` wrapper) import the wrapper with a 40-byte `ExistentialContainer1` result, which .NET returns indirectly, while the wrapper returns the boxed error as one pointer. The build's own lowering judgement records the contradiction ("the managed signature takes an indirect result …; the callee returns ptr directly"), yet the member stays a guarded direct import whose only shape, `LoweredSignature`, is refused on the CoreCLR interpreter x64 alone. Cause 2's disposition above says a plan the receipt contradicts stays fatal; here it falls back to the wrong call on Mono, NativeAOT and CoreCLR. Present at `sdk-v0.20.2` as well (same import and wrapper); read from both sides, not executed. Repro: a public Swift method with an `Optional` parameter returning `any Error`, bound through the SDK. | all | 6 members make a mismatched native call on every engine but x64 interpreter | generator (`any Error` result on the `_optbuf` wrapper path); lowering judgement's fallback for a contradicted plan | open, release stop (unauthorized native transition) |
| A framework built with testing enabled exports its internal declarations, and the generator binds them as public C# members (gates 3c and 3d in `MemberValidationPipeline`, arm 2b in `WrapperValidation`), against `scope-boundaries.md` ("internal members … should not be bound"). Their plans are refused as `NotPublicFromInterface` (cause 3 above), so nothing can route them. In one third-party library family 165 members refuse on Mono and on CoreCLR without a JIT on arm64, every Mono app included, where `sdk-v0.20.2` called them unguarded; 159 of its 179 guarded transitions are `NotPublicFromInterface` refusals, the rest a carrier-table gap, an `Any` argument, an unknown result type or the result mismatch above. Repro: an internal synchronous method on a public class of a framework built with `-enable-testing`, bound through the SDK with default settings. | all | 165 members throw before native work on Mono and arm64 CoreCLR without a JIT | generator emission policy for internal members | open: owner decision (drop them, emit them `internal`, or keep them guarded and record it as a known limitation) |
| Two members of a third-party library whose `UIControlEvent` parameter is not in the carrier table cannot be routed and refuse on Mono and arm64 CoreCLR without a JIT (shapes `SelfRegister`, `LoweredSignature`). | all | 2 members, guarded | carrier table | open |
| The generator CLI's output (the generated csproj, as the internal binding-testing apps build it) collects no lowering receipt and routes nothing, so every bridge plan stays a pending guarded direct import: 173 pending guarded transitions in 12 of those 15 libraries, 99 of them with `SelfRegister`, which throw on Mono. One app's test calls one of them and fails on Mono with `SwiftTransitionUnsupportedException` ("needs a native bridge"); `sdk-v0.20.2` made the call unguarded. Repro: generate any library with a public class method through the CLI, build the generated csproj and call the method on Mono. | all | 99 members throw on Mono in a CLI-generated binding | generated csproj (no receipt collection or routing outside the SDK) | open: the CLI path either routes or documents that it does not |
| The VisionKit test app's `ImageAnalyzer.AnalyzeAsync(UIImage, Configuration)` did not complete on .NET 11 in the first Debug and Release runs (iOS 27.2 simulator): the test's 30 s wait timed out. A second Debug run of the same build, an hour later, passed it (42 passed, transcript read back). The same app passes on Mono on the same simulator (`net10-xcode27`, 42 passed). The member is a cdecl `_async` wrapper that starts a Swift `Task` and reports through a cdecl completion callback; the test awaits it from app launch, on the UIKit synchronization context. The cause is not established: the thread sample was taken on the run that passed, and the failing runs fell in a period of heavy memory pressure on the host, so a load-sensitive completion path and a 30 s budget too short for it under the CoreCLR interpreter are both open. A third-party library's test app shows the same symptom on .NET 11 Debug only (an async callback-delivered result never arrives within 30 s; it passes on Release and on Mono), possibly the same cause. Repro: the VisionKit test app on the iOS 27.2 simulator under `net11-xcode27`. | `net11-xcode27` | 1 test fails in each app, intermittently in one | not established (generated async wrapper or its completion dispatch under CoreCLR) | open |

### Release stops

Measured against the release criteria (each row is in the tables above):

1. **Failed required lane, supported package:** the MusicKit package crashes on every .NET 11 lane
   (the downstream shim's `int`/`Int` mismatch); the published `26.2.16` package crashes the same
   way, so it is not a regression of the candidate, but MusicKit is not releasable on .NET 11.
2. **Native transition the evidence contradicts:** six third-party error-factory members make a
   call the build's own lowering judgement records as mismatched, unguarded on Mono, NativeAOT and
   CoreCLR (present at `sdk-v0.20.2` too).
3. **Loss of previously shipped API:** 165 members of one third-party library family now throw on
   Mono (internal Swift members the generator binds but cannot route); explained here, but whether
   to ship them guarded, drop them or make them `internal` is an owner decision.
4. **Member neither emitted nor in the skip report:** the three TipKit
   `Tips.OptionsBuilder.BuildFinalResult` specializations.
5. **Upgrade path:** every published Apple framework package with a non-primitive Swift transition
   fails a consumer build on the new Runtime (`SWIFTBIND132`), and at the final `0.21.0` label the
   published packages' Runtime range conflicts with the Apple package (`NU1107`); the downstream
   packages need a rebuild on the candidate released with or before it.
6. **Failed required lane, supported package:** the VisionKit test app failed on both .NET 11 lanes
   (`ImageAnalyzer.AnalyzeAsync` did not complete in 30 s) and passed on a second Debug run; a
   third-party app failed the same way on .NET 11 Debug. Intermittent, cause not established.
7. **Previously shipped API that now throws, CLI path:** a binding built from the generator CLI's
   csproj routes nothing, so 99 members across the internal binding-testing libraries throw on Mono
   where `sdk-v0.20.2` called them; one internal app fails on it. Not a shipped package, but the CLI
   is a documented entry point.

No unauthorized transition, unexplained loss or silent drop was found in the BindingTests corpus,
`SwiftBindings.Runtime`, `SwiftBindings.Apple` or the Apple framework packages beyond the rows
above, and every Apple framework package and its test apps built on every pairing.
