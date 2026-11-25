// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Runtime.CompilerServices;
using RuntimeTestsApp.Infrastructure;
using Swift;
using SwiftBindingsTestLib;

// The binding takes an IDictionary<SwiftKeyNode?, nint> for [SwiftKeyNode?: Int]; a .NET Dictionary
// over a nullable key type is legal (it refuses only a null key at run time).
#pragma warning disable CS8714

namespace RuntimeTestsApp.Collections;

/// <summary>
/// Collections hashed by a Swift class: a set of a Hashable Swift class, a dictionary keyed by one,
/// and their optional, nested, subclass and NSObject-rooted forms.
/// </summary>
/// <remarks>
/// A collection C# builds must be one over the class itself: hashed and compared through the
/// class's own Hashable conformance, so a Swift lookup with a node Swift makes finds a present key,
/// and holding a reference on every key, so a collection Swift keeps reads live objects after C#
/// lets go of them. A count cannot show either; a lookup, a retain count and a read after
/// collection can.
/// </remarks>
public class SwiftClassKeyedCollectionsTests : TestBase
{
    public SwiftClassKeyedCollectionsTests(TestResults results) : base(results) { }

    static long? Long(nint? value) => value is { } v ? (long)v : null;

    static HashSet<SwiftKeyNode> Nodes(params string[] names) => names.Select(n => new SwiftKeyNode(n)).ToHashSet();

    static Dictionary<SwiftKeyNode, nint> Index(params string[] names)
    {
        var result = new Dictionary<SwiftKeyNode, nint>();
        for (int i = 0; i < names.Length; i++)
            result[new SwiftKeyNode(names[i])] = 10 + i;
        return result;
    }

    static string NameOf(SwiftKeyNode node) => node.Name.ToString();

