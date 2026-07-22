# Apple Framework Binding — Architecture Reference

How the generator binds Apple Swift-only frameworks (StoreKit 2, WeatherKit, MusicKit, …) into NuGet packages. This doc captures the durable architectural decisions and the dead-ends future maintainers should not retry.

Companion: [`apple-swift-types-architecture.md`](apple-swift-types-architecture.md) covers the `SwiftBindings.Apple` supplement that hosts Swift-only Apple value types.

---

## The shape that ships

1. **ABI source**: `swift-api-digester -dump-sdk -module <X>` against the iPhoneOS / iPhoneSimulator SDK, **not** a wrapper Swift package.
2. **Generator runs in direct mode** (`-a/-d/-t -l '\@rpath/<X>.framework/<X>'`) with Apple-framework target auto-detection: when the ABI module name matches a built-in `*Database.xml`, that stub is dropped before parse-and-emit so the framework can be a target rather than a dependency (disable via `--keep-builtin-database`). The SDK item is `SwiftAppleFrameworkTarget`.
3. **Generator emits** `<Module>.cs`, `<Module>.Wrapper.swift`, and a packable project (default name `{Module}.Swift.iOS.csproj`) referencing `SwiftBindings.Runtime` at the published version (passed via `--swift-runtime-version`). Published Apple packages override `PackageId` to `SwiftBindings.Apple.<Framework>`.
4. **Wrapper compiles** into `<Module>SwiftBindings.xcframework` (one slice per platform/target) which is bundled into the NuGet at `runtimes/<rid>/native/`.
5. **P/Invokes resolve through `SwiftFrameworkResolver`**, which treats `@rpath/`, `@executable_path/`, `@loader_path/`, and absolute filesystem paths as dyld-resolvable and passes them verbatim to `NativeLibrary.TryLoad`. The wrapper dylib's own load commands link the system framework via `/System/Library/Frameworks/<X>.framework/<X>`, so dyld pulls the framework into the process transitively before any P/Invoke fires.
6. **Consumers reference the package** and use an `extern alias` **only where the C# namespace collides with Microsoft.iOS** (e.g. StoreKit). Pure-Swift frameworks (CryptoKit, WeatherKit, TipKit, MusicKit, …) need none.

---

## Approaches that do not work

Don't retry these — each one was tried, falsified, and produced a confusing failure mode.

- **Swift `@_exported import <X>` wrapper.** The wrapper's own `.swiftinterface` and `.abi.json` contain only the `Import` node — zero declarations. Confirmed across Swift 6.2.3 with and without `-enable-library-evolution`. There is no flag, no setting, no experimental feature.
- **Renaming the target module away from its real name** (e.g. `StoreKit` → `StoreKit2`) to dodge the `*Database.xml` collision. The Swift mangled-name length-prefix in TBD-exported symbols is load-bearing: `_$s8StoreKit...` is the bytes the demangling-key joins in `DemanglingResults.cs` rely on. Renaming compiles fine and runs into `EntryPointNotFoundException` on every P/Invoke at runtime.
- **Using `<Aliases>global,StoreKitSwift</Aliases>` to keep the framework globally visible.** Mono's ObjC type registrar walks all globally-visible types at startup and SIGABRTs inside `xamarin_bridge_initialize → mini_init → load_aot_module` before `Main()` runs when `Microsoft.iOS.StoreKit.AppStore` and `StoreKit.Swift.iOS.StoreKit.AppStore` collide. **Extern-alias-only is the right escape hatch.**

---

## NuGet package layout

One Apple framework, one NuGet package. Two naming truths:

- **Generator / project default:** `{Module}.Swift.<Platform>` (e.g. `CryptoKit.Swift.iOS`) via `GetDefaultSwiftPackageId` — the emitted csproj / assembly stem unless overridden.
- **Published Apple portfolio packages:** `SwiftBindings.Apple.<Framework>` (e.g. `SwiftBindings.Apple.CryptoKit`, `SwiftBindings.Apple.StoreKit2`). The `SwiftBindings.*` prefix is required on NuGet; bare `Swift.*` is reserved by Microsoft. StoreKit ships under the **StoreKit2** naming (package + namespace pattern), not bare `StoreKit.Swift.iOS`.

