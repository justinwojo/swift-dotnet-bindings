// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Generics;

/// <summary>
/// A generic struct constrained <c>Codable &amp; Equatable &amp; Sendable</c>, bound to stdlib
/// scalars. The generator proves each scalar's Codable conformance from its stdlib fact table;
/// before every scalar carried the Codable pair there, each member below was skipped as an
/// unsatisfied constraint. Reading the values back proves the bound generics marshal.
/// </summary>
public class StdlibScalarCodableConstraintTests : TestBase
{
    public StdlibScalarCodableConstraintTests(TestResults results) : base(results) { }

    public void TestFloatFactory()
    {
        using var box = Functions.MakeCodableFloatBox(1.5f);
        AssertEqual(1.5f, box.Value, "CodableScalarBox<Float>.value");
    }

    public void TestEveryScalarProperty()
    {
        using var readings = new CodableScalarReadings(0.25f, -2.5, -40000, 200, true, "tenor");
        using var gain = readings.Gain;
        using var offset = readings.Offset;
        using var count = readings.Count;
        using var level = readings.Level;
        using var enabled = readings.Enabled;
        using var label = readings.Label;
        AssertEqual(0.25f, gain.Value, "Float");
        AssertEqual(-2.5, offset.Value, "Double");
        AssertEqual(-40000, count.Value, "Int32");
        AssertEqual((byte)200, level.Value, "UInt8");
        AssertTrue(enabled.Value, "Bool");
        using var labelValue = label.Value;
        AssertEqual("tenor", labelValue.ToString(), "String");
    }

    public void TestEquality_OnScalarBoundBox()
    {
        using var a = Functions.MakeCodableFloatBox(3.0f);
        using var b = Functions.MakeCodableFloatBox(3.0f);
        using var c = Functions.MakeCodableFloatBox(4.0f);
        AssertTrue(a == b, "equal Float boxes compare equal");
        AssertFalse(a == c, "different Float boxes compare unequal");
    }
}
