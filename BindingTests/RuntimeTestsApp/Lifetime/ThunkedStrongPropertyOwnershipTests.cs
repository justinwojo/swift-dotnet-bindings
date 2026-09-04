// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using Swift;
using Swift.Runtime;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Lifetime;

/// <summary>
/// Asserts the ownership convention of a Swift stored-property setter whose accessor the
/// binding reaches through the generated native assembly thunk.
///
/// <para>Swift lowers such a setter as <c>(@owned Value, @guaranteed self) -&gt; ()</c>: the
/// callee consumes a +1 on the new value and releases whatever the slot held before. The
/// thunk shifts registers and tail-calls the accessor without owning anything, so the +1 has
/// to come from the C# side. Handing the object over borrowed costs it a retain it never
/// received; nothing fails at the assignment, and the over-release lands later — when the
/// slot is reassigned, or when the owner's deinit releases the ivar.</para>
///
/// <para>Every assertion here reads <c>swift_retainCount</c> through <see cref="Arc.RetainCount"/>
/// on a pointer the test holds, so it is independent of GC timing and runs the same on Mono
/// and NativeAOT. A borrowed hand-over shows up as a net zero where a strong store must be a
/// net +1 — the difference the count is measured for.</para>
/// </summary>
public class ThunkedStrongPropertyOwnershipTests : TestBase
{
    public ThunkedStrongPropertyOwnershipTests(TestResults results) : base(results) { }

    private static IntPtr HandleOf(object o) => ((ISwiftObject)o).SwiftHandle;

    private static void DrainFinalizers()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    /// <summary>
    /// Assigning through the non-optional class-typed setter must leave the assigned object
    /// with exactly one more reference: the host's stored slot now owns it.
    /// </summary>
    public void TestStrongClassSetterTakesOwnership()
    {
        var seed = new ThunkedPropertyPayload(tag: 1);
        var host = new ThunkedStrongPropertyHost(seed);

        var assigned = new ThunkedPropertyPayload(tag: 2);
        IntPtr assignedPtr = HandleOf(assigned);
        nint before = Arc.RetainCount(assignedPtr);

        host.StrongPayload = assigned;

        AssertEqual<long>(before + 1, Arc.RetainCount(assignedPtr),
            "a strong stored-property setter consumes a +1 on its new value, so the assigned "
            + "object must gain exactly one reference; an unchanged count means the value was "
            + "handed over borrowed and the callee released a count it was never given");

        // The slot really holds the object that was handed over.
        AssertEqual<long>(2, (long)host.GetStrongPayloadTag(),
            "the stored slot must hold the assigned payload");

        GC.KeepAlive(host);
        GC.KeepAlive(seed);
        GC.KeepAlive(assigned);
    }

    /// <summary>
    /// Assigning the SAME object into the slot twice nets exactly +1: the second assignment
    /// retains the new value and releases the old one, which is the same object.
    /// </summary>
    public void TestRepeatedStrongAssignmentNetsSingleReference()
    {
        var seed = new ThunkedPropertyPayload(tag: 3);
        var host = new ThunkedStrongPropertyHost(seed);

        var assigned = new ThunkedPropertyPayload(tag: 4);
        IntPtr assignedPtr = HandleOf(assigned);
        nint before = Arc.RetainCount(assignedPtr);

        host.StrongPayload = assigned;
        host.StrongPayload = assigned;

        AssertEqual<long>(before + 1, Arc.RetainCount(assignedPtr),
            "re-assigning the same object retains the incoming value and releases the outgoing "
            + "one, so two assignments still net a single stored reference");

        GC.KeepAlive(host);
        GC.KeepAlive(seed);
        GC.KeepAlive(assigned);
    }

