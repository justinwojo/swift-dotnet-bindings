// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using Swift.Runtime;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.AppIntents;

/// <summary>
/// Session 8 promotion smoke for the AppIntents framework. Exercises the
/// MockBook AppEntity fixture (BindingTests/Sources/SwiftBindingsTestLib/
/// AppIntents/MockAppEntity.swift) end-to-end across the @_cdecl boundary.
///
/// What this validates: AppIntents flipped from <c>unsupported: true</c> to
/// <c>wrapperImportable: true</c> in <c>apple-frameworks.json</c> actually
/// resolves at generator-time + runtime against the real Apple swiftinterface.
/// MockBook is a value-type AppEntity conformer; we round-trip its three
/// primitive-typed properties (id, title, pageCount) and the static
/// <c>DefaultQuery</c> entry point. The AppIntents-typed properties
/// <c>typeDisplayRepresentation</c> and <c>displayRepresentation</c>
/// intentionally tombstone (no TypeDatabase registration for
/// AppIntents.TypeDisplayRepresentation / DisplayRepresentation yet) — this
/// test must NOT touch them.
///
/// What this does NOT validate: per-property KeyPath singletons for
/// AppEntity conformers (<c>EntityProperty.init&lt;Entity&gt;(getter:)</c>
/// shape). Session 4's KeyPathSingletonEmitter walks closed conformers of a
/// PAT-constrained generic parent's associated-type bag; AppEntity-rooted
/// KeyPaths are method-own generics on initializer extensions, which is a
/// different emitter shape and follow-up work.
/// </summary>
[global::System.Runtime.Versioning.SupportedOSPlatform("ios16.0")]
[global::System.Runtime.Versioning.SupportedOSPlatform("maccatalyst16.0")]
[global::System.Runtime.Versioning.SupportedOSPlatform("macos13.0")]
[global::System.Runtime.Versioning.SupportedOSPlatform("tvos16.0")]
public class MockAppEntityTests : TestBase
{
    public MockAppEntityTests(TestResults results) : base(results) { }

    public void TestMakeMockBook_FreeFunction_RoundTripsAllFields()
    {
        using var book = TestLibFunctions.MakeMockBook("book-001", "Effective Swift", 320);

        AssertEqual("book-001", TestLibFunctions.MockBookId(book), "id round-trips through free function");
        AssertEqual("Effective Swift", TestLibFunctions.MockBookTitle(book), "title round-trips through free function");
        AssertEqual((nint)320, TestLibFunctions.MockBookPageCount(book), "pageCount round-trips through free function");
    }

    public void TestMockBook_Constructor_ExposesProperties()
    {
        using var book = new MockBook("isbn-9780131103627", "The C Programming Language", 272);

        AssertEqual("isbn-9780131103627", book.Id, "constructor-set id is readable via property");
        AssertEqual("The C Programming Language", book.Title, "constructor-set title is readable via property");
        AssertEqual(272, book.PageCount, "constructor-set pageCount is readable via property");
    }

    public void TestMockBook_PropertySetters_RoundTrip()
    {
        using var book = new MockBook("initial-id", "Initial Title", 0);

        book.Id = "mutated-id";
        book.Title = "Mutated Title";
        book.PageCount = 512;

        AssertEqual("mutated-id", book.Id, "Id setter persists through getter");
        AssertEqual("Mutated Title", book.Title, "Title setter persists through getter");
        AssertEqual(512, book.PageCount, "PageCount setter persists through getter");
    }

    public void TestMockBook_FreeFunctionAndPropertyAccessor_AgreeOnValues()
    {
        using var book = new MockBook("cross-check", "Cross Check Title", 128);

        // The free function path goes Swift -> @_cdecl thunk -> C# whereas the
        // property accessor path goes Swift -> property getter -> C#. Both
        // should see identical values for the same instance.
        AssertEqual(TestLibFunctions.MockBookId(book), book.Id, "free function id matches property id");
        AssertEqual(TestLibFunctions.MockBookTitle(book), book.Title, "free function title matches property title");
        AssertEqual(TestLibFunctions.MockBookPageCount(book), (nint)book.PageCount, "free function pageCount matches property pageCount");
    }

    public void TestMockBook_DefaultQuery_StaticAccessor()
    {
        // Static `defaultQuery` is the AppEntity protocol requirement that
        // matters most for downstream AppIntents APIs (App Shortcuts query
        // by entity type). The accessor must return a non-null value-type
        // wrapper without crashing the @_cdecl boundary.
        using var query = MockBook.DefaultQuery;

        AssertNotNull(query, "MockBook.DefaultQuery returns a non-null MockBookQuery");
    }

