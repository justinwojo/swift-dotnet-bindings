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
                    <TargetFramework>{pi.Tfm}</TargetFramework>
                    <ImplicitUsings>enable</ImplicitUsings>
                    <Nullable>enable</Nullable>
                    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
                    <IsPackable>true</IsPackable>
                    <PackageId>{packageId}</PackageId>{versionComment}
                    <PackageVersion>{options.Metadata.PackageVersion}</PackageVersion>
                    <SupportedOSPlatformVersion>{options.Metadata.EffectiveMinimumOSVersion}</SupportedOSPlatformVersion>
                    <NoWarn>CS0169;CA1420</NoWarn>
                  </PropertyGroup>

                  <!-- LibraryImport requires DisableRuntimeMarshalling for Swift interop types -->
                  <ItemGroup>
                    <AssemblyAttribute Include="System.Runtime.CompilerServices.DisableRuntimeMarshallingAttribute" />
                  </ItemGroup>

                  <ItemGroup>
                    <PackageReference Include="SwiftBindings.Runtime" Version="{runtimeVersion}" />{dependencyRefs}
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
    }
}
