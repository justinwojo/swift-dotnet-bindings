// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using Swift;
using Swift.Runtime;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Closures;

/// <summary>
/// Runtime coverage for a closure whose return type is a COLLECTION of existentials.
/// </summary>
/// <remarks>
/// An existential generic argument is marshalled by whatever carrier encloses it. A collection
/// container carries its own Swift type metadata and boxes each element through its own element
/// path, so the callback's return value marshals as an array and the existential is never handed
/// to the buffer marshal on its own. That is what makes this shape different from an existential
/// bound directly under <c>Optional</c>, which stays refused: there the projected payload carries
/// no Swift metadata to marshal through at all. These tests exist so the container half cannot
/// silently regress into the same fail-fast the direct half would take.
/// </remarks>
public class ExistentialClosureReturnTests : TestBase
{
    public ExistentialClosureReturnTests(TestResults results) : base(results) { }

    private static ExistentialContainer1 BoxedSignal()
    {
        var signal = new UnsupportedClosureSignalDefault();
        return ((IExistentialBoxable)signal).BoxAsExistential1<IUnsupportedClosureSignal>();
    }

    /// <summary>An empty collection round-trips — the channel works with nothing in it.</summary>
    public void TestExistentialArrayReturnDeliversEmptyCollection()
    {
        using var host = new UnsupportedClosureArrayOfExistentialReturn();

        var count = host.Enumerate(_ => new SwiftArray<ExistentialContainer1>(
            global::System.Array.Empty<ExistentialContainer1>()));

        AssertEqual(0, count, "an empty existential array should reach Swift as empty");
    }

    /// <summary>
    /// A populated collection round-trips, which is the arm that actually exercises per-element
    /// existential boxing rather than just the empty-container fast path.
    /// </summary>
    public void TestExistentialArrayReturnDeliversBoxedElements()
    {
        using var host = new UnsupportedClosureArrayOfExistentialReturn();

        var count = host.Enumerate(_ => new SwiftArray<ExistentialContainer1>(
            new[] { BoxedSignal(), BoxedSignal(), BoxedSignal() }));

        AssertEqual(3, count, "Swift should count every boxed existential the block returned");
    }

    /// <summary>
    /// Counting the returned array only proves the collection crossed the boundary. Calling a
    /// protocol requirement on each element proves every box Swift received carries a witness
    /// table it can dispatch on, which is the part a length assertion cannot see.
    /// </summary>
    public void TestExistentialArrayReturnElementsDispatchTheirWitness()
    {
        using var host = new UnsupportedClosureArrayOfExistentialReturn();

        var described = host.DescribeAll(_ => new SwiftArray<ExistentialContainer1>(
            new[] { BoxedSignal(), BoxedSignal() }));

        AssertEqual("default,default", described, "each returned box should dispatch describe()");
    }

    /// <summary>The block receives the argument Swift passed, not a default-constructed one.</summary>
    public void TestExistentialArrayReturnBlockReceivesItsArgument()
    {
        using var host = new UnsupportedClosureArrayOfExistentialReturn();
        var observedId = -1;

        host.Enumerate(request =>
        {
            observedId = request.Id;
            return new SwiftArray<ExistentialContainer1>(new[] { BoxedSignal() });
        });

        AssertEqual(2, observedId, "the block should observe the request Swift constructed");
    }
}
