// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Date as Parameter

/// Extracts the year from the given date using the current calendar.
public func dateYear(_ date: Date) -> Int32 {
    let calendar = Calendar.current
    return Int32(calendar.component(.year, from: date))
}

// MARK: - Date as Return

/// Returns the current date and time.
public func currentDate() -> Date {
    return Date()
}

// MARK: - Two Date Parameters

/// Returns true if the first date is before the second date.
public func isDateBefore(_ date: Date, other: Date) -> Bool {
    return date < other
}

// MARK: - Date Arithmetic

/// Returns a new date by adding the specified number of seconds.
public func dateByAdding(seconds: Double, to date: Date) -> Date {
    return date.addingTimeInterval(seconds)
}

// MARK: - Struct with Date Property

/// An event with a name and a timestamp.
public struct TimestampedEvent {
    public var name: String
    public var timestamp: Date

    public init(name: String, timestamp: Date) {
        self.name = name
        self.timestamp = timestamp
    }

    /// Returns the number of seconds since the event occurred.
    public func secondsAgo() -> Double {
        return -timestamp.timeIntervalSinceNow
    }
}

// MARK: - Optional Date

/// Returns an optional Date from an optional epoch seconds value.
public func optionalDate(epochSeconds: Double?) -> Date? {
    guard let seconds = epochSeconds else { return nil }
    return Date(timeIntervalSince1970: seconds)
}

// MARK: - Struct with Optional Date Properties

/// A config struct with optional Date properties.
public struct EventConfig {
    public var label: String
    public var startDate: Date?
    public var endDate: Date?

    public init(label: String, startDate: Date?, endDate: Date?) {
        self.label = label
        self.startDate = startDate
        self.endDate = endDate
    }
}

// MARK: - Date as Enum Associated Value

/// Enum with Date-typed associated value.
public enum SchedulerEvent {
    case scheduled(at: Date)
    case cancelled
}
