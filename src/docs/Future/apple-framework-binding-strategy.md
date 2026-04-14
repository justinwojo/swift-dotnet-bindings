# Apple Framework Binding Strategy

**Date**: 2026-04-09
**Grounded in**: StoreKit 2 (the first Apple framework brought up end-to-end through this pipeline)
**Sibling**: `../0.8.0-ship-plan.md` (release tracking doc)

---

## TL;DR

The shape that ships:

1. ABI source is `swift-api-digester -dump-sdk -module <X>` against the iPhoneOS / iPhoneSimulator SDK, **not** a wrapper Swift package.
2. The generator runs in **direct mode** (`-a/-d/-t -l '\@rpath/<X>.framework/<X>'`) with the `--apple-framework-target` auto-detect path enabled — `*Database.xml` stub for the target module is dropped before parse-and-emit so the framework can be a target instead of just a dependency.
3. The generator emits `<Module>.cs`, `<Module>.Wrapper.swift`, and a packable `<Module>.Swift.iOS.csproj` referencing `SwiftBindings.Runtime` at the published version (passed in via `--swift-runtime-version`).
4. The wrapper is compiled into `<Module>SwiftBindings.xcframework` (one slice per platform/target) which is bundled into the NuGet at `runtimes/<rid>/native/`.
5. P/Invokes resolve through `SwiftFrameworkResolver`, which now treats `@rpath/`, `@executable_path/`, `@loader_path/`, and absolute filesystem paths as dyld-resolvable and passes them verbatim to `NativeLibrary.TryLoad`. The wrapper dylib's own load commands link the system framework via `/System/Library/Frameworks/<X>.framework/<X>`, so dyld pulls the framework into the process transitively before any P/Invoke fires.
6. Consumers reference the package and route through an `extern alias` because Microsoft.iOS already exposes most Apple-framework namespaces.

What does **not** work, in case it tempts a future session:
- A Swift `@_exported import <X>` wrapper. The wrapper's own `.swiftinterface` and `.abi.json` contain only the `Import` node — zero declarations. Confirmed across Swift 6.2.3 with and without `-enable-library-evolution`. There is no flag, no setting, no experimental feature.
- Renaming the target module away from its real name (e.g. `StoreKit` → `StoreKit2`) to dodge the `*Database.xml` collision. The Swift mangled-name length-prefix in TBD-exported symbols is load-bearing: `_$s8StoreKit...` is the bytes the demangling-key joins in `DemanglingResults.cs:107,133` rely on. Renaming compiles fine and runs into `EntryPointNotFoundException` on every P/Invoke at runtime.
- Using `dotnet pack`'s `<Aliases>global,StoreKitSwift</Aliases>` to keep the framework globally visible. Mono's ObjC type registrar walks all globally-visible types at startup and SIGABRTs inside `xamarin_bridge_initialize → mini_init → load_aot_module` before `Main()` runs when `Microsoft.iOS.StoreKit.AppStore` and `StoreKit.Swift.iOS.StoreKit.AppStore` collide. Extern-alias-only is the right escape hatch — there is a warning comment in the StoreKit smoke csproj pinning this down.

---

## Strategy questions, answered

These are the twelve questions in `0.8.0-ship-plan.md` § "Strategy questions to answer in Phase 1". Each answer is grounded in something StoreKit 2 actually exercised, with a pointer to the evidence.

### 1. Wrapper package convention

**Decided**: There is no separate wrapper Swift package on disk. The Swift wrapper source is **emitted by the generator** as `<Module>.Wrapper.swift` next to the C# binding, then compiled in-place into `<Module>SwiftBindings.xcframework` as a build step inside the same direct-mode CLI invocation. No `swift-dotnet-packages/apple-frameworks/StoreKit2Wrapper/` directory survived the exploration phase — the falsified `@_exported import` artifact at `/Users/wojo/Dev/swift-dotnet-packages/apple-frameworks/StoreKit2Wrapper/` should be deleted whenever someone next touches that area.

**What the wrapper actually contains**: `@_cdecl` thunks bridging Swift's calling convention into a C ABI we can P/Invoke against. Same shape as the wrappers our generator already emits for third-party Swift libraries — the Apple-framework variant is not architecturally distinct.

**Naming**: `<Module>SwiftBindings.xcframework` (matches the third-party convention; e.g. `NukeSwiftBindings.xcframework`). The NuGet package id is `<Module>.Swift.iOS` (e.g. `StoreKit.Swift.iOS`).

**Evidence**: Session 1 (`108f4f12`) landed Apple-framework target mode; Sessions 2 + 4 (`eafe252d`, `ab3f25a8`) landed wrapper compilation in direct mode and the in-tree `Swift.Runtime` ProjectReference fallback; Session 7 (this session) landed the publishable pack flow. `0.8.0-ship-plan.md` § "Recommended next session" → § "Tracking" has the full chain.

### 2. Generator changes

**Decided**: Two real generator changes were required and have landed:

