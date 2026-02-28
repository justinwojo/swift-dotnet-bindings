# Workflow Assessment — Target Libraries (February 2026)

## Purpose

This document replaces the 10-category scoring rubric from binding-review-v1 through v4. Instead of subjective 1-5 scores across aesthetic categories, it tracks **binary workflow completion**: can a C# developer accomplish the thing they would actually use this library for?

## Target Libraries

These are the libraries that would actually be consumed in a .NET for iOS app:

| Library | Purpose |
|---|---|
| BlinkID | Document scanning (ID cards, passports, driver's licenses) |
| BlinkIDUX | Scanning UI overlay/experience for BlinkID |
| BRLMPrinterKit | Brother printer discovery and printing |
| Lottie | After Effects animation playback |
| Mappedin | Indoor mapping and wayfinding |
| MicroblinkPlatform | Base platform for Microblink SDKs (KYC/IDV) |
| Nuke | Image loading and caching |
| SmartCardIO | Smart card reader communication (NFC/contact) |
| Stripe | Payment processing (reference — complex multi-module) |

## Workflow Results

### Lottie — USABLE

| Workflow | Status | Notes |
|---|---|---|
| `new LottieAnimationView("name")` | **Works** | Mono-safe wrapper constructor |
| `LottieAnimation.Named("name")` | **Works** | Static factory |
| `animView.Play()` | **Works** | No-arg and completion callback variants |
| `animView.Play(from:to:loopMode:)` | **Works** | All overloads present |
| `animView.Stop()` / `Pause()` | **Works** | |
| `animView.CurrentProgress = 0.5` | **Works** | Get+set |
| `animView.AnimationSpeed = 2.0` | **Works** | |
| `animView.LoopMode = .Loop` | **Blocked** | Non-simple enum lacks Buffer marshalling |
| `animView.ContentMode = .ScaleAspectFit` | **Works** | ObjC enum resolved via `AppleFrameworkSimpleEnumRemappings` |
| `animView.BackgroundBehavior = .Pause` | **Works** | |
| `SetValueProvider(colorProvider, keypath)` | **Works** | Dynamic property animation |
| `ColorValueProvider` with block | **Works** | |

**Workaround for LoopMode**: Pass `loopMode:` parameter into every `Play()` call instead of setting the property. Functional but verbose. Root cause: non-simple enum property getter/setter requires Buffer marshalling (not UIKit issue).

**Verdict**: A developer can load, play, pause, scrub, set content mode, and dynamically color Lottie animations. The LoopMode property gap is annoying but not blocking. This library is usable.

---

### Nuke — USABLE (async path only)

| Workflow | Status | Notes |
|---|---|---|
| `ImagePipeline.Shared` | **Works** | Singleton access |
| `new ImageRequest("https://...")` | **Works** | String literal constructor |
| `new ImageRequest(url)` (Foundation.URL) | **Blocked** | URL struct can't satisfy ISwiftObject |
| `await pipeline.ImageAsync(request)` | **Works** | Returns `Task<UIImage>` with CancellationToken |
| `await pipeline.ImageAsync(nsUrl)` | **Works** | NSUrl overload |
| `await pipeline.DataAsync(request)` | **Works** | Returns `(byte[], NSUrlResponse?)` |
| `pipeline.LoadImage(request, completion)` | **Blocked** | `Result<T,E>` closure not bridgeable |
| `NukeExtensions.loadImage(into: imageView)` | **Blocked** | NukeExtensions not emitted |
| `ImagePrefetcher._startPrefetching([requests])` | **Works** | Underscore name, only ImageRequest[] overload |
| `prefetcher.StartPrefetching([urls])` | **Blocked** | `[Foundation.URL]` can't satisfy ISwiftObject |
| `cache.ContainsCachedImage(request)` | **Works** | |
| `cache.RemoveCachedImage(request)` | **Works** | |
| `cache.RemoveAll()` | **Works** | |

**Viable C# pattern**:
```csharp
using var pipeline = ImagePipeline.Shared;
using var request = new ImageRequest("https://example.com/image.jpg");
UIImage image = await pipeline.ImageAsync(request);
myImageView.Image = image;
```

**Verdict**: The async path is fully functional and idiomatic. The callback-based `loadImage(with:completion:)` and the UIImageView convenience extension are blocked. For new code using async/await, this is usable.

---

### BlinkID — USABLE

| Workflow | Status | Notes |
|---|---|---|
| `new BlinkIDSdkSettings(licenseKey, ...)` | **Works** | 5 constructor overloads, `bundleURL: NSUrl?` correct |
| `BlinkIDSdk.CreateBlinkIDSdkAsync(settings)` | **Works** | Async with CancellationToken |
| `new BlinkIDSessionSettings(...)` | **Works** | Multiple overloads, all correct |
| `sdk.CreateScanningSessionAsync(settings)` | **Works** | Async with CancellationToken |
| `new InputImage(uiImage, roi)` | **Works** | UIImage path |
| `new InputImage(cameraFrame)` | **Blocked** | CameraFrame has no public constructor |
| `session.Process(inputImage)` | **Works** | Returns FrameProcessResult |
| `result.FirstName?.Value` | **Works** | StringResult → string |
| `result.LastName?.Value` | **Works** | |
| `result.DocumentNumber?.Value` | **Works** | |
| `result.DateOfBirth?.Day/Month/Year` | **Works** | nint? (cast to int?) |
| `result.SubResults[0].Mrz?.RawMRZString` | **Works** | string, not SwiftString |
| SB0001 warnings | 3 total | Minor: alphabet-specific string, barcode element, session ID |
| SB0002 warnings | 0 | |

**Fixed (Feb 2026)**: `BlinkIDSdkSettings` constructors were blocked by `HasNonSwiftObjectGenericArg` gate rejecting `Optional<Foundation.URL>` (`bundleURL: URL? = nil`). Fixed by adding `!outerIsOptional` guard to the ObjC-bridged/native-remapped check in `BoundGenericsHandler.cs`. 5 constructor overloads now emitted.

**Viable C# pattern**:
```csharp
using var settings = new BlinkIDSdkSettings("license-key", null, true, true, "https://...");
using var sdk = await BlinkIDSdk.CreateBlinkIDSdkAsync(settings);
using var sessionSettings = new BlinkIDSessionSettings(...);
using var session = await sdk.CreateScanningSessionAsync(sessionSettings);
var result = session.Process(inputImage);
string firstName = result.FirstName?.Value;
```

**Verdict**: The full SDK initialization → scanning → result extraction flow now works. The only gap is `CameraFrame` construction (use `InputImage(UIImage)` instead).

---

### BlinkIDUX — BLOCKED AT ENTRY POINT

| Workflow | Status | Notes |
|---|---|---|
| Create `BlinkIDUXModel` | **Blocked** | No public constructor |
| Create `ScanningViewModel<T,U>` | **Blocked** | No public constructor |
| `new ScanningUXSettings(...)` | **Works** | 5 constructor overloads |
| `new Camera()` / `StartAsync()` / `StopAsync()` | **Works** | Full camera lifecycle |
| Camera torch control | **Works** | |
| `BlinkIDEventStream` / async event iteration | **Works** | UIEvent enum fully typed |
| `BlinkIDTheme.Shared` access | Exists | But IUXThemeProtocol is empty (SB0004) |
| Theme color/font customization | **Blocked** | All 21 protocol members dropped |
| Alert type inspection | **Works** | Title/Description strings |
| `ScanningResult<T,U>` discrimination | **Works** | Full TryGet pattern |
| `IBlinkIDClassFilter` implementation | **Blocked** | Takes `AnyType` (cross-module DocumentClassInfo) |

**Root cause**: BlinkIDUX is a SwiftUI-first SDK. The main orchestrator (`BlinkIDUXModel`) and view model (`ScanningViewModel<T,U>`) have no public constructors — they're created by factory methods in the companion BlinkID module. Cross-module factory resolution is not supported.

**What's good**: The infrastructure pieces (settings, camera, events, result discrimination) are well-projected. If the entry point were available, the downstream flow works.

**Verdict**: Blocked by cross-module factory pattern and empty theme protocol.

---

### MicroblinkPlatform — USABLE (NativeAOT only)

| Workflow | Status | Notes |
|---|---|---|
| `new MicroblinkPlatformConsent(...)` | **Works** | All parameters correct |
| `new MicroblinkPlatformServiceSettings(...)` | **Works** | |
| Implement `IMicroblinkPlatformSDKDelegate` in C# | **Works** | 3 callback methods |
| `new MicroblinkPlatformSDK(settings, delegate)` | **Works** | SB0001 — NativeAOT only |
| `sdk.StartSDK()` → present ViewController | **Works** | |
| `MicroblinkPlatformTheme.Shared` customization | **Works** | Colors, fonts, images |
| `MicroblinkPlatformResult` status inspection | **Works** | Accept/Review/Reject enum |
| `MicroblinkPlatformCancelState` inspection | **Works** | UserCanceled/ConsentDenied |

**Verdict**: Complete end-to-end KYC/IDV launch flow. Build consent → configure settings → implement delegate → create SDK → present. The only caveat is SB0001 on the constructor (Mono JIT crash on Simulator — use NativeAOT device builds).

---

### SmartCardIO — BLOCKED (protocol dispatch)

| Workflow | Status | Notes |
|---|---|---|
| `TerminalFactory.Shared(...)` | SB0001 | Mono JIT crash risk |
| `factory.GetTerminals()` | **Works** | Returns ICardTerminals proxy |
| `terminals.GetList()` | **Blocked** | SB0003 — NotSupportedException |
| `terminals.List(CardState)` | **Blocked** | SB0003 |
| `terminal.IsCardPresent()` | **Blocked** | SB0003 |
| `terminal.Connect("*")` | **Blocked** | SB0003 — returns `any Card` |
| `card.GetBasicChannel()` | **Blocked** | SB0003 — returns `any CardChannel` |
| `channel.Transmit(apdu)` | **Blocked** | SB0003 |
| `new CommandAPDU(0x00, 0xA4, 0x04, 0x00)` | **Works** | All 8 overloads |
| `CommandAPDU` properties (Cla, Ins, P1, P2, Data) | **Works** | byte, nint, IReadOnlyList<byte> |
| `new ResponseAPDU(bytes)` | **Works** | |
| `ResponseAPDU` properties (Sw1, Sw2, Sw, Data) | **Works** | byte, ushort, IReadOnlyList<byte> |
| `new ATR(bytes)` | **Works** | |
| `ATR.Bytes`, `ATR.HistoricalBytes` | **Works** | IReadOnlyList<byte> |
| `CardState` enum (All, Present, Absent, etc.) | **Works** | |
| `CardError` enum with TryGet | **Works** | |

**Root cause**: Every protocol in this library (`CardTerminal`, `CardTerminals`, `Card`, `CardChannel`) returns other protocols. `Connect()` returns `any Card`, `GetBasicChannel()` returns `any CardChannel`. The proxy can't dispatch methods with existential returns — they all throw `NotSupportedException`.

**What's good**: The APDU data model is perfect. `CommandAPDU`, `ResponseAPDU`, and `ATR` are concrete classes that construct, serialize, and inspect correctly. `CardState` and `CardError` enums work. If protocol dispatch were fixed, this library would be immediately usable.

**Verdict**: Completely blocked. The data types are ready but you can't reach a card to use them.

---

### BRLMPrinterKit — EMPTY

| Workflow | Status | Notes |
|---|---|---|
| Everything | **Empty** | 44 lines, no types emitted |

**Root cause**: Almost certainly built without `BUILD_LIBRARY_FOR_DISTRIBUTION=YES`. No ABI JSON or swiftinterface for the generator to process. This is an xcframework packaging issue, not a generator issue.

**Verdict**: Not usable. Need to rebuild the xcframework with library evolution enabled, or the SDK vendor needs to provide one.

---

### Stripe — USABLE

| Workflow | Status | Notes |
|---|---|---|
| `STPAPIClient.Shared` | **Works** | Static singleton (StripeCore module) |
| `new STPAPIClient(publishableKey)` | **Works** | String constructor |
| `client.PublishableKey = "pk_test_..."` | **Works** | Get+set, `string?` |
| `new PaymentSheet.Configuration()` | **Works** | Parameterless constructor |
| `config.MerchantDisplayName = "Shop"` | **Works** | Get+set |
| `config.ReturnURL = "myapp://stripe"` | **Works** | Get+set, `string?` |
| `config.AllowsDelayedPaymentMethods = true` | **Works** | Get+set |
| `config.ApplePay = applePayConfig` | **Works** | Get+set, `ApplePayConfiguration?` |
| `config.Appearance` | **Works** | Full customization struct |
| `config.PrimaryButtonColor` | **Works** | `UIColor?` |
| `config.PrimaryButtonLabel` | **Works** | `string?` |
| `config.DefaultBillingDetails` | **Works** | Nested struct |
| `new PaymentSheet(clientSecret, config)` | **Works** | Primary constructor |
| `new PaymentSheet(intentConfig, config)` | **Works** | IntentConfiguration overload |
| `await paymentSheet.PresentAsync(vc)` | **Works** | Returns `Task<PaymentSheetResult>` with CancellationToken |
| `result.Tag == .Completed` | **Works** | CaseTag enum discrimination |
| `result.TryGetFailed(out error)` | **Works** | AnyError extraction |
| `PaymentSheetResult.Completed` | **Works** | Static singleton |
| `PaymentSheetResult.Canceled` | **Works** | Static singleton |
| `new STPCardParams()` | **Works** | StripePayments module |
| `STPPaymentMethodParams` | **Works** | 100+ payment method type variants |
| `CustomerSheet.PresentAsync(vc)` | **Works** | Returns `Task<CustomerSheetResult>` |

**Viable C# pattern**:
```csharp
// Initialize (StripeCore module)
using var client = STPAPIClient.Shared;
client.PublishableKey = "pk_test_...";

// Configure (StripePaymentSheet module)
using var config = new PaymentSheet.Configuration();
config.MerchantDisplayName = "My Shop";
config.ReturnURL = "myapp://stripe-redirect";
config.AllowsDelayedPaymentMethods = true;

// Create & present
using var paymentSheet = new PaymentSheet(paymentIntentClientSecret, config);
var result = await paymentSheet.PresentAsync(viewController);

// Handle result
switch (result.Tag)
{
    case PaymentSheetResult.CaseTag.Completed:
        // Payment succeeded
        break;
    case PaymentSheetResult.CaseTag.Canceled:
        // User canceled
        break;
    case PaymentSheetResult.CaseTag.Failed:
        result.TryGetFailed(out var error);
        // Handle error
        break;
}
```

**Module coverage** (3 core modules assessed):

| Module | Types | Members | Coverage |
|---|---|---|---|
| StripeCore | 106/106 | 356/424 (84%) | `STPAPIClient`, networking, telemetry |
| StripePaymentSheet | 138/145 (95%) | 553/654 (85%) | PaymentSheet, Configuration, Appearance |
| StripePayments | 245/245 | 1341/1670 (80%) | STPCardParams, 100+ payment method types |

**Cross-module note**: `STPAPIClient` lives in StripeCore. PaymentSheet is in StripePaymentSheet. In production, both NuGet packages would be referenced. The `apiClient` property on `AddressViewController.Configuration` is skipped (cross-module type resolution) but this doesn't block the primary flow — PaymentSheet uses the shared singleton automatically.

**Verdict**: The complete payment flow works: initialize API client → configure payment sheet → present → handle result. Async/await with CancellationToken support. All configuration properties accessible. This is the most important commercial SDK in the set and it's fully usable.

---

### Mappedin — USABLE

| Workflow | Status | Notes |
|---|---|---|
| `new MPIMapView(frame)` | **Works** | CGRect constructor |
| `mapView.LoadVenue(options)` | **Works** | Optional closure param omitted, Swift fills `nil` |
| `mapView.ShowVenue(venueResponse)` | **Works** | String overload, closure omitted |
| `mapView.ShowVenue(venueResponse)` | **Works** | MPIVenueResponse overload, closure omitted |
| `MPIOptions.Init` struct | **Works** | clientId, clientSecret, venue, etc. |
| `MPIOptions.ShowVenue` struct | **Works** | 12 configuration parameters, 6 constructor overloads |
| `mapView.VenueData` | **Works** | Get+set, `MPIData?` |
| `mapView.Delegate` | **Works** | Get+set, `IMPIMapViewDelegate?` |
| `IMPIMapViewDelegate` proxy | **Works** | OnDataLoaded, OnFirstMapLoaded, OnMapChanged, OnPolygonClicked, etc. |
| `mapView.SetMap(map)` | **Works** | With and without callback |
| `mapView.SetMap(mapId)` | **Works** | String overload |
| `mapView.GetDirections(to, from, accessible, callback)` | **Works** | `Action<MPIDirections?>` — clean types |
| `mapView.GetDirections(destinations, from, accessible, callback)` | **Works** | Multi-destination with `MPIDestinationSet` |
| `mapView.GetDistance(to, from, accessible, callback)` | **Works** | `Action<float?>` |
| `mapView.GetDistanceAsync(to, from, accessible)` | **Works** | Auto-generated `Task<float?>` |
| `mapView.SetPolygonColor(polygon, color, ...)` | **Works** | With textColor and opacity params |
| `mapView.AddInteractivePolygon(polygon)` | **Works** | |
| `MPISearchManager.AddQuery(query, object, weight, callback)` | **Works** | MPICategory, MPILocation, AnyObject overloads |
| `MPISearchManager.Suggest(query, callback)` | **Works** | |
| `MPIBlueDotManager.Enable(options)` | **Works** | |
| `MPIBlueDotManager.UpdatePosition(position)` | **Works** | |
| `MPIData.Locations` | **Works** | `IReadOnlyList<MPILocation>` |
| `MPIData.Maps` | **Works** | `IReadOnlyList<MPIMap>` |
| `MPIData.Polygons` | **Works** | `IReadOnlyList<MPIPolygon>` |
| `MPILocation` properties | **Works** | id, name, type, description, sortOrder, etc. |
| `MPINode` properties | **Works** | coordinates (x, y), map reference |
| `MPIPathManager.Remove(paths)` | **Works** | Optional closure param omitted, Swift fills `nil` |

**Fixed (Feb 2026)**: `loadVenue`, `showVenue`, and `PathManager.remove` were blocked because they take `Optional<Closure>` parameters with default values (e.g., `errorCallback: ((MPIError?) -> Void)? = nil`). Fixed by extending `ExistentialBypassEmitter` to classify `Optional<Closure>` params with `HasDefaultArg=true` as omittable — the Swift wrapper omits these params and Swift fills in `nil`. `MemberEmissionValidator.ShouldSkipMethodEmission` B20 carve-out allows these methods through; `CanEmitMethod` stays conservative (protocol conformance unaffected). Reduced-signature dedup prevents CS0111 when stripping params creates duplicates.

**Binding statistics**: 120/120 types emitted (100%), 554/670 members (85%). 116 skips are synthesized Codable (expected).

**Viable C# pattern**:
```csharp
using var mapView = new MPIMapView(frame);
using var options = new MPIOptions.Init();
// Set clientId, clientSecret, venue, etc.
mapView.LoadVenue(options);

// After venue loads (via delegate OnDataLoaded):
mapView.SetMap(map);
var directions = mapView.GetDirections(to, from, accessible: true, callback);
await mapView.GetDistanceAsync(to, from, accessible: true);
```

**Verdict**: The full indoor mapping workflow is usable: create map view → load venue → navigate/search/wayfind. Directions, search, BlueDot positioning, polygon styling, and delegate callbacks all work. The optional error callbacks are omitted (Swift fills `nil`) — developers who need error handling can add a Swift wrapper that passes the callback explicitly.

---

## Root Cause Analysis

Six root causes account for all blockers across all 9 libraries:

### 1. ~~Missing public constructors on settings/config types~~ FIXED
**Affected**: BlinkID (`BlinkIDSdkSettings`), ~~BlinkIDUX (`BlinkIDUXModel`)~~
**Status**: **Fixed** — `HasNonSwiftObjectGenericArg` gate in `BoundGenericsHandler.cs` was rejecting `Optional<ObjCBridgedType>` and `Optional<NativeRemappedType>` despite `SwiftOptional<T>` having no `ISwiftObject` constraint. One-line fix: added `!outerIsOptional &&` guard, matching existing Void/tuple exemptions. 48 constructors recovered across 14 libraries.
**Note**: BlinkIDUX `BlinkIDUXModel` was NOT this bug — it's a cross-module factory pattern (no public init in Swift either). Removed from this root cause.

### 2. SB0003 — Protocol methods returning protocol existentials
**Affected**: SmartCardIO (all 5 workflows)
**Impact**: Every protocol-to-protocol method throws NotSupportedException
**Pattern**: `func connect(_ protocol: String) -> any Card` — the return type is an existential that can't be dispatched through the witness table
**Likely fix**: This is the hardest structural issue. Would require generating Swift wrapper functions that receive the existential, box it, and return a concrete handle. Multi-session effort.

### 3. Closure params with `Result<T,E>` or complex generic enums
**Affected**: Nuke (`loadImage(with:completion:)`)
**Impact**: Callback-based image loading blocked
**Pattern**: Completion handler type is `(Result<ImageResponse, Error>) -> Void` — `Result` is a Swift enum, not a class, and has two generic params
**Likely fix**: Extend `MethodClosureBridge` to handle `Result<T,E>` specifically, or project it to C#'s `Action<T?, Exception?>` pattern.

### 4. Missing Apple framework types — TWO DISTINCT SUB-PROBLEMS

**~~4a. Lottie `contentMode` — UIView.ContentMode enum missing from type database~~ FIXED**
**Affected**: Lottie, FSPagerView, Kingfisher, Stripe (sub-frameworks), PhoneNumberKit
**Status**: **Fixed** — Added `AppleFrameworkSimpleEnumRemappings` dictionary in `TypeDatabaseExtensions.cs` mapping 11 ObjC enum types (`NS_ENUM`/`NS_OPTIONS`) to their .NET equivalents. Wired into `TryGetTypeRecord`, `GetTypeRecordOrThrow`, `GetTypeRecordOrAnyType`, `IsTypeProcessed`, and core `TypeDatabase.TryGetTypeRecord(SwiftTypeName)`. Types recovered: `UIViewContentMode`, `UIControlState`, `UIControlEvent`, `UIBarStyle`, `UIKeyboardAppearance`, `UITextFieldViewMode`, `UIActivityIndicatorViewStyle`, `UIBlurEffectStyle`, `UITableViewStyle`, `UIModalPresentationStyle`, `UIUserInterfaceStyle`. 60+ property/method occurrences recovered across 5 libraries.

**4b. BlinkIDUX theme — SwiftUI module gate**
**Affected**: BlinkIDUX (`IUXThemeProtocol` — all 21 members dropped, SB0004)
**Impact**: Theme customization completely blocked
**Root cause**: NOT UIKit types. All 21 properties use `SwiftUI.Color` and `SwiftUI.Font`. The `UnsupportedConstraintModules` gate in `GenericTypeEmitter.cs` rejects any type from the `SwiftUI` module at the module level before the type database is consulted. `UIColor` and `UIImage` are already in `UIKitDatabase.xml` — they aren't the problem.
**Effort**: Large — would require adding SwiftUI type support or SwiftUI→UIKit bridging. Not a database fix.

### 5. `Foundation.URL` struct can't satisfy `ISwiftObject` constraint — PARTIALLY FIXED
**Affected**: Nuke (`ImageRequest(url:)`, `startPrefetching([URL])`)
**Impact**: URL-based constructors and array methods blocked
**Pattern**: `Foundation.URL` is a Swift value-type struct. Generic containers (`Optional<URL>`, `Array<URL>`) require elements to implement `ISwiftObject`.
**Status**: The `Optional<URL>` case is **fixed** (same fix as #1 — `SwiftOptional<T>` has no constraint). The `Array<URL>` case remains blocked because `SwiftArray<T> where T : ISwiftObject` genuinely can't hold `NSUrl`. Would need `Foundation.URL` → `NSUrl` bridging or lifting the constraint for bridged types.

### 6. ~~`Optional<Closure>` parameters — methods with nullable callbacks~~ FIXED
**Affected**: Mappedin (`loadVenue`, `showVenue`, `PathManager.remove`)
**Status**: **Fixed** — Extended `ExistentialBypassEmitter` to classify `Optional<Closure>` params with `HasDefaultArg=true` as omittable. Swift wrapper omits these params, Swift fills `nil`. `MemberEmissionValidator.ShouldSkipMethodEmission` B20 carve-out allows methods through; `CanEmitMethod` stays conservative (protocol conformance unaffected). Reduced-signature dedup via `BuildReducedMethodDecl` prevents CS0111 when param stripping creates duplicates. Bridge emitter ordering preserved: GenericClosureBridge → ProtocolExtensionClosureBridge → MethodClosureBridge → OptionalClosureBypass (bypass runs last, never preempts bridge-eligible methods). 14 unit tests added across `ThirdPartyValidationFixTestsV4.cs` and `MethodHandlerOutputTests.cs`.

## Fix Priority (by library impact)

| Priority | Root Cause | Libraries Unblocked | Effort | Status |
|---|---|---|---|---|
| ~~1~~ | ~~`HasNonSwiftObjectGenericArg` too broad (#1 + #5)~~ | ~~BlinkID, 14 libraries (48 constructors)~~ | ~~Small~~ | **DONE** |
| ~~2a~~ | ~~ObjC enums missing from type database (#4a)~~ | ~~Lottie, FSPagerView, Kingfisher, Stripe, PhoneNumberKit~~ | ~~Small~~ | **DONE** |
| ~~2b~~ | ~~`Optional<Closure>` with default value (#6)~~ | ~~Mappedin (fully — `loadVenue` entry point)~~ | ~~Small~~ | **DONE** |
| 3 | SwiftUI module gate (#4b) | BlinkIDUX (theme — 21 members) | Large | |
| 4 | Result<T,E> closure bridge (#3) | Nuke (`loadImage` callback path) | Medium-Large | |
| 5 | Protocol existential returns (#2) | SmartCardIO (fully — all 5 workflows) | Large — structural | |

### What to investigate next

**Priority #3 (SwiftUI module gate)** is a large effort. BlinkIDUX theme uses `SwiftUI.Color`/`SwiftUI.Font` — blocked at the module level by `GenericTypeEmitter.UnsupportedConstraintModules`. `UIColor` and `UIImage` are already in `UIKitDatabase.xml` and aren't the problem. Unblocking this requires either adding SwiftUI types to the type system or bridging SwiftUI→UIKit, neither of which is small.

Note: Lottie's `LoopMode` property is a *separate* issue (non-simple enum property Buffer marshalling), not UIKit types. The `loopMode` parameter in `Play()` methods already works.

**Priority #4 (Result closure bridge)** helps Nuke's callback path only — async path already works.

**Priority #5 (protocol existential returns)** is the highest-impact structural fix — it would take SmartCardIO from completely blocked to fully usable. Requires generating Swift wrapper functions that receive existentials, box them, and return concrete handles — multi-session effort.

## Summary

| Library | Verdict | Blocker |
|---|---|---|
| **Lottie** | USABLE | LoopMode property (workaround: pass in Play()) |
| **Nuke** | USABLE (async) | Callback path blocked (Result closure) |
| **BlinkID** | USABLE | CameraFrame constructor (use UIImage path) |
| **Stripe** | USABLE | Cross-module `apiClient` property (not blocking) |
| **MicroblinkPlatform** | USABLE (NativeAOT) | SB0001 on constructor (Mono JIT) |
| **Mappedin** | USABLE | Optional closure params omitted (#6 — fixed) |
| **BlinkIDUX** | BLOCKED | Cross-module factory + SwiftUI types |
| **SmartCardIO** | BLOCKED | Protocol existential returns (#2) |
| **BRLMPrinterKit** | EMPTY | Missing `BUILD_LIBRARY_FOR_DISTRIBUTION=YES` |

**6 of 9 libraries are usable today.** All three fixed root causes (#1, #4a, #6) were small, targeted changes.

## Deep Dive: Root Causes #1 and #5 Are the Same Bug — FIXED

> **Status**: Fixed in `BoundGenericsHandler.cs` line 504. 7 unit tests added/updated in `BoundGenericsHandlerTests.cs`. 53/53 library validation passing. Verified: BlinkID emits 5 `BlinkIDSdkSettings` constructors, Alamofire emits `DataResponse`/`DownloadResponse` constructors with `NSUrl?`/`URLRequest?` params.

Investigation revealed that "missing constructors" (#1) and "Foundation.URL can't satisfy ISwiftObject" (#5) share a single root cause: the `HasNonSwiftObjectGenericArg` gate in `BoundGenericsHandler.cs` is too broad for `Swift.Optional`.

### The gate

`BoundGenericsHandler.HasNonSwiftObjectGenericArg` (line 464) checks whether a bound generic type contains a generic argument that doesn't implement `ISwiftObject`. This protects against invalid C# like `SwiftArray<NSUrl>` — because `SwiftArray<T>` has `where T : ISwiftObject` and `NSUrl` doesn't satisfy that.

The gate already knows that `SwiftOptional<T>` has **no** `ISwiftObject` constraint — it exempts `Void` and tuples inside optionals at lines 477-482:

```csharp
// Swift.Optional (SwiftOptional<T>) has no ISwiftObject constraint on T,
// so tuples are valid generic args.
bool outerIsOptional = namedTypeSpec.Name == "Swift.Optional";

// These checks skip when outerIsOptional:
if (!outerIsOptional && genericParam is NamedTypeSpec { Name: "Swift.Void" })
    return true;
if (!outerIsOptional && genericParam is TupleTypeSpec)
    return true;
```

But line 501 does NOT apply the same exemption:

```csharp
// BUG: missing !outerIsOptional guard
if (genericParam is NamedTypeSpec namedArg && (IsObjCBridgedType(namedArg) || IsNonSwiftObjectMappedType(namedArg)))
    return true;
```

This blocks `Optional<Foundation.URL>` (where `Foundation.URL` has `NativeTypeName = "Foundation.NSUrl"`) and `Optional<UIImage>` (where `UIImage` has `objcBridged="true"`) — even though `SwiftOptional<T>` can hold any `T`.

### Why the projection infrastructure already handles it

The `TypeProjectionFactory` (line 141-150) correctly handles `Optional<Foundation.URL>`:
1. Detects `Swift.Optional` with 1 generic param
2. Recursively calls `Project()` on `Foundation.URL`
3. Gets `NativeRemappedProjection("Foundation.NSUrl", "Swift.URL", ...)` with `FromNSUrl`/`ToNSUrl` conversion methods
4. Wraps in `OptionalProjection` → C# type `Foundation.NSUrl?`

Similarly, `Optional<UIImage>` produces `OptionalProjection(ObjCBridgedProjection("UIKit.UIImage"))` → C# type `UIKit.UIImage?`.

The `OptionalProjection` class handles both parameter direction (null-check → `SwiftOptional<T>.NewSome(converted)` / `.NewNone()`) and return direction (`MarshalFromSwift<SwiftOptional<T>>().ToNullable()`). It composes correctly with both `NativeRemappedProjection` and `ObjCBridgedProjection`.

**The gate fires before the projection factory is ever consulted**, so this working infrastructure never gets a chance to run.

### The BlinkID case specifically

`BlinkIDSdkSettings` has a Swift init with parameter `bundleURL: Foundation.URL? = nil`. The gate sees `Optional<Foundation.URL>`, `IsNonSwiftObjectMappedType` returns `true` for `Foundation.URL` (it has `NativeTypeName`), and the entire constructor is blocked. The parameter even has a default value — it could literally just be `nil` in Swift.

### The fix

One line change in `BoundGenericsHandler.cs` at line 501:

```csharp
// Before:
if (genericParam is NamedTypeSpec namedArg && (IsObjCBridgedType(namedArg) || IsNonSwiftObjectMappedType(namedArg)))
    return true;

// After:
if (!outerIsOptional && genericParam is NamedTypeSpec namedArg && (IsObjCBridgedType(namedArg) || IsNonSwiftObjectMappedType(namedArg)))
    return true;
```

This mirrors the existing exemptions at lines 477 and 481. Same rationale, same pattern.

### Why this is the right fix (not a hack)

- `SwiftOptional<T>` has no `ISwiftObject` constraint on `T` (`SwiftOptional.cs:27`). So `SwiftOptional<NSUrl>` and `SwiftOptional<UIImage>` are valid C# — no CS0311.
- The projection factory already produces correct marshalling code for these combinations.
- The fix doesn't weaken protection for actual problems: `SwiftArray<Foundation.URL>` (where `SwiftArray<T> where T : ISwiftObject` would fail) is still blocked because `outerIsOptional` is `false` for arrays.
- The code comment at lines 469-471 literally explains the rationale that applies equally to this case.

### Impact: 48 constructors across 14 libraries

| Library | Blocked constructors recovered |
|---|---|
| BlinkID | 2 (BlinkIDSdkSettings, ResourceLoadError) |
| Nuke | 2 |
| Alamofire | 4 (DataResponse, DownloadResponse, PinnedCertificatesTrustEvaluator, PublicKeysTrustEvaluator) |
| Kingfisher | 8 |
| XMLCoder | 8 |
| StripeConnect | 6 |
| StripeUICore | 6 |
| StripePayments | 2 |
| StripePaymentSheet | 3 |
| DifferenceKit | 2 |
| SkeletonView | 2 |
| StripeCameraCore | 2 |
| SwiftyBeaver | 1 |
| Quick | 1 |

This also unblocks **properties** using `Optional<NativeRemappedType>` — the same gate is called in `MemberEmissionValidator.CanEmitProperty` (line 190). Fixing the method fixes all call sites simultaneously since `MemberGateEvaluator`, `MethodHandler`, `PropertyHandler`, and `MemberEmissionValidator` all call `HasNonSwiftObjectGenericArg`.
