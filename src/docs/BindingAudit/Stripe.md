# Stripe iOS SDK — Binding Audit

- **Package**: SwiftBindings.Stripe.* (14 submodule packages under one meta-package)   **Mode**: zip   **TFM(s)**: net10.0-ios
- **Native**: github.com/stripe/stripe-ios **26.0.0** (minIOS 15.0), shipped as `Stripe.xcframework.zip` → 14 products
- **Audited at**: main `8dcc3032`, generated 2026-06-27

## Verdict

The Stripe meta-package is **healthy and the consumer-critical path is genuinely usable from C#**. The two
submodules a .NET dev actually integrates against — **StripePayments** (the API surface: `STPPaymentMethod`,
`STPPaymentIntent`, `StripeAPI`, card params) and **StripePaymentSheet** (the drop-in UI) — are bound with
strong coverage, idiomatic `Task<>`/`CancellationToken` async, correct nullability, and proper `IDisposable`/
`NSObject` shapes. "Usable" here is **binding-level** — the configuration/client-secret constructors and async
entry points (`PaymentSheet.PresentAsync`, `StripeAPI`/`ConfirmPaymentIntentAsync`) are constructible and
callable; the end-to-end payment flow is **not** runtime-proven, because the test project exercises no
asynchronous flow (see §3). The headline numbers *look* alarming (StripeUICore 0/538, StripeCameraCore 0/65, StripeCore
65/452) but are **dominated by intended `@_spi`/`ModuleInternal` pruning**, not coverage gaps: Stripe marks the
overwhelming majority of its SDK internal and the generator correctly drops it. The empty modules are
**internal support libraries that should ship as transitive dependencies only**, not standalone consumer
packages. Real generator gaps are narrow and concentrated in **closure-typed configuration** (deferred-intent
`confirmHandler`, Apple Pay handlers) and a few PassKit existential/placeholder types. Biggest risks: (1) the
deferred/server-side-confirmation PaymentSheet flow is **not constructible** from C# (closure gap), and (2) a
**documented `STPAPIClient.AppInfo` NSString string-corruption bug is masked as a test `Skip`** rather than
fixed. *(Risk (2) is **RESOLVED** — see §3: it was a general `Optional<ObjC-rooted class>` accessor
double-VWT-copy bug, fixed generator-side and pinned by BindingTests; only an external test-mask cleanup in the
consuming repo remains.)*

## 1. Coverage

### Per-submodule coverage table

| Submodule | Types e/t | Members e/t | Dominant skip reason | Verdict |
|---|---|---|---|---|
| **Stripe** (umbrella) | 1/2 | 2/2 | ModuleInternal 1 | Thin cross-module shell — exposes `ISTPApplePayContextDelegate` only. Correct. |
| **StripePayments** | 205/244 | 1329/1654 | ModuleInternal 166 | **Workhorse, strong.** Only ~2 real public gaps. |
| **StripePaymentSheet** | 74/164 | 325/828 | ModuleInternal 100 | Drop-in UI **consumable**; closure-config gaps (see deep-dive). |
| **StripePaymentsUI** | 12/41 | 140/277 | ModuleInternal 38 | UIKit form views; 1 SwiftUI view → template-only bridge. OK. |
| **StripeApplePay** | 18/36 | 75/211 | ModuleInternal 18 | Usable; 4 PassKit-placeholder delegate methods skipped; `*TypeType` stutter. |
| **StripeConnect** | 22/41 | 100/172 | ModuleInternal 21 | Clean; 1 closure init skipped (`fetchClientSecret`). |
| **StripeFinancialConnections** | 26/31 | 75/120 | ModuleInternal 28 | Clean (skips are internal + SynthesizedCodable). |
| **StripeCore** | 21/113 | 65/452 | ModuleInternal 103 | Almost entirely internal infra; public surface (`STPAPIClient`, `StripeAPI`) emitted. |
| **StripeUICore** | 1/96 | **0/538** | ModuleInternal 57 | **100% `@_spi` internal** — ship as transitive dep only. |
| **StripeCameraCore** | 0/14 | **0/65** | ModuleInternal 10 | **100% `@_spi` internal** — ship as transitive dep only. |
| **StripeCardScan** | 8/14 | 14/20 | ModuleInternal 7 | Minimal public surface; covered. |
| **StripeIdentity** | 4/6 | 10/18 | ModuleInternal 5 | Thin; covered. |
| **StripeIssuing** | 7/7 | 22/22 | (PassKit existential 1) | Types fully covered; 1 PassKit-existential ctor skipped. |
| **Stripe3DS2** | ObjC mode | ~30 interfaces | — | **ObjC binding** (ApiDefinition.cs), full STDS* surface. Faithful. |

