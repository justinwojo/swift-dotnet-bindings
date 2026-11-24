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
/// Collections whose elements or values are one object reference: an Objective-C class
/// (<c>NSUUID</c>), an NSObject-rooted Swift class, a pure Swift class and a string-backed
/// Objective-C key type (<c>NSAttributedString.Key</c>).
/// </summary>
/// <remarks>
/// A collection C# builds is Swift's once it crosses. Its storage must be the element type's own,
/// holding one reference per element: Swift may keep it after C# has let go of every element,
/// replace an element (releasing the one it held), or hand it to Objective-C as an NSArray. A
/// collection Swift builds may be backed by an Objective-C collection, and C# must read it as one
/// of the element type. Retain counts are compared with a +1 control, and every read checks the
/// element's identity.
/// </remarks>
public class ObjectElementCollectionsTests : TestBase
{
    public ObjectElementCollectionsTests(TestResults results) : base(results) { }

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

    // ── C# builds, Swift keeps ──────────────────────────────────────────────────────────────

    /// <summary>
    /// An array C# builds holds exactly one reference per element while Swift keeps it, and none
    /// once Swift lets it go; a call that only reads it leaves the count where it was.
    /// </summary>
    public void TestArrayElementsCSharpBuiltAreRetainedOnceAndReleased()
    {
        using var uuid = new NSUuid(UuidA);
        using var store = new ObjectElementStore();
        nint baseline = TestLibFunctions.ObjectElementRetainCount(uuid);

        store.Keep(new[] { uuid });
        AssertEqual(baseline + 1, TestLibFunctions.ObjectElementRetainCount(uuid), "the array Swift keeps holds one reference on its element");
        AssertEqual(UuidA, store.GetKeptStrings(), "and reads it back");

        store.Clear();
        AssertEqual(baseline, TestLibFunctions.ObjectElementRetainCount(uuid), "clearing the array releases it");

        for (int i = 0; i < 25; i++)
            AssertEqual(UuidA, TestLibFunctions.ObjectElementThroughNSArray(new[] { uuid }), "the element reads back");
        AssertEqual(baseline, TestLibFunctions.ObjectElementRetainCount(uuid), "25 calls that only read the element leave its retain count where it was");
    }

