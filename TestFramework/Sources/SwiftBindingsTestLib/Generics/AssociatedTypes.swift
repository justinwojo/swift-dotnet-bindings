// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Protocol with Associated Type

/// A protocol with an associated type.
public protocol Container {
    associatedtype Element
    var count: Int32 { get }
    func element(at index: Int32) -> Element
}

/// Concrete conformance to Container.
public struct IntContainer: Container {
    public typealias Element = Int32

    private var items: [Int32]

    public init(items: [Int32]) {
        self.items = items
    }

    public var count: Int32 {
        return Int32(items.count)
    }

    public func element(at index: Int32) -> Int32 {
        return items[Int(index)]
    }
}
