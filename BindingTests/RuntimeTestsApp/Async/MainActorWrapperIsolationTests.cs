// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

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
}
