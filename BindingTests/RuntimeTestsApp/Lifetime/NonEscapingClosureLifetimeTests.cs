// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Lifetime;

/// <summary>
/// Theme C regression: a NON-escaping closure handed to Swift is invoked synchronously inside the
/// call and Swift never assumes ownership, so the wrapper's per-call <c>GCHandle</c> (which roots
/// the managed delegate and everything it captures) must be freed on return. The closure-bridge
/// emitters previously gated the GCHandle-freeing <c>try/finally</c> on <c>escaping</c>, so the
/// non-escaping branch never freed the handle: it leaked for the process lifetime, a
/// <see cref="WeakReference"/> to the captured target stayed alive forever, and N calls accumulated
/// N live targets.
/// </summary>
/// <remarks>
/// <para>
/// Covers all three non-escaping-closure paths in one place:
/// <list type="bullet">
/// <item><b>ProtocolExtensionClosureBridge</b> — <c>PExtClosureSeed.RunNonEscapingVoid</c>; the fix
/// emits the <c>finally</c> unconditionally (<c>ProtocolExtensionClosureBridge.cs</c>).</item>
/// <item><b>NestedClosureBridge</b> — <c>NestedClosureHost.RunNonEscapingOuter</c>; the fix changed
/// the outer-closure finally gate from <c>anyEscaping</c> to <c>hasClosures</c>
/// (<c>NestedClosureBridge.cs</c>).</item>
/// <item><b>MethodClosureBridge</b> — <c>NonEscapingMCBFixture.RunSynchronously</c>; freed via the
/// <c>ClosureHandle</c> helper's unconditional dispose (regression coverage for that path).</item>
/// </list>
/// </para>
/// <para>
/// Unlike the escaping/async leak probes (<see cref="EscapingClosureLifetimeTests"/> /
/// <see cref="AsyncClosureContextLifetimeTests"/>), which depend on the native <c>_SBClosureCtx</c>
/// deinit and are therefore <c>[SkipOnSimulator]</c>, the non-escaping handle is freed in pure
/// managed code (the C# <c>finally</c> for NCB/PECB, the <c>ClosureHandle</c> dispose for MCB) with
/// no dylib involvement — so these assertions hold on the Mono simulator too.
/// </para>
/// </remarks>
public class NonEscapingClosureLifetimeTests : TestBase
{
    public NonEscapingClosureLifetimeTests(TestResults results) : base(results) { }

    private const int GcCycles = 6;
    private const int BulkIterations = 25;
    private const int MaxResidualAlive = 5;

