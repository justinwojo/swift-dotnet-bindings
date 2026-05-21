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
}
