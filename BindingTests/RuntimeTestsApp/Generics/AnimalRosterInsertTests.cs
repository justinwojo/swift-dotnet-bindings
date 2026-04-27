// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using Swift;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Generics;

/// <summary>
/// Regression coverage for the RealityFoundation Bug 3 collection-template
/// re-resolution. <c>AnimalRoster</c> declares
/// <c>insert&lt;S: Sequence&gt;(contentsOf source: S, beforeIndex i: Int) where S.Element : Animal</c>
/// — a method-level generic with a class-inheritance bound that the parser routes
/// through <see cref="ConformanceKind.Protocol"/>.
///
/// Pre-fix: the bilateral filter in
/// <c>ConcreteProtocolSpecializationEmitter.DoesPairingSatisfyAssociatedTypeConstraints</c>
/// skipped Protocol-kind associated-type entries unconditionally, so every
/// <c>Swift.Sequence</c> conformer in the engine's pool was paired with this method —
/// including <c>Foundation.Data</c> and <c>[UInt8]</c>. The generator stamped the
/// EntityCollection-flavored body into wrappers for non-matching element types and
/// <c>Wrapper.swift</c> failed with <c>Data.Element (UInt8) does not inherit from Animal</c>.
///
/// Post-fix: the filter consults the type database. When the constraint target
/// resolves to a class, exact-name equality is enforced on the conformer's recorded
/// <c>Element</c>. Conformers whose <c>Element</c> doesn't exactly match the class
/// (UInt8, SongItem, AlbumItem, ArtistItem) are dropped; only the registered
/// <c>Swift.Array&lt;SwiftBindingsTestLib.Animal&gt;</c> conformer survives, producing a
/// single specialized <c>Insert(SwiftArray&lt;Animal&gt;, nint)</c> overload.
///
/// These tests prove (1) the specialized overload is callable end-to-end, (2) the
/// element ordering round-trips through the @_cdecl wrapper, and (3) inserting at
/// a non-zero index splices in the middle of the existing roster rather than
/// stomping it.
/// </summary>
public class AnimalRosterInsertTests : TestBase
{
    public AnimalRosterInsertTests(TestResults results) : base(results) { }

    public void TestAnimalRoster_InsertContentsOf_AtFront_RoundTripsArrayOrder()
    {
        // Factory-built roster with two animals, then insert two more at index 0.
        // The post-call order must be [Lion, Tiger, Bear, Wolf] — the inserted
        // pair sits in front of the existing pair, with internal element order
        // preserved across the @_cdecl boundary.
        using var roster = Functions.MakeAnimalRoster(firstName: "Bear", secondName: "Wolf");
        AssertEqual(2, (int)roster.Count, "Roster should start with 2 animals");

        using var newcomers = new SwiftArray<Animal>();
        newcomers.Append(new Animal(name: "Lion", sound: "Roar"));
        newcomers.Append(new Animal(name: "Tiger", sound: "Growl"));

        roster.Insert(newcomers, 0);

        AssertEqual(4, (int)roster.Count, "Roster should have 4 animals after insert");
        AssertEqual("Lion", roster[0].Name.ToString(), "Index 0 after front-insert should be Lion");
        AssertEqual("Tiger", roster[1].Name.ToString(), "Index 1 after front-insert should be Tiger");
        AssertEqual("Bear", roster[2].Name.ToString(), "Index 2 after front-insert should be Bear");
        AssertEqual("Wolf", roster[3].Name.ToString(), "Index 3 after front-insert should be Wolf");
    }

    public void TestAnimalRoster_InsertContentsOf_InMiddle_SplicesAtIndex()
    {
        // Insert at index 1 — the inserted block must land between the existing
        // first and second elements, not at the front or back. This exercises
        // the i (beforeIndex) parameter wiring through the @_cdecl wrapper.
        using var roster = Functions.MakeAnimalRoster(firstName: "Bear", secondName: "Wolf");

        using var newcomers = new SwiftArray<Animal>();
        newcomers.Append(new Animal(name: "Lynx", sound: "Hiss"));

        roster.Insert(newcomers, 1);

        AssertEqual(3, (int)roster.Count, "Roster should have 3 animals after middle-insert");
        AssertEqual("Bear", roster[0].Name.ToString(), "Index 0 unchanged");
        AssertEqual("Lynx", roster[1].Name.ToString(), "Lynx spliced at index 1");
        AssertEqual("Wolf", roster[2].Name.ToString(), "Wolf shifted to index 2");
    }

    public void TestAnimalRoster_InsertContentsOf_EmptySequence_NoOp()
    {
        // An empty source must leave the roster untouched. The @_cdecl wrapper
        // has to handle a zero-length sequence without dereferencing past the
        // pointer, and the underlying Swift `insert(contentsOf:)` body returns
        // immediately for empty input.
        using var roster = Functions.MakeAnimalRoster(firstName: "Otter", secondName: "Seal");
        using var empty = new SwiftArray<Animal>();

        roster.Insert(empty, 0);

        AssertEqual(2, (int)roster.Count, "Empty insert should not change count");
        AssertEqual("Otter", roster[0].Name.ToString(), "Otter unchanged at index 0");
        AssertEqual("Seal", roster[1].Name.ToString(), "Seal unchanged at index 1");
    }

    public void TestAnimalRoster_InsertContentsOf_DogSubclassArray_RoundTrips()
    {
        // Swift's class subtype admits subclasses: `where S.Element : Animal` accepts
        // `[Dog]` because `Dog : Animal`. The bilateral filter walks the conformer
        // Element's SuperclassTypeName chain via the type database — exact-name
        // matching alone would falsely reject the Dog conformer registered in
        // specialization-hints.json. The presence of an `Insert(SwiftArray<Dog>, nint)`
        // overload (separate from the Animal one) is the structural proof that the
        // subtype walk fired during emission; this test verifies it round-trips
        // through the @_cdecl wrapper so the upcast to the heterogeneous [Animal]
        // backing store happens correctly inside Swift.
        using var roster = Functions.MakeAnimalRoster(firstName: "Bear", secondName: "Wolf");

        using var dogs = new SwiftArray<Dog>();
        dogs.Append(new Dog(name: "Rex", breed: "Labrador"));
        dogs.Append(new Dog(name: "Bella", breed: "Poodle"));

        roster.Insert(dogs, 1);

        AssertEqual(4, (int)roster.Count, "Roster should have 4 animals after Dog insert");
        AssertEqual("Bear", roster[0].Name.ToString(), "Bear unchanged at index 0");
        AssertEqual("Rex", roster[1].Name.ToString(), "Rex spliced at index 1");
        AssertEqual("Bella", roster[2].Name.ToString(), "Bella spliced at index 2");
        AssertEqual("Wolf", roster[3].Name.ToString(), "Wolf shifted to index 3");
    }
}
