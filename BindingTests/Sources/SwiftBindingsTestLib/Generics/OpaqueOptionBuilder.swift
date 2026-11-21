// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

// MARK: - Per-conformer specializations of a static opaque-parameter builder
//
// The shape of a result-builder's `buildFinalResult`: a static method on a
// public struct that takes `some P` and returns `[any P]`. The generator binds
// it once per public conformer of `P` as a concrete overload, each through its
// own wrapper. Each such specialization is its own recovery unit: if the
// generic member is withdrawn, the specializations still stand, so this
// fixture calls every one of them and checks the value each returns.
//
// The conformers are resilient (non-frozen) structs, so each crosses the
// boundary as an opaque payload, and the result is an array of existentials.

/// The option protocol the builder collects.
public protocol LedgerOption {
    /// A code that identifies the option and the value it was made with.
    var optionCode: Int32 { get }
}

/// An option carrying a count.
public struct LedgerCountOption: LedgerOption {
    public let count: Int
    public init(_ count: Int) { self.count = count }
    public var optionCode: Int32 { 1000 + Int32(count) }
}

/// An option carrying a flag.
public struct LedgerFlagOption: LedgerOption {
    public let enabled: Bool
    public init(_ enabled: Bool) { self.enabled = enabled }
    public var optionCode: Int32 { enabled ? 2001 : 2000 }
}

/// An option carrying a duration in seconds.
public struct LedgerDurationOption: LedgerOption {
    public let seconds: Double
    public init(_ seconds: Double) { self.seconds = seconds }
    public var optionCode: Int32 { 3000 + Int32(seconds) }
}

/// The builder: one static generic method over `some LedgerOption`.
public struct LedgerOptionsBuilder {
    public static func buildFinalResult(_ component: some LedgerOption) -> [any LedgerOption] {
        [component]
    }
}
