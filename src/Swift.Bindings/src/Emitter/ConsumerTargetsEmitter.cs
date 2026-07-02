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
        /// Native linkage of the source framework. When <see cref="NativeLinkage.Static"/>, the
        /// wrapper is the sole carrier of the framework's ObjC classes (it force-loaded the static
        /// archive), so the source xcframework NativeReference is dropped from BOTH the nupkg
        /// <c>.targets</c> and the local <c>.ProjectReference.targets</c> — injecting it again
        /// would duplicate-register every ObjC class. Dynamic sources keep the reference.
        /// </summary>
        public NativeLinkage SourceNativeLinkage { get; init; } = NativeLinkage.Dynamic;
        /// <summary>
        /// Platform info for multi-platform support. Falls back to iOS if not specified (CLI default).
        /// </summary>
        public PlatformInfo? PlatformInfo { get; init; }
        /// <summary>
        /// SPM resource bundle names detected in the source framework.
        /// When non-empty, BundleResource items are emitted to include bundles from the NuGet package.
        /// </summary>
        public IReadOnlyList<string>? ResourceBundleNames { get; init; }
        /// <summary>
        /// File name (not full path) of the mixed framework's ObjC companion csproj, e.g.
        /// <c>Module.ObjC.iOS.csproj</c>. Non-null only for mixed (ObjC+Swift) frameworks. When
        /// set, the local <c>.ProjectReference.targets</c> injects an assembly <c>&lt;Reference&gt;</c>
        /// to the companion's built output so a ProjectReference consumer's own C# can see the ObjC
        /// types: the standalone Swift csproj references the companion with <c>PrivateAssets="all"</c>
        /// (which blocks both nuspec promotion AND transitive compile-asset flow), and
        /// <c>NativeReference</c> doesn't propagate through ProjectReference, so the companion's
        /// managed surface would otherwise be invisible to the app (CS0246). A plain
        /// <c>&lt;Reference&gt;</c> never promotes to a nuspec <c>&lt;dependency&gt;</c>, preserving the
        /// single-package contract. The companion lives next to this targets file in the output dir.
        /// </summary>
        public string? ObjCCompanionProjectFileName { get; init; }
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

            var wrapperXcfwPath = $"$(MSBuildThisFileDirectory)../../runtimes/{pi.NuGetRid}/native/{options.ModuleName}SwiftBindings.xcframework";
            var wrapperNativeRef = options.HasWrapperXCFramework
                ? $"""
                          <NativeReference Include="{wrapperXcfwPath}"
                                           Condition="Exists('{wrapperXcfwPath}')">
                            <Kind>Framework</Kind>
                          </NativeReference>
                """
                : "";

            // Gap 2: the source xcframework reference is never dropped from these frozen consumer
            // targets. They are written before the SDK's two-pass flow compiles the wrapper and
            // are evaluated later on the consumer's machine, so a baked drop decision would gamble
            // on a wrapper that a soft-failed or skipped compile may never produce — leaving the
            // consumer with no native carrier at all (DllNotFound). Instead a static-archive source
            // paired with a wrapper is referenced as a wrapper-absent fallback: when the wrapper is
            // present the static archive stays inert (its classes are force-loaded into the wrapper,
            // so referencing both would double-register them); when the wrapper is absent the source
            // self-heals as the sole carrier. Dynamic sources and a static source with no wrapper
            // are referenced unconditionally. The pack item (disk-based) keeps the source in tandem
            // when the wrapper is absent at pack, so within an internally-consistent nupkg the
            // runtimes/ path carries whichever xcframework this fallback resolves to: a static
            // source dropped at pack means the wrapper WAS on disk and is therefore packed, so the
            // consumer's Exists(wrapper) is satisfied. (The one unrecoverable case — a nupkg that
            // drops the source yet ships no wrapper — is post-pack corruption, not a pack decision
            // this code can make.) NativePackagingPolicy is the shared authority; the carrier here
            // is the "will be produced" intent (HasWrapperXCFramework).
            var sourceXcfwPath = $"$(MSBuildThisFileDirectory)../../runtimes/{pi.NuGetRid}/native/{options.ModuleName}.xcframework";
            var sourceCondition = NativePackagingPolicy.ResolveConsumerSourceReferenceMode(
                    options.SourceNativeLinkage, options.HasWrapperXCFramework) == SourceReferenceMode.WrapperAbsentFallback
                ? $"!Exists('{wrapperXcfwPath}') AND Exists('{sourceXcfwPath}')"
                : $"Exists('{sourceXcfwPath}')";
            var sourceNativeRef = $"""

                      <NativeReference Include="{sourceXcfwPath}"
                                       Condition="{sourceCondition}">
                        <Kind>Framework</Kind>
                      </NativeReference>
                """;

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
                    <ItemGroup>{sourceNativeRef}
                {wrapperNativeRef}{bridgeNativeRef}{resourceBundleItems}    </ItemGroup>
                  </Target>

                  <!-- NativeAOT trimmer-descriptor delivery (PackageReference consumers, ILC/PublishAot
                       path ONLY). The descriptor is packed loose beside this file in buildTransitive/
                       (per-TFM), so $(MSBuildThisFileDirectory){TrimmerDescriptorEmitter.FileName}
                       resolves on the consumer. The IL trimmer (PublishTrimmed without AOT) auto-
                       discovers the descriptor embedded as an EmbeddedResource in the binding assembly,
                       so that path needs nothing here. ILC does NOT auto-discover embedded descriptors
                       from a referenced assembly, so root the loose copy via an IlcArg descriptor entry,
                       plus a TrimmerRootDescriptor for the IL-trimmer sub-pass ILC runs internally. Both
                       are gated on PublishAot; under pure PublishTrimmed the embedded resource covers it.
                       The descriptor is a static package file that exists at the consumer's evaluation
                       time, so a top-level ItemGroup is correct here (unlike the ProjectReference path,
                       where the descriptor is generated late). Exists()-guarded so a binding with no open
                       generics — hence no descriptor packed — no-ops cleanly. -->
                  <ItemGroup Condition="'$(PublishAot)' == 'true' AND Exists('$(MSBuildThisFileDirectory){TrimmerDescriptorEmitter.FileName}')">
                    <TrimmerRootDescriptor Include="$(MSBuildThisFileDirectory){TrimmerDescriptorEmitter.FileName}" />
                    <IlcArg Include="--descriptor:$(MSBuildThisFileDirectory){TrimmerDescriptorEmitter.FileName}" />
                  </ItemGroup>

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

            var wrapperXcfwPath = $"$(MSBuildThisFileDirectory){options.ModuleName}SwiftBindings.xcframework";
            var wrapperNativeRef = options.HasWrapperXCFramework
                ? $"""
                          <NativeReference Include="{wrapperXcfwPath}"
                                           Condition="Exists('{wrapperXcfwPath}')">
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
            //
            // Gap 2: like the nupkg .targets, the source xcframework reference is never dropped —
            // a static-archive source paired with a wrapper is referenced as a wrapper-absent
            // fallback. This local PR path points at the on-disk source that DOES exist, so the
            // !Exists(wrapper) guard is what keeps the static archive inert while the wrapper is
            // present (referencing both would duplicate-register its classes); if the wrapper
            // compile soft-failed, the source self-heals as the sole carrier. Dynamic sources and
            // a static source with no wrapper are referenced unconditionally. NativePackagingPolicy
            // is the shared authority; carrier is the "will be produced" intent.
            var sourceXcfwRef = "";
            if (options.XcframeworkPath != null)
            {
                var outputFullPath = Path.GetFullPath(options.OutputDirectory);
                var xcfwFullPath = Path.GetFullPath(options.XcframeworkPath);
                var relativePath = Path.GetRelativePath(outputFullPath, xcfwFullPath);
                var sourceXcfwPath = $"$(MSBuildThisFileDirectory){relativePath}";
                var sourceCondition = NativePackagingPolicy.ResolveConsumerSourceReferenceMode(
                        options.SourceNativeLinkage, options.HasWrapperXCFramework) == SourceReferenceMode.WrapperAbsentFallback
                    ? $"!Exists('{wrapperXcfwPath}') AND Exists('{sourceXcfwPath}')"
                    : $"Exists('{sourceXcfwPath}')";
                sourceXcfwRef = $"""
                      <NativeReference Include="{sourceXcfwPath}"
                                       Condition="{sourceCondition}">
                        <Kind>Framework</Kind>
                      </NativeReference>
                """;
            }

            // Mixed (ObjC+Swift) companion managed reference (path c). The standalone Swift csproj
            // references the companion with PrivateAssets="all", which blocks nuspec promotion AND
            // the transitive flow of the companion's compile assets to the app; NativeReference also
            // doesn't propagate through ProjectReference. So a ProjectReference consumer's own C#
            // can't see the ObjC types (CS0246) unless we inject an explicit assembly Reference to
            // the companion's built output. GetTargetPath returns that output (the companion is built
            // out-of-band by the referenced binding's own _BuildMixedObjCCompanion, which runs on every
            // binding build; DependsOnTargets="ResolveProjectReferences" guarantees that ran first). A
            // <Reference> never promotes to a nuspec <dependency>, so the one-xcframework → one-package
            // contract is preserved. Emitted only for mixed frameworks.
            //
            // RemoveProperties MUST strip RuntimeIdentifier as well as TargetFramework. The companion is
            // a managed-only library with a single TFM and no <RuntimeIdentifiers>, so it always builds
            // RID-agnostic (…/<tfm>/<name>.dll). But a consumer app builds RID-specific, and when its RID
            // arrives as a GLOBAL property (e.g. the CI harness's `-p:RuntimeIdentifier=…`) it propagates
            // through this <MSBuild> task unless removed — making GetTargetPath report a RID-qualified
            // path (…/<tfm>/<rid>/<name>.dll) that does not exist. RAR then silently drops the missing
            // reference and the ObjC types fail to resolve (CS0012/CS0246 — the exact symptom on a mixed
            // ProjectReference consumer built with a command-line RID). Forwarding the RID is also unsafe
            // for a Build here: the companion's restore has no RID target, so a RID-specific build fails
            // NETSDK1047. Stripping the RID makes the query resolve the actual RID-agnostic output.
            var companionReferenceTarget = options.ObjCCompanionProjectFileName == null
                ? ""
                : $"""

                  <!-- Surface the mixed framework's ObjC companion managed types to this
                       ProjectReference consumer's compile (and copy it to the app output for
                       runtime). See ObjCCompanionProjectFileName for why an explicit <Reference>
                       is required and why it is safe (no nuspec <dependency>). Runs before
                       ResolveAssemblyReferences so RAR picks up the injected reference. -->
                  <Target Name="_ResolveLocal{sanitized}ObjCCompanionReference"
                          BeforeTargets="ResolveAssemblyReferences"
                          DependsOnTargets="ResolveProjectReferences"
                          Condition="'$(_SwiftBinding_{sanitized}_ObjCCompanionReferenced)' != 'true' AND Exists('$(MSBuildThisFileDirectory){options.ObjCCompanionProjectFileName}')">
                    <PropertyGroup>
                      <_SwiftBinding_{sanitized}_ObjCCompanionReferenced>true</_SwiftBinding_{sanitized}_ObjCCompanionReferenced>
                    </PropertyGroup>
                    <MSBuild Projects="$(MSBuildThisFileDirectory){options.ObjCCompanionProjectFileName}"
                             Targets="GetTargetPath"
                             Properties="Configuration=$(Configuration)"
                             RemoveProperties="TargetFramework;RuntimeIdentifier"
                             BuildInParallel="false">
                      <Output TaskParameter="TargetOutputs" ItemName="_SwiftBinding{sanitized}ObjCCompanionAssembly" />
                    </MSBuild>
                    <!-- Fail closed (non-pack sibling of SWIFTBIND039/041): this target only runs
                         when the companion csproj exists, so an empty GetTargetPath result means the
                         companion failed to build. A silent no-op would surface as a confusing CS0246
                         on the ObjC types in the ProjectReference consumer's own sources; fail loudly
                         instead so the real cause (companion build failure) is actionable. -->
                    <Error Condition="'@(_SwiftBinding{sanitized}ObjCCompanionAssembly)' == ''"
                           Code="SWIFTBIND042"
                           Text="Mixed-framework ObjC companion '{options.ObjCCompanionProjectFileName}' is present but produced no built assembly to reference. The ObjC types would be unresolved (CS0246) in this project's compile. Rebuild the referenced Swift binding so its companion csproj restores and builds." />
                    <ItemGroup>
                      <Reference Include="@(_SwiftBinding{sanitized}ObjCCompanionAssembly)">
                        <Private>true</Private>
                      </Reference>
                    </ItemGroup>
                  </Target>
                """;

            // NativeAOT trimmer-descriptor delivery for ProjectReference (path c) consumers.
            // Unlike the packed {PackageId}.targets, the descriptor here is GENERATED into the
            // generator output dir next to this file and does NOT exist at the consumer's outer
            // evaluation on a clean build — the referenced binding project has not generated it
            // yet. So the roots live INSIDE the deferred _ResolveLocal{sanitized}NativeReferences
            // target (DependsOnTargets="ResolveProjectReferences"), where Exists() re-evaluates
            // after the binding builds — exactly the reason the native references are in a target
            // rather than a static ItemGroup. Per-item PublishAot + Exists guards keep them inert
            // for non-AOT builds and for bindings with no open generics. When the consuming app
            // imports this file, these inject into the app's own ILC item collection.
            var descriptorRootsPR = $"""

                      <TrimmerRootDescriptor Include="$(MSBuildThisFileDirectory){TrimmerDescriptorEmitter.FileName}"
                                             Condition="'$(PublishAot)' == 'true' AND Exists('$(MSBuildThisFileDirectory){TrimmerDescriptorEmitter.FileName}')" />
                      <IlcArg Include="--descriptor:$(MSBuildThisFileDirectory){TrimmerDescriptorEmitter.FileName}"
                              Condition="'$(PublishAot)' == 'true' AND Exists('$(MSBuildThisFileDirectory){TrimmerDescriptorEmitter.FileName}')" />
                """;

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
                {sourceXcfwRef}{wrapperNativeRef}{bridgeNativeRef}{localResourceBundleItems}{descriptorRootsPR}
                    </ItemGroup>
                  </Target>
                {companionReferenceTarget}
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