    static void Collect()
    {
        for (int i = 0; i < 4; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
    }

    // ── C# builds, Swift looks up ───────────────────────────────────────────────────────────

    public void TestSwiftFindsElementsOfASetBuiltInCSharp()
    {
        var nodes = Nodes("alpha", "beta", "gamma");
        AssertTrue(TestLibFunctions.SwiftKeySetContains(nodes, "alpha"), "Swift's own node finds the first element");
        AssertTrue(TestLibFunctions.SwiftKeySetContains(nodes, "gamma"), "and the last");
        AssertFalse(TestLibFunctions.SwiftKeySetContains(nodes, "delta"), "an absent name is not found");
    }

    public void TestSwiftFindsKeysOfADictionaryBuiltInCSharp()
    {
        var index = Index("alpha", "beta", "gamma");
        AssertEqual(10L, Long(TestLibFunctions.SwiftKeyLookUp(index, "alpha")), "first key found");
        AssertEqual(12L, Long(TestLibFunctions.SwiftKeyLookUp(index, "gamma")), "last key found");
        AssertEqual(null, Long(TestLibFunctions.SwiftKeyLookUp(index, "delta")), "absent key not found");
    }

    public void TestSwiftFindsOptionalElementsAndKeysBuiltInCSharp()
    {
        var nodes = new HashSet<SwiftKeyNode?> { new SwiftKeyNode("alpha"), null, new SwiftKeyNode("beta") };
        AssertTrue(TestLibFunctions.SwiftKeyOptionalSetContains(nodes, "beta"), "a present optional node is found");
        AssertTrue(TestLibFunctions.SwiftKeyOptionalSetContains(nodes, null), "and the nil element");
        AssertFalse(TestLibFunctions.SwiftKeyOptionalSetContains(nodes, "gamma"), "an absent one is not");

        var index = new Dictionary<SwiftKeyNode?, nint> { { new SwiftKeyNode("alpha"), 1 }, { new SwiftKeyNode("beta"), 2 } };
        AssertEqual(2L, Long(TestLibFunctions.SwiftKeyOptionalLookUp(index, "beta")), "an optional key is found");
        AssertEqual(null, Long(TestLibFunctions.SwiftKeyOptionalLookUp(index, "gamma")), "an absent one is not");
        AssertEqual(null, Long(TestLibFunctions.SwiftKeyOptionalLookUp(index, null)), "nor nil, which C# did not store");
    }

    public void TestSwiftFindsKeysOfNestedAndOptionalContainers()
    {
        var groups = new Dictionary<string, IReadOnlySet<SwiftKeyNode>>
        {
            { "first", Nodes("alpha", "beta") },
            { "second", Nodes("gamma") },
        };
        AssertTrue(TestLibFunctions.SwiftKeyNestedContains(groups, "first", "beta"), "an inner set's element is found");
        AssertTrue(TestLibFunctions.SwiftKeyNestedContains(groups, "second", "gamma"), "and the other inner set's");
        AssertFalse(TestLibFunctions.SwiftKeyNestedContains(groups, "second", "alpha"), "an element of the other set is absent");

        var list = new List<IReadOnlySet<SwiftKeyNode>> { Nodes("alpha"), Nodes("beta", "gamma") };
        AssertTrue(TestLibFunctions.SwiftKeyArrayContains(list, 1, "gamma"), "an element of the array's second set is found");
        AssertFalse(TestLibFunctions.SwiftKeyArrayContains(list, 0, "gamma"), "but not in the first");

        AssertEqual((nint)1, TestLibFunctions.SwiftKeyOptionalContainerContains(Nodes("alpha", "beta"), "beta"), "an optional set's element is found");
        AssertEqual((nint)(-1), TestLibFunctions.SwiftKeyOptionalContainerContains(null, "beta"), "a nil set crosses as nil");
    }

    public void TestSwiftFindsSubclassAndNSObjectRootedKeys()
    {
        var derived = new[] { "alpha", "beta" }.Select(n => new SwiftKeyDerivedNode(n)).ToHashSet();
        AssertTrue(TestLibFunctions.SwiftKeyDerivedSetContains(derived, "beta"), "a subclass hashed through its parent is found");
        AssertFalse(TestLibFunctions.SwiftKeyDerivedSetContains(derived, "gamma"), "an absent one is not");

        var rooted = new Dictionary<SwiftKeyRootedNode, nint> { { new SwiftKeyRootedNode("alpha"), 4 }, { new SwiftKeyRootedNode("beta"), 5 } };
        AssertEqual(5L, Long(TestLibFunctions.SwiftKeyRootedLookUp(rooted, "beta")), "an NSObject-rooted key is found");
        AssertEqual(null, Long(TestLibFunctions.SwiftKeyRootedLookUp(rooted, "gamma")), "an absent one is not");
    }

    // ── Swift builds, C# looks up ───────────────────────────────────────────────────────────

    public void TestCSharpFindsElementsAndKeysSwiftReturned()
    {
        var nodes = TestLibFunctions.SwiftKeyMakeSet(new[] { "alpha", "beta" });
        try
        {
            AssertEqual(2, nodes.Count, "both elements come back");
            using var beta = new SwiftKeyNode("beta");
            using var gamma = new SwiftKeyNode("gamma");
            AssertTrue(nodes.Contains(beta), "C#'s own equal node is found");
            AssertFalse(nodes.Contains(gamma), "an absent one is not");
            AssertEqual("alpha,beta", string.Join(",", nodes.Select(NameOf).OrderBy(s => s, StringComparer.Ordinal)), "the elements read back");
        }
        finally
        {
            (nodes as IDisposable)?.Dispose();
        }

        var index = TestLibFunctions.SwiftKeyMakeIndex(new[] { "alpha", "beta" });
        try
        {
            using var beta = new SwiftKeyNode("beta");
            AssertTrue(index.TryGetValue(beta, out var position), "an equal node finds its entry");
            AssertEqual((nint)1, position, "and reads its value");
            AssertEqual("alpha,beta", string.Join(",", index.Keys.Select(NameOf).OrderBy(s => s, StringComparer.Ordinal)), "the keys read back");
        }
        finally
        {
            (index as IDisposable)?.Dispose();
        }
    }

    public void TestCSharpReadsOptionalSubclassAndRootedCollectionsSwiftReturned()
    {
        var optional = TestLibFunctions.SwiftKeyMakeOptionalSet(new[] { "alpha", null, "beta" });
        try
        {
            AssertEqual(3, optional.Count, "the nil element is one of them");
            AssertEqual("<nil>,alpha,beta",
                string.Join(",", optional.Select(n => n is null ? "<nil>" : NameOf(n)).OrderBy(s => s, StringComparer.Ordinal)),
                "every element reads back");
        }
        finally
        {
            (optional as IDisposable)?.Dispose();
        }

        var derived = TestLibFunctions.SwiftKeyMakeDerivedSet(new[] { "alpha", "beta" });
        try
        {
            using var alpha = new SwiftKeyDerivedNode("alpha");
            AssertTrue(derived.Contains(alpha), "C# finds a subclass element by its parent's hash");
        }
        finally
        {
            (derived as IDisposable)?.Dispose();
        }

        var rooted = TestLibFunctions.SwiftKeyMakeRootedIndex(new[] { "alpha", "beta" });
        try
        {
            AssertEqual(2, rooted.Count, "both rooted keys come back");
            AssertEqual("alpha,beta", string.Join(",", rooted.Keys.Select(k => k.Name.ToString()).OrderBy(s => s, StringComparer.Ordinal)), "and read back");
        }
        finally
        {
            (rooted as IDisposable)?.Dispose();
        }
    }

    // ── Closures and reverse dispatch ───────────────────────────────────────────────────────

    public void TestClosuresReadTheCollectionsSwiftHandsThem()
    {
        nint count = TestLibFunctions.SwiftKeyCallWithSet(new[] { "alpha", "beta", "gamma" }, nodes =>
            (nint)nodes.Count(n => NameOf(n) is "beta" or "gamma"));
        AssertEqual((nint)2, count, "the closure reads every element of the set Swift handed it");

        nint value = TestLibFunctions.SwiftKeyCallWithIndex(new[] { "alpha", "beta" }, index =>
        {
            using var beta = new SwiftKeyNode("beta");
            return index.TryGetValue(beta, out var position) ? position : -1;
        });
        AssertEqual((nint)1, value, "the closure finds a present key in the dictionary Swift handed it");
    }

    sealed class NodeSource : ISwiftKeyNodeSource
    {
        public IReadOnlyDictionary<SwiftKeyNode, nint> GetIndex() => Index("alpha", "beta");

        public nint Position(IReadOnlySet<SwiftKeyNode> nodes, string name)
        {
            using var probe = new SwiftKeyNode(name);
            return nodes.Contains(probe) ? nodes.Count : -1;
        }
    }

    public void TestReverseDispatchBothDirections()
    {
        var source = new NodeSource();
        AssertEqual(11L, Long(TestLibFunctions.SwiftKeyLookUpFromSource(source, "beta")), "Swift finds a key in the dictionary C# returned");
        AssertEqual(null, Long(TestLibFunctions.SwiftKeyLookUpFromSource(source, "gamma")), "and misses an absent one");
        AssertEqual((nint)3, TestLibFunctions.SwiftKeyAskSource(source, new[] { "alpha", "beta", "gamma" }, "gamma"),
            "C# finds an element of the set Swift handed it");
        AssertEqual((nint)(-1), TestLibFunctions.SwiftKeyAskSource(source, new[] { "alpha" }, "gamma"), "and misses an absent one");
    }

    // ── Ownership ───────────────────────────────────────────────────────────────────────────

    /// <summary>A set Swift keeps holds its own reference on every element: after C# drops them, they still read back.</summary>
    public void TestSwiftKeepsTheElementsOfASetCSharpBuilt()
    {
        using var store = new SwiftKeyStore();
        KeepFresh(store);
        Collect();
        AssertEqual("alpha,beta,gamma", store.GetKeptNames(), "the kept elements still read back");
        AssertEqual("alpha=10,beta=11", store.GetKeptIndexEntries(), "and so do the kept dictionary keys");
        AssertEqual("beta,nil", store.GetKeptOptionalNames(), "and the kept optional elements");
        AssertEqual("alpha=1", store.GetKeptOptionalIndexEntries(), "and the kept optional keys");
        AssertTrue(store.Contains("beta"), "and are still found by Swift's own node");
        store.Clear();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void KeepFresh(SwiftKeyStore store)
    {
        var nodes = Nodes("alpha", "beta", "gamma");
        store.Keep(nodes);
        var index = Index("alpha", "beta");
        store.KeepIndex(index);
        var optional = new HashSet<SwiftKeyNode?> { new SwiftKeyNode("beta"), null };
        store.KeepOptional(optional);
        var optionalIndex = new Dictionary<SwiftKeyNode?, nint> { { new SwiftKeyNode("alpha"), 1 } };
        store.KeepOptionalIndex(optionalIndex);
        foreach (var node in nodes)
            node.Dispose();
        foreach (var node in index.Keys)
            node.Dispose();
        foreach (var node in optional)
            node?.Dispose();
        foreach (var node in optionalIndex.Keys)
            node?.Dispose();
    }

    /// <summary>A set set through a property is Swift's too: its elements outlive C#'s references.</summary>
    public void TestPropertySetterSetIsRetained()
    {
        using var store = new SwiftKeyStore();
        SetFresh(store);
        Collect();
        AssertEqual("alpha,beta", store.GetKeptNames(), "the elements set through the property still read back");
        store.Clear();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void SetFresh(SwiftKeyStore store)
    {
        var nodes = Nodes("alpha", "beta");
        store.Nodes = nodes;
        foreach (var node in nodes)
            node.Dispose();
    }

    /// <summary>
    /// A set C# builds holds exactly one reference per element while Swift keeps it, and none once
    /// Swift lets it go, as an array of the class does (the control); the element Swift keeps is the
    /// very object C# handed over.
    /// </summary>
    public void TestElementsOfASetCSharpBuiltAreRetainedOnceAndReleased()
    {
        using var node = new SwiftKeyNode("alpha");
        nint baseline = TestLibFunctions.SwiftKeyRetainCount(node);

        using (var control = new SwiftKeyArrayStore())
        {
            control.Keep(new[] { node });
            AssertEqual(baseline + 1, TestLibFunctions.SwiftKeyRetainCount(node), "control: the array Swift keeps holds one reference");
            control.Clear();
            AssertEqual(baseline, TestLibFunctions.SwiftKeyRetainCount(node), "control: clearing it releases that reference");
        }

        using var store = new SwiftKeyStore();
        store.Keep(new HashSet<SwiftKeyNode> { node });
        AssertEqual(baseline + 1, TestLibFunctions.SwiftKeyRetainCount(node), "the set Swift keeps holds one reference on its element");
        AssertTrue(store.KeepsObject(node), "and it is the object C# handed over");
        store.Clear();
        AssertEqual(baseline, TestLibFunctions.SwiftKeyRetainCount(node), "clearing the set releases it");

        for (int i = 0; i < 25; i++)
        {
            AssertEqual((long)(10 + i), Long(TestLibFunctions.SwiftKeyLookUp(new Dictionary<SwiftKeyNode, nint> { { node, 10 + i } }, "alpha")), "the key is found");
            AssertTrue(TestLibFunctions.SwiftKeySetContains(new HashSet<SwiftKeyNode> { node }, "alpha"), "the element is found");
        }
        AssertEqual(baseline, TestLibFunctions.SwiftKeyRetainCount(node), "25 calls that only read the key leave its retain count where it was");
    }

    /// <summary>The elements C# reads from a set Swift keeps are the objects Swift holds, and outlive the set.</summary>
    public void TestElementsReadBackAreTheSameObjectsAndOutliveTheirSet()
    {
        using var node = new SwiftKeyNode("alpha");
        using var store = new SwiftKeyStore();
        store.Keep(new HashSet<SwiftKeyNode> { node });
        var read = store.Nodes;
        SwiftKeyNode element;
        try
        {
            element = read.Single();
        }
        finally
        {
            (read as IDisposable)?.Dispose();
        }
        AssertTrue(store.KeepsObject(element), "the element read back is the object Swift keeps");
        store.Clear();
        Collect();
        AssertEqual("alpha", NameOf(element), "and outlives the set it was read from");
        element.Dispose();
    }

    // ── A container whose sibling argument has no projection ────────────────────────────────

    public void TestDictionaryOfResultsSwiftReturnedReadsEachValue()
    {
        const string first = "6F9619FF-8B86-D011-B42D-00C04FC964FF";
        const string second = "0B2D7C4E-5A1F-4C3B-9E8D-7F6A5B4C3D2E";
        var results = TestLibFunctions.SwiftKeyMakeResults(new[] { first, second });
        try
        {
            AssertEqual(2, results.Count, "both entries come back");
            var byKey = results.ToDictionary(kv => kv.Key.AsString(), kv => kv.Value);
            AssertTrue(byKey[first].IsSuccess, "the first value is a success");
            AssertEqual((nint)7, byKey[first].Success, "of 7");
            AssertFalse(byKey[second].IsSuccess, "the second is a failure");
        }
        finally
        {
            (results as IDisposable)?.Dispose();
        }
    }
}
