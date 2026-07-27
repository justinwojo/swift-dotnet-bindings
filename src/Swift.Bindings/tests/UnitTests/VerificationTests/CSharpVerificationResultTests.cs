// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System.Collections.Generic;
using Xunit;

namespace BindingsGeneration.Tests
{
    /// <summary>
    /// The classifier that both verification legs (in-process probe and real-build SARIF) share.
    /// This is the contract the downstream attribution/recovery pass depends on: a CS#### error
    /// means "the emitted C# does not compile" and fails publication; an NU/MSB-only failure is
    /// inconclusive (the binding is not at fault); a clean build is clean.
    /// </summary>
    public class CSharpVerificationResultTests
    {
        private static CSharpCompileDiagnostic Err(string id, string? file = "A.cs", int line = 1, int col = 1)
            => new(id, CSharpDiagnosticSeverity.Error, file, line, col, line, col + 1, $"{id} message");

        private static CSharpCompileDiagnostic Warn(string id, string? file = "A.cs", int line = 1, int col = 1)
            => new(id, CSharpDiagnosticSeverity.Warning, file, line, col, line, col + 1, $"{id} message");

        [Fact]
        public void FromDiagnostics_CompilerError_IsCompileErrors_EvenWhenExitZero()
        {
            // A CS error is authoritative regardless of the reported build success flag.
            var result = CSharpVerificationResult.FromDiagnostics(
                new[] { Err("CS0246") }, buildSucceeded: true);

            Assert.Equal(CSharpVerificationOutcome.CompileErrors, result.Outcome);
            Assert.Single(result.CompilerErrors);
            Assert.Equal("CS0246", result.CompilerErrors[0].Id);
        }

        [Fact]
        public void FromDiagnostics_RestoreErrorOnly_FailedBuild_IsInconclusive()
        {
            // NuGet restore failed before the compiler ran: the C# was never checked, so a healthy
            // binding must not be reported as a compile failure.
            var result = CSharpVerificationResult.FromDiagnostics(
                new[] { Err("NU1101", file: null, line: 0, col: 0) }, buildSucceeded: false);

            Assert.Equal(CSharpVerificationOutcome.Inconclusive, result.Outcome);
            Assert.Empty(result.CompilerErrors);
            Assert.NotNull(result.InconclusiveReason);
            Assert.Contains("NU1101", result.InconclusiveReason!);
        }

        [Fact]
        public void FromDiagnostics_MsbuildErrorOnly_FailedBuild_IsInconclusive()
        {
            var result = CSharpVerificationResult.FromDiagnostics(
                new[] { Err("MSB3644", file: null, line: 0, col: 0) }, buildSucceeded: false);

            Assert.Equal(CSharpVerificationOutcome.Inconclusive, result.Outcome);
            Assert.Contains("MSB3644", result.InconclusiveReason!);
        }

        [Fact]
        public void FromDiagnostics_NoDiagnostics_SucceededBuild_IsClean()
        {
            var result = CSharpVerificationResult.FromDiagnostics(
                new List<CSharpCompileDiagnostic>(), buildSucceeded: true);

            Assert.Equal(CSharpVerificationOutcome.Clean, result.Outcome);
            Assert.Empty(result.CompilerErrors);
        }

        [Fact]
        public void FromDiagnostics_WarningsOnly_SucceededBuild_IsClean()
        {
            // Warnings do not fail the gate — the generated csproj does not treat warnings as errors.
            var result = CSharpVerificationResult.FromDiagnostics(
                new[] { Warn("CS0169"), Warn("CA1420") }, buildSucceeded: true);

            Assert.Equal(CSharpVerificationOutcome.Clean, result.Outcome);
            Assert.Empty(result.CompilerErrors);
        }

        [Fact]
        public void FromDiagnostics_FailedBuild_NoDiagnostics_IsInconclusiveNotClean()
        {
            // A non-zero exit with nothing parsed is NOT "clean" — the verifier could not prove
            // the C# compiled, so it must not green-light publication.
            var result = CSharpVerificationResult.FromDiagnostics(
                new List<CSharpCompileDiagnostic>(), buildSucceeded: false);

            Assert.Equal(CSharpVerificationOutcome.Inconclusive, result.Outcome);
        }

        [Fact]
        public void IsCompilerError_ClassifiesByIdPrefixAndSeverity()
        {
            Assert.True(Err("CS0246").IsCompilerError);
            Assert.False(Warn("CS0169").IsCompilerError);      // a CS warning is not a compile failure
            Assert.False(Err("NU1101").IsCompilerError);       // restore error is not a C# compile error
            Assert.True(Err("NU1101").IsRestoreOrInfrastructure);
            Assert.True(Err("MSB3644").IsRestoreOrInfrastructure);
            Assert.False(Err("CS0246").IsRestoreOrInfrastructure);
        }

        [Fact]
        public void IsCompilerError_SourceGeneratorError_CountsAsCompileFailure()
        {
            // The LibraryImport generator refuses to expand a stub whose parameter it cannot
            // marshal and reports SYSLIB1051 — with no CS error of its own unless a call site
            // happens to break too. Classifying that as infrastructure made a provably
            // uncompilable binding read as "inconclusive", which passes through.
            Assert.True(Err("SYSLIB1051").IsCompilerError);
            Assert.False(Err("SYSLIB1051").IsRestoreOrInfrastructure);
            Assert.False(Warn("SYSLIB1054").IsCompilerError);  // a generator warning is not a failure
        }

        [Fact]
        public void FromDiagnostics_SourceGeneratorErrorOnly_FailedBuild_IsCompileErrors()
        {
            // The whole point of the classification: this must be recoverable (withdraw the
            // offending member), not inconclusive (ship it unverified).
            var result = CSharpVerificationResult.FromDiagnostics(
                new[] { Err("SYSLIB1051") }, buildSucceeded: false);

            Assert.Equal(CSharpVerificationOutcome.CompileErrors, result.Outcome);
            Assert.Single(result.CompilerErrors);
            Assert.Equal("SYSLIB1051", result.CompilerErrors[0].Id);
            Assert.Null(result.InconclusiveReason);
        }

        [Fact]
        public void CompilerErrors_AreDeterministicallyOrdered_ByFileLineColumnId()
        {
            // Fed out of order; the shape contract requires a stable order so downstream attribution
            // and test assertions are reproducible.
            var result = CSharpVerificationResult.FromDiagnostics(new[]
            {
                Err("CS0111", file: "B.cs", line: 5, col: 2),
                Err("CS0246", file: "A.cs", line: 9, col: 1),
                Err("CS0103", file: "A.cs", line: 9, col: 1),
                Err("CS0246", file: "A.cs", line: 2, col: 1),
            }, buildSucceeded: false);

            var order = new List<(string, int, int, string)>();
            foreach (var e in result.CompilerErrors)
                order.Add((e.FilePath ?? "", e.Line, e.Column, e.Id));

            Assert.Equal(new[]
            {
                ("A.cs", 2, 1, "CS0246"),
                ("A.cs", 9, 1, "CS0103"),
                ("A.cs", 9, 1, "CS0246"),
                ("B.cs", 5, 2, "CS0111"),
            }, order);
        }
    }
}
