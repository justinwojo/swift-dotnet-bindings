// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

// MARK: - A payload-less raw-value Hashable enum used as a Set element and a Dictionary key
//
// A `public enum ...: Int, Hashable` with no associated values projects to a plain C#
// `enum`, which cannot implement `ISwiftObject`. Metadata for such an enum is registered
// by the module initializer, but the ISwiftObject-constrained conformance and
// witness-table registration lanes have no way to carry its Hashable conformance — so
// every `Set<Kind>` element lookup and every `[Kind: V]` key lookup used to fail at the
// witness-table resolution with "Unable to get protocol witness table", even though Swift
// synthesizes, exports and can itself use that conformance perfectly well.
//
// Both nestings are covered because the C# `typeof()` the module initializer emits must
// name the enum through its enclosing type: shipped bindings hit this with the nested
// shape (an enum declared inside a public struct), while a top-level enum is the
// unqualified control.

/// The nested shape: a raw-value Hashable enum declared inside a public struct.
public struct EnumSetHost {
    public enum Kind: Int, Hashable {
        case generic
        case phoneNumber
        case emailAddress
    }

    // Named so it does not collide with the nested type: a stored property spelled `kind`
    // would push the enum's C# name to `Kind2` through the CS0542 rename, which is a
    // different projection concern than the conformance this fixture covers.
    public let hostKind: Kind

    public init(hostKind: Kind) {
        self.hostKind = hostKind
    }
}

/// The top-level control, so the qualified and unqualified `typeof()` forms are both driven.
public enum TopLevelEnumSetKind: Int, Hashable {
    case alpha
    case beta
    case gamma
}

// MARK: - Set<Kind>: parameter direction

/// Member count, reported from the Swift side. A set whose elements did not hash through
/// the enum's real Hashable witness reads a wrong count here rather than failing loudly.
public func nestedKindSetCount(_ values: Set<EnumSetHost.Kind>) -> Int32 {
    return Int32(values.count)
}

/// Every member's raw value, sorted and joined. Proves the payloads crossed intact rather
/// than merely that the set has the right cardinality.
public func nestedKindSetSortedRawValues(_ values: Set<EnumSetHost.Kind>) -> String {
    return values.map { String($0.rawValue) }.sorted().joined(separator: ",")
}

/// Swift-side membership test against an independently marshalled probe.
public func nestedKindSetContains(_ values: Set<EnumSetHost.Kind>, _ probe: EnumSetHost.Kind) -> Bool {
    return values.contains(probe)
}

/// The top-level enum's parameter direction.
public func topLevelKindSetCount(_ values: Set<TopLevelEnumSetKind>) -> Int32 {
    return Int32(values.count)
}

// MARK: - Set<Kind>: return direction

/// Swift-built set of every nested case.
public func makeNestedKindSet() -> Set<EnumSetHost.Kind> {
    return [.generic, .phoneNumber, .emailAddress]
}

/// Swift-built set of every top-level case.
public func makeTopLevelKindSet() -> Set<TopLevelEnumSetKind> {
    return [.alpha, .beta, .gamma]
}

// MARK: - Dictionary keyed by the enum

/// Sum of the values, reported from the Swift side: the keys must hash through the same
/// witness on both sides of the boundary for the marshalled entries to be findable.
public func nestedKindDictionaryValueSum(_ entries: [EnumSetHost.Kind: Int32]) -> Int32 {
    return entries.values.reduce(0, +)
}

/// Value lookup by a marshalled key, returning -1 when the key is absent.
public func nestedKindDictionaryLookup(_ entries: [EnumSetHost.Kind: Int32], _ key: EnumSetHost.Kind) -> Int32 {
    return entries[key] ?? -1
}

/// Swift-built dictionary, for the return direction: each case mapped to its raw value * 10.
public func makeNestedKindDictionary() -> [EnumSetHost.Kind: Int32] {
    var result: [EnumSetHost.Kind: Int32] = [:]
    for kind in [EnumSetHost.Kind.generic, .phoneNumber, .emailAddress] {
        result[kind] = Int32(kind.rawValue) * 10
    }
    return result
}
