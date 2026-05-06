// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using Swift;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Lifetime;

/// <summary>
/// GC stress tests that trigger ForceGC() or construct MutableProps.
/// Separated from OwnershipTests for independent failure isolation.
/// </summary>
public class OwnershipGCStressTests : TestBase
{
    public OwnershipGCStressTests(TestResults results) : base(results) { }

    // ---- 0.10.0 Layer C lifetime harness (populated by Bundles 1 and 3) ------
    //
    // Long-running / GC-pressure assertions for this class are gated by
    // `TestRunFlags.Lifetime` — set via `nuke binding-tests --lifetime`. Off by
    // default for inner-loop simulator runs; enabled unconditionally on the
    // integration serial gate. The 0.10.0 SafeHandle-refcount and
    // closure-lifetime bundles will populate methods here that loop a repro
    // pattern ~10k times with `GC.Collect()` between runs and assert
    // deterministic Swift alloc/dealloc counters return to baseline,
    // `CFGetRetainCount` returns to baseline for bridged ObjC objects, RSS
    // stays under a budget, and no finalizer-thread exceptions are logged.
    // See `src/docs/0.10.0-fix-plan.md` §"Layer C — lifetime harness".

    #region Basic Retain/Release Balance (ForceGC)

    public void TestAnimalCreateUseRelease()
    {
        // Create object, use it, let it go out of scope, GC — no crash
        var animal = TestLibFunctions.CreateAnimal("Temp", "Woof");
        var name = animal.Name.ToString();
        AssertEqual("Temp", name, "Animal accessible after creation");

        var speak = animal.GetSpeak();
        AssertNotNull(speak, "Speak returns result");

        // Let the reference go and force GC — should not crash
        animal = null!;
        ForceGC();

        TestLogger.Info("Create-use-release cycle completed without crash");
    }

    public void TestUniqueResourceCreateUseRelease()
    {
        // UniqueResource via factory
        var resource = TestLibFunctions.CreateUniqueResource(42);
        var id = resource.Id;
        AssertEqual(42, id, "UniqueResource.Id accessible");

        var inspected = resource.GetInspect();
        AssertEqual(42, inspected, "Inspect returns correct id");

        resource = null!;
        ForceGC();

        TestLogger.Info("UniqueResource create-use-release completed");
    }

    public void TestUniqueResourceConstructorLifecycle()
    {
        // UniqueResource via public constructor
        var resource = new UniqueResource(99);
        AssertEqual(99, resource.Id, "Constructor-created resource has correct Id");

        resource = null!;
        ForceGC();

        TestLogger.Info("UniqueResource constructor lifecycle completed");
    }

    #endregion

    #region MutableProps (CallConvSwift constructor)

    public void TestMutablePropsLifecycle()
    {
        // MutableProps struct lifecycle
        var props = new MutableProps(10, "Test");
        AssertEqual(10, props.Value, "MutableProps.Value accessible");
        AssertEqual("Test", props.Name.ToString(), "MutableProps.Name accessible");

        // Modify and verify
        props.Value = 20;
        AssertEqual(20, props.Value, "MutableProps.Value after set");

        props = null!;
        ForceGC();

        TestLogger.Info("MutableProps lifecycle completed");
    }

    public void TestMutablePropsDoubleDispose()
    {
        var props = new MutableProps(5, "DoubleDispose");
        AssertEqual(5, props.Value, "Accessible before dispose");

        props.Dispose();
        props.Dispose();

        TestLogger.Info("MutableProps double-dispose safe");
    }

    public void TestMutablePropsAccessAfterDispose()
    {
        var props = new MutableProps(10, "Test");
        props.Dispose();

        AssertThrows<ObjectDisposedException>(() =>
        {
            _ = props.Value;
        }, "MutableProps.Value after dispose throws");

        TestLogger.Info("MutableProps access after dispose correctly throws");
    }

    public void TestMutablePropsSetAfterDispose()
    {
        var props = new MutableProps(10, "Test");
        props.Dispose();

        AssertThrows<ObjectDisposedException>(() =>
        {
            props.Value = 99;
        }, "MutableProps.Value set after dispose throws");

        TestLogger.Info("MutableProps set after dispose correctly throws");
    }

    #endregion

    #region GC Stress with Ownership

