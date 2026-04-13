// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Measurement as Return

/// Returns a Measurement<UnitLength> with the given value in meters.
public func measurementLengthMeters(_ value: Double) -> Measurement<UnitLength> {
    return Measurement(value: value, unit: .meters)
}

/// Returns a Measurement<UnitTemperature> with the given value in celsius.
public func measurementTemperatureCelsius(_ value: Double) -> Measurement<UnitTemperature> {
    return Measurement(value: value, unit: .celsius)
}

// MARK: - Measurement as Parameter

/// Extracts the numeric value from a Measurement<UnitLength>.
public func measurementLengthValue(_ m: Measurement<UnitLength>) -> Double {
    return m.value
}

/// Extracts the numeric value from a Measurement<UnitTemperature>.
public func measurementTemperatureValue(_ m: Measurement<UnitTemperature>) -> Double {
    return m.value
}

// MARK: - Struct with Measurement Properties

/// A weather reading with temperature and wind speed measurements.
public struct WeatherReading {
    public var location: String
    public var temperature: Measurement<UnitTemperature>
    public var windSpeed: Measurement<UnitSpeed>

    public init(location: String, temperature: Measurement<UnitTemperature>, windSpeed: Measurement<UnitSpeed>) {
        self.location = location
        self.temperature = temperature
        self.windSpeed = windSpeed
    }
}

/// Creates a sample weather reading.
public func sampleWeatherReading() -> WeatherReading {
    return WeatherReading(
        location: "San Francisco",
        temperature: Measurement(value: 18.5, unit: .celsius),
        windSpeed: Measurement(value: 12.0, unit: .kilometersPerHour)
    )
}

// MARK: - Measurement Round-Trip

/// Takes a Measurement<UnitLength>, adds 10 to its value, and returns the result.
public func addTenToLength(_ m: Measurement<UnitLength>) -> Measurement<UnitLength> {
    return Measurement(value: m.value + 10.0, unit: m.unit)
}
