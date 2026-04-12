// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;

namespace BindingsGeneration.ObjC;

/// <summary>
/// Emits binding-metadata.props for ObjC-only or mixed framework pipelines.
/// The SDK's _ImportSwiftBindingMetadata target reads this file.
/// </summary>
public static class ObjCMetadataPropsEmitter
{
    public static void Emit(
        string outputDirectory,
        string moduleName,
        string xcframeworkPath,
        string frameworkType,
        ILogger logger)
    {
        // Try to extract real metadata from the xcframework's inner plist
        var metadata = ExtractObjCMetadata(xcframeworkPath, moduleName, logger);

        var content = $"""
            <Project>
              <PropertyGroup>
                <_SwiftBindingModuleName>{moduleName}</_SwiftBindingModuleName>
                <_SwiftBindingFrameworkType>{frameworkType}</_SwiftBindingFrameworkType>
                <_SwiftBindingHasWrapperXCFramework>False</_SwiftBindingHasWrapperXCFramework>
                <_SwiftBindingWrapperModuleName></_SwiftBindingWrapperModuleName>
                <_SwiftBindingWrapperSliceCount>0</_SwiftBindingWrapperSliceCount>
                <_SwiftBindingPackageVersion>{metadata.PackageVersion}</_SwiftBindingPackageVersion>
                <_SwiftBindingMinimumOSVersion>{metadata.EffectiveMinimumOSVersion}</_SwiftBindingMinimumOSVersion>
                <_SwiftBindingIsVersionPlaceholder>{metadata.IsVersionPlaceholder}</_SwiftBindingIsVersionPlaceholder>
              </PropertyGroup>
            </Project>
            """;

        Directory.CreateDirectory(outputDirectory);
        var propsPath = Path.Combine(outputDirectory, "binding-metadata.props");
        File.WriteAllText(propsPath, content);
        logger.LogInformation("Wrote ObjC binding metadata props to {Path}", propsPath);
    }

    private static XCFrameworkMetadata ExtractObjCMetadata(
        string xcframeworkPath, string moduleName, ILogger logger)
    {
        try
        {
            return XCFrameworkMetadataExtractor.ExtractFromFrameworkPath(
                xcframeworkPath, moduleName, logger);
        }
        catch (Exception ex)
        {
            logger.LogDebug("Could not extract ObjC metadata: {Message}. Using defaults.", ex.Message);
            return new XCFrameworkMetadata
            {
                LibraryVersion = null,
                PackageVersion = "1.0.0",
                IsVersionPlaceholder = true,
                MinimumOSVersion = null,
                EffectiveMinimumOSVersion = "15.0",
                SdkVersion = null,
                ModuleName = moduleName,
                Platforms = new List<string>()
            };
        }
    }
}
