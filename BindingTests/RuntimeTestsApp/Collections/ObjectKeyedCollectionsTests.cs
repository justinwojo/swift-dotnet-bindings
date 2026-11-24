// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Foundation;
using RuntimeTestsApp.Infrastructure;
using Swift;
using Swift.Runtime;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Collections;

/// <summary>
/// Collections keyed by a type that crosses as one Objective-C object pointer: an ObjC class key
/// (<c>NSUUID</c>) and a string-backed ObjC key type (<c>NSAttributedString.Key</c>).
/// </summary>
/// <remarks>
/// A collection C# builds must hash and compare each key the way Swift does (an NSUUID by its
/// UUID, an attribute key by its string) and hold its own reference on every key. A count cannot
/// show either: only a Swift lookup of a key that is present, with a key object of Swift's own
/// making, can. The reverse direction is a C# lookup in a collection Swift built, with a key
/// object of C#'s making.
/// </remarks>
public class ObjectKeyedCollectionsTests : TestBase
{
    public ObjectKeyedCollectionsTests(TestResults results) : base(results) { }

    const string UuidA = "6F9619FF-8B86-D011-B42D-00C04FC964FF";
    const string UuidB = "0B2D7C4E-5A1F-4C3B-9E8D-7F6A5B4C3D2E";
    const string UuidAbsent = "11111111-2222-3333-4444-555555555555";

    static long? Long(nint? value) => value is { } v ? (long)v : null;

    static Dictionary<NSString, nint> Attributes(params string[] names)
    {
        var result = new Dictionary<NSString, nint>();
        for (int i = 0; i < names.Length; i++)
            result[new NSString(names[i])] = 10 + i;
        return result;
    }

    // ── C# builds, Swift looks up ───────────────────────────────────────────────────────────

    public void TestSwiftFindsStringBackedKeysOfADictionaryBuiltInCSharp()
    {
        var attributes = Attributes("NSFont", "NSColor", "NSKern");
        AssertEqual(10L, Long(TestLibFunctions.ObjectKeyLookUpAttribute(attributes, "NSFont")), "first key found");
        AssertEqual(12L, Long(TestLibFunctions.ObjectKeyLookUpAttribute(attributes, "NSKern")), "last key found");
        AssertEqual(null, Long(TestLibFunctions.ObjectKeyLookUpAttribute(attributes, "NSLink")), "absent key not found");
    }

    public void TestSwiftFindsObjCClassKeysOfADictionaryBuiltInCSharp()
    {
        var counts = new Dictionary<NSUuid, nint>
        {
            { new NSUuid(UuidA), 1 },
            { new NSUuid(UuidB), 2 },
        };
        AssertEqual(1L, Long(TestLibFunctions.ObjectKeyLookUpUUID(counts, UuidA)), "Swift's own NSUUID finds the first key");
        AssertEqual(2L, Long(TestLibFunctions.ObjectKeyLookUpUUID(counts, UuidB)), "and the second");
        AssertEqual(null, Long(TestLibFunctions.ObjectKeyLookUpUUID(counts, UuidAbsent)), "an absent UUID is not found");
    }

    public void TestSwiftFindsElementsOfASetBuiltInCSharp()
    {
        var uuids = new HashSet<NSUuid> { new NSUuid(UuidA), new NSUuid(UuidB) };
        AssertTrue(TestLibFunctions.ObjectKeySetContainsUUID(uuids, UuidA), "the set holds the first UUID");
        AssertTrue(TestLibFunctions.ObjectKeySetContainsUUID(uuids, UuidB), "and the second");
        AssertFalse(TestLibFunctions.ObjectKeySetContainsUUID(uuids, UuidAbsent), "but not an absent one");

        var keys = new HashSet<NSString> { new NSString("NSFont"), new NSString("NSColor") };
        AssertTrue(TestLibFunctions.ObjectKeySetContainsAttribute(keys, "NSColor"), "a string-backed key set holds its key");
        AssertFalse(TestLibFunctions.ObjectKeySetContainsAttribute(keys, "NSKern"), "but not an absent one");
    }

