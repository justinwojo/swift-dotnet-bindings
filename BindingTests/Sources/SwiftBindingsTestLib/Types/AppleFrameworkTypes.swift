// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation
import CoreGraphics

// MARK: - Foundation.Date Patterns
// Date.swift exists in Foundation/ but is excluded from Package.swift ("Separate from URL bridge scope").
// These patterns test Date as an Apple framework ObjC-bridged type in optional contexts.

/// Optional Date parameter — Date is ObjC-bridged, tests NullablePointer strategy.
public func processOptionalDate(_ date: Date?) -> String {
    guard let d = date else { return "nil" }
    return "\(d.timeIntervalSince1970)"
}

/// Date round-trip: create from epoch seconds and return.
public func dateFromEpoch(_ seconds: Double) -> Date {
    return Date(timeIntervalSince1970: seconds)
}

/// Check if a date is in the past.
public func isDateInPast(_ date: Date) -> Bool {
    return date < Date()
}

/// Date as a tuple element (A5 regression): returns the epoch-derived Date paired with the
/// truncated epoch seconds. The Date element must surface on the C# side as
/// System.DateTimeOffset with the same 2001-epoch conversion as the scalar Date path — not
/// as a bare double. Exercises WrapperEmitter's tuple-element type + marshalling seams.
public func dateEpochPair(_ seconds: Double) -> (date: Date, epoch: Int32) {
    return (date: Date(timeIntervalSince1970: seconds), epoch: Int32(seconds))
}

// MARK: - TimeInterval (Double) Patterns
// TimeInterval is a typealias for Double — common in Foundation APIs.

/// Optional TimeInterval (Double) parameter — exercises LargeOptionalPointer for Double.
public func processOptionalTimeInterval(_ interval: Double?) -> String {
    guard let t = interval else { return "nil" }
    return String(format: "%.1f", t)
}

// MARK: - Mixed Apple Framework Type Struct

/// Struct combining multiple Apple framework types — tests field marshalling.
public struct FrameworkTypeHolder {
    public var point: CGPoint
    public var label: String
    public var timestamp: Double

    public init(point: CGPoint, label: String, timestamp: Double) {
        self.point = point
        self.label = label
        self.timestamp = timestamp
    }

    public func describe() -> String {
        return "\(label) at (\(point.x),\(point.y)) t=\(timestamp)"
    }
}

/// Factory function for FrameworkTypeHolder.
public func makeFrameworkTypeHolder(x: CGFloat, y: CGFloat, label: String, timestamp: Double) -> FrameworkTypeHolder {
    return FrameworkTypeHolder(point: CGPoint(x: x, y: y), label: label, timestamp: timestamp)
}
