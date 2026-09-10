// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using Swift;
using Swift.Runtime;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Collections;

/// <summary>
/// End-to-end coverage for <see cref="SwiftHashable.GetHashCode{T}"/>, the runtime's
/// only caller of Swift's <c>Hashable.hashValue</c> getter.
///
/// That entry point is a protocol DISPATCH THUNK, not a concrete stdlib function: it
/// loads the requirement's function pointer out of the witness table it is handed and
/// branches to it, so the conforming type's own <c>hashValue</c> runs. That makes it
/// unforgiving in a way an ordinary call is not — a misplaced argument does not return
/// a wrong hash, it returns a garbage function pointer the thunk jumps into. It is also
/// the one member of the collection group that takes an untyped <c>SwiftSelf</c> while
/// targeting a <c>…Tj</c> thunk rather than a symbol, and it now dispatches through the
/// C swiftcall shim <c>SBW_Hashable_HashValue</c> like the rest of that group.
///
/// The unit tests on the build host already pin the semantics on CoreCLR. This file is
/// the part they cannot reach: the Mono runtimes — Mono JIT on the simulator and Mono
/// full-AOT on a device — where the managed-to-native wrapper parks its GC-safe-region
/// cookie in a callee-saved register the argument setup may then overwrite. It also runs
/// under NativeAOT, so all four runtimes in the taxonomy read the same dispatch.
///
/// <see cref="LabeledRank"/> is the key throughout: a public, non-<c>@frozen</c> (so
/// resilient) Hashable struct mixing a reference-counted <c>String</c> with a POD
/// <c>Int32</c>. Its layout is knowable only through the value-witness table at runtime,
/// and its generated binding's <c>GetHashCode()</c> is implemented over this very call —
/// so a broken thunk takes the managed projection down with it.
/// </summary>
public class HashableWitnessDispatchTests : TestBase
{
    public HashableWitnessDispatchTests(TestResults results) : base(results) { }

    /// <summary>
    /// Two independently constructed values with equal content must hash equally. They
    /// are separate Swift instances with separate <c>String</c> storage, so their
    /// marshalled bytes differ in the pointer word — which is what makes this a real
    /// check rather than a tautology. <see cref="SwiftHashable.GetHashCode{T}"/>'s
    /// fallback hashes exactly those differing bytes and could not agree here; only the
    /// Hashable witness can.
    /// </summary>
    public void TestEqualValuesHashEqually()
    {
        using var first = new LabeledRank("a heap-backed label, comfortably out of line", 7);
        using var again = new LabeledRank("a heap-backed label, comfortably out of line", 7);

        AssertEqual(SwiftHashable.GetHashCode(first), SwiftHashable.GetHashCode(again),
            "Equal LabeledRank values must hash equally — the Hashable witness is not being reached.");

        // The generated binding routes its own GetHashCode() through the same call.
        AssertEqual(first.GetHashCode(), again.GetHashCode(),
            "The generated binding's GetHashCode must agree with the runtime's.");
    }

    /// <summary>
    /// The POD half of the payload has to reach the witness too: same label, different
    /// rank. A thunk handed the wrong <c>self</c> would be hashing something else
    /// entirely and has no reason to separate these.
    /// </summary>
    public void TestDifferingPodFieldChangesHash()
    {
        using var seven = new LabeledRank("a heap-backed label, comfortably out of line", 7);
        using var eight = new LabeledRank("a heap-backed label, comfortably out of line", 8);

        AssertFalse(SwiftHashable.GetHashCode(seven) == SwiftHashable.GetHashCode(eight),
            "LabeledRank values differing only in their Int32 field must not hash alike.");
    }

    /// <summary>
    /// Repeated dispatch on one receiver returns one value, and the receiver survives.
    /// <c>hashValue</c> borrows its receiver (<c>@in_guaranteed</c>), so the buffer stays
    /// the caller's to destroy — a shim that consumed it instead would show up here as a
    /// changed hash, a use-after-free, or a crash on dispose.
    /// </summary>
    public void TestRepeatedDispatchIsStableAndBorrows()
    {
        using var value = new LabeledRank("stability under repeated dispatch-thunk calls", 42);

        int first = SwiftHashable.GetHashCode(value);
        for (int i = 0; i < 32; i++)
        {
            AssertEqual(first, SwiftHashable.GetHashCode(value),
                "Repeated hashValue dispatch on one receiver must return one value.");
        }

        AssertEqual("stability under repeated dispatch-thunk calls", value.Label,
            "The receiver must survive the call — hashValue borrows it, it does not consume it.");
        AssertEqual(42, value.Rank, "The receiver's POD field must survive the call.");
    }

