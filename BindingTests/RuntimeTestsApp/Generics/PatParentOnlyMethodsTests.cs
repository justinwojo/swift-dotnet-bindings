// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Generics;

/// <summary>
/// Runtime regression for the parent-only sync CSM gap.
/// <para>
/// <c>CubbyBag&lt;Item: Cubby&gt;</c> declares three plain instance methods —
/// <c>bump(by:)</c>, <c>track(amount:)</c>, <c>read()</c> — none of which have
/// method-own generic parameters. Before the engine fix, the CSM filter
/// <c>ownParams.Count == 0</c> in <c>FindSpecializableMethods</c> filtered
/// these out, so they never reached the per-conformer extension emission path
/// in <c>EmitConcreteSpecializationsForGenericParent</c>. The methods then
/// fell back to the BoundGenericsHandler path (direct <c>CallConvSwift</c> with
/// metadata) — the same path that crashes Mono JIT on
/// <c>GenericContainer.count()/tagBytes()</c>.
/// </para>
/// <para>
/// After the fix, the methods emit as static extensions on
/// <c>CubbyBagStringCubbyCsmExtensions</c> / <c>CubbyBagIntCubbyCsmExtensions</c>,
/// each with a proper <c>@_cdecl</c> wrapper. Mono JIT no longer crashes and
/// state mutations round-trip through <c>read()</c>.
/// </para>
/// </summary>
public class PatParentOnlyMethodsTests : TestBase
{
    public PatParentOnlyMethodsTests(TestResults results) : base(results) { }

    public void TestCubbyBagStringCubby_DefaultCounterIsZero()
    {
        using var bag = Functions.MakeCubbyBagStringCubby();
        AssertEqual(0, bag.Read(), "Default counter is 0");
    }

    public void TestCubbyBagStringCubby_BumpThenReadRoundTrips()
    {
        // The simplest parent-only CSM path: a mutating method that takes a
        // primitive Int32 arg and writes self-state, plus a non-mutating method
        // that reads self-state back. Both must dispatch through the
        // CubbyBagStringCubbyCsmExtensions extension class (not the open-generic
        // BoundGenericsHandler path).
        using var bag = Functions.MakeCubbyBagStringCubby();
        bag.Bump(7);
        AssertEqual(7, bag.Read(), "Counter after Bump(7) is 7");
        bag.Bump(35);
        AssertEqual(42, bag.Read(), "Counter after Bump(35) is 42");
    }

    public void TestCubbyBagStringCubby_TrackReturnsIncrementAndMutatesState()
    {
        // Track(amount:) takes a primitive Int32 arg, returns Int32, and
        // mutates self-state. The return value witnesses the per-call delta;
        // Read() witnesses the cumulative state. Distinct from Bump in that
        // Track has a non-void return — exercises the indirect-result return
        // path of the parent-only CSM emitter alongside the mutation.
        using var bag = Functions.MakeCubbyBagStringCubby();
        var first = bag.Track(5);
        AssertEqual(5, first, "Track(5) returns 5");
        AssertEqual(5, bag.Read(), "Counter after first Track is 5");

        var second = bag.Track(6);
        AssertEqual(6, second, "Track(6) returns 6");
        AssertEqual(11, bag.Read(), "Counter after second Track is 11");
    }

    public void TestCubbyBagIntCubby_SecondConformerEmitsIndependently()
    {
        // Per-closed-conformer CSM emission: the same method surface must
        // emit independently for each conformer's specialization. IntCubby
        // is a second hint-resolved conformer of Cubby, so the engine should
        // produce two parent tuples and the emitter should land in two
        // distinct CubbyBag{Conformer}CsmExtensions classes.
        using var bag = Functions.MakeCubbyBagIntCubby();
        AssertEqual(0, bag.Read(), "IntCubby bag default counter is 0");
        bag.Bump(11);
        AssertEqual(11, bag.Read(), "IntCubby bag counter after Bump(11) is 11");
        var inc = bag.Track(3);
        AssertEqual(3, inc, "IntCubby Track(3) returns 3");
        AssertEqual(14, bag.Read(), "IntCubby counter after Track is 14");
    }

    public void TestCubbyBag_MutationsAreInstanceLocal()
    {
        // Two instances must not share state — confirms each closed
        // CubbyBag<StringCubby> carries its own backing storage after writes
        // through the per-conformer-specialized cdecl wrappers. Catches a
        // class of bugs where a stale "self_" pointer would alias all calls
        // through one shared payload.
        using var a = Functions.MakeCubbyBagStringCubby();
        using var b = Functions.MakeCubbyBagStringCubby();
        a.Bump(3);
        b.Bump(9);
        AssertEqual(3, a.Read(), "Instance A retains its own counter (3)");
        AssertEqual(9, b.Read(), "Instance B retains its own counter (9)");
    }

    public void TestCubbyBag_CrossConformerInstancesAreIndependent()
    {
        // Different closed conformers — different extension classes — must
        // not alias each other. Mutating one closed instantiation must not
        // perturb the other.
        using var s = Functions.MakeCubbyBagStringCubby();
        using var i = Functions.MakeCubbyBagIntCubby();
        s.Bump(20);
        i.Bump(5);
        AssertEqual(20, s.Read(), "StringCubby bag retains 20 independently");
        AssertEqual(5, i.Read(), "IntCubby bag retains 5 independently");
    }
}
