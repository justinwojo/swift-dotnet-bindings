// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Marshalling;

/// <summary>
/// Regression coverage for Issue 5 — `ContainsRemappedObjCTypeInGenericArgs`
/// suppression in `EnumHandler.CaseConstruction.cs`. Before the narrowing,
/// payload-case factories for stdlib containers holding ObjC-bridged
/// elements (`UIImage?`, `NSURL?`, `[URL]`) were stripped from the emitted
/// bindings. This file confirms those factories exist and round-trip.
///
/// Fixture: <see cref="UpdatingStrategy"/> in MultiAssociatedValues.swift.
/// </summary>
public class EnumObjCBridgedPayloadTests : TestBase
{
    public EnumObjCBridgedPayloadTests(TestResults results) : base(results) { }

    #region Singleton no-payload cases

    public void TestNoneSingletonExists()
    {
        var strategy = UpdatingStrategy.None;
        AssertNotNull(strategy, "UpdatingStrategy.None singleton exists");
        AssertEqual(UpdatingStrategy.CaseTag.None, strategy.Tag, "None tag");
    }

    public void TestKeepSingletonExists()
    {
        var strategy = UpdatingStrategy.Keep;
        AssertNotNull(strategy, "UpdatingStrategy.Keep singleton exists");
        AssertEqual(UpdatingStrategy.CaseTag.Keep, strategy.Tag, "Keep tag");
    }

    #endregion

    #region Optional<URL> payload (Issue 5 primary regression)

    public void TestReplaceFactoryEmittedWithNonNullURL()
    {
        // The whole point of Issue 5: this static factory must exist.
        var url = Foundation.NSUrl.FromString("https://example.com/icon.png")!;
        using var strategy = UpdatingStrategy.Replace(url);
        AssertNotNull(strategy, "UpdatingStrategy.Replace(url) static factory exists");
        AssertEqual(UpdatingStrategy.CaseTag.Replace, strategy.Tag, "Replace tag");
    }

    public void TestReplaceFactoryAcceptsNullURL()
    {
        // Payload is `URL?` — nil must survive the crossing.
        using var strategy = UpdatingStrategy.Replace(null);
        AssertNotNull(strategy, "UpdatingStrategy.Replace(null) constructs");
        AssertEqual(UpdatingStrategy.CaseTag.Replace, strategy.Tag, "Replace tag (nil payload)");
    }

    public void TestReplaceRoundTripsURLThroughFreeFunction()
    {
        var url = Foundation.NSUrl.FromString("https://example.com/replace")!;
        using var strategy = UpdatingStrategy.Replace(url);
        var described = TestLibFunctions.DescribeUpdatingStrategy(strategy);
        AssertEqual("https://example.com/replace", described, "Replace URL survives round-trip");
    }

    public void TestReplaceWithNullDescribesAsNil()
    {
        using var strategy = UpdatingStrategy.Replace(null);
        var described = TestLibFunctions.DescribeUpdatingStrategy(strategy);
        AssertEqual("<nil>", described, "Replace(nil) describes as <nil>");
    }

    #endregion

    #region Array<URL> payload

    public void TestLoadAllFactoryEmittedWithURLArray()
    {
        var urls = new[]
        {
            Foundation.NSUrl.FromString("https://one.com")!,
            Foundation.NSUrl.FromString("https://two.com")!,
            Foundation.NSUrl.FromString("https://three.com")!
        };
        using var strategy = UpdatingStrategy.LoadAll(urls);
        AssertNotNull(strategy, "UpdatingStrategy.LoadAll([URL]) static factory exists");
        AssertEqual(UpdatingStrategy.CaseTag.LoadAll, strategy.Tag, "LoadAll tag");
    }

    public void TestLoadAllRoundTripsArrayCount()
    {
        var urls = new[]
        {
            Foundation.NSUrl.FromString("https://a.com")!,
            Foundation.NSUrl.FromString("https://b.com")!
        };
        using var strategy = UpdatingStrategy.LoadAll(urls);
        var described = TestLibFunctions.DescribeUpdatingStrategy(strategy);
        AssertEqual("count=2", described, "LoadAll array length survives round-trip");
    }

    public void TestLoadAllAcceptsEmptyArray()
    {
        using var strategy = UpdatingStrategy.LoadAll(Array.Empty<Foundation.NSUrl>());
        var described = TestLibFunctions.DescribeUpdatingStrategy(strategy);
        AssertEqual("count=0", described, "LoadAll accepts empty array");
    }

    #endregion

    #region Optional<Array<URL>> double-wrapped payload (Codex P2 regression)

    public void TestMaybeLoadAllFactoryAcceptsURLArray()
    {
        // Payload is Optional<Array<URL>> — Swift nil-pointer-optimizes to a single
        // IntPtr. The enum-payload fast path has to recognize the nested bridge;
        // otherwise it falls back to MarshalFromSwift<SwiftOptional<SwiftArray<IntPtr>>>
        // and reads the wrong ABI.
        var urls = new[]
        {
            Foundation.NSUrl.FromString("https://x.com")!,
            Foundation.NSUrl.FromString("https://y.com")!
        };
        using var strategy = UpdatingStrategy.MaybeLoadAll(urls);
        AssertNotNull(strategy, "UpdatingStrategy.MaybeLoadAll([URL]?) static factory exists");
        AssertEqual(UpdatingStrategy.CaseTag.MaybeLoadAll, strategy.Tag, "MaybeLoadAll tag (some)");
    }

    public void TestMaybeLoadAllFactoryAcceptsNull()
    {
        using var strategy = UpdatingStrategy.MaybeLoadAll(null);
        AssertNotNull(strategy, "UpdatingStrategy.MaybeLoadAll(null) constructs");
        AssertEqual(UpdatingStrategy.CaseTag.MaybeLoadAll, strategy.Tag, "MaybeLoadAll tag (nil)");
    }

    public void TestMaybeLoadAllRoundTripsSomeCount()
    {
        var urls = new[]
        {
            Foundation.NSUrl.FromString("https://a.com")!,
            Foundation.NSUrl.FromString("https://b.com")!,
            Foundation.NSUrl.FromString("https://c.com")!
        };
        using var strategy = UpdatingStrategy.MaybeLoadAll(urls);
        var described = TestLibFunctions.DescribeUpdatingStrategy(strategy);
        AssertEqual("maybe=3", described, "MaybeLoadAll(some) count survives round-trip");
    }

    public void TestMaybeLoadAllRoundTripsNil()
    {
        using var strategy = UpdatingStrategy.MaybeLoadAll(null);
        var described = TestLibFunctions.DescribeUpdatingStrategy(strategy);
        AssertEqual("maybe=<nil>", described, "MaybeLoadAll(nil) describes as <nil>");
    }

    #endregion

    #region Free-function description covers all cases

    public void TestDescribeNoneCase()
    {
        var described = TestLibFunctions.DescribeUpdatingStrategy(UpdatingStrategy.None);
        AssertEqual("<none>", described, "None describes correctly");
    }

    public void TestDescribeKeepCase()
    {
        var described = TestLibFunctions.DescribeUpdatingStrategy(UpdatingStrategy.Keep);
        AssertEqual("<keep>", described, "Keep describes correctly");
    }

    #endregion
}
