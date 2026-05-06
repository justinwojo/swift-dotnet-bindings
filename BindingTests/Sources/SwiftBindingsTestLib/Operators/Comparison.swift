// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Comparison Operators

/// Frozen struct for testing comparison operator emission.
@frozen
public struct ComparableValue: Equatable, Comparable {
    public let value: Int32

    public init(value: Int32) {
        self.value = value
    }

    public static func == (lhs: ComparableValue, rhs: ComparableValue) -> Bool {
        return lhs.value == rhs.value
    }

    public static func < (lhs: ComparableValue, rhs: ComparableValue) -> Bool {
        return lhs.value < rhs.value
    }

    // Note: >, <=, >= are automatically synthesized from == and < by the binding generator.
}

// MARK: - Custom Equality Logic

/// Frozen struct with custom equality that uses a tolerance of 5.
/// Two values are considered equal if their difference is within the tolerance.
/// This tests that the binding generator correctly emits custom == operators
/// rather than relying on default memberwise equality.
@frozen
public struct ApproximatelyEqual: Equatable {
    public let value: Int32

    public init(value: Int32) {
        self.value = value
    }

    public static func == (lhs: ApproximatelyEqual, rhs: ApproximatelyEqual) -> Bool {
        return abs(Int(lhs.value) - Int(rhs.value)) <= 5
    }
}

// MARK: - Non-Frozen Struct Equality (Alamofire HTTPHeader pattern)

/// Non-frozen struct with Equatable — takes the @_cdecl wrapper path for equality
/// (different from frozen structs which use CallConvSwift).
/// Real-world pattern: Alamofire HTTPHeader ==, KeychainAccess AuthenticationPolicy ==.
public struct Tag: Equatable {
    public var key: String
    public var value: String

    public init(key: String, value: String) {
        self.key = key
        self.value = value
    }
}

// MARK: - Extension-Declared Hashable (MusicItemID pattern)

/// `Tag` declares only `Equatable` on the type itself; Hashable is added via
/// extension. The generator must pick this up and emit a non-stub
/// GetHashCode just like a primary-conformance Hashable type.
extension Tag: Hashable {
    public func hash(into hasher: inout Hasher) {
        hasher.combine(key)
        hasher.combine(value)
    }
}

/// Frozen struct that adopts Hashable purely via extension (no Hashable in
/// the primary declaration). Verifies the predicate widening covers the
/// frozen path as well as the non-frozen one above.
@frozen
public struct LabeledScore: Equatable {
    public let label: String
    public let score: Int32

    public init(label: String, score: Int32) {
        self.label = label
        self.score = score
    }
}

extension LabeledScore: Hashable {
    public func hash(into hasher: inout Hasher) {
        hasher.combine(label)
        hasher.combine(score)
    }
}

// MARK: - Synthesised-Extension Hashable

/// Frozen struct whose Hashable conformance is added via an extension with NO
/// body — Swift auto-synthesises `hash(into:)` from the stored properties.
/// Contrasts with `LabeledScore` above, which carries a hand-written
/// `hash(into:)` in its extension. Both forms surface in the generator's
/// conformance list as a same-shape `Swift.Hashable` entry, so the predicate
/// widening MUST cover both — otherwise the synthesised form silently regresses
/// to the 0-stub GetHashCode while the manual form works.
@frozen
public struct PointKey: Equatable {
    public let x: Int32
    public let y: Int32

    public init(x: Int32, y: Int32) {
        self.x = x
        self.y = y
    }
}

extension PointKey: Hashable {}

/// Non-frozen counterpart to `PointKey` for the synthesised-extension form on
/// a SafeHandle-backed C# projection (the validation-library shape). Combined
/// with `Tag` (manual extension), this pins both extension shapes for the
/// non-frozen path.
public struct LabelKey: Equatable {
    public let category: String
    public let index: Int32

    public init(category: String, index: Int32) {
        self.category = category
        self.index = index
    }
}

extension LabelKey: Hashable {}

// MARK: - SafeHandle-Backed Hashable Class (MusicItemID-shape, intra-tree)

/// A non-frozen reference type that conforms to Hashable. Mirrors the
/// MusicKit `MusicItemID` shape that exposed Equatable Defect 1 in
/// validation: a SafeHandle-backed C# class whose GetHashCode used to
/// return a constant 0 because the Hashable witness wasn't being
/// recognised on classes. The runtime SwiftHashable bridge has to fold a
/// stable value here for Dictionary&lt;HashedHandle, V&gt; to work.
public class HashedHandle: Hashable {
    public let identifier: String

    public init(identifier: String) {
        self.identifier = identifier
    }

    public static func == (lhs: HashedHandle, rhs: HashedHandle) -> Bool {
        return lhs.identifier == rhs.identifier
    }

    public func hash(into hasher: inout Hasher) {
        hasher.combine(identifier)
    }
}
