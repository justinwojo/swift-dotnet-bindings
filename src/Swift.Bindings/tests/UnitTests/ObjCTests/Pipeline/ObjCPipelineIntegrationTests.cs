// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using BindingsGeneration.ObjC;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BindingsGeneration.Tests.ObjCTests;

/// <summary>
/// Integration tests for the ObjC pipeline.
/// Tests requiring Xcode/clang are skipped when not available.
/// </summary>
public class ObjCPipelineIntegrationTests
{
    private static readonly ILogger Logger = NullLogger.Instance;

    private static bool HasXcode()
    {
        try
        {
            var runner = new SystemCommandRunner();
            var (exitCode, _, _) = runner.Run("xcrun", "--find clang", timeoutMs: 10000);
            return exitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Full pipeline test: builds an ObjC xcframework fixture and routes through
    /// ResolveObjCFramework → ObjCPipeline.Run (clang invocation → parse → model).
    /// </summary>
    [Fact]
    public void Pipeline_XCFrameworkFixture_FullRouting()
    {
        if (!HasXcode())
        {
            // Skip gracefully — CI may not have Xcode
            return;
        }

        var tempDir = Path.Combine(Path.GetTempPath(), $"objc_pipeline_test_{Guid.NewGuid():N}");
        try
        {
            // Build fixture xcframework structure
            var xcfwPath = Path.Combine(tempDir, "TestObjCLib.xcframework");
            var sliceId = "ios-arm64_x86_64-simulator";
            var fwName = "TestObjCLib";
            var sliceDir = Path.Combine(xcfwPath, sliceId);
            var fwDir = Path.Combine(sliceDir, $"{fwName}.framework");
            var headersDir = Path.Combine(fwDir, "Headers");
            var modulesDir = Path.Combine(fwDir, "Modules");

            Directory.CreateDirectory(headersDir);
            Directory.CreateDirectory(modulesDir);

            // Info.plist
            File.WriteAllText(Path.Combine(xcfwPath, "Info.plist"), $"""
                <?xml version="1.0" encoding="UTF-8"?>
                <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
                <plist version="1.0">
                <dict>
                    <key>AvailableLibraries</key>
                    <array>
                        <dict>
                            <key>BinaryPath</key><string>{fwName}.framework/{fwName}</string>
                            <key>LibraryIdentifier</key><string>{sliceId}</string>
                            <key>LibraryPath</key><string>{fwName}.framework</string>
                            <key>SupportedArchitectures</key><array><string>arm64</string><string>x86_64</string></array>
                            <key>SupportedPlatform</key><string>ios</string>
                            <key>SupportedPlatformVariant</key><string>simulator</string>
                        </dict>
                    </array>
                    <key>CFBundlePackageType</key><string>XFWK</string>
                    <key>XCFrameworkFormatVersion</key><string>1.0</string>
                </dict>
                </plist>
                """);

            // module.modulemap
            File.WriteAllText(Path.Combine(modulesDir, "module.modulemap"),
                $"framework module {fwName} {{\n  umbrella header \"{fwName}.h\"\n  export *\n  module * {{ export * }}\n}}\n");

            // ObjC header
            File.WriteAllText(Path.Combine(headersDir, $"{fwName}.h"), """
                #import <Foundation/Foundation.h>

                NS_ASSUME_NONNULL_BEGIN

                typedef NS_ENUM(NSInteger, TLStatus) {
                    TLStatusIdle = 0,
                    TLStatusActive = 1,
                    TLStatusError = 2,
                };

                @protocol TLDelegate <NSObject>
                @optional
                - (void)didComplete;
                @end

                @interface TLManager : NSObject
                @property (nonatomic, readonly) BOOL isReady;
                - (instancetype)initWithName:(NSString *)name;
                - (void)startWithCompletion:(void (^)(BOOL success))completion;
                @end

                FOUNDATION_EXPORT NSString * const TLVersionString;

                NS_ASSUME_NONNULL_END
                """);

            // Stub binary (just needs to exist for plist validation; clang doesn't use it)
            File.WriteAllText(Path.Combine(sliceDir, $"{fwName}.framework/{fwName}"), "");

            var outputDir = Path.Combine(tempDir, "output");
            Directory.CreateDirectory(outputDir);

            // Route through ResolveObjCFramework
            var resolution = XCFrameworkResolver.ResolveObjCFramework(
                xcfwPath, XCFrameworkPlatformTarget.Simulator, Logger);
            Assert.NotNull(resolution);
            Assert.Equal(fwName, resolution!.ModuleName);

            // Run pipeline
            var result = ObjCPipeline.Run(
                resolution, xcfwPath, outputDir, XCFrameworkPlatformTarget.Simulator, Logger);

            Assert.Equal(0, result.ExitCode);
            Assert.NotNull(result.Module);
            Assert.Null(result.ErrorMessage);

            var module = result.Module!;
            Assert.Equal(fwName, module.ModuleName);

            // Verify parsed declarations
            Assert.True(module.Classes.Count >= 1, $"Expected at least 1 class, got {module.Classes.Count}");
            var manager = module.Classes.FirstOrDefault(c => c.Name == "TLManager");
            Assert.NotNull(manager);
            Assert.True(manager!.Methods.Count >= 1, $"Expected methods on TLManager, got {manager.Methods.Count}");
            Assert.True(manager.Properties.Count >= 1, $"Expected properties on TLManager, got {manager.Properties.Count}");

            Assert.True(module.Protocols.Count >= 1, $"Expected at least 1 protocol, got {module.Protocols.Count}");
            Assert.True(module.Enums.Count >= 1, $"Expected at least 1 enum, got {module.Enums.Count}");
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }
    }

    /// <summary>
    /// Parser-level test using CoreBluetooth SDK headers.
    /// Validates that the parser handles real Apple framework output correctly.
    /// </summary>
    [Fact]
    public void Parser_CoreBluetooth_ParsesRealFramework()
    {
        if (!HasXcode())
            return;

        var runner = new SystemCommandRunner();

        // Get SDK path
        var (sdkExit, sdkPath, _) = runner.Run("xcrun", "--sdk iphonesimulator --show-sdk-path");
        if (sdkExit != 0 || string.IsNullOrWhiteSpace(sdkPath))
            return; // Skip if SDK not found

        var cbFramework = Path.Combine(sdkPath, "System/Library/Frameworks/CoreBluetooth.framework");
        if (!Directory.Exists(cbFramework))
            return; // Skip if framework not present

        var headerPath = Path.Combine(cbFramework, "Headers/CoreBluetooth.h");
        if (!File.Exists(headerPath))
            return; // Skip if header not present

        // Invoke clang directly
        var invoker = new ClangAstInvoker(runner, Logger);
        var frameworksDir = Path.Combine(sdkPath, "System/Library/Frameworks");
        var json = invoker.InvokeClangAstDump(headerPath, frameworksDir, isSimulator: true);
        Assert.False(string.IsNullOrWhiteSpace(json));

        // Parse
        var headersPath = Path.Combine(cbFramework, "Headers");
        var module = ClangAstParser.Parse(json, "CoreBluetooth", headersPath);

        // CoreBluetooth should have at least 5 classes
        Assert.True(module.Classes.Count >= 5,
            $"Expected at least 5 CoreBluetooth classes, got {module.Classes.Count}: " +
            string.Join(", ", module.Classes.Select(c => c.Name)));

        // CBCentralManager should have methods and properties (recursive child parsing)
        var centralManager = module.Classes.FirstOrDefault(c => c.Name == "CBCentralManager");
        Assert.NotNull(centralManager);
        Assert.True(centralManager!.Methods.Count > 0,
            "CBCentralManager should have methods (nested ObjCMethodDecl parsing)");
        Assert.True(centralManager.Properties.Count > 0,
            "CBCentralManager should have properties (nested ObjCPropertyDecl parsing)");

        // Should also have protocols
        Assert.True(module.Protocols.Count >= 1,
            $"Expected at least 1 CoreBluetooth protocol, got {module.Protocols.Count}");
    }
}
