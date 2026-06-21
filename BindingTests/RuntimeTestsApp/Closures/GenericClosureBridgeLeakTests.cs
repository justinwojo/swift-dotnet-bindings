// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using Swift;
using Swift.Runtime;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Closures;

/// <summary>
/// Lifetime probes for the GenericClosureBridge throw-after-callback exception path
/// (<see cref="DatabaseReader.ReadThenThrow{T}"/>).
/// <para>
/// Unlike the <c>rethrows</c> readers (<c>Read</c>/<c>ReadWithCdecl</c>/<c>ReadWithSelf</c>), which
/// only throw when the closure itself threw — so they never wrote a result — <c>readThenThrow</c>
/// invokes the closure SUCCESSFULLY (the closure's <c>+1</c> result is written into the bridge's
/// result buffer) and then throws independently. The C# side therefore takes the Swift-error path
/// while an unconsumed <c>+1</c> still sits in <c>resultBuf</c>.
/// </para>
/// <para>
/// The generated returning bridge must value-witness <c>Destroy</c> that buffer on the error path
/// before freeing it, or the closure result leaks once per call. <see cref="TestGenericThrowAfterCallback_ReleasesResultBufOnError"/>
/// drives that path in a loop and asserts the per-call <c>+1</c> is released.
/// </para>
/// <para>
/// The borrowed <c>source</c> argument handed to the closure must also balance: it is marshalled as
/// an owning <c>+1</c> (MarshalBorrowedClassFromSwift) so the temporary wrapper's finalizer does not
/// over-release the caller's object — a use-after-free that surfaced as a NativeAOT device SIGTRAP
/// when the temporary was finalized after the call returned.
/// </para>
/// </summary>
public class GenericClosureBridgeLeakTests : TestBase
{
    public GenericClosureBridgeLeakTests(TestResults results) : base(results) { }

    private static void DrainFinalizers()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    /// <summary>
    /// The throw-after-callback path must surface the Swift error to the caller even though the
    /// closure ran to completion and produced a result.
    /// </summary>
    public void TestGenericThrowAfterCallback_SurfacesError()
    {
        using var source = new DatabaseReader("primary");
        using var reader = new DatabaseReader("outer");

        bool threw = false;
        try
        {
            reader.ReadThenThrow<TrackedRef>(db => new TrackedRef(1), source);
        }
        catch (SwiftException)
        {
            threw = true;
        }

        AssertTrue(threw, "readThenThrow must surface the Swift error after the closure returns");
    }

    /// <summary>
    /// Every throw-after-callback call writes one closure <c>+1</c> into the result buffer and then
    /// throws. The error path must value-witness Destroy the buffer, so after draining finalizers the
    /// allocations all balance — no per-call leak of the closure result.
    /// </summary>
    public void TestGenericThrowAfterCallback_ReleasesResultBufOnError()
    {
        using var source = new DatabaseReader("primary");
        using var reader = new DatabaseReader("outer");

        DrainFinalizers();
        LifetimeTracker.Reset();

        const int n = 500;
        for (int i = 0; i < n; i++)
        {
            try
            {
                reader.ReadThenThrow<TrackedRef>(db => new TrackedRef(i), source);
                throw new AssertionException($"call {i} did not throw");
            }
            catch (SwiftException) { }
        }

        DrainFinalizers();
        var (alloc, dealloc, live) = LifetimeTracker.GetStats();
        AssertEqual(n, alloc, $"each throw-after-callback call allocates exactly one closure result; got alloc={alloc}");
        AssertTrue(live <= n / 10, $"the closure +1 must be released on the error path; got live={live} (alloc={alloc}, dealloc={dealloc})");
    }
}
