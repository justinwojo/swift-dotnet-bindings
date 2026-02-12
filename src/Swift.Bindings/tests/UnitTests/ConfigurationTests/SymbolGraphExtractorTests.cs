// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BindingsGeneration.Tests
{
    #region A. SymbolGraphExtractor Tests

    public class SymbolGraphExtractorTests : IDisposable
    {
        private static readonly ILogger Logger = NullLogger.Instance;
        private readonly string _tempDir;
        private readonly string _outputDir;

        public SymbolGraphExtractorTests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), $"sgx_test_{Guid.NewGuid():N}");
            _outputDir = Path.Combine(_tempDir, "output");
            Directory.CreateDirectory(_outputDir);
        }

        public void Dispose()
        {
            if (Directory.Exists(_tempDir))
            {
                try { Directory.Delete(_tempDir, true); } catch { }
            }
        }

        private XCFrameworkResolution CreateResolution(bool isSimulator = true, string arch = "arm64")
        {
            // Create a minimal framework directory structure for ResolveDeploymentTarget
            var frameworkDir = Path.Combine(_tempDir, "TestLib.framework");
            Directory.CreateDirectory(frameworkDir);
            var dylibPath = Path.Combine(frameworkDir, "TestLib");
            File.WriteAllText(dylibPath, ""); // stub

            return new XCFrameworkResolution
            {
                AbiJsonPath = Path.Combine(_tempDir, "test.abi.json"),
                DylibPath = dylibPath,
                TbdPath = Path.Combine(_tempDir, "test.tbd"),
                ModuleName = "TestLib",
                XCFrameworkPath = Path.Combine(_tempDir, "TestLib.xcframework"),
                FrameworkSearchPath = Path.Combine(_tempDir, "ios-arm64-simulator"),
                LibraryIdentifier = isSimulator ? "ios-arm64-simulator" : "ios-arm64",
                IsSimulatorSlice = isSimulator,
                SelectedArchitecture = arch
            };
        }

        [Fact]
        public void Extract_Success_ReturnsOutputDirectory()
        {
            var runner = new MockCommandRunner();
            runner.SetResponse("--show-sdk-path", 0, "/Applications/Xcode.app/Contents/Developer/Platforms/iPhoneSimulator.platform/Developer/SDKs/iPhoneSimulator.sdk");
            runner.SetResponse("swift-symbolgraph-extract", 0, "");
            // Also mock the deployment target resolver
            runner.SetResponse("plutil", 0, "{\"MinimumOSVersion\":\"15.0\"}");

            var resolution = CreateResolution();

            // Pre-create output .symbols.json to simulate extraction output
            var symbolgraphDir = Path.Combine(_outputDir, "symbolgraph");
            Directory.CreateDirectory(symbolgraphDir);
            // The Extract method will delete and recreate, so we hook into the mock
            // to create the file after extraction runs
            runner.SetResponse("swift-symbolgraph-extract", 0, "");

            // We need the file to exist after Extract runs — use a callback approach
            // by creating it in advance and having Extract's clean+recreate flow work.
            // Actually, Extract deletes then recreates the dir, then runs the command.
            // We need the mock runner to trigger file creation. Instead, let's override
            // the mock to create the file as a side effect.
            var sideEffectRunner = new SideEffectCommandRunner(runner, (cmd, args) =>
            {
                if (args.Contains("swift-symbolgraph-extract"))
                {
                    // Simulate output file creation
                    var outDir = Path.Combine(_outputDir, "symbolgraph");
                    Directory.CreateDirectory(outDir);
                    File.WriteAllText(Path.Combine(outDir, "TestLib.symbols.json"), "{}");
                }
            });

            var result = SymbolGraphExtractor.Extract(resolution, _outputDir, Logger, sideEffectRunner);

            Assert.NotNull(result);
            Assert.Equal(Path.Combine(_outputDir, "symbolgraph"), result);
        }

        [Fact]
        public void Extract_CommandFails_ReturnsNull_LogsWarning()
        {
            var runner = new MockCommandRunner();
            runner.SetResponse("--show-sdk-path", 0, "/path/to/sdk");
            runner.SetResponse("swift-symbolgraph-extract", 1, "", "error: module not found");
            runner.SetResponse("plutil", 0, "{\"MinimumOSVersion\":\"15.0\"}");

            var resolution = CreateResolution();

            var result = SymbolGraphExtractor.Extract(resolution, _outputDir, Logger, runner);

            Assert.Null(result);
        }

        [Fact]
        public void Extract_XcrunNotFound_ReturnsNull()
        {
            var runner = new ThrowingCommandRunner();

            var resolution = CreateResolution();

            var result = SymbolGraphExtractor.Extract(resolution, _outputDir, Logger, runner);

            Assert.Null(result);
        }

        [Fact]
        public void Extract_SimulatorSlice_UsesIphonesimulatorSdk()
        {
            var runner = new MockCommandRunner();
            runner.SetResponse("--show-sdk-path", 0, "/path/to/sim/sdk");
            runner.SetResponse("swift-symbolgraph-extract", 0, "");
            runner.SetResponse("plutil", 0, "{\"MinimumOSVersion\":\"15.0\"}");

            var resolution = CreateResolution(isSimulator: true);

            // Pre-hook to create output file
            var sideEffectRunner = new SideEffectCommandRunner(runner, (cmd, args) =>
            {
                if (args.Contains("swift-symbolgraph-extract"))
                {
                    var outDir = Path.Combine(_outputDir, "symbolgraph");
                    Directory.CreateDirectory(outDir);
                    File.WriteAllText(Path.Combine(outDir, "TestLib.symbols.json"), "{}");
                }
            });

            SymbolGraphExtractor.Extract(resolution, _outputDir, Logger, sideEffectRunner);

            // Verify SDK resolution used iphonesimulator
            Assert.Contains(runner.Invocations, i => i.Arguments.Contains("--sdk iphonesimulator"));
        }

        [Fact]
        public void Extract_DeviceSlice_UsesIphoneosSdk()
        {
            var runner = new MockCommandRunner();
            runner.SetResponse("--show-sdk-path", 0, "/path/to/dev/sdk");
            runner.SetResponse("swift-symbolgraph-extract", 0, "");
            runner.SetResponse("plutil", 0, "{\"MinimumOSVersion\":\"15.0\"}");

            var resolution = CreateResolution(isSimulator: false);

            var sideEffectRunner = new SideEffectCommandRunner(runner, (cmd, args) =>
            {
                if (args.Contains("swift-symbolgraph-extract"))
                {
                    var outDir = Path.Combine(_outputDir, "symbolgraph");
                    Directory.CreateDirectory(outDir);
                    File.WriteAllText(Path.Combine(outDir, "TestLib.symbols.json"), "{}");
                }
            });

            SymbolGraphExtractor.Extract(resolution, _outputDir, Logger, sideEffectRunner);

            // Verify SDK resolution used iphoneos
            Assert.Contains(runner.Invocations, i => i.Arguments.Contains("--sdk iphoneos"));
        }

        [Fact]
        public void Extract_VerifyCommandArguments()
        {
            var runner = new MockCommandRunner();
            runner.SetResponse("--show-sdk-path", 0, "/path/to/sdk");
            runner.SetResponse("swift-symbolgraph-extract", 0, "");
            runner.SetResponse("plutil", 0, "{\"MinimumOSVersion\":\"16.0\"}");

            var resolution = CreateResolution(isSimulator: true, arch: "arm64");

            var sideEffectRunner = new SideEffectCommandRunner(runner, (cmd, args) =>
            {
                if (args.Contains("swift-symbolgraph-extract"))
                {
                    var outDir = Path.Combine(_outputDir, "symbolgraph");
                    Directory.CreateDirectory(outDir);
                    File.WriteAllText(Path.Combine(outDir, "TestLib.symbols.json"), "{}");
                }
            });

            SymbolGraphExtractor.Extract(resolution, _outputDir, Logger, sideEffectRunner);

            var extractInvocation = runner.Invocations.FirstOrDefault(i => i.Arguments.Contains("swift-symbolgraph-extract"));
            Assert.NotNull(extractInvocation.Arguments);
            Assert.Contains("-module-name TestLib", extractInvocation.Arguments);
            Assert.Contains("-target arm64-apple-ios", extractInvocation.Arguments);
            Assert.Contains("-minimum-access-level public", extractInvocation.Arguments);
            Assert.Contains($"-F \"{resolution.FrameworkSearchPath}\"", extractInvocation.Arguments);
        }

        [Fact]
        public void Extract_CleansOutputDirectory()
        {
            var runner = new MockCommandRunner();
            runner.SetResponse("--show-sdk-path", 0, "/path/to/sdk");
            runner.SetResponse("swift-symbolgraph-extract", 0, "");
            runner.SetResponse("plutil", 0, "{\"MinimumOSVersion\":\"15.0\"}");

            var resolution = CreateResolution();

            // Create a stale file in the symbolgraph directory
            var symbolgraphDir = Path.Combine(_outputDir, "symbolgraph");
            Directory.CreateDirectory(symbolgraphDir);
            var staleFile = Path.Combine(symbolgraphDir, "OldModule.symbols.json");
            File.WriteAllText(staleFile, "stale");

            var sideEffectRunner = new SideEffectCommandRunner(runner, (cmd, args) =>
            {
                if (args.Contains("swift-symbolgraph-extract"))
                {
                    // Verify stale file was cleaned
                    Assert.False(File.Exists(staleFile), "Stale file should have been cleaned before extraction");
                    var outDir = Path.Combine(_outputDir, "symbolgraph");
                    Directory.CreateDirectory(outDir);
                    File.WriteAllText(Path.Combine(outDir, "TestLib.symbols.json"), "{}");
                }
            });

            var result = SymbolGraphExtractor.Extract(resolution, _outputDir, Logger, sideEffectRunner);

            Assert.NotNull(result);
        }

        [Fact]
        public void Extract_NoSymbolsJsonOutput_ReturnsNull()
        {
            var runner = new MockCommandRunner();
            runner.SetResponse("--show-sdk-path", 0, "/path/to/sdk");
            runner.SetResponse("swift-symbolgraph-extract", 0, ""); // Succeeds but produces no files
            runner.SetResponse("plutil", 0, "{\"MinimumOSVersion\":\"15.0\"}");

            var resolution = CreateResolution();

            var result = SymbolGraphExtractor.Extract(resolution, _outputDir, Logger, runner);

            Assert.Null(result);
        }

        [Fact]
        public void Extract_SdkResolutionFails_ReturnsNull()
        {
            var runner = new MockCommandRunner();
            runner.SetResponse("--show-sdk-path", 1, "", "xcrun: error: unable to find sdk");

            var resolution = CreateResolution();

            var result = SymbolGraphExtractor.Extract(resolution, _outputDir, Logger, runner);

            Assert.Null(result);
        }
    }

    /// <summary>
    /// Command runner that delegates to an inner runner but triggers side effects
    /// (e.g., creating output files) to simulate real tool behavior.
    /// </summary>
    internal sealed class SideEffectCommandRunner : ICommandRunner
    {
        private readonly MockCommandRunner _inner;
        private readonly Action<string, string> _sideEffect;

        public SideEffectCommandRunner(MockCommandRunner inner, Action<string, string> sideEffect)
        {
            _inner = inner;
            _sideEffect = sideEffect;
        }

        public (int ExitCode, string StdOut, string StdErr) Run(string command, string arguments, int timeoutMs = 30000)
        {
            _sideEffect(command, arguments);
            return _inner.Run(command, arguments, timeoutMs);
        }
    }

    /// <summary>
    /// Command runner that throws on any invocation, simulating a missing tool.
    /// </summary>
    internal sealed class ThrowingCommandRunner : ICommandRunner
    {
        public (int ExitCode, string StdOut, string StdErr) Run(string command, string arguments, int timeoutMs = 30000)
        {
            throw new InvalidOperationException($"Command not found: {command}");
        }
    }

    #endregion

    #region B. ResolveSymbolGraphPath Tests

    [Trait("Category", "ResolveSymbolGraphPath")]
    public class ResolveSymbolGraphPathTests
    {
        private static readonly ILogger Logger = NullLogger.Instance;

        [Fact]
        public void ResolveSymbolGraphPath_ExplicitSymbolGraph_AlwaysReturned_EvenWithNoDocs()
        {
            var result = BindingsGenerator.ResolveSymbolGraphPath(
                explicitSymbolGraph: "/path/to/symbolgraph",
                noDocs: true,
                resolution: null,
                outputDirectory: "/output",
                Logger);

            Assert.Equal("/path/to/symbolgraph", result);
        }

        [Fact]
        public void ResolveSymbolGraphPath_NoDocs_NoExplicitPath_ReturnsNull()
        {
            var result = BindingsGenerator.ResolveSymbolGraphPath(
                explicitSymbolGraph: null,
                noDocs: true,
                resolution: null,
                outputDirectory: "/output",
                Logger);

            Assert.Null(result);
        }

        [Fact]
        public void ResolveSymbolGraphPath_XcframeworkMode_NoFlags_AutoExtracts()
        {
            // Create a minimal resolution that will trigger extraction
            var tempDir = Path.Combine(Path.GetTempPath(), $"rsgp_test_{Guid.NewGuid():N}");
            var outputDir = Path.Combine(tempDir, "output");
            Directory.CreateDirectory(outputDir);

            try
            {
                var frameworkDir = Path.Combine(tempDir, "TestLib.framework");
                Directory.CreateDirectory(frameworkDir);
                var dylibPath = Path.Combine(frameworkDir, "TestLib");
                File.WriteAllText(dylibPath, "");

                var resolution = new XCFrameworkResolution
                {
                    AbiJsonPath = Path.Combine(tempDir, "test.abi.json"),
                    DylibPath = dylibPath,
                    TbdPath = Path.Combine(tempDir, "test.tbd"),
                    ModuleName = "TestLib",
                    XCFrameworkPath = Path.Combine(tempDir, "TestLib.xcframework"),
                    FrameworkSearchPath = Path.Combine(tempDir, "ios-arm64-simulator"),
                    LibraryIdentifier = "ios-arm64-simulator",
                    IsSimulatorSlice = true,
                    SelectedArchitecture = "arm64"
                };

                var runner = new MockCommandRunner();
                runner.SetResponse("--show-sdk-path", 0, "/path/to/sdk");
                runner.SetResponse("swift-symbolgraph-extract", 0, "");
                runner.SetResponse("plutil", 0, "{\"MinimumOSVersion\":\"15.0\"}");

                var sideEffectRunner = new SideEffectCommandRunner(runner, (cmd, args) =>
                {
                    if (args.Contains("swift-symbolgraph-extract"))
                    {
                        var sgDir = Path.Combine(outputDir, "symbolgraph");
                        Directory.CreateDirectory(sgDir);
                        File.WriteAllText(Path.Combine(sgDir, "TestLib.symbols.json"), "{}");
                    }
                });

                var result = BindingsGenerator.ResolveSymbolGraphPath(
                    explicitSymbolGraph: null,
                    noDocs: false,
                    resolution: resolution,
                    outputDirectory: outputDir,
                    Logger,
                    commandRunner: sideEffectRunner);

                Assert.NotNull(result);
                Assert.Contains("symbolgraph", result);
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
        public void ResolveSymbolGraphPath_ManualMode_NoResolution_ReturnsNull()
        {
            var result = BindingsGenerator.ResolveSymbolGraphPath(
                explicitSymbolGraph: null,
                noDocs: false,
                resolution: null,
                outputDirectory: "/output",
                Logger);

            Assert.Null(result);
        }
    }

    #endregion
}
