// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Generics;

/// <summary>
/// Round-trips a struct or class nested in a bound generic outer (<c>NbgOptOuter&lt;int&gt;.Leaf</c>)
/// through an Optional return, an Optional parameter and a tuple return. The type spelling and
/// the marshal type for these container forms are built on different paths from a plain return,
/// so each has to name the nested leaf with the argument on the outer segment.
/// </summary>
public class NestedBoundGenericContainerReturnsTests : TestBase
{
    public NestedBoundGenericContainerReturnsTests(TestResults results) : base(results) { }

    public void TestOptionalReturn_NestedStruct()
    {
        using var leaf = NestedBoundGenericContainers.OptionalLeaf(7, true);
        AssertNotNull(leaf, "present Optional of a nested struct");
        AssertEqual(7, leaf!.Value, "nested struct value through an Optional return");

        var missing = NestedBoundGenericContainers.OptionalLeaf(8, false);
        AssertTrue(missing is null, "absent Optional of a nested struct is null");
    }

    public void TestOptionalReturn_NestedClass()
    {
        using var node = NestedBoundGenericContainers.OptionalNode(9, true);
        AssertNotNull(node, "present Optional of a nested class");
        AssertEqual(9, node!.Value, "nested class value through an Optional return");

        var missing = NestedBoundGenericContainers.OptionalNode(10, false);
        AssertTrue(missing is null, "absent Optional of a nested class is null");
    }

    public void TestOptionalParameter_NestedStruct()
    {
        using var leaf = NestedBoundGenericContainers.OptionalLeaf(21, true);
        AssertEqual(21, NestedBoundGenericContainers.LeafValue(leaf), "nested struct passed through an Optional parameter");
        AssertEqual(-1, NestedBoundGenericContainers.LeafValue(null), "nil passed as an Optional nested struct");
    }

    public void TestOptionalParameter_NestedClass()
    {
        using var node = NestedBoundGenericContainers.OptionalNode(22, true);
        AssertEqual(22, NestedBoundGenericContainers.NodeValue(node), "nested class passed through an Optional parameter");
        AssertEqual(-1, NestedBoundGenericContainers.NodeValue(null), "nil passed as an Optional nested class");
    }

    public void TestTupleReturn_NestedStruct()
    {
        var (leaf, extra) = NestedBoundGenericContainers.LeafTuple(31);
        using (leaf)
        {
            AssertEqual(31, leaf.Value, "nested struct element of a tuple return");
            AssertEqual(131, extra, "scalar element beside a nested struct");
        }
    }

    public void TestTupleReturn_NestedClass()
    {
        var (node, extra) = NestedBoundGenericContainers.NodeTuple(32);
        using (node)
        {
            AssertEqual(32, node.Value, "nested class element of a tuple return");
            AssertEqual(232, extra, "scalar element beside a nested class");
        }
    }
}
