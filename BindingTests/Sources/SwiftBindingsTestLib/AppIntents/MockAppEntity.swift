// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

// MARK: - AppIntents promotion smoke fixture (Session 8)
//
// AppIntents was previously suppressed at apple-frameworks.json with
// `unsupported: true`; every member of every AppIntents type was filtered out
// before reaching any emitter. This file is the minimal exercise that the
// promotion to `wrapperImportable: true` actually works end-to-end: a Swift
// type that conforms to `AppEntity`, the protocol that anchors most of the
// AppIntents KeyPath surface (`EntityProperty.init<Entity>(...) where
// Entity : AppEntity`), declared in a fixture module so the binding generator
// sees a downstream closed conformer.
//
// Important architectural note that constrains the v1 scope of Session 8:
// the 240 `WritableKeyPath<Entity, Value>` references in
// AppIntents.swiftinterface are all on `EntityProperty<Value>` initializer
// extensions of the form
//
//   convenience init<Entity>(... getter: KeyPath<Entity, Value>)
//     where Entity : AppIntents.AppEntity
//
// where `Entity` is a method-own generic parameter. Session 4's
// `KeyPathSingletonEmitter` (the only IN-path KeyPath origination machinery
// in the generator) walks closed conformers of a PAT-constrained generic
// *parent's* associated type, not method-own generics constrained by a
// protocol. Without a new emitter shape, the typed-singleton trampoline
// (e.g. `MockBookKeyPaths.Title`) does NOT get emitted from this fixture.
// MockBook below is therefore a *promotion smoke* — it proves the type
// declaration, conformance, and surrounding AppIntents API surface bind
// without crashing the generator. The full per-property KeyPath origination
// from a C#-side AppEntity conformer remains a follow-up. See
// `src/docs/keypath-subsystem/08-appintents-productionization.md`,
// section "Implementation outcomes (shipped)".

#if canImport(AppIntents)
import AppIntents
import Foundation

// MARK: - MockBook : AppEntity

/// A minimal AppEntity conformer. The interesting surface for Session 8 is
/// not the type itself but the EntityProperty / KeyPath / IntentParameter
/// initializer extensions Apple ships in AppIntents.swiftinterface that
/// constrain their method-own generic to AppEntity. MockBook is the closed
/// conformer those extensions can specialize against.
///
/// Three property shapes:
/// - `id: String`  — required by Identifiable; ID conforms to EntityIdentifierConvertible via the existing Foundation/Swift bridge.
/// - `title: String` — String-typed `var`, the most common shape in real AppEntities.
/// - `pageCount: Int` — Int-typed `var`, exercises a primitive value-type slot.
///
/// All `var`, so the synthesized `\MockBook.title` literal compiles to
/// `WritableKeyPath` (Session 3 ABI ground-truth point 3). Once a future
/// emitter walks AppEntity conformers, these are the three trampoline sites.
@available(iOS 16.0, macOS 13.0, tvOS 16.0, watchOS 9.0, *)
public struct MockBook: AppEntity {
    public typealias DefaultQuery = MockBookQuery

    public static var typeDisplayRepresentation: TypeDisplayRepresentation { "Book" }
    public static var defaultQuery: MockBookQuery { MockBookQuery() }

    public var id: String
    public var title: String
    public var pageCount: Int

    // Computed properties exercise the AppEntity-direct-root KeyPath surface beyond
    // stored slots. Swift forms a real `KeyPath` for a get-only computed property and a
    // `WritableKeyPath` for a get/set computed property, so both must surface as
    // `MockBookAppEntityKeyPaths` singletons.

    /// Get-only computed → `\MockBook.summary` is `KeyPath<MockBook, String>`.
    public var summary: String { "\(title) (\(pageCount) pages)" }

    /// Get/set computed → `\MockBook.displayTitle` is `WritableKeyPath<MockBook, String>`.
    /// The setter mutates the backing `title`, so a write through the KeyPath is
    /// observable on `title`.
    public var displayTitle: String {
        get { title }
        set { title = newValue }
    }

    public init(id: String, title: String, pageCount: Int) {
        self.id = id
        self.title = title
        self.pageCount = pageCount
    }

    public var displayRepresentation: DisplayRepresentation {
        DisplayRepresentation(title: "\(title)")
    }
}

// MARK: - MockBookQuery : EntityQuery
//
// EntityQuery is a `Sendable, DynamicOptionsProvider, PersistentlyIdentifiable`
// protocol bag. Apple's default conformance fills in everything except the
// two required methods below; we keep both empty so the fixture has no
// runtime side-effects.

@available(iOS 16.0, macOS 13.0, tvOS 16.0, watchOS 9.0, *)
public struct MockBookQuery: EntityQuery {
    public init() {}

