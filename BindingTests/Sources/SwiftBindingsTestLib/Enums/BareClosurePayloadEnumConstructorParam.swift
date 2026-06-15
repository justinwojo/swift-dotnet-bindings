// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Enum case carrying a BARE (non-optional) closure payload, used as a
//         struct constructor parameter (Alamofire URLEncoding.ArrayEncoding shape)
//
// This is the exact shape behind the Alamofire `URLEncoding(arrayEncoding:)`
// SIGSEGV regression. A bare (non-optional) function/closure payload such as
// `case custom((String, Int) -> String)` is encoded by swift-api-digester as a
// TypeFunc node in the payload-application position — NOT a nominal (TypeNominal)
// nor a tuple (Tuple). A parser that matched only those two kinds recorded ZERO
// associated values for the case, so the WHOLE enum was mis-classified as a
// simple (Int32-backed, 4-byte) enum. A struct constructor taking the enum BY
// VALUE then marshalled the argument as a 4-byte tag, while the Swift @_cdecl
// wrapper loaded the real (multi-word, closure-carrying) enum out of that
// undersized buffer — an out-of-bounds read whose garbage non-trivial payload
// crashed on ARC release.
//
// This differs from `ClosureCarrier` (OptionalClosurePayloadEnum.swift): there
// the payloads are `Optional<Closure>`, a bound-generic NOMINAL (`Swift.Optional`)
// that the parser already classified correctly. Only the BARE closure payload
// here exercises the regressed (TypeFunc) parse path.

/// Enum mixing tag-only cases with a bare-closure-payload case — the
/// `Alamofire.URLEncoding.ArrayEncoding` shape.
public enum ArrayBracketEncoding {
    /// Tag-only case.
    case brackets
    /// Tag-only case.
    case noBrackets
    /// Bare (non-optional) closure payload — the TypeFunc-node case that, when
    /// dropped by the parser, undersized the whole enum.
    case custom((_ key: String, _ index: Int) -> String)
}

/// Struct whose initializer takes the bare-closure-payload enum BY VALUE — the
/// exact `URLEncoding(arrayEncoding:)` crash surface. Constructing it with a
/// tag-only case must not crash: success proves the enum is sized as a complex
/// (associated-value) enum, not a 4-byte simple enum.
public struct BracketEncodingConfig {
    public let usesBrackets: Bool
    public let label: String

    public init(arrayEncoding: ArrayBracketEncoding) {
        switch arrayEncoding {
        case .brackets:
            self.usesBrackets = true
            self.label = "brackets"
        case .noBrackets:
            self.usesBrackets = false
            self.label = "noBrackets"
        case .custom:
            self.usesBrackets = true
            self.label = "custom"
        }
    }

    public func describe() -> String {
        return "\(label):\(usesBrackets)"
    }
}

/// Free function taking the enum by value — a second cdecl ABI surface for the
/// undersized-buffer regression, independent of the struct-ctor path.
public func bracketEncodingLabel(_ encoding: ArrayBracketEncoding) -> String {
    switch encoding {
    case .brackets: return "brackets"
    case .noBrackets: return "noBrackets"
    case .custom: return "custom"
    }
}
