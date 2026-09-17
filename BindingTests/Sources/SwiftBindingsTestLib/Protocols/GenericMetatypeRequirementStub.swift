// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

// A method-level generic requirement whose parameter is the metatype of a generic nominal
// (`FieldBox<K, V>.Type`). Such a requirement cannot be dispatched through a vtable, so it gets a
// stub witness, but the stub still has to spell the requirement's exact signature. The ABI type is
// the nominal with a nested `.Type`, and dropping that nested component declares the witness as
// taking an instance (`FieldBox<K, V>`) instead of the metatype, which the conformance rejects.

import Foundation

public struct FieldBox<Key, Value> {
    public init() {}
}

public protocol MetatypeFieldVisitor {
    mutating func visitScalar(value: Int32) throws
    mutating func visitMap<K, V>(fieldType: FieldBox<K, V>.Type, count: Int32) throws
}

extension MetatypeFieldVisitor {
    public mutating func visitScalar(value: Int32) throws {}
}
