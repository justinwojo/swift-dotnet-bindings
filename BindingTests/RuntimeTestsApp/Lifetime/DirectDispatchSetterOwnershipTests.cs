// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using Swift;
using Swift.Runtime;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Lifetime;

/// <summary>
/// Asserts the ownership convention of a class-valued Swift setter the binding reaches by
/// DIRECT dispatch — the P/Invoke names the accessor's own <c>$s…</c> symbol under
/// <c>CallConvSwift</c>, with no assembly thunk and no Swift-source wrapper in between.
///
/// <para>SILGen lowers this subscript setter as
/// <c>(@owned Slot, Int, @inout DirectSetterSlots) -&gt; ()</c>: the new value is a loadable
/// class reference passed directly at +1, and the callee releases whatever the slot held
/// before. Nothing between C# and that accessor can supply the +1, so the call site has to.
/// Handing the object over borrowed costs it a retain it never received; the assignment still
/// succeeds and the over-release lands later, when the slot is written again or the owner is
/// deinitialized.</para>
///
/// <para>The thunked arm of the same convention is covered next door. This class exists
/// because the direct arm had no first-party coverage at all: its only known instance was a
/// subscript setter in a shipped Apple binding, where the absence of a wrapper library is what
/// puts every member on the direct path.</para>
///
/// <para>Every assertion reads <c>swift_retainCount</c> through <see cref="Arc.RetainCount"/>
/// on a pointer the test holds, so it is independent of GC timing and reads the same on Mono
/// and NativeAOT. An under-retain shows up as a net zero where a strong store must be a net
/// +1; an over-retain shows up as a count that never comes back down after teardown.</para>
/// </summary>
public class DirectDispatchSetterOwnershipTests : TestBase
{
    public DirectDispatchSetterOwnershipTests(TestResults results) : base(results) { }

    private static IntPtr HandleOf(object o) => ((ISwiftObject)o).SwiftHandle;

    // The binding's payload-adopting constructor takes a SwiftHandle, which converts implicitly
    // from IntPtr — so an untyped integer literal is ambiguous against Slot's own `init(tag: Int)`.
    // Funnelling every construction through an nint parameter makes the tag overload an exact match.
    private static DirectSetterSlots.Slot MakeSlot(nint tag) => new DirectSetterSlots.Slot(tag);

    private static void DrainFinalizers()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    /// <summary>
    /// Assigning through the directly-dispatched subscript setter must leave the assigned
    /// object with exactly one more reference: the collection's slot now owns it.
    /// </summary>
    public void TestDirectSubscriptSetterTakesOwnership()
    {
        var slots = new DirectSetterSlots(
            MakeSlot(1),
            MakeSlot(2));

        var assigned = MakeSlot(3);
        IntPtr assignedPtr = HandleOf(assigned);
        nint before = Arc.RetainCount(assignedPtr);

        slots[0] = assigned;

        AssertEqual<long>(before + 1, Arc.RetainCount(assignedPtr),
            "a subscript setter consumes a +1 on its new value exactly as a stored-property "
            + "setter does, and reaching the accessor directly rather than through a wrapper "
            + "does not change that; an unchanged count means the value was handed over "
            + "borrowed and the callee released a count it was never given");

        AssertEqual<long>(3L, (long)slots.TagAt(0),
            "the slot must hold the object that was handed over");

        GC.KeepAlive(slots);
        GC.KeepAlive(assigned);
    }

    /// <summary>
    /// Assigning the SAME object into the slot twice nets exactly +1: the second assignment
    /// retains the incoming value and releases the outgoing one, which is the same object.
    /// </summary>
    public void TestRepeatedDirectAssignmentNetsSingleReference()
    {
        var slots = new DirectSetterSlots(
            MakeSlot(4),
            MakeSlot(5));

        var assigned = MakeSlot(6);
        IntPtr assignedPtr = HandleOf(assigned);
        nint before = Arc.RetainCount(assignedPtr);

        slots[1] = assigned;
        slots[1] = assigned;

        AssertEqual<long>(before + 1, Arc.RetainCount(assignedPtr),
            "re-assigning the same object retains the incoming value and releases the "
            + "outgoing one, so two assignments still net a single stored reference");

        GC.KeepAlive(slots);
        GC.KeepAlive(assigned);
    }

    /// <summary>
    /// The end state the count is a proxy for: once the owner is torn down, every reference
    /// its slots held must be given back — exactly once, and only those. An under-retain makes
    /// this teardown release a count the object never received; an over-retain strands one, and
    /// the stranded count is what the post-teardown assertions catch.
    ///
    /// <para>The payload wrappers stay referenced from C# across the whole measurement and the
    /// collection is torn down by disposing its handle, so the balance is read at a point the
    /// test chooses rather than whenever a finalizer happens to run.</para>
    /// </summary>
    public void TestDirectSetterTeardownBalancesExactly()
    {
        DrainFinalizers();

        // Churn the assign-and-drop path through the finalizer route first: an over-release
        // from any of these faults the process before the balance below is ever read.
        for (int i = 0; i < 8; i++)
        {
            var churned = new DirectSetterSlots(
                MakeSlot(i),
                MakeSlot(100 + i));
            churned[0] = MakeSlot(200 + i);
            churned[1] = MakeSlot(300 + i);
        }

        DrainFinalizers();
        System.Threading.Thread.Yield();
        DrainFinalizers();

        var seed = MakeSlot(400);
        var other = MakeSlot(401);
        var assigned = MakeSlot(402);

        IntPtr seedPtr = HandleOf(seed);
        IntPtr assignedPtr = HandleOf(assigned);

        nint seedBefore = Arc.RetainCount(seedPtr);
        nint assignedBefore = Arc.RetainCount(assignedPtr);

        var slots = new DirectSetterSlots(seed, other);

        // Precondition, not the subject: the collection stores its seeds strongly, which is what
        // makes the release-the-old-value half of the setter observable at all.
        AssertEqual<long>(seedBefore + 1, Arc.RetainCount(seedPtr),
            "constructing the collection stores the seed strongly");

        slots[0] = assigned;

        // The +1 has to be observed before the teardown, or a setter that never retained would
        // satisfy the balance below by having moved nothing at all.
        AssertEqual<long>(assignedBefore + 1, Arc.RetainCount(assignedPtr),
            "the slot must own the assigned payload before the collection is torn down");
        AssertEqual<long>(seedBefore, Arc.RetainCount(seedPtr),
            "assigning over the slot releases the value it held, so the seed is back to the "
            + "count it had before the collection was created");

        // Deterministic teardown: the wrapper's handle is the only reference to the collection,
        // so disposing it runs the storage release here rather than at some later collection.
        slots.Dispose();

        AssertEqual<long>(assignedBefore, Arc.RetainCount(assignedPtr),
            "tearing the collection down releases the stored slot exactly once, returning the "
            + "payload to its pre-assignment count; a count still one higher is a reference the "
            + "hand-over over-retained and nothing ever gave back");
        AssertEqual<long>(seedBefore, Arc.RetainCount(seedPtr),
            "the seed was already released when it was assigned over, so the teardown must not "
            + "release it a second time or hold it any longer");

        GC.KeepAlive(seed);
        GC.KeepAlive(other);
        GC.KeepAlive(assigned);
        GC.KeepAlive(slots);
    }
}
