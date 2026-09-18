// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Optional existential parameters kept by a default-argument overload
//
// A reduced-arity overload is two Swift functions: a @_silgen_name shim that re-declares the
// original with trailing defaults trimmed, and a @_cdecl wrapper that calls the shim. The shim
// takes a large Optional parameter as a raw buffer address. The wrapper must pass that same
// address through rather than loading the Optional itself; if the two disagree on which
// parameters are passed by address, swiftc rejects the wrapper and every overload of the member
// is withdrawn.
//
// The trailing `flag` default is a constructor call, which has no C# literal spelling, so it
// forces the reduced-arity overloads to be emitted.

/// A protocol that is not class-bound, so its existential is the opaque five-word container.
public protocol DefaultOptionalAdjuster {
    func adjust(_ value: Int32) -> Int32
}

/// A Swift conformer of the adjuster.
public struct DefaultOptionalDoubler: DefaultOptionalAdjuster {
    public init() {}
    public func adjust(_ value: Int32) -> Int32 { value * 2 }
}

/// Built through an initializer whose kept parameters include an optional existential, an
/// optional small primitive and an optional `Any`.
public final class DefaultOptionalAdjustedSession {
    /// The adjuster applied to 21, or -1 when no adjuster was given.
    public let adjusted: Int32
    /// The code given, or -1 when none was.
    public let code: Int32
    /// A description of the tag given, or "nil".
    public let tagDescription: String
    public let verbose: Bool

    public init(
        adjuster: (any DefaultOptionalAdjuster)? = nil,
        code: Int32? = nil,
        tag: Any? = nil,
        flag: OptParamFlags = OptParamFlags()
    ) {
        self.adjusted = adjuster?.adjust(21) ?? -1
        self.code = code ?? -1
        self.tagDescription = tag.map { "\($0)" } ?? "nil"
        self.verbose = flag.verbose
    }

    /// The same parameter shape on a method, which calls its shim through the method wrapper.
    public func apply(
        _ value: Int32,
        adjuster: (any DefaultOptionalAdjuster)? = nil,
        flag: OptParamFlags = OptParamFlags()
    ) -> Int32 {
        let result = adjuster?.adjust(value) ?? value
        return flag.verbose ? -result : result
    }
}
