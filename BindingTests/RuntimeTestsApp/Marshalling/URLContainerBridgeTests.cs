// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;

namespace RuntimeTestsApp.Marshalling;

/// <summary>
/// Tests ObjC bridge projection for containers of ObjC-bridgeable elements.
/// When a container holds ObjC-bridgeable elements (URL), the entire container bridges
/// to its ObjC collection counterpart (NSArray/NSDictionary/NSSet) at the @_cdecl boundary.
/// </summary>
public class URLContainerBridgeTests : TestBase
{
    public URLContainerBridgeTests(TestResults results) : base(results) { }

    #region Array Return

    public void TestGetURLArray()
    {
        using var helper = new SwiftBindingsTestLib.URLContainerTestHelper();
        var urls = helper.GetURLArray();
        AssertNotNull(urls, "GetURLArray returns non-null");
        AssertEqual(2, urls!.Count, "GetURLArray returns 2 elements");
        AssertEqual("https://example.com", urls[0]!.AbsoluteString, "First URL preserved");
        AssertEqual("https://test.com", urls[1]!.AbsoluteString, "Second URL preserved");
    }

    public void TestGetEmptyURLArray()
    {
        using var helper = new SwiftBindingsTestLib.URLContainerTestHelper();
        var urls = helper.GetEmptyURLArray();
        AssertNotNull(urls, "GetEmptyURLArray returns non-null");
        AssertEqual(0, urls!.Count, "Empty array has 0 elements");
    }

    #endregion

    #region Scalar URL parameter — string convenience overload

    // A scalar `URL` parameter projects to an `NSUrl` primary; the generator emits an
    // additive `string`-taking overload that forwards through `new NSUrl(s)`. Both the
    // primary and the string overload must reach the same Swift entry point and round-trip.

    public void TestDescribeURL_StringOverload_RoundTrips()
    {
        using var helper = new SwiftBindingsTestLib.URLContainerTestHelper();
        // Calls the additive `string` overload — no hand-constructed NSUrl at the call site.
        var described = helper.DescribeURL("https://example.com");
        AssertEqual("https://example.com", described, "String overload round-trips URL through absoluteString");
    }

    public void TestDescribeURL_NSUrlPrimary_RoundTrips()
    {
        using var helper = new SwiftBindingsTestLib.URLContainerTestHelper();
        // The NSUrl primary the overload forwards to still works directly.
        var described = helper.DescribeURL(Foundation.NSUrl.FromString("https://primary.example.com")!);
        AssertEqual("https://primary.example.com", described, "NSUrl primary round-trips URL through absoluteString");
    }

    #endregion

    #region Array Parameter

    public void TestAcceptURLArray()
    {
        using var helper = new SwiftBindingsTestLib.URLContainerTestHelper();
        var urls = new[]
        {
            Foundation.NSUrl.FromString("https://one.com")!,
            Foundation.NSUrl.FromString("https://two.com")!,
            Foundation.NSUrl.FromString("https://three.com")!
        };
        var count = helper.AcceptURLArray(urls);
        AssertEqual(3, count, "AcceptURLArray receives correct count");
    }

    public void TestAcceptEmptyURLArray()
    {
        using var helper = new SwiftBindingsTestLib.URLContainerTestHelper();
        var count = helper.AcceptEmptyURLArray(Array.Empty<Foundation.NSUrl>());
        AssertEqual(0, count, "AcceptEmptyURLArray receives 0 elements");
    }

    #endregion

    #region Dictionary Return

    public void TestGetURLDictionary()
    {
        using var helper = new SwiftBindingsTestLib.URLContainerTestHelper();
        var dict = helper.GetURLDictionary();
        AssertNotNull(dict, "GetURLDictionary returns non-null");
        AssertEqual(2, dict!.Count, "Dictionary has 2 entries");
        AssertTrue(dict.ContainsKey("home"), "Dictionary contains 'home' key");
        AssertTrue(dict.ContainsKey("api"), "Dictionary contains 'api' key");
        AssertEqual("https://example.com", dict["home"]!.AbsoluteString, "'home' URL preserved");
        AssertEqual("https://api.example.com", dict["api"]!.AbsoluteString, "'api' URL preserved");
    }

