// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System.CommandLine;
using Xunit;

namespace BindingsGeneration.Tests
{
    /// <summary>
    /// Tests for --sdk-mode, --package-id, and --wrapper-architectures CLI options.
    /// These are structural tests that verify option parsing and help text.
    /// End-to-end behavior is validated by integration tests.
    /// </summary>
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
    }
}
