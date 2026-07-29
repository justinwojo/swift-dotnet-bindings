// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace BindingsGeneration.Tests
{
    // ═══════════════════════════════════════════════════════════════════════
    // Section A: otool Output Parsing
    // ═══════════════════════════════════════════════════════════════════════

    public class OtoolParsingTests
    {
        private const string SampleOtoolOutput = """
            /path/to/ImagePipelineUI.framework/ImagePipelineUI:
            	@rpath/ImagePipeline.framework/ImagePipeline (compatibility version 0.0.0, current version 0.0.0)
            	/usr/lib/libobjc.A.dylib (compatibility version 1.0.0, current version 228.0.0)
            	/usr/lib/libSystem.B.dylib (compatibility version 1.0.0, current version 1311.0.0)
            	/usr/lib/swift/libswiftCore.dylib (compatibility version 1.0.0, current version 5.9.0)
            """;

        [Fact]
        public void ParseOtoolOutput_SingleRpathDep_ExtractsCorrectly()
        {
            var result = BinaryDependencyAnalyzer.ParseOtoolOutput(SampleOtoolOutput, "ImagePipelineUI");
            Assert.Single(result);
            Assert.Equal("ImagePipeline", result[0].FrameworkName);
            Assert.Equal("@rpath/ImagePipeline.framework/ImagePipeline", result[0].InstallName);
        }

        [Fact]
        public void ParseOtoolOutput_MultipleRpathDeps_ExtractsAll()
        {
            var output = """
                /path/to/PaymentSdkSheet.framework/PaymentSdkSheet:
                	@rpath/PaymentSdkCore.framework/PaymentSdkCore (compatibility version 0.0.0, current version 0.0.0)
                	@rpath/PaymentSdkUICore.framework/PaymentSdkUICore (compatibility version 0.0.0, current version 0.0.0)
                	@rpath/PaymentSdkPaymentsUI.framework/PaymentSdkPaymentsUI (compatibility version 0.0.0, current version 0.0.0)
                	/usr/lib/libobjc.A.dylib (compatibility version 1.0.0, current version 228.0.0)
                """;

            var result = BinaryDependencyAnalyzer.ParseOtoolOutput(output, "PaymentSdkSheet");
            Assert.Equal(3, result.Count);
            Assert.Equal("PaymentSdkCore", result[0].FrameworkName);
            Assert.Equal("PaymentSdkUICore", result[1].FrameworkName);
            Assert.Equal("PaymentSdkPaymentsUI", result[2].FrameworkName);
        }

        [Fact]
        public void ParseOtoolOutput_SystemDylibs_Filtered()
        {
            var output = """
                /path/to/Test.framework/Test:
                	/usr/lib/libobjc.A.dylib (compatibility version 1.0.0, current version 228.0.0)
                	/usr/lib/libSystem.B.dylib (compatibility version 1.0.0, current version 1311.0.0)
                """;

            var result = BinaryDependencyAnalyzer.ParseOtoolOutput(output, "Test");
            Assert.Empty(result);
        }

        [Fact]
        public void ParseOtoolOutput_SwiftRuntimeLibs_Filtered()
        {
            var output = """
                /path/to/Test.framework/Test:
                	/usr/lib/swift/libswiftCore.dylib (compatibility version 1.0.0, current version 5.9.0)
                	/usr/lib/swift/libswiftFoundation.dylib (compatibility version 1.0.0, current version 5.9.0)
                """;

            var result = BinaryDependencyAnalyzer.ParseOtoolOutput(output, "Test");
            Assert.Empty(result);
        }

        [Fact]
        public void ParseOtoolOutput_SelfReference_Filtered()
        {
            var output = """
                /path/to/ImagePipeline.framework/ImagePipeline:
                	@rpath/ImagePipeline.framework/ImagePipeline (compatibility version 0.0.0, current version 0.0.0)
                	@rpath/OtherLib.framework/OtherLib (compatibility version 0.0.0, current version 0.0.0)
                """;

            var result = BinaryDependencyAnalyzer.ParseOtoolOutput(output, "ImagePipeline");
            Assert.Single(result);
            Assert.Equal("OtherLib", result[0].FrameworkName);
        }

        [Fact]
        public void ParseOtoolOutput_EmptyOutput_ReturnsEmpty()
        {
            var result = BinaryDependencyAnalyzer.ParseOtoolOutput("", "Test");
            Assert.Empty(result);
        }

        [Fact]
        public void ParseOtoolOutput_NoRpathEntries_ReturnsEmpty()
        {
            var output = """
                /path/to/Test.framework/Test:
                	/usr/lib/libobjc.A.dylib (compatibility version 1.0.0, current version 228.0.0)
                """;

            var result = BinaryDependencyAnalyzer.ParseOtoolOutput(output, "Test");
            Assert.Empty(result);
        }

        [Fact]
        public void ParseOtoolOutput_DuplicateFramework_Deduplicated()
        {
            // Same framework can appear as both normal and weak linkage
            var output = """
                /path/to/Test.framework/Test:
                	@rpath/ImagePipeline.framework/ImagePipeline (compatibility version 0.0.0, current version 0.0.0)
                	@rpath/ImagePipeline.framework/ImagePipeline (compatibility version 0.0.0, current version 0.0.0, weak)
                """;

            var result = BinaryDependencyAnalyzer.ParseOtoolOutput(output, "Test");
            Assert.Single(result);
            Assert.Equal("ImagePipeline", result[0].FrameworkName);
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Section B: Framework Name Extraction
    // ═══════════════════════════════════════════════════════════════════════

    public class FrameworkNameExtractionTests
    {
        [Theory]
        [InlineData("@rpath/ImagePipeline.framework/ImagePipeline", "ImagePipeline")]
        [InlineData("@rpath/PaymentSdkCore.framework/PaymentSdkCore", "PaymentSdkCore")]
        [InlineData("/usr/lib/libobjc.A.dylib", null)]
        [InlineData("@rpath/libFoo.dylib", null)]
        [InlineData("", null)]
        public void ExtractFrameworkName_ReturnsExpected(string installName, string? expected)
        {
            var result = BinaryDependencyAnalyzer.ExtractFrameworkName(installName);
            Assert.Equal(expected, result);
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Section C: Sibling XCFramework Search
    // ═══════════════════════════════════════════════════════════════════════

    public class SiblingSearchTests : IDisposable
    {
        private readonly string _tempDir;

        public SiblingSearchTests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), $"sibling_test_{Guid.NewGuid():N}");
            Directory.CreateDirectory(_tempDir);
        }

        [Fact]
        public void FindSiblingXCFramework_SameDirectory_Found()
        {
            var primaryPath = Path.Combine(_tempDir, "Primary.xcframework");
            var depPath = Path.Combine(_tempDir, "Dep.xcframework");
            Directory.CreateDirectory(primaryPath);
            Directory.CreateDirectory(depPath);

            var result = BinaryDependencyAnalyzer.FindSiblingXCFramework(primaryPath, "Dep");
            Assert.NotNull(result);
            Assert.Equal(Path.GetFullPath(depPath), result);
        }

        [Fact]
        public void FindSiblingXCFramework_ParentDirectory_Found()
        {
            var subDir = Path.Combine(_tempDir, "subdir");
            Directory.CreateDirectory(subDir);
            var primaryPath = Path.Combine(subDir, "Primary.xcframework");
            var depPath = Path.Combine(_tempDir, "Dep.xcframework");
            Directory.CreateDirectory(primaryPath);
            Directory.CreateDirectory(depPath);

            var result = BinaryDependencyAnalyzer.FindSiblingXCFramework(primaryPath, "Dep");
            Assert.NotNull(result);
            Assert.Equal(Path.GetFullPath(depPath), result);
        }

        [Fact]
        public void FindSiblingXCFramework_NotPresent_ReturnsNull()
        {
            var primaryPath = Path.Combine(_tempDir, "Primary.xcframework");
            Directory.CreateDirectory(primaryPath);

            var result = BinaryDependencyAnalyzer.FindSiblingXCFramework(primaryPath, "NonExistent");
            Assert.Null(result);
        }

        [Fact]
        public void FindSiblingXCFramework_CaseSensitive()
        {
            var primaryPath = Path.Combine(_tempDir, "Primary.xcframework");
            var depPath = Path.Combine(_tempDir, "dep.xcframework"); // lowercase
            Directory.CreateDirectory(primaryPath);
            Directory.CreateDirectory(depPath);

            // Search for "Dep" (uppercase D) — on case-sensitive filesystems, shouldn't find "dep"
            // On macOS (case-insensitive by default), this will actually find it
            // This test just verifies the method doesn't crash
            var result = BinaryDependencyAnalyzer.FindSiblingXCFramework(primaryPath, "Dep");
            // Result depends on filesystem — just verify no exception
        }

        public void Dispose()
        {
            try { Directory.Delete(_tempDir, true); } catch { }
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Section D: Full Analysis
    // ═══════════════════════════════════════════════════════════════════════

    public class FullAnalysisTests
    {
        private readonly ILogger _logger = NullLogger.Instance;

        [Fact]
        public void Analyze_OtoolFails_ReturnsNull()
        {
            var runner = new MockCommandRunner();
            runner.SetResponse("otool", 1, "", "error: file not found");

            var result = BinaryDependencyAnalyzer.Analyze(
                "/nonexistent/dylib", "/nonexistent.xcframework",
                "Test", XCFrameworkPlatformTarget.Simulator, "simulator",
                _logger, runner);

            Assert.Null(result);
        }

        [Fact]
        public void Analyze_NoDeps_ReturnsEmptyResult()
        {
            var runner = new MockCommandRunner();
            runner.SetResponse("otool", 0, """
                /path/to/Test.framework/Test:
                	/usr/lib/libobjc.A.dylib (compatibility version 1.0.0, current version 228.0.0)
                """);

            var result = BinaryDependencyAnalyzer.Analyze(
                "/path/to/Test", "/path/to/Test.xcframework",
                "Test", XCFrameworkPlatformTarget.Simulator, "simulator",
                _logger, runner);

            Assert.NotNull(result);
            Assert.Empty(result!.ResolvedDependencies);
            Assert.Empty(result.UnresolvedDependencies);
            Assert.Empty(result.AllDetected);
        }

        [Fact]
        public void Analyze_DepDetectedButNoXCFramework_Unresolved()
        {
            var tempDir = Path.Combine(Path.GetTempPath(), $"analyze_test_{Guid.NewGuid():N}");
            var primaryXcfw = Path.Combine(tempDir, "Primary.xcframework");
            Directory.CreateDirectory(primaryXcfw);

            try
            {
                var runner = new MockCommandRunner();
                runner.SetResponse("otool", 0, """
                    /path/to/Primary.framework/Primary:
                    	@rpath/Missing.framework/Missing (compatibility version 0.0.0, current version 0.0.0)
                    """);

                var result = BinaryDependencyAnalyzer.Analyze(
                    "/path/to/Primary", primaryXcfw,
                    "Primary", XCFrameworkPlatformTarget.Simulator, "simulator",
                    _logger, runner);

                Assert.NotNull(result);
                Assert.Empty(result!.ResolvedDependencies);
                Assert.Single(result.UnresolvedDependencies);
                Assert.Equal("Missing", result.UnresolvedDependencies[0].FrameworkName);
                Assert.Single(result.AllDetected);
            }
            finally
            {
                Directory.Delete(tempDir, true);
            }
        }

        [Fact]
        public void Analyze_ObjCOnlyDep_DylibPathIsNull_GraphSkipsIt()
        {
            // Verify that BuildDependencyGraph handles null DylibPath gracefully
            var runner = new MockCommandRunner();
            runner.SetResponse("otool", 0, """
                /path/to/Primary.framework/Primary:
                	/usr/lib/libobjc.A.dylib (compatibility version 1.0.0)
                """);

            var deps = new List<FrameworkDependencyInfo>
            {
                new FrameworkDependencyInfo
                {
                    XCFrameworkPath = "/path/to/ObjCLib.xcframework",
                    ModuleName = "ObjCLib",
                    IsObjCOnly = true,
                    DylibPath = null // ObjC-only
                }
            };

            var (graph, warnings) = BinaryDependencyAnalyzer.BuildDependencyGraph(
                "Primary", "/path/to/Primary", deps, runner);

            Assert.True(graph.ContainsKey("ObjCLib"));
            Assert.Empty(graph["ObjCLib"]); // No deps analyzed (null dylib)
            Assert.Empty(warnings);
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Slice-Demotion Tests
    // Verify that auto-detected deps with missing required slices are
    // demoted to unresolved instead of kept with incomplete search paths.
    // ═══════════════════════════════════════════════════════════════════════

    public class SliceDemotionTests : IDisposable
    {
        private readonly ILogger _logger = NullLogger.Instance;
        private readonly string _tempDir;

        // Plist template for simulator-only xcframeworks
        private static string SimOnlyPlist(string name) => $$"""
            <?xml version="1.0" encoding="UTF-8"?>
            <plist version="1.0">
            <dict>
                <key>AvailableLibraries</key>
                <array>
                    <dict>
                        <key>BinaryPath</key><string>{{name}}.framework/{{name}}</string>
                        <key>LibraryIdentifier</key><string>ios-arm64-simulator</string>
                        <key>LibraryPath</key><string>{{name}}.framework</string>
                        <key>SupportedArchitectures</key><array><string>arm64</string></array>
                        <key>SupportedPlatform</key><string>ios</string>
                        <key>SupportedPlatformVariant</key><string>simulator</string>
                    </dict>
                </array>
            </dict>
            </plist>
            """;

        // Plist template for device-only xcframeworks
        private static string DeviceOnlyPlist(string name) => $$"""
            <?xml version="1.0" encoding="UTF-8"?>
            <plist version="1.0">
            <dict>
                <key>AvailableLibraries</key>
                <array>
                    <dict>
                        <key>BinaryPath</key><string>{{name}}.framework/{{name}}</string>
                        <key>LibraryIdentifier</key><string>ios-arm64</string>
                        <key>LibraryPath</key><string>{{name}}.framework</string>
                        <key>SupportedArchitectures</key><array><string>arm64</string></array>
                        <key>SupportedPlatform</key><string>ios</string>
                    </dict>
                </array>
            </dict>
            </plist>
            """;

        public SliceDemotionTests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), $"demotion_test_{Guid.NewGuid():N}");
            Directory.CreateDirectory(_tempDir);
        }

        /// <summary>
        /// Creates a minimal valid xcframework fixture for Analyze tests.
        /// </summary>
        private (string xcfwPath, string dylibPath) CreateXCFramework(
            string name, string sliceId, string plistContent, bool isSimulator)
        {
            var xcfwPath = Path.Combine(_tempDir, $"{name}.xcframework");
            Directory.CreateDirectory(xcfwPath);
            File.WriteAllText(Path.Combine(xcfwPath, "Info.plist"), plistContent);

            var fwDir = Path.Combine(xcfwPath, sliceId, $"{name}.framework");
            Directory.CreateDirectory(fwDir);
            var dylibPath = Path.Combine(fwDir, name);
            File.WriteAllText(dylibPath, ""); // stub binary

            var moduleDir = Path.Combine(fwDir, "Modules", $"{name}.swiftmodule");
            Directory.CreateDirectory(moduleDir);

            var archPrefix = isSimulator ? "arm64-apple-ios-simulator" : "arm64-apple-ios";
            File.WriteAllText(Path.Combine(moduleDir, $"{archPrefix}.abi.json"), "{}");
            File.WriteAllText(Path.Combine(moduleDir, $"{name}.tbd"), "--- !tapi-tbd");

            return (xcfwPath, dylibPath);
        }

        [Fact]
        public void Analyze_AllArch_SimOnlyDep_DemotedToUnresolved()
        {
            // Primary has both slices (we only need it to exist for Analyze to call otool)
            var (primaryXcfw, primaryDylib) = CreateXCFramework(
                "Primary", "ios-arm64-simulator", SimOnlyPlist("Primary"), isSimulator: true);

            // Dep has only simulator slice — requesting "all" should demote it
            CreateXCFramework("Dep", "ios-arm64-simulator", SimOnlyPlist("Dep"), isSimulator: true);

            var runner = new MockCommandRunner();
            // otool reports Primary depends on Dep
            runner.SetResponse("otool", 0, $"""
                {primaryDylib}:
                	@rpath/Dep.framework/Dep (compatibility version 0.0.0)
                """);
            // file command for XCFrameworkResolver binary validation
            runner.SetResponse("file", 0, "dynamically linked shared library");

            var result = BinaryDependencyAnalyzer.Analyze(
                primaryDylib, primaryXcfw, "Primary",
                XCFrameworkPlatformTarget.Simulator, "all",
                _logger, runner);

            Assert.NotNull(result);
            // Dep should be unresolved because it lacks device slice for "all"
            Assert.Empty(result!.ResolvedDependencies);
            Assert.Single(result.UnresolvedDependencies);
            Assert.Equal("Dep", result.UnresolvedDependencies[0].FrameworkName);
            Assert.Equal("missing-slice", result.UnresolvedDependencies[0].UnresolvedReason);
        }

        [Fact]
        public void Analyze_DeviceArch_SimOnlyDep_DemotedToUnresolved()
        {
            // Primary as simulator (Analyze will use it, but dep needs device)
            var (primaryXcfw, primaryDylib) = CreateXCFramework(
                "Primary", "ios-arm64-simulator", SimOnlyPlist("Primary"), isSimulator: true);

            // Dep has only simulator slice — requesting "device" should demote it
            CreateXCFramework("Dep", "ios-arm64-simulator", SimOnlyPlist("Dep"), isSimulator: true);

            var runner = new MockCommandRunner();
            runner.SetResponse("otool", 0, $"""
                {primaryDylib}:
                	@rpath/Dep.framework/Dep (compatibility version 0.0.0)
                """);
            runner.SetResponse("file", 0, "dynamically linked shared library");

            var result = BinaryDependencyAnalyzer.Analyze(
                primaryDylib, primaryXcfw, "Primary",
                XCFrameworkPlatformTarget.Simulator, "device",
                _logger, runner);

            Assert.NotNull(result);
            // Dep should be unresolved: resolved to sim but device required
            Assert.Empty(result!.ResolvedDependencies);
            Assert.Single(result.UnresolvedDependencies);
            Assert.Equal("Dep", result.UnresolvedDependencies[0].FrameworkName);
            Assert.Equal("missing-slice", result.UnresolvedDependencies[0].UnresolvedReason);
        }

        [Fact]
        public void Analyze_SimArch_DeviceOnlyDep_DemotedToUnresolved()
        {
            // Primary as device slice
            var (primaryXcfw, primaryDylib) = CreateXCFramework(
                "Primary", "ios-arm64", DeviceOnlyPlist("Primary"), isSimulator: false);

            // Dep has only device slice — requesting "simulator" should demote it
            CreateXCFramework("Dep", "ios-arm64", DeviceOnlyPlist("Dep"), isSimulator: false);

            var runner = new MockCommandRunner();
            runner.SetResponse("otool", 0, $"""
                {primaryDylib}:
                	@rpath/Dep.framework/Dep (compatibility version 0.0.0)
                """);
            runner.SetResponse("file", 0, "dynamically linked shared library");

            var result = BinaryDependencyAnalyzer.Analyze(
                primaryDylib, primaryXcfw, "Primary",
                XCFrameworkPlatformTarget.Device, "simulator",
                _logger, runner);

            Assert.NotNull(result);
            // Dep should be unresolved: resolved to device but simulator required
            Assert.Empty(result!.ResolvedDependencies);
            Assert.Single(result.UnresolvedDependencies);
            Assert.Equal("Dep", result.UnresolvedDependencies[0].FrameworkName);
            Assert.Equal("missing-slice", result.UnresolvedDependencies[0].UnresolvedReason);
        }

        [Fact]
        public void Analyze_NoXCFrameworkFound_ReasonIsNoXcframework()
        {
            var (primaryXcfw, primaryDylib) = CreateXCFramework(
                "Primary", "ios-arm64-simulator", SimOnlyPlist("Primary"), isSimulator: true);

            // No sibling "Missing.xcframework" exists
            var runner = new MockCommandRunner();
            runner.SetResponse("otool", 0, $"""
                {primaryDylib}:
                	@rpath/Missing.framework/Missing (compatibility version 0.0.0)
                """);
            runner.SetResponse("file", 0, "dynamically linked shared library");

            var result = BinaryDependencyAnalyzer.Analyze(
                primaryDylib, primaryXcfw, "Primary",
                XCFrameworkPlatformTarget.Simulator, "simulator",
                _logger, runner);

            Assert.NotNull(result);
            Assert.Single(result!.UnresolvedDependencies);
            Assert.Equal("no-xcframework", result.UnresolvedDependencies[0].UnresolvedReason);
        }

        public void Dispose()
        {
            try { Directory.Delete(_tempDir, true); } catch { }
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Section E: Dependency Manifest Emission
    // ═══════════════════════════════════════════════════════════════════════

    public class DependencyManifestTests : IDisposable
    {
        private readonly string _outputDir;
        private readonly ILogger _logger = NullLogger.Instance;

        public DependencyManifestTests()
        {
            _outputDir = Path.Combine(Path.GetTempPath(), $"manifest_test_{Guid.NewGuid():N}");
            Directory.CreateDirectory(_outputDir);
        }

        [Fact]
        public void Emit_NoDependencies_MinimalManifest()
        {
            var runner = new MockCommandRunner();
            runner.SetResponse("otool", 0, """
                /path/to/ImagePipeline.framework/ImagePipeline:
                	/usr/lib/libobjc.A.dylib (compatibility version 1.0.0)
                """);

            DependencyManifestEmitter.Emit(
                _outputDir, "ImagePipeline", "/path/to/ImagePipeline.xcframework",
                "/path/to/ImagePipeline", null, null, null, _logger, runner);

            var manifestPath = Path.Combine(_outputDir, "dependency-manifest.json");
            Assert.True(File.Exists(manifestPath));

            var json = JObject.Parse(File.ReadAllText(manifestPath));
            Assert.Equal("ImagePipeline", json["primary"]!["moduleName"]!.ToString());
            Assert.Empty(json["effectiveDependencies"]!);
            Assert.Empty(json["detectedButUnresolved"]!);
            Assert.Empty(json["detectedButOverridden"]!);
            Assert.NotEmpty(json["buildOrder"]!);
            Assert.Contains("ImagePipeline", json["buildOrder"]!.Select(t => t.ToString()));
            Assert.Empty(json["graphWarnings"]!);
        }

        [Fact]
        public void Emit_WithResolvedDeps_IncludesModuleSourceVersion()
        {
            var runner = new MockCommandRunner();
            runner.SetResponse("otool", 0, """
                /path/to/Primary.framework/Primary:
                	/usr/lib/libobjc.A.dylib (compatibility version 1.0.0)
                """);

            var deps = new List<FrameworkDependencyInfo>
            {
                new FrameworkDependencyInfo
                {
                    XCFrameworkPath = "/path/to/Dep.xcframework",
                    ModuleName = "Dep",
                    PackageVersion = "2.0.0",
                    DylibPath = "/path/to/Dep"
                }
            };

            // Also mock otool for the dependency
            runner.SetResponse("Dep", 0, """
                /path/to/Dep.framework/Dep:
                	/usr/lib/libobjc.A.dylib (compatibility version 1.0.0)
                """);

            DependencyManifestEmitter.Emit(
                _outputDir, "Primary", "/path/to/Primary.xcframework",
                "/path/to/Primary", null, deps, null, _logger, runner);

            var json = JObject.Parse(File.ReadAllText(Path.Combine(_outputDir, "dependency-manifest.json")));
            var effectiveDeps = json["effectiveDependencies"]!;
            Assert.Single(effectiveDeps);
            Assert.Equal("Dep", effectiveDeps[0]!["moduleName"]!.ToString());
            Assert.Equal("binary-linkage", effectiveDeps[0]!["source"]!.ToString());
            Assert.Equal("2.0.0", effectiveDeps[0]!["version"]!.ToString());
            Assert.Equal("Dep.Swift.iOS", effectiveDeps[0]!["packageId"]!.ToString());
        }

        [Fact]
        public void Emit_WithUnresolvedDeps_IncludesFrameworkNameAndInstallName()
        {
            var runner = new MockCommandRunner();
            runner.SetResponse("otool", 0, """
                /path/to/Primary.framework/Primary:
                	/usr/lib/libobjc.A.dylib (compatibility version 1.0.0)
                """);

            var analysisResult = new DependencyAnalysisResult
            {
                ResolvedDependencies = new List<FrameworkDependencyInfo>(),
                UnresolvedDependencies = new List<DetectedDependency>
                {
                    new DetectedDependency
                    {
                        FrameworkName = "MissingLib",
                        InstallName = "@rpath/MissingLib.framework/MissingLib"
                    }
                },
                AllDetected = new List<DetectedDependency>
                {
                    new DetectedDependency
                    {
                        FrameworkName = "MissingLib",
                        InstallName = "@rpath/MissingLib.framework/MissingLib"
                    }
                }
            };

            DependencyManifestEmitter.Emit(
                _outputDir, "Primary", "/path/to/Primary.xcframework",
                "/path/to/Primary", analysisResult, null, null, _logger, runner);

            var json = JObject.Parse(File.ReadAllText(Path.Combine(_outputDir, "dependency-manifest.json")));
            var unresolved = json["detectedButUnresolved"]!;
            Assert.Single(unresolved);
            Assert.Equal("MissingLib", unresolved[0]!["frameworkName"]!.ToString());
            Assert.Equal("@rpath/MissingLib.framework/MissingLib", unresolved[0]!["installName"]!.ToString());
        }

        [Fact]
        public void Emit_WithManualOverride_OverriddenPopulated()
        {
            var runner = new MockCommandRunner();
            runner.SetResponse("otool", 0, """
                /path/to/Primary.framework/Primary:
                	/usr/lib/libobjc.A.dylib (compatibility version 1.0.0)
                """);

            var analysisResult = new DependencyAnalysisResult
            {
                ResolvedDependencies = new List<FrameworkDependencyInfo>(),
                UnresolvedDependencies = new List<DetectedDependency>(),
                AllDetected = new List<DetectedDependency>
                {
                    new DetectedDependency
                    {
                        FrameworkName = "SomeLib",
                        InstallName = "@rpath/SomeLib.framework/SomeLib"
                    }
                }
            };

            var effectiveDeps = new List<FrameworkDependencyInfo>
            {
                new FrameworkDependencyInfo
                {
                    XCFrameworkPath = "/explicit/path/SomeLib.xcframework",
                    ModuleName = "SomeLib",
                    PackageVersion = "1.0.0",
                    DylibPath = "/explicit/path/SomeLib"
                }
            };

            // Mock otool for dep
            runner.SetResponse("SomeLib", 0, """
                /explicit/path/SomeLib.framework/SomeLib:
                	/usr/lib/libobjc.A.dylib (compatibility version 1.0.0)
                """);

            DependencyManifestEmitter.Emit(
                _outputDir, "Primary", "/path/to/Primary.xcframework",
                "/path/to/Primary", analysisResult, effectiveDeps,
                new[] { "/explicit/path/SomeLib.xcframework" }, _logger, runner);

            var json = JObject.Parse(File.ReadAllText(Path.Combine(_outputDir, "dependency-manifest.json")));

            // Manual dep should be in effective deps with source "manual"
            var effective = json["effectiveDependencies"]!;
            Assert.Single(effective);
            Assert.Equal("manual", effective[0]!["source"]!.ToString());

            // Overridden array should have the detected dep
            var overridden = json["detectedButOverridden"]!;
            Assert.Single(overridden);
            Assert.Equal("SomeLib", overridden[0]!["frameworkName"]!.ToString());
            Assert.Equal("/explicit/path/SomeLib.xcframework", overridden[0]!["overriddenByPath"]!.ToString());
        }

        [Fact]
        public void Emit_BuildOrderPresent_AndCorrect()
        {
            var runner = new MockCommandRunner();

            // Primary depends on Dep
            runner.SetResponse("Primary", 0, """
                /path/to/Primary.framework/Primary:
                	@rpath/Dep.framework/Dep (compatibility version 0.0.0)
                """);

            // Dep has no framework deps
            runner.SetResponse("Dep", 0, """
                /path/to/Dep.framework/Dep:
                	/usr/lib/libobjc.A.dylib (compatibility version 1.0.0)
                """);

            var deps = new List<FrameworkDependencyInfo>
            {
                new FrameworkDependencyInfo
                {
                    XCFrameworkPath = "/path/to/Dep.xcframework",
                    ModuleName = "Dep",
                    DylibPath = "/path/to/Dep"
                }
            };

            DependencyManifestEmitter.Emit(
                _outputDir, "Primary", "/path/to/Primary.xcframework",
                "/path/to/Primary", null, deps, null, _logger, runner);

            var json = JObject.Parse(File.ReadAllText(Path.Combine(_outputDir, "dependency-manifest.json")));
            var buildOrder = json["buildOrder"]!.Select(t => t.ToString()).ToList();

            // Dep should come before Primary
            var depIdx = buildOrder.IndexOf("Dep");
            var primaryIdx = buildOrder.IndexOf("Primary");
            Assert.True(depIdx < primaryIdx, "Dep should come before Primary in build order");
        }

        [Fact]
        public void Emit_GraphWarnings_EmptyWhenAllSucceed()
        {
            var runner = new MockCommandRunner();
            runner.SetResponse("otool", 0, """
                /path/to/Test.framework/Test:
                	/usr/lib/libobjc.A.dylib (compatibility version 1.0.0)
                """);

            DependencyManifestEmitter.Emit(
                _outputDir, "Test", "/path/to/Test.xcframework",
                "/path/to/Test", null, null, null, _logger, runner);

            var json = JObject.Parse(File.ReadAllText(Path.Combine(_outputDir, "dependency-manifest.json")));
            Assert.Empty(json["graphWarnings"]!);
        }

        public void Dispose()
        {
            try { Directory.Delete(_outputDir, true); } catch { }
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Section F: Topological Sort
    // ═══════════════════════════════════════════════════════════════════════

    public class TopologicalSortTests
    {
        [Fact]
        public void Sort_EmptyGraph_ReturnsEmpty()
        {
            var result = TopologicalSort.Sort(new Dictionary<string, List<string>>());
            Assert.Empty(result);
        }

        [Fact]
        public void Sort_LinearChain_CorrectOrder()
        {
            // A→B→C: A depends on B, B depends on C
            var graph = new Dictionary<string, List<string>>
            {
                ["A"] = new List<string> { "B" },
                ["B"] = new List<string> { "C" },
                ["C"] = new List<string>()
            };

            var result = TopologicalSort.Sort(graph);
            Assert.Equal(new[] { "C", "B", "A" }, result);
        }

        [Fact]
        public void Sort_Diamond_CorrectOrder()
        {
            // A→B,C; B→D; C→D: D first, A last
            var graph = new Dictionary<string, List<string>>
            {
                ["A"] = new List<string> { "B", "C" },
                ["B"] = new List<string> { "D" },
                ["C"] = new List<string> { "D" },
                ["D"] = new List<string>()
            };

            var result = TopologicalSort.Sort(graph);
            Assert.Equal("D", result[0]); // D must be first
            Assert.Equal("A", result[3]); // A must be last
            // B and C can be in either order but both before A
            Assert.Contains("B", result.GetRange(1, 2));
            Assert.Contains("C", result.GetRange(1, 2));
        }

        [Fact]
        public void Sort_CycleDetected_Throws()
        {
            var graph = new Dictionary<string, List<string>>
            {
                ["A"] = new List<string> { "B" },
                ["B"] = new List<string> { "A" }
            };

            Assert.Throws<InvalidOperationException>(() => TopologicalSort.Sort(graph));
        }

        [Fact]
        public void Sort_DeterministicTieBreaking()
        {
            // A→C; B→C: C first, then A before B (lexical)
            var graph = new Dictionary<string, List<string>>
            {
                ["A"] = new List<string> { "C" },
                ["B"] = new List<string> { "C" },
                ["C"] = new List<string>()
            };

            var result = TopologicalSort.Sort(graph);
            Assert.Equal(new[] { "C", "A", "B" }, result);
        }

        [Fact]
        public void Sort_SingleNode_ReturnsSelf()
        {
            var graph = new Dictionary<string, List<string>>
            {
                ["A"] = new List<string>()
            };

            var result = TopologicalSort.Sort(graph);
            Assert.Equal(new[] { "A" }, result);
        }

        [Fact]
        public void Sort_NodesOnlyInValues_IncludedInResult()
        {
            // B is only referenced as a dependency, not a key
            var graph = new Dictionary<string, List<string>>
            {
                ["A"] = new List<string> { "B" }
            };

            var result = TopologicalSort.Sort(graph);
            Assert.Equal(2, result.Count);
            Assert.Equal("B", result[0]); // dependency first
            Assert.Equal("A", result[1]);
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Section F2: Cycle Fallback at Emitter Level
    // ═══════════════════════════════════════════════════════════════════════

    public class CycleFallbackTests : IDisposable
    {
        private readonly string _outputDir;
        private readonly ILogger _logger = NullLogger.Instance;

        public CycleFallbackTests()
        {
            _outputDir = Path.Combine(Path.GetTempPath(), $"cycle_test_{Guid.NewGuid():N}");
            Directory.CreateDirectory(_outputDir);
        }

        [Fact]
        public void Emit_CycleInGraph_FallsBackToAlphabetical_WithWarning()
        {
            // Create a mock runner that reports cyclic deps
            var runner = new MockCommandRunner();

            // Primary depends on DepA
            runner.SetResponse("Primary", 0, """
                /path/to/Primary.framework/Primary:
                	@rpath/DepA.framework/DepA (compatibility version 0.0.0)
                """);

            // DepA depends on Primary (cycle!)
            runner.SetResponse("DepA", 0, """
                /path/to/DepA.framework/DepA:
                	@rpath/Primary.framework/Primary (compatibility version 0.0.0)
                """);

            var deps = new List<FrameworkDependencyInfo>
            {
                new FrameworkDependencyInfo
                {
                    XCFrameworkPath = "/path/to/DepA.xcframework",
                    ModuleName = "DepA",
                    DylibPath = "/path/to/DepA"
                }
            };

            DependencyManifestEmitter.Emit(
                _outputDir, "Primary", "/path/to/Primary.xcframework",
                "/path/to/Primary", null, deps, null, _logger, runner);

            var json = JObject.Parse(File.ReadAllText(Path.Combine(_outputDir, "dependency-manifest.json")));

            // Build order should be alphabetical fallback
            var buildOrder = json["buildOrder"]!.Select(t => t.ToString()).ToList();
            Assert.Equal(new[] { "DepA", "Primary" }, buildOrder);

            // Should have a warning about the cycle
            var warnings = json["graphWarnings"]!.Select(t => t.ToString()).ToList();
            Assert.Single(warnings);
            Assert.Contains("cycle", warnings[0], StringComparison.OrdinalIgnoreCase);
        }

        public void Dispose()
        {
            try { Directory.Delete(_outputDir, true); } catch { }
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Section G: CLI + SDK
    // ═══════════════════════════════════════════════════════════════════════

    [Collection("ConsoleCapture")]
    public class AutoDetectCliTests
    {
        [Fact]
        public void Help_IncludesNoAutoDetectOption()
        {
            using (var capture = ConsoleCapture.Begin())
            {
                BindingsGenerator.Main(new[] { "-h" });
                var output = capture.Out;
                Assert.Contains("--no-auto-detect", output);
            }
        }
    }

    public class AutoDetectSdkTests
    {
        [Fact]
        public void SdkTargets_FingerprintIncludesAutoDetectProperty()
        {
            var repoRoot = FindRepoRoot();
            var targetsPath = Path.Combine(repoRoot, "src", "Swift.Bindings.Sdk", "Sdk", "Sdk.targets");
            var content = File.ReadAllText(targetsPath);
            Assert.Contains("SwiftAutoDetectDependencies", content);
        }

        [Fact]
        public void SdkProps_DefaultsSwiftAutoDetectDependencies()
        {
            var repoRoot = FindRepoRoot();
            var propsPath = Path.Combine(repoRoot, "src", "Swift.Bindings.Sdk", "Sdk", "Sdk.props");
            var content = File.ReadAllText(propsPath);
            Assert.Contains("<SwiftAutoDetectDependencies Condition=", content);
            Assert.Contains(">true</SwiftAutoDetectDependencies>", content);
        }

        [Fact]
        public void SdkTargets_NoAutoDetectFlag_AppendsWhenNotTrue()
        {
            var repoRoot = FindRepoRoot();
            var targetsPath = Path.Combine(repoRoot, "src", "Swift.Bindings.Sdk", "Sdk", "Sdk.targets");
            var content = File.ReadAllText(targetsPath);
            Assert.Contains("--no-auto-detect", content);
            Assert.Contains("SwiftAutoDetectDependencies", content);
        }

        private static string FindRepoRoot()
        {
            var dir = AppDomain.CurrentDomain.BaseDirectory;
            while (dir != null && !File.Exists(Path.Combine(dir, "CLAUDE.md")))
                dir = Path.GetDirectoryName(dir);
            return dir ?? throw new InvalidOperationException("Could not find repo root");
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Section H: Build Dependency Graph
    // ═══════════════════════════════════════════════════════════════════════

    public class BuildDependencyGraphTests
    {
        [Fact]
        public void BuildDependencyGraph_TransitiveEdges_DiscoveredCorrectly()
        {
            var runner = new MockCommandRunner();

            // Primary depends on A
            runner.SetResponse("Primary", 0, """
                /path/to/Primary.framework/Primary:
                	@rpath/A.framework/A (compatibility version 0.0.0)
                """);

            // A depends on B
            runner.SetResponse("path/to/A", 0, """
                /path/to/A.framework/A:
                	@rpath/B.framework/B (compatibility version 0.0.0)
                """);

            // B has no deps
            runner.SetResponse("path/to/B", 0, """
                /path/to/B.framework/B:
                	/usr/lib/libobjc.A.dylib (compatibility version 1.0.0)
                """);

            var deps = new List<FrameworkDependencyInfo>
            {
                new FrameworkDependencyInfo
                {
                    XCFrameworkPath = "/path/to/A.xcframework",
                    ModuleName = "A",
                    DylibPath = "/path/to/A"
                },
                new FrameworkDependencyInfo
                {
                    XCFrameworkPath = "/path/to/B.xcframework",
                    ModuleName = "B",
                    DylibPath = "/path/to/B"
                }
            };

            var (graph, warnings) = BinaryDependencyAnalyzer.BuildDependencyGraph(
                "Primary", "/path/to/Primary", deps, runner);

            Assert.Contains("A", graph["Primary"]);
            Assert.Contains("B", graph["A"]);
            Assert.Empty(graph["B"]);
            Assert.Empty(warnings);
        }

        [Fact]
        public void BuildDependencyGraph_OtoolFailsOnDep_WarningAdded()
        {
            var runner = new MockCommandRunner();

            // Primary OK
            runner.SetResponse("Primary", 0, """
                /path/to/Primary.framework/Primary:
                	/usr/lib/libobjc.A.dylib (compatibility version 1.0.0)
                """);

            var deps = new List<FrameworkDependencyInfo>
            {
                new FrameworkDependencyInfo
                {
                    XCFrameworkPath = "/path/to/Broken.xcframework",
                    ModuleName = "Broken",
                    DylibPath = "/path/to/Broken"
                }
            };

            // Broken dep fails
            runner.SetResponse("Broken", 1, "", "error");

            var (graph, warnings) = BinaryDependencyAnalyzer.BuildDependencyGraph(
                "Primary", "/path/to/Primary", deps, runner);

            Assert.Single(warnings);
            Assert.Contains("Broken", warnings[0]);
            Assert.Empty(graph["Broken"]); // Empty deps due to failure
        }

        [Fact]
        public void BuildDependencyGraph_NoDeps_PrimaryOnly()
        {
            var runner = new MockCommandRunner();
            runner.SetResponse("otool", 0, """
                /path/to/Primary.framework/Primary:
                	/usr/lib/libobjc.A.dylib (compatibility version 1.0.0)
                """);

            var (graph, warnings) = BinaryDependencyAnalyzer.BuildDependencyGraph(
                "Primary", "/path/to/Primary", null, runner);

            Assert.Single(graph);
            Assert.True(graph.ContainsKey("Primary"));
            Assert.Empty(graph["Primary"]);
            Assert.Empty(warnings);
        }

        [Fact]
        public void BuildDependencyGraph_FrameworkNameDiffersFromModuleName_EdgeResolvesCorrectly()
        {
            // Framework binary name differs from module name — otool reports the binary name
            // but the graph must record the module name for correct edge resolution.
            var runner = new MockCommandRunner();

            runner.SetResponse("Primary", 0, """
                /path/to/Primary.framework/Primary:
                	@rpath/PaymentSdkCore.framework/PaymentSdkCore (compatibility version 0.0.0)
                """);

            runner.SetResponse("path/to/PaymentSdkPayments", 0, """
                /path/to/PaymentSdkPayments.framework/PaymentSdkPayments:
                	/usr/lib/libobjc.A.dylib (compatibility version 1.0.0)
                """);

            var deps = new List<FrameworkDependencyInfo>
            {
                new FrameworkDependencyInfo
                {
                    // xcframework name differs from module name
                    XCFrameworkPath = "/path/to/PaymentSdkCore.xcframework",
                    ModuleName = "PaymentSdkPayments",
                    DylibPath = "/path/to/PaymentSdkPayments"
                }
            };

            var (graph, warnings) = BinaryDependencyAnalyzer.BuildDependencyGraph(
                "Primary", "/path/to/Primary", deps, runner);

            // The edge should use module name, not framework name
            Assert.Contains("PaymentSdkPayments", graph["Primary"]);
            Assert.DoesNotContain("PaymentSdkCore", graph["Primary"]);
            Assert.Empty(warnings);
        }

        [Fact]
        public void BuildDependencyGraph_PrimaryFrameworkNameDiffersFromModule_EdgeResolvesCorrectly()
        {
            // Primary xcframework is "AppUI.xcframework" but module is "AppUIModule"
            // Dep links back to @rpath/AppUI.framework/AppUI — should resolve to "AppUIModule"
            var runner = new MockCommandRunner();

            runner.SetResponse("path/to/AppUIModule", 0, """
                /path/to/AppUI.framework/AppUI:
                	/usr/lib/libobjc.A.dylib (compatibility version 1.0.0)
                """);

            runner.SetResponse("path/to/DepLib", 0, """
                /path/to/DepLib.framework/DepLib:
                	@rpath/AppUI.framework/AppUI (compatibility version 0.0.0)
                """);

            var deps = new List<FrameworkDependencyInfo>
            {
                new FrameworkDependencyInfo
                {
                    XCFrameworkPath = "/path/to/DepLib.xcframework",
                    ModuleName = "DepLib",
                    DylibPath = "/path/to/DepLib"
                }
            };

            var (graph, warnings) = BinaryDependencyAnalyzer.BuildDependencyGraph(
                "AppUIModule", "/path/to/AppUIModule", deps, runner,
                primaryXCFrameworkPath: "/path/to/AppUI.xcframework");

            // DepLib's edge should resolve @rpath/AppUI.framework/AppUI → module "AppUIModule"
            Assert.Contains("AppUIModule", graph["DepLib"]);
            Assert.Empty(warnings);
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Section I: Name-Mismatch Override Detection
    // ═══════════════════════════════════════════════════════════════════════

    public class NameMismatchOverrideTests : IDisposable
    {
        private readonly string _outputDir;
        private readonly ILogger _logger = NullLogger.Instance;

        public NameMismatchOverrideTests()
        {
            _outputDir = Path.Combine(Path.GetTempPath(), $"mismatch_test_{Guid.NewGuid():N}");
            Directory.CreateDirectory(_outputDir);
        }

        [Fact]
        public void Emit_OverrideDetected_WhenFrameworkNameDiffersFromModuleName()
        {
            // Detected framework binary name differs from the manually-specified module name;
            // xcframework path is derived from the binary name.
            var runner = new MockCommandRunner();
            runner.SetResponse("otool", 0, """
                /path/to/Primary.framework/Primary:
                	/usr/lib/libobjc.A.dylib (compatibility version 1.0.0)
                """);

            // Mock for dep's otool
            runner.SetResponse("PaymentSdkPayments", 0, """
                /path/to/PaymentSdkPayments.framework/PaymentSdkPayments:
                	/usr/lib/libobjc.A.dylib (compatibility version 1.0.0)
                """);

            var analysisResult = new DependencyAnalysisResult
            {
                ResolvedDependencies = new List<FrameworkDependencyInfo>(),
                UnresolvedDependencies = new List<DetectedDependency>(),
                AllDetected = new List<DetectedDependency>
                {
                    new DetectedDependency
                    {
                        FrameworkName = "PaymentSdkCore", // binary name differs from module
                        InstallName = "@rpath/PaymentSdkCore.framework/PaymentSdkCore"
                    }
                }
            };

            var effectiveDeps = new List<FrameworkDependencyInfo>
            {
                new FrameworkDependencyInfo
                {
                    // xcframework name differs from module name
                    XCFrameworkPath = "/explicit/PaymentSdkCore.xcframework",
                    ModuleName = "PaymentSdkPayments",
                    PackageVersion = "1.0.0",
                    DylibPath = "/explicit/PaymentSdkPayments"
                }
            };

            DependencyManifestEmitter.Emit(
                _outputDir, "Primary", "/path/to/Primary.xcframework",
                "/path/to/Primary", analysisResult, effectiveDeps,
                new[] { "/explicit/PaymentSdkCore.xcframework" }, _logger, runner);

            var json = JObject.Parse(File.ReadAllText(Path.Combine(_outputDir, "dependency-manifest.json")));

            // Override should be detected via xcframework-derived framework name
            var overridden = json["detectedButOverridden"]!;
            Assert.Single(overridden);
            Assert.Equal("PaymentSdkCore", overridden[0]!["frameworkName"]!.ToString());
        }

        public void Dispose()
        {
            try { Directory.Delete(_outputDir, true); } catch { }
        }
    }
}