- **Apple-framework target mode** (`Program.cs`, `CliOptions.cs`, `BindingsGeneratorCommand.cs`, commit `108f4f12`): when the parsed abi.json's `moduleName` matches a built-in `*Database.xml` stub (e.g. `StoreKitDatabase.xml` for `StoreKit`), drop the stub from `_modules` before the parse-and-emit branch is gated. Auto-detected from input; opt-out via `--keep-builtin-database` when a third-party Swift library legitimately wants the stub. The minimum viable form of "the Apple framework IS the target, not a dependency."
- **Parameter-position opaque type lowering** (`Parser/SwiftABIParser.cs`, same commit): `swift-api-digester` emits `some P` parameters as `TypeNominal/GenericTypeParam` with `printedName="some Foo"` and no children — the existing `OpaqueTypeArchetype` branch only caught return-position opaques. Added `_opaqueParamCapture` + `SynthesizeOpaqueParameter` to lower them to `τ_opaque_N` synthetic generics with the constraint protocol attached. Real Swift language feature, not a defensive skip.

**Other small fixes that landed during the StoreKit 2 chain** (each generalizes beyond StoreKit, none was bespoke to it):
- `\@` CLI escape strip in `--library` / `--async-library` (commit `108f4f12`)
- Direct-mode `AsyncLibraryName` default = `{Module}SwiftBindings` (same)
- `MergeAvailabilityFromAncestors` helper across five emitter sites (commit `eafe252d`)
- Per-case enum `@available` propagation (same)
- Mutating-async receiver path (`WrapperEmitter.Async.cs`, commit `ab3f25a8`) — re-binds `__self` through `unsafeMutableAddress` so `AsyncIteratorProtocol.next()` and other `mutating async` methods compile cleanly
- Direct-mode `<ProjectReference>` fallback for `0.0.0-dev` `SwiftBindings.Runtime` + `IsPackable=false` gate + `--swift-runtime-version` CLI flag (same)
- `EnableDefaultCompileItems=false` in the emitted csproj (same)
- `SwiftWrapperPostProcessor.RemoveTrailingWrapperPreamble` walk-back (same) — strips dangling preamble comments after stripping broken wrappers
- `SwiftFrameworkResolver` dyld-style path passthrough (`IsDyldStylePath`, Session 5)
- Generator-emitted csproj uses `PlatformInfo.PackTfm` (derived as `Tfm + PlatformInfo.PlatformVersion`, e.g. `net10.0-ios26.2`) for BOTH the `<TargetFramework>` element and the `buildTransitive/<tfm>/` pack path so they cannot drift on multi-workload machines. `PlatformVersion` is sourced from the `--platform-version <X.Y>` CLI flag with a `DefaultPlatformVersion` fallback for in-tree local-dev callers (Session 7 introduced `LibTfm`; the Codex-review pass collapsed it to a single derived field; Session 1 of the 0.8.0 publishing release added the CLI flag and lifted the explicit TFM into `<TargetFramework>` after it surfaced that .NET 10 library projects default to the OLDEST installed TPV, not the newest).