    public void TestSwiftFindsKeysOfNestedOptionalAndArrayContainers()
    {
        var groups = new Dictionary<string, IDictionary<NSString, nint>>
        {
            { "body", Attributes("NSFont", "NSColor") },
            { "title", Attributes("NSKern") },
        };
        AssertEqual(11L, Long(TestLibFunctions.ObjectKeyNestedLookUp(groups, "body", "NSColor")), "inner dictionary key found");
        AssertEqual(10L, Long(TestLibFunctions.ObjectKeyNestedLookUp(groups, "title", "NSKern")), "second inner dictionary key found");
        AssertEqual(null, Long(TestLibFunctions.ObjectKeyNestedLookUp(groups, "title", "NSFont")), "a key of the other inner dictionary is absent");

        var list = new List<IDictionary<NSUuid, nint>>
        {
            new Dictionary<NSUuid, nint> { { new NSUuid(UuidA), 5 } },
            new Dictionary<NSUuid, nint> { { new NSUuid(UuidB), 6 } },
        };
        AssertEqual(6L, Long(TestLibFunctions.ObjectKeyArrayLookUp(list, 1, UuidB)), "a key of the array's second dictionary found");
        AssertEqual(null, Long(TestLibFunctions.ObjectKeyArrayLookUp(list, 0, UuidB)), "absent from the first");

        AssertEqual(11L, Long(TestLibFunctions.ObjectKeyOptionalLookUp(Attributes("NSFont", "NSColor"), "NSColor")), "optional dictionary key found");
        AssertEqual(-1L, Long(TestLibFunctions.ObjectKeyOptionalLookUp(null, "NSColor")), "nil dictionary crosses as nil");
    }

    // ── Swift builds, C# looks up ───────────────────────────────────────────────────────────

    public void TestCSharpFindsKeysOfADictionarySwiftReturned()
    {
        var attributes = TestLibFunctions.ObjectKeyMakeAttributes(new[] { "NSFont", "NSColor" });
        try
        {
            AssertEqual(2, attributes.Count, "both entries come back");
            AssertTrue(attributes.TryGetValue(new NSString("NSColor"), out var color), "C#'s own key object finds a present key");
            AssertEqual((nint)1, color, "and reads its value");
            AssertTrue(attributes.ContainsKey(new NSString("NSFont")), "ContainsKey finds the other");
            AssertFalse(attributes.ContainsKey(new NSString("NSKern")), "an absent key is not found");
        }
        finally
        {
            (attributes as IDisposable)?.Dispose();
        }

        var counts = TestLibFunctions.ObjectKeyMakeUUIDCounts(new[] { UuidA, UuidB });
        try
        {
            AssertTrue(counts.TryGetValue(new NSUuid(UuidB), out var index), "an equal NSUUID finds its entry");
            AssertEqual((nint)1, index, "and reads its value");
            AssertFalse(counts.ContainsKey(new NSUuid(UuidAbsent)), "an absent UUID is not found");
            var keys = counts.Keys.Select(k => k.AsString()).OrderBy(s => s, StringComparer.Ordinal).ToArray();
            AssertEqual($"{UuidB},{UuidA}", string.Join(",", keys), "the keys read back as the UUIDs Swift stored");
        }
        finally
        {
            (counts as IDisposable)?.Dispose();
        }
    }

    public void TestCSharpFindsElementsOfASetSwiftReturned()
    {
        var uuids = TestLibFunctions.ObjectKeyMakeUUIDSet(new[] { UuidA, UuidB });
        try
        {
            AssertEqual(2, uuids.Count, "both elements come back");
            AssertTrue(uuids.Contains(new NSUuid(UuidA)), "an equal NSUUID is found");
            AssertFalse(uuids.Contains(new NSUuid(UuidAbsent)), "an absent one is not");
            AssertTrue(uuids.Select(u => u.AsString()).Contains(UuidB), "the elements read back as the UUIDs Swift stored");
        }
        finally
        {
            (uuids as IDisposable)?.Dispose();
        }
    }

    public void TestDictionarySwiftReturnedFeedsBackIntoSwift()
    {
        var attributes = TestLibFunctions.ObjectKeyMakeAttributes(new[] { "NSFont", "NSColor" });
        try
        {
            var copy = new Dictionary<NSString, nint>(attributes);
            AssertEqual(1L, Long(TestLibFunctions.ObjectKeyLookUpAttribute(copy, "NSColor")), "a copy of Swift's dictionary is readable by Swift");
        }
        finally
        {
            (attributes as IDisposable)?.Dispose();
        }
    }