Package contents — where `iosX.Y` is the explicit Apple-workload platform version the binding was generated against, and `<PackageId>` is the published id (or the generator default when packing a local snapshot):

```
lib/net10.0-iosX.Y/<PackageId>.dll                                           # Generated C# bindings
buildTransitive/net10.0-iosX.Y/<PackageId>.targets                           # Consumer-side targets (extern alias hints, etc.)
runtimes/ios-arm64/native/<Module>SwiftBindings.xcframework/Info.plist       # Wrapper xcframework root
runtimes/ios-arm64/native/<Module>SwiftBindings.xcframework/<slice>/<Module>SwiftBindings.framework/<Module>SwiftBindings
```

`SwiftBindings.Runtime` is a published `<PackageReference>` dependency on the framework package — not bundled — emitted as a bounded version range (e.g. `[0.8.0,0.9.0)`) so ABI-compatible patch releases reach consumers without re-publishing the framework matrix while a future minor bump (allowed to break ABI) cannot silently resolve into older bindings. Each Apple-framework package floats its own version independently of `SwiftBindings.Runtime`'s.

### Why explicit `net10.0-iosX.Y` and not versionless `net10.0-ios`

Two distinct traps drive both the `<TargetFramework>` element and the `buildTransitive/` pack path to the same explicit, version-qualified TFM:

1. **NuGet NU1012**: rejects `<None>` items under `buildTransitive/<tfm>/` when the TFM lacks a platform version, so the pack path **must** be `iosX.Y`.
2. **.NET 10 library-project TPV defaults**: a library project that declares `<TargetFramework>net10.0-ios</TargetFramework>` (versionless) does NOT float to the newest installed Apple workload — that's app behavior, not library behavior. Libraries default to the **oldest** installed TPV unless `UseFloatingTargetPlatformVersion=true` is set, so on a multi-workload build machine the library half (`lib/`) and the buildTransitive half can desync.

The `--platform-version <X.Y>` CLI flag on the generator threads through `PlatformInfoFactory.Create` into `PlatformInfo.PlatformVersion`. Both `<TargetFramework>` and the `buildTransitive/` pack path source from the same `PlatformInfo.PackTfm` (= `Tfm + PlatformVersion`), so they cannot drift. **Publishing for nuget.org requires passing the explicit flag** (e.g. `--platform-version 26.2`).

The SDK pack target's dynamic `$(TargetPlatformVersion)` resolution (`Sdk.targets`) is intentionally NOT mirrored in the generator-emitted library projects — it's the right shape for SDK-consumer projects (apps), but library projects would need `UseFloatingTargetPlatformVersion=true` contortions and would still produce an unauditable static nupkg.

---

## Runtime loading strategy

The existing `SwiftFrameworkResolver` is the only resolver. `IsDyldStylePath` recognises four dyld tokens — `@rpath/`, `@executable_path/`, `@loader_path/`, absolute paths — and passes them verbatim to `NativeLibrary.TryLoad`. Anything else falls through to the standard prefix search so typos like `@rpathtypo` fail loudly.

The 2-arg `TryLoad(name, out handle)` overload is the right one — the 4-arg `DllImportSearchPath` overload applies .NET assembly-directory search semantics that conflict with dyld's `@rpath` resolution.

dyld handles simulator / device / Mac Catalyst / macOS uniformly given the right load commands in the wrapper dylib. The wrapper dylib is built per-platform/per-slice with `xcrun swiftc -target arm64-apple-ios<min>-simulator` (or `-macabi`, `-macos`, etc.) and the system framework's install name is baked into its load commands at link time, so the runtime resolves what's already there. **No per-platform resolver branching.**

---

## Smoke-test wiring (zero-regression shape)

When a smoke test consumes a generator-produced snapshot, **always** wire it via `<ProjectReference>` to the snapshot project plus a conditional `<Import>` of the generator-emitted `<Framework>.Swift.iOS.ProjectReference.targets`. **Never** consume the snapshot via a raw `<Reference HintPath>` pointing at an out-of-repo path.

