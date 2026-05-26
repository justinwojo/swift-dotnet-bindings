// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Runtime.CompilerServices;
using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.MemoryManagement;

/// <summary>
/// Probes ARC balance for Swift functions returning existentials (<c>any P</c> /
/// <c>(any P)?</c>). Swift transfers the existential at +1 — the C# caller owns the
/// release obligation. The generated marshalling reads the container out of the return
/// slot and constructs a <c>{Protocol}Proxy</c>; the Swift-backed proxy ctor adopts the
/// container but (before the ownership fix) took no retain and released nothing on
/// Dispose/finalize, orphaning the payload's +1.
///
/// Each fixture embeds a <see cref="LifetimeTracker"/>-counted instance inside the
/// existential, so a leaked retain shows up as a non-zero live count after the proxy is
/// disposed and the GC has drained — not merely as "does not crash". The dispose loops
/// run in a <c>[MethodImpl(NoInlining)]</c> helper so no stale stack slot keeps a proxy
/// alive past its <c>Dispose</c>.
///
/// The <c>(any Error)?</c> path is intentionally separate: <c>AnyError</c> is a blittable
/// value struct that cannot own a deterministic-release +1 across bitwise copies, so the
/// fix there is an ownership-model decision (reference-type wrapper vs. accept the bounded
/// box leak) rather than a localized projection change. Its probe asserts balance so the
/// leak is quantified until that decision lands.
/// </summary>
public class ExistentialReturnLeakProbeTests : TestBase
{
    public ExistentialReturnLeakProbeTests(TestResults results) : base(results) { }

