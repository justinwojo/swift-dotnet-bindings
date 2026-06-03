// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;

namespace BindingsGeneration.ObjC;

public sealed record ObjCBindingProjectOptions
{
    public required string OutputDirectory { get; init; }
    public required string ModuleName { get; init; }
    public required string SourceXCFrameworkPath { get; init; }
    public string? PackageId { get; init; }
    /// <summary>
    /// NuGet package version for the companion. Threaded from the Swift binding's
    /// <see cref="XCFrameworkMetadataExtractor.XCFrameworkMetadata.PackageVersion"/> so the
    /// two stay in lockstep (both are generated from one xcframework in one run). A non-null,
    /// non-empty value makes the companion csproj a *packable* sibling (PackageId + version),
    /// which is what lets the Swift binding's bare <c>&lt;ProjectReference&gt;</c> auto-promote
    /// into a real nuspec <c>&lt;dependency&gt;</c> at pack time. Falls back to 1.0.0 if absent.
    /// </summary>
    public string? PackageVersion { get; init; }
    /// <summary>
    /// Platform info for multi-platform support. Falls back to iOS if not specified (CLI default).
    /// </summary>
    public PlatformInfo? PlatformInfo { get; init; }
}

public static class ObjCBindingProjectEmitter
{
    public static string Emit(ObjCBindingProjectOptions options, ILogger logger)
    {
        var pi = options.PlatformInfo ?? PlatformInfoFactory.Create(ApplePlatform.iOS);
        var packageId = options.PackageId ?? pi.GetDefaultObjCPackageId(options.ModuleName);
        var packageVersion = string.IsNullOrWhiteSpace(options.PackageVersion) ? "1.0.0" : options.PackageVersion!;
        var outputDirFull = Path.GetFullPath(options.OutputDirectory);
        var sourceXcfwFull = Path.GetFullPath(options.SourceXCFrameworkPath);
        // Use absolute path for NativeReference to avoid /tmp → /private/tmp symlink issues
        var xcfwPath = sourceXcfwFull;

        Directory.CreateDirectory(outputDirFull);

        var csprojPath = Path.Combine(outputDirFull, $"{packageId}.csproj");

        // Explicit Sdk.props / Sdk.targets import form (instead of <Project Sdk="...">) so that
        // BaseIntermediateOutputPath/BaseOutputPath can be set in a PropertyGroup that runs
        // *before* Microsoft.Common.props computes MSBuildProjectExtensionsPath. The companion
        // and the Swift binding csproj are co-located in one output directory; with the default
        // obj/ both would write the same obj/project.assets.json and the Swift restore graph
        // (which carries the SwiftBindings.Runtime reference) loses the race to the companion's
        // (which doesn't) → Swift compile fails CS0246 (Gap 1.5 obj-stomp). Relocating ONLY the
        // companion's obj/bin keeps the Swift binding at the default obj/ — tooling that reads
        // obj/project.assets.json at the fixed path (build/Build.Validation.cs) is unaffected.
        // Setting these in the body of a <Project Sdk="..."> project is too late (MSB3539/3540);
        // a Directory.Build.props would work but has blast radius on every sibling project in the
        // directory. The explicit-import form is scoped to this csproj alone and restores cleanly
        // against the net10.0-ios workload.
        var content = $"""
            <Project>
              <PropertyGroup>
                <BaseIntermediateOutputPath>obj.objc/</BaseIntermediateOutputPath>
                <BaseOutputPath>bin.objc/</BaseOutputPath>
              </PropertyGroup>

              <Import Project="Sdk.props" Sdk="Microsoft.NET.Sdk" />

              <PropertyGroup>
                <!-- Explicit, version-qualified TFM. Mixed frameworks (BlinkID, BRLMPrinterKit,
                     etc.) emit a Swift binding csproj that ProjectReferences this ObjC binding
                     csproj. The Swift side now uses pi.PackTfm so the ObjC side MUST match,
                     or the ProjectReference resolution fails restore with NETSDK1005 ("Assets
                     file ... doesn't have a target for 'net10.0-ios'"). Both fragments source
                     from PlatformInfo.PackTfm so they cannot drift. -->
                <TargetFramework>{pi.PackTfm}</TargetFramework>
                <Nullable>enable</Nullable>
                <IsBindingProject>true</IsBindingProject>
                <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
                <IsPackable>true</IsPackable>
                <PackageId>{packageId}</PackageId>
                <!-- Lockstep with the Swift binding's XCFrameworkMetadata.PackageVersion.
                     A versioned PackageId makes this a packable sibling, so the Swift binding's
                     bare <ProjectReference> auto-promotes to a nuspec <dependency> at pack time
                     (no explicit PackageReference — that pairing is the NU5128 anti-pattern). -->
                <PackageVersion>{packageVersion}</PackageVersion>
              </PropertyGroup>

              <ItemGroup>
                <ObjcBindingApiDefinition Include="ApiDefinition.cs" />
                <ObjcBindingCoreSource Include="StructsAndEnums.cs"
                                       Condition="Exists('StructsAndEnums.cs')" />
                <!-- bgen-only delegate hints: visible to bgen for type resolution,
                     excluded from C# compilation (bgen auto-generates these in SupportDelegates.g.cs) -->
                <ObjcBindingCoreSource Include="BgenDelegates.cs"
                                       Condition="Exists('BgenDelegates.cs')" />
              </ItemGroup>

              <!-- Remove bgen-only delegate hints from C# compilation.
                   The SDK's ObjCBinding targets add ObjcBindingCoreSource to Compile,
                   but these delegates conflict with bgen's auto-generated SupportDelegates.g.cs. -->
              <Target Name="_RemoveBgenDelegatesFromCompile" BeforeTargets="CoreCompile">
                <ItemGroup>
                  <Compile Remove="BgenDelegates.cs" />
                </ItemGroup>
              </Target>

              <!-- Local-build NativeReference only: the companion is MANAGED-ONLY. Its ObjC class
                   symbols resolve at consume time from the Swift package's native — the source
                   xcframework and wrapper for a dynamic source, or the wrapper alone (which
                   force-loads the archive) for a static source (Gap 2) — loaded once. Pack=false
                   keeps the native out of the companion nupkg so the same Mach-O is never shipped
                   twice (single-registration; same decision as Gap 2). -->
              <ItemGroup>
                <NativeReference Include="{xcfwPath}">
                  <Kind>Framework</Kind>
                  <Pack>false</Pack>
                </NativeReference>
              </ItemGroup>

              <Import Project="Sdk.targets" Sdk="Microsoft.NET.Sdk" />
            </Project>
            """;

        File.WriteAllText(csprojPath, content);
        logger.LogInformation("Wrote ObjC binding project to {Path}", csprojPath);

        return csprojPath;
    }
}
