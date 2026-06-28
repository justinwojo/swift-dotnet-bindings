# BlinkID — Binding Audit

- **Package**: SwiftBindings.BlinkID v7.8.0   **Mode**: zip   **TFM(s)**: net10.0-ios
- **Native**: microblink/blinkid-ios 7.8.0
- **Audited at**: swift-dotnet-packages@1e8c27a, generated 2026-06-27T19:47Z

## Verdict

Strong binding: all 118 types emit (100%) and 595/646 members (92.1%) land; the dominant skip bucket is 22 `SynthesizedCodable` entries on internal-analytics `*Pinglet` structs — correct pruning, zero consumer impact. Real-gap surface is exactly **8 members** from 6 items. The create-sdk → create-session → process-frame → get-result async flow is fully surfaced with proper `Task<T>` / `CancellationToken` affordances. The one meaningful extraction-model gap is `DriverLicenseDetailedInfo.vehicleClassesInfo` (the per-permit vehicle-class list), which lands `null` in the bound type — an `AnyType` generic projection failure. Everything else compiles and looks idiomatic. Tests are thorough on enums and error types (~227 assertions) but leave all result-model properties and the async call path untested.

## 1. Coverage

### Totals

| | Types | Members |
|---|---|---|
| Total | 118 | 646 |
| Emitted | 118 (100%) | 595 (92.1%) |
| Skipped | 0 | 30 |
| Synthesized (generator-added) | — | 575 |

`EmittedMembersByKind`: Property 479, Method 100, Operator 16.

### Skip-reason breakdown

| Reason | Count | Classification |
|---|---|---|
| SynthesizedCodable | 22 | **(a) correctly excluded** — `Encoder`/`Decoder` existentials are unprojectable; only affects `CameraHardwareInfoPinglet`, `ScanningConditionsPinglet`, `WrapperProductInfoPinglet`, `UxEventPinglet`, `LogPinglet`, `SdkInitStartPinglet`, `CameraPermissionPinglet`, `ErrorPinglet`, `CameraInputInfoPinglet` (internal telemetry structs). No consumer-facing impact. |
| UnsupportedType | 3 | Mix — see below |
| UnsupportedSignature | 2 | Mix — see below |
| AnyTypeFallback | 1 | **(b) real gap** |
| UnsatisfiedGenericConstraint | 1 | **(b) real gap** (minor) |
| EveryProtocolConformanceSkipped | 1 | **(a) correctly excluded** — `Pinglet.PingletProxy` is an internal analytics protocol proxy; no consumer path touches it |

### Real gaps (b)

**B1 — `DriverLicenseDetailedInfo.vehicleClassesInfo` (AnyTypeFallback)**
- Swift: `public let vehicleClassesInfo: [VehicleClassInfo<StringType>]?` (`swiftinterface:1652`)
- Binding: property dropped; comment at `BlinkID.cs:40934` explains the type collapsed to `AnyType`
- Impact: **medium-high for DL scanning**. `DriverLicenseDetailedInfo` carries the list of vehicle-class permits (vehicle class code, licence type, effective/expiry date). The single-vehicle-class `VehicleClass`/`VehicleClassInfo` is emitted as a generic `VehicleClassInfo<TStringType>` (bound at `BlinkID.cs:40373`), but `vehicleClassesInfo` needs `SwiftArray<VehicleClassInfo<StringType>>` where `StringType` is itself a generic param — the generator can't project a nested open generic into a concrete array type.
- Worth fixing? Yes for commercial DL use. The unlock would be: bind `vehicleClassesInfo` as `IReadOnlyList<VehicleClassInfo<BlinkIDSDK.StringResult>>?` (the concrete instantiation at the extraction-result call site). Requires the generator to propagate the concrete type argument from the containing generic scope into the property type.

