// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;

namespace BindingsGeneration
{
    /// <summary>
    /// Options for consumer .targets file emission.
    /// </summary>
    public sealed class ConsumerTargetsEmitterOptions
    {
        public required string OutputDirectory { get; init; }
        public required string ModuleName { get; init; }
        public required string PackageId { get; init; }
        public required string EffectiveMinimumOSVersion { get; init; }
        public required bool HasWrapperXCFramework { get; init; }
        /// <summary>
        /// Absolute path to the source xcframework. Used to compute the relative path
        /// from the output directory in the ProjectReference.targets file.
        /// </summary>
        public string? XcframeworkPath { get; init; }
        /// <summary>
        /// Platform info for multi-platform support. Defaults to iOS if not specified.
        /// </summary>
        public PlatformInfo? PlatformInfo { get; init; }
    }

    /// <summary>
    /// Emits a .targets file for NuGet consumers that injects NativeReference items
    /// and validates platform version requirements.
    /// </summary>
    public static class ConsumerTargetsEmitter
    {
        /// <summary>
        /// Emits a {PackageId}.targets file into the output directory.
        /// This file is packaged into buildTransitive/net10.0-ios/ in the NuGet.
        /// Also emits a {PackageId}.ProjectReference.targets file for local ProjectReference
        /// consumers, which uses paths relative to the intermediate output directory.
        /// </summary>
        public static void Emit(ConsumerTargetsEmitterOptions options, ILogger logger)
        {
            var pi = options.PlatformInfo ?? PlatformInfoFactory.Create(ApplePlatform.iOS);
            var sanitized = SanitizeModuleName(options.ModuleName);
            var targetsPath = Path.Combine(options.OutputDirectory, $"{options.PackageId}.targets");

            var wrapperNativeRef = options.HasWrapperXCFramework
                ? $"""
                          <NativeReference Include="$(MSBuildThisFileDirectory)../../runtimes/{pi.NuGetRid}/native/{options.ModuleName}SwiftBindings.xcframework"
                                           Condition="Exists('$(MSBuildThisFileDirectory)../../runtimes/{pi.NuGetRid}/native/{options.ModuleName}SwiftBindings.xcframework')">
                            <Kind>Framework</Kind>
                          </NativeReference>
                """
                : "";

            var content = $"""
                <Project>
                  <!-- SwiftBindingsInteropMode: Auto (default) | Safe | Direct
                       Auto: NativeAOT (PublishAot=true) -> Direct, everything else -> Safe
                       Direct: Suppresses Mono JIT safety warnings (SB0001) - clean API for NativeAOT
                       Safe: Shows Mono JIT safety warnings - protects simulator/Mono builds -->
                  <PropertyGroup>
                    <SwiftBindingsInteropMode Condition="'$(SwiftBindingsInteropMode)' == ''">Auto</SwiftBindingsInteropMode>
                  </PropertyGroup>
                  <!-- Auto mode resolution: PublishAot=true -> Direct, else -> Safe -->
                  <PropertyGroup Condition="'$(SwiftBindingsInteropMode)' == 'Auto' AND '$(PublishAot)' == 'true'">
                    <SwiftBindingsInteropMode>Direct</SwiftBindingsInteropMode>
                  </PropertyGroup>
                  <PropertyGroup Condition="'$(SwiftBindingsInteropMode)' == 'Auto'">
                    <SwiftBindingsInteropMode>Safe</SwiftBindingsInteropMode>
                  </PropertyGroup>
                  <!-- Direct mode: suppress CallConvSwift fallback warnings (safe on NativeAOT) -->
                  <PropertyGroup Condition="'$(SwiftBindingsInteropMode)' == 'Direct'">
                    <NoWarn>$(NoWarn);SB0001</NoWarn>
                  </PropertyGroup>

                  <!-- Idempotency guard: prevents duplicate injection when multiple projects reference the same package -->
                  <Target Name="_Resolve{sanitized}NativeReferences"
                          BeforeTargets="ResolveNativeReferences"
                          Condition="'$(_SwiftBinding_{sanitized}_Injected)' != 'true'">
                    <PropertyGroup>
                      <_SwiftBinding_{sanitized}_Injected>true</_SwiftBinding_{sanitized}_Injected>
                    </PropertyGroup>
                    <ItemGroup>
                      <NativeReference Include="$(MSBuildThisFileDirectory)../../runtimes/{pi.NuGetRid}/native/{options.ModuleName}.xcframework"
                                       Condition="Exists('$(MSBuildThisFileDirectory)../../runtimes/{pi.NuGetRid}/native/{options.ModuleName}.xcframework')">
                        <Kind>Framework</Kind>
                      </NativeReference>
                {wrapperNativeRef}    </ItemGroup>
                  </Target>

                  <!-- SwiftBindingFramework registration for downstream tooling -->
                  <ItemGroup>
                    <SwiftBindingFramework Include="{options.ModuleName}">
                      <SourcePackage>{options.PackageId}</SourcePackage>
                    </SwiftBindingFramework>
                  </ItemGroup>

                  <!-- Module database for cross-module type resolution -->
                  <ItemGroup>
                    <SwiftModuleDatabase Include="$(MSBuildThisFileDirectory){options.ModuleName}Database.xml"
                                         Condition="Exists('$(MSBuildThisFileDirectory){options.ModuleName}Database.xml')">
                      <ModuleName>{options.ModuleName}</ModuleName>
                      <SourcePackage>{options.PackageId}</SourcePackage>
                    </SwiftModuleDatabase>
                  </ItemGroup>

                  <!-- Platform version warning (SWIFTBIND011) -->
                  <Target Name="_Validate{sanitized}PlatformVersion" BeforeTargets="Build"
                          Condition="'$(SupportedOSPlatformVersion)' != '' AND $([System.Version]::Parse('$(SupportedOSPlatformVersion)').CompareTo($([System.Version]::Parse('{options.EffectiveMinimumOSVersion}')))) &lt; 0">
                    <Warning Text="{options.PackageId} requires {pi.Platform} {options.EffectiveMinimumOSVersion}+, but SupportedOSPlatformVersion is '$(SupportedOSPlatformVersion)'. Update your project's SupportedOSPlatformVersion to at least {options.EffectiveMinimumOSVersion}."
                             Code="SWIFTBIND011" />
                  </Target>
                </Project>
                """;

            File.WriteAllText(targetsPath, content);
            logger.LogInformation("Wrote consumer targets to {Path}", targetsPath);

            // Also emit a ProjectReference-friendly targets file.
            // NativeReference doesn't propagate through ProjectReference in .NET iOS,
            // so consuming projects that use ProjectReference (local development) need
            // to import this file. It uses $(MSBuildThisFileDirectory) to resolve paths
            // relative to the intermediate output directory where the xcframeworks live.
            EmitProjectReferenceTargets(options, pi, sanitized, logger);
        }