    private static void DrainFinalizers()
    {
        for (int i = 0; i < 4; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
        GC.Collect();
    }

    /// <summary>
    /// <c>(any Renderable)?</c> return wrapping a tracked CLASS conformer (reference stored
    /// inline in the container's first payload word). Disposing the proxy must value-witness
    /// Destroy the adopted container, ARC-releasing the instance. A leaked container retain
    /// pins one tracked instance per call.
    /// </summary>
    public void TestOptionalExistentialReturnReleasesInlinePayload()
    {
        DrainFinalizers();
        LifetimeTracker.Reset();

        AllocAndDisposeOptionalRenderables(200);
        DrainFinalizers();

        LifetimeTracker.AssertNoLeaks("(any Renderable)? return must not orphan the existential payload's retain");
        TestLogger.Info("(any Renderable)?: 200 present returns released their inline class payload");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void AllocAndDisposeOptionalRenderables(int iterations)
    {
        for (int i = 0; i < iterations; i++)
        {
            var r = TestLibFunctions.MakeTrackedRenderableOptional(true, i);
            (r as IDisposable)?.Dispose();
        }
    }

    /// <summary>
    /// Non-optional <c>any Renderable</c> return — the <c>ExistentialProjection.GetReturnPlan</c>
    /// proxy-construction path, same adopt-without-release shape as the optional path. Disposing
    /// the proxy must release the inline class payload.
    /// </summary>
    public void TestNonOptionalExistentialReturnReleasesInlinePayload()
    {
        DrainFinalizers();
        LifetimeTracker.Reset();

        AllocAndDisposeRenderables(200);
        DrainFinalizers();

        LifetimeTracker.AssertNoLeaks("any Renderable return must not orphan the existential payload's retain");
        TestLogger.Info("any Renderable: 200 returns released their inline class payload");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void AllocAndDisposeRenderables(int iterations)
    {
        for (int i = 0; i < iterations; i++)
        {
            var r = TestLibFunctions.MakeTrackedRenderable(i);
            (r as IDisposable)?.Dispose();
        }
    }

    /// <summary>
    /// <c>(any Renderable)?</c> PROPERTY GETTER — a distinct owned-return emission mechanism from
    /// the standalone-function returns above. <c>RenderableHolder.Primary</c> is decomposed into a
    /// <c>(payload, hasValue)</c> buffer by the cdecl property wrapper; the getter reads the
    /// existential container out of that buffer at +1 and frees the buffer, so the wrapping proxy
    /// is the sole surviving retain and must release on Dispose. Each read lays a fresh +1 on the
    /// SAME tracked instance the holder owns — so the leak is structured around the surviving owner:
    /// after every getter-returned proxy AND the holder are disposed, the instance must deinit
    /// (live count 0). If the getter's proxy does not adopt the container, the per-read +1s outlive
    /// the holder and pin the instance alive.
    /// </summary>
    public void TestOptionalExistentialPropertyGetterReleasesPayload()
    {
        DrainFinalizers();
        LifetimeTracker.Reset();

        ReadAndDisposePropertyGetter(50);
        DrainFinalizers();

        LifetimeTracker.AssertNoLeaks("(any Renderable)? property getter must release each returned existential's +1");
        TestLogger.Info("RenderableHolder.Primary: 50 getter reads + holder disposed, inline class payload released");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ReadAndDisposePropertyGetter(int reads)
    {
        var holder = TestLibFunctions.MakeTrackedRenderableHolder(7);
        for (int i = 0; i < reads; i++)
        {
            var r = holder.Primary;          // owned +1: Swift's getter returns the existential retained
            (r as IDisposable)?.Dispose();
        }
        (holder as IDisposable)?.Dispose();  // release the holder's own stored +1
    }

    /// <summary>
    /// <c>(any Renderable)?</c> return wrapping a BOXED value-type conformer (five embedded
    /// tracked refs push it past the 3-word inline buffer). Disposing the proxy must release
    /// the existential via its value-witness table — which releases the box and its five
    /// embedded refs — rather than a bare release of the first payload word. A leaked container
    /// retain pins all five refs per call.
    /// </summary>
    public void TestOptionalExistentialReturnReleasesBoxedPayload()
    {
        DrainFinalizers();
        LifetimeTracker.Reset();

        AllocAndDisposeBoxedRenderables(50);
        DrainFinalizers();

        LifetimeTracker.AssertNoLeaks("(any Renderable)? boxed-payload return must not orphan the box's retains");
        TestLogger.Info("(any Renderable)? boxed: 50 returns x 5 embedded refs all released");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void AllocAndDisposeBoxedRenderables(int iterations)
    {
        for (int i = 0; i < iterations; i++)
        {
            var r = TestLibFunctions.MakeBoxedTrackedRenderableOptional(true, i);
            (r as IDisposable)?.Dispose();
        }
    }

    /// <summary>
    /// <c>any Nameable &amp; Ageable</c> return wrapping a tracked CLASS conforming to BOTH protocols
    /// — an EC2 COMPOSITION existential. The single conforming value (one class instance regardless
    /// of protocol count) lives inline in the container's first payload word; disposing the composition
    /// proxy must value-witness Destroy the adopted EC2 container, ARC-releasing the instance. Before
    /// the EC2+ ownership fix the composition proxy had an empty <c>Dispose()</c> and no ownership-aware
    /// ctor, so each owned return orphaned the payload's +1 and pinned one tracked instance per call.
    /// </summary>
    public void TestCompositionExistentialReturnReleasesInlinePayload()
    {
        DrainFinalizers();
        LifetimeTracker.Reset();

        AllocAndDisposeNameableAgeable(200);
        DrainFinalizers();

        LifetimeTracker.AssertNoLeaks("any Nameable & Ageable (EC2) return must not orphan the composition existential payload's retain");
        TestLogger.Info("any Nameable & Ageable: 200 returns released their inline class payload");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void AllocAndDisposeNameableAgeable(int iterations)
    {
        for (int i = 0; i < iterations; i++)
        {
            var v = TestLibFunctions.MakeTrackedNameableAgeable(i);
            (v as IDisposable)?.Dispose();
        }
    }

    /// <summary>
    /// <c>(any Nameable &amp; Ageable)?</c> return — the decomposed OPTIONAL composition-existential
    /// owned-return path, a distinct emission site from the non-optional path above but routed
    /// through the same EC2 composition proxy. Disposing the proxy must release the inline class payload.
    /// </summary>
    public void TestOptionalCompositionExistentialReturnReleasesInlinePayload()
    {
        DrainFinalizers();
        LifetimeTracker.Reset();

        AllocAndDisposeNameableAgeableOptional(200);
        DrainFinalizers();

        LifetimeTracker.AssertNoLeaks("(any Nameable & Ageable)? (EC2) return must not orphan the composition existential payload's retain");
        TestLogger.Info("(any Nameable & Ageable)?: 200 present returns released their inline class payload");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void AllocAndDisposeNameableAgeableOptional(int iterations)
    {
        for (int i = 0; i < iterations; i++)
        {
            var v = TestLibFunctions.MakeTrackedNameableAgeableOptional(true, i);
            (v as IDisposable)?.Dispose();
        }
    }

    /// <summary>
    /// <c>any Nameable &amp; Ageable</c> (EC2) return wrapping a BOXED value-type conformer — five
    /// embedded tracked refs push the struct past the container's 3-word inline buffer, so the
    /// payload is heap-boxed and the container's first word holds the box pointer. Disposing the
    /// composition proxy must release the EC2 container through its value-witness table (which
    /// releases the box and its five embedded refs), NOT a bare release of the first payload word.
    /// This guards the EC2+ release path for the boxed case — the inline-vs-boxed distinction lives
    /// in the existential's own VWT, independent of the witness-table word count, so a release path
    /// that only handled inline class refs would leak all five refs per call here.
    /// </summary>
    public void TestCompositionExistentialReturnReleasesBoxedPayload()
    {
        DrainFinalizers();
        LifetimeTracker.Reset();

        AllocAndDisposeBoxedNameableAgeable(50);
        DrainFinalizers();

        LifetimeTracker.AssertNoLeaks("any Nameable & Ageable (EC2) boxed-payload return must not orphan the box's retains");
        TestLogger.Info("any Nameable & Ageable boxed: 50 returns x 5 embedded refs all released");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void AllocAndDisposeBoxedNameableAgeable(int iterations)
    {
        for (int i = 0; i < iterations; i++)
        {
            var v = TestLibFunctions.MakeBoxedTrackedNameableAgeable(i);
            (v as IDisposable)?.Dispose();
        }
    }

    /// <summary>
    /// <c>(any Error)?</c> return wrapping a tracked CLASS conforming to <c>Error</c> — a 1-word
    /// class-bound existential the marshalling wraps in the <c>AnyError</c> value struct. Because
    /// <c>AnyError</c> is blittable and copied by value, it has no deterministic release point, so
    /// the box's +1 on the error instance is orphaned. This probe asserts ARC balance to quantify
    /// the leak; it stays red until the <c>AnyError</c> ownership model is decided.
    /// </summary>
    [Skip("(any Error)? return leaks its payload's +1: AnyError is a blittable [StructLayout(Sequential)] value struct passed by-value across the SwiftResult<TSuccess, AnyError> P/Invoke ABI, so it cannot own a deterministic-release obligation (a SafeHandle field or class conversion would break blittability and that ABI). Fixing this is a public-API ownership-model decision (reference-type wrapper vs. accept the bounded box leak), not a localized projection change. The assertion is retained so this probe goes green once that decision lands; the opaque-existential proxy path (any Renderable) IS fixed.")]
    public void TestOptionalErrorReturnReleasesPayload()
    {
        DrainFinalizers();
        LifetimeTracker.Reset();

        AllocAndDisposeOptionalErrors(200);
        DrainFinalizers();

        LifetimeTracker.AssertNoLeaks("(any Error)? return must not orphan the error box's retain");
        TestLogger.Info("(any Error)?: 200 present returns released their error payload");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void AllocAndDisposeOptionalErrors(int iterations)
    {
        for (int i = 0; i < iterations; i++)
        {
            // AnyError is a blittable value struct with no IDisposable/release path, so there
            // is nothing to dispose — the box's +1 is orphaned when `e` leaves scope. That is
            // exactly the leak this probe quantifies.
            var e = TestLibFunctions.MakeTrackedErrorOptional(true, i);
            _ = e;
        }
    }
}
