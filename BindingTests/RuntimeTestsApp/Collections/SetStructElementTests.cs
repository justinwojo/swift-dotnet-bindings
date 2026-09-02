// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Runtime.CompilerServices;
using RuntimeTestsApp.Infrastructure;
using Swift;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Collections;

/// <summary>
/// End-to-end coverage for a Swift <c>Set</c> whose Element is a public,
/// resilient, Hashable STRUCT (<c>LabeledRank</c> — a reference-counted
/// <c>String</c> plus a POD <c>Int32</c>).
///
/// This is the element shape that has no per-type <c>@_cdecl</c> insert wrapper
/// in the runtime: <c>Set&lt;Int&gt;</c>, <c>Set&lt;Int64&gt;</c> and
/// <c>Set&lt;String&gt;</c> each have one, so anything else has to go through
/// Swift's own generic <c>Set.insert(_:)</c>. That call returns
/// <c>(inserted: Bool, memberAfterInsert: Element)</c> — a mixed tuple whose
/// <c>@out Element</c> buffer arrives as an ordinary leading pointer argument
/// rather than an sret register — and calling it through a raw
/// <c>CallConvSwift</c> P/Invoke corrupts Mono's thread state on the iOS
/// Simulator: an immediate SIGABRT on the managed-to-native transition, or a
/// set whose <c>Count</c> reads garbage before a SIGSEGV on a later insert or
/// on release. <see cref="SwiftSet{Element}"/> routes this path through the
/// C-side <c>SBW_Set_Insert</c> swiftcall shim instead, so the managed boundary
/// stays plain Cdecl.
///
/// Two directions are covered on purpose. The generated-binding tests go
/// through the projection a consumer actually writes — a populated managed set
/// handed to a Swift function, which the emitter lowers to
/// <c>SwiftSet&lt;T&gt;.FromEnumerable</c> — and read the result back from the
/// Swift side, so a corrupted storage slot cannot pass by accident. The
/// <see cref="SwiftSet{Element}"/> tests then pin the runtime wrapper's own
/// semantics: duplicate insert returning <c>false</c>, membership, and dispose
/// safety after a bulk build.
///
/// The element type mixes a reference and a POD field so the ownership contract
/// is actually exercised: the incoming element is consumed at +1 by the insert,
/// and <c>memberAfterInsert</c> is handed back at +1 for the caller to destroy
/// through the value-witness table. Over-releasing either one shows up above as
/// a drifting count or a crash on dispose. LEAKING one does not — a leaked copy
/// changes nothing observable about the set — so the third region drives the
/// same insert arm with a lifetime-counted element and asserts the live count
/// returns to zero. That region's element is a class rather than a struct, which
/// is also the only coverage of the general insert arm for a class element.
/// </summary>
public class SetStructElementTests : TestBase
{
    public SetStructElementTests(TestResults results) : base(results) { }

    private static HashSet<LabeledRank> BuildManagedSet(params (string Label, int Rank)[] items)
    {
        // The generated binding for a resilient struct is a C# class, but it
        // implements Equals/GetHashCode over Swift's own Hashable witness — so
        // this HashSet de-duplicates by VALUE, not by handle identity.
        var set = new HashSet<LabeledRank>();
        foreach (var (label, rank) in items)
            set.Add(new LabeledRank(label, rank));
        return set;
    }

    /// <summary>
    /// An <see cref="IReadOnlySet{T}"/> whose enumeration deliberately yields
    /// equal-by-value members more than once. A <c>HashSet&lt;LabeledRank&gt;</c>
    /// cannot do that — it collapses duplicates through the Swift Hashable
    /// witness before the marshal ever runs — so this is what drives Swift's own
    /// duplicate-insert arm from the parameter direction. Only the members
    /// <c>SwiftSet.FromEnumerable</c> actually uses are implemented; the set
    /// algebra is not part of the projection and would be a lie to fake.
    /// </summary>
    private sealed class DuplicateYieldingSet : IReadOnlySet<LabeledRank>
    {
        private readonly List<LabeledRank> _items;

        public DuplicateYieldingSet(List<LabeledRank> items) => _items = items;

        public int Count => _items.Count;

        public IEnumerator<LabeledRank> GetEnumerator() => _items.GetEnumerator();

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => _items.GetEnumerator();

        public bool Contains(LabeledRank item) => _items.Contains(item);

