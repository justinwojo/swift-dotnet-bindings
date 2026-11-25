// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using Swift;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Collections;

/// <summary>
/// Optional keys and elements beyond the object keys <see cref="NilOptionalKeyedCollectionsTests"/>
/// covers: optional value keys (<c>[Int?: Int]</c>, <c>Set&lt;Int?&gt;</c>, <c>[String?: Int]</c>, an
/// optional enum or struct key), dictionaries keyed by an optional bound generic class nested in
/// another collection, and a nil optional key beside a value that sends the dictionary through
/// <c>NSDictionary</c> (<c>[NSUUID?: URL]</c>).
/// </summary>
/// <remarks>
/// Swift compares an optional key through <c>Optional</c>'s conditional <c>Hashable</c>, so a
/// collection over one needs that conformance's witness, holds <c>nil</c> like any other key, and,
/// copied into .NET, must still find a key C# looks up with a fresh, equal value.
/// </remarks>
public class OptionalKeyShapesTests : TestBase
{
    public OptionalKeyShapesTests(TestResults results) : base(results) { }

    const string First = "6F9619FF-8B86-D011-B42D-00C04FC964FF";
    const string Second = "0B2D7C4E-5A1F-4C3B-9E8D-7F6A5B4C3D2E";

    static long? Long(nint? value) => value is { } v ? (long)v : null;

    static SwiftKeyBox<SwiftString> Box(string value) => new(new SwiftString(value));

    static string Url(nint position) => $"https://example.com/{position}";

    static void Release(object? collection) => (collection as IDisposable)?.Dispose();

    // ── Optional value keys ────────────────────────────────────────────────────────────────

    public void TestSwiftReturnsADictionaryKeyedByOptionalInts()
    {
        var index = TestLibFunctions.OptionalKeyMakeIntIndex(new nint?[] { 7, null, 9 });
        try
        {
            AssertEqual(3, index.Count, "every entry comes back");
            AssertTrue(index.TryGetValue(7, out var seven) && seven == 0, "a present key is found");
            AssertTrue(index.TryGetValue(null, out var nil) && nil == 1, "the nil key is found");
            AssertTrue(index.TryGetValue(9, out var nine) && nine == 2, "and the last key");
            AssertFalse(index.ContainsKey(8), "an absent key is not");
        }
        finally
        {
            Release(index);
        }
    }

    public void TestSwiftLooksUpOptionalIntKeysCSharpBuilt()
    {
        var index = new NilKeyDictionary<nint?, nint> { [3] = 30, [null] = 40 };
        AssertEqual("3=30,nil=40", TestLibFunctions.OptionalKeyIntEntries(index), "Swift sees every key, nil included");
        AssertEqual(30L, Long(TestLibFunctions.OptionalKeyIntLookUp(index, (nint?)3)), "Swift finds a present key");
        AssertEqual(40L, Long(TestLibFunctions.OptionalKeyIntLookUp(index, (nint?)null)), "and the nil key");
        AssertEqual(null, Long(TestLibFunctions.OptionalKeyIntLookUp(index, (nint?)4)), "and misses an absent one");
    }

    public void TestNestedOptionalIntKeyedDictionariesKeepTheirKeys()
    {
        var list = TestLibFunctions.OptionalKeyMakeIntIndexList(new nint?[] { 5, null });
        try
        {
            AssertEqual(2, list.Count, "both dictionaries come back");
            AssertTrue(list[0].TryGetValue(5, out var five) && five == 0, "the first holds its key");
            AssertTrue(list[1].TryGetValue(null, out var nil) && nil == 1, "the second its nil key");
        }
        finally
        {
            Release(list);
        }
    }

    public void TestASetOfOptionalIntsHoldsNilOnce()
    {
        var set = TestLibFunctions.OptionalKeyMakeIntSet(new nint?[] { 1, null, 1, null });
        try
        {
            AssertEqual(2, set.Count, "duplicates collapse, nil included");
            AssertTrue(set.Contains(null), "nil is an element");
            AssertTrue(set.Contains(1), "beside its sibling");
        }
        finally
        {
            Release(set);
        }

        var built = new HashSet<nint?> { null, 3 };
        AssertTrue(TestLibFunctions.OptionalKeyIntSetContains(built, (nint?)null), "Swift finds nil in a set C# built");
        AssertTrue(TestLibFunctions.OptionalKeyIntSetContains(built, (nint?)3), "and a present element");
        AssertFalse(TestLibFunctions.OptionalKeyIntSetContains(built, (nint?)4), "and misses an absent one");
    }

