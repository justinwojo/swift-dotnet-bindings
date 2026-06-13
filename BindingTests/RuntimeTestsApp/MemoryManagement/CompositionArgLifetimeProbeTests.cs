// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Runtime.CompilerServices;
using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.MemoryManagement;

/// <summary>
/// Probes the C#-&gt;Swift SEND direction for EC2 composition existentials (<c>any P &amp; Q</c>) —
/// the mirror of <see cref="ExistentialReturnLeakProbeTests"/>, which covers the Swift-&gt;C#
/// RETURN-adopt direction. The single conforming class instance regardless of protocol count
/// is a <see cref="LifetimeTracker"/>-counted <c>TrackedNameableAgeable</c>, vended through an
/// EC2 composition proxy by <c>MakeTrackedNameableAgeable</c>.
///
/// Two distinct hazards live on the send side under the Design B2 lifetime model (proxy
/// registered WEAKLY; the proxy holds the conformer's sole construction +1 / R0):
///
///  - <b>Owned return to Swift (+1).</b> When a C# value flows OUT to Swift at +1 — a closure's
///    return, or a reverse-dispatched getter's return — the marshalling must hand Swift an
///    INDEPENDENT retain. The only C# type implementing the composition interface is the
///    Swift-vended <c>{Composition}Proxy</c>, whose <c>GetExistentialContainer()</c> hands back
///    its stored bytes with NO fresh retain (there is no <c>BoxAsExistential2</c>). So an owned
///    EC2 return ALWAYS mints via <c>ExistentialContainerFactory.CreateOwnedCompositionExistential</c>.
///    Without the mint, Swift's owned release at scope exit drops the proxy's sole +1 — the
///    tracked instance deinits to a live count of 0 while C# still holds the (now dangling) proxy,
///    and the proxy's later teardown double-releases. These probes assert the surviving owner
///    stays live (count 1) THROUGH the native call, then deinits cleanly on Dispose (count 0).
///
///  - <b>Borrowed argument (+0).</b> A composition existential passed as a borrowed argument
///    aliases the proxy's R0 without a fresh retain. A GC between the container bytes being copied
///    into the call buffer and Swift finishing its borrow could finalize the proxy and release R0
///    out from under Swift's borrow. The generated wrapper roots the proxy across the native call
///    with <c>GC.KeepAlive</c>. This GC-timing UAF cannot be forced deterministically red (the
///    fault needs a finalizer to land inside the borrow window), so the assertion is
///    no-crash / no-leak / correct round-trip under induced GC pressure.
///
/// Dispose loops run in <c>[MethodImpl(NoInlining)]</c> helpers so no stale stack slot keeps a
/// proxy alive past its Dispose under Mono's conservative stack scan.
/// </summary>
public class CompositionArgLifetimeProbeTests : TestBase
{
    public CompositionArgLifetimeProbeTests(TestResults results) : base(results) { }

