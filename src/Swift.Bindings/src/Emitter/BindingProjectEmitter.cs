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
        public required string SourceXCFrameworkPath { get; init; }
        public string? WrapperXCFrameworkPath { get; init; }
        public string? SwiftRuntimeVersion { get; init; }
        /// <summary>
        /// Framework dependencies that should be emitted as PackageReference items.
        /// </summary>
        public IReadOnlyList<FrameworkDependencyInfo>? Dependencies { get; init; }
    }

    /// <summary>
    /// Emits a .csproj file for a Swift binding project, ready for `dotnet build` and `dotnet pack`.
    /// </summary>
    public static class BindingProjectEmitter
    {
        internal const string DefaultSwiftRuntimeVersion = "0.1.0-preview.1";

        /// <summary>
        /// Emits a {PackageId}.csproj file into the output directory.
        /// </summary>
        public static void Emit(BindingProjectEmitterOptions options, ILogger logger)
        {
            var packageId = $"{options.ModuleName}.Swift.iOS";
            var runtimeVersion = options.SwiftRuntimeVersion ?? DefaultSwiftRuntimeVersion;
            var csprojPath = Path.Combine(options.OutputDirectory, $"{packageId}.csproj");

            // Compute relative path from output dir to source xcframework
            var outputDirFull = Path.GetFullPath(options.OutputDirectory);
            var sourceXcfwFull = Path.GetFullPath(options.SourceXCFrameworkPath);
            var relativeSourceXcfw = Path.GetRelativePath(outputDirFull, sourceXcfwFull);

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
                          PackagePath="runtimes/ios-arm64/native/{wrapperModuleName}.xcframework/" />
                """
                : "";

            // Optional compile items
            var wrappersCompile = $"""

                    <Compile Include="Swift.{options.ModuleName}.Wrappers.cs"
                             Condition="Exists('Swift.{options.ModuleName}.Wrappers.cs')" />
                """;
            var bridgeCompile = $"""

                    <Compile Include="Swift.{options.ModuleName}.SwiftUIBridge.cs"
                             Condition="Exists('Swift.{options.ModuleName}.SwiftUIBridge.cs')" />
                """;

            // Build dependency PackageReference items
            var dependencyRefs = "";
            if (options.Dependencies != null && options.Dependencies.Count > 0)
            {
                foreach (var dep in options.Dependencies)
                {
                    var depComment = dep.EffectiveVersion == "0.0.0"
                        ? "\n    <!-- WARNING: Placeholder version. Update before publishing. -->"
                        : "";
                    dependencyRefs += $"""

                    <PackageReference Include="{dep.EffectivePackageId}" Version="{dep.EffectiveVersion}" />{depComment}
                """;
                }
            }

            var content = $"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <OutputType>Library</OutputType>
                    <TargetFramework>net10.0-ios</TargetFramework>
                    <ImplicitUsings>enable</ImplicitUsings>
                    <Nullable>enable</Nullable>
                    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
                    <IsPackable>true</IsPackable>
                    <PackageId>{packageId}</PackageId>{versionComment}
                    <PackageVersion>{options.Metadata.PackageVersion}</PackageVersion>
                    <SupportedOSPlatformVersion>{options.Metadata.EffectiveMinimumOSVersion}</SupportedOSPlatformVersion>
                    <NoWarn>CS0169</NoWarn>
                  </PropertyGroup>

                  <ItemGroup>
                    <PackageReference Include="Swift.Runtime" Version="{runtimeVersion}" />{dependencyRefs}
                  </ItemGroup>

                  <!-- Generated C# bindings -->
                  <ItemGroup>
                    <Compile Include="Swift.{options.ModuleName}.cs" />{wrappersCompile}{bridgeCompile}
                  </ItemGroup>

                  <!-- NativeReference for local build -->
                  <ItemGroup>
                    <NativeReference Include="{relativeSourceXcfw}">
                      <Kind>Framework</Kind>
                    </NativeReference>{wrapperNativeRef}
                  </ItemGroup>

                  <!-- NuGet pack layout -->
                  <ItemGroup>
                    <None Include="{packageId}.targets" Pack="true"
                          PackagePath="buildTransitive/net10.0-ios/" />
                    <None Include="{relativeSourceXcfw}/**" Pack="true"
                          PackagePath="runtimes/ios-arm64/native/{options.ModuleName}.xcframework/" />{wrapperPackItem}
                  </ItemGroup>
                </Project>
                """;

            File.WriteAllText(csprojPath, content);
            logger.LogInformation("Wrote binding project to {Path}", csprojPath);
        }
    }
}
