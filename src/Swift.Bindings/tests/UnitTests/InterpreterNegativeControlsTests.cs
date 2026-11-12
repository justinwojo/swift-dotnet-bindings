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
    private static NegativeControlClass Probe => InterpreterNegativeControls.Find("SretSelfProbeTests")!;

    private static IReadOnlyCollection<string> ProbeMethods => Probe.Tests.Select(t => t.TestName).ToList();

    [Fact]
    public void Registry_EveryTestOfAControlClassHasAnExpectedOutcome()
    {
        foreach (var control in InterpreterNegativeControls.Classes)
        {
            var path = Directory.GetFiles(Path.Combine(LocateRepoRoot(), "BindingTests", "RuntimeTestsApp"),
                control.ClassName + ".cs", SearchOption.AllDirectories).Single();
            var methods = Regex.Matches(File.ReadAllText(path), @"public\s+(?:unsafe\s+)?(?:async\s+)?(?:void|Task)\s+(Test\w+)\s*\(")
                .Select(m => m.Groups[1].Value).Order(StringComparer.Ordinal).ToList();

            Assert.NotEmpty(methods);
            Assert.Equal(methods, control.Tests.Select(t => t.TestName).Order(StringComparer.Ordinal).ToList());
            // A class none of whose tests is expected to fail reproduces no defect: it is an ordinary
            // class and leaves the registry.
            Assert.Contains(control.Tests, t => t.Status == "fail");
            Assert.All(control.Tests.Where(t => t.Status == "fail"), t => Assert.False(string.IsNullOrEmpty(t.ErrorContains)));
        }
    }

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
        (string Class, string Test, string Error)? extra = null)
    {
        var lines = new List<string> { new JsonObject { ["run_token"] = "t" }.ToJsonString() };
        foreach (var (test, (status, error)) in rows)
        {
            var row = new JsonObject { ["class"] = Probe.ClassName, ["test"] = test, ["status"] = status, ["ms"] = 1 };
            if (error != null)
                row["error"] = error;
            lines.Add(row.ToJsonString());
        }
        if (completed)
            lines.Add(new JsonObject { ["class_done"] = Probe.ClassName, ["tests_run"] = rows.Count }.ToJsonString());
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
