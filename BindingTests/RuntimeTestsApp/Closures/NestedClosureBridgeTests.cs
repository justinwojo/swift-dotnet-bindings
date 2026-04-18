// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Closures;

/// <summary>
/// Runtime tests for NestedClosureBridge multi-outer-closure support. Covers the
/// single-wrapper path where one Swift wrapper handles N outer closures, each with
/// a nested inner completion. Before #10, NCB rejected any method with more than
/// one outer closure.
/// </summary>
public class NestedClosureBridgeTests : TestBase
{
    public NestedClosureBridgeTests(TestResults results) : base(results) { }

    public void TestRunOneOuterInvoked()
    {
        var host = new NestedClosureHost();
        int outerArg = -1;
        bool innerSeen = false;
        host.RunOne((arg, inner) =>
        {
            outerArg = arg;
            innerSeen = inner != null;
        });
        AssertEqual(7, outerArg, "RunOne outer received arg=7");
        AssertTrue(innerSeen, "RunOne outer received non-null inner");
    }

    public void TestRunTwoBothOutersInvokedInOrder()
    {
        var host = new NestedClosureHost();
        var order = new List<int>();
        host.RunTwo(
            first: (arg, _) => order.Add(arg),
            second: (arg, _) => order.Add(arg));
        AssertEqual(2, order.Count, "RunTwo invoked both outers");
        AssertEqual(10, order[0], "RunTwo first outer received arg=10");
        AssertEqual(20, order[1], "RunTwo second outer received arg=20");
    }

    public void TestRunThreeAllOutersInvokedInOrder()
    {
        var host = new NestedClosureHost();
        var order = new List<int>();
        host.RunThree(
            first: (arg, _) => order.Add(arg),
            second: (arg, _) => order.Add(arg),
            third: (arg, _) => order.Add(arg));
        AssertEqual(3, order.Count, "RunThree invoked all three outers");
        AssertEqual(100, order[0], "RunThree first outer received arg=100");
        AssertEqual(200, order[1], "RunThree second outer received arg=200");
        AssertEqual(300, order[2], "RunThree third outer received arg=300");
    }

    public void TestRunTwoInnerInvocationsSurvive()
    {
        var host = new NestedClosureHost();
        int invocations = 0;
        host.RunTwo(
            first: (_, inner) => { inner(111); invocations++; },
            second: (_, inner) => { inner(222); invocations++; });
        AssertEqual(2, invocations, "Both outer closures invoked their inners without crashing");
    }

    public void TestRunThreeInnerInvocationsSurvive()
    {
        var host = new NestedClosureHost();
        int invocations = 0;
        host.RunThree(
            first: (_, inner) => { inner(1); invocations++; },
            second: (_, inner) => { inner(2); invocations++; },
            third: (_, inner) => { inner(3); invocations++; });
        AssertEqual(3, invocations, "All three outer closures invoked their inners without crashing");
    }
}
