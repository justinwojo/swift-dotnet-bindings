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
    private delegate string Reporter(string directory, int verdict, string[] attempts, bool throwAtBoundary,
        bool permissive, string framework = "net10.0", bool coreClr = false, string output = "",
        string platform = "Simulator", bool harnessStallProbe = false, string stacksFile = null);
    private static readonly Lazy<Reporter> Report = new(CompileReporter);
    private const string CoreClrBanner = "Runtime flavor: IsMonoRuntime=False, IsNativeAotRuntime=False, " +
        "Framework=.NETCoreApp,Version=v11.0, Runtime=.NET 11";
    private const string Prefix = "{\"run_token\":\"prefix\"}\n" +
        "{\"class\":\"CompletedTests\",\"test\":\"TestPass\",\"status\":\"pass\"}\n";
    private const string Complete = Prefix + "{\"done\":true,\"total\":1,\"passed\":1,\"failed\":0,\"skipped\":0}\n";

    [Theory]
    [InlineData(TestResult.Failure, false, "Runtime tests failed (Simulator)")]
    [InlineData(TestResult.Failure, true, "Runtime tests failed (Simulator)")]
    [InlineData(TestResult.Crash, false, "Runtime tests crashed (Simulator)")]
    [InlineData(TestResult.Crash, true, "Runtime tests crashed (Simulator)")]
    [InlineData(TestResult.Timeout, false, "Runtime tests timed out (Simulator); no hung-process stacks were taken")]
    [InlineData(TestResult.Timeout, true, "Runtime tests timed out (Simulator); no hung-process stacks were taken")]
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

    [Theory]
    [InlineData("")]
    [InlineData("Runtime flavor: IsMonoRuntime=True, IsNativeAotRuntime=False, Framework=.NETCoreApp,Version=v11.0, Runtime=.NET 11")]
    [InlineData("Runtime flavor: IsMonoRuntime=False, IsNativeAotRuntime=True, Framework=.NETCoreApp,Version=v11.0, Runtime=.NET 11")]
    [InlineData("Runtime flavor: IsMonoRuntime=False, IsNativeAotRuntime=False, Framework=.NETCoreApp,Version=v10.0, Runtime=.NET 10")]
    public void Net11_WrongOrMissingLiveIdentityNeverReachesBaselineBoundary(string output)
    {
        InScratch(directory =>
        {
            var error = Report.Value(directory, (int)TestResult.Success, new[] { Complete }, true, false,
                framework: "net11.0", coreClr: true, output: output, platform: "Device/CoreCLR");
            Assert.Contains("Wrong or missing live .NET 11 coreclr identity", error);
            foreach (var name in BaselineNames)
                Assert.Equal("original", File.ReadAllText(Path.Combine(directory, name)));
        });
    }

    [Fact]
    public void Net11_CorrectLiveIdentityReachesBaselineBoundary()
    {
        InScratch(directory =>
        {
            Assert.Equal("BASELINE_BOUNDARY_FAILURE",
                Report.Value(directory, (int)TestResult.Success, new[] { Complete }, true, false,
                    framework: "net11.0", coreClr: true, output: CoreClrBanner, platform: "Device/CoreCLR"));
            foreach (var name in BaselineNames)
                Assert.Equal("original", File.ReadAllText(Path.Combine(directory, name)));
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Net11_MissingStructuredResultsFailsEvenWhenPermissive(bool permissive)
    {
        InScratch(directory =>
        {
            Assert.Equal("Runtime tests failed (Device/CoreCLR)",
                Report.Value(directory, (int)TestResult.Success, null, true, permissive,
                    framework: "net11.0", coreClr: true, output: CoreClrBanner, platform: "Device/CoreCLR"));
            foreach (var name in BaselineNames)
                Assert.Equal("original", File.ReadAllText(Path.Combine(directory, name)));
        });
    }

    [Theory]
    [InlineData(TestResult.Timeout, "/attempts/00/hung-stacks-1.txt",
        "Runtime tests timed out (Simulator); hung-process stacks (App, pid 42; #1 captured): /attempts/00/hung-stacks-1.txt")]
    [InlineData(TestResult.Crash, "/attempts/00/hung-stacks-1.txt",
        "Runtime tests crashed (Simulator); hung-process stacks (App, pid 42; #1 captured): /attempts/00/hung-stacks-1.txt")]
    [InlineData(TestResult.Crash, null, "Runtime tests crashed (Simulator)")]
    public void StalledLaunch_NamesItsStacksInTheFailure(TestResult verdict, string stacksFile, string message)
    {
        foreach (var throwAtBoundary in new[] { false, true })
            InScratch(directory =>
            {
                Assert.Equal(message, Report.Value(directory, (int)verdict, new[] { Prefix }, throwAtBoundary, false,
                    stacksFile: stacksFile));
                foreach (var name in BaselineNames)
                    Assert.Equal("original", File.ReadAllText(Path.Combine(directory, name)));
            });
    }

    // The stall probe proves the capture only through a stall: a run that passed (every test of
    // the filtered class skipped), failed or crashed instead fails as an unexercised probe.
    [Theory]
    [InlineData(TestResult.Success, true)]
    [InlineData(TestResult.Failure, false)]
    [InlineData(TestResult.Crash, false)]
    [InlineData(TestResult.LaunchFailure, false)]
    public void HarnessStallProbe_FailsUnlessTheRunStalled(TestResult verdict, bool complete)
    {
        foreach (var throwAtBoundary in new[] { false, true })
            InScratch(directory =>
            {
                var message = Report.Value(directory, (int)verdict, new[] { complete ? Complete : Prefix },
                    throwAtBoundary, false, harnessStallProbe: true);
                Assert.StartsWith("--harness-stall-probe: the Simulator run ended as ", message);
                Assert.Contains("not as a stall", message);
                foreach (var name in BaselineNames)
                    Assert.Equal("original", File.ReadAllText(Path.Combine(directory, name)));
            });
    }

    // A probe app that crashes while it is being sampled still fails the probe, and the failure
    // names the stacks taken before the crash rather than calling the capture unexercised.
    [Fact]
    public void HarnessStallProbe_CrashDuringCaptureNamesTheStacks()
        => InScratch(directory => Assert.Equal(
            "--harness-stall-probe: the Simulator run ended as Crash after its stacks were taken, not as a stall; " +
            "hung-process stacks (App, pid 42; #1 captured): /s.txt",
            Report.Value(directory, (int)TestResult.Crash, new[] { Prefix }, false, false,
                harnessStallProbe: true, stacksFile: "/s.txt")));

    [Fact]
    public void HarnessStallProbe_StallKeepsTheTimeoutFailure()
        => InScratch(directory => Assert.Equal(
            "Runtime tests timed out (Simulator); hung-process stacks (App, pid 42; #1 captured): /s.txt",
            Report.Value(directory, (int)TestResult.Timeout, new[] { Prefix }, false, false,
                harnessStallProbe: true, stacksFile: "/s.txt")));

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
    private static Reporter CompileReporter()
    {
        var root = LocateRepoRoot();
        var methods = CSharpSyntaxTree.ParseText(File.ReadAllText(Path.Combine(root, "build", "Build.RuntimeTests.cs")))
            .GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>().ToList();
        var production = methods.Single(m => m.Identifier.Text == "ReportRuntimeTestResult");
        var assertLiveLane = methods.Single(m => m.Identifier.Text == "AssertLiveRuntimeLane");
        // No copied verdict switch: the entire current production method is inserted verbatim.
        // The live identity gate and lane policy also come from production sources. The only
        // replaced authorities are external Log/grid and the terminal baseline IO boundary.
        var fixture = $$"""
            #nullable enable
            using System;
            using System.IO;
            using System.Linq;
            public class ReporterFixture {
                bool AbiGrid => false;
                bool Permissive;
                bool HarnessStallProbe;
                bool MonoAot => false;
                RuntimeTestLane RuntimeLane = RuntimeTestLane.Create(null, null, null, false, false, false);
                string BtFramework => RuntimeLane.Framework;
                string directory = "";
                bool throwAtBoundary;
                static class Log {
                    public static void Information(string text, params object[] args) { }
                    public static void Warning(string text, params object[] args) { }
                    public static void Error(string text, params object[] args) { }
                }
                void StashAbiGridResults(string platform, JsonlTestResults? results) { }
                void CompareRuntimeBaseline(string platform, JsonlTestResults results, RuntimeIdentitySeedProof? seedProof) {
                    if (throwAtBoundary) throw new Exception("BASELINE_BOUNDARY_FAILURE");
                    foreach (var name in new[] { "validation-baseline.json", "runtime-identity-baseline.json" })
                        File.WriteAllText(Path.Combine(directory, name), "comparison reached");
                }
                {{assertLiveLane}}
                {{production}}
                public static string Run(string directory, int verdict, string[]? attempts, bool throwAtBoundary,
                    bool permissive, string framework, bool coreClr, string output, string platform,
                    bool harnessStallProbe, string? stacksFile) {
                    var fixture = new ReporterFixture {
                        HarnessStallProbe = harnessStallProbe,
                        directory = directory, throwAtBoundary = throwAtBoundary, Permissive = permissive,
                        RuntimeLane = RuntimeTestLane.Create(framework, null, null, coreClr,
                            platform.StartsWith("Device/", StringComparison.Ordinal), false)
                    };
                    JsonlTestResults? merged = null;
                    if (attempts != null) {
                        merged = new JsonlTestResults();
                        foreach (var jsonl in attempts) merged.Merge(JsonlTestResults.Parse(jsonl));
                    }
                    try {
                        var stacks = stacksFile == null ? null
                            : new HungProcessStacks("App", 42, new[] { new StackSample(1, DateTimeOffset.Now, TimeSpan.Zero,
                                "lldb", HungProcessSampler.OutcomeCaptured, "thread #1") }) { Files = new[] { stacksFile } };
                        fixture.ReportRuntimeTestResult(new LaunchResult((TestResult)verdict, output, null, null,
                            HungStacks: stacks), platform, merged);
                        return "";
                    } catch (Exception error) { return error.Message; }
                }
            }
            """;
        var trees = new[] { "TestResult.cs", "JsonlTestResults.cs", "TestClassInventory.cs",
            "RuntimeTestLane.cs", "DeviceRuntimeFlavorGate.cs", "LaunchDiagnostics.cs", "HungProcessStacks.cs" }
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
            .CreateDelegate<Reporter>();
    }

    private static string LocateRepoRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "build", "Build.RuntimeTests.cs"))) return d.FullName;
        throw new InvalidOperationException("Could not locate the production runtime reporter source.");
    }
}
