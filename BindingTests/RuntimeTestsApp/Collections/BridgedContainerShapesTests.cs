// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Diagnostics;
using Foundation;
using RuntimeTestsApp.Infrastructure;
using Swift;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Collections;

/// <summary>
/// Containers that cross to .NET as the Objective-C collections they bridge to, and the keys inside
/// them: a C# conformer Swift hands a container of URLs (as an argument, a property's new value or a
/// subscript index) and asks for one back; a dictionary keyed by a Swift class, pure Swift or
/// NSObject-rooted, beside a URL value; and String keys, which Swift compares by canonical
/// equivalence rather than by their UTF-16.
/// </summary>
public class BridgedContainerShapesTests : TestBase
{
    public BridgedContainerShapesTests(TestResults results) : base(results) { }

    static string Url(string name) => $"https://example.com/{name}";

    static string Url(nint position) => $"https://example.com/{position}";

    static NSUrl NSUrlOf(string name) => new(Url(name));

    static string Join(IEnumerable<NSUrl> urls) => string.Join(",", urls.Select(u => u.AbsoluteString));

    static string Names(IEnumerable<NSUrl> urls) => string.Join(",", urls.Select(u => u.LastPathComponent));

    // ── A conformer Swift hands containers of URLs ────────────────────────────────────────

    sealed class UrlSink : IURLContainerSink
    {
        public IReadOnlyList<NSUrl> StoredURLs { get; set; } = Array.Empty<NSUrl>();

        public nint this[IEnumerable<NSUrl> count] => count.Count(u => u.AbsoluteString!.StartsWith("https://example.com/", StringComparison.Ordinal));

        public string DescribeArray(IEnumerable<NSUrl> urls) => Names(urls);

        public string DescribeSet(IReadOnlySet<NSUrl> urls)
            => Names(urls.OrderBy(u => u.AbsoluteString, StringComparer.Ordinal))
               + (urls.Contains(NSUrlOf("a")) ? "+a" : "-a")
               + (urls.Contains(NSUrlOf("c")) ? "+c" : "-c");

        public string DescribeIndex(IDictionary<string, NSUrl> urls)
            => string.Join(";", urls.OrderBy(e => e.Key, StringComparer.Ordinal).Select(e => $"{e.Key}={e.Value.LastPathComponent}"))
               + (urls.TryGetValue("b", out var b) ? $"|b={b.LastPathComponent}" : "|no b");

        public string DescribeNames(IDictionary<NSUrl, string> names)
            => string.Join(";", names.Values.OrderBy(n => n, StringComparer.Ordinal))
               + (names.TryGetValue(NSUrlOf("b"), out var b) ? $"|{b}" : "|no b");

        public string DescribeOptionalArray(IReadOnlyList<NSUrl>? urls) => urls is null ? "nil" : Names(urls);

        public string DescribeGroups(IDictionary<string, IEnumerable<NSUrl>> groups)
            => string.Join(";", groups.OrderBy(e => e.Key, StringComparer.Ordinal).Select(e => $"{e.Key}:{Names(e.Value)}"));

        public string DescribeRows(IEnumerable<IEnumerable<NSUrl>> rows) => string.Join("|", rows.Select(Names));

        public IReadOnlyDictionary<string, IReadOnlyList<NSUrl>> MakeGroups()
            => new Dictionary<string, IReadOnlyList<NSUrl>>
            {
                ["x"] = new[] { NSUrlOf("a") },
                ["yz"] = new[] { NSUrlOf("b"), NSUrlOf("c") },
            };

        public IReadOnlyList<NSUrl>? MakeOptionalArray(bool present) => present ? new[] { NSUrlOf("a"), NSUrlOf("c") } : null;
    }

    public void TestAConformerReadsEveryURLOfAnArraySwiftHandsIt()
        => AssertEqual("a,b", TestLibFunctions.UrlSinkDescribeArray(new UrlSink()), "both URLs, in order");

    public void TestAConformerReadsASetOfURLsSwiftHandsIt()
        => AssertEqual("a,b+a-c", TestLibFunctions.UrlSinkDescribeSet(new UrlSink()), "both URLs, and a fresh, equal URL is found");

    public void TestAConformerReadsADictionaryValuedByURLs()
        => AssertEqual("a=a;b=b|b=b", TestLibFunctions.UrlSinkDescribeIndex(new UrlSink()), "every entry, and a key looked up");