    public void TestMockBook_MultipleInstances_Independent()
    {
        // Each MockBook owns its own SwiftSafeHandle; mutating one must not
        // affect the other. Catches accidental aliasing of the Swift value
        // representation across instances.
        using var a = new MockBook("id-a", "Title A", 10);
        using var b = new MockBook("id-b", "Title B", 20);

        a.PageCount = 999;

        AssertEqual(999, a.PageCount, "Mutation visible on instance a");
        AssertEqual(20, b.PageCount, "Instance b unaffected by mutation on a");
        AssertEqual("id-a", a.Id, "Id on a unchanged");
        AssertEqual("id-b", b.Id, "Id on b unchanged");
    }

    // ---------------------------------------------------------------------------------------
    // Session 8b: AppEntity KeyPath singletons
    //
    // MockBookAppEntityKeyPaths.{Id,Title,PageCount} are WritableKeyPath
    // singletons rooted DIRECTLY on the closed AppEntity conformer (not on a
    // nested bag, as in Session 4). They are originated by Swift @_cdecl
    // trampolines and surface as C# `public static` properties. These tests
    // assert the container resolves, the singletons carry the right
    // Root/Value/flavor, and they round-trip through Swift consumers.
    // ---------------------------------------------------------------------------------------

    public void TestAppEntityKeyPaths_IdSingleton_NonNull()
    {
        var kp = MockBookAppEntityKeyPaths.Id;
        AssertNotNull(kp, "Id singleton resolves to non-null KeyPath");
        AssertFalse(kp.IsInvalid, "Id singleton handle is valid");
    }

    public void TestAppEntityKeyPaths_TitleSingleton_NonNull()
    {
        var kp = MockBookAppEntityKeyPaths.Title;
        AssertNotNull(kp, "Title singleton resolves to non-null KeyPath");
        AssertFalse(kp.IsInvalid, "Title singleton handle is valid");
    }

    public void TestAppEntityKeyPaths_PageCountSingleton_NonNull()
    {
        var kp = MockBookAppEntityKeyPaths.PageCount;
        AssertNotNull(kp, "PageCount singleton resolves to non-null KeyPath");
        AssertFalse(kp.IsInvalid, "PageCount singleton handle is valid");
    }

    public void TestAppEntityKeyPaths_RootedOnConformerItself()
    {
        // Unlike Session 4 (rooted on a nested LibraryFilter bag), the AppEntity
        // singletons are rooted on the conformer type directly. The static type
        // is WritableKeyPath<MockBook, *> because all three properties are `var`.
        var title = MockBookAppEntityKeyPaths.Title;
        AssertTrue(
            title is global::Swift.WritableKeyPath<MockBook, string>,
            "Title is WritableKeyPath<MockBook, string> (var property, conformer root)");
        AssertTrue(
            title is global::Swift.KeyPath<MockBook, string>,
            "WritableKeyPath is-a KeyPath");
        AssertTrue(
            title is global::Swift.AnyKeyPath,
            "And is-a AnyKeyPath");

        var pageCount = MockBookAppEntityKeyPaths.PageCount;
        AssertTrue(
            pageCount is global::Swift.WritableKeyPath<MockBook, nint>,
            "PageCount is WritableKeyPath<MockBook, nint> (Int value type)");
    }

    public void TestAppEntityKeyPaths_RepeatedAccess_SameInstance()
    {
        // Lazy<T>.Value contract: the static singleton returns the same reference.
        var a = MockBookAppEntityKeyPaths.Title;
        var b = MockBookAppEntityKeyPaths.Title;
        AssertTrue(ReferenceEquals(a, b),
            "Lazy<T>-backed AppEntity singleton returns same reference on repeated access");
    }

    public void TestAppEntityKeyPaths_ReadId_RoundTripsThroughSwiftConsumer()
    {
        using var book = new MockBook("isbn-42", "The Title", 100);
        var kp = MockBookAppEntityKeyPaths.Id;
        var read = TestLibFunctions.ReadMockBookString(book, kp);
        AssertEqual("isbn-42", read,
            "Swift consumer reads id through C#-originated AppEntity singleton");
    }

    public void TestAppEntityKeyPaths_ReadTitle_RoundTripsThroughSwiftConsumer()
    {
        using var book = new MockBook("id-1", "Gravity's Rainbow", 760);
        var kp = MockBookAppEntityKeyPaths.Title;
        var read = TestLibFunctions.ReadMockBookString(book, kp);
        AssertEqual("Gravity's Rainbow", read,
            "Swift consumer reads title through C#-originated AppEntity singleton");
    }

    public void TestAppEntityKeyPaths_ReadPageCount_RoundTripsThroughSwiftConsumer()
    {
        using var book = new MockBook("id-2", "Some Book", 432);
        var kp = MockBookAppEntityKeyPaths.PageCount;
        var read = TestLibFunctions.ReadMockBookInt(book, kp);
        AssertEqual<nint>(432, read,
            "Swift consumer reads pageCount (Int) through C#-originated AppEntity singleton");
    }

