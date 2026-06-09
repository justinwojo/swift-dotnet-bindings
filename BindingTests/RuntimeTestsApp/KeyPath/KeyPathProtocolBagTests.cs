// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.KeyPath;

/// <summary>
/// Protocol-bag variant of <see cref="KeyPathSingletonTests"/>. Where the original
/// fixture pins a <c>typealias</c> to a NESTED concrete struct
/// (<c>MockBookSession4.LibraryFilter</c>), this fixture pins to a MODULE-SCOPE
/// PROTOCOL — the shape MusicKit exposes through
/// <c>typealias LibraryFilter = MusicKit.LibraryAlbumFilter</c>.
///
/// <para>The emitter broadening for this shape lives in two places:</para>
/// <list type="number">
///   <item><see cref="KeyPathSingletonEmitter.FindBagDecl"/> branches 3 &amp; 4 resolve
///   typealiases that target module-scope types in addition to nested types.</item>
///   <item><c>IsEmittableBag</c> + <c>WhyPropertyNotEmittable</c> accept
///   <c>ProtocolDecl</c> bags whose property requirements have no storage
///   (<c>allowAbstract = true</c>) — Swift's <c>\Protocol.requirement</c> KeyPath
///   literal compiles and resolves through the conformer's witness table at
///   use time.</item>
/// </list>
///
/// <para>These tests cover end-to-end:</para>
/// <list type="bullet">
///   <item>Protocol-rooted singleton emission — <c>Title</c> resolves to
///   <c>KeyPath&lt;IProtocolBag_BookFilter, string&gt;</c>, not a generic stub.</item>
///   <item>Round-trip through a Swift consumer — the C# singleton's
///   <c>AnyKeyPath</c> handle is cast back to typed <c>KeyPath&lt;P, V&gt;</c> on
///   the Swift side and dispatched against a concrete witness instance via
///   <c>(filter as P)[keyPath: kp]</c>.</item>
///   <item>Two-conformer separation — <c>ProtocolBag_Book.Filter</c> resolves to
///   <c>ProtocolBag_BookFilter</c>; <c>ProtocolBag_Movie.Filter</c> resolves to
///   <c>ProtocolBag_MovieFilter</c>. Same property name (<c>title</c>) surfaces
///   as two distinct singletons.</item>
///   <item>Value-equality across uses — <c>AnyKeyPath.==</c> on the same
///   protocol-rooted singleton returns true through the Swift-side dispatch.</item>
///   <item>Optional value type composition — <c>Rating</c> projects to
///   <c>KeyPath&lt;IProtocolBag_BookFilter, nint?&gt;</c> and reads through cleanly,
///   exercising the Optional-projection path on a protocol-rooted bag.</item>
/// </list>
///
/// <para>The consumer's <c>filter</c> parameter takes the CONCRETE impl type
/// (<c>ProtocolBag_BookFilterImpl</c>, not the protocol existential
/// <c>ProtocolBag_BookFilter</c>). The Swift body upcasts to the protocol before
/// the KeyPath read so the witness-table dispatch fires. This is the
/// workaround for the <c>any P</c>-as-direct-parameter gate; broadening that
/// gate (or admitting <c>KeyPath&lt;any P, V&gt;</c> into the bound-generic
/// container allowlist) is a separate follow-up captured in the roadmap.</para>
/// </summary>
public class KeyPathProtocolBagTests : TestBase
{
    public KeyPathProtocolBagTests(TestResults results) : base(results) { }

    // ---------------------------------------------------------------------------------------
    // Singleton existence + static type shape
    // ---------------------------------------------------------------------------------------

    public void TestBookContainer_TitleSingleton_NonNull()
    {
        var kp = ProtocolBag_BookProtocolBag_BookFilterKeyPaths.Title;
        AssertNotNull(kp, "Title singleton resolves to non-null KeyPath");
        AssertFalse(kp.IsInvalid, "Title singleton handle is valid");
    }

    public void TestBookContainer_YearSingleton_NonNull()
    {
        var kp = ProtocolBag_BookProtocolBag_BookFilterKeyPaths.Year;
        AssertNotNull(kp, "Year singleton resolves to non-null KeyPath");
        AssertFalse(kp.IsInvalid, "Year singleton handle is valid");
    }

    public void TestBookContainer_IsFictionSingleton_NonNull()
    {
        var kp = ProtocolBag_BookProtocolBag_BookFilterKeyPaths.IsFiction;
        AssertNotNull(kp, "IsFiction singleton resolves to non-null KeyPath");
        AssertFalse(kp.IsInvalid, "IsFiction singleton handle is valid");
    }

