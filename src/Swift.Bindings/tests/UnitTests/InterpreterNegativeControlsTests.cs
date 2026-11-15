// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// The interpreter lane's negative controls: every test of a control class has an explicit expected
/// outcome, and a run is graded against it so that a probe which stops reproducing its defect, fails
/// for another reason, or takes the process down is reported rather than counted.
/// </summary>
public class InterpreterNegativeControlsTests
{
    private static NegativeControlClass Probe => InterpreterNegativeControls.Arm64.Find("SretSelfProbeTests")!;

    private static IReadOnlyCollection<string> ProbeMethods => Probe.Tests.Select(t => t.TestName).ToList();

    private static List<string> TestMethodsInSource(string className)
    {
        var path = Directory.GetFiles(Path.Combine(LocateRepoRoot(), "BindingTests", "RuntimeTestsApp"),
            className + ".cs", SearchOption.AllDirectories).Single();
        return Regex.Matches(File.ReadAllText(path), @"public\s+(?:unsafe\s+)?(?:async\s+)?(?:void|Task)\s+(Test\w+)\s*\(")
            .Select(m => m.Groups[1].Value).Order(StringComparer.Ordinal).ToList();
    }

    public static TheoryData<InterpreterArchitecture> Architectures => [InterpreterArchitecture.Arm64, InterpreterArchitecture.X64];

    [Theory]
    [MemberData(nameof(Architectures))]
    public void Registry_EveryTestOfAControlClassHasAnExpectedOutcome(InterpreterArchitecture architecture)
    {
        foreach (var control in InterpreterNegativeControls.For(architecture).Classes)
        {
            var methods = TestMethodsInSource(control.ClassName);

            Assert.NotEmpty(methods);
            Assert.Equal(methods, control.Tests.Select(t => t.TestName).Order(StringComparer.Ordinal).ToList());
            // A class none of whose tests is expected to fail reproduces no defect: it is an ordinary
            // class and leaves the registry.
            Assert.Contains(control.Tests, t => t.Status == "fail");
            Assert.All(control.Tests.Where(t => t.Status == "fail"), t => Assert.False(string.IsNullOrEmpty(t.ErrorContains)));
        }
    }

    [Theory]
    [MemberData(nameof(Architectures))]
    public void Registry_EveryExcludedClassListsExactlyItsTests(InterpreterArchitecture architecture)
    {
        foreach (var exclusion in InterpreterNegativeControls.For(architecture).Excluded)
        {
            Assert.Equal(TestMethodsInSource(exclusion.ClassName), exclusion.Tests.Order(StringComparer.Ordinal).ToList());
            Assert.False(string.IsNullOrWhiteSpace(exclusion.Reason));
        }
    }

    [Theory]
    [InlineData("iossimulator-arm64", InterpreterArchitecture.Arm64)]
    [InlineData("tvossimulator-arm64", InterpreterArchitecture.Arm64)]
    [InlineData("ios-arm64", InterpreterArchitecture.Arm64)]
    [InlineData("maccatalyst-arm64", InterpreterArchitecture.Arm64)]
    [InlineData("maccatalyst-x64", InterpreterArchitecture.X64)]
    public void For_SelectsTheSetByTheLanesArchitecture(string rid, InterpreterArchitecture expected)
        => Assert.Equal(expected, InterpreterNegativeControls.For(rid).Architecture);

    [Fact]
    public void For_RefusesARuntimeIdentifierWithNoArchitecture()
        => Assert.Throws<ArgumentException>(() => InterpreterNegativeControls.For("maccatalyst"));

    [Fact]
    public void Arm64_KeepsTheSretSelfProbeAsItsOnlyControlAndExcludesNothing()
    {
        var set = InterpreterNegativeControls.Arm64;
        Assert.Equal(["SretSelfProbeTests"], set.Classes.Select(c => c.ClassName));
        Assert.Empty(set.Excluded);
        Assert.Equal(4, set.TestsOutsideSuite);
        Assert.Equal(
            ["TestCombineDirectHeapBuffers:fail", "TestCombineDirectStackBuffers:fail", "TestCombineViaCdeclControl:pass", "TestFactoryMakeDirectCallConvSwiftSretPlusSelf:pass"],
            Probe.Tests.Select(t => $"{t.TestName}:{t.Status}"));
    }