    /// <summary>An array Swift keeps still reads back after C# dropped and collected every element.</summary>
    public void TestSwiftKeepsTheElementsOfAnArrayCSharpBuilt()
    {
        using var store = new ObjectElementStore();
        KeepFreshList(store);
        CollectAll();
        AssertEqual($"{UuidA},{UuidB}", store.GetKeptStrings(), "the kept elements still read back");
        store.Clear();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void KeepFreshList(ObjectElementStore store)
    {
        var list = new[] { new NSUuid(UuidA), new NSUuid(UuidB) };
        store.Keep(list);
        foreach (var uuid in list)
            uuid.Dispose();
    }

    /// <summary>The array a C# property setter hands Swift holds its own references.</summary>
    public void TestPropertySetterArrayIsRetained()
    {
        using var uuid = new NSUuid(UuidB);
        using var store = new ObjectElementStore();
        nint baseline = TestLibFunctions.ObjectElementRetainCount(uuid);
        store.Items = new[] { uuid };
        AssertEqual(baseline + 1, TestLibFunctions.ObjectElementRetainCount(uuid), "the property Swift keeps holds one reference");
        AssertEqual(UuidB, store.GetKeptStrings(), "and reads it back");
        store.Clear();
        AssertEqual(baseline, TestLibFunctions.ObjectElementRetainCount(uuid), "and releases it");
    }

    /// <summary>
    /// Swift replacing an element releases the one the storage held, so the storage must have held
    /// one: the replaced object's count drops back to where it was, not below.
    /// </summary>
    public void TestSwiftReplacingAnElementReleasesOnlyItsOwnReference()
    {
        using var first = new NSUuid(UuidA);
        using var second = new NSUuid(UuidB);
        using var store = new ObjectElementStore();
        nint baseline = TestLibFunctions.ObjectElementRetainCount(first);
        store.Keep(new[] { first });
        store.ReplaceFirst(second);
        AssertEqual(baseline, TestLibFunctions.ObjectElementRetainCount(first), "the replaced element is back at its count");
        AssertEqual(UuidB, store.GetKeptStrings(), "the replacement reads back");
        store.Clear();
    }

    /// <summary>Objective-C reads an array C# built through the NSArray Swift bridges it to.</summary>
    public void TestArrayCSharpBuiltBridgesToNSArray()
    {
        using var a = new NSUuid(UuidA);
        using var b = new NSUuid(UuidB);
        AssertEqual($"{UuidA},{UuidB}", TestLibFunctions.ObjectElementThroughNSArray(new[] { a, b }), "the NSArray holds the elements");

        using var store = new ObjectElementStore();
        store.Keep(new[] { a, b });
        AssertEqual($"{UuidA},{UuidB}", store.GetKeptThroughNSArray(), "a kept array bridges too");
        store.Clear();
    }

    /// <summary>A dictionary C# builds holds one reference per value.</summary>
    public void TestDictionaryValuesCSharpBuiltAreRetained()
    {
        using var uuid = new NSUuid(UuidA);
        using var store = new ObjectElementStore();
        nint baseline = TestLibFunctions.ObjectElementRetainCount(uuid);
        store.KeepValues(new Dictionary<string, NSUuid> { { "a", uuid } });
        AssertEqual(baseline + 1, TestLibFunctions.ObjectElementRetainCount(uuid), "the dictionary Swift keeps holds one reference on its value");
        store.Clear();
        AssertEqual(baseline, TestLibFunctions.ObjectElementRetainCount(uuid), "and releases it");

        KeepFreshValues(store);
        CollectAll();
        AssertEqual($"a={UuidA},b={UuidB}", store.GetKeptValueStrings(), "the kept values still read back");
        store.Clear();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void KeepFreshValues(ObjectElementStore store)
    {
        var values = new Dictionary<string, NSUuid> { { "a", new NSUuid(UuidA) }, { "b", new NSUuid(UuidB) } };
        store.KeepValues(values);
        foreach (var value in values.Values)
            value.Dispose();
    }

    /// <summary>An optional array and an array of arrays C# builds hold their own references.</summary>
    public void TestOptionalAndNestedArraysCSharpBuiltAreRetained()
    {
        using var uuid = new NSUuid(UuidC);
        using var store = new ObjectElementStore();
        nint baseline = TestLibFunctions.ObjectElementRetainCount(uuid);

        store.KeepOptional(new[] { uuid });
        AssertEqual(baseline + 1, TestLibFunctions.ObjectElementRetainCount(uuid), "the optional array holds one reference");
        AssertEqual(UuidC, store.GetKeptOptionalStrings(), "and reads it back");
        store.KeepOptional(null);
        AssertEqual("nil", store.GetKeptOptionalStrings(), "nil crosses as nil");
        AssertEqual(baseline, TestLibFunctions.ObjectElementRetainCount(uuid), "and releases it");

        store.MaybeItems = new[] { uuid };
        AssertEqual(baseline + 1, TestLibFunctions.ObjectElementRetainCount(uuid), "the optional array a property sets holds one reference");
        store.MaybeItems = null;
        AssertEqual(baseline, TestLibFunctions.ObjectElementRetainCount(uuid), "and releases it once Swift drops it");

        store.KeepNested(new[] { new[] { uuid }, new[] { uuid } });
        AssertEqual(baseline + 2, TestLibFunctions.ObjectElementRetainCount(uuid), "each inner array holds one reference");
        AssertEqual($"{UuidC}|{UuidC}", store.GetKeptNestedStrings(), "and reads back");
        store.Clear();
        AssertEqual(baseline, TestLibFunctions.ObjectElementRetainCount(uuid), "and releases them");
    }

    /// <summary>Arrays of NSObject-rooted and pure Swift classes C# builds hold their own references.</summary>
    public void TestSwiftClassElementsCSharpBuiltAreRetained()
    {
        using var tracked = new ObjectElementTracked(7);
        using var store = new ObjectElementStore();
        nint baseline = TestLibFunctions.ObjectElementTrackedRetainCount(tracked);
        store.KeepTracked(new[] { tracked });
        AssertEqual(baseline + 1, TestLibFunctions.ObjectElementTrackedRetainCount(tracked), "the array holds one reference on its NSObject-rooted element");
        store.Clear();
        AssertEqual(baseline, TestLibFunctions.ObjectElementTrackedRetainCount(tracked), "and releases it");

        KeepFreshSwiftObjects(store);
        CollectAll();
        AssertEqual("1,2", store.GetKeptTrackedTags(), "kept NSObject-rooted elements still read back");
        AssertEqual("3,4", store.GetKeptRefTags(), "kept Swift class elements still read back");
        store.Clear();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void KeepFreshSwiftObjects(ObjectElementStore store)
    {
        var tracked = new[] { new ObjectElementTracked(1), new ObjectElementTracked(2) };
        var refs = new[] { new ObjectElementRef(3), new ObjectElementRef(4) };
        store.KeepTracked(tracked);
        store.KeepRefs(refs);
        foreach (var t in tracked)
            t.Dispose();
        foreach (var r in refs)
            r.Dispose();
    }

    const string LongKeyA = "ObjectElementCollectionsTests.FirstKeptKey";
    const string LongKeyB = "ObjectElementCollectionsTests.SecondKeptKey";

    /// <summary>An array of a string-backed key type C# builds holds its own references.</summary>
    public void TestStringBackedKeyElementsCSharpBuiltAreRetained()
    {
        using var key = new NSString(new string(LongKeyA.ToCharArray()));
        using var store = new ObjectElementStore();
        nint baseline = TestLibFunctions.ObjectElementStringRetainCount(key);
        store.KeepKeys(new[] { key });
        AssertEqual(baseline + 1, TestLibFunctions.ObjectElementStringRetainCount(key), "the array Swift keeps holds one reference on its key's string");
        store.Clear();
        AssertEqual(baseline, TestLibFunctions.ObjectElementStringRetainCount(key), "and releases it");

        KeepFreshKeys(store);
        CollectAll();
        AssertEqual($"{LongKeyA},{LongKeyB}", store.GetKeptKeyNames(), "the kept keys still read back");
        store.Clear();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void KeepFreshKeys(ObjectElementStore store)
    {
        // Fresh heap strings: too long for a tagged pointer, and in no constant table, so nothing
        // but the array keeps them alive once C# lets go.
        var keys = new[] { new NSString(new string(LongKeyA.ToCharArray())), new NSString(new string(LongKeyB.ToCharArray())) };
        store.KeepKeys(keys);
        foreach (var key in keys)
            key.Dispose();
    }

    // ── Swift builds, C# reads ──────────────────────────────────────────────────────────────

    /// <summary>A native array Swift builds reads back, and its elements outlive it.</summary>
    public void TestArraySwiftBuiltReadsAndOutlivesItsContainer()
    {
        NSUuid first = ReadFirstAndDrop(UuidA, UuidB);
        CollectAll();
        AssertEqual(UuidA, first.AsString(), "the element C# read outlives the array");
        first.Dispose();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static NSUuid ReadFirstAndDrop(string a, string b)
    {
        var list = TestLibFunctions.ObjectElementMakeList(new[] { a, b });
        if (list.Count != 2 || list[1].AsString() != b)
            throw new InvalidOperationException($"the list Swift built read back {list.Count} elements");
        var first = list[0];
        (list as IDisposable)?.Dispose();
        return first;
    }

    /// <summary>An array backed by an NSArray reads as an array of its element type.</summary>
    public void TestArrayBackedByNSArrayReads()
    {
        var list = TestLibFunctions.ObjectElementMakeBridgedList(new[] { UuidA, UuidB, UuidC });
        AssertEqual(3, list.Count, "the bridged array's count");
        AssertEqual(UuidA, list[0].AsString(), "first element");
        AssertEqual(UuidC, list[2].AsString(), "last element");
        (list as IDisposable)?.Dispose();
    }

    /// <summary>A dictionary backed by an NSDictionary reads as one of its key and value types.</summary>
    public void TestDictionaryBackedByNSDictionaryReads()
    {
        var values = TestLibFunctions.ObjectElementMakeBridgedValues(new[] { UuidA, UuidB });
        AssertEqual(2, values.Count, "the bridged dictionary's count");
        var seen = new List<string>();
        foreach (var pair in values)
        {
            AssertEqual(pair.Key.AsString(), pair.Value.AsString(), "each value is its key");
            seen.Add(pair.Value.AsString());
        }
        seen.Sort(StringComparer.Ordinal);
        AssertEqual($"{UuidB},{UuidA}", string.Join(",", seen), "both values read");
    }

    /// <summary>A set backed by an NSSet reads as a set of its element type.</summary>
    public void TestSetBackedByNSSetReads()
    {
        var set = TestLibFunctions.ObjectElementMakeBridgedSet(new[] { UuidA, UuidB });
        var seen = new List<string>();
        foreach (var uuid in set)
            seen.Add(uuid.AsString());
        seen.Sort(StringComparer.Ordinal);
        AssertEqual($"{UuidB},{UuidA}", string.Join(",", seen), "both elements read");
    }

    /// <summary>A native dictionary of object values reads back.</summary>
    public void TestDictionarySwiftBuiltValuesRead()
    {
        var values = TestLibFunctions.ObjectElementMakeValues(new[] { UuidA, UuidB });
        AssertEqual(2, values.Count, "count");
        AssertEqual(UuidA, values["k0"].AsString(), "first value");
        AssertEqual(UuidB, values["k1"].AsString(), "second value");
    }

    // ── Closures ──────────────────────────────────────────────────────────────────────────

    /// <summary>A C# closure reads the array Swift hands it.</summary>
    public void TestClosureReadsTheArraySwiftHandsIt()
    {
        var result = TestLibFunctions.ObjectElementCallWithList(new[] { UuidA, UuidB }, list =>
            string.Join(",", list.Select(e => ObjCRuntime.Runtime.GetNSObject<NSUuid>(e.Handle)!.AsString())));
        AssertEqual($"{UuidA},{UuidB}", result, "the closure saw both elements");
    }
}
