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
        /// </summary>
        public static void Emit(ConsumerTargetsEmitterOptions options, ILogger logger)
        {
            var sanitized = SanitizeModuleName(options.ModuleName);
            var targetsPath = Path.Combine(options.OutputDirectory, $"{options.PackageId}.targets");

            var wrapperNativeRef = options.HasWrapperXCFramework
                ? $"""
                          <NativeReference Include="$(MSBuildThisFileDirectory)../../runtimes/ios-arm64/native/{options.ModuleName}SwiftBindings.xcframework"
                                           Condition="Exists('$(MSBuildThisFileDirectory)../../runtimes/ios-arm64/native/{options.ModuleName}SwiftBindings.xcframework')">
                            <Kind>Framework</Kind>
                          </NativeReference>
                """
                : "";

            var content = $"""
                <Project>
                  <!-- Idempotency guard: prevents duplicate injection when multiple projects reference the same package -->
                  <Target Name="_Resolve{sanitized}NativeReferences"
                          BeforeTargets="ResolveNativeReferences"
                          Condition="'$(_SwiftBinding_{sanitized}_Injected)' != 'true'">
                    <PropertyGroup>
                      <_SwiftBinding_{sanitized}_Injected>true</_SwiftBinding_{sanitized}_Injected>
                    </PropertyGroup>
                    <ItemGroup>
                      <NativeReference Include="$(MSBuildThisFileDirectory)../../runtimes/ios-arm64/native/{options.ModuleName}.xcframework"
                                       Condition="Exists('$(MSBuildThisFileDirectory)../../runtimes/ios-arm64/native/{options.ModuleName}.xcframework')">
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

                  <!-- Platform version warning (SWIFTBIND010) -->
                  <Target Name="_Validate{sanitized}PlatformVersion" BeforeTargets="Build"
                          Condition="'$(SupportedOSPlatformVersion)' != '' AND $([System.Version]::Parse('$(SupportedOSPlatformVersion)').CompareTo($([System.Version]::Parse('{options.EffectiveMinimumOSVersion}')))) &lt; 0">
                    <Warning Text="{options.PackageId} requires iOS {options.EffectiveMinimumOSVersion}+, but SupportedOSPlatformVersion is '$(SupportedOSPlatformVersion)'. Update your project's SupportedOSPlatformVersion to at least {options.EffectiveMinimumOSVersion}."
                             Code="SWIFTBIND010" />
                  </Target>
                </Project>
                """;

            File.WriteAllText(targetsPath, content);
            logger.LogInformation("Wrote consumer targets to {Path}", targetsPath);
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
