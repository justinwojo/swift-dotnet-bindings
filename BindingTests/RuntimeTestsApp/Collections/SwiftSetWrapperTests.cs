// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using Swift;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Collections;

/// <summary>
/// Targeted coverage for the per-type <c>@_cdecl</c> Set.insert wrappers.
/// Each Element type has its own native wrapper symbol so the tests must
/// exercise every path independently:
/// <list type="bullet">
///   <item><c>long</c> → <c>SBW_SetInt64_Insert</c> (Swift <c>Set&lt;Int64&gt;</c>)</item>
///   <item><c>nint</c> → <c>SBW_SetInt_Insert</c> (Swift <c>Set&lt;Int&gt;</c>)</item>
///   <item><c>SwiftString</c> → <c>SBW_SetString_Insert</c> (Swift <c>Set&lt;String&gt;</c>)</item>
/// </list>
/// On 64-bit hosts <c>long</c> and <c>nint</c> share byte layout, so a wrapper
/// that mis-binds storage (e.g. <c>Set&lt;Int&gt;</c> for both) would still pass
/// numeric round-trip — these tests exist so that any future divergence
/// between the two Swift generic instantiations is caught directly.
///
/// Duplicate-insert is its own path: Swift's <c>Set.insert</c> returns
/// <c>(inserted: false, memberAfterInsert: existing)</c> when the element is
/// already present, which means the wrapper writes a *different* value than
/// the caller passed in (the existing member, copied via the value-witness
/// table). For <c>SwiftString</c> that path also exercises the input element
/// being released by Swift while a freshly-retained string is written into
/// <c>outMember</c>.
/// </summary>
public class SwiftSetWrapperTests : TestBase
{
    public SwiftSetWrapperTests(TestResults results) : base(results) { }

    #region SwiftSet<nint> — Swift.Int wrapper path

    public void TestSwiftSetNintRoundTrip()
    {
        nint[] source = { 1, 2, 3, 5, 8, 13, 21, 34, 55, 89 };
        nint[] roundTripped;
        using (var set = SwiftSet<nint>.FromEnumerable(source))
        {
            AssertEqual(source.Length, set.Count, "SwiftSet<nint> count after FromEnumerable");
            roundTripped = set.ToArray();
        }

        AssertEqual(source.Length, roundTripped.Length, "SwiftSet<nint> ToArray length");
        var bag = new HashSet<nint>(roundTripped);
        for (int i = 0; i < source.Length; i++)
            AssertTrue(bag.Contains(source[i]), $"SwiftSet<nint> missing element {source[i]}");
    }

    public void TestSwiftSetNintAddAndContains()
    {
        using var set = new SwiftSet<nint>();
        AssertTrue(set.Add(42), "Adding 42 to empty SwiftSet<nint> returns true");
        AssertTrue(set.Add(-7), "Adding -7 to SwiftSet<nint> returns true");
        AssertEqual(2, set.Count, "SwiftSet<nint> count after two Adds");
        AssertTrue(set.Contains(42), "SwiftSet<nint> Contains(42)");
        AssertTrue(set.Contains(-7), "SwiftSet<nint> Contains(-7)");
        AssertTrue(!set.Contains(0), "SwiftSet<nint> does not contain 0");
    }

    public void TestSwiftSetNintDispose()
    {
        // Dispose the bulk-built set explicitly (mirrors the simulator-crash repro
        // for SwiftSet<long>) and assert that ARC release through the per-type
        // wrapper path leaves the runtime in a sane state — i.e. doesn't blow up
        // mid-finalize and doesn't leak so much that a follow-up allocation fails.
        var bulk = new nint[4096];
        for (int i = 0; i < bulk.Length; i++)
            bulk[i] = (nint)(i * 17 + 3);

        var set = SwiftSet<nint>.FromEnumerable(bulk);
        AssertEqual(bulk.Length, set.Count, "SwiftSet<nint> count after bulk insert (all distinct)");
        set.Dispose();

        // Allocate a fresh set after disposing — proves the runtime isn't poisoned.
        using var follow = SwiftSet<nint>.FromEnumerable(new nint[] { 1, 2, 3 });
        AssertEqual(3, follow.Count, "Follow-up SwiftSet<nint> after dispose still works");
    }

    #endregion

    #region Duplicate-insert path — Add returns false, wrapper copies existing memberAfterInsert

    public void TestSwiftSetLongDuplicateAddReturnsFalse()
    {
        using var set = new SwiftSet<long>();
        AssertTrue(set.Add(100L), "First Add(100) on SwiftSet<long> returns true (inserted)");
        AssertTrue(!set.Add(100L), "Duplicate Add(100) on SwiftSet<long> returns false (already present)");
        AssertEqual(1, set.Count, "SwiftSet<long> count stays at 1 after duplicate Add");
    }

    public void TestSwiftSetNintDuplicateAddReturnsFalse()
    {
        using var set = new SwiftSet<nint>();
        AssertTrue(set.Add((nint)42), "First Add(42) on SwiftSet<nint> returns true (inserted)");
        AssertTrue(!set.Add((nint)42), "Duplicate Add(42) on SwiftSet<nint> returns false (already present)");
        AssertEqual(1, set.Count, "SwiftSet<nint> count stays at 1 after duplicate Add");
    }

