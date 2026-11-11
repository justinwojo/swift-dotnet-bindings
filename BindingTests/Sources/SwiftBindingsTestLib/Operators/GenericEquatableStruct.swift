// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.
//
// A generic struct that is Equatable (and not Hashable). A generic type gets no
// per-type `@_cdecl` equality wrapper — the wrapper cannot be instantiated without
// knowing the type argument — so C# `==` goes through the runtime's
// SwiftEquatable.Equals, which looks up the type's Equatable conformance
// descriptor. Covers the direct comparison and elements read out of an array,
// which is how the shape usually reaches a consumer.

import Foundation

public struct EquatableOnlyBox<V: Equatable>: Equatable {
    public let value: V

    public init(value: V) {
        self.value = value
    }
}

public struct EquatableOnlyBoxHolder {
    public let items: [EquatableOnlyBox<Int>]

    public init(values: [Int]) {
        items = values.map { EquatableOnlyBox(value: $0) }
    }
}

public func makeEquatableOnlyBox(_ value: Int) -> EquatableOnlyBox<Int> {
    return EquatableOnlyBox(value: value)
}

public func makeEquatableOnlyStringBox(_ value: String) -> EquatableOnlyBox<String> {
    return EquatableOnlyBox(value: value)
}