    public void TestAConformerReadsADictionaryKeyedByURLs()
        => AssertEqual("a;b|b", TestLibFunctions.UrlSinkDescribeNames(new UrlSink()), "every entry, and a fresh, equal URL key is found");

    public void TestAConformerReadsAnOptionalArrayOfURLs()
    {
        var sink = new UrlSink();
        AssertEqual("c", TestLibFunctions.UrlSinkDescribeOptionalArray(sink, true), "a present array");
        AssertEqual("nil", TestLibFunctions.UrlSinkDescribeOptionalArray(sink, false), "and nil");
    }

    public void TestAConformerReadsArraysOfURLsNestedInADictionary()
        => AssertEqual("ab:a,b;c:c", TestLibFunctions.UrlSinkDescribeGroups(new UrlSink()), "every group, every URL");

    public void TestAConformerReadsArraysOfURLsNestedInAnArray()
        => AssertEqual("a|b,c", TestLibFunctions.UrlSinkDescribeRows(new UrlSink()), "every row, every URL");

    public void TestAConformerStoresAnArrayOfURLsSwiftSets()
    {
        var sink = new UrlSink();
        AssertEqual($"{Url("c")},{Url("a")}", TestLibFunctions.UrlSinkStoreAndRead(sink), "Swift reads back what it set");
        AssertEqual("c,a", Names(sink.StoredURLs), "and the conformer holds it");
    }

    public void TestAConformerReadsAnArrayOfURLsSwiftIndexesItBy()
        => AssertEqual((nint)3, TestLibFunctions.UrlSinkCount(new UrlSink()), "every URL of the index");

    public void TestSwiftReadsArraysOfURLsAConformerNests()
        => AssertEqual($"x:{Url("a")};yz:{Url("b")},{Url("c")}", TestLibFunctions.UrlSinkReadGroups(new UrlSink()), "every group Swift gets back");

    public void TestSwiftReadsAnOptionalArrayOfURLsAConformerMakes()
    {
        var sink = new UrlSink();
        AssertEqual($"{Url("a")},{Url("c")}", TestLibFunctions.UrlSinkReadOptionalArray(sink, true), "a present array");
        AssertEqual("nil", TestLibFunctions.UrlSinkReadOptionalArray(sink, false), "and nil");
    }

    /// <summary>
    /// Handing a conformer bridged containers leaves a URL the conformer keeps where it was, as one
    /// explicit retain moves it by exactly one (the control): the receiver adopts the collection
    /// Swift hands over at +1 and releases nothing it does not own.
    /// </summary>
    public void TestHandingAConformerBridgedContainersKeepsAKeptURLsRetainCount()
    {
        var sink = new UrlSink();
        TestLibFunctions.UrlSinkStoreAndRead(sink);
        var kept = sink.StoredURLs[0];
        nuint baseline = kept.RetainCount;

        kept.DangerousRetain();
        AssertEqual(baseline + 1, kept.RetainCount, "control: one retain adds one reference");
        kept.DangerousRelease();
        AssertEqual(baseline, kept.RetainCount, "control: releasing it takes that reference back");

        for (int i = 0; i < 25; i++)
        {
            using (new NSAutoreleasePool())
            {
                AssertEqual("a,b", TestLibFunctions.UrlSinkDescribeArray(sink), "the conformer reads the array");
                AssertEqual("a=a;b=b|b=b", TestLibFunctions.UrlSinkDescribeIndex(sink), "and the dictionary");
            }
        }
        AssertEqual(baseline, kept.RetainCount, "25 crossings leave the kept URL's retain count where it was");
    }

    // ── A Swift class key beside a bridged value ──────────────────────────────────────────

    static Dictionary<SwiftKeyNode, NSUrl> NodeIndex(params string[] names)
    {
        var result = new Dictionary<SwiftKeyNode, NSUrl>();
        for (int i = 0; i < names.Length; i++)
            result[new SwiftKeyNode(names[i])] = new NSUrl(Url(i));
        return result;
    }

    public void TestCSharpReadsADictionaryKeyedBySwiftClassValuedByURLs()
    {
        var index = TestLibFunctions.SwiftKeyNodeURLIndex(new[] { "a", "b" });
        AssertEqual(2, index.Count, "every entry comes back");
        AssertEqual("a,b", string.Join(",", index.Keys.Select(k => k.Name).OrderBy(n => n, StringComparer.Ordinal)), "keyed by the nodes Swift made");
        AssertTrue(index.TryGetValue(new SwiftKeyNode("b"), out var b) && b.AbsoluteString == Url(1), "a fresh, equal node finds its URL");
        AssertFalse(index.ContainsKey(new SwiftKeyNode("c")), "an absent node is not found");
    }

