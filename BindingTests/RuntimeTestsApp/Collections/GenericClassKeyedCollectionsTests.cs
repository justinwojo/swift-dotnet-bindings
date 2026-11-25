// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Runtime.CompilerServices;
using RuntimeTestsApp.Infrastructure;
using Swift;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Collections;

/// <summary>
/// Collections hashed by a bound generic Swift class: <c>Set&lt;SwiftKeyBox&lt;String&gt;&gt;</c> (Hashable
/// through a conditional conformance), <c>[SwiftKeyBox&lt;Int&gt;: Int]</c>,
/// <c>Set&lt;SwiftKeyLabel&lt;Int&gt;&gt;</c> (Hashable for every argument), and their optional, nested,
/// closure, reverse-dispatch and property forms.
/// </summary>
/// <remarks>
/// A bound generic class is a key type of its own: a collection over it must hash and compare through
/// that instantiation's Hashable conformance, so a Swift lookup with a box Swift makes finds a present
/// key, and must hold a reference on every key, so a collection Swift keeps outlives C#'s references.
/// </remarks>
public class GenericClassKeyedCollectionsTests : TestBase
{
    public GenericClassKeyedCollectionsTests(TestResults results) : base(results) { }

    static long? Long(nint? value) => value is { } v ? (long)v : null;

    static SwiftKeyBox<SwiftString> Box(string value) => new(new SwiftString(value));

    static string ValueOf(SwiftKeyBox<SwiftString> box) => box.Value.ToString();

    static SwiftSet<SwiftKeyBox<SwiftString>> Boxes(params string[] values)
    {
        var set = new SwiftSet<SwiftKeyBox<SwiftString>>();
        foreach (var value in values)
            set.Add(Box(value));
        return set;
    }

    static SwiftDictionary<SwiftKeyBox<nint>, nint> IntIndex(params nint[] values)
    {
        var index = new SwiftDictionary<SwiftKeyBox<nint>, nint>();
        for (int i = 0; i < values.Length; i++)
            index[new SwiftKeyBox<nint>(values[i])] = 10 + i;
        return index;
    }