Why: the `<Reference HintPath>` shape reintroduces a stale-AOT `load_aot_module` crash mode. MSBuild cannot see that a rebuilt `Swift.Runtime.dll` has invalidated the snapshot, so the consumer ends up with an AOT image compiled against the old runtime. With `<ProjectReference>`, MSBuild's incremental build graph stays coherent across `Swift.Runtime` rebuilds: ref-assembly-based change detection cascades rebuilds through the snapshot when public API changes, and leaves the snapshot alone when only implementation changes (which is safe because type references resolve by metadata reference at load time, not by embedded token).

Smoke tests also gate on (a) a per-framework compile symbol (`STOREKIT_SMOKE`, `WEATHERKIT_SMOKE`, …), (b) an explicit MSBuild opt-in property (`$(EnableStoreKitSmoke)=true`, …), and (c) `Exists()` checks on the in-tree snapshot at `BindingTests/obj/<Framework>Snapshot/` (gitignored) plus simulator RID. The csproj emits a loud `<Error>` when the opt-in is set but prerequisites are missing.

Snapshot regeneration lives in nuke as first-class targets: `nuke regenerate-apple-snapshot --framework <name>` (e.g. `--framework CryptoKit`) and the StoreKit special-case `nuke regenerate-storekit-snapshot`. Both shell out to `xcrun swift-api-digester -dump-sdk` against the active Xcode SDK to produce the ABI JSON, then run the generator in direct mode to produce the snapshot csproj + wrapper xcframework + `.ProjectReference.targets`. Incremental: skips the regen when output files are all newer than the Xcode SDK inputs (swiftinterface + TBD mtimes).

---

## iOS version availability

The generator surfaces Swift `@available` annotations as C# `[SupportedOSPlatform]` / `[UnsupportedOSPlatform]` attributes on the generated members. Consumers see CA1416 warnings at compile time when they call an API requiring a higher OS version than their app's `SupportedOSPlatformVersion`. **Runtime guards are NOT injected automatically** — the consumer is expected to gate their call behind `OperatingSystem.IsIOSVersionAtLeast(...)` themselves, the same way they would for any Microsoft.iOS API.

Per-case enum availability propagation lives alongside the type-level annotations (Swift's annotations can be more granular at the case level than at the type level).

---

## Naming convention

- **Generator / project default package id**: `{Module}.Swift.<Platform>` (e.g. `CryptoKit.Swift.iOS`, `Nuke.Swift.iOS`) — `GetDefaultSwiftPackageId`. Suffix is `.Swift.<Platform>` rather than `.SwiftBindings.<Platform>` because (a) it matches the third-party binding convention, (b) shorter is better for local snapshot project names, (c) `Swift` already disambiguates from Microsoft.iOS's ObjC bindings.
- **Published Apple portfolio NuGet id**: `SwiftBindings.Apple.<Framework>` (e.g. `SwiftBindings.Apple.CryptoKit`, `SwiftBindings.Apple.StoreKit2`). The `SwiftBindings.*` prefix is required on NuGet; bare `Swift.*` is reserved by Microsoft. Override via `--package-id` / csproj `PackageId`. StoreKit ships as **StoreKit2** naming, not bare StoreKit.
- **Wrapper xcframework / dylib**: `<Module>SwiftBindings`.
- **C# namespace**: typically `<Module>` (matches the Swift module name); StoreKit2 uses the StoreKit2 namespace pattern.

---

## Entitlements and capabilities

Per-framework, in the consumer's app, **not** in the binding package. The binding package has no opinion on entitlements — it ships C# wrappers and a Swift dylib. Consumers add `Entitlements.plist` entries (`com.apple.developer.in-app-payments`, `com.apple.developer.weatherkit`, etc.) the same way they would for Microsoft.iOS APIs.

Entitlement walkthroughs live in the [public wiki](https://github.com/justinwojo/swift-dotnet-bindings/wiki), per-framework. They evolve with Apple's developer-portal changes; pinning them into a NuGet readme would just bitrot.
