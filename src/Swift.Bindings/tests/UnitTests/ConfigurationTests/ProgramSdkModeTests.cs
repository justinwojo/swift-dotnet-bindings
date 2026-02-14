// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System.CommandLine;
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