    /// <summary>
    /// A thunk that "works" by returning a constant — a seed, a zero, or the witness-table
    /// pointer itself — passes every equality check above. This is the half that catches it.
    /// </summary>
    public void TestDistinctValuesSeparate()
    {
        var hashes = new HashSet<int>();
        var values = new List<LabeledRank>();
        try
        {
            for (int i = 0; i < 32; i++)
            {
                var value = new LabeledRank($"a distinct heap-backed label #{i}", i);
                values.Add(value);
                hashes.Add(SwiftHashable.GetHashCode(value));
            }
        }
        finally
        {
            foreach (var value in values)
                value.Dispose();
        }

        // 32 distinct Swift hashes folded to 32 bits; a collision is possible but very
        // unlikely, whereas a constant-returning thunk collapses the whole set to one.
        AssertTrue(hashes.Count >= 30,
            $"Only {hashes.Count} distinct hashes over 32 distinct values — the hashValue " +
            $"dispatch is not reaching LabeledRank's witness.");
    }

    /// <summary>
    /// Ties the hash to the collection that depends on it. Swift's own <c>Set</c> decides
    /// membership by hashing through the same conformance this runtime dispatches to by
    /// hand, so a value the Swift side finds must also agree under
    /// <see cref="SwiftHashable.GetHashCode{T}"/> — and the marshal in gives the live
    /// collection an independent read of that same conformance.
    /// </summary>
    public void TestSwiftSetMembershipAgreesWithGetHashCode()
    {
        var managed = new HashSet<LabeledRank>();
        var probe = new LabeledRank("a heap-backed label, comfortably out of line", 7);
        var twin = new LabeledRank("a heap-backed label, comfortably out of line", 7);
        var absent = new LabeledRank("a label that was never inserted", 99);
        try
        {
            managed.Add(probe);
            managed.Add(new LabeledRank("a second heap-backed label", 8));

            // The managed HashSet already de-duplicated through the same witness: adding
            // an equal-by-value twin must not grow it.
            managed.Add(twin);
            AssertEqual(2, managed.Count,
                "A HashSet<LabeledRank> de-duplicates through the Hashable witness; the twin must collapse.");

            AssertTrue(Functions.LabeledRankSetContains(managed, twin),
                "Swift's own Set must find a value equal to one that was marshalled in.");
            AssertFalse(Functions.LabeledRankSetContains(managed, absent),
                "Swift's own Set must not find a value that was never inserted.");

            AssertEqual(SwiftHashable.GetHashCode(probe), SwiftHashable.GetHashCode(twin),
                "The two values Swift's Set treats as one must hash alike here too.");
            AssertFalse(SwiftHashable.GetHashCode(probe) == SwiftHashable.GetHashCode(absent),
                "A value Swift's Set keeps apart must not hash alike here.");
        }
        finally
        {
            foreach (var item in managed)
                item.Dispose();
            twin.Dispose();
            absent.Dispose();
        }
    }

    /// <summary>
    /// The same agreement read through the runtime's own <see cref="SwiftDictionary{TKey,TValue}"/>,
    /// whose key type is the non-trivial Hashable and whose <c>removeAll</c> is one of the
    /// other calls in this group. Content-equal keys must collapse to one entry.
    /// </summary>
    public void TestSwiftDictionaryKeyIdentityAgreesWithGetHashCode()
    {
        using var dict = new SwiftDictionary<SwiftString, nint>();

        using var first = new SwiftString("a heap-backed dictionary key, written once");
        using var again = new SwiftString("a heap-backed dictionary key, written once");
        using var other = new SwiftString("a different heap-backed dictionary key");

        dict[first] = 1;
        dict[again] = 2;   // the same key by Swift's own hashing — this overwrites
        dict[other] = 3;

        AssertEqual(2, dict.Count, "Content-equal keys must collapse to a single dictionary entry.");
        AssertEqual((nint)2, dict[first], "The second write must have landed on the first key.");
        AssertEqual((nint)3, dict[other], "The distinct key must keep its own value.");

        AssertEqual(SwiftHashable.GetHashCode(first), SwiftHashable.GetHashCode(again),
            "The two keys the dictionary treats as one must hash alike here too.");

        // RemoveAll is the Dictionary member that moved to the cdecl shim alongside the
        // hashValue thunk; draining a dictionary keyed on a non-trivial Hashable exercises
        // it with real reference-counted key storage to release.
        dict.RemoveAll();
        AssertEqual(0, dict.Count, "RemoveAll must drain a dictionary with reference-counted keys.");
    }
}
