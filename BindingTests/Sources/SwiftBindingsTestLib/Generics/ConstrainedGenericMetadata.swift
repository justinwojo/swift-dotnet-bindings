// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

// MARK: - Constrained-generic type-metadata accessor coverage
//
// These types exercise the **type-level** metadata accessor PWT path —
// i.e. the path that emits `_payloadSize = SwiftObjectHelper<Self>.GetTypeMetadata().Size`
// and `static TypeMetadata ISwiftObject.GetTypeMetadata()`. The accessor's
// Swift ABI requires one PWT per protocol conformance after the type-metadata
// args, in declaration-order grouped, lex-sorted within each generic param.
//
// For background see src/docs/constrained-generic-metadata-witness-tables.md.
// The previous workaround was a fragile lazy `_payloadSize` field initializer
// that masked an arm64e PAC trap; these tests guard the correct ABI fix.

import Foundation

// MARK: - Generic enum constrained on a resolvable user protocol
//
// Reuses Describable from Protocols/BasicProtocols.swift. The metadata
// accessor for this enum needs (request, T_metadata, T_DescribablePWT).

public enum DescribableBox<T: Describable> {
    case wrap(T)

    /// Forces the metadata accessor to fire (the property accessor needs the
    /// type metadata to size the storage). On simulator + device this is the
    /// minimum ceremony to hit `__swift_instantiateGenericMetadata`.
    public func unwrappedDescription() -> String {
        switch self {
        case .wrap(let inner):
            return inner.describe()
        }
    }
}

// MARK: - Generic non-frozen struct constrained on a resolvable user protocol
//
// Non-frozen structs project as C# classes with SafeHandle payload — the
// `_payloadSize` field initializer was the original crash site. This type's
// `GetTypeMetadata()` must include the Describable PWT.

public struct DescribableHolder<T: Describable> {
    private let item: T

    public init(item: T) {
        self.item = item
    }

    public func held() -> String {
        return item.describe()
    }
}

