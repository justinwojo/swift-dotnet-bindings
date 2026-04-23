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

// MARK: - Namespace Enum CSM (Session 4)
// Swift's "namespace enum" pattern — caseless enums used solely as static member
// containers (e.g., CryptoKit's `enum AES { enum GCM { ... } }` and `enum ChaChaPoly`).
// The C# emitter projects these as `public static partial class` types. Before Session
// 4, ConcreteProtocolSpecializationEmitter was never invoked for EnumHandler's
// namespace-enum path, so static methods with method-level protocol generics
// (DataProtocol / ContiguousBytes) never got concrete overloads — the only surface
// was a tombstoned generic signature. This fixture is the direct reproducer:
// `BytesNamespace.countBytes<D: DataProtocol>` is non-throwing and takes a single
// DataProtocol parameter. With the EnumHandler CSM call in place, the emitter
// produces two static C# overloads — CountBytes(byte[]) and CountBytes(Data) —
// and skips them entirely without it.
public enum BytesNamespace {
    /// Static method with a method-level DataProtocol generic on a caseless (namespace)
    /// enum. Non-throwing so it lands purely through the sync CSM path. Returns the
    /// byte count so the test can round-trip a value through each conformer pairing.
    public static func countBytes<D: DataProtocol>(_ bytes: D) -> Int {
        return bytes.count
    }

    /// Second CSM-eligible static on the same namespace enum to ensure the per-type
    /// emission loop covers multiple methods — not just the first one.
    public static func firstByteOrZero<D: DataProtocol>(_ bytes: D) -> UInt8 {
        for region in bytes.regions {
            for byte in region { return byte }
        }
        return 0
    }
}

/// Error thrown by the sync-throws CSM fixture when a validation precondition fails.
/// Kept distinct from ParseError so the description assertion can pin the exact case.
public enum BytesValidationError: Error, CustomStringConvertible {
    case empty
    case tooLarge(Int)

    public var description: String {
        switch self {
        case .empty: return "BytesValidationError.empty"
        case .tooLarge(let n): return "BytesValidationError.tooLarge(\(n))"
        }
    }
}

// MARK: - Sync-throws namespace-enum CSM fixture
// Mirrors CryptoKit's AEAD `Seal<Plaintext: DataProtocol>(...) throws` shape:
// caseless namespace enum + static method with a DataProtocol method-level generic
// + `throws`. Each method exercises a different cdecl-return shape so the do/catch
// Swift wrapper and the `out IntPtr errorPtr` C# thread are stressed across the
// paths CryptoKit consumers hit (direct Int return, Bool return, Void, and indirect
// struct result).
public enum ThrowingBytesNamespace {
    /// Direct Int return. Throws when the input is empty.
    public static func countBytesOrThrow<D: DataProtocol>(_ bytes: D) throws -> Int {
        if bytes.count == 0 { throw BytesValidationError.empty }
        return bytes.count
    }

    /// Direct Bool return (Bool → Int8 cdecl sentinel). Throws when larger than `limit`.
    public static func fitsWithin<D: DataProtocol>(_ bytes: D, limit: Int) throws -> Bool {
        if bytes.count > 0x1000 { throw BytesValidationError.tooLarge(bytes.count) }
        return bytes.count <= limit
    }

    /// Void return. Throws when empty; otherwise no observable effect beyond the round-trip.
    public static func assertNonEmpty<D: DataProtocol>(_ bytes: D) throws {
        if bytes.count == 0 { throw BytesValidationError.empty }
    }

    /// Indirect struct result. Mirrors CryptoKit `Seal(...) throws -> SealedBox` where the
    /// return is a non-frozen struct landing through resultPtr.initializeMemory.
    public static func makeBytesSummary<D: DataProtocol>(_ bytes: D) throws -> BytesSummary {
        if bytes.count == 0 { throw BytesValidationError.empty }
        var xor: UInt8 = 0
        for region in bytes.regions {
            for byte in region { xor ^= byte }
        }
        return BytesSummary(count: bytes.count, xor: xor)
    }
}

/// Non-frozen struct used as the indirect return from `makeBytesSummary`.
public struct BytesSummary {
    public let count: Int
    public let xor: UInt8
    public init(count: Int, xor: UInt8) { self.count = count; self.xor = xor }
}

