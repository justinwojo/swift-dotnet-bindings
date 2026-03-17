// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Protocol with Associated Type

/// A protocol with an associated type for element storage.
/// Tests: associatedtype, type conforming to PAT, PAT as constraint.
/// Expected C#: Interface with generic parameter or type-erased wrapper.
public protocol CollectionContainer {
    associatedtype Element
    var count: Int32 { get }
    func element(at index: Int32) -> Element
    mutating func append(_ item: Element)
}

/// A protocol with an associated type that has a constraint.
public protocol Transformable {
    associatedtype Input
    associatedtype Output
    func transform(_ input: Input) -> Output
}

// MARK: - Types Conforming to PATs

/// An Int32 container backed by an array.
public struct Int32CollectionContainer: CollectionContainer {
    public typealias Element = Int32
    private var items: [Int32]

    public init() {
        self.items = []
    }

    public var count: Int32 {
        return Int32(items.count)
    }

    public func element(at index: Int32) -> Int32 {
        return items[Int(index)]
    }

    public mutating func append(_ item: Int32) {
        items.append(item)
    }
}

/// A String container backed by an array.
public struct StringCollectionContainer: CollectionContainer {
    public typealias Element = String
    private var items: [String]

    public init() {
        self.items = []
    }

    public var count: Int32 {
        return Int32(items.count)
    }

    public func element(at index: Int32) -> String {
        return items[Int(index)]
    }

    public mutating func append(_ item: String) {
        items.append(item)
    }
}

/// A transformer that converts Int32 to String.
public struct Int32ToString: Transformable {
    public typealias Input = Int32
    public typealias Output = String

    public init() {}

    public func transform(_ input: Int32) -> String {
        return String(input)
    }
}

// MARK: - PAT Used as Constraint

/// Returns the count of any CollectionContainer.
public func containerCount<C: CollectionContainer>(_ container: C) -> Int32 {
    return container.count
}

/// Returns the first element of any CollectionContainer, or nil if empty.
public func firstElement<C: CollectionContainer>(_ container: C) -> C.Element? {
    guard container.count > 0 else { return nil }
    return container.element(at: 0)
}

/// Applies a Transformable to a value.
public func applyTransform<T: Transformable>(_ transformer: T, to input: T.Input) -> T.Output {
    return transformer.transform(input)
}

// MARK: - PAT with Where Clause

/// Counts elements in a container where the element type is Equatable.
public func countEqual<C: CollectionContainer>(_ container: C, to value: C.Element) -> Int32 where C.Element: Equatable {
    var result: Int32 = 0
    for i in 0..<container.count {
        if container.element(at: i) == value {
            result += 1
        }
    }
    return result
}