        public bool IsProperSubsetOf(IEnumerable<LabeledRank> other) => throw new NotSupportedException();
        public bool IsProperSupersetOf(IEnumerable<LabeledRank> other) => throw new NotSupportedException();
        public bool IsSubsetOf(IEnumerable<LabeledRank> other) => throw new NotSupportedException();
        public bool IsSupersetOf(IEnumerable<LabeledRank> other) => throw new NotSupportedException();
        public bool Overlaps(IEnumerable<LabeledRank> other) => throw new NotSupportedException();
        public bool SetEquals(IEnumerable<LabeledRank> other) => throw new NotSupportedException();
    }

    private static void DisposeAll(IEnumerable<LabeledRank> items)
    {
        foreach (var item in items)
            item.Dispose();
    }

    #region Generated binding — populated managed set marshalled IN

    public void TestStructElementSetMarshalsInPopulated()
    {
        var managed = BuildManagedSet(("alpha", 1), ("beta", 2), ("gamma", 3), ("delta", 4));
        try
        {
            // Count comes from Swift. A corrupted storage slot reads garbage here
            // rather than 4, which is exactly how the raw CallConvSwift path failed.
            AssertEqual(4, TestLibFunctions.LabeledRankSetCount(managed),
                "Swift-side count of a populated Set<LabeledRank>");

            // The POD half of every member's payload survived the marshal.
            AssertEqual(10, TestLibFunctions.LabeledRankSetRankSum(managed),
                "Swift-side sum of ranks (1+2+3+4)");

            // The reference-counted half survived too — a string released a call
            // too early would surface here, not as a wrong count.
            AssertEqual("alpha,beta,delta,gamma", TestLibFunctions.LabeledRankSetSortedLabels(managed),
                "Swift-side sorted labels of a populated Set<LabeledRank>");
        }
        finally
        {
            DisposeAll(managed);
        }
    }

    public void TestStructElementManagedSetDeduplicatesByValue()
    {
        // The binding for a resilient struct is a C# class, but it is NOT
        // compared by handle identity: the generated Equals/GetHashCode run on
        // Swift's own Hashable witness table, so five distinct managed
        // instances collapse to two before any marshalling happens. Asserted
        // here because it is the premise the duplicate-arm test below has to
        // work around — if this ever changed, that test would silently stop
        // driving the path it exists for.
        var instances = new List<LabeledRank>();
        for (int i = 0; i < 5; i++)
            instances.Add(new LabeledRank(i < 2 ? "alpha" : "beta", i < 2 ? 1 : 2));
        try
        {
            var managed = new HashSet<LabeledRank>(instances);
            AssertEqual(2, managed.Count, "HashSet<LabeledRank> de-duplicates through the Swift Hashable witness");
            AssertEqual(2, TestLibFunctions.LabeledRankSetCount(managed),
                "Swift agrees on the de-duplicated member count");
        }
        finally
        {
            DisposeAll(instances);
        }
    }

    public void TestStructElementSetDeduplicatesSwiftSide()
    {
        // Hand the marshal a set whose enumeration yields equal members more
        // than once, so Swift's own insert is what has to collapse them. That
        // drives the duplicate arm, where the returned memberAfterInsert is the
        // EXISTING member rather than the one passed in — a different value
        // than the caller handed over, copied out through the element's
        // value-witness table and destroyed by the runtime.
        var instances = new List<LabeledRank>
        {
            new LabeledRank("alpha", 1),
            new LabeledRank("alpha", 1),
            new LabeledRank("beta", 2),
            new LabeledRank("beta", 2),
            new LabeledRank("beta", 2),
        };
        var yielding = new DuplicateYieldingSet(instances);
        try
        {
            AssertEqual(5, yielding.Count, "All five instances reach the marshal, duplicates included");
            AssertEqual(2, TestLibFunctions.LabeledRankSetCount(yielding),
                "Swift collapses the duplicates to two distinct members");
            AssertEqual(3, TestLibFunctions.LabeledRankSetRankSum(yielding),
                "Rank sum reflects the de-duplicated members (1+2)");
            AssertEqual("alpha,beta", TestLibFunctions.LabeledRankSetSortedLabels(yielding),
                "Labels survive the duplicate arm's copy-out and destroy");
        }
        finally
        {
            DisposeAll(instances);
        }
    }

