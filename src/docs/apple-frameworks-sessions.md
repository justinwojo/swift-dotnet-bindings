# Apple Frameworks — Remaining Work Sessions

## Session 1: macOS Smoke Test Wiring (CryptoKit + WeatherKit) ✅ ccc8cae6

### Context
The macOS runtime pipeline works (`nuke runtime-tests-macos` — 1053/1077 passing). macOS snapshots already exist at `BindingTests/obj/CryptoKitSnapshot-macOS/` and `BindingTests/obj/WeatherKitSnapshot-macOS/` with all artifacts (csproj, ProjectReference.targets, xcframework with `macos-arm64` slice). The only missing piece is the csproj wiring and Nuke smoke flag plumbing.

### Deliverables

1. **`RuntimeTestsApp.Mac.csproj`** — Add CryptoKit and WeatherKit smoke test blocks mirroring the iOS pattern in `RuntimeTestsApp.csproj`. For each framework, add all 5 parts:
   - `PropertyGroup` with `<XxxSnapshotDir>` (pointing to `../obj/XxxSnapshot-macOS/`), `<XxxSnapshotCsproj>` (`.Swift.macOS.csproj`), `<XxxSnapshotProjectRefTargets>` (`.Swift.macOS.ProjectReference.targets`), `<XxxSnapshotXcframework>`, and `<XxxSmokeEnabled>` sentinel (gate on `EnableXxxSmoke == true`, `RuntimeIdentifier == osx-arm64`, and `Exists()` checks for csproj, xcframework/macos-arm64, and targets)
   - `Target Name="_XxxSmokeGateCheck"` with `BeforeTargets="BeforeBuild"` — 4 `<Error>` items for each failed prereq (wrong RID, missing csproj, missing xcframework slice, missing targets file)
   - `PropertyGroup` defining `XXX_SMOKE` constant
   - `ItemGroup` with `ProjectReference` to the snapshot csproj (pass `IncludeSwiftBindingsRuntimeNative=false`)
   - `Import` of `ProjectReference.targets` (gated on raw opt-in + Exists, NOT on SmokeEnabled — Imports evaluate at load time)

2. **`RuntimeTestsApp.Mac.csproj`** — Add `<Compile>` items for the smoke test source files:
   - `<Compile Include="../RuntimeTestsApp/SmokeTests/CryptoKitSmokeTests.cs" />`
   - `<Compile Include="../RuntimeTestsApp/SmokeTests/WeatherKitSmokeTests.cs" />`

3. **`Build.RuntimeTests.cs`** — Wire macOS smoke flags into the Nuke build:
   - Add `AbsolutePath` properties for `CryptoKitMacOSSnapshotDir` and `WeatherKitMacOSSnapshotDir` pointing to `BindingTestsDir / "obj" / "XxxSnapshot-macOS"`
   - In the `RuntimeTestsMacOS` target, add `RegenerateAppleFrameworkSnapshot()` calls for both frameworks with `platform: MacOS`
   - Pass `EnableCryptoKitSmoke=true` and `EnableWeatherKitSmoke=true` to the `DotNetBuild` call when the respective `--enable-xxx-smoke` parameters are set
   - Register both flags in `GetActiveSmokeFlags()` for the macOS target
   - NOTE: Study the existing iOS smoke wiring carefully — the macOS wiring may need different snapshot generation or different parameter names. Follow the established patterns exactly.

4. **Validation**: Run `nuke runtime-tests-macos` (ideally with smoke flags if snapshots exist) and verify no regressions from the 1053/1077 baseline. Smoke tests should either pass or be gated correctly.

### Key naming differences from iOS
| iOS | macOS |
|-----|-------|
| `../obj/CryptoKitSnapshot/` | `../obj/CryptoKitSnapshot-macOS/` |
| `CryptoKit.Swift.iOS.csproj` | `CryptoKit.Swift.macOS.csproj` |
| `ios-arm64-simulator` xcframework slice | `macos-arm64` xcframework slice |
| `iossimulator-arm64` RID | `osx-arm64` RID |

---

## Session 2: Fix 7 AsyncMethodTests Timeouts on macOS ✅