    public void TestOptionalStringKeysCrossBothWays()
    {
        var index = TestLibFunctions.OptionalKeyMakeStringIndex(new[] { "alpha", null });
        try
        {
            AssertTrue(index.TryGetValue("alpha", out var alpha) && alpha == 0, "a present string key is found");
            AssertTrue(index.TryGetValue(null, out var nil) && nil == 1, "and the nil key");
        }
        finally
        {
            Release(index);
        }

        var built = new NilKeyDictionary<string?, nint> { ["beta"] = 2, [null] = 3 };
        AssertEqual(2L, Long(TestLibFunctions.OptionalKeyStringLookUp(built, "beta")), "Swift finds a present string key");
        AssertEqual(3L, Long(TestLibFunctions.OptionalKeyStringLookUp(built, null)), "and the nil key");
    }

    public void TestOptionalEnumKeysCrossBothWays()
    {
        var index = TestLibFunctions.GetOptionalKeyMakeShadeIndex();
        try
        {
            AssertEqual(3, index.Count, "every entry comes back");
            AssertTrue(index.TryGetValue(OptionalKeyShade.Red, out var red) && red == 0, "a present case is found");
            AssertTrue(index.TryGetValue(null, out var nil) && nil == 1, "and the nil key");
            AssertFalse(index.ContainsKey(OptionalKeyShade.Green), "an absent case is not");
        }
        finally
        {
            Release(index);
        }

        var built = new NilKeyDictionary<OptionalKeyShade?, nint> { [OptionalKeyShade.Blue] = 4, [null] = 5 };
        AssertEqual(4L, Long(TestLibFunctions.OptionalKeyShadeLookUp(built, OptionalKeyShade.Blue)), "Swift finds a present case");
        AssertEqual(5L, Long(TestLibFunctions.OptionalKeyShadeLookUp(built, null)), "and the nil key");
    }

    public void TestOptionalStructKeysCrossBothWays()
    {
        var index = TestLibFunctions.GetOptionalKeyMakePointIndex();
        try
        {
            AssertEqual(2, index.Count, "every entry comes back");
            using var point = new OptionalKeyPoint(1, 2);
            AssertTrue(index.TryGetValue(point, out var found) && found == 0, "a fresh, equal point is found");
            AssertTrue(index.TryGetValue(null, out var nil) && nil == 1, "and the nil key");
        }
        finally
        {
            Release(index);
        }

        using var key = new OptionalKeyPoint(3, 4);
        var built = new NilKeyDictionary<OptionalKeyPoint?, nint> { [key] = 6, [null] = 7 };
        AssertEqual(6L, Long(TestLibFunctions.OptionalKeyPointLookUp(built, true, 3, 4)), "Swift finds a point C# keyed by");
        AssertEqual(7L, Long(TestLibFunctions.OptionalKeyPointLookUp(built, false, 0, 0)), "and the nil key");
    }

    // ── Nested dictionaries keyed by an optional bound generic class ───────────────────────

    public void TestNestedOptionalBoxDictionariesFindFreshEqualBoxes()
    {
        var list = TestLibFunctions.SwiftBoxMakeOptionalIndexList(new[] { "alpha", null });
        try
        {
            AssertEqual(2, list.Count, "both dictionaries come back");
            using var alpha = Box("alpha");
            AssertTrue(list[0].TryGetValue(alpha, out var found) && found == 0, "a fresh box of a present value is found");
            using var beta = Box("beta");
            AssertFalse(list[0].ContainsKey(beta), "a box of an absent value is not");
            AssertTrue(list[1].TryGetValue(null, out var nil) && nil == 1, "the nil key is found");
        }
        finally
        {
            Release(list);
        }
    }

    public void TestDictionaryValuedOptionalBoxDictionariesFindFreshEqualBoxes()
    {
        var groups = TestLibFunctions.SwiftBoxMakeOptionalIndexGroups(new[] { "alpha", null, "beta" });
        try
        {
            var all = groups["all"];
            AssertEqual(3, all.Count, "every entry comes back");
            using var beta = Box("beta");
            AssertTrue(all.TryGetValue(beta, out var found) && found == 2, "a fresh box of a present value is found");
            AssertTrue(all.TryGetValue(null, out var nil) && nil == 1, "and the nil key");
        }
        finally
        {
            Release(groups);
        }
    }