        /// <summary>
        /// Emits a {PackageId}.ProjectReference.targets file for local ProjectReference consumers.
        /// Uses paths relative to the intermediate output directory (where this file lives).
        /// Consuming projects import this via:
        ///   &lt;Import Project="path/to/obj/Debug/net10.0-ios/swift-binding/{PackageId}.ProjectReference.targets"
        ///           Condition="Exists('...')" /&gt;
        /// </summary>
        private static void EmitProjectReferenceTargets(
            ConsumerTargetsEmitterOptions options, PlatformInfo pi, string sanitized, ILogger logger)
        {
            var localTargetsPath = Path.Combine(options.OutputDirectory, $"{options.PackageId}.ProjectReference.targets");

            var wrapperNativeRef = options.HasWrapperXCFramework
                ? $"""
                          <NativeReference Include="$(MSBuildThisFileDirectory){options.ModuleName}SwiftBindings.xcframework"
                                           Condition="Exists('$(MSBuildThisFileDirectory){options.ModuleName}SwiftBindings.xcframework')">
                            <Kind>Framework</Kind>
                          </NativeReference>
                """
                : "";

            // Compute the relative path from the output directory to the source xcframework.
            // This avoids hardcoding directory traversal depth, which breaks if the consumer
            // customizes IntermediateOutputPath or BaseIntermediateOutputPath.
            var sourceXcfwRef = "";
            if (options.XcframeworkPath != null)
            {
                var outputFullPath = Path.GetFullPath(options.OutputDirectory);
                var xcfwFullPath = Path.GetFullPath(options.XcframeworkPath);
                var relativePath = Path.GetRelativePath(outputFullPath, xcfwFullPath);
                sourceXcfwRef = $"""
                      <NativeReference Include="$(MSBuildThisFileDirectory){relativePath}"
                                       Condition="Exists('$(MSBuildThisFileDirectory){relativePath}')">
                        <Kind>Framework</Kind>
                      </NativeReference>
                """;
            }

            var content = $"""
                <Project>
                  <!-- ProjectReference consumer targets for {options.PackageId}.
                       Import this file in projects that reference this library via ProjectReference
                       (local development). NativeReference items don't propagate through ProjectReference
                       in .NET iOS, so this file injects them at build time.

                       The NativeReference items are inside a Target (not a static ItemGroup) to ensure
                       the Exists() conditions evaluate AFTER the library project builds — on a clean build,
                       the wrapper xcframework doesn't exist at MSBuild evaluation time. -->

                  <!-- Idempotency guard -->
                  <Target Name="_ResolveLocal{sanitized}NativeReferences"
                          BeforeTargets="ResolveNativeReferences"
                          DependsOnTargets="ResolveProjectReferences"
                          Condition="'$(_SwiftBinding_{sanitized}_Injected)' != 'true'">
                    <PropertyGroup>
                      <_SwiftBinding_{sanitized}_Injected>true</_SwiftBinding_{sanitized}_Injected>
                    </PropertyGroup>
                    <ItemGroup>
                {sourceXcfwRef}{wrapperNativeRef}    </ItemGroup>
                  </Target>
                </Project>
                """;

            File.WriteAllText(localTargetsPath, content);
            logger.LogInformation("Wrote ProjectReference consumer targets to {Path}", localTargetsPath);
        }

        /// <summary>
        /// Sanitizes a module name for use in MSBuild target/property names.
        /// Replaces dots, hyphens, and spaces with underscores.
        /// </summary>
        internal static string SanitizeModuleName(string moduleName)
        {
            return moduleName
                .Replace('.', '_')
                .Replace('-', '_')
                .Replace(' ', '_');
        }
    }
}
