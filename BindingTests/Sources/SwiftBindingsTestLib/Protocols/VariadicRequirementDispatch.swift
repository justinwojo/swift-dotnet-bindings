// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

// Protocol requirements with a variadic parameter. The ABI JSON spells `T...` as `Array<T>`, so a
// synthesized witness that copies the ABI type verbatim declares `_ tags: [Int32]` — a different
// function type from the requirement's `_ tags: Int32...`, and the conformance fails to type-check.
// A protocol may also declare the variadic and the array spelling side by side (a common
// convenience pair); those are distinct requirements that each need their own witness, so the two
// must not collapse into one signature. The existential variant covers a variadic of protocol
// type, and the generic protocol covers the method-level-generic stub path, which renders its
// signature separately.

import Foundation

public protocol VariadicTagSink: AnyObject {
    func record(_ label: String, _ tags: Int32...) -> Int32
    func record(_ label: String, _ tags: [Int32]) -> Int32
    func describe(_ items: any CustomStringConvertible...) -> String
}

/// Calls the variadic requirement through the existential, the way a library-internal caller would.
public func variadicTagSinkRecordVariadic(_ sink: any VariadicTagSink, label: String) -> Int32 {
    sink.record(label, 1, 2, 3)
}

/// Calls the array-spelled twin through the existential.
public func variadicTagSinkRecordArray(_ sink: any VariadicTagSink, label: String, tags: [Int32]) -> Int32 {
    sink.record(label, tags)
}

/// Calls the existential variadic requirement with mixed element types.
public func variadicTagSinkDescribe(_ sink: any VariadicTagSink) -> String {
    sink.describe(7, "x")
}

public protocol VariadicGenericSink: AnyObject {
    func recordAll<T>(_ first: T, _ rest: T...) -> Int32
    func recordAll<T>(_ first: T, _ rest: [T]) -> Int32
    func taggedCount() -> Int32
}
