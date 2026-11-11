// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace SwiftBindings.InterpIdentity;

/// <summary>Everything one cell's build and runs recorded about how its managed code executed.</summary>
/// <param name="AppName">The app's assembly and executable name.</param>
/// <param name="BindingAssembly">The generated binding's assembly name.</param>
/// <param name="Callers">One caller per fixture process that recorded frames.</param>
/// <param name="Inventory">The composite image's precompiled bodies.</param>
/// <param name="Crossgen">The crossgen2 command the build ran.</param>
/// <param name="Gaps">Fixtures whose Swift call crossgen2 leaves to the runtime, by fixture name.
/// Under a ReadyToRun claim these must be interpreted, and their method absent from the map.</param>
public sealed record LaneEvidence(
    string AppName, string BindingAssembly, IReadOnlyList<CallerReceipt> Callers,
    ReadyToRunInventory Inventory, Crossgen2Invocation Crossgen,
    IReadOnlyDictionary<string, ReadyToRunGap>? Gaps = null);

/// <summary>The lane the evidence proves, and every reason it disagrees with the claim.</summary>
public sealed record LaneVerdict(ExecutionLane Claimed, ExecutionLane? Derived, IReadOnlyList<string> Problems)
{
    public bool Accepted => Problems.Count == 0 && Derived == Claimed;
}

/// <summary>
/// Decides which lane a cell ran from method-level receipts: the image and symbol that called
/// each Swift entry point, and the composite inventory of precompiled binding bodies. Process-wide
/// flags (for example whether any dynamic code is compiled) are deliberately not consulted: a
/// ReadyToRun app still interprets what crossgen2 did not precompile, so only per-call evidence
/// says which code made a given transition. Under a ReadyToRun claim a fixture with a declared
/// <see cref="ReadyToRunGap"/> must show that gap, and every other fixture must be precompiled.
/// </summary>
public static class LaneClassifier
{
    const string InterpreterImage = "libcoreclr.dylib";
    const string InterpreterStubPrefix = "CallJittedMethod";

    public static LaneVerdict Classify(ExecutionLane claimed, LaneEvidence evidence)
    {
        var problems = new List<string>();
        if (evidence.Callers.Count == 0)
            problems.Add("no fixture recorded a caller frame");

        var gaps = evidence.Gaps ?? new Dictionary<string, ReadyToRunGap>();
        var r2rImage = new Regex($"^{Regex.Escape(evidence.AppName)}\\.r2r(\\.dylib)?$", RegexOptions.CultureInvariant);
        var interpreted = evidence.Callers.Where(IsInterpreterStub).ToList();
        var precompiled = evidence.Callers.Where(c => r2rImage.IsMatch(c.Image)).ToList();
        var other = evidence.Callers.Except(interpreted).Except(precompiled).ToList();
        foreach (var c in other)
            problems.Add($"{c.Fixture}: caller {Describe(c)} is neither the interpreter's call stub nor the {evidence.AppName} composite image");

        var bindingBodies = evidence.Inventory.BodiesFor(evidence.BindingAssembly);
        var bindingRooted = evidence.Crossgen.IsRooted(evidence.BindingAssembly);
        bool InMap(CallerReceipt c) => c.Symbol != null && evidence.Inventory.Contains(c.Symbol);
        bool GapPrecompiled(ReadyToRunGap g) =>
            evidence.Inventory.Contains(ReadyToRunInventory.SymbolPrefix(evidence.BindingAssembly) + g.Method);

        // Under a ReadyToRun claim, a declared gap is the one place an interpreted caller belongs.
        var gapCallers = evidence.Callers.Where(c => gaps.ContainsKey(c.Fixture)).ToList();
        var laneCallers = evidence.Callers.Except(gapCallers).ToList();

        var interpreterEvidence = evidence.Callers.Count > 0 && interpreted.Count == evidence.Callers.Count
            && bindingBodies == 0 && !bindingRooted;
        var r2rEvidence = laneCallers.Count > 0
            && laneCallers.All(c => precompiled.Contains(c) && InMap(c))
            && gapCallers.All(interpreted.Contains) && !gaps.Values.Any(GapPrecompiled)
            && bindingBodies > 0 && bindingRooted;
        ExecutionLane? derived = interpreterEvidence ? ExecutionLane.Interpreter : r2rEvidence ? ExecutionLane.ReadyToRun : null;

        if (claimed == ExecutionLane.Interpreter)
        {
            foreach (var c in precompiled)
                problems.Add($"{c.Fixture}: claimed interpreter, but the caller is precompiled code {Describe(c)}");
            if (bindingBodies > 0)
                problems.Add($"claimed interpreter, but the composite holds {bindingBodies} precompiled {evidence.BindingAssembly} bodies");
            if (bindingRooted)
                problems.Add($"claimed interpreter, but crossgen2 compiled {evidence.BindingAssembly}.dll as a rooted input");
        }
        else
        {
            if (evidence.Callers.Count > 0 && laneCallers.Count == 0)
                problems.Add("claimed ReadyToRun, but every caller is a declared crossgen2 gap, so none proves the lane");
            foreach (var c in laneCallers.Where(interpreted.Contains))
                problems.Add($"{c.Fixture}: claimed ReadyToRun, but the caller is the interpreter's call stub {Describe(c)}");
            foreach (var c in laneCallers.Where(c => precompiled.Contains(c) && !InMap(c)))
                problems.Add($"{c.Fixture}: caller {Describe(c)} is not a precompiled body in the composite map");
            foreach (var c in gapCallers.Where(precompiled.Contains))
                problems.Add($"{c.Fixture}: the declared crossgen2 gap no longer holds: the caller is precompiled code {Describe(c)}");
            foreach (var (fixture, gap) in gaps.Where(g => GapPrecompiled(g.Value)))
                problems.Add($"{fixture}: the declared crossgen2 gap no longer holds: {gap.Method} is precompiled in the composite map");
            if (bindingBodies == 0)
                problems.Add($"claimed ReadyToRun, but the composite holds no {evidence.BindingAssembly} bodies");
            if (!bindingRooted)
                problems.Add($"claimed ReadyToRun, but crossgen2 did not compile {evidence.BindingAssembly}.dll as a rooted input");
        }

        if (problems.Count == 0 && derived != claimed)
            problems.Add($"the evidence does not establish the claimed lane {claimed}");
        return new LaneVerdict(claimed, derived, problems);
    }

