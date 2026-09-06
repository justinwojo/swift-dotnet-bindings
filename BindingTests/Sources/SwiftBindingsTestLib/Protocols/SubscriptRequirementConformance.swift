// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.
//
// Regression fixture for a protocol whose requirement set includes a SUBSCRIPT.
//
// The conformance-keep decision and the indexer-emission decision are two different
// walks over the same subscript, and they used to answer differently: the emitter
// emitted `public string this[int position]` on the concrete type, while the
// conformance validator still answered a blanket "subscripts on concrete types are
// not supported". The result was a type that carried the indexer but had the
// `: IIndexedCatalog` quietly stripped off it, so a consumer holding the protocol
// could not reach the member the binding had in fact emitted.
//
// Both conformer kinds are here because the indexer emission path branches on the
// carrier: a class witnesses through its own instance, a struct through an opaque
// payload. `CatalogHost` is the consumer shape — a Swift-side property typed as the
// protocol, which is what a consumer reads the catalog through.

/// Plain (non-PAT) protocol whose requirements mix an ordinary property with a
/// read-only subscript.
public protocol IndexedCatalog {
    var entryCount: Int32 { get }
    subscript(position: Int32) -> String { get }
}

/// Class conformer.
public final class CatalogTable: IndexedCatalog {
    private let rows: [String]

    public init(rows: [String]) {
        self.rows = rows
    }

    public var entryCount: Int32 { Int32(rows.count) }

    public subscript(position: Int32) -> String {
        return rows[Int(position)]
    }
}

/// Struct conformer — same requirements, different carrier.
public struct CatalogRecord: IndexedCatalog {
    private let fields: [String]

    public init(fields: [String]) {
        self.fields = fields
    }

    public var entryCount: Int32 { Int32(fields.count) }

    public subscript(position: Int32) -> String {
        return fields[Int(position)]
    }
}

/// Consumer shape: a stored property typed as the protocol, so reading the catalog
/// goes through the interface rather than the concrete type.
public final class CatalogHost {
    public let catalog: any IndexedCatalog

    public init(catalog: any IndexedCatalog) {
        self.catalog = catalog
    }
}

public func makeCatalogTable(first: String, second: String, third: String) -> CatalogTable {
    return CatalogTable(rows: [first, second, third])
}

public func makeCatalogRecord(first: String, second: String) -> CatalogRecord {
    return CatalogRecord(fields: [first, second])
}

public func makeCatalogHost(first: String, second: String) -> CatalogHost {
    return CatalogHost(catalog: CatalogTable(rows: [first, second]))
}
