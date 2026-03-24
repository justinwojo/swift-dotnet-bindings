// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

#pragma warning disable SB0001 // CallConvSwift P/Invoke warning

namespace RuntimeTestsApp.Patterns;

/// <summary>
/// Tests the Lottie animation hierarchy inspection pattern:
/// - allKeypaths() returning string array
/// - Point/rect coordinate conversion with optional returns
/// - Node enable/disable by keypath
/// - Frame-based value query
///
/// Exercises L8 (Animation hierarchy inspection) from the library parity roadmap.
/// </summary>
public class HierarchyInspectionTests : TestBase
{
    public HierarchyInspectionTests(TestResults results) : base(results) { }

    #region Setup Helper

    private LayerContainer CreateTestHierarchy()
    {
        var container = new LayerContainer();
        var node1 = new LayerNode(
            name: "Background", isEnabled: true,
            x: 0, y: 0, width: 100, height: 100);
        var node2 = new LayerNode(
            name: "Shape Layer 1", isEnabled: true,
            x: 10, y: 20, width: 50, height: 50);
        var node3 = new LayerNode(
            name: "Fill 1", isEnabled: false,
            x: 15, y: 25, width: 30, height: 30);
        container.AddNode(node1, "**.Background");
        container.AddNode(node2, "**.Shape Layer 1");
        container.AddNode(node3, "**.Shape Layer 1.Fill 1");
        return container;
    }

    #endregion

    #region Keypath Enumeration

    [SkipOnSimulator("GetAllKeypaths uses CallConvSwift — array return not wrappable")]
    public void TestAllKeypathsPopulated()
    {
        using var container = CreateTestHierarchy();
        var keypaths = container.GetAllKeypaths();
        AssertEqual(3, keypaths.Count, "3 keypaths in hierarchy");
        AssertEqual("**.Background", keypaths[0], "First keypath");
        AssertEqual("**.Shape Layer 1", keypaths[1], "Second keypath");
        AssertEqual("**.Shape Layer 1.Fill 1", keypaths[2], "Third keypath");
        TestLogger.Info($"GetAllKeypaths: {keypaths.Count} paths returned");
    }

    public void TestLogKeypaths()
    {
        using var container = CreateTestHierarchy();
        var log = container.LogKeypaths();
        AssertTrue(log.Contains("**.Background"), "Log contains Background");
        AssertTrue(log.Contains("**.Shape Layer 1"), "Log contains Shape Layer 1");
        AssertTrue(log.Contains("**.Shape Layer 1.Fill 1"), "Log contains Fill 1");
        TestLogger.Info($"LogKeypaths:\n{log}");
    }

    #endregion

    #region Coordinate Conversion

    public void TestConvertPointValid()
    {
        using var container = CreateTestHierarchy();
        var point = container.ConvertPoint(30, 40, "**.Shape Layer 1");
        AssertTrue(point.HasValue, "Point conversion should succeed for existing layer");
        // Shape Layer 1 is at (10, 20), so (30,40) in container = (20,20) in layer
        AssertApproxEqual(20.0, point!.Value.X, message: "Converted X");
        AssertApproxEqual(20.0, point!.Value.Y, message: "Converted Y");
        TestLogger.Info($"ConvertPoint: (30,40) -> ({point!.Value.X},{point!.Value.Y})");
    }

    [Skip("SwiftOptional<CGPoint> None → Nullable<CGPoint> conversion returns HasValue=true — Mono JIT Nullable<struct> return issue")]
    public void TestConvertPointInvalidKeypath()
    {
        using var container = CreateTestHierarchy();
        var point = container.ConvertPoint(10, 10, "**.Nonexistent");
        AssertFalse(point.HasValue, "Point conversion returns null for invalid keypath");
        TestLogger.Info("ConvertPoint with invalid keypath returns null");
    }

