// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;

namespace SwiftBindings.InterpIdentity;

/// <summary>How a cell's managed code reaches native code.</summary>
public enum ExecutionLane
{
    /// <summary>The CoreCLR interpreter: binding methods have no precompiled body, and every
    /// native call leaves through the interpreter's call stubs in libcoreclr.</summary>
    Interpreter,

    /// <summary>ReadyToRun: binding methods are precompiled into the app's composite image and
    /// call native code from there.</summary>
    ReadyToRun,
}

/// <summary>What one fixture did in its own process.</summary>
public enum FixtureOutcome
{
    /// <summary>The app reported that every check held.</summary>
    Pass,

    /// <summary>The app reported a wrong value, a corrupted receiver or a lost error.</summary>
    Fail,

    /// <summary>The fixture began but the process died before it reported.</summary>
    Crash,

    /// <summary>The fixture never began: the app did not start, or ran something else.</summary>
    Missing,
}

/// <summary>What a cell must show for one fixture, given today's emission.</summary>
public enum FixtureExpectation
{
    /// <summary>Must pass.</summary>
    Pass,

    /// <summary>A negative control that must reproduce its defect: fail or crash.</summary>
    Reproduce,

    /// <summary>A measurement of a shape with no settled outcome yet: any outcome is recorded,
    /// but the fixture must have run.</summary>
    Observe,
}

/// <summary>One cell of the lane-identity harness.</summary>
/// <param name="Id">The acceptance-matrix row, e.g. <c>M1</c>.</param>
/// <param name="Label">Human-readable cell name.</param>
/// <param name="Platform">Apple platform name as the generator spells it (<c>ios</c>, <c>maccatalyst</c>).</param>
/// <param name="RuntimeIdentifier">The app's RID.</param>
/// <param name="Configuration">MSBuild configuration that selects the lane.</param>
/// <param name="ClaimedLane">The lane this cell claims to run; the receipts must prove it.</param>
/// <param name="Simulator">True when the app runs on the iOS simulator, false when it runs on the host.</param>
public sealed record IdentityCell(
    string Id, string Label, string Platform, string RuntimeIdentifier, string Configuration,
    ExecutionLane ClaimedLane, bool Simulator);

/// <summary>A fixture the app can run, with the transition shape it exercises.</summary>
/// <param name="Name">The name passed to the app.</param>
/// <param name="Shape">The transition shape, for the report.</param>
/// <param name="DirectMembers">Generated members that must carry a Swift-transition rule in the
/// binding assembly for this fixture to exercise a direct Swift transition (rule, member
/// substring). Empty for positive controls, which go through the Cdecl wrapper or a thunk.</param>
/// <param name="ReadyToRunGap">Set when crossgen2 leaves the method that makes this fixture's Swift
/// call to the runtime, so a ReadyToRun app still makes that call from the interpreter.</param>
public sealed record IdentityFixture(string Name, string Shape, IReadOnlyList<(string Rule, string Member)> DirectMembers,
    ReadyToRunGap? ReadyToRunGap = null);

/// <summary>
/// A generated method crossgen2 does not precompile, measured rather than assumed. In a
/// ReadyToRun cell the harness requires both halves: the method is absent from the composite map,
/// and the fixture's Swift call comes from the interpreter. If either stops holding the gap is
/// stale and the cell fails, so the table cannot keep excusing a call that is now precompiled.
/// </summary>
/// <param name="Method">The method's map symbol after the binding's assembly prefix, e.g.
/// <c>InterpIdentityFixture_IifFailable__TryCreate</c>.</param>
/// <param name="Reason">What crossgen2 reported, for the report.</param>
public sealed record ReadyToRunGap(string Method, string Reason);

/// <summary>The cells, fixtures and per-cell expectations for today's emission.</summary>
public static class InterpIdentityMatrix
{
    public static readonly IdentityCell M1 = new("M1", "iOS simulator arm64, interpreter (Debug)",
        "ios", "iossimulator-arm64", "Debug", ExecutionLane.Interpreter, Simulator: true);
    public static readonly IdentityCell M2 = new("M2", "iOS simulator arm64, ReadyToRun (Release)",
        "ios", "iossimulator-arm64", "Release", ExecutionLane.ReadyToRun, Simulator: true);
    public static readonly IdentityCell M3 = new("M3", "Mac Catalyst x86_64 under Rosetta, interpreter (Debug)",
        "maccatalyst", "maccatalyst-x64", "Debug", ExecutionLane.Interpreter, Simulator: false);
    public static readonly IdentityCell M4 = new("M4", "Mac Catalyst x86_64 under Rosetta, ReadyToRun (Release)",
        "maccatalyst", "maccatalyst-x64", "Release", ExecutionLane.ReadyToRun, Simulator: false);