    public void TestSwiftLooksUpADictionaryKeyedBySwiftClassCSharpBuilt()
    {
        var index = NodeIndex("a", "b");
        AssertEqual(Url(0), TestLibFunctions.SwiftKeyNodeURLLookUp(index, "a"), "Swift finds a node it made itself");
        AssertEqual(Url(1), TestLibFunctions.SwiftKeyNodeURLLookUp(index, "b"), "and the other one");
        AssertEqual(null, TestLibFunctions.SwiftKeyNodeURLLookUp(index, "c"), "and misses an absent one");
    }

    public void TestCSharpReadsDictionariesKeyedBySwiftClassNestedInAnArray()
    {
        var list = TestLibFunctions.SwiftKeyNodeURLIndexList(new[] { "a", "b" });
        AssertEqual(2, list.Count, "one dictionary per name");
        AssertTrue(list[1].TryGetValue(new SwiftKeyNode("b"), out var b) && b.AbsoluteString == Url(1), "a fresh, equal node finds its URL");
        AssertFalse(list[1].ContainsKey(new SwiftKeyNode("a")), "a node of another dictionary is not found");
    }

    public void TestCSharpReadsAnOptionalDictionaryKeyedBySwiftClass()
    {
        AssertTrue(TestLibFunctions.SwiftKeyNodeURLIndexOrNil(null) is null, "nil reads back as null");
        var index = TestLibFunctions.SwiftKeyNodeURLIndexOrNil(new[] { "a" });
        AssertTrue(index is not null && index.TryGetValue(new SwiftKeyNode("a"), out var a) && a.AbsoluteString == Url(0), "a dictionary reads back");
    }

    sealed class NodeSink : ISwiftKeyNodeURLSink
    {
        public string Url(IDictionary<SwiftKeyNode, NSUrl> index, string name)
            => index.TryGetValue(new SwiftKeyNode(name), out var url) ? $"{index.Count}:{url.AbsoluteString}" : $"{index.Count}:missing";
    }

    public void TestAConformerReadsADictionaryKeyedBySwiftClass()
    {
        var sink = new NodeSink();
        AssertEqual($"2:{Url(1)}", TestLibFunctions.SwiftKeyNodeAskURLSink(sink, new[] { "a", "b" }, "b"), "the conformer finds a fresh, equal node");
        AssertEqual("2:missing", TestLibFunctions.SwiftKeyNodeAskURLSink(sink, new[] { "a", "b" }, "c"), "and misses an absent one");
    }

    /// <summary>
    /// Handing Swift a dictionary keyed by a Swift class leaves the key's retain count where it was,
    /// as an array Swift keeps adds exactly one reference (the control).
    /// </summary>
    public void TestCrossingADictionaryKeyedBySwiftClassKeepsTheKeysRetainCount()
    {
        using var node = new SwiftKeyNode("a");
        nint baseline = TestLibFunctions.SwiftKeyNodeRetainCount(node);

        using (var control = new SwiftKeyNodeArrayStore())
        {
            control.Keep(new[] { node });
            AssertEqual(baseline + 1, TestLibFunctions.SwiftKeyNodeRetainCount(node), "control: the array Swift keeps holds one reference");
            control.Clear();
            AssertEqual(baseline, TestLibFunctions.SwiftKeyNodeRetainCount(node), "control: clearing it releases that reference");
        }

        var index = new Dictionary<SwiftKeyNode, NSUrl> { [node] = new NSUrl(Url(0)) };
        for (int i = 0; i < 25; i++)
        {
            using (new NSAutoreleasePool())
                AssertEqual(Url(0), TestLibFunctions.SwiftKeyNodeURLLookUp(index, "a"), "Swift finds the node");
        }
        AssertEqual(baseline, TestLibFunctions.SwiftKeyNodeRetainCount(node), "25 crossings leave the node's retain count where it was");
    }

    // ── An NSObject-rooted Swift class key beside a bridged value ──────────────────────────

    public void TestCSharpReadsADictionaryKeyedByARootedSwiftClass()
    {
        var index = TestLibFunctions.SwiftKeyRootedNodeURLIndex(new[] { "a", "b" });
        AssertEqual(2, index.Count, "every entry comes back");
        AssertTrue(index.TryGetValue(new SwiftKeyRootedNode("b"), out var b) && b.AbsoluteString == Url(1), "a fresh, equal node finds its URL");
        AssertFalse(index.ContainsKey(new SwiftKeyRootedNode("c")), "an absent node is not found");
    }