    public void TestBookContainer_RatingSingleton_NonNull()
    {
        var kp = ProtocolBag_BookProtocolBag_BookFilterKeyPaths.Rating;
        AssertNotNull(kp, "Rating (Optional<Int>) singleton resolves to non-null KeyPath");
        AssertFalse(kp.IsInvalid, "Rating singleton handle is valid");
    }

    public void TestMovieContainer_TitleSingleton_NonNull()
    {
        var kp = ProtocolBag_MovieProtocolBag_MovieFilterKeyPaths.Title;
        AssertNotNull(kp, "Movie Title singleton resolves to non-null KeyPath");
        AssertFalse(kp.IsInvalid, "Movie Title singleton handle is valid");
    }

    public void TestMovieContainer_RuntimeMinutesSingleton_NonNull()
    {
        var kp = ProtocolBag_MovieProtocolBag_MovieFilterKeyPaths.RuntimeMinutes;
        AssertNotNull(kp, "RuntimeMinutes singleton resolves to non-null KeyPath");
        AssertFalse(kp.IsInvalid, "RuntimeMinutes singleton handle is valid");
    }

    // ---------------------------------------------------------------------------------------
    // Read-only flavor: protocol property requirements with `{ get }` surface as KeyPath
    // (not WritableKeyPath — protocol requirement has no setter requirement to refer to).
    // ---------------------------------------------------------------------------------------

    public void TestTitleSingleton_IsReadOnlyKeyPath()
    {
        var kp = ProtocolBag_BookProtocolBag_BookFilterKeyPaths.Title;
        AssertTrue(
            kp is global::Swift.KeyPath<IProtocolBag_BookFilter, string>,
            "title { get } surfaces as KeyPath<IProtocolBag_BookFilter, string>");
        AssertTrue(
            kp is global::Swift.PartialKeyPath<IProtocolBag_BookFilter>,
            "KeyPath is-a PartialKeyPath (hierarchy)");
        AssertTrue(kp is global::Swift.AnyKeyPath, "And is-a AnyKeyPath");
        AssertFalse(
            kp is global::Swift.WritableKeyPath<IProtocolBag_BookFilter, string>,
            "protocol get-only requirement is NOT a WritableKeyPath");
    }

    public void TestRatingSingleton_IsKeyPathOfOptional()
    {
        var kp = ProtocolBag_BookProtocolBag_BookFilterKeyPaths.Rating;
        AssertTrue(
            kp is global::Swift.KeyPath<IProtocolBag_BookFilter, nint?>,
            "Optional<Int> property projects to KeyPath<IProtocolBag_BookFilter, nint?>");
    }

    // ---------------------------------------------------------------------------------------
    // Lazy initialisation + caching
    // ---------------------------------------------------------------------------------------

    public void TestSingleton_RepeatedAccess_ReturnsSameInstance()
    {
        var a = ProtocolBag_BookProtocolBag_BookFilterKeyPaths.Title;
        var b = ProtocolBag_BookProtocolBag_BookFilterKeyPaths.Title;
        AssertTrue(ReferenceEquals(a, b),
            "Lazy<T>-backed protocol-rooted singleton returns the same reference on repeated access");
    }

    public void TestSingleton_ValueEqualsItself()
    {
        var kp = ProtocolBag_BookProtocolBag_BookFilterKeyPaths.Title;
        AssertTrue(kp.Equals(kp), "Protocol-rooted singleton value-equals itself");
        AssertEqual(kp.GetHashCode(), kp.GetHashCode(),
            "Hash is stable across repeated calls");
    }

    // ---------------------------------------------------------------------------------------
    // Two-conformer separation: Book.Filter ≠ Movie.Filter
    // ---------------------------------------------------------------------------------------

    public void TestBookTitle_AndMovieTitle_AreSeparate()
    {
        var bookTitle = ProtocolBag_BookProtocolBag_BookFilterKeyPaths.Title;
        var movieTitle = ProtocolBag_MovieProtocolBag_MovieFilterKeyPaths.Title;

        AssertTrue(
            bookTitle is global::Swift.KeyPath<IProtocolBag_BookFilter, string>,
            "Book.Title Root is IProtocolBag_BookFilter");
        AssertTrue(
            movieTitle is global::Swift.KeyPath<IProtocolBag_MovieFilter, string>,
            "Movie.Title Root is IProtocolBag_MovieFilter");

        global::Swift.AnyKeyPath bookErased = bookTitle;
        global::Swift.AnyKeyPath movieErased = movieTitle;
        AssertFalse(bookErased.Equals(movieErased),
            "Different protocol-rooted singletons are value-distinct");
    }

