// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Reflection;
using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Generics;

/// <summary>
/// Coverage for a generic parameter pinned to another parameter's associated type
/// (<c>Generics/CrossParameterSameTypeRequirement.swift</c>). <c>E == S.Element</c> is a same-type
/// requirement, but it was spelled after the colon in the EveryProtocol stub — <c>Element</c> names
/// nothing there — so the wrapper did not compile and the whole <c>StaffRoster</c> binding was
/// withdrawn. The type existing and dispatching at all is the observable.
/// </summary>
public class CrossParameterSameTypeRequirementTests : TestBase
{
    public CrossParameterSameTypeRequirementTests(TestResults results) : base(results) { }

    /// <summary>
    /// The owner of the pinned generic members still binds and reaches Swift. Before the fix there
    /// was no <c>StaffRoster</c> type to call: the wrapper failed to compile and took the whole
    /// binding with it, non-generic members included.
    /// </summary>
    public void TestOwnerOfPinnedGenericMembersStillDispatches()
    {
        using var roster = new StaffRoster(new nint[] { 3, 7, 11 });
        AssertEqual(3, roster.Count, "count on the owner of a cross-parameter-pinned member");

        var above = roster.IdsAbove(5);
        AssertEqual(2, above.Count, "idsAbove(5) element count");
        AssertEqual((nint)7, above[0], "idsAbove(5) first element");
        AssertEqual((nint)11, above[1], "idsAbove(5) second element");
    }

    /// <summary>
    /// The two Swift overloads differ <i>only</i> in their same-type pin. Rendering the pins as a
    /// <c>where</c> clause keeps them apart; dropping them instead — the other way to stop the bad
    /// spelling — collapses both onto one signature, which is a redeclaration. Two distinct methods
    /// here is what says the pins were rendered rather than discarded.
    /// </summary>
    public void TestSameTypePinnedOverloadsRemainDistinct()
    {
        var overloads = typeof(StaffRoster)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => m.Name == "FirstEntry")
            .ToList();

        AssertEqual(2, overloads.Count, "both same-type-pinned overloads survive as distinct members");
        AssertEqual(1, overloads.Count(m => m.GetParameters().Length == 1),
            "the overload pinned by E == S.Element");
        AssertEqual(1, overloads.Count(m => m.GetParameters().Length == 2),
            "the sibling overload pinned by S.Element == Int");
    }
}
