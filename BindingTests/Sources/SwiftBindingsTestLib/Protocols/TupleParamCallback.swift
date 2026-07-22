// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Tuple-parameter reverse-callback regression
//
// When Swift calls back into a C# protocol implementation with a method whose
// parameter is a *tuple with projected elements* — e.g. `(Date, Date)`, which
// crosses the ABI as `ValueTuple<double, double>` but is surfaced to the C#
// interface as `(DateTimeOffset, DateTimeOffset)` — the generated proxy
// receiver used to pass the raw ABI carrier straight to the implementation
// (CS1503: cannot convert `(double, double)` to `(DateTimeOffset,
// DateTimeOffset)`). The fix lifts each element through its own Swift→C#
// conversion before the interface call; pure-blittable tuples keep the
// passthrough shape.
//
// The driver is synchronous (same-thread callback), so the C# test needs no
// runloop pumping. Element values are Swift reference-date second offsets, so
// the C# side can assert exact `DateTimeOffset` round-trips against the Swift
// epoch (2001-01-01T00:00:00Z).

/// Class-bound protocol whose callback receives a `(Date, Date)` tuple — the
/// per-element-projected shape — and a `(Int32, Int32)` tuple — the
/// pure-blittable passthrough shape that must remain unaffected by the lift.
public protocol DateRangeReceiver: AnyObject {
    /// Projected-element tuple: ABI `(Double, Double)`, interface `(DateTimeOffset, DateTimeOffset)`.
    func didReceiveRange(_ range: (Date, Date))
    /// Pure-blittable tuple: passthrough, no element conversion.
    func didReceiveCounts(_ counts: (Int32, Int32))
}

/// Synchronous driver: constructs the tuples Swift-side from caller-supplied
/// scalars and calls back into the receiver on the same thread.
public class DateRangeDriver {
    public init() {}

    /// Drive the `(Date, Date)` callback. The dates are built from reference-date
    /// offsets so the expected `DateTimeOffset` values are exact (no clock reads).
    public func driveRange(_ receiver: DateRangeReceiver, startSeconds: Double, endSeconds: Double) {
        let start = Date(timeIntervalSinceReferenceDate: startSeconds)
        let end = Date(timeIntervalSinceReferenceDate: endSeconds)
        receiver.didReceiveRange((start, end))
    }

    /// Drive the pure-blittable `(Int32, Int32)` callback.
    public func driveCounts(_ receiver: DateRangeReceiver, first: Int32, second: Int32) {
        receiver.didReceiveCounts((first, second))
    }
}
