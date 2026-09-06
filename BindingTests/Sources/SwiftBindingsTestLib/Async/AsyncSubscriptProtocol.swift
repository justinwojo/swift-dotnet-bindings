// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

// A protocol subscript requirement with an effectful getter (`get async`), satisfied by a public
// final class. The subscript cannot be projected as a C# indexer: an indexer getter has no async
// form. The conformance decision and the indexer emission are made in two places, and both must
// refuse the same shape — if the conformance keeps `: IProtocol` while the emitter drops the
// indexer, the class declares an interface member it never implements (CS0535) and the binding
// does not compile. The expected projection is a class WITHOUT the conformance and without the
// indexer, keeping its ordinary members.

public protocol AsyncIndexedSource {
    subscript(_ index: Int32) -> Int32 { get async }
    var count: Int32 { get }
}

public final class AsyncIndexedTable: AsyncIndexedSource {
    public init() {}

    public var count: Int32 { 3 }

    public subscript(_ index: Int32) -> Int32 {
        get async { index * 2 }
    }
}