    public void TestNestedOptionalBoxSetsFindFreshEqualBoxes()
    {
        var sets = TestLibFunctions.SwiftBoxMakeOptionalSetList(new[] { "alpha", null });
        try
        {
            using var alpha = Box("alpha");
            AssertTrue(sets[0].Contains(alpha), "a fresh box of a present value is an element");
            AssertTrue(sets[0].Contains(null), "and so is nil");
            using var gamma = Box("gamma");
            AssertFalse(sets[0].Contains(gamma), "a box of an absent value is not");
        }
        finally
        {
            Release(sets);
        }
    }

    public void TestNestedBoxDictionariesAndSetsFindFreshEqualBoxes()
    {
        using var alpha = Box("alpha");
        using var beta = Box("beta");
        using var gamma = Box("gamma");

        var list = TestLibFunctions.SwiftBoxMakeIndexList(new[] { "alpha", "beta" });
        try
        {
            AssertEqual(2, list.Count, "both dictionaries come back");
            AssertTrue(list[1].TryGetValue(beta, out var found) && found == 1, "a fresh box of a present value is found");
            AssertFalse(list[0].ContainsKey(gamma), "a box of an absent value is not");
        }
        finally
        {
            Release(list);
        }

        var groups = TestLibFunctions.SwiftBoxMakeIndexGroups(new[] { "alpha", "beta" });
        try
        {
            foreach (var group in groups)
                AssertTrue(group.Value.TryGetValue(alpha, out var found) && found == 0, "a fresh box is found in a dictionary-valued dictionary");
        }
        finally
        {
            Release(groups);
        }

        var sets = TestLibFunctions.SwiftBoxMakeSetList(new[] { "alpha", "beta" });
        try
        {
            AssertTrue(sets[0].Contains(alpha), "a fresh box of a present value is an element");
            AssertFalse(sets[0].Contains(gamma), "a box of an absent value is not");
        }
        finally
        {
            Release(sets);
        }
    }

    sealed class NestedSource : ISwiftBoxNestedSource
    {
        public nint Value(IEnumerable<IDictionary<SwiftKeyBox<SwiftString>?, nint>> list, string value)
        {
            using var key = Box(value);
            foreach (var index in list)
            {
                if (index.TryGetValue(key, out var found))
                    return found;
            }
            return -1;
        }
    }

    public void TestAConformerFindsFreshEqualBoxesInNestedDictionaries()
    {
        AssertEqual((nint)1, TestLibFunctions.SwiftBoxAskNestedSource(new NestedSource(), new[] { "alpha", "beta" }, "beta"),
            "the conformer finds a fresh box in the dictionaries Swift handed it");
        AssertEqual((nint)(-1), TestLibFunctions.SwiftBoxAskNestedSource(new NestedSource(), new[] { "alpha", "beta" }, "gamma"),
            "and misses an absent value");
    }

    /// <summary>
    /// Looking a box up in a nested dictionary compares it through Swift and leaves its retain count
    /// where it was, as an array Swift keeps adds exactly one reference (the control).
    /// </summary>
    public void TestLookingUpABoxInANestedDictionaryKeepsItsRetainCount()
    {
        using var box = Box("alpha");
        nint baseline = TestLibFunctions.SwiftBoxRetainCount(box);

        using (var control = new SwiftKeyBoxArrayStore())
        {
            using var array = new SwiftArray<SwiftKeyBox<SwiftString>>(new[] { box });
            control.Keep(array);
            array.Dispose();
            AssertEqual(baseline + 1, TestLibFunctions.SwiftBoxRetainCount(box), "control: the array Swift keeps holds one reference");
            control.Clear();
            AssertEqual(baseline, TestLibFunctions.SwiftBoxRetainCount(box), "control: clearing it releases that reference");
        }

        var list = TestLibFunctions.SwiftBoxMakeOptionalIndexList(new[] { "alpha", "beta" });
        try
        {
            for (int i = 0; i < 25; i++)
                AssertTrue(list[0].ContainsKey(box), "the box is found");
        }
        finally
        {
            Release(list);
        }
        AssertEqual(baseline, TestLibFunctions.SwiftBoxRetainCount(box), "25 lookups leave the box's retain count where it was");
    }

