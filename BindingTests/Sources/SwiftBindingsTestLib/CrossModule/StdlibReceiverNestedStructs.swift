// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

// Structs this module declares inside an extension of a nested standard-library type. The ABI
// descriptor roots the receiver at its module under its bare leaf name (`Index`), and the
// receiver's metadata accessor lives in no symbol table the generator reads. The nested structs
// are this module's own, with their own accessors, and a holder that stores one is laid out from
// them: losing them left the frozen payload's holder judged resilient, so a function taking it by
// value in registers was called with a pointer.

extension String.Index {
    /// Loadable: two words in registers.
    @frozen public struct StdlibNestedFrozenPayload {
        public var first: Int
        public var second: Int
        public init(first: Int, second: Int) {
            self.first = first
            self.second = second
        }
    }

    /// Resilient: clients reach it through memory.
    public struct StdlibNestedResilientPayload {
        public var value: Int
        public init(value: Int) { self.value = value }
    }
}

/// Stores the loadable payload, so it crosses in registers too.
@frozen public struct StdlibNestedFrozenPayloadHolder {
    public var inner: String.Index.StdlibNestedFrozenPayload
    public var tag: Int
    public init(inner: String.Index.StdlibNestedFrozenPayload, tag: Int) {
        self.inner = inner
        self.tag = tag
    }
}

/// Stores the resilient payload, so it crosses through memory.
@frozen public struct StdlibNestedResilientPayloadHolder {
    public var inner: String.Index.StdlibNestedResilientPayload
    public var tag: Int
    public init(inner: String.Index.StdlibNestedResilientPayload, tag: Int) {
        self.inner = inner
        self.tag = tag
    }
}

/// Swift names each stored word, so a crossing in the wrong convention reads as a wrong value.
public func stdlibNestedFrozenHolderDigest(_ holder: StdlibNestedFrozenPayloadHolder) -> Int {
    holder.inner.first * 10_000 + holder.inner.second * 100 + holder.tag
}

public func stdlibNestedResilientHolderDigest(_ holder: StdlibNestedResilientPayloadHolder) -> Int {
    holder.inner.value * 100 + holder.tag
}

public func makeStdlibNestedFrozenHolder(first: Int, second: Int, tag: Int) -> StdlibNestedFrozenPayloadHolder {
    StdlibNestedFrozenPayloadHolder(inner: .init(first: first, second: second), tag: tag)
}
