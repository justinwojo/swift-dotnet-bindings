// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.KeyPath;

/// <summary>
/// End-to-end IN-path tests for the Session 4 typed KeyPath singletons. Where
/// <see cref="KeyPathFoundationTests"/> exercises the OUT path (Swift returns a
/// KeyPath, C# adopts), this class exercises the IN path (C# originates a KeyPath
/// from a generator-emitted Swift trampoline, passes it to a Swift consumer).
///
/// <para>What this exercises:</para>
/// <list type="bullet">
///   <item>Container existence — generated <c>MockBookSession4LibraryFilterKeyPaths</c>
///   surface compiles and resolves singletons for every emittable property.</item>
///   <item>Lazy initialisation — first access calls the trampoline; subsequent
///   accesses return the cached singleton (Lazy&lt;T&gt;.Value contract).</item>
///   <item>Writable flavor selection — <c>var</c> properties surface as
///   <c>WritableKeyPath&lt;TRoot, TValue&gt;</c>; <c>let</c> properties (none in this
///   fixture; the design doc's <c>defaultFilter</c> is a static, not stored) would
///   surface as <c>KeyPath</c>.</item>
///   <item>Two-conformer separation — same property name on two distinct
///   conformers (<c>MockBookSession4.title</c> vs <c>MockMovieSession4.title</c>)
///   surfaces as two distinct singletons with different static <c>TRoot</c>.</item>
///   <item>Value-equality across uses — the Swift compiler interns KeyPath
///   instances per descriptor, but our contract is value-equality via
///   <c>AnyKeyPath.==</c>; both first- and second-access singletons compare
///   equal.</item>
///   <item>Round-trip through a Swift consumer — calling
///   <c>MockBookSession4Consumer.ReadTitle(filter, kp)</c> with the singleton
///   reads the correct value from the bag, proving the IN-path marshalling
///   contract is sound.</item>
///   <item>Optional &amp; primitive Value composition — <c>Year</c> (<c>Int</c>) and
///   <c>IsFiction</c> (<c>Bool</c>) singletons resolve and read through cleanly.</item>
/// </list>
///
/// <para>Pointer identity is intentionally NOT asserted; the Session 3 foundation
/// contract is value-equality only (cross-module compilation can produce
/// distinct AnyKeyPath instances for the same descriptor).</para>
/// </summary>
public class KeyPathSingletonTests : TestBase
{
    public KeyPathSingletonTests(TestResults results) : base(results) { }

    // ---------------------------------------------------------------------------------------
    // Container existence
    // ---------------------------------------------------------------------------------------

    public void TestMockBookContainer_TitleSingleton_NonNull()
    {
        var kp = MockBookSession4LibraryFilterKeyPaths.Title;
        AssertNotNull(kp, "Title singleton resolves to non-null KeyPath");
        AssertFalse(kp.IsInvalid, "Title singleton handle is valid");
    }

    public void TestMockBookContainer_YearSingleton_NonNull()
    {
        var kp = MockBookSession4LibraryFilterKeyPaths.Year;
        AssertNotNull(kp, "Year singleton resolves to non-null KeyPath");
        AssertFalse(kp.IsInvalid, "Year singleton handle is valid");
    }

    public void TestMockBookContainer_IsFictionSingleton_NonNull()
    {
        var kp = MockBookSession4LibraryFilterKeyPaths.IsFiction;
        AssertNotNull(kp, "IsFiction singleton resolves to non-null KeyPath");
        AssertFalse(kp.IsInvalid, "IsFiction singleton handle is valid");
    }

    public void TestMockMovieContainer_RuntimeMinutesSingleton_NonNull()
    {
        var kp = MockMovieSession4LibraryFilterKeyPaths.RuntimeMinutes;
        AssertNotNull(kp, "RuntimeMinutes singleton resolves to non-null KeyPath");
        AssertFalse(kp.IsInvalid, "RuntimeMinutes singleton handle is valid");
    }

