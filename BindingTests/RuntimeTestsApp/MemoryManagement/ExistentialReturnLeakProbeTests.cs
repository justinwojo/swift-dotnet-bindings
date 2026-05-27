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
/// The <c>(any Error)?</c> path uses a different wrapper than the proxy paths: <c>any Error</c>
/// is a single boxed reference wrapped in the <c>AnyError</c> reference type, which adopts the
/// box's +1 on an owned transfer and releases it via <c>SBW_AnyError_Destroy</c> on
/// Dispose/finalize. The enum-payload extraction (<c>TryGetFailed(out AnyError)</c>) is yet
/// another emission mechanism — the payload is value-witness-copied out of the enum at +1, so
/// the extracted wrapper owns a distinct release obligation from the enum's own stored +1.
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
    /// class-bound existential the marshalling wraps in the <c>AnyError</c> reference type. Swift
    /// returns the boxed error in x0 at +1; the <c>AnyError</c> adopts that retain
    /// (<c>ownsContainer: true</c>) and releases it via <c>SBW_AnyError_Destroy</c> on Dispose.
    /// A wrapper that did not adopt/release would orphan the box's +1 and pin one error per call.
    /// </summary>
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
            var e = TestLibFunctions.MakeTrackedErrorOptional(true, i);
            e?.Dispose();  // owned +1: AnyError adopts the box and releases it here
        }
    }

    /// <summary>
    /// <c>TrackedErrorBox.failure(any Error)</c> enum-payload EXTRACTION — a distinct owned-transfer
    /// emission mechanism from the direct returns above. The generated <c>TryGetFailed(out AnyError)</c>
    /// value-witness-copies the whole enum (retaining the boxed error at +1) into a buffer it never
    /// destroys, then wraps the box pointer in <c>AnyError</c>. Each extraction therefore lays a fresh
    /// +1 on the SAME tracked error the enum holds, so the leak is structured around the surviving
    /// owner: after every extracted <c>AnyError</c> AND the enum are disposed, the error must deinit
    /// (live count 0). If the extracted wrapper does not adopt the container, the per-extraction +1s
    /// outlive the enum and pin the error alive.
    /// </summary>
    public void TestErrorEnumPayloadExtractionReleasesPayload()
    {
        DrainFinalizers();
        LifetimeTracker.Reset();

        ExtractAndDisposeErrorPayload(50);
        DrainFinalizers();

        LifetimeTracker.AssertNoLeaks("TryGetFailed(out AnyError) must release each extracted error payload's +1");
        TestLogger.Info("TrackedErrorBox.failure: 50 extractions + enum disposed, error payload released");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ExtractAndDisposeErrorPayload(int extractions)
    {
        var box = TestLibFunctions.MakeTrackedErrorBoxFailure(7);
        for (int i = 0; i < extractions; i++)
        {
            if (box.TryGetFailed(out var e))   // owned +1: copied out of the enum, never re-destroyed
                e.Dispose();
        }
        (box as IDisposable)?.Dispose();        // release the enum's own stored +1
    }

    /// <summary>
    /// Non-optional <c>any Error</c> return — the direct existential-return projection
    /// (<c>ExistentialProjection.GetReturnPlan</c> well-known branch), a DISTINCT owned-return
    /// emission mechanism from the <c>(any Error)?</c> path (which routes through
    /// <c>OptionalProjection</c>). Swift returns the boxed error at +1; the wrapping
    /// <c>AnyError</c> must adopt it (<c>ownsContainer: true</c>) and release on Dispose. A
    /// non-owning construction here orphans one box per call.
    /// </summary>
    public void TestNonOptionalErrorReturnReleasesPayload()
    {
        DrainFinalizers();
        LifetimeTracker.Reset();

        AllocAndDisposeErrors(200);
        DrainFinalizers();

        LifetimeTracker.AssertNoLeaks("non-optional any Error return must not orphan the error box's retain");
        TestLogger.Info("any Error: 200 direct returns released their error payload");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void AllocAndDisposeErrors(int iterations)
    {
        for (int i = 0; i < iterations; i++)
        {
            var e = TestLibFunctions.MakeTrackedError(i);
            e.Dispose();  // owned +1: AnyError adopts the box and releases it here
        }
    }

    /// <summary>
    /// <c>TrackedRenderableBox.shown(any Renderable)</c> enum-payload EXTRACTION through a generated
    /// <c>RenderableProxy</c> — the proxy analogue of <see cref="TestErrorEnumPayloadExtractionReleasesPayload"/>
    /// (which goes through the well-known <c>AnyError</c> branch). This pins the
    /// <c>EnumHandler.Marshalling.cs</c> PROXY extraction branch: <c>TryGetShown</c> value-witness-copies
    /// the whole enum (retaining the boxed conformer at +1) into a buffer it never destroys, then wraps
    /// the container in <c>RenderableProxy</c>. Each extraction lays a fresh +1 the proxy must adopt
    /// (<c>ownsContainer: true</c>) and release on Dispose, distinct from the enum's own stored +1. A
    /// non-owning proxy would pin the conformer alive (live count never returns to 0).
    /// </summary>
    public void TestProxyEnumPayloadExtractionReleasesPayload()
    {
        DrainFinalizers();
        LifetimeTracker.Reset();

        ExtractAndDisposeRenderablePayload(50);
        DrainFinalizers();

        LifetimeTracker.AssertNoLeaks("TryGetShown(out IRenderable) must release each extracted existential payload's +1");
        TestLogger.Info("TrackedRenderableBox.shown: 50 extractions + enum disposed, payload released");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ExtractAndDisposeRenderablePayload(int extractions)
    {
        var box = TestLibFunctions.MakeTrackedRenderableBoxShown(9);
        for (int i = 0; i < extractions; i++)
        {
            if (box.TryGetShown(out var r))   // owned +1: copied out of the enum, never re-destroyed
                (r as IDisposable)?.Dispose();  // r is typed as the bare IRenderable interface; the RenderableProxy behind it owns the +1
        }
        (box as IDisposable)?.Dispose();        // release the enum's own stored +1
    }
}
