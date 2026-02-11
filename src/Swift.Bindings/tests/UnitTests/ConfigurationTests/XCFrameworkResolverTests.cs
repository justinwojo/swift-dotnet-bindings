// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BindingsGeneration.Tests
{
    /// <summary>
    /// Mock command runner for unit testing xcrun subprocess calls.
    /// </summary>
    internal sealed class MockCommandRunner : ICommandRunner
    {
        private readonly Dictionary<string, (int ExitCode, string StdOut, string StdErr)> _responses = new();

        public List<(string Command, string Arguments)> Invocations { get; } = new();

        public void SetResponse(string matchKey, int exitCode, string stdOut, string stdErr = "")
        {
            _responses[matchKey] = (exitCode, stdOut, stdErr);
        }

        public (int ExitCode, string StdOut, string StdErr) Run(string command, string arguments, int timeoutMs = 30000)
        {
            Invocations.Add((command, arguments));

            // Match against both command name and arguments
            var fullKey = $"{command} {arguments}";
            foreach (var (key, response) in _responses)
            {
                if (fullKey.Contains(key))
                    return response;
            }

            return (0, "", "");
        }
    }

    /// <summary>
    /// Helper to build temporary xcframework directory fixtures for testing.
    /// </summary>
    internal sealed class XCFrameworkFixture : IDisposable
    {
        public string RootPath { get; }
        public string OutputPath { get; }

        public XCFrameworkFixture(string name = "Test.xcframework")
        {
            RootPath = Path.Combine(Path.GetTempPath(), $"xcfw_test_{Guid.NewGuid():N}", name);
            OutputPath = Path.Combine(Path.GetDirectoryName(RootPath)!, "output");
            Directory.CreateDirectory(RootPath);
            Directory.CreateDirectory(OutputPath);
        }

        public void WriteInfoPlist(string xml)
        {
            File.WriteAllText(Path.Combine(RootPath, "Info.plist"), xml);
        }

        public string CreateSlice(string identifier, string libraryPath, string binaryPath)
        {
            var sliceDir = Path.Combine(RootPath, identifier);
            var binaryFullPath = Path.Combine(sliceDir, binaryPath);
            Directory.CreateDirectory(Path.GetDirectoryName(binaryFullPath)!);
            File.WriteAllText(binaryFullPath, ""); // stub
            return sliceDir;
        }

        public string CreateSwiftModule(string sliceDir, string libraryPath, string moduleName)
        {
            var moduleDir = Path.Combine(sliceDir, libraryPath, "Modules", $"{moduleName}.swiftmodule");
            Directory.CreateDirectory(moduleDir);
            return moduleDir;
        }

        public void CreateAbiJson(string moduleDir, string archPrefix)
        {
            File.WriteAllText(Path.Combine(moduleDir, $"{archPrefix}.abi.json"), "{}");
        }

        public void CreateSwiftInterface(string moduleDir, string archPrefix)
        {
            File.WriteAllText(Path.Combine(moduleDir, $"{archPrefix}.swiftinterface"), "// swift-interface");
        }

        public void CreatePrivateSwiftInterface(string moduleDir, string archPrefix)
        {
            File.WriteAllText(Path.Combine(moduleDir, $"{archPrefix}.private.swiftinterface"), "// private");
        }

        public void CreateTbd(string moduleDir, string name)
        {
            File.WriteAllText(Path.Combine(moduleDir, $"{name}.tbd"), "--- !tapi-tbd");
        }

        public void Dispose()
        {
            var parent = Path.GetDirectoryName(RootPath)!;
            if (Directory.Exists(parent))
            {
                try { Directory.Delete(parent, true); } catch { }
            }
        }
    }

    #region A. Plist Parsing Tests

    public class XCFrameworkPlistParsingTests
    {
        private static readonly ILogger Logger = NullLogger.Instance;

        private const string NukeStylePlist = """
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0">
            <dict>
                <key>AvailableLibraries</key>
                <array>
                    <dict>
                        <key>BinaryPath</key><string>Nuke.framework/Nuke</string>
                        <key>LibraryIdentifier</key><string>ios-arm64_x86_64-simulator</string>
                        <key>LibraryPath</key><string>Nuke.framework</string>
                        <key>SupportedArchitectures</key><array><string>arm64</string><string>x86_64</string></array>
                        <key>SupportedPlatform</key><string>ios</string>
                        <key>SupportedPlatformVariant</key><string>simulator</string>
                    </dict>
                    <dict>
                        <key>BinaryPath</key><string>Nuke.framework/Nuke</string>
                        <key>LibraryIdentifier</key><string>ios-arm64</string>
                        <key>LibraryPath</key><string>Nuke.framework</string>
                        <key>SupportedArchitectures</key><array><string>arm64</string></array>
                        <key>SupportedPlatform</key><string>ios</string>
                    </dict>
                </array>
                <key>CFBundlePackageType</key><string>XFWK</string>
                <key>XCFrameworkFormatVersion</key><string>1.0</string>
            </dict>
            </plist>
            """;

        private const string TestFrameworkStylePlist = """
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0">
            <dict>
                <key>AvailableLibraries</key>
                <array>
                    <dict>
                        <key>BinaryPath</key><string>TestLib.framework/TestLib</string>
                        <key>LibraryIdentifier</key><string>ios-arm64-simulator</string>
                        <key>LibraryPath</key><string>TestLib.framework</string>
                        <key>SupportedArchitectures</key><array><string>arm64</string></array>
                        <key>SupportedPlatform</key><string>ios</string>
                        <key>SupportedPlatformVariant</key><string>simulator</string>
                    </dict>
                </array>
                <key>CFBundlePackageType</key><string>XFWK</string>
            </dict>
            </plist>
            """;

        private const string LottieStylePlist = """
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0">
            <dict>
                <key>AvailableLibraries</key>
                <array>
                    <dict>
                        <key>BinaryPath</key><string>Lottie.framework/Lottie</string>
                        <key>LibraryIdentifier</key><string>ios-arm64</string>
                        <key>LibraryPath</key><string>Lottie.framework</string>
                        <key>SupportedArchitectures</key><array><string>arm64</string></array>
                        <key>SupportedPlatform</key><string>ios</string>
                    </dict>
                    <dict>
                        <key>BinaryPath</key><string>Lottie.framework/Lottie</string>
                        <key>LibraryIdentifier</key><string>tvos-arm64_x86_64-simulator</string>
                        <key>LibraryPath</key><string>Lottie.framework</string>
                        <key>SupportedArchitectures</key><array><string>arm64</string><string>x86_64</string></array>
                        <key>SupportedPlatform</key><string>tvos</string>
                        <key>SupportedPlatformVariant</key><string>simulator</string>
                    </dict>
                    <dict>
                        <key>BinaryPath</key><string>Lottie.framework/Versions/A/Lottie</string>
                        <key>LibraryIdentifier</key><string>ios-arm64_x86_64-maccatalyst</string>
                        <key>LibraryPath</key><string>Lottie.framework</string>
                        <key>SupportedArchitectures</key><array><string>arm64</string><string>x86_64</string></array>
                        <key>SupportedPlatform</key><string>ios</string>
                        <key>SupportedPlatformVariant</key><string>maccatalyst</string>
                    </dict>
                    <dict>
                        <key>BinaryPath</key><string>Lottie.framework/Versions/A/Lottie</string>
                        <key>LibraryIdentifier</key><string>macos-arm64_x86_64</string>
                        <key>LibraryPath</key><string>Lottie.framework</string>
                        <key>SupportedArchitectures</key><array><string>arm64</string><string>x86_64</string></array>
                        <key>SupportedPlatform</key><string>macos</string>
                    </dict>
                    <dict>
                        <key>BinaryPath</key><string>Lottie.framework/Lottie</string>
                        <key>LibraryIdentifier</key><string>xros-arm64</string>
                        <key>LibraryPath</key><string>Lottie.framework</string>
                        <key>SupportedArchitectures</key><array><string>arm64</string></array>
                        <key>SupportedPlatform</key><string>xros</string>
                    </dict>
                    <dict>
                        <key>BinaryPath</key><string>Lottie.framework/Lottie</string>
                        <key>LibraryIdentifier</key><string>xros-arm64_x86_64-simulator</string>
                        <key>LibraryPath</key><string>Lottie.framework</string>
                        <key>SupportedArchitectures</key><array><string>arm64</string><string>x86_64</string></array>
                        <key>SupportedPlatform</key><string>xros</string>
                        <key>SupportedPlatformVariant</key><string>simulator</string>
                    </dict>
                    <dict>
                        <key>BinaryPath</key><string>Lottie.framework/Lottie</string>
                        <key>LibraryIdentifier</key><string>tvos-arm64</string>
                        <key>LibraryPath</key><string>Lottie.framework</string>
                        <key>SupportedArchitectures</key><array><string>arm64</string></array>
                        <key>SupportedPlatform</key><string>tvos</string>
                    </dict>
                    <dict>
                        <key>BinaryPath</key><string>Lottie.framework/Lottie</string>
                        <key>LibraryIdentifier</key><string>ios-arm64_x86_64-simulator</string>
                        <key>LibraryPath</key><string>Lottie.framework</string>
                        <key>SupportedArchitectures</key><array><string>arm64</string><string>x86_64</string></array>
                        <key>SupportedPlatform</key><string>ios</string>
                        <key>SupportedPlatformVariant</key><string>simulator</string>
                    </dict>
                </array>
                <key>CFBundlePackageType</key><string>XFWK</string>
            </dict>
            </plist>
            """;

        private List<XCFrameworkSlice> ParsePlistString(string xml)
        {
            var tmpPath = Path.GetTempFileName();
            try
            {
                File.WriteAllText(tmpPath, xml);
                return XCFrameworkResolver.ParseInfoPlist(tmpPath);
            }
            finally
            {
                File.Delete(tmpPath);
            }
        }

        [Fact]
        public void ParsePlist_NukeStyle_TwoSlices()
        {
            var slices = ParsePlistString(NukeStylePlist);
            Assert.Equal(2, slices.Count);

            var sim = slices.First(s => s.SupportedPlatformVariant == "simulator");
            Assert.Equal("Nuke.framework/Nuke", sim.BinaryPath);
            Assert.Equal("ios-arm64_x86_64-simulator", sim.LibraryIdentifier);
            Assert.Equal("Nuke.framework", sim.LibraryPath);
            Assert.Equal(new[] { "arm64", "x86_64" }, sim.SupportedArchitectures);
            Assert.Equal("ios", sim.SupportedPlatform);

            var device = slices.First(s => s.SupportedPlatformVariant == null);
            Assert.Equal("ios-arm64", device.LibraryIdentifier);
            Assert.Single(device.SupportedArchitectures);
        }

        [Fact]
        public void ParsePlist_TestFrameworkStyle_SingleSlice()
        {
            var slices = ParsePlistString(TestFrameworkStylePlist);
            Assert.Single(slices);
            Assert.Equal("ios-arm64-simulator", slices[0].LibraryIdentifier);
            Assert.Equal("simulator", slices[0].SupportedPlatformVariant);
        }

        [Fact]
        public void ParsePlist_LottieStyle_AllEightSlicesParsed()
        {
            var slices = ParsePlistString(LottieStylePlist);
            Assert.Equal(8, slices.Count);
        }

        [Fact]
        public void ParsePlist_DeviceSlice_NullVariant()
        {
            var slices = ParsePlistString(NukeStylePlist);
            var device = slices.First(s => s.LibraryIdentifier == "ios-arm64");
            Assert.Null(device.SupportedPlatformVariant);
        }

        [Fact]
        public void ParsePlist_SimulatorSlice_HasSimulatorVariant()
        {
            var slices = ParsePlistString(NukeStylePlist);
            var sim = slices.First(s => s.LibraryIdentifier == "ios-arm64_x86_64-simulator");
            Assert.Equal("simulator", sim.SupportedPlatformVariant);
        }

        [Fact]
        public void ParsePlist_MaccatalystSlice_HasMaccatalystVariant()
        {
            var slices = ParsePlistString(LottieStylePlist);
            var catalyst = slices.First(s => s.LibraryIdentifier == "ios-arm64_x86_64-maccatalyst");
            Assert.Equal("maccatalyst", catalyst.SupportedPlatformVariant);
        }

        [Fact]
        public void ParsePlist_MissingAvailableLibraries_Throws()
        {
            var xml = """
                <?xml version="1.0" encoding="UTF-8"?>
                <plist version="1.0">
                <dict>
                    <key>CFBundlePackageType</key><string>XFWK</string>
                </dict>
                </plist>
                """;
            Assert.Throws<InvalidOperationException>(() => ParsePlistString(xml));
        }

        [Fact]
        public void ParsePlist_EmptyLibrariesArray_Throws()
        {
            var xml = """
                <?xml version="1.0" encoding="UTF-8"?>
                <plist version="1.0">
                <dict>
                    <key>AvailableLibraries</key>
                    <array/>
                </dict>
                </plist>
                """;
            Assert.Throws<InvalidOperationException>(() => ParsePlistString(xml));
        }
    }

    #endregion

    #region B. Slice Selection Tests

    public class XCFrameworkSliceSelectionTests
    {
        private static readonly ILogger Logger = NullLogger.Instance;

        private static XCFrameworkSlice MakeSlice(string platform, string? variant, params string[] archs)
            => new()
            {
                BinaryPath = "Lib.framework/Lib",
                LibraryIdentifier = $"{platform}-{string.Join("_", archs)}" + (variant != null ? $"-{variant}" : ""),
                LibraryPath = "Lib.framework",
                SupportedArchitectures = archs.ToList(),
                SupportedPlatform = platform,
                SupportedPlatformVariant = variant
            };

        [Fact]
        public void SelectSlice_PreferSimulator_BothAvailable_SelectsSimulator()
        {
            var slices = new List<XCFrameworkSlice>
            {
                MakeSlice("ios", null, "arm64"),
                MakeSlice("ios", "simulator", "arm64", "x86_64")
            };
            var result = XCFrameworkResolver.SelectSlice(slices, XCFrameworkPlatformTarget.Simulator, Logger);
            Assert.Equal("simulator", result.SupportedPlatformVariant);
        }

        [Fact]
        public void SelectSlice_PreferDevice_BothAvailable_SelectsDevice()
        {
            var slices = new List<XCFrameworkSlice>
            {
                MakeSlice("ios", null, "arm64"),
                MakeSlice("ios", "simulator", "arm64", "x86_64")
            };
            var result = XCFrameworkResolver.SelectSlice(slices, XCFrameworkPlatformTarget.Device, Logger);
            Assert.Null(result.SupportedPlatformVariant);
        }

        [Fact]
        public void SelectSlice_OnlySimulator_DeviceRequested_FallsBackWithWarning()
        {
            var slices = new List<XCFrameworkSlice>
            {
                MakeSlice("ios", "simulator", "arm64")
            };
            // Should not throw — falls back
            var result = XCFrameworkResolver.SelectSlice(slices, XCFrameworkPlatformTarget.Device, Logger);
            Assert.Equal("simulator", result.SupportedPlatformVariant);
        }

        [Fact]
        public void SelectSlice_OnlyDevice_SimulatorRequested_FallsBackWithWarning()
        {
            var slices = new List<XCFrameworkSlice>
            {
                MakeSlice("ios", null, "arm64")
            };
            var result = XCFrameworkResolver.SelectSlice(slices, XCFrameworkPlatformTarget.Simulator, Logger);
            Assert.Null(result.SupportedPlatformVariant);
        }

        [Fact]
        public void SelectSlice_MultiPlatform_OnlyiOSConsidered()
        {
            var slices = new List<XCFrameworkSlice>
            {
                MakeSlice("ios", "simulator", "arm64"),
                MakeSlice("tvos", "simulator", "arm64"),
                MakeSlice("macos", null, "arm64"),
                MakeSlice("ios", "maccatalyst", "arm64")  // maccatalyst excluded
            };
            var result = XCFrameworkResolver.SelectSlice(slices, XCFrameworkPlatformTarget.Simulator, Logger);
            Assert.Equal("ios", result.SupportedPlatform);
            Assert.Equal("simulator", result.SupportedPlatformVariant);
        }

        [Fact]
        public void SelectSlice_NoiOSSlices_ThrowsWithPlatformNames()
        {
            var slices = new List<XCFrameworkSlice>
            {
                MakeSlice("tvos", "simulator", "arm64"),
                MakeSlice("macos", null, "arm64")
            };
            var ex = Assert.Throws<InvalidOperationException>(() =>
                XCFrameworkResolver.SelectSlice(slices, XCFrameworkPlatformTarget.Simulator, Logger));
            Assert.Contains("No iOS platform slices found", ex.Message);
            Assert.Contains("tvos", ex.Message);
            Assert.Contains("macos", ex.Message);
        }
    }

    #endregion

    #region C. Module Discovery Tests

    public class XCFrameworkModuleDiscoveryTests
    {
        [Fact]
        public void Resolve_SingleSwiftModule_DiscoveredCorrectly()
        {
            using var fixture = new XCFrameworkFixture();
            fixture.WriteInfoPlist(MakeSimplePlist("TestLib"));
            var sliceDir = fixture.CreateSlice("ios-arm64-simulator", "TestLib.framework", "TestLib.framework/TestLib");
            var moduleDir = fixture.CreateSwiftModule(sliceDir, "TestLib.framework", "TestLib");
            fixture.CreateAbiJson(moduleDir, "arm64-apple-ios-simulator");
            fixture.CreateTbd(moduleDir, "TestLib");

            var runner = new MockCommandRunner();
            runner.SetResponse("file", 0, "dynamically linked shared library");

            var result = XCFrameworkResolver.Resolve(
                fixture.RootPath, fixture.OutputPath,
                XCFrameworkPlatformTarget.Simulator, NullLogger.Instance, runner);

            Assert.Equal("TestLib", result.ModuleName);
        }

        [Fact]
        public void Resolve_NoSwiftModule_ThrowsObjCError()
        {
            using var fixture = new XCFrameworkFixture();
            fixture.WriteInfoPlist(MakeSimplePlist("ObjCLib"));
            var sliceDir = fixture.CreateSlice("ios-arm64-simulator", "ObjCLib.framework", "ObjCLib.framework/ObjCLib");
            // Create Modules dir but no .swiftmodule inside
            Directory.CreateDirectory(Path.Combine(sliceDir, "ObjCLib.framework", "Modules"));

            var runner = new MockCommandRunner();
            runner.SetResponse("file", 0, "dynamically linked shared library");

            var ex = Assert.Throws<InvalidOperationException>(() =>
                XCFrameworkResolver.Resolve(
                    fixture.RootPath, fixture.OutputPath,
                    XCFrameworkPlatformTarget.Simulator, NullLogger.Instance, runner));
            Assert.Contains("No Swift module found", ex.Message);
        }

        [Fact]
        public void Resolve_MultipleSwiftModules_ThrowsMultiModuleError()
        {
            using var fixture = new XCFrameworkFixture();
            fixture.WriteInfoPlist(MakeSimplePlist("Multi"));
            var sliceDir = fixture.CreateSlice("ios-arm64-simulator", "Multi.framework", "Multi.framework/Multi");
            fixture.CreateSwiftModule(sliceDir, "Multi.framework", "ModuleA");
            fixture.CreateSwiftModule(sliceDir, "Multi.framework", "ModuleB");

            var runner = new MockCommandRunner();
            runner.SetResponse("file", 0, "dynamically linked shared library");

            var ex = Assert.Throws<InvalidOperationException>(() =>
                XCFrameworkResolver.Resolve(
                    fixture.RootPath, fixture.OutputPath,
                    XCFrameworkPlatformTarget.Simulator, NullLogger.Instance, runner));
            Assert.Contains("Multiple Swift modules found", ex.Message);
            Assert.Contains("ModuleA", ex.Message);
            Assert.Contains("ModuleB", ex.Message);
        }

        internal static string MakeSimplePlist(string name)
        {
            return $$"""
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
        }
    }

    #endregion

    #region D. ABI JSON Discovery Tests

    public class XCFrameworkAbiJsonTests
    {
        [Fact]
        public void Resolve_AbiJsonExists_Found()
        {
            using var fixture = new XCFrameworkFixture();
            fixture.WriteInfoPlist(XCFrameworkModuleDiscoveryTests.MakeSimplePlist("Lib"));
            var sliceDir = fixture.CreateSlice("ios-arm64-simulator", "Lib.framework", "Lib.framework/Lib");
            var moduleDir = fixture.CreateSwiftModule(sliceDir, "Lib.framework", "Lib");
            fixture.CreateAbiJson(moduleDir, "arm64-apple-ios-simulator");
            fixture.CreateTbd(moduleDir, "Lib");

            var runner = new MockCommandRunner();
            runner.SetResponse("file", 0, "dynamically linked shared library");

            var result = XCFrameworkResolver.Resolve(
                fixture.RootPath, fixture.OutputPath,
                XCFrameworkPlatformTarget.Simulator, NullLogger.Instance, runner);

            Assert.EndsWith("arm64-apple-ios-simulator.abi.json", result.AbiJsonPath);
            Assert.True(File.Exists(result.AbiJsonPath));
        }

        [Fact]
        public void Resolve_X86OnlySimSlice_SelectsX86AbiJson()
        {
            using var fixture = new XCFrameworkFixture();
            // Plist with only x86_64 architecture
            var plist = """
                <?xml version="1.0" encoding="UTF-8"?>
                <plist version="1.0">
                <dict>
                    <key>AvailableLibraries</key>
                    <array>
                        <dict>
                            <key>BinaryPath</key><string>Lib.framework/Lib</string>
                            <key>LibraryIdentifier</key><string>ios-x86_64-simulator</string>
                            <key>LibraryPath</key><string>Lib.framework</string>
                            <key>SupportedArchitectures</key><array><string>x86_64</string></array>
                            <key>SupportedPlatform</key><string>ios</string>
                            <key>SupportedPlatformVariant</key><string>simulator</string>
                        </dict>
                    </array>
                </dict>
                </plist>
                """;
            fixture.WriteInfoPlist(plist);
            var sliceDir = fixture.CreateSlice("ios-x86_64-simulator", "Lib.framework", "Lib.framework/Lib");
            var moduleDir = fixture.CreateSwiftModule(sliceDir, "Lib.framework", "Lib");
            fixture.CreateAbiJson(moduleDir, "x86_64-apple-ios-simulator");
            fixture.CreateSwiftInterface(moduleDir, "x86_64-apple-ios-simulator");
            fixture.CreateTbd(moduleDir, "Lib");

            var runner = new MockCommandRunner();
            runner.SetResponse("file", 0, "dynamically linked shared library");

            var result = XCFrameworkResolver.Resolve(
                fixture.RootPath, fixture.OutputPath,
                XCFrameworkPlatformTarget.Simulator, NullLogger.Instance, runner);

            Assert.Contains("x86_64", result.AbiJsonPath);
            Assert.NotNull(result.SwiftInterfacePath);
            Assert.Contains("x86_64", result.SwiftInterfacePath);
        }

        [Fact]
        public void Resolve_NoAbiJson_SwiftInterfaceExists_InvokesSwiftFrontend()
        {
            using var fixture = new XCFrameworkFixture();
            fixture.WriteInfoPlist(XCFrameworkModuleDiscoveryTests.MakeSimplePlist("Lib"));
            var sliceDir = fixture.CreateSlice("ios-arm64-simulator", "Lib.framework", "Lib.framework/Lib");
            var moduleDir = fixture.CreateSwiftModule(sliceDir, "Lib.framework", "Lib");
            // Only swiftinterface, no abi.json
            fixture.CreateSwiftInterface(moduleDir, "arm64-apple-ios-simulator");
            fixture.CreateTbd(moduleDir, "Lib");

            var runner = new MockCommandRunner();
            runner.SetResponse("file", 0, "dynamically linked shared library");
            runner.SetResponse("--show-sdk-path", 0, "/Applications/Xcode.app/Contents/Developer/Platforms/iPhoneSimulator.platform/Developer/SDKs/iPhoneSimulator.sdk");
            // swift-frontend will "create" the ABI JSON
            runner.SetResponse("swift-frontend", 0, "");

            // Pre-create the abi.json that swift-frontend would generate
            var expectedAbiPath = Path.Combine(fixture.OutputPath, "Lib.abi.json");
            File.WriteAllText(expectedAbiPath, "{}");

            var result = XCFrameworkResolver.Resolve(
                fixture.RootPath, fixture.OutputPath,
                XCFrameworkPlatformTarget.Simulator, NullLogger.Instance, runner);

            Assert.Equal(expectedAbiPath, result.AbiJsonPath);
            // Verify swift-frontend was invoked
            Assert.Contains(runner.Invocations, i => i.Arguments.Contains("swift-frontend"));
            Assert.Contains(runner.Invocations, i => i.Arguments.Contains("arm64-apple-ios-simulator"));
        }

        [Fact]
        public void Resolve_NoAbiJson_NoSwiftInterface_Throws()
        {
            using var fixture = new XCFrameworkFixture();
            fixture.WriteInfoPlist(XCFrameworkModuleDiscoveryTests.MakeSimplePlist("Lib"));
            var sliceDir = fixture.CreateSlice("ios-arm64-simulator", "Lib.framework", "Lib.framework/Lib");
            var moduleDir = fixture.CreateSwiftModule(sliceDir, "Lib.framework", "Lib");
            // No abi.json, no swiftinterface — only private.swiftinterface
            fixture.CreatePrivateSwiftInterface(moduleDir, "arm64-apple-ios-simulator");

            var runner = new MockCommandRunner();
            runner.SetResponse("file", 0, "dynamically linked shared library");

            var ex = Assert.Throws<InvalidOperationException>(() =>
                XCFrameworkResolver.Resolve(
                    fixture.RootPath, fixture.OutputPath,
                    XCFrameworkPlatformTarget.Simulator, NullLogger.Instance, runner));
            Assert.Contains("No ABI JSON or Swift interface found", ex.Message);
        }
    }

    #endregion

    #region E. TBD Discovery Tests

    public class XCFrameworkTbdTests
    {
        [Fact]
        public void Resolve_TbdExistsInSwiftModule_UsedDirectly()
        {
            using var fixture = new XCFrameworkFixture();
            fixture.WriteInfoPlist(XCFrameworkModuleDiscoveryTests.MakeSimplePlist("Lib"));
            var sliceDir = fixture.CreateSlice("ios-arm64-simulator", "Lib.framework", "Lib.framework/Lib");
            var moduleDir = fixture.CreateSwiftModule(sliceDir, "Lib.framework", "Lib");
            fixture.CreateAbiJson(moduleDir, "arm64-apple-ios-simulator");
            fixture.CreateTbd(moduleDir, "Lib");

            var runner = new MockCommandRunner();
            runner.SetResponse("file", 0, "dynamically linked shared library");

            var result = XCFrameworkResolver.Resolve(
                fixture.RootPath, fixture.OutputPath,
                XCFrameworkPlatformTarget.Simulator, NullLogger.Instance, runner);

            Assert.EndsWith(".tbd", result.TbdPath);
            Assert.Contains("Lib.tbd", result.TbdPath);
            // tapi should NOT have been invoked
            Assert.DoesNotContain(runner.Invocations, i => i.Arguments.Contains("tapi"));
        }

        [Fact]
        public void Resolve_NoTbd_InvokesTapiStubify()
        {
            using var fixture = new XCFrameworkFixture();
            fixture.WriteInfoPlist(XCFrameworkModuleDiscoveryTests.MakeSimplePlist("Lib"));
            var sliceDir = fixture.CreateSlice("ios-arm64-simulator", "Lib.framework", "Lib.framework/Lib");
            var moduleDir = fixture.CreateSwiftModule(sliceDir, "Lib.framework", "Lib");
            fixture.CreateAbiJson(moduleDir, "arm64-apple-ios-simulator");
            // No TBD file

            var runner = new MockCommandRunner();
            runner.SetResponse("file", 0, "dynamically linked shared library");
            runner.SetResponse("tapi", 0, "");

            // Pre-create what tapi would generate
            var expectedTbdPath = Path.Combine(fixture.OutputPath, "Lib.tbd");
            File.WriteAllText(expectedTbdPath, "--- !tapi-tbd");

            var result = XCFrameworkResolver.Resolve(
                fixture.RootPath, fixture.OutputPath,
                XCFrameworkPlatformTarget.Simulator, NullLogger.Instance, runner);

            Assert.Equal(expectedTbdPath, result.TbdPath);
            Assert.Contains(runner.Invocations, i => i.Arguments.Contains("tapi"));
        }
    }

    #endregion

    #region F. Static XCFramework Detection Tests

    public class XCFrameworkStaticDetectionTests
    {
        [Fact]
        public void Resolve_LibraryPathWithoutFramework_DetectsStatic()
        {
            using var fixture = new XCFrameworkFixture();
            // Static library plist: LibraryPath is just the .a file, no .framework
            var plist = """
                <?xml version="1.0" encoding="UTF-8"?>
                <plist version="1.0">
                <dict>
                    <key>AvailableLibraries</key>
                    <array>
                        <dict>
                            <key>BinaryPath</key><string>libStatic.a</string>
                            <key>LibraryIdentifier</key><string>ios-arm64-simulator</string>
                            <key>LibraryPath</key><string>libStatic.a</string>
                            <key>SupportedArchitectures</key><array><string>arm64</string></array>
                            <key>SupportedPlatform</key><string>ios</string>
                            <key>SupportedPlatformVariant</key><string>simulator</string>
                        </dict>
                    </array>
                </dict>
                </plist>
                """;
            fixture.WriteInfoPlist(plist);
            // Create the binary stub
            var sliceDir = Path.Combine(fixture.RootPath, "ios-arm64-simulator");
            Directory.CreateDirectory(sliceDir);
            File.WriteAllText(Path.Combine(sliceDir, "libStatic.a"), "");

            var ex = Assert.Throws<InvalidOperationException>(() =>
                XCFrameworkResolver.Resolve(
                    fixture.RootPath, fixture.OutputPath,
                    XCFrameworkPlatformTarget.Simulator, NullLogger.Instance));
            Assert.Contains("Static xcframeworks", ex.Message);
        }

        [Fact]
        public void Resolve_FrameworkBundleButStaticBinary_DetectsStatic()
        {
            using var fixture = new XCFrameworkFixture();
            fixture.WriteInfoPlist(XCFrameworkModuleDiscoveryTests.MakeSimplePlist("StaticLib"));
            var sliceDir = fixture.CreateSlice("ios-arm64-simulator", "StaticLib.framework", "StaticLib.framework/StaticLib");
            // Create full module structure so the error comes from file command, not module discovery
            var moduleDir = fixture.CreateSwiftModule(sliceDir, "StaticLib.framework", "StaticLib");
            fixture.CreateAbiJson(moduleDir, "arm64-apple-ios-simulator");

            var runner = new MockCommandRunner();
            runner.SetResponse("file", 0, "current ar archive random library");

            var ex = Assert.Throws<InvalidOperationException>(() =>
                XCFrameworkResolver.Resolve(
                    fixture.RootPath, fixture.OutputPath,
                    XCFrameworkPlatformTarget.Simulator, NullLogger.Instance, runner));
            Assert.Contains("static library", ex.Message);
        }

        [Fact]
        public void Resolve_FileCommandFails_ThrowsActionableError()
        {
            using var fixture = new XCFrameworkFixture();
            fixture.WriteInfoPlist(XCFrameworkModuleDiscoveryTests.MakeSimplePlist("Lib"));
            var sliceDir = fixture.CreateSlice("ios-arm64-simulator", "Lib.framework", "Lib.framework/Lib");
            var moduleDir = fixture.CreateSwiftModule(sliceDir, "Lib.framework", "Lib");
            fixture.CreateAbiJson(moduleDir, "arm64-apple-ios-simulator");

            var runner = new MockCommandRunner();
            runner.SetResponse("file", 1, "", "file: command not found");

            var ex = Assert.Throws<InvalidOperationException>(() =>
                XCFrameworkResolver.Resolve(
                    fixture.RootPath, fixture.OutputPath,
                    XCFrameworkPlatformTarget.Simulator, NullLogger.Instance, runner));
            Assert.Contains("Failed to verify binary type", ex.Message);
            Assert.Contains("xcode-select", ex.Message);
        }
    }

    #endregion

    #region G. Validation / Error Tests

    public class XCFrameworkValidationTests
    {
        [Fact]
        public void Resolve_PathDoesNotExist_Throws()
        {
            var ex = Assert.Throws<DirectoryNotFoundException>(() =>
                XCFrameworkResolver.Resolve(
                    "/nonexistent/path.xcframework", "/tmp/out",
                    XCFrameworkPlatformTarget.Simulator, NullLogger.Instance));
            Assert.Contains("xcframework not found", ex.Message);
        }

        [Fact]
        public void Resolve_NotXcframeworkExtension_Throws()
        {
            var tmpDir = Path.Combine(Path.GetTempPath(), $"notxcfw_{Guid.NewGuid():N}");
            Directory.CreateDirectory(tmpDir);
            try
            {
                var ex = Assert.Throws<InvalidOperationException>(() =>
                    XCFrameworkResolver.Resolve(
                        tmpDir, "/tmp/out",
                        XCFrameworkPlatformTarget.Simulator, NullLogger.Instance));
                Assert.Contains("not an xcframework directory", ex.Message);
            }
            finally
            {
                Directory.Delete(tmpDir, true);
            }
        }

        [Fact]
        public void Resolve_MissingInfoPlist_Throws()
        {
            var tmpDir = Path.Combine(Path.GetTempPath(), $"empty_{Guid.NewGuid():N}.xcframework");
            Directory.CreateDirectory(tmpDir);
            try
            {
                var ex = Assert.Throws<InvalidOperationException>(() =>
                    XCFrameworkResolver.Resolve(
                        tmpDir, "/tmp/out",
                        XCFrameworkPlatformTarget.Simulator, NullLogger.Instance));
                Assert.Contains("Info.plist not found", ex.Message);
            }
            finally
            {
                Directory.Delete(tmpDir, true);
            }
        }
    }

    #endregion

    #region H. SwiftInterface Discovery Tests

    public class XCFrameworkSwiftInterfaceTests
    {
        [Fact]
        public void FindSwiftInterface_ArchSpecific_ReturnsNonPrivate()
        {
            var tmpDir = Path.Combine(Path.GetTempPath(), $"si_test_{Guid.NewGuid():N}");
            Directory.CreateDirectory(tmpDir);
            try
            {
                File.WriteAllText(Path.Combine(tmpDir, "arm64-apple-ios-simulator.swiftinterface"), "public");
                File.WriteAllText(Path.Combine(tmpDir, "arm64-apple-ios-simulator.private.swiftinterface"), "private");

                var result = XCFrameworkResolver.FindSwiftInterface(tmpDir, "arm64");
                Assert.NotNull(result);
                Assert.EndsWith("arm64-apple-ios-simulator.swiftinterface", result);
                Assert.DoesNotContain("private", Path.GetFileName(result));
            }
            finally
            {
                Directory.Delete(tmpDir, true);
            }
        }

        [Fact]
        public void FindSwiftInterface_NoMatchingArch_FallsBackToAny()
        {
            var tmpDir = Path.Combine(Path.GetTempPath(), $"si_test_{Guid.NewGuid():N}");
            Directory.CreateDirectory(tmpDir);
            try
            {
                // Only x86_64 interface available, looking for arm64
                File.WriteAllText(Path.Combine(tmpDir, "x86_64-apple-ios-simulator.swiftinterface"), "public");
                File.WriteAllText(Path.Combine(tmpDir, "x86_64-apple-ios-simulator.private.swiftinterface"), "private");

                var result = XCFrameworkResolver.FindSwiftInterface(tmpDir, "arm64");
                Assert.NotNull(result);
                Assert.Contains("x86_64", result);
            }
            finally
            {
                Directory.Delete(tmpDir, true);
            }
        }

        [Fact]
        public void FindSwiftInterface_OnlyPrivate_ReturnsNull()
        {
            var tmpDir = Path.Combine(Path.GetTempPath(), $"si_test_{Guid.NewGuid():N}");
            Directory.CreateDirectory(tmpDir);
            try
            {
                File.WriteAllText(Path.Combine(tmpDir, "arm64-apple-ios-simulator.private.swiftinterface"), "private");

                var result = XCFrameworkResolver.FindSwiftInterface(tmpDir, "arm64");
                Assert.Null(result);
            }
            finally
            {
                Directory.Delete(tmpDir, true);
            }
        }
    }

    #endregion
}