Numbers verified against each `binding-report.json`. Generated 2026-06-27.

### The ModuleInternal story (the headline — and it is INTENDED)

Across the package the single largest skip bucket is `ModuleInternal` (`@_spi`/internal): StripePayments 166,
StripeCore 103, StripePaymentSheet 100, StripeUICore 57. Stripe ships a huge internal implementation surface
behind `@_spi(STP)` and Swift `internal`, and the generator correctly prunes all of it. **These skip counts are
correctly-excluded, not missing coverage.** Concretely: `StripeUICore.cs` is 108 lines of nothing but
`// Unsupported: type 'X' — ModuleInternal (@_spi type)` for every form-element/validation primitive
(`FormElement`, `TextFieldElement`, `SectionElement`, `Element`, `PhoneNumber`, …); `StripeCameraCore.cs`
likewise (`CameraSession`, `CameraPermissionsManager`, …). Nothing public is being dropped there.

**Member-denominator reconciliation.** The raw `EmittedMembers/TotalMembers` ratio understates true coverage and
must be read with care:
- StripePayments emits **1329** members (`EmittedMembersByKind`: Method 492, Property 837) and the generator
  *adds* **1159 SynthesizedMembers** (factory ctors, `Codable` round-trip helpers, `ISTPAPIResponseDecodable`
  impls) on top — so the "emitted" count is inflated by generator scaffolding, not all hand-mappable surface.
- Of StripePayments' **142** skipped members, `SkippedMembersByKind` = Method 106, Property 31, Type 4,
  Operator 1 — and 166 of the *skip items* are `ModuleInternal`. The `Total` (1654) folds internal members into
  the denominator. **Of the genuinely public surface, only ~2 members are real generator gaps** (below). Read
  StripePayments as effectively complete on its public API.

### (b) Real gaps (non-internal, non-SwiftUI)

| Submodule | API | Reason | Worth a fix? |
|---|---|---|---|
| StripePaymentSheet | `PaymentSheet.IntentConfiguration.confirmHandler` / `confirmationTokenConfirmHandler` | UnsupportedClosure — *"Async closure-typed properties cannot be stored via a sync accessor"* | **Yes, high.** Blocks the entire deferred/server-side-confirmation flow (see §2). |
| StripePaymentSheet | `PaymentSheet.ApplePayConfiguration.init` + `Handlers.paymentRequestHandler`/`authorizationResultHandler` | UnsupportedSignature (`PKPaymentButtonType` placeholder) + UnsupportedClosure | **Yes, med.** Blocks Apple Pay *inside the sheet* from C#. |
| StripePaymentSheet | `ExternalPaymentMethodConfiguration` / `CustomPaymentMethodConfiguration` confirm handlers + inits | UnsupportedClosure | Med-low (advanced/rare). |
| StripePaymentSheet | `CustomerSheet.init`, `CustomerAdapter.init`, `IntentConfiguration.init` | UnsupportedSignature (async-throwing closure param) | Med — same closure root cause. |
| StripePaymentSheet | `PaymentSheet.init` / `CustomerConfiguration.init` / `FlowController.presentPaymentOptions` | DuplicateSignature (3) | Low — collide on projected C# signature; one overload dropped. Tied to cross-cutting **theme #7** (no Swift-argument-label disambiguation). |
| StripeApplePay | `STPApplePayContext.paymentAuthorizationController` ×4 | UnsupportedSignature (PassKit placeholder type) | Med — PKPaymentAuthorization delegate-forwarding methods. |
| StripeIssuing | `STPFakeAddPaymentPassViewController.init` + `STPPushProvisioningContext.addPaymentPassViewController` | UnsupportedExistential / UnsupportedSignature (`any PKAddPaymentPassViewControllerDelegate`) | Low (push-provisioning niche). Cross-cutting **theme #1** (existential erasure). |
| StripeConnect | `EmbeddedComponentManager.init` (`fetchClientSecret` closure) | UnsupportedClosure | Med — blocks the Connect embedded-components entry from C#. |
| StripePayments | `STPAPIResponseDecodable.decodedObject` (returns AnyType) | AnyTypeFallback | Low — internal decode helper, not a consumer call. |
| StripePayments | `STPCardBrand.<` operator | UnsupportedType (operator on simple enum) | Trivial/none — enum ordering, not needed. |
| StripeCore | `StripeAPI.additionalEnabledApplePayNetworks` (`[Any]`) | AnyTypeFallback | Low. Cross-cutting **theme #1**. |

