// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

// Protocol requirements with typed throws (`throws(LoaderError)`). The requirement's function type
// includes the thrown error type, and a witness declared with untyped `throws` is a different
// (wider) function type, so the conformance is rejected. Both the dispatchable non-generic
// requirement and the method-level-generic stub must carry the typed clause.

import Foundation

public enum LoaderError: Error {
    case missing
    case corrupt(code: Int32)
}

public protocol ThrowingLoader: AnyObject {
    func load(_ key: String) throws(LoaderError) -> Int32
    func loadEach<T>(_ items: [T]) throws(LoaderError) -> Int32
}

/// Calls the typed-throws requirement through the existential and folds the error into a code.
public func throwingLoaderLoad(_ loader: any ThrowingLoader, key: String) -> Int32 {
    do {
        return try loader.load(key)
    } catch {
        switch error {
        case .missing: return -1
        case .corrupt(let code): return -code
        }
    }
}

/// Typed throws on a protocol with no generic member, so the requirement dispatches through the
/// vtable rather than stubbing.
public protocol TypedThrowingCounter: AnyObject {
    func count(_ key: String) throws(LoaderError) -> Int32
}

/// Calls the dispatchable typed-throws requirement through the existential.
public func typedThrowingCounterCount(_ counter: any TypedThrowingCounter, key: String) -> Int32 {
    do {
        return try counter.count(key)
    } catch {
        switch error {
        case .missing: return -1
        case .corrupt(let code): return -code
        }
    }
}

public struct LoaderQuery<Result> {
    public init() {}
}

public protocol LoaderClause {}

/// Method-level generic requirements whose variadic and array-spelled twins share a name and a
/// typed-throws clause. The ABI descriptor spells the method's generic parameter by depth and index
/// and prints the variadic twin as a plain array, so the twins' typed-throws facts are only found
/// once both spellings are reconciled with the interface; a witness that falls back to untyped
/// `throws` fails the conformance.
public protocol ClauseLoader: AnyObject {
    func first<R>(_ query: LoaderQuery<R>, _ clauses: any LoaderClause...) throws(LoaderError) -> R?
    func first<R>(_ query: LoaderQuery<R>, _ clauses: [any LoaderClause]) throws(LoaderError) -> R?
    func tally(_ keys: String...) throws(LoaderError) -> Int32
    func tally(_ keys: [String]) throws(LoaderError) -> Int32
}
