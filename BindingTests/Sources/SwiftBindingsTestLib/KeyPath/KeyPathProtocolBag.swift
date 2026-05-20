// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

// MARK: - Protocol-bag KeyPath singleton fixture (Session 4 protocol-bag extension)
//
// The original Session 4 fixture (`KeyPathSingletons.swift`) covers the
// nested-concrete-struct shape: `MockBookSession4.LibraryFilter` is a stored-
// property struct directly nested inside the conformer.
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
// resolves through the conforming type's witness table at use time. The
// Session 4 broadening admits this shape via two mechanisms:
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
// The `typealias` indirection is the trigger for Session 4's broadening —
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
// Session 4 demand walking inspects signatures only.

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
// The natural shape — `KeyPath<ProtocolBag_BookFilter, V>` as the parameter
// type — is rejected by the bound-generic-existential gate
// (`KeyPath<any P, V>` is not in the container allowlist). Lifting that gate
// requires its own pass; until then, consumers take the singleton typed as
// `Swift.AnyKeyPath` (a concrete class, no nested existential) and cast back
// to the typed `KeyPath<P, V>` inside the body. The cast is total — the C#
// caller always passes a singleton whose ABI matches the static spelling.
//
// The `filter` parameter takes the CONCRETE impl type to sidestep the
// independent existential-direct-parameter gate; reading through a
// protocol-rooted KeyPath requires the receiver to be the existential, so we
// upcast `filter as ProtocolBag_BookFilter` at the call site. The witness-
// table dispatch is the same as `(filter as ProtocolBag_BookFilter)[keyPath: kp]`.

public class ProtocolBag_BookConsumer {
    public class func readTitle(
        from filter: ProtocolBag_BookFilterImpl,
        by kp: Swift.AnyKeyPath
    ) -> Swift.String {
        let typed = kp as! KeyPath<ProtocolBag_BookFilter, Swift.String>
        return (filter as ProtocolBag_BookFilter)[keyPath: typed]
    }

    public class func readYear(
        from filter: ProtocolBag_BookFilterImpl,
        by kp: Swift.AnyKeyPath
    ) -> Swift.Int {
        let typed = kp as! KeyPath<ProtocolBag_BookFilter, Swift.Int>
        return (filter as ProtocolBag_BookFilter)[keyPath: typed]
    }

    public class func readIsFiction(
        from filter: ProtocolBag_BookFilterImpl,
        by kp: Swift.AnyKeyPath
    ) -> Swift.Bool {
        let typed = kp as! KeyPath<ProtocolBag_BookFilter, Swift.Bool>
        return (filter as ProtocolBag_BookFilter)[keyPath: typed]
    }

    public class func readRating(
        from filter: ProtocolBag_BookFilterImpl,
        by kp: Swift.AnyKeyPath
    ) -> Swift.Int? {
        let typed = kp as! KeyPath<ProtocolBag_BookFilter, Swift.Int?>
        return (filter as ProtocolBag_BookFilter)[keyPath: typed]
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
        by kp: Swift.AnyKeyPath
    ) -> Swift.String {
        let typed = kp as! KeyPath<ProtocolBag_MovieFilter, Swift.String>
        return (filter as ProtocolBag_MovieFilter)[keyPath: typed]
    }

    public class func readRuntimeMinutes(
        from filter: ProtocolBag_MovieFilterImpl,
        by kp: Swift.AnyKeyPath
    ) -> Swift.Int {
        let typed = kp as! KeyPath<ProtocolBag_MovieFilter, Swift.Int>
        return (filter as ProtocolBag_MovieFilter)[keyPath: typed]
    }
}