    [Fact]
    public void X64_ExcludesTheFatalSretSelfProbeAndObservesStructLoweringInstead()
    {
        var set = InterpreterNegativeControls.X64;
        var exclusion = Assert.Single(set.Excluded);
        Assert.Equal("SretSelfProbeTests", exclusion.ClassName);
        Assert.Contains("fatal", exclusion.Reason);
        // The fatal probe is not also run as a control: it would take the process down.
        Assert.DoesNotContain(set.Classes, c => c.ClassName == "SretSelfProbeTests");

        var control = Assert.Single(set.Classes);
        Assert.Equal("LoweredSignatureProbeTests", control.ClassName);
        var fail = Assert.Single(control.Tests, t => t.Status == "fail");
        Assert.Equal("TestLoweredStructReachesCallee", fail.TestName);
        Assert.Equal(InterpreterNegativeControls.StructNotLowered, fail.ErrorContains);
        Assert.Equal(7, set.TestsOutsideSuite);
    }

    [Fact]
    public void X64_ARunThatLosesTheStructLoweringIsAsExpected()
    {
        var control = InterpreterNegativeControls.X64.Find("LoweredSignatureProbeTests")!;
        var results = Run(new()
        {
            ["TestLoweredStructReachesCallee"] = ("fail", "AssertionException: lowered struct: Expected 7042301, got 4402012160"),
            ["TestSwiftConventionIntegerArgumentsArrive"] = ("pass", null),
            ["TestLoweredStructViaCdeclControl"] = ("pass", null),
        }, className: control.ClassName);

        Assert.Empty(InterpreterNegativeControls.Grade(control, control.Tests.Select(t => t.TestName).ToList(), results, TestResult.Failure));
    }

    [Fact]
    public void X64_ACrashingStructLoweringProbeIsAProblem()
    {
        var control = InterpreterNegativeControls.X64.Find("LoweredSignatureProbeTests")!;
        Assert.Contains(InterpreterNegativeControls.Grade(control, control.Tests.Select(t => t.TestName).ToList(), null, TestResult.Crash),
            p => p.Contains("take the process down", StringComparison.Ordinal));
    }

    [Fact]
    public void GradeExclusion_MatchesTheInventory()
    {
        var exclusion = Assert.Single(InterpreterNegativeControls.X64.Excluded);
        Assert.Empty(InterpreterNegativeControls.GradeExclusion(exclusion, exclusion.Tests));
    }

