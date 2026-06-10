// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Default-argument optional non-primitive value-type parameters (Issue #31)
//
// Exercises the reduced-arity wrapper path for Optional<T> where T is a non-primitive
// value type. The full-arity wrapper and the default-arg wrapper must use the same ABI
// for these params. Regression: prior to the fix, DBW Swift emission read the native
// Optional<T> layout while C# passed a SwiftOptional<IntPtr>, mismatching sizes on the
// wire.
//
// DBW reduced-arity overloads only emit when at least one trailing default is not
// C#-mappable. We use a trailing parameter with a struct-constructor default
// (`flag: OptParamFlags = OptParamFlags()`) to force DBW emission — the constructor
// call expression is not mappable to a C# literal default.

/// Small non-frozen struct. Not @frozen so it's projected as C# class-with-opaque-payload.
public struct OptParamSmallConfig {
    public var code: Int32
    public var label: String

    public init(code: Int32 = 0, label: String = "") {
        self.code = code
        self.label = label
    }
}

/// Large non-frozen struct (multi-field, well above 8 bytes).
public struct OptParamLargeConfig {
    public var a: Int64
    public var b: Int64
    public var c: Int64
    public var d: String
    public var e: String

    public init(a: Int64 = 0, b: Int64 = 0, c: Int64 = 0, d: String = "", e: String = "") {
        self.a = a
        self.b = b
        self.c = c
        self.d = d
        self.e = e
    }
}

/// Simple trailing struct that we can default-construct — the ctor call is not mappable
/// to a C# literal, which forces DBW reduced-arity emission for the callers below.
public struct OptParamFlags {
    public var verbose: Bool
    public init(verbose: Bool = false) {
        self.verbose = verbose
    }
}

/// Factory with optional struct parameters and constructor-call defaults.
public struct OptParamModel {
    public let name: String
    public let smallCode: Int32
    public let smallLabel: String
    public let largeA: Int64
    public let largeD: String
    public let flagVerbose: Bool

    public init(
        name: String,
        small: OptParamSmallConfig? = nil,
        large: OptParamLargeConfig? = nil,
        flag: OptParamFlags = OptParamFlags()
    ) {
        self.name = name
        self.smallCode = small?.code ?? -1
        self.smallLabel = small?.label ?? "<default>"
        self.largeA = large?.a ?? -1
        self.largeD = large?.d ?? "<default>"
        self.flagVerbose = flag.verbose
    }
}

/// Free function variant covers the same shape without class/struct parent scope.
public func buildOptParamSummary(
    title: String,
    small: OptParamSmallConfig? = nil,
    large: OptParamLargeConfig? = nil,
    flag: OptParamFlags = OptParamFlags()
) -> String {
    let smallPart = small.map { "(\($0.code),\($0.label))" } ?? "nil"
    let largePart = large.map { "(\($0.a),\($0.d))" } ?? "nil"
    let flagPart = flag.verbose ? "v" : "q"
    return "\(title)|small=\(smallPart)|large=\(largePart)|flag=\(flagPart)"
}

// MARK: - Full-wrapper Optional<non-primitive> parameters (non-DBW path)
//
// Regression gate for a second manifestation of the #31 layout mismatch: the full
// OptionalPointerWrapper path (used by regular methods that aren't DBW overloads) used
// to emit `assumingMemoryBound(to: Optional<T>.self).pointee` against a C#
// SwiftOptional<IntPtr> buffer. For non-frozen struct / complex enum inner types
// those layouts disagree. The fix routes the full-wrapper deref through the same
// opaque-aware GetDerefCode helper the DBW path uses.
//
// No default arguments here — these methods force the full OptionalPointerWrapper
// path, not the reduced-arity DBW path.

public struct OptParamHolder {
    public let code: Int32

    public init(code: Int32) {
        self.code = code
    }

    /// Regular method (no defaults) taking Optional<NonFrozenStruct>. Full-wrapper path.
    public func describeSmall(_ cfg: OptParamSmallConfig?) -> String {
        if let c = cfg { return "holder=\(code)|small=(\(c.code),\(c.label))" }
        return "holder=\(code)|small=nil"
    }

    /// Same shape with a large non-frozen struct — still no default, still full-wrapper path.
    public func describeLarge(_ cfg: OptParamLargeConfig?) -> String {
        if let c = cfg { return "holder=\(code)|large=(\(c.a),\(c.d))" }
        return "holder=\(code)|large=nil"
    }
}

/// Free-function form so the test covers both instance-method and free-function
/// full-wrapper paths.
public func summarizeOptHolder(_ cfg: OptParamSmallConfig?) -> String {
    if let c = cfg { return "free=(\(c.code),\(c.label))" }
    return "free=nil"
}
