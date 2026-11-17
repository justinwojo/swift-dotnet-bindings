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

| Measure | sdk-v0.20.2 | Branch | Change |
|---|---|---|---|
| API-manifest members | 6146 | 6983 | 837 added (21 from the fixture below), 0 removed |
| Members whose entry point moved (same C# signature) | | 43 | 15 direct Swift call → `@_cdecl` wrapper, 27 `@_cdecl` wrapper → native thunk, 1 wrapper → wrapper |
| Skip-surface markers | 348 | 348 | `SB0001` 23 → 11, `Unsupported` 284 → 294 (one from the fixture below), `UnsupportedSwiftType` 41 → 43 |
| Members carrying a transition guard | none (no guard existed) | 3 | see below |

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
MusicKit 6. RealityFoundation and MusicKit were regenerated again with the fixed generator and
compiled, and their removals are exactly the rows below that are not marked fixed.

| Module | Public members, sdk-v0.20.2 | Public members, branch | Added | Removed | Guarded, shipped (unrouted) | Skip markers cleared | Markers gained |
|---|---|---|---|---|---|---|---|
| RealityFoundation | 5642 | 5704 | 77 | 15 | 4 (27) | 27 | 0 |
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
| RealityFoundation | `PhotogrammetrySession(NSUrl)` | The default-omitting form of `init(input: URL, configuration: = .init())` is declined because `init<S: Sequence>(input: S, configuration:) where S.Element == PhotogrammetrySample` is counted as a rival. `URL` is not such a sequence, so Swift resolves the call to the URL initializer. The full `PhotogrammetrySession(NSUrl, Configuration)` remains. | all | **regression, release blocker**: ruling the generic sibling out needs proof that `URL` does not conform to `Sequence`, which the pinning replay cannot see for a type from another module. Workaround: pass `new PhotogrammetrySession.Configuration()`. |
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
libraries, .NET 10 + Xcode 26.3, iOS and tvOS simulator, build only) against this branch's packages
built and packed every library, but nine test apps fail to build and one sibling package fails to
pack. In every case the binding carries a valid transition manifest that the same SDK wrote, and the
pack-time check (`SWIFTBIND133`) or the consumer check (`SWIFTBIND132`) refuses it. At
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
transition rules treat every Swift-convention `UnmanagedCallersOnly` method as an R4 violation
whether or not a guard covers it, and both checks refuse an assembly with any R4 violation.

- `SwiftBindings.Apple.RealityFoundation` does not pack: `Entity.PlayAudio(AudioGeneratorConfiguration, Func<…>)`,
  `PrepareAudio(…)` and their two new overloads, 4 callbacks. RealityKit's app fails through it.
- Five of the ten third-party libraries fail the consumer check for the same reason (2 to 24
  callbacks each), on closure parameters of methods and on closure-typed property setters of
  nested configuration structs.

The BindingTests corpus has no member of this shape (its compile-check assembly scans R4 = 0), so
neither the compile gate nor the transition gate met it. The two policies disagree about whether a
guarded Swift-convention reverse callback may ship. Either the generator stops emitting such
members until a reverse-callback bridge template exists (they become skipped with a reason), or R4
accepts a reverse callback whose only construction site is a guarded member (they stay public and
refuse on the x64 interpreter).

**2. Lowering predicted differently from what swiftc compiles (one library).** The SDK routed two
members through bridges whose plans disagree with the collected lowering receipt: an initializer
whose swiftc lowering is `swiftcc ptr (ptr noalias, ptr noalias, ptr, i64, ptr, i64, ptr, ptr swiftself)`,
and a getter the plan lowers through an indirect (sret) result while swiftc returns `ptr`. The
consumer check refuses each as "planned through `SBSR_…`, but its lowering evidence does not make it
executable". The routing should not have planned a bridge the receipt does not authorize; the
predictor's disagreement is a second defect.

**3. Evidence collection cannot name some types (one library, four modules).** The receipt
collector's probe fails on types the module surfaces under another name or through Objective-C
(`no type named '…' in module '…'`, `swiftc shows no $s…`), yet the plans still route those members:
42, 89, 61 and 9 refused checks across the four modules.

**Release blockers**, owner decision on the R4 policy; causes 2 and 3 are SDK defects (routing must
fall back to the guarded direct call when the receipt does not authorize a bridge). Each fix needs a
first-party BindingTests fixture of its shape. The downstream patches and the full failure table are
outside this repository.

## Totals

| Surface | Rows (removed, reshaped, skipped or guarded at any point on the branch) | unsafe-before | refused-pre-native | withdrawn | intended | regression fixed | regression open |
|---|---|---|---|---|---|---|---|
| BindingTests corpus | 3 | 0 | 2 | 0 | 1 | 0 | 0 |
| SwiftBindings.Runtime | 0 | | | | | | |
| SwiftBindings.Apple | 0 | | | | | | |
| RealityFoundation | 21 | 12 | 2 | 0 | 2 | 4 | 1 |
| RealityKit | 1 | 1 | 0 | 0 | 0 | 0 | 0 |
| MusicKit | 6 | 0 | 0 | 0 | 1 | 5 | 0 |
| AppIntents | 11 | 2 | 8 | 0 | 1 | 0 | 0 |
| **All** | **42** | **15** | **12** | **0** | **5** | **9** | **1** |

The 9 fixed rows are public again; 33 members remain removed, reshaped or guarded. Release blockers
remain: `PhotogrammetrySession(NSUrl)`, and the three packaging causes above (the RealityFoundation
package refused at pack time and nine downstream test apps refused at build time).

## defect-closure-3: pending

`defect-closure-3` was not on `origin` when this ledger was written (checked with `git fetch` on
2026-11-17), so its surface delta is not included. When it lands, compare its API manifest and skip
surface against this ledger's base (`7383cf60`) and add a table here with a disposition for each
member it removes, skips or guards. It touches class equality and hashing on disposed instances,
protocol-proxy member names, enum case factories for optional tuples, verify-recover convergence,
inherited renamed-property forwarding and existential object returns.