    public void TestStructElementSetMembershipFromSwift()
    {
        var managed = BuildManagedSet(("alpha", 1), ("beta", 2), ("gamma", 3));
        // An independently constructed instance that is EQUAL by value to a
        // member: membership must be decided by the element's Hashable witness
        // table, not by handle identity.
        using var equalProbe = new LabeledRank("beta", 2);
        // Same label, different rank — proves both fields participate in
        // equality, so a probe that only matched the string half would fail.
        using var labelOnlyProbe = new LabeledRank("beta", 99);
        using var stranger = new LabeledRank("omega", 26);
        try
        {
            AssertTrue(TestLibFunctions.LabeledRankSetContains(managed, equalProbe),
                "Set<LabeledRank> contains an equal-by-value probe");
            AssertFalse(TestLibFunctions.LabeledRankSetContains(managed, labelOnlyProbe),
                "Set<LabeledRank> does not contain a probe matching on label alone");
            AssertFalse(TestLibFunctions.LabeledRankSetContains(managed, stranger),
                "Set<LabeledRank> does not contain an unrelated element");
        }
        finally
        {
            DisposeAll(managed);
        }
    }

    public void TestStructElementSetReturnedFromSwift()
    {
        // Return direction: Swift builds the set, the binding hands back a
        // carrier, and feeding it straight back into a Swift function rebuilds
        // it member by member through the same insert path.
        var produced = TestLibFunctions.MakeLabeledRankSet(4);
        var enumerated = new List<LabeledRank>();
        try
        {
            AssertEqual(4, produced.Count, "Swift-produced Set<LabeledRank> reports four members");

            enumerated.AddRange(produced);
            AssertEqual(4, enumerated.Count, "Enumerated member count matches the produced set's Count");

            // 0+1+2+3 — round-tripping the produced set back through the
            // marshal-in path re-inserts every member.
            AssertEqual(6, TestLibFunctions.LabeledRankSetRankSum(produced),
                "Round-tripped Swift-produced set sums its ranks");
            AssertEqual("item0,item1,item2,item3", TestLibFunctions.LabeledRankSetSortedLabels(produced),
                "Round-tripped Swift-produced set reports its labels");
        }
        finally
        {
            // Each enumerated member carries an independent +1 moved out by the
            // iterator, separate from the carrier's copy.
            DisposeAll(enumerated);
            (produced as IDisposable)?.Dispose();
        }
    }

    #endregion

    #region SwiftSet<Element> wrapper — insert semantics on a struct element

    public void TestStructElementSwiftSetAddDuplicateReturnsFalse()
    {
        using var set = new SwiftSet<LabeledRank>();
        using var first = new LabeledRank("alpha", 1);
        // Equal by value, distinct instance: Swift must report "already
        // present" and hand back the EXISTING member as memberAfterInsert.
        using var duplicate = new LabeledRank("alpha", 1);
        using var second = new LabeledRank("beta", 2);

        AssertTrue(set.Add(first), "First Add on SwiftSet<LabeledRank> returns true (inserted)");
        AssertEqual(1, set.Count, "SwiftSet<LabeledRank> count is 1 after the first Add");

        AssertFalse(set.Add(duplicate), "Duplicate Add on SwiftSet<LabeledRank> returns false (already present)");
        AssertEqual(1, set.Count, "SwiftSet<LabeledRank> count stays at 1 after the duplicate Add");

        // A distinct element after the duplicate path ran: catches bookkeeping
        // that only breaks once the memberAfterInsert copy-out has executed.
        AssertTrue(set.Add(second), "Add of a distinct element after the duplicate path returns true");
        AssertEqual(2, set.Count, "SwiftSet<LabeledRank> count is 2 after the distinct Add");

        AssertTrue(set.Contains(first), "SwiftSet<LabeledRank> contains its first member");
        AssertTrue(set.Contains(duplicate), "SwiftSet<LabeledRank> contains an equal-by-value instance");
    }

