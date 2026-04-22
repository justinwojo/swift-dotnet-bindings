// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Method-Level Generics
// Tests for methods with their own generic type parameters constrained
// to protocols without Self/associated type requirements.
// The generator bridges these via Swift 5.7+ implicit existential opening.

/// Non-generic class with method-level generic parameters.
/// Each method has <T: Describable> — the generator should emit @_cdecl wrappers
/// that receive an existential container and open it via implicit conversion.
public class GenericMethodHost {
    private var _label: String

    public init(label: String) {
        self._label = label
    }

    /// Simplest case: void return, single generic param.
    public func printDescription<T: Describable>(_ item: T) {
        _ = "\(_label): \(item.describe())"
    }

    /// Returns a non-generic type (String) from a generic param.
    public func getDescription<T: Describable>(_ item: T) -> String {
        return "\(_label): \(item.describe())"
    }

    /// Static method with method-level generic.
    public static func staticDescribe<T: Describable>(_ item: T) -> String {
        return "static: \(item.describe())"
    }

    /// Multiple parameters: one generic, one primitive.
    public func describeWithTag<T: Describable>(_ item: T, tag: Int32) -> String {
        return "[\(tag)] \(_label): \(item.describe())"
    }
}

/// A simple class conforming to Describable for testing.
public class SimpleDescribable: Describable {
    public let description: String

    public init(description: String) {
        self.description = description
    }

    public func describe() -> String {
        return description
    }
}

// MARK: - CSM DataProtocol Specialization (Session 1)
// Exercises the new CSM conformer categories:
//   • InlineSwiftStruct — Foundation.Data (blittable C# struct, pinned via &arg)
//   • RawBuffer — [UInt8] / byte[] (C# fixed(byte*) + zero-copy Data reconstruction)
// DataHasher additionally exercises the mutating-self path for CSM (Blocker 1).

/// Non-generic struct with a mutating method taking a DataProtocol-constrained generic.
/// CSM emits one overload per conformer (Foundation.Data, byte[]). Each overload pins
/// the caller-owned bytes, reconstructs a Swift Data, and calls the underlying update.
public struct DataHasher {
    private var _count: Int = 0
    private var _sum: UInt64 = 0
    // Byte-order-sensitive witnesses: count + sum (commutative) can't distinguish
    // [1,2,3] from [3,2,1]. firstByte/lastByte preserve order, and _hasSeenBytes
    // avoids Optional-on-frozen-struct layout mismatches for the initial-state flag.
    private var _firstByte: UInt8 = 0
    private var _lastByte: UInt8 = 0
    private var _hasSeenBytes: Bool = false

    public init() {}

    public mutating func update<D: DataProtocol>(_ data: D) {
        _count += data.count
        for region in data.regions {
            for byte in region {
                _sum = _sum &+ UInt64(byte)
                if !_hasSeenBytes {
                    _firstByte = byte
                    _hasSeenBytes = true
                }
                _lastByte = byte
            }
        }
    }

    public var count: Int { _count }
    public var checksum: UInt64 { _sum }
    public var firstByte: UInt8 { _firstByte }
    public var lastByte: UInt8 { _lastByte }
    public var hasSeenBytes: Bool { _hasSeenBytes }
}

/// Non-generic struct with a ContiguousBytes-constrained generic method. Parallel to
/// DataHasher but exercises the ContiguousBytes protocol path (different conformer
/// set from DataProtocol — Data and DispatchData conform; raw buffers feed in via
/// Array<UInt8>-or-equivalent). End-to-end coverage for the protocol-agnostic CSM
/// pipeline: swapping the constraint protocol must yield an equally-valid set of
/// concrete overloads without changes to the emitter.
public struct ContiguousBytesConsumer {
    private var _bytesConsumed: Int = 0
    private var _firstByte: UInt8 = 0
    private var _hasSeenBytes: Bool = false

    public init() {}

    public mutating func consume<C: ContiguousBytes>(_ bytes: C) {
        bytes.withUnsafeBytes { raw in
            _bytesConsumed += raw.count
            if !_hasSeenBytes, let first = raw.first {
                _firstByte = first
                _hasSeenBytes = true
            }
        }
    }

