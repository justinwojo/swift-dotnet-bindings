# Initial Ship — SDK 0.8.0 + Apple 26.2.0

**Lane:** Initial ship (anchors both SDK and Apple lanes at the same commit)
**Date:** TBD
**Companion tags:** `sdk-v0.8.0` and `apple-v26.2.0` on the same commit

## Packages published

| Package | Version | Role |
|---|---|---|
| `SwiftBindings.Sdk` | `0.8.0` | Generator + MSBuild SDK (`dotnet build` → bindings → assembly) |
| `SwiftBindings.Runtime` | `0.8.0` | Runtime support library (Swift→.NET marshalling, ARC, async, witness dispatch) |
| `SwiftBindings.Templates` | `0.8.0` | `dotnet new swift-binding` project template |
| `SwiftBindings.Apple` | `26.2.0` | Swift-only Apple value types (Foundation, ManagedSettings, CryptoKit primitives, …) — new in this release |

`SwiftBindings.Apple` declares `[0.8.0, 0.9.0)` on `SwiftBindings.Runtime`. Patch releases on either lane stay ABI-compatible within that range.

## Why this release matters

Microsoft.iOS is an ObjC binding generator with no Swift-first story. Every year more of Apple's platform ships Swift-only — StoreKit 2, WeatherKit, TipKit, CryptoKit's modern AEAD, and so on — leaving .NET on Apple progressively less accessible. 0.8.0 is the first SDK release that closes that gap end-to-end: a generator that takes any compiled Swift `.xcframework` and emits idiomatic C# bindings that P/Invoke directly into the Swift dylib. No proxy layer, no `@objc` shims, no `[Verify]` cleanup.

## Headline — Apple framework bindings