    public void TestObjectSurvivesRepeatedGC()
    {
        var animal = TestLibFunctions.CreateAnimal("Survivor", "Roar");

        // Multiple GC cycles
        for (int i = 0; i < 10; i++)
        {
            ForceGC();
            var name = animal.Name.ToString();
            AssertEqual("Survivor", name, $"Survives GC cycle {i}");
        }

        TestLogger.Info("Object survives 10 GC cycles");
    }

    public void TestManyObjectsCreateAndAbandon()
    {
        // Create many objects and let them go — GC should clean up without crash
        for (int i = 0; i < 100; i++)
        {
            var animal = TestLibFunctions.CreateAnimal($"Temp{i}", "Sound");
            _ = animal.Name.ToString();
            // Intentionally not holding reference — GC will collect
        }

        ForceGC();

        // Create one more to verify the system is still healthy
        var final = TestLibFunctions.CreateAnimal("Final", "OK");
        AssertEqual("Final", final.Name.ToString(), "System healthy after mass abandonment");

        TestLogger.Info("100 objects created and abandoned without crash");
    }

    public void TestInterleavedCreateDispose()
    {
        // Interleave creation and disposal
        var animals = new List<Animal>();

        for (int i = 0; i < 20; i++)
        {
            animals.Add(TestLibFunctions.CreateAnimal($"Animal{i}", $"Sound{i}"));

            // Dispose every 5th object
            if (i % 5 == 4 && animals.Count > 0)
            {
                var toDispose = animals[0];
                toDispose.Dispose();
                animals.RemoveAt(0);
            }
        }

        // Verify remaining animals are still valid
        foreach (var animal in animals)
        {
            var name = animal.Name.ToString();
            AssertNotNull(name, "Remaining animal has valid name");
        }

        TestLogger.Info("Interleaved create/dispose completed without corruption");
    }

    public void TestGCPressureDuringPropertyAccess()
    {
        var animal = TestLibFunctions.CreateAnimal("Pressure", "Test");

        // Access properties while creating GC pressure
        for (int i = 0; i < 50; i++)
        {
            // Create garbage
            _ = new byte[4096];

            // Access Swift object
            var name = animal.Name.ToString();
            AssertEqual("Pressure", name, $"Property access under GC pressure {i}");
        }

        ForceGC();

        // Still works after pressure
        AssertEqual("Pressure", animal.Name.ToString(), "Survives GC pressure loop");

        TestLogger.Info("Property access stable under GC pressure");
    }

    #endregion

    #region Bundle 01 Lifetime Stress (TestRunFlags.Lifetime gate)

