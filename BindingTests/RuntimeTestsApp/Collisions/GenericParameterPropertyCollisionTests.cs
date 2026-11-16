// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Collisions;

/// <summary>
/// A stored property named like its type's own type parameter (fixture:
/// GenericParameterPropertyCollision.swift). `var t: T` projects as <c>TValue</c> beside the type
/// parameter <c>T</c> instead of a duplicate <c>T</c> (CS0102), and reads and writes the Swift value.
/// </summary>
public class GenericParameterPropertyCollisionTests : TestBase
{
    public GenericParameterPropertyCollisionTests(TestResults results) : base(results) { }

    public void TestStructPropertyNamedLikeItsTypeParameterRoundTrips()
    {
        using var field = new GenericParameterNamedField<string>("first", 3);
        AssertEqual("first", field.TValue, "the renamed property reads the Swift value");
        AssertEqual(3, field.Tag, "the sibling property keeps its name");
        field.TValue = "second";
        AssertEqual("field:second:3", field.GetDescribe(), "Swift sees the value the renamed setter stored");
    }

    public void TestClassPropertyNamedLikeItsTypeParameterRoundTrips()
    {
        using var cell = new GenericParameterNamedCell<string>("start");
        AssertEqual("start", cell.TValue, "the renamed property reads the Swift value");
        cell.TValue = "next";
        AssertEqual("cell:next", cell.GetDescribe(), "Swift sees the value the renamed setter stored");
    }

    public void TestSuffixedNameTakenBySiblingKeepsBothProperties()
    {
        using var neighbour = new GenericParameterNamedNeighbour<string>("first", 7);
        AssertEqual("first", neighbour.TValue2, "the renamed property moves past the sibling's name");
        AssertEqual(7, neighbour.TValue, "the sibling keeps its own name and value");
        neighbour.TValue2 = "second";
        neighbour.TValue = 8;
        AssertEqual("neighbour:second:8", neighbour.GetDescribe(), "Swift sees both values");
    }
}