    public void TestGetURLsBySample()
    {
        // Pins the [Int: URL] NSDictionary integer-key unboxing fix. Before the fix,
        // FromNSObject emitted (nint)_nsKey for the Swift.Int → nint key, which
        // failed CS0030 because NSDictionary.Keys is NSObject[] holding boxed NSNumber.
        // The fix routes BlittableProjection numeric keys through NSNumber accessors
        // (NIntValue here). RealityFoundation's UrlsBySample triggered the original error.
        using var helper = new SwiftBindingsTestLib.URLContainerTestHelper();
        var dict = helper.GetURLsBySample();
        AssertNotNull(dict, "GetURLsBySample returns non-null");
        AssertEqual(2, dict!.Count, "Dictionary has 2 entries");
        AssertTrue(dict.ContainsKey(10), "Dictionary contains key 10");
        AssertTrue(dict.ContainsKey(42), "Dictionary contains key 42");
        AssertEqual("https://sample-10.example.com", dict[10]!.AbsoluteString,
            "Key 10 URL preserved (NSNumber unbox round-trip)");
        AssertEqual("https://sample-42.example.com", dict[42]!.AbsoluteString,
            "Key 42 URL preserved (NSNumber unbox round-trip)");
    }

    #endregion

    #region Set Return

    public void TestGetURLSet()
    {
        using var helper = new SwiftBindingsTestLib.URLContainerTestHelper();
        var set = helper.GetURLSet();
        AssertNotNull(set, "GetURLSet returns non-null");
        AssertEqual(2, set!.Count, "Set has 2 elements");
    }

    #endregion

    #region Set Parameter

    public void TestAcceptURLSet()
    {
        // Swift `Set<URL>` parameter projects to `IReadOnlySet<NSUrl>` post-Bundle-04 #9
        // (was `IEnumerable<NSUrl>` pre-fix — the uniqueness invariant was being dropped
        // at the public API surface). Caller must construct an IReadOnlySet<NSUrl>;
        // HashSet<NSUrl> is the natural shape on the C# side.
        using var helper = new SwiftBindingsTestLib.URLContainerTestHelper();
        var urls = new HashSet<Foundation.NSUrl>
        {
            Foundation.NSUrl.FromString("https://one.com")!,
            Foundation.NSUrl.FromString("https://two.com")!
        };
        var count = helper.AcceptURLSet(urls);
        AssertEqual(2, count, "AcceptURLSet receives correct count");
    }

    #endregion

    #region Nested Array Parameter

    public void TestAcceptNestedURLArray()
    {
        using var helper = new SwiftBindingsTestLib.URLContainerTestHelper();
        var nested = new[]
        {
            new[] { Foundation.NSUrl.FromString("https://a.com")! } as IEnumerable<Foundation.NSUrl>,
            new[] { Foundation.NSUrl.FromString("https://b.com")!, Foundation.NSUrl.FromString("https://c.com")! } as IEnumerable<Foundation.NSUrl>
        };
        var totalCount = helper.AcceptNestedURLArray(nested);
        AssertEqual(3, totalCount, "AcceptNestedURLArray receives correct total count");
    }

    #endregion

    #region Nested Array Return

    public void TestGetNestedURLArray()
    {
        using var helper = new SwiftBindingsTestLib.URLContainerTestHelper();
        var nested = helper.GetNestedURLArray();
        AssertNotNull(nested, "GetNestedURLArray returns non-null");
        AssertEqual(2, nested!.Count, "Outer array has 2 elements");
        AssertEqual(1, nested[0]!.Count, "First inner array has 1 element");
        AssertEqual(2, nested[1]!.Count, "Second inner array has 2 elements");
        AssertEqual("https://a.com", nested[0][0]!.AbsoluteString, "Nested URL a.com preserved");
        AssertEqual("https://b.com", nested[1][0]!.AbsoluteString, "Nested URL b.com preserved");
        AssertEqual("https://c.com", nested[1][1]!.AbsoluteString, "Nested URL c.com preserved");
    }

    #endregion
}