### Context
All 7 `AsyncMethodTests` pass on iOS simulator but time out on macOS. The root cause is the macOS launcher architecture:

- **iOS** (`BindingTests/RuntimeTestsApp/Program.cs`): Uses UIKit — `UIApplication.Main()` drives an NSRunLoop on the main thread. Between tests, `NSRunLoop.Current.RunUntil(NSDate.FromTimeIntervalSinceNow(0.001))` pumps the loop. UIKit keeps GCD alive and responsive.
- **macOS** (`BindingTests/RuntimeTestsApp.Mac/Program.cs`): Plain `async Task<int> Main()` console app. No AppKit, no NSRunLoop, no `NSApplication.run()`. No run loop pumping anywhere.

Swift async methods use `swift_task_enqueueGlobal_hook` (installed by `SwiftBindingsTestLib_InitializeConcurrency`) to route tasks to a GCD `DispatchQueue`. On iOS, the UIKit run loop keeps GCD pumping. On macOS with no run loop, GCD tasks may not execute, causing the .NET `TaskCompletionSource` to never complete → 5-second `DefaultAsyncTimeout` → test timeout.

### Deliverables

1. **Investigate and fix** the macOS launcher so that Swift async callbacks fire correctly. Likely approaches (investigate which works):
   - Add `NSRunLoop.Current.RunUntil()` pumping in the macOS test runner between async tests (requires checking if `Foundation.NSRunLoop` is available in `net10.0-macos` TFM)
   - Or: Use `NSApplication.shared` with a minimal AppKit run loop to keep GCD alive
   - Or: Use `CFRunLoop` directly via P/Invoke
   - The fix should be minimal and not change the macOS launcher from a console app to a full AppKit app unless absolutely necessary

2. **Verify** all 7 `AsyncMethodTests` pass on macOS after the fix (they currently all time out)

3. **No regressions** — the other 1053+ passing tests must still pass

### Key files
- macOS launcher: `BindingTests/RuntimeTestsApp.Mac/Program.cs`
- iOS launcher (reference for run loop pattern): `BindingTests/RuntimeTestsApp/Program.cs` (~line 295 and TestBase ~line 65)
- Concurrency hook: `BindingTests/Sources/SwiftBindingsTestLib/Async/Methods.swift` (lines 8-48)
- Async test infrastructure: `BindingTests/RuntimeTestsApp/Async/`
- `TestBase` timeout: Look for `DefaultAsyncTimeout` in test infrastructure

---

## Session 3: Additional Framework Smokes (WorkoutKit, RoomPlan, ProximityReader, LiveCommunicationKit) ✅ 3c445223

### Context
These 4 frameworks have already been validated in `swift-dotnet-packages` with passing sim tests (WorkoutKit 13/13, RoomPlan 13/13, ProximityReader 10/10, LiveCommunicationKit 18/18). The generator produces working bindings for all four. What's missing is the in-tree BindingTests smoke test integration.

The snapshot generation is fully automated via `nuke regenerate-apple-snapshot --framework <Name>`. The csproj wiring is a well-understood 5-part pattern done 5 times already (StoreKit, CryptoKit, WeatherKit, TipKit, MusicKit).

Note: Apple framework smoke tests are temporary scaffolding (they may be removed once `swift-dotnet-packages` has full coverage). Keep the smoke tests lightweight — 2-3 test methods per framework exercising metadata-only APIs (no entitlements, no network).

### Deliverables

1. **Generate snapshots** for all 4 frameworks:
   - Run `nuke regenerate-apple-snapshot --framework WorkoutKit`
   - Run `nuke regenerate-apple-snapshot --framework RoomPlan`
   - Run `nuke regenerate-apple-snapshot --framework ProximityReader`
   - Run `nuke regenerate-apple-snapshot --framework LiveCommunicationKit`
   - Verify all 4 produce the expected artifacts in `BindingTests/obj/<Name>Snapshot/`

