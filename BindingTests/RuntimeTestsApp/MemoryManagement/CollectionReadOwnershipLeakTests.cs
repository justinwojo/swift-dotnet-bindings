// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Runtime.CompilerServices;
using RuntimeTestsApp.Infrastructure;
using Swift;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.MemoryManagement;

/// <summary>
/// Read paths of <c>SwiftDictionary</c> and <c>SwiftSet</c> that receive an owned Swift value and
/// hand none of it out, or hand out only part of it.
///
/// Value side: Swift's dictionary subscript and iterator return every value at +1. <c>Keys</c>
/// snapshots whole entries, <c>ContainsKey</c> looks the value up, and an early exit from a
/// projection's <c>Values</c> stops before every entry is converted; each dropped the values it
/// did not return. A class-bound existential value is a raw two-word cell that only an
/// <c>ownsContainer: true</c> proxy releases, so a dropped cell pins its conformer for good.
///
/// Key side: marshalling a key or element copies it in at +1 (a class key is retained), and
/// Swift's lookup, <c>removeValue(forKey:)</c>, <c>updateValue(_:forKey:)</c>, <c>contains</c> and
/// <c>remove</c> only borrow it. The copy was never destroyed, so each call pinned a class key.
///
/// Every conformer and key is a <see cref="LifetimeTracker"/>-counted Swift object; the loops run on
/// an isolated thread and the finalizers are drained, so only a Swift-side reference nothing
/// releases is left counted.
/// </summary>
public class CollectionReadOwnershipLeakTests : TestBase
{
    public CollectionReadOwnershipLeakTests(TestResults results) : base(results) { }