// MARK: - Additional sync-throws CSM direct-return shapes
// These fixtures round out the throwing-CSM matrix that CountBytesOrThrow / FitsWithin
// cover for Int / Bool. They exercise the two direct-return shapes that were previously
// only validated on the non-CSM path: SimpleEnum → raw scalar and Class → pointer.
// Without a mapping conversion on the C# side, the generated public method would try
// to return a raw IntPtr/underlying scalar through a projected enum/class return type
// and fail compilation.

/// SimpleEnum (Int8 raw) used as the direct-return shape for a sync-throws CSM method.
public enum BytesKind: Int8 {
    case empty = 0
    case small = 1
    case large = 2
}

/// Class used as the direct ClassPointer return shape for a sync-throws CSM method.
public class BytesReport {
    public let byteCount: Int
    public let firstByte: UInt8
    public init(byteCount: Int, firstByte: UInt8) {
        self.byteCount = byteCount
        self.firstByte = firstByte
    }
}

extension BytesNamespace {
    /// Non-throwing direct SimpleEnum return. Pre-unification the @_cdecl header would
    /// read `-> BytesKind` (Swift enum), which swiftc silently strips with
    /// "result type cannot be represented in Objective-C" — the P/Invoke then blows up
    /// at runtime with "entry point not found". Exercises the lifted mapping gate.
    public static func classifyBytesNoThrow<D: DataProtocol>(_ bytes: D) -> BytesKind {
        if bytes.count == 0 { return .empty }
        return bytes.count < 8 ? .small : .large
    }

    /// Non-throwing direct ClassPointer return. Same ABI constraint as the SimpleEnum
    /// case: @_cdecl returning `BytesReport` directly strips the symbol. The fix
    /// routes through `Unmanaged.passRetained(_result as AnyObject).toOpaque()` and
    /// the C# side wraps the IntPtr in a SwiftHandle.
    public static func describeBytesNoThrow<D: DataProtocol>(_ bytes: D) -> BytesReport {
        var first: UInt8 = 0
        for region in bytes.regions {
            if let b = region.first { first = b; break }
        }
        return BytesReport(byteCount: bytes.count, firstByte: first)
    }
}

extension ThrowingBytesNamespace {
    /// Direct SimpleEnum return (BytesKind → Int8 @_cdecl). Throws on overflow.
    public static func classifyBytes<D: DataProtocol>(_ bytes: D) throws -> BytesKind {
        if bytes.count > 0x1000 { throw BytesValidationError.tooLarge(bytes.count) }
        if bytes.count == 0 { return .empty }
        return bytes.count < 8 ? .small : .large
    }

    /// Direct ClassPointer return (BytesReport → UnsafeMutableRawPointer @_cdecl).
    /// Throws when the input is empty so the error path can leak-check the
    /// caller-freed SwiftHandle buffer.
    public static func describeBytes<D: DataProtocol>(_ bytes: D) throws -> BytesReport {
        if bytes.count == 0 { throw BytesValidationError.empty }
        var first: UInt8 = 0
        for region in bytes.regions {
            for byte in region { first = byte; break }
            if first != 0 || bytes.count > 0 { break }
        }
        return BytesReport(byteCount: bytes.count, firstByte: first)
    }
}

/// Mutating + throwing + Bool-return CSM method on a non-generic struct. Covers the
/// Swift-side `selfWriteBack + throws + directReturnMapping` shape: the @_cdecl header
/// declares Int8 (via the Bool mapping) and the body must route `_result` through the
/// Bool→Int8 conversion *after* the self write-back. Without the conversion the raw
/// Bool `_result` fails Swift type-check and the @_cdecl is silently stripped.
public struct ThrowingByteCollector {
    private var _bytesSeen: Int = 0
    private var _accepted: Bool = false

    public init() {}

    /// Mutates internal counters regardless of the throw, so the write-back path is
    /// always exercised. Returns true when the input is non-empty; false otherwise.
    public mutating func acceptIfSmall<D: DataProtocol>(_ bytes: D, cap: Int) throws -> Bool {
        _bytesSeen += bytes.count
        if bytes.count > cap { throw BytesValidationError.tooLarge(bytes.count) }
        _accepted = bytes.count > 0
        return _accepted
    }

    public var bytesSeen: Int { _bytesSeen }
    public var accepted: Bool { _accepted }
}
