// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Linq;
using System.Reflection;
using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Generics;

/// <summary>
/// Mixed generic tuple returns — fail-closed skip coverage.
///
/// A generic tuple return mixing bare generic parameters with concrete or
/// bound-generic elements (e.g. (T, Int), ([T], T), (T, T?)) lowers
/// element-wise in Swift's ABI: address-only elements take leading
/// indirect-result pointer registers while loadable elements return direct in
/// result registers. The direct-symbol P/Invoke cannot express that split, so
/// the generator must SKIP these members instead of emitting a call with a
/// mismatched ABI (the old behavior emitted a single-x8 indirect-result
/// P/Invoke that mis-bound every register).
///
/// These tests assert both sides of the gate: the mixed members are absent
/// from the binding surface, and the uniformly-bare control from the same
/// fixture file keeps emitting and round-tripping via the multi-@out branch.
/// The structural absence assertions are authoritative on the simulator
/// (Mono, no trimming); on NativeAOT device builds trimming can remove unused
/// members independently, so the sim leg is the real signal for those.
/// </summary>
public class MixedGenericTupleReturnSkipTests : TestBase
{
    public MixedGenericTupleReturnSkipTests(TestResults results) : base(results) { }

    #region Bare-only control — must keep working

    public void TestBareControlPairRoundTrip()
    {
        // Swift fixture: mixedControlBarePair<T, U> in Generics/MixedTupleReturns.swift.
        var a = new SummableInt32(value: 21);
        var b = new SummableInt32(value: 34);
#pragma warning disable CS0618 // [Obsolete] — CallConvSwift fallback for method-level generics
        var pair = TestLibFunctions.MixedControlBarePair(a, b);
#pragma warning restore CS0618
        AssertEqual(21, pair.Item1.Value, "MixedControlBarePair Item1.Value");
        AssertEqual(34, pair.Item2.Value, "MixedControlBarePair Item2.Value");
        TestLogger.Info($"MixedControlBarePair(21, 34) = ({pair.Item1.Value}, {pair.Item2.Value})");
    }

    public void TestBareControlPairHeterogeneous()
    {
        var a = new SummableInt32(value: 5);
        var b = new SimpleItem("id-5", "five");
#pragma warning disable CS0618
        var pair = TestLibFunctions.MixedControlBarePair(a, b);
#pragma warning restore CS0618
        AssertEqual(5, pair.Item1.Value, "MixedControlBarePair heterogeneous Item1.Value");
        AssertEqual("id-5", pair.Item2.Id.ToString(), "MixedControlBarePair heterogeneous Item2.Id");
    }

    public void TestHostControlMemberStillBound()
    {
        // The host struct itself must survive the member skips: its
        // constructor and non-tuple method keep emitting.
        var host = new MixedTupleHost(tag: 9);
        AssertEqual(9, host.GetTag(), "MixedTupleHost.GetTag() after sibling members skipped");
    }

    #endregion

    #region Mixed members — must be absent from the binding surface

    public void TestMixedFreeFunctionsNotBound()
    {
        var names = typeof(TestLibFunctions)
            .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance)
            .Select(m => m.Name)
            .ToHashSet();

        foreach (var skipped in new[]
                 {
                     "MixedReturnPairTI",
                     "MixedReturnPairIT",
                     "MixedReturnPairTArray",
                     "MixedReturnPairArrayT",
                     "MixedReturnPairTOptional",
                     "MixedReturnTriple",
                     "MixedReturnThrowing",
                 })
        {
            AssertTrue(!names.Contains(skipped),
                $"{skipped} must NOT be bound — its mixed generic tuple return has a per-element indirect/direct ABI the P/Invoke cannot express");
        }

        // Sanity: the control from the same fixture file IS on the surface,
        // proving the absence assertions above are not a trivial pass.
        AssertTrue(names.Contains("MixedControlBarePair"),
            "MixedControlBarePair (uniformly bare control) must remain bound");
    }

    public void TestMixedHostMembersNotBound()
    {
        var names = typeof(MixedTupleHost)
            .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance)
            .Select(m => m.Name)
            .ToHashSet();

        AssertTrue(!names.Contains("MixedMemberPair"),
            "MixedTupleHost.MixedMemberPair must NOT be bound (mixed generic tuple return)");
        AssertTrue(!names.Contains("MixedStaticPair"),
            "MixedTupleHost.MixedStaticPair must NOT be bound (mixed generic tuple return)");
        AssertTrue(names.Contains("GetTag"),
            "MixedTupleHost.GetTag (non-tuple control member) must remain bound");
    }

    #endregion
}
