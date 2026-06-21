// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;

namespace BindingsGeneration
{
    /// <summary>
    /// Options for binding project emission.
    /// </summary>
    public sealed class BindingProjectEmitterOptions
    {
        public required string OutputDirectory { get; init; }
        public required string ModuleName { get; init; }
        public required XCFrameworkMetadata Metadata { get; init; }
        /// <summary>
        /// Path to the source xcframework. Null in direct mode (Apple system frameworks),
        /// where the runtime resolves the binary against the on-device system framework
        /// via dyld @rpath rather than a packaged xcframework.
        /// </summary>
        public string? SourceXCFrameworkPath { get; init; }
        /// <summary>
        /// Native linkage of the source framework. When <see cref="NativeLinkage.Static"/>, the
        /// source xcframework was force-loaded into the wrapper (the sole runtime carrier of its
        /// ObjC classes), so the source NativeReference and source pack item are BOTH dropped —
        /// re-embedding a static archive that the wrapper already carries duplicate-registers its
        /// ObjC classes. Dynamic sources keep both (the consumer links the source dylib directly).
        /// </summary>
        public NativeLinkage SourceNativeLinkage { get; init; } = NativeLinkage.Dynamic;
        public string? WrapperXCFrameworkPath { get; init; }
        /// <summary>
        /// Path to the bridge xcframework (for SwiftUI views). Null if no bridge.
        /// </summary>
        public string? BridgeXCFrameworkPath { get; init; }
        /// <summary>
        /// Whether bridge Swift source files were emitted. When true, bridge NativeReference
        /// and pack items are emitted with Exists() conditions so they activate once the bridge
        /// is compiled (which may happen after generation in SDK mode).
        /// </summary>
        public bool HasBridgeSwift { get; init; }
        public string? SwiftRuntimeVersion { get; init; }
        /// <summary>
        /// Framework dependencies that should be emitted as PackageReference items.
        /// </summary>
        public IReadOnlyList<FrameworkDependencyInfo>? Dependencies { get; init; }
        /// <summary>
        /// Resolved C# namespace for the module (used for generated file names).
        /// </summary>
        public string? ResolvedNamespace { get; init; }
        /// <summary>
        /// ObjC binding project filename for mixed framework ProjectReference.
        /// </summary>
        public string? ObjCProjectFileName { get; init; }
        /// <summary>
        /// Platform info for multi-platform support. Falls back to iOS if not specified (CLI default).
        /// </summary>
        public PlatformInfo? PlatformInfo { get; init; }
        /// <summary>
        /// SPM resource bundle names detected in the source framework.
        /// When non-empty, BundleResource and pack items are emitted for each bundle.
        /// </summary>
        public IReadOnlyList<string>? ResourceBundleNames { get; init; }

        /// <summary>
        /// When <c>true</c>, emit a PackageReference to <c>SwiftBindings.Apple</c> — meaning
        /// the consumer's generated bindings referenced at least one Swift-only Apple type
        /// resolved through <see cref="AppleSupplementResolver"/>. Non-Apple consumers leave
        /// this <c>false</c> so they do not pick up the supplement.
        /// </summary>
        public bool EmitsAppleSupplementReference { get; init; }

        /// <summary>
        /// When <see cref="EmitsAppleSupplementReference"/> is true, the open-ended NuGet
        /// version expression to attach. The Apple supplement is cross-major additive-only,
        /// so consumers float forward on the Apple SDK train and do not pin to a specific
        /// minor. Intentionally has no default: every caller (CLI, tests, templates) must
        /// thread an explicit version from <c>--apple-version</c> or the equivalent, and
        /// Emit throws if this is null/empty when <see cref="EmitsAppleSupplementReference"/>
        /// is true. A hardcoded fallback would silently ship stale supplement versions on
        /// train bumps.
        /// </summary>
        public string? AppleSupplementVersion { get; init; }

        /// <summary>
        /// When non-null, emit a <c>ProjectReference</c> (instead of <c>PackageReference</c>)
        /// to the Apple supplement at the given relative path. Used by the prototyping mode
        /// where the SDK materializes a supplement project into <c>obj/</c> and the consumer
        /// references it as a project to preserve canonical identity without round-tripping
        /// through NuGet restore.
        /// </summary>
        public string? AppleSupplementPrototypeProjectPath { get; init; }
    }

