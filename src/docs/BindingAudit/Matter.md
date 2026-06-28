# Matter — Binding Audit

- **Package**: SwiftBindings.Apple.Matter v26.2.8   **Mode**: apple (ObjC — no binding-report.json)   **TFM(s)**: net10.0-ios26.2 / net10.0-macos26.2 / net10.0-maccatalyst26.2
- **Native**: Apple Matter.framework (iOS 16.1+, macOS 13.0+, Mac Catalyst 16.1+)
- **Audited at**: swift-bindings main 8dcc3032 / swift-dotnet-packages 1e8c27a, generated 2026-06-27

> **ObjC mode note.** Matter is a pure Objective-C framework — there is no `.swiftinterface` and no `binding-report.json`. Coverage is assessed by comparing public `@interface`/`@protocol` declarations across the 62 ObjC headers against the generated `ApiDefinition.cs`. The bgen toolchain is used instead of the Swift-interop path.

## Verdict

Near-complete coverage of a massive surface: ~1,320 C# types map to ~1,323 distinct ObjC class definitions and 21 protocols (100% effective). Zero `[Verify]` artifacts; nullability is thorough (8,902 `[NullAllowed]`, 2,093 `[return: NullAllowed]`); naming follows standard bgen PascalCase conventions with correct acronym treatment (`OTA`, `LAN`, `UTC`). The binding is shippable and usable. The main architectural observation is the expected one for ObjC bindings: none of Matter's numerous completion-handler methods surface `Task`-returning overloads — all remain as `Action<T, NSError>` callbacks. Tests cover the setup-payload path well but are sparse on the cluster surface (appropriately so, given cluster use needs real hardware).

---

## 1. Coverage

### Header-vs-ApiDefinition comparison

| Source | Count | Notes |
|---|---|---|
| ObjC `@interface MTR*` (non-category) | 1,323 | From 62 headers; excludes the 461 ObjC category extensions (`@interface MTR* (CategoryName)`) |
| ObjC `@protocol MTR*` (unique, non-forward-decl) | 21 | |
| C# `partial interface MTR*` (ApiDefinition.cs) | 1,320 | Includes 9 `[Protocol, Model]` delegate interfaces and 12 `IMTR*` protocol interfaces |

**Effective type coverage: ~100%.** The apparent 3-type shortfall (1,323 − 1,320) is a diff-grep artefact, not a real gap:

- **Naming convention upgrades**: ObjC uses lowercase-first acronyms (`MTROtaSoftwareUpdateProvider`, `MTRBaseClusterWakeOnLan`, `MTRTimeSynchronizationClusterSetUtcTimeParams`); the C# binding promotes them to idiomatic uppercase (`MTROTASoftwareUpdateProvider`, `MTRBaseClusterWakeOnLAN`, `MTRTimeSynchronizationClusterSetUTCTimeParams`). These are present; just differently named.
- **Comment-with-parens false exclusions**: Five ObjC classes have inline comments containing `(` — e.g., `@interface MTRDeviceType : NSObject /* <NSCopying> (see below) */` — which caused them to be mistakenly excluded from the ObjC diff list. All five (`MTRDeviceType`, `MTRDeviceTypeRevision`, `MTRProductIdentity`, `MTROptionalQRCodeInfo`, `MTRSetupPayload`) are present in `ApiDefinition.cs`.
- **Protocol mapping**: Delegate protocols (`MTRCommissionableBrowserDelegate`, `MTRDeviceDelegate`, `MTROTAProviderDelegate`, etc.) appear as `@protocol` in ObjC and as `[Protocol, Model]` C# interfaces in the binding — covered under a different grep pattern.

No notable public ObjC class or protocol is missing from the C# binding.

### Quality marker scan

| Marker | Count | Assessment |
|---|---|---|
| `[Verify` attributes | **0** | Clean — bgen left no pending manual review tags |
| `// TODO` | 3 | All three are verbatim copies of Apple's own header `///` XML doc comments (ApiDefinition.cs:879, :887, :891 in `MTRDevice`). Not binding issues. |
| `NativeHandle Constructor(...)` | 564 | Standard bgen pattern for ObjC `initWith...:error:` initializers. Correct. |
| Opaque `IntPtr` | 13 | All legitimate: `opaqueDeviceHandle` (attestation callbacks), `CopyPublicKey`/`PublicKey` (MTRKeypair protocol). None are opaque blobs where a typed object was available. |
| `[Verify` / opaque `NativeObject` degrades | 0 | No types bound as opaque blobs. |

### StructsAndEnums.cs

541 enums; 175 `[Flags]` / option-sets (StructsAndEnums.cs:175-count). Enum values are well-named; digit-leading cases get the `_` prefix per bgen convention (e.g., `MTRNetworkCommissioningWiFiBand._2G4`). No structural issues observed.

### Deprecated-class carry-through (minor)