    static void Collect()
    {
        for (int i = 0; i < 4; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
    }

    // ── C# builds, Swift looks up ───────────────────────────────────────────────────────────

    public void TestSwiftFindsElementsOfABoxSetBuiltInCSharp()
    {
        using var boxes = Boxes("alpha", "beta", "gamma");
        AssertTrue(boxes.Contains(Box("beta")), "C# finds a present box by the conditional conformance");
        AssertFalse(boxes.Contains(Box("delta")), "and misses an absent one");
        AssertTrue(TestLibFunctions.SwiftBoxSetContains(boxes, "alpha"), "Swift's own box finds the first element");
        AssertTrue(TestLibFunctions.SwiftBoxSetContains(boxes, "gamma"), "and the last");
        AssertFalse(TestLibFunctions.SwiftBoxSetContains(boxes, "delta"), "an absent value is not found");
        AssertEqual(3, boxes.Count, "three distinct boxes");
        boxes.Add(Box("alpha"));
        AssertEqual(3, boxes.Count, "an equal box is not added twice");

        using var index = IntIndex(1, 2);
        AssertEqual(11L, Long(TestLibFunctions.SwiftBoxLookUp(index, 2)), "Swift finds a present key of a bound generic dictionary");
        AssertEqual(null, Long(TestLibFunctions.SwiftBoxLookUp(index, 3)), "and misses an absent one");

        using var labels = new SwiftSet<SwiftKeyLabel<nint>>();
        labels.Add(new SwiftKeyLabel<nint>("alpha"));
        labels.Add(new SwiftKeyLabel<nint>("beta"));
        AssertTrue(TestLibFunctions.SwiftLabelSetContains(labels, "beta"), "an unconditionally Hashable generic class is found");
        AssertFalse(TestLibFunctions.SwiftLabelSetContains(labels, "gamma"), "and an absent one is not");
    }

    public void TestSwiftFindsOptionalBoxElementsAndKeysBuiltInCSharp()
    {
        var boxes = new HashSet<SwiftKeyBox<SwiftString>?> { Box("alpha"), null, Box("beta") };
        AssertTrue(TestLibFunctions.SwiftBoxOptionalSetContains(boxes, "beta"), "a present optional box is found");
        AssertTrue(TestLibFunctions.SwiftBoxOptionalSetContains(boxes, null), "and the nil element");
        AssertFalse(TestLibFunctions.SwiftBoxOptionalSetContains(boxes, "gamma"), "an absent one is not");

#pragma warning disable CS8714
        var index = new Dictionary<SwiftKeyBox<SwiftString>?, nint> { { Box("alpha"), 1 }, { Box("beta"), 2 } };
#pragma warning restore CS8714
        AssertEqual(2L, Long(TestLibFunctions.SwiftBoxOptionalLookUp(index, "beta")), "an optional box key is found");
        AssertEqual(null, Long(TestLibFunctions.SwiftBoxOptionalLookUp(index, "gamma")), "an absent one is not");
    }

    public void TestSwiftFindsBoxesOfNestedAndOptionalContainers()
    {
        using var groups = new SwiftDictionary<SwiftString, SwiftSet<SwiftKeyBox<SwiftString>>>();
        groups[new SwiftString("first")] = Boxes("alpha", "beta");
        AssertTrue(TestLibFunctions.SwiftBoxNestedContains(groups, "first", "beta"), "a nested set's element is found");
        AssertFalse(TestLibFunctions.SwiftBoxNestedContains(groups, "first", "gamma"), "an absent one is not");

        using var list = new SwiftArray<SwiftSet<SwiftKeyBox<SwiftString>>>();
        list.Add(Boxes("alpha"));
        list.Add(Boxes("beta", "gamma"));
        AssertTrue(TestLibFunctions.SwiftBoxArrayContains(list, 1, "gamma"), "an array's set element is found");
        AssertFalse(TestLibFunctions.SwiftBoxArrayContains(list, 0, "gamma"), "and not in the other set");

        using var some = SwiftOptional<SwiftSet<SwiftKeyBox<SwiftString>>>.NewSome(Boxes("alpha", "beta"));
        AssertEqual((nint)1, TestLibFunctions.SwiftBoxOptionalContainerContains(some, "beta"), "an optional set's element is found");
        using var none = SwiftOptional<SwiftSet<SwiftKeyBox<SwiftString>>>.NewNone();
        AssertEqual((nint)(-1), TestLibFunctions.SwiftBoxOptionalContainerContains(none, "beta"), "a nil set crosses as nil");
    }

    // ── Swift builds, C# looks up ───────────────────────────────────────────────────────────

    public void TestCSharpFindsBoxesInCollectionsSwiftReturned()
    {
        using var boxes = TestLibFunctions.SwiftBoxMakeSet(new[] { "alpha", "beta" });
        AssertEqual(2, boxes.Count, "both boxes come back");
        AssertTrue(boxes.Contains(Box("beta")), "C# finds a present box with one it made");
        AssertFalse(boxes.Contains(Box("gamma")), "and misses an absent one");
        AssertEqual("alpha,beta", string.Join(",", boxes.Select(ValueOf).OrderBy(s => s, StringComparer.Ordinal)), "the elements read back");

        using var index = TestLibFunctions.SwiftBoxMakeIndex(new nint[] { 5, 7 });
        AssertTrue(index.TryGetValue(new SwiftKeyBox<nint>(7), out var position), "C# finds a present key");
        AssertEqual((nint)1, position, "with its value");

        using var labels = TestLibFunctions.SwiftLabelMakeSet(new[] { "alpha", "beta" });
        AssertTrue(labels.Contains(new SwiftKeyLabel<nint>("alpha")), "C# finds a present label");
    }

    public void TestCSharpFindsOptionalBoxesInASetSwiftReturned()
    {
        var optional = TestLibFunctions.SwiftBoxMakeOptionalSet(new[] { "alpha", null, "beta" });
        try
        {
            AssertEqual(3, optional.Count, "the nil element is one of them");
            AssertTrue(optional.Contains(null), "the nil element is found");
            AssertTrue(optional.Contains(Box("beta")), "and a present box, by Swift's equality");
            AssertFalse(optional.Contains(Box("gamma")), "but not an absent one");
            AssertEqual("<nil>,alpha,beta",
                string.Join(",", optional.Select(b => b is null ? "<nil>" : ValueOf(b)).OrderBy(s => s, StringComparer.Ordinal)),
                "and every element reads back");
        }
        finally
        {
            (optional as IDisposable)?.Dispose();
        }
    }

    // ── Reverse dispatch ───────────────────────────────────────────────────────────────────

    sealed class BoxSource : ISwiftKeyBoxSource
    {
        public SwiftSet<SwiftKeyBox<SwiftString>> GetBoxes() => Boxes("alpha", "beta");

        public nint Position(SwiftSet<SwiftKeyBox<SwiftString>> boxes, string value)
            => boxes.Contains(Box(value)) ? boxes.Count : -1;

        public nint OptionalPosition(SwiftOptional<SwiftSet<SwiftKeyBox<SwiftString>>> boxes, string value)
        {
            if (!boxes.HasValue)
                return -2;
            using var set = boxes.Value!;
            return set.Contains(Box(value)) ? set.Count : -1;
        }
    }

    public void TestBoxReverseDispatchBothDirections()
    {
        var source = new BoxSource();
        AssertTrue(TestLibFunctions.SwiftBoxContainsFromSource(source, "beta"), "Swift finds a box in the set C# returned");
        AssertFalse(TestLibFunctions.SwiftBoxContainsFromSource(source, "gamma"), "and misses an absent one");
        AssertEqual((nint)3, TestLibFunctions.SwiftBoxAskSource(source, new[] { "alpha", "beta", "gamma" }, "gamma"),
            "C# finds a box of the set Swift handed it");
        AssertEqual((nint)(-1), TestLibFunctions.SwiftBoxAskSource(source, new[] { "alpha" }, "gamma"), "and misses an absent one");
        AssertEqual((nint)2, TestLibFunctions.SwiftBoxAskSourceOptional(source, new[] { "alpha", "beta" }, "beta"),
            "C# finds a box of the optional set Swift handed it");
        AssertEqual((nint)(-1), TestLibFunctions.SwiftBoxAskSourceOptional(source, new[] { "alpha" }, "beta"), "and misses an absent one");
        AssertEqual((nint)(-2), TestLibFunctions.SwiftBoxAskSourceOptional(source, null, "beta"), "and reads Swift's nil set as nil");
    }

    // ── Ownership ───────────────────────────────────────────────────────────────────────────

    public void TestSwiftKeepsTheBoxesOfCollectionsCSharpBuilt()
    {
        using var store = new SwiftKeyBoxStore();
        KeepFresh(store);
        Collect();
        AssertEqual("alpha,beta,gamma", store.GetKeptValues(), "the kept boxes still read back");
        AssertEqual("1=10,2=11", store.GetKeptIndexEntries(), "and so do the kept dictionary keys");
        AssertEqual("beta,nil", store.GetKeptOptionalValues(), "and the kept optional boxes");
        AssertTrue(store.Contains("beta"), "and are still found by Swift's own box");
        store.Clear();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void KeepFresh(SwiftKeyBoxStore store)
    {
        var boxes = new[] { Box("alpha"), Box("beta"), Box("gamma") };
        using (var set = new SwiftSet<SwiftKeyBox<SwiftString>>())
        {
            foreach (var box in boxes)
                set.Add(box);
            store.Keep(set);
        }
        using (var index = IntIndex(1, 2))
            store.KeepIndex(index);
        var optional = new HashSet<SwiftKeyBox<SwiftString>?> { Box("beta"), null };
        store.KeepOptional(optional);
        foreach (var box in boxes)
            box.Dispose();
        foreach (var box in optional)
            box?.Dispose();
    }

    public void TestPropertySetterBoxSetIsRetained()
    {
        using var store = new SwiftKeyBoxStore();
        SetFresh(store);
        Collect();
        AssertEqual("alpha,beta", store.GetKeptValues(), "the boxes set through the property still read back");
        store.Clear();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void SetFresh(SwiftKeyBoxStore store)
    {
        using var set = Boxes("alpha", "beta");
        store.Boxes = set;
    }

    /// <summary>
    /// A set C# builds holds exactly one reference per box while Swift keeps it, and none once Swift
    /// lets it go, as an array of the class does (the control); the box Swift keeps is the very object
    /// C# handed over.
    /// </summary>
    public void TestBoxesOfASetCSharpBuiltAreRetainedOnceAndReleased()
    {
        using var box = Box("alpha");
        nint baseline = TestLibFunctions.SwiftBoxRetainCount(box);

        using (var control = new SwiftKeyBoxArrayStore())
        {
            using var array = new SwiftArray<SwiftKeyBox<SwiftString>>();
            array.Add(box);
            control.Keep(array);
            array.Dispose();
            AssertEqual(baseline + 1, TestLibFunctions.SwiftBoxRetainCount(box), "control: the array Swift keeps holds one reference");
            control.Clear();
            AssertEqual(baseline, TestLibFunctions.SwiftBoxRetainCount(box), "control: clearing it releases that reference");
        }

        using var store = new SwiftKeyBoxStore();
        using (var set = new SwiftSet<SwiftKeyBox<SwiftString>>())
        {
            set.Add(box);
            store.Keep(set);
        }
        AssertEqual(baseline + 1, TestLibFunctions.SwiftBoxRetainCount(box), "the set Swift keeps holds one reference on its box");
        AssertTrue(store.KeepsObject(box), "and it is the object C# handed over");
        store.Clear();
        AssertEqual(baseline, TestLibFunctions.SwiftBoxRetainCount(box), "clearing the set releases it");

        store.KeepOptional(new HashSet<SwiftKeyBox<SwiftString>?> { box, null });
        AssertEqual(baseline + 1, TestLibFunctions.SwiftBoxRetainCount(box), "the optional set Swift keeps holds one reference on its box");
        store.Clear();
        AssertEqual(baseline, TestLibFunctions.SwiftBoxRetainCount(box), "clearing it releases that reference");

        for (int i = 0; i < 25; i++)
            AssertTrue(TestLibFunctions.SwiftBoxOptionalSetContains(new HashSet<SwiftKeyBox<SwiftString>?> { box }, "alpha"), "the optional box is found");
        AssertEqual(baseline, TestLibFunctions.SwiftBoxRetainCount(box), "25 calls that only read the box leave its retain count where it was");
    }

    /// <summary>The boxes C# reads from a set Swift keeps are the objects Swift holds, and outlive the set.</summary>
    public void TestBoxesReadBackAreTheSameObjectsAndOutliveTheirSet()
    {
        using var box = Box("alpha");
        using var store = new SwiftKeyBoxStore();
        using (var set = new SwiftSet<SwiftKeyBox<SwiftString>>())
        {
            set.Add(box);
            store.Keep(set);
        }
        SwiftKeyBox<SwiftString> element;
        using (var read = store.Boxes)
            element = read.Single();
        AssertTrue(store.KeepsObject(element), "the box read back is the object Swift keeps");
        store.Clear();
        Collect();
        AssertEqual("alpha", ValueOf(element), "and outlives the set it was read from");
        element.Dispose();
    }
}

/// <summary>
/// A nil optional key: <c>nil</c> is a key like any other in <c>[Node?: V]</c> and <c>[NSUUID?: V]</c>, and
/// an element like any other in <c>Set&lt;Node?&gt;</c>, in every direction: a parameter, a return, a
/// property, a closure argument, a conformer's argument and return, and a nested dictionary.
/// </summary>
public class NilOptionalKeyedCollectionsTests : TestBase
{
    public NilOptionalKeyedCollectionsTests(TestResults results) : base(results) { }

    const string First = "6F9619FF-8B86-D011-B42D-00C04FC964FF";
    const string Second = "0B2D7C4E-5A1F-4C3B-9E8D-7F6A5B4C3D2E";

    static long? Long(nint? value) => value is { } v ? (long)v : null;

    static string Entries<TKey>(IEnumerable<KeyValuePair<TKey, nint>> index, Func<TKey, string> name)
        => string.Join(",", index.Select(kv => $"{(kv.Key is null ? "nil" : name(kv.Key))}={kv.Value}").OrderBy(s => s, StringComparer.Ordinal));

    static string NodeName(SwiftKeyNode node) => node.Name.ToString();

    static string UuidName(Foundation.NSUuid uuid) => uuid.AsString() == First ? "first" : uuid.AsString() == Second ? "second" : uuid.AsString();

    /// <summary>A dictionary C# builds whose nil key Swift sees.</summary>
    static NilKeyDictionary<SwiftKeyNode?, nint> NodeIndex()
    {
        var index = new NilKeyDictionary<SwiftKeyNode?, nint>();
        index[new SwiftKeyNode("alpha")] = 1;
        index[null] = 2;
        return index;
    }

    public void TestSwiftSeesTheNilKeyOfADictionaryCSharpBuilt()
    {
        var index = NodeIndex();
        AssertEqual("alpha=1,nil=2", TestLibFunctions.SwiftKeyOptionalIndexEntries(index), "Swift sees the nil key and its value");
        AssertEqual(2L, Long(TestLibFunctions.SwiftKeyOptionalLookUp(index, null)), "Swift finds the nil key");
        AssertEqual(1L, Long(TestLibFunctions.SwiftKeyOptionalLookUp(index, "alpha")), "and the other key");

        var uuids = new NilKeyDictionary<Foundation.NSUuid?, nint>();
        uuids[new Foundation.NSUuid(First)] = 1;
        uuids[null] = 2;
        AssertEqual(2L, Long(TestLibFunctions.SwiftUUIDOptionalLookUp(uuids, null)), "Swift finds the nil UUID key");
        AssertEqual(1L, Long(TestLibFunctions.SwiftUUIDOptionalLookUp(uuids, First)), "and the other key");
    }

    public void TestCSharpReadsTheNilKeyOfADictionarySwiftReturned()
    {
        var index = TestLibFunctions.SwiftKeyMakeOptionalIndex(new[] { "alpha", null, "beta" });
        try
        {
            AssertEqual(3, index.Count, "every entry comes back");
            AssertEqual("alpha=0,beta=2,nil=1", Entries(index, NodeName), "the nil key reads back as null");
            AssertTrue(index.TryGetValue(null!, out var value) && value == 1, "and is found");
        }
        finally
        {
            (index as IDisposable)?.Dispose();
        }

        var uuids = TestLibFunctions.SwiftUUIDMakeOptionalIndex(new[] { First, null });
        try
        {
            AssertEqual("first=0,nil=1", Entries(uuids, UuidName), "the nil UUID key reads back as null");
        }
        finally
        {
            (uuids as IDisposable)?.Dispose();
        }
    }

    public void TestCSharpReadsTheNilKeysOfNestedDictionariesSwiftReturned()
    {
        var list = TestLibFunctions.SwiftKeyMakeOptionalIndexList(new[] { "alpha", null });
        try
        {
            AssertEqual(2, list.Count, "both dictionaries come back");
            AssertEqual("alpha=0", Entries(list[0], NodeName), "the first holds its node");
            AssertEqual("nil=1", Entries(list[1], NodeName), "the second its nil key");
            AssertTrue(list[1].ContainsKey(null!), "which is found");
        }
        finally
        {
            (list as IDisposable)?.Dispose();
        }

        var uuids = TestLibFunctions.SwiftUUIDMakeOptionalIndexList(new[] { null, Second });
        try
        {
            AssertEqual("nil=0", Entries(uuids[0], UuidName), "a nested nil UUID key reads back");
            AssertEqual("second=1", Entries(uuids[1], UuidName), "beside its sibling");
        }
        finally
        {
            (uuids as IDisposable)?.Dispose();
        }
    }

    public void TestAPropertyKeepsTheNilKey()
    {
        using var store = new SwiftKeyOptionalIndexStore();
        store.Index = NodeIndex();
        AssertEqual("alpha=1,nil=2", store.GetEntries(), "the property's setter hands Swift the nil key");
        var read = store.Index;
        try
        {
            AssertEqual("alpha=1,nil=2", Entries(read, NodeName), "and its getter reads it back");
        }
        finally
        {
            (read as IDisposable)?.Dispose();
        }
    }

    public void TestClosuresSeeTheNilKey()
    {
        nint value = TestLibFunctions.SwiftKeyCallWithOptionalIndex(new[] { "alpha", null }, index =>
            index.Where(kv => kv.Key.Handle == IntPtr.Zero).Select(kv => kv.Value).DefaultIfEmpty(-1).Single());
        AssertEqual((nint)1, value, "the closure sees the nil key of the dictionary Swift handed it");

        nint count = TestLibFunctions.SwiftKeyCallWithOptionalSet(new[] { "alpha", null, "beta" }, nodes =>
            nodes.Count(n => n.Handle == IntPtr.Zero) == 1 ? nodes.Count : -1);
        AssertEqual((nint)3, count, "and the nil element of the set");

        nint uuid = TestLibFunctions.SwiftUUIDCallWithOptionalIndex(new[] { First, null }, index =>
            index.Where(kv => kv.Key.Handle == IntPtr.Zero).Select(kv => kv.Value).DefaultIfEmpty(-1).Single());
        AssertEqual((nint)1, uuid, "and the nil UUID key");
    }

    sealed class NodeSource : ISwiftKeyOptionalSource
    {
        public IReadOnlyDictionary<SwiftKeyNode?, nint> GetOptionalIndex() => NodeIndex();

        public nint Value(IDictionary<SwiftKeyNode?, nint> index, string? name)
        {
            if (name is null)
                return index.TryGetValue(null!, out var nilValue) ? nilValue : -1;
            using var probe = new SwiftKeyNode(name);
            return index.TryGetValue(probe, out var value) ? value : -1;
        }

        public nint Count(IReadOnlySet<SwiftKeyNode?> nodes, string? name)
        {
            if (name is null)
                return nodes.Contains(null) ? nodes.Count : -1;
            using var probe = new SwiftKeyNode(name);
            return nodes.Contains(probe) ? nodes.Count : -1;
        }
    }

    sealed class UuidSource : ISwiftUUIDOptionalSource
    {
        public IReadOnlyDictionary<Foundation.NSUuid?, nint> GetOptionalIndex()
        {
            var index = new NilKeyDictionary<Foundation.NSUuid?, nint>();
            index[new Foundation.NSUuid(First)] = 1;
            index[null] = 2;
            return index;
        }

        public nint Value(IDictionary<Foundation.NSUuid?, nint> index, string? uuid)
            => index.TryGetValue(uuid is null ? null! : new Foundation.NSUuid(uuid), out var value) ? value : -1;
    }

    public void TestReverseDispatchCarriesTheNilKeyBothWays()
    {
        var source = new NodeSource();
        AssertEqual(2L, Long(TestLibFunctions.SwiftKeyOptionalLookUpFromSource(source, null)), "Swift finds the nil key of the dictionary C# returned");
        AssertEqual(1L, Long(TestLibFunctions.SwiftKeyOptionalLookUpFromSource(source, "alpha")), "and its other key");
        AssertEqual((nint)1, TestLibFunctions.SwiftKeyAskOptionalSource(source, new[] { "alpha", null }, null), "C# finds the nil key of the dictionary Swift handed it");
        AssertEqual((nint)0, TestLibFunctions.SwiftKeyAskOptionalSource(source, new[] { "alpha", null }, "alpha"), "and its other key");
        AssertEqual((nint)3, TestLibFunctions.SwiftKeyAskOptionalSetSource(source, new[] { "alpha", null, "beta" }, null), "C# finds the nil element of the set Swift handed it");
        AssertEqual((nint)3, TestLibFunctions.SwiftKeyAskOptionalSetSource(source, new[] { "alpha", null, "beta" }, "beta"), "and a present node, by Swift's equality");

        var uuids = new UuidSource();
        AssertEqual(2L, Long(TestLibFunctions.SwiftUUIDOptionalLookUpFromSource(uuids, null)), "Swift finds the nil UUID key C# returned");
        AssertEqual((nint)1, TestLibFunctions.SwiftUUIDAskOptionalSource(uuids, new[] { First, null }, null), "C# finds the nil UUID key Swift handed it");
        AssertEqual((nint)0, TestLibFunctions.SwiftUUIDAskOptionalSource(uuids, new[] { First, null }, First), "and its other key");
    }
}
