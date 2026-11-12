// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using Swift;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Closures;

/// <summary>
/// @autoclosure parameters on members the closure bridges carry. The bridge adapts the managed
/// callback into a Swift closure; the call has to forward that closure's value, which Swift wraps
/// back into the autoclosure, or it binds to whichever same-named sibling takes a function.
/// </summary>
public class AutoclosureBridgeTests : TestBase
{
    public AutoclosureBridgeTests(TestResults results) : base(results) { }

    public void TestMethodClosureBridge_AutoclosureReachesItsOwnOverload()
    {
        // A call bound to the function-taking sibling answers -1 and never runs either callback.
        var host = new AutoclosureBridgeHost();
        int evaluations = 0;
        ProcessResult? reported = null;

        var outcome = host.Choose(true, () => { evaluations++; return true; }, result => { reported = result; });

        AssertEqual(1, outcome, "Choose ran the autoclosure overload with a true condition");
        AssertEqual(1, evaluations, "the condition was evaluated once");
        AssertNotNull(reported, "the handler was called");
        AssertTrue(TestLibFunctions.ProcessResultIsSuccess(reported!), "a true condition reports success");
        AssertEqual(7, TestLibFunctions.ProcessResultValue(reported!), "the omitted default code reached Swift");
    }

    public void TestMethodClosureBridge_AutoclosureFalseCondition()
    {
        var host = new AutoclosureBridgeHost();
        ProcessResult? reported = null;

        var outcome = host.Choose(true, () => false, result => { reported = result; });

        AssertEqual(0, outcome, "a false condition returns 0");
        AssertNotNull(reported, "the handler was called");
        AssertFalse(TestLibFunctions.ProcessResultIsSuccess(reported!), "a false condition reports failure");
    }

    public void TestMethodClosureBridge_AutoclosureStaysLazy()
    {
        // Forwarding the value must not evaluate it up front: Swift wraps the forwarding call into
        // the autoclosure, and the callee here never asks for it.
        var host = new AutoclosureBridgeHost();
        int evaluations = 0;
        bool handlerCalled = false;

        var outcome = host.Choose(false, () => { evaluations++; return true; }, _ => { handlerCalled = true; });

        AssertEqual(2, outcome, "a disabled call returns 2");
        AssertEqual(0, evaluations, "the condition was not evaluated");
        AssertFalse(handlerCalled, "the handler was not called");
    }

    public void TestGenericClosureBridge_AutoclosureReturnsItsValue()
    {
        var host = new AutoclosureBridgeHost();
        var reader = new DatabaseReader(name: "autoclosure");
        int evaluations = 0;

        var result = host.Evaluate(() => { evaluations++; return reader; });

        AssertEqual(1, evaluations, "the value was evaluated once");
        AssertEqual("autoclosure", result.Name, "Evaluate returned the closure's value");
    }
}
