# MatterSupport — Binding Audit

- **Package**: SwiftBindings.Apple.MatterSupport v26.2.8   **Mode**: apple   **TFM(s)**: net10.0-ios26.2
- **Native**: Apple MatterSupport.framework (iOS 16.1+, macOS 14+)
- **Audited at**: swift-bindings main 8dcc3032 / swift-dotnet-packages 1e8c27a, generated 2026-06-27T19:49:37Z

## Verdict

Clean binding — all 11 types emitted (100%), all 19 skips are SynthesizedCodable (intended exclusion), zero real gaps. The central `MatterAddDeviceRequest` flow is fully usable async: `PerformAsync` with `CancellationToken`, all six `MatterAddDeviceExtensionRequestHandler` methods properly `virtual Task<T>`, cross-module `Matter.MTRSetupPayload` reference wired correctly. Tests are strong on the core request path but leave the extension-handler nested types and `DeviceCriteria` factory cases untested.

## 1. Coverage

### Type coverage: 11/11 (100%)

| Type | Kind | Emitted |
|---|---|---|
| `MatterAddDeviceRequest` | class | ✅ |
| `MatterAddDeviceRequest.Room` | struct | ✅ |
| `MatterAddDeviceRequest.TopologyType` | struct (nested, `Type` suffix) | ✅ |
| `MatterAddDeviceRequest.DeviceCriteria` | enum-with-payload | ✅ |
| `MatterAddDeviceRequest.Home` | struct | ✅ |
| `MatterAddDeviceExtensionRequestHandler` | NSObject subclass | ✅ |
| `MatterAddDeviceExtensionRequestHandler.WiFiNetworkAssociation` | struct | ✅ |
| `MatterAddDeviceExtensionRequestHandler.ThreadScanResult` | struct | ✅ |
| `MatterAddDeviceExtensionRequestHandler.ThreadNetworkAssociation` | struct | ✅ |
| `MatterAddDeviceExtensionRequestHandler.WiFiScanResult` | struct | ✅ |
| `MatterAddDeviceExtensionRequestHandler.DeviceCredential` | struct | ✅ |

### Member count reconciliation

`binding-report.json` reports: **TotalMembers 86 / EmittedMembers 46 / SkippedMembers 19 / SynthesizedMembers 69**.

The "86 vs 46" gap is not missing coverage — it's an accounting artefact:

- **TotalMembers (86)** = raw Swift API slot count where each read-write property contributes 2 slots (getter + setter). `binding-emission-report.json` confirms 67 total wrapped slots (49 `CdeclProperty` + 8 `CdeclConstructor` + 9 `CdeclMethod` + 1 `NativeThunk`), consistent with `TotalMembers − SkippedMembers = 86 − 19 = 67`.
- **EmittedMembers (46)** = collapsed public C# members: 26 properties (each counting as 1, though using ≈49 wrapper slots), 19 methods, 1 operator. 23 of the 26 properties are read-write, yielding 23 extra setter wrapper slots; those 23 + 44 other slots = 67. ✅
- **SkippedMembers (19)** = all `SynthesizedCodable`: `encode` + `init(from:)` on 9 types that conform to `Codable` via Swift synthesis. Correctly excluded — `Encoder`/`Decoder` are unresolvable existential protocols the generator cannot wrap.
- **SynthesizedMembers (69)** = generator-added members beyond the native Swift surface: `Equatable` operators, `EncodeToJson`/`DecodeFromJson` helper pairs, additional constructor overloads, metadata accessors, etc.

**Effective native coverage: 67/67 wrapped slots = 100% of the emittable Swift API.**

### Skip analysis

| Reason | Count | Classification |
|---|---|---|
| `SynthesizedCodable` | 19 | **(a) Correctly excluded** — encode/decode conformances synthesized by the Swift compiler; the existential `Encoder`/`Decoder` protocols are unresolvable in the generator. Synthesized JSON helpers (`EncodeToJson`/`DecodeFromJson`) are provided instead for all affected types. |

No **(b) real gaps** found.

### Generator unlocks

None warranted — this is a purposely narrow framework (smart-home commissioning). The `SynthesizedCodable` exclusion is a known generator limitation with an adequate workaround. No high-value unlock opportunity exists here beyond the project-wide Codable existential work.

---

## 2. C# Quality

**Naming / shape.** PascalCase throughout, no leaked Swift mangling. One naming note: the nested `Topology` struct (MatterSupport.cs:899) is emitted as `MatterAddDeviceRequest.TopologyType` — the `Type` suffix avoids a name collision with the `Topology` property (line 128). Slightly clunky for consumers but explicitly documented in `MATTERSUPPORT-GUIDE.md` and unavoidable given the collision.

**Async.** `MatterAddDeviceRequest.PerformAsync` (line 2663) returns `Task` with a trailing `CancellationToken` (default = `default`). The full cancellation plumbing is present: pre-flight check, `SBW_CancelTask` callback, `TrySetCanceled` on the TCS, and cleanup of `SwiftAsyncCallHolder`. All six `MatterAddDeviceExtensionRequestHandler` async methods are `virtual Task` / `virtual Task<T>` (lines 5580, 5767, 5948, 6112, 6294, 6455), correct for app-extension subclassing. No blocking-only fallbacks.

**Nullability.** `#nullable enable` at line 1. `SetupPayload` property returns `Matter.MTRSetupPayload?` (line 201) — correctly nullable (the Swift property is `Optional<MTRSetupPayload>`). Optional `Home?` and `Room?` parameters in handler methods (lines 6112, 6294, 6455) are properly nullable. No contradictory or missing annotations observed.

