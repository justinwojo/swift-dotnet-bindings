// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import SwiftSyntax

/// Access level and member-name spelling of a declaration, read from the syntax
/// tree rather than from the shape of its modifier list.
///
/// A declaration is public when its own access modifier is `public` or `open`.
/// Nothing else in the modifier list changes that: ownership modifiers (`mutating`,
/// `nonmutating`, `consuming`, `borrowing`, and the legacy `__consuming` an interface
/// still prints on `Sequence` conformers), isolation (`nonisolated`,
/// `nonisolated(unsafe)`), dispatch (`final`, `static`, `class`, `override`,
/// `dynamic`), initializer kinds (`convenience`, `required`), storage (`lazy`,
/// `weak`, `unowned`, `unowned(unsafe)`), setter visibility (`private(set)`) and
/// `optional`/`indirect`. Attributes are not modifiers at all. A setter-visibility
/// modifier carries a detail (`public(set)`), so it never counts as the declaration's
/// own access level.
///
/// The walkers used to accept a member as public only when every modifier after the
/// access keyword was on a per-kind allow-list, a port of the line regexes they
/// replaced. Any modifier the list missed made a public member read as absent from
/// the public interface, which the ABI parser then classified as module-internal and
/// withdrew.
enum DeclAccess {
    /// True iff the declaration's own access level is `public` or `open`.
    static func isPublicOrOpen(_ modifiers: DeclModifierListSyntax) -> Bool {
        hasAccess(modifiers, ["public", "open"])
    }

    /// True iff the declaration states its own access level and that level is one of
    /// `levels`. A detail-bearing modifier (`private(set)`) is setter visibility, not
    /// the declaration's access level.
    static func hasAccess(_ modifiers: DeclModifierListSyntax, _ levels: Set<String>) -> Bool {
        modifiers.contains { levels.contains($0.name.text) && $0.detail == nil }
    }

    /// The access levels a `.swiftinterface` prints on a declaration it exposes:
    /// `public`/`open` API, and `internal`/`package` declarations that
    /// `@usableFromInline` or `@inlinable` exposed.
    static let interfaceAccessLevels: Set<String> = ["public", "open", "internal", "package"]

    /// The member name the ABI JSON prints for `token`, or `nil` when the
    /// declaration is an operator. A keyword-named declaration is printed with
    /// backticks in the interface (``func `repeat`(count:)``) and keeps them in the
    /// token's text, while the ABI JSON names it `repeat(count:)`. A raw identifier
    /// that is not a word identifier even unescaped has no ABI-side spelling the
    /// consumer reproduces, so it is skipped like an operator.
    static func memberName(_ token: TokenSyntax) -> String? {
        let unescaped = unescape(token.text)
        return RegexShape.isWordIdentifier(unescaped) ? unescaped : nil
    }

    /// Drops the backticks an interface prints around a keyword-named identifier.
    static func unescape(_ name: String) -> String {
        guard name.count >= 2, name.hasPrefix("`"), name.hasSuffix("`") else { return name }
        return String(name.dropFirst().dropLast())
    }
}
