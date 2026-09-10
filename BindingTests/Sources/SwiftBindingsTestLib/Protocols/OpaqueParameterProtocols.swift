// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Protocols with opaque `some` parameters

/// A layout protocol used as an opaque parameter type.
/// The `some` keyword in the method signature creates a generic signature mismatch
/// that prevents ABI parsing (2 type params vs 1 sugared).
public protocol RowLayout {
    var columnCount: Int32 { get }
}

/// Protocol whose requirement uses `some` parameter — triggers GenericSignatureParser
/// mismatch because `some RowLayout` creates an implicit τ_1_0 in the generic signature
/// that doesn't appear in the sugared signature.
/// EveryProtocol conformance should be SKIPPED because the requirement fails ABI parsing.
public protocol RowAdapter: Sendable {
    func layoutedAdapter(from layout: some RowLayout) throws -> String
}

/// Extension providing a default method (not a requirement).
extension RowAdapter {
    public func addingScopes(_ scopes: [String: any RowAdapter]) -> String {
        return "scopes:\(scopes.count)"
    }
}

/// Concrete implementation of RowLayout for testing.
public struct SimpleRowLayout: RowLayout {
    public let columnCount: Int32
    public init(columnCount: Int32) {
        self.columnCount = columnCount
    }
}

/// Concrete adapter implementing RowAdapter.
public class SimpleRowAdapter: @unchecked Sendable, RowAdapter {
    public init() {}
    public func layoutedAdapter(from layout: some RowLayout) throws -> String {
        return "adapted:\(layout.columnCount)"
    }
}

/// Factory to verify the concrete adapter works (even though EveryProtocol is skipped).
public func makeSimpleRowAdapter() -> SimpleRowAdapter {
    return SimpleRowAdapter()
}

/// A `@frozen` conformer of `RowLayout`. Frozen structs project to C# as blittable
/// structs, which the concrete-specialization engine declines to specialize, so
/// `layoutedAdapter(from:)` at this type argument exists ONLY as the fully generic arm —
/// the shape a consumer hits when no closed specialization is available.
@frozen
public struct FrozenRowLayout: RowLayout {
    public let columnCount: Int32
    public init(columnCount: Int32) {
        self.columnCount = columnCount
    }
}

/// Shaped exactly like `FrozenRowLayout` but deliberately NOT a `RowLayout` conformer.
/// The adversarial fixture declares the C# projection of this type as an `IRowLayout`
/// implementer, which satisfies the binding's managed constraint while the Swift metadata
/// still carries no conformance — the one case where the opening wrapper's runtime cast is
/// all that stands between the caller and a payload read at the wrong type.
@frozen
public struct NotARowLayout {
    public let columnCount: Int32
    public init(columnCount: Int32) {
        self.columnCount = columnCount
    }
}