    // ---------------------------------------------------------------------------------------
    // Round-trip through a Swift consumer (the IN-path proof for protocol-bag shape)
    //
    // The C# singleton is passed as typed
    // `Swift.KeyPath<IProtocolBag_*Filter, TValue>` directly — no `Swift.AnyKeyPath` boxing
    // and no Swift-side `as!` cast. The Swift body upcasts only the receiver
    // (`filter as ProtocolBag_*Filter`), which is required by Swift's typed-KeyPath
    // subscript rules when the KeyPath Root is existential; that upcast is a language
    // invariant, not a workaround. If the descriptor were malformed the witness-table
    // dispatch would trap inside the Swift body — passing the assert proves the
    // singleton is well-formed and runtime-resolvable through the conformer's witness.
    // ---------------------------------------------------------------------------------------

    public void TestSingleton_ReadTitle_RoundTripsThroughSwiftConsumer()
    {
        using var filter = new ProtocolBag_BookFilterImpl(
            title: "Pride and Prejudice", year: 1813, isFiction: true, rating: null);
        var kp = ProtocolBag_BookProtocolBag_BookFilterKeyPaths.Title;
        var read = ProtocolBag_BookConsumer.ReadTitle(filter, kp);
        AssertEqual("Pride and Prejudice", read,
            "Protocol-rooted singleton reads through witness-table dispatch");
    }

    public void TestSingleton_ReadYear_RoundTripsThroughSwiftConsumer()
    {
        using var filter = new ProtocolBag_BookFilterImpl(
            title: "Anything", year: 1949, isFiction: false, rating: 5);
        var kp = ProtocolBag_BookProtocolBag_BookFilterKeyPaths.Year;
        var read = ProtocolBag_BookConsumer.ReadYear(filter, kp);
        AssertEqual<nint>(1949, read,
            "Int property reads through protocol-rooted singleton");
    }

    public void TestSingleton_ReadIsFiction_RoundTripsThroughSwiftConsumer()
    {
        using var filter = new ProtocolBag_BookFilterImpl(
            title: "Hobbit", year: 1937, isFiction: true, rating: null);
        var kp = ProtocolBag_BookProtocolBag_BookFilterKeyPaths.IsFiction;
        var read = ProtocolBag_BookConsumer.ReadIsFiction(filter, kp);
        AssertTrue(read, "Bool property reads through protocol-rooted singleton");
    }

    public void TestSingleton_ReadRating_Some_RoundTripsThroughSwiftConsumer()
    {
        using var filter = new ProtocolBag_BookFilterImpl(
            title: "Ratings Test", year: 2000, isFiction: true, rating: 4);
        var kp = ProtocolBag_BookProtocolBag_BookFilterKeyPaths.Rating;
        var read = ProtocolBag_BookConsumer.ReadRating(filter, kp);
        AssertTrue(read.HasValue, "Optional<Int> .some round-trips with value present");
        AssertEqual<nint>(4, read!.Value, "Optional<Int> .some value matches");
    }

    public void TestSingleton_ReadRating_None_RoundTripsThroughSwiftConsumer()
    {
        using var filter = new ProtocolBag_BookFilterImpl(
            title: "No Rating", year: 2000, isFiction: false, rating: null);
        var kp = ProtocolBag_BookProtocolBag_BookFilterKeyPaths.Rating;
        var read = ProtocolBag_BookConsumer.ReadRating(filter, kp);
        AssertFalse(read.HasValue, "Optional<Int> .none round-trips as null");
    }

    public void TestMovieSingleton_ReadTitle_RoundTripsThroughSwiftConsumer()
    {
        using var filter = new ProtocolBag_MovieFilterImpl(
            title: "Casablanca", runtimeMinutes: 102);
        var kp = ProtocolBag_MovieProtocolBag_MovieFilterKeyPaths.Title;
        var read = ProtocolBag_MovieConsumer.ReadTitle(filter, kp);
        AssertEqual("Casablanca", read,
            "Second protocol-rooted conformer reads through its own singleton");
    }

    public void TestMovieSingleton_ReadRuntimeMinutes_RoundTripsThroughSwiftConsumer()
    {
        using var filter = new ProtocolBag_MovieFilterImpl(
            title: "Casablanca", runtimeMinutes: 102);
        var kp = ProtocolBag_MovieProtocolBag_MovieFilterKeyPaths.RuntimeMinutes;
        var read = ProtocolBag_MovieConsumer.ReadRuntimeMinutes(filter, kp);
        AssertEqual<nint>(102, read,
            "Movie Int property reads through protocol-rooted singleton");
    }

    // ---------------------------------------------------------------------------------------
    // Value-equality through Swift dispatch
    // ---------------------------------------------------------------------------------------

    public void TestSingleton_SwiftSidedEquality_OnSamePath()
    {
        var kp = ProtocolBag_BookProtocolBag_BookFilterKeyPaths.Title;
        AssertTrue(ProtocolBag_BookConsumer.SamePath(kp, kp),
            "Swift-side AnyKeyPath.== on identical protocol-rooted singleton returns true");
    }
}
