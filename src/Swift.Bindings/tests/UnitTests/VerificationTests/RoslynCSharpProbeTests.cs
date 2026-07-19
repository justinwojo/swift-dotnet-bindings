// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace BindingsGeneration.Tests
{
    /// <summary>
    /// The in-process Roslyn probe (the acceleration heuristic). These tests compile against the
    /// running runtime's BCL so they are deterministic and independent of installed Apple workloads;
    /// the renders use only <c>System</c> types for the same reason. The probe's job is exercised at
    /// its contract boundary: clean input is clean, a planted error reports the exact id and 1-based
    /// span, suppression is honoured, and two runs are byte-identical.
    /// </summary>
    public class RoslynCSharpProbeTests
    {
        private static CSharpProbeReferenceSet BclReferences()
        {
            var tpa = (string)(AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? "");
            var paths = tpa
                .Split(Path.PathSeparator)
                .Where(p => !string.IsNullOrEmpty(p) &&
                            p.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) &&
                            File.Exists(p))
                .ToList();
            Assert.NotEmpty(paths); // the test host is a .NET runtime; the BCL must resolve
            return CSharpProbeReferenceSet.ForTesting(paths);
        }

        // The interop generator is not needed for System-only renders, so leave it off to keep the
        // probe result a function of the render alone.
        private static readonly RoslynCSharpProbeOptions NoInterop =
            new() { RunInteropGenerator = false };

        private const string CleanRender =
            "namespace Probe;\n" +
            "public class Widget\n" +
            "{\n" +
            "    public int Value => 42;\n" +
            "    public string Name => \"hi\";\n" +
            "}\n";

        // DoesNotExistType is undefined -> CS0246. Deliberate layout: line 4, column 12 (four spaces
        // + "public " = 11 chars, so the 'D' is the 12th column, 1-based).
        private const string PlantedErrorRender =
            "namespace Probe;\n" +
            "public class Widget\n" +
            "{\n" +
            "    public DoesNotExistType Field;\n" +
            "}\n";

        [Fact]
        public void Probe_CleanRender_IsClean()
        {
            var result = RoslynCSharpProbe.Probe(
                new Dictionary<string, string> { ["Widget.cs"] = CleanRender },
                BclReferences(),
                NoInterop);

            Assert.Equal(CSharpVerificationOutcome.Clean, result.Outcome);
            Assert.Empty(result.CompilerErrors);
        }

        [Fact]
        public void Probe_PlantedError_ReportsExactIdFileAndSpan()
        {
            var result = RoslynCSharpProbe.Probe(
                new Dictionary<string, string> { ["Widget.cs"] = PlantedErrorRender },
                BclReferences(),
                NoInterop);

            Assert.Equal(CSharpVerificationOutcome.CompileErrors, result.Outcome);
            var cs0246 = Assert.Single(result.CompilerErrors, d => d.Id == "CS0246");
            Assert.Equal("Widget.cs", cs0246.FilePath);
            Assert.Equal(4, cs0246.Line);
            Assert.Equal(12, cs0246.Column);
            Assert.Equal(4, cs0246.EndLine);
            Assert.Equal(28, cs0246.EndColumn); // "DoesNotExistType" is 16 chars: 12 + 16 = 28
            Assert.Equal(CSharpDiagnosticSeverity.Error, cs0246.Severity);
        }

        [Fact]
        public void Probe_Suppression_HonorsNoWarnIds_AndIsLoadBearing()
        {
            // A private, never-used field emits CS0169. The generated csproj's NoWarn suppresses it,
            // so the default options must drop it — and, to prove the suppression is doing the work,
            // the same render with an empty suppression set must surface it.
            const string unusedFieldRender =
                "namespace Probe;\n" +
                "public class Widget\n" +
                "{\n" +
                "    private int _unused;\n" +
                "    public int Value => 42;\n" +
                "}\n";
            var files = new Dictionary<string, string> { ["Widget.cs"] = unusedFieldRender };
            var refs = BclReferences();

            var suppressed = RoslynCSharpProbe.Probe(files, refs); // defaults suppress CS0169
            Assert.DoesNotContain(suppressed.Diagnostics, d => d.Id == "CS0169");

            var unsuppressed = RoslynCSharpProbe.Probe(files, refs,
                new RoslynCSharpProbeOptions
                {
                    RunInteropGenerator = false,
                    SuppressedDiagnosticIds = Array.Empty<string>(),
                });
            Assert.Contains(unsuppressed.Diagnostics, d => d.Id == "CS0169");
        }

        [Fact]
        public void Probe_IsDeterministic_TwoRunsIdentical()
        {
            var files = new Dictionary<string, string>
            {
                ["B.cs"] = PlantedErrorRender,
                ["A.cs"] = CleanRender,
            };
            var refs = BclReferences();

            var first = RoslynCSharpProbe.Probe(files, refs, NoInterop);
            var second = RoslynCSharpProbe.Probe(files, refs, NoInterop);

            Assert.Equal(first.Outcome, second.Outcome);
            // Record equality over the full ordered diagnostic list — same ids, spans, order.
            Assert.Equal(first.Diagnostics, second.Diagnostics);
        }

        [Fact]
        public void Probe_OnlyCompilesCsFiles_IgnoresNonSource()
        {
            // A non-.cs entry (e.g. a stray .targets fragment) must not be parsed as C#.
            var result = RoslynCSharpProbe.Probe(
                new Dictionary<string, string>
                {
                    ["Widget.cs"] = CleanRender,
                    ["Widget.targets"] = "<Project><NotCSharp/></Project>",
                },
                BclReferences(),
                NoInterop);

            Assert.Equal(CSharpVerificationOutcome.Clean, result.Outcome);
        }
    }
}
