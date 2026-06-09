// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

// MARK: - Protocol-bag KeyPath singleton fixture
//
// The `KeyPathSingletons.swift` fixture covers the nested-concrete-struct shape:
// `MockBookSession4.LibraryFilter` is a stored-property struct directly nested
// inside the conformer.
//
// MusicKit, in contrast, exposes its associated-type bag through a typealias
// to a module-scope PROTOCOL:
//
//     extension MusicKit.Album : MusicKit.MusicLibraryRequestable {
//       public typealias LibraryFilter = MusicKit.LibraryAlbumFilter
//     }
//     public protocol LibraryAlbumFilter { var id: MusicItemID { get } ... }
//
// Property requirements on a `public protocol` are abstract — `HasStorage == false`
// — but `\Protocol.requirement` is a valid Swift KeyPath literal that the runtime
// resolves through the conforming type's witness table at use time. The protocol-bag
// extension admits this shape via two mechanisms:
//
// 1. `KeyPathSingletonEmitter.FindBagDecl` branches 3 & 4 resolve the
//    typealias against module-scope types (not just nested types).
// 2. `KeyPathSingletonEmitter.IsEmittableBag` and `WhyPropertyNotEmittable`
//    accept `ProtocolDecl` bags with abstract getter requirements
//    (`allowAbstract = true` when `bagDecl is ProtocolDecl`).
//
// This fixture exercises that shape end-to-end WITHOUT touching MusicKit:
// a non-MusicKit PAT generic parent whose conformers' `Filter` associated type
// resolves to a module-scope protocol with abstract getter requirements. The
// generator must emit per-conformer typed `KeyPath<I*Filter, Value>` singletons
// using `\ProtocolBag_BookFilter.title`-style literals, and a downstream Swift
// consumer must read through them via `subscript(keyPath:)` against a concrete
// witness type.

// MARK: PAT + closed conformers (the "MusicLibraryRequest<Item>" analogue)

public protocol ProtocolBag_Filterable {
    associatedtype Filter
    static var defaultFilterDescription: Swift.String { get }
}

// MARK: Module-scope protocol bags (the "LibraryAlbumFilter / LibrarySongFilter" analogue)
//
// Property requirements deliberately mix String / Int / Bool / Optional<Int>
// so the per-property KeyPath value-type spelling exercises projection across
// the basic shapes the OUT path supports.

public protocol ProtocolBag_BookFilter {
    var title: Swift.String { get }
    var year: Swift.Int { get }
    var isFiction: Swift.Bool { get }
    var rating: Swift.Int? { get }
}

public protocol ProtocolBag_MovieFilter {
    var title: Swift.String { get }
    var runtimeMinutes: Swift.Int { get }
}

// MARK: Closed conformers (the "extension Album: MusicLibraryRequestable" analogue)
//
// The `typealias` indirection is the trigger for protocol-bag broadening —
// Album.LibraryFilter is *not* a nested struct, it's a typealias to a module-
// scope protocol. ProtocolBag_Book / ProtocolBag_Movie mirror that shape.

public struct ProtocolBag_Book: ProtocolBag_Filterable {
    public typealias Filter = ProtocolBag_BookFilter
    public init() {}
    public static let defaultFilterDescription: Swift.String = "ProtocolBag_Book"
}

public struct ProtocolBag_Movie: ProtocolBag_Filterable {
    public typealias Filter = ProtocolBag_MovieFilter
    public init() {}
    public static let defaultFilterDescription: Swift.String = "ProtocolBag_Movie"
}

// MARK: Generic parent — the demand signal
//
// References `KeyPath<Item.Filter, *>` to register demand; the emitter walks
// each closed conformer's substituted Filter (a module-scope protocol bag) and
// emits a typed singleton per requirement. The method bodies are placeholders;
// the demand walk inspects signatures only.

public struct ProtocolBag_Request<Item: ProtocolBag_Filterable> {
    public init() {}

    public func count<V: Equatable>(
        matching keyPath: KeyPath<Item.Filter, V>,
        equalTo value: V
    ) -> Int {
        return 0
    }