`MTRBackwardsCompatShims.h` (3,461 lines) introduces backwards-compat aliases for a handful of deprecated cluster classes (`MTRBaseClusterOnOffSwitchConfiguration`, `MTRBaseClusterBinaryInputBasic`, `MTRBaseClusterBarrierControl`, `MTRBaseClusterElectricalMeasurement`, `MTRClusterBarrierControl`, etc.). These ARE present in `ApiDefinition.cs` (e.g., ApiDefinition.cs:86194 `partial interface MTRBaseClusterOnOffSwitchConfiguration`) but **without** `[Deprecated]` or `[Obsoleted]` C# attributes. Apple marks them `MTR_DEPRECATED(...)` in the headers, but bgen does not translate that macro to C# deprecation annotations. Consumers will not see deprecation warnings when using these classes. This is a bgen tooling limitation that applies to every Apple ObjC framework binding (not Matter-specific) — correctly excluded classes are not a problem; the issue is the missing deprecation signal.

### Generator unlocks

None applicable. ObjC binding through bgen; the generator (Swift interop path) is not involved. The `MTR_DEPRECATED` → `[Deprecated]` gap is a bgen concern, not a swiftbindings generator concern.

---

## 2. C# Quality

**Naming / shape.** PascalCase throughout. Acronyms are correctly uppercased (`OTA`, `LAN`, `UTC`) — better than the ObjC originals. No leaked ObjC selector fragments. The `MTR` prefix is retained across all 1,320 types, which is idiomatic for Apple ObjC bindings (the prefix acts as a namespace).

**Cluster breadth.** The binding spans all cluster types: `MTRBaseCluster*` (145 async base-cluster interfaces in ApiDefinition.cs), `MTRCluster*` (145 sync cluster interfaces), `MTR*ClusterParams` structs (548 in `MTRCommandPayloadsObjc.h`), and 412 event/struct types from `MTRStructsObjc.h`. The breadth is thorough and matches the Matter spec's full cluster catalog.

**Completion handlers — no Task overloads.** Matter exposes completion-handler callbacks across virtually every meaningful API: `readAttributesWithEndpointID:...:queue:completion:`, `invokeCommandWithEndpointID:...:completion:`, `subscribeAttributeOnOffWithParams:subscriptionEstablished:reportHandler:`, etc. The binding surfaces all of these faithfully as `Action<T, NSError>` callbacks (ApiDefinition.cs:723, :749, :917, :32944, etc.). **There are no `[Async]` attributes** and no `Task`-returning overloads. This is expected — bgen does not generate `[Async]` unless added manually, and the Apple ObjC tooling supports it optionally. For a C# developer, wrapping a completion handler in `TaskCompletionSource<T>` is the current consumption pattern. This is not a correctness bug but is the dominant ergonomic gap in a heavily callback-driven framework.

**Long method names from multi-keyword selectors.** Some ObjC selectors produce very long C# method names, e.g. `InvokeCommandWithEndpointIDClusterIDCommandIDCommandFieldsExpectedValuesExpectedValueIntervalTimedInvokeTimeoutClientQueueCompletion` (ApiDefinition.cs:976) from the 9-keyword ObjC selector. This is an inherent property of verbatim selector-to-C#-name translation and is standard across all Apple ObjC bindings. The majority of cluster command methods have reasonably short names (`OffWithParams`, `OnWithCompletion`, `SubscribeAttributeOnOffWithParams`), so this only arises on the most complex multi-argument paths.

**Nullability.** Dense and thorough: 8,902 `[NullAllowed]` on parameters, 2,093 `[return: NullAllowed]` on return values. The `out NSError?` pattern is correctly applied on failable methods (e.g., `SetupCommissioningSessionWithPayload(..., [NullAllowed] out NSError error)` at ApiDefinition.cs:1444). No contradictory or obviously missing annotations observed in the sampled interfaces.

**Lifetime / default-ctor.** 322 `[DisableDefaultCtor]` attributes — correct for ObjC types that require specific init arguments. Reference-counted ObjC objects are memory-managed by the ObjC ARC bridge (no `IDisposable` needed or expected). The guide correctly notes that callers should hold on to `MTRDevice` objects while using them. No lifetime smells observed.

**Failable initializer.** `MTRDeviceController`'s init: `NativeHandle Constructor(MTRDeviceControllerAbstractParameters parameters, [NullAllowed] out NSError error)` (ApiDefinition.cs:1436) — the failable ctor pattern is correctly applied.

**Protocol surfacing.** The 9 ObjC delegate protocols (`MTROTAProviderDelegate`, `MTRDeviceDelegate`, `MTRCommissioningDelegate`, etc.) are bound as `[Protocol, Model]` with abstract `[Export]` methods. The 12 pure-protocol interfaces (`IMTRStorage`, `IMTRKeypair`, `IMTROTAProviderDelegate`, etc.) use the `IMTR*` prefix and `[Abstract]` on required methods. Both shapes are correct bgen patterns.

**No broken or unusable surface observed.** The key commissioning types (`MTRSetupPayload`, `MTRDeviceController`, `MTRDeviceControllerFactory`, `MTRBaseDevice`, `MTRDevice`) all have usable constructors and methods.

---

## 3. Test Coverage

**Structure.** Tests live in `tests/Tests.cs` (205 lines). Runner: `Tests.Run()` called from both `Program.UIKit.cs` and `Program.MacConsole.cs`. Framework: homebrew pass/fail/skip counters.

