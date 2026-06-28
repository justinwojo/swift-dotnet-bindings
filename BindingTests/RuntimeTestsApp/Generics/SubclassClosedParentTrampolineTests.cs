// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Generics;

/// <summary>
/// Runtime regression for subclass-closed generic-parent trampolines.
/// <para>
/// A concrete (non-generic) Swift class that closes ALL of a bound-generic base's type
/// parameters — <c>final class ConcreteLifecycle: LifecycleKernel&lt;ScanReadout, ScanBanner&gt;</c>
/// — cannot be modeled as C# inheritance (the closed instantiation can't be represented in the
/// TypeName), so it emits flat and loses every inherited base method. The generator now surfaces
/// those methods via per-method concrete <c>@_cdecl</c> shims that <c>unsafeBitCast</c> the opaque
/// self to the concrete leaf and call the inherited method (Swift resolves the closed generic's
/// metadata internally), plus matching C# extension methods on the leaf.
/// </para>
/// <para>
/// Two shapes are exercised: the UNCONSTRAINED base (<c>LifecycleKernel</c>) and the
/// protocol-with-associated-type-CONSTRAINED base (<c>GatedKernel&lt;Readout, Gate: StateMachine&gt;</c>,
/// whose open-generic methods don't emit at all). Both must round-trip identically — the mechanism
/// is agnostic to the constraint because no metadata or witness table crosses the C boundary.
/// </para>
/// </summary>
public class SubclassClosedParentTrampolineTests : TestBase
{
    public SubclassClosedParentTrampolineTests(TestResults results) : base(results) { }

    // ---- Shape 1: unconstrained generic base (LifecycleKernel) ----

    public void TestConcreteLifecycle_DefaultPhaseIsZero()
    {
        using var vm = Functions.MakeConcreteLifecycle();
        AssertEqual(0, vm.CurrentPhase(), "Default phase is 0");
    }

    public void TestConcreteLifecycle_PauseResumeRestartRoundTrip()
    {
        // Three zero-arg void control methods inherited from the unconstrained generic base,
        // each mutating private phase state witnessed by the inherited CurrentPhase().
        using var vm = Functions.MakeConcreteLifecycle();
        vm.Pause();
        AssertEqual(1, vm.CurrentPhase(), "Phase after Pause() is 1");
        vm.Resume();
        AssertEqual(2, vm.CurrentPhase(), "Phase after Resume() is 2");
        vm.Restart();
        AssertEqual(0, vm.CurrentPhase(), "Phase after Restart() is 0");
    }

    public void TestConcreteLifecycle_DismissBannerAccumulates()
    {
        // dismissBanner() does `phase = phase &+ 10`, so repeated calls accumulate — confirms
        // each trampoline call mutates the SAME live self payload, not a transient copy.
        using var vm = Functions.MakeConcreteLifecycle();
        vm.DismissBanner();
        AssertEqual(10, vm.CurrentPhase(), "Phase after one DismissBanner() is 10");
        vm.DismissBanner();
        AssertEqual(20, vm.CurrentPhase(), "Phase after two DismissBanner() is 20");
    }

    public void TestConcreteLifecycle_PauseThenDismissComposes()
    {
        using var vm = Functions.MakeConcreteLifecycle();
        vm.Pause();
        vm.DismissBanner();
        AssertEqual(11, vm.CurrentPhase(), "Phase after Pause()+DismissBanner() is 11");
    }

    public void TestConcreteLifecycle_InstancesAreIndependent()
    {
        // Two leaf instances must not share state — catches a stale/aliased self_ pointer that
        // would route all trampoline calls through one shared payload.
        using var a = Functions.MakeConcreteLifecycle();
        using var b = Functions.MakeConcreteLifecycle();
        a.Pause();
        b.DismissBanner();
        AssertEqual(1, a.CurrentPhase(), "Instance A retains its own phase (1)");
        AssertEqual(10, b.CurrentPhase(), "Instance B retains its own phase (10)");
    }

    // ---- Shape 2: PAT-constrained generic base (GatedKernel) ----

    public void TestConcreteGated_DefaultTickCountIsZero()
    {
        using var vm = Functions.MakeConcreteGated();
        AssertEqual(0, vm.TickCount(), "Default tick count is 0");
    }

    public void TestConcreteGated_AdvanceResetRoundTrip()
    {
        // The base GatedKernel<Readout, Gate: StateMachine> is PAT-constrained, so its open-generic
        // methods never emit. These calls prove the concrete @_cdecl shim — bound to the closed
        // ConcreteGated — exposes them anyway, with the closed generic's witness tables resolved
        // entirely inside Swift.
        using var vm = Functions.MakeConcreteGated();
        vm.Advance();
        AssertEqual(1, vm.TickCount(), "Tick count after one Advance() is 1");
        vm.Advance();
        AssertEqual(2, vm.TickCount(), "Tick count after two Advance() is 2");
        vm.Reset();
        AssertEqual(0, vm.TickCount(), "Tick count after Reset() is 0");
    }

    public void TestConcreteGated_InstancesAreIndependent()
    {
        using var a = Functions.MakeConcreteGated();
        using var b = Functions.MakeConcreteGated();
        a.Advance();
        a.Advance();
        b.Advance();
        AssertEqual(2, a.TickCount(), "Instance A retains its own tick count (2)");
        AssertEqual(1, b.TickCount(), "Instance B retains its own tick count (1)");
    }

    public void TestConcreteLifecycleAndGated_DoNotAlias()
    {
        // Different leaves, different base shapes, different extension classes — mutating one must
        // not perturb the other.
        using var life = Functions.MakeConcreteLifecycle();
        using var gate = Functions.MakeConcreteGated();
        life.DismissBanner();
        gate.Advance();
        AssertEqual(10, life.CurrentPhase(), "Lifecycle phase is 10 independently");
        AssertEqual(1, gate.TickCount(), "Gated tick count is 1 independently");
    }
}
