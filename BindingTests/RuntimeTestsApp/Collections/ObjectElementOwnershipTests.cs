// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Runtime.CompilerServices;
using Foundation;
using RuntimeTestsApp.Infrastructure;
using Swift;
using Swift.Runtime;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Collections;

/// <summary>
/// The object references a Swift collection holds beyond its plain elements: optional object
/// elements and values, bound generic Objective-C elements, object keys and elements read out of a
/// raw collection and kept past it, set comparisons of distinct but Swift-equal objects, and the
/// inner collections C# builds only to have Swift copy them.
/// </summary>
/// <remarks>
/// Retain counts are compared with a +1 control, and every read checks the element's identity. A
/// value read out of a raw collection is checked after the collection is gone and the collector ran.
/// </remarks>
public class ObjectElementOwnershipTests : TestBase
{
    public ObjectElementOwnershipTests(TestResults results) : base(results) { }

    const string UuidA = "6F9619FF-8B86-D011-B42D-00C04FC964FF";
    const string UuidB = "0B2D7C4E-5A1F-4C3B-9E8D-7F6A5B4C3D2E";
    const string UuidC = "11111111-2222-3333-4444-555555555555";

    static void CollectAll()
    {
        for (int i = 0; i < 4; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
    }

    static nint Count(NSUuid uuid) => TestLibFunctions.ObjectElementRetainCount(uuid);

    static string Describe(IntPtr handle)
        => handle == IntPtr.Zero ? "nil" : ObjCRuntime.Runtime.GetNSObject<NSUuid>(handle)!.AsString();

    static string Describe<TIdentity>(SwiftObjectKey<TIdentity> key) where TIdentity : ISwiftObjectKeyIdentity
        => Describe(key.Handle);

    // ── Optional object elements ────────────────────────────────────────────────────────────

    /// <summary>An array of optional objects C# builds holds one reference per non-nil element.</summary>
    public void TestOptionalArrayElementsCSharpBuiltAreRetained()
    {
        using var uuid = new NSUuid(UuidA);
        using var store = new ObjectOwnershipStore();
        nint baseline = Count(uuid);

        store.Keep(new NSUuid?[] { uuid, null, uuid });
        AssertEqual(baseline + 2, Count(uuid), "the array Swift keeps holds one reference per non-nil element");
        AssertEqual($"{UuidA},nil,{UuidA}", store.GetKeptStrings(), "and reads them back, nil included");
        store.Clear();
        AssertEqual(baseline, Count(uuid), "clearing the array releases them");

        store.Items = new NSUuid?[] { null, uuid };
        AssertEqual(baseline + 1, Count(uuid), "the array a property sets holds one reference");
        store.Clear();
        AssertEqual(baseline, Count(uuid), "and releases it");
    }

    /// <summary>A kept array of optional objects still reads back after C# dropped and collected them.</summary>
    public void TestSwiftKeepsTheOptionalElementsOfAnArrayCSharpBuilt()
    {
        using var store = new ObjectOwnershipStore();
        KeepFreshOptionalList(store);
        CollectAll();
        AssertEqual($"{UuidA},nil,{UuidB}", store.GetKeptStrings(), "the kept elements still read back");
        store.Clear();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void KeepFreshOptionalList(ObjectOwnershipStore store)
    {
        var a = new NSUuid(UuidA);
        var b = new NSUuid(UuidB);
        store.Keep(new NSUuid?[] { a, null, b });
        a.Dispose();
        b.Dispose();
    }

    /// <summary>A dictionary of optional object values C# builds holds its own references.</summary>
    public void TestOptionalDictionaryValuesCSharpBuiltAreRetained()
    {
        using var uuid = new NSUuid(UuidB);
        using var store = new ObjectOwnershipStore();
        nint baseline = Count(uuid);
        store.KeepValues(new Dictionary<string, NSUuid?> { { "a", uuid }, { "b", null } });
        AssertEqual(baseline + 1, Count(uuid), "the dictionary Swift keeps holds one reference on its value");
        AssertEqual($"a={UuidB},b=nil", store.GetKeptValueStrings(), "and reads its values back");
        store.Clear();
        AssertEqual(baseline, Count(uuid), "and releases it");

        KeepFreshOptionalValues(store);
        CollectAll();
        AssertEqual($"a={UuidA},b=nil", store.GetKeptValueStrings(), "the kept values still read back");
        store.Clear();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void KeepFreshOptionalValues(ObjectOwnershipStore store)
    {
        var a = new NSUuid(UuidA);
        store.KeepValues(new Dictionary<string, NSUuid?> { { "a", a }, { "b", null } });
        a.Dispose();
    }

    /// <summary>Arrays of optional Swift class references C# builds hold their own references.</summary>
    public void TestOptionalSwiftClassElementsCSharpBuiltAreRetained()
    {
        using var tracked = new ObjectElementTracked(7);
        using var store = new ObjectOwnershipStore();
        nint baseline = TestLibFunctions.ObjectElementTrackedRetainCount(tracked);
        store.KeepTracked(new ObjectElementTracked?[] { tracked, null });
        AssertEqual(baseline + 1, TestLibFunctions.ObjectElementTrackedRetainCount(tracked), "the array holds one reference on its NSObject-rooted element");
        AssertEqual("7,nil", store.GetKeptTrackedTags(), "and reads it back");
        store.Clear();
        AssertEqual(baseline, TestLibFunctions.ObjectElementTrackedRetainCount(tracked), "and releases it");

        KeepFreshOptionalSwiftObjects(store);
        CollectAll();
        AssertEqual("1,nil", store.GetKeptTrackedTags(), "kept NSObject-rooted elements still read back");
        AssertEqual("nil,4", store.GetKeptRefTags(), "kept Swift class elements still read back");
        store.Clear();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void KeepFreshOptionalSwiftObjects(ObjectOwnershipStore store)
    {
        var tracked = new ObjectElementTracked(1);
        var reference = new ObjectElementRef(4);
        store.KeepTracked(new ObjectElementTracked?[] { tracked, null });
        store.KeepRefs(new ObjectElementRef?[] { null, reference });
        tracked.Dispose();
        reference.Dispose();
    }

    /// <summary>
    /// Optional Swift class elements Swift hands back read as wrappers that hold their own
    /// references: they outlive the array, the store's copy and the collector.
    /// </summary>
    public void TestOptionalSwiftClassElementsReadBackOutliveTheirContainer()
    {
        using var store = new ObjectOwnershipStore();
        KeepFreshOptionalSwiftObjects(store);
        var kept = ReadKeptRefs(store);
        store.Clear();
        CollectAll();
        AssertEqual(2, kept.Length, "every element read");
        AssertEqual(null, kept[0], "the nil element reads as null");
        AssertEqual(4L, (long)(kept[1]?.Tag ?? -1), "the Swift class element outlives the array and the store");
        foreach (var reference in kept) reference?.Dispose();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static ObjectElementRef?[] ReadKeptRefs(ObjectOwnershipStore store)
    {
        var list = store.GetKeptRefs();
        var kept = list.ToArray();
        (list as IDisposable)?.Dispose();
        return kept;
    }

    /// <summary>Arrays and dictionaries of optional objects Swift builds read back, nil included.</summary>
    public void TestOptionalElementsSwiftBuiltRead()
    {
        var list = TestLibFunctions.ObjectOwnershipMakeList(new[] { UuidA, "-", UuidC });
        AssertEqual(3, list.Count, "the list's count");
        AssertEqual(UuidA, list[0]?.AsString(), "first element");
        AssertEqual(null, list[1]?.AsString(), "nil element");
        AssertEqual(UuidC, list[2]?.AsString(), "last element");
        (list as IDisposable)?.Dispose();

        var values = TestLibFunctions.ObjectOwnershipMakeValues(new[] { "-", UuidB });
        AssertEqual(2, values.Count, "the dictionary's count");
        AssertEqual(null, values["k0"]?.AsString(), "nil value");
        AssertEqual(UuidB, values["k1"]?.AsString(), "present value");
        (values as IDisposable)?.Dispose();
    }

    /// <summary>The optional elements C# read outlive the array Swift returned.</summary>
    public void TestOptionalElementsSwiftBuiltOutliveTheirContainer()
    {
        NSUuid? kept = ReadOptionalAndDrop();
        CollectAll();
        AssertEqual(UuidB, kept?.AsString(), "the element C# read outlives the array");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static NSUuid? ReadOptionalAndDrop()
    {
        var list = TestLibFunctions.ObjectOwnershipMakeList(new[] { "-", UuidB });
        var kept = list[1];
        (list as IDisposable)?.Dispose();
        return kept;
    }

    /// <summary>A C# closure reads the array of optional objects Swift hands it.</summary>
    public void TestClosureReadsOptionalElementsSwiftHandsIt()
    {
        var result = TestLibFunctions.ObjectOwnershipCallWithList(new[] { UuidA, "-", UuidB }, list =>
            string.Join(",", list.Select(e => Describe(e))));
        AssertEqual($"{UuidA},nil,{UuidB}", result, "the closure saw every element, nil included");
    }

    // ── Bound generic Objective-C elements ──────────────────────────────────────────────────

    /// <summary>An array of a bound generic Objective-C class C# builds holds its own references.</summary>
    public void TestBoundGenericObjCElementsCSharpBuiltAreRetained()
    {
        using var store = new ObjectOwnershipStore();
        KeepFreshCaches(store);
        CollectAll();
        AssertEqual($"{UuidA},{UuidB}", store.KeptCacheStrings("k"), "the kept caches still read back");
        store.Clear();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void KeepFreshCaches(ObjectOwnershipStore store)
    {
        var first = new NSCache();
        var second = new NSCache();
        first.SetObjectForKey(new NSUuid(UuidA), new NSString("k"));
        second.SetObjectForKey(new NSUuid(UuidB), new NSString("k"));
        store.KeepCaches(new[] { first, second });
        first.Dispose();
        second.Dispose();
    }

    /// <summary>
    /// An array of optional bound generic Objective-C objects C# builds holds its own references,
    /// and the caches C# reads back out of it outlive the array.
    /// </summary>
    public void TestOptionalBoundGenericObjCElementsAreRetained()
    {
        using var store = new ObjectOwnershipStore();
        KeepFreshOptionalCaches(store);
        CollectAll();
        AssertEqual($"{UuidA},nil", store.KeptOptionalCacheStrings("k"), "the kept caches still read back");

        var kept = ReadKeptOptionalCaches(store);
        store.Clear();
        CollectAll();
        AssertEqual(2, kept.Length, "every element read");
        AssertEqual(UuidA, (kept[0]?.ObjectForKey(new NSString("k")) as NSUuid)?.AsString(), "the cache read outlives the array");
        AssertEqual(null, kept[1], "the nil element reads as null");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void KeepFreshOptionalCaches(ObjectOwnershipStore store)
    {
        var cache = new NSCache();
        cache.SetObjectForKey(new NSUuid(UuidA), new NSString("k"));
        store.KeepOptionalCaches(new NSCache?[] { cache, null });
        cache.Dispose();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static NSCache?[] ReadKeptOptionalCaches(ObjectOwnershipStore store)
    {
        var list = store.GetKeptOptionalCaches();
        var kept = list.ToArray();
        (list as IDisposable)?.Dispose();
        return kept;
    }

    // ── Raw collections: what a reader holds stays valid ────────────────────────────────────

    /// <summary>
    /// An element read out of a raw set holds its own reference: after the set is emptied and
    /// disposed, the carrier still names a live object, and once the carrier is gone the reference is
    /// released.
    /// </summary>
    public void TestRawSetElementOutlivesItsSet()
    {
        using var uuid = new NSUuid(UuidA);
        nint baseline = Count(uuid);
        var (heldCount, described) = ReadRawSetElementPastItsSet(uuid);
        AssertEqual(baseline + 1, heldCount, "the element read out of the set holds one reference of its own");
        AssertEqual(UuidA, described, "and names the object the set held, after the set is gone and the collector ran");
        CollectAll();
        AssertEqual(baseline, Count(uuid), "once the element is gone its reference is released");
    }

    // The carrier lives only in this frame, so no stale stack copy keeps its reference reachable once
    // the frame is gone (Mono scans the stack conservatively).
    [MethodImpl(MethodImplOptions.NoInlining)]
    static (nint HeldCount, string Described) ReadRawSetElementPastItsSet(NSUuid uuid)
    {
        var key = ReadRawSetElementAndDropSet(uuid);
        CollectAll();
        return (Count(uuid), Describe(key));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static SwiftObjectKey<__SwiftObjectKeys.Foundation_NSUUID> ReadRawSetElementAndDropSet(NSUuid uuid)
    {
        var set = new SwiftSet<SwiftObjectKey<__SwiftObjectKeys.Foundation_NSUUID>>();
        set.Add(new SwiftObjectKey<__SwiftObjectKeys.Foundation_NSUUID>(uuid.Handle));
        using var e = set.GetEnumerator();
        e.MoveNext();
        set.RemoveAll();
        var key = e.Current;
        set.Dispose();
        return key;
    }

    /// <summary>Keys and values read out of a raw dictionary, and elements of a raw array, outlive it.</summary>
    public void TestRawDictionaryAndArrayReadsOutliveTheirCollection()
    {
        using var uuid = new NSUuid(UuidB);
        nint baseline = Count(uuid);
        var (heldCount, described) = ReadRawDictionaryAndArrayPastThem(uuid);
        AssertEqual(baseline + 3, heldCount, "the key, the value and the array element read out each hold one reference");
        AssertEqual($"{UuidB},{UuidB},{UuidB}", described, "each names the object the collection held, after the collections are gone and the collector ran");
        CollectAll();
        AssertEqual(baseline, Count(uuid), "and release it once they are gone");
    }

    // The carriers live only in this frame (see ReadRawSetElementPastItsSet).
    [MethodImpl(MethodImplOptions.NoInlining)]
    static (nint HeldCount, string Described) ReadRawDictionaryAndArrayPastThem(NSUuid uuid)
    {
        var held = ReadRawDictionaryAndArray(uuid);
        CollectAll();
        return (Count(uuid), string.Join(",", held.Select(k => Describe(k))));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static List<SwiftObjectKey<__SwiftObjectKeys.Foundation_NSUUID>> ReadRawDictionaryAndArray(NSUuid uuid)
    {
        var carrier = new SwiftObjectKey<__SwiftObjectKeys.Foundation_NSUUID>(uuid.Handle);
        var held = new List<SwiftObjectKey<__SwiftObjectKeys.Foundation_NSUUID>>();
        using (var dictionary = new SwiftDictionary<SwiftObjectKey<__SwiftObjectKeys.Foundation_NSUUID>, SwiftObjectKey<__SwiftObjectKeys.Foundation_NSUUID>>())
        {
            dictionary[carrier] = carrier;
            foreach (var pair in dictionary)
            {
                held.Add(pair.Key);
                held.Add(pair.Value);
            }
        }
        using (var array = SwiftArray<SwiftObjectKey<__SwiftObjectKeys.Foundation_NSUUID>>.FromEnumerable(new[] { carrier }))
        {
            held.Add(array[0]);
        }
        GC.KeepAlive(uuid);
        return held;
    }

    /// <summary>
    /// Enumerating a raw set nothing else refers to: the set may be collected after the enumeration
    /// starts, and the elements still read are its last references.
    /// </summary>
    public void TestEnumeratingATemporaryRawSetKeepsItsElements()
    {
        var seen = new List<string>();
        foreach (var key in MakeTemporaryRawSet())
        {
            CollectAll();
            seen.Add(Describe(key));
        }
        seen.Sort(StringComparer.Ordinal);
        AssertEqual($"{UuidB},{UuidA}", string.Join(",", seen), "every element read after the set was collected names its object");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static SwiftSet<SwiftObjectKey<__SwiftObjectKeys.Foundation_NSUUID>> MakeTemporaryRawSet()
    {
        var set = new SwiftSet<SwiftObjectKey<__SwiftObjectKeys.Foundation_NSUUID>>();
        foreach (var text in new[] { UuidA, UuidB })
        {
            var uuid = new NSUuid(text);
            set.Add(new SwiftObjectKey<__SwiftObjectKeys.Foundation_NSUUID>(uuid.Handle));
            uuid.Dispose();
        }
        return set;
    }

    /// <summary>Elements a closure keeps from the set Swift hands it still read after the call.</summary>
    public void TestClosureKeepsElementsOfTheSetSwiftHandsIt()
    {
        var kept = new List<SwiftObjectKey<__SwiftObjectKeys.Foundation_NSUUID>>();
        nint count = TestLibFunctions.ObjectOwnershipCallWithSet(new[] { UuidA, UuidC }, set =>
        {
            kept.AddRange(set);
            return set.Count;
        });
        AssertEqual((nint)2, count, "the closure saw both elements");
        CollectAll();
        var seen = kept.Select(k => Describe(k)).OrderBy(s => s, StringComparer.Ordinal).ToArray();
        AssertEqual($"{UuidC},{UuidA}", string.Join(",", seen), "the elements kept outlive the set Swift released");
    }

    // ── Raw set comparisons agree with Swift ────────────────────────────────────────────────

    /// <summary>
    /// Two distinct objects Swift calls equal are one element to a raw set: its comparisons agree
    /// with its own <c>Contains</c>.
    /// </summary>
    public void TestRawSetComparisonsUseSwiftEquality()
    {
        using var a1 = new NSUuid(UuidA);
        using var a2 = new NSUuid(UuidA);
        using var b = new NSUuid(UuidB);
        AssertTrue(a1.Handle != a2.Handle, "the two UUIDs are distinct objects");
        var ka1 = new SwiftObjectKey<__SwiftObjectKeys.Foundation_NSUUID>(a1.Handle);
        var ka2 = new SwiftObjectKey<__SwiftObjectKeys.Foundation_NSUUID>(a2.Handle);
        var kb = new SwiftObjectKey<__SwiftObjectKeys.Foundation_NSUUID>(b.Handle);

        using var set = SwiftSet<SwiftObjectKey<__SwiftObjectKeys.Foundation_NSUUID>>.FromEnumerable(new[] { ka1 });
        AssertTrue(set.Contains(ka2), "Contains finds the equal object");
        AssertTrue(set.SetEquals(new[] { ka2 }), "SetEquals agrees");
        AssertTrue(set.SetEquals(new[] { ka2, ka1 }), "SetEquals counts Swift-equal elements of the other once");
        AssertTrue(set.IsSubsetOf(new[] { ka2 }), "IsSubsetOf agrees");
        AssertTrue(set.IsSupersetOf(new[] { ka2 }), "IsSupersetOf agrees");
        AssertTrue(set.IsProperSubsetOf(new[] { ka2, kb }), "IsProperSubsetOf agrees");
        AssertFalse(set.IsProperSubsetOf(new[] { ka2, ka1 }), "a Swift-equal pair is no larger set");
        AssertFalse(set.IsProperSupersetOf(new[] { ka2 }), "IsProperSupersetOf agrees");
        AssertTrue(set.Overlaps(new[] { ka2 }), "Overlaps agrees");

        using var both = SwiftSet<SwiftObjectKey<__SwiftObjectKeys.Foundation_NSUUID>>.FromEnumerable(new[] { ka1, kb });
        AssertTrue(both.IsProperSupersetOf(new[] { ka2, ka1 }), "IsProperSupersetOf counts the other's equal elements once");
        AssertFalse(both.SetEquals(new[] { ka2, ka1 }), "a missing element is still missing");
        GC.KeepAlive(a1);
        GC.KeepAlive(a2);
        GC.KeepAlive(b);
    }

    // ── Inner collections C# builds are released when Swift copies them ─────────────────────

    /// <summary>
    /// A dictionary whose values are arrays holds exactly the inner arrays' references once Swift
    /// keeps it: the inner arrays C# built to fill it gave theirs back as they were copied.
    /// </summary>
    public void TestDictionaryOfArraysCSharpBuiltHoldsOnlySwiftsReferences()
    {
        using var uuid = new NSUuid(UuidA);
        using var store = new ObjectOwnershipStore();
        nint baseline = Count(uuid);

        store.KeepGroups(new Dictionary<string, IEnumerable<NSUuid>> { { "g", new[] { uuid } } });
        AssertEqual(baseline + 1, Count(uuid), "the method's dictionary holds one reference, no temporary holds another");
        AssertEqual($"g={UuidA}", store.GetKeptGroupStrings(), "and reads back");
        store.Clear();
        AssertEqual(baseline, Count(uuid), "and releases it");

        store.Groups = new Dictionary<string, IReadOnlyList<NSUuid>> { { "g", new[] { uuid, uuid } } };
        AssertEqual(baseline + 2, Count(uuid), "the property's dictionary holds one reference per element, no temporary holds more");
        store.Clear();
        AssertEqual(baseline, Count(uuid), "and releases them");
    }

    /// <summary>An array of optional arrays a property setter builds holds only Swift's references.</summary>
    public void TestArrayOfOptionalArraysCSharpBuiltHoldsOnlySwiftsReferences()
    {
        using var uuid = new NSUuid(UuidB);
        using var store = new ObjectOwnershipStore();
        nint baseline = Count(uuid);
        store.Nested = new NSUuid[]?[] { new[] { uuid }, null };
        AssertEqual(baseline + 1, Count(uuid), "the property's array holds one reference, no temporary holds another");
        AssertEqual($"{UuidB}|nil", store.GetKeptNestedStrings(), "and reads back");
        store.Clear();
        AssertEqual(baseline, Count(uuid), "and releases it");
    }

    sealed class GroupSource : IObjectGroupSource
    {
        readonly NSUuid _uuid;
        public GroupSource(NSUuid uuid) => _uuid = uuid;
        public IReadOnlyDictionary<string, IReadOnlyList<NSUuid>> Groups
            => new Dictionary<string, IReadOnlyList<NSUuid>> { { "g", new[] { _uuid } } };
    }

    /// <summary>The dictionary of arrays a C# conformer returns to Swift leaves no temporary behind.</summary>
    public void TestReverseDispatchDictionaryOfArraysHoldsOnlySwiftsReferences()
    {
        using var uuid = new NSUuid(UuidC);
        var source = new GroupSource(uuid);
        nint baseline = Count(uuid);
        AssertEqual($"g={UuidC}", TestLibFunctions.ObjectOwnershipReadGroups(source), "Swift reads the groups C# returned");
        AssertEqual(baseline, Count(uuid), "and nothing C# built for it still holds a reference");
        GC.KeepAlive(source);
    }
}
