// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using Swift.Runtime;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Generics;

/// <summary>
/// Runtime gate for reduced (trailing-default-dropping) members on GENERIC classes. The wrapper
/// reaches the member through a private dispatch protocol; for a reduced overload that protocol's
/// requirement forwards to the full declaration so Swift supplies the dropped defaults. When the
/// requirement named the reduced signature instead, the class could not conform and the members
/// were never bound.
/// </summary>
public class GenericClassGateReducedMembersTests : TestBase
{
    public GenericClassGateReducedMembersTests(TestResults results) : base(results) { }

    public void TestReducedMethodFillsTrailingDefault()
    {
        using var tally = new GateReducedTally<int>(2);
        AssertEqual(7, (int)tally.Add(5), "Reduced add(_:) fills edges = [] so the total grows by the amount");
        AssertEqual(7, (int)tally.Total, "Total reflects the reduced call");
    }

    public void TestReducedMethodFillsDefaultedFlagAndCompletion()
    {
        using var tally = new GateReducedTally<int>(0);
        tally.Reset(9);
        AssertEqual(9, (int)tally.Total, "Reduced reset(to:) fills animated = true, so the value is stored as given");
    }

    public void TestReducedConstructorOnFinalGenericClass()
    {
        using var seed = new GateReducedSeed<string>(41);
        AssertEqual(41L, seed.Value, "Reduced init(value:) fills edges = [] and stores the value");
    }

    public void TestReducedThrowingConstructorOnFinalGenericClass()
    {
        using var seed = new GateReducedSeed<int>(12L);
        AssertEqual(12L, seed.Value, "Reduced throwing init(checked:) succeeds for a non-negative value");
        AssertThrows<SwiftException>(() => new GateReducedSeed<int>(-1L),
            "Reduced throwing init(checked:) surfaces the Swift error");
    }
}
