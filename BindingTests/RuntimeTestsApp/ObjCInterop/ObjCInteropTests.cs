// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using Swift;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.ObjCInterop;

/// <summary>
/// Tests for NSObject subclasses, @objc attributes, and @objc enums.
/// Exercises Objective-C interop emission patterns.
/// </summary>
public class ObjCInteropTests : TestBase
{
    public ObjCInteropTests(TestResults results) : base(results) { }

    #region SimpleNSObject

    public void TestSimpleNSObjectCreation()
    {
        using var obj = TestLibFunctions.CreateSimpleNSObject("test-label");
        AssertEqual("test-label", obj.Label.ToString(), "SimpleNSObject label preserved");
    }

    public void TestSimpleNSObjectDescribe()
    {
        using var obj = TestLibFunctions.CreateSimpleNSObject("my-obj");
        var desc = obj.GetDescribe();
        AssertTrue(desc.Contains("my-obj"), "Describe contains label");
    }

    #endregion

    #region LabeledItem (NSObject with Properties)

    public void TestLabeledItemCreation()
    {
        using var item = new LabeledItem("Widget", 5);
        AssertEqual("Widget", item.Name.ToString(), "LabeledItem name preserved");
        AssertEqual(5, item.Tag, "LabeledItem tag preserved");
    }

    public void TestLabeledItemDisplayName()
    {
        using var item = new LabeledItem("Item", 3);
        var display = item.DisplayName.ToString();
        AssertTrue(display.Contains("Item"), "DisplayName contains name");
        AssertTrue(display.Contains("3"), "DisplayName contains tag");
    }

    public void TestLabeledItemStaticFactory()
    {
        using var item = LabeledItem.Create("Factory", 7);
        AssertEqual("Factory", item.Name.ToString(), "Static factory creates item");
        AssertEqual(7, item.Tag, "Static factory sets tag");
    }

    #endregion

    #region SpecialItem (NSObject Subclass Inheritance)

    public void TestSpecialItemCreation()
    {
        using var special = new SpecialItem("Special", 1, 10);
        AssertEqual("Special", special.Name.ToString(), "Inherited name preserved");
        AssertEqual(1, special.Tag, "Inherited tag preserved");
        AssertEqual(10, special.Priority, "SpecialItem priority preserved");
    }

    public void TestSpecialItemOverriddenDisplayName()
    {
        using var special = new SpecialItem("Override", 2, 5);
        var display = special.DisplayName.ToString();
        AssertTrue(display.Contains("Override"), "Overridden DisplayName contains name");
        AssertTrue(display.Contains("P5") || display.Contains("5"), "Overridden DisplayName contains priority");
    }

    #endregion

    #region ObjCAnnotated

    public void TestObjCAnnotatedCreation()
    {
        using var annotated = TestLibFunctions.CreateObjCAnnotated("Annotated");
        AssertEqual("Annotated", annotated.Title.ToString(), "ObjCAnnotated title preserved");
    }

    public void TestObjCAnnotatedMethod()
    {
        using var annotated = TestLibFunctions.CreateObjCAnnotated("Hello");
        var result = annotated.GetObjcMethod();
        AssertTrue(result.Contains("Hello"), "ObjC method returns expected value");
    }

    public void TestObjCAnnotatedSwiftOnlyMethod()
    {
        using var annotated = TestLibFunctions.CreateObjCAnnotated("Test");
        var count = annotated.GetSwiftOnlyMethod();
        AssertEqual(4, count, "Swift-only method returns string length");
    }

    #endregion

    #region FullyObjCExposed

    public void TestFullyObjCExposedCreation()
    {
        using var exposed = new FullyObjCExposed("test-id", 42);
        AssertEqual("test-id", exposed.Identifier.ToString(), "Identifier preserved");
        AssertEqual(42, exposed.Value, "Value preserved");
    }

    public void TestFullyObjCExposedSummary()
    {
        using var exposed = new FullyObjCExposed("abc", 10);
        var summary = exposed.GetSummary();
        AssertTrue(summary.Contains("abc"), "Summary contains identifier");
        AssertTrue(summary.Contains("10"), "Summary contains value");
    }

    public void TestFullyObjCExposedDoubleValue()
    {
        using var exposed = new FullyObjCExposed("x", 21);
        AssertEqual(42, exposed.GetDoubleValue(), "DoubleValue returns 2x");
    }

    public void TestFullyObjCExposedDefaultItem()
    {
        using var item = FullyObjCExposed.GetDefaultItem();
        AssertEqual("default", item.Identifier.ToString(), "Default item has default identifier");
        AssertEqual(0, item.Value, "Default item has zero value");
    }

    #endregion

    #region ObjCPriority Enum

    public void TestObjCPriorityValues()
    {
        AssertEqual(0, (int)ObjCPriority.Low, "Low = 0");
        AssertEqual(1, (int)ObjCPriority.Medium, "Medium = 1");
        AssertEqual(2, (int)ObjCPriority.High, "High = 2");
        AssertEqual(3, (int)ObjCPriority.Critical, "Critical = 3");
    }

    public void TestPriorityLabelFunction()
    {
        AssertEqual("Low", TestLibFunctions.PriorityLabel(ObjCPriority.Low), "Low label");
        AssertEqual("Critical", TestLibFunctions.PriorityLabel(ObjCPriority.Critical), "Critical label");
    }

    #endregion

    #region Selectors (Unsupported)

    [Skip("Selector type not fully supported by generator")]
    public void TestSelectorCreation()
    {
        // SelectorTarget uses Selector type — partially supported
    }

    [Skip("Selector type not fully supported by generator")]
    public void TestSelectorPerformAction()
    {
        // Selector-based action dispatch not yet supported
    }

    [Skip("Selector type not fully supported by generator")]
    public void TestObjectRespondsToSelector()
    {
        // objectRespondsTo uses Selector parameter
    }

    #endregion
}
