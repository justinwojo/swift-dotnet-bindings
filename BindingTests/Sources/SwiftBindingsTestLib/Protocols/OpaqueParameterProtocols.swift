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
