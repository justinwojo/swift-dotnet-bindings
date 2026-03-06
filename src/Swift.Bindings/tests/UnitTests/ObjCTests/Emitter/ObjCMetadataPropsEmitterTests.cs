// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using BindingsGeneration.ObjC;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BindingsGeneration.Tests.ObjCTests;

public class ObjCMetadataPropsEmitterTests
{
    private static readonly ILogger Logger = NullLogger.Instance;

    [Fact]
    public void EmitsFrameworkTypeProperty()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"props_test_{Guid.NewGuid():N}");
        try
        {
            var xcfwPath = CreateMinimalXcframework(dir, "TestLib");
            ObjCMetadataPropsEmitter.Emit(dir, "TestLib", xcfwPath, "ObjC", Logger);

            var content = File.ReadAllText(Path.Combine(dir, "binding-metadata.props"));
            Assert.Contains("<_SwiftBindingFrameworkType>ObjC</_SwiftBindingFrameworkType>", content);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public void EmitsModuleNameProperty()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"props_test_{Guid.NewGuid():N}");
        try
        {
            var xcfwPath = CreateMinimalXcframework(dir, "MyModule");
            ObjCMetadataPropsEmitter.Emit(dir, "MyModule", xcfwPath, "ObjC", Logger);

            var content = File.ReadAllText(Path.Combine(dir, "binding-metadata.props"));
            Assert.Contains("<_SwiftBindingModuleName>MyModule</_SwiftBindingModuleName>", content);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public void EmitsNoWrapperProperties()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"props_test_{Guid.NewGuid():N}");
        try
        {
            var xcfwPath = CreateMinimalXcframework(dir, "TestLib");
            ObjCMetadataPropsEmitter.Emit(dir, "TestLib", xcfwPath, "ObjC", Logger);

            var content = File.ReadAllText(Path.Combine(dir, "binding-metadata.props"));
            Assert.Contains("<_SwiftBindingHasWrapperXCFramework>False</_SwiftBindingHasWrapperXCFramework>", content);
            Assert.Contains("<_SwiftBindingWrapperSliceCount>0</_SwiftBindingWrapperSliceCount>", content);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public void EmitsMixedFrameworkType()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"props_test_{Guid.NewGuid():N}");
        try
        {
            var xcfwPath = CreateMinimalXcframework(dir, "MixedLib");
            ObjCMetadataPropsEmitter.Emit(dir, "MixedLib", xcfwPath, "Mixed", Logger);

            var content = File.ReadAllText(Path.Combine(dir, "binding-metadata.props"));
            Assert.Contains("<_SwiftBindingFrameworkType>Mixed</_SwiftBindingFrameworkType>", content);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    private static string CreateMinimalXcframework(string parentDir, string moduleName)
    {
        var xcfwPath = Path.Combine(parentDir, $"{moduleName}.xcframework");
        Directory.CreateDirectory(xcfwPath);
        // Minimal Info.plist so ExtractFromFrameworkPath doesn't crash
        File.WriteAllText(Path.Combine(xcfwPath, "Info.plist"), """
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0">
            <dict>
                <key>AvailableLibraries</key>
                <array>
                    <dict>
                        <key>BinaryPath</key><string>lib.framework/lib</string>
                        <key>LibraryIdentifier</key><string>ios-arm64</string>
                        <key>LibraryPath</key><string>lib.framework</string>
                        <key>SupportedArchitectures</key><array><string>arm64</string></array>
                        <key>SupportedPlatform</key><string>ios</string>
                    </dict>
                </array>
            </dict>
            </plist>
            """);
        return xcfwPath;
    }
}
