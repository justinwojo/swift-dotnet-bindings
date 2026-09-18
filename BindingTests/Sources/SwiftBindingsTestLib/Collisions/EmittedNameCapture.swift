// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Members of bound types named like the names the emitted code relies on
//
// Both generated layers write code INSIDE the scope of the types they bind: the C# binding puts
// marshalling bodies in the projected class, and the Swift wrapper puts its @_cdecl thunks and
// conformer shims in extensions of the Swift type. In either language a member of the enclosing
// type outranks a namespace, module or standard-library name of the same spelling, so a bound
// property called `swift` (projected `Swift`) captures every `Swift.Runtime...` reference in the
// class, and a protocol-extension typealias called `UnsafeMutablePointer` captures the bare stdlib
// name in every extension of a conforming type. Every name the generators emit must be spelled so
// no member can capture it (`global::` in C#, `Swift.` / `_Concurrency.` in Swift).
//
// Nothing here shadows a top-level standard-library name for this module's own sources: every
// colliding name is a member, so it only captures inside the declaring type's scope.

/// A nested raw-value enum whose cases project onto the namespace roots `Swift` and `System`.
public struct CaptureRenderer {
    public enum Format: String {
        case swift
        case system
        case png
    }

    public var format: Format

    public init(format: Format) { self.format = format }

    public func describe() -> String { "render:\(format.rawValue)" }

    public static func parse(_ raw: String) -> Format? { Format(rawValue: raw) }
}

/// A class whose members are named after the namespaces, runtime helpers and BCL types the
/// emitted marshalling references in expression position, plus nested types named after BCL
/// types that emitted code names in type position. Each member kind forces a different
/// marshalling path to be emitted into the same class body.
public class CaptureMemberHost {
    public init() {}

    public var swift: Int32 = 8
    public var system: Int32 = 9
    public var swiftBindingsTestLib: Int32 = 10
    public var runtime: Int32 = 11
    public var intPtr: Int32 = 12
    public var nativeMemory: Int32 = 13
    public var unsafe: Int32 = 14
    public var marshal: Int32 = 15
    public var memoryMarshal: Int32 = 16
    public var math: Int32 = 17
    public var gc: Int32 = 18
    public var task: Int32 = 19
    public var swiftString: Int32 = 20
    public var swiftObjectHelper: Int32 = 21
    public var typeMetadata: Int32 = 22

    public enum `Type` { case plain, fancy }
    public struct Task { public init() {}; public var ticket: Int32 = 5 }
    public struct Action { public init() {}; public var code: Int32 = 6 }

    public func echo(_ text: String) -> String { "echo:\(text)" }
    public func joined(_ items: [String]) -> [String] { items + ["end"] }
    public func maybe(_ text: String?) -> String? { text.map { "some:\($0)" } }
    public func compute() async -> Int32 { swift + system }
    public func apply(_ transform: @escaping (Int32) -> Int32) -> Int32 { transform(runtime) }
    public func checked(_ fail: Bool) throws -> Int32 {
        if fail { throw CaptureFailure.rejected }
        return marshal
    }
    public func render(_ renderer: CaptureRenderer) -> String { renderer.describe() }
    public func nextTask() -> Task { Task() }
    public func nextAction() -> Action { Action() }
    public func kind(_ fancy: Bool) -> `Type` { fancy ? .fancy : .plain }
}

public enum CaptureFailure: Error {
    case rejected
}

/// A protocol whose extension declares statics, properties and typealiases named like the
/// standard-library functions and types the Swift wrapper emits. The wrapper's conformer and
/// thunks live in extensions of conforming types, where these members outrank the stdlib names.
public protocol CaptureStdlibNames {
    func captureValue() -> Int32
    func captureLabel(_ text: String) -> String
    var captureItems: [Int32] { get set }
}

extension CaptureStdlibNames {
    public static var max: Int32 { 0 }
    public static var min: Int32 { 0 }
    public static var type: Int32 { 0 }
    public var withUnsafeBytes: Int32 { 0 }
    public var unsafeBitCast: Int32 { 0 }
    public var withExtendedLifetime: Int32 { 0 }
    public static var fatalError: Int32 { 0 }
    public static var precondition: Int32 { 0 }
    public static var preconditionFailure: Int32 { 0 }
    public typealias Unmanaged = Int32
    public typealias MemoryLayout = Int32
    public typealias UnsafeMutableRawPointer = Int32
    public typealias UnsafeMutablePointer = Int32
    public typealias UnsafeRawPointer = Int32
    public typealias UnsafeBufferPointer = Int32
    public typealias UTF8 = Int32
}

/// Drives a C#-implemented conformer through every protocol requirement from Swift.
public func captureStdlibNamesSummary(_ source: CaptureStdlibNames) -> String {
    var source = source
    source.captureItems.append(source.captureValue())
    return "\(source.captureLabel("x")):\(source.captureItems.map(String.init).joined(separator: ","))"
}

/// A Swift-side conformer, so the wrapper also binds the protocol's members on a concrete type.
public final class CaptureStdlibNamesImpl: CaptureStdlibNames {
    public var captureItems: [Int32]
    public init(captureItems: [Int32]) { self.captureItems = captureItems }
    public func captureValue() -> Int32 { Int32(captureItems.count) }
    public func captureLabel(_ text: String) -> String { "impl:\(text)" }
}

/// A class with a nested class named like a BCL collection type, declared in its own file of the
/// binding, and a subclass whose nested class subclasses it. The C# binding names the inherited
/// nested class by its bare name inside the subclass (`PriorityQueue : Queue`), where C# lookup
/// finds it through inheritance ahead of the BCL `Queue<T>`.
public class CaptureQueuePlayer {
    public init() {}

    public class Queue {
        public init() {}
        public func depth() -> Int32 { 1 }
    }

    public func makeQueue() -> Queue { Queue() }
}

public final class CaptureAppQueuePlayer: CaptureQueuePlayer {
    public override init() { super.init() }

    public final class PriorityQueue: CaptureQueuePlayer.Queue {
        public override init() { super.init() }
        public override func depth() -> Int32 { 2 }
    }

    public func makePriorityQueue() -> PriorityQueue { PriorityQueue() }
}

/// A struct with stored members named like stdlib globals the wrapper calls unqualified.
public struct CaptureQualifierHost {
    public var max: Int32
    public var min: Int32
    public var print: Int32

    public init(max: Int32, min: Int32, print: Int32) {
        self.max = max
        self.min = min
        self.print = print
    }

    public func pick<T>(_ value: T) -> T { value }
    public func span() -> Int32 { max - min }
    public func label() -> String { "q:\(print)" }
}
