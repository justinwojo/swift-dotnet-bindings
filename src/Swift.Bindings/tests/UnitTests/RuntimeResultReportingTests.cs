// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Execute the actual Nuke reporting method with a filesystem baseline boundary.
/// The comparison stub either overwrites both scratch baseline files or throws;
/// neither action is permitted before a product-failure verdict. No real baseline is opened.
/// </summary>
public class RuntimeResultReportingTests
{
    private static readonly Lazy<Func<string, int, string[], bool, bool, string>> Report = new(CompileReporter);
    private const string Prefix = "{\"run_token\":\"prefix\"}\n" +
        "{\"class\":\"CompletedTests\",\"test\":\"TestPass\",\"status\":\"pass\"}\n";
    private const string Complete = Prefix + "{\"done\":true,\"total\":1,\"passed\":1,\"failed\":0,\"skipped\":0}\n";

    [Theory]
    [InlineData(TestResult.Failure, false, "Runtime tests failed (Simulator)")]
    [InlineData(TestResult.Failure, true, "Runtime tests failed (Simulator)")]
    [InlineData(TestResult.Crash, false, "Runtime tests crashed (Simulator)")]
    [InlineData(TestResult.Crash, true, "Runtime tests crashed (Simulator)")]
    [InlineData(TestResult.Timeout, false, "Runtime tests timed out (Simulator)")]
    [InlineData(TestResult.Timeout, true, "Runtime tests timed out (Simulator)")]
    [InlineData(TestResult.LaunchFailure, false, "Runtime tests launch failure (Simulator)")]
    public void NonSuccess_NeverComparesOrWritesBaselines(TestResult verdict, bool complete, string message)
    {
        AssertNoComparison(verdict, new[] { complete ? Complete : Prefix }, message);
    }

    [Fact]
    public void StickyFailure_WithLaterCompletePassingAggregate_CannotWriteOrMaskFailure()
    {
        InScratch(directory =>
        {
            var attempts = new RuntimeTestAttempts(Path.Combine(directory, "attempts"));
            var output = "[TEST] --- InterruptedTests.TestFirst ---\nUnhandled exception. InvalidOperationException";
            var first = new LaunchResult(LaunchDiagnostics.ClassifyFinalOutput(TestResult.LaunchFailure, output), output, 1, null);
            Assert.Equal(TestResult.Failure, first.Result);
            attempts.RecordLaunch(0, Guid.NewGuid().ToString("N"), first);
            var later = new LaunchResult(TestResult.Success, "TEST SUCCESS", 0, null, true);
            attempts.RecordLaunch(1, Guid.NewGuid().ToString("N"), later);
            var final = attempts.FinalResult(later);
            Assert.Equal(TestResult.Failure, final.Result);
            // No invented JSONL failure row: first attempt has only a token; later
            // completed JSONL supplies passes and Done. The sticky console failure wins.
            AssertNoComparison(final.Result,
                new[] { "{\"run_token\":\"first\"}\n", Complete }, "Runtime tests failed (Simulator)");
        });
    }

    [Theory]
    [InlineData("fail")]
    [InlineData("crash")]
    public void SuccessWithRecordedFailure_IsRejectedBeforeComparison(string status)
    {
        var row = "{\"class\":\"ObservedTests\",\"test\":\"TestObserved\",\"status\":\"" + status + "\"}\n";
        AssertNoComparison(TestResult.Success, new[] { Complete + row }, "Runtime tests failed (Simulator)");
    }

