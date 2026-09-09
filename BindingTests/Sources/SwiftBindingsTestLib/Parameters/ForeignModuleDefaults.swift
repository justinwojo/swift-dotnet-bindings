// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Defaults whose expression names a member of another module
//
// A defaulted parameter whose default expression is not a C#-expressible literal is reached
// through a trimmed overload: the generated Swift shim calls the declaration WITHOUT that
// argument, so Swift evaluates the default expression itself. The expressions below resolve to
// members of a class the binding does not own — one a static factory CALL, one a static
// PROPERTY on the same platform-vended class — so nothing about materialising them may depend on
// a symbol the generator derives for that foreign member. The callable that ends up behind the
// trimmed overload has to be one the module the binding links actually exports.

public final class ForeignModuleDefaultBox {
    public init() {}

    /// Default is a static factory CALL on a platform-owned class, itself declared with a
    /// defaulted parameter (`global(qos:)`) — the written expression names no arguments at all.
    public func onQueue(tag: String, queue: DispatchQueue = .global()) -> String {
        return "\(tag)|\(queue === DispatchQueue.global())"
    }

    /// Default is a static PROPERTY on the same platform-owned class — a member reference with no
    /// call at all, so it separates "expression is a call" from "expression names another module".
    public func onMainQueue(tag: String, queue: DispatchQueue = .main) -> String {
        return "\(tag)|\(queue === DispatchQueue.main)"
    }
}

/// Free-function form of the same shape: the declaration has no enclosing type, so the trimmed
/// overload's shim is a top-level function rather than an extension member.
public func describeDefaultQueue(tag: String, queue: DispatchQueue = .global()) -> String {
    return "\(tag)|\(queue === DispatchQueue.global())"
}