**B2 — `CameraFrame.buffer` (UnsupportedType) + `CameraFrame.init` (UnsupportedSignature)**
- Swift: `public let buffer: MBSampleBufferWrapper` + matching `init(buffer:roi:orientation:)` (`swiftinterface:1606,1609`)
- `MBSampleBufferWrapper` is a precompiled-only ObjC-bridge type not exported in the module's public Swift ABI; `SwiftWrapperRequired=false` because this is a closed-source binary where wrapper compilation is impossible anyway
- Impact: **low** — `CameraFrame` is only needed for apps driving the raw-frame pipeline without BlinkID's built-in camera UI. Such apps would supply frames by wrapping `CMSampleBuffer` themselves. The missing init + buffer property make `CameraFrame` a read-only shell for custom-pipeline consumers, but Microblink's intended usage is the higher-level `BlinkIDSession.process(:)` which takes `InputImage` (`BlinkID.cs:31962`), not `CameraFrame.buffer` directly.
- Worth fixing? Low priority given the closed-source constraint; would need a C# wrapper that accepts `IntPtr`/`CMSampleBufferWrapper` opaque handle.

**B3 — `ResourceLoadError.init` (UnsatisfiedGenericConstraint)**
- Swift: `public init(name: String, model: R) where R: BinaryFloatingPoint` (error detail carrying a float-precision value) (`swiftinterface:2059`)
- The `ResourceLoadError` struct is fully emitted (`BlinkID.cs:52257`) with its `Name` string property; the missing init only affects constructing the error from C# (not consuming one surfaced by Swift throws path)
- Impact: **negligible** — consumers don't construct `ResourceLoadError`; they receive it via `SDKInitError.resourceLoad(error)` (`BlinkID.cs:52707`) which is properly bound with `TryGetResourceLoad` (`BlinkID.cs:52799`)

**B4 — `PingManager.addPinglet` (UnsupportedSignature) + `PingManager.unownedExecutor` / `ProcessingActor.unownedExecutor` (UnsupportedType)**
- `addPinglet<P>` is an async Swift generic method on an actor; no `@_cdecl` wrapper shipped (`swiftinterface:1907`)
- `unownedExecutor` is an actor runtime slot, not consumer API
- Impact: **negligible** — `PingManager` is Microblink's internal analytics actor; consumer-facing analytics go via `BlinkIDSdk.SendPingletsAsync` (`BlinkID.cs:48497`) which is fully emitted.

### Prioritized generator unlocks

| # | API | Unlock | Value | Effort |
|---|---|---|---|---|
| 1 | `DriverLicenseDetailedInfo.vehicleClassesInfo` | Propagate concrete generic arg from containing type into nested generic property type | Med-High | Med |
| 2 | `CameraFrame.init` + `.buffer` | Expose opaque handle for closed-source types (MBSampleBufferWrapper → `IntPtr` shim) | Low | High |
| 3 | `ResourceLoadError.init` | Relax ISwiftObject constraint for `BinaryFloatingPoint`-bounded generics | Very Low | Med |

## 2. C# Quality

**Naming / shape** — Clean. PascalCase throughout; no mangled Swift symbols visible to consumers. Nested types (`BlinkIDSDK.StringResult`, `DocumentClassInfo`) are correctly placed in their namespacing type. The large generic types (`DriverLicenseDetailedInfo<TStringType>`, `VehicleClassInfo<TStringType>`) bind cleanly with a single type parameter.

**Async** — All async Swift methods surface as `Task<T>` with optional `CancellationToken`:
- `BlinkIDSdk.CreateBlinkIDSdkAsync(settings)` → `Task<BlinkIDSdk>` (`BlinkID.cs:46252`)
- `BlinkIDSession.ProcessAsync(inputImage)` → `Task<FrameProcessResult>` (`BlinkID.cs:31962`)
- `BlinkIDSession.GetResultAsync()` → `Task<BlinkIDScanningResult>` (`BlinkID.cs:32481`)
- `BlinkIDSession.ResetAsync()` / `AllowBarcodeStepAsync()` / `GetSessionIdAsync()` / `GetSessionNumberAsync()` all surface correctly

