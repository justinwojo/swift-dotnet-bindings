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

    private static IConsumedCarrier DisposedCarrier()
    {
        var carrier = TestLibFunctions.MakeConsumedCarrier(99);
        ((IDisposable)carrier).Dispose();
        return carrier;
    }

    private static IConsumedCarrierAndConsumedCarrierLabel DisposedComposite()
    {
        var composite = TestLibFunctions.MakeConsumedComposite(99);
        ((IDisposable)composite).Dispose();
        return composite;
    }

    private void ExpectDisposed(Action call, string context)
    {
        ObjectDisposedException? thrown = null;
        try
        {
            call();
        }
        catch (ObjectDisposedException ex)
        {
            thrown = ex;
        }
        AssertNotNull(thrown, context);
    }

    /// <summary>
    /// The first argument is a Swift class conformer, which the binding boxes fresh for the callee.
    /// When the second (a disposed proxy) throws, that box has to be destroyed, or the conformer it
    /// holds never deinitializes.
    /// </summary>
    public void TestConsumedExistentialIsReleasedWhenALaterArgumentThrows()
    {
        DrainFinalizers();
        LifetimeTracker.Reset();

        LifetimeTracker.RunIsolated(() =>
        {
            using var first = new ConsumedCarrierCounterCell(3);
            var disposed = DisposedCarrier();
            for (int i = 0; i < 10; i++)
                ExpectDisposed(() => ConsumedCarrierPair.TryCreate(first, disposed, out _),
                    $"pass {i}: a disposed second existential must throw before the call");

            var second = TestLibFunctions.MakeConsumedCarrier(4);
            try
            {
                AssertTrue(ConsumedCarrierPair.TryCreate(first, second, out var pair), "the first conformer still constructs");
                using (pair)
                    AssertEqual(7, pair!.Total, "both conformers read back");
            }
            finally
            {
                ((IDisposable)second).Dispose();
            }
        });
        DrainFinalizers();

        LifetimeTracker.AssertNoLeaks("the box minted for a consumed existential must be destroyed when the call never happens");
    }

    /// <summary>
    /// The composition twin: the first argument's container is copied for the callee, and that copy
    /// has to be destroyed when the second throws.
    /// </summary>
    public void TestConsumedCompositionIsReleasedWhenALaterArgumentThrows()
    {
        DrainFinalizers();
        LifetimeTracker.Reset();

        LifetimeTracker.RunIsolated(() =>
        {
            var first = TestLibFunctions.MakeConsumedComposite(3);
            var second = TestLibFunctions.MakeConsumedComposite(4);
            var disposed = DisposedComposite();
            try
            {
                for (int i = 0; i < 10; i++)
                    ExpectDisposed(() => ConsumedCompositePair.TryCreate(first, disposed, out _),
                        $"pass {i}: a disposed second composition must throw before the call");

                AssertTrue(ConsumedCompositePair.TryCreate(first, second, out var pair), "the first composition still constructs");
                using (pair)
                    AssertEqual(43, pair!.Total, "both compositions read back through their own witness tables");
            }
            finally
            {
                ((IDisposable)first).Dispose();
                ((IDisposable)second).Dispose();
            }
        });
        DrainFinalizers();

        LifetimeTracker.AssertNoLeaks("the copy minted for a consumed composition must be destroyed when the call never happens");
    }

    /// <summary>
    /// An array the binding builds for Swift owns each element, and so does an owning optional
    /// carrier. Each has to hold its own copy of the proxy's container: destroying the array (or the
    /// optional) after the call must not release the reference the proxy still owns.
    /// </summary>
    public void TestCompositionArrayAndOptionalLeaveTheirProxiesIntact()
    {
        DrainFinalizers();
        LifetimeTracker.Reset();

        LifetimeTracker.RunIsolated(() =>
        {
            var a = TestLibFunctions.MakeConsumedComposite(1);
            var b = TestLibFunctions.MakeConsumedComposite(2);
            try
            {
                for (int i = 0; i < 10; i++)
                {
                    AssertEqual(33, TestLibFunctions.SumConsumedComposites(new[] { a, b }), $"pass {i}: the array reads both elements");
                    AssertEqual(20, TestLibFunctions.LabelOfOptionalComposite(b), $"pass {i}: the optional reads its value");
                    AssertEqual(-1, TestLibFunctions.LabelOfOptionalComposite(null), $"pass {i}: nil stays nil");
                }
                DrainFinalizers();
                LifetimeTracker.AssertLiveCount(2, "the conformers outlive every array and optional built from their proxies");
                AssertEqual(1, TestLibFunctions.TagOfConsumedComposite(a), "the first proxy still reads back in Swift");
                AssertEqual(2, TestLibFunctions.TagOfConsumedComposite(b), "the second proxy still reads back in Swift");
            }
            finally
            {
                ((IDisposable)a).Dispose();
                ((IDisposable)b).Dispose();
            }
        });
        DrainFinalizers();

        LifetimeTracker.AssertNoLeaks("each array element and optional value must be released after the call");
    }
}
