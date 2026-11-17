// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

// Thunk-only fixture for the `nuke swift-transition-gate` slice census.
//
// Every public member here is one the generator routes through a native assembly thunk rather than
// a @_cdecl Swift wrapper, so the module's wrapper links thunk objects and no Swift source. Such a
// slice has nothing for the lowering-evidence collector to compile to IR, which leaves the module
// unrouted: no receipt, no bridges. The census holds that artifact to the same transition rules,
// ledger cross-check and residual-guard check as the compile-check tree.
//
// Keep it thunk-only. A member that needs a Swift wrapper (a struct constructor, a closure, an
// async or generic member) turns the slice into an ordinary wrapper slice and fails the census.

public class ThunkOnlyCounter {
    private var value: Int64

    // Class allocating constructor: the metatype travels in the self register.
    public init(start: Int64) { value = start }

    // Non-final instance methods: dispatched through the vtable thunk, self in the self register.
    public func add(_ delta: Int64) -> Int64 {
        value &+= delta
        return value
    }

    public func scaled(by factor: Double) -> Double {
        Double(value) * factor
    }

    // Static class method: a hidden metatype argument.
    public static func origin() -> Int64 { 0 }
}