    // ── A nil key beside a bridged value ───────────────────────────────────────────────────

    static NilKeyDictionary<Foundation.NSUuid?, Foundation.NSUrl> UrlIndex(Foundation.NSUuid uuid)
        => new() { [uuid] = new Foundation.NSUrl(Url(0)), [null] = new Foundation.NSUrl(Url(1)) };

    public void TestCSharpReadsTheNilKeyOfABridgedDictionarySwiftReturned()
    {
        var index = TestLibFunctions.SwiftUUIDMakeOptionalURLIndex(new[] { First, null });
        AssertEqual(2, index.Count, "every entry comes back");
        AssertTrue(index.TryGetValue(null, out var nil) && nil.AbsoluteString == Url(1), "the nil key reads back as null");
        AssertTrue(index.TryGetValue(new Foundation.NSUuid(First), out var first) && first.AbsoluteString == Url(0),
            "beside a fresh, equal UUID");
    }

    public void TestSwiftSeesTheNilKeyOfABridgedDictionaryCSharpBuilt()
    {
        var index = UrlIndex(new Foundation.NSUuid(First));
        AssertEqual((nint)2, TestLibFunctions.SwiftUUIDOptionalURLCount(index), "Swift sees both keys");
        AssertEqual(Url(1), TestLibFunctions.SwiftUUIDOptionalURLLookUp(index, null), "Swift finds the nil key");
        AssertEqual(Url(0), TestLibFunctions.SwiftUUIDOptionalURLLookUp(index, First), "and the UUID key");
        AssertEqual(null, TestLibFunctions.SwiftUUIDOptionalURLLookUp(index, Second), "and misses an absent one");
    }

    public void TestAnOptionalBridgedDictionaryKeepsTheNilKey()
    {
        AssertTrue(TestLibFunctions.SwiftUUIDMakeOptionalURLIndexOrNil(null) is null, "a nil dictionary reads back as null");
        var index = TestLibFunctions.SwiftUUIDMakeOptionalURLIndexOrNil(new[] { null, Second });
        AssertTrue(index is not null, "a dictionary reads back");
        AssertTrue(index!.TryGetValue(null, out var nil) && nil.AbsoluteString == Url(0), "with its nil key");
        AssertTrue(index.TryGetValue(new Foundation.NSUuid(Second), out var second) && second.AbsoluteString == Url(1), "and its UUID key");
    }

    sealed class UrlSource : ISwiftUUIDURLSource
    {
        public IReadOnlyDictionary<Foundation.NSUuid?, Foundation.NSUrl> GetUrls() => UrlIndex(new Foundation.NSUuid(First));
    }

    public void TestAConformerReturnsTheNilKeyOfABridgedDictionary()
    {
        var source = new UrlSource();
        AssertEqual(Url(1), TestLibFunctions.SwiftUUIDURLFromSource(source, null), "Swift finds the nil key of the dictionary a conformer returned");
        AssertEqual(Url(0), TestLibFunctions.SwiftUUIDURLFromSource(source, First), "and its UUID key");
    }

    /// <summary>
    /// Handing Swift a bridged dictionary with a nil key leaves the UUID key's retain count where it
    /// was, as one explicit retain moves it by exactly one (the control).
    /// </summary>
    public void TestCrossingABridgedDictionaryKeepsTheKeysRetainCount()
    {
        var uuid = new Foundation.NSUuid(First);
        var index = UrlIndex(uuid);
        nuint baseline = uuid.RetainCount;

        uuid.DangerousRetain();
        AssertEqual(baseline + 1, uuid.RetainCount, "control: one retain adds one reference");
        uuid.DangerousRelease();
        AssertEqual(baseline, uuid.RetainCount, "control: releasing it takes that reference back");

        for (int i = 0; i < 25; i++)
        {
            using (new Foundation.NSAutoreleasePool())
                AssertEqual(Url(0), TestLibFunctions.SwiftUUIDOptionalURLLookUp(index, First), "Swift finds the UUID key");
        }
        AssertEqual(baseline, uuid.RetainCount, "25 crossings leave the key's retain count where it was");
    }
}