    [Fact]
    public void SuccessWithoutJsonl_StrictFailureNeverCompares()
        => AssertNoComparison(TestResult.Success, null, "Runtime tests failed (Simulator)");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EffectiveSuccess_ReachesTheActualComparisonBoundary(bool throwAtBoundary)
    {
        InScratch(directory =>
        {
            var message = Report.Value(directory, (int)TestResult.Success, new[] { Complete }, throwAtBoundary, false);
            Assert.Equal(throwAtBoundary ? "BASELINE_BOUNDARY_FAILURE" : "", message);
            foreach (var name in BaselineNames)
                Assert.Equal(throwAtBoundary ? "original" : "comparison reached", File.ReadAllText(Path.Combine(directory, name)));
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SuccessWithIncompleteJsonl_CannotWriteEvenWhenPermissive(bool permissive)
    {
        foreach (var throwAtBoundary in new[] { false, true })
            InScratch(directory =>
            {
                Assert.Equal("Runtime tests failed (Simulator)",
                    Report.Value(directory, (int)TestResult.Success, new[] { Prefix }, throwAtBoundary, permissive));
                foreach (var name in BaselineNames)
                    Assert.Equal("original", File.ReadAllText(Path.Combine(directory, name)));
            });
    }

    [Fact]
    public void PermissiveSuccessWithoutJsonl_PreservesExplorationWithoutBaselineWrites()
    {
        InScratch(directory =>
        {
            Assert.Equal("", Report.Value(directory, (int)TestResult.Success, null, true, true));
            foreach (var name in BaselineNames)
                Assert.Equal("original", File.ReadAllText(Path.Combine(directory, name)));
        });
    }

    [Fact]
    public void CompletedFailureWithAnAppFailureRow_KeepsItsProductVerdict()
    {
        var failure = "{\"class\":\"ObservedTests\",\"test\":\"TestFail\",\"status\":\"fail\"}\n";
        AssertNoComparison(TestResult.Failure, new[] { Complete + failure }, "Runtime tests failed (Simulator)");
    }

    private static readonly string[] BaselineNames = { "validation-baseline.json", "runtime-identity-baseline.json" };

    private static void AssertNoComparison(TestResult result, string[] jsonl, string expectedMessage)
    {
        foreach (var throwAtBoundary in new[] { false, true })
            InScratch(directory =>
            {
                Assert.Equal(expectedMessage, Report.Value(directory, (int)result, jsonl, throwAtBoundary, false));
                foreach (var name in BaselineNames)
                    Assert.Equal("original", File.ReadAllText(Path.Combine(directory, name)));
            });
    }

    private static void InScratch(Action<string> action)
    {
        var directory = Path.Combine(Path.GetTempPath(), "runtime-reporting-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            foreach (var name in BaselineNames) File.WriteAllText(Path.Combine(directory, name), "original");
            action(directory);
        }
        finally { Directory.Delete(directory, true); }
    }

    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "Test-only dynamically compiled production method, retained in full.")]
    [UnconditionalSuppressMessage("Trimming", "IL2075", Justification = "Test-only dynamically compiled fixture entry, retained in full.")]
    private static Func<string, int, string[], bool, bool, string> CompileReporter()
    {
        var root = LocateRepoRoot();
        var production = CSharpSyntaxTree.ParseText(File.ReadAllText(Path.Combine(root, "build", "Build.RuntimeTests.cs")))
            .GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>()
            .Single(m => m.Identifier.Text == "ReportRuntimeTestResult");
        // No copied verdict switch: the entire current production method is inserted verbatim.
        // The only replaced authorities are external Log/grid and the baseline IO boundary.
        var fixture = $$"""
            #nullable enable
            using System;
            using System.IO;
            using System.Linq;
            public class ReporterFixture {
                bool AbiGrid => false;
                bool Permissive;
                string directory = "";
                bool throwAtBoundary;
                static class Log {
                    public static void Information(string text, params object[] args) { }
                    public static void Warning(string text, params object[] args) { }
                    public static void Error(string text, params object[] args) { }
                }
                void StashAbiGridResults(string platform, JsonlTestResults? results) { }
                void CompareRuntimeBaseline(string platform, JsonlTestResults results) {
                    if (throwAtBoundary) throw new Exception("BASELINE_BOUNDARY_FAILURE");
                    foreach (var name in new[] { "validation-baseline.json", "runtime-identity-baseline.json" })
                        File.WriteAllText(Path.Combine(directory, name), "comparison reached");
                }
                {{production}}
                public static string Run(string directory, int verdict, string[]? attempts, bool throwAtBoundary, bool permissive) {
                    var fixture = new ReporterFixture { directory = directory, throwAtBoundary = throwAtBoundary, Permissive = permissive };
                    JsonlTestResults? merged = null;
                    if (attempts != null) {
                        merged = new JsonlTestResults();
                        foreach (var jsonl in attempts) merged.Merge(JsonlTestResults.Parse(jsonl));
                    }
                    try {
                        fixture.ReportRuntimeTestResult(new LaunchResult((TestResult)verdict, "", null, null), "Simulator", merged);
                        return "";
                    } catch (Exception error) { return error.Message; }
                }
            }
            """;
        var trees = new[] { "TestResult.cs", "JsonlTestResults.cs", "TestClassInventory.cs" }
            .Select(name => CSharpSyntaxTree.ParseText(File.ReadAllText(Path.Combine(root, "build", "Models", name))))
            .Append(CSharpSyntaxTree.ParseText(fixture));
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES"))
            .Split(Path.PathSeparator).Select(p => MetadataReference.CreateFromFile(p));
        var compilation = CSharpCompilation.Create("RuntimeReporting" + Guid.NewGuid().ToString("N"), trees, references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var stream = new MemoryStream();
        var emitted = compilation.Emit(stream);
        Assert.True(emitted.Success, string.Join("\n", emitted.Diagnostics));
        return Assembly.Load(stream.ToArray()).GetType("ReporterFixture").GetMethod("Run")
            .CreateDelegate<Func<string, int, string[], bool, bool, string>>();
    }

    private static string LocateRepoRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "build", "Build.RuntimeTests.cs"))) return d.FullName;
        throw new InvalidOperationException("Could not locate the production runtime reporter source.");
    }
}
