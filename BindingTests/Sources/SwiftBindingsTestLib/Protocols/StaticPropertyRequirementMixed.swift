// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

// A protocol that mixes a static property requirement with dispatchable instance requirements (a
// storage backend that names its store kind statically). The instance members dispatch through
// the vtable as usual; the static requirement has no per-implementer receiver, so the carrier can
// only satisfy it with a stub. Omitting a witness for it leaves the whole conformance unsatisfied,
// which takes every instance member down with it.
//
// The static-only case (no instance members at all) stays skipped by design — see
// StaticOnlyProtocolSkipping.swift.

import Foundation

public protocol StaticTaggedStorage: AnyObject {
    static var storeKind: String { get }
    var capacity: Int32 { get }
    func store(_ value: Int32) -> Int32
}

/// Reads the instance requirement through the existential.
public func staticTaggedStorageCapacity(_ storage: any StaticTaggedStorage) -> Int32 {
    storage.capacity
}

/// Calls the instance method requirement through the existential.
public func staticTaggedStorageStore(_ storage: any StaticTaggedStorage, value: Int32) -> Int32 {
    storage.store(value)
}
