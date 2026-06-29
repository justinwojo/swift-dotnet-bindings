// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using BindingsGeneration.ObjC;
using Xunit;
using static BindingsGeneration.Tests.ObjCTests.ObjCTestHelpers;

namespace BindingsGeneration.Tests.ObjCTests;

/// <summary>
/// Integration tests for the ObjC pipeline.
/// Tests requiring Xcode/clang are skipped when not available.
/// </summary>
public class ObjCPipelineIntegrationTests
{

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
    /// Full pipeline test: verifies that emitters produce ApiDefinition.cs, StructsAndEnums.cs, and .csproj.
    /// </summary>
    [Fact]
    public void Pipeline_XCFrameworkFixture_EmitsBindingFiles()
    {
        if (!HasXcode())
            return;

        var tempDir = Path.Combine(Path.GetTempPath(), $"objc_emit_test_{Guid.NewGuid():N}");
        try
        {
            var xcfwPath = Path.Combine(tempDir, "TestObjCLib.xcframework");
            var sliceId = "ios-arm64_x86_64-simulator";
            var fwName = "TestObjCLib";
            var sliceDir = Path.Combine(xcfwPath, sliceId);
            var fwDir = Path.Combine(sliceDir, $"{fwName}.framework");
            var headersDir = Path.Combine(fwDir, "Headers");
            var modulesDir = Path.Combine(fwDir, "Modules");

            Directory.CreateDirectory(headersDir);
            Directory.CreateDirectory(modulesDir);

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

            File.WriteAllText(Path.Combine(modulesDir, "module.modulemap"),
                $"framework module {fwName} {{\n  umbrella header \"{fwName}.h\"\n  export *\n  module * {{ export * }}\n}}\n");

            File.WriteAllText(Path.Combine(headersDir, $"{fwName}.h"), """
                #import <Foundation/Foundation.h>

                NS_ASSUME_NONNULL_BEGIN

                typedef NS_ENUM(NSInteger, TLStatus) {
                    TLStatusIdle = 0,
                    TLStatusActive = 1,
                };

                @interface TLManager : NSObject
                @property (nonatomic, readonly) BOOL isReady;
                - (void)startWithCompletion:(void (^)(BOOL success))completion;
                @end

                NS_ASSUME_NONNULL_END
                """);

            File.WriteAllText(Path.Combine(sliceDir, $"{fwName}.framework/{fwName}"), "");

            var outputDir = Path.Combine(tempDir, "output");
            Directory.CreateDirectory(outputDir);

            var resolution = XCFrameworkResolver.ResolveObjCFramework(
                xcfwPath, XCFrameworkPlatformTarget.Simulator, Logger);
            Assert.NotNull(resolution);

            var result = ObjCPipeline.Run(
                resolution!, xcfwPath, outputDir, XCFrameworkPlatformTarget.Simulator, Logger);

            Assert.Equal(0, result.ExitCode);

            // Verify emitted file paths are populated
            Assert.NotNull(result.ApiDefinitionPath);
            Assert.NotNull(result.ProjectPath);

            // Verify files exist on disk
            Assert.True(File.Exists(result.ApiDefinitionPath), "ApiDefinition.cs should exist");
            Assert.True(File.Exists(result.ProjectPath), ".csproj should exist");

            // Verify ApiDefinition.cs content
            var apiDef = File.ReadAllText(result.ApiDefinitionPath!);
            Assert.Contains("partial interface TLManager", apiDef);
            Assert.Contains("[Export(", apiDef);
            Assert.Contains("namespace TestObjCLib", apiDef);

            // Verify .csproj content
            var csproj = File.ReadAllText(result.ProjectPath!);
            Assert.Contains("IsBindingProject", csproj);
            Assert.Contains("ObjcBindingApiDefinition", csproj);

            // StructsAndEnums may or may not be emitted depending on parsed declarations
            if (result.StructsAndEnumsPath != null)
                Assert.True(File.Exists(result.StructsAndEnumsPath), "StructsAndEnums.cs should exist when path is set");
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
    /// B2 end-to-end: a framework whose own protocols cross-reference each other (a protocol
    /// inheriting another own protocol, a class conforming to an own protocol, and a member
    /// typed <c>id&lt;OwnProtocol&gt;</c>) must spell those references by position in
    /// ApiDefinition.cs. DECLARATIONS and inheritance/conformance lists use the BARE name — bgen
    /// synthesizes <c>IFoo</c> from the bare <c>[Protocol] interface Foo</c> and converts the bare
    /// conformance to <c>: IFoo</c> in its output; a pre-prefixed declaration produced
    /// <c>IIFoo</c>. MEMBER types use the INTERFACE <c>IFoo</c>, so bgen binds them to the protocol
    /// interface — a bare member reference makes bgen pick the generated Model class and a
    /// conforming subclass throws InvalidCastException at runtime. An empty <c>interface IFoo {}</c>
    /// forward declaration is emitted per own protocol so the <c>IFoo</c> member references resolve
    /// in the plain-csc api-definition contract compile (which has no bgen-generated <c>IFoo</c> in
    /// scope). SDK protocols (NSCopying/NSCoding) keep their <c>I</c> prefix everywhere. This is the
    /// cross-referenced-protocol shape that real frameworks (e.g. MapLibre's
    /// <c>MLNFeature : MLNAnnotation</c>) exercise but synthetic single-protocol fixtures missed.
    /// </summary>
    [Fact]
    public void Pipeline_XCFrameworkFixture_CrossReferencedOwnProtocols_PositionalProtocolSpelling()
    {
        if (!HasXcode())
            return;

        var tempDir = Path.Combine(Path.GetTempPath(), $"objc_b2_test_{Guid.NewGuid():N}");
        try
        {
            var xcfwPath = Path.Combine(tempDir, "TestObjCLib.xcframework");
            var sliceId = "ios-arm64_x86_64-simulator";
            var fwName = "TestObjCLib";
            var sliceDir = Path.Combine(xcfwPath, sliceId);
            var fwDir = Path.Combine(sliceDir, $"{fwName}.framework");
            var headersDir = Path.Combine(fwDir, "Headers");
            var modulesDir = Path.Combine(fwDir, "Modules");

            Directory.CreateDirectory(headersDir);
            Directory.CreateDirectory(modulesDir);

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

            File.WriteAllText(Path.Combine(modulesDir, "module.modulemap"),
                $"framework module {fwName} {{\n  umbrella header \"{fwName}.h\"\n  export *\n  module * {{ export * }}\n}}\n");

            // An own protocol inheriting another own protocol AND an SDK protocol; a class
            // conforming to an own protocol AND an SDK protocol; and a member typed id<OwnProtocol>.
            File.WriteAllText(Path.Combine(headersDir, $"{fwName}.h"), """
                #import <Foundation/Foundation.h>

                NS_ASSUME_NONNULL_BEGIN

                @protocol MLNAnnotation <NSObject>
                @property (nonatomic, readonly, copy) NSString *title;
                @end

                @protocol MLNFeature <MLNAnnotation, NSCopying>
                @property (nonatomic, readonly) NSUInteger featureIdentifier;
                @end

                @interface MLNShape : NSObject <MLNFeature, NSCoding>
                @property (nonatomic, readonly, nullable) id<MLNAnnotation> primaryFeature;
                @end

                NS_ASSUME_NONNULL_END
                """);

            File.WriteAllText(Path.Combine(sliceDir, $"{fwName}.framework/{fwName}"), "");

            var outputDir = Path.Combine(tempDir, "output");
            Directory.CreateDirectory(outputDir);

            var resolution = XCFrameworkResolver.ResolveObjCFramework(
                xcfwPath, XCFrameworkPlatformTarget.Simulator, Logger);
            Assert.NotNull(resolution);

            var result = ObjCPipeline.Run(
                resolution!, xcfwPath, outputDir, XCFrameworkPlatformTarget.Simulator, Logger);

            Assert.Equal(0, result.ExitCode);
            Assert.NotNull(result.ApiDefinitionPath);
            var apiDef = File.ReadAllText(result.ApiDefinitionPath!);

            // Empty forward declarations for each own protocol's interface.
            Assert.Contains("interface IMLNAnnotation { }", apiDef);
            Assert.Contains("interface IMLNFeature { }", apiDef);
            // Own protocol inheriting another own protocol → bare (robust to whether the SDK
            // NSCopying survives the resolvability filter, which would append ", INSCopying").
            Assert.Contains("partial interface MLNFeature : MLNAnnotation", apiDef);
            // Class conforming to an own protocol → bare (SDK NSCoding, if kept, appends ", INSCoding").
            Assert.Contains("partial interface MLNShape : MLNFeature", apiDef);
            // Member typed id<MLNAnnotation> → the own-protocol INTERFACE.
            Assert.Contains("IMLNAnnotation PrimaryFeature", apiDef);
            // Declarations stay bare — never pre-prefixed to the double-I shape.
            Assert.DoesNotContain("partial interface IMLN", apiDef);
            Assert.DoesNotContain("IIMLNFeature", apiDef);
            Assert.DoesNotContain("IIMLNAnnotation", apiDef);
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

    /// <summary>
    /// End-to-end availability recovery (Finding 22, recovery option a2): a header carrying a
    /// macro-form <c>API_AVAILABLE(ios(15.0))</c> on a class and a bare
    /// <c>__attribute__((availability(...)))</c> on a method is run through REAL clang
    /// (<c>-ast-dump=json</c>), parsed, and emitted. Asserts the recovered availability surfaces as
    /// the same <c>[SupportedOSPlatform]</c>/<c>[ObsoletedOSPlatform]</c> shape the Swift path uses.
    /// This proves the source-offset recovery works against actual clang output (the JSON node
    /// itself carries only <c>{id, kind, range}</c>), not just hand-authored JSON. The header is NOT
    /// stripped — it flows through the full generation pipeline.
    /// </summary>
    [Fact]
    public void Pipeline_XCFrameworkFixture_RecoversAvailabilityFromSource()
    {
        if (!HasXcode())
            return;

        var tempDir = Path.Combine(Path.GetTempPath(), $"objc_avail_e2e_{Guid.NewGuid():N}");
        try
        {
            var xcfwPath = Path.Combine(tempDir, "TestObjCLib.xcframework");
            var sliceId = "ios-arm64_x86_64-simulator";
            var fwName = "TestObjCLib";
            var sliceDir = Path.Combine(xcfwPath, sliceId);
            var fwDir = Path.Combine(sliceDir, $"{fwName}.framework");
            var headersDir = Path.Combine(fwDir, "Headers");
            var modulesDir = Path.Combine(fwDir, "Modules");

            Directory.CreateDirectory(headersDir);
            Directory.CreateDirectory(modulesDir);

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

            File.WriteAllText(Path.Combine(modulesDir, "module.modulemap"),
                $"framework module {fwName} {{\n  umbrella header \"{fwName}.h\"\n  export *\n  module * {{ export * }}\n}}\n");

            // API_AVAILABLE (macro form, recovered via expansionLoc) on the class; bare
            // __attribute__((availability(...))) (recovered via direct offset) on a method.
            File.WriteAllText(Path.Combine(headersDir, $"{fwName}.h"), """
                #import <Foundation/Foundation.h>

                NS_ASSUME_NONNULL_BEGIN

                API_AVAILABLE(ios(15.0))
                @interface TLAvailManager : NSObject
                @property (nonatomic, readonly) BOOL isReady;
                - (void)legacyMethod __attribute__((availability(ios, introduced=16.0, deprecated=17.0, message="use newMethod")));
                @end

                NS_ASSUME_NONNULL_END
                """);

            File.WriteAllText(Path.Combine(sliceDir, $"{fwName}.framework/{fwName}"), "");

            var outputDir = Path.Combine(tempDir, "output");
            Directory.CreateDirectory(outputDir);

            var resolution = XCFrameworkResolver.ResolveObjCFramework(
                xcfwPath, XCFrameworkPlatformTarget.Simulator, Logger);
            Assert.NotNull(resolution);

            var result = ObjCPipeline.Run(
                resolution!, xcfwPath, outputDir, XCFrameworkPlatformTarget.Simulator, Logger);

            Assert.Equal(0, result.ExitCode);
            Assert.NotNull(result.ApiDefinitionPath);

            var apiDef = File.ReadAllText(result.ApiDefinitionPath!);
            Assert.Contains("partial interface TLAvailManager", apiDef);

            // Class-level macro availability recovered from source.
            Assert.Contains("[global::System.Runtime.Versioning.SupportedOSPlatform(\"ios15.0\")]", apiDef);

            // Method-level bare-attribute availability recovered from source.
            Assert.Contains("[global::System.Runtime.Versioning.SupportedOSPlatform(\"ios16.0\")]", apiDef);
            Assert.Contains("[global::System.Runtime.Versioning.ObsoletedOSPlatform(\"ios17.0\"", apiDef);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }
    }

    [Fact]
    public void Pipeline_SdkMode_ObjCOnly_SkipsCsproj()
    {
        if (!HasXcode()) return;

        var tempDir = Path.Combine(Path.GetTempPath(), $"objc_sdk_test_{Guid.NewGuid():N}");
        try
        {
            var (xcfwPath, outputDir) = BuildTestFixture(tempDir, "SdkTest");
            var resolution = XCFrameworkResolver.ResolveObjCFramework(
                xcfwPath, XCFrameworkPlatformTarget.Simulator, Logger);
            Assert.NotNull(resolution);

            var result = ObjCPipeline.Run(
                resolution!, xcfwPath, outputDir, XCFrameworkPlatformTarget.Simulator, Logger,
                sdkMode: true, isMixed: false);

            Assert.Equal(0, result.ExitCode);
            Assert.Null(result.ProjectPath); // SDK mode ObjC-only skips .csproj
            Assert.NotNull(result.ApiDefinitionPath);
            // Should still emit metadata props
            Assert.True(File.Exists(Path.Combine(outputDir, "binding-metadata.props")));
        }
        finally { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); }
    }

    [Fact]
    public void Pipeline_SdkMode_Mixed_EmitsCsproj()
    {
        if (!HasXcode()) return;

        var tempDir = Path.Combine(Path.GetTempPath(), $"objc_mixed_test_{Guid.NewGuid():N}");
        try
        {
            var (xcfwPath, outputDir) = BuildTestFixture(tempDir, "MixedTest");
            var resolution = XCFrameworkResolver.ResolveObjCFramework(
                xcfwPath, XCFrameworkPlatformTarget.Simulator, Logger);
            Assert.NotNull(resolution);

            var result = ObjCPipeline.Run(
                resolution!, xcfwPath, outputDir, XCFrameworkPlatformTarget.Simulator, Logger,
                sdkMode: true, isMixed: true);

            Assert.Equal(0, result.ExitCode);
            Assert.NotNull(result.ProjectPath); // SDK mode mixed DOES emit .csproj
        }
        finally { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); }
    }

    [Fact]
    public void Pipeline_WithExcludeTypes_FiltersDuplicates()
    {
        if (!HasXcode()) return;

        var tempDir = Path.Combine(Path.GetTempPath(), $"objc_dedup_test_{Guid.NewGuid():N}");
        try
        {
            var (xcfwPath, outputDir) = BuildTestFixture(tempDir, "DedupTest");
            var resolution = XCFrameworkResolver.ResolveObjCFramework(
                xcfwPath, XCFrameworkPlatformTarget.Simulator, Logger);
            Assert.NotNull(resolution);

            // Exclude the class name that exists in the test fixture
            var excludeTypes = new HashSet<string> { "TLManager" };
            var result = ObjCPipeline.Run(
                resolution!, xcfwPath, outputDir, XCFrameworkPlatformTarget.Simulator, Logger,
                excludeTypeNames: excludeTypes);

            Assert.Equal(0, result.ExitCode);
            Assert.NotNull(result.Module);
            // TLManager should have been filtered out
            Assert.DoesNotContain(result.Module!.Classes, c => c.Name == "TLManager");
        }
        finally { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); }
    }

    private static (string XcfwPath, string OutputDir) BuildTestFixture(string tempDir, string fwName)
    {
        var xcfwPath = Path.Combine(tempDir, $"{fwName}.xcframework");
        var sliceId = "ios-arm64_x86_64-simulator";
        var sliceDir = Path.Combine(xcfwPath, sliceId);
        var fwDir = Path.Combine(sliceDir, $"{fwName}.framework");
        var headersDir = Path.Combine(fwDir, "Headers");
        var modulesDir = Path.Combine(fwDir, "Modules");

        Directory.CreateDirectory(headersDir);
        Directory.CreateDirectory(modulesDir);

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
            </dict>
            </plist>
            """);

        File.WriteAllText(Path.Combine(modulesDir, "module.modulemap"),
            $"framework module {fwName} {{\n  umbrella header \"{fwName}.h\"\n  export *\n  module * {{ export * }}\n}}\n");

        File.WriteAllText(Path.Combine(headersDir, $"{fwName}.h"), """
            #import <Foundation/Foundation.h>
            NS_ASSUME_NONNULL_BEGIN
            @interface TLManager : NSObject
            @property (nonatomic, readonly) BOOL isReady;
            - (instancetype)initWithName:(NSString *)name;
            @end
            NS_ASSUME_NONNULL_END
            """);

        File.WriteAllText(Path.Combine(sliceDir, $"{fwName}.framework/{fwName}"), "");

        var outputDir = Path.Combine(tempDir, "output");
        Directory.CreateDirectory(outputDir);
        return (xcfwPath, outputDir);
    }
}
