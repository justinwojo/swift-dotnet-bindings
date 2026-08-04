# 2026-08 docs-pass — evidence appendix

Tracked evidence for the decisions and deferrals recorded in
[`not-planned.md`](not-planned.md) and
[`2026-08-02-third-party-docs-pass-work-breakdown.md`](2026-08-02-third-party-docs-pass-work-breakdown.md).

**Why this file exists.** The rows in those two documents originally cited the working
directories the program ran out of — `.agent/phase-N-summary.md` and
`src/docs/sessions/2026-08-docs-pass-upstream/`. Neither is tracked by git (the phase
summaries have been untracked by the owner three separate times, most recently in
`24e23da6`), so a durable row was pointing at something a future reader cannot open.
This appendix **inlines** the content those rows need rather than citing it. When a row
elsewhere says *"Evidence: § X of the docs-pass evidence appendix"*, § X below is the
whole of what it is claiming.

Two of the sections here are **migration documentation**, not background: § 2 (ObjC
naming renames) and § 3 (ObjC constant de-prefixing) are the complete old→new tables a
consumer needs to move their source across those changes.

**Method note for the rename tables.** The tables in § 2 were not transcribed from a
prose summary — none existed. They were re-derived on 2026-08-04 from the artifacts a
regeneration actually produced: the emitted `ApiDefinition.cs` / `StructsAndEnums.cs`
under each library's `obj/…/swift-binding/`, diffed against the raw ObjC declarations in
the vendor headers by replaying both the old and the new naming rule. A row is listed
only where the emitted name matches the new rule *and* differs from the old one. Any
member matching neither rule is called out explicitly rather than guessed at, and the one
library whose tables cannot be derived at all is named in § 2.4.

---

## 1. Overload rename ledger — argument-label-aware disambiguation

**What changed.** The overload disambiguator gained a rung ladder — bare-name ownership →
argument labels → Swift parameter types → refusal — so a colliding overload family no
longer falls back to a bare numeric suffix on the public surface. A `--compile-only`
overload-name gate reads the resolver's own assignment records (not a name regex, so an
author-written `Process2` survives) and fails on a bare numeric suffix.

**Result on the BindingTests corpus:** 36 names assigned, none numeric — 25 derived from
argument labels, 11 from Swift parameter types. The full ledger:

| Owning type | Old name | New name | Scheme |
|---|---|---|---|
| `ArrayMetatypeStore` | `LoadItems` | `LoadItemsAsWithArrayDouble` | TypeDerived |
| `ArrayMetatypeStore` | `LoadItems` | `LoadItemsAsWithArrayInt32` | TypeDerived |
| `CaptureSessionObserver` | `CaptureSession` | `CaptureSessionDidAdd` | LabelDerived |
| `CaptureSessionObserver` | `CaptureSession` | `CaptureSessionDidChange` | LabelDerived |
| `CaptureSessionObserver` | `CaptureSession` | `CaptureSessionDidUpdate` | LabelDerived |
| `CollisionDeclarationOrderBareName` | `Configure` | `ConfigureAlpha` | LabelDerived |
| `CollisionDeclarationOrderBareName` | `Configure` | `ConfigureZebra` | LabelDerived |
| `CollisionDeclarationOrderThreeWay` | `Render` | `RenderAlpha` | LabelDerived |
| `CollisionDeclarationOrderThreeWay` | `Render` | `RenderBeta` | LabelDerived |
| `CollisionDeclarationOrderThreeWay` | `Render` | `RenderGamma` | LabelDerived |
| `CollisionOverrideBase` | `Process` | `ProcessFirst` | LabelDerived |
| `CollisionOverrideBase` | `Process` | `ProcessSecond` | LabelDerived |
| `CollisionOverrideDerivedBoth` | `Process` | `ProcessFirst` | LabelDerived |
| `CollisionOverrideDerivedBoth` | `Process` | `ProcessSecond` | LabelDerived |
| `CollisionOverrideDerivedSecondPlusSibling` | `ProcessSecond` | `ProcessSecondWithInt32` | TypeDerived |
| `CollisionOverrideDerivedSiblingFirst` | `ProcessSecond` | `ProcessSecondWithInt32` | TypeDerived |
| `ConversationManagerDelegate` | `ConversationManager` | `ConversationManagerDidActivate` | LabelDerived |
| `ConversationManagerDelegate` | `ConversationManager` | `ConversationManagerDidDeactivate` | LabelDerived |
| `GenericConstrainedExtensionMapper` | `Map` | `MapJSONObjectWithAny` | TypeDerived |
| `GenericConstrainedExtensionMapper` | `Map` | `MapJSONObjectWithOptionalAny` | TypeDerived |
| `GenericExtensionOptionalReturnMapper` | `Lookup` | `LookupByOptional` | LabelDerived |
| `GenericExtensionOptionalReturnMapper` | `Lookup` | `LookupByRequired` | LabelDerived |
| `GenericIndexableCollection` | `FormIndex` | `FormIndexAfter` | LabelDerived |
| `GenericIndexableCollection` | `FormIndex` | `FormIndexBefore` | LabelDerived |
| `GenericIndexableCollection` | `Index` | `IndexAfter` | LabelDerived |
| `GenericIndexableCollection` | `Index` | `IndexBefore` | LabelDerived |
| `NullableRefOverrideBase` | `Transform` | `TransformWithOptionalRefBox` | TypeDerived |
| `NullableRefOverrideBase` | `Transform` | `TransformWithRefBox` | TypeDerived |
| `OverloadForwardHost` | `Configure` | `ConfigureWithMode` | LabelDerived |
| `OverloadForwardHost` | `Configure` | `ConfigureWithPriority` | LabelDerived |
| `PropertyMethodCollider` | `ConflictMethod` | `ConflictMethodWithInt32` | TypeDerived |
| `RoomActivityObserver` | `Room` | `RoomDidAdd` | LabelDerived |
| `RoomActivityObserver` | `Room` | `RoomDidFinishWithError` | LabelDerived |
| `RoomActivityObserver` | `Room` | `RoomDidRemove` | LabelDerived |
| `WitnessIndexProto` | `Consume` | `ConsumeWithWitnessIndexPayloadA` | TypeDerived |
| `WitnessIndexProto` | `Consume` | `ConsumeWithWitnessIndexPayloadB` | TypeDerived |

