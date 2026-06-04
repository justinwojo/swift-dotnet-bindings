// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using BindingsGeneration;
using Microsoft.Extensions.Logging;

namespace BindingsGeneration.ObjC;

public sealed record ObjCBindingProjectOptions
{
    public required string OutputDirectory { get; init; }
    public required string ModuleName { get; init; }
    public required string SourceXCFrameworkPath { get; init; }
    /// <summary>
    /// Names the companion csproj/assembly. A mixed framework is ONE xcframework, so it
    /// ships as ONE NuGet package: this companion's assembly is *embedded* into the Swift
    /// binding's package (lib/), never packed as a separate package. The id therefore only
    /// determines the assembly/file name (defaults to the platform's ObjC package id).
    /// </summary>
    public string? PackageId { get; init; }
    /// <summary>
    /// Native linkage of the source framework. When <see cref="NativeLinkage.Static"/> AND a
    /// wrapper carries the binding (see <see cref="HasWrapperXCFramework"/>), the wrapper
    /// force-loaded the static archive and is the sole native carrier, so the companion drops
    /// its own source <c>NativeReference</c> — re-linking the same archive would duplicate-register
    /// every ObjC class (the Kidoz #40 "implemented in both …" condition). Defaults to
    /// <see cref="NativeLinkage.Dynamic"/> so a pure-ObjC companion (the sole carrier, no Swift
    /// wrapper) keeps its reference unchanged.
    /// </summary>
    public NativeLinkage SourceNativeLinkage { get; init; } = NativeLinkage.Dynamic;
    /// <summary>
    /// Whether a Swift wrapper xcframework is (or will be) produced for this binding — the
    /// "will be produced" intent, since under the SDK's two-pass flow the wrapper does not yet
    /// exist when the companion csproj is emitted. Together with <see cref="SourceNativeLinkage"/>
    /// this decides whether the companion drops its source <c>NativeReference</c> (mixed +
    /// static + wrapper) or keeps it (pure-ObjC, or a dynamic source the Swift side references
    /// by install name). Mirrors the Swift binding csproj's own
    /// <c>NativePackagingPolicy.ShouldIncludeSourceXcframework</c> decision so the two cannot drift.
    /// </summary>
    public bool HasWrapperXCFramework { get; init; }
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
        var outputDirFull = Path.GetFullPath(options.OutputDirectory);
        var sourceXcfwFull = Path.GetFullPath(options.SourceXCFrameworkPath);
        // Use absolute path for NativeReference to avoid /tmp → /private/tmp symlink issues
        var xcfwPath = sourceXcfwFull;

        // Gap 2: a static-archive source is force-loaded into (and carried by) the Swift wrapper,
        // so when one exists the companion must NOT also link the source archive — doing so links
        // the same Mach-O twice and duplicate-registers every ObjC class (the Kidoz #40 condition).
        // The companion is emitted alongside the Swift binding on OUR machine where the linkage is
        // known, so the drop is BAKED here via the boolean ShouldIncludeSourceXcframework — the same
        // authority and shape the standalone Swift csproj (BindingProjectEmitter) uses, not the
        // frozen-consumer WrapperAbsentFallback enum. A pure-ObjC companion has no Swift wrapper
        // (HasWrapperXCFramework=false) so it stays the sole carrier and keeps the reference; a
        // dynamic mixed source is referenced by the Swift side via install name, and a dynamic
        // companion reference is inert (deduped), so it is kept too.
        var emitSourceNativeRef = NativePackagingPolicy.ShouldIncludeSourceXcframework(
            options.SourceNativeLinkage, options.HasWrapperXCFramework);

        Directory.CreateDirectory(outputDirFull);

        var csprojPath = Path.Combine(outputDirFull, $"{packageId}.csproj");

        // Local-build NativeReference only, and only when the companion is the carrier (see the
        // Gap-2 bake above). When a static source's wrapper carries the archive this block is
        // dropped entirely: the companion stays managed-only and its ObjC class symbols resolve
        // at consume time from the wrapper the Swift side references. Pack=false keeps the native
        // out of the companion's output even when it IS the carrier.
        var nativeReferenceItemGroup = emitSourceNativeRef
            ? $"""
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
            """
            : """
              <!-- Source NativeReference dropped (Gap 2): a static source archive is force-loaded
                   into the Swift wrapper, which is the sole native carrier. Linking it here too
                   would duplicate-register every ObjC class (Kidoz #40). The companion is
                   managed-only; its ObjC symbols resolve from the wrapper the Swift side
                   references at consume time. -->
            """;

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
                <!-- Not a standalone package. A mixed framework is ONE xcframework and ships as
                     ONE NuGet package: the Swift binding's pack embeds THIS companion's managed
                     assembly into its own lib/ (SDK: _BuildMixedObjCCompanion + the lib/ entry in
                     _ConfigureSwiftBindingPack; standalone: ProjectReference PrivateAssets=all +
                     BuildOutputInPackage). IsPackable=false keeps a stray companion nupkg from
                     being produced and keeps the Swift binding from promoting it to a separate
                     nuspec <dependency>. The AssemblyName defaults to the csproj file name
                     ({packageId}), which is what gets embedded. -->
                <IsPackable>false</IsPackable>
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

            {nativeReferenceItemGroup}

              <Import Project="Sdk.targets" Sdk="Microsoft.NET.Sdk" />
            </Project>
            """;

        File.WriteAllText(csprojPath, content);
        logger.LogInformation("Wrote ObjC binding project to {Path}", csprojPath);

        return csprojPath;
    }
}