    private static void DrainFinalizers()
    {
        for (int i = 0; i < 4; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
        GC.Collect();
    }

    // --- Owned return to Swift (+1): closure-return emission site --------------

    /// <summary>
    /// A C# closure returning <c>any Nameable &amp; Ageable</c> is invoked by Swift
    /// (<c>consumeProvidedNameableAgeable</c>), which takes the result at +1 and releases it at
    /// scope exit. The generated closure callback (<c>ClosureEmitter.BuildCallbackReturnStatement</c>)
    /// must MINT an independent EC2 retain. This is the deterministic double-free probe: the held
    /// proxy is the sole surviving owner of the tracked instance's R0, so a borrowed-alias return
    /// would let Swift's release deinit it (live count 0) WHILE C# still holds the proxy. With the
    /// mint, the instance survives the call (live count 1) and deinits only on Dispose (live count 0).
    /// </summary>
    public void TestClosureReturnOwnedCompositionMintsIndependentRetain()
    {
        DrainFinalizers();
        LifetimeTracker.Reset();

        HoldCallClosureReturnAndDispose();
        DrainFinalizers();

        LifetimeTracker.AssertNoLeaks("closure-return owned EC2: proxy must deinit cleanly after Dispose");
        TestLogger.Info("consumeProvidedNameableAgeable: owned EC2 closure-return minted an independent retain; no premature dealloc, no leak");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void HoldCallClosureReturnAndDispose()
    {
        var proxy = TestLibFunctions.MakeTrackedNameableAgeable(0);   // R0: live count 1
        LifetimeTracker.AssertLiveCount(1, "closure-return owned EC2: tracked instance live before the call");

        var description = TestLibFunctions.ConsumeProvidedNameableAgeable(() => proxy);
        if (description != "Tracked0:0")
            throw new AssertionException($"closure-return owned EC2: expected round-trip 'Tracked0:0', got '{description}'");

        // Swift took the closure result at +1 and released it at scope exit. With the mint that
        // release balanced an independent retain; the proxy's R0 must be untouched.
        LifetimeTracker.AssertLiveCount(1, "closure-return owned EC2: proxy's +1 must survive Swift's owned release (mint, not borrowed alias)");

        (proxy as IDisposable)?.Dispose();
    }

    // --- Owned return to Swift (+1): reverse-dispatch getter emission site ------

    /// <summary>
    /// Swift reverse-dispatches into a C# <c>INameableAgeableProvider</c> conformer's <c>Provided</c>
    /// getter (<c>readProvidedNameableAgeable</c>), taking the returned <c>any Nameable &amp; Ageable</c>
    /// at +1. The receiver-getter owned-return marshalling
    /// (<c>ProtocolProxyEmitter.Receivers</c> → <c>GetOwnedParameterElementConversion</c>) must mint
    /// the same independent EC2 retain as the closure path — a DISTINCT emission site with the same
    /// obligation. The conformer holds the sole proxy, so a borrowed-alias return would deinit the
    /// tracked instance (live count 0) the moment Swift releases.
    /// </summary>
    public void TestReverseDispatchGetterOwnedCompositionMintsIndependentRetain()
    {
        DrainFinalizers();
        LifetimeTracker.Reset();

        HoldCallReverseDispatchGetterAndDispose();
        DrainFinalizers();

        LifetimeTracker.AssertNoLeaks("reverse-dispatch getter owned EC2: proxy must deinit cleanly after Dispose");
        TestLogger.Info("readProvidedNameableAgeable: owned EC2 getter-return minted an independent retain; no premature dealloc, no leak");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void HoldCallReverseDispatchGetterAndDispose()
    {
        var proxy = TestLibFunctions.MakeTrackedNameableAgeable(3);   // R0: live count 1
        var provider = new TrackedNameableAgeableProvider(proxy);
        LifetimeTracker.AssertLiveCount(1, "reverse-dispatch getter owned EC2: tracked instance live before the call");

        var description = TestLibFunctions.ReadProvidedNameableAgeable(provider);
        if (description != "Tracked3:3")
            throw new AssertionException($"reverse-dispatch getter owned EC2: expected round-trip 'Tracked3:3', got '{description}'");

        LifetimeTracker.AssertLiveCount(1, "reverse-dispatch getter owned EC2: proxy's +1 must survive Swift's owned release (mint, not borrowed alias)");

        GC.KeepAlive(provider);
        (proxy as IDisposable)?.Dispose();
    }

    /// <summary>
    /// C# conformer whose <c>Provided</c> getter hands Swift the SAME held proxy on every read.
    /// Swift reverse-dispatches into this getter and takes the result at +1.
    /// </summary>
    private sealed class TrackedNameableAgeableProvider : INameableAgeableProvider
    {
        private readonly IAgeableAndNameable _proxy;
        public TrackedNameableAgeableProvider(IAgeableAndNameable proxy) => _proxy = proxy;
        public IAgeableAndNameable Provided => _proxy;
    }

    // --- Borrowed argument (+0): GC.KeepAlive across the native call ------------

    /// <summary>
    /// A composition existential passed as a borrowed (+0) argument
    /// (<c>processNameableAgeable</c>) aliases the proxy's R0; the generated wrapper roots the
    /// proxy with <c>GC.KeepAlive</c> across the native call so a mid-call finalizer can't release
    /// the bytes Swift is borrowing.
    ///
    /// <para><b>This probe cannot go deterministically red, by construction.</b> The keepAlive
    /// window lives INSIDE the generated wrapper, after it copies the container bytes into the call
    /// buffer and before the native call returns. A test caller can't reach into that window: it
    /// must hold a reference to pass the proxy in (and to Dispose it afterward), and any such
    /// caller-side reference already roots the proxy — so removing the wrapper's keepAlive would
    /// NOT make this probe fault. The Dispose below is cleanup for the no-leak assertion, not the
    /// safety mechanism under test. Deterministic proof that the wrapper actually EMITS the keepAlive
    /// is at the emitter layer:
    /// <c>ClosureEmitterDirectTests.EmitClosureReturnMarshalling_CompositionExistentialArg_PinsProxyAcrossNativeCall</c>
    /// and <c>PropertyHandlerTests.Emit_OptionalCompositionExistentialProperty_SetterPinsValueAcrossNativeCall</c>.
    /// This probe's role is the complementary end-to-end one: correct round-trip and no leak under
    /// induced GC pressure across many iterations, on both the Mono-JIT and NativeAOT runtimes.</para>
    /// </summary>
    public void TestBorrowedCompositionArgRootedAcrossCall()
    {
        DrainFinalizers();
        LifetimeTracker.Reset();

        PassBorrowedCompositionArgsUnderGcPressure(200);
        DrainFinalizers();

        LifetimeTracker.AssertNoLeaks("borrowed EC2 arg: each proxy must deinit after Dispose; no UAF, no leak");
        TestLogger.Info("processNameableAgeable: 200 borrowed EC2 args round-tripped correctly under GC pressure; no crash, no leak");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void PassBorrowedCompositionArgsUnderGcPressure(int iterations)
    {
        for (int i = 0; i < iterations; i++)
        {
            var proxy = TestLibFunctions.MakeTrackedNameableAgeable(i);
            // Pressure the GC right before the borrow. (Note: `proxy` is rooted here regardless of
            // the wrapper's keepAlive — see the method doc; this exercises the path, it does not
            // isolate the intra-wrapper keepAlive window.)
            GC.Collect();
            var description = TestLibFunctions.ProcessNameableAgeable(proxy);
            if (description != $"Tracked{i} is {i}")
                throw new AssertionException($"borrowed EC2 arg: expected round-trip 'Tracked{i} is {i}', got '{description}'");
            (proxy as IDisposable)?.Dispose();
        }
    }
}