    public var bytesConsumed: Int { _bytesConsumed }
    public var firstByte: UInt8 { _firstByte }
    public var hasSeenBytes: Bool { _hasSeenBytes }
}

/// Non-generic class with a method constrained by TWO DataProtocol generics. Exercises
/// the sync multi-param cartesian product path: 2×2 = 4 emitted C# overloads.
public class MultiPATCombiner {
    public init() {}

    public func combinedCount<A: DataProtocol, B: DataProtocol>(_ a: A, _ b: B) -> Int {
        return a.count + b.count
    }
}

// MARK: - CSM Generic Parent Specialization (Session 2)
// Exercises the parent-generic × method-generic cartesian emission path. The parent
// generic T is constrained to SearchableItem (a Self-requiring protocol with three
// struct conformers already registered in specialization-hints.json). The method
// generic D is constrained to DataProtocol. Together they produce
// 3 (parent) × 2 (method) = 6 emitted overloads per method, grouped into three
// per-parent-conformer extension helper classes:
//   GenericContainerSongItemCsmExtensions   { Append(..., Data); Append(..., byte[]) }
//   GenericContainerAlbumItemCsmExtensions  { Append(..., Data); Append(..., byte[]) }
//   GenericContainerArtistItemCsmExtensions { Append(..., Data); Append(..., byte[]) }

public struct GenericContainer<T: SearchableItem> {
    private var _count: Int = 0
    private var _tagBytes: Int = 0

    public init() {}

    public mutating func append<D: DataProtocol>(item: T, tag: D) {
        _count += 1
        _tagBytes += tag.count
    }

    public func count() -> Int { _count }
    public func tagBytes() -> Int { _tagBytes }

    /// Non-mutating CSM-eligible read. Exists because `count()/tagBytes()` are
    /// non-generic methods on a generic struct and crash under Mono JIT (metadata
    /// CallConvSwift). This method is method-generic, so it routes through the
    /// CSM extension pipeline just like `append` — giving us a crash-free observer
    /// that can witness `append`'s mutations across the parent×method pairing.
    /// The `probe` parameter is ignored; only its count is consulted to make the
    /// signature non-degenerate.
    public func countSeen<D: DataProtocol>(_ probe: D) -> Int {
        _ = probe.count
        return _count
    }
}

// MARK: - CSM Pairing Filter (Issue 3 regression guard)
// Exercises the bilateral pairing filter that rejects method-generic conformers whose
// associated types don't satisfy a same-type constraint against the parent generic.
// Without the filter, the planner enumerates the full cartesian — 3 parents (SongItem,
// AlbumItem, ArtistItem) × 6 Sequence conformers = 18 pairings — and emits wrappers
// like `ElementBoundContainer<SongItem>.appendAll<[UInt8]>` whose `where S.Element == T`
// constraint is unsatisfiable. The emitted Swift `@_cdecl` body calls through to the
// constrained method and the Swift compiler rejects it: "cannot convert value of type
// 'UInt8' to expected element type 'SongItem'". Filter keeps only the 3 matching pairs
// ([SongItem]×SongItem, [AlbumItem]×AlbumItem, [ArtistItem]×ArtistItem).
public struct ElementBoundContainer<T: SearchableItem> {
    private var _count: Int = 0

    public init() {}

    /// Same-type constraint `S.Element == T` forces the pairing filter: only Sequence
    /// conformers whose `Element` matches the concrete parent T can survive.
    public mutating func appendAll<S: Sequence>(_ items: S) where S.Element == T {
        for _ in items { _count += 1 }
    }

    /// Generic witness for _count. Non-generic methods on a generic struct crash Mono
    /// JIT (see GenericContainer.count()), so expose the state through a method-generic
    /// read that also routes through the CSM extension pipeline.
    public func countSeen<D: DataProtocol>(_ probe: D) -> Int {
        _ = probe.count
        return _count
    }
}
