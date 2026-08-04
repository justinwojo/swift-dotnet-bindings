# Third-party docs pass — upstream work breakdown (2026-08-02)

## What this is

On 2026-08-02 nine per-library usage guides were authored for the third-party bindings in
`swift-dotnet-packages` (Nuke, Lottie, Kingfisher, Mappedin, MapLibre, BlinkID, BlinkIDUX, Stripe,
Facebook), each written against a full crawl of the **freshly regenerated SDK 0.18.1** C# under
`libraries/*/obj/*/net10.0-ios/swift-binding/`. Writing a guide is the most adversarial read of a
binding we have: every documented call has to be a call a consumer can actually make, so every
awkward name, non-callable stub, dead surface, and "you must do it this way instead" became a
caveat paragraph in a shipped wiki page.

This document turns those caveats into upstream work items. **The goal is that a future SDK lets
the guides delete the caveat, not describe it better.** Nothing here is queued — this is a
breakdown for scoping, in the same spirit as `roadmap.md` (intent) and `not-planned.md`
(acknowledged-but-not-pursued). Where a finding is already registered in `not-planned.md` or bounded
by `roadmap.md` policy, the item says so and does not restate the entry; see
[Cross-references](#cross-references-to-not-plannedmd--roadmapmd) at the end.

**Evidence discipline.** Every item cites a library plus an exact C# symbol. Claims come from the
doc-pass findings digest or from a guide's own limitations section; a handful were re-verified
directly against the generated C# and the packed `.nupkg` during this write-up, and those are
marked *(verified 2026-08-02)* with the file that was read.

**Context worth keeping.** The pass also produced positive signals that bound the problem: BlinkID
emits **zero** `SB0001` stubs and its test app has zero skips; MapLibre emits zero `SB0001` and its
entire `ApiDefinition.cs` is callable; Lottie has exactly one `SB0001` in ~41k lines of generated
C#; the `string`→`NSUrl` convenience overloads and Swift `>>` → `Append` composition both landed
visibly in Kingfisher; and `_IncludeSwiftResourceBundleStubs` genuinely fixed the BlinkIDUX
resource-bundle trap in 0.18.1. The surface is broadly good — these items are the residue.

Buckets: **A** naming/disambiguation · **B** correctness & coverage · **C** packaging ·
**D** ObjC-generator gaps · **E** docs & pipeline.

---

## A. Generator naming & disambiguation

### A1 — Retire bare numeric collision suffixes, and split the async axis from the collision axis (P1)

**This is the headline item, called out explicitly by the owner: shipped bindings should never
surface a bare numeric suffix.**

**Problem.** When two Swift overloads collapse to the same C# signature, the generator keeps the
first and appends `2` to the next. The suffix carries no semantics, is not derivable from the Swift
source a consumer is reading, and — worse — is assigned per collision group with nothing in the
emitted artifact pinning *which* member of the group keeps the bare name. Separately, `Async` is
overloaded as a concept: it marks both the true Swift-`async` binding and the
`TaskCompletionSource` wrapper over a callback form, and `Async2` is used both for "the other async
lane" and for "a plain overload collision that happens to be async". One suffix, three meanings.

**Evidence** *(verified 2026-08-02 against the regenerated 0.18.1 C#)*. Filtering internal
trampolines (`SBW_*`, `PInvoke_*`, `MCB_*`) out of the emitted public surface across all nine
libraries leaves exactly **ten** consumer-visible numeric-suffixed member names, in two libraries:

| Library | Members | Declaring file |
|---|---|---|
| Lottie | `Play2`, `FrameTime2`, `LoadedFromAsync2` | `Lottie.Types.LottieAnimationView.cs`, `…LottieAnimationLayer.cs`, `…CompatibleAnimationView.cs`, `…LottieAnimation.cs`, `…DotLottieFile.cs` |
| Stripe | `Create2`, `CreateAsync2`, `ConfirmAsync2`, `ConfirmSetupIntent2`, `HandleNextAction2` (×2), `PresentAsync2` (×2), `PresentForTokenAsync2` | `StripePaymentSheet.Types.PaymentSheet.cs`, `StripePayments.Types.STPPaymentHandler.cs`, `StripeFinancialConnections.Types.FinancialConnectionsSheet.cs` |

Three concrete shapes, each a different flavour of the same defect:

1. **Semantics live in the discarded argument labels.** `LottieAnimationView` emits seven `Play`
   overloads plus one `Play2(double? fromFrame, double toFrame, LottieLoopMode?, Action<bool>?)`.
   It collides with `Play(double? fromProgress, double toProgress, …)` — identical C# signature,
   *opposite* unit. A consumer who picks wrong gets a silently wrong animation range, not an error.
   Same shape in `LottieAnimation.FrameTime2(double time)` (seconds → frame) vs
   `FrameTime(double progressTime)` (progress → frame). The discriminator (`fromFrame:toFrame:` vs
   `fromProgress:toProgress:`) is present in the Swift signature and is thrown away.
2. **The suffix does not even identify a distinct member.** On `STPPaymentHandler`, `HandleNextAction`
   and `HandleNextAction2` have **byte-identical C# signatures**
   (`string, ISTPAuthenticationContext, string?, Action<STPPaymentHandlerActionStatus, STPPaymentIntent?, NSError?>`),
   and the pair repeats for the setup-intent variant. The `2` member turns out to be the
   *upstream-deprecated* one (`[Obsolete("Deprecated: Use handleNextAction(paymentIntentClientSecret:authenticationContext:completion:) instead..")]`)
   — but that is an accident of emission order, not a rule. Nothing prevents an upstream reordering
   from swapping which member is `X` and which is `X2`, which is a **silent source-breaking change
   for every consumer** across an otherwise-additive patch bump.
3. **`Async` vs `Async2` is a second, unrelated axis riding the same suffix.** Per the Stripe guide's
   translation table, `…Async` is a `TaskCompletionSource` wrapper over the callback form
   (the `CancellationToken` cancels only the await) while `…Async2` is the true Swift-async binding
   with real cancellation and `@MainActor` awareness — so `2` means "better" here. But
   `FlowController.CreateAsync2` is the *SetupIntent* variant, where `2` means only "collided". A
   consumer cannot tell the two cases apart from the name, and the guide has to explain both.

The rename is already a recorded, addressable decision — `LottieDatabase.xml` carries
`<method swiftName="play" csharpName="Play2" …/>` and `<method swiftName="frameTime" csharpName="FrameTime2" …/>`
— so the collision is resolved at a point that has the full Swift signature in hand.

**Impact.** Silent wrong-call risk (shape 1), undiscoverable API (shape 2), unstable names across
versions (shape 2), and a documentation burden that no amount of guide-writing removes (shape 3).
It is also the first thing a reader notices, which makes it a credibility item beyond its raw
consumer cost.

**Direction** (direction, not a design):

- **Make the collision resolver label-aware.** Derive the disambiguator from the Swift argument
  labels or the first differing parameter's role — `PlayFromFrame` / `PlayFromProgress`,
  `FrameTimeForTime` / `FrameTimeForProgress` — with the *natural* name kept for the member the
  Swift API treats as primary, and the derived name applied to the others. Deterministic from the
  Swift signature, so it is stable across upstream reorderings.
- **Separate the async lane from the collision lane.** Decide one spelling per lane
  (e.g. the true Swift-async binding owns `…Async`; the callback wrapper gets its own distinct
  suffix, or is not emitted where the true async form exists) so that a numeric suffix never
  appears on an async member for a reason unrelated to asyncness.
- **Identical-signature pairs should not both ship.** The `HandleNextAction`/`HandleNextAction2`
  case is not a naming problem at all — it is two emissions of the same C# signature where one is
  upstream-deprecated. Prefer collapsing to a single member (carrying the deprecation) over
  disambiguating them.
- **Gate it.** A ship gate that fails when any emitted *public* member name matches `[A-Za-z]+\d+$`
  and is not a legitimate upstream name (`GetLast4`, `NIDActionArity1V0`) turns this from a policy
  into an invariant. Where the resolver cannot derive a principled name, a loud diagnostic and a
  refused member is preferable to a silent `2`.
- **Write the resulting rules down once** — see [E1](#e1--publish-the-naming--disambiguation-contract-p2).

**Affected libraries.** Lottie, Stripe (directly); every future binding (structurally).

**Status (2026-08-03).** Fixed — session 03 (`81bebf0c`). Argument-label-aware overload ladder in `OverloadNameDisambiguator`, with a Swift-parameter-type rung and refusal as the last rung, so the public surface never carries a bare numeric overload suffix; a build gate reads the resolver's own assignment records. The async axis was split out of the collision key. Written up in the wiki, § "How names are chosen".

---

### A2 — Five coexisting de-collision schemes for names (P2)

**Problem.** Beyond the numeric suffix, the pass found **five** different, individually reasonable
but mutually inconsistent schemes for resolving a name collision. Each guide had to publish its own
translation table because no two libraries resolve collisions the same way.

| Scheme | Where it renames | Evidence |
|---|---|---|
| `Info` / `Kind` suffix on the **nested type** | type, not member | Nuke `ImagePipeline.ConfigurationInfo`, `CacheInfo`, `ImageRequest.OptionsInfo`, `PriorityKind`, `ImageTask.StateKind`, `ImageResponse.CacheTypeKind`; Stripe `PaymentSheet.ConfigurationInfo`, `Appearance.FontInfo` / `ColorsInfo` / `ShadowInfo` / `PrimaryButtonInfo`, `AddressInfo`; Facebook `ShareDialog.ModeKind` |
| `Value` suffix on the **property** | member, not type | Stripe `.StatusValue`, `.PermissionsValue` |
| Outer-type **prefix** on the nested type | type | BlinkIDUX `Camera.Position` → `Camera.CameraPosition` |
| `Method` suffix on the **method** | member | BlinkID `StringResult.Value` (property) vs `ValueMethod(AlphabetType)` |
| `Get` **prefix** on the method | member | BlinkIDUX Swift `result()` → `GetResultAsync(…)` where the name collides with a property |

The `Info`/`Kind` scheme is the principled one and is already verified correct and test-pinned
(`not-planned.md` § *Consumer ergonomics* → **U-003**: `ApplyNestedTypeRenames` renames only on a
true CS0102 collision, kind-aware). The problem is that the other four exist alongside it, do the
opposite thing (rename the *member* rather than the type, contra U-003's stated policy), and are
undocumented. The old `Value`-suffixed nested-type naming (`CacheValue`, `ConfigurationValue`) is
gone from the emitted surface — but `Value` now means something else entirely (a renamed property),
so the same token changed meaning between SDK generations.

**Impact.** A consumer cannot predict a name; a guide cannot state one rule; repo memory and docs
that recorded the old meaning of `Value` are actively wrong.

**Direction.** Pick one axis (U-003 already argues the *type* is the right thing to rename, since
the property is the consumer-facing member) and one suffix vocabulary, then converge the outliers
onto it: fold `Camera.CameraPosition` into the `Info`/`Kind` scheme, and re-examine whether the
property-side `Value` / method-side `Method` / `Get` renames are collisions the type-side rule could
have absorbed. Where a member-side rename is genuinely unavoidable, make the token distinct from
any type-side token so a reader can tell which side moved. Then publish the table (E1).

**Affected libraries.** Nuke, Stripe, BlinkID, BlinkIDUX, Facebook.

**Status (2026-08-03).** Fixed — session 02 (`90d8089d`). `NameCollisionPolicy` is the single source of truth for the de-collision vocabularies and their precedence (six schemes). The believed "outer-type prefix on the nested type" fifth scheme was refuted — the vendor declares that name itself — and Apple platform-type flattening is a separate identity mapping, deliberately not converged here. Written up in the wiki, § "How names are chosen".

---

### A3 — Case-only collision between a generated namespace and a generated type (P2)

**Problem.** BlinkID emits a class `BlinkIDSdk` and a namespace `BlinkIDSDK` (from the Swift
`enum BlinkIDSDK` container). They differ only in case. C# resolves it, but a reader cannot see the
difference at a glance, IDE completion presents two near-identical entries, and any refactor tool
that case-normalizes is a hazard.

**Impact.** Confusing rather than blocking — but it is the kind of thing a consumer reports as a
binding bug.

**Direction.** Treat case-insensitive equality as a collision when a Swift container type becomes a
namespace, and disambiguate deterministically (the `.NET` acronym convention already applied to
`Sdk` vs `SDK` is the seam to reuse).

**Affected libraries.** BlinkID.

**Status (2026-08-03).** Fixed — session 02. `CaseOnlyCollisionPass` covers both the type-vs-namespace and the member-vs-member case-only shapes; the member arm is the one place the system deliberately reaches for a numeric suffix.

---

### A4 — Generated type names that collide with platform types in scope (P3)

**Problem.** `BlinkIDUX.UIEvent` collides with `UIKit.UIEvent`; any file that does
`using UIKit;` alongside `using BlinkIDUX;` needs an alias
(`using BUXUIEvent = BlinkIDUX.UIEvent;`). This is inherent to module-name-as-namespace and
faithful to the vendor's Swift, so it may be a documentation item rather than a generator item.

**Impact.** One alias line, documented in the BlinkIDUX guide.

**Direction.** Decide policy explicitly: either accept (faithful naming wins, document it) or emit a
build-time note when a generated type name shadows a type in a commonly-imported platform namespace.
Do **not** rename silently — that trades a compile-time inconvenience for an unpredictable name.

**Affected libraries.** BlinkIDUX.

**Status (2026-08-03).** Owner register. Left unfunded: the only shape that clears the prediction-gate freeze policy is a warn-only diagnostic, because the failure it would prevent compiles. Row in `not-planned.md` § Pending owner decisions.

---

### A5 — Origin-lane naming split inside one mixed vendor (P3)

**Problem.** In Facebook, a type's C# name depends on which lane bound it. ObjC-defined types keep
the full ObjC name (`FBSDKAccessToken`, `FBSDKAppEvents`, `FBSDKGraphRequest`); Swift-defined types
keep the short Swift name (`Settings`, `Profile`, `LoginManager`, `ShareDialog`). Meta's own Swift
docs write `AccessToken.current`, which in C# is `FBSDKAccessToken.CurrentAccessToken` — because
the Swift spelling is a rename of an ObjC class. Per module: `FBSDKCoreKit_Basics` is entirely
ObjC-shaped, `FBSDKLoginKit` and `FBAEMKit` entirely Swift-shaped, `FBSDKCoreKit` mixed.

**Impact.** The Facebook guide calls this "the rule that costs the most time if you don't know it".
It is inherent to the two-lane architecture, not a defect.

**Direction.** Consider whether the mixed-binding path can harmonize the *managed* name toward the
Swift spelling (the vendor's own documented name) while `[Register]`/`Name =` preserves native
registration — the same declaration-vs-registration split already used elsewhere in the ObjC
emitter. If that is not safe, this stays a documentation item permanently and should be recorded as
such rather than re-litigated per vendor.

**Affected libraries.** Facebook (structurally: every mixed ObjC+Swift vendor).

**Status (2026-08-03).** Fixed — session 17 (`403c0aeb`). `ObjCSwiftImportNameRewriter` applies the vetted Swift-import names as the last pre-emission step; `TypeRecord.ObjCRuntimeName` keeps the raw name so superclass resolution and ObjC runtime registration still work. This renames types that are already published, so it is an owner veto point before any package re-release.

---

## B. Generator correctness & coverage

### B1 — `SB0001` stubs land on canonical entry points (P1)

**Problem.** `[Obsolete(DiagnosticId = "SB0001")]` ("no `@_cdecl` wrapper or native thunk
available") is emitted uniformly, with no notion of how load-bearing the member is. In two
libraries it landed on *the* documented entry point for the whole product.

**Evidence.**

- **Mappedin `MapView.GetMapData(IGetMapDataOptions, …)`** — the only credential/load entry point
  in the SDK. A consumer must `#pragma warning disable SB0001` to load a map at all. 20 members in
  the module carry `SB0001` in total (`MapViewController.GetMapData`/`.Show3dMap` dictionary
  overloads, `UpdateState`/`AnimateState` for `Doors`/`Walls`, `MapView.Tween`,
  `Markers.SetPosition`, `Navigation.TrackCoordinate`, `MapData.GetGeoJSON`,
  `Style.SetFromStyleCollection`). Critically, **the in-repo test app never exercises `GetMapData`,
  so there is no runtime evidence that map loading works at all** — the guide has to say so.
- **Facebook `LoginManager.LogIn(permissions, viewController, handler)`** — the overload every Meta
  documentation snippet uses. Working path is `LoginConfiguration.TryCreate` +
  `LogIn(viewController, configuration, completion)`. The guide calls it "the single most likely way
  to translate a Meta doc snippet into a crash".
- Lower-stakes but same class: Nuke `ImagePipeline.LoadImage(request, progress, completion)` and
  `ImageDecoderRegistry.Decoder(context)`; Kingfisher `KingfisherWrapper<T>.Image(byte[], …)` /
  `AnimatedImage(…)` / `DownsampledImage(…)` and
  `ImageDownloader.DownloadImage(url, KingfisherParsedOptionsInfo, completionHandler)` /
  `DownloadLivePhotoResource(…)`; Lottie `DotLottieFile.LoadedFrom(byte[], string, DispatchQueue, handler)`
  (the single `SB0001` in that library); BlinkIDUX `ScanningViewModel<…>.analyzer`.

**Impact.** A stub on a leaf convenience overload is a documented workaround. A stub on the only
credential entry point means the package's primary flow is unproven and un-recommendable, and the
guide can only say "suppress the warning and validate on device yourself".

**Direction.** Two separable pieces. (a) **Wrapper recovery** for the specific shapes — the
Mappedin and Facebook cases are the ones with product consequences and deserve individual triage.
(b) **Entry-point awareness**: rank `SB0001` members by reachability (is any other public member a
path to this type's primary flow?) and surface a stronger signal — a build-time report line, or a
distinct diagnostic — when a stub has no callable alternative. A count of stubs is not a measure of
severity; today we only have the count. Pairs naturally with the *pending owner decision*
**Q1 — compile-time marking of wrapper-dependent members** in `not-planned.md`.

**Affected libraries.** Mappedin, Facebook (P1 severity); Nuke, Kingfisher, Lottie, BlinkIDUX
(routine).

**Status (2026-08-03).** Partly fixed, remainder report-only — session 05 (`504c464a`). 6 of 20 SB0001 cases recovered; the prominence ranking stayed a report, not a gate.

---

### B2 — Managed conformers that are silently never invoked (P1)

**Problem.** The reverse-dispatch (C#-implements-a-Swift/ObjC-protocol) path has three outcomes
today and they are not consistently distinguishable by the consumer:

1. **Works** — Stripe `ISTPCardFormViewDelegate`, `ISTPAUBECSDebitFormViewDelegate` forward
   correctly.
2. **Fails loudly** — `SB0008` on Stripe `STPPaymentHandler` (×4, `ISTPAuthenticationContext`
   never invoked), `ISTPIssuingCardEphemeralKeyProvider` (`SB0003`), Kingfisher
   `KF.DataProvider(IImageDataProvider?)` (`SB0008`), Facebook
   `ShareDialog.Dialog(…)`/`Show(…)` (`SB0008`); `STPApplePayContext.TryCreate` throws
   `NotSupportedException` for a C#-authored delegate.
3. **Fails silently** — **Stripe `ISTPPaymentCardTextFieldDelegate`: a C# implementation compiles
   clean, carries no diagnostic, is accepted by the API, and receives no callbacks, because the
   generated proxy's reverse-dispatch vtable is empty.**

Outcome 3 is the defect. It is the only failure mode in the entire pass with no compile-time
signal, no runtime exception, and a working sibling on the same type family to make it look like
the consumer's mistake.

A related inconsistency: on Facebook `ShareDialog`, the `ShareContent` **getter** is `SB0006`
(throws) while the **setter** works, and the static `Dialog`/`Show` factories are `SB0008` while the
identical-argument constructor is fine. Equivalent members get non-equivalent treatment, so a
consumer cannot generalize from one member to the next.

**Impact.** A silently dead delegate is the worst outcome a binding can produce: it costs debugging
time proportional to the consumer's trust in the binding, and it is invisible to every gate we run.

**Direction.** Make an empty (or partial) proxy vtable a **detectable emission state**: if no
method of a protocol's reverse-dispatch proxy is populated, refuse to emit an implementable
interface, or emit it with the same `SB0008` marking the loud cases already get. The general
principle — *never emit a surface whose only failure mode is silence* — is the item; the individual
Stripe delegate is the fixture. The `SB0006` getter/setter and `SB0008` factory/constructor
asymmetries should be checked in the same pass: if the getter genuinely cannot work while the setter
can, that is fine and documentable; if it is an artifact of per-accessor analysis, it is a bug.

**Affected libraries.** Stripe (P1 case), Facebook, Kingfisher.

**Status (2026-08-03).** Fixed — session 04 (`a40f87c4`), option (a): a protocol proxy whose vtable fills zero slots is no longer registered, and its interface is marked with a warning-level `[Obsolete(DiagnosticId = "SB0010")]`. Option (b), removing the interface entirely, was considered and rejected because it would break forward-direction code that works today; whether to revisit that is an owner-register row.

---

### B3 — Closure-carrying initializers strand entire product surfaces (P1)

**Problem.** When an initializer takes a closure the generator cannot marshal, the *type* is often
still emitted — so a whole family of downstream API exists, compiles, and is uncallable because its
one required argument cannot be constructed.

**Evidence.**

- **Stripe.** `PaymentSheet.IntentConfiguration.init(confirmHandler:)` is unmarshalable, which
  takes out **deferred-intent PaymentSheet**, **`EmbeddedPaymentElement`**, and
  `FlowController.Create(intentConfiguration, …)` — all three exist in the surface with no
  constructible argument. Same class: `StripeCustomerAdapter.init(customerEphemeralKeyProvider:)`
  (CustomerSheet dead; `ICustomerAdapter` members all `SB0003`) and `EmbeddedComponentManager`
  (no public constructor → the entire StripeConnect embedded-component surface is unreachable).
  The guide routes readers to the client-secret constructors and
  `PaymentSheet.CustomerConfiguration`.
- **Mappedin.** Members with non-trivial closure parameters are skipped outright, so
  `MapData.GetByType` / `GetById` / `GetByExternalId` — the vendor's canonical way to enumerate
  spaces and POIs — **do not exist in C# at all**. Typed event subscription (`MapView.on`,
  `MapData.on`, `BlueDot.on`) is likewise absent; only the stringly-typed
  `MapViewController.On(eventName, Action<object?>)` bridge works. The guide's verdict: "you cannot
  walk the full map graph from C#".

**Impact.** These are not edge members. They are the modern/recommended flow (Stripe deferred
intents) and the canonical enumeration API (Mappedin). Both guides had to teach an older or
lower-level path as the primary one.

**Direction.** Already an active trajectory — `roadmap.md` § *Demand-driven capability backlog* →
**UnsupportedClosure remaining shapes** (~600 skips, #5 skip reason). This item's contribution is
**consumer-demand evidence to prioritize the shapes**: an escaping non-throwing closure in an
**initializer** position, where the closure's absence orphans a whole type graph, is worth more than
an equal count of leaf-method closure skips. Two adjacent register entries bound the design:
*Async-closure start-thunk bridge is gated to async, cdecl-wrapped carriers* and
*Struct/enum-constructor exclusion from the unsupported-closure tombstone* (both under
§ *Pending owner decisions*). Also worth measuring: when an initializer is refused, how many
downstream public members become unreachable — an orphan count is the number that makes this
prioritizable.

**Affected libraries.** Stripe, Mappedin (P1); Kingfisher, BlinkIDUX (adjacent shapes).

**Status (2026-08-03).** Fixed within a bounded scope — session 06 (`927f41a4`); the B3(b) diagnostics landed in session 01. B3(c), widening the tombstone to struct and enum constructors, is trigger-gated in `not-planned.md` and its trigger has not fired.

---

### B4 — Swift struct value semantics do not survive the projection: silent write-back no-ops (P1)

**Problem.** Swift structs project as C# classes wrapping a copied native buffer. A property getter
therefore hands back a **copy**, and mutating through it compiles, runs, returns no error, and
changes nothing.

**Evidence.**

- Lottie: `LottieConfiguration.Shared.RenderingEngine = RenderingEngineOption.CoreAnimation;`
  silently no-ops; the correct form is whole-value assignment
  (`LottieConfiguration.Shared = new LottieConfiguration(…)`).
- BlinkID: `sessionSettings.ScanningSettings.ReturnInputImages = true;` **no-ops and leaks** (the
  intermediate copy is an `IDisposable` nobody disposes). Nearly the entire BlinkID surface is
  `ISwiftStruct`, so this is the library's dominant hazard — its guide leads with
  read → mutate → assign-back.
- Stripe: same shape; the guide teaches read → mutate → assign-back defensively, and the test app
  only ever mutates a copy and re-reads the local, so **the cheaper direct form has never been
  proven either way** (see [E3](#e3--prove-struct-write-back-at-runtime-p2)).

**Impact.** Silent wrong behaviour plus a leak, on the most idiomatic C# spelling a consumer will
reach for first. Three of nine guides have a dedicated warning box for it.

**Direction.** `roadmap.md` places *"Structs projected as C# value types"* explicitly out of scope
(safe only for the frozen+blittable subset), so the projection itself is not the lever. The
tractable levers, cheapest first: **(a)** a Roslyn analyzer shipped with the SDK that flags
`expr.StructProperty.Member = …` where the intermediate is a struct-projected type — this is a
pure additive diagnostic with no ABI risk and would eliminate the entire class at compile time;
**(b)** making the intermediate copy not leak (the BlinkID half of the defect is a lifetime bug
independent of the semantics); **(c)** generator-emitted write-back for the *property-of-property*
shape where the parent is itself addressable. (a) is the item; (b) should be checked regardless.

**Affected libraries.** BlinkID (dominant), Lottie, Stripe, BlinkIDUX; structurally any
struct-heavy binding.

**Status (2026-08-03).** Verified correct, plus an analyzer — session 07 (`2ba3849b`): a runtime proof and the SB1003 diagnostic. One known false negative — properties reached through a generated protocol interface — is recorded in `not-planned.md`.

---

### B5 — Concrete types do not carry their protocol conformance (P2)

**Problem.** Nuke's built-in processors — `ImageProcessors.Resize`, `Circle`, `RoundedCorners`,
`GaussianBlur`, `Anonymous` — do **not** implement the generated `IImageProcessing` interface
(only `Composition` and `CoreImageFilter` do). They therefore cannot be passed in
`ImageRequest.Processors`, which is the main pipeline-processing flow. The guide's workaround is
manual `Process(image)` on each processor, outside the pipeline.

**Impact.** The headline feature of the library ("declare processors on the request, the pipeline
applies and caches them") is unreachable with the stock processors. This is the largest single
functional gap found in an otherwise near-complete binding.

**Direction.** Determine why the conformance is emitted for two types and not the other five
(conditional conformance? extension-declared conformance? conformance declared in a different
module?) and close it. The asymmetry within one module makes this look tractable rather than
structural.

**Affected libraries.** Nuke.

**Status (2026-08-03).** Diagnostics session 01 (`8b65164c`), fixed session 09 (`485eb7ae`). The corpus count moved 496 → 400 and 6 protocol interfaces were gained.

---

### B6 — Only *trailing* Swift default arguments collapse into shorter overloads (P2)

**Problem.** Swift default arguments become C# overloads, but only a trailing run of defaults is
dropped. A Swift initializer that defaults *every* parameter therefore does not necessarily produce
a parameterless C# constructor if any non-defaulted parameter sits between defaulted ones — or if
the overload set is truncated before it gets there.

**Evidence.** BlinkID `ScanningSettings()` — 22 defaulted arguments in Swift — emits **no**
parameterless C# constructor; the shortest overload takes 18 arguments. The guide's instruction is
to never construct one, and instead obtain it from `new BlinkIDSessionSettings()` and mutate (which
then runs into [B4](#b4--swift-struct-value-semantics-do-not-survive-the-projection-silent-write-back-no-ops-p1)).
BlinkIDUX `BlinkIDSdkSettings` bottoms out at a 5-argument overload though Swift defaults 8 of 9 —
the consumer must pass `licensee`, `helloLogEnabled`, `downloadResources`, and
`resourceDownloadUrl` explicitly, re-typing Swift's defaults by hand from the vendor docs.

**Impact.** Both guides call this the biggest ergonomic wart in those bindings. It also forces the
guide to transcribe upstream default *values* into prose, which rots the moment the vendor changes
one.

**Direction.** Emit the full default-argument overload lattice (or at least the all-defaults
initializer) rather than only the trailing-suffix collapse. If the combinatorics are the blocker,
the all-defaults form specifically is the one with outsized consumer value. Note the interaction
with the existing placeholder-default recovery path (`not-planned.md` § *Consumer ergonomics*,
U-004/U-008), which already reconstructs truncated overloads for a different reason.

**Affected libraries.** BlinkID, BlinkIDUX.

**Status (2026-08-03).** Fixed — session 10 (`7079e81`): an all-defaults omission overload.

---

### B7 — Swift `OptionSet`s lose their bitwise operators (P2)

**Problem.** A Swift `OptionSet` emits named static members plus a `RawValue` and a raw-value
constructor, but no `|` operator, no `Union`/`Contains`, and no `[Flags]`-style composition. The
consumer does bit math by hand.

**Evidence.** Nuke `ImageRequest.OptionsInfo` and `ImagePipeline.CacheInfo.Caches`. The guide's
documented workaround is literally:
`new ImageRequest.OptionsInfo((ushort)(OptionsInfo.DisableMemoryCacheReads.RawValue | OptionsInfo.DisableDiskCacheWrites.RawValue))`.

**Impact.** Ergonomics only — but it is the most-repeated snippet in the Nuke guide, and the cast
width (`ushort` here, `nint` for `Caches`) is a footgun the consumer has to get right.

**Direction.** Emit `|`, `&`, `~`, and a `Contains`/`HasFlag` equivalent on generated OptionSet
types (they already carry the raw value and its width, so this is additive C# with no ABI surface).
Adjacent register entry: *`CdeclParamMapper` OptionSet arm assumes a non-failable `init(rawValue:)`*
in `not-planned.md` § *Cross-cutting emitter latents* — same type family, different mechanism.

**Affected libraries.** Nuke; any binding with a Swift `OptionSet`.

**Status (2026-08-03).** Fixed — session 08 (`34b2932f`): `OptionSetOperatorEmitter`. An option declared above bit 31 of a platform-width raw value is still out of reach; recorded in `not-planned.md`.

---

### B8 — Generic `Delegate<>` properties are skipped, removing a builder's entire callback surface (P2)

**Problem.** Kingfisher's `KF.Builder` exposes `onSuccess` / `onFailure` / `onProgress` as
`Delegate<…>` generic properties. All three are dropped with "generic constraint could not be
satisfied", so **the fluent builder has no result callbacks at all** — the consumer must abandon the
builder and fall back to `KingfisherManager.RetrieveImageAsync`.

**Impact.** The builder is the library's marquee API (`KF.Url(...).Set(imageView)`); it works for
the fire-and-forget case and silently offers no way to observe the outcome. The guide documents the
fallback, which means the guide's primary example and its "how do I know if it worked" example use
two different APIs.

**Direction.** Determine whether the constraint failure is inherent (a Swift generic the C# type
system cannot express) or an over-broad constraint on the emitted property — the adjacent
`not-planned.md` entry *some-protocol generic constraint over-broad* (in the archived 0.10.0 bug
set) suggests the latter is a known shape. If inherent, a non-generic projection of the callback
property is worth considering.

**Affected libraries.** Kingfisher.

**Status (2026-08-03).** Triage only — session 09, then routed to `not-planned.md` § Emitter — generics & concrete specialization (generic callback-holder properties).

---

### B9 — `SB0003` protocol-typed dispatch is the dominant limitation class (P2)

**Problem.** By raw count, `SB0003` ("member not dispatchable through a protocol witness") is the
largest limitation family in the pass: **32 members in Nuke, 32 in Kingfisher, 12 in BlinkIDUX**,
plus Stripe's `ICustomerAdapter` and `ISTPIssuingCardEphemeralKeyProvider`. The pattern is
consistent: members whose signature carries `Foundation.Data`, an optional return, a subscript, or a
closure are non-callable through an *interface reference* but work fine on the concrete type.

**Evidence.** Nuke `IDataCaching` / `IImageCaching` / `IImageDecoding` / `IImageEncoding` /
`IImageProcessing` / `IDataLoading`; Kingfisher `ICacheSerializer.Data(…)` / `.Image(…)`;
BlinkIDUX `ICameraModel.Status` / `.Orientation` / `.Error`, the async
`StartAsync`/`StopAsync`/`FocusAndExposeAsync`, `IPreviewSource.Connect(IPreviewTarget)`,
`IReticleStateProtocol.Appearance`; `IScanningResultProtocol` is empty (`SB0004`).

**Impact.** Bounded — every guide documents the same workaround (hold the concrete type) and it is
rarely load-bearing. It matters mainly when a consumer installs their own implementation into a
config property and then reads it back through the interface.

**Direction.** `roadmap.md` § *Apple-framework by-design limits* already records
**RC-SB0003 reverse witness dispatch** as case-by-case and largely a by-design Swift limit, so this
is **not** a call to fix the class. It is a call to **triage it by sub-shape**: the four signature
features above are distinguishable, and if (say) optional returns are recoverable while
`Foundation.Data` is not, the population splits into a fixable half and a documentable half. Today
they are one undifferentiated pile of 76+ members and every guide describes the pile.

**Affected libraries.** Nuke, Kingfisher, BlinkIDUX, Stripe.

**Status (2026-08-03).** Report-only — session 01: the `ProtocolWitnessNotDispatchable` report, 87 rows.

---

### B10 — Generated overload sets that are ambiguous at the call site (P2)

**Problem.** Overloads that differ only by an optional/nullable parameter, or only by an
additive convenience axis, can be unresolvable at the call site.

**Evidence.** Kingfisher `ImageDownloader.DownloadImageAsync(url)` vs
`DownloadImageAsync(url, options)` differ only by an optional `progressBlock` and produce a
compile-breaking ambiguity; the guide's workaround is an explicit named argument or the
parsed-options form. Same family for `StoreAsync`.

**Impact.** A compile error rather than a silent fault, and the guide documents the escape — but it
lands on the two most-called members of the downloader.

**Direction.** Largely already registered: `not-planned.md` § *Consumer ergonomics* → **U-008**
(`Placeholder(null)` nullable-literal ambiguity) and **U-007** (convenience-overload scope, incl. the
by-design `Foo(NSUrl)`/`Foo(string)` untyped-`null` sharp edge). What the docs pass adds is that the
ambiguity is reachable with **real arguments**, not just an untyped `null` literal — which is a
stronger trigger than the registered entries assume. Worth re-reading U-007/U-008 against the
Kingfisher `DownloadImageAsync` case specifically before deciding they cover it.

**Affected libraries.** Kingfisher.

**Status (2026-08-03).** Fixed — session 10: `OverloadAmbiguityGuard`. Which member loses under the suppression rule is a policy call, so it is an owner-register row.

---

### B11 — SwiftUI value types are unconstructible from C#, making theming APIs read-only (P2)

**Problem.** `BlinkIDTheme.Shared`'s instance properties are typed `SwiftUI.Color` / `SwiftUI.Font`.
Those project as marshalling shells with **no public constructor**, so every setter on the theme is
unusable. The only working path is the 21 static `Set*(SwiftColor / SwiftFont)` bridge methods.

**Impact.** A consumer reading the vendor's theming docs finds the exact property they want, can
read it, cannot set it, and gets no diagnostic explaining why. The guide has to redirect the entire
theming chapter to the static bridge.

**Direction.** Either give the SwiftUI value shells constructors from their `Swift.SwiftColor` /
`Swift.SwiftFont` equivalents (the bridge proves the conversion exists), or mark the un-settable
properties so the failure is visible at compile time. Bounded by `roadmap.md`'s SwiftUI/result-builder
scope, but this is a *value* type, not view composition — it sits inside the supported side of that
line.

**Affected libraries.** BlinkIDUX.

**Status (2026-08-03).** Fixed for `Color` and `Font` — session 11 (`cb64a1b8`). The other SwiftUI types still declared `frozen="false"` against a frozen reality are recorded in `not-planned.md`; the same mismatch is what produced the SIGSEGV this session root-caused.

---

### B12 — A generated UX bridge that marshals only a scalar outcome (P2)

**Problem.** `BlinkIDUXViewSession.CreateAsync` returns an outcome code (0–3 / −1). The actual
`BlinkIDScanningResult` **never crosses the ABI**, so a consumer who uses the packaged scanning UX
learns only *that* a scan finished, never *what* was scanned. Extracting fields requires abandoning
the packaged UX for the headless analyzer plus a hand-written camera pipeline.

**Impact.** This is a product gap, not a wart: the packaged UX is the reason BlinkIDUX exists as a
separate package, and in C# it cannot return scan data. The guide has to lead with it as "the
headline caveat".

**Direction.** Extend the generated bridge to marshal the result payload (or a projection of it)
rather than a status code. Worth filing as its own issue with the vendor surface attached, since the
fix is in the bridge template rather than in general marshalling.

**Affected libraries.** BlinkIDUX.

**Status (2026-08-03).** Fixed — session 15 (`e2493471`): an async `View`'s result callback hands back the value it produced.

---

### B13 — `api-surface.md` lists members that were never emitted (P2)

**Problem.** The generated `{Module}.api-surface.md` is documented as the authoritative member list
and is what a guide author reaches for first. For Mappedin it lists `MapView.GetInit()` and a
4-argument `MapView.GetInView` that **do not exist in the emitted C#**. The doc agent found them by
trusting the report and getting a compile error.

**Impact.** The one artifact whose whole job is to be trustworthy about the surface is not. Its
value is asymmetric: a missing entry costs a consumer nothing (they read the `.cs`), a *phantom*
entry costs them a debugging cycle and undermines the artifact.

**Direction.** Derive the report strictly from the post-collision, post-refusal emitted member set
(it is documented as doing this, so the divergence is a bug worth root-causing rather than a scope
gap), and consider a self-check that reconciles report entries against the emitted syntax tree.
Related but distinct residuals are already registered: `not-planned.md` § *Consumer ergonomics* →
**U-009** (a) properties/subscripts not listed, (b) README not wired to consume it, (c) additive
convenience overloads unrecorded. **Phantom methods are not one of the registered residuals** —
this is new.

**Affected libraries.** Mappedin (observed); the report is emitted for every library.

**Status (2026-08-03).** Fixed — session 16 (`3d628a24`). The verdict was recorded-but-differently-emitted rather than recorded-but-unemitted; `ApiSurfaceReconciler` is now an always-on hard generator error. 282 of 4,385 manifest entries still record a base symbol that no P/Invoke binds — recorded in `not-planned.md`.

---

### B14 — A bridge template default that contradicts the vendor's own default (P3)

**Problem.** The BlinkIDUX bridge's `preferFrontCamera` parameter defaults to `true`, while the
vendor's `ScanningUXSettings.preferredCameraPosition` defaults to `Back` — which is the correct
default for ID scanning. A consumer who takes the bridge default gets the selfie camera pointed at
a document.

**Impact.** Wrong-by-default behaviour on the package's primary flow, trivially fixable.

**Direction.** Fix the default in the bridge template to match the vendor setting.

**Affected libraries.** BlinkIDUX.

**Status (2026-08-03).** Fixed — session 15: `DefaultValue` is carried on `AsyncFlatParam`.

---

### B15 — Builder output type does not match the consumer's parameter type (P3)

**Problem.** Mappedin `Builders.GetMapDataOptionsFromKeys` returns
`IReadOnlyDictionary<string, object>`, but `MapViewController.GetMapData` takes
`IDictionary<string, object>`. The helper's output cannot be passed to the member it exists to feed
without `new Dictionary<string, object>(builderResult)`.

**Impact.** Small, but it is a generated helper that does not compose with its generated consumer —
exactly the kind of thing that reads as carelessness.

**Direction.** Align the collection-interface projection between producer and consumer positions
(prefer the mutable interface on the parameter side, or the same interface on both).

**Affected libraries.** Mappedin.

**Status (2026-08-03).** Verified and routed — session 08. The cast genuinely does not work; two pinning runtime tests plus a `not-planned.md` row. Changing the emitted signature is an owner call, not a silent fix.

---

### B16 — Argument order that inverts upstream intuition (P3)

**Problem.** Stripe `STPCardValidator.ValidationState(string, string)` takes **(year, month)** —
the reverse of the order a consumer familiar with card expiry (`MM/YY`) expects, and both
parameters are `string`, so a swap compiles and validates the wrong thing.

**Impact.** Silent wrong result, but almost certainly faithful to the ObjC selector
(`validationStateForExpirationYear:inMonth:`).

**Direction.** Verify faithfulness first. If faithful, no generator change — it belongs in the guide
(where it now is) and possibly in a "surprising but correct" list. Recorded here only so it is not
re-discovered as a suspected bug.

**Affected libraries.** Stripe.

**Status (2026-08-03).** Verified faithful — session 18. The reported order follows the selector's own order: the parse reads the selector text and the parameter list as two independent positional reads of the same declaration, and no path between parse and emission reorders parameters. No generator change. Recorded in the wiki § "Surprising, but correct" and in `not-planned.md` § Verified correct.

---

### B17 — `@_spi` suppression can hollow a module out while we still ship it as a package (P2)

**Problem.** Upstream `@_spi`/internal annotations are correctly excluded from public bindings.
The consequence at package granularity is that some modules compile to almost nothing but
`// Unsupported: … ModuleInternal (@_spi type)` comments. Stripe's `StripeUICore`,
`StripeCameraCore`, and the umbrella `Stripe` module are in that state — the umbrella's only real
export is `ISTPApplePayContextDelegate`.

**Impact.** We publish NuGet packages (`SwiftBindings.Stripe.UICore`, `…CameraCore`,
`SwiftBindings.Stripe`) whose public surface is effectively empty, while consumers still need them
installed as transitive native dependencies. The Stripe guide's package map has to explain that
three of the fourteen packages exist but contain nothing to call.

**Direction.** Not a generator defect — the exclusion is right. The item is a **packaging/labelling
policy**: decide whether a module whose emitted public surface is below some threshold should be
(a) shipped with a README/description that says "native dependency only, no callable surface",
(b) still shipped silently, or (c) reported at pack time so the decision is conscious per release.
Cheapest useful version is (c): a pack-time line stating emitted public member count per package.

**Affected libraries.** Stripe (3 of 14 packages).

**Status (2026-08-03).** Partly done — session 14 (`cbd5fc22`) added a pack-time member-count rider. Whether a hollow module should fail the pack outright is a product policy call and is an owner-register row.

---

### B18 — App-delegate dictionary overloads dropped for missing key-type projections (P3)

**Problem.** Facebook's `launchOptions` and `openURL:options:` app-delegate overloads are not bound,
because `UIApplication.LaunchOptionsKey` and `UIApplication.OpenURLOptionsKey` have no projection.

**Impact.** None functionally — the guide documents the `sourceApplication:annotation:` variant plus
.NET's `UIApplicationOpenUrlOptions`, and no capability is lost. It is listed because it is a
concrete, named instance of "a missing Apple key-type projection silently deletes an overload
family", which is the generalizable part.

**Direction.** Add the two `UIApplication` key types to the TypeDatabase; more generally, treat a
member dropped *solely* for an unprojected Apple key type as a distinct, countable skip reason so
the population is visible.

**Affected libraries.** Facebook.

**Status (2026-08-03).** Diagnostics session 01, fixed session 08: 4 members recovered.

---

## C. Packaging

### C1 — An emitted SwiftUI bridge P/Invokes a native library the package never ships (P1)

**Problem.** The Lottie package emits `LottieViewSession` and `LottieButtonSession` into the shipped
assembly. Both P/Invoke a native library named `LottieBridge` that **is not in the `.nupkg`**. The
types are constructible-looking, publicly documented by IntelliSense, and fail at load.

**Evidence** *(verified 2026-08-02)*.

- `libraries/Lottie/obj/Release/net10.0-ios/swift-binding/Lottie.SwiftUIBridge.cs` lines 43–79:
  `[LibraryImport("LottieBridge", EntryPoint = "SBW_Lottie_LottieView_Create")]` and nine siblings
  (`_GetViewController`, `_Free`, `_UpdateAnimation`, `_SetResizable`, `_SetIntrinsicSize`,
  `_SetPlay`, `_SetLooping`, `_SetPlaying`, `_SetAnimationSpeed`).
- `local-packages/SwiftBindings.Lottie.4.6.6.nupkg` contains exactly two native artifacts:
  `runtimes/ios-arm64/native/Lottie.xcframework` and
  `runtimes/ios-arm64/native/LottieSwiftBindings.xcframework`. No `LottieBridge.xcframework`.
- The generated MSBuild targets reference it **conditionally** —
  `SwiftBindings.Lottie.targets:64` and `SwiftBindings.Lottie.ProjectReference.targets:52` both wrap
  the `<NativeReference Include="…LottieBridge.xcframework" …>` in
  `Condition="Exists('…LottieBridge.xcframework')"` — so a missing bridge silently no-ops at build
  time instead of failing.
- The digest additionally records that `nm` over the shipped wrapper finds **zero**
  `SBW_Lottie_LottieView_*` symbols.

**Contrast (the correct behaviour):** Kingfisher's generated `SwiftUIBridge.cs` emits **no public
members at all** — that package is UIKit-only and the bridge correctly produces nothing.

**Impact.** The package ships a public API that cannot work. A consumer who finds
`LottieViewSession` in completion has no way to know it is dead until runtime. Any library that
gets a SwiftUI bridge emitted is exposed to the same shape.

**Direction.** Two independent fixes, both cheap: **(a)** do not emit bridge types when the bridge
native artifact is not part of the package — the emission and the packaging decision must be made
from the same input; **(b)** add a **pack-time gate** that resolves every `LibraryImport` /
`DllImport` library name in the emitted assembly against the native artifacts actually in the
`.nupkg`, and fails the pack on a miss. (b) generalizes beyond SwiftUI bridges and is the durable
one. The `Condition="Exists(…)"` in the generated targets should also be re-examined: silently
degrading is what let this ship.

**Affected libraries.** Lottie (live); any library that gets a SwiftUI bridge emitted.

**Status (2026-08-03).** Fixed — session 14: the PackGate P/Invoke resolver plus SWIFTBIND052, so a pack fails when a binding names a native the package never ships.

---

### C2 — Wrapper-required opt-out for closed-source binary frameworks is a manual per-library flag (P2)

**Problem.** BlinkID's binding is generated with `SwiftWrapperRequired=false` because the vendor's
closed-source binary framework has internal types that defeat wrapper compilation. This is correct
for BlinkID and is documented in its guide — but it is a hand-set csproj property, not a recorded,
diagnosable mode. Nothing distinguishes "wrapper legitimately impossible for this framework" from
"wrapper broke and someone silenced it".

**Impact.** Ships fine today (BlinkID has zero `SB0001` stubs and zero test skips — one of the best
bindings in the set). The risk is the next library where the flag hides a real regression.

**Direction.** Make binary-framework wrapper impossibility a **first-class, recorded state**
(detected or declared once, reported in the binding report), so the property is not a free-form
suppression. Relates to the *pending owner decision* **Q1 — compile-time marking of wrapper-dependent
members**: the fail-closed default is right, and this is about making the exceptions legible.

**Affected libraries.** BlinkID (today).

**Status (2026-08-03).** Report-only — session 05: `SwiftWrapperRequired` awareness plus an SDK toggle test.

---

### C3 — MapLibre native artifact reaches consumers via two paths at once (P3)

**Problem.** The MapLibre xcframework ships to consumers through a `resources.zip` sidecar **and**
duplicated under `runtimes/ios-arm64/native/`. The pending pure-ObjC-lane change is expected to drop
the second copy.

**Impact.** Package size and ambiguity about which copy is authoritative; no functional defect
observed.

**Direction.** Confirm the pure-ObjC-lane change drops the duplicate, and check the same shape does
not exist for other ObjC-lane libraries.

**Affected libraries.** MapLibre.

**Status (2026-08-03).** Verified already fixed — session 14; the change had landed in `d3d8b276` (2026-07-31). A repacked MapLibre carries no `runtimes/` folder (20.7 → 10.5 MB).

---

## D. ObjC-generator gaps

The ObjC lane is thinner than the Swift lane by design and its gap register lives in
`not-planned.md` § *ObjC & mixed bindings*. Items here are either **new** (D1, D3) or add
consumer-facing evidence to an existing entry (D2, D4, D5).

### D1 — No `[Field]` bindings: every `extern NSString * const` is unbound (P1 for the ObjC lane)

**Problem.** The ObjC binding emits **no `[Field]` members at all**. Every
`extern NSString * const` in a bound framework — notification names, `userInfo` keys, option-dictionary
keys — has no C# symbol.

**Evidence (MapLibre).** `MLNOfflinePackProgressChangedNotification`, `MLNOfflinePackErrorNotification`
and their `userInfo` keys; the `MLNShapeSourceOption*` family and the tile-source option keys. The
guide's workaround is literal strings, with a trap it has to spell out: **the runtime value of
`MLNOfflinePackProgressChangedNotification` is `"MLNOfflinePackProgressChanged"`** — *not* the symbol
name — while the `MLNShapeSourceOption*` keys' runtime values *are* identical to their names. So the
workaround is correct for one family and wrong for the other, and the only way to know is to read the
framework headers inside the `.xcframework`.

**Impact.** This is the single largest ObjC-lane gap in the pass. Whole feature areas are reachable
only through hand-typed strings: **clustering has no bound symbol at all**, and offline-pack progress
observation requires a string the consumer cannot derive from the documented constant name. It is
also the highest-risk workaround the pass documented — a typo produces a notification that never
fires, with no error.

**Direction.** Emit `[Field]` members for `extern NSString * const` (and the other `extern` constant
kinds) from the clang AST, resolving the value from the symbol at runtime as bgen does — i.e. bind
the *symbol*, so the name/value divergence that traps the workaround stops mattering. Not currently
registered in `not-planned.md`.

**Affected libraries.** MapLibre (observed); every pure-ObjC binding.

**Status (2026-08-03).** Fixed — session 12 (`98ebf20c`): `ObjCConstantsEmitter` emits into `ApiDefinition.cs`, which is where bgen parses the declaration it needs to back a `[Field]`.

---

### D2 — Enum member prefix-stripping is inconsistent (P2)

**Problem.** Plain `NS_ENUM`s get their common prefix stripped
(`MLNUserTrackingMode.FollowWithHeading`, `MLNOrnamentPosition.BottomLeft`, `MLNLineCap.Round`), but
`NS_OPTIONS` flag enums do not (`MLNMapDebugMaskOptions.MLNMapDebugTileBoundariesMask`) and neither
does `MLNWellKnownTileServer` (`.MLNMapLibre`, `.MLNMapTiler`, `.MLNMapbox`).

**Impact.** Ergonomics only — the guide's advice is "let IntelliSense confirm rather than assuming" —
but it means no rule can be stated, which is the same failure mode as [A2](#a2--five-coexisting-de-collision-schemes-for-names-p2) in the ObjC lane.

**Direction.** Apply one prefix-stripping rule across `NS_ENUM` and `NS_OPTIONS`, and find out why
`MLNWellKnownTileServer` is exempt (a prefix-detection heuristic failing on a name whose members
share less prefix than the type name?). Adjacent registered entry: *ObjC enum-case collision
disambiguation diverges from the `ToPascalCase` reference-site naming* — same emitter, different
mechanism; do not conflate them.

**Affected libraries.** MapLibre.

**Status (2026-08-03).** Fixed — session 17: `StructsAndEnumsEmitter.ResolveCasePrefix` strips an exact type-name prefix first, else the registered module tag at a token boundary, else leaves the case alone. Renames published names, so owner-vetoable.

---

### D3 — `NSValue` category class methods bind as instance extension methods (P2)

**Problem.** `+[NSValue valueWithMLNCoordinate:]` — a **class** method on a category — binds as an
**instance** method, so constructing one requires a throwaway receiver:
`NSValue.FromCGPoint(CGPoint.Empty).ValueWithMLNCoordinate(coord)`.

**Impact.** The workaround is absurd enough to read as a binding bug, and it is on the path for every
coordinate/bounds/span/transition value MapLibre's runtime-styling API takes.

**Direction.** Preserve the `+`/`-` distinction when projecting category members onto a foreign
class. Adjacent registered entry: *Projected category accessors omit `ArgumentSemantic`* — same
category-projection code path, so the two are worth doing together.

**Affected libraries.** MapLibre.

**Status (2026-08-03).** Fixed — session 13 (`b0ddba46`): `ObjCCategoryStaticsEmitter`, so a category's class methods are callable without a receiver.

---

### D4 — `NS_TYPED_EXTENSIBLE_ENUM` constants are silent tombstones (P2)

**Problem.** Facebook's `AppEventName`, `AppEventType`, and `AppEventUserAndAppDataField` produce
**no constants at all**, so consumers pass raw wire strings for every logged event.

**Impact.** App Events is one of the three things anyone installs the Facebook SDK for, and its
entire vocabulary is unbound. The guide documents raw strings and flags it as a known limitation.

**Direction.** Already half-registered: `not-planned.md` § *ObjC & mixed bindings* →
*Two minor FB mixed-binding drops (attribution + cross-module typed-enum)* records that
`FBSDKCoreKit.AppEvents.Name` (an `NS_TYPED_EXTENSIBLE_ENUM`) does not resolve across the module
boundary, with `FBSendButton.ImpressionTrackingEventName` / `FBShareButton.ImpressionTrackingEventName`
as the evidence. **The docs pass raises the severity of that entry**: it is not two dropped members,
it is the entire constant vocabulary of App Events. Overlaps with [D1](#d1--no-field-bindings-every-extern-nsstring--const-is-unbound-p1-for-the-objc-lane) —
both are "constants exist natively, nothing is bound" — and the two should probably be scoped
together.

**Affected libraries.** Facebook.

**Status (2026-08-03).** Fixed — session 12; the same defect as D1.

---

### D5 — An empty `ApiDefinition.cs` in a module that has ObjC metadata (P3)

**Problem.** Facebook's ShareKit emits `namespace FBSDKShareKit { }` — an entirely empty ObjC
ApiDefinition — while the module's whole callable surface comes from the Swift lane.

**Impact.** None observed; the Swift lane covers the surface (`ShareDialog`, `ShareLinkContent`, …).
Flagged only to confirm it is intended rather than a silent parse failure.

**Direction.** Verify the ObjC lane genuinely has nothing public to bind for that module (as opposed
to an umbrella-header or forward-declaration issue). Two registered entries make this worth a
five-minute check rather than an assumption: *Foreign-owned ObjC types are omitted, not resolved
against the sibling assembly* and *Classes forward-declared but never defined in the TU still emit as
empty shells* (whose live examples `FBSDKProfile` / `FBSDKAppLink` are in the neighbouring module),
plus *Umbrella-header convention short-circuits the modulemap*.

**Affected libraries.** Facebook.

**Status (2026-08-03).** Verified correct — session 18. The share kit's umbrella header declares no classes at all (two imports, zero `@interface`), so an empty `ApiDefinition` is the honest output; all three hypotheses this item named are ruled out by the shipped headers. Since D1 that module's `ApiDefinition` is not even empty any more — it carries the constants interface. Recorded in `not-planned.md` § Verified correct, with the cosmetic residual (a namespace-only file for a module with neither classes nor constants) as a trigger-gated P4.

---

### D6 — Delegate method names derived from the last selector keyword (P3)

**Problem.** When bgen's first-keyword name is taken, the C# override name comes from the *last*
keyword, producing overrides like `WithError(MLNMapView, NSError)` for
`mapViewDidFailLoadingMap:withError:` and `FullyRendered(MLNMapView, bool)` for
`mapViewDidFinishRenderingMap:fullyRendered:`.

**Impact.** Undiscoverable override names on the delegate a consumer must implement to do anything
with a map. The guide has to publish a selector → override-name table and tell readers to search
`ApiDefinition.cs` by `[Export("selector")]`.

**Direction.** Inherited bgen behaviour, so likely a documentation item rather than a change — but
worth an explicit decision, since delegate protocols are the highest-traffic surface in any ObjC UI
framework.

**Affected libraries.** MapLibre.

**Status (2026-08-03).** Fixed — session 17: a leading selector part matching the receiver is stripped and the remainder kept. Renames published names, so owner-vetoable.

---

## E. Docs & pipeline follow-ups

### E1 — Publish the naming & disambiguation contract (P2)

**Problem.** All nine guides independently reverse-engineered and published their own "Swift → C#
translation rules" table. Where the rules agree, that is nine copies of one truth that will drift.
Where they disagree ([A2](#a2--five-coexisting-de-collision-schemes-for-names-p2)), the reader has
no way to know which table generalizes.

**Direction.** Once A1/A2 settle the rules, publish **one** canonical naming contract (wiki page or
generated doc) that per-library guides link instead of restating: nested-type `Info`/`Kind`, failable
init → `TryCreate`, payload enum → class + `CaseTag` + `TryGetX`, `async` → `…Async` +
`CancellationToken`, struct → class-over-buffer, protocol → `I`-prefix, extension → `<Type><Module>Extensions`,
and whatever replaces the numeric suffix. Guides then document only their own vendor-specific
surprises. This is also the artifact that makes A1's ship gate explicable to consumers.

**Affected libraries.** All.

**Status (2026-08-03).** Delivered — session 18: the wiki's `How-Bindings-Map.md` gained a § "How names are chosen" covering name shapes, async suffixing, the overload ladder and its refusal rung, the de-collision schemes and their precedence, the ObjC-lane conventions, and a "surprising, but correct" list. Committed locally in the wiki repo as `fb9e7a3`, unpushed; the push is the owner's.

---

### E2 — Wire `api-surface.md` into the consumer-facing docs, and widen it (P2)

**Problem.** The generated `{Module}.api-surface.md` is the best available member list, but nothing
downstream consumes it and it is methods-only.

**Direction.** Already registered as `not-planned.md` § *Consumer ergonomics* → **U-009** residuals
(a) properties/subscripts, (b) packed-README wiring, (c) additive convenience overloads unrecorded.
The docs pass supplies the demand signal for (b) in particular: nine guide authors used the report as
their entry point, so it is now a load-bearing artifact for documentation, not just for audit — and
that raises the cost of [B13](#b13--api-surfacemd-lists-members-that-were-never-emitted-p2)'s phantom
entries.

**Affected libraries.** All.

**Status (2026-08-03).** Fixed — session 16: the api-surface doc packs as `PackageReadmeFile`, and the manifest widened to properties and subscripts, 2,993 → 4,385 entries.

---

### E3 — Prove struct write-back at runtime (P2)

**Problem.** No test in the corpus asserts what happens when a consumer mutates a struct-projected
property in place. Stripe's test app mutates a copy and re-reads the *local*, which passes either
way. So the guides teach the defensive read → mutate → assign-back form everywhere, including for
the cases where the direct form might actually work.

**Direction.** Add a runtime test that mutates through a struct-typed property and re-reads from the
**owner**, for at least one Swift-struct and one ObjC-bridged case. A red proves the no-op and gives
[B4](#b4--swift-struct-value-semantics-do-not-survive-the-projection-silent-write-back-no-ops-p1)'s
analyzer a fixture; a green would let the guides bless the cheaper form for that shape. Either
outcome retires a caveat.

**Affected libraries.** Stripe, BlinkID, Lottie (fixture candidates).

**Status (2026-08-03).** Fixed — session 07: seven runtime tests plus the SB1003 analyzer.

---

### E4 — Consumer-repo doc & test hygiene (P3, not generator work)

Recorded so the findings are not lost; all of these belong to `swift-dotnet-packages`, not to the
generator, and none change the SDK:

- Root `CLAUDE.md` still claims Stripe has 2 internal products (`Stripe3DS2`, `StripeCameraCore`
  marked `"internal": true`). **False today** — `library.json` marks nothing internal and both ship
  on nuget.org at 26.4.1 (14 Stripe packages total; confirmed at nuspec level).
- `CLAUDE.md` and briefs reference a root `KNOWN-ISSUES.md` that no longer exists.
- Repo memory records the old `Value`-suffix nested-type naming (`CacheValue`, `ConfigurationValue`);
  the emitted names are `Info`/`Kind` and `Value` now means a renamed *property*.
- Mappedin is `mode: "zip"` (GitHub release asset) as of 6.7.0, not `manual`; the README's
  "provisioned out-of-band" caveat is stale, and the csproj `<Version>6.2.0</Version>` plus test-app
  comments still say 6.2.0 against upstream 6.7.0.
- BlinkIDUX `tests/Program.cs` carries stale "SWIFTBIND051 wrapper failed" comments and `Fail()`
  paths that now pass; the only genuine skip left is `BlinkIDResultState.ScanningResult`. The test
  csproj's hand-rolled resource-bundle stub target is legacy — 0.18.1's
  `_IncludeSwiftResourceBundleStubs` handles it.
- Nuke tests: the `ImageRequest(NSUrlRequest, processors)` skip is stale (the ctor family is emitted
  now) and its comment — "constructor not exposed in Nuke 13.0 binding" — is wrong.
- Lottie tests: previously-skipped CallConvSwift / protocol-existential cases are all re-enabled and
  passing. `ContentMode` now routes through a real wrapper
  (`SBW_Set_Lottie_LottieAnimationViewBase_contentMode`) but is **untested** — verify visually on
  device before the guide drops its caveat.
- Facebook tests: the alias-import workaround for the 0.18.0 `MT4118` duplicate-declaration defect is
  vestigial at 0.18.1 (all seven colliding native names are gone) and can be removed.
- Fixed during the pass, noted for the record: `StripePaymentSheet/README.md` used a nonexistent
  `AddressDetails.AddressType` (generated name is `AddressInfo`) — that README is live on nuget.org
  with the wrong name; BlinkID / BlinkIDUX READMEs had dead links and placeholder snippets; the
  Mappedin README documented a nonexistent v5 API.

**Status (2026-08-03).** Out of scope for this pass. Every bullet belongs to `swift-dotnet-packages` rather than to the generator; recorded here so the findings are not lost, and carried forward to that repo's own work.

---

## Cross-references to `not-planned.md` / `roadmap.md`

Items that overlap an existing register entry, so the entry is the authority and this doc only adds
consumer evidence:

| This doc | Existing entry | Relationship |
|---|---|---|
| A2 | `not-planned.md` § *Consumer ergonomics* → **U-003** nested-type rename | U-003 verifies `Info`/`Kind` is correct and test-pinned; A2 adds the four *other* schemes found in the wild |
| B3 | `roadmap.md` § *Demand-driven capability backlog* → **UnsupportedClosure remaining shapes**; `not-planned.md` § *Pending owner decisions* → *Async-closure start-thunk bridge…* and *Struct/enum-constructor exclusion from the unsupported-closure tombstone* | B3 supplies demand evidence to prioritize **initializer**-position closures over leaf methods |
| B4 | `roadmap.md` § *Explicitly Out of Scope* → *Structs projected as C# value types* | Confirms the projection is not the lever; B4 proposes an analyzer instead |
| B6 | `not-planned.md` § *Consumer ergonomics* → **U-004/U-008** placeholder-default recovery | Same overload-truncation machinery, different trigger |
| B7 | `not-planned.md` § *Cross-cutting emitter latents* → *`CdeclParamMapper` OptionSet arm assumes a non-failable `init(rawValue:)`* | Same type family, unrelated mechanism |
| B9 | `roadmap.md` § *Apple-framework by-design limits* → **RC-SB0003 reverse witness dispatch** | Confirms the class is largely by-design; B9 asks for sub-shape triage, not a fix |
| B10 | `not-planned.md` § *Consumer ergonomics* → **U-007**, **U-008** | Kingfisher's ambiguity is reachable with *real* arguments, which is a stronger trigger than the entries assume |
| B11, B12, C1 | `not-planned.md` § *Emitter — SwiftUI bridge & KeyPaths* → *SwiftUI beyond current level*; `roadmap.md` § *Explicitly Out of Scope* → result builders | Value types and hosting bridges sit inside the supported side of the result-builder line; view composition stays out |
| B13, E2 | `not-planned.md` § *Consumer ergonomics* → **U-009** (a)(b)(c) | Registered residuals are scope gaps; **phantom methods are new** |
| B18, D4 | `not-planned.md` § *ObjC & mixed bindings* → *Two minor FB mixed-binding drops (attribution + cross-module typed-enum)* | Same `NS_TYPED_EXTENSIBLE_ENUM` mechanism; the docs pass raises its severity from "two members" to "all of App Events" |
| C2, B1 | `not-planned.md` § *Pending owner decisions* → **Q1 — compile-time marking of wrapper-dependent members** | Q1's trigger ("post-0.18 usage feedback shows runtime wrapper failures are a recurring support burden") is adjacent; B1/C2 are that feedback for the wrapper-*absent* case |
| D2 | `not-planned.md` § *ObjC & mixed bindings* → *ObjC enum-case collision disambiguation diverges from the `ToPascalCase` reference-site naming* | Same emitter, different mechanism — do not conflate |
| D3 | `not-planned.md` § *ObjC & mixed bindings* → *Projected category accessors omit `ArgumentSemantic`* | Same category-projection path; worth scoping together |
| D5 | `not-planned.md` § *ObjC & mixed bindings* → *Foreign-owned ObjC types are omitted…*, *Classes forward-declared but never defined…*, *Umbrella-header convention short-circuits the modulemap* | Any of the three could explain an empty ApiDefinition |
| Kingfisher `imageView.kf.setImage(with:)` | `not-planned.md` § *Consumer ergonomics* → **U-002** extensions on external / ObjC-owned types | Fully covered by U-002; no new item raised |
| — | `not-planned.md` § *ObjC & mixed bindings* → *Anonymous ObjC enums are dropped with no diagnostic* | Already carries MapLibre `MLNPluginLayer.h` as trigger evidence; the docs pass did **not** hit it (plugin-layer surface, not mainstream map API) — no new item |
| — | `not-planned.md` § *ObjC & mixed bindings* → *Foreign-owned ObjC types…* (the `MT4118` fix) | Confirmed fixed from the consumer side: Facebook's alias-import workaround is vestigial at 0.18.1 (see E4) |

---

## Summary

| Item | Bucket | Priority | Affected libraries |
|---|---|---|---|
| A1 Retire bare numeric collision suffixes; split the async axis | Naming | **P1** | Lottie, Stripe (structural: all) |
| A2 Five coexisting de-collision schemes | Naming | P2 | Nuke, Stripe, BlinkID, BlinkIDUX, Facebook |
| A3 Case-only namespace/type collision | Naming | P2 | BlinkID |
| A4 Generated types shadowing platform types | Naming | P3 | BlinkIDUX |
| A5 Origin-lane naming split in mixed vendors | Naming | P3 | Facebook |
| B1 `SB0001` stubs on canonical entry points | Correctness | **P1** | Mappedin, Facebook, Nuke, Kingfisher, Lottie, BlinkIDUX |
| B2 Managed conformers silently never invoked | Correctness | **P1** | Stripe, Facebook, Kingfisher |
| B3 Closure-carrying initializers strand surfaces | Correctness | **P1** | Stripe, Mappedin |
| B4 Struct write-back no-ops silently (and leaks) | Correctness | **P1** | BlinkID, Lottie, Stripe, BlinkIDUX |
| B5 Concrete types missing protocol conformance | Correctness | P2 | Nuke |
| B6 Only trailing default arguments collapse | Correctness | P2 | BlinkID, BlinkIDUX |
| B7 OptionSets lose bitwise operators | Correctness | P2 | Nuke |
| B8 Generic `Delegate<>` properties skipped | Correctness | P2 | Kingfisher |
| B9 `SB0003` dominant limitation class — triage by sub-shape | Correctness | P2 | Nuke, Kingfisher, BlinkIDUX, Stripe |
| B10 Ambiguous generated overload sets | Correctness | P2 | Kingfisher |
| B11 SwiftUI value types unconstructible | Correctness | P2 | BlinkIDUX |
| B12 UX bridge marshals only a scalar outcome | Correctness | P2 | BlinkIDUX |
| B13 `api-surface.md` lists phantom members | Correctness | P2 | Mappedin (all, structurally) |
| B17 `@_spi` suppression hollows out shipped packages | Correctness | P2 | Stripe |
| B14 Bridge default contradicts vendor default | Correctness | P3 | BlinkIDUX |
| B15 Builder output vs consumer parameter type | Correctness | P3 | Mappedin |
| B16 Reversed argument order vs upstream intuition | Correctness | P3 | Stripe |
| B18 App-delegate overloads dropped (key projections) | Correctness | P3 | Facebook |
| C1 Emitted SwiftUI bridge P/Invokes an unshipped library | Packaging | **P1** | Lottie |
| C2 Wrapper-required opt-out is a manual flag | Packaging | P2 | BlinkID |
| C3 Native artifact shipped by two paths | Packaging | P3 | MapLibre |
| D1 No `[Field]` bindings for `extern NSString * const` | ObjC | **P1** | MapLibre (all ObjC) |
| D2 Inconsistent enum prefix stripping | ObjC | P2 | MapLibre |
| D3 `NSValue` category class methods bind as instance methods | ObjC | P2 | MapLibre |
| D4 `NS_TYPED_EXTENSIBLE_ENUM` constants are silent tombstones | ObjC | P2 | Facebook |
| D5 Empty `ApiDefinition.cs` despite ObjC metadata | ObjC | P3 | Facebook |
| D6 Delegate names derived from last selector keyword | ObjC | P3 | MapLibre |
| E1 Publish the naming & disambiguation contract | Docs | P2 | All |
| E2 Wire `api-surface.md` into consumer docs; widen it | Docs | P2 | All |
| E3 Prove struct write-back at runtime | Docs | P2 | Stripe, BlinkID, Lottie |
| E4 Consumer-repo doc & test hygiene | Docs | P3 | All (consumer repo) |

**Totals** — 36 items: A 5 · B 18 · C 3 · D 6 · E 4. By priority: **P1 7** (A1, B1, B2, B3, B4, C1, D1) · **P2 19** · **P3 10**.

---

## Closeout status (2026-08-03)

Every item above carries an inline `**Status.**` line. This is the same information as one table.
Session numbers refer to `src/docs/sessions/2026-08-docs-pass-upstream/`; the phase summaries that
back each verdict are `.agent/phase-N-summary.md`.

| Item | Outcome | Landed / recorded |
|---|---|---|
| A1 | Fixed | session 03 (`81bebf0c`) |
| A2 | Fixed | session 02 (`90d8089d`) |
| A3 | Fixed | session 02 |
| A4 | Owner decision | `not-planned.md` § Pending owner decisions |
| A5 | Fixed (owner-vetoable rename) | session 17 (`403c0aeb`) |
| B1 | Partly fixed; remainder report-only | session 05 (`504c464a`) |
| B2 | Fixed (option (a)); whether to revisit option (b) is an owner decision | session 04 (`a40f87c4`) |
| B3 | Fixed, bounded; (c) trigger-gated | session 06 (`927f41a4`), session 01 |
| B4 | Verified correct + analyzer | session 07 (`2ba3849b`) |
| B5 | Fixed | session 09 (`485eb7ae`), session 01 (`8b65164c`) |
| B6 | Fixed | session 10 (`7079e81`) |
| B7 | Fixed | session 08 (`34b2932f`) |
| B8 | Triage only → not-planned | session 09 |
| B9 | Report-only | session 01 |
| B10 | Fixed; suppression rule is an owner decision | session 10 |
| B11 | Fixed for `Color`/`Font`; rest → not-planned | session 11 (`cb64a1b8`) |
| B12 | Fixed | session 15 (`e2493471`) |
| B13 | Fixed; 282-entry residual → not-planned | session 16 (`3d628a24`) |
| B14 | Fixed | session 15 |
| B15 | Verified → not-planned; signature change is an owner decision | session 08 |
| B16 | Verified faithful — no generator change | session 18 |
| B17 | Partly done; gating policy is an owner decision | session 14 (`cbd5fc22`) |
| B18 | Fixed | session 08, session 01 |
| C1 | Fixed | session 14 |
| C2 | Report-only | session 05 |
| C3 | Verified already fixed | session 14 (`d3d8b276`) |
| D1 | Fixed | session 12 (`98ebf20c`) |
| D2 | Fixed (owner-vetoable rename) | session 17 |
| D3 | Fixed | session 13 (`b0ddba46`) |
| D4 | Fixed (same defect as D1) | session 12 |
| D5 | Verified correct → not-planned | session 18 |
| D6 | Fixed (owner-vetoable rename) | session 17 |
| E1 | Delivered (wiki) | session 18 |
| E2 | Fixed | session 16 |
| E3 | Fixed | session 07 |
| E4 | Out of scope — consumer repo | — |

**Totals by outcome** — fixed or delivered **24** (A1, A2, A3, A5, B2, B3, B5, B6, B7, B10, B11, B12,
B13, B14, B18, C1, D1, D2, D3, D4, D6, E1, E2, E3) · headline ask not delivered, what did land is
partial **2** (B1, B17) · verified correct, no generator change needed **5** (B4, B15, B16, C3, D5) ·
report-only or triage-only **3** (B8, B9, C2) · owner decision **1** (A4) · out of scope **1** (E4).

The line between those first two rows is what the item *asked for*, not whether anything was left
over. Several items counted as fixed were fixed within a deliberately bounded scope and routed a
named residual — B2 (option (b) rejected, not deferred), B3 (the (c) arm is trigger-gated), B11
(`Color` and `Font` only), B13 (282 manifest entries still record an unbound base symbol). Each
residual is written down in `not-planned.md` with a trigger. B1 and B17 are counted apart because
their headline ask — a prominence ranking that gates, and a policy for hollow modules — was not
delivered at all.

**Where the leftovers live.** Everything not fixed is written down in `src/docs/not-planned.md` — the
trigger-gated latents under their subsystem headings, the "verified correct, do not re-file" findings
under their own heading, and the calls that need the owner under § Pending owner decisions. Nothing
from this pass is queued in `roadmap.md`; an entry in `not-planned.md` reopens only when its stated
trigger fires.

**Owner-vetoable renames.** A5, D2 and D6 change names that are already published on nuget.org. They
are on `main` but should be reviewed before the next package release rather than after.