    public void TestAppEntityKeyPaths_WriteTitle_RoundTripsThroughSwiftConsumer()
    {
        // WritableKeyPath flavor: assign through the KP subscript on the Swift
        // side. The consumer returns a mutated copy (inout-write-back for struct
        // args is a known generator gap), so we read the returned book back.
        using var book = new MockBook("id-3", "Old Title", 50);
        var kp = MockBookAppEntityKeyPaths.Title;
        using var mutated = TestLibFunctions.WriteMockBookString(book, kp, "New Title");
        AssertEqual("New Title", mutated.Title,
            "WritableKeyPath singleton assigns title through Swift KP subscript");
        AssertEqual("Old Title", book.Title,
            "Original book is unchanged (mutated copy returned)");
    }

    public void TestAppEntityKeyPaths_WritePageCount_RoundTripsThroughSwiftConsumer()
    {
        using var book = new MockBook("id-4", "Title", 50);
        var kp = MockBookAppEntityKeyPaths.PageCount;
        using var mutated = TestLibFunctions.WriteMockBookInt(book, kp, 777);
        AssertEqual(777, mutated.PageCount,
            "WritableKeyPath singleton assigns pageCount through Swift KP subscript");
        AssertEqual(50, book.PageCount,
            "Original book pageCount is unchanged (mutated copy returned)");
    }

    public void TestAppEntityKeyPaths_SwiftSidedEquality_OnSamePath()
    {
        // AnyKeyPath.== on the Swift side from two C#-originated singleton handles
        // (the same singleton here). Proves the IN-path handle is a real,
        // comparable KeyPath, not an opaque pointer.
        var kp = MockBookAppEntityKeyPaths.Id;
        AssertTrue(TestLibFunctions.SameMockBookPath(kp, kp),
            "AnyKeyPath.== on identical AppEntity singleton returns true");
    }

    // ---------------------------------------------------------------------------------------
    // Computed properties: a concrete AppEntity root forms valid KeyPaths for computed
    // properties too (`\Root.getOnly` → KeyPath, `\Root.getSet` → WritableKeyPath), not
    // just stored slots. MockBook.summary (get-only) and MockBook.displayTitle (get/set)
    // exercise the allowComputed gate.
    // ---------------------------------------------------------------------------------------

    public void TestAppEntityKeyPaths_ComputedGetOnly_IsReadOnlyKeyPath()
    {
        // summary has no setter → `\MockBook.summary` is a (read-only) KeyPath, NOT a
        // WritableKeyPath. The static type of the singleton must reflect that.
        var kp = MockBookAppEntityKeyPaths.Summary;
        AssertNotNull(kp, "Summary computed-property singleton resolves");
        AssertFalse(kp.IsInvalid, "Summary singleton handle is valid");
        AssertTrue(
            kp is global::Swift.KeyPath<MockBook, string>,
            "get-only computed property surfaces as KeyPath<MockBook, string>");
        AssertFalse(
            kp is global::Swift.WritableKeyPath<MockBook, string>,
            "get-only computed property is NOT a WritableKeyPath (no setter)");
    }

    public void TestAppEntityKeyPaths_ReadComputedSummary_RoundTripsThroughSwiftConsumer()
    {
        using var book = new MockBook("id-5", "The Hobbit", 310);
        var kp = MockBookAppEntityKeyPaths.Summary;
        var read = TestLibFunctions.ReadMockBookString(book, kp);
        AssertEqual("The Hobbit (310 pages)", read,
            "Swift consumer reads the computed summary through a C#-originated KeyPath singleton");
    }

    public void TestAppEntityKeyPaths_ComputedGetSet_IsWritableKeyPath()
    {
        // displayTitle has a setter → `\MockBook.displayTitle` is a WritableKeyPath.
        var kp = MockBookAppEntityKeyPaths.DisplayTitle;
        AssertNotNull(kp, "DisplayTitle computed-property singleton resolves");
        AssertTrue(
            kp is global::Swift.WritableKeyPath<MockBook, string>,
            "get/set computed property surfaces as WritableKeyPath<MockBook, string>");
    }

    public void TestAppEntityKeyPaths_WriteComputedDisplayTitle_MutatesBackingTitle()
    {
        // Writing through the computed get/set KeyPath invokes the Swift setter
        // (`set { title = newValue }`), which mutates the backing stored `title`.
        using var book = new MockBook("id-6", "Original", 42);
        var kp = MockBookAppEntityKeyPaths.DisplayTitle;
        using var mutated = TestLibFunctions.WriteMockBookString(book, kp, "Reassigned");
        AssertEqual("Reassigned", mutated.Title,
            "Writing computed displayTitle through the WritableKeyPath mutates the backing title");
        AssertEqual("Original", book.Title,
            "Original book unchanged (mutated copy returned)");
    }
}
