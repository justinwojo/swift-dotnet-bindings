// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Linq;
using RuntimeTestsApp.Infrastructure;
using Swift.Runtime;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Collisions;

/// <summary>
/// Types nested in a class and named <c>Type</c> or <c>Protocol</c>. Written as a value in Swift
/// (<c>Host.`Type`.self</c>), such a name is the host's metatype, so every wrapper site that names
/// the nested type in an expression must reach the type itself: its metadata accessor answered with
/// the host's metatype, sizing each value as one word. Fixture: Collisions/KeywordNestedTypes.swift.
/// </summary>
public class KeywordNestedTypeTests : TestBase
{
    public KeywordNestedTypeTests(TestResults results) : base(results) { }

    public void TestTypeMetadataIsTheNestedStruct()
    {
        var metadata = TypeMetadata.GetTypeMetadataOrThrow<MetatypeKeywordHost.Type>();
        AssertEqual((nuint)MetatypeKeywordHost.TypeSize, metadata.Size, "size of the nested `Type` struct");
        AssertEqual((nuint)MetatypeKeywordHost.TypeStride, metadata.Stride, "stride of the nested `Type` struct");
    }

    public void TestProtocolMetadataIsTheNestedStruct()
    {
        var metadata = TypeMetadata.GetTypeMetadataOrThrow<MetatypeKeywordHost.Protocol>();
        AssertEqual((nuint)MetatypeKeywordHost.ProtocolSize, metadata.Size, "size of the nested `Protocol` struct");
    }

    public void TestProtocolValuesRoundTrip()
    {
        using var host = new MetatypeKeywordHost();
        var point = host.Point(4);
        AssertEqual(4, point.X, "x");
        AssertEqual(5, point.Y, "y");
        AssertEqual(6, point.Z, "z");
        AssertEqual("4,5,6", host.DescribePoint(point), "a returned value passed back");
        AssertEqual("7,8,9", host.DescribePoint(new MetatypeKeywordHost.Protocol(7, 8, 9)), "a value built in C#");
    }

    public void TestProtocolArrayElementsRoundTrip()
    {
        using var host = new MetatypeKeywordHost();
        var points = host.GetPoints();
        AssertEqual(2, points.Count, "two elements");
        AssertEqual("1,2,3;10,11,12", string.Join(";", points.Select(p => $"{p.X},{p.Y},{p.Z}")), "elements read from the array");
        AssertEqual("1,2,3;10,11,12", host.DescribePoints(points.ToArray()), "elements written to an array");
    }
}
