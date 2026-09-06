// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - ObjC-bridged containers on the DIRECT CallConvSwift arm
//
// `[URL]`, `[String: URL]`, `Set<URL>` and their optionals do not marshal element by element.
// The C# side builds an NSArray / NSDictionary / NSSet and passes its handle, and that rendering
// is correct at exactly one kind of boundary: a `@_cdecl` wrapper, which takes the collection as
// an object pointer and bridges it back to the native container on entry. Swift's own entry
// point has no such boundary. It expects its native array storage — one refcounted pointer that
// is NOT an ObjC object — so a member reached on its own symbol with a bridged container in a
// slot would receive the wrong value going in, and on the way out would hand back native storage
// that C# then reads as an NSArray and takes ownership of.
//
// The generator refuses these members instead of making that call: the body throws
// NotSupportedException and (for methods and initializers) the declaration carries the SB0009
// marker. This fixture pins that refusal on every shape that reaches the direct arm, and pins
// that it does NOT reach the wrapper arm, where the same shapes are ordinary and live.
//
// Every refused member here takes a NESTED frozen-struct parameter beside the container. That is
// the lever that declines the wrapper: an ObjC-bridged parameter on its own is `@_cdecl`-
// compatible, so `init(urls: [URL]?)` alone would be reached through the wrapper and would bind.
// The nested frozen struct is what puts these members on Swift's own symbol.
//
// Shapes, and the plan each reaches the direct arm through:
//
//   * an initializer over an OPTIONAL container — the direct constructor path declines a bare
//     Array/Dictionary/Set parameter outright, but takes the optional, so this is the only
//     initializer shape that gets as far as the floor;
//   * a static method over a BARE container (array and set) — no refusal ahead of the floor;
//   * a subscript over a BARE container (array and dictionary) — both accessors land on the
//     floor. Accessors are refused without a declaration marker, since a marker on the private
//     synthesized accessor would stop the public indexer compiling; the indexer itself throws.
//
// The positive control is the same bare `[URL]` parameter on a member with NO frozen-struct
// sibling: wrapper-eligible, reached through the `@_cdecl` frame, binds and answers.

/// Host for the initializer and method shapes. Nothing here is constructible from C# — every
/// initializer is refused — which is the point: the static members carry the method shapes.
public struct DirectBridgedContainerHost {
    /// Nested and frozen purely to decline the wrapper for the member that takes it.
    @frozen
    public struct BridgedMarker {
        public let value: Int32

        public init(value: Int32) {
            self.value = value
        }
    }

    private let urls: [URL]
    private let lookup: [String: URL]
    private let unique: Set<URL>
    public let stamp: BridgedMarker

    /// Refused: optional array of a bridged element type on the direct arm.
    public init(urls: [URL]?, stamp: BridgedMarker) {
        self.urls = urls ?? []
        self.lookup = [:]
        self.unique = []
        self.stamp = stamp
    }

    /// Refused: optional dictionary whose values are a bridged element type.
    public init(lookup: [String: URL]?, stamp: BridgedMarker) {
        self.urls = []
        self.lookup = lookup ?? [:]
        self.unique = []
        self.stamp = stamp
    }

    /// Refused: optional set of a bridged element type.
    public init(unique: Set<URL>?, stamp: BridgedMarker) {
        self.urls = []
        self.lookup = [:]
        self.unique = unique ?? []
        self.stamp = stamp
    }

    /// Refused: a bare array of a bridged element type as a method parameter. Static so that it
    /// stays reachable from C# on a host with no callable initializer.
    public static func borrowedCount(_ others: [URL], stamp: BridgedMarker) -> Int32 {
        return Int32(others.count) &+ stamp.value
    }

    /// Refused: the same shape over a bare set.
    public static func borrowedUnique(_ unique: Set<URL>, stamp: BridgedMarker) -> Int32 {
        return Int32(unique.count) &+ stamp.value
    }

    /// Positive control: the same bare `[URL]` parameter with no frozen-struct sibling, so the
    /// member is wrapper-eligible and reached through the `@_cdecl` frame where the NSArray
    /// rendering is the right one. Must bind and answer.
    public static func liveCount(_ urls: [URL]) -> Int32 {
        return Int32(urls.count)
    }
}

/// Bare `[URL]` as a subscript element type, indexed by the nested frozen marker so both
/// accessors stay on Swift's own symbols. Constructible, so the accessors are reachable.
public struct DirectBridgedSlotHost {
    private var urls: [URL]

    public init(stamp: DirectBridgedContainerHost.BridgedMarker) {
        self.urls = []
    }

    /// Refused on both accessors: the getter would return native storage for C# to read as an
    /// NSArray, the setter would receive an NSArray where native storage belongs.
    public subscript(stamp: DirectBridgedContainerHost.BridgedMarker) -> [URL] {
        get { return urls }
        set { urls = newValue }
    }
}

/// The same subscript shape over a bare `[String: URL]`, on its own host so the two indexers do
/// not project onto one C# signature.
public struct DirectBridgedLookupHost {
    private var lookup: [String: URL]

    public init(stamp: DirectBridgedContainerHost.BridgedMarker) {
        self.lookup = [:]
    }

    public subscript(stamp: DirectBridgedContainerHost.BridgedMarker) -> [String: URL] {
        get { return lookup }
        set { lookup = newValue }
    }
}
