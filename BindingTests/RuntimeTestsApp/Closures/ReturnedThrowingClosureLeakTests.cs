// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using Swift;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Closures;

/// <summary>
/// Covers the Swift -> C# *returned* throwing-closure error path — C# invokes a
/// closure that Swift handed back (<c>() throws -> Int32</c>), exercising
/// <c>ClosureEmitter.EmitThrowingClosureReturnMarshalling</c>. That shape is
/// supported but had no end-to-end runtime coverage, and it returns the thrown
/// Swift error to C# <c>passRetained</c> (+1) inside a <c>SwiftError</c> that has
/// no Dispose — so the boundary's error-release behaviour was never measured.
///
/// Runs on both simulator and device: the returned throwing closure is invoked
/// through the generated CallConvCdecl invoker class (<c>_ClosureInv_*</c>), not
/// the inline <c>delegate* unmanaged[Swift]</c> lambda. The earlier simulator
/// crash here was self-inflicted — a dead cdecl thunk left the live invoker as a
/// CallConvSwift call from a display-class method (SIGSEGV), misread as the
/// Mono <c>!ji->async</c> assertion. Wiring the cdecl invoke thunk
/// (<c>WrapperEmitter.Return.cs</c>) removed the crash, so the prior
/// <c>[SkipOnSimulator]</c> is gone (Track-M4:105).
/// </summary>
public class ReturnedThrowingClosureLeakTests : TestBase
{
    public ReturnedThrowingClosureLeakTests(TestResults results) : base(results) { }

    private static void DrainFinalizers()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    /// <summary>
    /// Functional baseline: a Swift-returned <c>() throws -> Int32</c> that always
    /// throws must surface to C# as <see cref="SwiftResult{T,E}.IsFailure"/>.
    /// </summary>
    public void TestReturnedThrowingClosureSurfacesFailure()
    {
        var fn = TestLibFunctions.MakeAlwaysThrowingIntClosure();
        AssertNotNull(fn, "returned throwing-closure delegate should be non-null");

        var r = fn();
        AssertTrue(r.IsFailure, "a returned closure that throws must produce SwiftResult.IsFailure");
        r.Dispose();

        TestLogger.Info("Returned throwing closure surfaced failure correctly");
    }

    /// <summary>
    /// Success sibling of <see cref="TestReturnedThrowingClosureSurfacesFailure"/>: a
    /// Swift-returned <c>() throws -> Int32</c> that returns normally must surface to C#
    /// as <see cref="SwiftResult{T,E}.IsSuccess"/> carrying the value. Together the two
    /// prove the returned-throwing-closure path routes through the CallConvCdecl invoker
    /// (both directions), not the crashing inline CallConvSwift lambda (Track-M4:105).
    /// </summary>
    public void TestReturnedNeverThrowingClosureSurfacesSuccess()
    {
        var fn = TestLibFunctions.MakeNeverThrowingIntClosure();
        AssertNotNull(fn, "returned non-throwing closure delegate should be non-null");

        var r = fn();
        AssertTrue(r.IsSuccess, "a returned throwing closure that returns normally must produce SwiftResult.IsSuccess");
        AssertEqual(99, r.Success, "returned closure must surface the Swift value 99");
        r.Dispose();

        TestLogger.Info("Returned non-throwing closure surfaced success correctly");
    }

    /// <summary>
    /// Leak characterization. Each invocation throws a fresh tracked Swift error
    /// that the boundary hands back +1-retained. Assert (a) the path actually ran
    /// — exactly N errors were allocated — and (b) the errors still live afterward
    /// are bounded by the invocation count (0 &lt;= live &lt;= N).
    ///
    /// This passes today regardless of the boundary's current release behaviour,
    /// still passes if a future disposable-failure-carrier fix drives the leak to
    /// 0, and fails if the leak ever becomes *super*-linear (e.g. the closure
    /// context leaks per call on top of the error).
    /// </summary>
    public void TestReturnedThrowingClosureErrorLeakBounded()
    {
        var fn = TestLibFunctions.MakeAlwaysThrowingIntClosure();

        DrainFinalizers();
        LifetimeTracker.Reset();

        const int n = 1000;
        for (int i = 0; i < n; i++)
        {
            var r = fn();
            if (!r.IsFailure)
                throw new AssertionException($"invocation {i} did not throw (expected SwiftResult.IsFailure)");
            r.Dispose();
        }

        DrainFinalizers();
        var (alloc, dealloc, live) = LifetimeTracker.GetStats();

        AssertEqual(n, alloc,
            $"every invocation must allocate exactly one tracked error (proves the path ran); got alloc={alloc}");
        AssertTrue(live >= 0 && live <= n,
            $"leaked tracked-error count must be bounded by invocation count [0,{n}]; got live={live} (alloc={alloc}, dealloc={dealloc})");

        TestLogger.Info($"Returned throwing closure leak bound: {n} throws, alloc={alloc}, dealloc={dealloc}, live={live}");
    }
}