    [Fact]
    public void GradeExclusion_ReportsATestAddedToTheClassAndOneRemoved()
    {
        var exclusion = Assert.Single(InterpreterNegativeControls.X64.Excluded);
        var methods = exclusion.Tests.Skip(1).Append("TestNewShape").ToList();
        var problems = InterpreterNegativeControls.GradeExclusion(exclusion, methods);
        Assert.Contains(problems, p => p.Contains("TestNewShape is not listed in the exclusion", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains($"{exclusion.Tests[0]} is listed in the exclusion but is not a test of the app", StringComparison.Ordinal));
    }

    [Fact]
    public void GradeExclusion_ReportsAClassRemovedFromTheApp()
        => Assert.Contains("is not a test class of the app",
            Assert.Single(InterpreterNegativeControls.GradeExclusion(Assert.Single(InterpreterNegativeControls.X64.Excluded), [])));

    [Fact]
    public void Grade_ARunThatReproducesTheDefectAndPassesItsControlsIsAsExpected()
    {
        var problems = InterpreterNegativeControls.Grade(Probe, ProbeMethods, Run(Expected()), TestResult.Failure);

        Assert.Empty(problems);
    }

    [Fact]
    public void Grade_AProbeThatPassesNoLongerReproducesTheDefect()
    {
        var rows = Expected();
        rows["TestCombineDirectHeapBuffers"] = ("pass", null);

        var problem = Assert.Single(InterpreterNegativeControls.Grade(Probe, ProbeMethods, Run(rows), TestResult.Failure));

        Assert.Contains("no longer reproduces", problem);
    }

    [Fact]
    public void Grade_AProbeThatFailsForAnotherReasonIsNotTheDefect()
    {
        var rows = Expected();
        rows["TestCombineDirectStackBuffers"] = ("fail", "stack sret a = 1 + 10: Expected 11, got -1");

        var problem = Assert.Single(InterpreterNegativeControls.Grade(Probe, ProbeMethods, Run(rows), TestResult.Failure));

        Assert.Contains("not as", problem);
    }

    [Fact]
    public void Grade_AFailingControlIsAProblem()
    {
        var rows = Expected();
        rows["TestCombineViaCdeclControl"] = ("fail", "cdecl out a = 1 + 10: Expected 11, got 0");

        var problem = Assert.Single(InterpreterNegativeControls.Grade(Probe, ProbeMethods, Run(rows), TestResult.Failure));

        Assert.Contains("TestCombineViaCdeclControl recorded fail, expected pass", problem);
    }

    [Theory]
    [InlineData(TestResult.Crash)]
    [InlineData(TestResult.Timeout)]
    public void Grade_AProcessThatDiesIsAProblemEvenWhenTheRowsMatch(TestResult launch)
    {
        var problems = InterpreterNegativeControls.Grade(Probe, ProbeMethods, Run(Expected()), launch);

        Assert.Contains(problems, p => p.Contains(launch.ToString(), StringComparison.Ordinal));
    }

    [Fact]
    public void Grade_NoResultsOrAMissingTestIsAProblem()
    {
        Assert.Contains(InterpreterNegativeControls.Grade(Probe, ProbeMethods, null, TestResult.Failure),
            p => p.Contains("no results", StringComparison.Ordinal));

        var rows = Expected();
        rows.Remove("TestFactoryMakeDirectCallConvSwiftSretPlusSelf");
        Assert.Contains(InterpreterNegativeControls.Grade(Probe, ProbeMethods, Run(rows), TestResult.Failure),
            p => p.Contains("TestFactoryMakeDirectCallConvSwiftSretPlusSelf did not run", StringComparison.Ordinal));
    }

    [Fact]
    public void Grade_ATestWithoutAnExpectedOutcomeIsAProblem()
    {
        var methods = ProbeMethods.Append("TestSomethingNew").ToList();
        var rows = Expected();
        rows["TestSomethingNew"] = ("pass", null);

        var problems = InterpreterNegativeControls.Grade(Probe, methods, Run(rows), TestResult.Failure);

        Assert.Contains(problems, p => p.Contains("TestSomethingNew has no expected outcome", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("", "Test Suite")]
    [InlineData("", "SretSelfProbeTests")]
    [InlineData("SomeOtherTests", "TestSomething")]
    public void Grade_AFailureOutsideTheExpectedTestsIsAProblem(string className, string testName)
    {
        var problems = InterpreterNegativeControls.Grade(Probe, ProbeMethods,
            Run(Expected(), extra: (className, testName, "unexpected")), TestResult.Failure);

        var problem = Assert.Single(problems);
        Assert.Contains($"{testName} recorded fail but has no expected outcome: unexpected", problem);
    }

    [Fact]
    public void Grade_AClassThatDidNotCompleteIsAProblem()
    {
        var problems = InterpreterNegativeControls.Grade(Probe, ProbeMethods, Run(Expected(), completed: false), TestResult.Failure);

        Assert.Contains(problems, p => p.Contains("did not complete", StringComparison.Ordinal));
    }

    private static Dictionary<string, (string Status, string? Error)> Expected() => new()
    {
        ["TestCombineDirectHeapBuffers"] = ("fail", "AssertionException: heap self a mutated: Expected 11, got 4294967307"),
        ["TestCombineDirectStackBuffers"] = ("fail", "AssertionException: stack self a mutated: Expected 11, got 4294967307"),
        ["TestCombineViaCdeclControl"] = ("pass", null),
        ["TestFactoryMakeDirectCallConvSwiftSretPlusSelf"] = ("pass", null),
    };

    private static JsonlTestResults Run(Dictionary<string, (string Status, string? Error)> rows, bool completed = true,
        (string Class, string Test, string Error)? extra = null, string? className = null)
    {
        className ??= Probe.ClassName;
        var lines = new List<string> { new JsonObject { ["run_token"] = "t" }.ToJsonString() };
        foreach (var (test, (status, error)) in rows)
        {
            var row = new JsonObject { ["class"] = className, ["test"] = test, ["status"] = status, ["ms"] = 1 };
            if (error != null)
                row["error"] = error;
            lines.Add(row.ToJsonString());
        }
        if (completed)
            lines.Add(new JsonObject { ["class_done"] = className, ["tests_run"] = rows.Count }.ToJsonString());
        if (extra is { } e)
            lines.Add(new JsonObject { ["class"] = e.Class, ["test"] = e.Test, ["status"] = "fail", ["error"] = e.Error, ["ms"] = 1 }.ToJsonString());
        return JsonlTestResults.Parse(string.Join("\n", lines));
    }

    private static string LocateRepoRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "build", "Build.RuntimeTests.cs"))) return d.FullName;
        throw new InvalidOperationException("Could not locate the repository root.");
    }
}
