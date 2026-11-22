// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation
import SwiftBindingsTestLibDependency

// Plain (non-@objc) integral enums whose explicit raw values are NOT their declaration order.
// A `.swiftinterface` prints no raw values for these (`case light = 1` prints as `case light`), so
// the binding cannot know them: the C# constant is the case index, every crossing must map it to
// exactly that case, and the raw value comes only from Swift's own `.rawValue` /
// `init(rawValue:)`. Every function reports what Swift actually received (its raw value), so a
// test can tell a wrong case from a right one; a mismatched crossing either answers with the
// wrong raw value or traps in `init(rawValue:)`.

/// Starts at 1, so the case index 0 names no case by raw value.
public enum RawShade: Int {
    case light = 1
    case dark = 2
}

/// Gaps, and an implicit raw value after an explicit one (`d` is 101).
public enum RawGapped: Int {
    case a = 1
    case b = 5
    case c = 100
    case d
}

/// Negative raw values, Int32-backed.
public enum RawSigned: Int32 {
    case minus = -7
    case zero = 0
    case plus = 9
}

/// A raw value wider than 32 bits.
public enum RawLarge: Int64 {
    case small = 3
    case huge = 9_000_000_000_000
}

/// A single case: Swift stores it in zero bytes.
public enum RawSolo: Int {
    case only = 7
}

/// UInt8-backed.
public enum RawByte: UInt8 {
    case low = 10
    case high = 250
}

/// A raw-valued enum nested in a type.
public struct RawPalette {
    public enum Tone: Int {
        case warm = 3
        case cool = 8
    }
}

/// A case floored above the enum: the managed side refuses it before it reaches Swift.
public enum RawStaged: Int32 {
    case base = 2

    @available(iOS 99.0, macOS 99.0, tvOS 99.0, macCatalyst 99.0, *)
    case future = 40
}

// MARK: - Arguments and returns

public func rawShadeValue(_ s: RawShade) -> Int { s.rawValue }
public func rawShadeName(_ s: RawShade) -> String { s == .light ? "light" : "dark" }
public func rawShadeEcho(_ s: RawShade) -> RawShade { s }
public func rawShadeFor(raw: Int) -> RawShade { RawShade(rawValue: raw) ?? .light }
public func rawShadeLookup(raw: Int) -> RawShade? { RawShade(rawValue: raw) }
public func rawShadeOptionalValue(_ s: RawShade?) -> Int { s?.rawValue ?? -1 }
public func rawShadeOptionalEcho(_ s: RawShade?) -> RawShade? { s }

public func rawGappedValue(_ v: RawGapped) -> Int { v.rawValue }
public func rawGappedFor(raw: Int) -> RawGapped { RawGapped(rawValue: raw) ?? .a }
public func rawSignedValue(_ v: RawSigned) -> Int32 { v.rawValue }
public func rawSignedFor(raw: Int32) -> RawSigned { RawSigned(rawValue: raw) ?? .zero }
public func rawLargeValue(_ v: RawLarge) -> Int64 { v.rawValue }
public func rawLargeFor(raw: Int64) -> RawLarge { RawLarge(rawValue: raw) ?? .small }
public func rawSoloValue(_ v: RawSolo) -> Int { v.rawValue }
public func rawSoloMake() -> RawSolo { .only }
public func rawByteValue(_ v: RawByte) -> UInt8 { v.rawValue }
public func rawByteFor(raw: UInt8) -> RawByte { RawByte(rawValue: raw) ?? .low }
public func rawToneValue(_ v: RawPalette.Tone) -> Int { v.rawValue }
public func rawToneFor(raw: Int) -> RawPalette.Tone { RawPalette.Tone(rawValue: raw) ?? .warm }
public func rawStagedValue(_ v: RawStaged) -> Int32 { v.rawValue }
public func rawStagedBase() -> RawStaged { .base }

// MARK: - Collections

public func rawGappedAll() -> [RawGapped] { [.a, .b, .c, .d] }
public func rawGappedRawValues(_ values: [RawGapped]) -> [Int] { values.map { $0.rawValue } }
public func rawShadeSetCount(_ values: Set<RawShade>) -> Int { values.count }
public func rawShadeSetContainsDark(_ values: Set<RawShade>) -> Bool { values.contains(.dark) }

// MARK: - Cross-module

public func rawDependencyCodeValue(_ code: DependencyRawCode) -> Int { code.rawValue }
public func rawDependencyCodeFor(raw: Int) -> DependencyRawCode { DependencyRawCode(rawValue: raw) ?? .alpha }

// MARK: - The enum's own members (self crosses)

