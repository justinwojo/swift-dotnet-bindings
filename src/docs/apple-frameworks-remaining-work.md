# Apple Frameworks — Remaining Work

Tracked follow-ups from the apple-frameworks-bindingtests-plan.md sessions.

## Session A — Fix latent generator bugs *(done)*

Both generator bugs fixed, all latent-bug test pins flipped, all gates green.

- **PAT fallback IExistentialBoxable emission**: Generator now emits `IExistentialBoxable` on single-PAT conformers and populates `_protocolConformanceSymbols` with a `typeof(object)` entry. Multi-PAT conformers are guarded to fail explicitly rather than select an arbitrary witness table. Known residual: Self-requirement existential boxing through `object` parameters is untested (see roadmap.md).
- **SimpleEnum per-case @available drop**: `EnumHandler.SimpleEnum` now calls `AvailabilityAttributeEmitter` per case.
- **Tests flipped**: `PATFallbackBoundaryTests.TestReadTaggedAssociatorDispatch`, `TipKitSmokeTests.TestReadTipKitSmokeIdentifierDispatch`, `AvailabilityPropagationTests.TestStagedFeaturePerCaseAvailability`.

## Session B — macOS baseline + tvOS end-to-end unblock *(in progress)*

### tvOS end-to-end unblock *(done)*

- **`RunBuildBridge()` parameterized on `ApplePlatform`**: Added `platformOverride` parameter (same pattern as `RunBuildXcframework`, `RunRegenerateBindings`, `RunBuildAsyncWrapper`). tvOS target now calls `RunBuildBridge(platformOverride: platform)`.
- **Package.swift updated**: Added `.tvOS(.v15)` to platforms array.
- **SwiftUIBridge re-enabled in tvOS csproj**: Test files included, `SWIFTUI_BRIDGE` define conditional on generated bridge C# file, NativeReference for bridge framework added.

Gate: `nuke runtime-tests-tvos-simulator` (requires `xcodebuild -downloadPlatform tvOS`).

### macOS baseline unblock *(build pipeline done, execution blocked)*

**What's done:**
- Switched `RuntimeTestsApp.Mac.csproj` from bare `net10.0` to `net10.0-macos`, resolving the ~492 CS0246 errors from workload-dependent types.
- Added `ApplicationId`, `SupportedOSPlatformVersion`, `NativeReference` items for xcframeworks.
- Eliminated the separate `output-macos/` dir — macOS now uses the shared `output/` dir via `RunRegenerateMacOSBindings()`.
- Added missing test domain directories (`Collisions/`, `EdgeCases/`) to match tvOS.
- Updated `RunOnMacOS` to launch the native .app binary instead of `dotnet run`.
- `RunRegenerateMacOSBindings` handles dependency module bindings.

**What's blocked — macOS .app execution model:**
`net10.0-macos` produces a `.app` bundle with `Contents/MacOS/RuntimeTestsApp.Mac` (native launcher), `Contents/MonoBundle/` (.NET DLLs), and `Contents/Frameworks/` (NativeReference-embedded frameworks). The native launcher:
1. Gets killed by SIGKILL (code signature validation) even after ad-hoc codesigning
2. Disabling codesign (`EnableCodeSigning=false`) results in linker-signed binary that macOS still rejects
3. `dotnet run` exits immediately with code 0, no test output (macOS app lifecycle doesn't run console `Main()`)

**Root cause**: The macOS workload's `.app` bundle launcher isn't designed for console apps. It initializes an AppKit event loop that conflicts with our `async Task<int> Main()` entry point. Need to either:
- **(a)** Find the right property to make `net10.0-macos` produce a flat console exe instead of a `.app` bundle
- **(b)** Wrap tests in an `NSApplication` singleton so the macOS app lifecycle works
- **(c)** Use `dotnet exec` against the MonoBundle DLL with the right framework context