    public void TestClosureReceivesADictionaryCSharpCanLookUpIn()
    {
        nint result = TestLibFunctions.ObjectKeyCallWithAttributes(new[] { "NSFont", "NSColor", "NSKern" }, attributes =>
        {
            using var color = new NSString("NSColor");
            using var link = new NSString("NSLink");
            var colorKey = new SwiftObjectKey<__SwiftObjectKeys.Foundation_NSAttributedString_Key>(color.Handle);
            var linkKey = new SwiftObjectKey<__SwiftObjectKeys.Foundation_NSAttributedString_Key>(link.Handle);
            if (attributes.ContainsKey(linkKey))
                return -2;
            return attributes.TryGetValue(colorKey, out var value) ? value : -1;
        });
        AssertEqual((nint)1, result, "the closure finds a present key in the dictionary Swift handed it");
    }

    sealed class AttributeSource : IObjectKeyAttributeSource
    {
        public IReadOnlyDictionary<NSString, nint> GetAttributes() => Attributes("NSFont", "NSColor");

        public nint Value(IDictionary<NSString, nint> attributes, string name)
            => attributes.TryGetValue(new NSString(name), out var value) ? value : -1;
    }

    public void TestReverseDispatchBothDirections()
    {
        var source = new AttributeSource();
        AssertEqual(11L, Long(TestLibFunctions.ObjectKeyLookUpFromSource(source, "NSColor")), "Swift finds a key in the dictionary C# returned");
        AssertEqual(null, Long(TestLibFunctions.ObjectKeyLookUpFromSource(source, "NSKern")), "and misses an absent one");
        AssertEqual((nint)2, TestLibFunctions.ObjectKeyAskSource(source, new[] { "NSFont", "NSColor", "NSKern" }, "NSKern"),
            "C# finds a key in the dictionary Swift handed it");
        AssertEqual((nint)(-1), TestLibFunctions.ObjectKeyAskSource(source, new[] { "NSFont" }, "NSKern"),
            "and misses an absent one");
    }

    // ── Ownership ───────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Swift may keep a collection C# built after the call returns, so its storage must hold a
    /// reference on every key: after C# drops its key objects, the kept keys still read back.
    /// </summary>
    public void TestSwiftKeepsTheKeysOfADictionaryCSharpBuilt()
    {
        using var store = new ObjectKeyStore();
        KeepFreshAttributes(store);
        for (int i = 0; i < 4; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
        AssertEqual("NSColor,NSFont,NSKern", store.GetKeptNames(), "the kept keys still read back");
        AssertEqual(12L, Long(store.LookUp("NSKern")), "and are still found");
        store.Clear();
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    static void KeepFreshAttributes(ObjectKeyStore store)
    {
        var attributes = Attributes("NSFont", "NSColor", "NSKern");
        store.Keep(attributes);
        foreach (var key in attributes.Keys)
            key.Dispose();
    }

    /// <summary>
    /// A collection C# builds holds exactly one reference per key while Swift keeps it, and none
    /// once Swift lets it go; a call that only reads it leaves the count where it was.
    /// </summary>
    public void TestKeysOfACollectionCSharpBuiltAreRetainedOnceAndReleased()
    {
        using var uuid = new NSUuid(UuidA);
        using var store = new ObjectKeyStore();
        nint baseline = TestLibFunctions.ObjectKeyRetainCount(uuid);

        store.KeepUUIDs(new HashSet<NSUuid> { uuid });
        nint kept = TestLibFunctions.ObjectKeyRetainCount(uuid);
        AssertEqual(baseline + 1, kept, "the set Swift keeps holds one reference on its element");
        AssertEqual(UuidA, store.GetKeptUUIDStrings(), "and reads it back");

        store.Clear();
        AssertEqual(baseline, TestLibFunctions.ObjectKeyRetainCount(uuid), "clearing the set releases it");

        for (int i = 0; i < 25; i++)
        {
            var counts = new Dictionary<NSUuid, nint> { { uuid, i } };
            AssertEqual((long)i, Long(TestLibFunctions.ObjectKeyLookUpUUID(counts, UuidA)), "the key is found");
            AssertTrue(TestLibFunctions.ObjectKeySetContainsUUID(new HashSet<NSUuid> { uuid }, UuidA), "the element is found");
        }
        AssertEqual(baseline, TestLibFunctions.ObjectKeyRetainCount(uuid), "25 calls that only read the key leave its retain count where it was");
    }
}