    public static IReadOnlyList<IdentityCell> Cells { get; } = [M1, M2, M3, M4];

    public static IReadOnlyList<IdentityFixture> Fixtures { get; } =
    [
        new("primitive", "free function, primitives only, through a native thunk", []),
        new("string", "String round trip through the @_cdecl wrapper", []),
        new("class-member", "final-class method through a native thunk", []),
        new("self-receiver", "direct CallConvSwift: inout non-frozen struct + SwiftSelf (non-frozen struct receiver by address), repeated",
            [("R1", "IifSlot::PInvoke_stamp_")]),
        new("self-error", "direct CallConvSwift: inout non-frozen struct, bool, SwiftSelf (struct by address), SwiftError*",
            [("R1", "IifSlot::PInvoke_stampThrowing_")]),
        new("indirect-result", "direct CallConvSwift: SwiftIndirectResult (failable init of a resilient struct)",
            [("R1", "IifFailable::PInvoke_init_")],
            new ReadyToRunGap("InterpIdentityFixture_IifFailable__TryCreate",
                "the failable init reads the Optional's tag and copies the payload through value-witness function " +
                "pointers; crossgen2 does not precompile a method containing an unmanaged calli " +
                "(GetCookieForPInvokeCalliSig requires runtime JIT)")),
        new("reverse-error-mid", "Swift invokes a CallConvSwift UCO (void*, SwiftError*, SwiftSelf)",
            [("R4", "iifCallThrowing_arg0_")]),
        new("reverse-context-last", "Swift invokes a CallConvSwift UCO (void*, SwiftSelf) from a failable init",
            [("R4", "IifVisitorFactory::init_arg0_"), ("R1", "IifVisitorFactory::PInvoke_init_")]),
    ];

    public static IdentityFixture Fixture(string name) =>
        Fixtures.FirstOrDefault(f => f.Name == name) ?? throw new ArgumentException($"Unknown fixture '{name}'.", nameof(name));

    public static IdentityCell Cell(string id) =>
        Cells.FirstOrDefault(c => string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase))
        ?? throw new ArgumentException($"Unknown cell '{id}'. Known: {string.Join(", ", Cells.Select(c => c.Id))}.", nameof(id));

    /// <summary>
    /// What <paramref name="cell"/> must show for <paramref name="fixture"/> on today's emission.
    /// ReadyToRun cells pass every call they precompile; a fixture whose Swift call crossgen2
    /// leaves to the runtime (<see cref="IdentityFixture.ReadyToRunGap"/>) runs in the interpreter
    /// there too, so it carries the interpreter's expectation for the same architecture. The
    /// interpreter cells reproduce the measured defects: on arm64 the x20 write into the receiver
    /// and the reverse error slot; on x86_64 the missing Swift lowering, which misplaces a typed
    /// SwiftSelf, loses a typed SwiftError, drops the indirect result and breaks both reverse
    /// callbacks.
    /// </summary>
    public static FixtureExpectation ExpectationFor(IdentityCell cell, string fixture)
    {
        var f = Fixture(fixture);
        var x64 = cell.RuntimeIdentifier.EndsWith("-x64", StringComparison.Ordinal);
        if (cell.ClaimedLane == ExecutionLane.ReadyToRun && f.ReadyToRunGap == null)
            return FixtureExpectation.Pass;
        return (fixture, x64) switch
        {
            ("primitive" or "string" or "class-member", _) => FixtureExpectation.Pass,
            ("self-receiver" or "self-error", _) => FixtureExpectation.Reproduce,
            ("indirect-result", false) => FixtureExpectation.Pass,
            ("indirect-result", true) => FixtureExpectation.Reproduce,
            ("reverse-error-mid", _) => FixtureExpectation.Reproduce,
            ("reverse-context-last", false) => FixtureExpectation.Pass,
            ("reverse-context-last", true) => FixtureExpectation.Reproduce,
            _ => throw new ArgumentException($"No expectation for '{fixture}' on {cell.Id}."),
        };
    }

    /// <summary>True when <paramref name="outcome"/> satisfies <paramref name="expectation"/>.</summary>
    public static bool Satisfies(FixtureExpectation expectation, FixtureOutcome outcome) => expectation switch
    {
        FixtureExpectation.Pass => outcome == FixtureOutcome.Pass,
        FixtureExpectation.Reproduce => outcome is FixtureOutcome.Fail or FixtureOutcome.Crash,
        FixtureExpectation.Observe => outcome is not FixtureOutcome.Missing,
        _ => false,
    };
}