    private static void DrainFinalizers()
    {
        for (int i = 0; i < 4; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
        GC.Collect();
    }

    private void AssertNoLeaksAfter(Action churn, string context)
    {
        DrainFinalizers();
        LifetimeTracker.Reset();
        LifetimeTracker.RunIsolated(churn);
        DrainFinalizers();
        LifetimeTracker.AssertNoLeaks(context);
    }

    /// <summary><c>Keys</c> of a <c>[String: any Marker]</c> releases every value it does not return.</summary>
    public void TestExistentialValueMapKeysReleasesValues()
    {
        AssertNoLeaksAfter(() => ReadExistentialMapKeys(20, 5),
            "[String: any Marker].Keys must release each value cell the snapshot moved out");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void ReadExistentialMapKeys(int iterations, int entries)
    {
        for (int i = 0; i < iterations; i++)
        {
            var dict = TestLibFunctions.MakeTrackedMarkerMap(entries);
            try
            {
                var keys = dict.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();
                AssertEqual(entries, keys.Count, "Keys returns one key per entry");
                AssertEqual("k0", keys[0], "Keys returns the Swift keys");
            }
            finally
            {
                (dict as IDisposable)?.Dispose();
            }
        }
    }

    /// <summary><c>ContainsKey</c> of a <c>[String: any Marker]</c> releases the value its lookup returned.</summary>
    public void TestExistentialValueMapContainsKeyReleasesValue()
    {
        AssertNoLeaksAfter(() => ProbeExistentialMapContainsKey(20, 5),
            "[String: any Marker].ContainsKey must release the looked-up value cell");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void ProbeExistentialMapContainsKey(int iterations, int entries)
    {
        for (int i = 0; i < iterations; i++)
        {
            var dict = TestLibFunctions.MakeTrackedMarkerMap(entries);
            try
            {
                for (int k = 0; k < entries; k++)
                    AssertTrue(dict.ContainsKey($"k{k}"), $"ContainsKey finds k{k}");
                AssertFalse(dict.ContainsKey("absent"), "ContainsKey misses an absent key");
            }
            finally
            {
                (dict as IDisposable)?.Dispose();
            }
        }
    }

    /// <summary>
    /// Stopping early in a projection's <c>Values</c> or enumerator releases the entries it never
    /// reached, LINQ stepping over entries (<c>ElementAt</c>) releases the ones it skipped, and the
    /// values LINQ reads and drops (<c>Last</c>) are released too: the snapshot already holds each
    /// of them at +1.
    /// </summary>
    public void TestExistentialValueMapEarlyExitReleasesUnreadValues()
    {
        AssertNoLeaksAfter(() => ReadFirstExistentialMapEntries(20, 5),
            "an early exit from [String: any Marker] enumeration must release the entries it skipped");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void ReadFirstExistentialMapEntries(int iterations, int entries)
    {
        for (int i = 0; i < iterations; i++)
        {
            var dict = TestLibFunctions.MakeTrackedMarkerMap(entries);
            try
            {
                var firstValue = dict.Values.First();
                var firstId = firstValue.GetMarkerId();
                AssertTrue(firstId >= 0 && firstId < entries, "the first value is one of the conformers");
                (firstValue as IDisposable)?.Dispose();

                foreach (var entry in dict)
                {
                    (entry.Value as IDisposable)?.Dispose();
                    break;
                }

                // LINQ steps over the entries before the one it returns; each still held a +1.
                var lastValue = dict.Values.ElementAt(entries - 1);
                AssertTrue(lastValue.GetMarkerId() >= 0, "ElementAt returns a live conformer");
                (lastValue as IDisposable)?.Dispose();

                // LINQ's Last() reads every value and keeps only the final one.
                var tailValue = dict.Values.Last();
                AssertTrue(tailValue.GetMarkerId() >= 0, "Last returns a live conformer");
                (tailValue as IDisposable)?.Dispose();
            }
            finally
            {
                (dict as IDisposable)?.Dispose();
            }
        }
    }

    /// <summary>
    /// Lookups keyed by a class release the key copy they marshal: indexer, <c>TryGetValue</c>,
    /// <c>ContainsKey</c>, the setter's <c>updateValue(_:forKey:)</c> and <c>RemoveValue</c>, for a present
    /// key and an absent one.
    /// </summary>
    public void TestClassKeyDictionaryLookupsReleaseKeyCopies()
    {
        AssertNoLeaksAfter(() => ChurnClassKeyDictionary(10, 4),
            "a class-keyed SwiftDictionary must destroy each borrowed key copy it marshals");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void ChurnClassKeyDictionary(int iterations, int keyCount)
    {
        for (int i = 0; i < iterations; i++)
        {
            var keys = new List<TrackedRef>();
            var dict = new SwiftDictionary<TrackedRef, int>();
            try
            {
                for (int k = 0; k < keyCount; k++)
                {
                    var key = new TrackedRef(k);
                    keys.Add(key);
                    dict[key] = k * 10;
                    dict[key] = k * 10 + 1;
                }
                AssertEqual(keyCount, dict.Count, "each key inserted once");

                foreach (var key in keys)
                {
                    AssertEqual(key.Tag * 10 + 1, dict[key], "indexer reads the replaced value");
                    AssertTrue(dict.TryGetValue(key, out var value) && value == key.Tag * 10 + 1, "TryGetValue finds the key");
                    AssertTrue(dict.ContainsKey(key), "ContainsKey finds the key");
                }

                using (var stranger = new TrackedRef(100))
                    AssertFalse(dict.ContainsKey(stranger), "a distinct object is not a key");

                AssertEqual(1, dict.RemoveValue(keys[0]), "RemoveValue returns the removed value");
                AssertFalse(dict.ContainsKey(keys[0]), "the removed key is gone");
                dict.RemoveValue(keys[0]);
                AssertEqual(keyCount - 1, dict.Count, "one key removed");
            }
            finally
            {
                dict.Dispose();
                foreach (var key in keys)
                    key.Dispose();
            }
        }
    }

    /// <summary><c>Contains</c> and <c>Remove</c> on a class-element set release the element copy they marshal.</summary>
    public void TestClassElementSetContainsAndRemoveReleaseElementCopies()
    {
        AssertNoLeaksAfter(() => ChurnClassElementSet(10, 4),
            "SwiftSet<TrackedRef>.Contains/Remove must destroy each borrowed element copy");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void ChurnClassElementSet(int iterations, int memberCount)
    {
        for (int i = 0; i < iterations; i++)
        {
            // The projected IReadOnlySet is the runtime SwiftSet, whose Remove is the mutating path probed here.
            var set = (SwiftSet<TrackedRef>)TestLibFunctions.MakeTrackedRefSet(memberCount);
            var members = new List<TrackedRef>();
            try
            {
                members.AddRange(set);
                foreach (var member in members)
                    AssertTrue(set.Contains(member), "Contains finds a member");
                using (var stranger = new TrackedRef(100))
                {
                    AssertFalse(set.Contains(stranger), "a distinct object is not a member");
                    AssertFalse(set.Remove(stranger), "Remove of a non-member reports false");
                }
                AssertTrue(set.Remove(members[0]), "Remove drops a member");
                AssertEqual(memberCount - 1, set.Count, "one member removed");
            }
            finally
            {
                set.Dispose();
                foreach (var member in members)
                    member.Dispose();
            }
        }
    }

    /// <summary>
    /// An element read of <c>[Any]</c> and a value read of <c>[String: Any]</c> release the
    /// existential Swift handed over at +1. The generated conversion borrowed it
    /// (<c>ExistentialContainer0.Unbox</c>), so every read pinned the boxed value. A class payload
    /// cannot be unboxed and the read throws, which must not keep the element alive either, nor
    /// leave alive the entries an enumeration stepped over or never reached.
    /// </summary>
    public void TestBareAnyCollectionReadsReleaseElements()
    {
        AssertNoLeaksAfter(() => ReadBareAnyCollections(10, 3),
            "[Any] and [String: Any] element reads must destroy the +1 container they unbox");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void ReadBareAnyCollections(int iterations, int count)
    {
        for (int i = 0; i < iterations; i++)
        {
            var list = TestLibFunctions.MakeTrackedRefAnyArray(count);
            try
            {
                AssertEqual(count, list.Count, "[Any] keeps every element");
                for (int e = 0; e < count; e++)
                    AssertThrows<NotSupportedException>(() => _ = list[e], "a class element of [Any] does not unbox");
            }
            finally
            {
                (list as IDisposable)?.Dispose();
            }

            var map = TestLibFunctions.MakeTrackedRefAnyMap(count);
            try
            {
                AssertEqual(count, map.Count, "[String: Any] keeps every entry");
                for (int k = 0; k < count; k++)
                    AssertThrows<NotSupportedException>(() => _ = map[$"k{k}"], "a class value of [String: Any] does not unbox");
                AssertThrows<NotSupportedException>(() => _ = map.Values.ToList(),
                    "enumerating class values of [String: Any] stops at the first one");
                AssertThrows<NotSupportedException>(() => _ = map.Values.ElementAt(count - 1),
                    "the class value ElementAt reaches does not unbox; the ones it steps over are released unread");
                AssertEqual(count, map.Keys.Count(), "Keys still reads every entry");
            }
            finally
            {
                (map as IDisposable)?.Dispose();
            }
        }
    }
}
