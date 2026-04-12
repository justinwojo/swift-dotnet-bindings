# Apple Frameworks BindingTests Coverage Plan

**Status:** Planning  
**Date:** 2026-04-11  
**Goal:** Move our confidence in Apple Framework bindings from "compiles via `nuke validate`" to "exercised end-to-end in `nuke binding-tests` / `runtime-tests-*`," and extend the test infrastructure to cover macOS and tvOS in addition to iOS.

> **Revision note:** First draft of this document over-stated the gap. After verification it turned out the highest-risk fixes (mutating async, class-return closures, async `Optional<[URL]>`, `Optional<UUID>` metadata) already have BindingTests coverage. The plan below is the corrected, smaller-scope version.

---

## 1. Why this exists

Almost all Apple-Frameworks-related work in this repo landed after v0.7.0 — roughly 17 distinct generator/runtime fixes driven by AuthenticationServices, CryptoKit, TipKit, WeatherKit, StoreKit 2, SoundAnalysis, CoreSpotlight, MusicKit, WorkoutKit, RoomPlan, ProximityReader, and LiveCommunicationKit. Most of those fixes are exercised today only by `nuke validate`, which is a *compile gate* and doesn't run anything at runtime.

We want every Apple-Frameworks-specific code path to be reachable from BindingTests so a future regression shows up as a red runtime test rather than a customer ticket. BindingTests is the only layer that:

1. Generates real C# from real Swift,
2. Compiles both sides,
3. Runs the result on Mono JIT (iOS Simulator) **and** NativeAOT (physical iPhone), and could run on macOS / tvOS.

---

## 2. What BindingTests already covers (verified)

### 2A. Apple-Frameworks-driven fixes that are already exercised at runtime

| Fix | What's covered | Where |
|---|---|---|
| #8 — `mutating async` `__self` (`cee55a29`) | `AsyncMutatingCounter` advances state across `await` boundaries; comment explicitly names the StoreKit `AsyncIteratorProtocol` regression. | [`AsyncMethodTests.cs:159`](../../BindingTests/RuntimeTestsApp/Async/AsyncMethodTests.cs) |
| #9 — class return in closure indirect-result buffer (`f34068bd`) | Closure returning a Swift class instance; closure *property* returning a class; multi-invocation ownership. | [`ClosureEdgeCaseTests.cs:152, :164, :187`](../../BindingTests/RuntimeTestsApp/Closures/ClosureEdgeCaseTests.cs) |
| #5 — async `Optional<Container<ObjCBridgeable>>` NS-bridge (`1b7cd4f9`) | `Optional<[URL]>` Some-with-elements / Some-empty / None across an async boundary. Test region is literally labelled "Bug #5 Regression". | [`AsyncComplexTypeTests.cs:122`](../../BindingTests/RuntimeTestsApp/Async/AsyncComplexTypeTests.cs) |
| #17 — `Foundation.UUID` / `SwiftOptional<Guid>` metadata registration (`76f8a12e`) | `AppStore.DeviceVerificationID` returns `SwiftOptional<Guid>`, exercising `SBW_UUID_GetMetadata` end-to-end. | [`StoreKitSmokeTests.cs:90`](../../BindingTests/RuntimeTestsApp/SmokeTests/StoreKitSmokeTests.cs) (gated by `STOREKIT_SMOKE`) |
| #15 — Foundation `valueTypes` (`d83582f2`) | Indirectly covered by `AppleFrameworkTypeTests` — Date / CGPoint / CGSize / CGRect / CGFloat round-trips. | [`AppleFrameworkTypeTests.cs`](../../BindingTests/RuntimeTestsApp/Marshalling/AppleFrameworkTypeTests.cs) |

### 2B. The single existing real-Apple-framework smoke test

`StoreKitSmokeTests.cs` is gated by the `STOREKIT_SMOKE` compile symbol and currently asserts three calls against the real StoreKit 2 framework:

1. `AppStore.CanMakePayments` — primitive-bool resolver smoke test (Session 5).
2. `AppStore.DeviceVerificationID` — `SwiftOptional<Guid>` UUID metadata path (covers fix #17).
3. `Transaction.Unfinished` async-iterator enumeration with three passes (early-terminate, full empty-complete, amplified per-loop memory delta). Picked over `Transaction.updates` because the generator currently emits `Transaction.updates` as an orphan `[LibraryImport]` with no surrounding wrapper — a *separate, still-open generator bug*.

This is the model to generalize, but we should be honest about what it currently asserts vs. what the previous draft of this doc claimed it asserts.

### 2C. Platforms exercised today

| Platform | Runner | Notes |
|---|---|---|
| iOS Simulator (Mono JIT) | `nuke runtime-tests-simulator` | Default gate. |
| iOS Device (NativeAOT) | `nuke runtime-tests-device` | For calling-convention / marshalling / finalizer-order changes. |
| macOS (.NET 10 console) | `nuke runtime-tests-macos` | Portable subset via `RuntimeTestsApp.Mac.csproj`. Tests under `Marshalling/`, `Closures/`, `Async/`, `Protocols/`, `Generics/`, `Patterns/`, `Operators/`, `Lifetime/`, `Metadata/`, `ErrorHandling/`, `Concurrency/`, `Spike/`, `SwiftUIBridge/` are picked up automatically by glob. **`SmokeTests/` is *not* in the glob list.** |
| Mac Catalyst | (none) | The 0.8.0 SDK packs Catalyst slices for SoundAnalysis/CoreSpotlight/etc. (per [`0.8.0.md:15,24`](0.8.0.md)), but there is no Catalyst runtime test runner. |
| tvOS Simulator | (none) | Xcframework build path supports tvOS, runtime runner does not exist. |
| tvOS Device | (none) | Same. |

### 2D. Notable open generator bugs visible from the existing smoke test

These are not gaps in BindingTests — they are gaps in the generator that BindingTests already pins via the existing smoke test's choice of API:

- `Transaction.updates`, `Storefront.updates`, `Product.SubscriptionInfo.Status.updates` — orphan `[LibraryImport]` declarations with no wrapper or public property emitted. The smoke test routes around this with `Transaction.Unfinished`, but the underlying bug remains.
- `MobileDocumentReaderError.errorDescription` (ProximityReader) — `LocalizedError` extension emitted on the C# side without a matching `@_cdecl` on the Swift side. Asymmetric emission for nested vs. top-level enums.

Both are tracked elsewhere; calling them out so the test plan doesn't try to "cover" them as if the current behavior were correct.

---

## 3. Real coverage gaps (what's left)

After accounting for what already exists, the remaining Apple-Frameworks-driven fixes without BindingTests runtime coverage are:

| # | Fix | What's missing | Risk |
|---|---|---|---|
| 1 | Property-accessor `@available` propagation (`b51d2ff6`) | No synthetic types with `@available(iOS X, *)` properties returning `@available(iOS Y, *)` types in `SwiftBindingsTestLib`. The unit-test layer covers parser propagation; BindingTests doesn't compile-gate the resulting CA1416-clean wrappers. | Medium — failure mode is consumer-project compile warnings, not crashes. |
| 2 | `@available` on `@_cdecl` wrappers / async accessors / per-enum-case (`fcd0ca9c`, `eafe252d`, `52ddafa4`) | Same gap — no synthetic enum with per-case `@available`, no `@available` async accessor. | Medium. |
| 4 | Simple-enum `Optional<String>` / `LocalizedError` extension wrapping (`4235d568`) | Unit tests cover the emitter output. Nothing throws and catches at runtime. | Medium-high — runtime failure would mean caught error has wrong message or `null` where text expected. |
| 6 | Opaque-type parameter lowering (`some P` at parameter position) (`2c80b227`) | No fixture takes `some Codable` at parameter position. | Low — narrow surface. |
| 7 | PAT / `Self`-requirement protocol fallback to `object` (`4235d568`) | The interesting boundary is "method *takes* `any TipWithAssociated` as a parameter". Existing existential tests don't exercise the PAT fallback specifically. | Medium — the failure mode is wrong dispatch or thrown cast. |
| 10 | Nested-type / method name collision detection (`26f764f1`) | No fixture has a struct/enum where a nested type and a method share a name. | Low — failure is `CS0102` at compile time. |
| 11 | `UnsafeRawBufferPointer` parameter deferral (`26f764f1`) | The right assertion is "the method appears in `binding-report.json` with skip reason `UnsupportedSignature` AND the rest of the type still compiles." Today nothing exercises that. | Low — but cheap to add and protects against the deferral being silently dropped. |
| 12 | `@_silgen_name` extension wrapper `@available` inheritance (`26f764f1`, `52ddafa4`) | No `@available`-gated extension method on a synthetic type. | Medium. |
| 13 | `Optional<Swift-struct>` AutoBridge handling (`26f764f1`) | No fixture exercises `Optional<T>` where `T` is misclassified as ObjC under AutoBridge. The generator-side fix exists but the runtime path isn't pinned. | Medium. |
| 14 | Mac Catalyst framework path fallback (`63916020`) | Catalyst slices ship in NuGet packs but there is **no runtime runner** for Catalyst. The fallback is exercised only by `nuke validate`. | Low — fallback is in resolver code, easy to unit-test. |
| 16 | `--apple-framework` direct-mode + multi-TFM csproj (`44724887`, `2c80b227`, `a300ef4d`) | Only the StoreKit smoke test exercises this end-to-end. | Medium — generalizing the smoke-test pattern to other Apple frameworks closes this. |

### 3A. Real-Apple-framework runtime coverage we don't have at all

| Framework | What we have | What we don't |
|---|---|---|
| StoreKit 2 | `STOREKIT_SMOKE` smoke tests for resolver, UUID metadata, async-iterator lifecycle. | `Transaction.updates` (blocked on orphan P/Invoke bug); `getEligibleURLs() async` (the true `Optional<[URL]>` over a real Apple symbol — currently only synthetic coverage). |
| CryptoKit | None at runtime. | A hermetic, metadata-only smoke. **Avoid the `SHA256/SHA3` hashing path** — that hits the very `UnsafeRawBufferPointer` parameters that fix #11 *intentionally skips*. Pick something like `SymmetricKey(size: .bits256).bitCount` or `P256.Signing.PrivateKey()` which are pure metadata. |
| WeatherKit | None at runtime. | Property accessor `@available` propagation (fix #1). The actual weather-fetching APIs need entitlements + network and should *not* be in the default gate. A hermetic smoke would touch error-type metadata only. |
| TipKit | None at runtime. | The `Tip` PAT-fallback path (fix #7) at the boundary that takes a `some Tip`. Hermetic — no service dependencies. |
| MusicKit | `MusicItemID` string round-trip (direct-mode pipeline) + reflection pin on `Song.AudioVariants` (ios15 type / ios16 property) for fix #2. | Per-property `@available` propagation (fix #2). Entitlement-gated APIs (`MusicAuthorization`, `MusicCatalog.search`) are explicitly excluded. |
| WorkoutKit / RoomPlan / ProximityReader / LiveCommunicationKit | None at runtime. | All Tier-A candidates. Defer until after StoreKit 2 / CryptoKit / WeatherKit / TipKit smoke tests are stable, since ARKit/HealthKit/etc. carry the heaviest entitlement and device-class requirements. |

Hermetic = no entitlements, no service dependencies, no sandbox/configuration prerequisites. The smoke tests should fail loudly when the prerequisite framework is unavailable, **never** silently skip — that matches the existing StoreKit smoke gating model.

---

## 4. What to add

Each item below is scoped, named, and placed in an existing domain folder. **No new top-level `AppleFrameworks/` source folder** — the repo convention is to organize by domain, not by milestone or framework. The doc itself is the index that ties them together.

### 4A. Synthetic Swift fixtures — placed by domain

| Fixture | Location | Exercises | Notes |
|---|---|---|---|
| Availability-on-accessors fixture | `Sources/SwiftBindingsTestLib/EdgeCases/AvailabilityPropagation.swift` | Fixes #1, #2, #12 | Type with `@available(iOS 16, *)` properties returning `@available(iOS 18, *)` types; per-case `@available` enum; `@_silgen_name` extension wrapper on a `@available`-gated extension. |
| Opaque-param fixture | `Sources/SwiftBindingsTestLib/Closures/OpaqueParamClosures.swift` (or `Generics/`) | Fix #6 | Method taking `some Codable` at parameter position. |
| PAT-fallback fixture | `Sources/SwiftBindingsTestLib/Protocols/PATFallbackBoundary.swift` | Fix #7 | Protocol `P` with `associatedtype Item`, plus a method that takes `any P` (or generic `<T: P>`) as a *parameter*. The C# side should fall back to `object` and the round-trip should preserve identity. |
| LocalizedError throw/catch fixture | `Sources/SwiftBindingsTestLib/ErrorHandling/SimpleEnumLocalizedError.swift` | Fix #4 (runtime path) | A **true simple enum** — payloadless cases only, e.g. `enum DemoLocalizedError: Error { case missing; case truncated }` — plus `extension DemoLocalizedError: LocalizedError` whose `errorDescription` returns a per-case `Optional<String>`. **No associated values**: even one payload case (`case truncated(reason: String)`) reclassifies the enum out of the simple-enum codepath and misses fix #4 entirely. The payloadless case is what makes the throw/catch runtime test possible *and* keeps the fixture on the right emission path. |
| Caseless-namespace LocalizedError fixture (compile-only) | `Sources/SwiftBindingsTestLib/ErrorHandling/CaselessNamespaceLocalizedError.swift` | Fix #4 (emission path) | A caseless namespace enum (`enum WeatherErrorNamespace { static let missing = "..." }`) with `extension WeatherErrorNamespace: LocalizedError`. **No instance can be thrown** — this is the WeatherKit-shaped emission path, exercised only at compile/emit time. |
| Nested-type collision fixture | `Sources/SwiftBindingsTestLib/Collisions/NestedTypeMethodCollision.swift` | Fix #10 | Type with both a nested type and a method sharing a name. |
| UnsafeRawBufferPointer fixture | `Sources/SwiftBindingsTestLib/UnsafeTypes/UnsafeRawBufferParam.swift` | Fix #11 | Type with one `UnsafeRawBufferPointer` parameter method *and* one normal method on the same type. The skipped method must not break the rest of the type. |
| Optional AutoBridge struct fixture | `Sources/SwiftBindingsTestLib/Optionals/OptionalAutoBridgeStruct.swift` | Fix #13 | A struct registered as AutoBridge in the test type DB, exercised through `Optional<T>`. |

### 4B. C# runtime tests — also placed by domain

| Test class | Fixture | Key assertion |
|---|---|---|
| `EdgeCases/AvailabilityPropagationTests.cs` | AvailabilityPropagation.swift | Compiles without `CA1416`; runtime call works on simulator. The compile-clean assertion is what matters; runtime behavior is incidental. |
| `Closures/OpaqueParamTests.cs` | OpaqueParamClosures.swift | Pass-through value comes back unchanged. |
| `Protocols/PATFallbackBoundaryTests.cs` | PATFallbackBoundary.swift | Pass `object` through the existential boundary; assert dispatched method on the underlying type ran. This is the assertion that proves "fallback to `object`" actually works at the parameter position, not just at the type-checker boundary. |
| `ErrorHandling/SimpleEnumLocalizedErrorTests.cs` | SimpleEnumLocalizedError.swift (the *payloadless-cases* fixture) | Call a Swift function that throws `DemoLocalizedError.missing`, catch on the C# side, read `localizedDescription` — assert non-null and equal to the expected string. **Runtime coverage** for the throw/catch path. The caseless-namespace fixture is covered separately by emission/compile assertions only — it has no throwable instance, so there's no runtime test for it. |
| `Collisions/NestedTypeMethodCollisionTests.cs` | NestedTypeMethodCollision.swift | Both the nested type and the method are reachable and callable. Compile-only would be enough but a one-line invocation costs nothing. |
| `EdgeCases/UnsafeRawBufferDeferralTests.cs` | UnsafeRawBufferParam.swift | Runtime test: call the *unrelated* normal method on the same type, asserting the type itself is intact even though one of its members was deferred. **The "skip reason is `UnsupportedSignature`" assertion is *not* a runtime test** — it should be a Nuke step (or compile-check target) that reads `output/binding-report.json` from the host filesystem. Reading the report from inside the iOS Simulator/device test process is fragile because the file isn't bundled into the app's resources today; bundling it just to make this assertion would be the wrong tradeoff. Two-layer coverage: build-side (Nuke parses report, asserts skip reason) + runtime-side (rest of the type still callable). |
| `Marshalling/OptionalAutoBridgeTests.cs` | OptionalAutoBridgeStruct.swift | Round-trip `nil` and `.some(T)`. |

### 4C. Real-Apple-framework smoke tests — generalizing the StoreKit pattern

Each smoke test lives under `BindingTests/RuntimeTestsApp/SmokeTests/` and is gated by a per-framework compile symbol (`CRYPTOKIT_SMOKE`, `WEATHERKIT_SMOKE`, `TIPKIT_SMOKE`, …) following the StoreKit precedent. None of them should be on by default in `nuke runtime-tests-simulator` until the per-framework prerequisites are well-understood.

The hermetic call choices below are **candidates**, not committed API selections. The actual call set for each framework should be locked in *after* regenerating the snapshot and confirming which members are emitted, hermetic, and don't trip a gating bug. Treat the table as "what we're aiming at" and adjust as snapshots reveal reality.

| Smoke test | Symbol | Candidate hermetic call(s) | What it pins |
|---|---|---|---|
| `CryptoKitSmokeTests.cs` | `CRYPTOKIT_SMOKE` | *Candidates:* `SymmetricKey(size: .bits256).bitCount`; `Curve25519.Signing.PrivateKey()` round-trip. **Avoid `SHA256.hash(data:)`** — `Data` parameter routes through `UnsafeRawBufferPointer` paths that fix #11 deliberately skips. Final selection depends on which CryptoKit members the snapshot actually emits. | End-to-end Apple-framework direct-mode pipeline (fix #16) on a non-StoreKit framework. |
| `WeatherKitSmokeTests.cs` | `WEATHERKIT_SMOKE` | *Candidates:* construct a `WeatherError` value and read `errorDescription`; touch a property whose accessor was iOS-version-gated by fix #1. **No network calls, no `WeatherKit.entitlement`.** Confirm against the snapshot before locking in. | Property-accessor `@available` propagation (fix #1) at the level that consumer projects actually compile against. |
| `TipKitSmokeTests.cs` | `TIPKIT_SMOKE` | *Candidates:* a synthetic Swift `Tip`-conforming type passed through a method whose parameter is typed as `any Tip`. **The conformer Swift type lives behind `#if TIPKIT_SMOKE` in a smoke-only fixture file**, not in the default `SwiftBindingsTestLib` build — see Section 4E. | PAT fallback (fix #7) on a real Apple framework, not just on synthetic protocols. |
| `MusicKitSmokeTests.cs` | `MUSICKIT_SMOKE` | *Locked call set:* `MusicItemID(rawValue)` string round-trip through `RawValue` (exercises the end-to-end direct-mode pipeline), plus a reflection-only assertion that `Song.AudioVariants` carries `[SupportedOSPlatform("ios16.0")]` — `Song` is `ios15.0`, the property was added in iOS 16, so this is the canonical 1-version mismatch for fix #2. **No `MusicAuthorization.request()`, no `MusicCatalog.search(...)`, no network.** | Per-property `@available` propagation (fix #2) on a Tier-A framework. |

For each smoke test:

1. **Loud failure on missing prerequisite — at build time, not runtime.** Follow the existing StoreKit pattern at [`RuntimeTestsApp.csproj:189-201`](../../BindingTests/RuntimeTestsApp/RuntimeTestsApp.csproj): a `<Target Name="_<Framework>SmokeGateCheck" BeforeTargets="BeforeBuild">` with explicit `<Error>` elements for each missing prerequisite (snapshot csproj missing, xcframework slice missing, `ProjectReference.targets` missing, wrong RID, …). **Do not** rely on `Skip` at runtime — runtime skips are easy to miss in CI logs, and the existing StoreKit gate explicitly justifies the build-time pattern: "silently dropping the test defeats the opt-in safety net."
2. **No `--enable-apple-smoke` umbrella flag** until the per-framework tests have all been stable for a release cycle. Each one stays opt-in individually via its own `Enable<Framework>Smoke` MSBuild property + `<Framework>_SMOKE` compile symbol.
3. **Snapshots stay under `obj/` and stay gitignored**, matching the existing StoreKit pattern. Don't try to commit snapshots and "fail on diff" — that conflates "Apple shipped a new SDK" with "we broke the binding." If we want Apple-SDK API drift review, that's a separate tracked summary report (e.g., a markdown file under `src/docs/` regenerated by a Nuke target), not a diff against the gitignored generated source.

### 4D. Coverage of fix #14 (Mac Catalyst framework path fallback)

Catalyst is in the build matrix — `0.8.0.md` confirms multi-TFM packs ship for iOS, macOS, Mac Catalyst, and tvOS, and SoundAnalysis/CoreSpotlight build + pack across all four. What's missing is a *runtime runner*.

Two layers, do both:

1. **Unit-test the resolver fallback** in `src/Swift.Bindings/tests/UnitTests/`. Assert that `SwiftFrameworkResolver` (or whichever class owns Catalyst path resolution) tries `iOSSupport/` first and falls back to the regular macOS framework path. This is the cheap, deterministic gate.
2. **Defer a real Catalyst runtime runner** until there is customer demand. Standing up a Catalyst host involves a separate `RuntimeTestsApp.Catalyst.csproj`, Catalyst-specific code-signing, and a deployment story. The cost is high and the marginal coverage over the unit test is small.

### 4E. Smoke-only Swift fixtures

A few of the smoke tests need Swift-side helper code (e.g., a `TipKit.Tip`-conforming type for the TipKit smoke). **These must not be unconditionally part of the default `SwiftBindingsTestLib` xcframework build**, otherwise every BindingTests run would gain a hard build dependency on the corresponding Apple framework being available on the build host's selected SDK — which defeats the opt-in model.

Two viable mechanisms, pick one and apply it consistently:

1. **Conditional compilation guards in the source files.** Wrap the helper in `#if TIPKIT_SMOKE` (matching the C# smoke gate symbol). The Nuke `build-xcframework` target passes the symbol via `-D` only when the corresponding `Enable<Framework>Smoke=true` is set. Same compile gate on both sides of the bridge.
2. **Per-smoke Swift sources excluded from the default `Package.swift` `sources:` list.** A separate `SmokeFixtures/<Framework>` subdirectory whose files are added to the Swift target *only* when the smoke flag is on. Closer to how `Closures/Autoclosures.swift` and the other "excluded" files are handled today, but requires a Nuke-side mutation of the source list per smoke flag.

Recommendation: option 1 (conditional compilation). It keeps the file list stable, matches the C# `#if STOREKIT_SMOKE` pattern, and means the same `Enable<Framework>Smoke=true` flag is the single switch for both Swift and C# sides.

### 4F. Build-system work that the smoke pattern requires

The smoke gating story above describes the *intent*. Several pieces of the build system don't currently support it and need explicit implementation work — they should be tracked as their own P0/P1 line items, not assumed to "just work."

1. **Thread Swift `-D` defines through the xcframework build — *both* the dylib compile and the ABI JSON dump.** `CompileModuleSlice` builds two separate Swift toolchain invocations and *both* need the same defines or the smoke surface will be inconsistent:
   - **Dylib compile** at [`Build.BindingTests.cs:102`](../../build/Build.BindingTests.cs) — `SwiftCompilerSettings` chain (`SetTarget` / `SetSdk` / `SetEmitModule` / …) has no path for conditional `-D` flags. Without this, `#if TIPKIT_SMOKE` in a Swift fixture file is a no-op and the helper symbol never lands in the dylib.
   - **ABI JSON generation** at [`Build.BindingTests.cs:135`](../../build/Build.BindingTests.cs) — `SwiftFrontendSettings` builds the `.abi.json` from the swiftinterface in a *separate* invocation that the binding generator then consumes. If the dylib gets `-D TIPKIT_SMOKE` but the frontend does not, the helper exists in the binary but is invisible to the generator's view of the module — wrappers won't be emitted, and the C# smoke test won't compile because the symbol it tries to call doesn't exist on the C# side. The two views *must* match.
   
   Implementation: add an `IReadOnlyList<string> swiftDefines` parameter to `CompileModuleSlice`, plumb a corresponding setting through *both* `SwiftCompilerSettings` *and* `SwiftFrontendSettings` (`-D` is supported by both `swiftc` and `swift-frontend`), and have `RunBuildXcframework` populate it from the active `Enable<Framework>Smoke` flags. The same plumbing must reach `BuildDeviceSlices`, the macOS slice path, and the tvOS slice path, otherwise `--include-device` and `nuke runtime-tests-macos` will silently drop the smoke fixture. (Symbol-graph extraction would also benefit from the same defines if we ever start consuming symbol graphs for docs, but ABI JSON is the load-bearing one for the binding generator.)
2. **Generalize the `--skip-build + --enable-storekit-smoke` rejection.** [`Build.RuntimeTests.cs:507`](../../build/Build.RuntimeTests.cs) currently rejects `SkipBuild && EnableStoreKitSmoke` with a hard-coded check. The same rejection must apply for **every** `Enable<Framework>Smoke` we add — otherwise a user who passes `--skip-build --enable-cryptokit-smoke` runs the previous session's app bundle against the current snapshot, reproducing the exact stale-AOT footgun the StoreKit guard was added to prevent. Implementation: collect the enabled smoke flags into a list and reject the combination in a single check. Same applies wherever `SkipBuild` is honored.
3. **Make staleness detection aware of smoke flags.** [`Build.RuntimeTests.cs:1415`](../../build/Build.RuntimeTests.cs) — `AssertBindingsNotStale` only compares Swift source mtimes against the generated `.cs`. It does **not** track which `Enable<Framework>Smoke` flags were active when the bindings were last regenerated, so `--skip-regen + EnableTipKitSmoke=true` after a previous non-smoke run will happily reuse a Swift xcframework that was compiled *without* the `TIPKIT_SMOKE` define — and the smoke fixture will be missing from the dylib. Implementation: stamp the active smoke-flag set into a sidecar file alongside the generated bindings (e.g., `output/.smoke-flags`), and have `AssertBindingsNotStale` reject `--skip-regen` when the current flag set differs from the stamped one. Loud failure with "the smoke flag set has changed; rerun without --skip-regen" is the right shape — same loud-failure principle as the build-time `<Error>` gate.

These three items are the difference between "the smoke pattern is documented" and "the smoke pattern works." They block items P0 #2/#3 in Section 6.

---

## 5. Platform expansion: macOS and tvOS

### 5A. macOS — already mostly done

`RuntimeTestsApp.Mac.csproj` exists and `nuke runtime-tests-macos` works. Critically, the project picks up tests by glob across these directories (verified in [`RuntimeTestsApp.Mac.csproj:45-58`](../../BindingTests/RuntimeTestsApp.Mac/RuntimeTestsApp.Mac.csproj)):

```
Lifetime/  Marshalling/  Metadata/  Closures/  ErrorHandling/  Generics/
Operators/  Patterns/  Protocols/  Async/  Concurrency/  Spike/  SwiftUIBridge/
```

This means **the synthetic test classes from Section 4B above will be picked up automatically by macOS** as long as they live in those domain folders — which the table places them in. We don't need to touch `RuntimeTestsApp.Mac.csproj` for any of the synthetic Section 4B work.

What macOS *does* need:

1. **Smoke test wiring.** `SmokeTests/` is **not** in the macOS glob list. The right shape is per-framework conditional `<Compile>` lines mirroring the iOS pattern: `<Compile Include="../RuntimeTestsApp/SmokeTests/CryptoKitSmokeTests.cs" Condition="'$(EnableCryptoKitSmoke)'=='true'" />`, plus a copy of the build-time `_<Framework>SmokeGateCheck` target adapted for macOS RID and snapshot paths. Per-framework conditional inclusion is consistent with how `_StoreKitSmokeGateCheck` already gates on `iossimulator-arm64` — the macOS gate would gate on `osx-arm64` and the macOS snapshot path.
2. **macOS framework-availability handling.** WeatherKit and MusicKit have macOS variants with different minimum-OS requirements; CryptoKit is fully cross-platform. **There is no `SkipOnMacOS` attribute today** — `RuntimeTestsApp.Mac/Program.cs:18` reports `TestPlatform.Simulator` and the only platform-skip attributes in [`TestResults.cs:282, :303`](../../BindingTests/RuntimeTestsApp/Infrastructure/TestResults.cs) are `SkipOnSimulator` / `SkipOnDevice`. Two options:
   - **Compile-time exclusion (preferred):** if a smoke test isn't supposed to run on macOS, don't add its `<Compile Include="..." />` line to `RuntimeTestsApp.Mac.csproj` at all. Cleanest, no new attribute, mirrors how `SmokeTests/` is excluded today.
   - **Add a real macOS platform attribute:** introduce `TestPlatform.MacOS` + `SkipOnMacOSAttribute`, and have `RuntimeTestsApp.Mac/Program.cs:18` set `Platform = TestPlatform.MacOS` instead of aliasing to `Simulator`. This is a larger change but it removes the "macOS pretends to be Simulator" foot-gun for any test that ever needs to distinguish them. Worth doing if more than one or two smoke tests need per-platform skips.
   Until that decision is made, prefer compile-time exclusion and don't write `[SkipOnMacOS]` in any new test — it doesn't exist.
3. **One-time audit of macOS test results.** Run `nuke runtime-tests-macos` against the new fixtures and chase any platform-specific failures. Most should "just work" since the existing macOS gate already runs ~all of `Marshalling/`, `Closures/`, `Async/`.

### 5B. tvOS — needs a new runner

The xcframework build infra supports tvOS (`nuke build-xcframework --platform tvos --include-device`), and the SDK packs tvOS slices for SoundAnalysis/CoreSpotlight per `0.8.0.md`. What's missing is a runtime runner.

To stand one up:

1. **New project**: `BindingTests/RuntimeTestsApp.tvOS/RuntimeTestsApp.tvOS.csproj`. Copy `RuntimeTestsApp.csproj`, change `<TargetFramework>` to `net10.0-tvos`, set `<RuntimeIdentifier>tvossimulator-arm64</RuntimeIdentifier>`. Reuse the same `<Compile>` globs as iOS, minus anything UIKit-only that lacks a TVUIKit equivalent.
2. **New Nuke target**: `RuntimeTestsTvOSSimulator` in `Build.RuntimeTests.cs`. Mirror of `RuntimeTestsSimulator`. Uses `xcrun simctl` against an Apple TV device type. Same xUnit harness pipe.
3. **Wrapper framework**: confirm `BuildAsyncWrapper`'s tvOS path emits `arm64-apple-tvos-simulator` slice into the right `.build/` directory; today it's exercised by `nuke build-xcframework --platform tvos` but has no consumer.
4. **Defer device runner**. tvOS device deployment requires provisioning + a physical Apple TV — high friction, low marginal value over the simulator runner. Skip until requested.

Cost estimate: ~1 day for the simulator runner. Main risk is `simctl` differences for tvOS (fewer device types, slightly different launch behavior).

### 5C. Test matrix after expansion

| Test | iOS Sim | iOS Device | macOS | tvOS Sim | Catalyst |
|---|---|---|---|---|---|
| Existing language-construct tests (Marshalling, Closures, Async, …) | ✅ | ✅ | ✅ via globs | ✅ once 5B lands | — |
| Section 4B synthetic Apple-Frameworks fixtures | ✅ | ✅ | ✅ via globs | ✅ once 5B lands | — |
| Section 4C real Apple smoke tests | per-flag opt-in | per-flag opt-in | requires 5A wiring | per-flag opt-in once 5B lands | — |
| Resolver path fallback (fix #14) | unit test only | unit test only | unit test only | unit test only | covered by unit test |

The "must run on device + macOS" cases are the ones where Mono JIT and NativeAOT can diverge: anything touching calling conventions, struct marshalling, finalizer order, or P/Invoke signatures. The Section 4C smoke tests qualify — they go through the full Apple-framework direct-mode pipeline.

---

## 6. Prioritization

### P0 — high-value, must-do-first, blocks the smoke story
1. **Build-system smoke prerequisites (Section 4F).** Thread `-D` defines through `CompileModuleSlice`, generalize the `--skip-build + Enable<Framework>Smoke` rejection, and stamp the smoke-flag set into staleness detection. Without these three pieces of plumbing, items 2 and 3 below cannot work — `#if CRYPTOKIT_SMOKE` would be a no-op and `--skip-regen` would silently reuse stale dylibs.
2. **Generalize the StoreKit smoke-test pattern into a `nuke regen-apple-snapshot --framework <name>` Nuke target.** The current StoreKit snapshot is bespoke; a reusable target unblocks every other smoke test.
3. **CryptoKit smoke test** (Section 4C) — pick the *non*-`UnsafeRawBufferPointer` paths only. Highest-value second smoke test because CryptoKit is already a published Tier-A package and has zero entitlement requirements. Lock in the actual call set after the snapshot is regenerated.
4. **WeatherKit hermetic smoke test** for property-accessor `@available` propagation (Section 4C, fix #1). No network calls. Lock in the call set after the snapshot is regenerated.
5. **`SimpleEnumLocalizedErrorTests` (Section 4B)** for fix #4. Currently unit-test-only; the runtime path matters for any `LocalizedError` consumer.

### P1 — close the remaining synthetic gaps
6. `AvailabilityPropagationTests` + the EdgeCases availability fixture (fixes #1, #2, #12).
7. `PATFallbackBoundaryTests` at the parameter position (fix #7).
8. `UnsafeRawBufferDeferralTests` — **two-layer**: a Nuke build-side step that reads `output/binding-report.json` from the host filesystem and asserts the deferred method's skip reason is `UnsupportedSignature`, plus a runtime test on the same type that calls the *unrelated* normal method to prove the type is intact. Reading the binding report from inside the iOS test process is *not* part of the runtime test — see Section 4B.
9. Caseless-namespace LocalizedError emission/compile assertion (Section 4A second row). Compile-only — there's no throwable instance. Pair with item 5 above.
10. `OptionalAutoBridgeTests` (fix #13).
11. `NestedTypeCollisionTests` (fix #10).
12. `OpaqueParamTests` (fix #6).
13. **macOS smoke wiring** (Section 5A.1 + 5A.2). After P0 #3 and #4 land, add the conditional `<Compile>` lines so CryptoKit and WeatherKit smokes also run on `nuke runtime-tests-macos`.

### P2 — platform and additional frameworks
14. **TipKit smoke test** (Section 4C, fix #7 on a real framework).
15. **MusicKit smoke test** (Section 4C, fix #2 on a real framework, metadata-only).
16. **tvOS runner** (Section 5B).
17. **Catalyst resolver unit test** (Section 4D.1).

### P3 — defer
18. Real Catalyst runtime runner.
19. Smoke tests for WorkoutKit / RoomPlan / ProximityReader / LiveCommunicationKit.
20. tvOS device runner.

---

## 7. Open questions

- **Snapshot regeneration policy.** When Apple ships a new Xcode, the gitignored snapshots under `obj/<Framework>Snapshot/` will diverge. Do we want a separate `nuke check-apple-snapshot-drift` target that produces a tracked summary report under `src/docs/`, distinct from the snapshot itself? The previous draft conflated these and proposed "fail on diff and commit" — that's wrong because the snapshots aren't tracked.
- **Where do per-framework `*_SMOKE` compile symbols get set?** The StoreKit pattern sets `STOREKIT_SMOKE` from `RuntimeTestsApp.csproj` based on a file existence check. For a uniform pattern across N frameworks, a single `.props` file enumerated by framework name would scale better than N inline conditions.
- **macOS smoke test gating split.** On macOS, do we want one umbrella `EnableMacOSSmokeTests` switch or one per framework? Per-framework matches the iOS pattern and is more honest about which tests are stable on which platform.

---

## 8. Estimated effort

| Phase | Work | Estimate |
|---|---|---|
| P0 (5 items) | **Build-system smoke prerequisites (Section 4F)**, reusable Nuke regen target, CryptoKit + WeatherKit smokes, LocalizedError runtime test | ~3 days (the build-system plumbing is the bulk of the new work) |
| P1 (8 items) | Synthetic fixtures + tests for fixes #1/2/4-emission/6/7/10/11/12/13, plus macOS smoke wiring | ~2.5 days |
| P2 (4 items) | TipKit + MusicKit smokes, tvOS simulator runner, Catalyst resolver unit test | ~2 days |
| P3 | Deferred | — |
| **Total** | | **~7.5 days of focused work** |

The shape of the work hasn't changed much from the first draft of this doc, but the priorities are different: most of the effort is now in **generalizing the StoreKit smoke pattern and adding the next two real-Apple-framework smokes** (P0), not in re-covering the high-risk fixes (#5, #8, #9, #17) that already have BindingTests coverage.

---

## 9. Session plan

A "session" is one fresh Claude Code context window from kickoff to commit. Sessions are bounded by context budget and gate-run length, not by clock time — each one should end with passing gates (per the zero-regression policy in `CLAUDE.md`) and a single focused commit.

**Realistic minimum: 8 sessions. Safer estimate: 9.** Smoke tests and the build-infra plumbing each need their own session because they involve snapshot iteration, gate runs, and locking the call set; batching them risks context bloat or hiding a regression inside a sprawling diff. P3 work is explicitly deferred.

### P0 — 3 sessions

1. **Build infrastructure.** ✅ **COMPLETE** — commit `73a22676`. All of Section 4F (thread `-D` defines through `CompileModuleSlice` for *both* the dylib compile and the ABI JSON dump; generalize the `SkipBuild + Enable<Framework>Smoke` rejection at [`Build.RuntimeTests.cs:507`](../../build/Build.RuntimeTests.cs); stamp the active smoke-flag set into `AssertBindingsNotStale` at [`Build.RuntimeTests.cs:1415`](../../build/Build.RuntimeTests.cs)) **plus** the reusable `nuke regen-apple-snapshot --framework <name>` target from P0 #2. One cohesive build-infra commit. Blocks every smoke test.
2. **CryptoKit smoke test.** ✅ **COMPLETE** — commit `2b7df631`. Snapshot regen, lock call set (avoid `UnsafeRawBufferPointer` paths — `SymmetricKey(size:).bitCount` / `Curve25519.Signing.PrivateKey()` are the candidates), `CRYPTOKIT_SMOKE` gate symbol, build-time `_CryptoKitSmokeGateCheck` target with explicit `<Error>` elements per the StoreKit pattern at [`RuntimeTestsApp.csproj:189-201`](../../BindingTests/RuntimeTestsApp/RuntimeTestsApp.csproj).
3. **WeatherKit smoke test + LocalizedError runtime.** ⚠️ **PARTIAL** — commit `e6815626` shipped the **iOS slice only**: WeatherKit hermetic smoke + SimpleEnumLocalizedError runtime coverage (P0 #5). **macOS smoke wiring was deferred** after discovering that `nuke runtime-tests-macos` has a pre-existing baseline failure unrelated to this plan. Root cause: `BindingTests/Sources/SwiftBindingsTestLib/Foundation/URLRequestTestHelper.swift` (commit `d27c7e03`, long before Session 3) drags `Foundation.NSUrlRequest` / `ObjCRuntime.Selector` references into the generated `SwiftBindingsTestLib.cs`, but `RuntimeTestsApp.Mac.csproj` targets plain `net10.0` with no Microsoft.iOS/macOS workload — ~492 CS0246 errors from the baseline alone. **Section 5A below ("macOS — already mostly done") is stale** and needs revision before macOS smoke wiring can resume. See new Session **3.5** for the re-scoped macOS work.
3.5. **macOS baseline unblock + smoke wiring (NEW, re-scoped from Session 3).** Two halves: (a) **Fix the baseline.** Either switch `RuntimeTestsApp.Mac.csproj` to `net10.0-macos` (workload TFM — check whether this re-opens the cross-TFM ProjectReference problem noted in the existing csproj comment), or filter workload-dependent types out of `--platform macos` generator output, or exclude `URLRequestTestHelper.swift` from the macOS slice. Pick after investigation. Target: `nuke runtime-tests-macos` green on main with zero smoke flags set. (b) **Wire the smokes.** Once (a) is green, add per-framework conditional `<Compile>` lines for CryptoKit + WeatherKit in `RuntimeTestsApp.Mac.csproj` + mirrored gate-check targets for `osx-arm64`, and verify `RunRegenerateMacOSBindings` stamps `.smoke-flags`. Blocks any future session that needs macOS-only runtime coverage.

### P1 — 2 sessions

4. **Small synthetic-fixture batch.** ✅ **COMPLETE** — commit `feb97a4e`. Five small Section 4A/4B pairs that all run through the same `nuke binding-tests` gate: AvailabilityPropagation (fixes #1, #2, #12), SimpleEnumLocalizedError runtime + caseless-namespace compile-only (fix #4), NestedTypeMethodCollision (fix #10), OptionalAutoBridgeStruct (fix #13), OpaqueParamClosures (fix #6). Each is a single Swift fixture file plus a single C# test class — small enough to batch into one session.
5. **PAT fallback + UnsafeRawBufferDeferral.** ✅ **COMPLETE** — commit `a60de4da`. Latent runtime-dispatch bug for PAT conformers (missing `IExistentialBoxable` emission) pinned with a flip-when-fixed comment in `TestReadTaggedAssociatorDispatchLatentBug`. PATFallbackBoundary at parameter position (fix #7) **plus** the two-layer UnsafeRawBufferDeferral (fix #11): a Nuke build-side step that reads `output/binding-report.json` from the host filesystem and asserts the deferred method's skip reason is `UnsupportedSignature`, plus a runtime test on the same type that calls the *unrelated* normal method to prove the type is intact. Both are more involved than the Session 4 batch — PAT fallback because of dispatch verification, UnsafeRawBuffer because of the new Nuke target.

### P2 — 3 sessions

6. **TipKit smoke test.** ✅ **COMPLETE** — commit `ddc41c58`. Pins Session 5's latent PAT dispatch bug on a real Apple framework. Flip in lockstep with `PATFallbackBoundaryTests.TestReadTaggedAssociatorDispatchLatentBug` when the generator starts emitting `IExistentialBoxable` on PAT conformers. Also confirms Session 1's `-D` plumbing threads through both dylib compile and ABI JSON dump end-to-end. Includes the smoke-only Swift `Tip`-conforming fixture under `#if TIPKIT_SMOKE` (Section 4E option 1). Validates that the build-infra plumbing from Session 1 actually carries `-D` through both the dylib compile and the ABI JSON dump.
7. **MusicKit smoke test.** ✅ **COMPLETE.** `MusicItemID` string round-trip through the direct-mode pipeline + reflection-only pin on `Song.AudioVariants` (ios15 type / ios16 property) for fix #2 per-property `@available` propagation. No `MusicAuthorization.request()`, no `MusicCatalog` network fetch, no authorization sheet.
8. **tvOS simulator runner + Catalyst resolver unit test.** New `RuntimeTestsApp.tvOS.csproj` (mirror of iOS, `net10.0-tvos`, `tvossimulator-arm64`), new `RuntimeTestsTvOSSimulator` Nuke target, confirm `BuildAsyncWrapper` tvOS slice path, plus the Section 4D.1 Catalyst `SwiftFrameworkResolver` unit test (small enough to fold in here rather than spending a separate session on it).

### P3 — deferred

Real Catalyst runtime runner; WorkoutKit / RoomPlan / ProximityReader / LiveCommunicationKit smokes; tvOS device runner. Per Section 6, these wait until after customer demand or stability of the Tier-A smokes.

### Where 8 could slip to 9

- **Session 2 (CryptoKit) or Session 3 (WeatherKit)** could overflow if snapshot regeneration reveals that the chosen candidate calls aren't emitted, hit a gating bug, or aren't actually hermetic. Each would split into "snapshot iteration" + "lock and gate" sessions.
- **Session 4 (P1 batch)** could split if context fills up faster than expected — five fixtures plus end-of-session gates is on the upper end of what fits.
- **Session 8 (tvOS runner)** could split off the Catalyst unit test if the `simctl` differences for tvOS turn out to be more involved than expected.

Slipping to 10+ would mean something went wrong, not normal pacing.