    public void TestSwiftSetStringDuplicateAddReturnsFalse()
    {
        // The SwiftString duplicate-Add path is the most fragile: Swift consumes the
        // input element via .move() and, when the element already exists, must
        // release the freshly-marshalled string while writing a freshly-retained
        // copy of the existing member into outMember. A leak or double-free in that
        // path shows up as either (a) Count drifting on subsequent inserts, or
        // (b) a crash during set.Dispose() as ARC underflows.
        using var set = new SwiftSet<SwiftString>();
        var s1 = new SwiftString("hello");
        AssertTrue(set.Add(s1), "First Add(\"hello\") on SwiftSet<SwiftString> returns true");

        // Build an *equal but distinct* SwiftString — same characters, different
        // managed instance — so the duplicate-Add path actually does work
        // (compare-by-value, then release the new one). A wrapper that compared
        // identity would silently insert again and Count would reach 2.
        var s2 = new SwiftString("hello");
        AssertTrue(!set.Add(s2), "Duplicate Add(\"hello\") on SwiftSet<SwiftString> returns false");
        AssertEqual(1, set.Count, "SwiftSet<SwiftString> count stays at 1 after duplicate Add");

        // One more distinct element to confirm Count still tracks correctly after
        // the duplicate path executed (catches off-by-one bookkeeping bugs).
        var s3 = new SwiftString("world");
        AssertTrue(set.Add(s3), "Add(\"world\") after duplicate insert returns true");
        AssertEqual(2, set.Count, "SwiftSet<SwiftString> count is 2 after distinct insert post-duplicate");
    }

    #endregion

    #region Set<class> — Hashable witness table on a reference-type element (NativeAOT pre-registration)

    /// <summary>
    /// Count / contains / membership round-trip for a Swift <c>Set</c> whose element is a
    /// user-defined Swift CLASS conforming to Hashable (<c>TrackedRef</c>, identity-based
    /// <c>==</c>/<c>hash(into:)</c>). Every Set operation resolves the element's Hashable
    /// witness table through <c>HashableConformanceRegistry.GetHashableWitnessTable&lt;T&gt;</c>.
    /// On NativeAOT that path is reflection-free ONLY when the class's Hashable conformance
    /// was pre-registered into <c>WitnessTableDispatcher</c> by the generated
    /// <c>[ModuleInitializer]</c> — class (reference-type) Hashable conformances were
    /// previously dropped from that registration list, so this exercised the AOT-incompatible
    /// reflection fallback and crashed on device. Functional companion to
    /// <c>WireCarrierLeakProbeTests.TestSetOfClassReturnReleasesMembers</c>.
    ///
    /// The set is produced by Swift (<c>MakeTrackedRefSet</c>) rather than built C#-side with
    /// <c>Add</c>, which keeps this probe about witness resolution alone: <c>Count</c>,
    /// enumeration and <c>Contains</c> are the only Set operations it drives. Insert dispatch for
    /// an element type with no typed <c>@_cdecl</c> wrapper — which a user class is — is covered
    /// separately by <c>SetStructElementTests</c> — for a struct element throughout, and for a
    /// <c>TrackedRef</c> class element in
    /// <c>TestClassElementSetInsertReleasesMemberAfterInsert</c>. It routes through the C-side
    /// <c>SBW_Set_Insert</c> shim, because the stdlib <c>Set.insert</c> shape
    /// (<c>Bool</c> direct + <c>@out Element</c> via x0) is mishandled by Mono's CallConvSwift
    /// trampoline on the Simulator.
    /// </summary>
    public void TestSwiftSetOfClassContainsCount()
    {
        const int memberCount = 5;
        var set = TestLibFunctions.MakeTrackedRefSet(memberCount);
        var members = new List<TrackedRef>();
        try
        {
            AssertEqual(memberCount, set.Count, "Set<TrackedRef>.Count reflects the Swift-populated member count");

            // Enumeration drives Set.Iterator.next() — each member is moved out through the
            // element's value-witness table; the count must match what Swift inserted.
            members.AddRange(set);
            AssertEqual(memberCount, members.Count, "Enumerated member count matches Set<TrackedRef>.Count");

            // Identity hashing/equality (===) via the registered Hashable witness table: every
            // enumerated member wraps the same Swift object stored in the set, so Contains is true.
            foreach (var member in members)
                AssertTrue(set.Contains(member), $"Set<TrackedRef> contains its own member (tag={member.Tag})");

            // An independently constructed instance is a distinct object: not a member. Proves the
            // witness table drives membership, not a structural byte hash over the handle pointers.
            using var stranger = new TrackedRef(memberCount + 100);
            AssertTrue(!set.Contains(stranger), "Set<TrackedRef> does not contain an independently constructed instance");
        }
        finally
        {
            // Each enumerated member carries an independent +1 (moved out by the iterator),
            // separate from the set carrier's copy — release both deterministically.
            foreach (var member in members)
                member.Dispose();
            (set as IDisposable)?.Dispose();
        }
    }

    #endregion
}
