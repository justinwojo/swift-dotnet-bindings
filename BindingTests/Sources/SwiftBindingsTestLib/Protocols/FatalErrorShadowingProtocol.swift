// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

// A protocol whose constrained extension declares a static member named `fatalError` — the
// "static factory for a built-in conformer" idiom (`.fatalError`, `.runtimeWarning`). Inside any
// type conforming to the protocol, an unqualified `fatalError(...)` resolves to that static member
// first, so a synthesized stub body calling `fatalError("…")` no longer means `Swift.fatalError` and
// fails to compile. The requirement below has an `@autoclosure` parameter, which cannot be
// dispatched and therefore gets a trapping stub.

import Foundation

public protocol TrapReporter: Sendable {
    func report(_ message: @autoclosure () -> String?, line: UInt)
    func reportedCount() -> Int32
}

public struct _FatalTrapReporter: TrapReporter {
    public init() {}
    public func report(_ message: @autoclosure () -> String?, line: UInt) {}
    public func reportedCount() -> Int32 { 0 }
}

extension TrapReporter where Self == _FatalTrapReporter {
    public static var fatalError: Self { Self() }
}

/// Calls the dispatchable requirement through the existential.
public func trapReporterCount(_ reporter: any TrapReporter) -> Int32 {
    reporter.reportedCount()
}
