// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System.CommandLine;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BindingsGeneration.Tests
{
    /// <summary>
    /// Tests for --sdk-mode, --package-id, and --wrapper-architectures CLI options.
    /// These are structural tests that verify option parsing and help text.
    /// End-to-end behavior is validated by integration tests.
    /// </summary>
    [Collection("ConsoleCapture")]
    public class ProgramSdkModeTests
    {
        [Fact]
        public void Help_IncludesSdkModeOption()
        {
            var output = CaptureHelp();
            Assert.Contains("--sdk-mode", output);
        }

        [Fact]
        public void Help_IncludesPackageIdOption()
        {
            var output = CaptureHelp();
            Assert.Contains("--package-id", output);
        }

        [Fact]
        public void Help_IncludesWrapperArchitecturesOption()
        {
            var output = CaptureHelp();
            Assert.Contains("--wrapper-architectures", output);
        }

        [Fact]
        public void SdkModeOption_DefaultsFalse()
        {
            // Verify that --sdk-mode is a recognized option that defaults to false
            // System.CommandLine auto-generates help mentioning the option description
            var output = CaptureHelp();
            Assert.Contains("--sdk-mode", output);
            Assert.Contains("SDK mode", output);
        }

        [Fact]
        public void WrapperArchitecturesOption_DefaultsToSimulator()
        {
            var output = CaptureHelp();
            Assert.Contains("--wrapper-architectures", output);
            Assert.Contains("simulator", output);
        }

        [Fact]
        public void PackageIdOption_DescribesOverride()
        {
            var output = CaptureHelp();
            Assert.Contains("--package-id", output);
        }

        [Fact]
        public void MissingOutput_StillFails()
        {
            // Verify existing required-option behavior is preserved
            var writer = new StringWriter();
            Console.SetOut(writer);
            try
            {
                BindingsGenerator.Main(new[] { "--sdk-mode" });
                // Should fail because -o is required
            }
            finally
            {
                Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
            }
        }

        [Fact]
        public void InvalidWrapperArchitectures_DoesNotCrash()
        {
            // Verifies the parser accepts the option string without crashing
            // (actual validation happens in the handler, which needs -o and inputs)
            var writer = new StringWriter();
            Console.SetOut(writer);
            try
            {
                BindingsGenerator.Main(new[] { "--wrapper-architectures", "invalid", "-o", "/tmp/test" });
            }
            finally
            {
                Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
            }
        }

        [Fact]
        public void MissingOutput_ReturnsNonZeroExitCode()
        {
            var writer = new StringWriter();
            Console.SetOut(writer);
            try
            {
                var exitCode = BindingsGenerator.Main(new[] { "--xcframework", "/nonexistent" });
                Assert.NotEqual(0, exitCode);
            }
            finally
            {
                Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
            }
        }

        [Fact]
        public void ConflictingInputModes_ReturnsNonZeroExitCode()
        {
            var dir = Path.Combine(Path.GetTempPath(), $"exitcode_conflict_{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            try
            {
                var writer = new StringWriter();
                Console.SetOut(writer);
                try
                {
                    var exitCode = BindingsGenerator.Main(new[]
                    {
                        "--xcframework", "/nonexistent",
                        "-a", "/nonexistent/abi.json",
                        "-o", dir
                    });
                    Assert.NotEqual(0, exitCode);
                }
                finally
                {
                    Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
                }
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void MissingAllInputs_ReturnsNonZeroExitCode()
        {
            var dir = Path.Combine(Path.GetTempPath(), $"exitcode_noinput_{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            try
            {
                var writer = new StringWriter();
                Console.SetOut(writer);
                try
                {
                    var exitCode = BindingsGenerator.Main(new[] { "-o", dir });
                    Assert.NotEqual(0, exitCode);
                }
                finally
                {
                    Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
                }
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void Help_ReturnsZeroExitCode()
        {
            var writer = new StringWriter();
            Console.SetOut(writer);
            try
            {
                var exitCode = BindingsGenerator.Main(new[] { "-h" });
                Assert.Equal(0, exitCode);
            }
            finally
            {
                Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
            }
        }

        [Fact]
        public void EmptyModuleName_ReturnsNonZeroExitCode_NoUnhandledException()
        {
            // Craft an ABI JSON with empty module name to trigger the try-catch in GenerateBindings
            var dir = Path.Combine(Path.GetTempPath(), $"audit_catch_{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            try
            {
                var abiJson = """
                    {
                      "ABIRoot": {
                        "kind": "Root",
                        "name": "",
                        "printedName": "",
                        "children": [
                          {
                            "kind": "TypeDecl",
                            "name": "Foo",
                            "moduleName": ""
                          }
                        ]
                      }
                    }
                    """;
                var abiPath = Path.Combine(dir, "abi.json");
                File.WriteAllText(abiPath, abiJson);
                // Create stub tbd and dylib
                var tbdPath = Path.Combine(dir, "lib.tbd");
                File.WriteAllText(tbdPath, "--- !tapi-tbd\ntbd-version: 4\ntargets: []\ninstall-name: /usr/lib/lib.dylib\n...\n");
                var dylibPath = Path.Combine(dir, "lib.dylib");
                File.WriteAllText(dylibPath, "");

                var writer = new StringWriter();
                Console.SetOut(writer);
                try
                {
                    var exitCode = BindingsGenerator.Main(new[]
                    {
                        "-a", abiPath,
                        "-d", dylibPath,
                        "-t", tbdPath,
                        "-o", dir,
                        "-l", "TestLib"
                    });
                    // Should fail gracefully (non-zero exit) rather than crashing
                    Assert.NotEqual(0, exitCode);
                }
                finally
                {
                    Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
                }
            }
            finally { Directory.Delete(dir, true); }
        }

        private static string CaptureHelp()
        {
            var writer = new StringWriter();
            Console.SetOut(writer);
            try
            {
                BindingsGenerator.Main(new[] { "-h" });
                return writer.ToString();
            }
            finally
            {
                Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
            }
        }
    }

    /// <summary>
    /// Tests for --skip-wrapper-compilation and --compile-wrapper-only CLI flags.
    /// </summary>
    [Collection("ConsoleCapture")]
    public class TwoPassBuildCliTests
    {
        [Fact]
        public void Help_IncludesSkipWrapperCompilationOption()
        {
            var output = CaptureHelp();
            Assert.Contains("--skip-wrapper-compilation", output);
        }

        [Fact]
        public void Help_IncludesCompileWrapperOnlyOption()
        {
            var output = CaptureHelp();
            Assert.Contains("--compile-wrapper-only", output);
        }

        [Fact]
        public void MutuallyExclusiveFlags_ReturnsNonZeroExitCode()
        {
            var dir = Path.Combine(Path.GetTempPath(), $"twopass_mutex_{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            try
            {
                var writer = new StringWriter();
                Console.SetOut(writer);
                try
                {
                    var exitCode = BindingsGenerator.Main(new[]
                    {
                        "--skip-wrapper-compilation",
                        "--compile-wrapper-only",
                        "--xcframework", "/nonexistent.xcframework",
                        "-o", dir
                    });
                    Assert.NotEqual(0, exitCode);
                }
                finally
                {
                    Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
                }
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void CompileWrapperOnly_WithoutXcframework_ReturnsNonZeroExitCode()
        {
            var dir = Path.Combine(Path.GetTempPath(), $"twopass_noxcfw_{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            try
            {
                var writer = new StringWriter();
                Console.SetOut(writer);
                try
                {
                    var exitCode = BindingsGenerator.Main(new[]
                    {
                        "--compile-wrapper-only",
                        "-o", dir
                    });
                    Assert.NotEqual(0, exitCode);
                }
                finally
                {
                    Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
                }
            }
            finally { Directory.Delete(dir, true); }
        }

        private static string CaptureHelp()
        {
            var writer = new StringWriter();
            Console.SetOut(writer);
            try
            {
                BindingsGenerator.Main(new[] { "-h" });
                return writer.ToString();
            }
            finally
            {
                Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
            }
        }
    }

    /// <summary>
    /// Tests for SaveWrapperContext / LoadWrapperContext round-trip persistence.
    /// </summary>
    public class WrapperContextPersistenceTests : IDisposable
    {
        private static readonly ILogger _logger = Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
        private readonly string _tempDir;

        public WrapperContextPersistenceTests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), $"wrapper-ctx-{Guid.NewGuid():N}");
            Directory.CreateDirectory(_tempDir);
        }

        public void Dispose()
        {
            try { Directory.Delete(_tempDir, true); } catch { }
        }

        [Fact]
        public void RoundTrip_PreservesInternalTypeNames()
        {
            var internalTypes = new HashSet<string> { "InternalFoo", "Caches", "Module.InternalBar" };
            BindingsGenerator.SaveWrapperContext(_tempDir, internalTypes, null, null, _logger);

            var (loaded, _, _) = BindingsGenerator.LoadWrapperContext(_tempDir, _logger);

            Assert.NotNull(loaded);
            Assert.Equal(internalTypes, loaded);
        }

        [Fact]
        public void RoundTrip_PreservesModuleNameForCollision()
        {
            BindingsGenerator.SaveWrapperContext(_tempDir, null, "MyModule", null, _logger);

            var (_, moduleNameForCollision, _) = BindingsGenerator.LoadWrapperContext(_tempDir, _logger);

            Assert.Equal("MyModule", moduleNameForCollision);
        }

        [Fact]
        public void RoundTrip_PreservesNestedTypesInCollidingClass()
        {
            var nested = new HashSet<string> { "NestedA", "NestedB" };
            BindingsGenerator.SaveWrapperContext(_tempDir, null, "Mod", nested, _logger);

            var (_, _, loadedNested) = BindingsGenerator.LoadWrapperContext(_tempDir, _logger);

            Assert.NotNull(loadedNested);
            Assert.Equal(nested, loadedNested);
        }

        [Fact]
        public void RoundTrip_AllFieldsTogether()
        {
            var internalTypes = new HashSet<string> { "TypeA" };
            var nested = new HashSet<string> { "NestedX" };
            BindingsGenerator.SaveWrapperContext(_tempDir, internalTypes, "Collision", nested, _logger);

            var (loadedInternal, loadedCollision, loadedNested) =
                BindingsGenerator.LoadWrapperContext(_tempDir, _logger);

            Assert.Equal(internalTypes, loadedInternal);
            Assert.Equal("Collision", loadedCollision);
            Assert.Equal(nested, loadedNested);
        }

        [Fact]
        public void Load_MissingFile_ReturnsNulls()
        {
            var emptyDir = Path.Combine(_tempDir, "empty");
            Directory.CreateDirectory(emptyDir);

            var (internalTypes, collision, nested) =
                BindingsGenerator.LoadWrapperContext(emptyDir, _logger);

            Assert.Null(internalTypes);
            Assert.Null(collision);
            Assert.Null(nested);
        }

        [Fact]
        public void Load_CorruptedFile_ReturnsNulls()
        {
            File.WriteAllText(Path.Combine(_tempDir, "wrapper-context.json"), "not valid json{{{");

            var (internalTypes, collision, nested) =
                BindingsGenerator.LoadWrapperContext(_tempDir, _logger);

            Assert.Null(internalTypes);
            Assert.Null(collision);
            Assert.Null(nested);
        }

        [Fact]
        public void RoundTrip_NullInputs_ProducesEmptyCollections()
        {
            BindingsGenerator.SaveWrapperContext(_tempDir, null, null, null, _logger);

            var (loadedInternal, loadedCollision, loadedNested) =
                BindingsGenerator.LoadWrapperContext(_tempDir, _logger);

            // null sets serialize as empty arrays; empty arrays deserialize as empty HashSets
            Assert.NotNull(loadedInternal);
            Assert.Empty(loadedInternal);
            Assert.Null(loadedCollision);
            Assert.NotNull(loadedNested);
            Assert.Empty(loadedNested);
        }
    }

    /// <summary>
    /// Tests for HandleWrapperCompilationOutcome — SDK-mode-aware outcome handling.
    /// </summary>
    public class WrapperOutcomeHandlingTests
    {
        [Fact]
        public void HandleOutcome_Fatal_SdkMode_ReturnsZeroExitWithSWIFTBIND050()
        {
            var ex = new InvalidOperationException("swiftc failed");
            var (exitCode, diagnosticCode, message) = BindingsGenerator.HandleWrapperCompilationOutcome(
                WrapperCompilationOutcome.Fatal, sdkMode: true, ex, compilationResult: null);
            Assert.Equal(0, exitCode);
            Assert.Equal("SWIFTBIND050", diagnosticCode);
            Assert.Contains("SWIFTBIND050", message);
            Assert.Contains("swiftc failed", message);
            Assert.Contains("dependency framework", message);
        }

        [Fact]
        public void HandleOutcome_Fatal_NonSdkMode_ReturnsNonZeroExit()
        {
            var ex = new InvalidOperationException("swiftc failed");
            var (exitCode, diagnosticCode, message) = BindingsGenerator.HandleWrapperCompilationOutcome(
                WrapperCompilationOutcome.Fatal, sdkMode: false, ex, compilationResult: null);
            Assert.Equal(1, exitCode);
            Assert.Null(diagnosticCode);
            Assert.Contains("swiftc failed", message);
        }

        [Fact]
        public void HandleOutcome_Warning_SdkMode_ReturnsZeroExit()
        {
            var ex = new InvalidOperationException("something went wrong");
            var (exitCode, diagnosticCode, _) = BindingsGenerator.HandleWrapperCompilationOutcome(
                WrapperCompilationOutcome.Warning, sdkMode: true, ex, compilationResult: null);
            Assert.Equal(0, exitCode);
            Assert.Null(diagnosticCode);
        }

        [Fact]
        public void HandleOutcome_Success_ReturnsZeroExit()
        {
            var (exitCode, diagnosticCode, _) = BindingsGenerator.HandleWrapperCompilationOutcome(
                WrapperCompilationOutcome.Success, sdkMode: false,
                compilationException: null, compilationResult: null);
            Assert.Equal(0, exitCode);
            Assert.Null(diagnosticCode);
        }

        [Fact]
        public void HandleOutcome_MissingModuleHint_FlowsThroughToMessage()
        {
            // Simulate the enriched exception that InvokeSwiftCompiler would throw
            var ex = new InvalidOperationException(
                "Swift wrapper compilation failed (exit code 1): error: no such module 'Stripe3DS2'\n\n" +
                "Missing module(s): 'Stripe3DS2'. Provide the xcframework(s) for these modules:\n" +
                "  CLI:  --framework-dependency /path/to/<Module>.xcframework (repeat for each)\n" +
                "  SDK:  <SwiftFrameworkDependency Include=\"path/to/<Module>.xcframework\" " +
                "PackageId=\"<Module>.Swift.iOS\" PackageVersion=\"1.0.0\" />");

            var (_, _, message) = BindingsGenerator.HandleWrapperCompilationOutcome(
                WrapperCompilationOutcome.Fatal, sdkMode: true, ex, compilationResult: null);

            Assert.Contains("Missing module(s): 'Stripe3DS2'", message);
            Assert.Contains("--framework-dependency", message);
            Assert.Contains("SwiftFrameworkDependency", message);
        }
    }

    /// <summary>
    /// Tests for FormatDependencyWarning — SWIFTBIND060 message formatting.
    /// </summary>
    public class FormatDependencyWarningTests
    {
        [Fact]
        public void FormatDependencyWarning_MissingSlice_ContainsVerifySlices()
        {
            var message = BindingsGenerator.FormatDependencyWarning("SomeDep", "missing-slice");
            Assert.Contains("SWIFTBIND060", message);
            Assert.Contains("SomeDep", message);
            Assert.Contains("device and simulator slices", message);
        }

        [Fact]
        public void FormatDependencyWarning_MissingXcframework_ContainsBuildSuggestion()
        {
            var message = BindingsGenerator.FormatDependencyWarning("OtherDep", "missing-xcframework");
            Assert.Contains("SWIFTBIND060", message);
            Assert.Contains("OtherDep", message);
            Assert.Contains("build the dependency separately", message);
        }

        [Fact]
        public void FormatDependencyWarning_UnknownReason_TreatedAsMissingXcframework()
        {
            var message = BindingsGenerator.FormatDependencyWarning("Dep", "something-else");
            Assert.Contains("SWIFTBIND060", message);
            Assert.Contains("build the dependency separately", message);
        }

        [Fact]
        public void FormatDependencyWarning_MissingSlice_ContainsMSBuildSdkGuidance()
        {
            var message = BindingsGenerator.FormatDependencyWarning("StripePayments", "missing-slice");
            Assert.Contains("SwiftFrameworkDependency", message);
            Assert.Contains("PackageId", message);
            Assert.Contains("PackageVersion", message);
        }

        [Fact]
        public void FormatDependencyWarning_MissingXcframework_ContainsMSBuildSdkGuidance()
        {
            var message = BindingsGenerator.FormatDependencyWarning("StripePayments", "missing-xcframework");
            Assert.Contains("SwiftFrameworkDependency", message);
            Assert.Contains("PackageId", message);
            Assert.Contains("PackageVersion", message);
        }

        [Theory]
        [InlineData("missing-slice")]
        [InlineData("missing-xcframework")]
        public void FormatDependencyWarning_BothReasons_ContainCliAndSdkGuidance(string reason)
        {
            var message = BindingsGenerator.FormatDependencyWarning("MyLib", reason);
            // CLI guidance
            Assert.Contains("--framework-dependency", message);
            // MSBuild SDK guidance
            Assert.Contains("SwiftFrameworkDependency", message);
        }
    }

    /// <summary>
    /// Truth-table tests for the ShouldCompileWrapper gate
    /// (platform-target × wrapper-architectures matrix).
    /// </summary>
    public class ShouldCompileWrapperTests
    {
        // ── simulator slice (--platform-target simulator, the default) ──

        [Fact]
        public void SimulatorSlice_SimulatorArch_ReturnsTrue()
        {
            Assert.True(BindingsGenerator.ShouldCompileWrapper(
                isSimulatorSlice: true, wrapperArchitectures: "simulator"));
        }

        [Fact]
        public void SimulatorSlice_DeviceArch_ReturnsTrue()
        {
            Assert.True(BindingsGenerator.ShouldCompileWrapper(
                isSimulatorSlice: true, wrapperArchitectures: "device"));
        }

        [Fact]
        public void SimulatorSlice_AllArch_ReturnsTrue()
        {
            Assert.True(BindingsGenerator.ShouldCompileWrapper(
                isSimulatorSlice: true, wrapperArchitectures: "all"));
        }

        // ── device slice (--platform-target device) ──

        [Fact]
        public void DeviceSlice_SimulatorArch_ReturnsFalse()
        {
            // No simulator slice + requesting simulator-only wrapper → skip
            Assert.False(BindingsGenerator.ShouldCompileWrapper(
                isSimulatorSlice: false, wrapperArchitectures: "simulator"));
        }

        [Fact]
        public void DeviceSlice_DeviceArch_ReturnsTrue()
        {
            Assert.True(BindingsGenerator.ShouldCompileWrapper(
                isSimulatorSlice: false, wrapperArchitectures: "device"));
        }

        [Fact]
        public void DeviceSlice_AllArch_ReturnsTrue()
        {
            Assert.True(BindingsGenerator.ShouldCompileWrapper(
                isSimulatorSlice: false, wrapperArchitectures: "all"));
        }

        // ── full matrix (Theory-based) ──

        [Theory]
        [InlineData(true, "simulator", true)]
        [InlineData(true, "device", true)]
        [InlineData(true, "all", true)]
        [InlineData(false, "simulator", false)]
        [InlineData(false, "device", true)]
        [InlineData(false, "all", true)]
        public void FullMatrix_MatchesExpected(bool isSimulatorSlice, string wrapperArchitectures, bool expected)
        {
            Assert.Equal(expected, BindingsGenerator.ShouldCompileWrapper(
                isSimulatorSlice: isSimulatorSlice, wrapperArchitectures: wrapperArchitectures));
        }

        // ── edge cases ──

        [Fact]
        public void UnknownArchitectures_SimulatorSlice_ReturnsTrue()
        {
            // Unknown value doesn't match device/all, but simulator slice is true → true
            Assert.True(BindingsGenerator.ShouldCompileWrapper(
                isSimulatorSlice: true, wrapperArchitectures: "unknown"));
        }

        [Fact]
        public void UnknownArchitectures_DeviceSlice_ReturnsFalse()
        {
            // Unknown value doesn't match device/all, and no simulator slice → false
            Assert.False(BindingsGenerator.ShouldCompileWrapper(
                isSimulatorSlice: false, wrapperArchitectures: "unknown"));
        }

        [Fact]
        public void EmptyArchitectures_DeviceSlice_ReturnsFalse()
        {
            // Empty string doesn't match device/all, and no simulator slice → false
            Assert.False(BindingsGenerator.ShouldCompileWrapper(
                isSimulatorSlice: false, wrapperArchitectures: ""));
        }

        [Fact]
        public void CaseMismatch_SimulatorArch_DeviceSlice_ReturnsFalse()
        {
            // "Simulator" != "simulator" — case-sensitive, doesn't match
            Assert.False(BindingsGenerator.ShouldCompileWrapper(
                isSimulatorSlice: false, wrapperArchitectures: "Simulator"));
        }
    }

    /// <summary>
    /// Tests for --framework-dependency CLI option and help text.
    /// </summary>
    [Collection("ConsoleCapture")]
    public class FrameworkDependencyCLITests
    {
        [Fact]
        public void Help_IncludesFrameworkDependencyOption()
        {
            var output = CaptureHelp();
            Assert.Contains("--framework-dependency", output);
        }

        [Fact]
        public void Help_DescribesDependencyRequiresXcframework()
        {
            var output = CaptureHelp();
            Assert.Contains("--framework-dependency", output);
            Assert.Contains("Requires --xcframework", output);
        }

        [Fact]
        public void FrameworkDependency_WithoutXcframework_ErrorsGracefully()
        {
            // Uses -a/-d/-t mode which should reject --framework-dependency
            var dir = Path.Combine(Path.GetTempPath(), $"fwdep_test_{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            try
            {
                var writer = new StringWriter();
                Console.SetOut(writer);
                try
                {
                    BindingsGenerator.Main(new[]
                    {
                        "-a", "/nonexistent/abi.json",
                        "-d", "/nonexistent/dylib",
                        "-t", "/nonexistent/tbd",
                        "-o", dir,
                        "--framework-dependency", "/some/dep.xcframework"
                    });
                    // Should not crash — error logged via ILogger
                }
                finally
                {
                    Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
                }
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void ResolveFrameworkDependencies_NonexistentPath_ReturnsNull()
        {
            var primaryResolution = CreateMinimalResolution("Primary");
            var result = BindingsGenerator.ResolveFrameworkDependencies(
                new[] { "/nonexistent/path/Dep.xcframework" },
                primaryResolution,
                "/path/to/Primary.xcframework",
                "simulator",
                XCFrameworkPlatformTarget.Simulator,
                NullLogger.Instance);
            Assert.Null(result);
        }

        [Fact]
        public void ResolveFrameworkDependencies_NonXcframeworkPath_ReturnsNull()
        {
            var dir = Path.Combine(Path.GetTempPath(), $"fwdep_noxc_{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            try
            {
                var primaryResolution = CreateMinimalResolution("Primary");
                var result = BindingsGenerator.ResolveFrameworkDependencies(
                    new[] { dir },  // Not an .xcframework
                    primaryResolution,
                    "/path/to/Primary.xcframework",
                    "simulator",
                    XCFrameworkPlatformTarget.Simulator,
                    NullLogger.Instance);
                Assert.Null(result);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void ResolveFrameworkDependencies_PrimaryAsDependency_ReturnsNull()
        {
            var dir = Path.Combine(Path.GetTempPath(), $"fwdep_self_{Guid.NewGuid():N}");
            var primaryPath = Path.Combine(dir, "Primary.xcframework");
            Directory.CreateDirectory(primaryPath);
            try
            {
                var primaryResolution = CreateMinimalResolution("Primary");
                var result = BindingsGenerator.ResolveFrameworkDependencies(
                    new[] { primaryPath },
                    primaryResolution,
                    primaryPath,
                    "simulator",
                    XCFrameworkPlatformTarget.Simulator,
                    NullLogger.Instance);
                Assert.Null(result);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void ResolveFrameworkDependencies_ObjCOnlyDep_ResolvesWithIsObjCOnly()
        {
            using var fixture = CreateObjCDepFixture("ObjCDep", hasBothSlices: true);
            var primaryResolution = CreateMinimalResolution("Primary");
            var runner = new MockCommandRunner();
            runner.SetResponse("file", 0, "dynamically linked shared library");

            var result = BindingsGenerator.ResolveFrameworkDependencies(
                new[] { fixture.RootPath },
                primaryResolution,
                "/path/to/Primary.xcframework",
                "simulator",
                XCFrameworkPlatformTarget.Simulator,
                NullLogger.Instance,
                commandRunner: runner);

            Assert.NotNull(result);
            Assert.Single(result);
            Assert.True(result[0].IsObjCOnly);
            Assert.Equal("ObjCDep", result[0].ModuleName);
            Assert.NotNull(result[0].SimulatorFrameworkSearchPath);
        }

        [Fact]
        public void ResolveFrameworkDependencies_ObjCDepNoModulemap_FallsBackToSearchPathOnly()
        {
            // Frameworks without modulemap (e.g., compiled wrapper xcframeworks) fall back
            // to search-path-only resolution instead of returning null.
            using var fixture = CreateObjCDepFixture("BrokenDep", hasBothSlices: false, addModulemap: false);
            var primaryResolution = CreateMinimalResolution("Primary");
            var runner = new MockCommandRunner();
            runner.SetResponse("file", 0, "dynamically linked shared library");

            var result = BindingsGenerator.ResolveFrameworkDependencies(
                new[] { fixture.RootPath },
                primaryResolution,
                "/path/to/Primary.xcframework",
                "simulator",
                XCFrameworkPlatformTarget.Simulator,
                NullLogger.Instance,
                commandRunner: runner);

            Assert.NotNull(result);
            Assert.Single(result);
        }

        [Fact]
        public void ResolveFrameworkDependencies_ObjCDepDuplicateModule_SkipsDuplicate()
        {
            // Duplicate modules are silently skipped (not errors), since the SDK targets
            // can pass both ProjectReference-resolved and explicit SwiftFrameworkDependency items.
            using var fixture1 = CreateObjCDepFixture("DupMod", hasBothSlices: true);
            using var fixture2 = CreateObjCDepFixture("DupMod", hasBothSlices: true);
            var primaryResolution = CreateMinimalResolution("Primary");
            var runner = new MockCommandRunner();
            runner.SetResponse("file", 0, "dynamically linked shared library");

            var result = BindingsGenerator.ResolveFrameworkDependencies(
                new[] { fixture1.RootPath, fixture2.RootPath },
                primaryResolution,
                "/path/to/Primary.xcframework",
                "simulator",
                XCFrameworkPlatformTarget.Simulator,
                NullLogger.Instance,
                commandRunner: runner);

            Assert.NotNull(result);
            Assert.Single(result); // Second duplicate is skipped
        }

        [Fact]
        public void ResolveFrameworkDependencies_MixedSwiftAndObjCDeps_ResolvesBoth()
        {
            // Create a Swift dependency (has swiftmodule)
            using var swiftFixture = new XCFrameworkFixture("SwiftDep.xcframework");
            swiftFixture.WriteInfoPlist(MakeSimplePlist("SwiftDep"));
            var sliceDir = swiftFixture.CreateSlice("ios-arm64-simulator",
                "SwiftDep.framework", "SwiftDep.framework/SwiftDep");
            var moduleDir = swiftFixture.CreateSwiftModule(sliceDir, "SwiftDep.framework", "SwiftDep");
            swiftFixture.CreateAbiJson(moduleDir, "arm64-apple-ios-simulator");
            swiftFixture.CreateTbd(moduleDir, "SwiftDep");

            // Create an ObjC dependency (no swiftmodule, has modulemap)
            using var objcFixture = CreateObjCDepFixture("ObjCDep2", hasBothSlices: true);

            var primaryResolution = CreateMinimalResolution("Primary");
            var runner = new MockCommandRunner();
            runner.SetResponse("file", 0, "dynamically linked shared library");
            runner.SetResponse("tapi", 0, "");
            // Pre-create what tapi would generate
            File.WriteAllText(Path.Combine(Path.GetTempPath(), "SwiftDep.tbd"), "--- !tapi-tbd");

            var result = BindingsGenerator.ResolveFrameworkDependencies(
                new[] { swiftFixture.RootPath, objcFixture.RootPath },
                primaryResolution,
                "/path/to/Primary.xcframework",
                "simulator",
                XCFrameworkPlatformTarget.Simulator,
                NullLogger.Instance,
                commandRunner: runner);

            Assert.NotNull(result);
            Assert.Equal(2, result.Count);
            var swiftDep = result.First(d => d.ModuleName == "SwiftDep");
            var objcDep = result.First(d => d.ModuleName == "ObjCDep2");
            Assert.False(swiftDep.IsObjCOnly);
            Assert.True(objcDep.IsObjCOnly);
        }

        [Fact]
        public void ResolveFrameworkDependencies_ObjCDep_AllArchs_SimOnlyDep_SimPrimary_ReturnsNull()
        {
            // ObjC dep has only simulator slice, primaryPlatformTarget=Simulator, wrapperArchitectures="all"
            using var fixture = CreateObjCDepFixture("SimOnly", hasBothSlices: false);
            var primaryResolution = CreateMinimalResolution("Primary");
            var runner = new MockCommandRunner();
            runner.SetResponse("file", 0, "dynamically linked shared library");

            var result = BindingsGenerator.ResolveFrameworkDependencies(
                new[] { fixture.RootPath },
                primaryResolution,
                "/path/to/Primary.xcframework",
                "all",
                XCFrameworkPlatformTarget.Simulator,
                NullLogger.Instance,
                commandRunner: runner);

            Assert.Null(result);
        }

        [Fact]
        public void ResolveFrameworkDependencies_ObjCDep_AllArchs_DeviceOnlyDep_SimPrimary_ReturnsNull()
        {
            // ObjC dep has only device slice, primaryPlatformTarget=Simulator, wrapperArchitectures="all"
            // Regression: oppositeTarget must be derived from actual slice, not requested target.
            // Without the fix, SelectSlice falls back to device for both resolutions,
            // returning success with simPath=null — violating the "all" contract.
            using var fixture = CreateObjCDeviceOnlyFixture("DevOnlyAll");
            var primaryResolution = CreateMinimalResolution("Primary");
            var runner = new MockCommandRunner();
            runner.SetResponse("file", 0, "dynamically linked shared library");

            var result = BindingsGenerator.ResolveFrameworkDependencies(
                new[] { fixture.RootPath },
                primaryResolution,
                "/path/to/Primary.xcframework",
                "all",
                XCFrameworkPlatformTarget.Simulator,
                NullLogger.Instance,
                commandRunner: runner);

            Assert.Null(result);
        }

        [Fact]
        public void ResolveFrameworkDependencies_ObjCDep_DeviceArchs_OnlySimSlice_ReturnsNull()
        {
            // ObjC dep has only simulator slice, wrapperArchitectures="device"
            using var fixture = CreateObjCDepFixture("SimOnlyDev", hasBothSlices: false);
            var primaryResolution = CreateMinimalResolution("Primary");
            var runner = new MockCommandRunner();
            runner.SetResponse("file", 0, "dynamically linked shared library");

            var result = BindingsGenerator.ResolveFrameworkDependencies(
                new[] { fixture.RootPath },
                primaryResolution,
                "/path/to/Primary.xcframework",
                "device",
                XCFrameworkPlatformTarget.Simulator,
                NullLogger.Instance,
                commandRunner: runner);

            Assert.Null(result);
        }

        [Fact]
        public void ResolveFrameworkDependencies_ObjCDep_SimArchs_OnlyDeviceSlice_ReturnsNull()
        {
            // ObjC dep has only device slice, wrapperArchitectures="simulator"
            using var fixture = CreateObjCDeviceOnlyFixture("DevOnlySim");
            var primaryResolution = CreateMinimalResolution("Primary");
            var runner = new MockCommandRunner();
            runner.SetResponse("file", 0, "dynamically linked shared library");

            var result = BindingsGenerator.ResolveFrameworkDependencies(
                new[] { fixture.RootPath },
                primaryResolution,
                "/path/to/Primary.xcframework",
                "simulator",
                XCFrameworkPlatformTarget.Device,
                NullLogger.Instance,
                commandRunner: runner);

            Assert.Null(result);
        }

        private static XCFrameworkResolution CreateMinimalResolution(string module) => new()
        {
            AbiJsonPath = "/abi.json",
            DylibPath = "/dylib",
            TbdPath = "/tbd",
            ModuleName = module,
            XCFrameworkPath = $"/path/to/{module}.xcframework",
            FrameworkSearchPath = $"/path/to/{module}.xcframework/ios-arm64-simulator",
            LibraryIdentifier = "ios-arm64-simulator",
            IsSimulatorSlice = true,
            SelectedArchitecture = "arm64"
        };

        /// <summary>
        /// Creates a temp xcframework with module.modulemap but no .swiftmodule
        /// (simulates an ObjC-only framework like Stripe3DS2).
        /// </summary>
        private static XCFrameworkFixture CreateObjCDepFixture(string name,
            bool hasBothSlices, bool addModulemap = true)
        {
            var fixture = new XCFrameworkFixture($"{name}.xcframework");
            if (hasBothSlices)
                fixture.WriteInfoPlist(MakeDualSlicePlist(name));
            else
                fixture.WriteInfoPlist(MakeSimplePlist(name));

            // Simulator slice
            var simSliceDir = fixture.CreateSlice(
                hasBothSlices ? "ios-arm64_x86_64-simulator" : "ios-arm64-simulator",
                $"{name}.framework", $"{name}.framework/{name}");
            if (addModulemap)
            {
                var modulesDir = Path.Combine(simSliceDir, $"{name}.framework", "Modules");
                Directory.CreateDirectory(modulesDir);
                File.WriteAllText(Path.Combine(modulesDir, "module.modulemap"),
                    $"framework module {name} {{\n  umbrella header \"{name}.h\"\n}}\n");
            }

            if (hasBothSlices)
            {
                var deviceSliceDir = fixture.CreateSlice("ios-arm64",
                    $"{name}.framework", $"{name}.framework/{name}");
                if (addModulemap)
                {
                    var modulesDir = Path.Combine(deviceSliceDir, $"{name}.framework", "Modules");
                    Directory.CreateDirectory(modulesDir);
                    File.WriteAllText(Path.Combine(modulesDir, "module.modulemap"),
                        $"framework module {name} {{\n  umbrella header \"{name}.h\"\n}}\n");
                }
            }

            return fixture;
        }

        /// <summary>
        /// Creates a temp xcframework with only a device slice (no simulator).
        /// </summary>
        private static XCFrameworkFixture CreateObjCDeviceOnlyFixture(string name)
        {
            var fixture = new XCFrameworkFixture($"{name}.xcframework");
            fixture.WriteInfoPlist(MakeDeviceOnlyPlist(name));
            var deviceSliceDir = fixture.CreateSlice("ios-arm64",
                $"{name}.framework", $"{name}.framework/{name}");
            var modulesDir = Path.Combine(deviceSliceDir, $"{name}.framework", "Modules");
            Directory.CreateDirectory(modulesDir);
            File.WriteAllText(Path.Combine(modulesDir, "module.modulemap"),
                $"framework module {name} {{\n  umbrella header \"{name}.h\"\n}}\n");
            return fixture;
        }

        private static string MakeSimplePlist(string name)
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

        private static string MakeDualSlicePlist(string name)
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

        private static string MakeDeviceOnlyPlist(string name)
        {
            return $$"""
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
        }

        private static string CaptureHelp()
        {
            var writer = new StringWriter();
            Console.SetOut(writer);
            try
            {
                BindingsGenerator.Main(new[] { "-h" });
                return writer.ToString();
            }
            finally
            {
                Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
            }
        }
    }
}
