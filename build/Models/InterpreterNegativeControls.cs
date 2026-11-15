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

/// <summary>The instruction set an interpreter lane runs, which decides its negative controls.</summary>
public enum InterpreterArchitecture { Arm64, X64 }

/// <summary>
/// A test class an interpreter lane does not run at all, because its probes are fatal on that engine:
/// they reproduce a defect by taking the process down rather than by failing an assertion. The
/// lane passes it to the app as an excluded class; every other lane runs it.
/// </summary>
/// <param name="Reason">Why the class is fatal on this engine.</param>
/// <param name="Tests">The class's test methods, which must match the app's inventory so a test
/// added to the class is not silently dropped from the lane.</param>
public sealed record NegativeControlExclusion(string ClassName, string Reason, IReadOnlyList<string> Tests);

/// <summary>One interpreter architecture's negative controls and the classes it excludes.</summary>
public sealed record InterpreterControlSet(
    InterpreterArchitecture Architecture,
    IReadOnlyList<NegativeControlClass> Classes,
    IReadOnlyList<NegativeControlExclusion> Excluded)
{
    /// <summary>Tests the lane runs apart (isolated controls) or not at all (exclusions): they are
    /// outside a Debug suite's count, while the Release lane runs and passes them as ordinary tests.</summary>
    public int TestsOutsideSuite => Classes.Sum(c => c.Tests.Count) + Excluded.Sum(e => e.Tests.Count);

    public NegativeControlClass? Find(string className) =>
        Classes.FirstOrDefault(c => string.Equals(c.ClassName, className, StringComparison.Ordinal));
}

/// <summary>The interpreter lane's negative-control classes and how a run of one is graded.</summary>
/// <remarks>
/// <para>These controls are the fence that lets a Debug .NET 11 lane earn a runtime-identity floor:
/// a Debug seed requires every class registered for the lane's architecture to be in the app, to
/// run, and to grade as expected, and it refuses when that registry is empty
/// (<see cref="RuntimeIdentitySeedPolicy"/>).</para>
/// <para>Each interpreter architecture has its own set, chosen from the lane's runtime identifier
/// (<see cref="For(string)"/>), because the engines' defects differ. The arm64 interpreter lowers the
/// Swift convention but loses a write through self when an indirect result and arguments travel with
/// it (dotnet/runtime#136521). The x86_64 interpreter applies no Swift lowering at all, so the same
/// probes write through an unset self register and kill the process; that lane excludes them and
/// observes the missing lowering through a struct argument the Swift convention passes in registers,
/// which arrives as C passes it and only produces a wrong integer. A self-register probe would not
/// do: the arm64 interpreter and Mono mishandle the self register too, and the control must pass on
/// every lane that runs it as an ordinary class.</para>
/// <para>When the upstream defect a control reproduces is fixed, its expected failures start
/// passing, the grade reports each one, and every interpreter run of that architecture fails until
/// the registry changes. Confirm the fix on every interpreter lane of the architecture and change
/// those expectations to <c>pass</c>. A class with no expected failure left reproduces no defect, so
/// it leaves the set and runs as an ordinary class: the Debug counts then include its tests, so
/// reseed the Debug floors in the same commit. If that empties a set, every Debug seed of that
/// architecture refuses until <see cref="RuntimeIdentitySeedPolicy.ProofRefusals(string, RuntimeIdentitySeedProof?, InterpreterControlSet)"/>
/// is changed, in that commit, to name what fences the interpreter lane instead, or to retire the
/// requirement deliberately once nothing is left to fence.</para>
/// </remarks>
public static class InterpreterNegativeControls
{
    /// <summary>
    /// The text an interpreter failure of the direct sret + self probes carries: the callee's
    /// write through self does not land (dotnet/runtime#136521), whatever word is left there.
    /// </summary>
    public const string SelfNotMutated = "self a mutated: Expected 11, got ";

    /// <summary>
    /// The text an x86_64 interpreter failure of the struct-lowering probe carries: the callee read
    /// the three registers the Swift convention assigns the struct, and the engine did not put the
    /// fields there.
    /// </summary>
    public const string StructNotLowered = "lowered struct: Expected 7042301, got ";

