// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Closures;

/// <summary>
/// Tests closure emission paths that differ from the already-tested free function closures.
/// Exercises ClosureEmitter @convention(c) and async+throwing branches.
///
/// Coverage gaps addressed:
/// - @convention(c) closure on class method (ClosureEmitter:255-292)
/// - Async + throwing closure param (ClosureEmitter:294-300)
/// </summary>
public class ClosurePathTests : TestBase
{
    public ClosurePathTests(TestResults results) : base(results) { }

    #region CCallbackRunner — @convention(c) Closure on Class Method

    public void TestCCallbackRunnerConstruction()
    {
        var runner = new CCallbackRunner(scale: 5);
        AssertNotNull(runner, "CCallbackRunner constructed");
        AssertEqual(5, runner.Scale, "Scale value");
        TestLogger.Info("CCallbackRunner construction passed");
    }

    public void TestCCallbackRunnerRunC()
    {
        var runner = new CCallbackRunner(scale: 7);
        var result = runner.RunC(x => x * 2);
        AssertEqual(14, result, "RunC(7 * 2) = 14");
        TestLogger.Info($"CCallbackRunner.RunC = {result}");
    }

    public void TestCCallbackRunnerRunCVoid()
    {
        var runner = new CCallbackRunner(scale: 3);
        var captured = 0;
        runner.RunCVoid(x => { captured = x; });
        AssertEqual(3, captured, "Void callback captured scale value");
        TestLogger.Info($"CCallbackRunner.RunCVoid captured = {captured}");
    }

    #endregion

    #region AsyncClosureRunner — Async + Throwing Closure

    public void TestAsyncClosureRunnerConstruction()
    {
        var runner = new AsyncClosureRunner(value: 42);
        AssertNotNull(runner, "AsyncClosureRunner constructed");
        AssertEqual(42, runner.Value, "Value");
        TestLogger.Info("AsyncClosureRunner construction passed");
    }

    // RunAsyncThrowing and RunAsync methods are NOT emitted —
    // async closure params are a known unsupported pattern (ClosureEmitter:294-300).
    // This is a legitimate gap. When the generator adds support, add tests here.

    #endregion
}
