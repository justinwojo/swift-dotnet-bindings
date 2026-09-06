// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Properties;

/// <summary>
/// Width behaviour for pointer-width Swift integers (Int/UInt) that enter through an initializer
/// and leave through a property. The constructor and the property are emitted by different paths,
/// so the two halves have to agree on the range a consumer may use.
///
/// The last two tests pin what a value ABOVE the narrowed 32-bit range currently does: the getter
/// truncates it silently. That is the shipped width policy, not an endorsement of it — whether a
/// native-width accessor should exist alongside the narrowed property is an open product question,
/// and these tests are here so that answering it has to change them deliberately.
/// </summary>
public class NativeIntWidthTests : TestBase
{
    public NativeIntWidthTests(TestResults results) : base(results) { }

    public void TestResourceBudget_SmallValues_RoundTrip()
    {
        // Idiomatic 32-bit arguments: this only compiles because the constructor carries the same
        // int/uint convenience overload the free functions get.
        using var budget = new ResourceBudget(5u, 7);

        AssertEqual(5u, budget.SizeLimit, "SizeLimit round-trips");
        AssertEqual(7, budget.Offset, "Offset round-trips");
        AssertEqual(14, budget.DoubledOffset, "DoubledOffset round-trips");
    }

    public void TestResourceBudget_FunctionAndConstructorAcceptSameRange()
    {
        // The free function's convenience overload and the constructor's must accept the same
        // idiomatic argument types, or one half of the API is reachable and the other is not.
        using var viaFunction = Functions.MakeResourceBudget(2147483648u, 100);
        using var viaConstructor = new ResourceBudget(2147483648u, 100);

        AssertEqual(2147483648u, viaFunction.SizeLimit, "function-built SizeLimit at 2^31");
        AssertEqual(2147483648u, viaConstructor.SizeLimit, "constructor-built SizeLimit at 2^31");
        AssertEqual(viaFunction.Offset, viaConstructor.Offset, "both offsets agree");
    }

    public void TestResourceBudget_SignedOnlyConstructorOverload()
    {
        using var budget = new ResourceBudget(9);

        AssertEqual(9, budget.Offset, "single-argument constructor offset");
        AssertEqual(0u, budget.SizeLimit, "single-argument constructor limit defaults to zero");
    }

    public void TestResourceBudget_LimitAboveNarrowedRange_TruncatesSilently()
    {
        // 2^32 crosses the boundary intact — the Swift side still reads it back in full — but it
        // does not fit the narrowed 32-bit property, which wraps to 0 with nothing saying so.
        using var budget = Functions.MakeOversizedResourceBudget();

        AssertEqual("4294967296", Functions.ReadSizeLimitAsString(budget),
            "Swift still holds the full-width value");
        AssertEqual(0u, budget.SizeLimit,
            "SizeLimit above uint range wraps to 0 under the current width policy");
    }

    public void TestResourceBudget_OffsetAboveNarrowedRange_TruncatesSilently()
    {
        using var budget = Functions.MakeOverSignedResourceBudget();

        AssertEqual("2147483648", Functions.ReadOffsetAsString(budget),
            "Swift still holds the full-width offset");
        AssertEqual(int.MinValue, budget.Offset,
            "Offset above int range wraps to int.MinValue under the current width policy");
    }
}