Twelve Apple frameworks ship as production-ready packages from the companion [`swift-dotnet-packages`](https://github.com/justinwojo/swift-dotnet-packages) repo. All twelve pass on both runtimes (iOS Simulator on Mono JIT and physical device on NativeAOT) with **0 fail / 0 skip** at the Round 7 closing validation:

| Framework | Tests | iOS minimum | Notes |
|---|---|---|---|
| **StoreKit 2** | 36 | 15+ | Modern in-app purchases, subscriptions, transaction history |
| **CryptoKit** | 40 | 13+ | Includes primary AEAD path (`AES.GCM`, `ChaChaPoly`) seal/open and tamper detection |
| **MusicKit** | 37 | 15+ | Apple Music catalog, library, playback |
| **WeatherKit** | 27 | 16+ | Apple's weather service |
| **RoomPlan** | 29 | 17+ | LiDAR room capture |
| **WorkoutKit** | 25 | 17+ | Workout sessions and schedules |
| **TipKit** | 20 | 17+ | Feature discovery / tips UI |
| **LiveCommunicationKit** | 18 | 17.4+ | Live communication sessions |
| **FamilyControls** | 15 | 16+ | Screen Time integration |
| **Translation** | 12 | 17.4+ | On-device translation |
| **ProximityReader** | 10 | 16+ | Contactless payments reader |
| **ActivityKit** | (subset) | 16.1+ | Live Activities — `Activity<T>.request/update/end` permanently skipped (app-defined `ActivityAttributes` not crossable) |

Aggregate: 269 simulator + 269 device assertions passing across the twelve packages.

## Headline — third-party Swift libraries

Real-world Swift libraries validated on both runtimes:

- **BlinkID** / **BlinkIDUX** — production document-scanning SDKs
- **GRDB** — SQLite Swift wrapper
- **Kingfisher**, **Nuke** — image-loading frameworks
- **Lottie** — animation runtime
- **Mappedin** — indoor mapping SDK

Plus 15 libraries in the sim-validation portfolio (Alamofire, RxSwift, SnapKit, CryptoSwift, KeychainAccess, Starscream, DeviceKit, PhoneNumberKit, Reachability, Swinject, ObjectMapper, SwiftyBeaver, XMLCoder, BonMot, plus Kingfisher).

Validation totals: **2,077 runnable assertions** on simulator + **2,077** on device, all passing.

## What's new since 0.7.0

0.7.0 was a generator that could bind a small set of Swift libraries on iOS Simulator. 0.8.0 is a generator that ships twelve Apple frameworks plus seven third-party libraries to production on both Mono JIT (Simulator) and NativeAOT (device), across iOS, macOS, Mac Catalyst, and tvOS, with a brand-new supplement package (`SwiftBindings.Apple`) for Swift-only Apple value types and a hardened multi-TFM SDK target mode that drives both lanes.

**Scope, in numbers:** 118 commits, 674 files changed, ~61K lines added across roughly six months of work. Highlights below; the full diff is linked at the bottom.

### `SwiftBindings.Apple` — new fourth package

The largest architectural addition. A standalone supplement package that hosts Swift-only Apple value types — `Foundation.Locale.Language`, `ManagedSettings.Application`, `CryptoKit.P256.Signing.ECDSASignature`, and others — that have no ObjC bridge and therefore can't live in `Microsoft.iOS`. Framework packages pull it transitively only when a binding actually references a supplement-owned type.

- **Manifest pipeline + opaque emitter** — the supplement is driven by an additive-only manifest under `SwiftBindings.Sdk/tools/`, with a dedicated emitter for opaque (VWT-backed) storage and a sequential-layout whitelist for validated frozen types.
- **`TypeOwnerRegistry` + cross-module identity** — the generator routes Swift-only types to the supplement's canonical assembly so identity stays stable across framework packages. Consumers can pass identity tests like `typeof(X) == typeof(X)` even when two framework packages reference the same supplement type.
- **Demand-driven prototyping** — `SwiftAppleSupplementPrototypeDir` lets consumers patch supplement sources locally without losing canonical identity (`AssemblyName=SwiftBindings.Apple` + `RootNamespace=Swift` are preserved on the prototype).
- **`[ModuleInitializer]`-registered `SwiftFrameworkResolver`** — bare-library DllImport names + an automatically-registered resolver, so consuming apps don't need to hand-wire DLL paths. The resolver is safe to call from both the supplement's module initializer and a consuming app's (the prior `SetDllImportResolver` conflict is wrapped in try/catch).
- **Legacy canonicals stay in Runtime** — `Foundation.Date`, `.URL`, `.Decimal`, `.Measurement<T>`, `ManagedSettings.Token<T>`, and `SwiftUI.Text` remain in `SwiftBindings.Runtime` indefinitely. The supplement does not re-emit them.

Design reference: [`Design/apple-swift-types-architecture.md`](Design/apple-swift-types-architecture.md).

### Apple framework SDK target mode

A first-class `--target-mode apple-framework` path through the SDK, with everything that entails:

- **Multi-TFM emission** — explicit `--platform-version` selects `net10.0-ios{V}` / `net10.0-macos{V}` / `net10.0-maccatalyst{V}` / `net10.0-tvos{V}` and stamps both `<TargetFramework>` and `buildTransitive/` paths. Dynamic `$(TargetPlatformVersion)` resolution was rejected because .NET 10 library projects default to the oldest installed TPV.
- **Multi-slice packaging** — xcframeworks slice per NuGet RID at pack time so consumers download only the platform they target. Embedded `Info.plist` is written for the device-slice wrapper in multi-slice packs.
- **Catalyst + tvOS support** — `iOSSupport` search path, dependency slices that don't have a simulator pair, ObjC-only dependency module imports in wrappers, and a tvOS simulator runner.
- **Apple-framework validation tier** — a dedicated `nuke validate` tier with a multi-slice packaging gate, plus a `framework registry` that drives discovery.
- **Wrapper code generation** — `@available` is now propagated across all `@_cdecl` wrapper code paths and per-case parsers; wrapper compilation + csproj emission run in direct mode.

### `Collection`-family rewrite

A complete rewrite of how the generator binds Swift's `Collection` protocol family. Witness dispatch is now structural:

- `Array<T>` property getters route through `@_cdecl` for stable codegen.
- Generic `Collection` structs project as `IReadOnlyList<T>`.
- Witness-dispatch fallback handles Collection-family generic structs that don't pair through CSM.
- Indirect-buffer ABI types from MusicKit and similar.
- `inout` writeback is hardened.
- Parent-generic-param gates are relaxed for Collection-family conformers.

### CSM (Concrete Specialized Methods)

CSM is the path that lets generic Swift methods be called from C# with the right specialization. Most of this lane was new in 0.8.0:

- **Direct-return ABI fix** — earlier emissions had a mismatch between the wrapper's return ABI and the P/Invoke; this also closed a `resultPtr` leak.
- **Mutating-self thunks** + **generic-parent specialization** + **namespace-enum routing** through `EmitConcreteSpecializations`.
- **Non-frozen struct vs class discrimination** in `@_cdecl` wrappers via `unsafeBitCast` (the Round 7 CryptoKit AEAD blocker fix).
- **Method-level generic substitution** in CSM return types, with rejection of bad pairings.
- **Cross-level same-type constraint filtering** of CSM pairings.
- **Async CSM** — Phase 4a intercept with Reserve/Commit dedup, plus actor-isolated instance method wrappers.

### Async, closures, and effects

- **Async closures** — full bridge for `@escaping` async closures, built up across four sessions: baseline (no args, primitive return) → arg-bearing throwing → non-throwing primitive return → `Foundation.Data` return. 10K-iteration stress test included; Codex review pass folded in.
- **`ActorIsolatedAsyncStream`** + MusicKit indirect-buffer ABI types.
- **`UnsafeRawBufferPointer → ReadOnlySpan<byte>`** bridging.
- **`Result<(), E>`**, **`Optional<any Error>`**, and **`Result<T, any Error>`** as closure args through MCB.
- **`Optional<Container<ObjCBridgeable>>`** async returns through the NS-bridge ABI.
- **Closures over ObjC-bridged Apple types** (UIImage, NSDictionary, etc.) and metatype arrays.

### Generics, enums, and existentials

- **Generic enum payloads** — `TryGetVerified` for sugared generic payloads, multi-value `TryGet` (the `VerificationResult` projection), nested-generic / class-T / `AnyType` payload cases.
- **Generic constructors** with `Array<T>` parameters dispatch through the static factory.
- **Sugared generic-param name resolution** through context lookup.
- **Type-name aliases** parsed; bare generic `T` returns marshal through the class path.
- **Module qualification** preserved for nested generic args in the existential bypass.
- **PAT existential boxing** + per-case enum availability.
- **Parameterized protocol availability** in the constrained existential bridge.
- **Silent-tombstone reporting** for opaque types with no usable surface (replaces noisy aborts).

### Type projections (new in 0.8.0)

- `simd_float4x4` → `System.Numerics.Matrix4x4`; `SIMD<Float>` aliases projected.
- ObjC-bridged enum payloads marshalled.
- `Foundation.Locale.Currency`, `DateComponents.FormatStyle`, `Decimal.FormatStyle` added to Foundation value types.
- `Foundation.UUID` metadata registration fixed for `SwiftOptional<Guid>`.

### Parser & demangler robustness

- **TBD parser** — multi-line `objc-eh-types` continuation handling (the Round 7 Stripe blocker; generalized so any future multi-line export property is consumed safely).
- **Demangler** — stop dropping types on demangler crash; clashing nested-type renames resolved.
- **Parser** — opaque-param compositions and inline-annotated enum cases carried through; constrained-extension property conflicts on generic types skipped instead of erroring.

### Stripe + third-party hardening

- **Stripe suite (12 products)** — generator output is SHIP-eligible across the umbrella and all sub-products. SB0001 cleanup, NativeAOT fixes, and the TBD parser fix combine to unblock all 12 products at the binding step.
- **BlinkID, BlinkIDUX, GRDB, Kingfisher, Lottie, Mappedin, Nuke** — all green on both runtimes (1,369 assertions / 0 fail / 2 documented skips).
- **15-library `sim-validation` portfolio** — Alamofire, RxSwift, SnapKit, CryptoSwift, KeychainAccess, Starscream, DeviceKit, PhoneNumberKit, Reachability, Swinject, ObjectMapper, SwiftyBeaver, XMLCoder, BonMot, Kingfisher — all green on both runtimes (439 assertions each).

### NativeAOT, macOS, tvOS, Catalyst

- **NativeAOT** — device test regressions resolved; generic factory registration hardened.
- **macOS** — `net10.0-macos` build pipeline unblocked; nested extension types and `SwiftOptional<Guid>` failures fixed.
- **tvOS** — simulator runner wired; bridge unblocked.
- **Catalyst** — `iOSSupport` search path, dependency slices without simulator pairs, ObjC-only dependency imports in wrappers.

### Runtime library

- New runtime types and type databases for Apple framework + Stripe audits.
- Native dylib rebuild with platform-correct Swift runtime layout.
- Bounded `[0.8.0, 0.9.0)` `PackageReference` from emitted csprojs (avoids republish cascades on runtime patches).
- Explicit Apple TFM forced in emitted csprojs.
- `Dispose` safety verified for all generated types (with runtime tests).
- Flaky `SwiftObjectRegistryTests` fixed by retaining proxy references.

### SDK, templates, and CI

- `nuke pack --version X.Y.Z --apple-version A.B.C` packs all four NuGets in one invocation with separate version stamping per lane (`VersionScope` rewrites and restores 8 files).
- `nuke binding-tests` consolidates simulator (Mono JIT) and device (NativeAOT) end-to-end gates under a single target with composable `--sim` / `--device` / `--macos` / `--catalyst` / `--tvos` / `--compile-only` / `--skip-regen` / `--skip-build` / `--strict` / `--class-filter` flags.
- Xcode 26.3 selected in CI; Swift 5.9 minimum.
- Apple-framework smoke coverage for CryptoKit, WeatherKit, TipKit, MusicKit, StoreKit 2 (transitional — durable coverage lives in BindingTests + the swift-dotnet-packages consumer suite).
- SDK template improvements + wrapper compiler fixes.
- Co-gater (defensive throwing-closure facade) cross-scope property stripping fixed; property-shaped co-gated members preserved; co-gater fenced against events and nested types.
- Pre-1.0 cleanup + licensing pass landed (`NOTICE`, package metadata, `CONTRIBUTING`).

## Versioning model

- **`SwiftBindings.Sdk` / `SwiftBindings.Runtime` / `SwiftBindings.Templates`** — semantic versioning. Released together as the SDK lane (`sdk-v{version}` tags). Runtime ABI is bounded `[0.8.0, 0.9.0)` — any ABI break forces the next minor.
- **`SwiftBindings.Apple`** — `{iOS-SDK-major}.{iOS-SDK-minor}.{binding-revision}`. `26.2.0` follows iOS 26.2. Released independently as the Apple lane (`apple-v{version}` tags).
- **Apple framework packages** (StoreKit 2, WeatherKit, …) — versioned per their underlying Apple SDK train, released from the [`swift-dotnet-packages`](https://github.com/justinwojo/swift-dotnet-packages) repo on its own cadence.
- **Third-party packages** — mirror upstream library versions (e.g., `SwiftBindings.Nuke 12.8.x` matches Nuke 12.8.x).

## Known limitations at ship

- **Stripe SDK** — generator step now succeeds across all 12 products (TBD parser fix in this release). Sim runtime crashes on first `STPPaymentHandler.SharedHandler` access because `spm-to-xcframework` does not propagate SwiftPM `.bundle` resources into the produced `.xcframework`. The fix lives in [`spm-to-xcframework`](https://github.com/justinwojo/spm-to-xcframework), not the binding generator. Tracked as a Round 8 candidate.
- **ActivityKit** — `Activity<T>.request/update/end` permanently skipped. App-defined `ActivityAttributes` cannot cross the .NET ↔ Swift boundary as a generic parameter.
- **Kingfisher fluent builder chains** (`KF.Builder`, `KingfisherWrapper<UIImageView>`) — 102 SB0001 diagnostics. Architectural; deferred to post-1.0.
- **Swift variadic parameter packs** — permanent. No C# equivalent. Affects 12 WeatherKit methods.
- **Result-builder DSLs** (`@_alwaysEmitIntoClient`) — no binary symbol to bind against. Affects 9 TipKit items.
- **TipKit `Tips.Rule.init` Predicate overloads** — orthogonal demangler failure on `Foundation.Predicate` closures. Niche; falls back to existential path.

Five confirmed upstream .NET runtime bugs (Mono JIT and NativeAOT) shape a small number of `[MonoJitCrash]` / NativeAOT-skipped tests. The full list is tracked in the project memory and roadmap. Everything else is owned in this repo.

## Links

- **NuGet** — [`SwiftBindings.Sdk`](https://www.nuget.org/packages/SwiftBindings.Sdk) / [`SwiftBindings.Runtime`](https://www.nuget.org/packages/SwiftBindings.Runtime) / [`SwiftBindings.Templates`](https://www.nuget.org/packages/SwiftBindings.Templates) / [`SwiftBindings.Apple`](https://www.nuget.org/packages/SwiftBindings.Apple)
- **Apple framework bindings** — [`swift-dotnet-packages`](https://github.com/justinwojo/swift-dotnet-packages)
- **Wiki** — [Supported Features](https://github.com/justinwojo/swift-dotnet-bindings/wiki/Supported-Features) · [Known Limitations](https://github.com/justinwojo/swift-dotnet-bindings/wiki/Known-Limitations) · [Getting Started](https://github.com/justinwojo/swift-dotnet-bindings/wiki)
- **Companion tag** — [`apple-v26.2.0`](https://github.com/justinwojo/swift-dotnet-bindings/releases/tag/apple-v26.2.0) (same commit as this release)
- **Full diff since 0.7.0** — [`v0.7.0...sdk-v0.8.0`](https://github.com/justinwojo/swift-dotnet-bindings/compare/v0.7.0...sdk-v0.8.0)

---

*Final notes are typically generated by Grok at publish time. This file is the human-authored draft attached to the release; it can be edited post-publish to incorporate anything Grok surfaced or to correct the record after real-world use.*
