// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Runtime.CompilerServices;
using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.MemoryManagement;

/// <summary>
/// Mirror image of <see cref="ExistentialReturnLeakProbeTests"/>: a value-type conformer
/// passed from C# INTO a Swift <c>any P</c> PARAMETER (audit P1-03). The generated C#
/// marshalling calls <c>ExistentialContainerFactory.GetOrCreate(value, out owns)</c>; for a
/// boxable value conformer this freshly boxes the payload at +1 (an inline
/// <c>InitializeWithCopy</c> for a small conformer, or a <c>swift_allocBox</c> for one that
/// overflows the 3-word inline buffer). Every Swift existential parameter is
/// <c>@in_guaranteed</c> (borrowed — the callee reads via <c>load</c>/<c>.pointee</c> and never
/// releases the caller's buffer), so the C# caller owns that +1 and MUST run the existential
/// value-witness destroy after the call. Before the P1-03 fix nothing released it, leaking the
/// embedded <see cref="LifetimeTracker"/>-tracked refs per call.
///
/// One probe per emission mechanism that boxes an EC1 existential parameter through
/// <c>GetOrCreate(..., out owns)</c>:
/// <list type="bullet">
/// <item>synchronous function param — <c>WrapperEmitter.Marshalling.cs</c> sync path + the
///   foreground finally's <c>DestroyAndFreeExistential</c>;</item>
/// <item>async function param — the async-callback cleanup loop
///   (<c>ExistentialContainerHeap</c> carrying owns-bit + witness count), since the async
///   wrapper has no foreground finally;</item>
/// <item>enum-case factory — <c>EnumHandler.CaseConstruction.cs</c> (<c>TrackedRenderableBox.Shown</c>);</item>
/// <item>optional-existential setter — <c>PropertyHandler.cs</c> (<c>RenderableHolder.Primary</c>).</item>
/// </list>
///
/// Each probe embeds <see cref="InlineTrackedRenderable"/> — a value conformer holding two
/// tracked refs — and structures the leak around a surviving C# owner: the same two refs are
/// retained by the owner AND by each per-call box, so a leaked box-retain pins them alive even
/// after the owner is disposed (live count never returns to 0). A borrowed proxy/class conformer
/// reports <c>owns == false</c> and must NOT be destroyed (would over-release, audit P0-09/P0-10);
/// the value-conformer probes here drive the owning (<c>owns == true</c>) branch.
///
/// The alloc/dispose loops run in <c>[MethodImpl(NoInlining)]</c> helpers so no stale stack slot
/// keeps a conformer or box alive past its <c>Dispose</c>.
/// </summary>
public class ExistentialParamLeakProbeTests : TestBase
{
    public ExistentialParamLeakProbeTests(TestResults results) : base(results) { }

