// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import SwiftSyntax

extension SyntaxProtocol {
    /// Canonical spelling for facts compared with ABI JSON, which uses dotted
    /// module names even when the interface uses Swift 6.4's `Module::Type`.
    /// Render syntax tokens, never replace text: literal contents and comment trivia
    /// containing `::` must survive unchanged. Walkers retain the original tree
    /// for source positions, so UTF-8 diagnostic columns need no translation.
    ///
    /// Swift 6.4 qualifies every component of a nested name, so an interface prints
    /// `Kit::Outer.Kit::Inner` where ABI JSON prints `Kit.Outer.Inner`. A selector
    /// that leads a name becomes a dotted module prefix. A selector after a `.` only
    /// says which module declares that member, so it is dropped: rendering it as a
    /// component would invent a nested type named after the module.
    var dottedDescription: String {
        let original = trimmedDescription
        guard original.contains("::") else { return original }
        let tokens = Array(trimmed.tokens(viewMode: .sourceAccurate))
        var rendered = ""
        var index = 0
        while index < tokens.count {
            let token = tokens[index]
            if index > 0, tokens[index - 1].tokenKind == .period,
               index + 1 < tokens.count, tokens[index + 1].tokenKind == .colonColon {
                // `.Kit::Inner`: drop the selector, keep its surrounding trivia, and
                // continue with the member name it qualifies.
                rendered += token.leadingTrivia.description + tokens[index + 1].trailingTrivia.description
                index += 2
                continue
            }
            if token.tokenKind == .colonColon {
                rendered += token.leadingTrivia.description + "." + token.trailingTrivia.description
            } else {
                rendered += token.description
            }
            index += 1
        }
        return rendered
    }
}