**Accepted characteristics of a content-derived ladder.** Names can still move when
inserting a member changes a family's rung. Three ledger rows name members that never
emit (the ladder is conservative and assigns before emission is settled). The
free-function lane is not covered by the gate's positive control.

**Two residuals were left open at the time, both pre-existing rather than introduced:**

- **Family fold.** The protocol lane folds a disambiguated family onto one shared name,
  which renames a non-colliding protocol sibling (`RoomDidFinishWithError`) that the class
  lane leaves bare — a Swift class conforming to that protocol would lose the conformance.
  Not reachable with the fixtures as they stand: the fixture has no conforming class, only
  an existential harness. Both reviewers warned against generalizing the fold to the other
  lanes. The fork is *retire the fold* vs *mirror it in the class lane*; either arm moves
  published names. Carried as an owner decision in `not-planned.md`.
- **Occupancy policy.** The class lane applies `RungFits` family-wide; the protocol lane
  accepts per-member. They diverge on `configure(mode:)` / `configure(other:)` beside a
  natural `configureMode`. Both lanes are fixture-pinned today, so the divergence is
  observable and stable rather than silent drift.

**Refusal set not enumerated.** The last rung of the ladder is refusal. The pass did not
enumerate which members the resolver refuses for a *return-type-only* difference (Swift
permits overloading on return type; the C# projection cannot), so there is no list of
those members here. That gap is registered in `not-planned.md`.

---

## 2. ObjC naming renames — the migration tables

Three naming rules changed together in the ObjC lane. All three make the emitted C# read
the way an Apple developer reads the corresponding Swift import, and all three are
**source-breaking** for a consumer already compiled against the previous names. The owner
ruled on 2026-08-04 that they ship as breaking (see § 7); these tables are the migration
path.

The tables below cover every library in the third-party corpus that has an ObjC lane. A
library not listed has zero rows for that rule.

### 2.1 Enum-case prefix stripping

**Old rule.** Strip the enum's own type name from each case, and only if *every* case
starts with it.

**New rule.** Same first test; failing that, strip the module tag when *every* case
carries it at a token boundary (the character after the tag must be uppercase); failing
that, leave the case unchanged. There is no case-set longest-common-prefix arm — a tag has
to be a registered module tag, derived from the module's exported `extern` constants.

**Where the tag comes from is itself an open design question** — see § 7, decision (a).

**Derived rows: 8 renames, 0 uncertain.** All in MapLibre; FBSDKCoreKit, FBSDKShareKit,
FBSDKCoreKit_Basics, Stripe3DS2 and BlinkID have zero (their case sets either already
carried the full type name — the old rule already stripped them — or did not carry a
registered module tag at a token boundary).

| Enum | Old C# case | New C# case |
|---|---|---|
| `MLNMapDebugMaskOptions` | `MLNMapDebugTileBoundariesMask` | `MapDebugTileBoundariesMask` |
| `MLNMapDebugMaskOptions` | `MLNMapDebugTileInfoMask` | `MapDebugTileInfoMask` |
| `MLNMapDebugMaskOptions` | `MLNMapDebugTimestampsMask` | `MapDebugTimestampsMask` |
| `MLNMapDebugMaskOptions` | `MLNMapDebugCollisionBoxesMask` | `MapDebugCollisionBoxesMask` |
| `MLNMapDebugMaskOptions` | `MLNMapDebugOverdrawVisualizationMask` | `MapDebugOverdrawVisualizationMask` |
| `MLNWellKnownTileServer` | `MLNMapTiler` | `MapTiler` |
| `MLNWellKnownTileServer` | `MLNMapLibre` | `MapLibre` |
| `MLNWellKnownTileServer` | `MLNMapbox` | `Mapbox` |

`MLNMapDebugMaskOptions` declares two further cases in the header —
`MLNMapDebugStencilBufferMask` and `MLNMapDebugDepthBufferMask` — but both sit behind
`#if !TARGET_OS_IPHONE`, so neither reaches the iOS surface and neither is a migration
row there.

The BindingTests umbrella fixture exercises both arms of the rule and is not a shipped
surface: `OUSourceKindOUMapTiler → MapTiler` (tag arm), `OUSourceKindPhotos → Photos`
(type-name arm), and an unrecognised tag leaves the case unchanged.

**A note on where the old prose example came from.** An earlier record illustrated this
rule with `MLNSourceKindTiler → Tiler`. No such enum exists in MapLibre; the real shipped
rows are the eight above.

### 2.2 Delegate-selector method names

**Old rule.** For a multi-part selector, drop `parts[0]` wholesale and PascalCase-join the
rest. For a single-part selector, PascalCase it.

**New rule.** If `parts[0]` begins with the delegating protocol's receiver token (the
protocol name with a trailing `Delegate`/`DataSource` role suffix removed, optionally with
its leading acronym peeled), strip just that token and keep the remainder; then join the
rest as before. If `parts[0]` does not carry the receiver, behaviour is unchanged.

**Derived rows: 25 renames, 0 uncertain** — 23 in MapLibre, 2 in FBSDKCoreKit.
FBSDKCoreKit_Basics (25 protocol methods), FBSDKShareKit, Stripe3DS2 (15 protocol methods)
and BlinkID produce zero rows.

