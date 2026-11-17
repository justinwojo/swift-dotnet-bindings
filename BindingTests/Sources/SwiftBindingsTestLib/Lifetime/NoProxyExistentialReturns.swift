// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// Existentials the binding has no proxy class for, returned to C# at +1: protocols with no
// type record (standard-library protocols), which C# sees as `object`, and protocols with
// associated types whose conformers are known, which C# sees as an ExistentialUnion. Each
// returned value is counted, so a read that never releases its +1 shows up as a live instance.

nonisolated(unsafe) private var noProxyExistentialLive: Int32 = 0

/// How many values handed out by this fixture are alive.
public func getNoProxyExistentialLiveCount() -> Int32 { noProxyExistentialLive }

/// A class conforming only to standard-library protocols the binding has no record for.
public final class NoProxyDescribedToken: CustomStringConvertible, CustomDebugStringConvertible {
    public let id: Int32
    public init(id: Int32) {
        self.id = id
        noProxyExistentialLive += 1
    }
    deinit { noProxyExistentialLive -= 1 }
    public var description: String { "described\(id)" }
    public var debugDescription: String { "debug\(id)" }
}

public func makeNoProxyDescribed(_ id: Int32) -> any CustomStringConvertible {
    NoProxyDescribedToken(id: id)
}

public func makeNoProxyComposition(_ id: Int32) -> any CustomStringConvertible & CustomDebugStringConvertible {
    NoProxyDescribedToken(id: id)
}

public func makeNoProxyDescribedAsync(_ id: Int32) async -> any CustomStringConvertible {
    NoProxyDescribedToken(id: id)
}

public func makeNoProxyAnyAsync(_ value: Int) async -> Any {
    value
}

/// Reads a value handed back from C#, so a round trip shows the value is still the one Swift made.
public func describeNoProxy(_ value: any CustomStringConvertible) -> String {
    value.description
}

public final class NoProxyDescribedHolder {
    public init(id: Int32) { described = NoProxyDescribedToken(id: id) }
    public var described: any CustomStringConvertible
    public func current() -> any CustomStringConvertible { described }
}

/// A protocol with an associated type: no C# interface can stand for it, so a return of it whose
/// conformers are known is an ExistentialUnion.
public protocol NoProxyKeyed {
    associatedtype Key
    var keyedID: Int32 { get }
}

public final class NoProxyKeyedToken: NoProxyKeyed {
    public typealias Key = Int32
    public let keyedID: Int32
    public init(keyedID: Int32) {
        self.keyedID = keyedID
        noProxyExistentialLive += 1
    }
    deinit { noProxyExistentialLive -= 1 }
}

public struct NoProxyKeyedValue: NoProxyKeyed {
    public typealias Key = String
    public let keyedID: Int32
    public init(keyedID: Int32) { self.keyedID = keyedID }
}

public func makeNoProxyKeyed(_ id: Int32) -> any NoProxyKeyed {
    NoProxyKeyedToken(keyedID: id)
}

/// The same protocol shape, class-bound: its existential is a class reference and a witness table.
public protocol NoProxyClassKeyed: AnyObject {
    associatedtype Key
    var classKeyedID: Int32 { get }
}

public final class NoProxyClassKeyedToken: NoProxyClassKeyed {
    public typealias Key = Int32
    public let classKeyedID: Int32
    public init(classKeyedID: Int32) {
        self.classKeyedID = classKeyedID
        noProxyExistentialLive += 1
    }
    deinit { noProxyExistentialLive -= 1 }
}

public func makeNoProxyClassKeyed(_ id: Int32) -> any NoProxyClassKeyed {
    NoProxyClassKeyedToken(classKeyedID: id)
}

/// A requirement returning the associated-type existential. The witness on a conforming class
/// returns what the C# interface declares (`object`), not the union its own position allows.
public protocol NoProxyKeyedProducer {
    func produceKeyed(_ id: Int32) -> any NoProxyKeyed
    var producedKeyed: any NoProxyKeyed { get }
}

public final class NoProxyKeyedFactory: NoProxyKeyedProducer {
    public init() {}
    public func produceKeyed(_ id: Int32) -> any NoProxyKeyed { NoProxyKeyedToken(keyedID: id) }
    public var producedKeyed: any NoProxyKeyed { NoProxyKeyedToken(keyedID: 40) }
}

// The same existentials crossing a closure. Swift lends a closure argument for the call only and
// destroys it after the closure returns, so a C# body that keeps it needs its own reference. A
// closure result is taken by Swift at +1, so the C# value handed back keeps its own.

/// Hands a fresh token to `body`; Swift releases its own reference once `body` returns.
public func withNoProxyDescribed(_ id: Int32, _ body: (any CustomStringConvertible) -> Void) {
    body(NoProxyDescribedToken(id: id))
}

/// Hands a fresh token inside a tuple, the shape a closure argument list can also take.
public func withNoProxyDescribedPair(_ id: Int32, _ body: ((any CustomStringConvertible, Int32)) -> Void) {
    body((NoProxyDescribedToken(id: id), id))
}

/// Hands a fresh associated-type token to `body`.
public func withNoProxyKeyed(_ id: Int32, _ body: (any NoProxyKeyed) -> Void) {
    body(NoProxyKeyedToken(keyedID: id))
}

/// Reads the value `make` returns and drops it.
public func readNoProxyKeyed(_ make: () -> any NoProxyKeyed) -> Int32 {
    make().keyedID
}

/// Reads the value `make` returns and drops it.
public func describeNoProxyMade(_ make: () -> any CustomStringConvertible) -> String {
    make().description
}

/// Reads both requirements of a producer, which may be a C# conformer, and drops what they return.
public func readNoProxyKeyedProducer(_ producer: any NoProxyKeyedProducer, _ id: Int32) -> Int32 {
    producer.produceKeyed(id).keyedID + producer.producedKeyed.keyedID
}
