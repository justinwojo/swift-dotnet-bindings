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
    /// Both verification legs — the in-process Roslyn probe and the real-build SARIF leg — must emit
    /// the SAME structured-diagnostic shape for the same compiler error, because the downstream
    /// attribution/recovery pass consumes one shape and must not care which leg produced it. This
    /// test drives the probe on a planted error, then reconstructs the equivalent SARIF a real build
    /// would write for that same error and parses it back, and asserts the two
    /// <see cref="CSharpCompileDiagnostic"/> records are equal field-for-field.
    ///
    /// (The fully independent cross-check — probe output vs a real <c>dotnet build</c> SARIF on a
    /// LibraryImport+unsafe+dependency binding — is the live corpus parity experiment, not a unit
    /// test; this asserts the two parsers converge on one record shape.)
    /// </summary>
    public class CSharpProbeVsSarifAgreementTests
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
            return CSharpProbeReferenceSet.ForTesting(paths);
        }

        [Fact]
        public void ProbeAndSarifLegs_ProduceIdenticalDiagnosticRecord_ForSameError()
        {
            const string render =
                "namespace Probe;\n" +
                "public class Widget\n" +
                "{\n" +
                "    public DoesNotExistType Field;\n" +
                "}\n";

            var probe = RoslynCSharpProbe.Probe(
                new Dictionary<string, string> { ["Widget.cs"] = render },
                BclReferences(),
                new RoslynCSharpProbeOptions { RunInteropGenerator = false });

            Assert.Equal(CSharpVerificationOutcome.CompileErrors, probe.Outcome);
            var probeDiag = Assert.Single(probe.CompilerErrors, d => d.Id == "CS0246");

            // Reconstruct the SARIF the real compiler writes for that exact error (same id/level,
            // same span, same message), then parse it through the gate's SARIF leg.
            var sarif = $$"""
            {
              "version": "2.1.0",
              "runs": [ { "results": [ {
                "ruleId": "{{probeDiag.Id}}",
                "level": "error",
                "message": { "text": {{Json(probeDiag.Message)}} },
                "locations": [ { "physicalLocation": {
                  "artifactLocation": { "uri": "{{probeDiag.FilePath}}" },
                  "region": { "startLine": {{probeDiag.Line}}, "startColumn": {{probeDiag.Column}}, "endLine": {{probeDiag.EndLine}}, "endColumn": {{probeDiag.EndColumn}} }
                } } ]
              } ] } ]
            }
            """;

            var sarifResult = CSharpVerificationResult.FromDiagnostics(
                MsbuildSarifCSharpVerifier.ParseSarif(sarif), buildSucceeded: false);

            Assert.Equal(CSharpVerificationOutcome.CompileErrors, sarifResult.Outcome);
            var sarifDiag = Assert.Single(sarifResult.CompilerErrors, d => d.Id == "CS0246");

            // The agreement: two independent parsers, one identical record (record equality compares
            // every field — id, severity, file, 1-based span, message).
            Assert.Equal(probeDiag, sarifDiag);
        }

        // Minimal JSON string encoder for the message (it can contain quotes/backslashes).
        private static string Json(string s)
        {
            var sb = new System.Text.StringBuilder("\"");
            foreach (var c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default: sb.Append(c); break;
                }
            }
            return sb.Append('"').ToString();
        }
    }
}
