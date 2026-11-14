// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

// Self-contained nullable context so this file compiles identically whether built in the Nuke
// build assembly or link-compiled into the unit-test project.
#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;

/// <summary>
/// What one lane's run proved, handed to the seed so a Debug .NET 11 floor is written only from a
/// run that proved it.
/// </summary>
/// <param name="ProvenLane">The execution lane every class of the run proved, or null when the run
/// proved none: the macOS lane has no lane-identity check, and a run in which no class started has
/// no evidence.</param>
/// <param name="InventoryClasses">The app's test classes.</param>
/// <param name="ControlGrades">Each negative control that ran, by class, with the problems its
/// grade found (empty when it behaved as expected).</param>
public sealed record RuntimeIdentitySeedProof(
    RuntimeExecutionLane? ProvenLane,
    IReadOnlyCollection<string> InventoryClasses,
    IReadOnlyDictionary<string, IReadOnlyList<string>> ControlGrades);

/// <summary>
/// When a .NET 11 Debug lane, which runs the CoreCLR interpreter, may seed its runtime-identity
/// floor. The run-level preconditions (a full, green run of a fresh or receipt-verified app, inside
/// the net11 profile) are <see cref="RuntimeTestLane.IdentitySeedRefusal"/> and the seed itself;
/// this adds what only the run can show.
/// </summary>
/// <remarks>
/// <para>The interpreter has known defects, which the generated bindings route around. A Debug
/// floor is only as trustworthy as the fence around those defects, so a Debug seed needs all of:</para>
/// <list type="bullet">
///   <item><description>Lane identity proven Interpreter for every class that ran.</description></item>
///   <item><description>Every registered negative control present in the app's inventory, run in
///   its isolated process, and graded as expected. A control that vanished from the app, never ran,
///   or behaved otherwise refuses, and an empty registry refuses, so the fence cannot pass by
///   being absent.</description></item>
///   <item><description>No lowering against the lane's previous Debug floor or its Release sibling
///   (see <see cref="Lowerings"/>) unless the run carries a justification, which is written into the
///   entry it explains.</description></item>
/// </list>
/// <para>The seed checks all of this before it writes anything. The controls run after the suite,
/// and the run fails on a control problem only after the suite is reported, so the seed reads the
/// grades from this proof rather than relying on that later failure.</para>
/// </remarks>
public static class RuntimeIdentitySeedPolicy
{
    /// <summary>Why this run's proof does not qualify the lane's floor; empty when it does.</summary>
    /// <param name="buildConfiguration">The configuration the lane built. Only Debug, the
    /// interpreter lane, needs run proof; Release floors keep the run-level preconditions.</param>
    /// <param name="registeredControls">The negative controls registered for the interpreter lane.</param>
    public static IReadOnlyList<string> ProofRefusals(string buildConfiguration, RuntimeIdentitySeedProof? proof,
        IReadOnlyList<NegativeControlClass> registeredControls)
    {
        if (buildConfiguration != "Debug")
            return [];
        var refusals = new List<string>();
        if (proof?.ProvenLane is not { } lane)
            refusals.Add("the run did not prove its execution lane, so it cannot show the CoreCLR interpreter ran");
        else if (lane != RuntimeExecutionLane.Interpreter)
            refusals.Add($"a Debug floor qualifies the CoreCLR interpreter, but the run proved {lane}");
        if (registeredControls.Count == 0)
            refusals.Add("no negative control is registered for the interpreter lane, so nothing shows its known defects are fenced");
        foreach (var control in registeredControls)
        {
            if (proof == null)
                refusals.Add($"{control.ClassName} ({control.Defect}) has no recorded grade");
            else if (!proof.InventoryClasses.Contains(control.ClassName))
                refusals.Add($"{control.ClassName} ({control.Defect}) is registered but is not a test class of the app");
            else if (!proof.ControlGrades.TryGetValue(control.ClassName, out var problems))
                refusals.Add($"{control.ClassName} ({control.Defect}) did not run");
            else if (problems.Count > 0)
                refusals.Add($"{control.ClassName} ({control.Defect}) did not behave as expected: {string.Join("; ", problems)}");
        }
        return refusals;
    }

    /// <summary>
    /// How a Debug seed lowers the floor, each as a reason; empty when it lowers nothing.
    /// </summary>
    /// <param name="seed">The entry the run would write.</param>
    /// <param name="previous">The lane's current Debug floor, or null when it has none.</param>
    /// <param name="release">The lane's Release floor, or null when it has none.</param>
    /// <param name="isolatedControlTests">The tests the Debug lane runs as isolated negative controls.
    /// They are outside the Debug count, while the Release lane runs and passes them as ordinary
    /// tests, so they are added back before the two counts are compared.</param>
    public static IReadOnlyList<string> Lowerings(RuntimeIdentityBaseline.PlatformIdentities seed,
        RuntimeIdentityBaseline.PlatformIdentities? previous, RuntimeIdentityBaseline.PlatformIdentities? release,
        int isolatedControlTests)
    {
        var lowerings = new List<string>();
        if (previous != null && seed.PassCount < previous.PassCount)
            lowerings.Add($"the pass count drops from the previous Debug floor's {previous.PassCount} to {seed.PassCount}");
        if (release != null && seed.PassCount + isolatedControlTests < release.PassCount)
            lowerings.Add($"the pass count with the {isolatedControlTests} isolated control test(s) added back " +
                          $"({seed.PassCount + isolatedControlTests}) is below the Release floor's {release.PassCount}");
        foreach (var (reference, name) in new[] { (previous, "the previous Debug floor"), (release, "the Release floor") })
        {
            if (reference == null)
                continue;
            var known = reference.Skips.Select(s => (s.Class, s.Method)).ToHashSet();
            foreach (var skip in seed.Skips.Where(s => !known.Contains((s.Class, s.Method))))
                lowerings.Add($"{skip.Class}.{skip.Method} skips ('{skip.Reason}') but is not a skip of {name}");
        }
        return lowerings;
    }
}