    /// <summary>The defect the x86_64 interpreter's control observes.</summary>
    public const string X64NoSwiftLowering = "x86_64 CoreCLR interpreter applies no Swift lowering (a struct the Swift convention passes in registers arrives as C passes it)";

    static readonly NegativeControlClass SretSelfProbe = new("SretSelfProbeTests", "dotnet/runtime#136521",
    [
        new("TestCombineDirectHeapBuffers", "fail", SelfNotMutated),
        new("TestCombineDirectStackBuffers", "fail", SelfNotMutated),
        // The C-convention control of the same computation.
        new("TestCombineViaCdeclControl", "pass"),
        // Self and an indirect result with no arguments: the interpreter places these.
        new("TestFactoryMakeDirectCallConvSwiftSretPlusSelf", "pass"),
    ]);

    public static InterpreterControlSet Arm64 { get; } = new(InterpreterArchitecture.Arm64, [SretSelfProbe], []);

    public static InterpreterControlSet X64 { get; } = new(InterpreterArchitecture.X64,
    [
        new("LoweredSignatureProbeTests", X64NoSwiftLowering,
        [
            new("TestLoweredStructReachesCallee", "fail", StructNotLowered),
            // Integer arguments place as C does, so the call itself works.
            new("TestSwiftConventionIntegerArgumentsArrive", "pass"),
            // The C-convention control of the same callee.
            new("TestLoweredStructViaCdeclControl", "pass"),
        ]),
    ],
    [
        new(SretSelfProbe.ClassName,
            "fatal on the x86_64 CoreCLR interpreter: with no Swift lowering the callee writes through an unset self register (r13) and the process dies (SIGBUS) before any assertion runs",
            SretSelfProbe.Tests.Select(t => t.TestName).ToList()),
    ]);

    /// <summary>Every architecture's set.</summary>
    public static IReadOnlyList<InterpreterControlSet> Sets { get; } = [Arm64, X64];

    /// <summary>The interpreter architecture a runtime identifier names.</summary>
    public static InterpreterArchitecture ArchitectureOf(string runtimeIdentifier) =>
        runtimeIdentifier.EndsWith("-arm64", StringComparison.Ordinal) ? InterpreterArchitecture.Arm64
        : runtimeIdentifier.EndsWith("-x64", StringComparison.Ordinal) ? InterpreterArchitecture.X64
        : throw new ArgumentException($"No interpreter architecture for runtime identifier '{runtimeIdentifier}'.", nameof(runtimeIdentifier));

    /// <summary>The negative controls of an interpreter lane running <paramref name="runtimeIdentifier"/>.</summary>
    public static InterpreterControlSet For(string runtimeIdentifier) => For(ArchitectureOf(runtimeIdentifier));

    public static InterpreterControlSet For(InterpreterArchitecture architecture) =>
        Sets.Single(s => s.Architecture == architecture);

    /// <summary>
    /// Why the app's inventory does not match an exclusion; empty when it does. The lane never runs
    /// the class, so the inventory is the only thing to check.
    /// </summary>
    public static IReadOnlyList<string> GradeExclusion(NegativeControlExclusion exclusion, IReadOnlyCollection<string> inventoryMethods)
    {
        var problems = new List<string>();
        if (inventoryMethods.Count == 0)
        {
            problems.Add($"{exclusion.ClassName} is excluded on this lane but is not a test class of the app");
            return problems;
        }
        foreach (var method in inventoryMethods.Where(m => !exclusion.Tests.Contains(m)))
            problems.Add($"{exclusion.ClassName}.{method} is not listed in the exclusion; add it");
        foreach (var name in exclusion.Tests.Where(t => !inventoryMethods.Contains(t)))
            problems.Add($"{exclusion.ClassName}.{name} is listed in the exclusion but is not a test of the app");
        return problems;
    }

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
                problems.Add($"{name} passed: {control.Defect} no longer reproduces on this lane; confirm the fix on every interpreter lane and change the expectation to pass (see the registry's remarks before removing a class)");
            else
                problems.Add($"{name} recorded {row.Status}, expected {expectation.Status}{(row.Error is { } e ? ": " + e : "")}");
        }
        if (!results.CompletedClasses.Contains(control.ClassName) && launch is not (TestResult.Crash or TestResult.Timeout))
            problems.Add($"{control.ClassName}: the class did not complete");
        return problems;
    }
}