    /// <summary>
    /// Emits a .csproj file for a Swift binding project, ready for `dotnet build` and `dotnet pack`.
    /// </summary>
    public static partial class BindingProjectEmitter
    {
        // DefaultSwiftRuntimeVersion is single-sourced from $(SwiftBindingsSdkVersion) and emitted
        // into obj/GeneratedVersion.cs by a build target (see Swift.Bindings.csproj). It defaults to
        // the 0.0.0-dev sentinel in-tree and is baked to the real version when the generator is
        // published at pack time, so the shipped generator emits the correct Runtime reference
        // without any source file being rewritten.

        /// <summary>
        /// Emits a {PackageId}.csproj file into the output directory.
        /// </summary>
        public static void Emit(BindingProjectEmitterOptions options, ILogger logger)
        {
            var pi = options.PlatformInfo ?? PlatformInfoFactory.Create(ApplePlatform.iOS);
            var packageId = pi.GetDefaultSwiftPackageId(options.ModuleName);
            var runtimeVersion = options.SwiftRuntimeVersion ?? DefaultSwiftRuntimeVersion;
            var resolvedNamespace = options.ResolvedNamespace ?? options.ModuleName;
            var csprojPath = Path.Combine(options.OutputDirectory, $"{packageId}.csproj");

            // Compute relative path from output dir to source xcframework.
            // Null in direct mode — Apple system frameworks have no packaged xcframework;
            // the binary lives on-device under /System/Library/Frameworks/ and is resolved
            // via dyld @rpath at runtime, so the project file omits the source NativeReference
            // and the source pack item entirely.
            var hasSourceXcfw = !string.IsNullOrEmpty(options.SourceXCFrameworkPath);
            // The wrapper is what force-loads a static source and carries its ObjC classes,
            // so the static-source drop below is conditioned on the wrapper actually existing.
            var hasWrapper = options.WrapperXCFrameworkPath != null &&
                             Directory.Exists(options.WrapperXCFrameworkPath);
            // Gap 2: a static-archive source was force-loaded into the wrapper, so the source
            // xcframework must be dropped from BOTH the local NativeReference and the pack item
            // (and the generation-time slice is skipped — there's nothing to pack). The drop is
            // gated on a wrapper carrier actually being present: with --skip-wrapper-compilation
            // (or a wrapper failure) there is no force_load, so the source is still the sole
            // native carrier. This standalone-csproj site decides from on-disk presence (the CLI
            // path compiles the wrapper before emitting, so the disk is settled) via the boolean
            // ShouldIncludeSourceXcframework. The frozen NuGet/PR .targets ConsumerTargetsEmitter
            // writes can't see the wrapper's eventual fate, so they use the enum-valued
            // ResolveConsumerSourceReferenceMode instead — a different decision shape, but both live
            // in NativePackagingPolicy as the single source of truth so the drop policy can't drift.
            var emitSourceXcfw = hasSourceXcfw &&
                                 NativePackagingPolicy.ShouldIncludeSourceXcframework(
                                     options.SourceNativeLinkage, hasWrapper);
            var outputDirFull = Path.GetFullPath(options.OutputDirectory);
            var relativeSourceXcfw = hasSourceXcfw
                ? Path.GetRelativePath(outputDirFull, Path.GetFullPath(options.SourceXCFrameworkPath!))
                : null;

            // Slice source xcframework at generation time so the emitted csproj's pack item
            // ships only RID-compatible slices. The local NativeReference still references the
            // raw source xcfw (the consumer's local build environment has full source access);
            // only the <None Pack="true"> item targets the sliced path. CLI standalone csprojs
            // are single-TFM, so generation-time slicing is sufficient — re-run the generator
            // when the source xcframework changes. The SDK pack-time path slices in Sdk.targets
            // via _SliceSourceXcframework instead.
            //
            // Stage under `pack-staging/` (NOT `obj/`) so `dotnet clean` doesn't silently empty
            // the pack glob and produce a nupkg with no source xcframework. Only re-running the
            // generator (which calls PrepareDestination on the slice dir) clears the staged tree.
            //
            // Skip when SourceXCFrameworkPath is a synthetic test stub (no Info.plist on disk):
            // unit tests pass paths like Path.Combine(dir, "..", "Module.xcframework") to
            // exercise emitter string output without standing up a full xcframework. In real
            // CLI runs XCFrameworkResolver has already validated the source's Info.plist
            // before BindingProjectEmitter runs, so the skip path can only be hit by tests.
            var packSourceXcfwRelative = relativeSourceXcfw;
            if (emitSourceXcfw && File.Exists(Path.Combine(options.SourceXCFrameworkPath!, "Info.plist")))
            {
                var sliceDest = Path.Combine(
                    outputDirFull, "pack-staging", pi.NuGetRid, $"{options.ModuleName}.xcframework");
                XCFrameworkSlicer.Slice(options.SourceXCFrameworkPath!, pi.NuGetRid, sliceDest, logger);
                packSourceXcfwRelative = Path.GetRelativePath(outputDirFull, sliceDest);
            }

            // Version placeholder warning comment
            var versionComment = options.Metadata.IsVersionPlaceholder
                ? "\n    <!-- WARNING: Version is a placeholder (Xcode default). Set PackageVersion manually. -->"
                : "";

            // Wrapper xcframework items (conditional). hasWrapper is computed above, where the
            // static-source drop is gated on it.
            var wrapperModuleName = $"{options.ModuleName}SwiftBindings";

            var wrapperNativeRef = hasWrapper
                ? $"""

                    <NativeReference Include="{wrapperModuleName}.xcframework"
                                     Condition="Exists('{wrapperModuleName}.xcframework')">
                      <Kind>Framework</Kind>
                    </NativeReference>
                """
                : "";

            var wrapperPackItem = hasWrapper
                ? $"""

                    <None Include="{wrapperModuleName}.xcframework/**" Pack="true"
                          Condition="Exists('{wrapperModuleName}.xcframework')"
                          PackagePath="{pi.GetNativePackPath($"{wrapperModuleName}.xcframework")}" />
                """
                : "";

            // Bridge xcframework items — emitted when bridge .swift source exists,
            // with Exists() conditions in the XML so they activate after bridge compilation.
            // This ensures the .csproj is correct on first-run even before --compile-bridge-only.
            // The native-macOS exclusion mirrors the .SwiftUIBridge.cs Compile gate below: the
            // SwiftUI bridge is UIKit-only, so its macOS xcframework slice is an empty Mach-O.
            // Referencing or packing it makes a native-macOS consumer fail with Xamarin MT158
            // ("missing/empty Mach-O"). Mac Catalyst keeps the bridge — its TFM lacks "-macos".
            var hasBridge = options.HasBridgeSwift ||
                            (options.BridgeXCFrameworkPath != null && Directory.Exists(options.BridgeXCFrameworkPath));
            var bridgeModuleName = $"{options.ModuleName}Bridge";

            var bridgeNativeRef = hasBridge
                ? $"""

                    <NativeReference Include="{bridgeModuleName}.xcframework"
                                     Condition="Exists('{bridgeModuleName}.xcframework') AND !$(TargetFramework.Contains('-macos'))">
                      <Kind>Framework</Kind>
                    </NativeReference>
                """
                : "";

            var bridgePackItem = hasBridge
                ? $"""

                    <None Include="{bridgeModuleName}.xcframework/**" Pack="true"
                          Condition="Exists('{bridgeModuleName}.xcframework') AND !$(TargetFramework.Contains('-macos'))"
                          PackagePath="{pi.GetNativePackPath($"{bridgeModuleName}.xcframework")}" />
                """
                : "";

            // Optional compile items
            var wrappersCompile = $"""

                    <Compile Include="{resolvedNamespace}.Wrappers.cs"
                             Condition="Exists('{resolvedNamespace}.Wrappers.cs')" />
                """;
            // SwiftUI bridge body is `#if __IOS__ || __TVOS__ || __MACCATALYST__` gated
            // so it compiles to nothing on native macOS. Skip the Compile include there
            // anyway (defense in depth) — keeps the assembly entirely free of the
            // SwiftUI session API surface on TFMs that can't reach UIKit, matching the
            // Swift side where the .SwiftUIBridge.swift is `canImport(UIKit)` gated.
            var bridgeCompile = $$"""

                    <Compile Include="{{resolvedNamespace}}.SwiftUIBridge.cs"
                             Condition="Exists('{{resolvedNamespace}}.SwiftUIBridge.cs') AND !$(TargetFramework.Contains('-macos'))" />
                """;

            // Build dependency PackageReference items
            var dependencyRefs = "";
            if (options.Dependencies != null && options.Dependencies.Count > 0)
            {
                foreach (var dep in options.Dependencies)
                {
                    if (dep.IsObjCOnly) continue;
                    var depComment = dep.EffectiveVersion == "0.0.0"
                        ? "\n    <!-- WARNING: Placeholder version. Update before publishing. -->"
                        : "";
                    dependencyRefs += $"""

                    <PackageReference Include="{dep.GetEffectivePackageId(pi)}" Version="{dep.EffectiveVersion}" />{depComment}
                """;
                }
            }

            // Apple supplement reference. Emitted only when the generator resolved at least one
            // Swift-only Apple type (e.g. Foundation.Locale.Language) via AppleSupplementResolver.
            // Non-Apple consumers leave EmitsAppleSupplementReference=false and pick up no extra dep.
            //
            // The open-ended version (e.g. "26.0.0" instead of a bounded range) is deliberate: the
            // supplement is cross-major additive-only, so consumers must float forward as Apple
            // ships new SDK trains. A closed upper bound would force a coordinated release for
            // every train bump.
            //
            // Prototype mode takes precedence: when a prototype project path is supplied the
            // consumer references it as a project (canonical identity preserved, no NuGet
            // round-trip). Swapping project → package reference must be transparent so the
            // consumer's generated code keeps compiling unchanged — that's what the SDK
            // targets rely on in --apple-supplement-prototype-dir flows.
            var appleSupplementRef = "";
            if (!string.IsNullOrEmpty(options.AppleSupplementPrototypeProjectPath))
            {
                appleSupplementRef = $"""

                    <!-- Apple supplement — prototyping mode. Project-reference form keeps
                         canonical identity across compilations and is swappable for a
                         PackageReference once the supplement version is pinned. -->
                    <ProjectReference Include="{XmlEscape(options.AppleSupplementPrototypeProjectPath)}" />
                """;
            }
            else if (options.EmitsAppleSupplementReference)
            {
                if (string.IsNullOrWhiteSpace(options.AppleSupplementVersion))
                {
                    throw new InvalidOperationException(
                        "BindingProjectEmitterOptions.AppleSupplementVersion must be set (typically threaded from --apple-version) " +
                        "when EmitsAppleSupplementReference is true. A hardcoded fallback would silently ship a stale supplement version " +
                        "on Apple SDK train bumps — fail loudly here instead.");
                }

                appleSupplementRef = $"""

                    <!-- Apple supplement — open-ended version range (e.g. [26.0.0,)) because
                         the supplement is cross-major additive-only per architecture doc
                         §Decision summary item 5. A bare "26.0.0" becomes an exact pin in
                         NuGet, which blocks consumers from floating forward when Apple ships
                         new SDK trains — use [ver,) so only the floor is enforced. -->
                    <PackageReference Include="SwiftBindings.Apple" Version="[{XmlEscape(options.AppleSupplementVersion)},)" />
                """;
            }

            // IsPackable for the dev sentinel: a project that resolves Swift.Runtime via the
            // in-tree ProjectReference is by definition local-dev only and MUST NOT be packed.
            // SDK-style pack would (a) emit a phantom `SwiftBindings.Runtime 0.0.0-dev` package
            // dependency from the ProjectReference (because that's the in-tree project's
            // PackageId/PackageVersion), and (b) not roll the in-tree native dylibs into the
            // outer .nupkg, since they live behind ProjectReference, not as `<None Pack="true">`
            // items in this project. The result would be an unusable .nupkg whose dependency
            // doesn't exist on any feed. Refusing to pack is the loud failure mode — to publish,
            // pass --swift-runtime-version <published-version> so the PackageReference path is
            // taken instead.
            var isPackable = runtimeVersion != DefaultSwiftRuntimeVersion;

            // SwiftBindings.Runtime resolution. The default version "0.0.0-dev" is a sentinel
            // for local-dev runs (direct mode against an Apple SDK framework, ad-hoc generator
            // invocations from inside the swift-bindings repo). No "0.0.0-dev" nupkg exists in
            // the cache, so a plain PackageReference would resolve against whatever stale 0.x
            // package is sitting in ~/.nuget/packages/ — producing CS errors against types
            // that have since landed in current Swift.Runtime source.
            //
            // For the dev sentinel we emit a ProjectReference to the in-tree Swift.Runtime
            // .csproj, gated on the SwiftBindingsRepoRoot MSBuild property (settable via
            // -p:SwiftBindingsRepoRoot=... or in the consumer's Directory.Build.props). The
            // ProjectReference (NOT a raw `<Reference>`+`<HintPath>`) is load-bearing: it
            // builds the binding against the in-tree Swift.Runtime MANAGED assembly from
            // source (so it tracks current runtime types) and resolves the runtime-flavor
            // property wiring — a bare assembly reference would bind against a stale on-disk
            // dll and miss that wiring. The NATIVE runtime is a separate concern: it now
            // ships as a `SwiftBindingsRuntime.framework` embedded via `<NativeReference>`,
            // and NativeReference items do NOT propagate across a ProjectReference. The
            // repo's in-tree deploy harnesses inject that framework into the app bundle
            // themselves (and set IncludeSwiftBindingsRuntimeNative=false on the reference);
            // a standalone dev binding built this way is for local compile/test, not device
            // distribution. When the property is unset, we fall back to an exact-version
            // PackageReference (`[0.0.0-dev]`) so the failure mode is a clean NU1102
            // ("package not found"), not a silent stale binding.
            //
            // For any real version the PackageReference path is unchanged — published
            // consumers see the same csproj they always did, and the published nupkg's
            // `buildTransitive/SwiftBindings.Runtime.targets` embeds the native framework
            // (via NativeReference) for them.
            var runtimeReference = runtimeVersion == DefaultSwiftRuntimeVersion
                ? $"""
                    <!-- Local-dev wiring: 0.0.0-dev has no published nupkg. Set
                         SwiftBindingsRepoRoot (-p:SwiftBindingsRepoRoot=/path/to/swift-bindings
                         or in Directory.Build.props) to bind against the in-tree Swift.Runtime
                         project. The ProjectReference form (not a bare HintPath) is required so
                         the binding compiles against current Swift.Runtime managed types and
                         picks up its runtime-flavor wiring. (The native runtime ships as a
                         SwiftBindingsRuntime.framework NativeReference, which does not flow
                         across a ProjectReference; for local device testing the harness injects
                         it, and published consumers get it via the package's buildTransitive
                         targets.) Without the property, the build falls back to an exact-version
                         PackageReference (`[0.0.0-dev]`), which fails with a clear "package not
                         found" error instead of silently resolving to a stale cached
                         SwiftBindings.Runtime nupkg under ~/.nuget/packages/. -->
                    <ProjectReference Include="$(SwiftBindingsRepoRoot)/src/Swift.Runtime/src/Swift.Runtime.csproj"
                                      Condition="'$(SwiftBindingsRepoRoot)' != ''" />
                    <PackageReference Include="SwiftBindings.Runtime" Version="[{runtimeVersion}]" Condition="'$(SwiftBindingsRepoRoot)' == ''" />
                """
                // Published path: emit a bounded version range that floats forward across
                // SwiftBindings.Runtime patch releases (so a 0.8.1 ABI-compatible bug fix
                // reaches every Apple-framework consumer without re-publishing the framework
                // package matrix) but slams shut at the next minor (so a future 0.9.0 with
                // any ABI/struct-layout/P/Invoke break can't silently hose older bindings'
                // consumers). Patch-level ABI compatibility is a strict internal rule —
                // only bug-fix implementations land in 0.X.Y, never struct layout or
                // P/Invoke signature changes. The plain `Version="{runtimeVersion}"` shape
                // we used to emit was minimum-only and would have happily resolved a
                // future-incompatible 0.9.0 cached locally, producing the kind of silent
                // breakage this constraint exists to prevent.
                : $"""
                    <PackageReference Include="SwiftBindings.Runtime" Version="{BuildBoundedRuntimeVersionRange(runtimeVersion)}" />
                """;

            // ObjC companion for mixed frameworks. A mixed framework is ONE xcframework → ONE
            // package, so the companion's managed assembly is EMBEDDED into this package's lib/
            // rather than shipped as a separate dependency. PrivateAssets="all" builds the
            // companion (its ObjC class symbols resolve from the shared native at consume time)
            // without NuGet promoting the ProjectReference to a package <dependency>;
            // _EmbedObjCCompanionInPackage then adds the built assembly to lib/ via
            // BuildOutputInPackage. It resolves the companion's REAL output via GetTargetPath
            // (not a guessed bin.objc/$(Configuration)/$(TargetFramework)/ path, which silently
            // drifts under output-path overrides), so the embedded assembly is always the one
            // the companion actually built.
            var objcProjectRef = "";
            if (!string.IsNullOrEmpty(options.ObjCProjectFileName))
            {
                objcProjectRef = $"""

                  <!-- ObjC companion (mixed framework): embedded into this package, not a separate
                       dependency. ONE xcframework → ONE package, so the companion's managed assembly
                       ships in THIS package's lib/. PrivateAssets="all" builds the companion (its ObjC
                       class symbols resolve from the shared native at consume time) without NuGet
                       promoting the ProjectReference to a package <dependency>. _EmbedObjCCompanionInPackage
                       captures the companion's REAL built assembly via GetTargetPath rather than guessing
                       its output location (a hardcoded path silently drifts under output-path overrides
                       such as AppendTargetFrameworkToOutputPath=false or -p:OutputPath=..., and would ship
                       a Swift-only package), then adds it to lib/ via BuildOutputInPackage.
                       The companion declares its own single TFM, so RemoveProperties="TargetFramework"
                       keeps this project's per-TFM pack pass from cross-wiring it (mirrors the SDK's
                       _BuildMixedObjCCompanion). -->
                  <ItemGroup Condition="Exists('{options.ObjCProjectFileName}')">
                    <ProjectReference Include="{options.ObjCProjectFileName}" PrivateAssets="all" />
                  </ItemGroup>
                  <PropertyGroup>
                    <TargetsForTfmSpecificBuildOutput>$(TargetsForTfmSpecificBuildOutput);_EmbedObjCCompanionInPackage</TargetsForTfmSpecificBuildOutput>
                  </PropertyGroup>
                  <Target Name="_EmbedObjCCompanionInPackage">
                    <MSBuild Condition="Exists('{options.ObjCProjectFileName}')"
                             Projects="{options.ObjCProjectFileName}"
                             Properties="Configuration=$(Configuration)"
                             RemoveProperties="TargetFramework"
                             Targets="GetTargetPath"
                             BuildInParallel="false">
                      <Output TaskParameter="TargetOutputs" ItemName="_SwiftBindingCompanionBuildOutput" />
                    </MSBuild>
                    <ItemGroup>
                      <BuildOutputInPackage Include="@(_SwiftBindingCompanionBuildOutput)"
                                            Condition="'@(_SwiftBindingCompanionBuildOutput)' != ''" />
                    </ItemGroup>
                    <!-- Fail closed (standalone sibling of SWIFTBIND039): a mixed binding was emitted
                         (ObjC companion present) but no companion assembly was captured to embed.
                         Block the pack rather than silently shipping a Swift-only package whose
                         consumers would hit TypeLoadException on the ObjC types. -->
                    <Error Condition="'@(_SwiftBindingCompanionBuildOutput)' == ''"
                           Code="SWIFTBIND039"
                           Text="Mixed-framework binding emitted an ObjC companion ({options.ObjCProjectFileName}) but no built companion assembly was captured to embed in lib/. The mixed framework ships as a single package with the companion embedded, so packing without it would ship the Swift surface but not the ObjC types. Verify the companion csproj restores+builds before pack." />
                  </Target>
                """;
            }

            // SPM resource bundle items (local build: BundleResource, NuGet pack: None)
            var resourceBundleItems = "";
            var resourceBundlePackItems = "";
            if (options.ResourceBundleNames != null && options.ResourceBundleNames.Count > 0)
            {
                foreach (var bundleName in options.ResourceBundleNames)
                {
                    resourceBundleItems += $"""

                    <BundleResource Include="{bundleName}.bundle/**"
                                    LinkBase="{bundleName}.bundle"
                                    Condition="Exists('{bundleName}.bundle')" />
                """;
                    resourceBundlePackItems += $"""

                    <None Include="{bundleName}.bundle/**" Pack="true"
                          Condition="Exists('{bundleName}.bundle')"
                          PackagePath="{pi.GetNativePackPath($"{bundleName}.bundle")}" />
                """;
                }
            }

            var content = $"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <OutputType>Library</OutputType>
                    <!-- Explicit, version-qualified TFM. .NET 10 library projects default to
                         the OLDEST installed TPV (apps float, libraries don't unless
                         UseFloatingTargetPlatformVersion=true), so a versionless
                         "net10.0-ios" emission would silently desync from the version-qualified
                         buildTransitive/ pack path on any multi-workload machine. Both fragments
                         now source from PlatformInfo.PackTfm so they cannot drift. Override
                         the version via the generator CLI's platform-version flag. -->
                    <TargetFramework>{pi.PackTfm}</TargetFramework>
                    <ImplicitUsings>enable</ImplicitUsings>
                    <Nullable>enable</Nullable>
                    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
                    <IsPackable>{(isPackable ? "true" : "false")}</IsPackable>
                    <PackageId>{packageId}</PackageId>{versionComment}
                    <PackageVersion>{options.Metadata.PackageVersion}</PackageVersion>
                    <SupportedOSPlatformVersion>{options.Metadata.EffectiveMinimumOSVersion}</SupportedOSPlatformVersion>
                    <!-- Emit the XML doc file so the thousands of generated /// doc comments
                         travel with the package (NuGet auto-includes the sibling assembly-named
                         .xml in lib/, surfacing IntelliSense for consumers). CS1591 is suppressed
                         because doc-comment coverage of the generated surface is partial — not
                         every public member carries one — and we will not fail the binding build
                         over a member the generator chose not to document. -->
                    <GenerateDocumentationFile>true</GenerateDocumentationFile>
                    <NoWarn>CS0169;CS0414;CA1420;CS1591</NoWarn>
                    <!-- Disable default Compile items: the generator already lists every emitted
                         .cs file explicitly below, and the SDK's wildcard would otherwise pull in
                         the same files a second time and trip NETSDK1022 ("duplicate Compile
                         items"). Setting it inside the .csproj keeps the property scoped to this
                         project — passing -p:EnableDefaultCompileItems=false on the command line
                         would propagate to Swift.Runtime, which DOES rely on default Compile items. -->
                    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
                  </PropertyGroup>

                  <!-- LibraryImport requires DisableRuntimeMarshalling for Swift interop types -->
                  <ItemGroup>
                    <AssemblyAttribute Include="System.Runtime.CompilerServices.DisableRuntimeMarshallingAttribute" />
                  </ItemGroup>

                  <ItemGroup>
                {runtimeReference}{dependencyRefs}{appleSupplementRef}
                  </ItemGroup>

                  <!-- Generated C# bindings -->
                  <ItemGroup>
                    <Compile Include="{resolvedNamespace}.cs" />{wrappersCompile}{bridgeCompile}
                  </ItemGroup>

                  <!-- ILLink trimmer descriptor — only emitted when the generator produced at
                       least one open-generic ISwiftObject in this module. ILC does not auto-
                       discover descriptors embedded in referenced assemblies, so TrimmerRootDescriptor
                       roots the file for the local NativeAOT publish; EmbeddedResource keeps the
                       descriptor in the shipped assembly so trimmer-mode consumers (PublishTrimmed
                       / IsTrimmable) auto-discover it. Both items are gated on Exists() so they
                       no-op cleanly when the generator wrote no descriptor. -->
                  <ItemGroup Condition="Exists('{TrimmerDescriptorEmitter.FileName}')">
                    <EmbeddedResource Include="{TrimmerDescriptorEmitter.FileName}">
                      <LogicalName>ILLink.Descriptors.xml</LogicalName>
                    </EmbeddedResource>
                    <TrimmerRootDescriptor Include="{TrimmerDescriptorEmitter.FileName}" />
                  </ItemGroup>

                  <!-- NativeReference for local build -->
                  <ItemGroup>{(emitSourceXcfw ? $"""

                    <NativeReference Include="{relativeSourceXcfw}">
                      <Kind>Framework</Kind>
                    </NativeReference>
                """ : "")}{wrapperNativeRef}{bridgeNativeRef}
                  </ItemGroup>{(resourceBundleItems != "" ? $"""

                  <!-- SPM resource bundles (included in app bundle at runtime) -->
                  <ItemGroup>{resourceBundleItems}
                  </ItemGroup>
                """ : "")}

                  <!-- NuGet pack layout -->
                  <ItemGroup>
                    <None Include="{packageId}.targets" Pack="true"
                          PackagePath="{pi.GetBuildTransitivePath()}" />
                    <!-- Ship the ILLink descriptor loose, adjacent to {packageId}.targets in
                         buildTransitive/ (per-TFM), so the consumer-facing targets can root it via
                         $(MSBuildThisFileDirectory)ILLink.Descriptors.xml for a downstream
                         PackageReference consumer's NativeAOT publish. ILC does not auto-discover
                         the EmbeddedResource copy from a referenced assembly. Exists()-guarded so
                         it no-ops when the module emitted no open generics (no descriptor). -->
                    <None Include="{TrimmerDescriptorEmitter.FileName}" Pack="true"
                          Condition="Exists('{TrimmerDescriptorEmitter.FileName}')"
                          PackagePath="{pi.GetBuildTransitivePath()}" />{(emitSourceXcfw ? $"""

                    <None Include="{packSourceXcfwRelative}/**" Pack="true"
                          PackagePath="{pi.GetNativePackPath($"{options.ModuleName}.xcframework")}" />
                """ : "")}{wrapperPackItem}{bridgePackItem}{resourceBundlePackItems}
                  </ItemGroup>{objcProjectRef}
                </Project>
                """;

            File.WriteAllText(csprojPath, content);
            logger.LogInformation("Wrote binding project to {Path}", csprojPath);
        }

        /// <summary>
        /// Builds a NuGet bounded version range that floats forward across patch releases of
        /// SwiftBindings.Runtime but slams shut at the next minor. Pre-1.0 SwiftBindings.Runtime
        /// patch releases are an internal ABI-stable contract; minor bumps are explicitly
        /// allowed to break ABI. Examples:
        ///   "0.8.0"          → "[0.8.0,0.9.0)"
        ///   "0.10.0"         → "[0.10.0,0.11.0)"
        ///   "0.8.0-preview.1" → "[0.8.0-preview.1,0.9.0)"
        /// Falls back to the raw version string if the input doesn't look like a parseable
        /// SemVer (e.g. someone passes "1" or "garbage") — defensive, so we never crash
        /// the emitter on a malformed input from a downstream tool.
        /// </summary>
        private static string XmlEscape(string value)
        {
            return value
                .Replace("&", "&amp;")
                .Replace("<", "&lt;")
                .Replace(">", "&gt;")
                .Replace("\"", "&quot;")
                .Replace("'", "&apos;");
        }

        internal static string BuildBoundedRuntimeVersionRange(string version)
        {
            return RuntimeVersionRange.Build(version);
        }
    }
}