**Case inventory:**

| # | Test | Depth | Surface touched |
|---|---|---|---|
| 1–12 | `ClassHandleTest<T>` × 12 | Weak (ObjC class registration) | `MTRSetupPayload`, `MTRCommissioningParameters`, `MTRDeviceController`, `MTRDeviceControllerFactory`, `MTRDeviceControllerStartupParams`, `MTRBaseDevice`, `MTRDevice`, `MTRClusterStateCacheContainer`, `MTRBaseClusterOnOff`, `MTROnboardingPayloadParser`, `MTRQRCodeSetupPayloadParser`, `MTRManualSetupPayloadParser` |
| 13 | `MTRNetworkCommissioningWiFiBand` case values | Strong (pinned enum values) | `_2G4 = 0 … _1G = 5` |
| 14 | `MTRSetupPayload(passcode, discriminator)` round-trip | **Strong** (ctor + P/Invoke + property read) | `initWithSetupPasscode:discriminator:`, `SetupPasscode`, `Discriminator` |
| 15 | `MTRSetupPayload(qrPayload)` parse | **Strong** (QR parse + non-null check) | `initWithPayload:`, `SetupPasscode`, `Discriminator` |
| 16 | `MTRQRCodeSetupPayloadParser.PopulatePayload` | **Strong** (cross-path consistency) | `initWithQRCode:`, `PopulatePayload(out NSError)`, cross-checks vs test 15 |
| 17 | `MTROnboardingPayloadParser.SetupPayloadForOnboardingPayload` | **Strong** (static dispatcher) | Static factory, cross-checks passcode vs test 15 |
| 18 | `MTRSetupPayload` static random generators | Medium | `GenerateRandomPIN()`, `GenerateRandomSetupPasscode()` |
| 19 | `MTRCommissioningParameters()` ctor | Weak (handle smoke) | Default ctor, `Handle` non-zero check |
| — | `MTRDeviceController` construction | Skip (legitimate) | Requires factory + storage delegate — not a smoke-test concern |

**Depth assessment.** The 12 class-registration smokes (tests 1–12) are weak but serve a real purpose: they verify the framework is loaded and the bgen `[Register]` attribute maps correctly to an Apple-supplied class. The four setup-payload tests (14–17) are strong — they round-trip real values, catch out-param marshalling bugs, and cross-validate three distinct parse paths. The enum-pin test (13) guards against Apple silently reordering enum cases across SDK versions.

**Untested surface:**

| Gap | Severity | Notes |
|---|---|---|
| `MTR*Cluster*Params` struct construction (e.g., `MTROnOffClusterOffParams`, `MTRIdentifyClusterIdentifyParams`) | Low | These are plain NSObject subclasses with properties; construction + property assignment is testable headlessly. Proves the bgen binding round-trip for the cluster-param shape without needing hardware. |
| `MTRManualSetupPayloadParser` parse | Low | Registered (test 12) but never invoked. A manual code parse (`"35048...") could exercise `PopulatePayload(out NSError)` on the manual path. |
| `MTRDeviceType`, `MTRDeviceTypeRevision`, `MTRProductIdentity` | Low | Pure data objects; construction and property read is headlessly testable. |
| Cluster command execution, subscription, commissioning flow | N/A | **All correctly legitimately untestable headlessly** — require a real Matter controller, fabric, and commissioned device. Hardware-skips are appropriate; no recommendation to add these. |

---

## Action Items

| # | Dimension | Finding | Recommendation | Effort | Value |
|---|---|---|---|---|---|
| 1 | Coverage | Deprecated ObjC class aliases (`MTRBaseClusterOnOffSwitchConfiguration`, `MTRBaseClusterBinaryInputBasic`, etc. from BackwardsCompatShims.h) are in the binding without `[Deprecated]`/`[Obsoleted]` C# annotations | Not actionable here — bgen tooling limitation. Document in the package README's naming-conventions section that deprecated shim classes exist and recommend using the canonical successor names. | XS (doc note) | Low |
| 2 | C# Quality | No `Task`-returning `[Async]` overloads for any of Matter's ~8,400+ completion-handler methods | Evaluate adding `[Async]` attributes to the highest-value methods (`MTRDeviceController`'s commissioning methods, `MTRBaseDevice.ReadAttributesWithEndpointID`, `InvokeCommandWithEndpointID`) in the ApiDefinition source before bgen runs. Not worth doing for all 8,400 — pick the 10–15 primary entry points. | M | Medium |
| 3 | Test | `MTRManualSetupPayloadParser.PopulatePayload` registered but never exercised | Add one manual-code parse test: `new MTRManualSetupPayloadParser("35048..."); parser.PopulatePayload(out NSError)`. Headlessly runnable. | XS | Low |
| 4 | Test | No `MTR*ClusterParams` construction test | Add construction smoke for 2–3 representative cluster param types (e.g., `new MTROnOffClusterOffWithEffectParams()`, `new MTRIdentifyClusterIdentifyParams()`) to verify the params-struct bgen shape. Headlessly runnable. | S | Low |
