// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System.Diagnostics;
using Xunit;

namespace BindingsGeneration.Tests
{
    /// <summary>
    /// Behavioral tests that verify the actual shell commands and MSBuild patterns
    /// used by Sdk.targets work correctly at runtime — not just that the XML is present.
    /// </summary>
    public class SdkTargetsBehaviorTests : IDisposable
    {
        private readonly string _tempDir;

        public SdkTargetsBehaviorTests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), $"swift-sdk-test-{Guid.NewGuid():N}");
            Directory.CreateDirectory(_tempDir);
        }

        public void Dispose()
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, recursive: true);
        }

        // ── Bug 1: find -type d discovers xcframework directories ──

        [Fact]
        public void FindTypeD_DiscoversXCFrameworkDirectory()
        {
            // Create a directory bundle (what xcframeworks actually are)
            var xcfwDir = Path.Combine(_tempDir, "Nuke.xcframework");
            Directory.CreateDirectory(xcfwDir);

            var result = RunFind(_tempDir);

            Assert.Equal(0, result.ExitCode);
            Assert.Contains("Nuke.xcframework", result.StdOut);
        }

        [Fact]
        public void FindTypeD_IgnoresXCFrameworkFiles()
        {
            // Create a regular file with .xcframework extension (should NOT be found)
            File.WriteAllText(Path.Combine(_tempDir, "Fake.xcframework"), "not a directory");

            var result = RunFind(_tempDir);

            Assert.Equal(0, result.ExitCode);
            Assert.DoesNotContain("Fake.xcframework", result.StdOut);
        }

        [Fact]
        public void FindTypeD_ReturnsEmptyForNoXCFrameworks()
        {
            // Empty directory — find should return nothing (not an error)
            var result = RunFind(_tempDir);

            Assert.Equal(0, result.ExitCode);
            Assert.Empty(result.StdOut.Trim());
        }

        [Fact]
        public void FindTypeD_MaxDepth1_IgnoresNestedXCFrameworks()
        {
            // Nested xcframework directory should NOT be found (maxdepth 1)
            var nested = Path.Combine(_tempDir, "subdir", "Nested.xcframework");
            Directory.CreateDirectory(nested);

            var result = RunFind(_tempDir);

            Assert.Equal(0, result.ExitCode);
            Assert.DoesNotContain("Nested.xcframework", result.StdOut);
        }

        [Fact]
        public void FindTypeD_ReturnsFullPathsForConsoleToMSBuild()
        {
            // MSBuild's ConsoleToMSBuild expects full paths to populate ItemName
            var xcfwDir = Path.Combine(_tempDir, "Library.xcframework");
            Directory.CreateDirectory(xcfwDir);

            var result = RunFind(_tempDir);

            // find returns absolute paths when given an absolute search dir
            Assert.StartsWith("/", result.StdOut.Trim());
            Assert.Contains(_tempDir, result.StdOut);
        }

        [Fact]
        public void FindTypeD_DiscoversMultipleXCFrameworks()
        {
            Directory.CreateDirectory(Path.Combine(_tempDir, "Nuke.xcframework"));
            Directory.CreateDirectory(Path.Combine(_tempDir, "Lottie.xcframework"));
            Directory.CreateDirectory(Path.Combine(_tempDir, "NotAnXCFW")); // should not match

            var result = RunFind(_tempDir);
            var lines = result.StdOut.Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries);

            Assert.Equal(0, result.ExitCode);
            Assert.Equal(2, lines.Length);
            Assert.Contains(lines, l => l.Contains("Nuke.xcframework"));
            Assert.Contains(lines, l => l.Contains("Lottie.xcframework"));
        }

        // ── Bug 3: IntermediateOutputPath resolution ──

        [Fact]
        public void IntermediateOutputPath_EmptyInPropsContext_PopulatedInTargetsContext()
        {
            // Demonstrates WHY _SwiftBindingIntermediateDir must be in .targets, not .props:
            // $(IntermediateOutputPath) is set by Microsoft.NET.Sdk's targets, so it's empty
            // when .props files and the project body are evaluated, but populated when
            // .targets files (like Directory.Build.targets) are evaluated.
            //
            // _PropsDir is defined in the project body (same timing as .props) → empty prefix.
            // _TargetsDir is defined in Directory.Build.targets (after SDK targets) → has obj/.
            var projectContent = """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <_PropsDir>$(IntermediateOutputPath)swift-binding/</_PropsDir>
                  </PropertyGroup>
                </Project>
                """;
            var targetsContent = """
                <Project>
                  <PropertyGroup>
                    <_TargetsDir>$(IntermediateOutputPath)swift-binding/</_TargetsDir>
                  </PropertyGroup>
                </Project>
                """;
            File.WriteAllText(Path.Combine(_tempDir, "Test.csproj"), projectContent);
            File.WriteAllText(Path.Combine(_tempDir, "Directory.Build.targets"), targetsContent);
            // Prevent inheriting repo-level Directory.Build.props/targets
            File.WriteAllText(Path.Combine(_tempDir, "Directory.Build.props"), "<Project />");

            var propsResult = RunDotnet($"msbuild \"{Path.Combine(_tempDir, "Test.csproj")}\" -getProperty:_PropsDir -nologo");
            var targetsResult = RunDotnet($"msbuild \"{Path.Combine(_tempDir, "Test.csproj")}\" -getProperty:_TargetsDir -nologo");

            if (propsResult.ExitCode != 0 || targetsResult.ExitCode != 0)
            {
                // Skip if dotnet msbuild -getProperty isn't available
                return;
            }

            var propsDir = propsResult.StdOut.Trim();
            var targetsDir = targetsResult.StdOut.Trim();

            // Props context: $(IntermediateOutputPath) is empty → just "swift-binding/"
            Assert.Equal("swift-binding/", propsDir);

            // Targets context: $(IntermediateOutputPath) resolved → contains "obj/"
            Assert.Contains("obj", targetsDir);
            Assert.EndsWith("swift-binding/", targetsDir);
        }

        // ── Helpers ──

        /// <summary>
        /// Runs the exact same find command used in Sdk.targets _DiscoverSwiftFrameworks.
        /// </summary>
        private static (int ExitCode, string StdOut, string StdErr) RunFind(string searchDir)
        {
            return RunShell($"find \"{searchDir}\" -maxdepth 1 -type d -name '*.xcframework' 2>/dev/null || true");
        }

        private static (int ExitCode, string StdOut, string StdErr) RunDotnet(string args)
        {
            return RunProcess("dotnet", args);
        }

        private static (int ExitCode, string StdOut, string StdErr) RunShell(string command)
        {
            return RunProcess("/bin/sh", $"-c \"{command.Replace("\"", "\\\"")}\"");
        }

        private static (int ExitCode, string StdOut, string StdErr) RunProcess(string fileName, string arguments)
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var process = Process.Start(psi)!;
            var stdOut = process.StandardOutput.ReadToEnd();
            var stdErr = process.StandardError.ReadToEnd();
            process.WaitForExit(30_000);
            return (process.ExitCode, stdOut, stdErr);
        }
    }
}