**Evidence**: `0.8.0-ship-plan.md` § Session 1 outcome (target mode + opaque param fix), § Session 2 outcome (availability propagation + four C# emit bugs subsequently fixed), § Session 4 outcome (mutating async + ProjectReference fallback), § Session 5 outcome (resolver dyld passthrough), § Session 7 outcome (pack-time `PackTfm`).

**Open**: `IsModuleLoaded` vs `IsModuleProcessed` predicate split. `TypeDatabase.IsModuleLoaded` and `IsModuleProcessed` are aliases today (both `_modules.ContainsKey`). The narrow target-mode fix sidesteps the distinction; the long-term refactor is to split them so a module can be `Loaded` (we have a dependency database) without being `Processed` (we have generated real bindings as a target). Filed in `roadmap.md`. Not load-bearing for framework #2.

### 3. Runtime loading strategy

**Decided**: The existing `SwiftFrameworkResolver` is the only resolver. After the Session 5 dyld-passthrough fix, it correctly handles **all** of:
- `@rpath/<X>.framework/<X>` (the form Apple-framework target bindings emit for direct metadata accessors)
- `/System/Library/Frameworks/<X>.framework/<X>` (the on-device install name in the framework's TBD)
- `/Library/Developer/CoreSimulator/.../<X>.framework/<X>` (the simulator runtime path; dyld translates it transparently)
- `<X>SwiftBindings` (the wrapper dylib name, which Microsoft.iOS bundles into `Frameworks/` per `runtimes/<rid>/native/...` entries in the NuGet)

**No** `SystemFrameworkResolver` was needed. The fix was that `ResolveSwiftFramework` was unconditionally prepending `@rpath/{name}.framework/{name}` on every incoming library name and producing nonsense like `@rpath/@rpath/StoreKit.framework/StoreKit.framework/@rpath/StoreKit.framework/StoreKit`. dyld was masking the bug by happy-pathing the wrapper dylib's own load commands. The fix added an `IsDyldStylePath` predicate that recognizes the four documented dyld tokens (and only those four — typos like `@rpathtypo` correctly fall through to the standard prefix search so they fail loudly) and passes them verbatim to `NativeLibrary.TryLoad`. The 2-arg `TryLoad(name, out handle)` overload is the right one — the 4-arg `DllImportSearchPath` overload applies .NET assembly-directory search semantics that conflict with dyld's `@rpath` resolution.

**Simulator vs device vs Mac Catalyst vs macOS**: dyld handles all four uniformly given the right load commands in the wrapper dylib. The wrapper dylib is built per-platform/per-slice with `xcrun swiftc -target arm64-apple-ios<min>-simulator` (or `-macabi`, `-macos`, etc.) and the system framework's install name is baked into its load commands at link time, so the runtime just resolves what's already there. No per-platform resolver branching has been needed so far. Framework #2 should re-verify on whatever platform variant matters most for it; if it works on simulator and device for StoreKit it probably works for everything else, but "probably" is not the same as "verified."

**Evidence**: `0.8.0-ship-plan.md` § Session 5 outcome ("What the resolver bug actually was" + "Fix"). 15 new resolver unit tests in `SwiftFrameworkResolverTests.cs`.

### 4. NuGet package layout

**Decided**: One Apple framework, one NuGet package, `<Module>.Swift.iOS` (e.g. `StoreKit.Swift.iOS`). Package contents (where `iosX.Y` is the explicit Apple-workload platform version the binding was generated against — see `--platform-version` below):

```
lib/net10.0-iosX.Y/<Module>.Swift.iOS.dll                                  # Generated C# bindings
buildTransitive/net10.0-iosX.Y/<Module>.Swift.iOS.targets                  # Consumer-side targets (extern alias hints, etc.)
runtimes/ios-arm64/native/<Module>SwiftBindings.xcframework/Info.plist     # Wrapper xcframework root
runtimes/ios-arm64/native/<Module>SwiftBindings.xcframework/ios-arm64-simulator/<Module>SwiftBindings.framework/<Module>SwiftBindings  # The actual dylib
```

`SwiftBindings.Runtime` is a published `<PackageReference>` dependency on the framework package — not bundled — emitted as a bounded version range (e.g. `[0.8.0,0.9.0)`) so ABI-compatible patch releases reach consumers without re-publishing the framework matrix while a future minor bump (allowed to break ABI) cannot silently resolve into older bindings. Versioning: each Apple-framework package floats its own version independently of `SwiftBindings.Runtime`'s version. The framework package declares the bounded Runtime range it was built against.

**Why explicit `net10.0-iosX.Y` and not versionless `net10.0-ios`**: two distinct traps drive both the `<TargetFramework>` element and the `buildTransitive/` pack path to the same explicit, version-qualified TFM:
1. **NuGet NU1012**: rejects `<None>` items under `buildTransitive/<tfm>/` when the TFM lacks a platform version, so the pack path *must* be `iosX.Y`.
2. **.NET 10 library-project TPV defaults**: a library project that declares `<TargetFramework>net10.0-ios</TargetFramework>` (versionless) does NOT float to the newest installed Apple workload — that's app behavior, not library behavior. Libraries default to the **oldest** installed TPV unless `UseFloatingTargetPlatformVersion=true` is set, so on a multi-workload build machine the library half (`lib/`) and the buildTransitive half can desync. Today's pack flow only produced an internally-consistent nupkg by coincidence (single Apple workload installed on the build machine).

The fix (Session 1 of the 0.8.0 Apple-framework publishing release) is an explicit `--platform-version <X.Y>` CLI flag on the generator, threaded through `PlatformInfoFactory.Create` into `PlatformInfo.PlatformVersion`. Both `<TargetFramework>` and the `buildTransitive/` pack path source from the same `PlatformInfo.PackTfm` (= `Tfm + PlatformVersion`), so they cannot drift. The default value (no flag passed) keeps the in-tree fallback so existing local-dev callers don't break, but **publishing for nuget.org requires passing the explicit flag** (e.g. `--platform-version 26.2` for an iOS 26.2 SDK cut). The SDK pack target's dynamic `$(TargetPlatformVersion)` resolution (`Sdk.targets`) is intentionally NOT mirrored here — it's the right shape for SDK-consumer projects (apps) but the wrong shape for generator-emitted library projects (which would need `UseFloatingTargetPlatformVersion=true` contortions to reach a usable result, and would still produce an unauditable static nupkg). See `roadmap.md` ("explicit TPV in generator-emitted csproj") for the full reasoning trail.

**iOS-only initially**: the Session 7 nupkg is iOS only because Sessions 1–7 only validated iOS. macOS / Mac Catalyst / tvOS slices exist in `PlatformInfoFactory` and the emitter produces them, but they have not been exercised against StoreKit 2 end-to-end. Phase 2 (`0.8.0-ship-plan.md` § "Multi-Platform Validation") covers that work; the framework #2 checklist below has it as a checkpoint.

**Evidence**: `unzip -l /tmp/storekit2-session7-fresh/nupkg/StoreKit.Swift.iOS.0.8.0-preview.1.nupkg` produces exactly the layout above. Session 7 outcome block in `0.8.0-ship-plan.md` has the verification.

### 5. Naming convention

**Decided**: `<Module>.Swift.iOS` for the package id. `<Module>SwiftBindings` for the wrapper xcframework / dylib name. C# namespace stays `<Module>` (matches the Swift module name).

The package suffix is `.Swift.<Platform>` rather than `.SwiftBindings.<Platform>` because (a) it's already what every third-party shipping library uses (`Nuke.Swift.iOS`, etc.), (b) shorter is better for a name consumers type into csprojs, (c) the `Swift` part already disambiguates from Microsoft.iOS's ObjC bindings. There is no `Apple.` infix — Microsoft.iOS uses bare framework names and the precedent is well-established.

### 6. iOS version availability

**Decided**: The generator surfaces Swift `@available` annotations as C# `[SupportedOSPlatform]` / `[UnsupportedOSPlatform]` attributes on the generated members. Consumers see CA1416 warnings at compile time when they call an API that requires a higher OS version than their app's `SupportedOSPlatformVersion`. Runtime guards are NOT injected automatically — the consumer is expected to gate their call behind `OperatingSystem.IsIOSVersionAtLeast(...)` themselves, the same way they would for any Microsoft.iOS API.

**StoreKit 2 evidence**: 359 availability annotations on the StoreKit surface, 235 CA1416 warnings on the generated `StoreKit.cs` build (correctly flagging iOS-version-gated members like `Transaction.RefundRequestStatus`). The 235 warnings are noise on a clean build but they are correct — they map to Apple's `@available(iOS 17, *)` and similar annotations. The minimum `<SupportedOSPlatformVersion>` in the generated csproj defaults to whatever the swiftinterface declares (iOS 16.0 for StoreKit 2).

**Per-case enum availability** had to be added in Session 2 (commit `eafe252d`) because the parser was only propagating type-level annotations, not per-case ones. The concrete trigger was `StoreKit.ExternalPurchase.NoticeResult.continuedWithExternalPurchaseToken` (iOS 17.4). Framework #2 will not need to repeat this fix.

**Foreign value-type metadata gap as an availability-adjacent hazard**: `System.Guid ↔ Foundation.UUID`, `System.DateTime ↔ Foundation.Date`, `System.Decimal ↔ Foundation.Decimal` are mapped at generator time but lack runtime `RegisterMetadata` calls. Any `SwiftOptional<Guid>` cctor throws `SwiftRuntimeException` on first use. StoreKit 2's `AppStore.deviceVerificationID` is the canonical reproducer. Filed in `roadmap.md`. Framework #2 should expect to dodge this if it returns optional UUID/Date/Decimal values; permanent fix is generator-level and unblocks every Apple framework simultaneously.

### 7. Entitlements and capabilities

**Decided**: Per-framework, in the consumer's app, **not** in the binding package. The binding package has no opinion on entitlements — it ships C# wrappers and a Swift dylib. Consumers add `Entitlements.plist` entries (`com.apple.developer.in-app-payments`, `com.apple.developer.weatherkit`, etc.) the same way they would for Microsoft.iOS APIs.

**Where this gets documented**: in the **wiki** (`https://github.com/justinwojo/swift-dotnet-bindings/wiki`), per-framework, **not** in the NuGet package readme. The wiki is the right home for entitlement walkthroughs because they evolve with Apple's developer-portal changes; pinning them into a NuGet readme would just bitrot.

**Sample apps** (next question) demonstrate the entitlement setup so consumers have something concrete to copy.

### 8. Async / AsyncSequence / Result patterns

**Decided**: The generator already handles the async surface area cleanly given the Session 4 + Session 6 fixes, with two known follow-ups:

- **Async methods**: emitted as `Task<T>` / `Task` returning C# methods. Throwing async methods become `Task<T>` that throws on the awaiter. StoreKit 2 has 31 throwing/async methods; all compile and resolve at runtime after Session 4's wrapper-compilation work.
- **AsyncSequence**: emitted as a custom C# class implementing `IAsyncEnumerable<T>` (or close to it — actual shape is `MakeAsyncIterator()` + `NextAsync(CancellationToken) → Task<T?>`). Validated end-to-end in Session 6 against `Transaction.unfinished` (a faithful proxy for `Transaction.updates` because of the orphan-PInvoke bug below). Three iteration passes — early-terminate, full empty-complete, memory-tracked empty-complete — all green. Managed-memory delta on the empty-complete pass is 656 bytes against a 256 KB ceiling.
- **Result-style returns**: Swift's `Result<Success, Failure>` doesn't appear directly in StoreKit 2's public surface (StoreKit 2 uses `VerificationResult<TSignedType>` + throws for its error model). When it shows up in framework #2, it should fall through to whatever the generator emits for any other generic Swift enum — no special-case work expected, but flag it if a new shape surfaces.

**Two known async-path follow-ups, both filed in `roadmap.md`**:
1. **Orphan `[LibraryImport]` for `Transaction.updates` / `Storefront.updates` / `Product.SubscriptionInfo.Status.updates`**. The generator emits the `[LibraryImport]` declaration for the metadata accessor but drops the private wrapper getter AND the public C# property. Hypothesis (background subagent diagnosis pending focused repro): `CSharpWrapperCoGater.FindAndMarkCallers` removes the `_Get()` wrapper because it transitively calls a stripped helper P/Invoke, but doesn't co-remove the now-dead primary `[LibraryImport]` declaration. Generalizes to **any static property getter whose `_Get()` wrapper transitively references a stripped helper**, not just `*.updates`. Permanent fix is small; until then, the workaround is to use a sibling AsyncSequence (like `Transaction.unfinished`) that exercises the same code path.
2. **Foreign value-type metadata gap** (covered under question 6). Reachable on `VerificationResult<Transaction>` field access; smoke test sidesteps it.

**Evidence**: `0.8.0-ship-plan.md` § Session 6 outcome and `roadmap.md` lines 101–102.

### 9. Macro-based frameworks

**Not exercised by StoreKit 2.** StoreKit 2 has no `@Model` / `@Observable` macros. The strategy here is unchanged from `0.8.0-ship-plan.md`: defer SwiftData / Observation until after the simpler Apple frameworks ship. The `.swiftinterface` ought to contain the expanded form, but verifying that is framework #N work (where N is "first macro-heavy framework we attempt"), not framework #2.

### 10. Testing strategy

**Decided** (mirroring how StoreKit 2 was validated):

- **Unit tests** for any generator changes the framework requires. StoreKit 2 added unit tests for parameter-position opaque lowering, mutating-async wrapping, dyld-path resolver passthrough, `EnableDefaultCompileItems=false`, `PackTfm`-based pack paths (and the derivation formula `PackTfm = Tfm + DefaultPlatformVersion`), and a few more. New generator changes should ship with new unit tests; that's not optional.
- **BindingTests fixtures** for any new patterns the framework exercises that the existing fixtures don't already cover. StoreKit 2 added `AsyncMutatingCounter` to lock in mutating-async behavior. Pattern: add the smallest possible Swift fixture in `BindingTests/Sources/SwiftBindingsTestLib/` that reproduces the new pattern, then a runtime test in `BindingTests/RuntimeTestsApp/` that exercises it.
- **Smoke tests** (`BindingTests/RuntimeTestsApp/SmokeTests/<Framework>SmokeTests.cs`) gated on (1) a per-framework compile symbol (`STOREKIT_SMOKE`, `WEATHERKIT_SMOKE`, ...), (2) an **explicit MSBuild opt-in property** (`$(EnableStoreKitSmoke)=true`, `$(EnableWeatherKitSmoke)=true`, ...), and (3) `Exists()` checks on the in-tree snapshot at `BindingTests/obj/<Framework>2Snapshot/` (gitignored) + simulator RID. The csproj consumes the snapshot via `<ProjectReference>` + a conditional `<Import>` of the generator-emitted `<Framework>.Swift.iOS.ProjectReference.targets`; the test class is wrapped in `#if`. **Never** consume the snapshot via a raw `<Reference HintPath>` pointing at an out-of-repo path — that was the original StoreKit 2 shape, and it reintroduced the stale-AOT `load_aot_module` crash mode (documented in the Session 5 outcome of `0.8.0-ship-plan.md`) because MSBuild could not see that a rebuilt `Swift.Runtime.dll` had invalidated the snapshot. With ProjectReference, MSBuild's incremental build graph stays coherent across Swift.Runtime rebuilds: ref-assembly-based change detection cascades rebuilds through the snapshot when public API changes, and leaves the snapshot alone when only implementation changes (which is safe because type references resolve by metadata reference at load time, not by embedded token). Requiring an explicit `$(Enable<X>Smoke)=true` forces a human acknowledgement ("yes, regenerate for this run"); the csproj emits a loud `<Error>` when the opt-in is set but prerequisites are missing. This is the **zero-regression, non-hermetic-safe** wiring pattern; framework #2 should copy it shape-for-shape.
- **Snapshot regeneration lives in nuke as a first-class target**: `nuke regenerate-<framework>-snapshot` (and as an automatic conditional prerequisite of `nuke runtime-tests-simulator --enable-<framework>-smoke`). The target shells out to `xcrun swift-api-digester -dump-sdk` against the active Xcode SDK to produce the ABI JSON, then runs the generator in direct mode to produce `<Framework>.Swift.iOS.csproj` + wrapper xcframework + `.ProjectReference.targets` under `BindingTests/obj/<Framework>2Snapshot/`. Incremental: skips the regen when output files are all newer than the Xcode SDK inputs (swiftinterface + TBD mtimes). `BindingTests/obj/` is gitignored by the top-level `[Oo]bj/` rule so nothing gets committed. Xcode is already a hard prerequisite of any iOS-targeted build, so "needs swift-api-digester" is zero additional cost. See `Build.RuntimeTests.cs:RegenerateStoreKit2Snapshot()` for the StoreKit implementation — framework #2 should model its helper on this shape.
- **Validation libraries** (`build/validation-libraries.json`) — Apple frameworks are NOT added here. The validation pool is for compile-gating third-party Swift libraries against the generator; Apple frameworks have their own (much smaller, much more focused) smoke-test wiring.
- **Sandbox / credential setup**: per-framework. StoreKit 2 uses Xcode StoreKit configuration files; WeatherKit will need real Apple Developer service credentials and is therefore harder to put under CI; TipKit needs runtime UI. Each framework decides its own test approach inside `<Framework>SmokeTests.cs` and documents prerequisites in the test's XML doc.

**Evidence**: `BindingTests/RuntimeTestsApp/SmokeTests/StoreKitSmokeTests.cs` is the reference shape — read it before writing framework #2's smoke tests. The XML docs at the top of `TestAppStoreCanMakePayments` and `TestTransactionUnfinishedAsyncSequenceEnumerates` are the load-bearing documentation for "why this accessor and not that one."

### 11. Sample app strategy

**Decided**: A **fresh consumer reproducer** outside the swift-bindings repo, using `dotnet new ios -n <X>Consumer`, `<PackageReference>` to the published nupkg, and a tiny `AppDelegate.cs` that calls one accessor in `FinishedLaunching` and prints `[<X>-SMOKE] PASS` to the simulator console. This is what Session 7 validated for StoreKit 2 (`/tmp/storekit2-consumer-fresh/`).

The fresh consumer is NOT a real product app — it's a smoke-level "the published nupkg is consumable from outside the repo" reproducer. Real sample apps (full sandbox-purchase walkthroughs etc.) are a separate effort, live in a separate repo, and are not part of the binding package's release criteria.

**Where the real sample apps live**: in the public docs / wiki, **not** checked into this repo. The wiki has room for per-framework walkthrough tutorials with screenshots, entitlement setup, and real device testing notes. A copy-paste-ready code snippet in the wiki is more useful than a maintained sample app in the repo, and it doesn't add CI cost.

**MAUI vs .NET iOS**: both are valid consumers. The fresh consumer reproducer is `.NET iOS` because that's the simplest possible test surface. MAUI consumers should also work but are not validated end-to-end in the per-framework release flow yet — flag for framework #N.

### 12. Multi-platform

**Decided**: iOS first. macOS / Mac Catalyst / tvOS support is in `PlatformInfoFactory` and the emitter, but Sessions 1–7 only exercised iOS. Multi-platform validation is Phase 2 (`0.8.0-ship-plan.md` § "Phase 2 — Multi-Platform Validation"). The framework #2 checklist below treats macOS/Mac Catalyst as a stretch checkpoint — if framework #2 ships iOS-only and macOS lands later, that's fine.

**The pattern when framework #2 wants macOS support**: `--platform macos --platform-target device` instead of `--platform ios --platform-target simulator` in the direct-mode invocation. The emitter produces a `<Module>.Swift.macOS.csproj` with the `osx-arm64` RID baked in. NuGet packs ship one xcframework slice per platform under `runtimes/<rid>/native/`. There is no per-platform binding code divergence — same generated `.cs`, same generated `.Wrapper.swift`, just compiled against a different SDK.

**Open**: `PackTfm` for macOS / tvOS / Mac Catalyst is derived in `PlatformInfo` as `Tfm + PlatformVersion`, matching the iOS form, with `PlatformVersion` sourced from the `--platform-version` CLI flag (default `DefaultPlatformVersion` = `"26.0"`). This needs verification when a non-iOS Apple framework actually packs — the four platforms share the same `PlatformVersion` field today, but a future Apple workload that diverges versioning across platforms would need a per-platform override. Filed in the framework #2 checklist as a checkpoint.

---

## Framework #2 checklist

This is the working checklist for adding the next Apple framework (WeatherKit / TipKit / App Intents / …). It assumes the StoreKit 2 evidence above and references it where relevant. Read this section without reading the rest of the doc; if anything in it is unclear, then the rest of the doc has the context.

### Pre-flight

- [ ] Pick the framework. Constraints: must be a Swift-first framework (not pure ObjC — Microsoft.iOS already covers those), must have a public swiftinterface in the iPhone OS SDK, must have a non-trivial-but-bounded surface (≤ ~100 public types is comfortable; StoreKit 2 was 78). Avoid macro-based frameworks (SwiftData, Observation) until a session is dedicated to them.
- [ ] Verify the framework is **NOT** already covered by Microsoft.iOS as ObjC bindings. If it is, the binding will need an `extern alias` to disambiguate (every Apple framework so far hits this — it's the rule, not the exception). WeatherKit / TipKit / App Intents all collide with Microsoft.iOS namespaces; assume yours does too.
- [ ] Read the framework's `@available` annotations in its swiftinterface. If it requires iOS 17+ or 18+, set the consumer's `<SupportedOSPlatformVersion>` accordingly in the generated csproj before pack.

### Generator + binding

> **Preferred path**: model a `nuke regenerate-<framework>-snapshot` target on `Build.RuntimeTests.cs:RegenerateStoreKit2Snapshot()` and run it. The automated target writes the snapshot under `BindingTests/obj/<Framework>2Snapshot/` (gitignored), is incremental against the SDK input mtimes, and is the same code path the smoke-test wiring consumes via `<ProjectReference>` + `<Import>` of the generator-emitted `.ProjectReference.targets`. Adding the nuke target up-front keeps the per-framework smoke tests on the zero-regression wiring shape (question 10 above) and avoids the stale-AOT footgun the original StoreKit 2 chain hit. The manual commands below remain valid for ad-hoc exploration or one-off generator-bug repro outside of the smoke-test path.

- [ ] Dump ABI: `xcrun swift-api-digester -dump-sdk -module <X> -target arm64-apple-ios<min>-simulator -sdk $(xcrun --sdk iphonesimulator --show-sdk-path) -o /tmp/<X>.abi.json`
- [ ] Run the generator in direct mode against the dump:
  ```
  SDK="$(xcrun --sdk iphonesimulator --show-sdk-path)"
  SWIFTINTERFACE="$SDK/System/Library/Frameworks/<X>.framework/Modules/<X>.swiftmodule/arm64-apple-ios-simulator.swiftinterface"
  DYLIB="$SDK/System/Library/Frameworks/<X>.framework/<X>.tbd"

  mkdir -p /tmp/<x>-out && \
  dotnet run --project src/Swift.Bindings/src -- \
    -a /tmp/<X>.abi.json \
    -d "$DYLIB" -t "$DYLIB" -s "$SWIFTINTERFACE" \
    -l '\@rpath/<X>.framework/<X>' \
    --platform ios --platform-target simulator \
    --swift-runtime-version <published-runtime-version> \
    -o /tmp/<x>-out
  ```
- [ ] Verify the generator runs to completion. Expected output: `<X>.cs`, `<X>.Wrapper.swift`, `<X>SwiftBindings.xcframework/`, `<X>.Swift.iOS.csproj`, `<X>.Swift.iOS.targets`. If any of these are missing or empty, file a generator bug — the Apple-framework target mode should have auto-detected the module name and emitted everything.
- [ ] If the generator hits a new emit bug (wrong type qualification, missing dedup, missing async path, etc.), **stop and file it as a focused commit with a regression test** before continuing. StoreKit 2 surfaced 5+ unrelated emit bugs across Sessions 1–6; assume framework #2 will surface 1–3 more. Each one generalizes beyond a single framework — don't paper over them.
- [ ] Check `nm -g <X>SwiftBindings.framework/<X>SwiftBindings | grep SBW_` against the `[LibraryImport(... EntryPoint = "SBW_...")]` declarations in the generated `.cs`. Mismatches indicate either the orphan-PInvoke bug or a stripped-symbols co-gating gap. If you see orphans, check `roadmap.md` for the open follow-up — the workaround is to use a sibling accessor that exercises the same code path.
- [ ] `cd /tmp/<x>-out && dotnet build <X>.Swift.iOS.csproj` — must produce 0 errors. Warnings (CA1416 platform availability) are expected and acceptable.

### Smoke test wiring

- [ ] Add a `<X>SmokeTests.cs` to `BindingTests/RuntimeTestsApp/SmokeTests/`, gated on a `<X>_SMOKE` compile symbol and a `$(<X>SmokeEnabled)` MSBuild property. Copy the conditional `<Reference>` + `<NativeReference>` block from `RuntimeTestsApp.csproj` (the StoreKit one). Use an `extern alias <X>Swift` to disambiguate from Microsoft.iOS — do NOT add `global` to the alias list.
- [ ] Pick a non-throwing, non-async, non-heap-returning accessor as the first smoke target. Avoid anything returning `SwiftOptional<Guid>` / `SwiftOptional<DateTime>` / `SwiftOptional<Decimal>` — those hit the foreign value-type metadata gap. A `bool` or `Int` returning static property is ideal.
- [ ] Add a second smoke test exercising the framework's headline async pattern (if it has one). Use the three-pass shape from `TestTransactionUnfinishedAsyncSequenceEnumerates` (early-terminate / full empty-complete / memory-tracked) to validate iterator dispose, terminal completion, and managed-memory boundedness.
- [ ] Run `nuke runtime-tests-simulator --skip-regen --class-filter <X>SmokeTests` and confirm both pass.
- [ ] Run `nuke runtime-tests-simulator --skip-regen` (no filter) and confirm the baseline pass count ticks up by exactly the number of new tests (zero regressions).

### Pack + publish

- [ ] Build the consumer csproj in Release: `dotnet build -c Release <X>.Swift.iOS.csproj`
- [ ] Pack: `dotnet pack <X>.Swift.iOS.csproj --no-build -c Release -o ./nupkg -p:PackageVersion=<version> -p:Authors=... -p:PackageProjectUrl=... -p:RepositoryUrl=... -p:PackageLicenseExpression=MIT -p:Description=...` (use underscores in `Description` if there are spaces — em-dashes and other punctuation break MSBuild's `-p:` parsing).
- [ ] Verify the nupkg layout matches the StoreKit 2 shape via `unzip -l nupkg/<X>.Swift.iOS.<version>.nupkg`. Required entries:
  - `lib/net10.0-ios26.0/<X>.Swift.iOS.dll`
  - `buildTransitive/net10.0-ios26.0/<X>.Swift.iOS.targets`
  - `runtimes/ios-arm64/native/<X>SwiftBindings.xcframework/Info.plist`
  - `runtimes/ios-arm64/native/<X>SwiftBindings.xcframework/ios-arm64-simulator/<X>SwiftBindings.framework/<X>SwiftBindings`
- [ ] Verify the nuspec dependency lists `SwiftBindings.Runtime <version>` for `net10.0-ios26.0` only. No phantom `0.0.0-dev` dependencies.

### Fresh consumer reproducer

- [ ] `mkdir /tmp/<x>-consumer-fresh && cd /tmp/<x>-consumer-fresh && dotnet new ios -n <X>Consumer -o .`
- [ ] Add a `nuget.config` with the local feed pointing at `./nupkg` from the pack step.
- [ ] Add `<PackageReference Include="<X>.Swift.iOS" Version="<version>"><Aliases><X>Swift</Aliases></PackageReference>` to the consumer csproj. Set `<SupportedOSPlatformVersion>` to match the framework's minimum iOS version. Set `<AllowUnsafeBlocks>true</AllowUnsafeBlocks>`.
- [ ] Add `extern alias <X>Swift;` to `AppDelegate.cs` and call your smoke accessor in `FinishedLaunching`, printing `[<X>-SMOKE] PASS` on success.
- [ ] `dotnet build` — must succeed. Expect MT7155 warnings about duplicate `Swift/*.xml` BundleResources between the framework binding dll and `Swift.Runtime.dll` (these are warnings, not errors; the duplicates are cosmetic — both copies of `StoreKitDatabase.xml` etc. are identical).
- [ ] Install + launch on a booted simulator: `xcrun simctl install <device> bin/Debug/net10.0-ios/iossimulator-arm64/<X>Consumer.app && xcrun simctl launch --console-pty <device> com.companyname.<X>Consumer`
- [ ] Confirm `[<X>-SMOKE] PASS` in the console output.

### Validation gates (matches CLAUDE.md table)

- [ ] `nuke test` — green
- [ ] `nuke validate` — green, baseline unchanged
- [ ] `nuke binding-tests` — green (only if generator/emitter changed)
- [ ] `nuke runtime-tests-simulator --skip-regen` — green, baseline +N where N = new smoke tests
- [ ] `nuke runtime-tests-device --skip-regen` — green if the framework touches calling conventions, struct marshalling, or async paths
- [ ] Update the `validation-baseline.json` pass counts only after gates are green AND only if the count actually changed — never prophylactically.

### Documentation

- [ ] Wiki entry for the framework: minimum iOS version, entitlements required, sample code snippet, known limitations.
- [ ] If new generator changes landed: update `roadmap.md` to reflect any closed follow-ups, and file any new ones with the same level of detail as the existing entries (concrete reproducer, root cause, proposed fix).
- [ ] **Do NOT update this strategy doc** unless framework #2 surfaces a new pattern that generalizes (e.g. "all frameworks with X need Y"). One-off observations go in `<Framework>2-exploration.md` for that framework, modeled after `0.8.0-ship-plan.md`. This strategy doc should stay the durable cross-framework reference; framework-specific session-by-session notes should not pollute it.

---

## Future questions appendix

These are the questions Sessions 1–7 raised but did not answer. They are NOT load-bearing for framework #2 — list them so they don't get lost.

- **Apple-framework target mode CLI ergonomics**: today the auto-detect path drops a `*Database.xml` stub when its `moduleName` matches the input abi.json. There's an opt-out flag (`--keep-builtin-database`) but no opt-in flag (`--apple-framework-target StoreKit`). If a framework's name happens to collide with a third-party Swift library's name in the wild, the auto-detect path picks the wrong direction. Has not happened yet; defer until it does.
- **`IsModuleLoaded` vs `IsModuleProcessed` predicate split** in `TypeDatabase`: the narrow target-mode fix sidesteps this. Long-term refactor is documented above (question 2). Still a follow-up.
- **`Swift/*.xml` BundleResource duplication** between framework binding dlls and `Swift.Runtime.dll`: produces 45 MT7155 warnings on the fresh consumer build (Session 7 evidence). Both copies are byte-identical so it's cosmetic, but the warning noise is real and a future grooming pass could either (a) suppress the warning code in the generated csproj, (b) strip the embedded XMLs from the framework dll (they're already in `Swift.Runtime.dll`), or (c) ignore.
- **Multi-slice xcframework wrappers**: Sessions 1–7 only built simulator slices. The `--wrapper-architectures all` mode produces device + simulator slices but has not been exercised against an Apple framework. Framework #N needs to verify this when shipping a real consumer app for App Store submission.
- **Macro-based frameworks** (SwiftData, Observation): deferred. The generator's swiftinterface parser may or may not handle macro-expanded forms cleanly; "may not" is the assumption until proven otherwise.
- **Sandbox / credential CI strategy**: StoreKit 2 has Xcode StoreKit configuration files for sandbox testing. WeatherKit needs real Apple Developer service credentials. TipKit needs runtime UI. Per-framework, ad-hoc, no shared infrastructure. If the matrix grows, may justify a shared "smoke test fixture credential vault" pattern; not justified yet.
- **NU1012 / `PackTfm` evolution**: `PlatformInfo.PackTfm` derives from a single shared constant `PlatformInfo.DefaultPlatformVersion = "26.0"`. Bumping the workload's default platform version is now a one-line change instead of four parallel string edits in `PlatformInfoFactory`. Could still be derived dynamically from the workload at runtime (`$(TargetPlatformVersion)`), but the cost of the static constant is one line per workload bump and the dynamic form would add complexity. Leave static; revisit if it bites.
- **Real `dotnet pack` integration test**: current test coverage for `PackTfm` pins the emitted csproj text (`buildTransitive/net10.0-ios26.0/`) and the derivation formula, but nothing actually runs `dotnet pack` and asserts on the produced nupkg's internal paths. A real pack+restore integration test would give end-to-end assurance against future regressions where the text-level assertions pass but NuGet silently packs the wrong TFM group. Not added in the Codex-review pass because a realistic fixture needs a fake xcframework and a real NuGet source to restore against — substantial infrastructure. Filed as a follow-up in `src/docs/roadmap.md`.

---

## Cross-references

- `../0.8.0-ship-plan.md` § Phase 1 — the original strategy questions this doc answers
- `../0.8.0-ship-plan.md` — the full Session 1–7 trail this doc is grounded in
- `../roadmap.md` lines 101–102 — the two open StoreKit follow-ups (foreign value-type metadata gap; orphan PInvoke for `*.updates`)
- `BindingTests/RuntimeTestsApp/SmokeTests/StoreKitSmokeTests.cs` — the reference shape for per-framework smoke tests
- `src/Swift.Bindings/src/Configuration/PlatformInfo.cs` — `PackTfm` (derived from `DefaultPlatformVersion`) field doc explaining the NU1012 trap
- `src/Swift.Runtime/src/Swift/Runtime/SwiftFrameworkResolver.cs` — `IsDyldStylePath` predicate and the dyld-passthrough fix
