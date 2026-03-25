// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Properties;

/// <summary>
/// Tests property getters and setters on non-frozen structs through generated @_cdecl wrappers.
/// Non-frozen struct property accessors use opaque accessor calling conventions (x8 indirect
/// buffer for getters, [x0] indirect read for setters) that are incompatible with ARM64 thunks.
/// The thunk gate (commit 7edd11a7) rejects these from thunk emission, falling back to @_cdecl.
/// If the thunk gate were reverted, these tests would SIGSEGV on simulator.
///
/// Coverage:
/// - NonFrozenPoint: Double property get/set (blittable)
/// - NonFrozenMutableProps: Int32 property get/set + String property get/set
/// - NonFrozenStructWithProperties: let property get + var property get/set + computed property get
/// </summary>
public class NonFrozenPropertyTests : TestBase
{
    public NonFrozenPropertyTests(TestResults results) : base(results) { }

    #region NonFrozenPoint — Double Properties (get + set)

    public void TestNonFrozenPointConstruction()
    {
        using var point = new NonFrozenPoint(x: 3.0, y: 4.0);
        AssertNotNull(point, "NonFrozenPoint constructed");
        TestLogger.Info("NonFrozenPoint construction passed");
    }

    public void TestNonFrozenPointXGetter()
    {
        using var point = new NonFrozenPoint(x: 3.0, y: 4.0);
        AssertApproxEqual(3.0, point.X, message: "X getter");
        TestLogger.Info($"NonFrozenPoint.X = {point.X}");
    }

    public void TestNonFrozenPointYGetter()
    {
        using var point = new NonFrozenPoint(x: 3.0, y: 4.0);
        AssertApproxEqual(4.0, point.Y, message: "Y getter");
        TestLogger.Info($"NonFrozenPoint.Y = {point.Y}");
    }

    public void TestNonFrozenPointXSetter()
    {
        using var point = new NonFrozenPoint(x: 3.0, y: 4.0);
        point.X = 10.0;
        AssertApproxEqual(10.0, point.X, message: "X after set");
        TestLogger.Info($"NonFrozenPoint.X set to {point.X}");
    }

    public void TestNonFrozenPointYSetter()
    {
        using var point = new NonFrozenPoint(x: 3.0, y: 4.0);
        point.Y = 20.0;
        AssertApproxEqual(20.0, point.Y, message: "Y after set");
        TestLogger.Info($"NonFrozenPoint.Y set to {point.Y}");
    }

    public void TestNonFrozenPointSetBothProperties()
    {
        using var point = new NonFrozenPoint(x: 1.0, y: 2.0);
        point.X = 5.0;
        point.Y = 12.0;
        AssertApproxEqual(5.0, point.X, message: "X after set both");
        AssertApproxEqual(12.0, point.Y, message: "Y after set both");
        TestLogger.Info($"NonFrozenPoint set both: ({point.X}, {point.Y})");
    }

    public void TestNonFrozenPointMethodAfterPropertySet()
    {
        // Verify method works correctly after property mutation
        using var point = new NonFrozenPoint(x: 3.0, y: 4.0);
        point.X = 0.0;
        point.Y = 0.0;
        var dist = point.GetDistanceFromOrigin();
        AssertApproxEqual(0.0, dist, message: "Distance from origin after zeroing");
        TestLogger.Info($"NonFrozenPoint.GetDistanceFromOrigin() after set = {dist}");
    }

    #endregion

    #region NonFrozenMutableProps — Int32 + String Properties (get + set)

    public void TestNonFrozenMutablePropsConstruction()
    {
        using var props = new NonFrozenMutableProps(value: 42, label: "hello");
        AssertNotNull(props, "NonFrozenMutableProps constructed");
        TestLogger.Info("NonFrozenMutableProps construction passed");
    }

