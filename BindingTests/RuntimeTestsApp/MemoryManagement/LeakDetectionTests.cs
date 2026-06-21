// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using Swift;
using Swift.Runtime;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.MemoryManagement;

/// <summary>
/// Tests for DeinitTracker and struct-with-ref-field patterns.
/// Exercises Buffer vs SafeHandle emission paths for structs containing
/// reference type fields at various offsets.
/// </summary>
public class LeakDetectionTests : TestBase
{
    public LeakDetectionTests(TestResults results) : base(results) { }

    #region FrozenStructWithRef (ClassWithBufferStruct)

    public void TestFrozenStructWithRefCreation()
    {
        using var fs = new FrozenStructWithRef(42);
        AssertEqual(42, fs.GetValue(), "Frozen struct with ref preserves value");
    }

    public void TestFrozenStructWithRefPassThrough()
    {
        using var fs = new FrozenStructWithRef(99);
        using var result = TestLibFunctions.PassThroughFrozenWithRef(fs);
        AssertEqual(99, result.GetValue(), "Pass-through preserves frozen struct value");
    }

    #endregion

    #region NestedFrozenStructWithRef

    public void TestNestedFrozenStructWithRefCreation()
    {
        using var nfs = new NestedFrozenStructWithRef(77);
        AssertEqual(77, nfs.GetValue(), "Nested frozen struct preserves value");
    }

    public void TestNestedFrozenStructWithRefPassThrough()
    {
        using var nfs = new NestedFrozenStructWithRef(55);
        using var result = TestLibFunctions.PassThroughNestedFrozenWithRef(nfs);
        AssertEqual(55, result.GetValue(), "Pass-through preserves nested struct value");
    }

    #endregion

    #region RetainCycles (Unsupported)

    [Skip("weak/unowned references not supported by generator")]
    public void TestStrongCycleCreation()
    {
        // StrongNodeA/B use weak/unowned — not yet supported
    }

    [Skip("weak/unowned references not supported by generator")]
    public void TestTreeCycleWithWeakParent()
    {
        // CycleTreeNode uses weak parent — not yet supported
    }

    [Skip("weak/unowned references not supported by generator")]
    public void TestOwnerResourceUnowned()
    {
        // ResourceOwner/OwnedResource use unowned — not yet supported
    }

    [Skip("weak/unowned references not supported by generator")]
    public void TestDelegatePatternWeakRef()
    {
        // DelegateHolder uses weak delegate — not yet supported
    }

    #endregion

    #region WeakSwiftReference + leak census (F35 — leak-surfacing toolkit)

    // AsyncClosurePropertySetterHolder is a Swift class projected to an ISwiftObject C# class with a
    // stored closure property (Observer) and a trigger that invokes it — the exact ingredients of the
    // SB1002 retain-cycle shape, so it doubles as the runtime fixture for WeakSwiftReference.

    public void TestWeakSwiftReference_TargetRoundTripsWhileAlive()
    {
        using var holder = new AsyncClosurePropertySetterHolder();
        var weak = new WeakSwiftReference<AsyncClosurePropertySetterHolder>(holder);

        AssertTrue(weak.IsAlive, "Weak reference should be alive while a strong reference is held");
        AssertTrue(ReferenceEquals(holder, weak.Target), "Weak.Target should return the same live instance");
        AssertTrue(
            weak.TryGetTarget(out var got) && ReferenceEquals(got, holder),
            "TryGetTarget should hand back the live target");
    }

    public void TestWeakSwiftReference_BreaksObserverCycleAndStillFires()
    {
        // The prescribed fix for the SB1002 cycle: the stored observer reaches the holder through the
        // WeakSwiftReference rather than capturing it strongly. The callback must still fire correctly
        // through the real Swift closure-property setter round trip while the holder is alive.
        using var holder = new AsyncClosurePropertySetterHolder();
        var weak = new WeakSwiftReference<AsyncClosurePropertySetterHolder>(holder);

        int observed = -1;
        holder.Observer = v =>
        {
            var target = weak.Target;
            if (target != null)
                observed = v * 2;
        };

        holder.TriggerObserver(21);
        AssertEqual(42, observed, "Weak-captured observer should fire while the holder is alive");
    }

    public void TestStrongSelfCaptureCycle_DisposesWithoutCrash()
    {
        // The exact retain-cycle shape SB1002 flags: the delegate stored on the holder strongly
        // captures the holder. The leak is not GC-observable for this untracked fixture, so we assert
        // only that constructing the cycle, firing it, and disposing the strong owner does not crash.
        var holder = new AsyncClosurePropertySetterHolder();
        int fired = -1;
        holder.Observer = v =>
        {
            GC.KeepAlive(holder);
            fired = v;
        };

        holder.TriggerObserver(7);
        AssertEqual(7, fired, "Self-capturing observer should fire");

        // Disposing the strong owner of an unbreakable self-capture cycle must not crash; reaching the
        // end of the method after Dispose is the no-crash proof (no tautological assertion needed).
        holder.Dispose();
    }

    public void TestSwiftLeakCensus_ReportIsCoherent()
    {
        using var holder = new AsyncClosurePropertySetterHolder();

        var report = SwiftLeakCensus.Report();

        // Coherence invariants: strongly-held proxies are a subset of all registered proxies, and no
        // count is negative. A before/after leak *delta* is deliberately not asserted: the generated
        // holder registers through SwiftClassHandle/SwiftDisposeScope, not the weak proxy registry the
        // census counts, and GC timing makes a registry delta non-deterministic — the unbreakable cycle
        // cannot be made deterministically red.
        AssertTrue(
            report.StronglyHeldProxies <= report.RegisteredProxies,
            "Strongly-held proxies are a subset of registered proxies");
        AssertTrue(
            report.RegisteredProxies >= 0 && report.ProxyImplRoots >= 0 && report.StronglyHeldProxies >= 0,
            "Census counts must be non-negative");

        // The report's own rendering carries the [SwiftBindings] breadcrumb and all three named counts,
        // so a logged census is attributable to the binding layer and is never a silent empty string.
        var rendered = report.ToString();
        AssertTrue(rendered.Contains("[SwiftBindings]"), "Census ToString carries the binding breadcrumb");
        AssertTrue(
            rendered.Contains("registered=") && rendered.Contains("stronglyHeld=") && rendered.Contains("implRoots="),
            "Census ToString surfaces all three named counts");
    }

    #endregion
}