    public void TestConvertRectValid()
    {
        using var container = CreateTestHierarchy();
        var rect = container.ConvertRect(25, 35, 40, 40, "**.Shape Layer 1");
        AssertTrue(rect.HasValue, "Rect conversion should succeed");
        // Shape Layer 1 at (10,20): origin shifts, size preserved
        AssertApproxEqual(15.0, rect!.Value.X, message: "Rect X");
        AssertApproxEqual(15.0, rect!.Value.Y, message: "Rect Y");
        AssertApproxEqual(40.0, rect!.Value.Width, message: "Rect width preserved");
        AssertApproxEqual(40.0, rect!.Value.Height, message: "Rect height preserved");
        TestLogger.Info($"ConvertRect: origin ({rect!.Value.X},{rect!.Value.Y}), size {rect!.Value.Width}x{rect!.Value.Height}");
    }

    [Skip("SwiftOptional<CGRect> None → Nullable<CGRect> conversion returns HasValue=true — Mono JIT Nullable<struct> return issue")]
    public void TestConvertRectInvalidKeypath()
    {
        using var container = CreateTestHierarchy();
        var rect = container.ConvertRect(0, 0, 10, 10, "**.Missing");
        AssertFalse(rect.HasValue, "Rect conversion returns null for invalid keypath");
        TestLogger.Info("ConvertRect with invalid keypath returns null");
    }

    #endregion

    #region Node Enable/Disable

    public void TestSetNodeEnabled()
    {
        using var container = CreateTestHierarchy();
        // Fill 1 starts disabled
        AssertFalse(container.IsNodeEnabled("**.Shape Layer 1.Fill 1"), "Fill 1 starts disabled");
        container.SetNodeEnabled(true, "**.Shape Layer 1.Fill 1");
        AssertTrue(container.IsNodeEnabled("**.Shape Layer 1.Fill 1"), "Fill 1 now enabled");
        TestLogger.Info("SetNodeEnabled toggles node state");
    }

    public void TestSetNodeDisabled()
    {
        using var container = CreateTestHierarchy();
        // Background starts enabled
        AssertTrue(container.IsNodeEnabled("**.Background"), "Background starts enabled");
        container.SetNodeEnabled(false, "**.Background");
        AssertFalse(container.IsNodeEnabled("**.Background"), "Background now disabled");
        TestLogger.Info("SetNodeEnabled(false) disables node");
    }

    public void TestIsNodeEnabledNonexistent()
    {
        using var container = CreateTestHierarchy();
        AssertFalse(container.IsNodeEnabled("**.Missing"), "Nonexistent node returns false");
        TestLogger.Info("IsNodeEnabled for missing keypath returns false");
    }

    #endregion

    #region Frame-Based Value Query

    public void TestGetValueAtFrame()
    {
        using var container = CreateTestHierarchy();
        // Background: 100x100, at frame 60 scale=1.0 -> value=10000
        var value = container.GetValueAtFrame("**.Background", 60.0);
        AssertTrue(value.HasValue, "Value should not be null");
        AssertApproxEqual(10000.0, value!.Value, message: "Background area at frame 60");
        TestLogger.Info($"GetValueAtFrame(Background, 60) = {value!.Value}");
    }

    public void TestGetValueAtFrameHalfway()
    {
        using var container = CreateTestHierarchy();
        // Background: 100x100, at frame 30 scale=0.5 -> value=5000
        var value = container.GetValueAtFrame("**.Background", 30.0);
        AssertTrue(value.HasValue, "Value should not be null");
        AssertApproxEqual(5000.0, value!.Value, message: "Background area at frame 30");
        TestLogger.Info($"GetValueAtFrame(Background, 30) = {value!.Value}");
    }

    public void TestGetValueAtFrameNonexistent()
    {
        using var container = CreateTestHierarchy();
        var value = container.GetValueAtFrame("**.Missing", 30.0);
        AssertFalse(value.HasValue, "Nonexistent keypath returns null");
        TestLogger.Info("GetValueAtFrame for missing keypath returns null");
    }

    #endregion
}
