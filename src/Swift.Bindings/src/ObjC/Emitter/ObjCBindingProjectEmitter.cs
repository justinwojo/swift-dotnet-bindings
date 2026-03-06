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
}

public static class ObjCBindingProjectEmitter
{
    public static string Emit(ObjCBindingProjectOptions options, ILogger logger)
    {
        var packageId = options.PackageId ?? $"{options.ModuleName}.ObjC.iOS";
        var outputDirFull = Path.GetFullPath(options.OutputDirectory);
        var sourceXcfwFull = Path.GetFullPath(options.SourceXCFrameworkPath);
        var relativeXcfwPath = Path.GetRelativePath(outputDirFull, sourceXcfwFull);

        Directory.CreateDirectory(outputDirFull);

        var csprojPath = Path.Combine(outputDirFull, $"{packageId}.csproj");

        var content = $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0-ios</TargetFramework>
                <Nullable>enable</Nullable>
                <IsBindingProject>true</IsBindingProject>
                <IsPackable>true</IsPackable>
                <PackageId>{packageId}</PackageId>
              </PropertyGroup>

              <ItemGroup>
                <ObjcBindingApiDefinition Include="ApiDefinition.cs" />
                <ObjcBindingCoreSource Include="StructsAndEnums.cs"
                                       Condition="Exists('StructsAndEnums.cs')" />
              </ItemGroup>

              <!-- NativeReference for local build -->
              <ItemGroup>
                <NativeReference Include="{relativeXcfwPath}">
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
