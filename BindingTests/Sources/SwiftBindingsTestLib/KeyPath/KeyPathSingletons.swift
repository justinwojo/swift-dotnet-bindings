// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

// MARK: - Typed KeyPath singleton fixtures (Session 4)
//
// The IN-path counterpart to KeyPathFoundation.swift. Session 4 emits one Swift
// `@_cdecl` trampoline per stored property of a closed conformer's nested bag
// (e.g. `MockBookSession4.LibraryFilter.title`) and surfaces them as C#
// `public static` properties (`MockBookSession4LibraryFilterKeyPaths.Title`),
// initialised lazily by the trampoline.
//
// The closed conformers' nested bags supply the property surface; the parent
// generic type (`BagSession4<Item: Session4_Filterable>`) supplies the consumer-
// demand signal — the emitter walks the parent's API for
// `KeyPath<Item.LibraryFilter, *>` references and only emits singletons for
// conformer.LibraryFilter when demand exists.
//
// Two conformers with same-named bag properties exercise the "two-conformer
// separation" risk (D in the design doc): the typed Root distinguishes them.

// MARK: PAT + closed conformers

public protocol Session4_Filterable {
    associatedtype LibraryFilter
    static var defaultFilter: LibraryFilter { get }
}

public struct MockBookSession4: Session4_Filterable {
    public struct LibraryFilter {
        public var title: String = ""
        public var year: Int = 0
        public var isFiction: Bool = false
        public init() {}
        public init(title: String, year: Int, isFiction: Bool) {
            self.title = title
            self.year = year
            self.isFiction = isFiction
        }
    }
    public init() {}
    public static let defaultFilter = LibraryFilter()
}

public struct MockMovieSession4: Session4_Filterable {
    public struct LibraryFilter {
        public var title: String = ""
        public var runtimeMinutes: Int = 0
        public init() {}
        public init(title: String, runtimeMinutes: Int) {
            self.title = title
            self.runtimeMinutes = runtimeMinutes
        }
    }
    public init() {}
    public static let defaultFilter = LibraryFilter()
}

// MARK: Generic parent — the consumer-demand signal
//
// The parent's API references `KeyPath<Item.LibraryFilter, *>` parameters,
// which is what triggers Session 4's singleton-emission walk. The method
// signature itself is not required to bind through CSM in this session — the
// open-associated-type-rooted KeyPath substitution is out of scope for Session
// 4 (called out as a Phase-3+ follow-up in `00-overview.md`).
//
// The methods are defined so the demand walk has something to recognise. C#
// consumers exercise the IN path through the concrete-rooted readers below
// rather than calling these open-generic methods directly.

public struct BagSession4<Item: Session4_Filterable> {
    public init() {}

    public func count<Value: Equatable>(
        matching keyPath: KeyPath<Item.LibraryFilter, Value>,
        equalTo value: Value
    ) -> Int {
        // Implementation is irrelevant for binding-test purposes — Session 4's
        // demand walk only inspects the signature.
        return 0
    }

    public func optionalCount<Value: Equatable>(
        matching keyPath: KeyPath<Item.LibraryFilter, Value>?,
        equalTo value: Value
    ) -> Int {
        return 0
    }
}

// MARK: Concrete consumers — exercise the IN path without CSM
//
// Session 4 only emits the singletons. The closed-conformer CSM substitution
// that would turn `BagSession4<MockBookSession4>.count<V>(matching:equalTo:)`
// into a directly-callable C# method requires the engine to substitute
// `Item.LibraryFilter` → `MockBookSession4.LibraryFilter` inside the KeyPath
// generic argument — which is the explicit Phase-3+ follow-up.
//
// To prove the singletons round-trip into Swift consumers TODAY, these
// concrete-rooted (non-generic) readers take a typed `KeyPath<R, V>` and read
// through it via `subscript(keyPath:)`. They bind through the existing
// foundation path (Session 3) without needing any closed-conformer CSM work.

public class MockBookSession4Consumer {
    public class func readTitle(
        from filter: MockBookSession4.LibraryFilter,
        by kp: KeyPath<MockBookSession4.LibraryFilter, String>
    ) -> String {
        return filter[keyPath: kp]
    }

    public class func readYear(
        from filter: MockBookSession4.LibraryFilter,
        by kp: KeyPath<MockBookSession4.LibraryFilter, Int>
    ) -> Int {
        return filter[keyPath: kp]
    }

    public class func readIsFiction(
        from filter: MockBookSession4.LibraryFilter,
        by kp: KeyPath<MockBookSession4.LibraryFilter, Bool>
    ) -> Bool {
        return filter[keyPath: kp]
    }

    // Value-equality check via AnyKeyPath.== — exercises Session 3's runtime
    // shim from a Session-4-emitted singleton path.
    public class func samePath(
        _ a: KeyPath<MockBookSession4.LibraryFilter, String>,
        _ b: KeyPath<MockBookSession4.LibraryFilter, String>
    ) -> Bool {
        return a == b
    }
}

public class MockMovieSession4Consumer {
    public class func readTitle(
        from filter: MockMovieSession4.LibraryFilter,
        by kp: KeyPath<MockMovieSession4.LibraryFilter, String>
    ) -> String {
        return filter[keyPath: kp]
    }

    public class func readRuntimeMinutes(
        from filter: MockMovieSession4.LibraryFilter,
        by kp: KeyPath<MockMovieSession4.LibraryFilter, Int>
    ) -> Int {
        return filter[keyPath: kp]
    }
}