    // ---------------------------------------------------------------------------------------
    // Writable flavor: var properties surface as WritableKeyPath
    // ---------------------------------------------------------------------------------------

    public void TestTitleSingleton_IsWritableKeyPath()
    {
        var kp = MockBookSession4LibraryFilterKeyPaths.Title;
        AssertTrue(
            kp is global::Swift.WritableKeyPath<MockBookSession4.LibraryFilter, string>,
            "var property surfaces as WritableKeyPath");
        AssertTrue(
            kp is global::Swift.KeyPath<MockBookSession4.LibraryFilter, string>,
            "WritableKeyPath is-a KeyPath (inheritance chain)");
        AssertTrue(
            kp is global::Swift.PartialKeyPath<MockBookSession4.LibraryFilter>,
            "WritableKeyPath is-a PartialKeyPath");
        AssertTrue(kp is global::Swift.AnyKeyPath, "And is-a AnyKeyPath");
    }

    // ---------------------------------------------------------------------------------------
    // Lazy initialisation + caching
    // ---------------------------------------------------------------------------------------

    public void TestSingleton_RepeatedAccess_ReturnsSameInstance()
    {
        // Lazy<T>.Value contract: same instance returned on every access. This is
        // not the value-equality test (which compares AnyKeyPath.==); it's the
        // C#-side caching contract. Pointer identity is allowed and expected here
        // because the singleton lives as a static field.
        var a = MockBookSession4LibraryFilterKeyPaths.Title;
        var b = MockBookSession4LibraryFilterKeyPaths.Title;
        AssertTrue(ReferenceEquals(a, b),
            "Lazy<T>-backed singleton returns the same reference on repeated access");
    }

    public void TestSingleton_ValueEqualsItself()
    {
        // AnyKeyPath.==-driven value equality — the contract Session 3 enforces.
        var kp = MockBookSession4LibraryFilterKeyPaths.Title;
        AssertTrue(kp.Equals(kp), "Singleton value-equals itself");
        AssertEqual(kp.GetHashCode(), kp.GetHashCode(),
            "Hash is stable across repeated calls");
    }

    // ---------------------------------------------------------------------------------------
    // Two-conformer separation: same Swift property name, different Roots
    // ---------------------------------------------------------------------------------------

    public void TestMockBookTitle_AndMockMovieTitle_AreSeparate()
    {
        var bookTitle = MockBookSession4LibraryFilterKeyPaths.Title;
        var movieTitle = MockMovieSession4LibraryFilterKeyPaths.Title;

        // Static type: same Value (Swift String → C# `string` via idiomatic
        // projection) but different Root, so they're not assignable to each other.
        AssertTrue(
            bookTitle is global::Swift.KeyPath<MockBookSession4.LibraryFilter, string>,
            "Book.Title Root is MockBookSession4.LibraryFilter");
        AssertTrue(
            movieTitle is global::Swift.KeyPath<MockMovieSession4.LibraryFilter, string>,
            "Movie.Title Root is MockMovieSession4.LibraryFilter");

        // Dynamic check: as AnyKeyPath, both share the runtime base type. The
        // KeyPath equality dispatches to AnyKeyPath.==, which compares path
        // content; different Roots produce different paths.
        global::Swift.AnyKeyPath bookErased = bookTitle;
        global::Swift.AnyKeyPath movieErased = movieTitle;
        AssertFalse(bookErased.Equals(movieErased),
            "Different conformers' same-named KeyPaths are value-distinct");
    }

    // ---------------------------------------------------------------------------------------
    // Round-trip through a Swift consumer (IN path proves end-to-end marshalling)
    // ---------------------------------------------------------------------------------------

    public void TestSingleton_ReadTitle_RoundTripsThroughSwiftConsumer()
    {
        using var filter = new MockBookSession4.LibraryFilter("Pride and Prejudice", 1813, true);
        var kp = MockBookSession4LibraryFilterKeyPaths.Title;
        // Idiomatic-string projection: the consumer's Swift `String` return surfaces
        // as a plain C# `string` here, owned by the runtime — no `using` needed.
        var read = MockBookSession4Consumer.ReadTitle(filter, kp);
        AssertEqual("Pride and Prejudice", read,
            "Swift consumer reads through C#-originated singleton correctly");
    }

