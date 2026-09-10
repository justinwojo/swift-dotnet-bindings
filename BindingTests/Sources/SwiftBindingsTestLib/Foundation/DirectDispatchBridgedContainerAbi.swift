// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - ObjC-bridged containers beside a nested frozen struct
//
// `[URL]`, `[String: URL]`, `Set<URL>` and their optionals do not marshal element by element.
// The C# side builds an NSArray / NSDictionary / NSSet and passes its handle, and that rendering
// is correct at exactly one kind of boundary: a `@_cdecl` wrapper, which takes the collection as
// an object pointer and bridges it back to the native container on entry. Swift's own entry
// point has no such boundary. It expects its native array storage — one refcounted pointer that
// is NOT an ObjC object — so a member reached on its own symbol with a bridged container in a
// slot would receive the wrong value going in, and on the way out would hand back native storage
// that C# then reads as an NSArray and takes ownership of. The generator refuses a member in
// that position rather than making the call.
//
// Every member here pairs a bridged container with a NESTED frozen-struct operand, and that
// pairing used to be what put them on Swift's own symbol: a nested frozen struct declined the
// wrapper outright. It no longer does — such a struct now travels to the wrapper as a raw
// pointer and is rebuilt inside the wrapper body, where a nested name is ordinary Swift — so
// every member below reaches Swift through the `@_cdecl` frame, which is the boundary the
// NSArray / NSDictionary / NSSet rendering is correct at. What the fixture pins is that the
// container survives that frame: the values Swift sees going in and the values C# reads coming
// back are the ones that were handed over.
//
// Shapes, each carrying the container across the wrapper in a different position:
//
//   * an initializer over an OPTIONAL container, so the nil arm crosses as well as the present
//     one;
//   * a static method over a BARE container (array and set), where the container is an ordinary
//     borrowed parameter;
//   * a subscript over a BARE container (array and dictionary), which carries it in both
//     directions — in through the setter's new value, out through the getter's return.
//
// The control is the same bare `[URL]` parameter on a member with NO frozen-struct sibling: it
// was always wrapper-eligible, so it answers the same way its siblings now do.

/// Host for the initializer and method shapes. The initializers store their container privately
/// and expose `stamp`, so a construction is observed through the value that comes back out.
public struct DirectBridgedContainerHost {
    /// Nested and frozen: the operand the wrapper has to rebuild from a raw pointer beside the
    /// bridged container.
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

    /// Optional array of a bridged element type, present and nil arms both live.
    public init(urls: [URL]?, stamp: BridgedMarker) {
        self.urls = urls ?? []
        self.lookup = [:]
        self.unique = []
        self.stamp = stamp
    }

    /// Optional dictionary whose values are a bridged element type.
    public init(lookup: [String: URL]?, stamp: BridgedMarker) {
        self.urls = []
        self.lookup = lookup ?? [:]
        self.unique = []
        self.stamp = stamp
    }

    /// Optional set of a bridged element type.
    public init(unique: Set<URL>?, stamp: BridgedMarker) {
        self.urls = []
        self.lookup = [:]
        self.unique = unique ?? []
        self.stamp = stamp
    }

    /// Read-back for the initializer arms. A stamp-only assertion cannot separate a container
    /// that crossed intact from one that arrived empty, because the nil arm stores an empty
    /// container too — the present and nil arms would answer identically. These report what the
    /// initializer actually stored.
    public var urlCount: Int32 { return Int32(urls.count) }

    /// Read-back for the dictionary arm, same reasoning.
    public var lookupCount: Int32 { return Int32(lookup.count) }

    /// Read-back for the set arm, same reasoning.
    public var uniqueCount: Int32 { return Int32(unique.count) }

    /// A bare array of a bridged element type as a method parameter. Static so it is reachable
    /// without first choosing one of the initializers' container arms.
    public static func borrowedCount(_ others: [URL], stamp: BridgedMarker) -> Int32 {
        return Int32(others.count) &+ stamp.value
    }

    /// The same shape over a bare set.
    public static func borrowedUnique(_ unique: Set<URL>, stamp: BridgedMarker) -> Int32 {
        return Int32(unique.count) &+ stamp.value
    }

    /// Control: the same bare `[URL]` parameter with no frozen-struct sibling. It was reached
    /// through the `@_cdecl` frame before its siblings were, so it answers the same way and
    /// isolates the container handling from the frozen-struct operand beside it.
    public static func liveCount(_ urls: [URL]) -> Int32 {
        return Int32(urls.count)
    }
}

/// Bare `[URL]` as a subscript element type, indexed by the nested frozen marker so both
/// accessors carry the marker beside the container.
public struct DirectBridgedSlotHost {
    private var urls: [URL]

    public init(stamp: DirectBridgedContainerHost.BridgedMarker) {
        self.urls = []
    }

    /// Both accessors carry the container: the getter returns it, the setter receives it.
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
