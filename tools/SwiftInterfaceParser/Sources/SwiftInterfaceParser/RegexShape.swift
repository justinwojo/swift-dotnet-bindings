// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation
import SwiftSyntax

/// Shared line-shape gates used by every walker to enforce the same acceptance
/// criteria as the per-fact regex patterns.
///
/// The walker tree visits every syntax node SwiftSyntax recognizes; the
/// shape predicates here gate each visit so the host emits only when the
/// corresponding pattern would have matched.
///
/// Conventions:
///   * `name`/`s` arguments are the unmodified `node.name.text` /
///     `node.extendedType.trimmedDescription` strings — backticks INCLUDED.
///   * Empty strings always return `false` (captures `\w+` / `[\w.]+`
///     are at-least-one-character).
enum RegexShape {
    /// True iff every character in `s` satisfies the `\w+` Unicode word-character
    /// class: general categories L (all letters), Mn (nonspacing mark), Nd (decimal
    /// number), Pc (connector punctuation), Lm (modifier letter).
    ///
    /// Used to gate type names, member names, var names, etc. against the
    /// backtick-escaped form. For example `public struct \`class\``: SwiftSyntax
    /// keeps the backticks in `name.text` (`"\`class\`"`), but the backtick is
    /// not a word character, so this returns `false` — exactly when the `\w+`
    /// capture would fail.
    static func isWordIdentifier(_ s: String) -> Bool {
        if s.isEmpty { return false }
        for scalar in s.unicodeScalars {
            switch scalar.properties.generalCategory {
            case .uppercaseLetter, .lowercaseLetter, .titlecaseLetter,
                 .modifierLetter, .otherLetter,
                 .nonspacingMark, .decimalNumber, .connectorPunctuation:
                continue
            default:
                return false
            }
        }
        return true
    }

    /// Mirrors the operator-character branch of the updated PublicFuncRegex —
    /// captures one or more Swift operator characters: `/=-+!*%<>&|^~?.`.
    /// Used for static-func operator declarations (`==`, `+=`, …) so the
    /// availability key flows for retroactive Equatable conformances.
    static func isOperatorIdentifier(_ s: String) -> Bool {
        if s.isEmpty { return false }
        for scalar in s.unicodeScalars {
            switch scalar {
            case "/", "=", "-", "+", "!", "*", "%", "<", ">", "&", "|", "^", "~", "?", ".":
                continue
            default:
                return false
            }
        }
        return true
    }

    /// True iff every character in `s` is either `.` or a Unicode word character
    /// (see `isWordIdentifier`) — the `[\w.]+` capture class.
    ///
    /// Used to gate extension target names. `extension Foo<T>`, `extension (Foo)`,
    /// or `extension Foo & Bar` all fail this gate, matching the
    /// `ExtensionDeclRegex`/`ConformanceExtensionRegex` shapes which capture only
    /// dot-qualified word identifiers.
    static func isWordOrDotOnly(_ s: String) -> Bool {
        if s.isEmpty { return false }
        for scalar in s.unicodeScalars {
            if scalar == "." { continue }
            switch scalar.properties.generalCategory {
            case .uppercaseLetter, .lowercaseLetter, .titlecaseLetter,
                 .modifierLetter, .otherLetter,
                 .nonspacingMark, .decimalNumber, .connectorPunctuation:
                continue
            default:
                return false
            }
        }
        return true
    }

    /// True iff the type/extension keyword and its body's opening `{` are on
    /// the same source line — the `openBraces > 0` condition on the
    /// `TypeDeclRegex` match line. A scope is pushed only when this holds.
    static func opensOnSameLine(keyword: TokenSyntax, leftBrace: TokenSyntax,
                                 converter: SourceLocationConverter) -> Bool {
        let kwLine = converter.location(for: keyword.positionAfterSkippingLeadingTrivia).line
        let braceLine = converter.location(for: leftBrace.positionAfterSkippingLeadingTrivia).line
        return kwLine == braceLine
    }

    /// `TypeDeclRegex` shape: `(?:public|internal|open)\s+(?:final\s+)?(?:class|struct|enum|actor|protocol)\s+(\w+)`.
    /// Modifiers BEFORE access are tolerated (unanchored scan); after access only
    /// an optional `final` is allowed before the type keyword.
    /// `public indirect enum`, `public nonisolated class`, etc. fail this gate.
    static func matchesTypeDeclShape(_ modifiers: DeclModifierListSyntax) -> Bool {
        var iter = modifiers.makeIterator()
        guard advanceToAccess(&iter, ["public", "internal", "open"]) else { return false }
        var current = iter.next()
        if let mod = current, mod.name.text == "final", mod.detail == nil {
            current = iter.next()
        }
        return current == nil
    }

    /// `PublicTypeDeclRegex` shape: `(?:public|open)\s+(?:final\s+)?(?:class|struct|enum|actor|protocol)\s+(\w+)`.
    /// Same shape rules but the access set is restricted to public/open (used by
    /// emission gates that depend on `PublicTypeDeclRegex` rather than the
    /// tracker's broader `TypeDeclRegex`).
    static func matchesPublicTypeShape(_ modifiers: DeclModifierListSyntax) -> Bool {
        var iter = modifiers.makeIterator()
        guard advanceToAccess(&iter, ["public", "open"]) else { return false }
        var current = iter.next()
        if let mod = current, mod.name.text == "final", mod.detail == nil {
            current = iter.next()
        }
        return current == nil
    }

    /// Iterator helper: consume modifiers until we find one whose `name.text` is in
    /// `accessTexts` AND whose `detail` is nil. Modifiers before the access keyword
    /// are tolerated (unanchored scan).
    static func advanceToAccess(_ iter: inout DeclModifierListSyntax.Iterator, _ accessTexts: [String]) -> Bool {
        while let mod = iter.next() {
            if accessTexts.contains(mod.name.text) && mod.detail == nil {
                return true
            }
        }
        return false
    }
}