| Protocol | Selector | Old C# name | New C# name |
|---|---|---|---|
| `MLNMapViewDelegate` | `mapViewRegionIsChanging:` | `MapViewRegionIsChanging` | `RegionIsChanging` |
| `MLNMapViewDelegate` | `mapViewWillStartLoadingMap:` | `MapViewWillStartLoadingMap` | `WillStartLoadingMap` |
| `MLNMapViewDelegate` | `mapViewDidFinishLoadingMap:` | `MapViewDidFinishLoadingMap` | `DidFinishLoadingMap` |
| `MLNMapViewDelegate` | `mapViewDidFailLoadingMap:withError:` | `WithError` | `DidFailLoadingMapWithError` |
| `MLNMapViewDelegate` | `mapViewWillStartRenderingMap:` | `MapViewWillStartRenderingMap` | `WillStartRenderingMap` |
| `MLNMapViewDelegate` | `mapViewDidFinishRenderingMap:fullyRendered:` | `FullyRendered` | `DidFinishRenderingMapFullyRendered` |
| `MLNMapViewDelegate` | `mapViewWillStartRenderingFrame:` | `MapViewWillStartRenderingFrame` | `WillStartRenderingFrame` |
| `MLNMapViewDelegate` | `mapViewDidFinishRenderingFrame:fullyRendered:` | `FullyRendered` | `DidFinishRenderingFrameFullyRendered` |
| `MLNMapViewDelegate` | `mapViewDidFinishRenderingFrame:fullyRendered:frameEncodingTime:frameRenderingTime:` | `FullyRenderedFrameEncodingTimeFrameRenderingTime` | `DidFinishRenderingFrameFullyRenderedFrameEncodingTimeFrameRenderingTime` |
| `MLNMapViewDelegate` | `mapViewDidFinishRenderingFrame:fullyRendered:renderingStats:` | `FullyRenderedRenderingStats` | `DidFinishRenderingFrameFullyRenderedRenderingStats` |
| `MLNMapViewDelegate` | `mapViewDidBecomeIdle:` | `MapViewDidBecomeIdle` | `DidBecomeIdle` |
| `MLNMapViewDelegate` | `mapViewRendererDidError:` | `MapViewRendererDidError` | `RendererDidError` |
| `MLNMapViewDelegate` | `mapViewWillStartLocatingUser:` | `MapViewWillStartLocatingUser` | `WillStartLocatingUser` |
| `MLNMapViewDelegate` | `mapViewDidStopLocatingUser:` | `MapViewDidStopLocatingUser` | `DidStopLocatingUser` |
| `MLNMapViewDelegate` | `mapViewStyleForDefaultUserLocationAnnotationView:` | `MapViewStyleForDefaultUserLocationAnnotationView` | `StyleForDefaultUserLocationAnnotationView` |
| `MLNMapViewDelegate` | `mapViewUserLocationAnchorPoint:` | `MapViewUserLocationAnchorPoint` | `UserLocationAnchorPoint` |
| `MLNCalloutViewDelegate` | `calloutViewShouldHighlight:` | `CalloutViewShouldHighlight` | `ShouldHighlight` |
| `MLNCalloutViewDelegate` | `calloutViewTapped:` | `CalloutViewTapped` | `Tapped` |
| `MLNCalloutViewDelegate` | `calloutViewWillAppear:` | `CalloutViewWillAppear` | `WillAppear` |
| `MLNCalloutViewDelegate` | `calloutViewDidAppear:` | `CalloutViewDidAppear` | `DidAppear` |
| `MLNLocationManagerDelegate` | `locationManagerShouldDisplayHeadingCalibration:` | `LocationManagerShouldDisplayHeadingCalibration` | `ShouldDisplayHeadingCalibration` |
| `MLNLocationManagerDelegate` | `locationManagerDidChangeAuthorization:` | `LocationManagerDidChangeAuthorization` | `DidChangeAuthorization` |
| `MLNMapSnapshotterDelegate` | `mapSnapshotterDidFail:withError:` | `WithError` | `DidFailWithError` |
| `FBSDKWebDialogViewDelegate` | `webDialogViewDidCancel:` | `WebDialogViewDidCancel` | `DidCancel` |
| `FBSDKWebDialogViewDelegate` | `webDialogViewDidFinishLoad:` | `WebDialogViewDidFinishLoad` | `DidFinishLoad` |

One FBSDKCoreKit member does not fit either rule and is **not** a delegate rename:
`FBSDKGraphRequestFactory`'s
`createGraphRequestWithGraphPath:parameters:tokenString:HTTPMethod:flags:useAlternativeDefaultDomainPrefix:`
emits as `CreateGraphRequestWithGraphPath…` — the full-selector form the dedup path uses
when the short name would collide. It reads the same before and after this change.

**Known incompleteness in the receiver-token peel.** The leading-acronym helper removes
the whole leading uppercase run rather than just the framework prefix, so
`NSURLSessionDelegate` yields the candidate `Session` instead of `URLSession`, no
candidate matches, and such a delegate method keeps its previous name. That is
conservative — an un-peeled name is correct, just less Apple-like — and it is registered
as a latent in `not-planned.md`.

### 2.3 Swift-import type names

**Rule.** A pre-emission rewriter maps an ObjC class/protocol/enum name to the name the
Swift importer would give it, via a vetted accept-list shared with the bridge-record
rekeyer. The raw ObjC name is persisted on the type record and re-emitted as
`[BaseType(…, Name = "<raw>")]` / `[Protocol(Name = "<raw>")]` so native registration and
superclass resolution are unaffected — only the managed name moves.

**Derived rows: 15 renames, 0 uncertain** — 14 in FBSDKCoreKit, 1 in FBSDKShareKit.
MapLibre, FBSDKCoreKit_Basics, Stripe3DS2 and BlinkID have zero.

| Module | Kind | Old C# name (= raw ObjC name) | New C# name |
|---|---|---|---|
| FBSDKCoreKit | class | `FBSDKAccessToken` | `AccessToken` |
| FBSDKCoreKit | class | `FBSDKAuthenticationToken` | `AuthenticationToken` |
| FBSDKCoreKit | class | `FBSDKAppEventsState` | `_AppEventsState` |
| FBSDKCoreKit | class | `FBSDKBridgeAPIResponse` | `BridgeAPIResponse` |
| FBSDKCoreKit | class | `FBSDKContainerViewController` | `_ContainerViewController` |
| FBSDKCoreKit | class | `FBSDKDialogConfiguration` | `_DialogConfiguration` |
| FBSDKCoreKit | class | `FBSDKLocation` | `Location` |
| FBSDKCoreKit | class | `FBSDKLogger` | `_Logger` |
| FBSDKCoreKit | class | `FBSDKPaymentProductRequestor` | `PaymentProductRequestor` |
| FBSDKCoreKit | class | `FBSDKUserAgeRange` | `UserAgeRange` |
| FBSDKCoreKit | class | `FBSDKWebDialogView` | `FBWebDialogView` |
| FBSDKCoreKit | enum | `FBSDKAdvertisingTrackingStatus` | `AdvertisingTrackingStatus` |
| FBSDKCoreKit | enum | `FBSDKAppLinkNavigationType` | `AppLinkNavigationType` |
| FBSDKCoreKit | enum | `FBSDKFeature` | `SDKFeature` |
| FBSDKShareKit | enum | `FBSDKShareBridgeOptions` | `ShareBridgeOptions` |

