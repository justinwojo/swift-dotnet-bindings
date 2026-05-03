// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Protocols;

/// <summary>
/// Runtime tests for the CS0738 covariant-return forwarder emitted by
/// <c>ProtocolProxyEmitter.InterfaceImpl.cs</c>. Two pairs cover both branches:
///
/// <list type="bullet">
/// <item>Subclass case — refined return is a subclass of the base return.
/// Generator emits a real cast forwarder; calling through the base interface
/// dispatches to the refined method and returns the up-cast instance.</item>
/// <item>Sibling case — refined and base returns are unrelated classes.
/// Generator emits a throwing <c>NotSupportedException</c> stub; calling through
/// the base interface throws.</item>
/// </list>
/// </summary>
public class RefinedReturnProtocolTests : TestBase
{
    public RefinedReturnProtocolTests(TestResults results) : base(results) { }

    #region Subclass case — cast forwarder

    public void TestSubclassCase_RefinedSlot_ReturnsSubclassInstance()
    {
        var existential = TestLibFunctions.GetCrtMakeRefinedShapeExistential();
        var refined = existential.MakeShape();
        AssertNotNull(refined, "refined slot returns instance");
        AssertEqual("TAG", refined.RefinedTag.ToString(), "refined.RefinedTag");
        AssertEqual("refined-shape", refined.Name.ToString(), "inherited Name property");
    }

    public void TestSubclassCase_BaseSlot_DispatchesViaCastForwarder()
    {
        var existential = TestLibFunctions.GetCrtMakeRefinedShapeExistential();
        ICRTBaseShapeProvider baseRef = existential;
        // Base slot is satisfied by the explicit-interface forwarder:
        //   CRTBaseShape ICRTBaseShapeProvider.MakeShape() => (CRTBaseShape)this.MakeShape();
        // It dispatches into the refined method and casts down to the declared base type.
        var shape = baseRef.MakeShape();
        AssertNotNull(shape, "base slot dispatches via cast forwarder");
        // Static type is CRTBaseShape; runtime type is the refined CRTRefinedShape instance.
        AssertTrue(shape is CRTRefinedShape, "base slot returns the refined runtime instance");
        AssertEqual("refined-shape", shape.Name.ToString(), "base slot exposes inherited Name");
    }

    #endregion

    #region Sibling case — throwing NotSupportedException stub

    public void TestSiblingCase_RefinedSlot_ReturnsSiblingInstance()
    {
        var existential = TestLibFunctions.GetCrtMakePropertyExistential();
        var refined = existential.MakeColumn();
        AssertNotNull(refined, "refined slot returns instance");
        AssertEqual("prop-1", refined.PropertyName.ToString(), "refined.PropertyName");
    }

    public void TestSiblingCase_BaseSlot_ThrowsNotSupportedException()
    {
        var existential = TestLibFunctions.GetCrtMakePropertyExistential();
        ICRTColumnProvider baseRef = existential;
        // Base slot is satisfied by the throwing-stub forwarder:
        //   CRTColumnLike ICRTColumnProvider.MakeColumn() => throw new NotSupportedException(
        //       "Refined return type ('CRTPropertyLike') is not assignable to 'CRTColumnLike'. Use ICRTPropertyProvider (the refined protocol) instead.");
        AssertThrows<NotSupportedException>(
            () => baseRef.MakeColumn(),
            "base slot throws NotSupportedException for sibling-class refinement");
    }

    #endregion
}