    public func entities(for identifiers: [MockBook.ID]) async throws -> [MockBook] {
        []
    }

    public func suggestedEntities() async throws -> [MockBook] {
        []
    }
}

// MARK: - C# reachability helpers
//
// The promotion smoke needs a way for a C# test to (a) construct a MockBook
// and observe a property, (b) round-trip a MockBook reference through the
// AppIntents-import boundary, without depending on any of the
// EntityProperty.init<Entity>(getter:) overloads that tombstone today.
// These free functions take MockBook (or its inner values) by value /
// String to keep the C# surface to "primitive parameters, primitive return
// types" — exactly the shape that should work even if every KeyPath-taking
// AppIntents API tombstones.

@available(iOS 16.0, macOS 13.0, tvOS 16.0, watchOS 9.0, *)
public func makeMockBook(id: String, title: String, pageCount: Int) -> MockBook {
    MockBook(id: id, title: title, pageCount: pageCount)
}

@available(iOS 16.0, macOS 13.0, tvOS 16.0, watchOS 9.0, *)
public func mockBookTitle(_ book: MockBook) -> String {
    book.title
}

@available(iOS 16.0, macOS 13.0, tvOS 16.0, watchOS 9.0, *)
public func mockBookPageCount(_ book: MockBook) -> Int {
    book.pageCount
}

@available(iOS 16.0, macOS 13.0, tvOS 16.0, watchOS 9.0, *)
public func mockBookId(_ book: MockBook) -> String {
    book.id
}

// MARK: - AppEntity KeyPath singleton round-trip consumers (Session 8b)
//
// Session 8b emits `MockBookAppEntityKeyPaths.{Id,Title,PageCount}` —
// `WritableKeyPath<MockBook, *>` singletons rooted directly on the closed
// AppEntity conformer, originated by Swift `@_cdecl` trampolines. These free
// functions are the consumer side that proves a C#-originated singleton
// marshals back into Swift and reads/writes the correct property.
//
// They take `MockBook` (a value-type AppEntity, surfaced as a SafeHandle-backed
// C# class) plus a typed KeyPath, mirroring `KeyPathConsumer.writeInt` in
// KeyPathFoundation.swift: the writable variants return a mutated copy rather
// than using `inout` because the generated C# inout-write-back for struct
// arguments is a known generator gap, out of scope here. A `WritableKeyPath`
// singleton binds fine to a `KeyPath` parameter (it is-a KeyPath), so the read
// helpers accept the broader `KeyPath` type.

@available(iOS 16.0, macOS 13.0, tvOS 16.0, watchOS 9.0, *)
public func readMockBookString(_ book: MockBook, by kp: KeyPath<MockBook, String>) -> String {
    book[keyPath: kp]
}

@available(iOS 16.0, macOS 13.0, tvOS 16.0, watchOS 9.0, *)
public func readMockBookInt(_ book: MockBook, by kp: KeyPath<MockBook, Int>) -> Int {
    book[keyPath: kp]
}

@available(iOS 16.0, macOS 13.0, tvOS 16.0, watchOS 9.0, *)
public func writeMockBookString(into book: MockBook, by kp: WritableKeyPath<MockBook, String>, to value: String) -> MockBook {
    var copy = book
    copy[keyPath: kp] = value
    return copy
}

@available(iOS 16.0, macOS 13.0, tvOS 16.0, watchOS 9.0, *)
public func writeMockBookInt(into book: MockBook, by kp: WritableKeyPath<MockBook, Int>, to value: Int) -> MockBook {
    var copy = book
    copy[keyPath: kp] = value
    return copy
}

// AnyKeyPath.==-driven value equality, called on the Swift side from two
// C#-originated singleton handles — proves the IN-path handle is a real,
// comparable KeyPath rather than an opaque pointer.
@available(iOS 16.0, macOS 13.0, tvOS 16.0, watchOS 9.0, *)
public func sameMockBookPath(_ a: KeyPath<MockBook, String>, _ b: KeyPath<MockBook, String>) -> Bool {
    a == b
}

// Type-erased AnyKeyPath equality. The consumer-side EntityProperty factory
// (Session 8b.3) captures the C#-originated KeyPath singleton inside the
// dependency's `MiniEntityProperty.capturedKeyPath` (an `AnyKeyPath`). Reading
// that property back across the dependency boundary and comparing it here to
// the original singleton proves the factory threaded the *exact same* KeyPath
// into `init<Entity>(getter:)` — not merely a non-null pointer.
@available(iOS 16.0, macOS 13.0, tvOS 16.0, watchOS 9.0, *)
public func sameAnyKeyPath(_ a: AnyKeyPath, _ b: AnyKeyPath) -> Bool {
    a == b
}

#endif // canImport(AppIntents)