**Prioritized generator unlocks (value × tractability):**
1. **Async closure-typed config properties / async-throwing closure init params** (StripePaymentSheet
   `confirmHandler`, `CustomerSheet`, Connect `fetchClientSecret`). This single closure-marshalling limitation
   accounts for the *only* consumer-facing functional gaps in the whole package — the deferred-intent flow,
   Apple-Pay-in-sheet, and Connect embedded components. **Highest value.** Effort: medium-high (Swift cannot
   synthesize `(Args…) async throws -> T` from a C# `(funcPtr, context)` pair via a sync accessor — needs a
   wrapper-shape that stores the C# delegate and re-enters async).
2. **DuplicateSignature → Swift-argument-label disambiguation** (theme #7). 3 collisions here silently drop one
   overload of `PaymentSheet.init` / `FlowController.presentPaymentOptions`. Broad cross-library win.
3. **PassKit existential/placeholder types** (`PKPaymentButtonType`, `any PKAddPaymentPassViewControllerDelegate`,
   `PKPaymentAuthorizationResult`). Medium; ties to theme #1.

## 2. C# Quality

**StripePayments — strong, idiomatic.** The full `STP*` model surface emits as `Foundation.NSObject`-derived
classes with `IEquatable<T>`, `ISTPAPIResponseDecodable`/`ISTPFormEncodable` interfaces, and `long`-backed C#
enums (`STPPaymentIntentStatus`, `STPPaymentMethodCardWalletType`, `StripePayments.cs:5771,17193`). The API
entry points are present and usable:
- `STPPaymentMethod` (`StripePayments.cs:32169`), `STPPaymentHandler` (`:62643`), all `STPPaymentMethod*`/
  `*Params` types, `STPPaymentMethodCardParams` (`:16292`).
- `STPAPIClient` lives in StripeCore and Stripe's Swift extensions surface as C# **extension methods** —
  `StripeAPIStripePaymentsExtensions` (`:67386`): `CreatePaymentMethodAsync(this STPAPIClient, …)` (`:67962`),
  `CreateTokenAsync` (`:67546`/`:67652`), `CreateSourceAsync` (`:67723`), `CreateRadarSessionAsync` (`:68211`).
- **Async is done right.** `async`/throwing Swift methods surface as `Task<>` with a trailing
  `CancellationToken`: `STPPaymentHandler.ConfirmPaymentIntentAsync` (`:63118`) →
  `Task<(STPPaymentHandlerActionStatus, STPPaymentIntent?, AnyError?)>`, `ConfirmSetupIntentAsync` (`:63590`),
  `HandleNextActionAsync` (`:63284`), `CollectBankAccountForPaymentAsync` (`:61445`). Nullability is faithful
  (`STPPaymentIntent?`, `string? returnURL`, `Action<…>? onEvent = null`).
  - *Minor:* error types are inconsistent across siblings — `ConfirmPaymentIntentAsync` returns
    `Swift.Foundation.AnyError?` but `HandleNextActionAsync` returns `Foundation.NSError?`. Cosmetic; mirrors the
    Swift signatures.

**StripePaymentSheet — the drop-in UI IS consumable from C# (it is UIKit-imperative, not SwiftUI-only).** The
core flow round-trips cleanly:
- `new PaymentSheet(string paymentIntentClientSecret, PaymentSheet.ConfigurationType configuration)`
  (`StripePaymentSheet.cs:30816`); `ConfigurationType` has usable ctors (`:11733` parameterless + property
  setters).
- `PaymentSheet.PresentAsync(UIViewController, CancellationToken)` → `Task<PaymentSheetResult>` (`:30906`,
  a synthesized async wrapper over the completion-handler `Present` at `:30869`). So a C# dev does
  `var result = await sheet.PresentAsync(vc);` — the headline use case works end to end at the binding level.
- `FlowController.PresentPaymentOptionsAsync` (`:28708`), `CustomerSheet.PresentAsync` (`:7396`), and the
  5 SwiftUI presentation views are bridged 100% (`BridgeSummary` 5/5 Generated) with
  `Create(...)`/`PresentAsSheet(fromViewController)`/`ViewController` accessors in
  `StripePaymentSheet.SwiftUIBridge.cs`.

  **Two real consumption warts:**
  - **Deferred-intent flow is not constructible.** The ctor `PaymentSheet(IntentConfiguration, ConfigurationType)`
    (`:30836`) exists, but `IntentConfiguration` (`:29114`) has **no public constructor** — both `confirmHandler`
    and `confirmationTokenConfirmHandler` are skipped (UnsupportedClosure), so you can *receive* an
    `IntentConfiguration` but cannot *build* one. **The server-side-confirmation / deferred-payment flow is
    effectively dead from C#.** The simpler client-secret flow is the usable path. (Documented gap; workaround =
    Swift shim.)
  - **`PresentAsync2` naming noise.** Because the generator synthesizes `PresentAsync` from the completion
    handler *and* binds the native Swift `present() async` method, the native one collides and gets renamed
    `PresentAsync2` (`:31051`). The clean `PresentAsync` exists, so this is cosmetic, but the `2`-suffixed
    duplicate is machine-noise on the most important type.

**`*TypeType` stutter (cross-cutting naming theme) — confined to StripeApplePay.** Grep-confirmed: `TypeType`
appears **only** in `StripeApplePay.cs`, in its re-emission of the shared `StripeAPI.PaymentMethod.Card.Wallet`
value tree. Root cause is double mechanical disambiguation: Swift struct `Wallet` is renamed `WalletType` to
avoid colliding with the `wallet`→`Wallet` property, then renamed *again* to `WalletTypeType` to avoid the
C# "member name == enclosing type name" collision with its own nested enum `WalletType`
(`StripeApplePay.cs:3846`, used at `:3132`,`:3174`). Also note `Card`→`CardType` (single-suffix) for the same
property-vs-type reason. It's ugly but the type is still navigable/usable; **cosmetic, low priority.** The same
StripeAPI value tree is emitted *more cleanly* under StripePayments (no `TypeType`), so this is an
inconsistency from per-submodule re-emission of shared value types, not a functional defect.

**Stripe3DS2 (ObjC mode).** Faithful Objective-Sharpie-style projection: ~30 `partial interface STDS*` types
in `ApiDefinition.cs` (1238 lines) covering the full 3-D Secure 2 surface (`STDSThreeDS2Service`,
`STDSTransaction`, `STDSChallengeParameters`, `STDSUICustomization` + the customization tree, `STDSException`
hierarchy) plus `StructsAndEnums.cs`. Treated lightly per the ObjC-binding methodology; no material findings.

## 3. Test Coverage

One test project (`tests/Program.cs`, 6418 lines) guards the whole meta-package: **222 distinct test cases**
(`results.Pass/Fail("name", …)`), broad across submodules (AddrConfig, ApplePay, CardBrandAcceptance, CardScan,
Config, Connect, CrossModule, CustomerSheet, EmbeddedConfig, …).

**Depth: moderate — real value round-trips, but zero end-to-end.**
- *Stronger than metadata-only:* tests set and read back real string/enum/value data through the ABI —
  `STPAPIClient.PublishableKey` round-trip (`Program.cs:517`), `StripeAPI.DefaultPublishableKey` set/get/null
  (`:737`,`:750`), config-property round-trips, enum raw-value checks, `IEquatable` checks. This proves struct
  marshalling and property accessors work.
- *But no functional flow is exercised.* **There is not a single `await` in the entire test file.** None of the
  11 async surfaces (`CreatePaymentMethodAsync`, `ConfirmPaymentIntentAsync`, `PresentAsync`, …) is ever
  invoked. `CrossModule_FullPaymentFlow` (`:4826`) despite its name only constructs+wires the
  APIClient/PaymentHandler/params chain — it never confirms an intent or presents a sheet. Most of these
  legitimately need a publishable key + network + a live `UIViewController` (reasonable to guard), but they are
  **silently untested, not even `Skip`-documented**, and the generated `Task` machinery is therefore unproven.
- **Flagged real bug masked as a Skip:** `STPAPIClient.AppInfo` round-trip detects **NSString string
  corruption** and downgrades to `results.Skip(...)` (`Program.cs:707`) — the in-source comment hypothesizes
  *"swift_retain on NSString tagged pointers corrupts inline data"* on the getter path through `NewSome`. Per
  project policy (ALL runtime crashes/corruption are our bug until proven otherwise; no weakening assertions to
  go green), this is a **real StripeCore marshalling defect** that should be reproduced in BindingTests and
  fixed, not skipped. Only 2 `Skip`s exist and this is one of them.

  > **RESOLVED — classified as a real, already-fixed *general* marshalling bug (not package-specific, not a
  > bad test).** Root cause: the emitter returned `Optional<Class/ObjC-rooted class>` property getters as
  > `SwiftOptional<T>`, routing them through `MarshalFromSwift` + `NewSome` — **two VWT `InitializeWithCopy`
  > calls** that mangled the returned object's inline small-string (SSO) ivar (+2 at byte offset 4, cumulative
  > per access). The audit's *`swift_retain`-on-tagged-pointer* mechanism is **refuted**: the corruption
  > *location* (the inline small-string ivar) matched, but the cause was the double VWT copy in the accessor
  > return, not a retain on a tagged pointer. **The corruption was accessor-only**, fixed by `19560c96`
  > (accessor → direct `IntPtr` + `GetINativeObject<T>(ptr, owns:true)` / `MarshalFromSwift<T>`, zero VWT ops;
  > also closes a retain leak). The sibling method-**return** path (`OptionalProjection`) always bypassed
  > `SwiftOptional` (the IntPtr result IS the payload), so it never had the corruption; `793e77a4` fixed a
  > *separate* retain **leak** there (`ownsReference:false → true`), not string corruption.
  > Durable gate: BindingTests `OptionalObjCClassPropertyTests` over the faithful shape
  > (`ClientCarrier.info: InfoCarrier?` / `InfoCarrier.name: String`). The property-accessor path is green on
  > Mono JIT (sim) + NativeAOT (device) (original gate `7ae2ed3c`); the method-return copy-out
  > (`snapshotInfo`/`makeInfoCarrier`) was added here and is green on Mono JIT (sim), exercising the identical
  > `IntPtr` + `GetINativeObject(owns:true)` copy-out as the accessor (no new NativeAOT ABI surface), so string
  > integrity is now gated on both emitter copy-out paths (the accessor that had the bug and the return path
  > that never did). **Remaining action is external** (out of this
  > repo): the mask still lives in `swift-dotnet-packages/libraries/Stripe/tests/Program.cs:684–718` — flip the
  > `results.Skip("StripeCore_STPAPIClient_AppInfo", "String corruption…")` branch to `results.Pass` asserting
  > `readBack.Name == "TestApp"` (all four fields ideally); the generator fix already makes it pass, so the
  > Skip is now a stale mask of a fixed defect and a standing no-expected-failures violation.

