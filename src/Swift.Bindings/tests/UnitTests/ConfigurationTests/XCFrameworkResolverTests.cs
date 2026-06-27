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

        /// <summary>
        /// When true, the runner faithfully simulates a SUCCESSFUL compile/link: a command that exits
        /// zero and carries a <c>-o &lt;path&gt;</c> target writes a minimal valid Mach-O image there,
        /// and an un-mocked <c>lipo -archs</c> probe reports a universal arch list. This satisfies the
        /// post-compile slice-binary validation (existence + Mach-O magic + expected arch) that a real
        /// swiftc would otherwise produce, so happy-path wrapper-compile tests can run without a
        /// toolchain. Off by default so failure/negative tests still observe a missing binary. An
        /// explicit <see cref="SetResponse"/> always wins over the synthesized lipo default.
        /// </summary>
        public bool SynthesizeMachOOutputs { get; set; }

        public void SetResponse(string matchKey, int exitCode, string stdOut, string stdErr = "")
        {
            _responses[matchKey] = (exitCode, stdOut, stdErr);
        }

        public (int ExitCode, string StdOut, string StdErr) Run(string command, string arguments, int timeoutMs = 30000)
        {
            Invocations.Add((command, arguments));

            // Match against both command name and arguments
            var fullKey = $"{command} {arguments}";
            (int ExitCode, string StdOut, string StdErr)? matched = null;
            foreach (var (key, response) in _responses)
            {
                if (fullKey.Contains(key))
                {
                    matched = response;
                    break;
                }
            }

            if (SynthesizeMachOOutputs)
            {
                // A successful compile/link writes its -o target; mirror that so validation sees a real
                // (magic-bearing, non-empty) binary instead of the missing file a bare mock leaves.
                if ((matched?.ExitCode ?? 0) == 0)
                    TrySynthesizeDashOOutput(arguments);

                // Default an un-mocked `lipo -archs <binary>` to a universal slice list so the arch
                // assertion passes on any host (an explicit SetResponse still takes precedence above).
                if (matched == null && command == "lipo" && arguments.Contains("-archs"))
                    return (0, "arm64 x86_64 arm64e", "");
            }

            return matched ?? (0, "", "");
        }

        /// <summary>
        /// Writes a minimal valid 64-bit Mach-O magic (MH_MAGIC_64, little-endian) to the <c>-o</c>
        /// target found in <paramref name="arguments"/>, creating its parent directory. Best-effort:
        /// a synthesis failure just surfaces as the same missing-binary error the test guards against.
        /// </summary>
        private static void TrySynthesizeDashOOutput(string arguments)
        {
            var path = ExtractDashOTarget(arguments);
            if (string.IsNullOrEmpty(path))
                return;
            try
            {
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);
                File.WriteAllBytes(path, new byte[] { 0xCF, 0xFA, 0xED, 0xFE, 0x00, 0x00, 0x00, 0x00 });
            }
            catch { /* best-effort: missing binary still fails the test as before */ }
        }

        /// <summary>
        /// Extracts the target of a standalone <c>-o</c> flag (quoted or bare) — the compile/link output
        /// binary — from a command-line argument string, or null if none is present.
        /// </summary>
        private static string? ExtractDashOTarget(string arguments)
        {
            var idx = arguments.IndexOf("-o ", StringComparison.Ordinal);
            while (idx >= 0)
            {
                if (idx == 0 || char.IsWhiteSpace(arguments[idx - 1]))
                {
                    var rest = arguments.Substring(idx + 3).TrimStart();
                    if (rest.StartsWith("\"", StringComparison.Ordinal))
                    {
                        var end = rest.IndexOf('"', 1);
                        if (end > 1)
                            return rest.Substring(1, end - 1);
                    }
                    else if (rest.Length > 0)
                    {
                        var end = rest.IndexOf(' ');
                        return end > 0 ? rest.Substring(0, end) : rest;
                    }
                }
                idx = arguments.IndexOf("-o ", idx + 1, StringComparison.Ordinal);
            }
            return null;
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

        private const string ImagePipelineStylePlist = """
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0">
            <dict>
                <key>AvailableLibraries</key>
                <array>
                    <dict>
                        <key>BinaryPath</key><string>ImagePipeline.framework/ImagePipeline</string>
                        <key>LibraryIdentifier</key><string>ios-arm64_x86_64-simulator</string>
                        <key>LibraryPath</key><string>ImagePipeline.framework</string>
                        <key>SupportedArchitectures</key><array><string>arm64</string><string>x86_64</string></array>
                        <key>SupportedPlatform</key><string>ios</string>
                        <key>SupportedPlatformVariant</key><string>simulator</string>
                    </dict>
                    <dict>
                        <key>BinaryPath</key><string>ImagePipeline.framework/ImagePipeline</string>
                        <key>LibraryIdentifier</key><string>ios-arm64</string>
                        <key>LibraryPath</key><string>ImagePipeline.framework</string>
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

        private const string VectorAnimationStylePlist = """
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0">
            <dict>
                <key>AvailableLibraries</key>
                <array>
                    <dict>
                        <key>BinaryPath</key><string>VectorAnimation.framework/VectorAnimation</string>
                        <key>LibraryIdentifier</key><string>ios-arm64</string>
                        <key>LibraryPath</key><string>VectorAnimation.framework</string>
                        <key>SupportedArchitectures</key><array><string>arm64</string></array>
                        <key>SupportedPlatform</key><string>ios</string>
                    </dict>
                    <dict>
                        <key>BinaryPath</key><string>VectorAnimation.framework/VectorAnimation</string>
                        <key>LibraryIdentifier</key><string>tvos-arm64_x86_64-simulator</string>
                        <key>LibraryPath</key><string>VectorAnimation.framework</string>
                        <key>SupportedArchitectures</key><array><string>arm64</string><string>x86_64</string></array>
                        <key>SupportedPlatform</key><string>tvos</string>
                        <key>SupportedPlatformVariant</key><string>simulator</string>
                    </dict>
                    <dict>
                        <key>BinaryPath</key><string>VectorAnimation.framework/Versions/A/VectorAnimation</string>
                        <key>LibraryIdentifier</key><string>ios-arm64_x86_64-maccatalyst</string>
                        <key>LibraryPath</key><string>VectorAnimation.framework</string>
                        <key>SupportedArchitectures</key><array><string>arm64</string><string>x86_64</string></array>
                        <key>SupportedPlatform</key><string>ios</string>
                        <key>SupportedPlatformVariant</key><string>maccatalyst</string>
                    </dict>
                    <dict>
                        <key>BinaryPath</key><string>VectorAnimation.framework/Versions/A/VectorAnimation</string>
                        <key>LibraryIdentifier</key><string>macos-arm64_x86_64</string>
                        <key>LibraryPath</key><string>VectorAnimation.framework</string>
                        <key>SupportedArchitectures</key><array><string>arm64</string><string>x86_64</string></array>
                        <key>SupportedPlatform</key><string>macos</string>
                    </dict>
                    <dict>
                        <key>BinaryPath</key><string>VectorAnimation.framework/VectorAnimation</string>
                        <key>LibraryIdentifier</key><string>xros-arm64</string>
                        <key>LibraryPath</key><string>VectorAnimation.framework</string>
                        <key>SupportedArchitectures</key><array><string>arm64</string></array>
                        <key>SupportedPlatform</key><string>xros</string>
                    </dict>
                    <dict>
                        <key>BinaryPath</key><string>VectorAnimation.framework/VectorAnimation</string>
                        <key>LibraryIdentifier</key><string>xros-arm64_x86_64-simulator</string>
                        <key>LibraryPath</key><string>VectorAnimation.framework</string>
                        <key>SupportedArchitectures</key><array><string>arm64</string><string>x86_64</string></array>
                        <key>SupportedPlatform</key><string>xros</string>
                        <key>SupportedPlatformVariant</key><string>simulator</string>
                    </dict>
                    <dict>
                        <key>BinaryPath</key><string>VectorAnimation.framework/VectorAnimation</string>
                        <key>LibraryIdentifier</key><string>tvos-arm64</string>
                        <key>LibraryPath</key><string>VectorAnimation.framework</string>
                        <key>SupportedArchitectures</key><array><string>arm64</string></array>
                        <key>SupportedPlatform</key><string>tvos</string>
                    </dict>
                    <dict>
                        <key>BinaryPath</key><string>VectorAnimation.framework/VectorAnimation</string>
                        <key>LibraryIdentifier</key><string>ios-arm64_x86_64-simulator</string>
                        <key>LibraryPath</key><string>VectorAnimation.framework</string>
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
        public void ParsePlist_ImagePipelineStyle_TwoSlices()
        {
            var slices = ParsePlistString(ImagePipelineStylePlist);
            Assert.Equal(2, slices.Count);

            var sim = slices.First(s => s.SupportedPlatformVariant == "simulator");
            Assert.Equal("ImagePipeline.framework/ImagePipeline", sim.BinaryPath);
            Assert.Equal("ios-arm64_x86_64-simulator", sim.LibraryIdentifier);
            Assert.Equal("ImagePipeline.framework", sim.LibraryPath);
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
        public void ParsePlist_VectorAnimationStyle_AllEightSlicesParsed()
        {
            var slices = ParsePlistString(VectorAnimationStylePlist);
            Assert.Equal(8, slices.Count);
        }

        [Fact]
        public void ParsePlist_DeviceSlice_NullVariant()
        {
            var slices = ParsePlistString(ImagePipelineStylePlist);
            var device = slices.First(s => s.LibraryIdentifier == "ios-arm64");
            Assert.Null(device.SupportedPlatformVariant);
        }

        [Fact]
        public void ParsePlist_SimulatorSlice_HasSimulatorVariant()
        {
            var slices = ParsePlistString(ImagePipelineStylePlist);
            var sim = slices.First(s => s.LibraryIdentifier == "ios-arm64_x86_64-simulator");
            Assert.Equal("simulator", sim.SupportedPlatformVariant);
        }

        [Fact]
        public void ParsePlist_MaccatalystSlice_HasMaccatalystVariant()
        {
            var slices = ParsePlistString(VectorAnimationStylePlist);
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

        [Fact]
        public void SelectSlice_DefaultRecordResolution_RecordsNothing()
        {
            // Finding 50 (Codex High): only the PRIMARY generation target records to the ambient
            // input-resolution report. Secondary callers (ObjC-detection, search-paths-only, sibling
            // search) pass recordResolution at its default of false and must record NOTHING — even on
            // a fallback — or they trip false-positive SWIFTBIND027 and pollute the manifest snapshot.
            InputResolutionReport.Reset();
            var slices = new List<XCFrameworkSlice> { MakeSlice("ios", "simulator", "arm64") };

            // Device requested but only a simulator slice exists -> this is a fallback.
            var result = XCFrameworkResolver.SelectSlice(slices, XCFrameworkPlatformTarget.Device, Logger);

            Assert.Equal("simulator", result.SupportedPlatformVariant);
            Assert.Empty(InputResolutionReport.Decisions);
            Assert.False(InputResolutionReport.HasDegradations);
        }

        [Fact]
        public void SelectSlice_RecordResolutionTrue_PreferredSlice_RecordsInfoNotDegradation()
        {
            // The primary target found the requested slice as-is: an Info SliceSelection decision,
            // never a degradation.
            InputResolutionReport.Reset();
            var slices = new List<XCFrameworkSlice>
            {
                MakeSlice("ios", null, "arm64"),
                MakeSlice("ios", "simulator", "arm64", "x86_64")
            };

            var result = XCFrameworkResolver.SelectSlice(
                slices, XCFrameworkPlatformTarget.Simulator, Logger, platformInfo: null, recordResolution: true);

            Assert.Equal("simulator", result.SupportedPlatformVariant);
            var decision = Assert.Single(InputResolutionReport.Decisions);
            Assert.Equal(InputResolutionCategory.SliceSelection, decision.Category);
            Assert.Equal(InputResolutionSeverity.Info, decision.Severity);
            Assert.False(InputResolutionReport.HasDegradations);
        }

        [Fact]
        public void SelectSlice_RecordResolutionTrue_Fallback_RecordsDegradation()
        {
            // The primary target's requested slice was absent and a different kind was substituted:
            // a SliceSelection degradation that --strict-inputs escalates (SWIFTBIND027).
            InputResolutionReport.Reset();
            var slices = new List<XCFrameworkSlice> { MakeSlice("ios", "simulator", "arm64") };

            var result = XCFrameworkResolver.SelectSlice(
                slices, XCFrameworkPlatformTarget.Device, Logger, platformInfo: null, recordResolution: true);

            Assert.Equal("simulator", result.SupportedPlatformVariant);
            var decision = Assert.Single(InputResolutionReport.Decisions);
            Assert.Equal(InputResolutionCategory.SliceSelection, decision.Category);
            Assert.Equal(InputResolutionSeverity.Degradation, decision.Severity);
            Assert.True(InputResolutionReport.HasDegradations);

            InputResolutionReport.Reset();
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

            var ex = Assert.Throws<SwiftModuleNotFoundException>(() =>
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
        /// <summary>
        /// Regression test: wrapper xcframeworks generated before the BinaryPath fix
        /// had no BinaryPath in Info.plist. The resolver must infer it from LibraryPath.
        /// </summary>
        [Fact]
        public void Resolve_MissingBinaryPath_InferredFromLibraryPath()
        {
            using var fixture = new XCFrameworkFixture();

            // Plist WITHOUT BinaryPath — the exact bug pattern from wrapper xcframeworks
            var plistWithoutBinaryPath = """
                <?xml version="1.0" encoding="UTF-8"?>
                <plist version="1.0">
                <dict>
                    <key>AvailableLibraries</key>
                    <array>
                        <dict>
                            <key>LibraryIdentifier</key><string>ios-arm64-simulator</string>
                            <key>LibraryPath</key><string>Lib.framework</string>
                            <key>SupportedArchitectures</key><array><string>arm64</string></array>
                            <key>SupportedPlatform</key><string>ios</string>
                            <key>SupportedPlatformVariant</key><string>simulator</string>
                        </dict>
                    </array>
                </dict>
                </plist>
                """;
            fixture.WriteInfoPlist(plistWithoutBinaryPath);
            // Create the dylib at the INFERRED path (Lib.framework/Lib)
            var sliceDir = fixture.CreateSlice("ios-arm64-simulator", "Lib.framework", "Lib.framework/Lib");
            var moduleDir = fixture.CreateSwiftModule(sliceDir, "Lib.framework", "Lib");
            fixture.CreateAbiJson(moduleDir, "arm64-apple-ios-simulator");
            fixture.CreateTbd(moduleDir, "Lib");

            var runner = new MockCommandRunner();
            runner.SetResponse("file", 0, "dynamically linked shared library");

            var result = XCFrameworkResolver.Resolve(
                fixture.RootPath, fixture.OutputPath,
                XCFrameworkPlatformTarget.Simulator, NullLogger.Instance, runner);

            Assert.Equal("Lib", result.ModuleName);
            Assert.Contains("Lib.framework/Lib", result.DylibPath);
        }

        [Fact]
        public void ParseInfoPlist_MissingBinaryPath_DefaultsToEmpty()
        {
            using var fixture = new XCFrameworkFixture();
            var plist = """
                <?xml version="1.0" encoding="UTF-8"?>
                <plist version="1.0">
                <dict>
                    <key>AvailableLibraries</key>
                    <array>
                        <dict>
                            <key>LibraryIdentifier</key><string>ios-arm64-simulator</string>
                            <key>LibraryPath</key><string>Lib.framework</string>
                            <key>SupportedArchitectures</key><array><string>arm64</string></array>
                            <key>SupportedPlatform</key><string>ios</string>
                        </dict>
                    </array>
                </dict>
                </plist>
                """;
            fixture.WriteInfoPlist(plist);

            var slices = XCFrameworkResolver.ParseInfoPlist(
                Path.Combine(fixture.RootPath, "Info.plist"));

            Assert.Single(slices);
            Assert.Equal("", slices[0].BinaryPath);
            Assert.Equal("Lib.framework", slices[0].LibraryPath);
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
        public void Resolve_MultipleTbds_PicksOrdinalFirst_Deterministically_AndRecordsDegradation()
        {
            // Finding 50: the previous unsorted Directory.GetFiles()[0] could pick a different
            // .tbd across runs when more than one was present. Resolution now sorts ordinally and
            // surfaces the ambiguity as an input-resolution degradation.
            using var fixture = new XCFrameworkFixture();
            fixture.WriteInfoPlist(XCFrameworkModuleDiscoveryTests.MakeSimplePlist("Lib"));
            var sliceDir = fixture.CreateSlice("ios-arm64-simulator", "Lib.framework", "Lib.framework/Lib");
            var moduleDir = fixture.CreateSwiftModule(sliceDir, "Lib.framework", "Lib");
            fixture.CreateAbiJson(moduleDir, "arm64-apple-ios-simulator");
            fixture.CreateSwiftInterface(moduleDir, "arm64-apple-ios-simulator");
            // Two TBDs, planted out of order. Ordinal-first is "Alpha.tbd".
            fixture.CreateTbd(moduleDir, "Zulu");
            fixture.CreateTbd(moduleDir, "Alpha");

            var runner = new MockCommandRunner();
            runner.SetResponse("file", 0, "dynamically linked shared library");

            InputResolutionReport.Reset();
            var result = XCFrameworkResolver.Resolve(
                fixture.RootPath, fixture.OutputPath,
                XCFrameworkPlatformTarget.Simulator, NullLogger.Instance, runner);

            Assert.EndsWith("Alpha.tbd", result.TbdPath);
            Assert.DoesNotContain(runner.Invocations, i => i.Arguments.Contains("tapi"));

            var tbdDecisions = InputResolutionReport.Decisions
                .Where(d => d.Category == InputResolutionCategory.Tbd)
                .ToList();
            var degradation = Assert.Single(
                tbdDecisions, d => d.Severity == InputResolutionSeverity.Degradation);
            Assert.Contains("2 TBD files", degradation.Detail);
            Assert.Contains("Alpha.tbd", degradation.Detail);
        }

        [Fact]
        public void Resolve_SingleTbd_RecordsInfo_NotDegradation()
        {
            // Finding 50: the unambiguous single-TBD case is an Info decision, never a degradation,
            // so it does not trip the --strict-inputs fail-closed gate.
            using var fixture = new XCFrameworkFixture();
            fixture.WriteInfoPlist(XCFrameworkModuleDiscoveryTests.MakeSimplePlist("Lib"));
            var sliceDir = fixture.CreateSlice("ios-arm64-simulator", "Lib.framework", "Lib.framework/Lib");
            var moduleDir = fixture.CreateSwiftModule(sliceDir, "Lib.framework", "Lib");
            fixture.CreateAbiJson(moduleDir, "arm64-apple-ios-simulator");
            fixture.CreateSwiftInterface(moduleDir, "arm64-apple-ios-simulator");
            fixture.CreateTbd(moduleDir, "Lib");

            var runner = new MockCommandRunner();
            runner.SetResponse("file", 0, "dynamically linked shared library");

            InputResolutionReport.Reset();
            XCFrameworkResolver.Resolve(
                fixture.RootPath, fixture.OutputPath,
                XCFrameworkPlatformTarget.Simulator, NullLogger.Instance, runner);

            var tbdDecisions = InputResolutionReport.Decisions
                .Where(d => d.Category == InputResolutionCategory.Tbd)
                .ToList();
            Assert.All(tbdDecisions, d => Assert.Equal(InputResolutionSeverity.Info, d.Severity));
            Assert.Contains(tbdDecisions, d => d.Detail.Contains("Lib.tbd"));
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

            var ex = Assert.Throws<StaticLibraryException>(() =>
                XCFrameworkResolver.Resolve(
                    fixture.RootPath, fixture.OutputPath,
                    XCFrameworkPlatformTarget.Simulator, NullLogger.Instance));
            Assert.Contains("Static xcframeworks", ex.Message);
        }

        [Theory]
        [InlineData("current ar archive random library")]                                                               // .a archive
        [InlineData("Mach-O 64-bit object arm64")]                                                                      // Static .framework (SwiftProtobuf pattern)
        [InlineData("Mach-O universal binary with 2 architectures: [x86_64:Mach-O 64-bit object x86_64] [arm64]")]      // Universal static
        public void Resolve_StaticBinary_NoSwiftEvidence_DetectsStatic(string fileOutput)
        {
            // True ObjC static framework: static binary, NO .swiftmodule. Resolver
            // throws StaticLibraryException so the caller can route to ObjC fallback.
            using var fixture = new XCFrameworkFixture();
            fixture.WriteInfoPlist(XCFrameworkModuleDiscoveryTests.MakeSimplePlist("StaticLib"));
            fixture.CreateSlice("ios-arm64-simulator", "StaticLib.framework", "StaticLib.framework/StaticLib");

            var runner = new MockCommandRunner();
            runner.SetResponse("file", 0, fileOutput);

            var ex = Assert.Throws<StaticLibraryException>(() =>
                XCFrameworkResolver.Resolve(
                    fixture.RootPath, fixture.OutputPath,
                    XCFrameworkPlatformTarget.Simulator, NullLogger.Instance, runner));
            Assert.Contains("static library", ex.Message);
        }

        [Fact]
        public void StaticLibraryException_CanBeCaughtSeparately()
        {
            // Verify StaticLibraryException is distinct from other InvalidOperationExceptions
            // so Program.cs can fall back to ObjC resolution.
            using var fixture = new XCFrameworkFixture();
            fixture.WriteInfoPlist(XCFrameworkModuleDiscoveryTests.MakeSimplePlist("StaticLib"));
            fixture.CreateSlice("ios-arm64-simulator", "StaticLib.framework", "StaticLib.framework/StaticLib");

            var runner = new MockCommandRunner();
            runner.SetResponse("file", 0, "current ar archive random library");

            bool caughtStatic = false;
            try
            {
                XCFrameworkResolver.Resolve(
                    fixture.RootPath, fixture.OutputPath,
                    XCFrameworkPlatformTarget.Simulator, NullLogger.Instance, runner);
            }
            catch (StaticLibraryException)
            {
                caughtStatic = true;
            }
            catch (Exception)
            {
                // Should not reach here
            }
            Assert.True(caughtStatic, "StaticLibraryException should be catchable distinctly from InvalidOperationException");
        }

        [Theory]
        [InlineData("current ar archive random library")]                                                               // .a archive (static-archive shape)
        [InlineData("Mach-O 64-bit object arm64")]                                                                      // Static Mach-O object
        [InlineData("Mach-O universal binary with 2 architectures: [x86_64:Mach-O 64-bit object x86_64] [arm64]")]      // Universal static
        public void Resolve_StaticFrameworkBinary_WithSwiftInterface_TakesSwiftPath(string fileOutput)
        {
            // Static-archive-with-swiftmodule shape: a `.framework` whose binary is a static `ar`
            // archive paired with a complete `Modules/<Mod>.swiftmodule/...`.
            // The detection-order rule routes this to the Swift binding path
            // because Swift evidence is present, regardless of binary kind.
            // No `.tbd` planted: this exercises the static-archive synthesis
            // path so the test catches downstream regressions, not just step 7
            // routing.
            using var fixture = new XCFrameworkFixture();
            fixture.WriteInfoPlist(XCFrameworkModuleDiscoveryTests.MakeSimplePlist("IndoorMapsSdk"));
            var sliceDir = fixture.CreateSlice("ios-arm64-simulator", "IndoorMapsSdk.framework", "IndoorMapsSdk.framework/IndoorMapsSdk");
            var moduleDir = fixture.CreateSwiftModule(sliceDir, "IndoorMapsSdk.framework", "IndoorMapsSdk");
            fixture.CreateSwiftInterface(moduleDir, "arm64-apple-ios-simulator");
            fixture.CreateAbiJson(moduleDir, "arm64-apple-ios-simulator");

            var runner = new MockCommandRunner();
            // VerifyDynamicLibrary at step 8 must NOT run on Swift-evidence
            // slices. `file` is, however, invoked downstream by IsStaticArchive
            // when synthesizing a TBD — that call is driven by the response
            // below.
            runner.SetResponse("file", 0, fileOutput);
            runner.SetResponse("nm", 0,
                "IndoorMapsSdk-1.o:\n0000000000000000 T _$s12IndoorMapsSdk1FunctionV6methodyyF\n");

            var result = XCFrameworkResolver.Resolve(
                fixture.RootPath, fixture.OutputPath,
                XCFrameworkPlatformTarget.Simulator, NullLogger.Instance, runner);

            Assert.Equal("IndoorMapsSdk", result.ModuleName);
            Assert.NotNull(result.SwiftInterfacePath);
            Assert.Contains("IndoorMapsSdk.framework/IndoorMapsSdk", result.DylibPath);
            // Detection-order proof: tapi stubify was never reached.
            Assert.DoesNotContain(runner.Invocations, i => i.Arguments.Contains("tapi stubify"));
            // Synthesis proof: nm fed the TBD generator.
            Assert.Contains(runner.Invocations, i => i.Command == "nm" && i.Arguments.Contains("-gU"));
            // The synthesized TBD must be parseable by the in-process JSON parser.
            Assert.True(File.Exists(result.TbdPath));
            var tbdJson = File.ReadAllText(result.TbdPath);
            Assert.Contains("\"tapi_tbd_version\"", tbdJson);
            Assert.Contains("$s12IndoorMapsSdk1FunctionV6methodyyF", tbdJson);
        }

        [Fact]
        public void Resolve_BareStaticArchive_WithSwiftInterface_TakesSwiftPath()
        {
            // BindingTests/Fixtures/StaticSwift shape: a bare `libFoo.a` slice
            // with `Modules/Foo.swiftmodule/` at the slice root (NOT wrapped
            // under a .framework bundle). Common for Swift packages emitting
            // `-static -emit-library` directly.
            using var fixture = new XCFrameworkFixture();
            var plist = """
                <?xml version="1.0" encoding="UTF-8"?>
                <plist version="1.0">
                <dict>
                    <key>AvailableLibraries</key>
                    <array>
                        <dict>
                            <key>LibraryIdentifier</key><string>ios-arm64-simulator</string>
                            <key>LibraryPath</key><string>libStaticSwiftLib.a</string>
                            <key>SupportedArchitectures</key><array><string>arm64</string></array>
                            <key>SupportedPlatform</key><string>ios</string>
                            <key>SupportedPlatformVariant</key><string>simulator</string>
                        </dict>
                    </array>
                </dict>
                </plist>
                """;
            fixture.WriteInfoPlist(plist);
            var sliceDir = Path.Combine(fixture.RootPath, "ios-arm64-simulator");
            Directory.CreateDirectory(sliceDir);
            File.WriteAllText(Path.Combine(sliceDir, "libStaticSwiftLib.a"), "");
            var moduleDir = Path.Combine(sliceDir, "Modules", "StaticSwiftLib.swiftmodule");
            Directory.CreateDirectory(moduleDir);
            File.WriteAllText(Path.Combine(moduleDir, "arm64-apple-ios-simulator.swiftinterface"), "// swift-interface");
            File.WriteAllText(Path.Combine(moduleDir, "arm64-apple-ios-simulator.abi.json"), "{}");

            var runner = new MockCommandRunner();
            runner.SetResponse("file", 0, "current ar archive random library");
            runner.SetResponse("nm", 0,
                "StaticSwiftLib-1.o:\n0000000000000120 T _$s14StaticSwiftLib0A8GreetingV5greet4nameS2S_tF\n");

            var result = XCFrameworkResolver.Resolve(
                fixture.RootPath, fixture.OutputPath,
                XCFrameworkPlatformTarget.Simulator, NullLogger.Instance, runner);

            Assert.Equal("StaticSwiftLib", result.ModuleName);
            Assert.NotNull(result.SwiftInterfacePath);
            Assert.EndsWith("libStaticSwiftLib.a", result.DylibPath);
            Assert.DoesNotContain(runner.Invocations, i => i.Arguments.Contains("tapi stubify"));
            Assert.Contains(runner.Invocations, i => i.Command == "nm" && i.Arguments.Contains("-gU"));
            Assert.True(File.Exists(result.TbdPath));
        }

        [Fact]
        public void Resolve_StaticArchive_TbdSynthesis_ProducesParseableJson()
        {
            // Direct coverage of the JSON-TBD synthesis path: feed a realistic
            // `nm -gU` listing (multiple object headers, duplicate symbols
            // across members, non-Swift entries) and confirm the generator
            // emits valid JSON with deduped symbol names.
            using var fixture = new XCFrameworkFixture();
            fixture.WriteInfoPlist(XCFrameworkModuleDiscoveryTests.MakeSimplePlist("Sample"));
            var sliceDir = fixture.CreateSlice("ios-arm64-simulator", "Sample.framework", "Sample.framework/Sample");
            var moduleDir = fixture.CreateSwiftModule(sliceDir, "Sample.framework", "Sample");
            fixture.CreateSwiftInterface(moduleDir, "arm64-apple-ios-simulator");
            fixture.CreateAbiJson(moduleDir, "arm64-apple-ios-simulator");

            var nmOutput =
                "Sample-1.o:\n" +
                "0000000000000000 T _$s6Sample1AV3fooyyF\n" +
                "0000000000000010 T _$s6Sample1BV3baryyF\n" +
                "0000000000000020 S _$s6Sample1AVMn\n" +
                "Sample-2.o:\n" +
                "0000000000000000 T _$s6Sample1AV3fooyyF\n" +   // duplicate
                "0000000000000030 T ___swift_memcpy16_8\n" +     // non-Swift global
                "0000000000000040 T _objc_class_$_SampleClass\n" +
                // Multi-token symbolic refs from Swift reflection metadata —
                // must round-trip whole, not just the last whitespace token.
                "0000000000000050 S _symbolic SS\n" +
                "0000000000000060 S _symbolic _____ 6Sample1AV\n";

            var runner = new MockCommandRunner();
            runner.SetResponse("file", 0, "current ar archive random library");
            runner.SetResponse("nm", 0, nmOutput);

            var result = XCFrameworkResolver.Resolve(
                fixture.RootPath, fixture.OutputPath,
                XCFrameworkPlatformTarget.Simulator, NullLogger.Instance, runner);

            Assert.True(File.Exists(result.TbdPath));
            var tbdJson = File.ReadAllText(result.TbdPath);

            // Structural shape — JsonTbdFormatParser keys the parse on these.
            Assert.Contains("\"tapi_tbd_version\"", tbdJson);
            Assert.Contains("\"main_library\"", tbdJson);
            Assert.Contains("\"exported_symbols\"", tbdJson);
            Assert.Contains("\"global\"", tbdJson);

            // Symbol deduplication — the duplicated `_$s6Sample1AV3fooyyF`
            // must appear exactly once in the synthesized TBD.
            var firstIdx = tbdJson.IndexOf("$s6Sample1AV3fooyyF", StringComparison.Ordinal);
            var secondIdx = tbdJson.IndexOf("$s6Sample1AV3fooyyF", firstIdx + 1, StringComparison.Ordinal);
            Assert.True(firstIdx > 0, "expected first occurrence of the deduped symbol");
            Assert.Equal(-1, secondIdx);

            // Roundtrip: the synthesized JSON must parse cleanly via the
            // in-process JsonTbdFormatParser used by the binding generator.
            using var doc = System.Text.Json.JsonDocument.Parse(tbdJson);
            var root = doc.RootElement;
            Assert.Equal(5, root.GetProperty("tapi_tbd_version").GetInt32());
            var globals = root.GetProperty("main_library")
                              .GetProperty("exported_symbols")[0]
                              .GetProperty("text")
                              .GetProperty("global");
            // Every symbol from nm (Swift, runtime helpers, ObjC globals) is
            // emitted as-is — Symbol classification happens downstream.
            var emitted = new HashSet<string>();
            foreach (var sym in globals.EnumerateArray())
                emitted.Add(sym.GetString()!);
            Assert.Contains("_$s6Sample1AV3fooyyF", emitted);
            Assert.Contains("_$s6Sample1BV3baryyF", emitted);
            Assert.Contains("___swift_memcpy16_8", emitted);
            Assert.Contains("_objc_class_$_SampleClass", emitted);
            // Full multi-token names must round-trip — taking only the last
            // whitespace-delimited token would emit `SS` / `6Sample1AV`.
            Assert.Contains("_symbolic SS", emitted);
            Assert.Contains("_symbolic _____ 6Sample1AV", emitted);
            Assert.DoesNotContain("SS", emitted);
        }

        [Fact]
        public void Resolve_BareStaticArchive_NoSwiftEvidence_DetectsStatic()
        {
            // True ObjC static archive (no Modules/ alongside) — must still
            // throw StaticLibraryException so the caller routes to ObjC fallback.
            using var fixture = new XCFrameworkFixture();
            var plist = """
                <?xml version="1.0" encoding="UTF-8"?>
                <plist version="1.0">
                <dict>
                    <key>AvailableLibraries</key>
                    <array>
                        <dict>
                            <key>BinaryPath</key><string>libObjC.a</string>
                            <key>LibraryIdentifier</key><string>ios-arm64-simulator</string>
                            <key>LibraryPath</key><string>libObjC.a</string>
                            <key>SupportedArchitectures</key><array><string>arm64</string></array>
                            <key>SupportedPlatform</key><string>ios</string>
                            <key>SupportedPlatformVariant</key><string>simulator</string>
                        </dict>
                    </array>
                </dict>
                </plist>
                """;
            fixture.WriteInfoPlist(plist);
            var sliceDir = Path.Combine(fixture.RootPath, "ios-arm64-simulator");
            Directory.CreateDirectory(sliceDir);
            File.WriteAllText(Path.Combine(sliceDir, "libObjC.a"), "");

            var ex = Assert.Throws<StaticLibraryException>(() =>
                XCFrameworkResolver.Resolve(
                    fixture.RootPath, fixture.OutputPath,
                    XCFrameworkPlatformTarget.Simulator, NullLogger.Instance));
            Assert.Contains("Static xcframeworks", ex.Message);
        }

        [Fact]
        public void Resolve_FileCommandFails_ThrowsActionableError()
        {
            // No Swift evidence (.swiftmodule absent) so the resolver actually
            // invokes `file` — this is the path that surfaces the actionable
            // "install xcode-select" error when the host lacks command-line tools.
            using var fixture = new XCFrameworkFixture();
            fixture.WriteInfoPlist(XCFrameworkModuleDiscoveryTests.MakeSimplePlist("Lib"));
            fixture.CreateSlice("ios-arm64-simulator", "Lib.framework", "Lib.framework/Lib");

            var runner = new MockCommandRunner();
            runner.SetResponse("file", 1, "", "file: command not found");

            var ex = Assert.Throws<InvalidOperationException>(() =>
                XCFrameworkResolver.Resolve(
                    fixture.RootPath, fixture.OutputPath,
                    XCFrameworkPlatformTarget.Simulator, NullLogger.Instance, runner));
            Assert.Contains("Failed to verify binary type", ex.Message);
            Assert.Contains("xcode-select", ex.Message);
        }

        [Fact]
        public void ResolveSiblingFrameworkSearchPaths_FindsSiblingXCFrameworks()
        {
            // Create a parent dir with two sibling xcframeworks
            var tempDir = Path.Combine(Path.GetTempPath(), $"sibling_test_{Guid.NewGuid():N}");
            try
            {
                Directory.CreateDirectory(tempDir);

                // Main framework
                var mainXcfw = Path.Combine(tempDir, "Main.xcframework");
                Directory.CreateDirectory(mainXcfw);
                WriteSiblingPlist(mainXcfw, "Main");

                // Sibling framework
                var siblingXcfw = Path.Combine(tempDir, "Sibling.xcframework");
                Directory.CreateDirectory(siblingXcfw);
                WriteSiblingPlist(siblingXcfw, "Sibling");

                var paths = XCFrameworkResolver.ResolveSiblingFrameworkSearchPaths(
                    mainXcfw, XCFrameworkPlatformTarget.Simulator, NullLogger.Instance);

                Assert.Single(paths);
                Assert.Contains("Sibling.xcframework", paths[0]);
                Assert.Contains("ios-arm64-simulator", paths[0]);
            }
            finally
            {
                Directory.Delete(tempDir, true);
            }
        }

        [Fact]
        public void ResolveSiblingFrameworkSearchPaths_ExcludesSelf()
        {
            var tempDir = Path.Combine(Path.GetTempPath(), $"sibling_self_test_{Guid.NewGuid():N}");
            try
            {
                Directory.CreateDirectory(tempDir);
                var mainXcfw = Path.Combine(tempDir, "Main.xcframework");
                Directory.CreateDirectory(mainXcfw);
                WriteSiblingPlist(mainXcfw, "Main");

                var paths = XCFrameworkResolver.ResolveSiblingFrameworkSearchPaths(
                    mainXcfw, XCFrameworkPlatformTarget.Simulator, NullLogger.Instance);

                Assert.Empty(paths);
            }
            finally
            {
                Directory.Delete(tempDir, true);
            }
        }

        private static void WriteSiblingPlist(string xcfwPath, string name)
        {
            var plist = $"""
                <?xml version="1.0" encoding="UTF-8"?>
                <plist version="1.0">
                <dict>
                    <key>AvailableLibraries</key>
                    <array>
                        <dict>
                            <key>BinaryPath</key><string>{name}.framework/{name}</string>
                            <key>LibraryIdentifier</key><string>ios-arm64-simulator</string>
                            <key>LibraryPath</key><string>{name}.framework</string>
                            <key>SupportedArchitectures</key><array><string>arm64</string></array>
                            <key>SupportedPlatform</key><string>ios</string>
                            <key>SupportedPlatformVariant</key><string>simulator</string>
                        </dict>
                    </array>
                </dict>
                </plist>
                """;
            File.WriteAllText(Path.Combine(xcfwPath, "Info.plist"), plist);
            var sliceDir = Path.Combine(xcfwPath, "ios-arm64-simulator");
            Directory.CreateDirectory(sliceDir);
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

    #region I. ObjC Framework Resolution Tests

    public class XCFrameworkObjCResolutionTests
    {
        private static readonly ILogger Logger = NullLogger.Instance;

        [Fact]
        public void ResolveObjCFramework_WithModulemap_ReturnsResolution()
        {
            using var fixture = new XCFrameworkFixture("ObjCLib.xcframework");
            fixture.WriteInfoPlist(MakeObjCPlist("ObjCLib"));
            var sliceDir = fixture.CreateSlice("ios-arm64_x86_64-simulator",
                "ObjCLib.framework", "ObjCLib.framework/ObjCLib");
            // Create Modules dir with module.modulemap but no .swiftmodule
            var modulesDir = Path.Combine(sliceDir, "ObjCLib.framework", "Modules");
            Directory.CreateDirectory(modulesDir);
            File.WriteAllText(Path.Combine(modulesDir, "module.modulemap"),
                "framework module ObjCLib {\n  umbrella header \"ObjCLib.h\"\n}\n");

            var result = XCFrameworkResolver.ResolveObjCFramework(
                fixture.RootPath, XCFrameworkPlatformTarget.Simulator, Logger);

            Assert.NotNull(result);
            Assert.Equal("ObjCLib", result.ModuleName);
            Assert.True(result.IsSimulatorSlice);
            Assert.Contains("ios-arm64_x86_64-simulator", result.FrameworkSearchPath);
        }

        [Fact]
        public void ResolveObjCFramework_NoModulemap_ReturnsNull()
        {
            using var fixture = new XCFrameworkFixture("NoMap.xcframework");
            fixture.WriteInfoPlist(MakeObjCPlist("NoMap"));
            var sliceDir = fixture.CreateSlice("ios-arm64_x86_64-simulator",
                "NoMap.framework", "NoMap.framework/NoMap");
            // Create Modules dir but no modulemap
            var modulesDir = Path.Combine(sliceDir, "NoMap.framework", "Modules");
            Directory.CreateDirectory(modulesDir);

            var result = XCFrameworkResolver.ResolveObjCFramework(
                fixture.RootPath, XCFrameworkPlatformTarget.Simulator, Logger);

            Assert.Null(result);
        }

        [Fact]
        public void ResolveObjCFramework_NoModulesDir_ReturnsNull()
        {
            using var fixture = new XCFrameworkFixture("NoModules.xcframework");
            fixture.WriteInfoPlist(MakeObjCPlist("NoModules"));
            fixture.CreateSlice("ios-arm64_x86_64-simulator",
                "NoModules.framework", "NoModules.framework/NoModules");
            // No Modules directory at all

            var result = XCFrameworkResolver.ResolveObjCFramework(
                fixture.RootPath, XCFrameworkPlatformTarget.Simulator, Logger);

            Assert.Null(result);
        }

        [Fact]
        public void ResolveObjCFramework_InvalidPath_ReturnsNull()
        {
            var result = XCFrameworkResolver.ResolveObjCFramework(
                "/nonexistent/path.xcframework",
                XCFrameworkPlatformTarget.Simulator, Logger);

            Assert.Null(result);
        }

        [Fact]
        public void ResolveObjCFramework_SimulatorSlice_IsSimulatorSliceTrue()
        {
            using var fixture = new XCFrameworkFixture("SimLib.xcframework");
            fixture.WriteInfoPlist(MakeDualSliceObjCPlist("SimLib"));
            // Create both slices
            var simSliceDir = fixture.CreateSlice("ios-arm64_x86_64-simulator",
                "SimLib.framework", "SimLib.framework/SimLib");
            var deviceSliceDir = fixture.CreateSlice("ios-arm64",
                "SimLib.framework", "SimLib.framework/SimLib");
            // Add modulemaps to both
            foreach (var dir in new[] { simSliceDir, deviceSliceDir })
            {
                var modulesDir = Path.Combine(dir, "SimLib.framework", "Modules");
                Directory.CreateDirectory(modulesDir);
                File.WriteAllText(Path.Combine(modulesDir, "module.modulemap"),
                    "framework module SimLib {}\n");
            }

            var simResult = XCFrameworkResolver.ResolveObjCFramework(
                fixture.RootPath, XCFrameworkPlatformTarget.Simulator, Logger);
            var deviceResult = XCFrameworkResolver.ResolveObjCFramework(
                fixture.RootPath, XCFrameworkPlatformTarget.Device, Logger);

            Assert.NotNull(simResult);
            Assert.True(simResult.IsSimulatorSlice);
            Assert.NotNull(deviceResult);
            Assert.False(deviceResult.IsSimulatorSlice);
        }

        [Fact]
        public void ParseModuleNameFromModulemap_FrameworkModule_ExtractsName()
        {
            var tmpFile = Path.GetTempFileName();
            try
            {
                File.WriteAllText(tmpFile, "framework module PaymentSdk3DS2 {\n  umbrella header \"PaymentSdk3DS2.h\"\n}\n");
                var result = XCFrameworkResolver.ParseModuleNameFromModulemap(tmpFile);
                Assert.Equal("PaymentSdk3DS2", result);
            }
            finally { File.Delete(tmpFile); }
        }

        [Fact]
        public void ParseModuleNameFromModulemap_PlainModule_ExtractsName()
        {
            var tmpFile = Path.GetTempFileName();
            try
            {
                File.WriteAllText(tmpFile, "module FooBar {\n}\n");
                var result = XCFrameworkResolver.ParseModuleNameFromModulemap(tmpFile);
                Assert.Equal("FooBar", result);
            }
            finally { File.Delete(tmpFile); }
        }

        [Fact]
        public void ParseModuleNameFromModulemap_ModuleStar_Skipped()
        {
            var tmpFile = Path.GetTempFileName();
            try
            {
                // "module * {}" is a wildcard — should be skipped
                File.WriteAllText(tmpFile, "module * { export * }\n");
                var result = XCFrameworkResolver.ParseModuleNameFromModulemap(tmpFile);
                Assert.Null(result);
            }
            finally { File.Delete(tmpFile); }
        }

        [Fact]
        public void DiscoverSwiftModule_NoModulesDir_ThrowsSwiftModuleNotFoundException()
        {
            using var fixture = new XCFrameworkFixture("NoMod.xcframework");
            fixture.WriteInfoPlist(MakeObjCPlist("NoMod"));
            var sliceDir = fixture.CreateSlice("ios-arm64_x86_64-simulator",
                "NoMod.framework", "NoMod.framework/NoMod");
            // Create Modules dir but no .swiftmodule inside
            var modulesDir = Path.Combine(sliceDir, "NoMod.framework", "Modules");
            Directory.CreateDirectory(modulesDir);

            var runner = new MockCommandRunner();
            runner.SetResponse("file", 0, "dynamically linked shared library");

            // Should throw SwiftModuleNotFoundException specifically
            Assert.Throws<SwiftModuleNotFoundException>(() =>
                XCFrameworkResolver.Resolve(
                    fixture.RootPath, fixture.OutputPath,
                    XCFrameworkPlatformTarget.Simulator, NullLogger.Instance, runner));
        }

        // ─────────────────────────────────────────────────────────────────
        // Synthesized-xcframework regression tests.
        // The SDK's _SynthesizeAppleFrameworkXcframework target hand-builds a
        // single-slice xcframework around an Apple system framework (Matter,
        // etc.) because xcodebuild -create-xcframework rejects .tbd-only
        // frameworks. These tests confirm XCFrameworkResolver accepts the
        // exact layout the SDK emits: device slice (no SupportedPlatformVariant
        // key), arm64-only, with the .tbd link stub but no Mach-O binary.
        // If these regress, Matter and MatterSupport stop building.
        // ─────────────────────────────────────────────────────────────────

        [Fact]
        public void ResolveObjCFramework_DeviceOnlySynthesizedSlice_Resolves()
        {
            using var fixture = new XCFrameworkFixture("Matter.xcframework");
            fixture.WriteInfoPlist(MakeSynthesizedDevicePlist("Matter"));
            // SDK uses "ios-arm64" as the device slice id (matches
            // _SwiftBindingDeviceSliceId for iOS device targets).
            var sliceDir = Path.Combine(fixture.RootPath, "ios-arm64",
                "Matter.framework");
            Directory.CreateDirectory(sliceDir);
            // .tbd link stub instead of a Mach-O binary — Apple system
            // frameworks ship as .tbd-only and that's what cp -R copies.
            File.WriteAllText(Path.Combine(sliceDir, "Matter.tbd"), "--- !tapi-tbd");
            // ObjC modulemap (no swiftmodule).
            var modulesDir = Path.Combine(sliceDir, "Modules");
            Directory.CreateDirectory(modulesDir);
            File.WriteAllText(Path.Combine(modulesDir, "module.modulemap"),
                "framework module Matter {\n  umbrella header \"Matter.h\"\n}\n");

            var result = XCFrameworkResolver.ResolveObjCFramework(
                fixture.RootPath, XCFrameworkPlatformTarget.Device, NullLogger.Instance);

            Assert.NotNull(result);
            Assert.Equal("Matter", result.ModuleName);
            Assert.False(result.IsSimulatorSlice);
            Assert.Contains("ios-arm64", result.FrameworkSearchPath);
        }

        [Fact]
        public void ResolveObjCFramework_SimulatorSynthesizedSlice_Resolves()
        {
            using var fixture = new XCFrameworkFixture("Matter.xcframework");
            fixture.WriteInfoPlist(MakeSynthesizedSimulatorPlist("Matter"));
            // Simulator slice id matches _SwiftBindingSimulatorSliceId.
            var sliceDir = Path.Combine(fixture.RootPath, "ios-arm64-simulator",
                "Matter.framework");
            Directory.CreateDirectory(sliceDir);
            File.WriteAllText(Path.Combine(sliceDir, "Matter.tbd"), "--- !tapi-tbd");
            var modulesDir = Path.Combine(sliceDir, "Modules");
            Directory.CreateDirectory(modulesDir);
            File.WriteAllText(Path.Combine(modulesDir, "module.modulemap"),
                "framework module Matter {\n  umbrella header \"Matter.h\"\n}\n");

            var result = XCFrameworkResolver.ResolveObjCFramework(
                fixture.RootPath, XCFrameworkPlatformTarget.Simulator, NullLogger.Instance);

            Assert.NotNull(result);
            Assert.Equal("Matter", result.ModuleName);
            Assert.True(result.IsSimulatorSlice);
            Assert.Contains("ios-arm64-simulator", result.FrameworkSearchPath);
        }

        // Mimics _SynthesizeAppleFrameworkXcframework's Info.plist exactly:
        // one device slice with no SupportedPlatformVariant key. Apple's plist
        // convention OMITS the variant key for plain device slices; XCFrameworkResolver
        // must treat null/missing variant as device.
        private static string MakeSynthesizedDevicePlist(string name)
        {
            return $$"""
                <?xml version="1.0" encoding="UTF-8"?>
                <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
                <plist version="1.0">
                <dict>
                    <key>AvailableLibraries</key>
                    <array>
                        <dict>
                            <key>LibraryIdentifier</key><string>ios-arm64</string>
                            <key>LibraryPath</key><string>{{name}}.framework</string>
                            <key>SupportedArchitectures</key><array><string>arm64</string></array>
                            <key>SupportedPlatform</key><string>ios</string>
                        </dict>
                    </array>
                    <key>CFBundlePackageType</key><string>XFWK</string>
                    <key>XCFrameworkFormatVersion</key><string>1.0</string>
                </dict>
                </plist>
                """;
        }

        // Mimics _SynthesizeAppleFrameworkXcframework's Info.plist for the
        // simulator case: the SupportedPlatformVariant key IS present.
        private static string MakeSynthesizedSimulatorPlist(string name)
        {
            return $$"""
                <?xml version="1.0" encoding="UTF-8"?>
                <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
                <plist version="1.0">
                <dict>
                    <key>AvailableLibraries</key>
                    <array>
                        <dict>
                            <key>LibraryIdentifier</key><string>ios-arm64-simulator</string>
                            <key>LibraryPath</key><string>{{name}}.framework</string>
                            <key>SupportedArchitectures</key><array><string>arm64</string></array>
                            <key>SupportedPlatform</key><string>ios</string>
                            <key>SupportedPlatformVariant</key><string>simulator</string>
                        </dict>
                    </array>
                    <key>CFBundlePackageType</key><string>XFWK</string>
                    <key>XCFrameworkFormatVersion</key><string>1.0</string>
                </dict>
                </plist>
                """;
        }

        private static string MakeObjCPlist(string name)
        {
            return $$"""
                <?xml version="1.0" encoding="UTF-8"?>
                <plist version="1.0">
                <dict>
                    <key>AvailableLibraries</key>
                    <array>
                        <dict>
                            <key>BinaryPath</key><string>{{name}}.framework/{{name}}</string>
                            <key>LibraryIdentifier</key><string>ios-arm64_x86_64-simulator</string>
                            <key>LibraryPath</key><string>{{name}}.framework</string>
                            <key>SupportedArchitectures</key><array><string>arm64</string><string>x86_64</string></array>
                            <key>SupportedPlatform</key><string>ios</string>
                            <key>SupportedPlatformVariant</key><string>simulator</string>
                        </dict>
                    </array>
                </dict>
                </plist>
                """;
        }

        private static string MakeDualSliceObjCPlist(string name)
        {
            return $$"""
                <?xml version="1.0" encoding="UTF-8"?>
                <plist version="1.0">
                <dict>
                    <key>AvailableLibraries</key>
                    <array>
                        <dict>
                            <key>BinaryPath</key><string>{{name}}.framework/{{name}}</string>
                            <key>LibraryIdentifier</key><string>ios-arm64_x86_64-simulator</string>
                            <key>LibraryPath</key><string>{{name}}.framework</string>
                            <key>SupportedArchitectures</key><array><string>arm64</string><string>x86_64</string></array>
                            <key>SupportedPlatform</key><string>ios</string>
                            <key>SupportedPlatformVariant</key><string>simulator</string>
                        </dict>
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

        // ─────────────────────────────────────────────────────────────────────
        // SelectArchitecture — forced/requested CPU arch with fail-loud
        // ─────────────────────────────────────────────────────────────────────

        private static XCFrameworkSlice FatSlice(params string[] archs) => new XCFrameworkSlice
        {
            BinaryPath = "Lib.framework/Lib",
            LibraryIdentifier = "macos-arm64",
            LibraryPath = "Lib.framework",
            SupportedArchitectures = archs.ToList(),
            SupportedPlatform = "macos",
            SupportedPlatformVariant = null,
        };

        [Fact]
        public void SelectArchitecture_NullRequest_PrefersArm64()
        {
            Assert.Equal("arm64", XCFrameworkResolver.SelectArchitecture(FatSlice("arm64", "x86_64"), null));
        }

        [Fact]
        public void SelectArchitecture_NullRequest_FallsBackToFirstWhenNoArm64()
        {
            Assert.Equal("x86_64", XCFrameworkResolver.SelectArchitecture(FatSlice("x86_64"), null));
        }

        [Fact]
        public void SelectArchitecture_RequestX86_64_PresentInFatSlice_Returns()
        {
            Assert.Equal("x86_64", XCFrameworkResolver.SelectArchitecture(FatSlice("arm64", "x86_64"), "x86_64"));
        }

        [Fact]
        public void SelectArchitecture_RequestX86_64_Absent_ThrowsSwiftBind052()
        {
            var ex = Assert.Throws<InvalidOperationException>(
                () => XCFrameworkResolver.SelectArchitecture(FatSlice("arm64"), "x86_64"));
            Assert.Contains("SWIFTBIND052", ex.Message);
            Assert.Contains("x86_64", ex.Message);
        }
    }

    #endregion

    #region J. Companion Module ABI Extraction Tests

    /// <summary>
    /// Issue #41: a thin Swift wrapper whose <c>.swiftinterface</c> imports a companion module
    /// could not have its ABI extracted because <c>swift-frontend</c> was invoked with no
    /// <c>-F</c> framework search paths, so it could not resolve the companion and aborted before
    /// writing any report. These tests pin the fix: the framework's own slice plus explicit and
    /// auto-detected companion slices are passed as <c>-F</c> paths, and a missing companion now
    /// yields an actionable error that names the module and flags the misleading SDK-version cascade.
    /// </summary>
    public class XCFrameworkCompanionModuleTests
    {
        // ----- Pure helper: search-path list construction -----

        [Fact]
        public void BuildAbiFrameworkSearchPaths_OrdersSelfThenExplicitThenSiblings()
        {
            var result = XCFrameworkResolver.BuildAbiFrameworkSearchPaths(
                "/fw/Self.xcframework/ios",
                new[] { "/explicit/A.xcframework/ios", "/explicit/B.xcframework/ios" },
                new[] { "/siblings/C.xcframework/ios" });

            Assert.Equal(4, result.Count);
            Assert.EndsWith("Self.xcframework/ios", result[0]);
            Assert.EndsWith("A.xcframework/ios", result[1]);
            Assert.EndsWith("B.xcframework/ios", result[2]);
            Assert.EndsWith("C.xcframework/ios", result[3]);
        }

        [Fact]
        public void BuildAbiFrameworkSearchPaths_DedupsAcrossSources()
        {
            // The same slice appears as both an explicit companion AND a sibling — keep one,
            // and self is added only once even when it reappears among siblings.
            var result = XCFrameworkResolver.BuildAbiFrameworkSearchPaths(
                "/fw/Self.xcframework/ios",
                new[] { "/shared/Dup.xcframework/ios" },
                new[] { "/shared/Dup.xcframework/ios", "/fw/Self.xcframework/ios" });

            Assert.Equal(2, result.Count);
            Assert.EndsWith("Self.xcframework/ios", result[0]);
            Assert.EndsWith("Dup.xcframework/ios", result[1]);
        }

        // ----- Pure helper: missing-companion hint -----

        [Fact]
        public void BuildMissingCompanionModuleHint_EmptyWhenNothingMissing()
        {
            var hint = XCFrameworkResolver.BuildMissingCompanionModuleHint("Foo", new List<string>());
            Assert.Equal(string.Empty, hint);
        }

        [Fact]
        public void BuildMissingCompanionModuleHint_NamesModulesAndRemediation()
        {
            var hint = XCFrameworkResolver.BuildMissingCompanionModuleHint(
                "MlVisionLibTasksGenAI", new List<string> { "MlVisionLibTasksGenAIC" });

            Assert.Contains("MlVisionLibTasksGenAIC", hint);
            Assert.Contains("--framework-dependency", hint);
            Assert.Contains("SwiftFrameworkDependency", hint);
            Assert.Contains("misleading", hint);   // the SDK-version cascade caveat
        }

        // ----- TryResolveSliceSearchPath -----

        [Fact]
        public void TryResolveSliceSearchPath_ResolvesMatchingSlice()
        {
            var tempDir = Path.Combine(Path.GetTempPath(), $"companion_resolve_{Guid.NewGuid():N}");
            try
            {
                var companion = Path.Combine(tempDir, "Engine.xcframework");
                Directory.CreateDirectory(companion);
                WriteCompanionPlist(companion, "Engine");

                var sliceDir = XCFrameworkResolver.TryResolveSliceSearchPath(
                    companion, XCFrameworkPlatformTarget.Simulator, NullLogger.Instance);

                Assert.NotNull(sliceDir);
                Assert.EndsWith("ios-arm64-simulator", sliceDir);
            }
            finally { Directory.Delete(tempDir, true); }
        }

        [Fact]
        public void TryResolveSliceSearchPath_NullWhenNotAnXCFramework()
        {
            var sliceDir = XCFrameworkResolver.TryResolveSliceSearchPath(
                Path.Combine(Path.GetTempPath(), $"does_not_exist_{Guid.NewGuid():N}"),
                XCFrameworkPlatformTarget.Simulator, NullLogger.Instance);
            Assert.Null(sliceDir);
        }

        // ----- End-to-end through Resolve: -F propagation (success) -----

        [Fact]
        public void Resolve_GeneratesAbi_PassesExplicitCompanionAsFrameworkSearchPath()
        {
            using var fixture = new XCFrameworkFixture("Wrapper.xcframework");
            fixture.WriteInfoPlist(XCFrameworkModuleDiscoveryTests.MakeSimplePlist("Wrapper"));
            var sliceDir = fixture.CreateSlice("ios-arm64-simulator", "Wrapper.framework", "Wrapper.framework/Wrapper");
            var moduleDir = fixture.CreateSwiftModule(sliceDir, "Wrapper.framework", "Wrapper");
            fixture.CreateSwiftInterface(moduleDir, "arm64-apple-ios-simulator");  // no abi.json → generate
            fixture.CreateTbd(moduleDir, "Wrapper");

            // A companion xcframework in a SEPARATE directory, passed explicitly.
            var companionParent = Path.Combine(Path.GetTempPath(), $"companion_explicit_{Guid.NewGuid():N}");
            var companion = Path.Combine(companionParent, "Engine.xcframework");
            Directory.CreateDirectory(companion);
            WriteCompanionPlist(companion, "Engine");

            try
            {
                var runner = new MockCommandRunner();
                runner.SetResponse("--show-sdk-path", 0, "/fake/iPhoneSimulator.sdk");
                runner.SetResponse("swift-frontend", 0, "");
                // Pre-create the abi.json that swift-frontend would emit so the success path is taken.
                File.WriteAllText(Path.Combine(fixture.OutputPath, "Wrapper.abi.json"), "{}");

                XCFrameworkResolver.Resolve(
                    fixture.RootPath, fixture.OutputPath,
                    XCFrameworkPlatformTarget.Simulator, NullLogger.Instance, runner,
                    companionFrameworkPaths: new[] { companion });

                // Self slice AND explicit companion slice both appear as -F paths on the frontend call.
                Assert.Contains(runner.Invocations, i =>
                    i.Arguments.Contains("compile-module-from-interface") &&
                    i.Arguments.Contains("-F \"" + Path.Combine(fixture.RootPath, "ios-arm64-simulator")) &&
                    i.Arguments.Contains("Engine.xcframework"));
            }
            finally { Directory.Delete(companionParent, true); }
        }

        [Fact]
        public void Resolve_GeneratesAbi_DropsUnresolvableExplicitCompanionKeepsValidOne()
        {
            using var fixture = new XCFrameworkFixture("Wrapper.xcframework");
            fixture.WriteInfoPlist(XCFrameworkModuleDiscoveryTests.MakeSimplePlist("Wrapper"));
            var sliceDir = fixture.CreateSlice("ios-arm64-simulator", "Wrapper.framework", "Wrapper.framework/Wrapper");
            var moduleDir = fixture.CreateSwiftModule(sliceDir, "Wrapper.framework", "Wrapper");
            fixture.CreateSwiftInterface(moduleDir, "arm64-apple-ios-simulator");
            fixture.CreateTbd(moduleDir, "Wrapper");

            var companionParent = Path.Combine(Path.GetTempPath(), $"companion_mixed_{Guid.NewGuid():N}");
            var goodCompanion = Path.Combine(companionParent, "Engine.xcframework");
            Directory.CreateDirectory(goodCompanion);
            WriteCompanionPlist(goodCompanion, "Engine");
            // A bogus path that cannot resolve to a slice — must be dropped, not crash, not appear in -F.
            var bogusCompanion = Path.Combine(companionParent, "DoesNotExist.xcframework");

            try
            {
                var runner = new MockCommandRunner();
                runner.SetResponse("--show-sdk-path", 0, "/fake/iPhoneSimulator.sdk");
                runner.SetResponse("swift-frontend", 0, "");
                File.WriteAllText(Path.Combine(fixture.OutputPath, "Wrapper.abi.json"), "{}");

                XCFrameworkResolver.Resolve(
                    fixture.RootPath, fixture.OutputPath,
                    XCFrameworkPlatformTarget.Simulator, NullLogger.Instance, runner,
                    companionFrameworkPaths: new[] { goodCompanion, bogusCompanion });

                // The resolvable companion is present; the bogus one never appears as a -F path.
                Assert.Contains(runner.Invocations, i =>
                    i.Arguments.Contains("compile-module-from-interface") &&
                    i.Arguments.Contains("Engine.xcframework") &&
                    !i.Arguments.Contains("DoesNotExist.xcframework"));
            }
            finally { Directory.Delete(companionParent, true); }
        }

        [Fact]
        public void Resolve_GeneratesAbi_AutoDetectsCoLocatedCompanionSibling()
        {
            // The reported co-located companion scenario: companion sits NEXT TO the wrapper, no explicit flag.
            var parent = Path.Combine(Path.GetTempPath(), $"colocated_{Guid.NewGuid():N}");
            var wrapperRoot = Path.Combine(parent, "Wrapper.xcframework");
            var outputPath = Path.Combine(parent, "output");
            Directory.CreateDirectory(wrapperRoot);
            Directory.CreateDirectory(outputPath);
            try
            {
                File.WriteAllText(Path.Combine(wrapperRoot, "Info.plist"),
                    XCFrameworkModuleDiscoveryTests.MakeSimplePlist("Wrapper"));
                var fwDir = Path.Combine(wrapperRoot, "ios-arm64-simulator", "Wrapper.framework");
                Directory.CreateDirectory(fwDir);
                File.WriteAllText(Path.Combine(fwDir, "Wrapper"), "");
                var moduleDir = Path.Combine(fwDir, "Modules", "Wrapper.swiftmodule");
                Directory.CreateDirectory(moduleDir);
                File.WriteAllText(Path.Combine(moduleDir, "arm64-apple-ios-simulator.swiftinterface"), "// iface");
                File.WriteAllText(Path.Combine(moduleDir, "Wrapper.tbd"), "--- !tapi-tbd");

                // Co-located companion sibling
                var companion = Path.Combine(parent, "Engine.xcframework");
                Directory.CreateDirectory(companion);
                WriteCompanionPlist(companion, "Engine");

                var runner = new MockCommandRunner();
                runner.SetResponse("--show-sdk-path", 0, "/fake/iPhoneSimulator.sdk");
                runner.SetResponse("swift-frontend", 0, "");
                File.WriteAllText(Path.Combine(outputPath, "Wrapper.abi.json"), "{}");

                XCFrameworkResolver.Resolve(
                    wrapperRoot, outputPath,
                    XCFrameworkPlatformTarget.Simulator, NullLogger.Instance, runner);

                // Sibling auto-detected and surfaced as a -F path even with no explicit dependency.
                Assert.Contains(runner.Invocations, i =>
                    i.Arguments.Contains("compile-module-from-interface") &&
                    i.Arguments.Contains("Engine.xcframework"));
            }
            finally { Directory.Delete(parent, true); }
        }

        // ----- End-to-end through Resolve: actionable error (failure) -----

        [Fact]
        public void Resolve_MissingCompanionModule_ThrowsActionableSwiftbind103()
        {
            using var fixture = new XCFrameworkFixture("Wrapper.xcframework");
            fixture.WriteInfoPlist(XCFrameworkModuleDiscoveryTests.MakeSimplePlist("Wrapper"));
            var sliceDir = fixture.CreateSlice("ios-arm64-simulator", "Wrapper.framework", "Wrapper.framework/Wrapper");
            var moduleDir = fixture.CreateSwiftModule(sliceDir, "Wrapper.framework", "Wrapper");
            fixture.CreateSwiftInterface(moduleDir, "arm64-apple-ios-simulator");
            fixture.CreateTbd(moduleDir, "Wrapper");

            var runner = new MockCommandRunner();
            runner.SetResponse("--show-sdk-path", 0, "/fake/iPhoneSimulator.sdk");
            // swift-frontend fails with the missing-companion error (the issue #41 cascade).
            runner.SetResponse("compile-module-from-interface", 1, "",
                "error: no such module 'EngineKit'\n" +
                "error: failed to build module 'Wrapper'; this SDK is not supported by the compiler");
            // Deliberately do NOT pre-create the abi.json — generation must be seen as failed.

            var ex = Assert.Throws<InvalidOperationException>(() =>
                XCFrameworkResolver.Resolve(
                    fixture.RootPath, fixture.OutputPath,
                    XCFrameworkPlatformTarget.Simulator, NullLogger.Instance, runner));

            Assert.Contains("SWIFTBIND103", ex.Message);
            Assert.Contains("EngineKit", ex.Message);                  // names the missing companion
            Assert.Contains("--framework-dependency", ex.Message);     // remediation
            Assert.Contains("misleading", ex.Message);                 // SDK-version cascade caveat
            Assert.Contains("no such module 'EngineKit'", ex.Message); // preserves underlying stderr
        }

        [Fact]
        public void Resolve_AbiGenFailsWithoutMissingModule_NoCascadeClaim()
        {
            // A swift-frontend failure that is NOT a missing-module error must not claim the
            // "SDK-version line is misleading" cascade — that caveat is only valid for missing modules.
            using var fixture = new XCFrameworkFixture("Wrapper.xcframework");
            fixture.WriteInfoPlist(XCFrameworkModuleDiscoveryTests.MakeSimplePlist("Wrapper"));
            var sliceDir = fixture.CreateSlice("ios-arm64-simulator", "Wrapper.framework", "Wrapper.framework/Wrapper");
            var moduleDir = fixture.CreateSwiftModule(sliceDir, "Wrapper.framework", "Wrapper");
            fixture.CreateSwiftInterface(moduleDir, "arm64-apple-ios-simulator");
            fixture.CreateTbd(moduleDir, "Wrapper");

            var runner = new MockCommandRunner();
            runner.SetResponse("--show-sdk-path", 0, "/fake/iPhoneSimulator.sdk");
            runner.SetResponse("compile-module-from-interface", 1, "", "error: some unrelated frontend failure");

            var ex = Assert.Throws<InvalidOperationException>(() =>
                XCFrameworkResolver.Resolve(
                    fixture.RootPath, fixture.OutputPath,
                    XCFrameworkPlatformTarget.Simulator, NullLogger.Instance, runner));

            Assert.Contains("SWIFTBIND103", ex.Message);
            Assert.DoesNotContain("misleading", ex.Message);
            Assert.Contains("some unrelated frontend failure", ex.Message);
        }

        private static void WriteCompanionPlist(string xcfwPath, string name)
        {
            var plist = $"""
                <?xml version="1.0" encoding="UTF-8"?>
                <plist version="1.0">
                <dict>
                    <key>AvailableLibraries</key>
                    <array>
                        <dict>
                            <key>BinaryPath</key><string>{name}.framework/{name}</string>
                            <key>LibraryIdentifier</key><string>ios-arm64-simulator</string>
                            <key>LibraryPath</key><string>{name}.framework</string>
                            <key>SupportedArchitectures</key><array><string>arm64</string></array>
                            <key>SupportedPlatform</key><string>ios</string>
                            <key>SupportedPlatformVariant</key><string>simulator</string>
                        </dict>
                    </array>
                </dict>
                </plist>
                """;
            File.WriteAllText(Path.Combine(xcfwPath, "Info.plist"), plist);
            Directory.CreateDirectory(Path.Combine(xcfwPath, "ios-arm64-simulator"));
        }
    }

    #endregion

    #region MergeWrapperDependencySearchPaths (gap-a wrapper sibling parity)

    /// <summary>
    /// The wrapper compile must see the same co-located sibling xcframeworks the ABI extraction
    /// already auto-detects, so a companion dropped next to the source resolves <c>import</c>
    /// for <c>swiftc</c> and not just for symbol-graph extraction. <see
    /// cref="XCFrameworkResolver.MergeWrapperDependencySearchPaths"/> folds explicit
    /// <c>--framework-dependency</c> paths together with auto-detected siblings: explicit first,
    /// de-duplicated and full-pathed, null when the merged set is empty.
    /// </summary>
    public class MergeWrapperDependencySearchPathsTests
    {
        private static readonly ILogger Logger = NullLogger.Instance;

        [Fact]
        public void ExplicitPaths_NoSiblings_PreservedAndFullPathed()
        {
            var root = CreateTempDir();
            try
            {
                // A primary whose parent dir has no other *.xcframework -> no siblings.
                var primary = Path.Combine(root, "Primary.xcframework");
                Directory.CreateDirectory(primary);
                var dep = CreateTempDir();
                try
                {
                    var merged = XCFrameworkResolver.MergeWrapperDependencySearchPaths(
                        new[] { dep }, primary, XCFrameworkPlatformTarget.Simulator, Logger);

                    Assert.NotNull(merged);
                    Assert.Single(merged!);
                    Assert.Equal(Path.GetFullPath(dep), merged![0]);
                }
                finally { Directory.Delete(dep, true); }
            }
            finally { Directory.Delete(root, true); }
        }

        [Fact]
        public void NoExplicit_NoSiblings_ReturnsNull()
        {
            // Null preserves the historical "no additional -F" behavior for the common case.
            var root = CreateTempDir();
            try
            {
                var primary = Path.Combine(root, "Primary.xcframework");
                Directory.CreateDirectory(primary);
                var merged = XCFrameworkResolver.MergeWrapperDependencySearchPaths(
                    null, primary, XCFrameworkPlatformTarget.Simulator, Logger);
                Assert.Null(merged);
            }
            finally { Directory.Delete(root, true); }
        }

        [Fact]
        public void DuplicateExplicitPaths_Deduped()
        {
            var root = CreateTempDir();
            try
            {
                var primary = Path.Combine(root, "Primary.xcframework");
                Directory.CreateDirectory(primary);
                var dep = CreateTempDir();
                try
                {
                    var merged = XCFrameworkResolver.MergeWrapperDependencySearchPaths(
                        new[] { dep, dep }, primary, XCFrameworkPlatformTarget.Simulator, Logger);
                    Assert.NotNull(merged);
                    Assert.Single(merged!);
                }
                finally { Directory.Delete(dep, true); }
            }
            finally { Directory.Delete(root, true); }
        }

        [Fact]
        public void SiblingAutoDetected_AppendedAfterExplicit()
        {
            // Drop a companion xcframework next to the primary; it must be discovered and merged
            // AFTER the explicit path — the gap-a parity proof for the wrapper compile.
            var root = CreateTempDir();
            try
            {
                var primary = Path.Combine(root, "Primary.xcframework");
                Directory.CreateDirectory(primary);
                var companion = Path.Combine(root, "Companion.xcframework");
                Directory.CreateDirectory(companion);
                WriteSimSlicePlist(companion, "Companion");

                var dep = CreateTempDir();
                try
                {
                    var merged = XCFrameworkResolver.MergeWrapperDependencySearchPaths(
                        new[] { dep }, primary, XCFrameworkPlatformTarget.Simulator, Logger);

                    Assert.NotNull(merged);
                    var siblingSlice = Path.GetFullPath(Path.Combine(companion, "ios-arm64-simulator"));
                    Assert.Equal(Path.GetFullPath(dep), merged![0]);
                    Assert.Contains(siblingSlice, merged);
                    Assert.True(merged.IndexOf(Path.GetFullPath(dep)) < merged.IndexOf(siblingSlice),
                        "explicit path must precede the auto-detected sibling");
                }
                finally { Directory.Delete(dep, true); }
            }
            finally { Directory.Delete(root, true); }
        }

        private static void WriteSimSlicePlist(string xcfwPath, string name)
        {
            var plist = $"""
                <?xml version="1.0" encoding="UTF-8"?>
                <plist version="1.0">
                <dict>
                    <key>AvailableLibraries</key>
                    <array>
                        <dict>
                            <key>BinaryPath</key><string>{name}.framework/{name}</string>
                            <key>LibraryIdentifier</key><string>ios-arm64-simulator</string>
                            <key>LibraryPath</key><string>{name}.framework</string>
                            <key>SupportedArchitectures</key><array><string>arm64</string></array>
                            <key>SupportedPlatform</key><string>ios</string>
                            <key>SupportedPlatformVariant</key><string>simulator</string>
                        </dict>
                    </array>
                </dict>
                </plist>
                """;
            File.WriteAllText(Path.Combine(xcfwPath, "Info.plist"), plist);
            Directory.CreateDirectory(Path.Combine(xcfwPath, "ios-arm64-simulator"));
        }

        private static string CreateTempDir()
        {
            var dir = Path.Combine(Path.GetTempPath(), $"mwd_test_{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    #endregion
}