extension RawGapped {
    /// `self` crosses into Swift.
    public var doubled: Int { rawValue * 2 }
    /// The case after this one, wrapping.
    public func following() -> RawGapped {
        switch self {
        case .a: return .b
        case .b: return .c
        case .c: return .d
        case .d: return .a
        }
    }
    /// A second argument of the same enum.
    public func sum(with other: RawGapped) -> Int { rawValue + other.rawValue }
    public static func fromSwift(raw: Int) -> RawGapped { RawGapped(rawValue: raw) ?? .a }
}

// MARK: - Properties and subscripts

public final class RawEnumHolder {
    public var shade: RawShade = .dark
    public var tone: RawPalette.Tone = .cool
    public var maybeShade: RawShade? = nil
    public var signed: RawSigned = .minus

    public init() {}

    public var shadeRawValue: Int { shade.rawValue }
    public var toneRawValue: Int { tone.rawValue }
    public var signedRawValue: Int32 { signed.rawValue }

    public subscript(raw: Int) -> RawGapped { RawGapped(rawValue: raw) ?? .a }
    public subscript(gapped: RawGapped) -> Int { gapped.rawValue }
}

public struct RawEnumValueBox {
    public var shade: RawShade
    public init(shade: RawShade) { self.shade = shade }
    public var shadeRawValue: Int { shade.rawValue }
}

// MARK: - Closures and callbacks

/// The callback receives a Swift case and hands one back.
public func rawShadeApply(_ s: RawShade, _ transform: (RawShade) -> RawShade) -> Int {
    transform(s).rawValue
}

/// An escaping callback, called before returning.
public func rawShadeNotify(_ s: RawShade, _ handler: @escaping (RawShade) -> Void) {
    handler(s)
}

/// A callback receiving the enum and returning an Int, so the received case is observable.
public func rawGappedVisit(_ handler: (RawGapped) -> Int) -> Int {
    handler(.c)
}

/// A vended closure: Swift builds the case from the argument's raw value.
public func rawShadeMaker() -> (Int) -> RawShade {
    { raw in RawShade(rawValue: raw) ?? .light }
}

/// A vended closure taking the enum.
public func rawShadeReader() -> (RawShade) -> Int {
    { $0.rawValue }
}

// MARK: - Protocols (C# implements, Swift calls)

public protocol RawShadeChooser {
    func choose(_ current: RawShade) -> RawShade
}

/// Asks the C# implementation for a shade and reports the raw value Swift received back.
public func rawShadeAsk(_ chooser: any RawShadeChooser, _ current: RawShade) -> Int {
    chooser.choose(current).rawValue
}

// MARK: - SwiftUI bridge probe

nonisolated(unsafe) private var _rawShadeViewLastRaw: Int = -1

/// What the last `RawShadeView` received: shade * 10000 + preferred * 100 + the shades' raw values
/// as digits (0 for a nil preferred).
public func rawShadeViewLastRaw() -> Int { _rawShadeViewLastRaw }

func rawShadeViewRecord(_ value: Int) { _rawShadeViewLastRaw = value }

// MARK: - Non-integral raw values

/// A frozen String-backed enum projects as a C# enum of case indices; its raw values, which the
/// interface does not print either, come from Swift. One is not ASCII and one is longer than the
/// helper's first buffer.
@frozen public enum RawFrozenTitle: String {
    case greeting = "hello"
    case farewell = "see you, \u{1F44B}"
    case lengthy = "xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx"
    case plain
}

public func rawFrozenTitleValue(_ t: RawFrozenTitle) -> String { t.rawValue }
public func rawFrozenTitleFor(raw: String) -> RawFrozenTitle { RawFrozenTitle(rawValue: raw) ?? .plain }

/// A non-frozen String-backed enum projects as a class whose raw value is read from Swift.
public enum RawTitle: String {
    case greeting = "hello"
    case plain
}

public func rawTitleValue(_ t: RawTitle) -> String { t.rawValue }

/// Floating-point and Character raw values: class projections that read the raw value from Swift.
public enum RawRatio: Double {
    case half = 0.5
    case whole = 1.0
}

public func rawRatioValue(_ r: RawRatio) -> Double { r.rawValue }
public func rawRatioFor(raw: Double) -> RawRatio { RawRatio(rawValue: raw) ?? .whole }

public enum RawLetter: Character {
    case ex = "x"
    case why = "Y"
}

public func rawLetterValue(_ l: RawLetter) -> String { String(l.rawValue) }

// MARK: - Method-closure bridge

/// A callback whose `RawShade` argument sits beside an `any Error`, which routes it through the
/// method-closure bridge rather than the plain closure adapter.
public final class RawShadeReporter {
    public let shade: RawShade

    public init(shade: RawShade) { self.shade = shade }

    // The `any Error` closure argument routes this method through the method-closure bridge,
    // which takes only class, primitive and String parameters: the shade rides on the receiver.
    public func report(callback: (RawShade, any Error) -> Void) {
        callback(shade, MathError.divisionByZero)
    }
}
