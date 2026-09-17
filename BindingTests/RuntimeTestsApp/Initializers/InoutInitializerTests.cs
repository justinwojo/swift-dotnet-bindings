// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Initializers;

/// <summary>
/// Initializers and members that take <c>inout</c> parameters, on a class and on generic structs.
/// The Swift body reads the argument and writes a new value back; both directions have to arrive.
/// </summary>
public class InoutInitializerTests : TestBase
{
    public InoutInitializerTests(TestResults results) : base(results) { }

    public void TestClassInitializerReadsAndAdvancesInoutSeed()
    {
        nint seed = 10;
        using var tally = new InoutTally(ref seed);

        AssertEqual(10, tally.Total, "the initializer read the incoming value");
        AssertEqual((nint)11, seed, "the initializer's write reached the caller");
    }

    public void TestGenericStructInitializerAndMemberWriteBack()
    {
        nint count = 3;
        using var box = new InoutBox<int>(ref count);

        AssertEqual(3, box.Count, "the initializer read the incoming value");
        AssertEqual((nint)6, count, "the initializer's write reached the caller");

        nint accumulator = 1;
        AssertEqual((nint)4, box.Fold(ref accumulator), "the member returns the folded value");
        AssertEqual((nint)4, accumulator, "the member's write reached the caller");
    }

    public void TestGenericParameterInoutInitializerSwapsValue()
    {
        int value = 7;
        using var cell = new InoutCell<int>(ref value, 9);

        AssertEqual(7, cell.Stored, "the initializer stored the incoming value");
        AssertEqual(9, value, "the replacement was written back through the generic inout");
    }
}