    public void TestNonFrozenMutablePropsValueGetter()
    {
        using var props = new NonFrozenMutableProps(value: 42, label: "hello");
        AssertEqual(42, props.Value, "Value getter");
        TestLogger.Info($"NonFrozenMutableProps.Value = {props.Value}");
    }

    public void TestNonFrozenMutablePropsValueSetter()
    {
        using var props = new NonFrozenMutableProps(value: 42, label: "hello");
        props.Value = 99;
        AssertEqual(99, props.Value, "Value after set");
        TestLogger.Info($"NonFrozenMutableProps.Value set to {props.Value}");
    }

    public void TestNonFrozenMutablePropsLabelGetter()
    {
        using var props = new NonFrozenMutableProps(value: 1, label: "world");
        AssertEqual("world", props.Label, "Label getter");
        TestLogger.Info($"NonFrozenMutableProps.Label = {props.Label}");
    }

    public void TestNonFrozenMutablePropsLabelSetter()
    {
        using var props = new NonFrozenMutableProps(value: 1, label: "original");
        props.Label = "changed";
        AssertEqual("changed", props.Label, "Label after set");
        TestLogger.Info($"NonFrozenMutableProps.Label set to {props.Label}");
    }

    public void TestNonFrozenMutablePropsSetBothProperties()
    {
        using var props = new NonFrozenMutableProps(value: 0, label: "start");
        props.Value = 100;
        props.Label = "end";
        AssertEqual(100, props.Value, "Value after set both");
        AssertEqual("end", props.Label, "Label after set both");
        TestLogger.Info($"NonFrozenMutableProps set both: {props.Value}, {props.Label}");
    }

    #endregion

    #region NonFrozenStructWithProperties — let/var/computed (mixed accessors)

    public void TestNonFrozenStructWithPropsConstruction()
    {
        using var s = new NonFrozenStructWithProperties(constantValue: 10, mutableValue: 20);
        AssertNotNull(s, "NonFrozenStructWithProperties constructed");
        TestLogger.Info("NonFrozenStructWithProperties construction passed");
    }

    public void TestNonFrozenStructWithPropsConstantValue()
    {
        using var s = new NonFrozenStructWithProperties(constantValue: 10, mutableValue: 20);
        AssertEqual(10, s.ConstantValue, "ConstantValue getter (let)");
        TestLogger.Info($"NonFrozenStructWithProperties.ConstantValue = {s.ConstantValue}");
    }

    public void TestNonFrozenStructWithPropsMutableValueGetter()
    {
        using var s = new NonFrozenStructWithProperties(constantValue: 10, mutableValue: 20);
        AssertEqual(20, s.MutableValue, "MutableValue getter");
        TestLogger.Info($"NonFrozenStructWithProperties.MutableValue = {s.MutableValue}");
    }

    public void TestNonFrozenStructWithPropsMutableValueSetter()
    {
        using var s = new NonFrozenStructWithProperties(constantValue: 10, mutableValue: 20);
        s.MutableValue = 50;
        AssertEqual(50, s.MutableValue, "MutableValue after set");
        TestLogger.Info($"NonFrozenStructWithProperties.MutableValue set to {s.MutableValue}");
    }

    public void TestNonFrozenStructWithPropsComputedDoubled()
    {
        using var s = new NonFrozenStructWithProperties(constantValue: 10, mutableValue: 7);
        AssertEqual(14, s.Doubled, "Doubled computed property (7 * 2)");
        TestLogger.Info($"NonFrozenStructWithProperties.Doubled = {s.Doubled}");
    }

    public void TestNonFrozenStructWithPropsComputedAfterSet()
    {
        // Verify computed property reflects mutation
        using var s = new NonFrozenStructWithProperties(constantValue: 10, mutableValue: 5);
        s.MutableValue = 15;
        AssertEqual(30, s.Doubled, "Doubled after MutableValue set to 15");
        TestLogger.Info($"NonFrozenStructWithProperties.Doubled after set = {s.Doubled}");
    }

    #endregion
}