    public void TestSwiftLooksUpADictionaryKeyedByARootedSwiftClassCSharpBuilt()
    {
        var index = new Dictionary<SwiftKeyRootedNode, NSUrl>
        {
            [new SwiftKeyRootedNode("a")] = new NSUrl(Url(0)),
            [new SwiftKeyRootedNode("b")] = new NSUrl(Url(1)),
        };
        AssertEqual(Url(0), TestLibFunctions.SwiftKeyRootedNodeURLLookUp(index, "a"), "Swift finds a node it made itself");
        AssertEqual(Url(1), TestLibFunctions.SwiftKeyRootedNodeURLLookUp(index, "b"), "and the other one");
        AssertEqual(null, TestLibFunctions.SwiftKeyRootedNodeURLLookUp(index, "c"), "and misses an absent one");
    }

    public void TestCSharpReadsDictionariesKeyedByARootedSwiftClassNestedInAnArray()
    {
        var list = TestLibFunctions.SwiftKeyRootedNodeURLIndexList(new[] { "a", "b" });
        AssertEqual(2, list.Count, "one dictionary per name");
        AssertTrue(list[0].TryGetValue(new SwiftKeyRootedNode("a"), out var a) && a.AbsoluteString == Url(0), "a fresh, equal node finds its URL");
        AssertFalse(list[0].ContainsKey(new SwiftKeyRootedNode("b")), "a node of another dictionary is not found");
    }

    /// <summary>
    /// Handing Swift a dictionary keyed by an NSObject-rooted Swift class leaves the key's retain
    /// count where it was, as one explicit retain moves it by exactly one (the control).
    /// </summary>
    public void TestCrossingADictionaryKeyedByARootedSwiftClassKeepsTheKeysRetainCount()
    {
        var node = new SwiftKeyRootedNode("a");
        var index = new Dictionary<SwiftKeyRootedNode, NSUrl> { [node] = new NSUrl(Url(0)) };
        nuint baseline = node.RetainCount;

        node.DangerousRetain();
        AssertEqual(baseline + 1, node.RetainCount, "control: one retain adds one reference");
        node.DangerousRelease();
        AssertEqual(baseline, node.RetainCount, "control: releasing it takes that reference back");

        for (int i = 0; i < 25; i++)
        {
            using (new NSAutoreleasePool())
                AssertEqual(Url(0), TestLibFunctions.SwiftKeyRootedNodeURLLookUp(index, "a"), "Swift finds the node");
        }
        AssertEqual(baseline, node.RetainCount, "25 crossings leave the node's retain count where it was");
    }

    // ── String keys compared as Swift compares them ─────────────────────────────────────────

    const string Precomposed = "été";      // "été", each é one scalar
    const string Decomposed = "été";     // "été", each é an e and a combining acute
    const string Kelvin = "K";                  // KELVIN SIGN, canonically "K"

    public void TestANestedStringKeyedCopyFindsACanonicallyEqualKey()
    {
        var list = TestLibFunctions.CanonicalStringIndexList(new[] { Precomposed, Kelvin, "plain" });
        AssertTrue(list[0].TryGetValue(Decomposed, out var first) && first == 0, "the decomposed spelling finds the precomposed key");
        AssertTrue(list[1].ContainsKey("K"), "an ASCII K finds the Kelvin sign");
        AssertTrue(list[2].ContainsKey("plain"), "an ASCII key finds itself");
        AssertFalse(list[0].ContainsKey("ete"), "a key without accents is another key");
        AssertFalse(list[2].ContainsKey("Plain"), "and so is another case");
    }

    public void TestANestedOptionalStringKeyedCopyFindsACanonicallyEqualKey()
    {
        var list = TestLibFunctions.CanonicalOptionalStringIndexList(new[] { Precomposed, null });
        AssertTrue(list[0].TryGetValue(Decomposed, out var first) && first == 0, "the decomposed spelling finds the precomposed key");
        AssertTrue(list[1].TryGetValue(null, out var nil) && nil == 1, "the nil key is still found");
        AssertFalse(list[1].ContainsKey(Decomposed), "and holds no other key");
    }