    public void TestStructElementSwiftSetRoundTrip()
    {
        var source = new List<LabeledRank>();
        for (int i = 0; i < 12; i++)
            source.Add(new LabeledRank($"item{i}", i));

        var enumerated = new List<LabeledRank>();
        try
        {
            using var set = SwiftSet<LabeledRank>.FromEnumerable(source);
            AssertEqual(source.Count, set.Count, "SwiftSet<LabeledRank> count after FromEnumerable");

            foreach (var element in source)
                AssertTrue(set.Contains(element), "SwiftSet<LabeledRank> contains every source element");

            enumerated.AddRange(set);
            AssertEqual(source.Count, enumerated.Count, "Enumerating SwiftSet<LabeledRank> yields every member");

            // Every rank 0..11 came back exactly once, so no member was dropped
            // or duplicated by the insert path.
            int sum = 0;
            foreach (var element in enumerated)
                sum += element.Rank;
            AssertEqual(66, sum, "Enumerated ranks sum to 0+1+…+11");
        }
        finally
        {
            DisposeAll(enumerated);
            DisposeAll(source);
        }
    }

    [Slow]
    public void TestStructElementSwiftSetBulkAddThenDispose()
    {
        // Bulk build then explicit dispose: the shape that used to take a
        // SIGSEGV either mid-insert or on the set's release once a trampoline
        // scratch address had been written into the storage slot. A leak or
        // over-release of the per-insert memberAfterInsert copy would also
        // surface here rather than in the small cases above.
        const int bulkCount = 512;
        var source = new List<LabeledRank>();
        for (int i = 0; i < bulkCount; i++)
            source.Add(new LabeledRank($"bulk{i}", i));

        try
        {
            var set = SwiftSet<LabeledRank>.FromEnumerable(source);
            AssertEqual(bulkCount, set.Count, "SwiftSet<LabeledRank> count after a bulk insert of distinct elements");
            set.Dispose();

            // A fresh set after the dispose proves the runtime is not poisoned.
            using var follow = SwiftSet<LabeledRank>.FromEnumerable(source.GetRange(0, 3));
            AssertEqual(3, follow.Count, "Follow-up SwiftSet<LabeledRank> after dispose still works");
        }
        finally
        {
            DisposeAll(source);
        }
    }

    #endregion

    #region Ownership — memberAfterInsert is destroyed, on a class element

    private static void DrainFinalizers()
    {
        for (int i = 0; i < 4; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
        GC.Collect();
    }

    /// <summary>
    /// The insert path's ownership half, made observable. Every call hands back
    /// <c>memberAfterInsert</c> at +1 and the runtime destroys it through the
    /// element's value-witness table; nothing above can see whether that destroy
    /// happens, because a leaked copy changes neither the count, the payloads,
    /// nor the ability to dispose. So this drives the same path with
    /// <c>TrackedRef</c>, whose Swift <c>deinit</c> decrements the shared live
    /// count — dropping the destroy leaves one pinned object per call.
    ///
    /// The element is also a CLASS rather than a struct, which makes this the
    /// only place the general insert arm is exercised with a class element: its
    /// payload word IS the object pointer, so the +1 the destroy has to release
    /// is an ARC retain on the object itself rather than on a field inside a
    /// resilient buffer. Both arms of insert are driven — the inserted arm hands
    /// back the new member, the duplicate arm hands back the existing one.
    /// </summary>
    [Slow]
    public void TestClassElementSetInsertReleasesMemberAfterInsert()
    {
        DrainFinalizers();
        LifetimeTracker.Reset();

        const int membersPerSet = 5;
        const int iterations = 50;
        InsertAndDisposeTrackedSets(iterations, membersPerSet);
        DrainFinalizers();

        LifetimeTracker.AssertNoLeaks(
            "Set.insert must destroy every memberAfterInsert copy it hands back");
        TestLogger.Info(
            $"Set<TrackedRef> insert: {iterations} sets x {membersPerSet} members x 2 arms all released");
    }

    // Runs in a non-inlined helper so no stale stack slot keeps the last set or
    // member alive past its Dispose and shows up as a false residual.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void InsertAndDisposeTrackedSets(int iterations, int membersPerSet)
    {
        for (int i = 0; i < iterations; i++)
        {
            var set = new SwiftSet<TrackedRef>();
            var members = new List<TrackedRef>();
            try
            {
                for (int m = 0; m < membersPerSet; m++)
                {
                    var member = new TrackedRef(m);
                    members.Add(member);

                    // Inserted arm: memberAfterInsert is the member just added.
                    set.Add(member);
                    // Duplicate arm: TrackedRef hashes on identity, so the same
                    // instance is already present and the existing member comes
                    // back instead — a second +1 to release.
                    set.Add(member);
                }
            }
            finally
            {
                foreach (var member in members)
                    member.Dispose();
                set.Dispose();
            }
        }
    }

    #endregion
}
