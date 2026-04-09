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
    }

    /// <summary>
    /// Emits a .csproj file for a Swift binding project, ready for `dotnet build` and `dotnet pack`.
    /// </summary>
    public static class BindingProjectEmitter
    {
        internal const string DefaultSwiftRuntimeVersion = "0.0.0-dev";

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
            var outputDirFull = Path.GetFullPath(options.OutputDirectory);
            var relativeSourceXcfw = hasSourceXcfw
                ? Path.GetRelativePath(outputDirFull, Path.GetFullPath(options.SourceXCFrameworkPath!))
                : null;

            // Version placeholder warning comment
            var versionComment = options.Metadata.IsVersionPlaceholder
                ? "\n    <!-- WARNING: Version is a placeholder (Xcode default). Set PackageVersion manually. -->"
                : "";

            // Wrapper xcframework items (conditional)
            var hasWrapper = options.WrapperXCFrameworkPath != null &&
                             Directory.Exists(options.WrapperXCFrameworkPath);
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
            var hasBridge = options.HasBridgeSwift ||
                            (options.BridgeXCFrameworkPath != null && Directory.Exists(options.BridgeXCFrameworkPath));
            var bridgeModuleName = $"{options.ModuleName}Bridge";

            var bridgeNativeRef = hasBridge
                ? $"""

                    <NativeReference Include="{bridgeModuleName}.xcframework"
                                     Condition="Exists('{bridgeModuleName}.xcframework')">
                      <Kind>Framework</Kind>
                    </NativeReference>
                """
                : "";

            var bridgePackItem = hasBridge
                ? $"""

                    <None Include="{bridgeModuleName}.xcframework/**" Pack="true"
                          Condition="Exists('{bridgeModuleName}.xcframework')"
                          PackagePath="{pi.GetNativePackPath($"{bridgeModuleName}.xcframework")}" />
                """
                : "";

            // Optional compile items
            var wrappersCompile = $"""

                    <Compile Include="{resolvedNamespace}.Wrappers.cs"
                             Condition="Exists('{resolvedNamespace}.Wrappers.cs')" />
                """;
            var bridgeCompile = $"""

                    <Compile Include="{resolvedNamespace}.SwiftUIBridge.cs"
                             Condition="Exists('{resolvedNamespace}.SwiftUIBridge.cs')" />
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
            // pulls in the Swift.Runtime project's `<Content Include="../native/.../libSwift
            // BindingsRuntime.dylib">` items, which copy the concurrency runtime dylib into
            // the consumer's output (and into Frameworks/ on iOS/tvOS/maccatalyst). A bare
            // assembly reference would compile cleanly but ship a project that can't load
            // its native runtime at run/pack time. When the property is unset, we fall back
            // to an exact-version PackageReference (`[0.0.0-dev]`) so the failure mode is a
            // clean NU1102 ("package not found"), not a silent stale binding.
            //
            // For any real version the PackageReference path is unchanged — published
            // consumers see the same csproj they always did, and the published nupkg's
            // `buildTransitive/SwiftBindings.Runtime.targets` carries the same dylib-copy
            // logic for them.
            var runtimeReference = runtimeVersion == DefaultSwiftRuntimeVersion
                ? $"""
                    <!-- Local-dev wiring: 0.0.0-dev has no published nupkg. Set
                         SwiftBindingsRepoRoot (-p:SwiftBindingsRepoRoot=/path/to/swift-bindings
                         or in Directory.Build.props) to bind against the in-tree Swift.Runtime
                         project. The ProjectReference form (not a bare HintPath) is required
                         so the in-tree project's native dylib copy items flow through to the
                         consumer — without it, builds compile but ship without
                         libSwiftBindingsRuntime.dylib. Without the property, the build falls
                         back to an exact-version PackageReference (`[0.0.0-dev]`), which fails
                         with a clear "package not found" error instead of silently resolving to
                         a stale cached SwiftBindings.Runtime nupkg under ~/.nuget/packages/. -->
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

            // ObjC binding project reference for mixed frameworks
            var objcProjectRef = "";
            if (!string.IsNullOrEmpty(options.ObjCProjectFileName))
            {
                objcProjectRef = $"""

                  <!-- ObjC binding project reference (mixed framework) -->
                  <ItemGroup Condition="Exists('{options.ObjCProjectFileName}')">
                    <ProjectReference Include="{options.ObjCProjectFileName}" />
                  </ItemGroup>
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
                    <NoWarn>CS0169;CA1420</NoWarn>
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
                {runtimeReference}{dependencyRefs}
                  </ItemGroup>

                  <!-- Generated C# bindings -->
                  <ItemGroup>
                    <Compile Include="{resolvedNamespace}.cs" />{wrappersCompile}{bridgeCompile}
                  </ItemGroup>

                  <!-- NativeReference for local build -->
                  <ItemGroup>{(hasSourceXcfw ? $"""

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
                          PackagePath="{pi.GetBuildTransitivePath()}" />{(hasSourceXcfw ? $"""

                    <None Include="{relativeSourceXcfw}/**" Pack="true"
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
        internal static string BuildBoundedRuntimeVersionRange(string version)
        {
            var firstDot = version.IndexOf('.');
            if (firstDot <= 0) return version;
            var majorStr = version.Substring(0, firstDot);
            // Validate the major component is a plain integer too — `"x.8.0"` would
            // otherwise produce `[x.8.0,x.9.0)`, which NuGet rejects at restore time.
            // Both halves must parse cleanly or the range is meaningless.
            if (!int.TryParse(majorStr, out _)) return version;
            var rest = version.Substring(firstDot + 1);
            var secondDot = rest.IndexOf('.');
            // The substring before the second dot may carry a pre-release suffix (e.g. "8-preview"),
            // but minor must be a plain integer for the +1 to make sense — so strip nothing,
            // and reject the input via TryParse below if it isn't a clean integer.
            var minorStr = secondDot < 0 ? rest : rest.Substring(0, secondDot);
            if (!int.TryParse(minorStr, out var minor)) return version;
            return $"[{version},{majorStr}.{minor + 1}.0)";
        }
    }
}
