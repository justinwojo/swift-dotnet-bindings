// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Threading.Tasks;
using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Lifetime;

/// <summary>
/// The runtime half of the `~Copyable`-through-a-copy-lane work. The refused lanes have nothing to
/// call by construction — that is what a refusal means — so their evidence lives at the corpus
/// layer. What can only be observed here is the opposite decision: the lane that was made to TAKE
/// the value, and the copyable siblings that had to keep working while it changed.
///
/// <para>
/// The `deinit` counter is the whole point of the first test. A non-copyable value duplicated by a
/// value witness would run its `deinit` once per owner; here the async harness's carrier hands the
/// value over instead, so exactly one `deinit` must be observable across the whole round trip, and
/// it must happen when C# disposes rather than when the carrier is freed.
/// </para>
/// </summary>
public class NoncopyableCopyLaneTests : TestBase
{
    public NoncopyableCopyLaneTests(TestResults results) : base(results) { }

    /// <summary>
    /// The async RETURN carrier is the one lane with an owner to hand the value over from, so it
    /// takes. Counting `deinit`s across the await is what tells a take from a copy: a copy leaves
    /// the carrier still holding a live value, and freeing it would run a second `deinit` on the
    /// value C# is holding.
    /// </summary>
    public async Task TestAsyncReturnedNoncopyableRunsExactlyOneDeinit()
    {
        LifetimeTracker.Reset();

        var resource = await WithTimeout(
            TestLibFunctions.MakeTrackedResourceAsync(4301), DefaultAsyncTimeout);

        AssertEqual(1, LifetimeTracker.GetStats().allocations, "one value was constructed");
        AssertEqual(0, LifetimeTracker.GetStats().deallocations,
            "the async carrier hands the value to C#; freeing it must not destroy a value C# now owns");
        AssertEqual(4301, TestLibFunctions.BorrowTrackedResource(resource),
            "the taken value is intact, not a moved-from husk");

        resource.Dispose();
        AssertEqual(1, LifetimeTracker.GetStats().deallocations,
            "C# is the sole owner after the take, so its Dispose is the only deinit");
    }

    /// <summary>
    /// Taking rather than copying must not weaken the ordinary consumed-ownership contract on the
    /// value it produced: a consuming call still destroys exactly once, and the handle it leaves
    /// behind is inert.
    /// </summary>
    public async Task TestAsyncReturnedNoncopyableStillConsumesExactlyOnce()
    {
        LifetimeTracker.Reset();

        var resource = await WithTimeout(
            TestLibFunctions.MakeTrackedResourceAsync(4302), DefaultAsyncTimeout);

        AssertEqual(4302, TestLibFunctions.ConsumeTrackedResource(resource), "consuming reads the taken value");
        AssertEqual(1, LifetimeTracker.GetStats().deallocations, "consumption destroys once");

        resource.Dispose();
        AssertEqual(1, LifetimeTracker.GetStats().deallocations,
            "Dispose after a consuming call must not destroy moved-from bytes");
    }

    /// <summary>
    /// A `~Copyable` `Self` constructed through a member taking an existential argument. A bare
    /// `any P` argument never reaches the existential-bypass factory (that lane claims only a
    /// defaulted existential inside a container), so this rides the ordinary constructor path, which
    /// writes the result straight into the buffer C# owns. What it holds down is that the path stays
    /// a binding rather than a refusal, and owes the same single `deinit`.
    /// </summary>
    public void TestExistentialArgumentConstructorRunsExactlyOneDeinit()
    {
        LifetimeTracker.Reset();

        using (var label = new PlainResourceLabel("copy-lane"))
        using (var resource = new TrackedLabeledResource(4401, label))
        {
            AssertEqual(4401, resource.GetPeek(), "the constructed value is readable");
            AssertEqual("copy-lane", resource.Label, "the existential argument reached the initializer");
            AssertEqual(0, LifetimeTracker.GetStats().deallocations, "still owned by C#");
        }

        AssertEqual(1, LifetimeTracker.GetStats().deallocations, "exactly one deinit for one value");
    }

    /// <summary>
    /// Copyable siblings of the four refused closure ARGUMENT lanes plus the refused closure RESULT
    /// lane. Each differs from its refused twin only in the copyability of the type inside the
    /// closure, so a refusal that had keyed on "a closure appears in the signature" would have taken
    /// these bindings with it and this test would not compile.
    /// </summary>
    public void TestCopyableClosureLanesRoundTrip()
    {
        AssertEqual(4201, TestLibFunctions.InspectThroughCopyableClosure(t => t.GetPeek()),
            "non-escaping closure argument");
        AssertEqual(4202, TestLibFunctions.InspectThroughEscapingCopyableClosure(t => t.GetPeek()),
            "escaping closure argument");
        AssertEqual(4203, TestLibFunctions.InspectThroughThrowingCopyableClosure(t => t.GetPeek()),
            "throwing closure argument");
        AssertEqual(4207, TestLibFunctions.InspectThroughCopyablePairClosure((t, n) => t.GetPeek() + n),
            "two-argument closure, whose argument list is a tuple");
        AssertEqual(4205, TestLibFunctions.ProduceCopyableThroughClosure(() => new CopyableResourceToken(4205)),
            "closure result position");
    }

    /// <summary>
    /// Copyable siblings of the two refused async PARAMETER lanes and of the async RETURN lane that
    /// takes. The parameter sibling still stages its value into the buffer that outlives the
    /// suspension point, and the return sibling still copies out of the carrier and destroys it —
    /// both unchanged, which is what keeps the take narrow.
    /// </summary>
    public async Task TestCopyableAsyncLanesRoundTrip()
    {
        using (var token = new CopyableResourceToken(4206))
        {
            AssertEqual(4206,
                await WithTimeout(TestLibFunctions.AwaitCopyableResourcePeekAsync(token), DefaultAsyncTimeout),
                "copyable async parameter survives the suspension point");
            AssertEqual(4206, token.GetPeek(), "the caller still owns its own value after the await");
        }

        using var produced = await WithTimeout(
            TestLibFunctions.MakeCopyableResourceAsync(4208), DefaultAsyncTimeout);
        AssertEqual(4208, produced.GetPeek(), "copyable async return still copies out of the carrier");
    }

    /// <summary>
    /// The failable-constructor lane. Its `~Copyable` half is refused one gate earlier — a failable
    /// initializer returns `Optional&lt;Self&gt;`, and `Optional` is itself `~Copyable` when its
    /// payload is — so the copyable twin is what shows the lane itself is untouched, on both the
    /// success and the failure arm.
    /// </summary>
    public void TestCopyableFailableConstructorRoundTrips()
    {
        AssertTrue(CopyableFailableResource.TryCreate(4209, out var created), "non-negative id constructs");
        using (created)
            AssertEqual(4209, created.GetPeek(), "the constructed value is readable");

        AssertFalse(CopyableFailableResource.TryCreate(-1, out var refused), "negative id returns nil");
        refused?.Dispose();
    }

    /// <summary>
    /// The copyable twin of the existential-argument constructor, which keeps the bypass path it
    /// always had.
    /// </summary>
    public void TestCopyableExistentialArgumentConstructorRoundTrips()
    {
        using var label = new PlainResourceLabel("copy-lane-twin");
        using var resource = new CopyableLabeledResource(4402, label);

        AssertEqual(4402, resource.GetPeek(), "the constructed value is readable");
        AssertEqual("copy-lane-twin", resource.Label, "the existential argument reached the initializer");
    }
}
