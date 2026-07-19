// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BindingsGeneration.Tests
{
    /// <summary>
    /// The real-build SARIF verification leg — the publication gate. These tests exercise the SARIF
    /// and console parsers directly, and drive the end-to-end <see cref="MsbuildSarifCSharpVerifier.Verify"/>
    /// with a fake runner that behaves like the real toolchain: it writes the SARIF to the ErrorLog
    /// path the compiler was told to use, then returns an exit code and console text.
    /// </summary>
    public class MsbuildSarifCSharpVerifierTests
    {
        // A runner that mimics csc/MSBuild: it parses the -p:ErrorLog="<path>,version=2.1" argument
        // and writes the supplied SARIF there (as a real compiler would), then returns the supplied
        // exit code and console output. This keeps the end-to-end path — arg construction, SARIF
        // read, console scan, classification — under test without a toolchain.
        private sealed class FakeBuildRunner : ICommandRunner
        {
            private readonly string? _sarif;
            private readonly int _exitCode;
            private readonly string _stdout;
            private readonly string _stderr;

            public string? CapturedArguments { get; private set; }

            public FakeBuildRunner(string? sarif, int exitCode, string stdout = "", string stderr = "")
            {
                _sarif = sarif;
                _exitCode = exitCode;
                _stdout = stdout;
                _stderr = stderr;
            }

            public (int ExitCode, string StdOut, string StdErr) Run(string command, string arguments, int timeoutMs = 30000)
            {
                CapturedArguments = arguments;
                if (_sarif is not null)
                {
                    var m = Regex.Match(arguments, @"-p:ErrorLog=""(?<path>[^""]+?),version=2\.1""");
                    Assert.True(m.Success, "Verify must pass -p:ErrorLog=<path>,version=2.1");
                    File.WriteAllText(m.Groups["path"].Value, _sarif);
                }
                return (_exitCode, _stdout, _stderr);
            }
        }

        private static string SarifV21(string ruleId, string level, string uri, int startLine, int startCol, int endLine, int endCol, string message)
            => $$"""
            {
              "version": "2.1.0",
              "runs": [
                {
                  "results": [
                    {
                      "ruleId": "{{ruleId}}",
                      "level": "{{level}}",
                      "message": { "text": "{{message}}" },
                      "locations": [
                        {
                          "physicalLocation": {
                            "artifactLocation": { "uri": "{{uri}}" },
                            "region": { "startLine": {{startLine}}, "startColumn": {{startCol}}, "endLine": {{endLine}}, "endColumn": {{endCol}} }
                          }
                        }
                      ]
                    }
                  ]
                }
              ]
            }
            """;

        [Fact]
        public void ParseSarif_V21_ExtractsIdSeverityAndOneBasedSpan()
        {
            var sarif = SarifV21("CS0246", "error", "/out/Widget.cs", 4, 12, 4, 28, "The type or namespace name 'X' could not be found");
            var diags = MsbuildSarifCSharpVerifier.ParseSarif(sarif);

            var d = Assert.Single(diags);
            Assert.Equal("CS0246", d.Id);
            Assert.Equal(CSharpDiagnosticSeverity.Error, d.Severity);
            Assert.Equal("/out/Widget.cs", d.FilePath);
            Assert.Equal(4, d.Line);
            Assert.Equal(12, d.Column);
            Assert.Equal(4, d.EndLine);
            Assert.Equal(28, d.EndColumn);
            Assert.Contains("could not be found", d.Message);
        }

        [Fact]
        public void ParseSarif_V10_ResultFileFallback_IsTolerated()
        {
            // Older SARIF shape: message is a bare string and the location is under resultFile.
            const string sarifV1 = """
            {
              "version": "1.0.0",
              "runs": [
                {
                  "results": [
                    {
                      "ruleId": "CS0103",
                      "level": "error",
                      "message": "The name 'foo' does not exist",
                      "locations": [
                        { "resultFile": { "uri": "Types.cs", "region": { "startLine": 7, "startColumn": 3, "endLine": 7, "endColumn": 6 } } }
                      ]
                    }
                  ]
                }
              ]
            }
            """;
            var diags = MsbuildSarifCSharpVerifier.ParseSarif(sarifV1);
            var d = Assert.Single(diags);
            Assert.Equal("CS0103", d.Id);
            Assert.Equal("Types.cs", d.FilePath);
            Assert.Equal(7, d.Line);
            Assert.Equal("The name 'foo' does not exist", d.Message);
        }

        [Fact]
        public void ParseSarif_FileUri_IsNormalizedToLocalPath()
        {
            // csc writes absolute file:// URIs; they must normalize to a plain local path so a
            // SARIF diagnostic and its console twin share one file key.
            var sarif = SarifV21("CS0246", "error", "file:///out/Widget.cs", 4, 12, 4, 28, "type not found");
            var d = Assert.Single(MsbuildSarifCSharpVerifier.ParseSarif(sarif));
            Assert.Equal("/out/Widget.cs", d.FilePath);
        }

        [Fact]
        public void Verify_SameCsError_InSarifAndConsole_DedupsToOne()
        {
            // A real build reports each CS error in BOTH the SARIF (file:// uri) and the console
            // (plain path). Without URI normalization these double-count; the gate must report one.
            var dir = Path.Combine(Path.GetTempPath(), $"sarif_verify_{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            try
            {
                var csproj = Path.Combine(dir, "Widget.Swift.iOS.csproj");
                File.WriteAllText(csproj, "<Project/>");
                var runner = new FakeBuildRunner(
                    SarifV21("CS0246", "error", "file:///out/Widget.cs", 4, 12, 4, 28, "type not found"),
                    exitCode: 1,
                    stdout: "/out/Widget.cs(4,12): error CS0246: type not found [/out/Widget.csproj]\n");

                var result = MsbuildSarifCSharpVerifier.Verify(csproj, runner, logger: NullLogger.Instance);

                Assert.Equal(CSharpVerificationOutcome.CompileErrors, result.Outcome);
                var e = Assert.Single(result.CompilerErrors);
                Assert.Equal("/out/Widget.cs", e.FilePath); // the SARIF entry (with the span) wins
                Assert.Equal(12, e.Column);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void ParseConsoleDiagnostics_ExtractsRestoreErrorsWithoutSpan()
        {
            const string console =
                "  Determining projects to restore...\n" +
                "/repo/Foo.csproj : error NU1101: Unable to find package SwiftBindings.Apple.\n" +
                "error MSB3644: The reference assemblies for net10.0-ios were not found.\n";

            var diags = MsbuildSarifCSharpVerifier.ParseConsoleDiagnostics(console);

            Assert.Contains(diags, d => d.Id == "NU1101" && d.Severity == CSharpDiagnosticSeverity.Error);
            Assert.Contains(diags, d => d.Id == "MSB3644" && d.Severity == CSharpDiagnosticSeverity.Error);
        }

        [Fact]
        public void ParseConsoleDiagnostics_ExtractsCsErrorWithFileSpan()
        {
            const string console =
                "/out/Widget.cs(4,12): error CS0246: The type or namespace name 'X' could not be found [/out/Widget.csproj]\n";

            var d = Assert.Single(MsbuildSarifCSharpVerifier.ParseConsoleDiagnostics(console));
            Assert.Equal("CS0246", d.Id);
            Assert.Equal("/out/Widget.cs", d.FilePath);
            Assert.Equal(4, d.Line);
            Assert.Equal(12, d.Column);
        }

        [Fact]
        public void Verify_CsErrorInSarif_FailedBuild_IsCompileErrors()
        {
            var dir = Path.Combine(Path.GetTempPath(), $"sarif_verify_{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            try
            {
                var csproj = Path.Combine(dir, "Widget.Swift.iOS.csproj");
                File.WriteAllText(csproj, "<Project/>");
                var runner = new FakeBuildRunner(
                    SarifV21("CS0246", "error", "Widget.cs", 4, 12, 4, 28, "type not found"),
                    exitCode: 1);

                var result = MsbuildSarifCSharpVerifier.Verify(
                    csproj, runner, swiftBindingsRepoRoot: "/repo", logger: NullLogger.Instance);

                Assert.Equal(CSharpVerificationOutcome.CompileErrors, result.Outcome);
                var e = Assert.Single(result.CompilerErrors);
                Assert.Equal("CS0246", e.Id);
                Assert.Equal(4, e.Line);
                // The repo root must be threaded into the build so Swift.Runtime resolves in-tree.
                Assert.Contains("-p:SwiftBindingsRepoRoot=\"/repo\"", runner.CapturedArguments);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void Verify_Build_OverridesTreatWarningsAsErrors_ForConsumerParity()
        {
            // The gate measures whether the emitted C# genuinely compiles for a consumer, not whether
            // it satisfies the generator repo's stricter warnings-as-errors policy. A binding generated
            // inside this repo would otherwise inherit a parent Directory.Build.props and escalate a
            // benign workload warning into a publication-failing error, so the build must force the
            // switch off (a command-line property wins over any imported prop).
            var dir = Path.Combine(Path.GetTempPath(), $"sarif_verify_{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            try
            {
                var csproj = Path.Combine(dir, "Widget.Swift.iOS.csproj");
                File.WriteAllText(csproj, "<Project/>");
                const string emptySarif = """{ "version": "2.1.0", "runs": [ { "results": [] } ] }""";
                var runner = new FakeBuildRunner(emptySarif, exitCode: 0);

                MsbuildSarifCSharpVerifier.Verify(csproj, runner, logger: NullLogger.Instance);

                Assert.Contains("-p:TreatWarningsAsErrors=false", runner.CapturedArguments);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void Verify_EmptySarif_ExitZero_IsClean()
        {
            var dir = Path.Combine(Path.GetTempPath(), $"sarif_verify_{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            try
            {
                var csproj = Path.Combine(dir, "Widget.Swift.iOS.csproj");
                File.WriteAllText(csproj, "<Project/>");
                const string emptySarif = """{ "version": "2.1.0", "runs": [ { "results": [] } ] }""";
                var runner = new FakeBuildRunner(emptySarif, exitCode: 0);

                var result = MsbuildSarifCSharpVerifier.Verify(csproj, runner, logger: NullLogger.Instance);

                Assert.Equal(CSharpVerificationOutcome.Clean, result.Outcome);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void Verify_RestoreFailure_NoSarif_IsInconclusive()
        {
            var dir = Path.Combine(Path.GetTempPath(), $"sarif_verify_{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            try
            {
                var csproj = Path.Combine(dir, "Widget.Swift.iOS.csproj");
                File.WriteAllText(csproj, "<Project/>");
                // Restore fails before csc runs: no SARIF is written; only a console NU error.
                var runner = new FakeBuildRunner(
                    sarif: null,
                    exitCode: 1,
                    stdout: "/repo/Widget.csproj : error NU1101: Unable to find package Dep.Swift.iOS.\n");

                var result = MsbuildSarifCSharpVerifier.Verify(csproj, runner, logger: NullLogger.Instance);

                Assert.Equal(CSharpVerificationOutcome.Inconclusive, result.Outcome);
                Assert.Empty(result.CompilerErrors);
                Assert.Contains("NU1101", result.InconclusiveReason!);
            }
            finally { Directory.Delete(dir, true); }
        }
    }
}
