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
        /// Whether a bridge xcframework was compiled for SwiftUI views.
        /// </summary>
        public bool HasBridgeXCFramework { get; init; }
        /// <summary>
        /// Absolute path to the source xcframework. Used to compute the relative path
        /// from the output directory in the ProjectReference.targets file.
        /// </summary>
        public string? XcframeworkPath { get; init; }
        /// <summary>
        /// Platform info for multi-platform support. Falls back to iOS if not specified (CLI default).
        /// </summary>
        public PlatformInfo? PlatformInfo { get; init; }
        /// <summary>
        /// SPM resource bundle names detected in the source framework.
        /// When non-empty, BundleResource items are emitted to include bundles from the NuGet package.
        /// </summary>
        public IReadOnlyList<string>? ResourceBundleNames { get; init; }
    }

    /// <summary>
    /// Emits a .targets file for NuGet consumers that injects NativeReference items
    /// and validates platform version requirements.
    /// </summary>
    public static class ConsumerTargetsEmitter
    {
        // The maccatalyst-x64 Mono JIT instability workaround (upstream-issue-04).
        // Injected into both the nupkg buildTransitive .targets and the local
        // .ProjectReference.targets so PR-based local development is also covered.
        // The <Message> shows the consumer-visible current values of MtouchInterpreter
        // and UseInterpreter; if a consumer pre-set either, the default-only inner
        // conditions leave their value intact and the message still reads truthfully.
        internal const string MacCatalystX64Workaround = """
                  <!-- maccatalyst-x64 Mono JIT instability workaround (upstream-issue-04).
                       The Mono x64 workload runtime that ships with Microsoft.iOS.Sdk has
                       four confirmed deterministic JIT crash classes during Swift interop
                       on maccatalyst-x64 (not present on osx-x64, maccatalyst-arm64, or
                       any other RID). Defaulting to the Mono interpreter bypasses all
                       four because each lives in the JIT subsystem.
                       Tracked at: src/docs/Future/upstream-issue-04-mono-catalyst-x64-instability.md
                       Public docs: https://github.com/justinwojo/swift-dotnet-bindings/wiki/Known-Limitations
                       Opt out (to probe an upstream Mono fix):
                         <SwiftBindingsMacCatalystX64UseJit>true</SwiftBindingsMacCatalystX64UseJit> -->
                  <PropertyGroup Condition="'$(RuntimeIdentifier)' == 'maccatalyst-x64' AND '$(SwiftBindingsMacCatalystX64UseJit)' != 'true'">
                    <MtouchInterpreter Condition="'$(MtouchInterpreter)' == ''">all</MtouchInterpreter>
                    <UseInterpreter Condition="'$(UseInterpreter)' == ''">true</UseInterpreter>
                  </PropertyGroup>
                  <Target Name="_SwiftBindingsAnnounceMacCatalystX64Workaround"
                          BeforeTargets="Build"
                          Condition="'$(RuntimeIdentifier)' == 'maccatalyst-x64'
                                     AND '$(SwiftBindingsMacCatalystX64UseJit)' != 'true'
                                     AND '$(_SwiftBindingsMacCatalystX64WorkaroundAnnounced)' != 'true'">
                    <PropertyGroup>
                      <_SwiftBindingsMacCatalystX64WorkaroundAnnounced>true</_SwiftBindingsMacCatalystX64WorkaroundAnnounced>
                    </PropertyGroup>
                    <Message Importance="high"
                             Text="SwiftBindings: maccatalyst-x64 defaults MtouchInterpreter and UseInterpreter to interpreter mode (current: MtouchInterpreter='$(MtouchInterpreter)', UseInterpreter='$(UseInterpreter)') — the Mono x64 JIT has four confirmed crash classes on this RID (upstream-issue-04). Docs: https://github.com/justinwojo/swift-dotnet-bindings/wiki/Known-Limitations. Set &lt;SwiftBindingsMacCatalystX64UseJit&gt;true&lt;/SwiftBindingsMacCatalystX64UseJit&gt; to opt back into the JIT path (will crash until upstream fix lands)." />
                  </Target>
            """;

        /// <summary>
        /// Emits a {PackageId}.targets file into the output directory.
        /// This file is packaged into buildTransitive/{tfm}/ in the NuGet (e.g. net10.0-ios, net10.0-macos).
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

            // Native-macOS exclusion: the SwiftUI bridge is UIKit-only, so its macOS
            // slice is an empty Mach-O. Referencing it makes a native-macOS consumer
            // fail with MT158. Mac Catalyst keeps it — its TFM does not contain "-macos".
            var bridgeNativeRef = options.HasBridgeXCFramework
                ? $"""
                          <NativeReference Include="$(MSBuildThisFileDirectory)../../runtimes/{pi.NuGetRid}/native/{options.ModuleName}Bridge.xcframework"
                                           Condition="Exists('$(MSBuildThisFileDirectory)../../runtimes/{pi.NuGetRid}/native/{options.ModuleName}Bridge.xcframework') AND !$(TargetFramework.Contains('-macos'))">
                            <Kind>Framework</Kind>
                          </NativeReference>
                """
                : "";

            // SPM resource bundle items for NuGet consumers
            var resourceBundleItems = "";
            if (options.ResourceBundleNames != null && options.ResourceBundleNames.Count > 0)
            {
                foreach (var bundleName in options.ResourceBundleNames)
                {
                    resourceBundleItems += $"""

                      <BundleResource Include="$(MSBuildThisFileDirectory)../../runtimes/{pi.NuGetRid}/native/{bundleName}.bundle/**"
                                      LinkBase="{bundleName}.bundle"
                                      Condition="Exists('$(MSBuildThisFileDirectory)../../runtimes/{pi.NuGetRid}/native/{bundleName}.bundle')" />
                """;
                }
            }

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

                {MacCatalystX64Workaround}

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
                {wrapperNativeRef}{bridgeNativeRef}{resourceBundleItems}    </ItemGroup>
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
        ///   &lt;Import Project="path/to/obj/Debug/{tfm}/swift-binding/{PackageId}.ProjectReference.targets"
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

            // Native-macOS exclusion (see Emit): UIKit-only bridge → empty macOS slice → MT158.
            var bridgeNativeRef = options.HasBridgeXCFramework
                ? $"""
                          <NativeReference Include="$(MSBuildThisFileDirectory){options.ModuleName}Bridge.xcframework"
                                           Condition="Exists('$(MSBuildThisFileDirectory){options.ModuleName}Bridge.xcframework') AND !$(TargetFramework.Contains('-macos'))">
                            <Kind>Framework</Kind>
                          </NativeReference>
                """
                : "";

            // SPM resource bundle items for ProjectReference consumers
            var localResourceBundleItems = "";
            if (options.ResourceBundleNames != null && options.ResourceBundleNames.Count > 0)
            {
                foreach (var bundleName in options.ResourceBundleNames)
                {
                    localResourceBundleItems += $"""

                          <BundleResource Include="$(MSBuildThisFileDirectory){bundleName}.bundle/**"
                                          LinkBase="{bundleName}.bundle"
                                          Condition="Exists('$(MSBuildThisFileDirectory){bundleName}.bundle')" />
                """;
                }
            }

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
                       in .NET Apple platforms, so this file injects them at build time.

                       The NativeReference items are inside a Target (not a static ItemGroup) to ensure
                       the Exists() conditions evaluate AFTER the library project builds — on a clean build,
                       the wrapper xcframework doesn't exist at MSBuild evaluation time. -->

                {MacCatalystX64Workaround}

                  <!-- Idempotency guard -->
                  <Target Name="_ResolveLocal{sanitized}NativeReferences"
                          BeforeTargets="ResolveNativeReferences"
                          DependsOnTargets="ResolveProjectReferences"
                          Condition="'$(_SwiftBinding_{sanitized}_Injected)' != 'true'">
                    <PropertyGroup>
                      <_SwiftBinding_{sanitized}_Injected>true</_SwiftBinding_{sanitized}_Injected>
                    </PropertyGroup>
                    <ItemGroup>
                {sourceXcfwRef}{wrapperNativeRef}{bridgeNativeRef}{localResourceBundleItems}    </ItemGroup>
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