    private static void ForceGc()
    {
        var worker = new System.Threading.Thread(ForceGcWorker) { IsBackground = true };
        worker.Start();
        worker.Join();
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void ForceGcWorker()
    {
        var scratch = new object[256];
        for (int i = 0; i < scratch.Length; i++)
            scratch[i] = new object();
        GC.KeepAlive(scratch);

        for (int i = 0; i < GcCycles; i++)
        {
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
        }
    }

    private static int CountAlive(List<WeakReference> weaks)
    {
        int alive = 0;
        foreach (var w in weaks)
            if (w.IsAlive) alive++;
        return alive;
    }

    // ─── ProtocolExtensionClosureBridge: non-escaping protocol-extension closure ───

    /// <summary>
    /// Sanity: the non-escaping protocol-extension closure dispatches synchronously. If the bridge
    /// mis-wired the funcPtr/context, the callback would not fire (or would crash).
    /// </summary>
    public void TestPExtNonEscapingClosure_FiresSynchronously()
    {
        using var seed = new PExtClosureSeed(1);
        bool fired = false;
        seed.RunNonEscapingVoid(() => fired = true);
        AssertTrue(fired, "Non-escaping protocol-extension closure must fire synchronously inside the call");
    }

    /// <summary>
    /// Leak regression: N independent calls must not accumulate live delegate targets. Pre-fix the
    /// PExtCB <c>try/finally</c> was gated on <c>IsEscaping</c>, so the non-escaping handle leaked
    /// and the live count grew linearly with the number of calls.
    /// </summary>
    public void TestPExtNonEscapingClosure_DoesNotLeakDelegateTarget()
    {
        var weaks = new List<WeakReference>(BulkIterations);
        for (int i = 0; i < BulkIterations; i++)
            weaks.Add(PExtNonEscapingRound());

        ForceGc();

        int alive = CountAlive(weaks);
        TestLogger.Info($"[NonEscapingClosureLifetime] PExtCB {BulkIterations}x: alive={alive}");
        AssertTrue(alive <= MaxResidualAlive,
            $"{alive}/{BulkIterations} non-escaping protocol-extension closure targets stayed alive after GC " +
            $"(tolerance {MaxResidualAlive}). The per-call GCHandle's finally free is not running.");
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static WeakReference PExtNonEscapingRound()
    {
        var seed = new PExtClosureSeed(1);
        var target = new CapturedTarget();
        // Lambda captures `target`; while the per-call GCHandle is live the delegate (and `target`)
        // stay rooted. Freeing the handle in the wrapper's finally makes both collectible.
        Action callback = () => target.Receive(1);
        var weak = new WeakReference(target);

        seed.RunNonEscapingVoid(callback);

        callback = null!;
        target = null!;
        seed.Dispose();
        return weak;
    }

    // ─── NestedClosureBridge: non-escaping outer closure ───

    /// <summary>
    /// Sanity: the non-escaping outer closure dispatches with the correct argument and a non-null
    /// inner completion. Mirrors <c>TestRunOneOuterInvoked</c> but for the non-escaping outer.
    /// </summary>
    public void TestNestedNonEscapingOuter_FiresSynchronously()
    {
        using var host = new NestedClosureHost();
        int outerArg = -1;
        bool innerSeen = false;
        host.RunNonEscapingOuter((arg, inner) =>
        {
            outerArg = arg;
            innerSeen = inner != null;
        });
        AssertEqual(7, outerArg, "Non-escaping outer closure received arg=7");
        AssertTrue(innerSeen, "Non-escaping outer closure received non-null inner completion");
    }

    /// <summary>
    /// Leak regression: N independent calls must not accumulate live outer-delegate targets. Pre-fix
    /// the NCB outer-closure <c>try/finally</c> was gated on <c>anyEscaping</c>, so a non-escaping
    /// outer leaked its handle and the live count grew linearly.
    /// </summary>
    public void TestNestedNonEscapingOuter_DoesNotLeakDelegateTarget()
    {
        var weaks = new List<WeakReference>(BulkIterations);
        for (int i = 0; i < BulkIterations; i++)
            weaks.Add(NestedNonEscapingRound(i));

        ForceGc();

        int alive = CountAlive(weaks);
        TestLogger.Info($"[NonEscapingClosureLifetime] NCB {BulkIterations}x: alive={alive}");
        AssertTrue(alive <= MaxResidualAlive,
            $"{alive}/{BulkIterations} non-escaping outer-closure targets stayed alive after GC " +
            $"(tolerance {MaxResidualAlive}). The outer GCHandle's finally free is not running.");
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static WeakReference NestedNonEscapingRound(int seed)
    {
        var host = new NestedClosureHost();
        var target = new CapturedTarget();
        Action<int, Action<int>> handler = (arg, _) => target.Receive(arg + seed);
        var weak = new WeakReference(target);

        host.RunNonEscapingOuter(handler);

        handler = null!;
        target = null!;
        host.Dispose();
        return weak;
    }

    // ─── MethodClosureBridge: non-escaping closure via ClosureHandle ───

    /// <summary>
    /// Sanity: the non-escaping MCB closure dispatches and the method returns the delegate's result.
    /// </summary>
    public void TestMcbNonEscapingClosure_FiresSynchronously()
    {
        using var fixture = new NonEscapingMCBFixture();
        bool result = fixture.RunSynchronously(pr => TestLibFunctions.ProcessResultIsSuccess(pr));
        AssertTrue(result, "Non-escaping MCB closure dispatches and returns the delegate's value");
    }

    /// <summary>
    /// Leak regression for the MethodClosureBridge non-escaping path (freed via the
    /// <c>ClosureHandle</c> helper's unconditional dispose). N independent calls must not accumulate
    /// live delegate targets.
    /// </summary>
    public void TestMcbNonEscapingClosure_DoesNotLeakDelegateTarget()
    {
        var weaks = new List<WeakReference>(BulkIterations);
        for (int i = 0; i < BulkIterations; i++)
            weaks.Add(McbNonEscapingRound());

        ForceGc();

        int alive = CountAlive(weaks);
        TestLogger.Info($"[NonEscapingClosureLifetime] MCB {BulkIterations}x: alive={alive}");
        AssertTrue(alive <= MaxResidualAlive,
            $"{alive}/{BulkIterations} non-escaping MCB closure targets stayed alive after GC " +
            $"(tolerance {MaxResidualAlive}). The ClosureHandle dispose is not freeing the handle.");
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static WeakReference McbNonEscapingRound()
    {
        var fixture = new NonEscapingMCBFixture();
        var target = new CapturedTarget();
        // Capture `target` in the predicate. The ProcessResult arg is borrowed (the wrapper owns its
        // lifetime for a non-escaping closure); the probe weak-refs only `target`, a pure C# object.
        Func<ProcessResult, bool> predicate = _ => { target.Receive(1); return true; };
        var weak = new WeakReference(target);

        fixture.RunSynchronously(predicate);

        predicate = null!;
        target = null!;
        fixture.Dispose();
        return weak;
    }
}
