// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Async generic-Collection parameter returning an array of resilient structs
//
// Shape under test:
//
//   static func f<C>(for items: C) async throws -> [ResilientStruct]
//       where C: Collection, C.Element == String
//
// The C# side lowers `IEnumerable<string>` into a `SwiftArray<SwiftString>`, the async `@_cdecl`
// wrapper reads it back as `Array<String>`, the function maps each element to a resilient struct,
// and the `[Struct]` result round-trips back to C#.
//
// Three things converge here that no other async fixture combines: a *generic* parameter
// constrained to `Collection` with `Element == String` (rather than a concrete `[String]`), a
// *static* async throwing member, and a return of resilient structs carrying heap-allocated
// `String` fields. `AsyncNonFrozenStructArrayParamTests` covers resilient struct arrays but its
// element type is all-numeric, so the heap-string ownership path inside a returned element is
// unique to this fixture.
//
// This is the shape behind StoreKit's `Product.products(for:)`, where a field report of
// "0 products returned, no error" could only originate in the binding if this round-trip dropped
// or emptied the identifier array. A known array of identifiers surviving with the correct count
// and intact string fields exonerates the marshalling.

/// Resilient (non-`@frozen`, library-evolution) struct carrying `String` fields derived from the
/// input element. Resilience matters — it forces element extraction through the value-witness move
/// path rather than a flat memcpy.
public struct NonFrozenIdentifiedRecord {
    public let id: String
    public let displayName: String

    public init(id: String, displayName: String) {
        self.id = id
        self.displayName = displayName
    }

    /// `static`, `async throws`, generic over `Collection` whose `Element == String`, returning
    /// `[Self]`. Nothing is dropped, so the C# result count must equal the input element count.
    public static func records<Identifiers>(for identifiers: Identifiers) async throws -> [NonFrozenIdentifiedRecord]
        where Identifiers: Collection, Identifiers.Element == String {
        // Force a real suspension so the C# foreground frame unwinds before the array is read,
        // matching an async fetch that crosses a network round-trip.
        try? await Task.sleep(nanoseconds: 1_000_000)
        return identifiers.map { NonFrozenIdentifiedRecord(id: $0, displayName: "name:" + $0) }
    }
}

/// Concrete-`[String]`-parameter variant — isolates the `SwiftArray<String>` serialization from
/// generic-collection dispatch. If the generic `records(for:)` above ever diverges from this one,
/// the fault is in generic dispatch, not the element-array marshalling.
public func fetchIdentifiedRecordsConcrete(for identifiers: [String]) async throws -> [NonFrozenIdentifiedRecord] {
    try? await Task.sleep(nanoseconds: 1_000_000)
    return identifiers.map { NonFrozenIdentifiedRecord(id: $0, displayName: "name:" + $0) }
}
