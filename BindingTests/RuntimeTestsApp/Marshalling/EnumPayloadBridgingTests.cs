// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Foundation;
using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Marshalling;

/// <summary>
/// Enum payloads that hold Objective-C-bridgeable Swift values (<c>URL</c>, <c>IndexPath</c>,
/// <c>Decimal</c>) and collections of them. The payload sits in the enum as Swift values, so
/// TryGet reads it through a Swift bridge that returns one retained Objective-C object; the
/// binding adopts that +1 exactly once. Also covers an existential payload whose protocol has no
/// record, whose TryGet is refused. Fixture: Enums/EnumPayloadBridging.swift.
/// </summary>
public class EnumPayloadBridgingTests : TestBase
{
    public EnumPayloadBridgingTests(TestResults results) : base(results) { }

    #region Single bridged values

    public void TestUrlPayloadRoundTrips()
    {
        var url = NSUrl.FromString("https://bridged.example/single")!;
        using var payload = BridgedValuePayload.Link(url);
        for (int i = 0; i < 3; i++)
        {
            AssertTrue(payload.TryGetLink(out var read), $"TryGetLink read {i}");
            AssertEqual("https://bridged.example/single", read!.AbsoluteString, $"URL read {i}");
        }
        AssertFalse(payload.TryGetPath(out _), "Other cases do not match");
    }

    public void TestOptionalUrlPayloadRoundTrips()
    {
        using var some = BridgedValuePayload.MaybeLink(NSUrl.FromString("https://bridged.example/maybe")!);
        AssertTrue(some.TryGetMaybeLink(out var read), "TryGetMaybeLink matches");
        AssertEqual("https://bridged.example/maybe", read?.AbsoluteString, "Optional URL some");

        using var none = BridgedValuePayload.MaybeLink(null);
        AssertTrue(none.TryGetMaybeLink(out var empty), "TryGetMaybeLink matches nil payload");
        AssertNull(empty, "Optional URL nil");
    }

    public void TestIndexPathPayloadRoundTrips()
    {
        using var payload = BridgedValuePayload.Path(NSIndexPath.FromIndex(4));
        AssertTrue(payload.TryGetPath(out var read), "TryGetPath matches");
        AssertEqual(1L, (long)read!.Length, "IndexPath length");
        AssertEqual(4L, (long)read.IndexAtPosition(0), "IndexPath index");
    }

    public void TestDecimalPayloadRoundTrips()
    {
        using var payload = BridgedValuePayload.MakeAmount("12.75");
        AssertTrue(payload.TryGetAmount(out var read), "TryGetAmount matches");
        AssertEqual("12.75", read!.StringValue, "Decimal value");
    }

    #endregion

    #region Bridged collections

    public void TestUrlArrayPayloadBridgesElements()
    {
        using var pool = new NSAutoreleasePool();
        using var payload = BridgedValuePayload.MakeLinks(3);
        for (int i = 0; i < 2; i++)
        {
            AssertTrue(payload.TryGetLinks(out var links), $"TryGetLinks read {i}");
            AssertEqual(3, links!.Count, $"Array count read {i}");
            for (int j = 0; j < links.Count; j++)
                AssertEqual($"https://bridged.example/{j}", links[j].AbsoluteString, $"Element {j} read {i}");
        }
    }

    public void TestOptionalUrlArrayPayload()
    {
        var urls = new[] { NSUrl.FromString("https://bridged.example/a")!, NSUrl.FromString("https://bridged.example/b")! };
        using var some = BridgedValuePayload.MaybeLinks(urls);
        AssertTrue(some.TryGetMaybeLinks(out var read), "TryGetMaybeLinks matches");
        AssertEqual(2, read!.Count, "Optional array count");
        AssertEqual("https://bridged.example/b", read[1].AbsoluteString, "Optional array element");

        using var none = BridgedValuePayload.MaybeLinks(null);
        AssertTrue(none.TryGetMaybeLinks(out var empty), "TryGetMaybeLinks matches nil payload");
        AssertNull(empty, "Optional array nil");
    }

    public void TestUrlDictionaryPayloadBridgesValues()
    {
        using var pool = new NSAutoreleasePool();
        using var payload = BridgedValuePayload.MakeLinkMap(3);
        AssertTrue(payload.TryGetLinkMap(out var map), "TryGetLinkMap matches");
        AssertEqual(3, map!.Count, "Dictionary count");
        for (int i = 0; i < 3; i++)
            AssertEqual($"https://bridged.example/map/{i}", map[$"k{i}"].AbsoluteString, $"Dictionary value k{i}");
    }

    public void TestUrlSetPayloadBridgesElements()
    {
        using var pool = new NSAutoreleasePool();
        using var payload = BridgedValuePayload.MakeLinkSet(3);
        AssertTrue(payload.TryGetLinkSet(out var set), "TryGetLinkSet matches");
        AssertEqual(3, set!.Count, "Set count");
        var strings = set.Select(u => u.AbsoluteString).OrderBy(s => s, StringComparer.Ordinal).ToArray();
        for (int i = 0; i < 3; i++)
            AssertEqual($"https://bridged.example/set/{i}", strings[i], $"Set element {i}");
    }

