// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Operators;

/// <summary>
/// Equality on a generic Equatable (not Hashable) struct. Without a per-type equality
/// wrapper, <c>==</c> resolves the Swift Equatable witness table at runtime through the type's
/// conformance descriptor; before the descriptor was registered every comparison threw
/// "no conformance was found". Values come both from a factory and out of a Swift array,
/// the two ways the shape reaches a consumer.
/// </summary>
public class GenericEquatableStructTests : TestBase
{
    public GenericEquatableStructTests(TestResults results) : base(results) { }

    public void TestDirectComparison_EqualAndUnequal()
    {
        using var a = Functions.MakeEquatableOnlyBox(42);
        using var b = Functions.MakeEquatableOnlyBox(42);
        using var c = Functions.MakeEquatableOnlyBox(7);
        AssertTrue(a == b, "boxes holding equal values compare equal");
        AssertFalse(a == c, "boxes holding different values compare unequal");
        AssertTrue(a != c, "!= is the negation of ==");
        AssertTrue(a.Equals(b), "IEquatable.Equals agrees with ==");
    }

    public void TestStringArgument_ComparesByContent()
    {
        using var a = Functions.MakeEquatableOnlyStringBox("aria");
        using var b = Functions.MakeEquatableOnlyStringBox("aria");
        using var c = Functions.MakeEquatableOnlyStringBox("rondo");
        AssertTrue(a == b, "separately created String boxes with equal content compare equal");
        AssertFalse(a == c, "String boxes with different content compare unequal");
    }

    public void TestElementsReadFromArray()
    {
        using var holder = new EquatableOnlyBoxHolder(new nint[] { 5, 5, 6 });
        using var items = holder.Items;
        AssertEqual(3, items.Count, "holder carries three boxes");
        using var first = items[0];
        using var second = items[1];
        using var third = items[2];
        using var firstAgain = items[0];
        AssertTrue(first == firstAgain, "an element read twice equals itself");
        AssertTrue(first == second, "elements built from equal values compare equal");
        AssertFalse(first == third, "elements built from different values compare unequal");
        using var standalone = Functions.MakeEquatableOnlyBox(6);
        AssertTrue(third == standalone, "an array element equals a separately created box");
    }
}
