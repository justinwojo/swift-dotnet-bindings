// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;

namespace RuntimeTestsApp.Marshalling;

/// <summary>
/// Tests Foundation.Measurement&lt;T&gt; non-frozen generic struct projection.
/// Verifies VWT-backed storage, Value property, and round-trip through P/Invoke.
/// Most tests require Measurement metadata resolution which crashes on Mono JIT
/// (upstream Issue 1: !ji->async assertion on generic metadata accessor).
/// </summary>
[SkipOnSimulator("Mono JIT crashes resolving Measurement<T> generic metadata (upstream Issue 1)")]
public class MeasurementTests : TestBase
{
    public MeasurementTests(TestResults results) : base(results) { }

    #region Measurement as Return

    public void TestMeasurementLengthReturn()
    {
        using var m = SwiftBindingsTestLib.Functions.MeasurementLengthMeters(42.5);
        AssertEqual(42.5, m.Value, "Measurement<UnitLength>.Value from return");
    }

    public void TestMeasurementTemperatureReturn()
    {
        using var m = SwiftBindingsTestLib.Functions.MeasurementTemperatureCelsius(22.0);
        AssertEqual(22.0, m.Value, "Measurement<UnitTemperature>.Value from return");
    }

    #endregion

    #region Measurement as Parameter

    public void TestMeasurementLengthParam()
    {
        using var m = SwiftBindingsTestLib.Functions.MeasurementLengthMeters(7.5);
        var value = SwiftBindingsTestLib.Functions.MeasurementLengthValue(m);
        AssertEqual(7.5, value, "Measurement<UnitLength> param preserves value");
    }

    public void TestMeasurementTemperatureParam()
    {
        using var m = SwiftBindingsTestLib.Functions.MeasurementTemperatureCelsius(100.0);
        var value = SwiftBindingsTestLib.Functions.MeasurementTemperatureValue(m);
        AssertEqual(100.0, value, "Measurement<UnitTemperature> param preserves value");
    }

    #endregion

    #region Measurement Round-Trip

    public void TestMeasurementRoundTrip()
    {
        using var m = SwiftBindingsTestLib.Functions.MeasurementLengthMeters(5.0);
        using var result = SwiftBindingsTestLib.Functions.AddTenToLength(m);
        AssertEqual(15.0, result.Value, "Measurement round-trip: 5 + 10 = 15");
    }

    #endregion

    #region Struct with Measurement Properties

    public void TestWeatherReadingTemperature()
    {
        using var reading = SwiftBindingsTestLib.Functions.GetSampleWeatherReading();
        AssertEqual(18.5, reading.Temperature.Value, "WeatherReading.Temperature.Value");
    }

    public void TestWeatherReadingWindSpeed()
    {
        using var reading = SwiftBindingsTestLib.Functions.GetSampleWeatherReading();
        AssertEqual(12.0, reading.WindSpeed.Value, "WeatherReading.WindSpeed.Value");
    }

    public void TestWeatherReadingLocation()
    {
        using var reading = SwiftBindingsTestLib.Functions.GetSampleWeatherReading();
        AssertEqual("San Francisco", reading.Location, "WeatherReading.Location");
    }

    #endregion

    #region UnitHandle

    public void TestMeasurementUnitHandleNonZero()
    {
        using var m = SwiftBindingsTestLib.Functions.MeasurementTemperatureCelsius(0.0);
        AssertTrue(m.UnitHandle != IntPtr.Zero, "Measurement.UnitHandle is non-zero");
    }

    #endregion
}
