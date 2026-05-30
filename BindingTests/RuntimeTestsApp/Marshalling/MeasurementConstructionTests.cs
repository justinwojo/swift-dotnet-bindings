// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Foundation;
using RuntimeTestsApp.Infrastructure;
using Swift;
using Swift.Foundation;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Marshalling;

/// <summary>
/// End-to-end coverage for constructing <c>Foundation.Measurement&lt;T&gt;</c> from C#
/// (the WorkoutKit range-alert surface). Three things must work together:
///   <list type="number">
///     <item>the <c>Measurement&lt;T&gt;(double, T unit)</c> ctor, which runs the real
///       Swift initializer through the <c>SBW_Measurement_InitFromValueUnit</c> shim;</item>
///     <item>passing a C#-constructed Measurement back into Swift (value preserved);</item>
///     <item><c>SwiftClosedRange&lt;Measurement&lt;T&gt;&gt;</c> construction, which requires
///       the conditional <c>Measurement : Comparable where UnitType : Dimension</c>
///       conformance descriptor to resolve the Bound's Comparable witness table.</item>
///   </list>
/// Runs on simulator (Mono) and device (NativeAOT): the WorkoutKit consumer validates
/// §4 on the simulator lane, so this gate must too.
/// </summary>
public class MeasurementConstructionTests : TestBase
{
    public MeasurementConstructionTests(TestResults results) : base(results) { }

    #region Construct + read back

    public void TestConstructMeasurementValueRoundTrips()
    {
        using var m = new Measurement<NSUnitLength>(42.5, NSUnitLength.Meters);
        AssertApproxEqual(42.5, m.Value, message: "Constructed Measurement<NSUnitLength>.Value");
        AssertTrue(m.UnitHandle != IntPtr.Zero, "Constructed Measurement has a non-zero unit handle");
    }

    public void TestConstructMeasurementRoundTripsThroughSwift()
    {
        using var m = new Measurement<NSUnitLength>(7.5, NSUnitLength.Meters);
        // Hand the C#-built value to a Swift function that reads `.value` back out.
        var value = Functions.MeasurementLengthValue(m);
        AssertApproxEqual(7.5, value, message: "C#-constructed Measurement round-trips through Swift");
    }

    public void TestConstructMeasurementRejectsNullUnit()
    {
        AssertThrows<ArgumentNullException>(
            () => { _ = new Measurement<NSUnitLength>(1.0, null!); },
            "Constructing with a null unit throws");
    }

    public void TestConstructMeasurementRejectsNonNSUnitType()
    {
        // T is constrained only to `class` (there is no managed NSUnit base type in the bindings),
        // so a caller can supply an ObjC object whose dynamic type is not an NSUnit subclass. NSObject
        // passes the INativeObject/non-null-handle guards but is not a Foundation.Unit, so the Swift
        // shim's conditional `as? Unit` cast fails. The ctor must surface that as a managed
        // ArgumentException — never the `as!` process trap that would abort the whole app.
        AssertThrows<ArgumentException>(
            () => { _ = new Measurement<NSObject>(1.0, new NSObject()); },
            "Constructing with a non-NSUnit ObjC unit throws ArgumentException");
    }

    #endregion

    #region SwiftClosedRange<Measurement<T>> — the range-alert bound shape

    public void TestClosedRangeOfMeasurementConstructsAndReadsBounds()
    {
        using var lo = new Measurement<NSUnitLength>(60.0, NSUnitLength.Meters);
        using var hi = new Measurement<NSUnitLength>(180.0, NSUnitLength.Meters);

        // Constructing the range forces Measurement<NSUnitLength>'s Comparable
        // witness table to instantiate (ClosedRange<Bound: Comparable>). Reading the
        // bounds back proves the marshalled bytes are a valid Measurement.
        using var range = new SwiftClosedRange<Measurement<NSUnitLength>>(lo, hi);
        AssertApproxEqual(60.0, range.LowerBound.Value, message: "ClosedRange<Measurement>.LowerBound.Value");
        AssertApproxEqual(180.0, range.UpperBound.Value, message: "ClosedRange<Measurement>.UpperBound.Value");
    }

    #endregion
}