    public func countMatchingOptional<V: Equatable>(
        matching keyPath: KeyPath<Item.Filter, V?>,
        equalTo value: V?
    ) -> Int {
        return 0
    }
}

// MARK: Concrete witness types
//
// The protocol-rooted singletons resolve through the witness table of these
// concrete types at C# call time. We expose two witnesses per protocol bag so
// tests can confirm the same singleton reads through *different* conformers
// correctly (witness-table dispatch, not pointer equality).

public struct ProtocolBag_BookFilterImpl: ProtocolBag_BookFilter {
    public let title: Swift.String
    public let year: Swift.Int
    public let isFiction: Swift.Bool
    public let rating: Swift.Int?
    public init(title: Swift.String, year: Swift.Int, isFiction: Swift.Bool, rating: Swift.Int?) {
        self.title = title
        self.year = year
        self.isFiction = isFiction
        self.rating = rating
    }
}

public struct ProtocolBag_MovieFilterImpl: ProtocolBag_MovieFilter {
    public let title: Swift.String
    public let runtimeMinutes: Swift.Int
    public init(title: Swift.String, runtimeMinutes: Swift.Int) {
        self.title = title
        self.runtimeMinutes = runtimeMinutes
    }
}

// MARK: Concrete consumers — round-trip a singleton back through Swift
//
// The bound-generic-existential gate was widened to admit `KeyPath<any P, V>`
// directly, so the consumer parameter type is now the natural typed-existential
// shape (`KeyPath<ProtocolBag_BookFilter, V>`) instead of the previous
// `Swift.AnyKeyPath + as!` workaround.
//
// The `filter` parameter still takes the CONCRETE impl type — this sidesteps
// the *independent* existential-direct-parameter gate (lifting `any P` as a
// direct parameter is unrelated to KeyPath admission and remains future work).
// Reading through a protocol-rooted KeyPath requires the receiver to be the
// existential, so `filter as ProtocolBag_BookFilter` upcasts at the call site.

public class ProtocolBag_BookConsumer {
    public class func readTitle(
        from filter: ProtocolBag_BookFilterImpl,
        by kp: KeyPath<ProtocolBag_BookFilter, Swift.String>
    ) -> Swift.String {
        return (filter as ProtocolBag_BookFilter)[keyPath: kp]
    }

    public class func readYear(
        from filter: ProtocolBag_BookFilterImpl,
        by kp: KeyPath<ProtocolBag_BookFilter, Swift.Int>
    ) -> Swift.Int {
        return (filter as ProtocolBag_BookFilter)[keyPath: kp]
    }

    public class func readIsFiction(
        from filter: ProtocolBag_BookFilterImpl,
        by kp: KeyPath<ProtocolBag_BookFilter, Swift.Bool>
    ) -> Swift.Bool {
        return (filter as ProtocolBag_BookFilter)[keyPath: kp]
    }

    public class func readRating(
        from filter: ProtocolBag_BookFilterImpl,
        by kp: KeyPath<ProtocolBag_BookFilter, Swift.Int?>
    ) -> Swift.Int? {
        return (filter as ProtocolBag_BookFilter)[keyPath: kp]
    }

    // Value-equality across two reads of the same singleton — exercises
    // `AnyKeyPath.==` on a protocol-rooted singleton. Takes AnyKeyPath
    // directly; the C# side passes two singleton reads, the Swift body
    // compares via `==` which dispatches to AnyKeyPath's value-equality.
    public class func samePath(
        _ a: Swift.AnyKeyPath,
        _ b: Swift.AnyKeyPath
    ) -> Swift.Bool {
        return a == b
    }
}

public class ProtocolBag_MovieConsumer {
    public class func readTitle(
        from filter: ProtocolBag_MovieFilterImpl,
        by kp: KeyPath<ProtocolBag_MovieFilter, Swift.String>
    ) -> Swift.String {
        return (filter as ProtocolBag_MovieFilter)[keyPath: kp]
    }

    public class func readRuntimeMinutes(
        from filter: ProtocolBag_MovieFilterImpl,
        by kp: KeyPath<ProtocolBag_MovieFilter, Swift.Int>
    ) -> Swift.Int {
        return (filter as ProtocolBag_MovieFilter)[keyPath: kp]
    }
}