**Zero protocol renames.** The `Name = "…"` attribute also appears on eleven FBSDKCoreKit
protocols and one FBSDKCoreKit_Basics protocol, but those are the pre-existing
`{Name}Protocol` suffix that resolves a protocol/class name clash
(`FBSDKGraphRequestConnectionFactory` → `FBSDKGraphRequestConnectionFactoryProtocol`, and
similarly `FBSDKCrashHandler` → `FBSDKCrashHandlerProtocol`). That suffix and its
`Name = "…"` companion predate this change; those are not migration rows.

**A note on where the old prose examples came from.** An earlier record illustrated this
rule with `FBSDKSharing → Sharing`. The rewriter's accept-list is vetted per name, and
`FBSDKSharing` is not among the renames the regeneration produced — the FBSDKShareKit row
is the enum above.

### 2.4 FBSDKLoginKit — resolved: zero rows for all three rules

This section previously recorded FBSDKLoginKit's rename rows as **unknown**, because the
generator aborted that module during emission on the api-surface reconciler (§ 4.4) and
wrote no output to diff against. The reconciler failure is fixed; FBSDKLoginKit now
generates end to end, and the answer is **zero rows for § 2.1, § 2.2 and § 2.3** — not
"unknown", and not "some renames we have yet to enumerate".

**Why zero.** FBSDKLoginKit emits no ObjC companion lane at all. Its ObjC-visible classes
are Swift `@objc` declarations that the Swift lane already binds, so the mixed-module
dedup pass removes them from the ObjC side and nothing is left for the ObjC emitter:

```
Mixed dedup: removed 2 shared class(es) and 0 shared protocol(s) from ObjC output, extracted 0 category interface(s).
Mixed framework 'FBSDKLoginKit': no ObjC classes, protocols, or enums found — skipping ObjC emission.
```

All three rules in § 2.1–2.3 rewrite names on ObjC *declarations*, so with no declarations
emitted there is nothing to rename. Confirmed on the generated output: no
`ApiDefinition.cs` and no `StructsAndEnums.cs` are written, and the emitted C# contains
zero `[BaseType(…)]` and zero `Name = "…"` occurrences. A `-v 2` regeneration logs no
`… is imported into Swift as …` lines for this module. The C# type names a consumer sees
(`CodeVerifier`, `DeviceLoginCodeInfo`, `DeviceLoginManager`, `FBLoginButton`,
`LoginConfiguration`, …) are the Swift declaration names, carried straight through — they
are not products of a rename rule and carry no migration rows.

**The one bridge record that matters.** The same regeneration reports:

```
Mixed bridge: synthesized 3 ObjC type-resolution record(s) for module 'FBSDKLoginKit' (2 class(es), 0 enum(s), 1 typed enum(s)).
```

The typed-enum record is `FBSDKLoginAuthType`, an `NS_TYPED_EXTENSIBLE_ENUM`. Type
resolution for it runs through the bridge-record rekeyer, which needs the typedef's
Swift-import rename even though the typedef is never *declared* in the ObjC output — the
case § 2.3's accept-list pass-through now covers. With that in place `AuthType` resolves
and binds rather than being skipped: it appears as `Foundation.NSString?` on both
`FBLoginButton` and `LoginConfiguration`, and twice in `FBSDKLoginKit.api-surface.md`. The
module's `binding-report.json` contains **zero** occurrences of the string `AuthType` — no
skip of any class mentions it.

Nothing here requires a consumer migration row, so § 2.5's downstream-cost picture is
unchanged by FBSDKLoginKit.

### 2.5 Downstream cost, measured

The renames were applied to a real consumer to size the migration. The MapLibre test app
needed **three mechanical renames** — two delegate methods and one enum family:

- `MapViewDidFinishLoadingMap(MLNMapView)` → `DidFinishLoadingMap(MLNMapView)`
- `FullyRendered(MLNMapView, bool)` → `DidFinishRenderingMapFullyRendered(MLNMapView, bool)`
- `MLNMapDebugMaskOptions.MLNMapDebugTileBoundariesMask` / `…CollisionBoxesMask` →
  `MapDebugTileBoundariesMask` / `MapDebugCollisionBoxesMask`

The failures presented as `CS0115` (no suitable method to override) on the delegate
methods and `CS0117` (no such member) on the enum cases — loud, at compile time, with the
new name derivable from the table above.

---

## 3. ObjC constant de-prefixing

**What changed.** Exported ObjC `extern` constants now emit as a `[Static] partial
interface {Module}Constants` inside `ApiDefinition.cs` (the only input bgen backs a
`[Field]` from), and free C functions emit as `{Module}Functions`. Each `[Field]` names
the raw symbol and `"__Internal"` as its library — `dlopen(null)` ≡ `RTLD_DEFAULT` is the
only form that resolves for both dynamic and static linkage; a leaf framework name yields
a null handle.

**The de-prefix rule is bijective and all-or-nothing per module:** the module tag is
stripped from every constant name, or from none. So `old = tag + new`, and the mapping is
mechanically reversible from the tag alone.

| Module | Tag | Constants | Renamed |
|---|---|---|---|
| MapLibre | `MLN` | 58 | all 58 |
| ObjCUmbrella (BindingTests fixture) | `OU` | 5 bound of 7 declared | all 5 |
| FBSDKCoreKit | — | 171 | none — `DefaultKeychainServicePrefix` breaks the all-or-nothing tag, by design |

### 3.1 MapLibre — all 58 constants, tag `MLN`

Every row below is `MLN{new} → {new}`.

