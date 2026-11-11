// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.
//
// Generic parameters spelled with multi-letter source names (`Value`, `Key`). A
// module compiled from source describes these in its ABI as depth/index parameters
// (τ_0_0), while an SDK interface dump spells them by source name; the generator has
// to treat both as the same generic parameter. The source-name dialect is exercised
// by the generator unit tests; this fixture is the runtime half, so the shape stays
// bound and callable end to end.

import Foundation

public struct SourceNamedBox<Value> {
    public let value: Value

    public init(value: Value) {
        self.value = value
    }
}

public struct SourceNamedPair<Key, Value> {
    public let first: Key
    public let second: Value

    public init(first: Key, second: Value) {
        self.first = first
        self.second = second
    }
}

public func makeSourceNamedIntBox(_ value: Int) -> SourceNamedBox<Int> {
    return SourceNamedBox(value: value)
}

public func makeSourceNamedStringBox(_ value: String) -> SourceNamedBox<String> {
    return SourceNamedBox(value: value)
}

public func makeSourceNamedPair(_ first: String, _ second: Int) -> SourceNamedPair<String, Int> {
    return SourceNamedPair(first: first, second: second)
}