    static bool IsInterpreterStub(CallerReceipt c) =>
        c.Image == InterpreterImage && c.Symbol != null && c.Symbol.StartsWith(InterpreterStubPrefix, StringComparison.Ordinal);

    static string Describe(CallerReceipt c) => $"{c.Image}!{c.Symbol ?? "?"}";
}

/// <summary>One fixture's verdict in one cell.</summary>
public sealed record FixtureVerdict(IdentityFixture Fixture, FixtureExpectation Expectation, FixtureReceipt Receipt, bool Satisfied);

/// <summary>The whole cell: lane identity plus every fixture's expectation.</summary>
public sealed record CellVerdict(IdentityCell Cell, LaneVerdict Lane, IReadOnlyList<FixtureVerdict> Fixtures, LaneEvidence Evidence)
{
    public bool Passed => Lane.Accepted && Fixtures.All(f => f.Satisfied);
}

/// <summary>
/// Confirms that each negative control still exercises a direct Swift transition: the generated
/// binding must carry the transition-gate rule its fixture names. Without this a generator change
/// that reroutes a member through the Cdecl wrapper would turn a control into a silent pass.
/// </summary>
public static class DirectLaneCoverage
{
    /// <summary>Every (fixture, rule, member) the binding no longer exercises directly.</summary>
    public static IReadOnlyList<string> Missing(IEnumerable<(string Rule, string Member)> violations)
    {
        var all = violations.ToList();
        var missing = new List<string>();
        foreach (var fixture in InterpIdentityMatrix.Fixtures)
            foreach (var (rule, member) in fixture.DirectMembers)
                if (!all.Any(v => v.Rule == rule && v.Member.Contains(member, StringComparison.Ordinal)))
                    missing.Add($"{fixture.Name}: no {rule} transition on a member matching '{member}'");
        return missing;
    }
}

public static class CellClassifier
{
    public static CellVerdict Classify(IdentityCell cell, string appName, string bindingAssembly,
        IReadOnlyList<FixtureReceipt> receipts, ReadyToRunInventory inventory, Crossgen2Invocation crossgen)
    {
        var fixtures = new List<FixtureVerdict>();
        foreach (var fixture in InterpIdentityMatrix.Fixtures)
        {
            var receipt = receipts.FirstOrDefault(r => r.Fixture == fixture.Name)
                ?? new FixtureReceipt(fixture.Name, FixtureOutcome.Missing, "not run", [], [], null);
            var expectation = InterpIdentityMatrix.ExpectationFor(cell, fixture.Name);
            fixtures.Add(new FixtureVerdict(fixture, expectation, receipt, InterpIdentityMatrix.Satisfies(expectation, receipt.Outcome)));
        }
        var callers = receipts.Select(ConsoleReceiptParser.Caller).Where(c => c != null).Select(c => c!).ToList();
        var gaps = InterpIdentityMatrix.Fixtures.Where(f => f.ReadyToRunGap != null).ToDictionary(f => f.Name, f => f.ReadyToRunGap!);
        var evidence = new LaneEvidence(appName, bindingAssembly, callers, inventory, crossgen, gaps);
        return new CellVerdict(cell, LaneClassifier.Classify(cell.ClaimedLane, evidence), fixtures, evidence);
    }
}
