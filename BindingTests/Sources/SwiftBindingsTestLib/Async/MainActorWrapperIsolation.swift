// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation
import SwiftBindingsTestLibDependency

// MARK: - Wrappers that call a main-actor member synchronously
//
// Each wrapper below is a nonisolated top-level function unless the generator carries the member's
// main-actor isolation onto it; swiftc (6.2 and 6.4 alike, even under -strict-concurrency=minimal)
// then refuses the call ("call to main actor-isolated ... in a synchronous nonisolated context")
// and verify-recover withdraws the member. Each shape takes a different wrapper emitter: the
// operator wrapper of a frozen struct, the member wrappers of a simple enum, and the trampoline of
// a cross-module extension on a dependency's frozen struct.

/// Operators of a main-actor frozen struct are main-actor isolated.
@MainActor
@frozen
public struct MainActorTally {
    public let count: Int32

    public init(count: Int32) {
        self.count = count
    }

    public static func + (lhs: MainActorTally, rhs: MainActorTally) -> MainActorTally {
        MainActorTally(count: lhs.count + rhs.count)
    }

    public static prefix func - (operand: MainActorTally) -> MainActorTally {
        MainActorTally(count: -operand.count)
    }
}

/// Members of a main-actor simple enum are main-actor isolated (its synthesized `rawValue` and
/// `init(rawValue:)` are not).
@MainActor
public enum MainActorSignal: Int32 {
    case red = 1
    case green = 2
    case blue = 3

    public var weight: Int32 { rawValue * 10 }

    public func scaled(by factor: Int32) -> Int32 { rawValue * factor }

    public static var brightest: MainActorSignal { .blue }

    public static func offset(_ value: Int32) -> Int32 { value + 100 }
}

/// Main-actor members added to a dependency's frozen struct from this module.
extension DependencyPoint {
    @MainActor
    public func mainActorSum() -> Double {
        x + y
    }

    @MainActor
    public var mainActorSpan: Double {
        abs(x) + abs(y)
    }
}