    /// <summary>
    /// 0.10.0 Bundle 01 Layer C: stress the four SafeHandle / refcount fixes
    /// under GC pressure. Off by default for inner-loop sim runs (gated by
    /// <see cref="TestRunFlags.Lifetime"/>); the integration serial gate
    /// enables it via <c>nuke binding-tests --lifetime</c>.
    ///
    /// Three bug-fix surfaces are exercised in a single 10k-iteration loop:
    ///
    /// 1. <b>Bug 1a — Equals pinning.</b> <c>Tag.Equals(other)</c> is the
    ///    refType + eqSymbol path that historically called the @_cdecl
    ///    PInvoke_eq with raw <c>DangerousGetHandle()</c> on both operands.
    ///    The fix routes through <c>_PInvoke_eq_pinned</c>, which brackets
    ///    DangerousAddRef/DangerousRelease around both SafeHandles. A
    ///    10k-iteration loop with <see cref="GC.Collect"/> between groups
    ///    forces concurrent finalization windows; underflow would surface as
    ///    an ObjectDisposedException or use-after-free on the underlying
    ///    Swift heap payload.
    ///
    /// 2. <b>Bug 3 — Generic enum extractor heap-alloc.</b>
    ///    <c>Holder&lt;IntBox&gt;.TryGetWrapped</c> exercises the class-T
    ///    branch of the bare-generic-parameter extractor: enumCopy is
    ///    stackalloc-d, the class pointer is dereferenced, and Arc.Retain
    ///    explicitly balances the SwiftClassHandle's eventual Arc.Release on
    ///    Dispose. <c>Holder&lt;SwiftString&gt;.TryGetWrapped</c> exercises
    ///    the non-class branch, which now heap-allocates + InitializeWithCopy
    ///    + transfer-ownership. The pre-fix shape would NativeMemory.Free a
    ///    stack pointer on Dispose — undefined behavior.
    ///
    /// 3. <b>Bug 2 — DeferredSafeHandleRelease balance.</b> Indirectly via
    ///    the <see cref="SwiftClassHandle{T}"/> dispose path executed inside
    ///    every iteration. A non-balanced AddRef would surface as an
    ///    ObjectDisposedException on the second iteration that re-uses the
    ///    same Swift heap object identity (after refcount underflow).
    ///
    /// Bug 4 (NSArray owns:true) is generator-only — the bridged-ObjC
    /// surface lives in Apple framework consumers, not BindingTests.
    /// </summary>
    public void TestBundle01_LifetimeStress_EqualsAndGenericEnumExtractor()
    {
        if (!TestRunFlags.Lifetime)
        {
            TestLogger.Info("Bundle 01 lifetime stress skipped (run with --lifetime to enable)");
            return;
        }

        // Reset Swift's allocation counter so we can assert leak-free
        // exit at the end. The counter tracks every TrackedObject /
        // TrackedContainer ctor/deinit; IntBox / SwiftString do NOT increment
        // the counter, so the stress loop's leak budget is purely the
        // TrackedObject pre-loop allocation.
        LifetimeTracker.Reset();

        const int Iterations = 10_000;
        const int GCInterval = 500;

        // Bug 1a — Equals stress. Tag is a non-frozen Equatable struct
        // (the refType + eqSymbol path patched by _PInvoke_eq_pinned).
        for (int i = 0; i < Iterations; i++)
        {
            var a = new Tag("env", "prod");
            var b = new Tag("env", "prod");
            var c = new Tag("env", "dev");

            AssertTrue(a.Equals(b), $"iter {i}: Tag.Equals(b) — same key+value");
            AssertFalse(a.Equals(c), $"iter {i}: Tag.Equals(c) — different value");

            // Don't Dispose explicitly — let GC drive finalization. That's the
            // exact path Bug 1a was designed to protect: concurrent finalizer
            // freeing the Swift heap payload between DangerousGetHandle and
            // Swift function entry.
            if (i % GCInterval == 0)
            {
                ForceGC();
            }
        }

        // Bug 3 — Generic enum extractor stress. Holder<IntBox> exercises
        // the class-T branch (Arc.Retain explicit balance); Holder<SwiftString>
        // exercises the non-class branch (heap-alloc + InitializeWithCopy +
        // Free-if-not-ISwiftObject). Pre-fix Holder<SwiftString> would crash
        // on Dispose via NativeMemory.Free on a stack pointer; pre-fix
        // Holder<IntBox> would underflow ARC and surface as a crash on the
        // second iteration that re-allocates the same Swift heap address.
        for (int i = 0; i < Iterations; i++)
        {
            using (var holderClass = TestLibFunctions.MakeWrappedIntBox(42))
            {
                AssertTrue(holderClass.TryGetWrapped(out var box),
                    $"iter {i}: Holder<IntBox>.TryGetWrapped");
                using (box)
                {
                    AssertEqual(42, box!.Value, $"iter {i}: IntBox.value round-trip");
                }
            }

            using (var holderStruct = TestLibFunctions.MakeWrappedString("hello"))
            {
                AssertTrue(holderStruct.TryGetWrapped(out var str),
                    $"iter {i}: Holder<SwiftString>.TryGetWrapped");
                using (str)
                {
                    AssertEqual("hello", str!.ToString(), $"iter {i}: SwiftString round-trip");
                }
            }

            if (i % GCInterval == 0)
            {
                ForceGC();
            }
        }

        // Final GC + assertion: every IntBox / SwiftString allocation in the
        // loop should be deallocated. We don't assert exact counts — the loop
        // doesn't track them — but we DO assert no NEW TrackedObject leaked
        // (the global counter is for TrackedObject, which we don't allocate
        // in this loop). The assertion's primary value is forcing a final GC
        // round and verifying no finalizer-thread crashes occurred (those
        // would have surfaced as a process abort, not a counter mismatch).
        ForceGC();
        Thread.Sleep(100);
        ForceGC();

        var (alloc, dealloc, live) = LifetimeTracker.GetStats();
        TestLogger.Info(
            $"Bundle 01 stress completed. {Iterations * 2} IntBox + {Iterations} SwiftString " +
            $"allocations; tracker: alloc={alloc} dealloc={dealloc} live={live}");

        // The TrackedObject counter must be zero — we never allocated any in
        // this loop. A non-zero value indicates a finalizer-thread leak from
        // a different test class; surface that explicitly so the cause is
        // diagnosable.
        AssertEqual(0, live, "Bundle 01 stress: tracker live count returned to baseline");
    }

    #endregion
}
