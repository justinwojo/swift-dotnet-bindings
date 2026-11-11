// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation
import CoreMedia

// MARK: - CoreMedia time value types (CMTime / CMTimeRange / CMTimeMapping)
//
// CMTime is a 24-byte C struct (value Int64, timescale Int32, flags UInt32, epoch Int64);
// CMTimeRange is two of them (48 bytes) and CMTimeMapping two ranges (96 bytes). Media
// frameworks hand them out as stored properties of result structs, as arrays, and as plain
// parameters and returns — the shapes below. Every value is distinct per field so a
// truncated, shifted or misaligned copy shows up as a wrong field rather than a lucky match.

/// A result struct with a stored CMTime, like a timed analysis sample.
public struct TimedSample {
    public let time: CMTime
    public let value: Double

    public init(time: CMTime, value: Double) {
        self.time = time
        self.value = value
    }
}

/// A result struct with a stored CMTimeRange next to a reference-counted field.
public struct TimedSpan {
    public let range: CMTimeRange
    public let label: String

    public init(range: CMTimeRange, label: String) {
        self.range = range
        self.label = label
    }
}

/// A result struct with arrays of CMTime and CMTimeRange, like beat and section lists.
public struct TimeTrack {
    public let beats: [CMTime]
    public let sections: [CMTimeRange]

    public init(beats: [CMTime], sections: [CMTimeRange]) {
        self.beats = beats
        self.sections = sections
    }
}

/// A class with mutable CMTime and CMTimeRange properties.
public final class CMTimeBox {
    public var time: CMTime
    public var range: CMTimeRange

    public init(time: CMTime, range: CMTimeRange) {
        self.time = time
        self.range = range
    }
}

public func makeCMTime(value: Int64, timescale: Int32, flags: UInt32, epoch: Int64) -> CMTime {
    return CMTime(value: value, timescale: timescale, flags: CMTimeFlags(rawValue: flags), epoch: epoch)
}

/// Renders every field so the caller can check what Swift actually received.
public func describeCMTime(_ time: CMTime) -> String {
    return "\(time.value)/\(time.timescale)/\(time.flags.rawValue)/\(time.epoch)"
}

public func echoCMTime(_ time: CMTime) -> CMTime {
    return time
}

/// Scalars on both sides of the struct catch an argument-slot shift.
public func describeCMTimeBetween(_ leading: Int32, _ time: CMTime, _ trailing: Int64) -> String {
    return "\(leading)|\(describeCMTime(time))|\(trailing)"
}

public func shiftCMTime(_ time: CMTime, by delta: Int64) -> CMTime {
    return CMTime(value: time.value + delta, timescale: time.timescale, flags: time.flags, epoch: time.epoch)
}

public func makeCMTimeRange(start: CMTime, duration: CMTime) -> CMTimeRange {
    return CMTimeRange(start: start, duration: duration)
}

public func describeCMTimeRange(_ range: CMTimeRange) -> String {
    return "[\(describeCMTime(range.start)) + \(describeCMTime(range.duration))]"
}

public func echoCMTimeRange(_ range: CMTimeRange) -> CMTimeRange {
    return range
}

public func echoCMTimeMapping(_ mapping: CMTimeMapping) -> CMTimeMapping {
    return mapping
}

public func describeCMTimeMapping(_ mapping: CMTimeMapping) -> String {
    return "\(describeCMTimeRange(mapping.source)) -> \(describeCMTimeRange(mapping.target))"
}

/// `count` times with value i*10+1, timescale 600, valid flag, epoch i.
public func makeCMTimes(count: Int) -> [CMTime] {
    return (0..<count).map { i in
        CMTime(value: Int64(i * 10 + 1), timescale: 600, flags: .valid, epoch: Int64(i))
    }
}

public func describeCMTimes(_ times: [CMTime]) -> String {
    return times.map(describeCMTime).joined(separator: ",")
}

public func makeTimedSample(value: Int64, timescale: Int32, epoch: Int64, sample: Double) -> TimedSample {
    return TimedSample(time: CMTime(value: value, timescale: timescale, flags: .valid, epoch: epoch), value: sample)
}

public func makeTimedSpan(startValue: Int64, durationValue: Int64, timescale: Int32, label: String) -> TimedSpan {
    let range = CMTimeRange(
        start: CMTime(value: startValue, timescale: timescale, flags: .valid, epoch: 1),
        duration: CMTime(value: durationValue, timescale: timescale, flags: .valid, epoch: 2))
    return TimedSpan(range: range, label: label)
}

/// `beatCount` beats (value i*100+7, timescale 44100) and `sectionCount` sections
/// (start i*1000, duration 500+i, timescale 48000).
public func makeTimeTrack(beatCount: Int, sectionCount: Int) -> TimeTrack {
    let beats = (0..<beatCount).map { i in
        CMTime(value: Int64(i * 100 + 7), timescale: 44100, flags: .valid, epoch: 0)
    }
    let sections = (0..<sectionCount).map { i in
        CMTimeRange(
            start: CMTime(value: Int64(i * 1000), timescale: 48000, flags: .valid, epoch: 0),
            duration: CMTime(value: Int64(500 + i), timescale: 48000, flags: .valid, epoch: 0))
    }
    return TimeTrack(beats: beats, sections: sections)
}

/// CoreMedia packs CMTime to 4-byte alignment while the .NET struct is 8-byte aligned, so after a
/// 4-byte field the two layouts disagree: Swift places `time` at offset 4, a sequential .NET
/// struct at 8. A frozen struct is the shape whose layout a binding could be tempted to copy.
@frozen public struct TaggedTime {
    public var tag: Int32
    public var time: CMTime

    public init(tag: Int32, time: CMTime) {
        self.tag = tag
        self.time = time
    }
}

public func makeTaggedTime(tag: Int32, value: Int64, timescale: Int32, epoch: Int64) -> TaggedTime {
    return TaggedTime(tag: tag, time: CMTime(value: value, timescale: timescale, flags: .valid, epoch: epoch))
}

public func describeTaggedTime(_ tagged: TaggedTime) -> String {
    return "\(tagged.tag)@\(describeCMTime(tagged.time))"
}

/// An array strides the frozen struct by Swift's 28 bytes; each element must still read back whole.
public func makeTaggedTimes(count: Int) -> [TaggedTime] {
    var items: [TaggedTime] = []
    for i in 0..<count {
        let time = CMTime(value: Int64(i * 7 + 1), timescale: 600, flags: .valid, epoch: Int64(i))
        items.append(TaggedTime(tag: Int32(-i), time: time))
    }
    return items
}

public func describeTaggedTimes(_ items: [TaggedTime]) -> String {
    return items.map(describeTaggedTime).joined(separator: ",")
}

public func maybeCMTime(_ present: Bool, value: Int64) -> CMTime? {
    return present ? CMTime(value: value, timescale: 90000, flags: .valid, epoch: 3) : nil
}

public func describeOptionalCMTime(_ time: CMTime?) -> String {
    return time.map(describeCMTime) ?? "nil"
}
