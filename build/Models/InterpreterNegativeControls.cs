// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

// Self-contained nullable context so this file compiles identically whether built in the Nuke
// build assembly or link-compiled into the unit-test project.
#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;

/// <summary>What one test of a negative-control class must do on the interpreter lane.</summary>
/// <param name="TestName">The test method.</param>
/// <param name="Status">The JSONL status it must record: <c>pass</c> or <c>fail</c>.</param>
/// <param name="ErrorContains">For an expected failure, text its error must carry, so a failure for
/// any other reason is not mistaken for the defect the probe reproduces.</param>
public sealed record NegativeControlExpectation(string TestName, string Status, string? ErrorContains = null);

/// <summary>
/// A RuntimeTestsApp class that makes a raw Swift-convention transition on purpose, to reproduce a
/// runtime defect the generated bindings route around. On an interpreter lane the recovering suite
/// runs (simulator, tvOS simulator, device) it runs alone, in its own process after the suite, and
/// is graded against <see cref="Tests"/>; it is not counted in the suite's verdict. JIT, Mono and
/// NativeAOT lanes run it as an ordinary class, where each test passes.
/// </summary>
/// <param name="Defect">The upstream defect the expected failures reproduce.</param>
public sealed record NegativeControlClass(string ClassName, string Defect, IReadOnlyList<NegativeControlExpectation> Tests);

/// <summary>The interpreter lane's negative-control classes and how a run of one is graded.</summary>
public static class InterpreterNegativeControls
{
    /// <summary>
    /// The text an interpreter failure of the direct sret + self probes carries: the callee's
    /// write through self does not land (dotnet/runtime#136521), whatever word is left there.
    /// </summary>
    public const string SelfNotMutated = "self a mutated: Expected 11, got ";

    public static IReadOnlyList<NegativeControlClass> Classes { get; } =
    [
        new("SretSelfProbeTests", "dotnet/runtime#136521",
        [
            new("TestCombineDirectHeapBuffers", "fail", SelfNotMutated),
            new("TestCombineDirectStackBuffers", "fail", SelfNotMutated),
            // The C-convention control of the same computation.
            new("TestCombineViaCdeclControl", "pass"),
            // Self and an indirect result with no arguments: the interpreter places these.
            new("TestFactoryMakeDirectCallConvSwiftSretPlusSelf", "pass"),
        ]),
    ];

    public static NegativeControlClass? Find(string className) =>
        Classes.FirstOrDefault(c => string.Equals(c.ClassName, className, StringComparison.Ordinal));

    /// <summary>
    /// Why a run of <paramref name="control"/> does not meet its expectations; empty when it does.
    /// </summary>
    /// <param name="inventoryMethods">The class's test methods as the app's inventory lists them.</param>
    /// <param name="results">The run's JSONL results, or null when none were retrieved.</param>
    /// <param name="launch">How the run's process ended.</param>
    public static IReadOnlyList<string> Grade(NegativeControlClass control, IReadOnlyCollection<string> inventoryMethods,
        JsonlTestResults? results, TestResult launch)
    {
        var problems = new List<string>();
        var expected = control.Tests.Select(t => t.TestName).ToHashSet(StringComparer.Ordinal);
        foreach (var method in inventoryMethods.Where(m => !expected.Contains(m)))
            problems.Add($"{control.ClassName}.{method} has no expected outcome; add one");
        foreach (var name in expected.Where(e => !inventoryMethods.Contains(e)))
            problems.Add($"{control.ClassName}.{name} has an expected outcome but is not a test of the app");

        if (launch is TestResult.Crash or TestResult.Timeout or TestResult.LaunchFailure)
            problems.Add($"{control.ClassName}: the process ended with {launch}; a probe must fail by its assertions, not take the process down");
        if (results is null)
        {
            problems.Add($"{control.ClassName}: no results were retrieved");
            return problems;
        }

        // The process runs this class alone, so every row it records is one of the expected tests: a
        // row for anything else (an unexpected method, a class-level or suite-level failure) is a problem.
        var rows = results.Tests.Where(t => t.ClassName == control.ClassName && expected.Contains(t.TestName)).ToList();
        foreach (var row in results.Tests.Except(rows))
            problems.Add($"{(row.ClassName.Length == 0 ? "" : row.ClassName + ".")}{row.TestName} recorded {row.Status} but has no expected outcome{(row.Error is { } error ? ": " + error : "")}");
        foreach (var expectation in control.Tests)
        {
            var row = rows.LastOrDefault(r => r.TestName == expectation.TestName);
            var name = $"{control.ClassName}.{expectation.TestName}";
            if (row is null)
                problems.Add($"{name} did not run");
            else if (row.Status == expectation.Status)
            {
                if (expectation.ErrorContains is { } text && !(row.Error ?? "").Contains(text, StringComparison.Ordinal))
                    problems.Add($"{name} failed, but not as {control.Defect} does (expected \"{text}\"): {row.Error}");
            }
            else if (expectation.Status == "fail" && row.Status == "pass")
                problems.Add($"{name} passed: {control.Defect} no longer reproduces on this lane; confirm the fix and change the expectation to pass, or remove the class from the registry once no expected failure remains");
            else
                problems.Add($"{name} recorded {row.Status}, expected {expectation.Status}{(row.Error is { } e ? ": " + e : "")}");
        }
        if (!results.CompletedClasses.Contains(control.ClassName) && launch is not (TestResult.Crash or TestResult.Timeout))
            problems.Add($"{control.ClassName}: the class did not complete");
        return problems;
    }
}