    public void TestANestedStringSetCopyFindsACanonicallyEqualElement()
    {
        var list = TestLibFunctions.CanonicalStringSetList(new[] { Precomposed, Kelvin });
        AssertEqual(2, list[0].Count, "both elements come back");
        AssertTrue(list[0].Contains(Decomposed), "the decomposed spelling is in the set");
        AssertTrue(list[0].Contains("K"), "and so is an ASCII K");
        AssertFalse(list[0].Contains("k"), "but not a lowercase k");
    }

    public void TestAStringSetCopyFindsACanonicallyEqualElement()
    {
        var set = TestLibFunctions.CanonicalStringSet(new[] { Precomposed });
        AssertTrue(set.Contains(Decomposed), "the decomposed spelling is in the set");
    }

    public void TestABridgedStringKeyedCopyFindsACanonicallyEqualKey()
    {
        var index = TestLibFunctions.CanonicalStringURLIndex(new[] { Precomposed, "plain" });
        AssertTrue(index.TryGetValue(Decomposed, out var url) && url.AbsoluteString == Url(0), "the decomposed spelling finds its URL");
        AssertTrue(index.ContainsKey("plain"), "an ASCII key finds itself");
    }

    sealed class StringSink : ICanonicalStringSink
    {
        public nint Value(IDictionary<string, nint> index, string key) => index.TryGetValue(key, out var value) ? value : -1;
    }

    public void TestAConformerFindsACanonicallyEqualKey()
    {
        var sink = new StringSink();
        AssertEqual((nint)0, TestLibFunctions.CanonicalStringAskSink(sink, new[] { Precomposed, "x" }, Decomposed), "the decomposed spelling finds the precomposed key");
        AssertEqual((nint)1, TestLibFunctions.CanonicalStringAskSink(sink, new[] { Precomposed, "x" }, "x"), "an ASCII key finds itself");
        AssertEqual((nint)(-1), TestLibFunctions.CanonicalStringAskSink(sink, new[] { Precomposed }, "ete"), "a key without accents is another key");
    }

    /// <summary>
    /// The cost of comparing through Swift on a large nested copy: 200 dictionaries of 100 keys each,
    /// half of them non-ASCII. Every dictionary is read once, then every key is looked up, the
    /// non-ASCII ones by their decomposed spelling (so each of those lookups asks Swift's <c>==</c>),
    /// beside the same lookups in ordinal copies. Logged for the record; the assertions only check
    /// every key is found.
    /// </summary>
    public void TestALargeNestedStringKeyedCopyFindsEveryKey()
    {
        const int count = 200, width = 100;
        var list = TestLibFunctions.CanonicalStringLargeIndexList(count, width);
        string[][] Keys(string accented) => Enumerable.Range(0, count)
            .Select(row => Enumerable.Range(0, width)
                .Select(column => column % 2 == 0 ? $"key-{row}-{column}" : $"{accented}-{row}-{column}")
                .ToArray())
            .ToArray();
        var decomposedKeys = Keys("clé");
        var precomposedKeys = Keys("clé");

        // The list reads an element from Swift each time it is indexed: read each dictionary once.
        var read = Stopwatch.StartNew();
        var rows = list.ToList();
        read.Stop();

        var lookUp = Stopwatch.StartNew();
        int found = 0;
        for (int row = 0; row < count; row++)
            for (int column = 0; column < width; column++)
                if (rows[row].TryGetValue(decomposedKeys[row][column], out var value) && value == column)
                    found++;
        lookUp.Stop();

        var ordinalRows = rows.Select(index => index.ToDictionary(e => e.Key, e => e.Value, StringComparer.Ordinal)).ToList();
        var ordinalLookUp = Stopwatch.StartNew();
        int ordinalFound = 0;
        for (int row = 0; row < count; row++)
            for (int column = 0; column < width; column++)
                if (ordinalRows[row].TryGetValue(precomposedKeys[row][column], out var value) && value == column)
                    ordinalFound++;
        ordinalLookUp.Stop();

        Console.WriteLine($"[rcf3-perf] {count}x{width} String-keyed copy: read {read.Elapsed.TotalMilliseconds:F1} ms, " +
                          $"{count * width} lookups through Swift {lookUp.Elapsed.TotalMilliseconds:F1} ms (half by the decomposed spelling), " +
                          $"{count * width} ordinal lookups {ordinalLookUp.Elapsed.TotalMilliseconds:F1} ms");
        AssertEqual(count * width, found, "every key is found, the non-ASCII ones by their decomposed spelling");
        AssertEqual(count * width, ordinalFound, "every key is found in the ordinal copies by its own spelling");
    }
}