2. **Write smoke test source files** in `BindingTests/RuntimeTestsApp/SmokeTests/`:
   - `WorkoutKitSmokeTests.cs` — `#if WORKOUTKIT_SMOKE` gated, 2-3 metadata-only tests
   - `RoomPlanSmokeTests.cs` — `#if ROOMPLAN_SMOKE` gated, 2-3 tests
   - `ProximityReaderSmokeTests.cs` — `#if PROXIMITYREADER_SMOKE` gated, 2-3 tests (skip `MobileDocumentReaderError.errorDescription` — known emitter bug)
   - `LiveCommunicationKitSmokeTests.cs` — `#if LIVECOMMUNICATIONKIT_SMOKE` gated, 2-3 tests
   - Follow the existing `CryptoKitSmokeTests.cs` / `WeatherKitSmokeTests.cs` pattern exactly. Reference the `apple-framework-portfolio.md` doc for which APIs are safe to call.

3. **Wire into `RuntimeTestsApp.csproj`** — Add the standard 5-part block for each framework (PropertyGroup, GateCheck target, DefineConstants, ProjectReference, Import). Follow the MusicKit block as template.

4. **Wire into `Build.RuntimeTests.cs`** — Add for each framework:
   - `AbsolutePath XxxSnapshotDir` property
   - `[Parameter] readonly bool EnableXxxSmoke` field
   - Registration in `GetActiveSmokeFlags()`
   - `RegenerateAppleFrameworkSnapshot()` call in the simulator target
   - `SetProperty("EnableXxxSmoke", "true")` in the build settings

5. **OS version notes**:
   - WorkoutKit: iOS 17.0+
   - RoomPlan: iOS 17.0+
   - ProximityReader: iOS 17.4+
   - LiveCommunicationKit: iOS 26.0+ (`SupportedOSPlatformVersion=26.0` in snapshot csproj)

6. **Validation**: Run `nuke runtime-tests-simulator` with the new smoke flags enabled. Verify smoke tests pass (or are correctly gated).

---

## Session 4: Catalyst Runtime Runner

### Context
Mac Catalyst generator/binding/SDK support is fully implemented:
- `ApplePlatform.MacCatalyst` exists in the generator (`src/Swift.Bindings/src/Configuration/ApplePlatform.cs`)
- `Sdk.props` handles TFM detection (`maccatalyst` → RID `maccatalyst-arm64`, device slice `ios-arm64-maccatalyst`)
- `Sdk.targets` has full Catalyst framework resolution with iOSSupport primary/fallback paths
- Native runtime dylib exists at `src/Swift.Runtime/native/maccatalyst/libSwiftBindingsRuntime.dylib`
- Resolver fallback is unit-tested (3 tests in `SdkPropsTargetsTests.cs`)

What's missing: the Nuke build model entry, a test app project, and the Nuke target to build/run it. Catalyst apps produce macOS `.app` bundles that run directly on the host — same deployment mechanism as `RunOnMacOS`.

### Deliverables

1. **`build/Models/ApplePlatform.cs`** — Add `MacCatalyst` entry. Currently only `IOS`, `MacOS`, and `TvOS` exist. Study the existing entries to determine the right field values (Name, TfmSuffix, etc.). The `FromName` method needs to handle `"maccatalyst"`.

2. **`BindingTests/RuntimeTestsApp.MacCatalyst/` project** — Create a new csproj:
   - TFM: `net10.0-maccatalyst`
   - RID: `maccatalyst-arm64`
   - Use explicit `<Compile>` items (like Mac project, not SDK glob)
   - Reference the same shared test infrastructure and test classes as the Mac project
   - Include smoke test compile items if appropriate

3. **`Build.RuntimeTests.cs`** — Add `RuntimeTestsCatalyst` Nuke target:
   - Build xcframework with Catalyst platform
   - Regenerate bindings with `--platform maccatalyst`
   - Build the Catalyst test app
   - Inject native libraries (same pattern as macOS: `InjectMacOSNativeLibraries` or similar)
   - Codesign the app bundle
   - Run the app via `Process` (same as `RunOnMacOS`)
   - Parse JSONL results

4. **Consider**: Whether the macOS async fix from Session 2 applies to Catalyst as well (Catalyst apps are macOS processes, so the same run loop considerations apply).

5. **Validation**: Run `nuke runtime-tests-catalyst` and verify tests pass. The pass count should be close to the macOS baseline (1053/1077).