    /// <summary>
    /// The <c>Optional&lt;class&gt;</c> slot follows the same convention, and clearing it back to
    /// nil must give the reference back.
    /// </summary>
    public void TestOptionalClassSetterTakesAndReleasesOwnership()
    {
        var seed = new ThunkedPropertyPayload(tag: 5);
        var host = new ThunkedStrongPropertyHost(seed);

        var assigned = new ThunkedPropertyPayload(tag: 6);
        IntPtr assignedPtr = HandleOf(assigned);
        nint before = Arc.RetainCount(assignedPtr);

        host.OptionalPayload = assigned;

        AssertEqual<long>(before + 1, Arc.RetainCount(assignedPtr),
            "an Optional<class> stored-property setter is @owned exactly like the non-optional "
            + "one, so the assigned object must gain one reference");

        host.OptionalPayload = null;

        AssertEqual<long>(before, Arc.RetainCount(assignedPtr),
            "clearing the slot to nil releases the stored reference, returning the count to "
            + "what it was before the assignment");

        GC.KeepAlive(host);
        GC.KeepAlive(seed);
        GC.KeepAlive(assigned);
    }

    /// <summary>
    /// The end state the count is a proxy for: once the owner is torn down, every reference its
    /// slots held must be given back — exactly once, and only those. An under-retain makes this
    /// teardown release a count the object never received; an over-retain strands one.
    ///
    /// <para>The payload wrappers stay referenced from C# across the whole measurement and the
    /// host is torn down by disposing its handle, so the balance is read at a point the test
    /// chooses rather than whenever a finalizer happens to run.</para>
    /// </summary>
    public void TestOwnerTeardownAfterAssignmentDoesNotOverRelease()
    {
        DrainFinalizers();

        // Churn the assign-and-drop path through the finalizer route first: an over-release
        // from any of these faults the process before the balance below is ever read.
        for (int i = 0; i < 8; i++)
        {
            var churned = new ThunkedStrongPropertyHost(new ThunkedPropertyPayload(tag: i));
            churned.StrongPayload = new ThunkedPropertyPayload(tag: 100 + i);
            churned.OptionalPayload = new ThunkedPropertyPayload(tag: 200 + i);
            churned.OptionalPayload = null;
        }

        DrainFinalizers();
        // A runloop turn so any queued native teardown drains before the balance is measured.
        System.Threading.Thread.Yield();
        DrainFinalizers();

        var seed = new ThunkedPropertyPayload(tag: 300);
        var strong = new ThunkedPropertyPayload(tag: 301);
        var optional = new ThunkedPropertyPayload(tag: 302);

        IntPtr seedPtr = HandleOf(seed);
        IntPtr strongPtr = HandleOf(strong);
        IntPtr optionalPtr = HandleOf(optional);

        nint seedBefore = Arc.RetainCount(seedPtr);
        nint strongBefore = Arc.RetainCount(strongPtr);
        nint optionalBefore = Arc.RetainCount(optionalPtr);

        var host = new ThunkedStrongPropertyHost(seed);
        host.StrongPayload = strong;
        host.OptionalPayload = optional;

        // The +1 has to be observed before the teardown, or a setter that never retained would
        // satisfy the balance below by having moved nothing at all.
        AssertEqual<long>(strongBefore + 1, Arc.RetainCount(strongPtr),
            "the strong slot must own the assigned payload before the host is torn down");
        AssertEqual<long>(optionalBefore + 1, Arc.RetainCount(optionalPtr),
            "the Optional<class> slot must own the assigned payload before the host is torn down");
        AssertEqual<long>(seedBefore, Arc.RetainCount(seedPtr),
            "assigning over the strong slot releases the value the slot held, so the seed is "
            + "back to the count it had before the host was created");

        // Deterministic teardown: the wrapper's handle is the only reference to the host, so
        // disposing it runs the host's deinit here rather than at some later collection.
        host.Dispose();

        AssertEqual<long>(strongBefore, Arc.RetainCount(strongPtr),
            "the host's deinit releases the strong slot exactly once, returning the payload to "
            + "its pre-assignment count; a count still one higher is a reference the teardown "
            + "leaked");
        AssertEqual<long>(optionalBefore, Arc.RetainCount(optionalPtr),
            "the host's deinit releases the Optional<class> slot exactly once, returning that "
            + "payload to its pre-assignment count as well");
        AssertEqual<long>(seedBefore, Arc.RetainCount(seedPtr),
            "the seed was already released when it was assigned over, so the teardown must not "
            + "release it a second time or hold it any longer");

        GC.KeepAlive(seed);
        GC.KeepAlive(strong);
        GC.KeepAlive(optional);
        GC.KeepAlive(host);
    }
}
