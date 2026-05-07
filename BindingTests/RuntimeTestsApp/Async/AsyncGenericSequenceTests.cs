// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using Swift;
using Swift.Runtime;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Async;

/// <summary>
/// Regression coverage for the MusicKit-shape generic-over-Sequence
/// async-throws bug. <c>AnimalAsyncRoster</c> declares
/// <c>insertAsync&lt;S: Sequence&gt;(contentsOf:beforeIndex:shouldThrow:) async throws
/// where S.Element : Animal</c> — the same class-inheritance constraint shape
/// the SYNC <c>AnimalRoster.insert</c> already exercises, but routed through
/// the CSM-async pipeline.
///
/// Pre-fix the per-pairing structural filter in
/// <c>ConcreteProtocolSpecializationEmitter.Async.IsEmittableAsyncPairing</c>
/// did not call the bilateral
/// <c>DoesPairingSatisfyAssociatedTypeConstraints</c> filter that the sync
/// path uses. The cartesian product of the engine's hint-scoped Sequence
/// conformers (UInt8, SongItem, AlbumItem, ArtistItem, Animal, Dog) all
/// passed the filter — generating overloads like
/// <c>InsertAsync(IEnumerable&lt;byte&gt;, …)</c> whose
/// <c>SBW_CSM_…_Swift_Array_Swift_UInt8_insertAsync_*_async</c> wrapper
/// symbol the Swift side never emits. First call →
/// <c>EntryPointNotFoundException</c>.
///
/// Post-fix the async path threads <c>method</c> + <c>parentTypeDecl</c> into
/// <c>IsEmittableAsyncPairing</c> and consults
/// <c>DoesPairingSatisfyAssociatedTypeConstraints</c> against the type
/// database. Conformers whose recorded <c>Element</c> doesn't match the
/// Animal class (or one of its subclasses) are rejected before any wrapper
/// symbol is reserved. Only the <c>Animal</c> and <c>Dog</c> overloads
/// reach the C# surface, and both have a corresponding <c>_async</c>
/// trampoline in the dylib.
///
/// These tests pin three end-to-end properties of the post-fix shape:
///   1. <c>InsertAsync(IEnumerable&lt;Animal&gt;, …)</c> is callable and
///      round-trips its inserted elements through the @_cdecl async
///      trampoline (no EntryPointNotFoundException).
///   2. The class-subtype path admits <c>InsertAsync(IEnumerable&lt;Dog&gt;, …)</c>
///      because <c>Dog : Animal</c> — exact-name match alone would falsely
///      reject the Dog conformer registered in
///      <c>specialization-hints.json</c>.
///   3. The async-throws plumbing surfaces a thrown error as a Task
///      faulted with a <c>SwiftException</c> — the constraint fix doesn't
///      suppress error reporting on the surviving overloads.
/// </summary>
public class AsyncGenericSequenceTests : TestBase
{
    public AsyncGenericSequenceTests(TestResults results) : base(results) { }

    public async Task TestAnimalAsyncRoster_InsertAsync_Animals_RoundTripsArrayOrder()
    {
        // Factory-built roster with two animals, then async-insert two more at
        // index 0. The post-call order must be [Lion, Tiger, Bear, Wolf] — the
        // inserted pair sits in front of the existing pair, with internal
        // element order preserved across the @_cdecl async boundary.
        using var roster = Functions.MakeAnimalAsyncRoster(firstName: "Bear", secondName: "Wolf");
        AssertEqual(2, (int)roster.Count, "Roster should start with 2 animals");

        var newcomers = new List<Animal>
        {
            new Animal(name: "Lion", sound: "Roar"),
            new Animal(name: "Tiger", sound: "Growl"),
        };

        await roster.InsertAsync(newcomers, 0, shouldThrow: false);

        AssertEqual(4, (int)roster.Count, "Roster should have 4 animals after async insert");
        AssertEqual("Lion", roster[0].Name.ToString(), "Index 0 after front-insert should be Lion");
        AssertEqual("Tiger", roster[1].Name.ToString(), "Index 1 after front-insert should be Tiger");
        AssertEqual("Bear", roster[2].Name.ToString(), "Index 2 after front-insert should be Bear");
        AssertEqual("Wolf", roster[3].Name.ToString(), "Index 3 after front-insert should be Wolf");
    }

    public async Task TestAnimalAsyncRoster_InsertAsync_DogSubclass_RoundTrips()
    {
        // Class-subtype path: `where S.Element : Animal` accepts `[Dog]` because
        // `Dog : Animal`. The post-fix bilateral filter walks the conformer
        // Element's SuperclassTypeName chain via the type database, admitting
        // the Dog conformer registered in specialization-hints.json. Without
        // the fix, the engine emits an InsertAsync(IEnumerable<byte>, …)
        // overload alongside Animal/Dog and whichever overload C# picks for
        // a `List<byte>` arg invokes a wrapper that doesn't exist in the dylib.
        using var roster = Functions.MakeAnimalAsyncRoster(firstName: "Bear", secondName: "Wolf");

        var dogs = new List<Dog>
        {
            new Dog(name: "Rex", breed: "Labrador"),
            new Dog(name: "Bella", breed: "Poodle"),
        };

        await roster.InsertAsync(dogs, 1, shouldThrow: false);

        AssertEqual(4, (int)roster.Count, "Roster should have 4 animals after async Dog insert");
        AssertEqual("Bear", roster[0].Name.ToString(), "Bear unchanged at index 0");
        AssertEqual("Rex", roster[1].Name.ToString(), "Rex spliced at index 1");
        AssertEqual("Bella", roster[2].Name.ToString(), "Bella spliced at index 2");
        AssertEqual("Wolf", roster[3].Name.ToString(), "Wolf shifted to index 3");
    }

    public async Task TestAnimalAsyncRoster_InsertAsync_Throwing_FaultsTask()
    {
        // The fix preserves async-throws plumbing for the surviving overloads:
        // `shouldThrow: true` makes the Swift body raise AsyncError.requestedThrow,
        // which the CSM-async harness must surface as a faulted Task with a
        // SwiftException — the type the CSM-async harness raises for thrown
        // Swift errors. Pinning the exact type also rules out
        // EntryPointNotFoundException (the pre-fix failure mode for a
        // missing wrapper symbol) silently greening this test: a missing
        // wrapper would surface as P/Invoke resolution failure, not as a
        // SwiftException, so this assertion is implicit coverage that the
        // right overload was selected and the wrapper is reachable.
        using var roster = Functions.MakeAnimalAsyncRoster(firstName: "Otter", secondName: "Seal");
        var animals = new List<Animal>
        {
            new Animal(name: "Lynx", sound: "Hiss"),
        };

        SwiftException? caught = null;
        try
        {
            await roster.InsertAsync(animals, 0, shouldThrow: true);
        }
        catch (SwiftException ex)
        {
            caught = ex;
            TestLogger.Info($"InsertAsync(shouldThrow: true) threw SwiftException: {ex.Message}");
        }

        AssertTrue(caught is not null,
            "InsertAsync(shouldThrow: true) should fault the Task with a SwiftException");
        AssertEqual(2, (int)roster.Count, "Roster should be unchanged after a thrown async insert");
    }
}
