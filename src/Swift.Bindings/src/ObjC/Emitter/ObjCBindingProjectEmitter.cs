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

        Directory.CreateDirectory(outputDirFull);

        var csprojPath = Path.Combine(outputDirFull, $"{packageId}.csproj");

        var content = $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>{pi.Tfm}</TargetFramework>
                <Nullable>enable</Nullable>
                <IsBindingProject>true</IsBindingProject>
                <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
                <IsPackable>true</IsPackable>
                <PackageId>{packageId}</PackageId>
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

              <!-- NativeReference for local build -->
              <ItemGroup>
                <NativeReference Include="{xcfwPath}">
                  <Kind>Framework</Kind>
                </NativeReference>
              </ItemGroup>
            </Project>
            """;

        File.WriteAllText(csprojPath, content);
        logger.LogInformation("Wrote ObjC binding project to {Path}", csprojPath);

        return csprojPath;
    }
}