`AbstractClassException` · `ErrorDomain` · `InvalidStyleLayerException` ·
`StyleFunctionOptionInterpolationBase` · `StyleFunctionOptionDefaultValue` ·
`FontNamesAttribute` · `FontScaleAttribute` · `FontColorAttribute` ·
`ClusterIdentifierInvalid` · `InvalidStyleSourceException` ·
`ShapeSourceOptionClustered` · `ShapeSourceOptionClusterRadius` ·
`ShapeSourceOptionClusterMinPoints` · `ShapeSourceOptionClusterProperties` ·
`ShapeSourceOptionMaximumZoomLevelForClustering` ·
`ShapeSourceOptionMinimumZoomLevel` · `ShapeSourceOptionMaximumZoomLevel` ·
`ShapeSourceOptionBuffer` · `ShapeSourceOptionSimplificationTolerance` ·
`ShapeSourceOptionLineDistanceMetrics` · `ShapeSourceOptionSynchronousUpdate` ·
`ShapeSourceOptionWrapsCoordinates` · `ShapeSourceOptionClipsCoordinates` ·
`InvalidDatasourceException` · `InvalidStyleURLException` ·
`RedundantLayerException` · `RedundantLayerIdentifierException` ·
`RedundantSourceException` · `RedundantSourceIdentifierException` ·
`InvalidOfflinePackException` · `OfflinePackProgressChangedNotification` ·
`OfflinePackErrorNotification` · `OfflinePackMaximumMapboxTilesReachedNotification` ·
`OfflinePackUserInfoKeyState` · `OfflinePackUserInfoKeyProgress` ·
`OfflinePackUserInfoKeyError` · `OfflinePackUserInfoKeyMaximumCount` ·
`UnsupportedRegionTypeException` · `TileSourceOptionMinimumZoomLevel` ·
`TileSourceOptionMaximumZoomLevel` · `TileSourceOptionCoordinateBounds` ·
`TileSourceOptionAttributionHTMLString` · `TileSourceOptionAttributionInfos` ·
`TileSourceOptionTileCoordinateSystem` · `TileSourceOptionTileSize` ·
`TileSourceOptionDEMEncoding` · `VectorTileSourceOptionEncoding` ·
`ExpressionInterpolationModeLinear` · `ExpressionInterpolationModeExponential` ·
`ExpressionInterpolationModeCubicBezier` · `MapViewDecelerationRateNormal` ·
`MapViewDecelerationRateFast` · `MapViewDecelerationRateImmediate` ·
`MapViewPreferredFramesPerSecondDefault` · `MapViewPreferredFramesPerSecondLowPower` ·
`MapViewPreferredFramesPerSecondMaximum` ·
`MissingLocationServicesUsageDescriptionException` ·
`UserLocationAnnotationTypeException`

Types: 52 project as `NSString`, three as `double`
(`MapViewDecelerationRate{Normal,Fast,Immediate}`), three as `nint`
(`MapViewPreferredFramesPerSecond{Default,LowPower,Maximum}` — `typedef NSInteger`, which
already mapped), and one as `nuint` (`ClusterIdentifierInvalid`).

### 3.2 ObjCUmbrella fixture — 5 bound of 7, tag `OU`

| Raw ObjC symbol | Emitted | Note |
|---|---|---|
| `OUDefaultChannelName` | `DefaultChannelName` | `NSString` |
| `OUEventNameLaunch` | `EventNameLaunch` | NS_TYPED |
| `OUMaxRetryCount` | `MaxRetryCount` | `NSInteger` |
| `OUNativeWidthTicks` | `NativeWidthTicks` | `long` → `nint` |
| `OUScaleFactor` | `ScaleFactor` | `double` |
| `OUFixedWidthTicks` | — | not bound; `int64_t`, recorded skip |
| `OUDefaultTileSize` | — | not bound; `CGSize`, struct-constant skip |

The fixture also carries one free C function (`OUExportedTriple`), which is what exercises
the `{Module}Functions` split.

### 3.3 One further rename observed in the 2026-08-03 regeneration

FBSDKShareKit emits `FBSDKShareErrorDomain → ShareErrorDomain`. This is recorded as
**observed at regeneration time**, not attributed retroactively to the original constants
work — the enumeration made at the time named MapLibre and the umbrella fixture only.
Treat it as a migration row for FBSDKShareKit alongside § 2.3's enum rename.

### 3.4 Rider coverage note

