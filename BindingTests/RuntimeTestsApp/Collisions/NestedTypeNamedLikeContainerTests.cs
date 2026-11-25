// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Collisions;

/// <summary>
/// Nested types named like their container (<c>Outer.Outer</c>, <c>Outer.outer</c>), which C#
/// forbids (CS0542). The nested type takes the kind-aware suffix — <c>Info</c> for a struct or
/// class, <c>Kind</c> for an enum — in every container kind, and the container's member that
/// projects onto that leaf yields with <c>Value</c>. Fixture:
/// Collisions/NestedTypeNamedLikeContainer.swift.
/// </summary>
public class NestedTypeNamedLikeContainerTests : TestBase
{
    public NestedTypeNamedLikeContainerTests(TestResults results) : base(results) { }

    public void TestStructContainer_NestedClassTakesTheInfoSuffix()
    {
        using var inner = new NestedNameOuter.NestedNameOuterInfo();
        AssertEqual(41, inner.Ping(), "the renamed nested class must call into Swift");

        var outer = new NestedNameOuter(5);
        AssertEqual(5, outer.Value, "the container keeps its name and members");
        AssertEqual(6, outer.NestedNameOuterInfoValue, "the member on the renamed leaf yields with Value");
    }

    public void TestStructFacade_NestedStructTakesTheInfoSuffix()
    {
        var inner = new NestedNameFacade.NestedNameFacadeInfo(3);
        AssertEqual(3, inner.A, "the facade's renamed nested struct must round-trip its field");
    }

    public void TestCaselessEnum_NestedEnumTakesTheKindSuffix()
    {
        AssertEqual(20, NestedNameSpace.Describe(NestedNameSpace.NestedNameSpaceKind.Two),
            "the renamed nested enum must reach Swift as its own case");
        AssertEqual(1, NestedNameSpace.NestedNameSpaceKind.One.ToRawValue(), "the raw value survives the rename");
        AssertEqual(12, NestedNameSpace.NestedNameSpaceKindMethod(5),
            "a static method projecting onto the renamed enum's leaf yields to it");
    }

    public void TestClassContainer_NestedEnumTakesTheKindSuffix()
    {
        using var box = new NestedNameBox();
        AssertEqual(NestedNameBox.NestedNameBoxKind.Second, box.Make(),
            "a method returning the renamed nested enum must return its case");
    }

    public void TestPayloadEnum_NestedStructTakesTheInfoSuffix()
    {
        using var payload = NestedNamePayload.Value(7);
        var tagged = payload.Tagged;
        AssertEqual(7, tagged.Tag, "the renamed nested struct must carry the payload's value");
        AssertEqual(9, new NestedNamePayload.NestedNamePayloadInfo(9).Tag, "the renamed nested struct constructs");
        using var named = NestedNamePayload.NestedNamePayloadValue(4);
        AssertEqual(8, named.Tagged.Tag, "the case named like the enum keeps its factory rename");
    }
}
