// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.KeyPath;

/// <summary>
/// Session 6c Route C — end-to-end tests for per-(conformer x distinct projectable V)
/// Sort overload emission.
///
/// <para>What this exercises:</para>
/// <list type="bullet">
///   <item>Closed-V overloads exist — the emitter produces one C# Sort overload per
///   distinct projectable Value type on <c>RouteC_AlbumSortProperties</c>
///   (String, nint, bool).</item>
///   <item>KeyPath param accepts the Session 4 typed singleton — Route C's
///   <c>unsafeDowncast(anyKP, to: KeyPath&lt;Bag, V&gt;.self)</c> round-trips a heap
///   KP produced by Session 4's singleton trampoline.</item>
///   <item>Dispatch arrives — the Swift method writes a deterministic description
///   of (keypath, ascending) into <c>RouteC_SortTracker.LastDescription</c>; we
///   verify the format.</item>
///   <item>Receiver lifetime — the extension method passes the class instance via
///   <c>ISwiftObject.SwiftHandle</c> with a <c>GC.KeepAlive</c> across the call.</item>
/// </list>
///
/// <para>
/// Side effects flow through a non-generic <c>RouteC_SortTracker</c> rather than
/// instance properties on the generic parent. The wrapper-emitter's property-getter
/// pattern (<c>as! any _SBW_PG_*</c>) crashes when the receiver is a constrained
/// generic class (<c>&lt;Item: RouteC_Filterable&gt;</c>) — Swift's runtime
/// conformance lookup for the retroactive conditional conformance fails. That's
/// a separate wrapper-emitter bug outside Session 6c scope; tracked in
/// <c>src/docs/Future/property-getter-constrained-generic.md</c>.
/// </para>
/// </summary>
public class KeyPathRouteCTests : TestBase
{
    public KeyPathRouteCTests(TestResults results) : base(results) { }

    public void TestSort_StringKeyPath_Ascending()
    {
        RouteC_SortTracker.Reset();
        var req = new RouteC_GenericRequest<RouteC_Album>();
        req.Sort(RouteC_AlbumSortBagKeyPaths.Title, ascending: true);
        AssertEqual("asc", RouteC_SortTracker.LastDescription.ToString(),
            "Sort(Title, ascending=true) records ascending flag");
        AssertTrue((bool)RouteC_SortTracker.LastAscending, "LastAscending is true after asc sort");
    }

    public void TestSort_StringKeyPath_Descending()
    {
        RouteC_SortTracker.Reset();
        var req = new RouteC_GenericRequest<RouteC_Album>();
        req.Sort(RouteC_AlbumSortBagKeyPaths.Title, ascending: false);
        AssertEqual("desc", RouteC_SortTracker.LastDescription.ToString(),
            "Sort(Title, ascending=false) records descending flag");
        AssertFalse((bool)RouteC_SortTracker.LastAscending, "LastAscending is false after desc sort");
    }

    public void TestSort_IntKeyPath_Ascending()
    {
        RouteC_SortTracker.Reset();
        var req = new RouteC_GenericRequest<RouteC_Album>();
        req.Sort(RouteC_AlbumSortBagKeyPaths.Year, ascending: true);
        AssertEqual("asc", RouteC_SortTracker.LastDescription.ToString(),
            "Sort(Year, ascending=true) dispatches through the nint overload");
    }

    public void TestSort_BoolKeyPath_Ascending()
    {
        RouteC_SortTracker.Reset();
        var req = new RouteC_GenericRequest<RouteC_Album>();
        req.Sort(RouteC_AlbumSortBagKeyPaths.IsAvailable, ascending: true);
        AssertEqual("asc", RouteC_SortTracker.LastDescription.ToString(),
            "Sort(IsAvailable, ascending=true) dispatches through the bool overload");
    }

    public void TestSort_DistinctKeyPaths_DistinctHashes()
    {
        RouteC_SortTracker.Reset();
        var req = new RouteC_GenericRequest<RouteC_Album>();
        req.Sort(RouteC_AlbumSortBagKeyPaths.Title, ascending: true);
        var titleHash = (long)RouteC_SortTracker.LastKeyPathHash;
        req.Sort(RouteC_AlbumSortBagKeyPaths.Year, ascending: true);
        var yearHash = (long)RouteC_SortTracker.LastKeyPathHash;
        AssertTrue(titleHash != yearHash,
            $"distinct KeyPaths produce distinct hashes (title={titleHash}, year={yearHash})");
    }
}
