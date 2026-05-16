// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Diagnostics.CodeAnalysis;
using Foundation;
using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Collisions;

/// <summary>
/// N-1: two Swift overloads that differ only in the generic element type of
/// their Array parameter must both emit. Pre-fix, the primary dedup key
/// collapsed `Array<T>` to bare `Swift.SwiftArray`, dropping the second
/// overload entirely.
/// </summary>
public class ContainerElementOverloadCollisionTests : TestBase
{
    public ContainerElementOverloadCollisionTests(TestResults results) : base(results) { }

    #region ItemEnqueuer (two custom Swift class element types)

    public void TestItemEnqueuerArrayA()
    {
        using var q = new ItemEnqueuer();
        using var a1 = new FetchItemA("first");
        using var a2 = new FetchItemA("second");
        q.Enqueue(new[] { a1, a2 });
        AssertEqual("A", q.LastTag.ToString(), "Array<A> overload dispatched");
        AssertEqual(2, q.LastCount, "Count preserved through array marshalling");
    }

    public void TestItemEnqueuerArrayB()
    {
        using var q = new ItemEnqueuer();
        using var b1 = new FetchItemB(10);
        using var b2 = new FetchItemB(20);
        using var b3 = new FetchItemB(30);
        q.Enqueue(new[] { b1, b2, b3 });
        AssertEqual("B", q.LastTag.ToString(), "Array<B> overload dispatched");
        AssertEqual(3, q.LastCount, "Count preserved through array marshalling");
    }

    [DynamicDependency(
        DynamicallyAccessedMemberTypes.PublicMethods,
        typeof(ItemEnqueuer))]
    public void TestItemEnqueuerBothOverloadsExist()
    {
        // Compile-time proof that the pre-fix primary-dedup collapse is gone:
        // both `IEnumerable<FetchItemA>` and `IEnumerable<FetchItemB>` overloads
        // must be present.
        var aOverload = typeof(ItemEnqueuer).GetMethod(
            "Enqueue", new[] { typeof(IEnumerable<FetchItemA>) });
        var bOverload = typeof(ItemEnqueuer).GetMethod(
            "Enqueue", new[] { typeof(IEnumerable<FetchItemB>) });
        AssertNotNull(aOverload, "Enqueue(IEnumerable<FetchItemA>) exists");
        AssertNotNull(bOverload, "Enqueue(IEnumerable<FetchItemB>) exists");
    }

    #endregion

    #region UrlPrefetcher (ObjC-bridge container vs custom Swift class — Nuke shape)

    public void TestUrlPrefetcherUrlOverload()
    {
        using var p = new UrlPrefetcher();
        using var u1 = new NSUrl("https://example.com/a");
        using var u2 = new NSUrl("https://example.com/b");
        p.Prefetch(new[] { u1, u2 });
        AssertEqual("url", p.LastSource.ToString(), "[URL]/NSUrl overload dispatched");
        AssertEqual(2, p.LastCount, "URL count preserved");
    }

    public void TestUrlPrefetcherItemOverload()
    {
        using var p = new UrlPrefetcher();
        using var a1 = new FetchItemA("alpha");
        p.Prefetch(new[] { a1 });
        AssertEqual("item", p.LastSource.ToString(), "[FetchItemA] overload dispatched");
        AssertEqual(1, p.LastCount, "Item count preserved");
    }

    [DynamicDependency(
        DynamicallyAccessedMemberTypes.PublicMethods,
        typeof(UrlPrefetcher))]
    public void TestUrlPrefetcherBothOverloadsExist()
    {
        // The Nuke `startPrefetching([URL]) / startPrefetching([ImageRequest])`
        // shape: one overload bridges through NSArray of NSUrl (ObjC bridge),
        // the other through a typed Swift array. Both must reach C# consumers.
        var urlOverload = typeof(UrlPrefetcher).GetMethod(
            "Prefetch", new[] { typeof(IEnumerable<NSUrl>) });
        var itemOverload = typeof(UrlPrefetcher).GetMethod(
            "Prefetch", new[] { typeof(IEnumerable<FetchItemA>) });
        AssertNotNull(urlOverload, "Prefetch(IEnumerable<NSUrl>) exists");
        AssertNotNull(itemOverload, "Prefetch(IEnumerable<FetchItemA>) exists");
    }

    #endregion

    #region Free function variant (ModuleHandler primary-dedup path)

    public void TestEnqueueItemsArrayA()
    {
        using var a1 = new FetchItemA("x");
        using var a2 = new FetchItemA("y");
        var result = TestLibFunctions.EnqueueItems(new[] { a1, a2 });
        AssertEqual("A:2", result, "Free function A overload dispatched");
    }

    public void TestEnqueueItemsArrayB()
    {
        using var b1 = new FetchItemB(7);
        var result = TestLibFunctions.EnqueueItems(new[] { b1 });
        AssertEqual("B:1", result, "Free function B overload dispatched");
    }

    [DynamicDependency(
        DynamicallyAccessedMemberTypes.PublicMethods,
        typeof(TestLibFunctions))]
    public void TestEnqueueItemsBothOverloadsExist()
    {
        var aOverload = typeof(TestLibFunctions).GetMethod(
            "EnqueueItems", new[] { typeof(IEnumerable<FetchItemA>) });
        var bOverload = typeof(TestLibFunctions).GetMethod(
            "EnqueueItems", new[] { typeof(IEnumerable<FetchItemB>) });
        AssertNotNull(aOverload, "EnqueueItems(IEnumerable<FetchItemA>) exists");
        AssertNotNull(bOverload, "EnqueueItems(IEnumerable<FetchItemB>) exists");
    }

    #endregion
}