    public void TestSingleton_ReadYear_RoundTripsThroughSwiftConsumer()
    {
        using var filter = new MockBookSession4.LibraryFilter("Title", 1949, false);
        var kp = MockBookSession4LibraryFilterKeyPaths.Year;
        var read = MockBookSession4Consumer.ReadYear(filter, kp);
        AssertEqual<nint>(1949, read, "Swift Int property reads through singleton");
    }

    public void TestSingleton_ReadIsFiction_RoundTripsThroughSwiftConsumer()
    {
        using var filter = new MockBookSession4.LibraryFilter("Title", 1937, true);
        var kp = MockBookSession4LibraryFilterKeyPaths.IsFiction;
        var read = MockBookSession4Consumer.ReadIsFiction(filter, kp);
        AssertTrue(read, "Swift Bool property reads through singleton");
    }

    public void TestMovieSingleton_ReadTitle_RoundTripsThroughSwiftConsumer()
    {
        using var filter = new MockMovieSession4.LibraryFilter("Casablanca", 102);
        var kp = MockMovieSession4LibraryFilterKeyPaths.Title;
        var read = MockMovieSession4Consumer.ReadTitle(filter, kp);
        AssertEqual("Casablanca", read,
            "Second conformer's singleton reads through its own consumer correctly");
    }

    public void TestMovieSingleton_ReadRuntimeMinutes_RoundTripsThroughSwiftConsumer()
    {
        using var filter = new MockMovieSession4.LibraryFilter("Casablanca", 102);
        var kp = MockMovieSession4LibraryFilterKeyPaths.RuntimeMinutes;
        var read = MockMovieSession4Consumer.ReadRuntimeMinutes(filter, kp);
        AssertEqual<nint>(102, read, "Movie Int property reads through singleton");
    }

    // ---------------------------------------------------------------------------------------
    // Value-equality through a Swift consumer
    // ---------------------------------------------------------------------------------------

    public void TestSingleton_SwiftSidedEquality_OnSamePath()
    {
        // SamePath calls AnyKeyPath.== on the Swift side from two C#-originated
        // singleton handles (in this case, the same singleton). Identity-equal
        // handles must also produce true under value equality.
        var kp = MockBookSession4LibraryFilterKeyPaths.Title;
        AssertTrue(MockBookSession4Consumer.SamePath(kp, kp),
            "AnyKeyPath.== on identical singleton returns true");
    }

    // ---------------------------------------------------------------------------------------
    // Concurrent first-access — Lazy<T> default mode is ExecutionAndPublication
    // ---------------------------------------------------------------------------------------

    public void TestSingleton_ConcurrentFirstAccess_NoCrash()
    {
        // Risk C from the design doc: multiple threads racing the first access.
        // Lazy<T> with default ExecutionAndPublication mode serialises construction,
        // so all threads should observe the same singleton instance.
        const int threadCount = 8;
        var barrier = new System.Threading.Barrier(threadCount);
        var results = new global::Swift.WritableKeyPath<MockBookSession4.LibraryFilter, string>?[threadCount];
        var threads = new System.Threading.Thread[threadCount];
        for (int i = 0; i < threadCount; i++)
        {
            int idx = i;
            threads[idx] = new System.Threading.Thread(() =>
            {
                barrier.SignalAndWait();
                results[idx] = MockBookSession4LibraryFilterKeyPaths.Title;
            });
            threads[idx].Start();
        }
        foreach (var t in threads) t.Join();

        for (int i = 0; i < threadCount; i++)
            AssertNotNull(results[i], $"Thread {i}: singleton resolved non-null");
        for (int i = 1; i < threadCount; i++)
            AssertTrue(ReferenceEquals(results[0], results[i]),
                $"Thread {i}: same singleton instance as thread 0 (Lazy<T> publishing)");
    }
}