The `@BlinkID.ProcessingActor` global-actor isolation on `createBlinkIDSdk` and `createScanningSession` is handled by the async bridge; consumers get clean `await` semantics with no actor-isolation leakage into the C# type.

**Nullability** — Correct throughout: `BlinkIDScanningResult` exposes `FirstName`/`LastName`/`FullName`/`DateOfBirth` etc. as `BlinkIDSDK.StringResult?` (`BlinkID.cs:41263`, 41313, 41363 …); `DataMatchResult` as nullable (`BlinkID.cs:41168`). No obviously missing `?` annotations spotted.

**Lifetime** — `BlinkIDSdk` and `BlinkIDSession` are reference types with `IDisposable`; `BlinkIDScanningResult`, `BlinkIDSessionSettings`, `BlinkIDSdkSettings`, `SingleSideScanningResult` are value types with `IDisposable` wrapping `SwiftSafeHandle`. Double-dispose is safe (confirmed by tests). VWT destroy chain runs via finalizer fallback.

**Constructor ergonomics** — `BlinkIDSdkSettings` has a 9-parameter primary constructor and 4 trailing-defaults overloads (`BlinkID.cs:45423,45474,45509,45548,45588`). The shortest usable overload is 5 params (`licenseKey, licensee, helloLogEnabled, downloadResources, resourceDownloadUrl`) — acceptable given the underlying API complexity. The default `BlinkIDSessionSettings()` constructor (`BlinkID.cs:38872`) makes zero-config sessions straightforward.

**Minor quality note** — `BlinkIDSdkSettings` implements `ISdkSettings` and `Swift.Runtime.IExistentialBoxable` (`BlinkID.cs:44670`). The existential boxing interface leaks into the public signature. This is intentional infrastructure — the `BlinkIDSdk.CreateBlinkIDSdkAsync` path takes a settings parameter that needs existential boxing under the hood — but `IExistentialBoxable` is a `Swift.Runtime` implementation detail that a C# consumer should never need to invoke or pattern-match on. Low severity, no functional impact.

## 3. Test Coverage

**Depth overview**: 227 distinct result registrations in 8 sections. Sections 1–3 and 5–8 exercise real runtime behavior (metadata reads, enum ABI, enum raw values, error factory + TryGet dispatch, memory pressure, double-dispose). Section 6 is **metadata-only** for the result-model types — weak by methodology definition but pragmatically justified since result structs can only be populated by an actual scan (license + camera).

**What is covered (strong)**:
- Type metadata for 29 types including all domain enums (Section 1) — runtime ABI check
- 15+ simple enums with exact raw-value verification and distinctness proofs (Section 2)
- 7 tag-based enums with `.Tag`, `.RawValue`, `.FromRawValue` round-trips and valid/invalid sentinel coverage (Sections 3, 7)
- `ResourceDownloaderError` and `ResourcesError` enum factory methods, `TryGet` pattern, wrong-case sentinel (Section 5) — proves associated-value dispatch
- Memory pressure: 100 DocumentType cycles, 50 Country/DetectionStatus/RequestTimeout cycles, 30 ResourceDownloaderError factory cycles (Section 8)
- Double-dispose safety on `DocumentType` (Section 8)
- 16 result-struct metadata probes for `BlinkIDScanningResult`, `VIZResult`, `MRZResult`, `BarcodeResult`, `SingleSideScanningResult`, `FrameProcessResult`, `ScanningSettings`, `BlinkIDSessionSettings`, `InputImageAnalysisResult`, etc. (Section 6) — proves metadata resolution works even without a real result

**Significant untested surface**:

| Gap | Surface | Why it matters |
|---|---|---|
| Async call path | `BlinkIDSdk.CreateBlinkIDSdkAsync`, `BlinkIDSession.ProcessAsync`, `GetResultAsync`, `ResetAsync` | The core SDK flow is never exercised end-to-end; callback bridge / TCS wiring unverified |
| Result model reads | `BlinkIDScanningResult.FirstName/LastName/FullName/DateOfBirth/DocumentClassInfo/DataMatchResult/RecognitionMode` | 100+ emitted properties on the most valuable type — zero property read coverage |
| VIZResult / MRZResult / BarcodeResult properties | All individual field properties | Only metadata probed |
| `BlinkIDSessionSettings` constructor | `BlinkIDSessionSettings(inputImageSource:)` | Default constructor trivial; the real overloads are untested |
| `ScanningSettings` constructor | All 4 overloads (BlinkID.cs:38259,38339,38379,38419) | Long positional arg lists at risk for ordering bugs |
| `DriverLicenseDetailedInfo<TStringType>` properties | `VehicleClass`, `LicenceType`, `EffectiveDate`, `ExpiryDate` | Known-accessible substitute for the missing `vehicleClassesInfo` |
| `BlinkIDSdk.TerminateBlinkIDSdk()` | Static termination (`BlinkID.cs:46475`) | Cleanup path untested |

**Legitimate omissions**: `CreateBlinkIDSdkAsync` / `ProcessAsync` / `GetResultAsync` require a valid Microblink license key + camera input — not testable on simulator without those. The metadata-only pattern in Section 6 is the practical ceiling without a license.

**Recommended tests to add** (matched to right layer):

1. **BindingTests layer** (Swift fixture + C# runtime test, no license needed):
   - Construct `BlinkIDSdkSettings` with the shortest overload; assert each property value (`LicenseKey`, `Licensee`, `HelloLogEnabled`, etc.) round-trips. Proves the 9-param positional constructor is correct.
   - Construct `BlinkIDSessionSettings` default + each typed overload; assert `ScanningMode` and `InputImageSource` read back correctly.
   - Construct `ScanningSettings` via all 4 overloads; spot-check `AnonymizationMode`, `EnableBarcodeScanOnly`, `MaxAllowedMismatchesPerField`. Catches parameter ordering bugs without a license.

2. **Unit / metadata tests** (no runtime, no license):
   - Verify `BlinkIDScanningResult` metadata `.Size > 0` is already in Section 6; extend to also check `.IsValueType == true` and `.NumberOfStoredProperties > 0` to guard against struct-ABI-gone-empty regressions.

3. **BindingTests — `DriverLicenseDetailedInfo` fields**:
   - Build a minimal Swift fixture that returns a `DriverLicenseDetailedInfo<SwiftString>` instance with `vehicleClass` set, then call the `VehicleClass` property from C#. Confirms the generic specialization path works for the one surviving single-vehicle-class property.

## Action Items

| # | Dimension | Finding | Recommendation | Effort | Value |
|---|---|---|---|---|---|
| 1 | Coverage | `DriverLicenseDetailedInfo.vehicleClassesInfo` skipped — vehicle-class permit list missing for DL results | Teach generator to propagate containing-type generic arg into nested generic collection property type | Med | Med-High |
| 2 | Coverage | `CameraFrame` init + buffer inaccessible (closed-source `MBSampleBufferWrapper`) | Document as known limitation; if raw-frame pipeline is needed, provide an opaque-handle shim in a PR against BlinkID binding | High | Low |
| 3 | Test Coverage | Core async flow (`CreateBlinkIDSdkAsync` → `ProcessAsync` → `GetResultAsync`) has zero test coverage | Add BindingTests fixtures exercising round-trips on settings / session construction even without a live license | Low | High |
| 4 | Test Coverage | 100+ `BlinkIDScanningResult` properties have only metadata coverage | Add BindingTests fixtures returning a synthetic result struct and reading `FirstName`, `DocumentClassInfo`, `RecognitionMode`, `DataMatchResult` | Med | High |
| 5 | Test Coverage | `ScanningSettings` + `BlinkIDSessionSettings` constructors untested | Add construction + property read-back tests in BindingTests; catches positional-arg ordering bugs | Low | Med |
| 6 | Quality | `ISdkSettings` + `IExistentialBoxable` surface on `BlinkIDSdkSettings` — implementation detail in public signature | Low severity; consider hiding `IExistentialBoxable` with `EditorBrowsable(Never)` | Low | Low |
