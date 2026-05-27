// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BindingsGeneration.Tests.ConfigurationTests;

public class MixedFrameworkDetectionTests
{
    private static readonly ILogger Logger = NullLogger.Instance;

    private static string BuildSwiftResolution(string tempDir, string moduleName,
        string[] headerFiles, bool includeModulemap = true)
    {
        var xcfwPath = Path.Combine(tempDir, $"{moduleName}.xcframework");
        var sliceId = "ios-arm64_x86_64-simulator";
        var sliceDir = Path.Combine(xcfwPath, sliceId);
        var fwDir = Path.Combine(sliceDir, $"{moduleName}.framework");
        var headersDir = Path.Combine(fwDir, "Headers");
        var modulesDir = Path.Combine(fwDir, "Modules");
        var swiftModuleDir = Path.Combine(modulesDir, $"{moduleName}.swiftmodule");

        Directory.CreateDirectory(headersDir);
        Directory.CreateDirectory(modulesDir);
        Directory.CreateDirectory(swiftModuleDir);

        // Info.plist
        File.WriteAllText(Path.Combine(xcfwPath, "Info.plist"), $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0">
            <dict>
                <key>AvailableLibraries</key>
                <array>
                    <dict>
                        <key>BinaryPath</key><string>{moduleName}.framework/{moduleName}</string>
                        <key>LibraryIdentifier</key><string>{sliceId}</string>
                        <key>LibraryPath</key><string>{moduleName}.framework</string>
                        <key>SupportedArchitectures</key><array><string>arm64</string><string>x86_64</string></array>
                        <key>SupportedPlatform</key><string>ios</string>
                        <key>SupportedPlatformVariant</key><string>simulator</string>
                    </dict>
                </array>
            </dict>
            </plist>
            """);

        // Headers
        foreach (var h in headerFiles)
            File.WriteAllText(Path.Combine(headersDir, h), "// stub");

        // Modulemap
        if (includeModulemap)
        {
            File.WriteAllText(Path.Combine(modulesDir, "module.modulemap"),
                $"framework module {moduleName} {{\n  umbrella header \"{moduleName}.h\"\n  export *\n}}\n");
        }

        return xcfwPath;
    }

    private static XCFrameworkResolution CreateSwiftResolution(string xcfwPath, string moduleName)
    {
        var sliceId = "ios-arm64_x86_64-simulator";
        return new XCFrameworkResolution
        {
            AbiJsonPath = "/fake/abi.json",
            DylibPath = Path.Combine(xcfwPath, sliceId, $"{moduleName}.framework/{moduleName}"),
            TbdPath = "/fake/tbd",
            ModuleName = moduleName,
            XCFrameworkPath = xcfwPath,
            FrameworkSearchPath = Path.Combine(xcfwPath, sliceId),
            LibraryIdentifier = sliceId,
            IsSimulatorSlice = true,
            SelectedArchitecture = "arm64",
            SupportedArchitectures = new[] { "arm64" }
        };
    }

    [Fact]
    public void SwiftOnly_NoModulemap_ReturnsNull()
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), $"mixed_test_{Guid.NewGuid():N}");
        try
        {
            // Alamofire pattern: no module.modulemap at all
            var xcfwPath = BuildSwiftResolution(tmpDir, "Alamofire",
                new[] { "Alamofire-Swift.h" }, includeModulemap: false);
            var swiftRes = CreateSwiftResolution(xcfwPath, "Alamofire");

            var result = XCFrameworkResolver.DetectMixedFrameworkObjC(
                swiftRes, XCFrameworkPlatformTarget.Simulator, Logger);

            Assert.Null(result);
        }
        finally { if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, true); }
    }

    [Fact]
    public void SwiftOnly_ModulemapWithOnlySwiftHeader_ReturnsNull()
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), $"mixed_test_{Guid.NewGuid():N}");
        try
        {
            // Kingfisher/RxSwift pattern: modulemap but only {Module}-Swift.h
            var xcfwPath = BuildSwiftResolution(tmpDir, "Kingfisher",
                new[] { "Kingfisher-Swift.h" });
            var swiftRes = CreateSwiftResolution(xcfwPath, "Kingfisher");

            var result = XCFrameworkResolver.DetectMixedFrameworkObjC(
                swiftRes, XCFrameworkPlatformTarget.Simulator, Logger);

            Assert.Null(result);
        }
        finally { if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, true); }
    }

    [Fact]
    public void Mixed_ModulemapWithObjCUmbrellaAndOtherHeaders_ReturnsResolution()
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), $"mixed_test_{Guid.NewGuid():N}");
        try
        {
            // BlinkID pattern: real ObjC headers alongside Swift
            var xcfwPath = BuildSwiftResolution(tmpDir, "BlinkID",
                new[] { "BlinkID-Swift.h", "BlinkID.h", "MBRecognizer.h" });
            var swiftRes = CreateSwiftResolution(xcfwPath, "BlinkID");

            var result = XCFrameworkResolver.DetectMixedFrameworkObjC(
                swiftRes, XCFrameworkPlatformTarget.Simulator, Logger);

            Assert.NotNull(result);
            Assert.Equal("BlinkID", result!.ModuleName);
            Assert.True(result.IsSimulatorSlice);
        }
        finally { if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, true); }
    }

    [Fact]
    public void Mixed_VersionExportOnlyHeaders_ReturnsResolution_ButPostHocFilterCanEliminate()
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), $"mixed_test_{Guid.NewGuid():N}");
        try
        {
            // CryptoSwift pattern: has a non-Swift header with only version exports
            // Detection still returns a resolution — post-hoc validation handles it
            var xcfwPath = BuildSwiftResolution(tmpDir, "CryptoSwift",
                new[] { "CryptoSwift-Swift.h", "CryptoSwift.h" });
            var swiftRes = CreateSwiftResolution(xcfwPath, "CryptoSwift");

            var result = XCFrameworkResolver.DetectMixedFrameworkObjC(
                swiftRes, XCFrameworkPlatformTarget.Simulator, Logger);

            // Structural detection returns resolution (post-hoc ObjC pipeline
            // would find zero classes/protocols and skip emission)
            Assert.NotNull(result);
        }
        finally { if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, true); }
    }

    [Fact]
    public void Mixed_ParsesModuleNameFromModulemap()
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), $"mixed_test_{Guid.NewGuid():N}");
        try
        {
            // Framework where ObjC module name differs from Swift module name
            var xcfwPath = BuildSwiftResolution(tmpDir, "MyLib",
                new[] { "MyLib-Swift.h", "MyObjCHeader.h" });

            // Override the modulemap to declare a different module name
            var modulesDir = Path.Combine(xcfwPath, "ios-arm64_x86_64-simulator",
                "MyLib.framework", "Modules");
            File.WriteAllText(Path.Combine(modulesDir, "module.modulemap"),
                "framework module MyObjCLib {\n  umbrella header \"MyLib.h\"\n  export *\n}\n");

            var swiftRes = CreateSwiftResolution(xcfwPath, "MyLib");

            var result = XCFrameworkResolver.DetectMixedFrameworkObjC(
                swiftRes, XCFrameworkPlatformTarget.Simulator, Logger);

            Assert.NotNull(result);
            // ModuleName comes from modulemap, FrameworkDirectoryName from LibraryPath
            Assert.Equal("MyObjCLib", result!.ModuleName);
            Assert.Equal("MyLib", result.FrameworkDirectoryName);
        }
        finally { if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, true); }
    }
}
