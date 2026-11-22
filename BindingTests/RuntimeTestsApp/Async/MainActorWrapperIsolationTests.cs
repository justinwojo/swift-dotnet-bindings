// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Threading;
using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;
using SwiftBindingsTestLibDependency;
// The test library declares its own DependencyPoint too; these members extend the dependency's.
using DependencyPoint = SwiftBindingsTestLibDependency.DependencyPoint;

namespace RuntimeTestsApp.Async;

/// <summary>
/// Main-actor members reached through wrapper emitters that once wrote a nonisolated wrapper:
/// the operator wrapper of a frozen struct, the member wrappers of a simple enum, the trampoline
/// of a cross-module extension on a dependency's frozen struct, and the Route C keypath-sort
/// trampoline. swiftc refuses such a wrapper, so before the fix verify-recover withdrew every one
/// of these members; with the isolation carried over, each call must reach the member and return
/// Swift's value. The runner calls these on the main thread.
/// </summary>
public class MainActorWrapperIsolationTests : TestBase
{
    public MainActorWrapperIsolationTests(TestResults results) : base(results) { }

    public void TestInfixOperatorOfAMainActorStructReachesTheMember()
    {
        var sum = new MainActorTally(3) + new MainActorTally(4);
        AssertEqual(7, sum.Count, "MainActorTally + returned Swift's sum");
    }

    public void TestPrefixOperatorOfAMainActorStructReachesTheMember()
    {
        var negated = -new MainActorTally(5);
        AssertEqual(-5, negated.Count, "prefix - returned Swift's negation");
    }

    public void TestSimpleEnumInstanceMembersReachTheMembers()
    {
        AssertEqual(20, MainActorSignal.Green.GetWeight(), "weight is rawValue * 10");
        AssertEqual(9, MainActorSignal.Blue.Scaled(3), "scaled(by:) is rawValue * factor");
    }

    public void TestSimpleEnumStaticMembersReachTheMembers()
    {
        AssertEqual(MainActorSignal.Blue, MainActorSignalExtensions.Brightest, "brightest is .blue");
        AssertEqual(142, MainActorSignalExtensions.Offset(42), "offset(_:) adds 100");
    }

    public void TestCrossModuleExtensionMembersOnADependencyStructReachTheMembers()
    {
        var point = new DependencyPoint(3.0, -4.0);
        AssertApproxEqual(-1.0, point.MainActorSum(), 1e-9, "mainActorSum() is x + y");
        AssertApproxEqual(7.0, point.GetMainActorSpan(), 1e-9, "mainActorSpan is |x| + |y|");
    }

    public void TestRouteCSortOnAMainActorGenericParentReachesTheMember()
    {
        RouteC_SortTracker.Reset();
        var req = new RouteC_MainActorRequest<RouteC_Album>();
        req.Sort(RouteC_AlbumSortBagKeyPaths.Title, ascending: true);
        AssertEqual("main-actor asc", RouteC_SortTracker.LastDescription.ToString(),
            "Sort(Title, ascending=true) reached the main-actor member");
        req.Sort(RouteC_AlbumSortBagKeyPaths.Year, ascending: false);
        AssertEqual("main-actor desc", RouteC_SortTracker.LastDescription.ToString(),
            "Sort(Year, ascending=false) reached the main-actor member through the nint overload");
    }

    public void TestMainActorMembersWithoutTheirDefaultedArgumentReachTheMembers()
    {
        AssertTrue(MainActorGauge.TryCreate(21, out var gauge), "init?(reading:) with Swift's default unit succeeded");
        using (gauge)
        {
            AssertEqual(42, gauge.Reading, "the default unit doubles the reading");
            AssertEqual(48, gauge.Adjusted(3), "adjusted(by:) with Swift's default unit");
        }
        AssertFalse(MainActorGauge.TryCreate(-1, out _), "init?(reading:) returned nil for a negative reading");
        using var fixedGauge = new MainActorGauge(5L);
        AssertEqual(10, fixedGauge.Reading, "init(fixed:) with Swift's default unit");
    }

    // The C# side of each member above carries the Debug main-thread guard, as every regular
    // main-actor member does. Off the main thread a Debug build must refuse the call before any
    // Swift code runs. MainActorGuard is [Conditional("DEBUG")], so a Release build has no guard
    // to observe; those builds do not call off the main thread at all, since the member's Swift
    // code is main-actor isolated.

    // The operands are built here, on the main thread: the struct's initializer carries its own
    // guard, and only the operator may run on the worker.
    public void TestOperatorOffTheMainThreadTripsTheDebugGuard()
    {
#if DEBUG
        var left = new MainActorTally(1);
        var right = new MainActorTally(2);
        AssertDebugGuardTrips("MainActorTally +", () => _ = left + right);
#endif
    }

    public void TestPrefixOperatorOffTheMainThreadTripsTheDebugGuard()
    {
#if DEBUG
        var operand = new MainActorTally(1);
        AssertDebugGuardTrips("prefix MainActorTally -", () => _ = -operand);
#endif
    }

    public void TestSimpleEnumMembersOffTheMainThreadTripTheDebugGuard()
    {
        AssertDebugGuardTrips("MainActorSignal.weight", () => _ = MainActorSignal.Green.GetWeight());
        AssertDebugGuardTrips("MainActorSignal.scaled(by:)", () => _ = MainActorSignal.Blue.Scaled(2));
        AssertDebugGuardTrips("MainActorSignal.brightest", () => _ = MainActorSignalExtensions.Brightest);
        AssertDebugGuardTrips("MainActorSignal.offset(_:)", () => _ = MainActorSignalExtensions.Offset(1));
    }

    public void TestCrossModuleExtensionMembersOffTheMainThreadTripTheDebugGuard()
    {
#if DEBUG
        var point = new DependencyPoint(3.0, -4.0);
        AssertDebugGuardTrips("DependencyPoint.mainActorSum()", () => _ = point.MainActorSum());
        AssertDebugGuardTrips("DependencyPoint.mainActorSpan", () => _ = point.GetMainActorSpan());
#endif
    }

    public void TestRouteCSortOffTheMainThreadTripsTheDebugGuard()
    {
#if DEBUG
        RouteC_SortTracker.Reset();
        var req = new RouteC_MainActorRequest<RouteC_Album>();
        AssertDebugGuardTrips("RouteC_MainActorRequest.sort",
            () => req.Sort(RouteC_AlbumSortBagKeyPaths.Title, ascending: true));
        // The guard refused the call before the trampoline ran.
        AssertEqual("", RouteC_SortTracker.LastDescription.ToString(), "the refused sort never reached Swift");
#endif
    }

    private void AssertDebugGuardTrips(string member, Action call)
    {
#if DEBUG
        Exception? captured = null;
        var worker = new Thread(() =>
        {
            try { call(); }
            catch (Exception ex) { captured = ex; }
        });
        worker.Start();
        worker.Join();
        AssertTrue(
            captured is InvalidOperationException,
            $"{member} off the main thread should throw InvalidOperationException from the Debug guard, got {captured?.GetType().Name ?? "no exception"}");
#else
        _ = member;
        _ = call;
#endif
    }
}