**Lifetime.** All Swift structs (`Room`, `TopologyType`, `DeviceCriteria`, `Home`, `WiFiNetworkAssociation`, `ThreadScanResult`, `ThreadNetworkAssociation`, `WiFiScanResult`, `DeviceCredential`) implement `ISwiftStruct` + `IDisposable`. `MatterAddDeviceExtensionRequestHandler` extends `Foundation.NSObject` (reference-counted ObjC object) — no `IDisposable` needed. The async `PerformAsync` holds a `DeferredSafeHandleRelease` to prevent premature collection of the struct buffer during the async flight (line 2672). Correct.

**DeviceCriteria enum-with-payload.** `DeviceCriteria` (line 1340) is a Swift indirect enum with 9 cases. The binding uses the standard generator pattern: a `CaseTag` discriminator enum (line 1568, `AllDevices = 8` matching Swift's no-payload-cases-last ordering), static factory methods (`Any`, `All`, `Not`, `CommissioningID`, `VendorID`, `ProductID`, `SerialNumber`, `FabricNode`, `AllDevices` singleton), and `TryGet*` payload accessors. Correct and idiomatic given the generator's enum-with-payload shape.

**Cross-module reference (ObjCPrefixBridge).** `Matter.MTRSetupPayload` is an ObjC type from the sibling `SwiftBindings.Apple.Matter` package. The binding correctly depends on that package; the wrapper Swift file uses `import Matter` to resolve the cross-module reference. The ObjCPrefixBridges entry in binding-report.json (`"Matter.MTRSetupPayload"`) confirms the bridge is registered. Consumers must add both `using Matter` and `using MatterSupport`.

**No broken or unusable surface observed.**

---

## 3. Test Coverage

**Structure.** Tests live in `tests/Tests.cs` (154 lines). Runner: `Tests.Run()` called from both UIKit (`Program.UIKit.cs`) and macOS console (`Program.MacConsole.cs`) entry points. Framework: homebrew pass/fail/skip counters (no XUnit).

**Case inventory (7 substantive + 1 skip = 8 total entries):**

| # | Test name | Depth | Surface touched |
|---|---|---|---|
| 1–6 | `MetadataTest<T>` × 6 | Weak (metadata only) | `MatterAddDeviceRequest`, `.TopologyType`, `.Home`, `.Room`, `.DeviceCriteria`, `WiFiNetworkAssociation` |
| 7 | `MatterAddDeviceRequest.IsSupported` | Medium (static bool P/Invoke) | `IsSupported` static property (iOS 17+ gate) |
| 8 | 2-arg ctor + `SetupPayload` round-trip | **Strong** (instantiate, P/Invoke, assert ObjC round-trip) | `MatterAddDeviceRequest(topology, setupPayload)`, `SetupPayload` getter, `MTRSetupPayload.SetupPasscode` |
| 9 | 3-arg ctor + `SetupPayload` | **Strong** | `MatterAddDeviceRequest(topology, setupPayload, deviceCriteria)` + `AllDevices` singleton |
| 10 | `ShowDeviceCriteria` default tag | **Strong** (construct + discriminate) | `ShowDeviceCriteria` property, `DeviceCriteria.Tag`, `CaseTag.AllDevices` |
| — | `PerformAsync` | Skip (legitimate) | Not tested — requires HomeKit entitlement + commissioned Matter device |

**Untested surface:**

| Untested | Severity | Recommended test |
|---|---|---|
| `MatterAddDeviceExtensionRequestHandler` nested type metadata + ctors (`WiFiScanResult.Init`, `DeviceCredential.Init`, `ThreadScanResult.Init`, `ThreadNetworkAssociation.Network`, `WiFiNetworkAssociation.Network`) | Medium | Metadata smokes + struct construction on sim; no device needed. `new WiFiScanResult(ssid, rssi, security, band)` should instantiate and not crash. |
| `DeviceCriteria` factory cases beyond `AllDevices`: `VendorID(nint)`, `CommissioningID(Guid)`, `Any(IReadOnlyList<…>)`, `All(…)`, `Not(…)` | Medium | Construct each case, check `Tag`, call `TryGet*`, assert payload round-trip. Fully sim-testable. |
| JSON helpers: `EncodeToJson`/`DecodeFromJson` on `MatterAddDeviceRequest`, `Room`, `Home`, `TopologyType` | Low | Encode a simple `Room` to JSON, decode it back, assert field equality. Validates the synthesized-Codable workaround end-to-end. |
| `ShouldScanNetworks` bool property (iOS 16.4+) | Low | Construct with 4-arg ctor (including `shouldScanNetworks: false`), read back, assert `false`. |
| `TopologyType.EcosystemName` property read-back | Low | Already constructed in test 8/9 — add `Assert(topology.EcosystemName == "Test Ecosystem")` to existing test. One-liner. |

---

## Action Items

| # | Dimension | Finding | Recommendation | Effort | Value |
|---|---|---|---|---|---|
| 1 | Test | Extension handler nested types (`WiFiScanResult`, `DeviceCredential`, `ThreadScanResult`, `WiFiNetworkAssociation`) have zero coverage | Add metadata smokes + struct-ctor construction tests for all 5 nested handler types | XS | Medium |
| 2 | Test | `DeviceCriteria` factory cases beyond `AllDevices` untested | Add round-trip tests for `VendorID`, `CommissioningID`, and at least one compound case (`Any`/`All`) | S | Medium |
| 3 | Test | JSON encode/decode helpers untested | Add `Room` encode→decode round-trip to validate the synthesized-Codable workaround | S | Low |
| 4 | Test | `TopologyType.EcosystemName` property never read back | Extend existing test-8 to assert `topology.EcosystemName` value | XS | Low |
