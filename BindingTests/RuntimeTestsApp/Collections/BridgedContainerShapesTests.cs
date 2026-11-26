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
        // A URL the conformer puts in what it makes, when set, so a caller can watch its retain count.
        public NSUrl? Kept { get; set; }

        public IReadOnlyList<NSUrl> StoredURLs { get; set; } = Array.Empty<NSUrl>();

        public IReadOnlyDictionary<string, IReadOnlyList<NSUrl>> StoredGroups { get; set; } = new Dictionary<string, IReadOnlyList<NSUrl>>();

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

        public string DescribeTagged(IEnumerable<NSUrl> urls, string urlsNS, NSUrl url, nint urlNS, nint urlsBridged)
            => $"{Names(urls)}|{urlsNS}|{url.LastPathComponent}|{urlNS}|{urlsBridged}";

        public IReadOnlyDictionary<string, IReadOnlyList<NSUrl>> MakeGroups()
            => new Dictionary<string, IReadOnlyList<NSUrl>>
            {
                ["x"] = new[] { NSUrlOf("a") },
                ["yz"] = new[] { NSUrlOf("b"), NSUrlOf("c") },
            };

        public IReadOnlyList<NSUrl>? MakeOptionalArray(bool present) => present ? new[] { Kept ?? NSUrlOf("a"), NSUrlOf("c") } : null;
    }

    public void TestAConformerStoresTheNestedGroupsSwiftSetsAndHandsThemBack()
        => AssertEqual("ab:a,b;c:c", TestLibFunctions.UrlSinkStoreAndReadGroups(new UrlSink()), "the groups Swift set, read back");

    public void TestAConformerReadsEachArgumentBesideSiblingsNamedLikeTheBridgedLocals()
        => AssertEqual("a,b|tag|c|7|9", TestLibFunctions.UrlSinkDescribeTagged(new UrlSink()), "every argument as Swift passed it");

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

    /// <summary>
    /// A conformer's array goes back to Swift as an NSArray built for the call, which the binding
    /// releases once Swift holds its own reference: a URL the conformer keeps returns to its retain
    /// count after every crossing, as one explicit retain moves it by exactly one (the control). A
    /// wrapper left to the finalizer instead holds the array, and the URL in it, until a collection runs.
    /// </summary>
    public void TestAContainerAConformerMakesLeavesAKeptURLsRetainCount()
    {
        var kept = NSUrlOf("a");
        var sink = new UrlSink { Kept = kept };
        nuint baseline = kept.RetainCount;

        kept.DangerousRetain();
        AssertEqual(baseline + 1, kept.RetainCount, "control: one retain adds one reference");
        kept.DangerousRelease();
        AssertEqual(baseline, kept.RetainCount, "control: releasing it takes that reference back");

        for (int i = 0; i < 25; i++)
        {
            using (new NSAutoreleasePool())
            {
                AssertEqual($"{Url("a")},{Url("c")}", TestLibFunctions.UrlSinkReadOptionalArray(sink, true), "Swift reads the optional array");
            }
        }
        AssertEqual(baseline, kept.RetainCount, "25 crossings leave the kept URL's retain count where it was");
    }

    // ── Native-only containers inside a bridged one ───────────────────────────────────────

    static string Counts(IEnumerable<KeyValuePair<NSUrl, IEnumerable<nint>>> groups)
        => string.Join(";", groups.Select(e => $"{e.Key.LastPathComponent}:{string.Join(",", e.Value)}").OrderBy(n => n, StringComparer.Ordinal));

    static string Rows(IEnumerable<IEnumerable<NSUrl>> rows)
        => string.Join("|", rows.Select(Names).OrderBy(n => n, StringComparer.Ordinal));

    public void TestCSharpReadsArraysOfIntsInADictionaryKeyedByURLs()
    {
        var groups = TestLibFunctions.GetUrlCountGroups();
        AssertEqual(2, groups.Count, "both entries come back");
        AssertTrue(groups.TryGetValue(NSUrlOf("a"), out var a) && a.SequenceEqual(new nint[] { 1, 2 }), "a fresh, equal URL finds its counts, in order");
        AssertEqual("3", string.Join(",", groups[NSUrlOf("b")]), "and the other entry's");
    }

    public void TestSwiftReadsArraysOfIntsInADictionaryKeyedByURLsCSharpBuilt()
    {
        var groups = new Dictionary<NSUrl, IEnumerable<nint>> { [NSUrlOf("a")] = new nint[] { 1, 2 }, [NSUrlOf("b")] = new nint[] { 3 } };
        AssertEqual("a:1,2;b:3", TestLibFunctions.UrlCountGroupsDescribe(groups), "every count as C# built it");
        var read = TestLibFunctions.GetUrlCountGroups().ToDictionary(e => e.Key, e => (IEnumerable<nint>)e.Value);
        AssertEqual("a:1,2;b:3", TestLibFunctions.UrlCountGroupsDescribe(read), "and what Swift returned, handed back");
    }

    public void TestCSharpReadsArraysOfOptionalIntsInADictionaryKeyedByURLs()
    {
        var groups = TestLibFunctions.GetUrlOptionalCountGroups();
        AssertTrue(groups.TryGetValue(NSUrlOf("a"), out var a) && a.SequenceEqual(new nint?[] { 1, null }), "the nil count comes back as null, in its place");
        var built = new Dictionary<NSUrl, IEnumerable<nint?>> { [NSUrlOf("a")] = new nint?[] { 1, null } };
        AssertEqual("a:1,nil", TestLibFunctions.UrlOptionalCountGroupsDescribe(built), "and a null C# count reaches Swift as nil");
    }

    public void TestCSharpReadsDictionariesOfIntsInADictionaryKeyedByURLs()
    {
        var tallies = TestLibFunctions.GetUrlTallies();
        AssertTrue(tallies.TryGetValue(NSUrlOf("a"), out var a), "a fresh, equal URL finds its tallies");
        AssertEqual(2, a!.Count, "both tallies come back");
        AssertTrue(a.TryGetValue("x", out var x) && x == 1 && a["y"] == 2, "each under its key");
    }

    public void TestSwiftReadsDictionariesOfIntsInADictionaryKeyedByURLsCSharpBuilt()
    {
        var tallies = new Dictionary<NSUrl, IDictionary<string, nint>> { [NSUrlOf("a")] = new Dictionary<string, nint> { ["x"] = 1, ["y"] = 2 } };
        AssertEqual("a:x=1,y=2", TestLibFunctions.UrlTalliesDescribe(tallies), "every tally as C# built it");
    }

    public void TestCSharpReadsASetOfArraysOfURLs()
    {
        var rows = TestLibFunctions.GetUrlRowSet();
        AssertEqual(2, rows.Count, "both rows come back");
        AssertEqual("a,b|c", Rows(rows), "every row, every URL, in order");
    }

    public void TestSwiftReadsASetOfArraysOfURLsCSharpBuilt()
    {
        var rows = new HashSet<IEnumerable<NSUrl>> { new[] { NSUrlOf("a"), NSUrlOf("b") }, new[] { NSUrlOf("c") } };
        AssertEqual("a,b|c", TestLibFunctions.UrlRowSetDescribe(rows), "every row as C# built it");
    }

    // ── Swift objects inside a bridged container ──────────────────────────────────────────

    static string Boxes(IEnumerable<KeyValuePair<NSUrl, URLValueBox>> index)
        => string.Join(";", index.Select(e => $"{e.Key.LastPathComponent}={e.Value.Name}").OrderBy(n => n, StringComparer.Ordinal));

    static string BoxRows(IEnumerable<KeyValuePair<NSUrl, IReadOnlyList<URLValueBox>>> rows)
        => string.Join(";", rows.Select(e => $"{e.Key.LastPathComponent}:{string.Join(",", e.Value.Select(b => b.Name))}").OrderBy(n => n, StringComparer.Ordinal));

    public void TestCSharpReadsSwiftObjectsValuedByURLs()
    {
        var index = TestLibFunctions.GetUrlBoxIndex();
        AssertEqual("a=x;b=y", Boxes(index), "every Swift object comes back under its URL");
        AssertTrue(index.TryGetValue(NSUrlOf("a"), out var a) && a.Name == "x", "a fresh, equal URL finds its object");
    }

    public void TestSwiftReadsSwiftObjectsValuedByURLsCSharpBuilt()
    {
        using var x = new URLValueBox("x");
        using var y = new URLValueBox("y");
        var index = new Dictionary<NSUrl, URLValueBox> { [NSUrlOf("a")] = x, [NSUrlOf("b")] = y };
        AssertEqual("a=x;b=y", TestLibFunctions.UrlBoxIndexDescribe(index), "every object as C# built it");
        var read = TestLibFunctions.GetUrlBoxIndex().ToDictionary(e => e.Key, e => e.Value);
        AssertEqual("a=x;b=y", TestLibFunctions.UrlBoxIndexDescribe(read), "and what Swift returned, handed back");
    }

    public void TestCSharpReadsArraysOfSwiftObjectsValuedByURLs()
    {
        var rows = TestLibFunctions.GetUrlBoxRows();
        AssertEqual("a:x,y;b:", BoxRows(rows), "every row of Swift objects, in order, and the empty one");
    }

    public void TestSwiftReadsArraysOfSwiftObjectsValuedByURLsCSharpBuilt()
    {
        using var x = new URLValueBox("x");
        using var y = new URLValueBox("y");
        var rows = new Dictionary<NSUrl, IEnumerable<URLValueBox>> { [NSUrlOf("a")] = new[] { x, y }, [NSUrlOf("b")] = Array.Empty<URLValueBox>() };
        AssertEqual("a:x,y;b:", TestLibFunctions.UrlBoxRowsDescribe(rows), "every row as C# built it");
        var read = TestLibFunctions.GetUrlBoxRows().ToDictionary(e => e.Key, e => (IEnumerable<URLValueBox>)e.Value);
        AssertEqual("a:x,y;b:", TestLibFunctions.UrlBoxRowsDescribe(read), "and what Swift returned, handed back");
    }

    static readonly DateTimeOffset ReferenceDate = new(2001, 1, 1, 0, 0, 0, TimeSpan.Zero);

    static string Dates(IReadOnlyDictionary<NSUrl, DateTimeOffset> dates)
        => string.Join(";", dates.Select(e => $"{e.Key.LastPathComponent}={(e.Value - ReferenceDate).TotalSeconds:0.0###}").OrderBy(s => s, StringComparer.Ordinal));

    /// <summary>A Date Swift puts in a dictionary keyed by URLs reaches C# as the same instant.</summary>
    public void TestCSharpReadsDatesValuedByURLs()
    {
        var dates = TestLibFunctions.GetUrlDateIndex();
        AssertEqual("a=1000.0;b=-2.5", Dates(dates), "every date comes back under its URL");
        AssertTrue(dates.TryGetValue(NSUrlOf("b"), out var b) && b == ReferenceDate.AddSeconds(-2.5), "a fresh, equal URL finds its instant");
    }

    /// <summary>A DateTimeOffset C# puts in a dictionary keyed by URLs reaches Swift as the same instant.</summary>
    public void TestSwiftReadsDatesValuedByURLsCSharpBuilt()
    {
        var dates = new Dictionary<NSUrl, DateTimeOffset> { [NSUrlOf("a")] = ReferenceDate.AddSeconds(1000), [NSUrlOf("b")] = ReferenceDate.AddSeconds(-2.5) };
        AssertEqual("a=1000.0;b=-2.5", TestLibFunctions.UrlDateIndexDescribe(dates), "every date as C# built it");
        AssertEqual("a=1000.0;b=-2.5", TestLibFunctions.UrlDateIndexDescribe(new Dictionary<NSUrl, DateTimeOffset>(TestLibFunctions.GetUrlDateIndex())), "and what Swift returned, handed back");
        using var holder = new URLDateHolder();
        holder.Dates = dates;
        AssertEqual("a=1000.0;b=-2.5", holder.GetDescribe(), "a property's new value");
        AssertEqual("a=1000.0;b=-2.5", Dates(holder.Dates), "and the property read back");
    }

    /// <summary>
    /// Swift objects C# puts in a bridged dictionary, with no other owner, are read by Swift after a
    /// collector has run beside the call.
    /// </summary>
    public void TestSwiftObjectsInABridgedDictionarySurviveACollectorRunningBeside()
    {
        using var stop = new CancellationTokenSource();
        var collector = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                // Paused between collections, so an interpreted worker is not held at its safe point throughout.
                Thread.Sleep(2);
            }
        });
        try
        {
            var worker = Task.Run(() =>
            {
                for (int i = 0; i < 25; i++)
                {
                    using (new NSAutoreleasePool())
                    {
                        var index = new Dictionary<NSUrl, URLValueBox> { [NSUrlOf("a")] = new URLValueBox($"x{i}") };
                        var described = TestLibFunctions.UrlBoxIndexDescribe(index);
                        if (described != $"a=x{i}")
                            return $"iteration {i}: {described}";
                        var rows = new Dictionary<NSUrl, IEnumerable<URLValueBox>> { [NSUrlOf("a")] = new[] { new URLValueBox($"x{i}"), new URLValueBox($"y{i}") } };
                        described = TestLibFunctions.UrlBoxRowsDescribe(rows);
                        if (described != $"a:x{i},y{i}")
                            return $"iteration {i}: {described}";
                    }
                }
                return "ok";
            });
            AssertEqual("ok", worker.GetAwaiter().GetResult(), "every object Swift read is the one C# put in");
        }
        finally
        {
            stop.Cancel();
            collector.GetAwaiter().GetResult();
        }
    }

    // ── Sets of collections ───────────────────────────────────────────────────────────────

    public void TestASetOfArraysOfURLsFindsAFreshEqualRow()
    {
        var rows = TestLibFunctions.GetUrlRowSet();
        AssertTrue(rows.Contains(new[] { NSUrlOf("a"), NSUrlOf("b") }), "a fresh row with the same URLs in the same order is in the set, as Swift's == says");
        AssertFalse(rows.Contains(new[] { NSUrlOf("b"), NSUrlOf("a") }), "the same URLs in another order are another row");
        AssertFalse(rows.Contains(new[] { NSUrlOf("a") }), "a shorter row is another row");
        AssertTrue(TestLibFunctions.UrlRowSetContains(rows.Select(r => (IEnumerable<NSUrl>)r).ToHashSet(), new[] { NSUrlOf("c") }), "Swift agrees on the set C# read");
    }

    public void TestASetOfSetsOfURLsFindsAFreshEqualGroup()
    {
        var groups = TestLibFunctions.GetUrlGroupSet();
        AssertEqual(2, groups.Count, "both groups come back");
        AssertTrue(groups.Contains(new HashSet<NSUrl> { NSUrlOf("b"), NSUrlOf("a") }), "a fresh group with the same URLs, in any order, is in the set");
        AssertTrue(groups.Contains(new HashSet<NSUrl> { NSUrlOf("c") }), "and so is the other one");
        AssertFalse(groups.Contains(new HashSet<NSUrl> { NSUrlOf("a") }), "a smaller group is another group");
    }

    // ── Dictionaries C# hands Swift, enumerated once ──────────────────────────────────────

    /// <summary>
    /// A dictionary whose every enumeration starts one entry later than the one before: a writer
    /// that reads its keys and values in separate passes pairs each key with another key's value.
    /// </summary>
    sealed class ShiftingIndex : Dictionary<string, NSUrl>, IEnumerable<KeyValuePair<string, NSUrl>>
    {
        int _passes;

        public ShiftingIndex(params string[] names)
        {
            foreach (var name in names)
                this[name] = NSUrlOf(name);
        }

        IEnumerator<KeyValuePair<string, NSUrl>> IEnumerable<KeyValuePair<string, NSUrl>>.GetEnumerator()
        {
            var entries = new List<KeyValuePair<string, NSUrl>>();
            foreach (var entry in (Dictionary<string, NSUrl>)this)
                entries.Add(entry);
            var shift = entries.Count == 0 ? 0 : _passes++ % entries.Count;
            return entries.Skip(shift).Concat(entries.Take(shift)).GetEnumerator();
        }
    }

    sealed class IndexSource : IURLIndexSource
    {
        public IReadOnlyDictionary<string, NSUrl> MakeIndex() => new ShiftingIndex("a", "b", "c");
    }

    public void TestSwiftReadsEachKeyWithItsOwnURL()
    {
        AssertEqual("a=a;b=b;c=c", TestLibFunctions.UrlIndexDescribe(new ShiftingIndex("a", "b", "c")), "an argument");
        AssertEqual("a=a;b=b;c=c|x=x;y=y", TestLibFunctions.UrlIndexRowsDescribe(new IDictionary<string, NSUrl>[] { new ShiftingIndex("a", "b", "c"), new ShiftingIndex("x", "y") }), "a dictionary in an array");
        using var holder = new URLIndexHolder();
        holder.Index = new ShiftingIndex("a", "b", "c");
        AssertEqual("a=a;b=b;c=c", holder.GetDescribe(), "a property's new value");
        holder[(nint)0] = new ShiftingIndex("p", "q", "r");
        AssertEqual("p=p;q=q;r=r", holder.GetDescribe(), "a subscript's new value");
        AssertEqual("a=a;b=b;c=c", TestLibFunctions.UrlIndexSourceDescribe(new IndexSource()), "a dictionary a conformer makes");
    }

    public void TestASubscriptSetterReadsAnIndexNamedLikeItsLocals()
    {
        using var holder = new URLIndexHolder();
        holder[(nint)0] = new Dictionary<string, NSUrl> { ["k"] = NSUrlOf("v") };
        AssertEqual("k=v", holder.GetDescribe(), "the Int index named __val reaches Swift beside the new value");
        AssertEqual("k=v", string.Join(";", holder[(nint)0].Select(e => $"{e.Key}={e.Value.LastPathComponent}")), "and reads back");
        AssertEqual(0, holder[(nint)1].Count, "another index reads Swift's empty dictionary");
        holder["urls"] = new[] { NSUrlOf("a"), NSUrlOf("b") };
        AssertEqual("a,b", holder.GetDescribeURLs(), "the String index named __val reaches Swift beside the new value");
        AssertEqual("a,b", Names(holder["urls"]!), "and reads back");
        holder["other"] = new[] { NSUrlOf("c") };
        AssertEqual("a,b", holder.GetDescribeURLs(), "another key leaves the URLs as they were");
        holder["urls"] = null;
        AssertEqual("nil", holder.GetDescribeURLs(), "a null new value reaches Swift as nil");
    }

    sealed class NestedSink : INestedNativeContainerSink
    {
        public string DescribeCounts(IDictionary<NSUrl, IEnumerable<nint>> counts) => Counts(counts);

        public string DescribeRowSet(IReadOnlySet<IEnumerable<NSUrl>> rows) => Rows(rows);

        public IReadOnlyDictionary<NSUrl, IReadOnlyList<nint>> MakeCounts()
            => new Dictionary<NSUrl, IReadOnlyList<nint>> { [NSUrlOf("x")] = new nint[] { 4, 5 }, [NSUrlOf("y")] = Array.Empty<nint>() };

        public IReadOnlySet<IReadOnlyList<NSUrl>> MakeRowSet()
            => new HashSet<IReadOnlyList<NSUrl>> { new[] { NSUrlOf("p"), NSUrlOf("q") }, new[] { NSUrlOf("r") } };

        public string DescribeSelfNamed(IEnumerable<NSUrl> urls, nint selfProto) => $"{Names(urls)}|{selfProto}";
    }

    public void TestAConformerReadsArraysOfIntsInADictionaryKeyedByURLs()
        => AssertEqual("a:1,2;b:3", TestLibFunctions.NestedSinkDescribeCounts(new NestedSink()), "every count Swift handed over");

    public void TestAConformerReadsASetOfArraysOfURLs()
        => AssertEqual("a,b|c", TestLibFunctions.NestedSinkDescribeRowSet(new NestedSink()), "every row Swift handed over");

    public void TestSwiftReadsArraysOfIntsInADictionaryAConformerMakes()
        => AssertEqual("x:4,5;y:", TestLibFunctions.NestedSinkReadCounts(new NestedSink()), "every count the conformer made");

    public void TestSwiftReadsASetOfArraysOfURLsAConformerMakes()
        => AssertEqual("p,q|r", TestLibFunctions.NestedSinkReadRowSet(new NestedSink()), "every row the conformer made");

    public void TestAConformerReadsAnArgumentNamedLikeTheThunksOwnLocal()
        => AssertEqual("a|41", TestLibFunctions.NestedSinkDescribeSelfNamed(new NestedSink()), "the argument Swift passed, not the conformer");

    // ── Optional bridged containers C# sets ───────────────────────────────────────────────

    static void SetEveryContainer(OptionalBridgedContainerHolder holder, NSUrl url, SwiftKeyNode node)
    {
        holder.Urls = new[] { url };
        holder.UrlSet = new HashSet<NSUrl> { url };
        holder.Index = new Dictionary<string, NSUrl> { ["k"] = url };
        holder.NodeIndex = new Dictionary<SwiftKeyNode, NSUrl> { [node] = url };
    }

    static void ClearEveryContainer(OptionalBridgedContainerHolder holder)
    {
        holder.Urls = null;
        holder.UrlSet = null;
        holder.Index = null;
        holder.NodeIndex = null;
    }

    public void TestSwiftReadsOptionalBridgedContainersCSharpSets()
    {
        using var holder = new OptionalBridgedContainerHolder();
        using var node = new SwiftKeyNode("n");
        AssertEqual("nil|nil|nil|nil", holder.GetDescribe(), "every container starts nil");
        SetEveryContainer(holder, NSUrlOf("a"), node);
        AssertEqual("a|a|k=a|n=a", holder.GetDescribe(), "every container Swift stored");
        ClearEveryContainer(holder);
        AssertEqual("nil|nil|nil|nil", holder.GetDescribe(), "and nil again");
    }

    /// <summary>
    /// Setting an optional bridged container builds the collection Swift stores, which the setter
    /// releases once Swift holds its own copy: after the containers are set and cleared, the URL and
    /// the node key in them are back at their retain counts, as one explicit retain moves each by
    /// exactly one (the control). A collection whose handle is read off a temporary wrapper is left
    /// to that wrapper's finalizer, holding every element until a collection runs.
    /// </summary>
    public void TestSettingOptionalBridgedContainersLeavesTheirElementsRetainCounts()
    {
        using var holder = new OptionalBridgedContainerHolder();
        using var node = new SwiftKeyNode("n");
        var url = NSUrlOf("a");
        nuint baseline = url.RetainCount;
        nint nodeBaseline = TestLibFunctions.SwiftKeyNodeRetainCount(node);

        url.DangerousRetain();
        AssertEqual(baseline + 1, url.RetainCount, "control: one retain adds one reference");
        url.DangerousRelease();
        AssertEqual(baseline, url.RetainCount, "control: releasing it takes that reference back");
        using (var control = new SwiftKeyNodeArrayStore())
        {
            control.Keep(new[] { node });
            AssertEqual(nodeBaseline + 1, TestLibFunctions.SwiftKeyNodeRetainCount(node), "control: the array Swift keeps holds one reference");
            control.Clear();
            AssertEqual(nodeBaseline, TestLibFunctions.SwiftKeyNodeRetainCount(node), "control: clearing it releases that reference");
        }

        for (int i = 0; i < 25; i++)
        {
            using (new NSAutoreleasePool())
            {
                SetEveryContainer(holder, url, node);
                AssertEqual("a|a|k=a|n=a", holder.GetDescribe(), "Swift stored every container");
                ClearEveryContainer(holder);
            }
        }
        AssertEqual(baseline, url.RetainCount, "25 sets leave the URL's retain count where it was");
        AssertEqual(nodeBaseline, TestLibFunctions.SwiftKeyNodeRetainCount(node), "and the node key's");
    }

    /// <summary>
    /// The collections a setter builds are read while a collector runs beside it, on a background
    /// thread: every value Swift stores is the one C# set.
    /// </summary>
    public void TestOptionalBridgedContainersSurviveACollectorRunningBeside()
    {
        using var stop = new CancellationTokenSource();
        var collector = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
        });
        try
        {
            var worker = Task.Run(() =>
            {
                using var holder = new OptionalBridgedContainerHolder();
                for (int i = 0; i < 200; i++)
                {
                    using (new NSAutoreleasePool())
                    {
                        var name = $"u{i}";
                        holder.Urls = new[] { NSUrlOf(name) };
                        holder.UrlSet = new HashSet<NSUrl> { NSUrlOf(name) };
                        holder.Index = new Dictionary<string, NSUrl> { ["k"] = NSUrlOf(name) };
                        holder.NodeIndex = new Dictionary<SwiftKeyNode, NSUrl> { [new SwiftKeyNode("n")] = NSUrlOf(name) };
                        var described = holder.GetDescribe();
                        if (described != $"{name}|{name}|k={name}|n={name}")
                            return $"iteration {i}: {described}";
                    }
                }
                return "ok";
            });
            AssertEqual("ok", worker.GetAwaiter().GetResult(), "every container Swift stored is the one C# set");
        }
        finally
        {
            stop.Cancel();
            collector.GetAwaiter().GetResult();
        }
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

    public void TestCSharpSetsAPropertyKeyedBySwiftClassValuedByURLs()
    {
        var holder = new SwiftKeyNodeURLHolder();
        holder.Index = NodeIndex("a", "b");
        AssertEqual(Url(1), holder.Url("b"), "Swift finds a node C# set with one it made itself");
        AssertEqual(null, holder.Url("c"), "and misses an absent one");
        AssertTrue(holder.Index.TryGetValue(new SwiftKeyNode("a"), out var a) && a.AbsoluteString == Url(0), "the property reads back");
    }

    public void TestCSharpReadsADictionaryKeyedBySwiftClassNestedInAnother()
    {
        var nested = TestLibFunctions.SwiftKeyNodeNestedURLIndex("g", new[] { "a", "b" });
        AssertTrue(nested.TryGetValue(new SwiftKeyNode("g"), out var inner), "a fresh, equal node finds the inner dictionary");
        AssertTrue(inner!.TryGetValue(new SwiftKeyNode("b"), out var b) && b.AbsoluteString == Url(1), "and its URL");
    }

    public void TestSwiftLooksUpADictionaryKeyedBySwiftClassNestedInAnotherCSharpBuilt()
    {
        var index = new Dictionary<SwiftKeyNode, IDictionary<SwiftKeyNode, NSUrl>> { [new SwiftKeyNode("g")] = NodeIndex("a", "b") };
        AssertEqual(Url(1), TestLibFunctions.SwiftKeyNodeNestedURLLookUp(index, "g", "b"), "Swift finds both nodes");
        AssertEqual(null, TestLibFunctions.SwiftKeyNodeNestedURLLookUp(index, "h", "b"), "and misses an absent outer node");
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

    public void TestANestedBridgedStringKeyedCopyFindsACanonicallyEqualKey()
    {
        var groups = TestLibFunctions.CanonicalStringNestedURLIndex(new[] { "x", Precomposed }, new[] { Precomposed, "plain" });
        AssertTrue(groups.TryGetValue(Decomposed, out var group), "the decomposed spelling finds the outer key");
        AssertTrue(group!.TryGetValue(Decomposed, out var url) && url.AbsoluteString == Url(0), "and the inner one");
        AssertTrue(groups["x"].ContainsKey("plain"), "an ASCII key finds itself");
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