    private const int Iterations = 50;

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
    /// Synchronous <c>any Renderable</c> parameter (<c>consumeRenderable</c>). The C# wrapper boxes
    /// the value conformer at +1 (<c>GetOrCreate(..., out owns)</c>); the <c>@in_guaranteed</c> callee
    /// only borrows it, so the foreground finally must run the existential destroy gated on the
    /// owns-bit. Reusing one owner across the loop means a leaked per-call box pins the owner's two
    /// refs after it is disposed.
    /// </summary>
    public void TestSyncExistentialParamReleasesBox()
    {
        DrainFinalizers();
        LifetimeTracker.Reset();

        PassToSyncConsumer(Iterations);
        DrainFinalizers();

        LifetimeTracker.AssertNoLeaks("any Renderable sync param must release each per-call boxed +1");
        TestLogger.Info($"consumeRenderable: {Iterations} calls reusing one conformer, boxed payload released");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void PassToSyncConsumer(int iterations)
    {
        var owner = new InlineTrackedRenderable(1);     // two tracked refs, held by this owner
        for (int i = 0; i < iterations; i++)
        {
            // Each call boxes a fresh +1 copy of the conformer; the finally must destroy it.
            _ = TestLibFunctions.ConsumeRenderable(owner);
        }
        (owner as IDisposable)?.Dispose();              // release the owner's own held value
    }

    /// <summary>
    /// Asynchronous <c>any Renderable</c> parameter (<c>consumeRenderableAsync</c>). The async wrapper
    /// has no foreground finally, so the freshly boxed +1 is balanced by the async-callback cleanup
    /// loop (<c>ExistentialContainerHeap</c> carrying the owns-bit + witness count) once the Swift
    /// continuation has finished reading the <c>@in_guaranteed</c> buffer. A leaked box pins the
    /// owner's refs after disposal.
    /// </summary>
    public async Task TestAsyncExistentialParamReleasesBox()
    {
        DrainFinalizers();
        LifetimeTracker.Reset();

        // Await the async Swift calls — never block with .GetAwaiter().GetResult(): the
        // discovery runner awaits this Task while pumping the iOS run loop, and the Swift
        // async continuation completes on that same loop. Synchronously blocking the test
        // thread on the continuation deadlocks (the loop can never run it). WithTimeout keeps
        // a future regression a fast TimeoutException rather than a 90s launch hang.
        await WithTimeout(PassToAsyncConsumer(Iterations), TimeSpan.FromSeconds(30));
        DrainFinalizers();

        LifetimeTracker.AssertNoLeaks("any Renderable async param must release each per-call boxed +1");
        TestLogger.Info($"consumeRenderableAsync: {Iterations} awaited calls reusing one conformer, boxed payload released");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task PassToAsyncConsumer(int iterations)
    {
        var owner = new InlineTrackedRenderable(2);
        for (int i = 0; i < iterations; i++)
        {
            _ = await TestLibFunctions.ConsumeRenderableAsync(owner);
        }
        (owner as IDisposable)?.Dispose();
    }

    /// <summary>
    /// Enum-case factory carrying an <c>any Renderable</c> associated value
    /// (<c>TrackedRenderableBox.Shown</c>) — the <c>EnumHandler.CaseConstruction.cs</c> mechanism.
    /// Swift copies the loaded existential into the enum payload (the enum owns its own +1), while
    /// the C# factory's temporary box is a distinct +1 the finally must destroy gated on the
    /// owns-bit. Disposing each constructed enum releases the enum's +1; a leaked factory box pins
    /// the owner's refs after the owner is disposed.
    /// </summary>
    public void TestEnumCaseFactoryExistentialParamReleasesBox()
    {
        DrainFinalizers();
        LifetimeTracker.Reset();

        ConstructAndDisposeEnumCases(Iterations);
        DrainFinalizers();

        LifetimeTracker.AssertNoLeaks("TrackedRenderableBox.Shown(...) must release each factory's boxed +1");
        TestLogger.Info($"TrackedRenderableBox.Shown: {Iterations} constructions + enums disposed, factory box released");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ConstructAndDisposeEnumCases(int iterations)
    {
        var owner = new InlineTrackedRenderable(3);
        for (int i = 0; i < iterations; i++)
        {
            var box = TrackedRenderableBox.Shown(owner);   // factory boxes a temporary +1
            (box as IDisposable)?.Dispose();                 // release the enum's own stored +1
        }
        (owner as IDisposable)?.Dispose();                  // release the owner's own held value
    }

    /// <summary>
    /// Optional-existential setter (<c>RenderableHolder.Primary = ...</c>) — the
    /// <c>PropertyHandler.cs</c> mechanism. The setter wrapper boxes the value conformer at +1 and
    /// passes the buffer <c>@in_guaranteed</c>; Swift copies it into the stored property (releasing
    /// the previous value), so the C# box is a distinct +1 the finally must destroy gated on the
    /// owns-bit. Disposing the holder releases the property's final +1; a leaked setter box pins the
    /// owner's refs after both are disposed.
    /// </summary>
    public void TestOptionalExistentialSetterReleasesBox()
    {
        DrainFinalizers();
        LifetimeTracker.Reset();

        SetAndDisposeHolder(Iterations);
        DrainFinalizers();

        LifetimeTracker.AssertNoLeaks("RenderableHolder.Primary setter must release each set's boxed +1");
        TestLogger.Info($"RenderableHolder.Primary: {Iterations} sets reusing one conformer + holder disposed, setter box released");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void SetAndDisposeHolder(int iterations)
    {
        var holder = TestLibFunctions.MakeEmptyRenderableHolder();
        var owner = new InlineTrackedRenderable(4);
        for (int i = 0; i < iterations; i++)
        {
            holder.Primary = owner;                         // setter boxes a temporary +1
        }
        (holder as IDisposable)?.Dispose();                 // release the property's final +1
        (owner as IDisposable)?.Dispose();                  // release the owner's own held value
    }
}
