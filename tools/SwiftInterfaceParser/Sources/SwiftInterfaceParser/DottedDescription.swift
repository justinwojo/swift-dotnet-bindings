// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import SwiftSyntax

extension SyntaxProtocol {
    /// Canonical spelling for facts compared with ABI JSON, which uses dotted
    /// module names even when the interface uses Swift 6.4's `Module::Type`.
    /// Render syntax tokens, never replace text: literal contents and comment trivia
    /// containing `::` must survive unchanged. Walkers retain the original tree
    /// for source positions, so UTF-8 diagnostic columns need no translation.
    var dottedDescription: String {
        let original = trimmedDescription
        guard original.contains("::") else { return original }
        var rendered = ""
        for token in trimmed.tokens(viewMode: .sourceAccurate) {
            if token.tokenKind == .colonColon {
                rendered += token.leadingTrivia.description + "." + token.trailingTrivia.description
            } else {
                rendered += token.description
            }
        }
        return rendered
    }
}
