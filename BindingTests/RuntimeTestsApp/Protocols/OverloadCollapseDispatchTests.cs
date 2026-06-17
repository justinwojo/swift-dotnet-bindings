// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Protocols;

/// <summary>
/// Regression tests for the existential-overload collapse first seen breaking
/// FirebaseFirestore generation (24× CS1503 in the generated binding).
///
/// Shape: a reverse-dispatch protocol declares TWO overloads of the same method
/// name whose parameters are DIFFERENT existentials —
/// <c>record(any OverloadCollapseTagPrimary)</c> /
/// <c>record(any OverloadCollapseTagSecondary)</c> (Firestore's was
/// <c>add(any Expression)</c> / <c>add(any Sendable)</c>).
///
/// The generator's three protocol key functions diverge on this shape: the
/// interface dedups to a SINGLE C# method (<c>Record(IOverloadCollapseTagPrimary)</c>,
/// first by declaration order) while the vtable allocates two witness slots. Before
/// the fix the proxy emitted a SECOND receiver dispatching to a non-existent
/// <c>Record(IOverloadCollapseTagSecondary)</c> overload against the one-method
/// interface → CS1503. The fix adds a raw-signature dedup to the proxy receiver +
/// static-init loops so only the surviving overload's receiver is emitted.
///
/// The mere fact that this fixture's binding COMPILES is the primary regression
/// guard (a re-break reappears as CS1503 in SwiftBindingsTestLib.cs). The runtime
/// leg below additionally proves the surviving overload reverse-dispatches into a
/// C# conformer and round-trips the existential argument's value.
/// </summary>
public class OverloadCollapseDispatchTests : TestBase
{
    public OverloadCollapseDispatchTests(TestResults results) : base(results) { }

    /// <summary>
    /// Assign a C# conformer of the collapsed delegate to a Swift driver and fire the
    /// surviving overload. Swift constructs a concrete <c>OverloadCollapsePrimaryBox</c>
    /// and dispatches <c>record(any OverloadCollapseTagPrimary)</c> back through the
    /// EveryProtocol vtable into the C# <c>Record</c>; the conformer reads the
    /// existential argument's <c>PrimaryId</c> and returns a derived value that Swift
    /// hands back to us. A re-break in the proxy raw-key dedup either fails to compile
    /// (CS1503) or, if it compiled, drops the dispatch (CallCount == 0).
    /// </summary>
    public void TestSurvivingOverloadReverseDispatches()
    {
        var impl = new OverloadCollapseDelegateImpl();
        using var source = new OverloadCollapseSource();

        source.Delegate = impl;
        int result = source.FirePrimary(21);

        AssertEqual(1, impl.CallCount, "surviving record overload dispatched into the C# conformer exactly once");
        AssertEqual(21, impl.LastReceivedPrimaryId, "C# conformer read the existential argument's PrimaryId");
        AssertEqual(42, result, "FirePrimary returned the conformer's result (PrimaryId * 2)");
    }

    /// <summary>
    /// Control: with no delegate set the Swift driver returns its sentinel (-1). This
    /// exists so a regression in basic delegate plumbing isn't misattributed to the
    /// overload-collapse fix.
    /// </summary>
    public void TestNoDelegateReturnsSentinel()
    {
        using var source = new OverloadCollapseSource();

        int result = source.FirePrimary(7);

        AssertEqual(-1, result, "FirePrimary returns -1 when no delegate is set");
    }
}

internal class OverloadCollapseDelegateImpl : IOverloadCollapseDelegate
{
    public int LastReceivedPrimaryId { get; private set; } = -999;
    public int CallCount { get; private set; }

    public int Record(IOverloadCollapseTagPrimary value)
    {
        LastReceivedPrimaryId = value.PrimaryId;
        CallCount++;
        return value.PrimaryId * 2;
    }
}
