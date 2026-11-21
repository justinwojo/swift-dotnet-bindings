// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using Swift.Foundation;
using Swift.Runtime;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.MemoryManagement;

/// <summary>
/// An initializer consumes its arguments, so the binding retains each one for the callee before the
/// call. When a later argument cannot be converted (an <c>any Error</c> whose wrapper is already
/// disposed), the call never happens, and the retain taken for each earlier argument has to be given
/// back. Every value here is tracked, so a retain left behind shows up as an instance that never
/// deinitializes after its C# owner is disposed.
/// </summary>
public class ConsumedArgumentRollbackTests : TestBase
{
    public ConsumedArgumentRollbackTests(TestResults results) : base(results) { }

    private static void DrainFinalizers()
    {
        for (int i = 0; i < 4; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
        GC.Collect();
    }

    private static AnyError DisposedError()
    {
        var error = TestLibFunctions.MakeTrackedError(99);
        error.Dispose();
        return error;
    }

    public void TestConsumedErrorsReachTheInitializer()
    {
        DrainFinalizers();
        LifetimeTracker.Reset();

        LifetimeTracker.RunIsolated(() =>
        {
            using var first = TestLibFunctions.MakeTrackedError(3);
            using var second = TestLibFunctions.MakeTrackedError(4);
            for (int i = 0; i < 10; i++)
            {
                AssertTrue(ConsumedErrorPair.TryCreate(first, second, out var pair), $"pass {i} constructs");
                using (pair)
                    AssertEqual(7, pair!.Total, $"pass {i} reads both errors");
            }
        });
        DrainFinalizers();

        LifetimeTracker.AssertNoLeaks("a consumed error must be released by the callee, not leaked or freed early");
    }

    public void TestConsumedErrorIsReleasedWhenALaterArgumentThrows()
    {
        DrainFinalizers();
        LifetimeTracker.Reset();

        LifetimeTracker.RunIsolated(() =>
        {
            using var first = TestLibFunctions.MakeTrackedError(3);
            var disposed = DisposedError();
            for (int i = 0; i < 10; i++)
            {
                ObjectDisposedException? thrown = null;
                try
                {
                    ConsumedErrorPair.TryCreate(first, disposed, out _);
                }
                catch (ObjectDisposedException ex)
                {
                    thrown = ex;
                }
                AssertNotNull(thrown, $"pass {i}: a disposed second error must throw before the call");
            }
            // The first error is still usable: the rolled-back retain did not take its owner's count.
            using var second = TestLibFunctions.MakeTrackedError(4);
            AssertTrue(ConsumedErrorPair.TryCreate(first, second, out var pair), "the first error still constructs");
            using (pair)
                AssertEqual(7, pair!.Total, "the first error still reads back");
        });
        DrainFinalizers();

        LifetimeTracker.AssertNoLeaks("the retain taken for a consumed error must be given back when the call never happens");
    }

    public void TestConsumedObjCArgumentIsReleasedWhenALaterArgumentThrows()
    {
        DrainFinalizers();
        LifetimeTracker.Reset();

        LifetimeTracker.RunIsolated(() =>
        {
            using var shape = new TrackedObjCShapeThing(5);
            var disposed = DisposedError();
            for (int i = 0; i < 10; i++)
            {
                ObjectDisposedException? thrown = null;
                try
                {
                    ConsumedObjCAndError.TryCreate(shape, disposed, out _);
                }
                catch (ObjectDisposedException ex)
                {
                    thrown = ex;
                }
                AssertNotNull(thrown, $"pass {i}: a disposed error must throw before the call");
            }
            using var error = TestLibFunctions.MakeTrackedError(6);
            AssertTrue(ConsumedObjCAndError.TryCreate(shape, error, out var cell), "the object still constructs");
            using (cell)
                AssertEqual(11, cell!.Total, "the object still reads back");
        });
        DrainFinalizers();

        LifetimeTracker.AssertNoLeaks("the retain taken for a consumed object must be given back when the call never happens");
    }
}