**Untested high-value surface:** the entire async payment path (`CreatePaymentMethodAsync`,
`ConfirmPaymentIntentAsync`, `HandleNextActionAsync`), `PaymentSheet.PresentAsync`, and `STPPaymentMethod`
decode from a JSON response. **Recommended additions (headless where possible):** (1) **DONE** — the
`STPAPIClient.AppInfo` NSString corruption is reproduced and gated by BindingTests `OptionalObjCClassPropertyTests`
(see the §3 RESOLVED callout); the only leftover is the external `Program.cs:707` Skip→assert flip; (2)
drive `PaymentSheet.PresentAsync(vc)` against a fake client secret to at least exercise the Task/present path
(assert it returns a `PaymentSheetResult` failure, not a crash); (3) decode a captured PaymentIntent JSON
through `STPPaymentIntent`/`STPPaymentMethod` to prove the `ISTPAPIResponseDecodable` synthesized helpers.

## Action Items

| # | Dimension | Finding | Recommendation | Effort | Value |
|---|---|---|---|---|---|
| 1 | 3 (bug) | **RESOLVED.** `STPAPIClient.AppInfo` NSString round-trip corruption masked as a test `Skip` (`Program.cs:707`); the tagged-pointer hypothesis was refuted — real cause was a double VWT `InitializeWithCopy` in the `SwiftOptional<T>` accessor return | Fixed generator-side by `19560c96` (accessor; the corruption was accessor-only — `793e77a4` was a separate return-path leak fix). Gated by BindingTests `OptionalObjCClassPropertyTests` on both copy-out paths. External follow-up: flip the `Program.cs:707` Skip to a `Name == "TestApp"` assertion | Med | **High** |
| 2 | 1 | Deferred-intent flow unusable: `PaymentSheet.IntentConfiguration` has no C# ctor — `confirmHandler`/`confirmationTokenConfirmHandler` async closures skipped (`StripePaymentSheet.cs:29114`) | Generator unlock for async closure-typed config properties (store C# delegate, re-enter async); or document a Swift-shim workaround | High | **High** |
| 3 | 1 | Apple-Pay-in-sheet unconstructible: `PaymentSheet.ApplePayConfiguration.init` (PassKit placeholder) + handlers skipped | Resolve `PKPaymentButtonType`/closure handlers, or ship a Swift shim | Med | Med |
| 4 | 1 | DuplicateSignature drops one overload of `PaymentSheet.init`/`FlowController.presentPaymentOptions` (×3) | Cross-cutting theme #7: disambiguate colliding overloads via Swift argument labels instead of dropping | Med | Med (broad) |
| 5 | 3 | Zero `await` in tests; no async/payment/present flow exercised | Add the 3 headless functional tests above | Low | Med |
| 6 | 2 | `StripeApplePay` `*TypeType` stutter on re-emitted `StripeAPI.Wallet` tree (`StripeApplePay.cs:3846`); `PresentAsync2` duplicate (`StripePaymentSheet.cs:31051`) | Cosmetic naming polish; de-duplicate the nested-type and synthesized-vs-native async collisions | Low | Low |
| 7 | 1 (doc) | StripeUICore (0/538) + StripeCameraCore (0/65) emit nothing — 100% `@_spi` internal | **Recommendation: ship as transitive dependency packages only, not standalone consumer packages.** Not a coverage hole — confirmed internal-only (`StripeUICore.cs` is all `ModuleInternal` comments). | — | (owner decision) |