The `long` / `unsigned long` → `nint` / `nuint` rider applies at the constant site only. It
fires on **zero** corpus constants: MapLibre's three `nint` FPS fields are `typedef
NSInteger`, which already mapped. Its coverage is the umbrella fixture plus unit tests, not
the corpus.

---

## 4. Deferrals and residuals cited by `not-planned.md`

Each subsection below is the evidence for a specific row.

### 4.1 Nested-closure constructor recovery is bounded

The closure-bearing-initializer recovery widened one bridge — `NestedClosureBridge` — to
admit root, non-failable, non-generic, non-ObjC-rooted, non-isolated **classes** with no
defaulted parameters and *sync* nested closures. It recovered two initializers
(`DeferredIntentConfiguration`, `ImmediateConfirmationConfiguration`) plus a downstream
type (`DeferredIntentController`), leaving no `SB0005` in those types.

What stayed refused: failable, throwing, async, struct and enum nested-closure
constructors, and every `MethodClosureBridge` constructor. This is not "constructors with
closures work now".

Throwing inner closures are refused **deliberately**, and this was a review finding, not
an omission: admitting them force-casts to a non-throwing Swift type that compiles cleanly
and then traps on the first callback. Refusal is unit-pinned.

The related orphan-shell trigger did not fire — all three closure-orphan shell types are
classes, with no struct or enum orphan, so the closure-param tombstone emitter was not
touched.

### 4.2 SwiftUI value types still declared non-frozen

`SwiftUICore` declares these types `@frozen`. A binding that believes otherwise passes a
buffer address where Swift wants the boxed value — a SIGSEGV, not a compile error.

Only `Color` and `Font` were corrected, to `frozen="true" inlineSize="8" abiLayout="p8"`
plus `Buffer` / `PayloadBuffer` on their runtime shells. The correction was found as a
root cause, not planned: the value-constructibility shims for those two crashed until the
declaration was fixed.

`EdgeInsets`, `Animation`, `Image`, `Text`, `AnyView` and `Binding` remain declared
`frozen="false"` in `SwiftUIDatabase.xml`. The per-type fix is the same attribute
correction plus shells.

The accompanying value-constructibility work added four runtime shims
(`SBW_SwiftUI_Color_Create`, `_Color_Destroy`, `_Font_System`, `_Font_Destroy`), present
in every slice except Mac Catalyst, where they are compiled out and the C# factories throw
`PlatformNotSupportedException` instead.

### 4.3 ObjC delegate-receiver acronym strip and the companion re-emit path

Both are deferrals recorded when the ObjC naming rules changed:

- **Acronym strip.** `StripLeadingAcronym`, reached from `DelegateReceiverCandidates`,
  removes the whole leading uppercase run rather than just the framework prefix, so
  `NSURLSessionDelegate` yields `Session` instead of `URLSession`. No candidate matches
  the selector's first part and the delegate method keeps its previous name. This is the
  selector-naming path, not the Swift-import name rewriter — the two were deferred
  together but are separate mechanisms.
- **Companion re-emit.** On the path where a module is re-emitted as an ObjC companion
  after already being processed, the rewriter's vetted rename map is not in scope. That
  path generates no Swift half at all, so the Swift-import renames do not apply there.

### 4.4 FBSDKLoginKit api-surface reconciliation failure

**This is a regression introduced by the api-surface reconciler, not a pre-existing gap in
that library.** The earlier record classified it as pre-existing on the grounds that it
also failed on `HEAD` — but the reconciler *is* on `HEAD`, having landed in `3d628a24`, so
"fails on HEAD too" was not evidence of age.

Verified from the regeneration's own failure report
(`libraries/Facebook/FBSDKLoginKit/obj/Debug/net10.0-ios/swift-binding/binding-failure-report.json`):

- `Outcome.Kind = "UnhandledException"`, `Stage = "Emit"`
- generator version stamped `1.0.0+24e783…` — i.e. current `main`, reconciler included
- diagnostic: *API surface reconciliation failed for module 'FBSDKLoginKit': 1 manifest
  entry names a member the emitted C# has no distinct counterpart for —
  `LoginManager.RefreshLimitedLogin(UIKit.UIViewController, FBSDKLoginKit.RefreshFallbackPolicy, Action<SwiftResult<…>>)`*

The reconciler is an unconditional hard generator error with no permissive arm, so an
unmatched entry aborts the module rather than degrading it. The mechanism is a member
whose *emitted shape* differs from the shape recorded in the manifest (an async→callback
rewrite plus existential erasure, in this case), which the recording side does not stamp.
Six further corpus libraries fail the same way. The fix is to have the reshaping emitters
record the shape they actually emitted; demoting the reconciler to a warning would trade a
loud stop for a silent lie and was rejected.

FBSDKLoginKit therefore has no ObjC emission at all right now — which is why § 2.4 cannot
give its rename tables.

### 4.5 Occupancy policy divergence between the class and protocol naming lanes

See § 1, second residual. The class lane applies `RungFits` family-wide; the protocol lane
accepts per-member; they diverge on `configure(mode:)` / `configure(other:)` beside a
natural `configureMode`. Both lanes are dual-pinned by fixtures, so the divergence is
observable rather than drifting, and converging them moves published names.

### 4.6 `WrappedMemberCount` is a floor, not a count

`RecordMemberWrapped` is never called on the mainline `@_cdecl` path, so the recorded
number under-reports. The count was phrased from a complete entry-point count during the
wrapper-recovery work rather than corrected, because closing it changes `WrappedItems`
semantics repo-wide. Anything reading the value as exact will be wrong low; reading it as a
trend is fine.

### 4.7 Cross-producer source-order dependence in overload naming

When two producers contribute overload candidates, the order they arrive in can change
which name each ends up with. This was accepted as a documented design residual when the
overload lattice work landed: the *inversion is reported*, not silent, and making emission
transactional was out of scope. The same pass fixed a cross-key reservation leak, an
unshaped failable-factory key, cap arithmetic spending a slot on a doomed candidate, an
over-strict unlabeled rule, and an unseeded manifest ladder — the source-order residual was
the one item left standing.

### 4.8 282 of 4,385 api-manifest entries record an unbound base symbol

When the manifest widened to properties and subscripts (2,993 → 4,385 entries), accessor
symbols were resolved to the entry point their P/Invoke actually binds. 282 entries still
record a *base* symbol no P/Invoke binds: members emitted by specialized bridges
(method-generic, multi-callback closure, AsyncStream property) mint entry-point names
outside `ComputeEntryPoint`. Routing those through `ComputeEntryPoint` over-suffixes them
(`_XM_XC` instead of `_XM`), so the fix was scoped to accessors and the gap is documented
on `ModuleEmissionContext.GetMethodEntryPointSymbol`. Closing it needs each bridge to stamp
the entry point it wrote.

### 4.9 `SB1003` does not fire through a generated protocol interface

The struct write-back analyzer stays silent when the receiver is a generated protocol
interface: those types do not implement `ISwiftObject` and are consumer-implementable, so
firing there would false-positive on a consumer's own implementation. The result is a known
false negative — a lost write through a protocol-typed receiver is not diagnosed. Accepted
deliberately, documented in code, pinned by a test.

**What SB1003 does cover:** plain assignments, compound assignments, `++` / `--`, and
indexer targets, peeling parentheses, casts and `!` off the receiver. It is silent on
local, field, parameter and plain-C# receivers. Severity is Warning.

**What a discarded struct copy actually costs** (this is the wording the consumer
documentation is built from, and it is deliberately narrower than "leak"): the payload is
a `SwiftSafeHandle` — a `SafeHandle` / `CriticalFinalizerObject` — so the native buffer is
reclaimed by the critical finalizer. The cost is *deferred, non-deterministic reclamation*
and *deferred Swift `deinit` side effects*. The one true loss is at process exit, where
`ReleaseHandle` takes the `FreeBufferOnly()` arm and deliberately skips the value-witness
Destroy, so `deinit` never runs. `using var` makes reclamation deterministic.

**The ObjC lane has no equivalent shape.** A platform value-type property (`NSRange span`
on the umbrella fixture) emits as `NSRange Span { get; set; }`, so `obj.Span.Location = 1`
is `CS1612` — a compile error, not a silent no-op.

### 4.10 Whether a hollow module should fail the pack

A pack-time member-count rider makes a module that binds almost nothing visible in the pack
report. Whether it should also *fail* the pack is a product policy call rather than a
correctness one — a constants-only or umbrella-only ObjC module is legitimately near-empty.
Carried as an owner decision; see § 7(f).

### 4.11 Which member loses when the CS0121 guard fires

The overload-ambiguity guard prevents an ambiguous-call compile error by declining one of
two candidates. *Which* one is a policy the generator applies on the consumer's behalf.
Declines are reported, not silent. The guard also caught a real corpus regression
(CocoaLumberjackSwift failing its Swift wrapper compile) which was fixed by the gate rather
than by moving a baseline: a shorter Swift call reaches *more* sibling declarations, so a
sibling differing only in a defaulted parameter's type makes the shim ambiguous and fails
the whole wrapper library.

### 4.12 Whether a reverse-dispatch-inert interface should be removed rather than marked

Option (a) shipped: a protocol proxy whose vtable fills zero slots is no longer registered,
and its emitted interface carries a warning-level `[Obsolete(DiagnosticId = "SB0010")]`, so
the trap is visible at compile time while the type stays usable in the forward direction.
Option (b) — suppressing the interface entirely — was considered and rejected because it
removes surface from the manifest and breaks forward-direction code that works today.

Nine protocols are marked and unregistered under this rule: `Summable`,
`BaselineAsyncClosureRequirement`, `BaselineAsyncClosureEligibleRequirement`,
`BaselineAsyncClosureDebugDefaultRequirement`, `AsyncClosureFanOwner`,
`AsyncClosureFanPeer`, `PhantomOwnerMixedGeneric`, `CombinedMixedSelfGeneric`,
`HollowUploadDelegate`. The gate is strictly *filled*-count == 0; partial vtables are
untouched.

### 4.13 A prediction gate the verify-recover loop provably cannot replace

The prediction-gate freeze policy says a new emission-time predictor is justified only when the
failure it prevents would *compile* — a compile-error-catchable shape belongs to the verify-recover
loop instead. `ProtocolConformanceValidator`'s `EmitsStaticPropertyUnderRequirementName` predicate
predicts a compile error (`CS0736`, a static member offered as an instance-interface implementation),
so on its face it is exactly what the policy bans.

It was retained, because removing it was tried end-to-end and the loop **cannot** recover the shape:

- `CS0736` attributes to the conforming type's whole type surface, not to a leaf member, so there is
  no leaf to withdraw.
- Bisection in the recovery loop is leaf-only, so it cannot narrow to the offending conformance.
- Coarse (type-scope) withdrawal exists but is **unauthorized** in production.

The observed end state with the predicate removed is a `binding-failure-report.json` carrying
`Kind = "RecoveryNonConvergence"`, `ReasonCode = "SWIFTBIND111"`, `Stage = "CSharpCompile"`,
`Scope = "TypeSurface"`, `AuthorizationOutcome = "Unauthorized"`,
`ObstructionCode = "RequiresGraphClosure"` — i.e. generator exit 1. So the trade is not "one prediction
gate versus one compile error"; it is **one dropped conformance versus one dropped library**.

The predicate stays, with the reasoning written up in code at
`ProtocolConformanceValidator.cs:1023-1032`. The underlying emitter inconsistency that produces the
shape was fixed separately. What is left is a policy question — see § 7(g).

### 4.14 No repo gate exercises the C# verify-recover loop on the main test library

The main BindingTests regeneration passes `--no-verify-csharp` (`build/Build.BindingTests.cs:425`), so
the in-generator compile-withdraw-re-emit loop does not run over the primary test corpus. The loop is
exercised only by the partial-success and ingestion kitchens, which are small hostile fixtures rather
than the broad surface. A recovery regression that those two do not happen to shape would not be
caught by any gate in the repo.

---

## 5. Runtime-leg coverage: which changes never ran on a device

Recorded so the gap is visible rather than assumed closed. No device leg in this program
was deferred for phone unavailability — the legs that ran, ran.

**Ran a device (NativeAOT) leg:** the wrapper-recovery, nested-closure-constructor,
SwiftUI-value-type, ObjC-constants and async-payload changes.

**Did not run a device leg, but warranted one** (each introduced new members, new P/Invokes
or new ObjC surface): the struct-write-back, projection-completeness, conformance-recovery,
overload-lattice, ObjC-category-ergonomics and ObjC-naming changes — plus the
api-surface-truthfulness change, which ran **no** runtime leg at all while re-stamping
accessor symbols.

Command to close the gap:

```
nuke binding-tests --device --device-udid 559479FD-3C60-51E4-8B2C-872D8CBA8B54
```

(no `--skip-regen` — the first device leg after a fixture change must regenerate; device
regen does not refresh the simulator wrapper slice or vice versa).

**Simulator-leg caveat, recorded 2026-08-04.** A later audit of the gate logs found that
the full-corpus simulator gate did not reach a clean PASS in this program, and that
several quoted pass-counts were read from runs that had failed or been truncated. The
largest single consumer-visible change here — the ObjC renames in § 2 — has no valid
runtime evidence of its own; what evidence exists for it is the downstream compile-and-run
in § 2.5. One clean, unfiltered, full-corpus simulator run is owed before the simulator
floor is reseeded from any number in this program.

---

## 6. Status corrections made on 2026-08-04

The breakdown document's per-item `Status` lines are preserved as written; the corrections
below are additive annotations on the same items, and are repeated there inline. They are
collected here so a reader has one place to check whether a "Fixed" line means what it
says.

| Item | Correction |
|---|---|
| A1 (numeric overload suffixes) | The invariant holds for the *overload* resolver, which is what the gate reads. The case-only collision pass is a separate producer and does emit a deliberate case-only numeric scheme (`Url` / `Url2`) that the gate does not see. |
| A2 (coexisting de-collision schemes) | "Converged" means centralized into `NameCollisionPolicy` with an explicit precedence order. Six schemes still coexist — by design, with disjoint token vocabularies so a reader can tell which side moved. |
| B1 (SB0001 prominence) | Six of twenty cases recovered, but **neither named P1 entry point** was among them: Mappedin `GetMapData` (genuine `SB0001`, reason `closure_params`) and Facebook `LogIn` both remain stubs. |
| B5 (conformance not carried) | The recoveries were **4 `IImageProcessing` + 2 `IImageEncoding`** interfaces — not "the five processors" the problem statement named. |
| B12 (UX bridge marshals only a scalar) | The machinery landed and is runtime-tested on fixtures, but the production registry entry carried no payload declaration, so no shipped binding returned a payload until the follow-up fix wave wired it. |
| B13 (`api-surface.md` phantoms) | What shipped is the drift **detector** (an always-on reconciler), not regeneration of the affected docs. Mappedin's `api-surface.md` still documents a 4-parameter `GetInView` against a 2-parameter emission. |

---

## 7. Owner-decision register — evidence

Each entry here backs a row in `not-planned.md` § *Pending owner decisions*. The
recommendation is what stands if no answer is given.

### (a) Where the enum-case strip tag comes from — PENDING

The module tag used by § 2.1's second arm is derived at generation time as the longest
common prefix of the module's exported `extern` **constants**. Nothing persists the chosen
tag, so it is recomputed from the constant set on every generation.

**Consequence.** A vendor adding one upstream constant that does not carry the tag
collapses the LCP, the tag arm stops matching, and **every** enum case in that module
reverts to its unstripped name — a silent, whole-module public-surface change triggered by
an unrelated upstream edit. MapLibre's 58-constant set is what makes `MLN` the tag today;
FBSDKCoreKit already demonstrates the collapse (one constant,
`DefaultKeychainServicePrefix`, breaks the tag and the module de-prefixes nothing).

**Recommendation.** Persist the tag at first generation of a module and reuse the persisted
value, so a name once published can only change deliberately. Absent an answer, the current
recompute-every-time behaviour stands and this is a live source-stability hazard.

### (b) Case-only collision naming scheme — PENDING

The case-only collision pass resolves a pair whose distinct Swift names project onto the
same C# identifier by appending a numeric suffix (`Url` / `Url2`). This is deliberate and
distinct from the overload ladder's refusal of numeric suffixes: the two members are not
overloads, and there is no argument label or parameter type to derive a name from.

The fork is whether to keep the numeric scheme for this producer (and carve it out of the
"no numeric suffix" statement, as § 6's A1 correction does), or to give the case-only pass
a derived scheme consistent with `NameCollisionPolicy`'s side-indicating token vocabulary.

**Recommendation.** Keep the numeric scheme and state the carve-out in the naming
documentation — a case-only pair has no content to derive a token from, and inventing one
would be less predictable than the digit. Absent an answer, that is what stands.

### (c) Deprecated / identical-pair collapse — PENDING, never implemented

The breakdown's A1 direction included a second ask that was not built: where two emissions
share the same C# signature and one is upstream-deprecated (`HandleNextAction` /
`HandleNextAction2`), *collapse to a single member carrying the deprecation* rather than
disambiguating them. The overload ladder addressed the naming half only.

Today the survivor of an identical-signature pair is simply **first in declaration order** —
nothing prefers the non-deprecated member, and nothing prefers the instance member over a
static one in the analogous static/instance shadow case.

**Recommendation.** Implement the collapse with an explicit preference (non-deprecated
wins; on a tie, first in declaration order) rather than leaving the outcome to ABI JSON
ordering. Absent an answer, declaration order decides.

### (d) ObjC renames: ship as breaking — **DECIDED (2026-08-04, owner)**

**Decision.** The § 2 renames (Swift-import type names, enum-case prefix stripping,
delegate-selector receiver stripping) and the § 3 constant de-prefixing **ship as
breaking**. No compatibility re-exports, no revert, no staging behind a major.

**Principle as stated by the owner:** do the right and best long-term thing for the
binding; if the renames were intentional and a net win for the C# surface, source-breaking
is fine.

**Migration path.** § 2.1–2.3 and § 3.1–3.3 above are the migration document. Publish them
with the release notes of every affected package.

**Evidence for the size of the break.** § 2.5 — the MapLibre test app needed three
mechanical renames, all surfacing as compile errors with the new name derivable from the
tables.

**Outstanding before any FBSDKLoginKit release:** § 2.4 — that module's tables cannot be
produced until its reconciler failure (§ 4.4) is fixed and it regenerates.

### (e) `SWIFTBIND052` stays an error rather than a warning — PENDING confirmation

The bridge-required diagnostic was split into an Error arm and a Warning arm, defaulting to
required-and-error. The error arm has since fired on a real consumer surface for an
unrelated reason (an unqualified nested SwiftUI View name), which is what surfaced the
underlying naming bug — the diagnostic behaved correctly.

**Recommendation.** Keep it an error. A silently missing bridge fails at runtime with a
`DllNotFound`-shaped symptom, which is strictly worse to diagnose than a build stop. Absent
an answer, error stands.

### (g) A standing freeze-policy exception for the `CS0736` conformance predicate — PENDING

The evidence is § 4.13: the predicate predicts a compile error, which the freeze policy bans, but the
verify-recover loop was proven unable to recover the shape, and removing the predicate drops the whole
library rather than the one conformance.

Three options:

1. **Grant a standing policy exception for this predictor.** It is now honestly documented in code, and
   the emitter inconsistency behind it was fixed separately.
2. **Fund conformance-aware static/instance name arbitration in the type emitters**, so the shape is
   unreachable by construction. This is the better long-term fix; note the symmetric static-*requirement*
   case, which has the same root.
3. **Fund `ConformanceEdge`-scope withdrawal in the recovery loop**, giving the loop a scope between
   leaf and whole-type so it can recover this shape on its own.

**Recommendation: (1).** Absent an answer, the predicate stays and the exception is de facto rather
than stated — which is the one outcome worth avoiding, because the next predictor request will cite
this one as precedent without its evidence.

### (f) Hollow-module packing — PENDING

See § 4.10. **Recommendation.** Keep the pack-time member-count rider and warn; do not fail
the pack. A constants-only or umbrella-only ObjC module is legitimately near-empty, and
failing would block a legitimate shape to catch an illegitimate one. Absent an answer,
current behaviour (report, don't fail) stands.

---

## 8. Release-notes input

Two consumer-visible behaviour changes from this program need a release-notes line the next
time an affected package publishes. Neither is a bug fix a consumer will notice
automatically.

**1 — ObjC naming renames are source-breaking.** Type names, enum cases and delegate method
names change in the ObjC lane for MapLibre and the Facebook kits. The complete old→new
tables are § 2.1–2.3 of this appendix, and the constant de-prefixing tables are § 3 — link
them from the release notes; they are the migration path. Expect `CS0115` on delegate
overrides and `CS0117` on enum cases. FBSDKLoginKit must not publish until § 2.4 is closed.

**2 — a BlinkIDUX bridge default flipped.** The generated UX bridge's `preferFrontCamera`
parameter now defaults to `false` (rear camera), matching the vendor's own
`ScanningUXSettings.preferredCameraPosition` default of `Back`. It previously defaulted to
`true`, which pointed the selfie camera at a document on the package's primary flow. A
consumer who relied on the old default — or who followed the published guide's note that
"the default is `true`" — must now pass `preferFrontCamera: true` explicitly. The
BlinkIDUX guide's note needs updating in the same release.