    public void TestIndexPathArrayPayloadBridgesElements()
    {
        using var pool = new NSAutoreleasePool();
        using var payload = BridgedValuePayload.MakePaths(3);
        AssertTrue(payload.TryGetPaths(out var paths), "TryGetPaths matches");
        AssertEqual(3, paths!.Count, "Path array count");
        for (int i = 0; i < 3; i++)
            AssertEqual((nuint)i, paths[i].IndexAtPosition(0), $"Path {i}");
    }

    public void TestLabelledTuplePayloadConstructsWithoutTryGet()
    {
        var urls = new[] { NSUrl.FromString("https://bridged.example/t")! };
        using var payload = BridgedValuePayload.LabelledLinks(urls, 1);
        AssertEqual(BridgedValuePayload.CaseTag.LabelledLinks, payload.Tag, "Tuple payload constructs");
        AssertTrue(typeof(BridgedValuePayload).GetMethod("TryGetLabelledLinks") is null,
            "TryGet for a tuple payload holding a bridged collection is refused");
    }

    public void TestNestedOptionalBridgedPayloadsHaveNoTryGet()
    {
        AssertTrue(typeof(BridgedValuePayload).GetMethod("TryGetNestedLink") is null,
            "TryGet for a doubly optional bridgeable value is refused");
        AssertTrue(typeof(BridgedValuePayload).GetMethod("TryGetNestedLinks") is null,
            "TryGet for a doubly optional bridged collection is refused");
    }

    #endregion

    #region Ownership: each read adopts exactly one reference

    // Reads every collection case repeatedly, then drops the enum. A read that retained the
    // Swift storage without the binding releasing it, or a bridged container nobody released,
    // keeps the storage alive; a read that released once too often crashes on the next read.
    public void TestArrayPayloadStorageReleasedAfterReads()
        => AssertStorageReleased(() => BridgedValuePayload.MakeLinks(4), p => { p.TryGetLinks(out var v); return v!.Count; }, 4, "[URL]");

    public void TestDictionaryPayloadStorageReleasedAfterReads()
        => AssertStorageReleased(() => BridgedValuePayload.MakeLinkMap(4), p => { p.TryGetLinkMap(out var v); return v!.Count; }, 4, "[String: URL]");

    public void TestSetPayloadStorageReleasedAfterReads()
        => AssertStorageReleased(() => BridgedValuePayload.MakeLinkSet(4), p => { p.TryGetLinkSet(out var v); return v!.Count; }, 4, "Set<URL>");

    public void TestIndexPathArrayStorageReleasedAfterReads()
        => AssertStorageReleased(() => BridgedValuePayload.MakePaths(4), p => { p.TryGetPaths(out var v); return v!.Count; }, 4, "[IndexPath]");

    private void AssertStorageReleased(Func<BridgedValuePayload> make, Func<BridgedValuePayload, int> read, int expectedCount, string shape)
    {
        RunOnFinishedThread(() =>
        {
            using var pool = new NSAutoreleasePool();
            using var payload = make();
            AssertTrue(TestLibFunctions.IsBridgedPayloadStorageAlive(), $"{shape}: storage alive while the enum holds it");
            for (int i = 0; i < 5; i++)
                AssertEqual(expectedCount, read(payload), $"{shape}: read {i}");
            AssertTrue(TestLibFunctions.IsBridgedPayloadStorageAlive(), $"{shape}: reads leave the payload intact");
        });
        AssertTrue(WaitForStorageRelease(), $"{shape}: storage released once the enum and the read results are gone");
    }

    private static bool WaitForStorageRelease()
    {
        for (int attempt = 0; attempt < 60; attempt++)
        {
            if (!TestLibFunctions.IsBridgedPayloadStorageAlive())
                return true;
            ForceGCThorough();
            using (new NSAutoreleasePool())
                NSRunLoop.Current.RunUntil(NSDate.FromTimeIntervalSinceNow(0.05));
        }
        return !TestLibFunctions.IsBridgedPayloadStorageAlive();
    }

    #endregion

    #region Existential payload with no protocol record

    public void TestUnrecordedExistentialTryGetIsRefused()
    {
        AssertTrue(typeof(UnrecordedExistentialPayload).GetMethod("TryGetDescribed") is null,
            "No TryGet for an existential payload no proxy can own");
    }

    public void TestUnrecordedExistentialPayloadReleasesOnDispose()
    {
        var before = TestLibFunctions.GetDescribedTokenLiveCount();
        RunOnFinishedThread(() =>
        {
            using var payload = UnrecordedExistentialPayload.MakeDescribed(9);
            AssertEqual(before + 1, TestLibFunctions.GetDescribedTokenLiveCount(), "Payload holds one token");
            AssertEqual(UnrecordedExistentialPayload.CaseTag.Described, payload.Tag, "Described tag");
            for (int i = 0; i < 3; i++)
                AssertEqual("token9", payload.PayloadDescription, $"Swift reads the payload {i}");
        });
        ForceGCThorough();
        AssertEqual(before, TestLibFunctions.GetDescribedTokenLiveCount(), "Dispose releases the token exactly once");
    }

    #endregion
}
